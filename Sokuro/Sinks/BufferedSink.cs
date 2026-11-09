using Sokuro.Rings;
using Sokuro.Sources;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sokuro.Sinks;

public abstract class BufferedSink : ILogSink, IDisposable
{
    private static readonly TimeSpan ParkTimeout = TimeSpan.FromMilliseconds(5);

    private readonly byte[] _queue;
    private readonly long _mask;
    private readonly long _headroom;
    private readonly Thread _thread;
    private readonly SemaphoreSlim _wake = new(0);
    private long _tail;
    private long _cachedHead;
    private long _published;
    private long _head;
    private long _dropped;
    private long _failedBatches;
    private Exception? _lastError;
    private int _started;
    private int _parked;
    private int _flushRequested;
    private int _flushCompleted;
    private volatile bool _stop;

    protected BufferedSink(int capacityBytes = 1 << 20, double errorHeadroom = 0.125, string? name = null)
    {
        if (!int.IsPow2(capacityBytes) || capacityBytes < 4096)
            throw new ArgumentException("Capacity must be a power of two of at least 4096 bytes.", nameof(capacityBytes));
        if (!double.IsFinite(errorHeadroom) || errorHeadroom is < 0 or >= 1)
            throw new ArgumentOutOfRangeException(nameof(errorHeadroom));

        _queue    = new byte[capacityBytes];
        _mask     = capacityBytes - 1;
        _headroom = (long)(capacityBytes * errorHeadroom) & ~7L;
        _thread   = new Thread(Run) { IsBackground = true, Name = name ?? GetType().Name };
    }

    public TimeSpan FlushTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public long Dropped         => Interlocked.Read(ref _dropped);
    public long FailedBatches   => Interlocked.Read(ref _failedBatches);
    public Exception? LastError => Volatile.Read(ref _lastError);

    protected void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("The buffered sink has already been started.");
        try { _thread.Start(); }
        catch
        {
            Volatile.Write(ref _started, 0);
            throw;
        }
    }

    public void Write(in LogEntry entry) => TryWrite(in entry);

    internal bool TryWrite(in LogEntry entry)
    {
        EnsureStarted();
        if (_stop)
            throw new ObjectDisposedException(GetType().Name);
        int need     = Record.Align8(BufferedHeader.Size + entry.Line.Length);
        int capacity = _queue.Length;

        if (need > capacity / 4)
        {
            Interlocked.Increment(ref _dropped);
            return false;
        }

        long tail       = _tail;
        long contiguous = capacity - (tail & _mask);
        long waste      = contiguous < need ? contiguous : 0;
        long allowance  = entry.Level >= LogLevel.Error ? 0 : _headroom;
        long required   = need + waste + allowance;

        if (capacity - (tail - _cachedHead) < required)
        {
            _cachedHead = Volatile.Read(ref _head);
            if (capacity - (tail - _cachedHead) < required)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
        }

        if (waste > 0)
        {
            WritePad((int)(tail & _mask), (int)waste);
            tail += waste;
        }

        WriteEntry((int)(tail & _mask), need, in entry);
        _tail = tail + need;

        Volatile.Write(ref _published, _tail);
        if (Volatile.Read(ref _parked) != 0)
            _wake.Release();
        return true;
    }

    public void Flush()
    {
        if (!TryFlush(FlushTimeout))
            throw new TimeoutException("The buffered sink did not flush before the timeout.");
        if (LastError is { } error)
            throw new InvalidOperationException("The buffered sink has failed.", error);
    }

    internal bool TryFlush(TimeSpan timeout)
    {
        EnsureStarted();
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        if (LastError is not null)
            return true;
        if (_stop)
            throw new ObjectDisposedException(GetType().Name);
        int generation = Interlocked.Increment(ref _flushRequested);
        long start     = Stopwatch.GetTimestamp();

        while (Volatile.Read(ref _flushCompleted) < generation)
        {
            if (LastError is not null)
                return true;
            if (Stopwatch.GetElapsedTime(start) >= timeout)
                return false;

            _wake.Release();
            Thread.Sleep(1);
        }
        return true;
    }

    public void Dispose()
    {
        EnsureStarted();
        Stop(Timeout.InfiniteTimeSpan);
        if (LastError is { } error)
            throw new InvalidOperationException("The buffered sink has failed.", error);
    }

    internal bool Stop(TimeSpan timeout)
    {
        EnsureStarted();
        _stop = true;
        _wake.Release();

        return Thread.CurrentThread != _thread && _thread.Join(timeout);
    }

    protected abstract void WriteBatch(BufferedBatch batch);

    protected virtual void FlushOutput()
    {
    }

    protected virtual void OnIdle()
    {
    }

    protected virtual void Close()
    {
    }

    private void WritePad(int offset, int length)
    {
        Span<byte> at         = _queue.AsSpan(offset);
        BufferedPrefix prefix = new() { Length = length, Flags = BufferedLayout.FlagPad };

        MemoryMarshal.Write(at, in prefix);
    }

    private void WriteEntry(int offset, int total, in LogEntry entry)
    {
        Span<byte> at = _queue.AsSpan(offset, total);

        BufferedHeader header = new()
        {
            Prefix = new()
            {
                Length = total,
                Flags  = (byte)((entry.Synthetic ? BufferedLayout.FlagSynthetic : 0) | (entry.Truncated ? BufferedLayout.FlagTruncated : 0))
            },

            Level         = (byte)entry.Level,
            EventID       = entry.EventID,
            LineLength    = entry.Line.Length,
            SourceID      = entry.SourceID,
            Timestamp     = entry.Timestamp,
            MessageOffset = entry.Line.Length - entry.Message.Length - 1
        };

        MemoryMarshal.Write(at, in header);
        entry.Line.CopyTo(at[BufferedHeader.Size..]);
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                if (DrainQueue())
                    continue;

                CompleteFlush();
                if (_stop)
                    break;

                OnIdle();
                Park();
            }
        }
        catch (Exception exception)
        {
            RecordError(exception);
        }
        finally
        {
            _stop = true;
            try { Close(); }
            catch (Exception exception) { RecordError(exception); }
        }
    }

    private bool DrainQueue()
    {
        long head      = _head;
        long published = Volatile.Read(ref _published);

        if (head >= published)
            return false;

        long contiguous = _queue.Length - (head & _mask);
        long end        = head + Math.Min(published - head, contiguous);

        try
        {
            WriteBatch(new BufferedBatch(_queue.AsSpan((int)(head & _mask), (int)(end - head))));
        }
        catch (Exception exception)
        {
            Interlocked.Increment(ref _failedBatches);
            RecordError(exception);
        }

        Volatile.Write(ref _head, end);
        return true;
    }

    private void CompleteFlush()
    {
        int requested = Volatile.Read(ref _flushRequested);
        if (requested == Volatile.Read(ref _flushCompleted))
            return;

        try
        {
            FlushOutput();
        }
        catch (Exception exception)
        {
            RecordError(exception);
        }

        Volatile.Write(ref _flushCompleted, requested);
    }

    private void Park()
    {
        Volatile.Write(ref _parked, 1);
        if (Volatile.Read(ref _published) == _head && !_stop)
        {
            _wake.Wait(ParkTimeout);
        }

        Volatile.Write(ref _parked, 0);
    }

    private void EnsureStarted()
    {
        if (Volatile.Read(ref _started) == 0)
            throw new InvalidOperationException("The buffered sink has not been started.");
    }

    private void RecordError(Exception exception) => Volatile.Write(ref _lastError, exception);
}