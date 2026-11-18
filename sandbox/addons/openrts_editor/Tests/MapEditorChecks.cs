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

            var verifiedBundle = await plugin.BuildAsync(bundle: true, verify: true, selectedStarts: ["west"],
                selectedStartSet: MapScenario.InfantryStartSet);
            Check(verifiedBundle?.Verification is { Tick: 8, EntityCount: 6 }, "3D bundle verification failed: " + plugin.Status);
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
            await CheckReliefMap(plugin, tree, Check);
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

        await Check2DOperations(plugin, map, expectedShape, check);
        if (isometric) await CheckLocalPlaytest(plugin, map, check);

        GD.Print($"OPENRTS_MAP_2D:{sceneName}, {expected.Source.Width}x{expected.Source.Height}, {expected.Source.Slots.Count} starts, " +
            $"{expected.Source.Objects.Count} objects, gameplay={expected.GameplayFingerprint}, data={expected.DataFingerprint}");
    }

    private static async Task Check2DOperations(OpenRtsEditorPlugin plugin, OpenRtsMap2D map,
        TileSet.TileShapeEnum shape, Action<bool, string> check)
    {
        var fresh = OpenRtsEditorPlugin.NewMap2D(shape == TileSet.TileShapeEnum.Isometric);
        try
        {
            var source = MapSceneExtractor2D.Extract(fresh);
            check(fresh.Terrain!.Owner == fresh && fresh.Terrain.TileSet.TileShape == shape &&
                source.Terrain.Palette.SequenceEqual(["grass"]) && source.Slots.Count == 0 && source.Objects.Count == 0,
                "New 2D map did not have a complete, correctly projected starter terrain.");
            using var packed = new PackedScene();
            check(packed.Pack(fresh) == Error.Ok, "The 2D map template could not be packed.");
            var copy = packed.Instantiate<OpenRtsMap2D>();
            try { check(MapSceneExtractor2D.Extract(copy).Write() == source.Write(), "The template TileSet or layer was lost on save."); }
            finally { copy.Free(); }
            CheckTerrainTransitions(fresh, check);
        }
        finally { fresh.Free(); }

        string before = MapSceneExtractor2D.Extract(map).Write();
        var selection = EditorInterface.Singleton.GetSelection();
        var manager = plugin.GetUndoRedo();
        var undo = manager.GetHistoryUndoRedo(manager.GetObjectHistoryId(map));
        plugin.AddObject();
        var obj = (OpenRtsMapObject2D)selection.GetSelectedNodes().Single();
        string id = obj.ObjectId;
        check(id.Length > 0 && obj.Order > 0 && obj.OwnerSlot == "neutral" && obj.Owner == map,
            "Adding a 2D object did not assign its ID, order or ownership.");
        plugin.AddStart();
        var added = (OpenRtsMapStart2D)selection.GetSelectedNodes().Single();
        selection.AddNode(obj);
        plugin.DuplicateSelected();
        var duplicated = MapSceneExtractor2D.Extract(map);
        check(duplicated.Objects.Count == 9 && duplicated.Slots.Count == 4 &&
            duplicated.Objects.Select(item => item.Id).Distinct().Count() == 9 && duplicated.Slots.Select(slot => slot.Id).Distinct().Count() == 4,
            "Duplicating 2D markers lost their identity or order.");
        undo.Undo(); undo.Undo(); undo.Undo();
        check(MapSceneExtractor2D.Extract(map).Write() == before, "Undo did not restore the original 2D map.");
        undo.Redo(); undo.Redo();
        check(obj.ObjectId == id && obj.Owner == map && added.Owner == map, "2D marker redo changed its identity or owner.");
        int originalOrder = obj.Order;
        obj.ObjectId = ""; obj.Order = 0;
        string startId = added.StartId;
        added.StartId = "neutral";
        plugin.RepairIdentities();
        check(obj.ObjectId.Length != 0 && obj.Order != 0 && added.StartId != "neutral",
            "Repair did not fix invalid 2D marker IDs and order.");
        undo.Undo();
        check(obj.ObjectId == "" && obj.Order == 0 && added.StartId == "neutral", "Undo did not restore the 2D repair inputs.");
        obj.ObjectId = id; obj.Order = originalOrder; added.StartId = startId;
        undo.Undo(); undo.Undo();

        plugin.SelectById("patrol/tank");
        check(selection.GetSelectedNodes().Single() == map.GetNode("Tank"), "2D selection by ID failed.");
        var terrain = map.Terrain!;
        var cell = Vector2I.Zero;
        int sourceId = terrain.GetCellSourceId(cell), alternative = terrain.GetCellAlternativeTile(cell);
        var atlas = terrain.GetCellAtlasCoords(cell);
        terrain.EraseCell(cell);
        try
        {
            await plugin.BuildAsync();
            plugin.SelectErrorSource();
            check(plugin.Status.Contains("cell (0, 0)", StringComparison.Ordinal) && selection.GetSelectedNodes().Single() == terrain,
                "The 2D tile error did not identify its layer and cell.");
        }
        finally { terrain.SetCell(cell, sourceId, atlas, alternative); }
    }

    private static void CheckTerrainTransitions(OpenRtsMap2D map, Action<bool, string> check)
    {
        map.IsSkirmish = false;
        var layer = map.Terrain!;
        var catalog = GameManifest.Load(ProjectSettings.GlobalizePath(map.ContentManifest)).LoadUnits();
        layer.SetCell(new(2, 2), 0, new(4, 2)); // Keep the gameplay road beside the visual brush.
        var original = MapCompiler.Compile(MapSceneExtractor2D.Extract(map), catalog);
        var dirt = new Godot.Collections.Array<Vector2I>();
        for (int y = 3; y <= 6; y++)
            for (int x = 0; x <= y; x++) dirt.Add(new(x, y));
        layer.SetCellsTerrainConnect(dirt, 0, 1);
        var painted = MapCompiler.Compile(MapSceneExtractor2D.Extract(map), catalog);
        check(painted.Source.Visual.Palette.Any(id => id.StartsWith("grass-mud-", StringComparison.Ordinal)) &&
            painted.Source.Visual.Palette.Contains("mud") && painted.GameplayFingerprint == original.GameplayFingerprint &&
            painted.DataFingerprint != original.DataFingerprint, "Terrain Connect did not produce visual-only transitions.");

        for (int x = 0; x < 4; x++)
        {
            layer.SetCell(new(8 + x, 3), 0, new(x, 2));
            var data = layer.GetCellTileData(new(8 + x, 3));
            check(data.GetCustomData("TerrainId").AsString() == "grass" && data.GetCustomData("VisualId").AsString() == $"grass-mud-{32 + x}",
                "The explicit diagonal tile did not keep the expected gameplay and visual IDs.");
        }

        using var packed = new PackedScene();
        check(packed.Pack(map) == Error.Ok, "Terrain transition scene could not be packed.");
        var reopened = packed.Instantiate<OpenRtsMap2D>();
        try
        {
            var art = MapCompiler.Compile(MapSceneExtractor2D.Extract(reopened), catalog);
            check(art.GameplayFingerprint == original.GameplayFingerprint &&
                Enumerable.Range(0, 4).All(x => art.Map.VisualAt(8 + x, 3) == $"grass-mud-{32 + x}") &&
                art.Map.VisualIds.All(id => GD.Load<Texture2D>($"res://resources/terrain/{id}.tres") is not null),
                "Transition orientations or sandbox textures were lost during compilation.");
        }
        finally { reopened.Free(); }

        layer.SetCell(new(8, 3), 0, new(0, 2), (int)TileSetAtlasSource.TransformFlipH);
        bool rejected = false;
        try { _ = MapSceneExtractor2D.Extract(map); }
        catch (DeclarativeContent.ContentException error) when (error.Message.Contains("required direction", StringComparison.Ordinal)) { rejected = true; }
        check(rejected, "A flipped directional visual compiled with the unflipped VisualId.");
    }

    private static async Task CheckLocalPlaytest(OpenRtsEditorPlugin plugin, OpenRtsMap2D map, Action<bool, string> check)
    {
        var manifest = GameManifest.Load(ProjectSettings.GlobalizePath(map.ContentManifest));
        string path = ProjectSettings.GlobalizePath(map.CompiledPath);
        string previous = File.ReadAllText(path);
        var original = MapCompiler.Read(new(path, previous), manifest.LoadUnits());
        var west = map.GetNode<OpenRtsMapStart2D>("West");
        var candidates = Enumerable.Range(1, 5).Select(i => new OpenRtsMapStart2D
            { Name = $"Candidate{i}", StartId = $"candidate-{i}", Position = west.Position }).ToArray();
        foreach (var start in candidates) map.AddChild(start);
        const string set = MapScenario.InfantryStartSet;
        try
        {
            var validated = (await plugin.BuildAsync(verify: true, selectedStarts: ["west"], selectedStartSet: set))?.Playtest;
            check(validated is not null && validated.Slots.SequenceEqual(["west"]) && validated.StartSetPath == set,
                "Selecting one of more than four starts did not validate: " + plugin.Status);
            string good = File.ReadAllText(path);
            var published = MapCompiler.Read(new(path, good), manifest.LoadUnits());
            check(published.Source.Slots.Count == 7, "A playtest removed unused candidates from the artifact.");
            var receipt = LocalMapPlaytest.Read(validated!.Write());
            receipt.CheckMap(published);
            check(receipt.Prepare(published.Map, manifest).UnitCount == published.Source.Objects.Count + 3,
                "The launch receipt did not reproduce the selected starting set or active slots.");
            bool staleRejected = false;
            try { receipt.CheckMap(original); }
            catch (InvalidDataException) { staleRejected = true; }
            check(staleRejected, "A replaced artifact passed the local launch check.");
            bool changedSetRejected = false;
            try { (receipt with { StartSetPath = MapScenario.DefaultStartSet }).ReadStartingSet(manifest); }
            catch (InvalidDataException) { changedSetRejected = true; }
            check(changedSetRejected, "A replaced starting set passed the local launch check.");

            var refused = await plugin.BuildAsync(verify: true, selectedStarts: ["candidate-1", "west"], selectedStartSet: set);
            plugin.SelectErrorSource();
            check(refused is null && plugin.Status.Contains("west/infantry", StringComparison.Ordinal) && File.ReadAllText(path) == good &&
                EditorInterface.Singleton.GetSelection().GetSelectedNodes().Single() == west,
                "An occupied start was accepted, replaced the last valid artifact or did not select its marker: " + plugin.Status);
            refused = await plugin.BuildAsync(verify: true, selectedStarts: [], selectedStartSet: set);
            check(refused is null && plugin.Status.StartsWith("Failed", StringComparison.Ordinal) && File.ReadAllText(path) == good,
                "An empty choice replaced the last valid artifact.");
        }
        finally { foreach (var start in candidates) { map.RemoveChild(start); start.Free(); } }
        var repeat = await plugin.BuildAsync(verify: true, selectedStarts: ["west"], selectedStartSet: set);
        check(repeat is not null && File.ReadAllText(path) == previous, "Repeating a valid match failed after the refusal.");

        var tile = map.Terrain!.GetCellTileData(Vector2I.Zero)!;
        var visual = tile.GetCustomData("VisualId");
        tile.SetCustomData("VisualId", "p0-missing-terrain-resource");
        try
        {
            var failed = await plugin.BuildAsync(verify: true, selectedStarts: ["west"], selectedStartSet: set);
            check(failed is null && plugin.Status.Contains("Godot map verification failed", StringComparison.Ordinal) &&
                plugin.Status.Contains("p0-missing-terrain-resource", StringComparison.Ordinal) && File.ReadAllText(path) == previous,
                "Failed Godot verification replaced a previously published compiled map: " + plugin.Status);
        }
        finally { tile.SetCustomData("VisualId", visual); }
    }

    private static async Task CheckReliefMap(OpenRtsEditorPlugin plugin, SceneTree tree, Action<bool, string> check)
    {
        const string scenePath = "res://examples/maps/RockyPass.tscn";
        var editor = EditorInterface.Singleton;
        editor.OpenSceneFromPath(scenePath);
        OpenRtsMap2D? map = null;
        for (int i = 0; i < 120 && map is null; i++)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            if (editor.GetEditedSceneRoot() is OpenRtsMap2D opened && opened.SceneFilePath == scenePath)
                map = opened;
        }
        if (map is null) throw new InvalidOperationException("Rocky Pass did not open in the editor.");
        editor.EditNode(map);
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

        var catalog = GameManifest.Load(ProjectSettings.GlobalizePath(map.ContentManifest)).LoadUnits();
        var original = MapSceneExtractor2D.Extract(map);
        var expected = new OpenRTS.Samples.RampScenario(catalog);
        var compiled = MapCompiler.Compile(original, catalog);
        var grid = compiled.Map.Grid;
        check(grid.Width == expected.Grid.Width && grid.Height == expected.Grid.Height && grid.HasSlopedSurface &&
            Enumerable.Range(0, grid.CellCount).All(i => grid.SurfaceAt(i) == expected.Grid.SurfaceAt(i) &&
                grid.SurfaceRiseAt(i) == expected.Grid.SurfaceRiseAt(i) && grid.TerrainAt(i) == expected.Grid.TerrainAt(i)),
            "The authored terrain differs from the original Rocky Pass.");
        var players = new Dictionary<string, PlayerDefinition> { ["blue"] = new(1, "Синий") };
        using var reference = expected.CreateWorld(1);
        using var actual = compiled.Map.Prepare(players).CreateWorld();
        var referencePoses = new EntityPose[original.Objects.Count];
        var actualPoses = new EntityPose[original.Objects.Count];
        reference.CopyPoses(referencePoses);
        actual.CopyPoses(actualPoses);
        check(referencePoses.AsSpan().SequenceEqual(actualPoses), "The authored spawn positions, heights or owners differ.");

        var layer = map.Terrain!;
        var cell = new Vector2I(15, 14);
        var rampTile = layer.GetCellAtlasCoords(cell);
        int atlas = layer.GetCellSourceId(cell);
        layer.SetCell(cell, atlas, layer.GetCellAtlasCoords(Vector2I.Zero));
        var edited = MapCompiler.Compile(MapSceneExtractor2D.Extract(map), catalog);
        check(edited.Map.Grid.SurfaceAt(grid.Index(new(15, 14))).Elevation == 0 &&
            edited.GameplayFingerprint != compiled.GameplayFingerprint, "Repainting a ramp did not change the playable terrain.");
        layer.SetCell(cell, atlas, rampTile);

        await plugin.BuildAsync(sourceOnly: true);
        string path = ProjectSettings.GlobalizePath(map.CompiledPath);
        string sourcePath = path.Replace(".ortmap.json", ".mapsource.json", StringComparison.Ordinal);
        check(plugin.Status.StartsWith("Extracted", StringComparison.Ordinal) && File.Exists(sourcePath) &&
            MapCompiler.Compile(MapSource.Read(new(sourcePath, File.ReadAllText(sourcePath))), catalog).Write() == compiled.Write(),
            "Relief MapSource did not round-trip: " + plugin.Status);

        var verified = await plugin.BuildAsync(verify: true);
        check(plugin.Status.StartsWith("Compiled", StringComparison.Ordinal) && File.Exists(path) &&
            MapCompiler.Read(new(path, File.ReadAllText(path)), catalog).Write() == compiled.Write(),
            "The editor did not publish a compatible relief map: " + plugin.Status);
        check(verified?.Playtest is { StartSetPath: null } playtest && playtest.Slots.SequenceEqual(["blue"]) &&
            verified.Verification is { Tick: 8, EntityCount: 6 },
            "Rocky Pass did not validate as a fixture with its fixed placements.");
        GD.Print($"OPENRTS_RELIEF_MAP:{grid.Width}x{grid.Height}, {original.Objects.Count} objects, game={compiled.GameplayFingerprint}");
    }

}
#endif
