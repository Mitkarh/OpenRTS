using Frent;
namespace OpenRTS.Core.Construction;

internal sealed partial class ConstructionSystem
{
    internal bool SupportsSnapshot => _mechanic is StandardConstruction or IRuntimeSnapshotMechanic;
    internal void SnapshotEntity(RuntimeArchive a, Entity entity)
    { if (_mechanic is StandardConstruction construction) construction.SnapshotEntity(a, entity); else a.Custom(_mechanic, entity); }
    internal void Snapshot(RuntimeArchive a)
    {
        a.Custom(_mechanic);
        a.Layout<BuildDefinition>("Ticks", "Cost", "Kind", "PlacementRadius", "InitialHealthPercent");
        a.Layout<ConstructionProject>("Id", "Owner", "UnitType", "Position", "Recipe", "QuarterTurns", "ReservedAt", "LastAttempt", "Paid");
        a.Fields(this, "_constructionProjects", "_foundationCount");
        int n = a.Count(_siteReservations.Count, 4096);
        if (a.Reading) _siteReservations.Clear();
        for (int i = 0; i < n; i++)
        {
            uint owner = a.Value(a.Reading ? 0u : _siteReservations[i].Owner);
            uint id = a.Value(a.Reading ? 0u : _siteReservations[i].Id.Value);
            if (a.Reading) _siteReservations.Add(_constructionProjects[owner][id]);
        }
    }
}
