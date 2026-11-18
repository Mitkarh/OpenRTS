using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;
using OpenRTS.Godot.Rendering;

public partial class Sandbox3D : Node3D
{
    private const int MapWidth = 24;
    private const int MapDepth = 16;
    private const int CellSize = WorldPosition.UnitsPerCell;
    private const int VerificationTick = 120;
    private static readonly WorldPosition Start = new(4 * CellSize, 5 * CellSize);
    private static readonly WorldPosition Goal = new(12 * CellSize, 10 * CellSize);

    private SimulationSession? _session;
    private WorldView _view = null!;
    private bool _verify;

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

            _view = new WorldView(catalog.Units[0], 1);
            _view.Node.Multimesh.VisibleInstanceCount = 0;
            AddChild(_view.Node);

            _session = new SimulationSession(
                () => new SimulationWorld(catalog.Units, MapWidth * CellSize, MapDepth * CellSize,
                    navigationGrid: new NavigationGrid(MapWidth, MapDepth)),
                [new(1, 0, 1, CommandKind.Spawn, Position: Start), new(2, 0, 2, CommandKind.MoveAll, Position: Goal)],
                endTick: _verify ? VerificationTick : null, realtime: !_verify);
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    public override void _Process(double delta)
    {
        if (_session is null) return;
        try
        {
            bool completed = _session.Completion.IsCompleted;
            if (completed) _session.Completion.GetAwaiter().GetResult();
            using var frame = _session.Frames.AcquireLatest();
            if (frame is null) return;

            _view.Apply(frame.Poses, completed ? 1 : _session.GetInterpolationAlpha(frame.Tick));
            if (_verify && completed)
            {
                if (frame.Tick != VerificationTick || frame.Poses.Length != 1 || frame.Poses[0].Current != Goal)
                    throw new InvalidOperationException("The sandbox scout did not reach the scripted destination.");
                GD.Print($"Sandbox3D verified: scout reached {Goal} at or before tick {frame.Tick}.");
                SetProcess(false);
                GetTree().Quit();
            }
        }
        catch (Exception error)
        {
            Fail(error);
        }
    }

    public override void _ExitTree() => _session?.Dispose();

    private void Fail(Exception error)
    {
        GD.PushError(error.ToString());
        SetProcess(false);
        GetTree().Quit(1);
    }
}
