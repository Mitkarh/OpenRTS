using Frent;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    // Work uses a stable simulation anchor; optional flights across the roof belong to presentation.
    private bool PrepareAirWork(Entity actor, WorldPosition target)
    {
        if (TypeOf(actor).Flight is not { Mode: FlightMode.Hover } flight) return true;
        var at = actor.Get<SpatialState>().Position;
        int index = _indices[actor.Get<EntityIdentity>().Id];
        if (ObjectGeometry.Squared(at, target) <= 256L * 256 && at.Elevation == GroundHeight(at) + flight.CruiseAltitude &&
            !actor.Get<FlightState>().LandRequested && !actor.Get<FlightState>().ReturnRequested)
        { actor.Get<FlightState>().WorkingTick = Tick; Halt(index); return true; }
        SetTarget(index, target);
        actor.Get<MotionState>().Moving = true; // A vertical takeoff is needed even at the same X/Y.
        return false;
    }
}
