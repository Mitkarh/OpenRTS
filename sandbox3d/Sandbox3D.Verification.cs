using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Presentation;
using OpenRTS.Godot.Controls;

public partial class Sandbox3D
{
    private enum CheckStage { Input, Queue, Detour, Group, Stop, Hold }
    private static readonly WorldPosition FirstGoal = new(19 * CellSize + 384, 8 * CellSize + 512);
    private static readonly WorldPosition FinalGoal = new(20 * CellSize + 768, 10 * CellSize + 128);
    private CheckStage _checkStage;
    private long _checkTick;
    private EntityId _scout, _secondScout;
    private WorldPosition _secondStart;
    private bool _sawDetour, _reachedFirstGoal;

    private void VerifyControls(FrameExchange.FrameLease frame)
    {
        if (frame.Tick < _checkTick) return;
        var session = _session!;
        if (_checkStage == CheckStage.Input)
        {
            Check(frame.Poses.Length == Starts.Length, "Initial frame does not contain the test squad.");
            Check(frame.Grid is { } grid && grid.IsBlocked(grid.Index(new GridCell(WallX, WallStart))),
                "The published navigation grid is missing the test wall.");
            _scout = frame.Poses[0].Id;
            _secondScout = frame.Poses[1].Id;
            _secondStart = frame.Poses[1].Current;
            VerifySelectionAndCamera();
            Press(Key.M);
            Click(MouseButton.Left, Pixel(FirstGoal));
            Click(MouseButton.Right, Pixel(FinalGoal), shift: true);
            Advance(frame.Tick, CheckStage.Queue);
            return;
        }

        var scout = ActorPose(frame.Poses, _scout);
        var second = ActorPose(frame.Poses, _secondScout);
        switch (_checkStage)
        {
            case CheckStage.Queue:
                var log = session.GetCommandLog();
                Check(log.Length == 2 && log[0].Kind == CommandKind.Move && log[0].Position == FirstGoal &&
                    !log[0].Queued && log[1].Kind == CommandKind.Move && log[1].Position == FinalGoal && log[1].Queued &&
                    log.All(command => command.Entity == _scout), "Pointer orders did not preserve the selected ID, exact targets or queue flag.");
                Check(scout.Order.QueuedCount == 1 && second.Current == _secondStart, "Move affected an unselected unit or lost the queued target.");
                Advance(frame.Tick, CheckStage.Detour);
                break;
            case CheckStage.Detour:
                Check(second.Current == _secondStart, "An unselected scout moved during the detour.");
                if (scout.Current.X >= WallX * CellSize && scout.Current.X < (WallX + 1) * CellSize)
                {
                    Check(scout.Current.Y < WallStart * CellSize || scout.Current.Y >= WallEnd * CellSize,
                        "The scout entered a blocked cell.");
                    _sawDetour = true;
                }
                _reachedFirstGoal |= scout.Current == FirstGoal;
                if (scout.Current == FinalGoal)
                {
                    Check(_sawDetour && _reachedFirstGoal, "The route did not visit the first waypoint and detour around the wall.");
                    Press(Key.Key1);
                    Click(MouseButton.Right, Pixel(new WorldPosition(7 * CellSize, 7 * CellSize)));
                    Advance(frame.Tick, CheckStage.Group);
                    break;
                }
                Check(frame.Tick < 350, "The scout did not reach the second waypoint.");
                Advance(frame.Tick, CheckStage.Detour);
                break;
            case CheckStage.Group:
                var group = session.GetCommandLog()[^1];
                Check(group.Kind == CommandKind.MoveSelected && group.Recipients is { } recipients && recipients.Ids.SequenceEqual([_scout, _secondScout]) &&
                    scout.Order.Destination != second.Order.Destination, "The recalled group did not receive separate destinations.");
                Click(MouseButton.Right, Pixel(new WorldPosition(7 * CellSize, 11 * CellSize)), shift: true);
                Press(Key.S);
                Advance(frame.Tick, CheckStage.Stop);
                break;
            case CheckStage.Stop:
                Check(scout.Order.Phase == OrderPhase.Idle && second.Order.Phase == OrderPhase.Idle &&
                    scout.Order.QueuedCount == 0 && second.Order.QueuedCount == 0, "Stop did not clear the group orders.");
                Press(Key.H);
                Advance(frame.Tick, CheckStage.Hold);
                break;
            case CheckStage.Hold:
                Check(scout.Order.Phase == OrderPhase.Holding && second.Order.Phase == OrderPhase.Holding,
                    "Hold position did not reach both selected units.");
                GD.Print($"Sandbox3D verified: camera, marquee, groups, exact and queued orders, wall detour, stop and hold at tick {frame.Tick}.");
                SetProcess(false);
                GetTree().Quit();
                break;
        }
    }

    private void VerifySelectionAndCamera()
    {
        Vector2 ScoutPixel(EntityId id) => _camera.UnprojectPosition(_actors[id].PickCenter);
        var first = ScoutPixel(_scout);
        var second = ScoutPixel(_secondScout);
        Click(MouseButton.Left, first);
        Check(_selection.Count == 1 && _selection.Contains(_scout), "Single click missed the scout.");
        Click(MouseButton.Left, second, shift: true);
        Check(_selection.Count == 2 && _selection.Contains(_secondScout), "Shift-click did not add the second scout.");
        Click(MouseButton.Left, second, shift: true);
        Check(_selection.Count == 1 && _selection.Contains(_scout), "Shift-click did not remove the selected scout.");
        Click(MouseButton.Left, second, shift: true);
        Press(Key.Key1, control: true);
        Click(MouseButton.Left, first, doubleClick: true);
        Check(_selection.Count == 3, "Double-click did not select only the visible units of the same type.");
        var from = new Vector2(Math.Max(first.X, second.X) + 8, Math.Max(first.Y, second.Y) + 8);
        var to = new Vector2(Math.Min(first.X, second.X) - 8, Math.Min(first.Y, second.Y) - 8);
        Mouse(MouseButton.Left, from, true);
        Motion(to, to - from, MouseButtonMask.Left);
        Check(_overlay.Marquee is { } box && box.HasPoint(first) && box.HasPoint(second), "Dragging did not show the selection rectangle.");
        Mouse(MouseButton.Left, to, false);
        Check(_overlay.Marquee is null && _selection.Ids.SequenceEqual([_scout, _secondScout]),
            $"Marquee expected {_scout.Value}, {_secondScout.Value}, selected {string.Join(", ", _selection.Snapshot().Select(id => id.Value))}; rectangle {_overlay.Marquee}.");
        Press(Key.Escape);
        Check(_selection.Count == 0, "Escape did not clear the selection.");
        Press(Key.Key1);
        Check(_selection.Ids.SequenceEqual([_scout, _secondScout]), "Control group recall failed.");
        Press(Key.A, control: true);
        Check(_selection.Count == Starts.Length, "Select all missed a unit.");
        Click(MouseButton.Left, first);
        Press(Key.Space);
        var centerPixel = ViewRect.GetCenter();
        var centered = PlanarCamera.GroundPoint(_camera, centerPixel)!.Value;
        Check(centered.DistanceTo(_actors[_scout].Node.Position) < 0.004f, "Center camera did not focus on the selected scout.");
        Motion(centerPixel, new Vector2(-180, 12), MouseButtonMask.Middle);
        Check(PlanarCamera.GroundPoint(_camera, centerPixel)!.Value.X > centered.X + 1, "Middle-button panning did not move the camera.");
        var cursor = centerPixel + new Vector2(80, 40);
        var anchor = PlanarCamera.GroundPoint(_camera, cursor)!.Value;
        float size = _camera.Size;
        Mouse(MouseButton.WheelUp, cursor, true);
        Check(_camera.Size < size && PlanarCamera.GroundPoint(_camera, cursor)!.Value.DistanceTo(anchor) < 0.004f,
            "Zoom moved the point under the cursor.");
    }

    private void Advance(long tick, CheckStage stage)
    {
        _checkStage = stage;
        _checkTick = tick + 1;
        _session!.StepOnce();
    }

    private Vector2 Pixel(WorldPosition point) => _camera.UnprojectPosition(Display(point));

    private static EntityPose ActorPose(ReadOnlySpan<EntityPose> poses, EntityId id)
    {
        foreach (var pose in poses)
            if (pose.Id == id) return pose;
        throw new InvalidOperationException("Verified actor disappeared.");
    }

    private void Click(MouseButton button, Vector2 pixel, bool shift = false, bool doubleClick = false)
    {
        Mouse(button, pixel, true, shift, doubleClick);
        Mouse(button, pixel, false, shift);
    }

    private void Mouse(MouseButton button, Vector2 pixel, bool down, bool shift = false, bool doubleClick = false)
    {
        using var input = new InputEventMouseButton
        { ButtonIndex = button, Position = pixel, Pressed = down, ShiftPressed = shift, DoubleClick = doubleClick };
        GetViewport().PushInput(input, inLocalCoords: true);
    }

    private void Motion(Vector2 pixel, Vector2 relative, MouseButtonMask buttons)
    {
        using var input = new InputEventMouseMotion { Position = pixel, Relative = relative, ButtonMask = buttons };
        GetViewport().PushInput(input, inLocalCoords: true);
    }

    private void Press(Key key, bool control = false)
    {
        using var input = new InputEventKey { PhysicalKeycode = key, Pressed = true, CtrlPressed = control };
        GetViewport().PushInput(input, inLocalCoords: true);
    }

    private static void Check(bool success, string error)
    {
        if (!success) throw new InvalidOperationException(error);
    }
}
