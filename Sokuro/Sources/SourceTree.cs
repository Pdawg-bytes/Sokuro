using System.Runtime.InteropServices;

namespace Sokuro.Sources;

internal sealed unsafe class SourceTree
{
    internal const int MaxNodes  = 65536;
    internal const byte None     = 0xFF;
    internal const ushort NoKind = ushort.MaxValue;

    internal static readonly SourceTree Global = new();

    private readonly Lock _lock = new();

    private readonly Dictionary<NodeKey, uint> _lookup = [];

    private readonly uint[] _parents    = new uint[MaxNodes];
    private readonly ushort[] _kinds    = new ushort[MaxNodes];
    private readonly int[] _indexes     = new int[MaxNodes];
    private readonly ulong[] _tags      = new ulong[MaxNodes];
    private readonly byte[] _own        = new byte[MaxNodes];
    private readonly byte[] _kindLevels = new byte[MaxNodes];
    private readonly byte[] _tagLevels  = new byte[LogTag.MaxTags];

    private readonly byte* _effective;
    private int _count = 1;
    private long _overflows;
    private bool _active;

    internal SourceTree()
    {
        _effective = (byte*)NativeMemory.AlignedAlloc(MaxNodes, 64);
        new Span<byte>(_effective, MaxNodes).Fill((byte)LogLevel.Off);

        Array.Fill(_own, None);
        Array.Fill(_kindLevels, None);
        Array.Fill(_tagLevels, None);

        _kinds[0]   = NoKind;
        _indexes[0] = -1;
        _own[0]     = (byte)LogLevel.Off;
    }

    internal byte* Effective => _effective;
    internal int Count       => Volatile.Read(ref _count);
    internal long Overflows  => Interlocked.Read(ref _overflows);

    internal LogLevel EffectiveLevel(uint id) => (LogLevel)_effective[id];

    internal uint Child(uint parent, LogKind kind, int index, LogTags tags)
    {
        ValidateID(parent);
        ValidateKind(kind);
        int tagCount = NameRegistry.Tags.Count;
        if (tagCount < LogTag.MaxTags && (tags.Mask >> tagCount) != 0)
            throw new ArgumentOutOfRangeException(nameof(tags), "Tags must be registered.");
        lock (_lock)
        {
            NodeKey key = new(parent, kind.ID, index);
            if (_lookup.TryGetValue(key, out uint existing))
            {
                if (_tags[existing] != tags.Mask)
                    throw new InvalidOperationException("The existing source has different tags.");
                return existing;
            }

            if (_count >= MaxNodes)
            {
                Interlocked.Increment(ref _overflows);
                return parent;
            }

            uint id      = (uint)_count;
            _parents[id] = parent;
            _kinds[id]   = kind.ID;
            _indexes[id] = index;
            _tags[id]    = tags.Mask;
            _own[id]     = None;

            _lookup.Add(key, id);
            _effective[id] = (byte)Resolve(id);
            Volatile.Write(ref _count, _count + 1);

            return id;
        }
    }

    internal void SetRoot(LogLevel level) => Mutate(() => _own[0] = (byte)level);
    internal void SetNode(uint id, LogLevel level)
    {
        ValidateID(id);
        Mutate(() => _own[id] = (byte)level);
    }

    internal void ClearNode(uint id)
    {
        ValidateID(id);
        if (id != 0)
            Mutate(() => _own[id] = None);
    }

    internal void SetKind(LogKind kind, LogLevel level) { ValidateKind(kind); Mutate(() => _kindLevels[kind.ID] = (byte)level); }
    internal void ClearKind(LogKind kind)               { ValidateKind(kind); Mutate(() => _kindLevels[kind.ID] = None); }

    internal void SetTag(LogTag tag, LogLevel level) { ValidateTag(tag); Mutate(() => _tagLevels[tag.Bit] = (byte)level); }
    internal void ClearTag(LogTag tag)               { ValidateTag(tag); Mutate(() => _tagLevels[tag.Bit] = None); }

    internal void SetActive(bool active) => Mutate(() => _active = active);

    internal uint ParentOf(uint id)   { ValidateID(id); return _parents[id]; }
    internal ushort KindIDOf(uint id) { ValidateID(id); return _kinds[id]; }
    internal int IndexOf(uint id)     { ValidateID(id); return _indexes[id]; }
    internal ulong TagsOf(uint id)    { ValidateID(id); return _tags[id]; }

    internal void ValidateID(uint id)
    {
        if (id >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(id), "Source ID is not registered.");
    }

    internal static void ValidateKind(LogKind kind)
    {
        if (kind.ID >= NameRegistry.Kinds.Count)
            throw new ArgumentOutOfRangeException(nameof(kind), "Kind is not registered.");
    }

    internal static void ValidateTag(LogTag tag)
    {
        if ((uint)tag.Bit >= (uint)NameRegistry.Tags.Count)
            throw new ArgumentOutOfRangeException(nameof(tag), "Tag is not registered.");
    }

    private void Mutate(Action change)
    {
        lock (_lock)
        {
            change();
            Recompute();
        }
    }

    private void Recompute()
    {
        for (uint id = 0; id < (uint)_count; id++)
            _effective[id] = (byte)Resolve(id);
    }

    private LogLevel Resolve(uint id)
    {
        if (!_active)
            return LogLevel.Off;

        if (id == 0)
            return (LogLevel)_own[0];

        if (_own[id] != None)
            return (LogLevel)_own[id];

        ushort kind = _kinds[id];
        if (kind != NoKind && _kindLevels[kind] != None)
            return (LogLevel)_kindLevels[kind];

        byte tagLevel = MostVerboseTagLevel(_tags[id]);
        if (tagLevel != None)
            return (LogLevel)tagLevel;

        return (LogLevel)_effective[_parents[id]];
    }

    private byte MostVerboseTagLevel(ulong mask)
    {
        byte best = None;

        while (mask != 0)
        {
            int bit = System.Numerics.BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;

            byte level = _tagLevels[bit];

            if (level < best)
                best = level;
        }

        return best;
    }

    private readonly record struct NodeKey(uint Parent, ushort Kind, int Index);
}