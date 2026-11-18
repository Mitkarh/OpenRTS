using Godot;
using OpenRTS.Core;

public partial class Sandbox
{
    private long _rampCheckTick;
    private bool _reachedPlateau, _descendedRamp;

    private void VerifyRamps(FrameExchange.FrameLease frame)
    {
        if (frame.Tick < _rampCheckTick) return;
        var session = _session!;
        var grid = frame.Grid!;
        var scout = frame.Poses[0];
        if (frame.Tick == 0)
        {
            Require(frame.Poses.Length == RampScenario.UnitCount && grid.HasSlopedSurface &&
                _rampTerrain!.Mesh.GetSurfaceCount() == 3, "The ramp scenario or its three terrain materials did not load.");
            foreach (var pose in frame.Poses)
                Require(pose.Current.Elevation == grid.SurfaceElevationAt(pose.Current), "A unit started off its surface.");
            Require(CommandPoint(Pixel(RampScenario.PlateauGoal)) == RampScenario.PlateauGoal, "Plateau picking lost height or position.");
            var ramp = new WorldPosition(16 * RampScenario.CellSize + 256, 16 * RampScenario.CellSize + 256);
            ramp = ramp with { Elevation = grid.SurfaceElevationAt(ramp) };
            Require(CommandPoint(Pixel(ramp)) == ramp, "Picking the ramp lost the sub-cell position.");
            var cliff = new WorldPosition(8 * RampScenario.CellSize + 256, RampScenario.PlateauSouth * RampScenario.CellSize, RampScenario.CellSize);
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
            Click(MouseButton.Right, Pixel(RampScenario.PlateauGoal));
            Click(MouseButton.Right, Pixel(RampScenario.LowGoal), shift: true);
        }
        else
        {
            if (frame.Tick == 1)
            {
                var commands = session.GetCommandLog();
                Require(commands.Length == 2 && commands[0].Position == RampScenario.PlateauGoal &&
                    commands[1].Position == RampScenario.LowGoal && commands[1].Queued,
                    "Sandbox mouse commands lost the exact target or queue flag.");
            }
            Require(scout.Current.Elevation == grid.SurfaceElevationAt(scout.Current), "The scout lost contact with its surface.");
            int step = scout.Current.Elevation - scout.Previous.Elevation;
            Require(Math.Abs(step) <= RampScenario.RisePerCell / 4, "The scout crossed a cliff discontinuity.");
            if (step != 0)
                Require(scout.Current.X >= RampScenario.RampLeft * RampScenario.CellSize &&
                    scout.Current.X < RampScenario.RampRight * RampScenario.CellSize, "Height changed outside the ramp.");
            _descendedRamp |= _reachedPlateau && step < 0;
            if (!_reachedPlateau && scout.Current == RampScenario.PlateauGoal)
            {
                _reachedPlateau = true;
                if (_capturePath is not null && DisplayServer.GetName() != "headless") CaptureRampTerrain();
            }
            if (_reachedPlateau && scout.Current == RampScenario.LowGoal)
            {
                Require(_descendedRamp, "The scout did not descend the ramp.");
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
                GD.Print($"OPENRTS_RAMPS:PASS exact clicks, rock occlusion, ascent, descent and replay at tick {frame.Tick}");
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
