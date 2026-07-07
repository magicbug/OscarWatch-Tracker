namespace OscarWatch.Core.Models;

public sealed class PassConflictResult
{
    public static readonly PassConflictResult Empty = new([]);

    private readonly Dictionary<(string NoradId, long AosTicks), List<PassConflict>> _lookup;

    public PassConflictResult(IReadOnlyList<PassConflict> conflicts)
    {
        Conflicts = conflicts;
        _lookup = new();
        foreach (var c in conflicts)
        {
            AddToLookup(c.PassA.NoradId, c.PassA.AosUtc.Ticks, c);
            AddToLookup(c.PassB.NoradId, c.PassB.AosUtc.Ticks, c);
        }
    }

    public IReadOnlyList<PassConflict> Conflicts { get; }

    public bool HasConflicts(string noradId, DateTime aosUtc)
        => _lookup.ContainsKey((noradId, aosUtc.Ticks));

    public IReadOnlyList<PassConflict> GetConflictsFor(string noradId, DateTime aosUtc)
        => _lookup.TryGetValue((noradId, aosUtc.Ticks), out var list) ? list : [];

    private void AddToLookup(string noradId, long ticks, PassConflict conflict)
    {
        var key = (noradId, ticks);
        if (!_lookup.TryGetValue(key, out var list))
        {
            list = [];
            _lookup[key] = list;
        }
        list.Add(conflict);
    }
}
