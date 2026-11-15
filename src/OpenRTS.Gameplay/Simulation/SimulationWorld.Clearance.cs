using Frent;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private readonly List<EntityId> _exitOccupants = [];
    private readonly List<(WorldPosition Position, BodyShape Body)> _exitGoals = [];

    private WorldPosition ProductionExit(Entity producer, WorldPosition offset)
    {
        var from = producer.Get<SpatialState>().Position;
        var (x, y) = RotationOf(producer) switch { 1 => (-offset.Y, offset.X), 2 => (-offset.X, -offset.Y),
            3 => (offset.Y, -offset.X), _ => (offset.X, offset.Y) };
        return new(Math.Clamp(from.X + x, 0, Width), Math.Clamp(from.Y + y, 0, Depth), from.Elevation);
    }

    private void ClearExit(Entity producer, WorldPosition exit)
    {
        var type = TypeOf(producer);
        if (type.Footprint is null && ObjectGeometry.WithinRadius(type, producer.Get<SpatialState>().Position, exit, 0)) return;
        EnsureMotionIndex();
        uint owner = producer.Get<OwnershipState>().Owner;
        ClearArea(exit, 768, owner, producer.Get<EntityIdentity>().Id, TypeOf(producer), producer.Get<SpatialState>().Position);
    }

    private void ClearArea(WorldPosition center, int radius, uint owner, EntityId exclude = default,
        UnitDefinition? producer = null, WorldPosition producerPosition = default)
    {
        var index = _motion ?? _combatIndex;
        if (index is null) return;
        index.Query(center, center, radius, _exitOccupants);
        _exitOccupants.Sort(static (a, b) => a.Value.CompareTo(b.Value));
        var area = new WorldBox(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius);
        _exitGoals.Clear();
        foreach (var id in _exitOccupants)
        {
            if (id == exclude || !TryGetEntity(id, out var actor) || !IsOnMap(actor) || IsAircraft(actor)) continue;
            var type = TypeOf(actor);
            uint otherOwner = actor.Get<OwnershipState>().Owner;
            if (!type.Movable || otherOwner != 0 && otherOwner != owner) continue;
            var at = actor.Get<SpatialState>().Position;
            if (!BodyGeometry.SweptBox(at, at, area, type.Body)) continue;
            var motion = actor.Get<MotionState>();
            if (motion.Moving && actor.Get<NavigationAgentState>().WaitTicks < 8 &&
                !BodyGeometry.SweptBox(motion.Target, motion.Target, area, type.Body))
            { _exitGoals.Add((motion.Target, type.Body)); continue; }
            if (!TryFindExitPlacement(actor.Get<EntityIdentity>().Type, at, 3072, out var point,
                producer, producerPosition, area, clearPath: true)) continue;
            bool reserved = false;
            foreach (var goal in _exitGoals)
                if (BodyGeometry.Overlap(point, point, type.Body, goal.Position, goal.Position, goal.Body)) { reserved = true; break; }
            if (reserved) continue;
            if (TryEvacuate(id, point, preserveOrder: false)) _exitGoals.Add((point, type.Body));
        }
    }

    private void ClearStoppedPassages()
    {
        if (_navigation?.Grid.HasStoppingRestrictions != true) return;
        EnsureMovementOrder();
        for (int n = 0; n < Count; n++)
        {
            int i = _movementOrder[n];
            var actor = _entityHandles[i];
            var type = TypeOf(actor);
            if (!type.Movable || IsAircraft(actor) || !IsOnMap(actor) || actor.Get<MotionState>().Moving ||
                actor.Get<NavigationAgentState>().TerrainStranded || IsConstructing(actor) || UpgradeImmobilizes(actor)) continue;
            var at = actor.Get<SpatialState>().Position;
            if (StopAllowed(actor.Get<EntityIdentity>().Type, at)) continue;
            if (TryFindExitPlacement(actor.Get<EntityIdentity>().Type, at, 2048, out var point, clearPath: true) &&
                TryEvacuate(actor.Get<EntityIdentity>().Id, point, preserveOrder: false)) continue;
            if ((Tick + actor.Get<EntityIdentity>().Id.Value) % 8 == 0)
                ClearArea(at, 1024, actor.Get<OwnershipState>().Owner, actor.Get<EntityIdentity>().Id);
        }
    }
}
