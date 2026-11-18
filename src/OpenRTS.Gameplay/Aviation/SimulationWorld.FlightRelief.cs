using OpenRTS.Core.Navigation;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private bool _flightTerrainInitialized;
    private NavigationGrid? _flightTerrain;
    private int _flightTerrainFloor;

    private void InitializeFlightTerrain()
    {
        if (_flightTerrainInitialized) return;
        _flightTerrainInitialized = true;
        if (_baseGrid is not { HasSurface: true } grid) return;

        int floor = int.MaxValue, peak = int.MinValue;
        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            int elevation = grid.SurfaceAt(cell).Elevation;
            int rise = HalfRise(grid.SurfaceRiseAt(cell));
            floor = Math.Min(floor, elevation - rise);
            peak = Math.Max(peak, elevation + rise);
            var cut = grid.SurfaceCutAt(cell);
            if (cut != default) peak = Math.Max(peak, cut.UpperElevation);
        }
        if (peak == floor) return;
        _flightTerrain = grid;
        _flightTerrainFloor = floor;
    }

    private static int HalfRise(SurfaceRise rise) => (Math.Abs(rise.X) + Math.Abs(rise.Y) + 1) / 2;

    private bool FlightSupportClear(WorldPosition point, BodyShape body) =>
        _flightTerrain is null || point.Elevation == GroundHeight(point) && FlightTerrainPeak(point, point, body) <= point.Elevation;

    private int FlightCruiseAt(WorldPosition point, UnitDefinition type)
    {
        var flight = type.Flight!;
        int surface = GroundHeight(point), radius = flight.TerrainLookaheadRadius;
        if (_flightTerrain is not { } grid || radius == 0) return surface + flight.CruiseAltitude;

        int halfWidth = type.Body.HalfWidth, halfHeight = type.Body.HalfHeight;
        int clearance = type.Body.Kind == BodyKind.Box
            ? (int)SquareRoot((ulong)((long)halfWidth * halfWidth + (long)halfHeight * halfHeight)) + 1
            : halfWidth;
        int reach = radius + clearance, size = grid.CellSize;
        for (int y = Math.Max(0, (point.Y - reach) / size); y <= Math.Min(grid.Height - 1, (point.Y + reach) / size); y++)
            for (int x = Math.Max(0, (point.X - reach) / size); x <= Math.Min(grid.Width - 1, (point.X + reach) / size); x++)
            {
                int cell = y * grid.Width + x;
                long u = point.X - x * size, v = point.Y - y * size;
                var cut = grid.SurfaceCutAt(cell);
                Consider(grid.SurfaceAt(cell).Elevation + HalfRise(grid.SurfaceRiseAt(cell)), BoxDistanceSquared(u, v, size));
                if (cut != default)
                {
                    if (cut.UpperCorner is SurfaceCorner.NorthEast or SurfaceCorner.SouthEast) u = size - u;
                    if (cut.UpperCorner is SurfaceCorner.SouthWest or SurfaceCorner.SouthEast) v = size - v;
                    Consider(cut.UpperElevation, TriangleDistanceSquared(u, v, size));
                }
            }
        return surface + flight.CruiseAltitude;

        void Consider(int elevation, long distanceSquared)
        {
            if (elevation <= surface || distanceSquared >= (long)reach * reach) return;
            int distance = Math.Max(0, (int)SquareRoot((ulong)distanceSquared) - clearance);
            int candidate = elevation - (int)((long)(elevation - _flightTerrainFloor) * distance / radius);
            surface = Math.Max(surface, candidate);
        }
    }

    private static long BoxDistanceSquared(long x, long y, int size)
    {
        long dx = x - Math.Clamp(x, 0, size), dy = y - Math.Clamp(y, 0, size);
        return dx * dx + dy * dy;
    }

    private static long TriangleDistanceSquared(long x, long y, int size)
    {
        if (Math.Clamp(x, 0, size) + Math.Clamp(y, 0, size) <= size)
            return BoxDistanceSquared(x, y, size);
        if (x - y >= size) return (x - size) * (x - size) + y * y;
        if (y - x >= size) return x * x + (y - size) * (y - size);
        long diagonal = x + y - size;
        return diagonal * diagonal / 2;
    }

    private int FlightTerrainPeak(WorldPosition from, WorldPosition to, BodyShape body)
    {
        var grid = _flightTerrain!;
        int peak = int.MinValue, size = grid.CellSize;
        int left = Math.Min(from.X, to.X) - body.HalfWidth, right = Math.Max(from.X, to.X) + body.HalfWidth;
        int top = Math.Min(from.Y, to.Y) - body.HalfHeight, bottom = Math.Max(from.Y, to.Y) + body.HalfHeight;
        for (int y = Math.Max(0, (top - 1) / size); y <= Math.Min(grid.Height - 1, bottom / size); y++)
            for (int x = Math.Max(0, (left - 1) / size); x <= Math.Min(grid.Width - 1, right / size); x++)
            {
                int cell = y * grid.Width + x;
                var bounds = new WorldBox(x * size, y * size, (x + 1) * size, (y + 1) * size);
                if (!BodyGeometry.SweptBox(from, to, bounds, body)) continue;
                peak = Math.Max(peak, grid.SurfaceAt(cell).Elevation + HalfRise(grid.SurfaceRiseAt(cell)));
                var cut = grid.SurfaceCutAt(cell);
                if (cut.UpperElevation > peak && cut != default &&
                    BodyGeometry.SweptTriangle(from, to, bounds, cut.UpperCorner, body))
                    peak = cut.UpperElevation;
            }
        return peak;
    }

    private WorldPosition FollowFlightRelief(WorldPosition from, WorldPosition next, UnitDefinition type)
    {
        var flight = type.Flight!;
        if (_flightTerrain is null)
            return next with { Elevation = Approach(from.Elevation,
                Math.Max(GroundHeight(from), GroundHeight(next)) + flight.CruiseAltitude, flight.VerticalSpeed) };
        int clearance = Math.Min(flight.CruiseAltitude, FlightGroundContactHeight + 1);
        int required = FlightTerrainPeak(from, next, type.Body) + clearance;
        int target = Math.Max(required, FlightCruiseAt(next, type));
        int elevation = Approach(from.Elevation, target, flight.VerticalSpeed);
        // With a short radius or a steep rise, finish gaining clearance before advancing.
        return (Math.Min(from.Elevation, elevation) >= required ? next : from) with { Elevation = elevation };
    }
}
