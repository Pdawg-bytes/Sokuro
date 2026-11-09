using System.Buffers;
using System.Reflection;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Sokuro.Types;

public delegate bool LogValueFormatter(ReadOnlySpan<byte> raw, ReadOnlySpan<char> format, Span<byte> utf8, out int written);

internal readonly record struct TypeEntry(int Size, LogValueFormatter Formatter);

internal static class TypeRegistry
{
    internal const ushort FirstCustomID = 256;
    internal const ushort Int32ID       = 6;
    internal const ushort Int64ID       = 8;

    private static readonly Lock _lock = new();

    private static readonly TypeEntry[] _builtins = CreateBuiltins();
    private static TypeEntry[] _custom            = [];


    internal static ushort Resolve<T>() where T : unmanaged
    {
        ushort builtin = BuiltinID<T>();
        if (builtin != 0)
            return builtin;

        lock (_lock)
        {
            return typeof(T).IsEnum ? Append(Unsafe.SizeOf<T>(), CreateEnumFormatter<T>()) : (ushort)0;
        }
    }

    internal static ushort Register<T>(LogValueFormatter formatter) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(formatter);
        lock (_lock)
        {
            if (BuiltinID<T>() != 0)
                throw new InvalidOperationException($"The built-in formatter for {typeof(T)} cannot be replaced.");
            return Append(Unsafe.SizeOf<T>(), formatter);
        }
    }

    internal static bool TryGet(ushort id, out TypeEntry entry)
    {
        if (id < FirstCustomID)
        {
            entry = id < _builtins.Length ? _builtins[id] : default;
            return entry.Formatter is not null;
        }

        TypeEntry[] custom = Volatile.Read(ref _custom);
        int index          = id - FirstCustomID;

        if (index < custom.Length)
        {
            entry = custom[index];
            return true;
        }

        entry = default;
        return false;
    }

    private static ushort Append(int size, LogValueFormatter formatter)
    {
        TypeEntry[] custom = _custom;
        if (custom.Length > ushort.MaxValue - FirstCustomID)
            throw new InvalidOperationException("The custom log type registry is full.");
        TypeEntry[] grown = new TypeEntry[custom.Length + 1];

        custom.CopyTo(grown, 0);
        grown[^1] = new TypeEntry(size, formatter);

        Volatile.Write(ref _custom, grown);
        return (ushort)(FirstCustomID + custom.Length);
    }

    private static LogValueFormatter CreateEnumFormatter<T>() where T : unmanaged
    {
        MethodInfo factory = typeof(TypeRegistry)
            .GetMethod(nameof(EnumFormatter), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(T));

        return (LogValueFormatter)factory.Invoke(null, null)!;
    }

    private static LogValueFormatter EnumFormatter<TEnum>() where TEnum : struct, Enum => FormatEnum<TEnum>;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool FormatEnum<TEnum>(ReadOnlySpan<byte> raw, ReadOnlySpan<char> format, Span<byte> utf8, out int written)
        where TEnum : struct, Enum
    {
        Span<char> chars = stackalloc char[256];
        TEnum value      = MemoryMarshal.Read<TEnum>(raw);
        if (!Enum.TryFormat(value, chars, out int count, format))
        {
            written = 0;
            return false;
        }

        return UTF8Transcode.TryEncode(chars[..count], utf8, out written);
    }

    private static ushort BuiltinID<T>()
    {
        if (typeof(T) == typeof(bool))   return 1;
        if (typeof(T) == typeof(sbyte))  return 2;
        if (typeof(T) == typeof(byte))   return 3;
        if (typeof(T) == typeof(short))  return 4;
        if (typeof(T) == typeof(ushort)) return 5;
        if (typeof(T) == typeof(int))    return Int32ID;
        if (typeof(T) == typeof(uint))   return 7;
        if (typeof(T) == typeof(long))   return Int64ID;
        if (typeof(T) == typeof(ulong))  return 9;
        if (typeof(T) == typeof(float))  return 10;
        if (typeof(T) == typeof(double)) return 11;
        if (typeof(T) == typeof(char))   return 12;
        if (typeof(T) == typeof(nint))   return 13;
        if (typeof(T) == typeof(nuint))  return 14;
        if (typeof(T) == typeof(Guid))   return 15;
        return 0;
    }

    private static TypeEntry[] CreateBuiltins() =>
    [
        default,
        new(sizeof(bool), FormatBool),
        Builtin<sbyte>(),
        Builtin<byte>(),
        Builtin<short>(),
        Builtin<ushort>(),
        Builtin<int>(),
        Builtin<uint>(),
        Builtin<long>(),
        Builtin<ulong>(),
        Builtin<float>(),
        Builtin<double>(),
        Builtin<char>(),
        Builtin<nint>(),
        Builtin<nuint>(),
        Builtin<Guid>()
    ];

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool FormatBool(ReadOnlySpan<byte> raw, ReadOnlySpan<char> format, Span<byte> utf8, out int written)
    {
        ReadOnlySpan<byte> text = raw[0] != 0 ? "True"u8 : "False"u8;
        written                 = text.Length;
        return text.TryCopyTo(utf8);
    }

    private static TypeEntry Builtin<T>() where T : unmanaged, IUtf8SpanFormattable =>
        new(Unsafe.SizeOf<T>(), FormatBuiltin<T>);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool FormatBuiltin<T>(ReadOnlySpan<byte> raw, ReadOnlySpan<char> format, Span<byte> utf8, out int written)
        where T : unmanaged, IUtf8SpanFormattable =>
        MemoryMarshal.Read<T>(raw).TryFormat(utf8, out written, format, CultureInfo.InvariantCulture);
}

internal static class LogTypeInfo<T> where T : unmanaged
{
    private static readonly Lock _lock = new();
    private static int _id             = -1;

    internal static ushort ID
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            int id = Volatile.Read(ref _id);
            return id >= 0 ? (ushort)id : Resolve();
        }
    }

    private static ushort Resolve()
    {
        lock (_lock)
        {
            if (_id < 0)
                Volatile.Write(ref _id, TypeRegistry.Resolve<T>());
            return (ushort)_id;
        }
    }

    internal static void Register(LogValueFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        lock (_lock)
        {
            if (_id >= 0)
                throw new InvalidOperationException($"{typeof(T)} has already been resolved.");

            Volatile.Write(ref _id, TypeRegistry.Register<T>(formatter));
        }
    }
}

internal static class UTF8Transcode
{
    internal static bool TryEncode(ReadOnlySpan<char> chars, Span<byte> destination, out int written)
    {
        OperationStatus status = System.Text.Unicode.Utf8.FromUtf16(chars, destination, out _, out written, true, true);
        return status == OperationStatus.Done;
    }
}