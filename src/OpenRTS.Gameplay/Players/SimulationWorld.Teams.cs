namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private readonly SortedDictionary<uint, uint> _teams = new();

    /// <summary>Fixed match alliances. Team zero means free-for-all; ownership and vision remain separate.</summary>
    public void SetInitialTeams(IReadOnlyDictionary<uint, uint> teams)
    {
        ThrowIfDisposed();
        if (Tick != 0) throw new InvalidOperationException("Teams are fixed at match creation.");
        if (teams.Count > 8 || teams.Any(p => p.Key is < 1 or > 8 || p.Value > 8)) throw new ArgumentException("Invalid teams.");
        _teams.Clear();
        foreach (var pair in teams.Where(p => p.Value != 0)) _teams.Add(pair.Key, pair.Value);
    }
    public bool AreAllied(uint first, uint second) => first != 0 && second != 0 &&
        (first == second || _teams.TryGetValue(first, out uint team) && _teams.TryGetValue(second, out uint other) && team == other);
    private void WriteTeams(BinaryWriter writer)
    {
        // An extension record preserves the byte stream of existing free-for-all scenarios.
        if (_teams.Count == 0) return;
        writer.Write(-40); writer.Write(_teams.Count);
        foreach (var pair in _teams) { writer.Write(pair.Key); writer.Write(pair.Value); }
    }
}
