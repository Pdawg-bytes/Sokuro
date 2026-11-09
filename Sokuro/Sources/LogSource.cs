using System.Runtime.CompilerServices;

namespace Sokuro.Sources;

public enum LogLevel : byte
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
    Fatal,
    Off
}

public readonly struct LogKind(ushort id)
{
    internal ushort ID { get; } = id;

    public string Name => NameRegistry.Kinds.NameOf(ID);

    public static LogKind Register(string name) => new((ushort)NameRegistry.Kinds.Intern(name));
}

public readonly struct LogSource
{
    public LogSource(uint id)
    {
        SourceTree.Global.ValidateID(id);
        ID = id;
    }

    public static LogSource Root => default;

    public uint ID { get; }

    public LogSource Child(LogKind kind, int index = -1, LogTags tags = default) =>
        new(SourceTree.Global.Child(ID, kind, index, tags));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe bool IsEnabled(LogLevel level) => level >= (LogLevel)SourceTree.Global.Effective[ID];
}