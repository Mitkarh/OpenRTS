#if TOOLS
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Godot.Authoring;

internal static class MapEditorChecks
{
    private static bool _running;
    internal static async Task Run(OpenRtsEditorPlugin plugin)
    {
        if (_running) return;
        _running = true;
        var tree = plugin.GetTree(); int checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        try
        {
            var editor = EditorInterface.Singleton;
            editor.OpenSceneFromPath("res://examples/maps/AuthoringMap.tscn");
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            OpenRtsMap? root = null;
            for (int i = 0; i < 120 && root is null; i++)
            {
                root = editor.GetEditedSceneRoot() as OpenRtsMap;
                if (root is null) await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }
            if (root is null) throw new InvalidOperationException("Sample did not load as a tool OpenRtsMap.");
            await CheckMap2D(plugin, tree, Check, "InitialPropertiesMap2D", isometric: false);
            editor.OpenSceneFromPath("res://examples/maps/AuthoringMap.tscn");
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            root = editor.GetEditedSceneRoot() as OpenRtsMap
                ?? throw new InvalidOperationException("The 3D sample did not reopen after the 2D check.");
            plugin.ShowDock(); Check(plugin.DockAttached, "Editor dock was not attached.");
            root.CompiledPath = "res://artifacts/maps/editor-check.ortmap.json";
            var original = MapSceneExtractor.Extract(root).Write();
            MapTerrainEditorChecks.Run(plugin, root, Check);
            var undo = plugin.GetUndoRedo().GetHistoryUndoRedo(plugin.GetUndoRedo().GetObjectHistoryId(root));
            editor.GetSelection().Clear();
            var obj = new OpenRtsMapObject { TypeId = "rifleman" };
            plugin.AddMarker(obj);
            Check(obj.GetParent() == root && obj.Owner == root && obj.ObjectId.Length != 0, "Add marker ownership/ID failed.");
            string id = obj.ObjectId;
            undo.Undo(); Check(obj.GetParent() is null, "Add undo failed.");
            undo.Redo(); Check(obj.ObjectId == id && obj.GetParent() == root, "Add redo changed identity.");
            var manager = plugin.GetUndoRedo();
            manager.CreateAction("Inspector position check", customContext: root);
            manager.AddDoProperty(obj, Node3D.PropertyName.Position, new Vector3(7.25f, 0, 8.5f));
            manager.AddUndoProperty(obj, Node3D.PropertyName.Position, obj.Position); manager.CommitAction();
            Check(MapSceneExtractor.Extract(root).Objects.Single(o => o.Id == id).Position == new OpenRTS.Core.WorldPosition(7424, 8704), "Inspector position was not extracted exactly.");
            undo.Undo();
            editor.GetSelection().Clear(); editor.GetSelection().AddNode(obj);
            plugin.DuplicateSelected();
            Check(MapSceneExtractor.Extract(root).Objects.Count == 5, "Duplicate action failed.");
            undo.Undo(); Check(MapSceneExtractor.Extract(root).Objects.Count == 4, "Duplicate undo failed.");
            undo.Undo(); Check(MapSceneExtractor.Extract(root).Write() == original, "Add undo did not restore original data.");

            var group = root.GetNode<OpenRtsMapGroup>("Patrol");
            editor.GetSelection().Clear(); editor.GetSelection().AddNode(group);
            plugin.DuplicateSelected();
            Check(MapSceneExtractor.Extract(root).Objects.Count == 6, "Prefab duplication lost descendants/IDs.");
            using (var packed = new PackedScene())
            {
                Check(packed.Pack(root) == Error.Ok, "Duplicated prefab scene could not be packed.");
                var copy = packed.Instantiate<OpenRtsMap>();
                Check(MapSceneExtractor.Extract(copy).Write() == MapSceneExtractor.Extract(root).Write(), "Duplicated prefab IDs/children were not saved.");
                copy.Free();
            }
            undo.Undo(); Check(MapSceneExtractor.Extract(root).Write() == original, "Prefab duplicate undo changed source.");

            var invalid = new OpenRtsMapObject(); root.AddChild(invalid); invalid.Owner = root;
            plugin.RepairIdentities();
            Check(invalid.ObjectId.Length > 0 && MapSceneExtractor.Extract(root).Objects.Count == 4, "Repair failed.");
            string repaired = invalid.ObjectId;
            undo.Undo(); Check(invalid.ObjectId == "", "Repair undo failed.");
            undo.Redo(); Check(invalid.ObjectId == repaired, "Repair redo generated another ID.");
            root.RemoveChild(invalid); invalid.Free();

            string saved = "res://artifacts/maps/editor-saved.tscn";
            Directory.CreateDirectory(Path.GetDirectoryName(ProjectSettings.GlobalizePath(saved))!);
            editor.SaveSceneAs(saved);
            var reopened = GD.Load<PackedScene>(saved).Instantiate<OpenRtsMap>();
            Check(MapSceneExtractor.Extract(reopened).Write() == original, "Saved scene lost marker data or prefab ownership.");
            reopened.Free();

            await plugin.BuildAsync(bundle: true);
            string bundlePath = ProjectSettings.GlobalizePath(root.CompiledPath).Replace(".ortmap.json", SceneMapBundle.Suffix, StringComparison.Ordinal);
            Check(plugin.Status.StartsWith("Compiled ", StringComparison.Ordinal) && File.Exists(bundlePath), "Bundle build failed: " + plugin.Status);
            string published = File.ReadAllText(bundlePath);
            var bundle = SceneMapBundle.Read(new(bundlePath, published));
            Sandbox.CheckSceneMapDependencies(bundle);
            Check(bundle.Scene == saved && bundle.LoadMap(bundlePath, file => new(file, File.ReadAllText(file)), OpenRtsEditorPlugin.LoadCatalog(root)).Map.Id == root.MapId,
                "Bundle does not pair the saved scene and its map.");
            var staleBuild = plugin.BuildAsync(bundle: true, saveScene: false);
            root.GetNode<OpenRtsMapGroup>("Patrol").Position += Vector3.Right;
            await staleBuild;
            root.GetNode<OpenRtsMapGroup>("Patrol").Position -= Vector3.Right;
            Check(File.ReadAllText(bundlePath) == published, "A stale authoring edit replaced the bundle pointer.");

            string path = ProjectSettings.GlobalizePath(root.CompiledPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "previous-file");
            var pending = plugin.BuildAsync(); root.MapId += "-changed";
            await pending;
            Check(File.ReadAllText(path) == "previous-file", "Stale job replaced the current artifact.");
            root.MapId = "scene-authoring-sample";
            pending = plugin.BuildAsync(); plugin.Invalidate("Test cancellation"); await pending;
            Check(File.ReadAllText(path) == "previous-file", "Cancelled job replaced the current artifact.");
            await plugin.BuildAsync();
            Check(plugin.Status.StartsWith("Compiled", StringComparison.Ordinal), "Editor compile failed: " + plugin.Status);
            var compiled = MapCompiler.Read(new(path, File.ReadAllText(path)), OpenRtsEditorPlugin.LoadCatalog(root));
            Check(compiled.Source.Objects.Count == 3, "Editor output did not contain three types.");
            await plugin.BuildAsync(sourceOnly: true);
            Check(File.Exists(path.Replace(".ortmap.json", ".mapsource.json", StringComparison.Ordinal)), "Source extraction did not write a document.");
            await MapTerrainEditorChecks.Build(plugin, root, Check);
            string good = File.ReadAllText(path); root.GetNode<OpenRtsMapGroup>("Patrol").GetNode<OpenRtsMapObject>("Tank").TypeId = "missing";
            await plugin.BuildAsync();
            Check(File.ReadAllText(path) == good && plugin.Status.StartsWith("Failed", StringComparison.Ordinal), "Failed compile overwrote artifact.");
            plugin.SelectErrorSource();
            Check(editor.GetSelection().GetSelectedNodes().Single() == root.GetNode("Patrol/Tank"), "Diagnostic did not select its source marker.");
            root.GetNode<OpenRtsMapGroup>("Patrol").GetNode<OpenRtsMapObject>("Tank").TypeId = "tank";
            string editedScenePath = root.SceneFilePath;
            await CheckMap2D(plugin, tree, Check, "InitialPropertiesMapIsometric", isometric: true);
            editor.OpenSceneFromPath(editedScenePath);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            Check(editor.GetEditedSceneRoot() == root, "The 3D sample was not restored for the cancellation check.");
            pending = plugin.BuildAsync();
            editor.SetPluginEnabled("openrts_editor", false);
            await pending;
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            Check(!editor.IsPluginEnabled("openrts_editor") && File.ReadAllText(path) == good, "Plugin disable did not cancel its pending job.");
            editor.SetPluginEnabled("openrts_editor", true);
            Check(editor.IsPluginEnabled("openrts_editor"), "Plugin could not be enabled again.");
            // Do not save the edited example; all check outputs live in ignored artifacts/.
            GD.Print($"OPENRTS_MAP_EDITOR_CHECKS:{checks} passed"); tree.Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); tree.Quit(1); }
    }

    private static async Task CheckMap2D(OpenRtsEditorPlugin plugin, SceneTree tree, Action<bool, string> check,
        string sceneName, bool isometric)
    {
        using var baselineScene = GD.Load<PackedScene>("res://examples/maps/InitialPropertiesMap.tscn");
        var baseline = baselineScene.Instantiate<OpenRtsMap>();
        CompiledMap expected;
        try
        {
            expected = MapCompiler.Compile(MapSceneExtractor.Extract(baseline), OpenRtsEditorPlugin.LoadCatalog(baseline));
        }
        finally { baseline.Free(); }

        var editor = EditorInterface.Singleton;
        string scenePath = $"res://examples/maps/{sceneName}.tscn";
        editor.OpenSceneFromPath(scenePath);
        OpenRtsMap2D? map = null;
        for (int i = 0; i < 120 && map is null; i++)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (editor.GetEditedSceneRoot() is OpenRtsMap2D opened && opened.SceneFilePath == scenePath)
                map = opened;
        }
        if (map is null) throw new InvalidOperationException($"{sceneName} did not open in the editor.");
        editor.EditNode(map);
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

        await plugin.BuildAsync(sourceOnly: true);
        string path = ProjectSettings.GlobalizePath(map.CompiledPath);
        string sourcePath = path.Replace(".ortmap.json", ".mapsource.json", StringComparison.Ordinal);
        check(plugin.Status.StartsWith("Extracted", StringComparison.Ordinal) && File.Exists(sourcePath) && File.ReadAllText(sourcePath) == expected.Source.Write(),
            "The 2D scene did not extract the same canonical source as B5: " + plugin.Status);
        await plugin.BuildAsync();
        check(plugin.Status.StartsWith("Compiled", StringComparison.Ordinal) && File.Exists(path) && File.ReadAllText(path) == expected.Write(),
            "The 2D scene did not compile to the same bytes as B5: " + plugin.Status);

        var terrain = map.Terrain!;
        var expectedShape = isometric ? TileSet.TileShapeEnum.Isometric : TileSet.TileShapeEnum.Square;
        check(terrain.TileSet.TileShape == expectedShape, $"{sceneName} uses the wrong native TileSet shape.");
        var cell = new Vector2I(2, 3);
        var road = terrain.GetCellSourceId(cell);
        var roadAtlas = terrain.GetCellAtlasCoords(cell);
        terrain.SetCell(cell, terrain.GetCellSourceId(Vector2I.Zero), terrain.GetCellAtlasCoords(Vector2I.Zero));
        var wounded = map.GetNode<OpenRtsMapObject2D>("Wounded");
        var east = map.GetNode<OpenRtsMapStart2D>("East");
        var woundedPosition = wounded.Position;
        var eastPosition = east.Position;
        wounded.Position += isometric ? new Vector2(0.25f, 0.125f) : new Vector2(0.25f, 0);
        east.Position += isometric ? new Vector2(-0.03125f, 0.015625f) : new Vector2(0, 0.03125f);
        var edited = MapSceneExtractor2D.Extract(map);
        check(edited.Terrain.Palette[edited.Terrain.Cells[3 * edited.Width + 2]] == "grass",
            "Tile painting was lost during extraction.");
        check(edited.Objects.Single(obj => obj.Id == "wounded").Position == new WorldPosition(12296, 16384) &&
            edited.Slots.Single(slot => slot.Id == "east").Position == new WorldPosition(27136, 19457),
            "The precise positions of the moved object and start were not extracted.");
        check(terrain.LocalToMap(east.Position) == new Vector2I(26, 19),
            "The moved start is not in the expected native tile.");
        terrain.SetCell(cell, road, roadAtlas);
        wounded.Position = woundedPosition;
        east.Position = eastPosition;

        using var packed = new PackedScene();
        check(packed.Pack(map) == Error.Ok, "The 2D scene could not be packed.");
        var reopened = packed.Instantiate<OpenRtsMap2D>();
        try
        {
            var catalog = GameManifest.Load(ProjectSettings.GlobalizePath(reopened.ContentManifest)).LoadUnits();
            check(MapCompiler.Compile(MapSceneExtractor2D.Extract(reopened), catalog).Write() == expected.Write(),
                "Reopening the 2D scene changed compiled data.");
        }
        finally { reopened.Free(); }

        GD.Print($"OPENRTS_MAP_2D:{sceneName}, {expected.Source.Width}x{expected.Source.Height}, {expected.Source.Slots.Count} starts, " +
            $"{expected.Source.Objects.Count} objects, gameplay={expected.GameplayFingerprint}, data={expected.DataFingerprint}");
    }
}
#endif
