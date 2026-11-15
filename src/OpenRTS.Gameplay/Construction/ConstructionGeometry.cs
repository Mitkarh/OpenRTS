namespace OpenRTS.Core.Construction;

internal static class ConstructionGeometry
{
    internal static bool Overlaps(FootprintDefinition first, WorldPosition a, FootprintDefinition second, WorldPosition b)
        => first.Overlaps(a, second, b);

    internal static bool Touches(FootprintDefinition footprint, WorldPosition site, WorldPosition from, WorldPosition to, BodyShape body)
    {
        var bounds = new WorldBox(site.X - footprint.HalfWidth, site.Y - footprint.HalfHeight, site.X + footprint.HalfWidth, site.Y + footprint.HalfHeight);
        if (!BodyGeometry.SweptBox(from, to, bounds, body)) return false;
        foreach (var cell in footprint.Cells)
            if (BodyGeometry.SweptBox(from, to, footprint.Bounds(cell, site), body)) return true;
        return false;
    }

    internal static IEnumerable<WorldPosition> Exits(FootprintDefinition footprint, WorldPosition site, WorldPosition from, BodyShape body, int width, int depth)
        => PerimeterExits(footprint.HalfWidth, footprint.HalfHeight, site, from, body, width, depth);

    internal static IEnumerable<WorldPosition> Approaches(FootprintDefinition footprint, WorldPosition site, WorldPosition from,
        BodyShape body, int range, int width, int depth)
    {
        for (int margin = 0; ; margin = margin == 0 ? Math.Min(256, range - 1) : Math.Min(margin * 2, range - 1))
        {
            foreach (var point in PerimeterExits(footprint.HalfWidth + margin, footprint.HalfHeight + margin, site, from, body, width, depth))
                yield return point;
            if (margin >= range - 1) yield break;
        }
    }

    private static IEnumerable<WorldPosition> PerimeterExits(int halfWidth, int halfHeight, WorldPosition site, WorldPosition from,
        BodyShape body, int width, int depth)
    {
        var primary = Exits(halfWidth, halfHeight, site, from, body, width, depth).ToArray();
        foreach (var point in primary.OrderBy(p => ObjectGeometry.Squared(from, p))) yield return point;

        int left = site.X - halfWidth - body.HalfWidth - 1;
        int right = site.X + halfWidth + body.HalfWidth + 1;
        int top = site.Y - halfHeight - body.HalfHeight - 1;
        int bottom = site.Y + halfHeight + body.HalfHeight + 1;
        var perimeter = new List<WorldPosition>();
        // shortcut: fallback samples the bounded footprint perimeter every 256 units; narrow authored entrances may need exact points.
        for (int x = left; x < right; x += 256) { Add(new(x, top)); Add(new(x, bottom)); }
        for (int y = top + 256; y < bottom; y += 256) { Add(new(left, y)); Add(new(right, y)); }
        Add(new(right, top)); Add(new(right, bottom));
        foreach (var point in perimeter.OrderBy(p => ObjectGeometry.Squared(from, p))) yield return point;

        void Add(WorldPosition point)
        {
            if (point.X >= body.HalfWidth && point.Y >= body.HalfHeight &&
                point.X <= width - body.HalfWidth && point.Y <= depth - body.HalfHeight && !primary.Contains(point))
                perimeter.Add(point);
        }
    }

    internal static IEnumerable<WorldPosition> Exits(int halfWidth, int halfHeight, WorldPosition site, WorldPosition from, BodyShape body, int width, int depth)
    {
        int left = site.X - halfWidth - body.HalfWidth - 1;
        int right = site.X + halfWidth + body.HalfWidth + 1;
        int top = site.Y - halfHeight - body.HalfHeight - 1;
        int bottom = site.Y + halfHeight + body.HalfHeight + 1;
        int x = Math.Clamp(from.X, left, right), y = Math.Clamp(from.Y, top, bottom);
        WorldPosition[] candidates = [new(left, y), new(right, y), new(x, top), new(x, bottom)];
        return candidates.Where(p => p.X >= body.HalfWidth && p.Y >= body.HalfHeight &&
            p.X <= width - body.HalfWidth && p.Y <= depth - body.HalfHeight);
    }
}
