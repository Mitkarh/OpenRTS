using OpenRTS.Core.Simulation;
using OpenRTS.Core.Navigation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private static long ProjectileDistanceSquared(WorldPosition a, WorldPosition b, bool spatial)
    {
        long z = spatial ? (long)a.Elevation - b.Elevation : 0;
        return DistanceSquared(a, b) + z * z;
    }
    private static WorldPosition SpatialInterpolate(WorldPosition a, WorldPosition b, int t) =>
        new(a.X + (int)(((long)b.X - a.X) * t / 65536), a.Y + (int)(((long)b.Y - a.Y) * t / 65536),
            a.Elevation + (int)(((long)b.Elevation - a.Elevation) * t / 65536));

    private static bool ClipAxis(long value, long delta, long minimum, long maximum, ref int low, ref int high)
    {
        if (delta == 0) return value >= minimum && value <= maximum;
        long a = (minimum - value) * 65536, b = (maximum - value) * 65536;
        if (delta < 0) { (a, b) = (-b, -a); delta = -delta; }
        static long Floor(long n, long d) => n >= 0 ? n / d : -((-n + d - 1) / d);
        long entry = -Floor(-a, delta), exit = Floor(b, delta);
        if (entry > high || exit < low) return false;
        low = (int)Math.Max(low, entry); high = (int)Math.Min(high, exit);
        return low <= high;
    }

    private static int SpatialBoxHit(WorldPosition a, WorldPosition b, WorldBox box, int minimumZ, int maximumZ)
    {
        int low = 0, high = 65536;
        return ClipAxis(a.X, (long)b.X - a.X, box.MinX, box.MaxX, ref low, ref high) &&
            ClipAxis(a.Y, (long)b.Y - a.Y, box.MinY, box.MaxY, ref low, ref high) &&
            ClipAxis(a.Elevation, (long)b.Elevation - a.Elevation, minimumZ, maximumZ, ref low, ref high) ? low : 65537;
    }

    private static int SpatialHitFraction(WorldPosition a, WorldPosition b, WorldPosition previous, WorldPosition current, UnitDefinition type)
    {
        int height = type.Flight?.HalfHeight ?? 256;
        var from = new WorldPosition(a.X - previous.X, a.Y - previous.Y, a.Elevation - previous.Elevation);
        var to = new WorldPosition(b.X - current.X, b.Y - current.Y, b.Elevation - current.Elevation);
        if (type.Footprint is { } footprint)
        {
            int first = 65537;
            foreach (var bounds in footprint.TargetBounds) first = Math.Min(first, SpatialBoxHit(from, to, bounds, -height, height));
            return first;
        }
        if (type.Body.Kind == BodyKind.Box) return SpatialBoxHit(from, to, ObjectGeometry.Box(type.Body, default), -height, height);
        int low = HitFraction(from, to, default, default, type.Body.HalfWidth);
        if (low > 65536) return low;
        int high = 65536 - HitFraction(to, from, default, default, type.Body.HalfWidth);
        return ClipAxis(from.Elevation, (long)to.Elevation - from.Elevation, -height, height, ref low, ref high) ? low : 65537;
    }

    private bool TracesCliffRelief(WeaponDefinition weapon) => weapon.SpatialTrajectory && _baseGrid?.HasSurfaceCuts == true;

    // Ground poses give the support height. Fire from inside the existing ground hit volume.
    private WorldPosition WeaponPoint(WorldPosition point, WeaponDefinition weapon, bool airborne = false) =>
        !airborne && (weapon.HasFlightProfile || TracesCliffRelief(weapon))
        ? point with { Elevation = point.Elevation + 128 } : point;

    private bool SpatialTerrainClear(WorldPosition from, WorldPosition to) => SpatialTerrainHit(from, to) == 65537;

    private int SpatialTerrainHit(WorldPosition from, WorldPosition to, bool profile = false)
    {
        if (_baseGrid is not { } grid) return 65537;
        int size = grid.CellSize;
        if (Math.Min(from.X, to.X) < 0 || Math.Min(from.Y, to.Y) < 0 || Math.Max(from.X, to.X) >= Width || Math.Max(from.Y, to.Y) >= Depth) return 0;
        int x = from.X / size, y = from.Y / size, ex = to.X / size, ey = to.Y / size;
        long dx = (long)to.X - from.X, dy = (long)to.Y - from.Y;
        int sx = Math.Sign(dx), sy = Math.Sign(dy);
        long ax = Math.Abs(dx), ay = Math.Abs(dy);
        long tx = sx > 0 ? (long)(x + 1) * size - from.X : from.X - (long)x * size;
        long ty = sy > 0 ? (long)(y + 1) * size - from.Y : from.Y - (long)y * size;
        bool relief = grid.HasSurfaceCuts || profile;
        int first = 65537;
        while (true)
        {
            Visit(x, y);
            if (relief && sx == 0 && x > 0 && from.X % size == 0) Visit(x - 1, y);
            if (relief && sy == 0 && y > 0 && from.Y % size == 0) Visit(x, y - 1);
            if (relief && sx == 0 && sy == 0 && x > 0 && y > 0 && from.X % size == 0 && from.Y % size == 0)
                Visit(x - 1, y - 1);
            if ((!relief && first <= 65536) || x == ex && y == ey) return first;
            if (x != ex && y != ey && tx * ay == ty * ax) Visit(x, y + sy);
            if (x != ex && (y == ey || tx * ay <= ty * ax)) { x += sx; tx += size; }
            else { y += sy; ty += size; }
        }
        void Visit(int cx, int cy)
        {
            int cell = cy * grid.Width + cx;
            var bounds = new WorldBox(cx * size, cy * size, (cx + 1) * size, (cy + 1) * size);
            int surface = grid.SurfaceAt(cell).Elevation;
            // shortcut: binary terrain blockers have a 512-unit height; author obstacle heights when a game needs tall air obstacles.
            int extra = grid.IsBlocked(cell) ? 512 : -1;
            var cut = grid.SurfaceCutAt(cell);
            var rise = profile ? grid.SurfaceRiseAt(cell) : default;
            if (rise == default)
                first = Math.Min(first, SpatialBoxHit(from, to, bounds, -WorldPosition.MaximumExtent, surface + extra));
            else
            {
                int low = 0, high = Math.Min(first, 65536);
                if (ClipAxis(from.X, dx, bounds.MinX, bounds.MaxX, ref low, ref high) &&
                    ClipAxis(from.Y, dy, bounds.MinY, bounds.MaxY, ref low, ref high))
                {
                    long start = AbovePlane(from), end = AbovePlane(to);
                    if (AtMost(start, end - start, 2L * size * extra, ref low, ref high))
                        first = Math.Min(first, low);
                }

                long AbovePlane(WorldPosition point) => 2L * size * (point.Elevation - (long)surface)
                    - (2L * (point.X - bounds.MinX) - size) * rise.X
                    - (2L * (point.Y - bounds.MinY) - size) * rise.Y;
            }
            if (cut.UpperElevation > surface && cut.UpperCorner != SurfaceCorner.None)
            {
                // The low slab covers the square. Only the upper triangle adds rock above it.
                int low = 0, high = Math.Min(first, 65536);
                long start = cut.SignedDistance(from.X - bounds.MinX, from.Y - bounds.MinY, size);
                long end = cut.SignedDistance(to.X - bounds.MinX, to.Y - bounds.MinY, size);
                if (ClipAxis(from.X, dx, bounds.MinX, bounds.MaxX, ref low, ref high) &&
                    ClipAxis(from.Y, dy, bounds.MinY, bounds.MaxY, ref low, ref high) &&
                    ClipAxis(start, end - start, 0, size, ref low, ref high) &&
                    ClipAxis(from.Elevation, (long)to.Elevation - from.Elevation,
                        -WorldPosition.MaximumExtent, cut.UpperElevation + extra, ref low, ref high))
                    first = Math.Min(first, low);
            }
            foreach (int obstacle in grid.ObstaclesAt(cell))
                first = Math.Min(first, SpatialBoxHit(from, to, grid.Obstacle(obstacle), surface, surface + 512));
        }
    }

    private static bool AtMost(long start, long delta, long ceiling, ref int low, ref int high)
    {
        Int128 distance = ((Int128)ceiling - start) * 65536;
        if (delta >= 0)
        {
            if (distance < (Int128)delta * low) return false;
            if (delta > 0 && distance < (Int128)delta * high) high = (int)(distance / delta);
            return true;
        }
        if (distance < (Int128)delta * high) return false;
        if (distance < (Int128)delta * low) low = (int)((-distance - delta - 1) / -delta);
        return true;
    }

    private WorldPosition ProfileStep(in ProjectileState shot, int budget)
    {
        var weapon = shot.Weapon;
        int duration = Math.Max(4, (shot.InitialDistance + weapon.ProjectileSpeed - 1) / weapon.ProjectileSpeed);
        int age = (int)Math.Min(Tick - shot.BornTick, duration);
        int remainingTicks = duration - age + 1;
        ulong squared = (ulong)DistanceSquared(shot.Position, shot.Destination);
        int distance = (int)SquareRoot(squared);
        if ((ulong)((long)distance * distance) < squared) distance++;
        int horizontal = Math.Min(weapon.ProjectileSpeed, (distance + remainingTicks - 1) / remainingTicks);
        var next = StepTowards(shot.Position, shot.Destination, horizontal);
        if (shot.FanDirection != default && age < duration && age <= 6 && distance > horizontal)
        {
            int strength = Math.Min(5, 7 - age);
            var launchHeading = new WorldPosition(shot.Position.X + shot.FanDirection.X, shot.Position.Y + shot.FanDirection.Y);
            var fanStep = StepTowards(shot.Position, launchHeading, horizontal);
            next = next with
            {
                X = Math.Clamp(next.X + (fanStep.X - next.X) * strength / 5, 0, Width - 1),
                Y = Math.Clamp(next.Y + (fanStep.Y - next.Y) * strength / 5, 0, Depth - 1)
            };
        }
        const long one = 65536;
        long t = age * one / duration;
        long height = weapon.TrajectoryHeight;
        long baseline = shot.LaunchElevation + (shot.Destination.Elevation - (long)shot.LaunchElevation) * t / one;
        long elevation = weapon.Trajectory switch
        {
            ProjectileTrajectory.Arc => baseline + 4 * height * t * (one - t) / (one * one),
            ProjectileTrajectory.Ballistic => baseline + BallisticLift(t, height),
            ProjectileTrajectory.Cruise => CruiseHeight(t, shot.LaunchElevation, shot.Destination.Elevation, height),
            _ => throw new InvalidOperationException("Expected a flight profile.")
        };
        return SpatialStep(shot.Position, next with { Elevation = (int)elevation }, budget);

        static long BallisticLift(long t, long height)
        {
            long side = t <= one / 3 ? one / 3 : one - one / 3;
            long toApex = t - one / 3;
            return height - height * toApex * toApex / (side * side);
        }

        static long CruiseHeight(long t, int from, int to, long height)
        {
            long shelf = Math.Max(from, to) + height;
            long leg = one / 5;
            if (t < leg) return from + (shelf - from) * t / leg;
            if (t > one - leg) return to + (shelf - to) * (one - t) / leg;
            return shelf;
        }
    }
}
