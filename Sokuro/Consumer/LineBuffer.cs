using System.Text;
using System.Buffers;
using System.Text.Unicode;
using System.Runtime.CompilerServices;

namespace Sokuro.Consumer;

internal sealed class LineBuffer(int capacity)
{
    private readonly byte[] _bytes = new byte[capacity];
    private readonly int _limit    = capacity - 4;
    private int _length;

    internal bool Overflowed { get; private set; }

    internal int Length => _length;

    internal Span<byte> Free            => _bytes.AsSpan(_length, _limit - _length);
    internal ReadOnlySpan<byte> Written => _bytes.AsSpan(0, _length);

    internal void Reset()
    {
        _length    = 0;
        Overflowed = false;
    }

    internal void Advance(int count) => _length += count;

    internal void MarkOverflow() => Overflowed = true;

    internal void Append(byte value)
    {
        if (_length < _limit)
            _bytes[_length++] = value;
        else
            Overflowed = true;
    }

    internal void Append(ReadOnlySpan<byte> value)
    {
        if (value.Length > _limit - _length)
        {
            Overflowed = true;
            return;
        }

        value.CopyTo(_bytes.AsSpan(_length));
        _length += value.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Append(ReadOnlySpan<char> value)
    {
        if (value.Length <= 64 && value.Length <= _limit - _length)
        {
            Span<byte> destination = _bytes.AsSpan(_length, value.Length);
            if (Ascii.FromUtf16(value, destination, out int writtenAscii) == OperationStatus.Done)
            {
                _length += writtenAscii;
                return;
            }
        }

        OperationStatus status = Utf8.FromUtf16(value, Free, out _, out int written, true, true);

        _length    += written;
        Overflowed |= status != OperationStatus.Done;
    }

    internal void Pad(int count)
    {
        if (count <= 0)
            return;

        int length = Math.Min(count, _limit - _length);
        _bytes.AsSpan(_length, length).Fill((byte)' ');
        _length    += length;
        Overflowed |= length < count;
    }

    internal void AppendNumber(long value)
    {
        value.TryFormat(Free, out int written, default, System.Globalization.CultureInfo.InvariantCulture);
        _length += written;
    }

    internal void Finish()
    {
        if (Overflowed)
        {
            _bytes[_length++] = (byte)'.';
            _bytes[_length++] = (byte)'.';
            _bytes[_length++] = (byte)'.';
        }

        _bytes[_length++] = (byte)'\n';
    }
}