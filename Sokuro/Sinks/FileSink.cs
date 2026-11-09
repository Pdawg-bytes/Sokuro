using System.Diagnostics;

namespace Sokuro.Sinks;

public sealed class FileSink : BufferedSink
{
    private readonly string _path;
    private readonly long _maxFileBytes;
    private readonly int _retainedFiles;
    private readonly TimeSpan _flushInterval;
    private FileStream _stream;
    private long _size;
    private long _lastFlush = Stopwatch.GetTimestamp();
    private bool _dirty;

    public FileSink(
        string path,
        long maxFileBytes       = 16 * 1024 * 1024,
        int retainedFiles       = 3,
        TimeSpan? flushInterval = null,
        int capacityBytes       = 1 << 20)
        : base(capacityBytes, name: "Sokuro.FileSink")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedFiles);

        if (flushInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(flushInterval));

        _path          = Path.GetFullPath(path);
        _maxFileBytes  = maxFileBytes;
        _retainedFiles = retainedFiles;
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(1);

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _stream = Open(FileMode.Append);
        try
        {
            _size = _stream.Length;
            Start();
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    protected override void WriteBatch(BufferedBatch batch)
    {
        while (batch.TryRead(out BufferedEntry entry))
        {
            if (_size > 0 && _size + entry.Line.Length > _maxFileBytes)
                Rotate();

            _stream.Write(entry.Line);
            _size  += entry.Line.Length;
            _dirty  = true;
        }
    }

    protected override void FlushOutput()
    {
        _stream.Flush();
        _dirty     = false;
        _lastFlush = Stopwatch.GetTimestamp();
    }

    protected override void OnIdle()
    {
        if (_dirty && Stopwatch.GetElapsedTime(_lastFlush) >= _flushInterval)
            FlushOutput();
    }

    protected override void Close() => _stream.Dispose();

    private FileStream Open(FileMode mode) => new(_path, mode, FileAccess.Write, FileShare.Read, 64 * 1024);

    private string Rotated(int index) => $"{_path}.{index}";

    private void Rotate()
    {
        _stream.Dispose();
        if (_retainedFiles > 0)
        {
            File.Delete(Rotated(_retainedFiles));
            for (int i = _retainedFiles - 1; i >= 1; i--)
            {
                if (File.Exists(Rotated(i)))
                    File.Move(Rotated(i), Rotated(i + 1));
            }

            File.Move(_path, Rotated(1));
        }

        _stream = Open(FileMode.Create);
        _size   = 0;
    }
}