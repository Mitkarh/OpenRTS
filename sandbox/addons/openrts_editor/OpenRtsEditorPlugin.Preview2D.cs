#if TOOLS
using System.Text.RegularExpressions;
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;

namespace OpenRTS.Godot.Authoring;

public partial class OpenRtsEditorPlugin
{
    private const int ReliefPreviewLimit = 512;
    private VBoxContainer _tools2D = null!;
    private CheckBox _show2D = null!, _showRelief = null!;
    private OptionButton _previewStartSets = null!;
    private ItemList _reliefIssues = null!;
    private Label _reliefSummary = null!;
    private OpenRtsMap2D? _map2D;
    private MapEditorPreview2D? _preview2D;
    private GameManifest? _manifest2D;
    private UnitCatalog? _catalog2D;
    private ModelCatalog? _models2D;
    private MapReliefDiagnostics.Edge[] _edges2D = [];
    private readonly List<TileMapLayer> _watchedLayers = [];
    private readonly Dictionary<ulong, Transform2D> _transforms2D = [];
    private IReadOnlySet<string>? _selectedPreviewStarts;
    private Vector2I? _focusedCell;
    private string _manifestPath2D = "";
    internal MapEditorPreview2D? Preview2D => _preview2D;

    private void SetupPreview2D()
    {
        _tools2D = new VBoxContainer();
        _dock.AddChild(_tools2D);
        _show2D = new() { Text = "2D Models + bodies and footprints", ButtonPressed = true };
        _show2D.Toggled += _ => _refresh = true;
        _tools2D.AddChild(_show2D);
        Button("Reload 2D preview", ReloadPreview2D, _tools2D);
        _previewStartSets = new() { TooltipText = "Starting set to preview at the map starts" };
        _previewStartSets.ItemSelected += _ => { _selectedPreviewStarts = null; _refresh = true; };
        _tools2D.AddChild(_previewStartSets);
        _showRelief = new() { Text = "2D relief seams and height steps", ButtonPressed = true };
        _showRelief.Toggled += _ => _refresh = true;
        _tools2D.AddChild(_showRelief);
        _reliefSummary = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _tools2D.AddChild(_reliefSummary);
        _reliefIssues = new() { CustomMinimumSize = new(0, 110) };
        _reliefIssues.ItemSelected += index =>
        {
            if (_map2D?.Terrain is { } terrain && index >= 0 && index < _edges2D.Length)
                FocusCell(terrain, _edges2D[index].Cell);
        };
        _tools2D.AddChild(_reliefIssues);
    }

    private void ReloadPreview2D()
    {
        ClearPreview2D();
        _refresh = true;
    }

    private void ClearPreview2D()
    {
        if (_preview2D is not null && GodotObject.IsInstanceValid(_preview2D)) _preview2D.Free();
        _preview2D = null;
        foreach (var layer in _watchedLayers) if (GodotObject.IsInstanceValid(layer)) layer.Changed -= PreviewTerrainChanged;
        _watchedLayers.Clear();
        _map2D = null; _manifest2D = null; _catalog2D = null; _models2D = null;
        _transforms2D.Clear(); _selectedPreviewStarts = null; _focusedCell = null; _edges2D = [];
    }

    private void PreviewTerrainChanged() => _refresh = true;

    private void UpdatePreview2D(OpenRtsMap2D root)
    {
        if (_map2D != root || _manifestPath2D != root.ContentManifest) _refresh = true;
        foreach (var node in EditorInterface.Singleton.GetSelection().GetSelectedNodes().OfType<Node2D>())
        {
            ulong id = node.GetInstanceId();
            if (!_transforms2D.TryGetValue(id, out var previous) || previous != node.GlobalTransform)
            {
                _transforms2D[id] = node.GlobalTransform;
                _refresh = true;
            }
        }
        if (_refresh) RefreshPreview2D(root);
    }

    internal void RefreshPreview2D(OpenRtsMap2D root)
    {
        _refresh = false;
        if (_map2D != root || _manifestPath2D != root.ContentManifest)
        {
            ClearPreview2D();
            _map2D = root; _manifestPath2D = root.ContentManifest;
        }
        _reliefIssues.Clear();
        _reliefIssues.Visible = false;
        _edges2D = [];
        try
        {
            foreach (var old in _watchedLayers) if (GodotObject.IsInstanceValid(old)) old.Changed -= PreviewTerrainChanged;
            _watchedLayers.Clear();
            var layer = root.Terrain;
            foreach (var watch in root.VisualLayers.Prepend(layer).OfType<TileMapLayer>().Distinct())
            {
                watch.Changed += PreviewTerrainChanged;
                _watchedLayers.Add(watch);
            }
            if (layer?.TileSet is null)
            {
                if (_preview2D is not null) { _preview2D.Free(); _preview2D = null; }
                _reliefSummary.Text = "Assign the gameplay Terrain layer.";
                return;
            }
            if (_preview2D is null)
            {
                _preview2D = new() { Name = "OpenRTSPreview2D", ZIndex = 100 };
                root.AddChild(_preview2D, false, Node.InternalMode.Back);
            }
            _preview2D.Reset(MapProjection2D.From(layer));
            _preview2D.ShowRelief([], _focusedCell);
            if (!_show2D.ButtonPressed && !_showRelief.ButtonPressed) { _reliefSummary.Text = ""; return; }

            if (_manifest2D is null)
            {
                var manifest = GameManifest.Load(ProjectFile(root.ContentManifest, ".yaml"));
                _catalog2D = manifest.LoadUnits();
                _models2D = ModelCatalog.Load(manifest.Models);
                _manifest2D = manifest;
                FillPreviewStartSets(manifest);
            }
            _previewStartSets.Visible = root.IsSkirmish;
            var source = MapSceneExtractor2D.Extract(root);
            // A draft can show the terrain and placement markers before it has a valid match.
            var terrain = new MapSource(source.Id, false, source.Width, source.Height, source.Terrain, source.Visual, [], [],
                source.CellSize, source.Origin, source.Surface);
            var grid = MapCompiler.Compile(terrain, _catalog2D!).Map.Grid;
            if (_showRelief.ButtonPressed)
            {
                var edges = MapReliefDiagnostics.Scan(grid).Take(ReliefPreviewLimit + 1).ToArray();
                _edges2D = edges.Take(ReliefPreviewLimit).ToArray();
                foreach (var edge in _edges2D) _reliefIssues.AddItem(edge.ToString());
                _reliefIssues.Visible = _edges2D.Length > 0;
                _reliefSummary.Text = edges.Length == 0 ? "No relief seam differences." :
                    $"{_edges2D.Length}{(edges.Length > ReliefPreviewLimit ? "+" : "")} relief edges. Select a row to highlight its cell.";
            }
            else _reliefSummary.Text = "";
            _preview2D.ShowRelief(_edges2D, _focusedCell);
            if (!_show2D.ButtonPressed) return;

            string setPath = _previewStartSets.Selected <= 0 ? "" : _previewStartSets.GetItemMetadata(_previewStartSets.Selected).AsString();
            var set = root.IsSkirmish && setPath.Length != 0 ? StartingSet.Load(_manifest2D.Files.Read(setPath)) : null;
            var effective = _catalog2D!.PrepareMapUnits(source);
            _preview2D.Apply(source, grid, effective, _models2D!, set, _selectedPreviewStarts);
            var compiled = MapCompiler.Compile(source, _catalog2D);
            if (set is not null && _selectedPreviewStarts is { Count: > 0 } starts)
                LocalMapPlaytest.Validate(compiled, _manifest2D, starts.Order(StringComparer.Ordinal).ToArray(), setPath);
            else ValidateObjects(compiled, _catalog2D, default);
        }
        catch (Exception error)
        {
            _lastError = error;
            _reliefSummary.Text = error.Message;
            if (FindDiagnosticCell(error.Message, root.Size) is { } cell)
            {
                _focusedCell = cell;
                _preview2D?.ShowRelief(_edges2D, cell);
            }
            else if (ErrorNode(root, error.Message) is OpenRtsMapObject2D marker)
                _preview2D?.MarkInvalid(root.ToLocal(marker.GlobalPosition));
            else if (Regex.Match(error.Message, @"\bat \((-?\d+),\s*(-?\d+)\)") is { Success: true } position &&
                int.TryParse(position.Groups[1].Value, out int x) && int.TryParse(position.Groups[2].Value, out int y) &&
                root.Terrain is { TileSet: not null } layer)
                _preview2D?.MarkInvalid(MapProjection2D.From(layer).Project(new(x, y)));
        }
    }

    private void FillPreviewStartSets(GameManifest manifest)
    {
        _previewStartSets.Clear();
        _previewStartSets.AddItem("No starting set preview");
        _previewStartSets.SetItemMetadata(0, "");
        var match = manifest.Settings.Match;
        var choices = match.StartingSets.Length == 0
            ? [new MatchChoice { Path = match.DefaultStartSet, Title = match.DefaultStartSet }]
            : match.StartingSets;
        foreach (var choice in choices)
        {
            _previewStartSets.AddItem(choice.Title);
            int index = _previewStartSets.ItemCount - 1;
            _previewStartSets.SetItemMetadata(index, choice.Path);
            if (choice.Path == match.DefaultStartSet) _previewStartSets.Select(index);
        }
    }

    private void SyncPlaytestPreview()
    {
        if (_map2D is null || _playtestSets.Selected < 0) return;
        string path = _playtestSets.GetItemMetadata(_playtestSets.Selected).AsString();
        for (int i = 1; i < _previewStartSets.ItemCount; i++)
            if (_previewStartSets.GetItemMetadata(i).AsString() == path) { _previewStartSets.Select(i); break; }
        _selectedPreviewStarts = _playtestStarts.GetSelectedItems().Select(i => _playtestStarts.GetItemText(i)).ToHashSet(StringComparer.Ordinal);
        _refresh = true;
    }

    private void FocusCell(TileMapLayer layer, Vector2I cell)
    {
        _focusedCell = cell;
        _preview2D?.ShowRelief(_edges2D, cell);
        SelectNode(layer);
    }

    internal static Vector2I? FindDiagnosticCell(string message, Vector2I size)
    {
        var position = Regex.Match(message, @"\bcell \((-?\d+),\s*(-?\d+)\)");
        if (position.Success && int.TryParse(position.Groups[1].Value, out int x) && int.TryParse(position.Groups[2].Value, out int y))
            return new(x, y);
        var index = Regex.Match(message, @"\bsurface\[(\d+)\]");
        if (index.Success && size.X > 0 && int.TryParse(index.Groups[1].Value, out int i))
            return new(i % size.X, i / size.X);
        return null;
    }
}
#endif
