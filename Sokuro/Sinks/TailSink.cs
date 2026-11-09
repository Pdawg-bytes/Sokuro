using Sokuro.Rings;
using Sokuro.Sources;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Sokuro.Sinks;

public sealed class TailSink : ILogSink
{
    private const int TrailerBytes        = 4;
    private const int DefaultMaxLineBytes = 8192;

    private readonly Lock _lock = new();
    private readonly byte[] _buffer;
    private readonly byte[] _scratch;
    private readonly int _maxLineBytes;
    private readonly ISourceRegistry _registry;
    private ulong _head;
    private ulong _tail;
    private int _count;

    public TailSink(int capacityBytes, ISourceRegistry? registry = null, int maxLineBytes = DefaultMaxLineBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, 72);
        ArgumentOutOfRangeException.ThrowIfNegative(maxLineBytes);

        int alignedCapacity = capacityBytes & ~7;

        _maxLineBytes = Math.Min(maxLineBytes, alignedCapacity / 2 - TailHeader.Size - TrailerBytes - 8);
        _buffer       = new byte[alignedCapacity];
        _scratch      = new byte[_maxLineBytes];
        _registry     = registry ?? global::Sokuro.Logging.Logging.Registry;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    public void Write(in LogEntry entry)
    {
        ReadOnlySpan<byte> line = entry.Line.Length > _maxLineBytes ? entry.Line[.._maxLineBytes] : entry.Line;
        int total               = Record.Align8(TailHeader.Size + line.Length + TrailerBytes);

        TailHeader header = new()
        {
            Length     = (uint)total,
            Level      = (byte)entry.Level,
            Synthetic  = entry.Synthetic ? (byte)1 : (byte)0,
            SourceID   = entry.SourceID,
            LineLength = line.Length,
            Timestamp  = entry.Timestamp
        };

        Span<byte> trailer = stackalloc byte[TrailerBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, (uint)total);

        lock (_lock)
        {
            while (_tail + (ulong)total - _head > (ulong)_buffer.Length)
                Evict();

            CopyIn(_tail, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref header, 1)));
            CopyIn(_tail + TailHeader.Size, line);
            CopyIn(_tail + (ulong)total - TrailerBytes, trailer);
            _tail += (ulong)total;
            _count++;
        }
    }

    public void Flush()
    {
    }

    public int Query(in TailQuery query, TailVisitor visitor)
    {
        int visited        = 0;
        TailHeader header  = default;
        Span<byte> trailer = stackalloc byte[TrailerBytes];

        lock (_lock)
        {
            ulong position = _tail;

            while (position > _head && (query.Limit == 0 || visited < query.Limit))
            {
                CopyOut(position - TrailerBytes, trailer);
                position -= BinaryPrimitives.ReadUInt32LittleEndian(trailer);
                CopyOut(position, MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref header, 1)));

                LogLevel level = (LogLevel)header.Level;
                uint sourceID  = header.SourceID;

                if (!Matches(in query, level, sourceID))
                    continue;

                Span<byte> line = _scratch.AsSpan(0, header.LineLength);
                CopyOut(position + TailHeader.Size, line);
                visited++;

                TailEntry entry = new(level, sourceID, header.Timestamp, header.Synthetic != 0, line);
                if (!visitor(in entry))
                    break;
            }
        }

        return visited;
    }

    private bool Matches(in TailQuery query, LogLevel level, uint sourceID)
    {
        if (level < query.MinLevel)
            return false;

        if (query.Subtree is { } subtree && !_registry.IsDescendantOf(sourceID, subtree.ID))
            return false;

        if (query.Kind is { } kind && _registry.KindOf(sourceID).ID != kind.ID)
            return false;

        return query.Tag is not { } tag || _registry.HasTag(sourceID, tag);
    }

    private void Evict()
    {
        Span<byte> length = stackalloc byte[4];
        CopyOut(_head, length);
        _head += BinaryPrimitives.ReadUInt32LittleEndian(length);
        _count--;
    }

    private void CopyIn(ulong position, ReadOnlySpan<byte> source)
    {
        int offset = (int)(position % (ulong)_buffer.Length);
        int first  = Math.Min(source.Length, _buffer.Length - offset);

        source[..first].CopyTo(_buffer.AsSpan(offset));
        source[first..].CopyTo(_buffer);
    }

    private void CopyOut(ulong position, Span<byte> destination)
    {
        int offset = (int)(position % (ulong)_buffer.Length);
        int first  = Math.Min(destination.Length, _buffer.Length - offset);

        _buffer.AsSpan(offset, first).CopyTo(destination);
        _buffer.AsSpan(0, destination.Length - first).CopyTo(destination[first..]);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct TailHeader
{
    internal const int Size = 24;

    private uint _length;
    internal byte Level;
    internal byte Synthetic;
    internal ushort Reserved;
    private uint _sourceID;
    private int _lineLength;
    private long _timestamp;

    internal uint Length    { readonly get => LittleEndian.Convert(_length);     set => _length = LittleEndian.Convert(value); }
    internal uint SourceID  { readonly get => LittleEndian.Convert(_sourceID);   set => _sourceID = LittleEndian.Convert(value); }
    internal int LineLength { readonly get => LittleEndian.Convert(_lineLength); set => _lineLength = LittleEndian.Convert(value); }
    internal long Timestamp { readonly get => LittleEndian.Convert(_timestamp);  set => _timestamp = LittleEndian.Convert(value); }
}