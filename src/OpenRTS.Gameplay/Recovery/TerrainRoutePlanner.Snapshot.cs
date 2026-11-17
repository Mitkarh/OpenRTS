using Frent;
namespace OpenRTS.Core.Navigation;

internal sealed partial class TerrainRoutePlanner
{
    internal bool SupportsSnapshot => _pointPaths is GridNavigator or IRuntimeSnapshotMechanic;
    internal void Snapshot(RuntimeArchive a)
    {
        if (_pointPaths is GridNavigator builtIn) builtIn.Snapshot(a); else a.Custom(_pointPaths);
        _weighted?.Snapshot(a);
        _groupRoot = a.Value(_groupRoot);
        bool tree = a.Value(_groupTree is not null);
        if (a.Reading) _groupTree = tree ? new(Grid, _groupRoot, a.Cancellation) : null;
        int n = a.Count(_weightedTrees.Count, 16);
        var keys = _weightedTrees.Keys.Order().ToArray();
        if (a.Reading) _weightedTrees.Clear();
        for (int i = 0; i < n; i++)
        {
            int profile = a.Value(a.Reading ? 0 : keys[i]);
            // A group's fallback root may differ from the centroid for an inaccessible profile.
            int root = a.Value(a.Reading ? 0 : _weightedTrees[profile].Root);
            if (a.Reading) _weightedTrees.Add(profile, new(Grid, root, a.Cancellation, _weighted!.ProfileAt(profile), _weighted.Geometry(profile)));
        }
    }
}
