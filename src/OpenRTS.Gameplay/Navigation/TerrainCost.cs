namespace OpenRTS.Core.Navigation;

internal static class TerrainCost
{
    // Doubled integral of the centerline's price. Q32 intersections avoid floating-point route decisions.
    internal static long Segment(NavigationGrid grid, Locomotor profile, WorldPosition a, WorldPosition b)
    {
        if (grid.HasSplitTerrain && (a.Y > b.Y || a.Y == b.Y && a.X > b.X)) (a, b) = (b, a);
        long dx = (long)b.X - a.X, dy = (long)b.Y - a.Y;
        if (dx == 0 && dy == 0) return 0;
        long length = (long)SimulationWorld.SquareRoot((ulong)(dx * dx + dy * dy));
        if (length * length < dx * dx + dy * dy) length++;
        const long one = 1L << 32;
        int size = grid.CellSize, x = Math.Min(a.X / size, grid.Width - 1), y = Math.Min(a.Y / size, grid.Height - 1);
        int sx = Math.Sign(dx), sy = Math.Sign(dy);
        long t = 0; Int128 sum = 0;
        while (t < one)
        {
            long tx = dx == 0 ? one : ((sx > 0 ? (long)(x + 1) * size - a.X : (long)a.X - x * size) * one / Math.Abs(dx));
            long ty = dy == 0 ? one : ((sy > 0 ? (long)(y + 1) * size - a.Y : (long)a.Y - y * size) * one / Math.Abs(dy));
            long next = Math.Min(one, Math.Min(tx, ty));
            int cell = y * grid.Width + x;
            var rule = profile.At(grid, cell);
            if (grid.IsBlocked(cell)) return long.MaxValue;
            if (grid.HasSplitTerrain && grid.SurfaceCutAt(cell) is { UpperTerrain: not null } cut)
            {
                long start = cut.SignedDistance(a.X - x * size, a.Y - y * size, size);
                long end = cut.SignedDistance(b.X - x * size, b.Y - y * size, size);
                long upperTime;
                if (start == end) upperTime = start >= 0 ? next - t : 0;
                else
                {
                    long crossing = Math.Clamp(-start * one / (end - start), t, next);
                    upperTime = end > start ? next - crossing : crossing - t;
                }
                long lowerTime = next - t - upperTime;
                var upper = profile.At(grid, cell, upper: true);
                if (lowerTime > 0 && !rule.Traverse || upperTime > 0 && !upper.Traverse) return long.MaxValue;
                sum += ((Int128)lowerTime * rule.Cost + (Int128)upperTime * upper.Cost) * length * 2;
            }
            else
            {
                if (!rule.Traverse) return long.MaxValue;
                sum += (Int128)(next - t) * length * rule.Cost * 2;
            }
            if (next == one) break;
            if (tx == next) x += sx;
            if (ty == next) y += sy;
            t = next;
        }
        return checked((long)((sum + one - 1) / one));
    }

    internal static long Edge(NavigationGrid grid, Locomotor profile, int a, int b)
        => profile.At(grid, a).Cost + profile.At(grid, b).Cost;
}
