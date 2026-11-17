using DeclarativeContent;
using Godot;
using OpenRTS.Content;
using OpenRTS.Godot.Authoring;

internal static class MapTerrainChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var root = new OpenRtsMap { Size = new(8, 8), IsSkirmish = false };
        try
        {
            var catalog = GameManifest.Load(ProjectSettings.GlobalizePath("res://game.yaml")).LoadUnits();
            var baseline = MapCompiler.Compile(MapSceneExtractor.Extract(root), catalog);
            string Terrain(MapSource s, int x, int y) => s.Terrain.Palette[s.Terrain.Cells[y * s.Width + x]];
            string Visual(MapSource s, int x, int y) => s.Visual.Palette[s.Visual.Cells[y * s.Width + x]];
            void Reject(Action action, string message)
            { bool rejected = false; try { action(); } catch (Exception error) when (error is ContentException or ArgumentException) { rejected = true; } check(rejected, message); }
            OpenRtsTerrainStroke Stroke(string id, OpenRtsTerrainBrush brush, Vector2I cell, int turns = 0) =>
                new() { StrokeId = id, Brush = brush, Cells = new() { cell }, QuarterTurns = turns };
            var shaped = new OpenRtsTerrainBrush { Size = new(2, 1), Terrain = ["road", "water"], Visual = ["", "mud"] };
            root.TerrainStrokes.Add(Stroke("first", shaped, new(2, 2), 1));
            var source = MapSceneExtractor.Extract(root);
            check(Terrain(source, 2, 2) == "road" && Terrain(source, 2, 3) == "water" && Visual(source, 2, 2) == "grass" && Visual(source, 2, 3) == "mud", "Quarter-turn / independent brush layers failed.");
            var visual = Stroke("visual", new() { Terrain = [""], Visual = ["forest"] }, new(2, 2));
            root.TerrainStrokes.Add(visual);
            var before = MapCompiler.Compile(source, catalog);
            var after = MapCompiler.Compile(MapSceneExtractor.Extract(root), catalog);
            check(before.GameplayFingerprint == after.GameplayFingerprint && before.DataFingerprint != after.DataFingerprint, "Visual brush changed gameplay identity.");
            check(Terrain(after.Source, 2, 2) == "road" && Visual(after.Source, 2, 2) == "forest", "A visual-only stamp erased terrain.");
            root.TerrainStrokes.Reverse();
            check(Visual(MapSceneExtractor.Extract(root), 2, 2) == "forest", "Retain visual lost an earlier stroke.");
            root.TerrainStrokes.Clear();
            var first = Stroke("a", new() { Terrain = ["road"], Visual = ["road"] }, new(1, 1));
            var last = Stroke("b", new() { Terrain = ["water"], Visual = ["water"] }, new(1, 1));
            root.TerrainStrokes.Add(first); root.TerrainStrokes.Add(last);
            check(Terrain(MapSceneExtractor.Extract(root), 1, 1) == "water", "Later stroke did not win.");
            root.TerrainStrokes.Reverse();
            check(Terrain(MapSceneExtractor.Extract(root), 1, 1) == "road", "Stroke sequence is not significant.");
            root.TerrainStrokes.Clear();
            check(MapCompiler.Compile(MapSceneExtractor.Extract(root), catalog).Write() == baseline.Write(), "Removing strokes failed to restore the base layers.");
            root.TerrainStrokes.Add(Stroke("edge", shaped, new(7, 7), 1));
            Reject(() => MapSceneExtractor.Extract(root), "Out-of-bounds brush was clipped silently."); root.TerrainStrokes.Clear();
            root.TerrainStrokes.Add(Stroke("bad", new() { Size = new(2, 2) }, new(0, 0)));
            Reject(() => MapSceneExtractor.Extract(root), "Malformed brush arrays accepted."); root.TerrainStrokes.Clear();
            root.TerrainStrokes.Add(first); root.TerrainStrokes.Add(first);
            Reject(() => MapSceneExtractor.Extract(root), "Duplicate StrokeId accepted."); root.TerrainStrokes.Clear();
            root.TerrainStrokes.Add(Stroke("unknown", new() { Terrain = ["missing"] }, new(0, 0)));
            Reject(() => MapCompiler.Compile(MapSceneExtractor.Extract(root), catalog), "Unknown brush terrain accepted."); root.TerrainStrokes.Clear();
            root.TerrainStrokes.Add(Stroke("rot", shaped, new(1, 1), 4));
            Reject(() => MapSceneExtractor.Extract(root), "Unsupported brush rotation accepted."); root.TerrainStrokes.Clear();
            check(MapTerrain.Line(new(0, 0), new(5, 2)).Count() == 6 && MapTerrain.Line(new(5, 2), new(0, 0)).Last() == Vector2I.Zero, "Fast mouse samples leave holes or change endpoints.");
            check(!MapTerrain.Fits(new(7, 7), root.Size, new(2, 1), 1), "Rotated bounds test accepted a partial brush.");
            root.TerrainStrokes.Add(first);
            using var packed = new PackedScene(); check(packed.Pack(root) == Error.Ok, "Terrain scene packing failed.");
            var loaded = packed.Instantiate<OpenRtsMap>();
            try { check(MapSceneExtractor.Extract(root).Write() == MapSceneExtractor.Extract(loaded).Write(), "Brush resources did not survive scene serialization."); }
            finally { loaded.Free(); }
            check(shaped.Snapshot().Terrain is var copy && !ReferenceEquals(copy, shaped.Terrain), "Stroke retains mutable palette arrays from the tool preset.");
            var sample = GD.Load<PackedScene>("res://examples/maps/TerrainAuthoringMap.tscn").Instantiate<OpenRtsMap>();
            try
            {
                var result = MapCompiler.Compile(MapSceneExtractor.Extract(sample), catalog);
                check(result.Source.Objects.Count == 4 && result.Map.TerrainAt(2, 3) == "road", "Reusable brush sample did not load/compile.");
                string output = ProjectSettings.GlobalizePath("res://artifacts/maps/terrain-sample.ortmap.json");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!); MapCompiler.Save(result, output);
            }
            finally { sample.Free(); }
        }
        finally { root.Free(); }
    }
}
