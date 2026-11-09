using Sokuro.Logging;
using Sokuro.Sources;
using Sokuro.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Sokuro.Rings;

internal sealed class ThreadNotifier(Ring ring)
{
    internal void Dismiss() => GC.SuppressFinalize(this);

    ~ThreadNotifier() => ring.MarkDead();
}

[StructLayout(LayoutKind.Explicit, Size = Ring.ControlBytes)]
internal struct RingBlock
{
    [FieldOffset(0)]   public ulong Tail;
    [FieldOffset(128)] public ulong Head;
    [FieldOffset(256)] public ulong Capacity;
}

[StructLayout(LayoutKind.Explicit, Size = 256)]
internal struct RingDropCounter
{
    [FieldOffset(128)] public long Value;
}

internal sealed unsafe class Ring
{
    internal const int ControlBytes = 384;

    [ThreadStatic]
    private static Ring? t_current;

    [ThreadStatic]
    private static ThreadNotifier? t_notifier;

    private const ulong LinkBytes = 8;

    private readonly double _headroomFraction;
    private RingBlock* _block;
    private byte* _data;
    private ulong _capacity;
    private ulong _mask;
    private ulong _headroom;
    private ulong _tail;
    private ulong _cachedHead;
    private nint _nextBlock;
    private nint _reserveMarker;
    private bool _reserveOpen;

    private RingBlock* _readBlock;
    private byte* _readData;
    private ulong _readMask;
    private RingBlock* _pending;
    private List<nint>? _garbage;
    private long _checkedDrops;
    private int _strikes;

    private ulong _consumed;
    private bool _dead;
    private RingDropCounter _dropped;

    internal Ring(LoggingOptions options, int threadID)
    {
        _headroomFraction = options.ErrorHeadroom;

        _block = AllocateBlock((ulong)options.RingBytes);
        Adopt(_block);

        _readBlock = _block;
        _readData  = _data;
        _readMask  = _mask;

        LatestCapacity = _capacity;
        MaxRecordBytes = options.MaxRecordBytes;
        MaxTextChars   = options.MaxTextChars;

        ThreadID = threadID;
    }

    internal ulong LatestCapacity { get; private set; }

    internal static Ring? Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => t_current;
    }

    internal int MaxRecordBytes { get; }
    internal int MaxTextChars   { get; }
    internal int ThreadID      { get; }

    internal ulong CollectPos  { get; set; }
    internal long ReportedDrops { get; set; }

    internal long Dropped    => Volatile.Read(ref _dropped.Value);
    internal ulong Published => Volatile.Read(ref _readBlock->Tail);

    internal ulong SnapshotPublished()
    {
        ulong published = Published;

        if (_pending is not null)
            published = Math.Max(published, Volatile.Read(ref _pending->Tail));

        return published;
    }

    internal byte* At(ulong position) => _readData + (position & _readMask);

    internal void Release(ulong head)
    {
        Volatile.Write(ref _readBlock->Head, head);
        Volatile.Write(ref _consumed, head);
        FreeGarbage();
    }

    internal void SwitchRead()
    {
        (_garbage ??= []).Add((nint)_readBlock);

        _readBlock = _pending;
        _pending   = null;
        _readData  = (byte*)_readBlock + ControlBytes;
        _readMask  = _readBlock->Capacity - 1;
    }

    internal bool CheckGrowth(ulong maxCapacity)
    {
        long dropped  = Dropped;
        _strikes      = dropped > _checkedDrops ? _strikes + 1 : 0;
        _checkedDrops = dropped;

        if (_strikes < 2 || _pending is not null || LatestCapacity >= maxCapacity)
            return false;

        _strikes        = 0;
        LatestCapacity *= 2;

        _pending = AllocateBlock(LatestCapacity);
        Volatile.Write(ref _nextBlock, (nint)_pending);
        return true;
    }

    internal ulong Consumed => Volatile.Read(ref _consumed);
    internal bool IsDead    => Volatile.Read(ref _dead);

    internal bool Retired { get; private set; }

    internal void MarkDead() => Volatile.Write(ref _dead, true);

    internal void Retire()
    {
        Retired = true;
        FreeGarbage();
        NativeMemory.AlignedFree(_readBlock);

        if (_pending is not null)
            NativeMemory.AlignedFree(_pending);
    }

    internal static void DetachCurrent()
    {
        t_current?.MarkDead();
        t_notifier?.Dismiss();

        t_current  = null;
        t_notifier = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte* Reserve(LogLevel level, nint marker)
    {
        if (_reserveOpen && marker < _reserveMarker)
            return Reject();

        ulong need = (ulong)MaxRecordBytes;
        if (!HasRoom(level, need, out ulong waste) && !MakeRoom(level, need, out waste))
            return Reject();

        if (waste > 0)
            WriteMarker(waste, Record.FlagPad);

        _reserveOpen   = true;
        _reserveMarker = marker;

        return _data + (_tail & _mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Commit(uint length)
    {
        _tail += length;
        Volatile.Write(ref _block->Tail, _tail);
        _reserveOpen = false;

        if (_tail - _cachedHead > _capacity / 2)
            SignalPressure();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SignalPressure()
    {
        _cachedHead = Volatile.Read(ref _block->Head);

        if (_tail - _cachedHead > _capacity / 2)
            ConsumerWake.Signal();
    }

    internal static Ring AttachCurrent(bool admitted = false)
    {
        if (t_current is { } existing)
            return existing;

        Ring ring  = Logging.Logging.Engine.CreateRing(admitted);
        t_current  = ring;
        t_notifier = new ThreadNotifier(ring);

        return ring;
    }

    private static RingBlock* AllocateBlock(ulong capacity)
    {
        RingBlock* block = (RingBlock*)NativeMemory.AlignedAlloc((nuint)(ControlBytes + capacity), 64);
        NativeMemory.Clear(block, ControlBytes);
        block->Capacity = capacity;
        return block;
    }

    private void Adopt(RingBlock* block)
    {
        _block    = block;
        _capacity = block->Capacity;
        _mask     = _capacity - 1;
        _data     = (byte*)block + ControlBytes;
        _headroom = (ulong)(_headroomFraction * _capacity) & ~7UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasRoom(LogLevel level, ulong need, out ulong waste)
    {
        ulong contiguous = _capacity - (_tail & _mask);
        waste            = contiguous < need ? contiguous : 0;
        ulong allowance  = level >= LogLevel.Error ? 0 : _headroom;

        return _capacity - (_tail - _cachedHead) >= need + waste + allowance;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool MakeRoom(LogLevel level, ulong need, out ulong waste)
    {
        _cachedHead = Volatile.Read(ref _block->Head);
        if (HasRoom(level, need, out waste))
            return true;

        if (Volatile.Read(ref _nextBlock) == 0 || _capacity - (_tail - _cachedHead) < LinkBytes)
            return false;

        SwitchBlock();
        return HasRoom(level, need, out waste);
    }

    private void SwitchBlock()
    {
        RingBlock* next = (RingBlock*)Volatile.Read(ref _nextBlock);
        WriteMarker(LinkBytes, Record.FlagLink);

        next->Tail = _tail;
        next->Head = _tail;
        Volatile.Write(ref _block->Tail, _tail);

        Adopt(next);
        _cachedHead = _tail;

        Interlocked.CompareExchange(ref _nextBlock, 0, (nint)next);
    }

    private void FreeGarbage()
    {
        if (_garbage is not { Count: > 0 })
            return;

        foreach (nint block in _garbage)
            NativeMemory.AlignedFree((void*)block);

        _garbage.Clear();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private byte* Reject()
    {
        _dropped.Value++;
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WriteMarker(ulong length, byte flag)
    {
        RecordPrefix* marker = (RecordPrefix*)(_data + (_tail & _mask));

        marker->Length  = (uint)length;
        marker->Level   = 0;
        marker->Flags   = flag;
        _tail          += length;
    }
}