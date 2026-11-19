#if TOOLS
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;

namespace OpenRTS.Godot.Authoring;

public partial class OpenRtsEditorPlugin
{
    private CheckButton _paint = null!, _previewEnabled = null!;
    private OptionButton _terrainIds = null!, _visualIds = null!, _paintLayer = null!;
    private SpinBox _brushSize = null!, _turns = null!;
    private EditorResourcePicker _brushPicker = null!;
    private LineEdit _semanticId = null!;
    private Label _details = null!;
    private VBoxContainer _terrainTools = null!;
    private OpenRtsMap? _previewRoot;
    private MapEditorPreview? _preview;
    private UnitCatalog? _previewCatalog;
    private ModelCatalog? _previewModels;
    private OpenRtsTerrainStroke? _stroke;
    private readonly HashSet<Vector2I> _strokeCells = [];
    private Vector2I? _lastCell;
    private bool _refresh = true;
    private double _refreshTime;
    private string _previewContent = "";
    private readonly Dictionary<ulong, Transform3D> _selectedTransforms = [];
    private MeshInstance3D? _brushCursor;
    private Vector2I _cursorSize;
    private Exception? _lastError;
    internal int PreviewSprites => _preview?.SpriteCount ?? 0;
    internal MapEditorPreview? Preview => _preview;
    internal void SetPainting(bool enabled) => _paint.ButtonPressed = enabled;

    private void SetupTerrainTools()
    {
        _terrainTools = new();
        _dock.AddChild(_terrainTools);
        _previewEnabled = new() { Text = "Models + terrain preview", ButtonPressed = true };
        _previewEnabled.Toggled += _ => { _refresh = true; if (!_previewEnabled.ButtonPressed) ClearPreview(); };
        _terrainTools.AddChild(_previewEnabled);
        Button("Reload Models and textures", () => { LoadTypes(); RefreshPreview(); }, _terrainTools);
        _paint = new() { Text = "Paint terrain (left drag)" };
        _paint.Toggled += _ => CancelStroke(); _terrainTools.AddChild(_paint);
        _paintLayer = new(); foreach (string layer in new[] { "Terrain + visual", "Terrain only", "Visual only" }) _paintLayer.AddItem(layer);
        _terrainTools.AddChild(_paintLayer);
        _terrainIds = new(); _visualIds = new();
        _terrainTools.AddChild(new Label { Text = "Terrain / visual ID" }); _terrainTools.AddChild(_terrainIds); _terrainTools.AddChild(_visualIds);
        _brushSize = new() { MinValue = 1, MaxValue = 16, Value = 1, Step = 1, Prefix = "Square size " };
        _turns = new() { MinValue = 0, MaxValue = 3, Step = 1, Prefix = "Quarter-turns " };
        _terrainTools.AddChild(_brushSize); _terrainTools.AddChild(_turns);
        _terrainTools.AddChild(new Label { Text = "Optional shaped brush (.tres)" });
        _brushPicker = new() { BaseType = nameof(OpenRtsTerrainBrush) };
        _brushPicker.ResourceSelected += (resource, _) => EditorInterface.Singleton.EditResource(resource);
        _terrainTools.AddChild(_brushPicker);
        Button("Cancel brush stroke (Esc)", CancelStroke, _terrainTools);
        _details = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _terrainTools.AddChild(_details);
        _terrainTools.AddChild(new Label { Text = "Cyan: body · Blue: blocked\nAmber: no stop · Green: other cells\nPink diagonal: not targetable", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        _semanticId = new() { PlaceholderText = "Semantic ID, e.g. patrol/tank" }; _dock.AddChild(_semanticId);
        Button("Select by ID", () => SelectById(_semanticId.Text));
        Button("Select error source", SelectErrorSource);
        SetupPreview2D();
        SetInputEventForwardingAlwaysEnabled();
    }

    private void LoadPreviewCatalog(OpenRtsMap root, UnitCatalog catalog)
    {
        ClearPreview();
        _previewCatalog = catalog;
        var manifest = GameManifest.Load(ProjectFile(root.ContentManifest, ".yaml"));
        _previewModels = manifest.LoadModels();
        _previewContent = root.ContentManifest;
        _previewRoot = root;
        _gizmos.Catalog = catalog;
        _terrainIds.Clear(); _visualIds.Clear();
        foreach (string id in catalog.Locomotion!.Terrains) _terrainIds.AddItem(id);
        foreach (string id in catalog.Locomotion.Terrains.Concat(root.VisualTextures.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)) _visualIds.AddItem(id);
        for (int i = 0; i < _terrainIds.ItemCount; i++) if (_terrainIds.GetItemText(i) == root.TerrainId) _terrainIds.Select(i);
        for (int i = 0; i < _visualIds.ItemCount; i++) if (_visualIds.GetItemText(i) == root.VisualId) _visualIds.Select(i);
        _refresh = true;
    }

    public override bool _Handles(GodotObject obj) => obj is OpenRtsMap or OpenRtsMapObject or OpenRtsMapGroup or OpenRtsMapStart;

    public override void _Process(double delta)
    {
        _refreshTime += delta;
        if (_refreshTime < 0.15) return;
        _refreshTime = 0;
        UpdatePlaytestControls();
        var edited = EditorInterface.Singleton.GetEditedSceneRoot();
        if (edited is OpenRtsMap2D map2D) { UpdatePreview2D(map2D); return; }
        var root = edited as OpenRtsMap;
        if (root is null) { ClearPreview(); return; }
        if (_previewRoot != root || _previewContent != root.ContentManifest) _refresh = true;
        foreach (Node node in EditorInterface.Singleton.GetSelection().GetSelectedNodes())
            if (node is Node3D spatial)
            {
                ulong id = node.GetInstanceId(); var transform = spatial.GlobalTransform;
                if (!_selectedTransforms.TryGetValue(id, out var previous) || previous != transform) _refresh = true;
                _selectedTransforms[id] = transform;
            }
        if (_refresh)
            try { RefreshPreview(); }
            catch (Exception error)
            {
                _refresh = false; ClearPreview(); _previewRoot = root; _previewContent = root.ContentManifest;
                _previewCatalog = null; _previewModels = null; _gizmos.Catalog = null; Fail(error);
            }
    }

    internal void RefreshPreview()
    {
        _refresh = false;
        var root = Root();
        if (_previewRoot != root || _previewCatalog is null || _previewContent != root.ContentManifest)
        { LoadPreviewCatalog(root, LoadCatalog(root)); _previewRoot = root; _refresh = false; }
        foreach (var node in Markers(root)) node.UpdateGizmos();
        UpdateDetails();
        if (!_previewEnabled.ButtonPressed) return;
        var source = MapSceneExtractor.Extract(root, _stroke);
        _preview ??= new(root, _previewCatalog!, _previewModels!);
        _preview.Apply(root, source);
    }

    private static IEnumerable<Node3D> Markers(Node root)
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is OpenRtsMapObject or OpenRtsMapGroup or OpenRtsMapStart) yield return (Node3D)child;
            foreach (var nested in Markers(child)) yield return nested;
        }
    }
    private void ClearPreview()
    {
        _preview?.Dispose(); _preview = null; _previewRoot = null; _brushCursor = null;
        ClearPreview2D();
    }

    private OpenRtsTerrainBrush CurrentBrush()
    {
        if (_brushPicker.EditedResource is OpenRtsTerrainBrush preset) return preset.Snapshot();
        if (_terrainIds.GetItemCount() == 0 || _visualIds.GetItemCount() == 0) throw new ArgumentException("Load the type/terrain catalog before painting.");
        int size = (int)_brushSize.Value;
        return new() { Size = new(size, size), Terrain = Enumerable.Repeat(_paintLayer.Selected == 2 ? "" : _terrainIds.GetItemText(_terrainIds.Selected), size * size).ToArray(),
            Visual = Enumerable.Repeat(_paintLayer.Selected == 1 ? "" : _visualIds.GetItemText(_visualIds.Selected), size * size).ToArray(), ResourceLocalToScene = true };
    }

    internal void BeginStroke(OpenRtsTerrainBrush brush, int turns)
    {
        CancelStroke();
        _stroke = new() { StrokeId = MapAuthoringIdentity.NewId(), Brush = brush.Snapshot(), QuarterTurns = turns, ResourceLocalToScene = true };
    }
    internal void PaintCell(Vector2I cell)
    {
        var root = Root();
        if (_stroke?.Brush is not { } brush) return;
        if (!MapTerrain.Fits(cell, root.Size, brush.Size, _stroke.QuarterTurns)) { _lastCell = null; return; }
        foreach (var at in MapTerrain.Line(_lastCell ?? cell, cell))
        {
            if (_strokeCells.Count >= MapTerrain.MaximumStamps) throw new ArgumentException("Stroke stamp budget exceeded.");
            if (MapTerrain.Fits(at, root.Size, brush.Size, _stroke.QuarterTurns) && _strokeCells.Add(at)) _stroke.Cells.Add(at);
        }
        _lastCell = cell; _refresh = true;
    }
    internal void CommitStroke()
    {
        var stroke = _stroke; _stroke = null; _strokeCells.Clear(); _lastCell = null; _refresh = true;
        if (stroke is null || stroke.Cells.Count == 0) return;
        var root = Root();
        // Validate before touching scene data; a malformed preset cannot create a partial undo action.
        MapTerrain.Compose(root, stroke);
        var after = new global::Godot.Collections.Array<OpenRtsTerrainStroke>(root.TerrainStrokes) { stroke };
        var undo = GetUndoRedo(); undo.CreateAction("Paint OpenRTS terrain", customContext: root);
        undo.AddDoProperty(root, nameof(OpenRtsMap.TerrainStrokes), after);
        undo.AddUndoProperty(root, nameof(OpenRtsMap.TerrainStrokes), root.TerrainStrokes);
        undo.CommitAction();
    }
    internal void CancelStroke() { _stroke = null; _strokeCells.Clear(); _lastCell = null; _refresh = true; if (GodotObject.IsInstanceValid(_brushCursor)) _brushCursor!.Visible = false; }

    public override void _Input(InputEvent input)
    {
        if (_stroke is null) return;
        if (input is InputEventKey { Pressed: true, Keycode: Key.Escape }) CancelStroke();
        else if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
            try { CommitStroke(); } catch (Exception error) { CancelStroke(); Fail(error); }
    }

    public override int _Forward3DGuiInput(Camera3D camera, InputEvent input)
    {
        if (!_paint.ButtonPressed || EditorInterface.Singleton.GetEditedSceneRoot() is not OpenRtsMap root) return (int)AfterGuiInput.Pass;
        if (input is InputEventWithModifiers { AltPressed: true }) { CancelStroke(); return (int)AfterGuiInput.Pass; }
        if (input is InputEventKey { Pressed: true, Keycode: Key.Escape } || _stroke is not null && input is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
        { CancelStroke(); return (int)AfterGuiInput.Stop; }
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Right }) return (int)AfterGuiInput.Pass;
        if (input is not InputEventMouse mouse) return (int)AfterGuiInput.Pass;
        try
        {
            var inverse = root.GlobalTransform.AffineInverse();
            var origin = inverse * camera.ProjectRayOrigin(mouse.Position);
            var direction = inverse.Basis * camera.ProjectRayNormal(mouse.Position);
            Vector2I? cell = null;
            if (Math.Abs(direction.Y) > 0.000001f && -origin.Y / direction.Y >= 0)
            {
                var point = origin + direction * (-origin.Y / direction.Y);
                // Reject outside hits before integer conversion or line interpolation.
                if (point.IsFinite() && point.X >= 0 && point.Z >= 0 && point.X < root.Size.X && point.Z < root.Size.Y)
                    cell = new((int)Math.Floor(point.X), (int)Math.Floor(point.Z));
            }
            var brush = _stroke?.Brush ?? CurrentBrush(); int turns = _stroke?.QuarterTurns ?? (int)_turns.Value;
            UpdateBrushCursor(cell, brush.Size, turns);
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
            {
                if (button.Pressed && cell.HasValue && MapTerrain.Fits(cell.Value, root.Size, brush.Size, turns)) { BeginStroke(brush, turns); PaintCell(cell.Value); }
                else if (!button.Pressed) CommitStroke();
                return (int)AfterGuiInput.Stop;
            }
            if (_stroke is not null && input is InputEventMouseMotion)
            { if (cell.HasValue) PaintCell(cell.Value); else _lastCell = null; return (int)AfterGuiInput.Stop; }
        }
        catch (Exception error) { CancelStroke(); Fail(error); return (int)AfterGuiInput.Stop; }
        return (int)AfterGuiInput.Pass;
    }

    private void UpdateBrushCursor(Vector2I? cell, Vector2I size, int turns)
    {
        if (_preview is null) return;
        if (_brushCursor is null)
        {
            _brushCursor = new() { Name = "BrushCursor", MaterialOverride = new StandardMaterial3D
                { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, NoDepthTest = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, RenderPriority = 127 } };
            _preview.Node.AddChild(_brushCursor);
        }
        _brushCursor.Visible = cell.HasValue;
        if (!cell.HasValue) return;
        Vector2I dimensions = turns % 2 == 0 ? size : new(size.Y, size.X);
        if (_cursorSize != dimensions || _brushCursor.Mesh is null)
        {
            var mesh = new ImmediateMesh(); mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
            foreach (var point in MapPreviewGeometry.Box(0, 0, dimensions.X, dimensions.Y)) mesh.SurfaceAddVertex(point);
            mesh.SurfaceEnd(); _brushCursor.Mesh = mesh; _cursorSize = dimensions;
        }
        _brushCursor.Position = new(cell.Value.X, 0, cell.Value.Y);
        ((StandardMaterial3D)_brushCursor.MaterialOverride).AlbedoColor = MapTerrain.Fits(cell.Value, Root().Size, size, turns) ? Colors.Yellow : Colors.Red;
    }

    internal void SelectById(string id)
    {
        var found = SemanticMarkers(EditedMap()).Where(marker => marker.Id == id).Select(marker => marker.Node).ToArray();
        if (found.Length != 1) throw new ArgumentException($"Semantic ID '{id}' resolves to {found.Length} markers.");
        SelectNode(found[0]);
    }

    private static IEnumerable<(Node Node, string Id)> SemanticMarkers(Node root)
    {
        IEnumerable<(Node Node, string Id)> Find(Node node, string prefix)
        {
            if (MapAuthoringIdentity.GroupId(node) is { } id) prefix += id + "/";
            if (node is OpenRtsMapObject or OpenRtsMapStart or OpenRtsMapObject2D or OpenRtsMapStart2D)
                yield return (node, prefix + node.Get(MapAuthoringIdentity.IdField(node)).AsString());
            foreach (Node child in node.GetChildren())
                foreach (var marker in Find(child, prefix)) yield return marker;
        }
        return Find(root, "");
    }
    private static void SelectNode(Node node)
    {
        var editor = EditorInterface.Singleton; editor.GetSelection().Clear(); editor.GetSelection().AddNode(node); editor.EditNode(node);
    }
    private static Node? ErrorNode(Node root, string message)
    {
        int start = message.IndexOf("::", StringComparison.Ordinal), end = message.IndexOf(":1:1", Math.Max(0, start), StringComparison.Ordinal);
        return start >= 0 && end > start ? root.GetNodeOrNull<Node>(message[(start + 2)..end]) : null;
    }
    internal void SelectErrorSource()
    {
        if (_lastError is null || EditorInterface.Singleton.GetEditedSceneRoot() is not { } root) return;
        string message = _lastError.Message;
        var node = ErrorNode(root, message);
        if (root is OpenRtsMap2D map2D && (node as TileMapLayer ?? map2D.Terrain) is { } layer &&
            FindDiagnosticCell(message, map2D.Size) is { } cell)
        { FocusCell(layer, cell); return; }
        if (node is not null && node != root)
        { SelectNode(node); return; }
        string? mapId = root is OpenRtsMap map3D ? map3D.MapId : (root as OpenRtsMap2D)?.MapId;
        if (mapId is not null)
        {
            foreach (var marker in SemanticMarkers(root).OrderByDescending(marker => marker.Id.Length))
            {
                bool isStart = marker.Node is OpenRtsMapStart or OpenRtsMapStart2D;
                if (isStart && (message.Contains($"[{mapId}.{marker.Id}/", StringComparison.Ordinal) ||
                    message.Contains($"[starts.{marker.Id}.position]", StringComparison.Ordinal)))
                { SelectNode(marker.Node); return; }
            }
        }
        if (root is OpenRtsMap map)
            foreach (var stroke in map.TerrainStrokes)
                if (!string.IsNullOrEmpty(stroke.StrokeId) && message.Contains("TerrainStrokes." + stroke.StrokeId, StringComparison.Ordinal))
                { EditorInterface.Singleton.EditResource(stroke); return; }
        SelectNode(root);
    }
    private void UpdateDetails()
    {
        var selected = EditorInterface.Singleton.GetSelection().GetSelectedNodes(); _details.Text = "";
        if (selected.Count != 1 || selected[0] is not OpenRtsMapObject obj || _previewCatalog is null) return;
        var unit = _previewCatalog.Units.FirstOrDefault(u => u.Id == obj.TypeId);
        if (unit is null) { _details.Text = "Unknown TypeId: " + obj.TypeId; return; }
        int index = _previewCatalog.Units.IndexOf(unit);
        int maximum = obj.HealthVariant?.MaxHealth ?? unit.MaxHealth;
        string variant = obj.HealthVariant is { } health ? " · " + health.VariantId : "";
        int initial = obj.InitialHealth == 0 ? maximum : obj.InitialHealth;
        _details.Text = $"{unit.Id}{variant} · HP {initial}/{maximum}\nModel: {_previewCatalog.Presentations[index].Model}\nBody: {unit.Body.Kind} {unit.Body.HalfWidth}×{unit.Body.HalfHeight}\nFootprint: {unit.Footprint?.Cells.Count ?? 0} cells";
    }

    internal static void ValidateObjects(MapSource source, UnitCatalog catalog) =>
        ValidateObjects(MapCompiler.Compile(source, catalog), catalog, default);

    private static void ValidateObjects(CompiledMap compiled, UnitCatalog catalog, CancellationToken stop)
    {
        var source = compiled.Source;
        var map = compiled.Map;
        if (source.IsSkirmish)
        {
            var fixture = new MapSource(source.Id, false, source.Width, source.Height, source.Terrain, source.Visual,
                [], source.Objects, source.CellSize, source.Origin, source.Surface);
            map = MapCompiler.Compile(fixture, catalog, stop).Map;
        }
        var players = map.Slots.Select((id, i) => (id, player: new PlayerDefinition((uint)i + 1, id))).ToDictionary(p => p.id, p => p.player);
        using var world = map.Prepare(players).CreateWorld();
    }
}
#endif
