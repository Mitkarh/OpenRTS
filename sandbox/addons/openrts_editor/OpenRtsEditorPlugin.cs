#if TOOLS
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;

namespace OpenRTS.Godot.Authoring;

[Tool]
public partial class OpenRtsEditorPlugin : EditorPlugin
{
    internal sealed record MapBuild(string Path, string Mod, LocalMapPlaytest? Playtest, SimulationReport? Verification);

    private VBoxContainer _dock = null!;
    private EditorDock _editorDock = null!;
    private OptionButton _types = null!;
    private OptionButton _owners = null!;
    private Button _addGroup = null!, _buildBundle = null!;
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
        Button("New top-down map", () => CreateMap2D(isometric: false));
        Button("New isometric map", () => CreateMap2D(isometric: true));
        Button("Load type catalog", LoadTypes);
        _types = new OptionButton(); _dock.AddChild(_types);
        _owners = new OptionButton { TooltipText = "Owner of the next object" }; _dock.AddChild(_owners);
        _owners.Pressed += RefreshOwners;
        RefreshOwners();
        Button("Add object", AddObject);
        Button("Add start", AddStart);
        _addGroup = Button("Add reusable group", () => AddMarker(new OpenRtsMapGroup()));
        Button("Duplicate selected", DuplicateSelected);
        Button("Repair IDs and order", RepairIdentities);
        SetupTerrainTools();
        Button("Extract source", () => _ = BuildAsync(sourceOnly: true));
        Button("Compile map", () => _ = BuildAsync());
        _buildBundle = Button("Build scene-map bundle", () => _ = BuildAsync(bundle: true));
        Button("Build and play map", () => ChoosePlaytest(verify: false));
        Button("Build and verify map", () => ChoosePlaytest(verify: true));
        Button("Cancel build", () => Invalidate("Cancelled; previous file retained."));
        _status = new Label { Text = "Open a map scene, or create an empty scene.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _dock.AddChild(_status);
        _editorDock = new() { Title = "OpenRTS", LayoutKey = "OpenRTSMapAuthoring", Global = true, DefaultSlot = EditorDock.DockSlot.LeftBr };
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _dock.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(_dock); _editorDock.AddChild(scroll); AddDock(_editorDock);
        SetupPlaytest();
        UpdateMapTools(EditorInterface.Singleton.GetEditedSceneRoot());
        AddToolMenuItem("OpenRTS map authoring", Callable.From(ShowDock));
        _gizmos = new(); AddNode3DGizmoPlugin(_gizmos);
        SceneChanged += OnSceneChanged;
        GetUndoRedo().VersionChanged += OnEdited;
        if (OS.GetCmdlineUserArgs().Any(arg => arg == "--build-scene-map" || arg.StartsWith("--build-map=", StringComparison.Ordinal)))
            Callable.From(() => { _ = BuildFromCommandLine(); }).CallDeferred();
        if (OS.GetCmdlineUserArgs().Contains("--verify-map-editor"))
            Callable.From(() => { _ = MapEditorChecks.Run(this); }).CallDeferred();
    }

    private Button Button(string text, Action action, VBoxContainer? parent = null)
    {
        var button = new Button { Text = text };
        button.Pressed += () => { try { action(); } catch (Exception error) { Fail(error); } };
        (parent ?? _dock).AddChild(button);
        return button;
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

    private static Node EditedMap() => EditorInterface.Singleton.GetEditedSceneRoot() switch
    {
        Node root when root is OpenRtsMap or OpenRtsMap2D => root,
        _ => throw new ArgumentException("Open an OpenRtsMap or OpenRtsMap2D scene.")
    };

    private static string ContentPath(Node root) => root is OpenRtsMap map ? map.ContentManifest : ((OpenRtsMap2D)root).ContentManifest;

    private static MapSource Extract(Node root) => root is OpenRtsMap map ? MapSceneExtractor.Extract(map) : MapSceneExtractor2D.Extract((OpenRtsMap2D)root);

    private void CreateMap()
    {
        if (EditorInterface.Singleton.GetEditedSceneRoot() is not null) throw new ArgumentException("Open an empty scene first (Scene → New Scene).");
        EditorInterface.Singleton.AddRootNode(new OpenRtsMap { Name = "Map" });
    }

    private void LoadTypes()
    {
        CancelStroke();
        var root = EditedMap();
        var catalog = GameManifest.Load(ProjectFile(ContentPath(root), ".yaml")).LoadUnits();
        _types.Clear();
        foreach (var unit in catalog.Units) _types.AddItem(unit.Id);
        if (root is OpenRtsMap map) LoadPreviewCatalog(map, catalog);
        RefreshOwners();
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
        var root = EditedMap();
        var selection = EditorInterface.Singleton.GetSelection().GetSelectedNodes().ToArray();
        var nodes = selection.Where(n => root is OpenRtsMap2D
                ? n.GetParent() == root && n is OpenRtsMapObject2D or OpenRtsMapStart2D
                : root.IsAncestorOf(n) && n is OpenRtsMapObject or OpenRtsMapStart or OpenRtsMapGroup)
            .Where(n => !selection.Any(other => other != n && other.IsAncestorOf(n))).ToArray();
        if (nodes.Length == 0) throw new ArgumentException("Select one or more object/start/group markers.");
        var nextOrders = new Dictionary<Node, IEnumerator<int>>();
        var copies = new List<(Node Parent, Node Copy)>();
        try
        {
            foreach (var node in nodes)
            {
                var copy = node.Duplicate(); copies.Add((node.GetParent(), copy));
                copy.Set(MapAuthoringIdentity.IdField(copy), MapAuthoringIdentity.NewId());
                if (MapAuthoringIdentity.HasOrder(copy))
                {
                    Node context = MapAuthoringIdentity.Context(node);
                    if (!nextOrders.TryGetValue(context, out var orders))
                        nextOrders.Add(context, orders = MapAuthoringIdentity.AvailableOrders(context).GetEnumerator());
                    orders.MoveNext();
                    copy.Set("Order", orders.Current);
                }
            }
        }
        catch { foreach (var pair in copies) pair.Copy.Free(); throw; }
        finally { foreach (var orders in nextOrders.Values) orders.Dispose(); }
        var undo = GetUndoRedo(); undo.CreateAction("Duplicate OpenRTS markers", customContext: root);
        foreach (var pair in copies) AddAttachment(undo, root, pair.Parent, pair.Copy);
        undo.CommitAction();
        if (root is OpenRtsMap2D) SelectMarkers(copies.Select(pair => pair.Copy).ToArray());
    }

    private void AttachUndo(Node root, Node parent, Node[] nodes, string action)
    {
        var undo = GetUndoRedo(); undo.CreateAction(action, customContext: root);
        foreach (var node in nodes) AddAttachment(undo, root, parent, node);
        undo.CommitAction();
        SelectMarkers(nodes);
    }

    private static void SelectMarkers(Node[] nodes)
    {
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
        var root = EditedMap(); var undo = GetUndoRedo();
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

    internal async Task<MapBuild?> BuildAsync(bool sourceOnly = false, bool bundle = false, bool play = false, bool saveScene = true,
        string[]? selectedStarts = null, string? selectedStartSet = null, bool verify = false)
    {
        try { CommitStroke(); } catch (Exception error) { Fail(error); return null; }
        _lastError = null;
        Invalidate("Extracting…");
        var stop = _build = new(); int generation = _generation;
        string? staged = null;
        try
        {
            var edited = EditedMap();
            var root = edited as OpenRtsMap;
            var map2D = edited as OpenRtsMap2D;
            if (bundle && root is null)
                throw new ArgumentException("A 2D scene publishes a .ortmap.json compiled map. Use Build and verify map or --format=compiled.");
            bundle |= play && root is not null;

            string OutputPath() => root?.CompiledPath ?? map2D!.CompiledPath;
            GameManifest Manifest() => GameManifest.Load(ProjectFile(ContentPath(edited), ".yaml"));

            string outputPath = OutputPath(), contentPath = ContentPath(edited);
            if (bundle && (!SceneMapBundle.ResourcePath(root!.SceneFilePath, ".tscn") || saveScene && EditorInterface.Singleton.SaveScene() != Error.Ok))
                throw new IOException("Save the map as a project .tscn before building its bundle.");
            var dependencies = bundle ? SceneDependencies(root!.SceneFilePath) : null;
            string output = ProjectFile(outputPath, ".ortmap.json");
            var manifest = Manifest();
            var catalog = manifest.LoadUnits();
            var source = Extract(edited);
            string snapshot = source.Write();
            void CheckInputs()
            {
                stop.Token.ThrowIfCancellationRequested();
                if (generation != _generation || !GodotObject.IsInstanceValid(edited) ||
                    EditorInterface.Singleton.GetEditedSceneRoot() != edited || OutputPath() != outputPath ||
                    ContentPath(edited) != contentPath || Extract(edited).Write() != snapshot)
                    throw new OperationCanceledException("Scene changed during compilation.");
            }

            _status.Text = "Compiling…";
            var (compiled, receipt) = await Task.Run(() =>
            {
                var result = MapCompiler.Compile(source, catalog, stop.Token);
                LocalMapPlaytest? receipt = null;
                if (play || verify) receipt = LocalMapPlaytest.Validate(result, manifest, selectedStarts, selectedStartSet);
                else if (!sourceOnly) ValidateObjects(result, catalog, stop.Token);
                stop.Token.ThrowIfCancellationRequested(); return (result, receipt);
            }, stop.Token);
            void CheckContent()
            {
                var currentManifest = Manifest();
                if (currentManifest.LoadUnits().Summary.Fingerprint != catalog.Summary.Fingerprint)
                    throw new OperationCanceledException("Scene/content changed during compilation.");
                receipt?.ReadStartingSet(currentManifest);
            }
            CheckInputs();
            CheckContent();
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (sourceOnly) output = output[..^".ortmap.json".Length] + ".mapsource.json";
            else if (bundle) output = output[..^".ortmap.json".Length] + SceneMapBundle.Suffix;
            if (verify) staged = Path.Combine(Path.GetDirectoryName(output)!, $".verify-{Guid.NewGuid():N}-{Path.GetFileName(output)}");
            string destination = staged ?? output;
            if (sourceOnly)
                SaveTextAtomically(compiled.Source.Write(), destination, stop.Token);
            else if (dependencies is not null) PublishBundle(root!, destination, compiled, dependencies, stop.Token);
            else MapCompiler.Save(compiled, destination, stop.Token);

            SimulationReport? verification = null;
            if (staged is not null)
            {
                _status.Text = "Verifying in Godot and headless…";
                verification = await VerifyMap(staged, contentPath, receipt!, manifest, catalog, stop.Token);
                CheckInputs();
                CheckContent();
                if (bundle) Sandbox.CheckSceneMapDependencies(SceneMapBundle.Read(new(staged, File.ReadAllText(staged))));
                stop.Token.ThrowIfCancellationRequested();
                File.Move(staged, output, overwrite: true);
                staged = null;
            }
            string resource = ProjectSettings.LocalizePath(output);
            if (play) PlayMap(resource, contentPath, receipt!);
            string validation = verification is not null ? $"Godot and headless agree after {verification.Tick} ticks ({verification.EntityCount} objects)." :
                receipt is not null ? "Selected local match validated." :
                sourceOnly ? "Structural validation only." : "Placed objects validated; starting armies are checked at match creation.";
            _status.Text = $"{(sourceOnly ? "Extracted" : "Compiled")} {compiled.Map.Id}\n{compiled.Source.Objects.Count} objects, {compiled.Source.Slots.Count} slots\n{resource}\n{validation}";
            if (saveScene) EditorInterface.Singleton.GetResourceFilesystem().Scan();
            return new(resource, contentPath, receipt, verification);
        }
        catch (OperationCanceledException) { if (generation == _generation) _status.Text = "Cancelled/stale; previous file retained."; }
        catch (Exception error) { if (generation == _generation) Fail(error); }
        finally
        {
            if (_build == stop) _build = null;
            stop.Dispose();
            if (staged is not null) File.Delete(staged);
        }
        return null;
    }

    private static void SaveTextAtomically(string text, string path, CancellationToken stop)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new System.Text.UTF8Encoding(false));
            stop.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void OnEdited() { RefreshOwners(); _refresh = true; Invalidate("Changed; compile again."); }
    private void OnSceneChanged(Node node)
    {
        CancelStroke(); ClearPreview(); _selectedTransforms.Clear(); _types.Clear(); _playtestDialog.Hide();
        _lastError = null;
        UpdateMapTools(node); RefreshOwners(); _refresh = true;
        Invalidate("Scene changed; load its type catalog.");
    }

    private void UpdateMapTools(Node? node)
    {
        _terrainTools.Visible = _addGroup.Visible = _buildBundle.Visible = node is OpenRtsMap;
    }
    internal void Invalidate(string message) { _generation++; _build?.Cancel(); _status.Text = message; }
    private void Fail(Exception error) { _lastError = error; _status.Text = "Failed: " + error.Message; GD.PushError("OpenRTS authoring: " + error.Message); }
}
#endif
