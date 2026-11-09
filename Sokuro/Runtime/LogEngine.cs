using Sokuro.Rings;
using Sokuro.Sinks;
using Sokuro.Logging;
using Sokuro.Sources;
using Sokuro.Consumer;
using System.Diagnostics;

namespace Sokuro.Runtime;

internal sealed class LogEngine
{
    private readonly Lock _lock               = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private Ring[] _rings = [];
    private ILogSink[] _sinks;
    private ConsumerThread? _thread;

    private long _records;
    private long _retiredDropped;
    private long _sinkDropped;
    private long _producers;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private bool _shutdownFlushed;
    private Exception? _lastError;
    private long _errorCount;
    private readonly HashSet<ILogSink> _disposedSinks = new(ReferenceEqualityComparer.Instance);
    private int _ticks;

    internal LogEngine(LoggingOptions options)
    {
        Options  = options;
        _sinks   = [..options.Sinks];
        Clock    = new Clock();
        Registry = new SourceRegistry(SourceTree.Global);
        Consumer = new LogConsumer(this);
    }

    internal LoggingOptions Options   { get; }
    internal Clock Clock              { get; }
    internal SourceRegistry Registry  { get; }
    internal LogConsumer Consumer     { get; }

    internal Ring[] Rings         => Volatile.Read(ref _rings);
    internal ILogSink[] Sinks     => Volatile.Read(ref _sinks);
    internal long Records         => Interlocked.Read(ref _records);
    internal Exception? LastError => Volatile.Read(ref _lastError);
    internal long ErrorCount      => Interlocked.Read(ref _errorCount);

    internal void ReportFailure(Exception exception)
    {
        Volatile.Write(ref _lastError, exception);
        Interlocked.Increment(ref _errorCount);
    }


    internal void Run()
    {
        if (Options.ManualPump)
            return;

        _thread = new ConsumerThread(this);
        _thread.Start();
    }

    internal void Tick()
    {
        Consumer.CheckGrowth();

        if (++_ticks % Clock.ReanchorSeconds == 0)
            Consumer.AlignClock();
    }

    internal void CountRecords(long count) => Interlocked.Add(ref _records, count);
    internal void CountSinkDrop()          => Interlocked.Increment(ref _sinkDropped);

    internal bool TryEnterProducer()
    {
        while (true)
        {
            long state = Volatile.Read(ref _producers);
            if (state < 0)
                return false;
            if (Interlocked.CompareExchange(ref _producers, state + 1, state) == state)
                return true;
        }
    }

    internal void ExitProducer() => Interlocked.Decrement(ref _producers);

    internal Ring CreateRing(bool admitted = false)
    {
        lock (_lock)
        {
            if (_shutdownStarted && !admitted)
                throw new InvalidOperationException("Logging is shutting down.");

            Ring ring = new(Options, Environment.CurrentManagedThreadId);
            Volatile.Write(ref _rings, [.. _rings, ring]);
            return ring;
        }
    }

    internal void RemoveRings(List<Ring> removed)
    {
        lock (_lock)
        {
            foreach (Ring ring in removed)
                _retiredDropped += ring.Dropped;

            Volatile.Write(ref _rings, _rings.Where(r => !removed.Contains(r)).ToArray());
        }
    }

    internal void AddSink(ILogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_lock)
        {
            if (_shutdownStarted)
                throw new InvalidOperationException("Logging is shutting down.");

            Volatile.Write(ref _sinks, [.. _sinks, sink]);
        }
    }

    internal void RemoveSink(ILogSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        lock (_lock)
        {
            if (_shutdownStarted)
                throw new InvalidOperationException("Logging is shutting down.");

            Volatile.Write(ref _sinks, _sinks.Where(s => !ReferenceEquals(s, sink)).ToArray());
        }
    }

    internal bool Flush(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        long start = Stopwatch.GetTimestamp();

        if (!_lifecycle.Wait(timeout))
            return false;

        try
        {
            if (_shutdownStarted)
                throw new InvalidOperationException("Logging is shutting down.");

            if (!WaitForDrain(Remaining(start, timeout)))
                return false;

            return FlushSinks(start, timeout) && LastError is null;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal bool Shutdown(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        long start = Stopwatch.GetTimestamp();

        if (!_lifecycle.Wait(timeout))
            return false;

        try
        {
            if (_shutdownComplete)
                return LastError is null;

            if (!_shutdownStarted)
            {
                lock (_lock)
                {
                    _shutdownStarted = true;
                    Interlocked.Or(ref _producers, long.MinValue);
                }
                SourceTree.Global.SetActive(false);
                Consumer.DelayRelease();
            }

            while (Volatile.Read(ref _producers) != long.MinValue)
            {
                if (Stopwatch.GetElapsedTime(start) >= timeout)
                    return false;

                Thread.Sleep(1);
            }

            if (!WaitForDrain(Remaining(start, timeout)) && !(_thread?.Failed ?? false))
                return false;

            if (!(_thread?.Stop(Remaining(start, timeout)) ?? true))
                return false;

            if (!_shutdownFlushed)
            {
                if (!FlushSinks(start, timeout))
                    return false;

                _shutdownFlushed = true;
            }

            foreach (ILogSink sink in Sinks)
            {
                if (_disposedSinks.Contains(sink))
                    continue;

                if (sink is BufferedSink buffered)
                {
                    if (!buffered.Stop(Remaining(start, timeout)))
                        return false;

                    if (buffered.LastError is { } error)
                        ReportFailure(error);
                }
                else if (sink is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch (Exception exception) { ReportFailure(exception); }
                }

                _disposedSinks.Add(sink);
            }

            _shutdownComplete = true;
            return LastError is null;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal void Halt()
    {
        _thread?.Stop(TimeSpan.FromSeconds(1));
        _shutdownStarted = true;
    }

    internal LogStatistics Statistics()
    {
        Ring[] rings;
        long dropped;

        lock (_lock)
        {
            rings   = _rings;
            dropped = _retiredDropped;
        }

        foreach (Ring ring in rings)
            dropped += ring.Dropped;

        return new LogStatistics(Records, dropped + Interlocked.Read(ref _sinkDropped), SourceTree.Global.Overflows, rings.Length);
    }

    private static TimeSpan Remaining(long start, TimeSpan timeout)
    {
        TimeSpan left = timeout - Stopwatch.GetElapsedTime(start);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private bool WaitForDrain(TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();

        if (!Consumer.TrySnapshot(Remaining(start, timeout), out Ring[] rings, out ulong[] targets))
            return false;

        while (true)
        {
            if (_thread?.Failed == true)
                return false;
            if (Options.ManualPump)
                Consumer.DrainOnce();

            if (!Consumer.TryAllConsumed(rings, targets, Remaining(start, timeout), out bool consumed))
                return false;
            if (consumed)
                return true;

            if (Stopwatch.GetElapsedTime(start) >= timeout)
                return false;

            Thread.Sleep(1);
        }
    }

    private bool FlushSinks(long start, TimeSpan timeout)
    {
        foreach (ILogSink sink in Sinks)
        {
            if (_disposedSinks.Contains(sink))
                continue;

            try
            {
                if (sink is BufferedSink buffered)
                {
                    if (!buffered.TryFlush(Remaining(start, timeout)))
                        return false;

                    if (buffered.LastError is { } error)
                        ReportFailure(error);
                }
                else
                    sink.Flush();
            }
            catch (Exception exception)
            {
                ReportFailure(exception);
            }
        }
        return true;
    }
}