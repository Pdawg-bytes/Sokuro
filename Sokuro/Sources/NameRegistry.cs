namespace Sokuro.Sources;

internal sealed class NameRegistry(int capacity)
{
    internal static readonly NameRegistry Kinds = new(ushort.MaxValue);
    internal static readonly NameRegistry Tags  = new(LogTag.MaxTags);

    private readonly Lock _lock                   = new();
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly List<string> _names          = [];
    private readonly int _capacity                = capacity;

    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _names.Count;
            }
        }
    }

    internal int Intern(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_lock)
        {
            if (_ids.TryGetValue(name, out int id))
                return id;

            if (_names.Count >= _capacity)
                throw new InvalidOperationException($"Cannot register more than {_capacity} names.");

            id = _names.Count;
            _names.Add(name);
            _ids.Add(name, id);
            return id;
        }
    }

    internal string NameOf(int id)
    {
        lock (_lock)
        {
            return _names[id];
        }
    }
}