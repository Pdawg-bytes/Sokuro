using Sokuro.Sinks;
using Sokuro.Sources;

namespace Sokuro.Logging;

public readonly struct LogStatistics(long records, long dropped, long sourceOverflows, int threads)
{
    public long Records         { get; } = records;
    public long Dropped         { get; } = dropped;
    public long SourceOverflows { get; } = sourceOverflows;
    public int Threads          { get; } = threads;
}

public sealed class LoggingOptions
{
    public LogLevel DefaultLevel { get; init; } = LogLevel.Info;

    public int RingBytes      { get; init; } = 128 * 1024;
    public int MaxRingBytes   { get; init; } = 4 * 1024 * 1024;
    public int MaxRecordBytes { get; init; } = 1024;
    public int MaxTextChars   { get; init; } = 256;

    public double ErrorHeadroom { get; init; } = 0.125;

    public TimeSpan ReorderDelay { get; init; } = TimeSpan.Zero;

    public LogLevel WakeOnLevel { get; init; } = LogLevel.Off;

    public IList<ILogSink> Sinks { get; } = [];

    public bool ManualPump { get; init; }


    internal void Validate()
    {
        if (MaxRecordBytes < 128 || MaxRecordBytes % 8 != 0)
            throw new ArgumentException("MaxRecordBytes must be a multiple of 8 and at least 128.");

        if (!int.IsPow2(RingBytes) || (long)RingBytes < 64L * MaxRecordBytes)
            throw new ArgumentException("RingBytes must be a power of two and at least 64 * MaxRecordBytes.");

        if (!int.IsPow2(MaxRingBytes) || MaxRingBytes < RingBytes)
            throw new ArgumentException("MaxRingBytes must be a power of two and at least RingBytes.");

        if (MaxTextChars is < 1 or > ushort.MaxValue)
            throw new ArgumentException("MaxTextChars must be between 1 and 65535.");

        if (!double.IsFinite(ErrorHeadroom) || ErrorHeadroom is < 0 or >= 1)
            throw new ArgumentException("ErrorHeadroom must be in [0, 1).");

        if (ReorderDelay < TimeSpan.Zero ||
            ReorderDelay.TotalSeconds * System.Diagnostics.Stopwatch.Frequency >= 9223372036854775808d)
            throw new ArgumentException("ReorderDelay must be non-negative and representable in stopwatch ticks.");
    }
}