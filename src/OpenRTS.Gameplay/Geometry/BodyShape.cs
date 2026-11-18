namespace OpenRTS.Core.Navigation;

public enum BodyKind { None, Circle, Box }

/// <summary>Ground collision shape; boxes are axis aligned, independent of cosmetic facing.</summary>
public readonly record struct BodyShape
{
    public BodyKind Kind { get; }
    public int HalfWidth { get; }
    public int HalfHeight { get; }
    public static BodyShape Default => Circle(256);
    public static BodyShape None => default;
    private BodyShape(BodyKind kind, int x, int y) { Kind = kind; HalfWidth = x; HalfHeight = y; }
    public static BodyShape Circle(int radius)
    { if (radius is < 16 or > 4096) throw new ArgumentOutOfRangeException(nameof(radius)); return new(BodyKind.Circle, radius, radius); }
    public static BodyShape Box(int halfWidth, int halfHeight)
    { if (halfWidth is < 16 or > 4096 || halfHeight is < 16 or > 4096) throw new ArgumentOutOfRangeException(nameof(halfWidth)); return new(BodyKind.Box, halfWidth, halfHeight); }
    internal void WriteState(BinaryWriter writer) { writer.Write((int)Kind); writer.Write(HalfWidth); writer.Write(HalfHeight); }
}

internal static class BodyGeometry
{
    internal static bool SweptTriangle(WorldPosition from, WorldPosition to, WorldBox bounds,
        SurfaceCorner corner, BodyShape body)
    {
        bool west = corner is SurfaceCorner.NorthWest or SurfaceCorner.SouthWest;
        bool north = corner is SurfaceCorner.NorthWest or SurfaceCorner.NorthEast;
        var a = new WorldPosition(west ? bounds.MinX : bounds.MaxX, north ? bounds.MinY : bounds.MaxY);
        var b = new WorldPosition(west ? bounds.MaxX : bounds.MinX, a.Y);
        var c = new WorldPosition(a.X, north ? bounds.MaxY : bounds.MinY);
        return Inside(from) || Inside(to) || SweptSegment(from, to, a, b, body) ||
            SweptSegment(from, to, a, c, body) || SweptSegment(from, to, b, c, body);

        bool Inside(WorldPosition p) => p.X >= bounds.MinX && p.X <= bounds.MaxX &&
            p.Y >= bounds.MinY && p.Y <= bounds.MaxY &&
            Math.Abs(p.X - a.X) + Math.Abs(p.Y - a.Y) <= bounds.MaxX - bounds.MinX;
    }

    internal static bool SweptSegment(WorldPosition from, WorldPosition to, WorldPosition a, WorldPosition b, BodyShape body)
    {
        if (body.Kind == BodyKind.Circle)
        {
            int radius = body.HalfWidth;
            if (Separated(1, 0, radius, radius) || Separated(0, 1, radius, radius)) return false;
            // For disjoint segments the closest pair contains an endpoint of at least one segment.
            return Intersects(0, 0) || SegmentCircle(from, to, a, radius) || SegmentCircle(from, to, b, radius) ||
                SegmentCircle(a, b, from, radius) || SegmentCircle(a, b, to, radius);
        }
        return Intersects(body.HalfWidth, body.HalfHeight);

        bool Intersects(int halfWidth, int halfHeight) => !Separated(1, 0, halfWidth, halfHeight) &&
            !Separated(0, 1, halfWidth, halfHeight) && !Separated(a.Y - b.Y, b.X - a.X, halfWidth, halfHeight) &&
            !Separated(from.Y - to.Y, to.X - from.X, halfWidth, halfHeight);

        bool Separated(long nx, long ny, int halfWidth, int halfHeight)
        {
            long p = nx * from.X + ny * from.Y, q = nx * to.X + ny * to.Y;
            long u = nx * a.X + ny * a.Y, v = nx * b.X + ny * b.Y;
            long padding = Math.Abs(nx) * halfWidth + Math.Abs(ny) * halfHeight;
            return Math.Max(p, q) + padding < Math.Min(u, v) || Math.Max(u, v) < Math.Min(p, q) - padding;
        }
    }

    internal static bool SweptBox(WorldPosition a, WorldPosition b, WorldBox box, BodyShape body)
    {
        if (body.Kind == BodyKind.Box) return TerrainGeometry.IntersectsBox(a, b, box.MinX - body.HalfWidth, box.MinY - body.HalfHeight, box.MaxX + body.HalfWidth, box.MaxY + body.HalfHeight);
        int r = body.HalfWidth;
        if (TerrainGeometry.IntersectsBox(a, b, box.MinX - r, box.MinY, box.MaxX + r, box.MaxY) ||
            TerrainGeometry.IntersectsBox(a, b, box.MinX, box.MinY - r, box.MaxX, box.MaxY + r)) return true;
        return SegmentCircle(a, b, new(box.MinX, box.MinY), r) || SegmentCircle(a, b, new(box.MaxX, box.MinY), r) ||
            SegmentCircle(a, b, new(box.MinX, box.MaxY), r) || SegmentCircle(a, b, new(box.MaxX, box.MaxY), r);
    }
    internal static bool SegmentCircle(WorldPosition a, WorldPosition b, WorldPosition center, int radius, bool strict = false)
    {
        long x = (long)a.X - center.X, y = (long)a.Y - center.Y, dx = (long)b.X - a.X, dy = (long)b.Y - a.Y;
        Int128 length = (Int128)dx * dx + (Int128)dy * dy, dot = -((Int128)x * dx + (Int128)y * dy);
        long r2 = (long)radius * radius;
        if (dot <= 0 || length == 0) return strict ? (Int128)x * x + (Int128)y * y < r2 : (Int128)x * x + (Int128)y * y <= r2;
        if (dot >= length) return strict ? (Int128)(x + dx) * (x + dx) + (Int128)(y + dy) * (y + dy) < r2 : (Int128)(x + dx) * (x + dx) + (Int128)(y + dy) * (y + dy) <= r2;
        return strict ? ((Int128)x * x + (Int128)y * y - r2) * length < dot * dot : ((Int128)x * x + (Int128)y * y - r2) * length <= dot * dot;
    }
    internal static bool Overlap(WorldPosition a0, WorldPosition a1, BodyShape a, WorldPosition b0, WorldPosition b1, BodyShape b)
    {
        if (a.Kind == BodyKind.None || b.Kind == BodyKind.None) return false;
        var start = new WorldPosition(a0.X - b0.X, a0.Y - b0.Y);
        var end = new WorldPosition(a1.X - b1.X, a1.Y - b1.Y);
        if (a.Kind == BodyKind.Circle && b.Kind == BodyKind.Circle) return SegmentCircle(start, end, default, a.HalfWidth + b.HalfWidth, strict: true);
        if (a.Kind == BodyKind.Box && b.Kind == BodyKind.Box)
            return TerrainGeometry.IntersectsBox(start, end, -a.HalfWidth - b.HalfWidth, -a.HalfHeight - b.HalfHeight, a.HalfWidth + b.HalfWidth, a.HalfHeight + b.HalfHeight);
        var circle = a.Kind == BodyKind.Circle ? a : b; var box = a.Kind == BodyKind.Box ? a : b;
        return SweptBox(start, end, new(-box.HalfWidth, -box.HalfHeight, box.HalfWidth, box.HalfHeight), circle);
    }
    internal static WorldPosition ClosestPoint(WorldPosition center, BodyShape shape, WorldPosition from)
    {
        if (shape.Kind == BodyKind.Box) return new WorldBox(center.X - shape.HalfWidth, center.Y - shape.HalfHeight, center.X + shape.HalfWidth, center.Y + shape.HalfHeight).Closest(from);
        long x = (long)from.X - center.X, y = (long)from.Y - center.Y;
        long distance = (long)SimulationWorld.SquareRoot((ulong)(x * x + y * y));
        if (distance <= shape.HalfWidth) return from;
        return new(center.X + (int)(x * shape.HalfWidth / distance), center.Y + (int)(y * shape.HalfWidth / distance), center.Elevation);
    }
    internal static int HitBoxFraction(WorldPosition from, WorldPosition to, WorldBox box)
    {
        long low = 0, high = 65536;
        if (!Axis(from.X, to.X - from.X, box.MinX, box.MaxX) || !Axis(from.Y, to.Y - from.Y, box.MinY, box.MaxY)) return 65537;
        return (int)low;
        bool Axis(int value, int delta, int minimum, int maximum)
        {
            if (delta == 0) return value >= minimum && value <= maximum;
            long a = ((long)minimum - value) * 65536, b = ((long)maximum - value) * 65536;
            if (delta < 0) { (a, b) = (-b, -a); delta = -delta; }
            long entry = -Floor(-a, delta), exit = Floor(b, delta);
            low = Math.Max(low, entry); high = Math.Min(high, exit); return low <= high;
        }
        static long Floor(long value, long divisor) => value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);
    }
}
