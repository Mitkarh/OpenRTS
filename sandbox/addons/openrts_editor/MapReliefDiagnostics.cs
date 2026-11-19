using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;

namespace OpenRTS.Godot.Authoring;

internal static class MapReliefDiagnostics
{
    internal readonly record struct Edge(Vector2I Cell, Vector2I Neighbor, WorldPosition From, WorldPosition To,
        long FirstDelta, long SecondDelta, bool Ramp)
    {
        public override string ToString() => $"{Cell} → {Neighbor} · {(Ramp ? "Ramp seam" : "Height step")} · " +
            $"Δ {FirstDelta / 2m:0.#}, {SecondDelta / 2m:0.#}";
    }

    internal static IEnumerable<Edge> Scan(NavigationGrid grid)
    {
        if (!grid.HasSurface) yield break;
        int size = grid.CellSize;
        for (int y = 0; y < grid.Height; y++)
            for (int x = 0; x < grid.Width; x++)
            {
                var cell = new Vector2I(x, y);
                int first = y * grid.Width + x;
                if (x + 1 < grid.Width && Compare(first, first + 1, cell, Vector2I.Right,
                    new((x + 1) * size, y * size), new((x + 1) * size, (y + 1) * size)) is { } east) yield return east;
                if (y + 1 < grid.Height && Compare(first, first + grid.Width, cell, Vector2I.Down,
                    new(x * size, (y + 1) * size), new((x + 1) * size, (y + 1) * size)) is { } south) yield return south;
            }

        Edge? Compare(int first, int second, Vector2I cell, Vector2I direction, WorldPosition from, WorldPosition to)
        {
            long a = Height(first, from, to) - Height(second, from, to);
            long b = Height(first, to, from) - Height(second, to, from);
            if (a == 0 && b == 0) return null;
            var left = grid.SurfaceRiseAt(first); var right = grid.SurfaceRiseAt(second);
            bool ramp = direction.X != 0 ? left.X != 0 || right.X != 0 : left.Y != 0 || right.Y != 0;
            return new(cell, cell + direction, from, to, a, b, ramp);
        }

        long Height(int index, WorldPosition end, WorldPosition other)
        {
            var rise = grid.SurfaceRiseAt(index);
            if (rise == default) return 2L * grid.SurfaceElevationAtEdge(index, end.X, end.Y, other.X, other.Y);
            int x = end.X - index % grid.Width * size, y = end.Y - index / grid.Width * size;
            // Edge endpoints sit on cell corners. Doubled heights retain half-unit seams.
            return 2L * grid.SurfaceAt(index).Elevation + (x == 0 ? -(long)rise.X : rise.X) + (y == 0 ? -(long)rise.Y : rise.Y);
        }
    }
}
