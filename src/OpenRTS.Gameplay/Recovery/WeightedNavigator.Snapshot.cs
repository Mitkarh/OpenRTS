using Frent;
namespace OpenRTS.Core.Navigation;

public sealed partial class WeightedNavigator
{
    internal void Snapshot(RuntimeArchive a)
    {
        a.Layout<RouteRequest>("Profile", "Goal");
        int budget = a.Value(_jobs.Length), capacity = a.Value(_fields.Length);
        if (budget != _jobs.Length || capacity != _fields.Length) throw new System.IO.InvalidDataException("Weighted navigation budget mismatch.");
        a.Fields(this, "_cursor", "_replacement");
        if (a.Reading) _cached.Clear();
        for (int i = 0; i < _fields.Length; i++)
        {
            var key = a.Value(_fields[i].Key);
            if (!a.Reading) continue;
            var field = _fields[i]; field.Key = key;
            if (key == new RouteRequest(-1, -1)) continue;
            Validate(key);
            field.Costs = new long[_routeCount]; field.Visited = Build(Geometry(key.Profile), key.Goal, field.Costs, a.Cancellation);
            _cached.Add(key, i);
        }
    }
}
