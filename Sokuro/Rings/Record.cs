using System.Runtime.InteropServices;

namespace Sokuro.Rings;

[StructLayout(LayoutKind.Sequential, Size = 8)]
internal struct RecordPrefix
{
    internal uint Length;
    internal byte Level;
    internal byte Flags;
    internal ushort ItemCount;
}

[StructLayout(LayoutKind.Sequential, Size = 24)]
internal struct RecordHeader
{
    internal RecordPrefix Prefix;
    internal long Timestamp;
    internal uint SourceID;
    internal ushort EventID;
    internal ushort Reserved;
}

internal static class Record
{
    internal const byte FlagTruncated = 1;
    internal const byte FlagPad       = 2;
    internal const byte FlagLink      = 4;

    internal const byte ItemLiteral  = 1;
    internal const byte ItemValue    = 2;
    internal const byte ItemText     = 3;
    internal const byte ItemBlob     = 4;
    internal const byte KindMask     = 0x0F;
    internal const byte HasFormat    = 0x10;
    internal const byte HasAlignment = 0x20;

    internal const int MaxFormatLength = 31;

    internal static int Align8(int value) => (value + 7) & ~7;
}