using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Sokuro.Sinks;

internal static class LittleEndian
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ushort Convert(ushort value) => BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static uint Convert(uint value) => BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Convert(int value) => BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Convert(long value) => BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);
}