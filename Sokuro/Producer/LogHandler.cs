using Sokuro.Rings;
using Sokuro.Types;
using Sokuro.Runtime;
using Sokuro.Sources;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Sokuro.Producer;

[InterpolatedStringHandler]
public unsafe ref struct LogHandler<TLevel> where TLevel : struct, ILogLevel
{
    private readonly Ring? _ring;
    private LogEngine? _engine;
    private readonly byte* _start;
    private readonly byte* _end;
    private byte* _cursor;
    private readonly uint _sourceID;
    private readonly ushort _eventID;
    private ushort _items;
    private bool _truncated;
    private bool _textTruncated;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LogHandler(int literalLength, int formattedCount, LogSource s, out bool enabled)
        : this(literalLength, formattedCount, s, 0, out enabled)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public LogHandler(int literalLength, int formattedCount, LogSource s, ushort eventID, out bool enabled)
    {
        enabled = false;
        if (!s.IsEnabled(TLevel.Value))
            return;

        LogEngine engine = global::Sokuro.Logging.Logging.Engine;
        if (!engine.TryEnterProducer())
            return;

        try
        {
            Ring ring   = Ring.Current ?? Ring.AttachCurrent(admitted: true);
            byte* start = ring.Reserve(TLevel.Value, (nint)Unsafe.AsPointer(ref enabled));
            if (start == null)
                return;

            _engine   = engine;
            _ring     = ring;
            _start    = start;
            _cursor   = start + sizeof(RecordHeader);
            _end      = start + ring.MaxRecordBytes;
            _sourceID = s.ID;
            _eventID  = eventID;
            enabled   = true;
        }
        finally
        {
            if (!enabled)
                engine.ExitProducer();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendLiteral(string s)
    {
        if (!_truncated && s is { Length: > 0 and <= ushort.MaxValue } &&
            s.Length <= ((int)(_end - _cursor) - 3) / 2)
        {
            byte* at = _cursor;
            *at++    = Record.ItemLiteral;

            WriteChars(ref at, s.AsSpan());
            _cursor = at;
            _items++;
            return;
        }

        AppendLiteralSlow(s);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void AppendLiteralSlow(string s)
    {
        ReadOnlySpan<char> text = s.AsSpan();
        while (text.Length > 0 && !_truncated)
        {
            int room = (int)(_end - _cursor) - 3;
            if (room < 2)
            {
                _truncated = true;
                return;
            }

            int count = Math.Min(Math.Min(text.Length, room / 2), ushort.MaxValue);
            WriteChars(Record.ItemLiteral, text[..count]);
            text = text[count..];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted<T>(T value) where T : unmanaged => WriteValue(value, false, 0, null);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted<T>(T value, string? format) where T : unmanaged => WriteValue(value, false, 0, format);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted<T>(T value, int alignment) where T : unmanaged => WriteValue(value, true, alignment, null);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AppendFormatted<T>(T value, int alignment, string? format) where T : unmanaged =>
        WriteValue(value, true, alignment, format);

    public void AppendFormatted(ReadOnlySpan<char> value) => WriteText(value, false, 0);

    public void AppendFormatted(ReadOnlySpan<char> value, int alignment) => WriteText(value, true, alignment);

    public void AppendFormatted(string? value) => WriteText(value, false, 0);

    public void AppendFormatted(string? value, int alignment) => WriteText(value, true, alignment);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Commit()
    {
        if (_engine is { } engine)
        {
            _engine = null;
            try { CommitRecord(); }
            finally { engine.ExitProducer(); }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CommitRecord()
    {
        Ring ring   = _ring!;
        uint length = (uint)Record.Align8((int)(_cursor - _start));

        RecordHeader* header     = (RecordHeader*)_start;
        header->Prefix.Length    = length;
        header->Prefix.Level     = (byte)TLevel.Value;
        header->Prefix.Flags     = _truncated || _textTruncated ? Record.FlagTruncated : (byte)0;
        header->Prefix.ItemCount = _items;
        header->Timestamp        = Stopwatch.GetTimestamp();
        header->SourceID         = _sourceID;
        header->EventID          = _eventID;
        header->Reserved         = 0;
        ring.Commit(length);

        if (TLevel.Value >= ConsumerWake.Threshold)
            ConsumerWake.Signal();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteValue<T>(T value, bool aligned, int alignment, string? format) where T : unmanaged
    {
        ushort id = LogTypeInfo<T>.ID;
        if (id == 0)
        {
            WriteBlob(ref value);
            return;
        }

        int formatLength = format is { Length: > 0 and <= Record.MaxFormatLength } ? format.Length : 0;
        long need        = 3L + (aligned ? 2 : 0) + (formatLength > 0 ? 1 + formatLength : 0) + Unsafe.SizeOf<T>();

        if (_truncated || _end - _cursor < need)
        {
            _truncated = true;
            return;
        }

        byte* at = _cursor;
        *at++    = (byte)(Record.ItemValue | (formatLength > 0 ? Record.HasFormat : 0) | (aligned ? Record.HasAlignment : 0));

        Write(ref at, id);
        if (aligned)
            Write(ref at, (short)Math.Clamp(alignment, short.MinValue, short.MaxValue));

        if (formatLength > 0)
        {
            *at++ = (byte)formatLength;
            for (int i = 0; i < formatLength; i++)
                *at++ = (byte)format![i];
        }

        Write(ref at, value);
        _cursor = at;
        _items++;
    }

    private void WriteBlob<T>(ref T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        if (_truncated || size > ushort.MaxValue || _end - _cursor < 3L + size)
        {
            _truncated = true;
            return;
        }

        byte* at = _cursor;
        *at++    = Record.ItemBlob;

        Write(ref at, (ushort)size);
        Write(ref at, value);

        _cursor = at;
        _items++;
    }

    private void WriteText(ReadOnlySpan<char> value, bool aligned, int alignment)
    {
        if (_truncated)
            return;

        int overhead = 3 + (aligned ? 2 : 0);
        int room     = (int)(_end - _cursor) - overhead;

        if (room < 0)
        {
            _truncated = true;
            return;
        }

        int limit = Math.Min(value.Length, _ring!.MaxTextChars);
        int count = Math.Min(limit, room / 2);

        _truncated     |= count < limit;
        _textTruncated |= count < value.Length;

        byte* at = _cursor;
        *at++    = (byte)(Record.ItemText | (aligned ? Record.HasAlignment : 0));

        if (aligned)
            Write(ref at, (short)Math.Clamp(alignment, short.MinValue, short.MaxValue));

        WriteChars(ref at, value[..count]);
        _cursor = at;
        _items++;
    }

    private void WriteChars(byte kind, ReadOnlySpan<char> text)
    {
        byte* at = _cursor;
        *at++    = kind;

        WriteChars(ref at, text);
        _cursor = at;
        _items++;
    }

    private static void WriteChars(ref byte* at, ReadOnlySpan<char> text)
    {
        Write(ref at, (ushort)text.Length);
        text.CopyTo(new Span<char>(at, text.Length));
        at += text.Length * sizeof(char);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write<T>(ref byte* at, T value) where T : unmanaged
    {
        Unsafe.WriteUnaligned(at, value);
        at += sizeof(T);
    }
}