using OpenRTS.Core.Simulation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private bool UpdateWithdrawal(int index, int target, WeaponDefinition weapon, bool started)
    {
        var actor = _entityHandles[index];
        var identity = actor.Get<EntityIdentity>();
        var motion = actor.Get<MotionState>();
        bool recheck = started || (Tick + identity.Id.Value) % 4 == 0;
        if (!recheck) return motion.Moving && actor.Get<OrderState>().OrderPhase == OrderPhase.Withdrawing;

        var origin = actor.Get<SpatialState>().Position;
        var body = TypeOf(actor).Body;
        if (2 * body.HalfWidth > Width || 2 * body.HalfHeight > Depth) return false;
        EnsureMotionIndex();
        if (motion.Moving && actor.Get<OrderState>().OrderPhase == OrderPhase.Withdrawing && CanFireFrom(motion.Target))
            return true;
        if (!TargetBelowMinimum(index, target, weapon)) return false;

        var aim = AimPoint(index, target);
        long dx = origin.X - (long)aim.X, dy = origin.Y - (long)aim.Y;
        if (dx == 0 && dy == 0)
        {
            var center = _entityHandles[target].Get<SpatialState>().Position;
            dx = origin.X - (long)center.X;
            dy = origin.Y - (long)center.Y;
            if (dx == 0 && dy == 0) dx = 1;
        }

        // Probe the outward ray first, then both sides and finally across the target.
        ReadOnlySpan<(long X, long Y)> rays =
        [(dx, dy), (dx - dy, dy + dx), (dx + dy, dy - dx), (-dy, dx), (dy, -dx),
            (-dx - dy, dx - dy), (dy - dx, -dy - dx), (-dx, -dy)];
        foreach (var (x, y) in rays)
        {
            long length = (long)SquareRoot((ulong)(x * x + y * y));
            int low = 0, high = 2 * Math.Max(Width, Depth);
            if (BelowMinimum(At(high))) continue;
            while (high - low > 1)
            {
                int middle = low + (high - low) / 2;
                if (BelowMinimum(At(middle))) low = middle;
                else high = middle;
            }
            for (int offset = 0; offset <= WorldPosition.UnitsPerCell; offset += WorldPosition.UnitsPerCell / 2)
            {
                var point = At(high + offset);
                if (CanFireFrom(point) && SetTarget(index, point)) return true;
            }

            WorldPosition At(int distance) => GroundPosition(new(
                (int)Math.Clamp(origin.X + x * distance / length, body.HalfWidth, Width - body.HalfWidth),
                (int)Math.Clamp(origin.Y + y * distance / length, body.HalfHeight, Depth - body.HalfHeight)), origin.Elevation);
        }
        return false;

        bool BelowMinimum(WorldPosition point) =>
            weapon.BelowMinimum(WeaponDistance(index, target, weapon, out _, out _, out _, out _, point));
        bool CanFireFrom(WorldPosition point) =>
            StopAllowed(identity.Type, point) &&
            InWeaponRange(index, target, weapon.Range, weapon, out _, point) &&
            (_motion ?? _combatIndex)?.ClearPlacement(point, out _, body, identity.Id) != false &&
            (_navigation is null || Resolver(identity.Type).CanReach(origin, point, StepCancellation));
    }
}
