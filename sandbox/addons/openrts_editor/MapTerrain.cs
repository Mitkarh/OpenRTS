using DeclarativeContent;
using Godot;
using OpenRTS.Content;

namespace OpenRTS.Godot.Authoring;

public static class MapTerrain
{
    public const int MaximumBrushSide = 64, MaximumStamps = 1000000;

    public static (MapLayer Terrain, MapLayer Visual) Compose(OpenRtsMap root, OpenRtsTerrainStroke? pending = null)
    {
        ContentException Error(string field, string message) => new(new(root.SceneFilePath.Length == 0 ? "<scene>" : root.SceneFilePath, 1, 1), field, message);
        var names = new List<string>();
        byte TerrainId(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw Error("Terrain", "Terrain ID cannot be empty.");
            int at = names.IndexOf(name);
            if (at >= 0) return (byte)at;
            if (names.Count >= 256) throw Error("Terrain", "TerrainLayout supports at most 256 distinct terrain IDs.");
            names.Add(name); return (byte)(names.Count - 1);
        }
        int count = checked(root.Size.X * root.Size.Y);
        byte basis = TerrainId(root.TerrainId);
        var layout = new TerrainLayout(root.Size.X, root.Size.Y, Enumerable.Repeat(basis, count), Enumerable.Repeat(root.VisualId, count));
        var ids = new HashSet<string>(StringComparer.Ordinal); int stamps = 0; long work = 0;
        foreach (var stroke in pending is null ? root.TerrainStrokes : root.TerrainStrokes.Concat([pending]))
        {
            if (stroke is null || string.IsNullOrWhiteSpace(stroke.StrokeId) || !ids.Add(stroke.StrokeId))
                throw Error("TerrainStrokes", "Each stroke needs a nonempty unique StrokeId.");
            string path = $"TerrainStrokes.{stroke.StrokeId}";
            var brush = stroke.Brush ?? throw Error(path + ".Brush", "Missing brush.");
            if (brush.Size.X is < 1 or > MaximumBrushSide || brush.Size.Y is < 1 or > MaximumBrushSide ||
                brush.Terrain.Length != brush.Size.X * brush.Size.Y || brush.Visual.Length != brush.Terrain.Length ||
                stroke.QuarterTurns is < 0 or > 3 || stroke.Cells.Count == 0 || (long)stamps + stroke.Cells.Count > MaximumStamps)
                throw Error(path, "Invalid brush dimensions/layers, quarter-turn or stamp count (maximum 1 million).");
            stamps += stroke.Cells.Count;
            var cells = new List<TerrainBrushCell>();
            for (int i = 0; i < brush.Terrain.Length; i++)
            {
                string terrain = brush.Terrain[i], visual = brush.Visual[i];
                if (terrain is null || visual is null || terrain.Length != 0 && string.IsNullOrWhiteSpace(terrain) || visual.Length != 0 && string.IsNullOrWhiteSpace(visual))
                    throw Error(path + ".Brush", "Use an empty string to retain a layer; whitespace/null IDs are invalid.");
                if (terrain.Length != 0 || visual.Length != 0)
                    cells.Add(new(i % brush.Size.X, i / brush.Size.X, terrain.Length == 0 ? null : TerrainId(terrain), visual.Length == 0 ? null : visual));
            }
            if (cells.Count == 0) throw Error(path + ".Brush", "Brush has no painted cells.");
            work += (long)cells.Count * stroke.Cells.Count;
            if (work > OpenRTS.Core.Navigation.NavigationGrid.MaximumCells) throw Error(path, "Brush composition exceeds the 16 Mi painted-cell budget.");
            foreach (var cell in stroke.Cells)
                try { layout.Apply(cell.X, cell.Y, brush.Size.X, brush.Size.Y, cells, stroke.QuarterTurns); }
                catch (ArgumentException error) { throw Error(path + ".Cells", error.Message); }
        }
        var visuals = layout.VisualIds(); var visualIndices = visuals.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i, StringComparer.Ordinal);
        var terrainCells = new int[count]; var visualCells = new int[count];
        for (int y = 0; y < root.Size.Y; y++) for (int x = 0; x < root.Size.X; x++)
        { int i = y * root.Size.X + x; terrainCells[i] = layout.TerrainAt(x, y); visualCells[i] = visualIndices[layout.VisualAt(x, y)]; }
        return (new(names, terrainCells), new(visuals, visualCells));
    }

    public static bool Fits(Vector2I cell, Vector2I mapSize, Vector2I brushSize, int turns) =>
        cell.X >= 0 && cell.Y >= 0 && (long)cell.X + (turns % 2 == 0 ? brushSize.X : brushSize.Y) <= mapSize.X &&
        (long)cell.Y + (turns % 2 == 0 ? brushSize.Y : brushSize.X) <= mapSize.Y;

    /// <summary>All cells between mouse samples; a fast drag must not leave gaps.</summary>
    public static IEnumerable<Vector2I> Line(Vector2I from, Vector2I to)
    {
        int x = from.X, y = from.Y, dx = Math.Abs(to.X - x), dy = -Math.Abs(to.Y - y);
        int sx = x < to.X ? 1 : -1, sy = y < to.Y ? 1 : -1, error = dx + dy;
        while (true)
        {
            yield return new(x, y); if (x == to.X && y == to.Y) yield break;
            int e = 2 * error; if (e >= dy) { error += dy; x += sx; } if (e <= dx) { error += dx; y += sy; }
        }
    }
}
