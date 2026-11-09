namespace Sokuro.Sources;

public readonly struct LogTag(int bit)
{
    public const int MaxTags  = 64;
    internal int Bit { get; } = bit;

    public string Name => NameRegistry.Tags.NameOf(Bit);

    public static LogTag Register(string name) => new(NameRegistry.Tags.Intern(name));

    public static LogTags operator |(LogTag a, LogTag b) => (LogTags)a | b;
}

public readonly struct LogTags(ulong mask)
{
    internal ulong Mask { get; } = mask;

    public static implicit operator LogTags(LogTag tag)
    {
        SourceTree.ValidateTag(tag);
        return new(1UL << tag.Bit);
    }

    public static LogTags operator |(LogTags a, LogTags b) => new(a.Mask | b.Mask);
}