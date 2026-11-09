using Sokuro.Sources;
using System.Runtime.InteropServices;

namespace Sokuro.Sinks;

public readonly ref struct BufferedEntry(
    LogLevel level,
    uint sourceID,
    long timestamp,
    ushort eventID,
    bool synthetic,
    bool truncated,
    ReadOnlySpan<byte> line)
{
    public LogLevel Level { get; } = level;
    public uint SourceID  { get; } = sourceID;
    public long Timestamp { get; } = timestamp;
    public ushort EventID { get; } = eventID;
    public bool Synthetic { get; } = synthetic;
    public bool Truncated { get; } = truncated;

    public ReadOnlySpan<byte> Line { get; } = line;
    internal int MessageOffset { get; init; }
}

public ref struct BufferedBatch(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _offset;

    public bool TryRead(out BufferedEntry entry)
    {
        while (_offset < _data.Length)
        {
            ReadOnlySpan<byte> at = _data[_offset..];

            BufferedPrefix prefix = MemoryMarshal.Read<BufferedPrefix>(at);
            int total             = prefix.Length;
            byte flags            = prefix.Flags;

            _offset += total;
            if ((flags & BufferedLayout.FlagPad) != 0)
                continue;

            BufferedHeader header = MemoryMarshal.Read<BufferedHeader>(at);

            entry = new BufferedEntry(
                (LogLevel)header.Level,
                header.SourceID,
                header.Timestamp,
                header.EventID,
                (flags & BufferedLayout.FlagSynthetic) != 0,
                (flags & BufferedLayout.FlagTruncated) != 0,
                at.Slice(BufferedHeader.Size, header.LineLength)
            )
            {
                MessageOffset = header.MessageOffset
            };

            return true;
        }

        entry = default;
        return false;
    }
}


[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct BufferedPrefix
{
    private int _length;
    internal byte Flags;

    internal int Length { readonly get => LittleEndian.Convert(_length); set => _length = LittleEndian.Convert(value); }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct BufferedHeader
{
    internal const int Size = 28;

    internal BufferedPrefix Prefix;
    internal byte Level;
    private ushort _eventID;
    private int _lineLength;
    private uint _sourceID;
    private long _timestamp;
    private int _messageOffset;

    internal ushort EventID    { readonly get => LittleEndian.Convert(_eventID);       set => _eventID = LittleEndian.Convert(value); }
    internal int LineLength    { readonly get => LittleEndian.Convert(_lineLength);    set => _lineLength = LittleEndian.Convert(value); }
    internal uint SourceID     { readonly get => LittleEndian.Convert(_sourceID);      set => _sourceID = LittleEndian.Convert(value); }
    internal long Timestamp    { readonly get => LittleEndian.Convert(_timestamp);     set => _timestamp = LittleEndian.Convert(value); }
    internal int MessageOffset { readonly get => LittleEndian.Convert(_messageOffset); set => _messageOffset = LittleEndian.Convert(value); }
}

internal static class BufferedLayout
{
    internal const byte FlagSynthetic = 1;
    internal const byte FlagTruncated = 2;
    internal const byte FlagPad       = 4;
}