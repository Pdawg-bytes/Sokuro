using System.Text;

namespace Sokuro.Sources;

public interface ISourceRegistry
{
    uint ParentOf(uint id);
    LogKind KindOf(uint id);
    int IndexOf(uint id);

    bool HasTag(uint id, LogTag tag);
    bool IsDescendantOf(uint id, uint ancestor);

    int AppendPath(uint id, Span<byte> destination);
}

internal sealed class SourceRegistry(SourceTree tree) : ISourceRegistry
{
    private const string RootName  = "Root";
    private const string Separator = "/";

    private readonly byte[]?[] _paths = new byte[]?[SourceTree.MaxNodes];

    public uint ParentOf(uint id)  => tree.ParentOf(id);
    public LogKind KindOf(uint id) => new(tree.KindIDOf(id));
    public int IndexOf(uint id)    => tree.IndexOf(id);

    public bool HasTag(uint id, LogTag tag)
    {
        SourceTree.ValidateTag(tag);
        return (tree.TagsOf(id) & (1UL << tag.Bit)) != 0;
    }

    public bool IsDescendantOf(uint id, uint ancestor)
    {
        tree.ValidateID(id);
        tree.ValidateID(ancestor);
        while (id > ancestor)
            id = tree.ParentOf(id);

        return id == ancestor;
    }

    public int AppendPath(uint id, Span<byte> destination) => AppendPath(id, destination, out _);

    internal int AppendPath(uint id, Span<byte> destination, out bool truncated)
    {
        tree.ValidateID(id);
        byte[] path = _paths[id] ?? Interlocked.CompareExchange(ref _paths[id], BuildPath(id), null) ?? _paths[id]!;
        int length  = Math.Min(path.Length, destination.Length);
        truncated   = length < path.Length;
        while (length > 0 && length < path.Length && (path[length] & 0xC0) == 0x80)
            length--;

        path.AsSpan(0, length).CopyTo(destination);
        return length;
    }

    private byte[] BuildPath(uint id)
    {
        if (id == 0)
            return Encoding.UTF8.GetBytes(RootName);

        List<string> segments = [];
        for (; id != 0; id = tree.ParentOf(id))
        {
            string name = NameRegistry.Kinds.NameOf(tree.KindIDOf(id));
            int index   = tree.IndexOf(id);

            segments.Add(index < 0 ? name : $"{name} #{index}");
        }

        segments.Reverse();
        return Encoding.UTF8.GetBytes(string.Join(Separator, segments));
    }
}