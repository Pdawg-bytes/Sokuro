using Sokuro.Sources;

namespace Sokuro.Sinks;

public delegate bool TailVisitor(in TailEntry entry);

public readonly ref struct TailEntry(LogLevel level, uint sourceID, long timestamp, bool synthetic, ReadOnlySpan<byte> line)
{
    public LogLevel Level { get; } = level;
    public uint SourceID  { get; } = sourceID;
    public long Timestamp { get; } = timestamp;
    public bool Synthetic { get; } = synthetic;

    public ReadOnlySpan<byte> Line { get; } = line;
}

public readonly record struct TailQuery(
    LogLevel MinLevel  = LogLevel.Trace,
    LogSource? Subtree = null,
    LogKind? Kind      = null,
    LogTag? Tag        = null,
    int Limit          = 0
);