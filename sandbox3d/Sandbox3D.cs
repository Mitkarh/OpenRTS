using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Presentation;
using OpenRTS.Godot.Controls;
using OpenRTS.Godot.Rendering;

public partial class Sandbox3D : Node3D
{
    private const int MapWidth = 24;
    private const int MapDepth = 16;
    private const int CellSize = WorldPosition.UnitsPerCell;
    private const int WallX = 12;
    private const int WallStart = 4;
    private const int WallEnd = 12;
    private static readonly Vector2 MapSize = new(MapWidth, MapDepth);
    private static readonly (string Type, float X, float Z)[] Starts =
    [
        ("scout", 5, 7.5f), ("scout", 5, 10), ("scout", 3, 8.5f),
        ("tank", 7.5f, 6), ("apc", 7.5f, 9), ("engineer", 7.5f, 12)
    ];

    private readonly SelectionState _selection = new();
    private readonly Dictionary<EntityId, ActorView> _actors = new();
    private readonly HashSet<EntityId> _displayed = [];
    private readonly List<EntityId> _removed = [];
    private readonly ObstacleView _obstacles = new();
    private SimulationSession? _session;
    private ActorModel[] _models = [];
    private Camera3D _camera = null!;
    private RtsOverlay _overlay = null!;
    private Label _status = null!;
    private MeshInstance3D _goal = null!;
    private Mesh _selectionMesh = null!;
    private float _baseCameraSize, _goalTime;
    private bool _verify, _failed;
    private Rect2 ViewRect => GetViewport().GetVisibleRect();

    public override void _Ready()
    {
        try
        {
            _verify = OS.GetCmdlineUserArgs().Contains("--verify");
            const string unitsPath = "res://units.yaml";
            using var unitsFile = Godot.FileAccess.Open(unitsPath, Godot.FileAccess.ModeFlags.Read);
            if (unitsFile is null)
                throw new IOException($"Cannot open {unitsPath}: {Godot.FileAccess.GetOpenError()}");
            var catalog = UnitCatalog.Load([new(unitsPath, unitsFile.GetAsText())]);
            _models = catalog.Units.Select(LoadModel).ToArray();

            _camera = GetNode<Camera3D>("Camera");
            _baseCameraSize = _camera.Size;
            var window = GetWindow();
            if (_verify && DisplayServer.GetName() == "headless")
                window.Size = new(ProjectSettings.GetSetting("display/window/size/viewport_width").AsInt32(),
                    ProjectSettings.GetSetting("display/window/size/viewport_height").AsInt32());
            window.FocusExited += CancelDrag;
            AddChild(_obstacles.Node);
            _selectionMesh = RingMesh(new Color("#84efb9"));
            _goal = new MeshInstance3D { Mesh = RingMesh(new Color("#ffe184")), Visible = false };
            AddChild(_goal);

            var layer = new CanvasLayer();
            AddChild(layer);
            _overlay = new RtsOverlay { ShowCursor = false };
            layer.AddChild(_overlay);
            var hints = new Label
            {
                Position = new Vector2(16, 12),
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Text = "ЛКМ / рамка — выбор    ПКМ — движение    Shift — очередь\n" +
                    "Стрелки / СКМ — камера    Колесо — масштаб    Пробел — к выбору\n" +
                    "M — цель движения    S / H — стоп / удерживать    Ctrl+1–9 — сохранить группу"
            };
            hints.AddThemeConstantOverride("outline_size", 5);
            hints.AddThemeColorOverride("font_outline_color", new Color("#141c1d"));
            layer.AddChild(hints);
            _status = new Label { Position = new Vector2(16, 80), MouseFilter = Control.MouseFilterEnum.Ignore };
            _status.AddThemeConstantOverride("outline_size", 5);
            _status.AddThemeColorOverride("font_outline_color", new Color("#141c1d"));
            layer.AddChild(_status);

            _session = new SimulationSession(() => CreateWorld(catalog), startPaused: _verify);
            UpdateStatus();
        }
        catch (Exception error) { Fail(error); }
    }

    private static SimulationWorld CreateWorld(UnitCatalog catalog)
    {
        var blocked = Enumerable.Range(WallStart, WallEnd - WallStart).Select(z => new GridCell(WallX, z));
        var world = new SimulationWorld(catalog.Units, MapWidth * CellSize, MapDepth * CellSize,
            initialCapacity: Starts.Length, navigationGrid: new NavigationGrid(MapWidth, MapDepth, blocked),
            players: [new PlayerDefinition(1, "Sandbox")]);
        try
        {
            var types = catalog.Units.Select((unit, index) => (unit.Id, Index: index)).ToDictionary(x => x.Id, x => x.Index);
            foreach (var (type, x, z) in Starts)
                world.AddInitialUnit(types[type], new WorldPosition((int)(x * CellSize), (int)(z * CellSize)), owner: 1);
            return world;
        }
        catch { world.Dispose(); throw; }
    }

    public override void _Process(double delta)
    {
        if (_session is null) return;
        try
        {
            if (_session.Completion.IsCompleted) _session.Completion.GetAwaiter().GetResult();
            if (!_verify) PanCamera(delta);
            if (_goal.Visible && (_goalTime -= (float)delta) <= 0) _goal.Visible = false;
            using var frame = _session.Frames.AcquireLatest();
            if (frame is null) return;

            _obstacles.Apply(frame.Grid);
            UpdateActors(frame.Poses, _session.GetInterpolationAlpha(frame.Tick));
            while (_session.TryReadEvent(out _)) { }
            if (_verify) VerifyControls(frame);
        }
        catch (Exception error) { Fail(error); }
    }

    private void UpdateActors(ReadOnlySpan<EntityPose> poses, float alpha)
    {
        _displayed.Clear();
        foreach (var pose in poses)
        {
            if (pose.Carrier.Value != 0) continue;
            _displayed.Add(pose.Id);
            if (!_actors.TryGetValue(pose.Id, out var actor))
            {
                actor = new ActorView(_models[pose.UnitType], _selectionMesh, pose.UnitType);
                AddChild(actor.Node);
                _actors.Add(pose.Id, actor);
            }
            actor.Apply(pose, alpha);
        }
        _removed.Clear();
        foreach (var (id, actor) in _actors)
            if (!_displayed.Contains(id))
            {
                actor.Node.QueueFree();
                _removed.Add(id);
            }
        if (_removed.Count == 0) return;
        foreach (var id in _removed) _actors.Remove(id);
        _selection.Prune(_actors.ContainsKey);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        foreach (var (id, view) in _actors) view.SetSelected(_selection.Contains(id));
        _status.Text = _moveArmed ? "Укажите цель движения (ЛКМ)" : $"Выбрано: {_selection.Count}";
    }

    public override void _ExitTree()
    {
        GetWindow().FocusExited -= CancelDrag;
        _session?.Dispose();
        _session = null;
    }

    private void Fail(Exception error)
    {
        GD.PushError(error.ToString());
        _failed = true;
        SetProcess(false);
        GetTree().Quit(1);
    }
}
