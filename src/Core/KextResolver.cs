namespace Pome.Core;

public sealed record Kext(string Id, string BundlePath, string[] Dependencies, int MinDarwin, int MaxDarwin);
public static class KextResolver
{
    public static IReadOnlyList<Kext> Resolve(IEnumerable<Kext> items, int darwin)
    {
        var map = items.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var active = new HashSet<string>(); var complete = new HashSet<string>(); var ordered = new List<Kext>();
        void Visit(string id)
        {
            if (complete.Contains(id)) return;
            if (!map.TryGetValue(id, out var item)) throw new InvalidDataException($"Missing kext dependency: {id}");
            if (darwin < item.MinDarwin || darwin > item.MaxDarwin) throw new InvalidDataException($"{id} does not support Darwin {darwin}.");
            if (!active.Add(id)) throw new InvalidDataException($"Kext dependency cycle at {id}.");
            foreach (var dependency in item.Dependencies) Visit(dependency);
            active.Remove(id); complete.Add(id); ordered.Add(item);
        }
        foreach (var id in map.Keys.Order(StringComparer.Ordinal)) Visit(id);
        return ordered;
    }
}
