using Sokuro.Rings;
using Sokuro.Sinks;
using Sokuro.Runtime;
using Sokuro.Sources;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Sokuro.Consumer;

internal sealed unsafe class LogConsumer
{
    private const int BatchCapacity = 4096;

    private readonly LogEngine _engine;
    private readonly Lock _lock = new();

    private readonly RecordRef[] _batch = new RecordRef[BatchCapacity];
    private readonly RecordFormatter _formatter;

    private readonly List<Ring> _retired = [];
    private readonly long _reorderDelayTicks;
    private volatile bool _releaseAll;
    private int _rotation;

    internal LogConsumer(LogEngine engine)
    {
        _engine            = engine;
        _reorderDelayTicks = (long)(engine.Options.ReorderDelay.TotalSeconds * Stopwatch.Frequency);
        _formatter         = new RecordFormatter(engine.Registry, 4 * engine.Options.MaxRecordBytes);
    }


    internal void DelayRelease() => _releaseAll = true;

    internal void AlignClock()
    {
        lock (_lock)
            _engine.Clock.Align();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal bool DrainOnce()
    {
        lock (_lock)
        {
            Ring[] rings     = _engine.Rings;
            ILogSink[] sinks = _engine.Sinks;

            int notices = DispatchDropNotices(rings, sinks);
            int count   = Collect(rings, Horizon());

            RecordSorter.Sort(_batch.AsSpan(0, count), default(RecordOrder));

            for (int i = 0; i < count; i++)
                DispatchRecord(in _batch[i], rings[_batch[i].Ring], sinks);

            foreach (Ring ring in rings)
                ring.Release(ring.CollectPos);

            RetireDeadRings();
            _engine.CountRecords(count);
            return count + notices > 0;
        }
    }

    internal void CheckGrowth()
    {
        lock (_lock)
        {
            ulong max = (ulong)_engine.Options.MaxRingBytes;

            foreach (Ring ring in _engine.Rings)
                ring.CheckGrowth(max);
        }
    }

    internal (Ring[] Rings, ulong[] Targets) Snapshot()
    {
        lock (_lock)
            return SnapshotCore();
    }

    internal bool TrySnapshot(TimeSpan timeout, out Ring[] rings, out ulong[] targets)
    {
        if (!_lock.TryEnter(timeout))
        {
            rings   = [];
            targets = [];
            return false;
        }
        try
        {
            (rings, targets) = SnapshotCore();
            return true;
        }
        finally
        {
            _lock.Exit();
        }
    }

    internal bool AllConsumed(Ring[] rings, ulong[] targets)
    {
        lock (_lock)
            return AllConsumedCore(rings, targets);
    }

    internal bool TryAllConsumed(Ring[] rings, ulong[] targets, TimeSpan timeout, out bool consumed)
    {
        consumed = false;
        if (!_lock.TryEnter(timeout))
            return false;
        try
        {
            consumed = AllConsumedCore(rings, targets);
            return true;
        }
        finally
        {
            _lock.Exit();
        }
    }

    private (Ring[] Rings, ulong[] Targets) SnapshotCore()
    {
        Ring[] rings    = _engine.Rings;
        ulong[] targets = new ulong[rings.Length];

        for (int i = 0; i < rings.Length; i++)
            targets[i] = rings[i].SnapshotPublished();

        return (rings, targets);
    }

    private static bool AllConsumedCore(Ring[] rings, ulong[] targets)
    {
        for (int i = 0; i < rings.Length; i++)
        {
            if (!rings[i].Retired && rings[i].Consumed < targets[i])
                return false;
        }

        return true;
    }

    private long Horizon() =>
        _reorderDelayTicks == 0 || _releaseAll ? long.MaxValue : Stopwatch.GetTimestamp() - _reorderDelayTicks;

    private void RetireDeadRings()
    {
        if (_retired.Count == 0)
            return;

        _engine.RemoveRings(_retired);
        foreach (Ring ring in _retired)
            ring.Retire();

        _retired.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int Collect(Ring[] rings, long horizon)
    {
        int count    = 0;
        int sequence = 0;

        for (int step = 0; step < rings.Length && count < BatchCapacity; step++)
        {
            int index      = (_rotation + step) % rings.Length;
            Ring ring      = rings[index];
            bool dead      = ring.IsDead;
            ulong position = ring.CollectPos;
            ulong end      = ring.Published;

            while (position < end && count < BatchCapacity)
            {
                RecordPrefix* prefix = (RecordPrefix*)ring.At(position);
                uint length          = prefix->Length;
                byte flags           = prefix->Flags;

                if ((flags & Record.FlagLink) != 0)
                {
                    ring.SwitchRead();

                    position += length;
                    end       = ring.Published;

                    continue;
                }

                if ((flags & Record.FlagPad) == 0)
                {
                    RecordHeader* record = (RecordHeader*)prefix;
                    long timestamp       = record->Timestamp;

                    if (timestamp > horizon)
                        break;

                    _batch[count++] = new RecordRef(timestamp, index, sequence++, record);
                }

                position += length;
            }

            ring.CollectPos = position;
            if (dead && position == end && ring.Dropped == ring.ReportedDrops)
                _retired.Add(ring);
        }

        _rotation = rings.Length == 0 ? 0 : (_rotation + 1) % rings.Length;
        return count;
    }

    private int DispatchDropNotices(Ring[] rings, ILogSink[] sinks)
    {
        int notices = 0;
        foreach (Ring ring in rings)
        {
            long dropped = ring.Dropped;
            if (dropped == ring.ReportedDrops)
                continue;

            long timestamp = Stopwatch.GetTimestamp();
            DateTime utc   = _engine.Clock.ToUTC(timestamp);

            _formatter.Begin(utc, LogLevel.Warn, 0);
            _formatter.AppendNotice(dropped - ring.ReportedDrops, ring.ThreadID);
            _formatter.End();

            ring.ReportedDrops = dropped;
            Dispatch(sinks, timestamp, utc, LogLevel.Warn, 0, ring.ThreadID, 0, false, true);
            notices++;
        }

        return notices;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void DispatchRecord(in RecordRef reference, Ring ring, ILogSink[] sinks)
    {
        RecordHeader* record = reference.Pointer;
        LogLevel level       = (LogLevel)record->Prefix.Level;
        uint sourceID        = record->SourceID;
        DateTime utc         = _engine.Clock.ToUTC(reference.Timestamp);

        _formatter.Begin(utc, level, sourceID);
        _formatter.AppendItems((byte*)(record + 1), record->Prefix.ItemCount);
        _formatter.End();

        Dispatch(
            sinks,
            reference.Timestamp,
            utc,
            level,
            sourceID,
            ring.ThreadID,
            record->EventID,
            (record->Prefix.Flags & Record.FlagTruncated) != 0 || _formatter.Line.Overflowed,
            false
        );
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Dispatch(
        ILogSink[] sinks,
        long timestamp,
        DateTime utc,
        LogLevel level,
        uint sourceID,
        int threadID,
        ushort eventID,
        bool truncated,
        bool synthetic)
    {
        LogEntry entry = new(
            timestamp,
            utc,
            level,
            sourceID,
            threadID,
            eventID,
            truncated,
            synthetic,
            _formatter.Message,
            _formatter.Line.Written
        );

        foreach (ILogSink sink in sinks)
        {
            try
            {
                if (sink is BufferedSink buffered)
                {
                    if (!buffered.TryWrite(in entry))
                        _engine.CountSinkDrop();
                }
                else
                    sink.Write(in entry);
            }
            catch (Exception exception)
            {
                _engine.ReportFailure(exception);
            }
        }
    }

    private readonly struct RecordRef(long timestamp, int ring, int sequence, RecordHeader* pointer)
    {
        internal long Timestamp        { get; } = timestamp;
        internal int Ring              { get; } = ring;
        internal int Sequence          { get; } = sequence;
        internal RecordHeader* Pointer { get; } = pointer;
    }

    private readonly struct RecordOrder : IComparer<RecordRef>
    {
        public int Compare(RecordRef x, RecordRef y)
        {
            int byTime = x.Timestamp.CompareTo(y.Timestamp);
            if (byTime != 0)
                return byTime;

            int byRing = x.Ring.CompareTo(y.Ring);
            return byRing != 0 ? byRing : x.Sequence.CompareTo(y.Sequence);
        }
    }
}