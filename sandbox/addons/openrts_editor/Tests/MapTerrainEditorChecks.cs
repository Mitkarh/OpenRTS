#if TOOLS
using Godot;
using OpenRTS.Content;
using OpenRTS.Core.Navigation;
using OpenRTS.Godot.Authoring;

internal static class MapTerrainEditorChecks
{
    internal static async Task Build(OpenRtsEditorPlugin plugin, OpenRtsMap root, Action<bool, string> check)
    {
        var undo = plugin.GetUndoRedo().GetHistoryUndoRedo(plugin.GetUndoRedo().GetObjectHistoryId(root));
        plugin.BeginStroke(new() { Terrain = ["road"], Visual = ["road"] }, 0);
        plugin.PaintCell(new(1, 2)); plugin.PaintCell(new(5, 2)); plugin.CommitStroke();
        await plugin.BuildAsync();
        check(plugin.Status.StartsWith("Compiled", StringComparison.Ordinal), "Painted scene did not compile: " + plugin.Status);
        string path = ProjectSettings.GlobalizePath(root.CompiledPath), good = File.ReadAllText(path);
        var compiled = MapCompiler.Read(new(path, good), OpenRtsEditorPlugin.LoadCatalog(root));
        check(compiled.Map.TerrainAt(3, 2) == "road" && compiled.Map.VisualAt(3, 2) == "road", "Compiler lost authored terrain/visual stamps.");
        File.WriteAllText(ProjectSettings.GlobalizePath("res://artifacts/maps/editor-terrain-check.ortmap.json"), good);
        plugin.BeginStroke(new() { Terrain = ["water"], Visual = ["water"] }, 0);
        plugin.PaintCell(new(12, 12)); plugin.CommitStroke(); await plugin.BuildAsync();
        check(plugin.Status.StartsWith("Failed", StringComparison.Ordinal) && File.ReadAllText(path) == good, "Invalid physical terrain placement replaced the good artifact.");
        undo.Undo(); undo.Undo();
        await plugin.BuildAsync();
        check(plugin.Status.StartsWith("Compiled", StringComparison.Ordinal), "Undo did not restore valid authored placement.");
    }

    internal static void Run(OpenRtsEditorPlugin plugin, OpenRtsMap root, Action<bool, string> check)
    {
        var manager = plugin.GetUndoRedo(); var undo = manager.GetHistoryUndoRedo(manager.GetObjectHistoryId(root));
        var catalog = OpenRtsEditorPlugin.LoadCatalog(root);
        double nodes = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);
        bool loaderFailed = false;
        try
        {
            var models = GameManifest.Load(ProjectSettings.GlobalizePath(root.ContentManifest)).LoadModels();
            _ = new OpenRTS.Godot.Rendering.SpriteWorldView(catalog, models, framesFor: _ => throw new ArgumentException("Missing test frames"));
        }
        catch (ArgumentException) { loaderFailed = true; }
        check(loaderFailed && Performance.GetMonitor(Performance.Monitor.ObjectNodeCount) == nodes, "Failed Models loader leaked its native preview node.");
        string original = MapSceneExtractor.Extract(root).Write();
        plugin.RefreshPreview();
        check(plugin.PreviewSprites >= 3 && plugin.Preview?.Node.Owner is null, "Models preview did not draw the three types, or became authored data.");
        check(MapSceneExtractor.Extract(root).Write() == original, "Preview introduced semantic markers/data.");
        var rifleman = root.GetNode<OpenRtsMapObject>("Patrol/Rifleman");
        rifleman.HealthVariant = new() { VariantId = "veteran", MaxHealth = 180 };
        rifleman.InitialHealth = 35;
        try
        {
            plugin.RefreshPreview();
            var veteran = MapSceneExtractor.Extract(root).Objects.Single(o => o.Id == "patrol/rifle");
            check(veteran.HealthVariant == new MapHealthVariant("veteran", 180) && veteran.InitialHealth == 35 && plugin.PreviewSprites >= 3,
                "Preview failed to use authored initial health and the inherited variant Model.");
        }
        finally
        {
            rifleman.HealthVariant = null;
            rifleman.InitialHealth = 0;
            plugin.RefreshPreview();
        }
        plugin.SelectById("patrol/tank");
        check(EditorInterface.Singleton.GetSelection().GetSelectedNodes().Single() == root.GetNode("Patrol/Tank"), "Semantic ID selection failed in a prefab.");
        var brush = new OpenRtsTerrainBrush { Terrain = ["road"], Visual = ["road"] };
        plugin.BeginStroke(brush, 0); plugin.PaintCell(new(1, 1)); plugin.PaintCell(new(5, 1));
        plugin.RefreshPreview();
        check(root.TerrainStrokes.Count == 0, "Uncommitted drag mutated the authored scene.");
        plugin.CommitStroke();
        check(root.TerrainStrokes.Count == 1 && root.TerrainStrokes[0].Cells.Count == 5, "One drag did not become one complete stroke.");
        string painted = MapSceneExtractor.Extract(root).Write();
        brush.Terrain[0] = "water";
        check(MapSceneExtractor.Extract(root).Write() == painted, "Editing the palette changed an already saved stroke.");
        undo.Undo(); check(MapSceneExtractor.Extract(root).Write() == original, "One undo did not revert the whole brush stroke.");
        undo.Redo(); check(MapSceneExtractor.Extract(root).Write() == painted, "Redo changed the saved brush data.");
        plugin.BeginStroke(brush, 0); plugin.PaintCell(new(6, 6)); plugin.CancelStroke();
        check(MapSceneExtractor.Extract(root).Write() == painted, "Cancelled brush affected authored data.");
        plugin.RefreshPreview();
        using (var packed = new PackedScene())
        {
            check(packed.Pack(root) == Error.Ok, "Preview scene pack failed.");
            var loaded = packed.Instantiate<OpenRtsMap>();
            check(loaded.GetNodeOrNull("OpenRTSEditorPreview") is null && MapSceneExtractor.Extract(loaded).Write() == painted, "Editor preview leaked into saved scene.");
            loaded.Free();
        }
        var priorTerrain = plugin.Preview!.Node.GetChildren().OfType<Node3D>().Single(n => n.Name == "TerrainTiles");
        string fingerprint = MapCompiler.Compile(MapSceneExtractor.Extract(root), catalog).GameplayFingerprint;
        root.VisualTextures["grass"] = GD.Load<Texture2D>("res://resources/terrain/mud.tres");
        plugin.RefreshPreview();
        check(!GodotObject.IsInstanceValid(priorTerrain) && MapCompiler.Compile(MapSceneExtractor.Extract(root), catalog).GameplayFingerprint == fingerprint, "Visual binding did not refresh independently of gameplay.");
        root.VisualTextures.Clear();
        undo.Undo(); plugin.RefreshPreview();
        check(MapSceneExtractor.Extract(root).Write() == original, "Terrain editor checks did not restore original source.");
        var circle = MapPreviewGeometry.Body(BodyShape.Circle(256));
        check(circle.Length == 64 && Math.Abs(circle.Max(p => p.X) - 0.25f) < 0.000001f, "Circle overlay is not the physical radius.");
        var box = MapPreviewGeometry.Body(BodyShape.Box(512, 1024));
        check(box.Length == 8 && box.Min(p => p.X) == -0.5f && box.Max(p => p.Z) == 1, "Box overlay has render rather than physical dimensions.");
        var footprint = new FootprintDefinition(3, 2, [new(0, 0, new(Traverse: false)), new(2, 1, new(CanStop: false), false)]);
        check(footprint.Cells.Select(c => MapPreviewGeometry.Cell(footprint, c)).Sum(c => c.Length) == 16 &&
            MapPreviewGeometry.Cell(footprint, footprint.Cells[1])[0].X == 0.5f, "Sparse footprint was expanded into its bounding rectangle.");
        var sample = MapSceneExtractor.Extract(root);
        var objects = sample.Objects.Select(o => o.Id == "patrol/tank" ? o with { Position = sample.Objects[0].Position } : o).ToArray();
        bool rejected = false;
        try { OpenRtsEditorPlugin.ValidateObjects(new(sample.Id, sample.IsSkirmish, sample.Width, sample.Height, sample.Terrain, sample.Visual, sample.Slots, objects), catalog); }
        catch (DeclarativeContent.ContentException) { rejected = true; }
        check(rejected, "Overlapping authored bodies passed physical validation.");
        OpenRtsEditorPlugin.ValidateObjects(new(sample.Id, true, sample.Width, sample.Height, sample.Terrain, sample.Visual,
            Enumerable.Range(0, 100).Select(i => new MapSlotSource("start-" + i, new(1024, 1024))).ToArray(), []), catalog);
        check(true, "Candidate starts must not become 100 active players during authored-object validation.");
        plugin.SelectById("patrol/rifle");
        // Give the synthetic camera a stable viewport when the editor switches between 2D and 3D scenes.
        var viewport = new SubViewport { Size = new(640, 480) };
        root.AddChild(viewport, false, Node.InternalMode.Back);
        var camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = 8, Position = new(4.5f, 10, 4.5f) };
        viewport.AddChild(camera); camera.LookAt(new(4.5f, 0, 4.5f), Vector3.Forward);
        try
        {
            plugin.SetPainting(true);
            Vector2 center = camera.UnprojectPosition(new Vector3(4.5f, 0, 4.5f));
            int result = plugin._Forward3DGuiInput(camera, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = center });
            plugin._Forward3DGuiInput(camera, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = center });
            check(result == (int)EditorPlugin.AfterGuiInput.Stop && root.TerrainStrokes.Count == 1 && root.TerrainStrokes[0].Cells[0] == new Vector2I(4, 4),
                $"Viewport ray did not paint cell (4, 4). Painted: {string.Join(", ", root.TerrainStrokes.SelectMany(stroke => stroke.Cells))}.");
            undo.Undo();
            check(plugin._Forward3DGuiInput(camera, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, AltPressed = true, Position = center }) == (int)EditorPlugin.AfterGuiInput.Pass, "Brush swallowed camera navigation.");
            plugin.BeginStroke(new() { Terrain = ["road"], Visual = ["road"] }, 0); plugin.PaintCell(new(2, 2));
            plugin._Input(new InputEventKey { Keycode = Key.Escape, Pressed = true }); plugin.CommitStroke();
            check(root.TerrainStrokes.Count == 0, "Escape did not cancel the in-progress stroke.");
        }
        finally { plugin.SetPainting(false); viewport.Free(); }
    }
}
#endif
