using Sokuro.Rings;
using Sokuro.Types;
using Sokuro.Sources;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Sokuro.Consumer;

internal sealed unsafe class RecordFormatter(SourceRegistry registry, int lineCapacity)
{
    private static ReadOnlySpan<byte> FormatError => "<fmt error>"u8;
    private static ReadOnlySpan<byte> HexDigits   => "0123456789ABCDEF"u8;

    private readonly SourceRegistry _registry  = registry;
    private readonly byte[] _scratch           = new byte[512];
    private readonly byte[] _timePrefix        = new byte[10];
    private long _prefixSecond                 = -1;
    private int _messageStart;

    internal LineBuffer Line { get; } = new LineBuffer(lineCapacity);


    internal ReadOnlySpan<byte> Message => Line.Written[_messageStart..^1];

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void Begin(DateTime utc, LogLevel level, uint sourceID)
    {
        Line.Reset();
        Span<byte> free = Line.Free;
        if (free.Length >= 23)
        {
            WriteTimestamp(utc, free);
            free[16] = (byte)' ';
            LevelName(level).CopyTo(free[17..]);
            "] ["u8.CopyTo(free[20..]);
            Line.Advance(23);
        }
        else
        {
            AppendTimestamp(utc);
            Line.Append((byte)' ');
            Line.Append(LevelName(level));
            Line.Append("] ["u8);
        }

        Line.Advance(_registry.AppendPath(sourceID, Line.Free, out bool pathTruncated));

        if (pathTruncated)
            Line.MarkOverflow();

        Line.Append("] "u8);
        _messageStart = Line.Length;
    }

    internal void AppendNotice(long dropped, int threadID)
    {
        Line.Append("Dropped "u8);
        Line.AppendNumber(dropped);
        Line.Append(" records on thread "u8);
        Line.AppendNumber(threadID);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void AppendItems(byte* items, int count)
    {
        byte* at = items;

        for (int i = 0; i < count && at != null; i++)
        {
            byte header = *at++;
            if ((header & Record.KindMask) == Record.ItemValue &&
                (header & (Record.HasAlignment | Record.HasFormat)) == 0)
            {
                ushort id       = Unsafe.ReadUnaligned<ushort>(at);
                Span<byte> free = Line.Free;
                if (id == TypeRegistry.Int32ID && free.Length >= 11)
                {
                    AppendFastValue<int>(ref at, free);
                    continue;
                }
                if (id == TypeRegistry.Int64ID && free.Length >= 20)
                {
                    AppendFastValue<long>(ref at, free);
                    continue;
                }
            }

            at = (header & Record.KindMask) switch
            {
                Record.ItemLiteral => AppendLiteral(at),
                Record.ItemValue   => AppendValue(at, header),
                Record.ItemText    => AppendText(at, header),
                Record.ItemBlob    => AppendBlob(at),
                _                  => null
            };
        }
    }

    internal void End() => Line.Finish();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AppendFastValue<T>(ref byte* at, Span<byte> destination) where T : unmanaged, IUtf8SpanFormattable
    {
        T value = Unsafe.ReadUnaligned<T>(at + sizeof(ushort));

        if (value.TryFormat(destination, out int written, default, CultureInfo.InvariantCulture))
            Line.Advance(written);
        else
            Line.Append(FormatError);

        at += sizeof(ushort) + sizeof(T);
    }

    private static ReadOnlySpan<byte> LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC"u8,
        LogLevel.Debug => "DBG"u8,
        LogLevel.Info  => "INF"u8,
        LogLevel.Warn  => "WRN"u8,
        LogLevel.Error => "ERR"u8,
        LogLevel.Fatal => "FTL"u8,
        _              => "OFF"u8
    };

    private void AppendTimestamp(DateTime utc)
    {
        Span<byte> free = Line.Free;
        if (free.Length >= 16)
        {
            WriteTimestamp(utc, free);
            Line.Advance(16);
        }
        else
        {
            Span<byte> timestamp = stackalloc byte[16];
            WriteTimestamp(utc, timestamp);
            
            foreach (byte value in timestamp)
                Line.Append(value);
        }
    }

    private void WriteTimestamp(DateTime utc, Span<byte> destination)
    {
        long ticks  = utc.Ticks;
        long second = ticks / TimeSpan.TicksPerSecond;

        if (second != _prefixSecond)
        {
            int seconds = (int)(second % 86_400);
            _timePrefix[0] = (byte)'[';
            WriteTwoDigits(_timePrefix.AsSpan(1), seconds / 3600);
            _timePrefix[3] = (byte)':';
            WriteTwoDigits(_timePrefix.AsSpan(4), seconds / 60 % 60);
            _timePrefix[6] = (byte)':';
            WriteTwoDigits(_timePrefix.AsSpan(7), seconds % 60);
            _timePrefix[9] = (byte)'.';
            _prefixSecond = second;
        }

        int micros = (int)(ticks % TimeSpan.TicksPerSecond / 10);
        _timePrefix.CopyTo(destination);
        WriteTwoDigits(destination[10..], micros / 10_000);
        WriteTwoDigits(destination[12..], micros / 100 % 100);
        WriteTwoDigits(destination[14..], micros % 100);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteTwoDigits(Span<byte> destination, int value)
    {
        destination[0] = (byte)('0' + value / 10);
        destination[1] = (byte)('0' + value % 10);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte* AppendLiteral(byte* at)
    {
        Line.Append(ReadText(ref at));
        return at;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private byte* AppendValue(byte* at, byte header)
    {
        ushort id = Read<ushort>(ref at);

        int alignment = ReadAlignment(ref at, header);

        Span<char> format = stackalloc char[Record.MaxFormatLength];
        int formatLength  = 0;

        if ((header & Record.HasFormat) != 0)
        {
            formatLength = *at++;

            for (int i = 0; i < formatLength; i++)
                format[i] = (char)at[i];

            at += formatLength;
        }

        if (!TypeRegistry.TryGet(id, out TypeEntry entry))
            return null;

        ReadOnlySpan<byte> raw = new(at, entry.Size);
        if (alignment == 0 && Line.Free.Length >= _scratch.Length)
        {
            Span<byte> destination = Line.Free[.._scratch.Length];

            int written = FormatValue(entry, raw, format[..formatLength], destination);
            Line.Advance(destination[..written].Length);
        }
        else
        {
            int written = FormatValue(entry, raw, format[..formatLength], _scratch);
            AppendAligned(_scratch.AsSpan(0, written), written, alignment);
        }
        return at + entry.Size;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int FormatValue(TypeEntry entry, ReadOnlySpan<byte> raw, ReadOnlySpan<char> format, Span<byte> destination)
    {
        try
        {
            if (entry.Formatter(raw, format, destination, out int written) &&
                (uint)written <= (uint)destination.Length)
                return written;
        }
        catch (Exception)
        {
        }

        FormatError.CopyTo(destination);
        return FormatError.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private byte* AppendText(byte* at, byte header)
    {
        int alignment = ReadAlignment(ref at, header);
        AppendAligned(ReadText(ref at), alignment);
        return at;
    }

    private byte* AppendBlob(byte* at)
    {
        ushort size     = Read<ushort>(ref at);
        Span<byte> free = Line.Free;

        if (free.Length >= size * 2)
        {
            for (int i = 0; i < size; i++)
            {
                free[2 * i]     = HexDigits[at[i] >> 4];
                free[2 * i + 1] = HexDigits[at[i] & 0xF];
            }

            Line.Advance(size * 2);
            return at + size;
        }

        for (int i = 0; i < size; i++)
        {
            Line.Append(HexDigits[at[i] >> 4]);
            Line.Append(HexDigits[at[i] & 0xF]);
        }

        return at + size;
    }

    private void AppendAligned(ReadOnlySpan<byte> text, int width, int alignment)
    {
        PadBefore(width, alignment);
        Line.Append(text);
        PadAfter(width, alignment);
    }

    private void AppendAligned(ReadOnlySpan<char> text, int alignment)
    {
        PadBefore(text.Length, alignment);
        Line.Append(text);
        PadAfter(text.Length, alignment);
    }

    private void PadBefore(int width, int alignment)
    {
        if (alignment > width)
            Line.Pad(alignment - width);
    }

    private void PadAfter(int width, int alignment)
    {
        if (-alignment > width)
            Line.Pad(-alignment - width);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T Read<T>(ref byte* at) where T : unmanaged
    {
        T value  = Unsafe.ReadUnaligned<T>(at);
        at      += sizeof(T);
        return value;
    }

    private static int ReadAlignment(ref byte* at, byte header) =>
        (header & Record.HasAlignment) != 0 ? Read<short>(ref at) : 0;

    private static ReadOnlySpan<char> ReadText(ref byte* at)
    {
        ushort count            = Read<ushort>(ref at);
        ReadOnlySpan<char> text = new(at, count);

        at += count * sizeof(char);
        return text;
    }
}