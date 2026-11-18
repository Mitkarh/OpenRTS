using Godot;
using OpenRTS.Core;

public partial class Sandbox
{
    private long _rampCheckTick;
    private bool _reachedPlateau, _descendedRamp, _climbedDiagonally, _detouredAroundCliff;

    private void VerifyRamps(FrameExchange.FrameLease frame)
    {
        if (frame.Tick < _rampCheckTick) return;
        var session = _session!;
        var grid = frame.Grid!;
        var scout = frame.Poses[0];
        bool diagonal = _mapMatch?.Map.Id == "diagonal-ramps";
        const int size = RampScenario.CellSize;
        var highGoal = diagonal ? new WorldPosition(13 * size + 256, 12 * size + 384, size) : RampScenario.PlateauGoal;
        var lowGoal = diagonal ? highGoal with { Y = 20 * size + 384, Elevation = 0 } : RampScenario.LowGoal;
        if (frame.Tick == 0)
        {
            Require(frame.Poses.Length == (diagonal ? 2 : RampScenario.UnitCount) && grid.HasSlopedSurface &&
                _rampTerrain!.Mesh.GetSurfaceCount() == (diagonal ? 4 : 3), "The ramp scenario or its terrain materials did not load.");
            foreach (var pose in frame.Poses)
                Require(pose.Current.Elevation == grid.SurfaceElevationAt(pose.Current), "A unit started off its surface.");
            Require(CommandPoint(Pixel(highGoal)) == highGoal, "Plateau picking lost height or position.");
            int rampCell = diagonal ? 8 : 16;
            var ramp = new WorldPosition(rampCell * size + 256, rampCell * size + 256);
            ramp = ramp with { Elevation = grid.SurfaceElevationAt(ramp) };
            Require(CommandPoint(Pixel(ramp)) == ramp, "Picking the ramp lost the sub-cell position.");
            var cliff = diagonal ? new WorldPosition(highGoal.X, 17 * size, size / 2)
                : new WorldPosition(8 * size + 256, RampScenario.PlateauSouth * size, size);
            Require(CommandPoint(Pixel(cliff)) is null, "An exposed cliff face accepted a ground order.");
            var cameraPosition = _camera!.Position;
            float cameraSize = _camera.Size;
            var anchor = Pixel(ramp);
            ZoomCamera(anchor, 0.8f);
            Require(CommandPoint(anchor) == ramp, "Zoom did not preserve the point on the ramp.");
            _camera.Position = cameraPosition;
            _camera.Size = cameraSize;

            _selection.Reset();
            var scoutPixel = Screen(scout);
            Click(MouseButton.Left, scoutPixel);
            Click(MouseButton.Left, scoutPixel, pressed: false);
            Require(_selected == scout.Id, $"The usual sandbox selection did not pick the scout at {scoutPixel}.");
            Click(MouseButton.Right, Pixel(highGoal));
            Click(MouseButton.Right, Pixel(lowGoal), shift: true);
        }
        else
        {
            if (frame.Tick == 1)
            {
                var commands = session.GetCommandLog();
                Require(commands.Length == 2 && commands[0].Position == highGoal &&
                    commands[1].Position == lowGoal && commands[1].Queued,
                    "Sandbox mouse commands lost the exact target or queue flag.");
            }
            Require(scout.Current.Elevation == grid.SurfaceElevationAt(scout.Current), "The scout lost contact with its surface.");
            int step = scout.Current.Elevation - scout.Previous.Elevation;
            Require(Math.Abs(step) <= RampScenario.RisePerCell / 4, "The scout crossed a cliff discontinuity.");
            if (diagonal)
            {
                var rise = grid.SurfaceRiseAt(grid.CellAt(scout.Current));
                _climbedDiagonally |= step > 0 && rise is { X: not 0, Y: not 0 };
                // The second command points straight across the south wall. Leaving the plateau requires a route around it.
                _detouredAroundCliff |= _reachedPlateau && (scout.Current.X < 10 * size || scout.Current.Y < 10 * size);
            }
            else if (step != 0)
                Require(scout.Current.X >= RampScenario.RampLeft * RampScenario.CellSize &&
                    scout.Current.X < RampScenario.RampRight * RampScenario.CellSize, "Height changed outside the ramp.");
            _descendedRamp |= _reachedPlateau && step < 0;
            if (!_reachedPlateau && scout.Current == highGoal)
            {
                _reachedPlateau = true;
                if (_capturePath is not null && DisplayServer.GetName() != "headless") CaptureRampTerrain();
            }
            if (_reachedPlateau && scout.Current == lowGoal)
            {
                Require(_descendedRamp, "The scout did not descend the ramp.");
                Require(!diagonal || _climbedDiagonally && _detouredAroundCliff,
                    "The route did not climb a two-axis ramp and detour around the cliff.");
                using var replay = _mapMatch is { } match
                    ? match.CreateWorld(new(), speedPerTick: _manifest.Settings.Simulation.SpeedPerTick, tickRate: _manifest.Settings.Simulation.TickRate)
                    : _rampScenario!.CreateWorld(1);
                var log = session.GetCommandLog();
                int begin = 0;
                for (long tick = 1; tick <= frame.Tick; tick++)
                {
                    int end = begin;
                    while (end < log.Length && log[end].Tick == tick) end++;
                    replay.Step(log.AsSpan(begin, end - begin));
                    begin = end;
                }
                var poses = new EntityPose[frame.Poses.Length];
                replay.CopyPoses(poses);
                Require(frame.Poses.SequenceEqual(poses), "Replaying the same orders produced different poses.");
                _verificationDone = true;
                string route = diagonal ? "diagonal ascent, descent and cliff detour" : "ascent and descent";
                GD.Print($"OPENRTS_RAMPS:PASS exact clicks, rock occlusion, {route} and replay at tick {frame.Tick}");
                GetTree().Quit();
                return;
            }
        }

        Require(frame.Tick < 650, $"The scout did not complete its route: {scout.Current}.");
        _rampCheckTick = frame.Tick + 1;
        session.StepOnce();

        Vector2 Pixel(WorldPosition p) => _camera!.UnprojectPosition(new Vector3(p.X, p.Elevation, p.Y) / RampScenario.CellSize);
        void Click(MouseButton button, Vector2 point, bool pressed = true, bool shift = false) =>
            _UnhandledInput(new InputEventMouseButton { ButtonIndex = button, Position = point, Pressed = pressed, ShiftPressed = shift });
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }

    private void CaptureRampTerrain()
    {
        var path = Path.GetFullPath(_capturePath!);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RenderingServer.ForceDraw(false);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(path) != Error.Ok) throw new IOException("Could not save the ramp terrain capture.");
    }
}
