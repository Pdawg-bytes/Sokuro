using Sokuro.Sources;

namespace Sokuro.Sinks;

public interface ILogSink
{
    void Write(in LogEntry entry);

    void Flush();
}

public readonly ref struct LogEntry(
    long timestamp,
    DateTime utcTime,
    LogLevel level,
    uint sourceID,
    int threadID,
    ushort eventID,
    bool truncated,
    bool synthetic,
    ReadOnlySpan<byte> message,
    ReadOnlySpan<byte> line)
{
    public long Timestamp   { get; } = timestamp;
    public DateTime UTCTime { get; } = utcTime;
    public LogLevel Level   { get; } = level;

    public uint SourceID  { get; } = sourceID;
    public int ThreadID   { get; } = threadID;
    public ushort EventID { get; } = eventID;

    public bool Truncated { get; } = truncated;
    public bool Synthetic { get; } = synthetic;

    public ReadOnlySpan<byte> Message { get; } = message;
    public ReadOnlySpan<byte> Line    { get; } = line;
}