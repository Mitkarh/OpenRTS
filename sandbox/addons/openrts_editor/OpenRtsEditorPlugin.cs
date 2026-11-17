#if TOOLS
using Godot;
using OpenRTS.Content;

namespace OpenRTS.Godot.Authoring;

[Tool]
public partial class OpenRtsEditorPlugin : EditorPlugin
{
    private VBoxContainer _dock = null!;
    private EditorDock _editorDock = null!;
    private OptionButton _types = null!;
    private Label _status = null!;
    private OpenRtsMapGizmos _gizmos = null!;
    private CancellationTokenSource? _build;
    private int _generation;
    internal string Status => _status.Text;
    internal bool DockAttached => _editorDock.IsInsideTree();
    internal void ShowDock() => _editorDock.Open();

    public override void _EnterTree()
    {
        _dock = new() { Name = "OpenRTS", CustomMinimumSize = new(240, 0) };
        _dock.AddChild(new Label { Text = "OpenRTS map authoring" });
        Button("New map (empty scene)", CreateMap);
        Button("Load type catalog", LoadTypes);
        _types = new OptionButton(); _dock.AddChild(_types);
        Button("Add object", () => AddMarker(new OpenRtsMapObject { TypeId = _types.GetItemCount() == 0 ? "rifleman" : _types.GetItemText(_types.Selected) }));
        Button("Add start", () => AddMarker(new OpenRtsMapStart()));
        Button("Add reusable group", () => AddMarker(new OpenRtsMapGroup()));
        Button("Duplicate selected", DuplicateSelected);
        Button("Repair IDs and order", RepairIdentities);
        SetupTerrainTools();
        Button("Extract source", () => _ = BuildAsync(sourceOnly: true));
        Button("Compile map", () => _ = BuildAsync());
        Button("Cancel compilation", () => Invalidate("Cancelled; previous file retained."));
        _status = new Label { Text = "Open a map scene, or create an empty scene.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _dock.AddChild(_status);
        _editorDock = new() { Title = "OpenRTS", LayoutKey = "OpenRTSMapAuthoring", Global = true, DefaultSlot = EditorDock.DockSlot.LeftBr };
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _dock.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_dock); _editorDock.AddChild(scroll); AddDock(_editorDock);
        AddToolMenuItem("OpenRTS map authoring", Callable.From(ShowDock));
        _gizmos = new(); AddNode3DGizmoPlugin(_gizmos);
        SceneChanged += OnSceneChanged;
        GetUndoRedo().VersionChanged += OnEdited;
        if (OS.GetCmdlineUserArgs().Contains("--verify-map-editor"))
            Callable.From(() => { _ = MapEditorChecks.Run(this); }).CallDeferred();
    }

    private void Button(string text, Action action)
    {
        var button = new Button { Text = text };
        button.Pressed += () => { try { action(); } catch (Exception error) { Fail(error); } };
        _dock.AddChild(button);
    }

    public override void _ExitTree()
    {
        Invalidate("Plugin disabled.");
        CancelStroke(); ClearPreview(); _selectedTransforms.Clear();
        SceneChanged -= OnSceneChanged;
        GetUndoRedo().VersionChanged -= OnEdited;
        RemoveNode3DGizmoPlugin(_gizmos); _gizmos.Dispose();
        RemoveToolMenuItem("OpenRTS map authoring");
        RemoveDock(_editorDock); _editorDock.QueueFree();
    }

    private OpenRtsMap Root() => EditorInterface.Singleton.GetEditedSceneRoot() as OpenRtsMap
        ?? throw new ArgumentException("The edited scene root must be OpenRtsMap. Open examples/maps/AuthoringMap.tscn or create a new empty scene.");

    private void CreateMap()
    {
        if (EditorInterface.Singleton.GetEditedSceneRoot() is not null) throw new ArgumentException("Open an empty scene first (Scene → New Scene).");
        EditorInterface.Singleton.AddRootNode(new OpenRtsMap { Name = "Map" });
    }

    private void LoadTypes()
    {
        CancelStroke();
        var catalog = LoadCatalog(Root());
        _types.Clear();
        foreach (var unit in catalog.Units) _types.AddItem(unit.Id);
        LoadPreviewCatalog(Root(), catalog);
        _status.Text = $"{catalog.Units.Count} types. Select a marker to edit its properties in Inspector.";
    }

    internal static UnitCatalog LoadCatalog(OpenRtsMap root) => GameManifest.Load(ProjectFile(root.ContentManifest, ".yaml")).LoadUnits();

    private static string ProjectFile(string path, string suffix)
    {
        if (!path.StartsWith("res://", StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Expected a project res:// path ending with {suffix}.");
        string full = Path.GetFullPath(ProjectSettings.GlobalizePath(path));
        string directory = Path.GetFullPath(ProjectSettings.GlobalizePath("res://"));
        if (!full.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Path must stay inside the project.");
        return full;
    }

    internal void AddMarker(Node3D marker)
    {
        var root = Root();
        var selected = EditorInterface.Singleton.GetSelection().GetSelectedNodes();
        Node parent = selected.Count == 1 && (selected[0] == root || selected[0] is OpenRtsMapGroup) ? selected[0] : root;
        marker.Set(MapAuthoringIdentity.IdField(marker), MapAuthoringIdentity.NewId());
        if (marker is not OpenRtsMapStart) marker.Set("Order", MapAuthoringIdentity.NextOrder(parent));
        marker.Name = marker is OpenRtsMapObject obj ? obj.TypeId : marker is OpenRtsMapStart ? "Start" : "Group";
        if (parent == root) marker.Position = new(root.Size.X / 2f, 0, root.Size.Y / 2f);
        AttachUndo(root, parent, [marker], "Add OpenRTS marker");
    }

    internal void DuplicateSelected()
    {
        var root = Root();
        var selection = EditorInterface.Singleton.GetSelection().GetSelectedNodes().ToArray();
        var nodes = selection.Where(n => n is OpenRtsMapObject or OpenRtsMapStart or OpenRtsMapGroup)
            .Where(n => !selection.Any(other => other != n && other.IsAncestorOf(n))).ToArray();
        if (nodes.Length == 0) throw new ArgumentException("Select one or more object/start/group markers.");
        var nextOrders = new Dictionary<Node, int>();
        var copies = new List<(Node Parent, Node Copy)>();
        try
        {
            foreach (var node in nodes)
            {
                if (!root.IsAncestorOf(node)) throw new ArgumentException("Select markers in the current map scene.");
                var copy = node.Duplicate(); copies.Add((node.GetParent(), copy));
                copy.Set(MapAuthoringIdentity.IdField(copy), MapAuthoringIdentity.NewId());
                if (copy is not OpenRtsMapStart)
                {
                    Node context = MapAuthoringIdentity.Context(node);
                    int order = nextOrders.TryGetValue(context, out int next) ? next : MapAuthoringIdentity.NextOrder(context);
                    copy.Set("Order", order); nextOrders[context] = checked(order + 1);
                }
            }
        }
        catch { foreach (var pair in copies) pair.Copy.Free(); throw; }
        var undo = GetUndoRedo(); undo.CreateAction("Duplicate OpenRTS markers", customContext: root);
        foreach (var pair in copies) AddAttachment(undo, root, pair.Parent, pair.Copy);
        undo.CommitAction();
    }

    private void AttachUndo(OpenRtsMap root, Node parent, Node[] nodes, string action)
    {
        var undo = GetUndoRedo(); undo.CreateAction(action, customContext: root);
        foreach (var node in nodes) AddAttachment(undo, root, parent, node);
        undo.CommitAction();
        var selection = EditorInterface.Singleton.GetSelection(); selection.Clear();
        foreach (var node in nodes) selection.AddNode(node);
        EditorInterface.Singleton.EditNode(nodes[0]);
    }

    private void AddAttachment(EditorUndoRedoManager undo, Node root, Node parent, Node child)
    {
        undo.AddDoMethod(parent, Node.MethodName.AddChild, child, true);
        undo.AddDoMethod(this, MethodName.SetSceneOwner, child, root);
        undo.AddUndoMethod(parent, Node.MethodName.RemoveChild, child);
        undo.AddDoReference(child);
    }

    private void SetSceneOwner(Node node, Node root)
    {
        node.Owner = root;
        foreach (Node child in node.GetChildren())
            if (child.Owner is null || child.Owner == root) SetSceneOwner(child, root);
    }

    internal void RepairIdentities()
    {
        var root = Root(); var undo = GetUndoRedo();
        var edits = new List<(Node Node, string Field, Variant Before, Variant After)>();
        MapAuthoringIdentity.Repair(root, (node, field, value) => edits.Add((node, field, node.Get(field), value)));
        if (edits.Count == 0) { _status.Text = "IDs and order need no repair."; return; }
        undo.CreateAction("Repair OpenRTS identities", customContext: root);
        foreach (var edit in edits)
        {
            undo.AddDoProperty(edit.Node, edit.Field, edit.After);
            undo.AddUndoProperty(edit.Node, edit.Field, edit.Before);
        }
        undo.CommitAction();
    }

    internal async Task BuildAsync(bool sourceOnly = false)
    {
        try { CommitStroke(); } catch (Exception error) { Fail(error); return; }
        Invalidate("Extracting…");
        var stop = _build = new(); int generation = _generation;
        try
        {
            var root = Root();
            string outputPath = root.CompiledPath, contentPath = root.ContentManifest;
            string output = ProjectFile(root.CompiledPath, ".ortmap.json");
            var catalog = LoadCatalog(root);
            var source = MapSceneExtractor.Extract(root);
            string snapshot = source.Write();
            _status.Text = "Compiling…";
            var compiled = await Task.Run(() =>
            {
                var result = MapCompiler.Compile(source, catalog, stop.Token);
                if (!sourceOnly) ValidateObjects(source, catalog);
                stop.Token.ThrowIfCancellationRequested(); return result;
            }, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
            if (generation != _generation || !GodotObject.IsInstanceValid(root) || EditorInterface.Singleton.GetEditedSceneRoot() != root ||
                root.CompiledPath != outputPath || root.ContentManifest != contentPath || MapSceneExtractor.Extract(root).Write() != snapshot ||
                LoadCatalog(root).Summary.Fingerprint != catalog.Summary.Fingerprint)
                throw new OperationCanceledException("Scene/content changed during compilation.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (sourceOnly)
            {
                output = output[..^".ortmap.json".Length] + ".mapsource.json";
                // Atomic writer reused for a separate inspection document; this is not a bundle manifest.
                SaveSource(compiled.Source.Write(), output, stop.Token);
            }
            else MapCompiler.Save(compiled, output, stop.Token);
            _status.Text = $"{(sourceOnly ? "Extracted" : "Compiled")} {compiled.Map.Id}\n{compiled.Source.Objects.Count} objects, {compiled.Source.Slots.Count} slots\n{ProjectSettings.LocalizePath(output)}\n{(sourceOnly ? "Structural validation only." : "Placed objects validated; starting armies are checked at match creation.")}";
            EditorInterface.Singleton.GetResourceFilesystem().Scan();
        }
        catch (OperationCanceledException) { if (generation == _generation) _status.Text = "Cancelled/stale; previous file retained."; }
        catch (Exception error) { if (generation == _generation) Fail(error); }
        finally { if (_build == stop) _build = null; stop.Dispose(); }
    }

    private static void SaveSource(string text, string path, CancellationToken stop)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new System.Text.UTF8Encoding(false));
            stop.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void OnEdited() { _refresh = true; Invalidate("Changed; compile again."); }
    private void OnSceneChanged(Node node) { CancelStroke(); ClearPreview(); _selectedTransforms.Clear(); _types.Clear(); _refresh = true; Invalidate("Scene changed; load its type catalog."); }
    internal void Invalidate(string message) { _generation++; _build?.Cancel(); _status.Text = message; }
    private void Fail(Exception error) { _lastError = error; _status.Text = "Failed: " + error.Message; GD.PushError("OpenRTS authoring: " + error.Message); }
}
#endif
