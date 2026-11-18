using Frent;
using OpenRTS.Core.Simulation;
using OpenRTS.Core.Orders;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private bool PrepareAirWork(Entity actor, WorldPosition target)
    {
        if (TypeOf(actor).Flight is not { Mode: FlightMode.Hover } flight) return true;
        var at = actor.Get<SpatialState>().Position;
        int index = _indices[actor.Get<EntityIdentity>().Id];
        var state = actor.Get<FlightState>();
        var order = actor.Get<OrderState>().ActiveOrder;
        bool roaming = flight.WorkRoaming && order.Kind is UnitActionKind.Build or UnitActionKind.Capture or UnitActionKind.UpgradeSupport;
        if (!roaming) state.WorkTarget = default;
        if ((ObjectGeometry.Squared(at, target) <= 256L * 256 || roaming && state.WorkTarget == order.Target) && at.Elevation == FlightCruiseAt(at, TypeOf(actor)) &&
            !actor.Get<FlightState>().LandRequested && !actor.Get<FlightState>().ReturnRequested)
        {
            state.WorkingTick = Tick;
            if (roaming && state.WorkTarget != order.Target)
            { state.WorkTarget = order.Target; state.WorkStart = Tick; state.WorkAnchor = at; }
            Halt(index); return true;
        }
        SetTarget(index, target);
        actor.Get<MotionState>().Moving = true; // A vertical takeoff is needed even at the same X/Y.
        return false;
    }
    private bool UpdateAirWork(Entity actor)
    {
        var state = actor.Get<FlightState>();
        if (state.WorkingTick != Tick || !TypeOf(actor).Flight!.WorkRoaming)
        { state.WorkTarget = default; return false; }
        if (state.WorkTarget == default || !TryGetEntity(state.WorkTarget, out var target)) return false;
        var footprint = TypeOf(target).Footprint;
        if (footprint is not { Cells.Count: > 0 }) return true;
        long elapsed = Tick - state.WorkStart, segment = elapsed / 56;
        int travel = (int)Math.Clamp(elapsed % 56 - 32, 0, 24);
        var from = segment == 0 ? state.WorkAnchor : Point(segment - 1);
        var to = Point(segment);
        long blend = (long)travel * travel * (72 - 2 * travel);
        var point = new WorldPosition(from.X + (int)((to.X - (long)from.X) * blend / 13824),
            from.Y + (int)((to.Y - (long)from.Y) * blend / 13824), state.WorkAnchor.Elevation);
        var body = TypeOf(actor).Body;
        point = point with { X = Math.Clamp(point.X, body.HalfWidth, Width - body.HalfWidth), Y = Math.Clamp(point.Y, body.HalfHeight, Depth - body.HalfHeight) };
        PreciseFlightMove(actor, point, TypeOf(actor).Flight!.Speed, followRelief: true);
        return true;

        WorldPosition Point(long leg)
        {
            uint seed = unchecked(actor.Get<EntityIdentity>().Id.Value * 2654435761u ^ state.WorkTarget.Value * 2246822519u ^ (uint)leg * 3266489917u);
            seed ^= seed >> 16; seed *= 2246822519u; seed ^= seed >> 13;
            int parity = footprint.Cells.Count == 1 ? 0 : (int)(leg & 1);
            int choices = (footprint.Cells.Count + 1 - parity) / 2;
            var box = footprint.Bounds(footprint.Cells[parity + 2 * (int)(seed % (uint)choices)], target.Get<SpatialState>().Position);
            return new((box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2, state.WorkAnchor.Elevation);
        }
    }
}
