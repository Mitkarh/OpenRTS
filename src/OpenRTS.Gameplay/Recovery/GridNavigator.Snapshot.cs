using Frent;
namespace OpenRTS.Core.Navigation;

public sealed partial class GridNavigator
{
    internal void Snapshot(RuntimeArchive a)
    {
        int budget = a.Value(FieldsPerTick), capacity = a.Value(CachedFields);
        if (budget != FieldsPerTick || capacity != CachedFields) throw new System.IO.InvalidDataException("Navigation budget mismatch.");
        a.Fields(this, "_requestCursor", "_replacementCursor");
        if (a.Reading) _cached.Clear();
        for (int i = 0; i < _fields.Length; i++)
        {
            int goal = a.Value(_fields[i].Goal);
            if (!a.Reading) continue;
            var field = _fields[i]; field.Goal = goal;
            if (goal < -1 || goal >= Grid.CellCount) throw new System.IO.InvalidDataException("Invalid navigation field.");
            if (goal < 0) continue;
            field.Distances = new int[Grid.CellCount]; field.Visited = Build(Grid, goal, field.Distances, a.Cancellation);
            _cached.Add(goal, i);
        }
    }
}
