using Sokuro.Sources;

namespace Sokuro.Sinks;

public sealed class ConsoleSink : BufferedSink
{
    private const int BufferSize = 32 * 1024;

    private readonly Stream _output;
    private readonly bool _color;
    private readonly byte[] _buffer = new byte[BufferSize];
    private int _length;

    public ConsoleSink(bool useColor = false, int capacityBytes = 1 << 20)
        : this(Console.OpenStandardOutput(), useColor, capacityBytes)
    {
    }

    public ConsoleSink(Stream output, bool useColor = false, int capacityBytes = 1 << 20)
        : base(capacityBytes, name: "Sokuro.ConsoleSink")
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _color  = useColor;
        Start();
    }

    protected override void WriteBatch(BufferedBatch batch)
    {
        while (batch.TryRead(out BufferedEntry entry))
        {
            if (_color) AppendColored(entry);
            else        Append(entry.Line);
        }

        Drain();
    }

    protected override void FlushOutput()
    {
        Drain();
        _output.Flush();
    }

    protected override void Close() => FlushOutput();

    private static ReadOnlySpan<byte> ColorOf(LogLevel level) => level switch
    {
        LogLevel.Trace => "\e[90m"u8,
        LogLevel.Debug => "\e[36m"u8,
        LogLevel.Info  => "\e[38;5;46m"u8,
        LogLevel.Warn  => "\e[33m"u8,
        LogLevel.Error => "\e[31m"u8,
        LogLevel.Fatal => "\e[97;41m"u8,
        _              => default
    };

    private static ReadOnlySpan<byte> MessageColorOf(LogLevel level) => level switch
    {
        LogLevel.Trace => "\e[38;5;245m"u8,
        LogLevel.Debug => "\e[38;5;110m"u8,
        LogLevel.Info  => "\e[38;5;114m"u8,
        LogLevel.Warn  => "\e[38;5;179m"u8,
        LogLevel.Error => "\e[38;5;174m"u8,
        LogLevel.Fatal => "\e[38;5;203m"u8,
        _              => default
    };

    private void AppendColored(in BufferedEntry entry)
    {
        ReadOnlySpan<byte> line = entry.Line;
        int messageOffset       = entry.MessageOffset;

        Append("\e[0m["u8);
        Append("\e[90m"u8);
        Append(line.Slice(1, 15));
        Append("\e[0m "u8);
        Append(ColorOf(entry.Level));
        Append(line.Slice(17, 3));
        Append("\e[0m]"u8);
        Append(line.Slice(21, messageOffset - 21));
        Append(MessageColorOf(entry.Level));
        Append(line.Slice(messageOffset, line.Length - messageOffset - 1));
        Append("\e[0m\n"u8);
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > BufferSize - _length)
            Drain();

        if (bytes.Length > BufferSize)
        {
            _output.Write(bytes);
            return;
        }

        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
    }

    private void Drain()
    {
        if (_length == 0)
            return;

        _output.Write(_buffer, 0, _length);
        _length = 0;
    }
}