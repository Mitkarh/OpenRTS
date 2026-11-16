using Frent;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    // A Land order with a passenger target is an automatic pickup; all intent is in existing hashed order/flight state.
    private bool IsBoardingPassenger(EntityId id, Entity carrier) => TryGetEntity(id, out var passenger) && IsOnMap(passenger) &&
        passenger.Get<OwnershipState>().Owner == carrier.Get<OwnershipState>().Owner &&
        passenger.Get<OrderState>() is { HasOrder: true, ActiveOrder.Kind: UnitActionKind.Enter } orders &&
        orders.ActiveOrder.Target == carrier.Get<EntityIdentity>().Id && orders.ActiveOrder.BoardingRevision == carrier.Get<FlightState>().BoardingRevision;

    private void CancelBoardingRequests(Entity carrier)
    {
        if (TypeOf(carrier).Transport is null || TypeOf(carrier).Flight!.Mode != FlightMode.Hover) return;
        // Each passenger checks the revision; a group command does not scan every passenger for every carrier.
        var flight = carrier.Get<FlightState>();
        flight.BoardingRevision = checked(flight.BoardingRevision + 1);
        flight.RetryTick = 0;
    }

    private bool BoardingSiteClear(Entity carrier, WorldPosition point)
    {
        var body = TypeOf(carrier).Body;
        return point.X >= body.HalfWidth && point.Y >= body.HalfHeight && point.X <= Width - body.HalfWidth && point.Y <= Depth - body.HalfHeight &&
            _construction?.SiteStopClear(point, body) != false && LandingClear(carrier, point, point, default);
    }

    private bool BoardingApproach(Entity passenger, Entity carrier, WorldPosition site, int range, out WorldPosition approach)
    {
        var type = TypeOf(passenger); var host = TypeOf(carrier);
        var from = passenger.Get<SpatialState>().Position;
        int left = site.X - host.Body.HalfWidth - type.Body.HalfWidth - 1;
        int right = site.X + host.Body.HalfWidth + type.Body.HalfWidth + 1;
        int top = site.Y - host.Body.HalfHeight - type.Body.HalfHeight - 1;
        int bottom = site.Y + host.Body.HalfHeight + type.Body.HalfHeight + 1;
        Span<WorldPosition> points = [new(left, Math.Clamp(from.Y, site.Y - host.Body.HalfHeight, site.Y + host.Body.HalfHeight)),
            new(right, Math.Clamp(from.Y, site.Y - host.Body.HalfHeight, site.Y + host.Body.HalfHeight)),
            new(Math.Clamp(from.X, site.X - host.Body.HalfWidth, site.X + host.Body.HalfWidth), top),
            new(Math.Clamp(from.X, site.X - host.Body.HalfWidth, site.X + host.Body.HalfWidth), bottom)];
        approach = default; long best = long.MaxValue;
        int unitType = passenger.Get<EntityIdentity>().Type;
        foreach (var point in points)
        {
            long distance = ObjectGeometry.Squared(from, point);
            if (distance >= best || point.X < type.Body.HalfWidth || point.Y < type.Body.HalfHeight ||
                point.X > Width - type.Body.HalfWidth || point.Y > Depth - type.Body.HalfHeight || !StopAllowed(unitType, point) ||
                ObjectGeometry.DistanceSquared(type, point, host, site, out _, out _) > (long)range * range ||
                _navigation is not null && !Resolver(unitType).CanReach(from, point, StepCancellation)) continue;
            best = distance; approach = GroundPosition(point, 0);
        }
        return best != long.MaxValue;
    }

    private bool PrepareBoardingLanding(Entity passenger, Entity carrier, int range, out WorldPosition approach)
    {
        approach = default;
        EnsureMotionIndex();
        var flight = carrier.Get<FlightState>();
        var orders = carrier.Get<OrderState>();
        bool pickup = flight.LandRequested && orders.HasOrder && orders.ActiveOrder.Kind == UnitActionKind.Land &&
            orders.ActiveOrder.Target != default && IsBoardingPassenger(orders.ActiveOrder.Target, carrier);
        if (pickup && BoardingSiteClear(carrier, flight.LandingPoint))
            return BoardingApproach(passenger, carrier, flight.LandingPoint, range, out approach);
        if (Tick < flight.RetryTick) return false;
        flight.RetryTick = Tick + 8;
        var center = carrier.Get<SpatialState>().Position;
        var from = passenger.Get<SpatialState>().Position;
        long best = long.MaxValue, bias = long.MaxValue;
        WorldPosition landing = default, waiting = default;
        // shortcut: pickup searches an 8192-unit neighbourhood on a 256-unit lattice; no free reachable site keeps Enter waiting.
        const int step = 256, radius = 8192;
        for (int ring = 0; ring <= radius / step && (long)ring * ring * step * step <= best; ring++)
        {
            StepCancellation.ThrowIfCancellationRequested();
            for (int x = -ring; x <= ring; x++) { Test(x, -ring); if (ring > 0) Test(x, ring); }
            for (int y = -ring + 1; y < ring; y++) { Test(-ring, y); Test(ring, y); }
        }
        if (best == long.MaxValue) return false;
        IssueOrder(_indices[carrier.Get<EntityIdentity>().Id], new(UnitActionKind.Land, landing, passenger.Get<EntityIdentity>().Id), false);
        flight.RetryTick = Tick + 8;
        approach = waiting;
        return true;

        void Test(int x, int y)
        {
            long distance = ((long)x * x + (long)y * y) * step * step;
            if (distance > (long)radius * radius || distance > best) return;
            var point = new WorldPosition(center.X + x * step, center.Y + y * step);
            var body = TypeOf(carrier).Body;
            if (point.X < body.HalfWidth || point.Y < body.HalfHeight || point.X > Width - body.HalfWidth || point.Y > Depth - body.HalfHeight) return;
            point = GroundPosition(point, 0);
            long toward = ObjectGeometry.Squared(from, point);
            if (distance == best && toward >= bias || !BoardingSiteClear(carrier, point) ||
                !BoardingApproach(passenger, carrier, point, range, out var goal)) return;
            best = distance; bias = toward; landing = point; waiting = goal;
        }
    }
}
