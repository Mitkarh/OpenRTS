#if TOOLS
using Godot;
using OpenRTS.Content;
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
            plugin.ShowDock(); Check(plugin.DockAttached, "Editor dock was not attached.");
            root.CompiledPath = "res://artifacts/maps/editor-check.ortmap.json";
            var original = MapSceneExtractor.Extract(root).Write();
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
            string good = File.ReadAllText(path); root.GetNode<OpenRtsMapGroup>("Patrol").GetNode<OpenRtsMapObject>("Tank").TypeId = "missing";
            await plugin.BuildAsync();
            Check(File.ReadAllText(path) == good && plugin.Status.StartsWith("Failed", StringComparison.Ordinal), "Failed compile overwrote artifact.");
            root.GetNode<OpenRtsMapGroup>("Patrol").GetNode<OpenRtsMapObject>("Tank").TypeId = "tank";
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
}
#endif
