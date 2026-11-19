using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Godot.Rendering;

public partial class Sandbox
{
    private bool _verifyAviation;
    private async void VerifyAviation(FrameExchange.FrameLease frame)
    {
        try
        {
            _verificationDone = true;
            VerifyAirWorkVisuals();
            VerifyAirTransportInput();
            if (_manifest.Settings.Presentation.ModelOverrides.GetValueOrDefault("airfield") == "iso-airfield")
                VerifyIsometricAirfield();
            var poses = frame.Poses.ToArray();
            var report = _session!.Completion.GetAwaiter().GetResult();
            if (frame.Tick != report.Tick || !poses.Any(p => _catalog!.Units[p.UnitType].Airbase is not null) ||
                !poses.Any(p => _catalog!.Units[p.UnitType].Flight is not null) || _sprites!.SpriteCount < poses.Length)
                throw new InvalidOperationException("Incomplete aviation presentation.");
            var engineer = poses.Single(p => p.Id.Value == 13);
            if (engineer.Order.Kind != UnitActionKind.Capture || engineer.Order.Phase != OrderPhase.Engaging ||
                !engineer.Flight.Airborne || poses.Single(p => p.Id.Value == 16).Upgrade.Completed != "owned" ||
                poses.Where(p => p.Id.Value is 18 or 19).Any(p => p.Carrier != default || p.Current.Elevation != 0))
                throw new InvalidOperationException("Air construction, upgrade, capture or mixed-size transport failed in the shared scenario.");
            if (_capturePath is not null && DisplayServer.GetName() != "headless")
            {
                _selected = engineer.Id;
                _camera!.Size = 42; CenterCamera(new(32, 38));
                RefreshSelection(); UpdateCommandPanel(frame); UpdateSelection(1); UpdateTerminal(frame, 1);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                string path = Path.GetFullPath(_capturePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var picture = GetViewport().GetTexture().GetImage();
                if (picture.SavePng(path) != Error.Ok) throw new IOException("Aviation capture failed.");
            }
            GD.Print("OPENRTS_AVIATION:PASS");
            GD.Print("OPENRTS_SIMULATION:" + JsonSerializer.Serialize(report)); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }

    private void VerifyIsometricAirfield()
    {
        var units = _catalog!.Units.ToList();
        int fieldType = units.FindIndex(t => t.Id == "airfield");
        int fighterType = units.FindIndex(t => t.Id == "fighter");
        var airbase = units[fieldType].Airbase!;
        const int size = 64;
        byte grass = _catalog.Locomotion!.TerrainIndex("grass");
        using var world = new SimulationWorld(units, size * 1024, size * 1024,
            navigationGrid: new(size, size, terrain: Enumerable.Repeat(grass, size * size)));
        var origin = new WorldPosition(32 * 1024, 32 * 1024);
        var field = world.AddInitialUnit(fieldType, origin, 1);
        WorldPosition At(WorldPosition offset) => new(origin.X + offset.X, origin.Y + offset.Y);

        // These offsets are the centers of the painted stands and runway in the Blender model.
        WorldPosition[] stands = [new(-2624, -2432), new(-576, -2432), new(1600, -1152), new(1600, 1024)];
        var runwayStart = At(new(-1664, -1536));
        var runwayEnd = At(new(-1664, 4608));
        if (!airbase.Slots.SequenceEqual(stands) || At(airbase.RunwayStart!.Value) != runwayStart ||
            At(airbase.RunwayEnd!.Value) != runwayEnd)
            throw new InvalidOperationException("Airfield parking or runway is out of sync with the model.");
        var fighters = stands.Select(at => world.AddInitialUnit(fighterType, At(at), 1)).ToArray();
        var poses = new EntityPose[world.Count];
        world.Step([new(1, 0, 1, CommandKind.MoveSelected, Recipients: new(fighters), Position: new(49152, 49152))]);
        var departed = new bool[fighters.Length];
        var observedRun = new bool[fighters.Length];
        for (int tick = 0; tick < 360 && departed.Any(value => !value); tick++)
        {
            world.Step();
            world.CopyPoses(poses);
            if (poses.Count(p => p.Flight.Phase is FlightPhase.Taxiing or FlightPhase.RunwayTakeoff) > 1)
                throw new InvalidOperationException("Two fighters reserved the same runway.");
            for (int i = 0; i < fighters.Length; i++)
            {
                var fighter = poses.Single(p => p.Id == fighters[i]);
                if (fighter.Flight.Phase == FlightPhase.RunwayTakeoff)
                {
                    if ((!observedRun[i] && fighter.Current != runwayStart) ||
                        fighter.Current.X != runwayStart.X || fighter.Flight.Airbase != field)
                        throw new InvalidOperationException($"Fighter from stand {i + 1} missed the runway.");
                    observedRun[i] = true;
                }
                if (observedRun[i] && !departed[i] && fighter.Flight.Phase == FlightPhase.Flying)
                {
                    if (fighter.Current.X != runwayEnd.X || fighter.Current.Y != runwayEnd.Y || fighter.Current.Elevation <= 0)
                        throw new InvalidOperationException($"Fighter from stand {i + 1} did not depart through the open end.");
                    departed[i] = true;
                }
            }
        }
        if (departed.Any(value => !value))
            throw new InvalidOperationException("A fighter failed to depart from its airfield stand.");

        world.Step(fighters.Select((fighter, i) =>
            new WorldCommand(world.Tick + 1, 0, i + 1, CommandKind.ReturnToBase, fighter)).ToArray());
        var returned = new bool[fighters.Length];
        var observedLanding = new bool[fighters.Length];
        var landingY = new int[fighters.Length];
        for (int tick = 0; tick < 900 && returned.Any(value => !value); tick++)
        {
            world.Step();
            world.CopyPoses(poses);
            if (poses.Count(p => p.Flight.Phase is FlightPhase.RunwayLanding or FlightPhase.Taxiing) > 1)
                throw new InvalidOperationException("Two fighters used the same runway on return.");
            for (int i = 0; i < fighters.Length; i++)
            {
                var fighter = poses.Single(p => p.Id == fighters[i]);
                if (fighter.Flight.Phase == FlightPhase.RunwayLanding)
                {
                    if ((!observedLanding[i] && fighter.Current.Y <= runwayEnd.Y) ||
                        (observedLanding[i] && fighter.Current.Y >= landingY[i]) ||
                        fighter.Current.X != runwayStart.X || fighter.Flight.Airbase != field)
                        throw new InvalidOperationException($"Fighter from stand {i + 1} did not approach from the open end.");
                    observedLanding[i] = true;
                    landingY[i] = fighter.Current.Y;
                }
                returned[i] |= observedLanding[i] && fighter.Current == At(stands[i]) &&
                    fighter.Flight.Phase is FlightPhase.Landed or FlightPhase.Servicing;
            }
        }
        if (returned.Any(value => !value))
            throw new InvalidOperationException("A fighter failed to return to its airfield stand. " +
                string.Join(", ", poses.Where(p => p.Id != field).Select(p => $"{p.Id}: {p.Flight.Phase} at {p.Current}")));
        GD.Print("OPENRTS_ISOMETRIC_AIRFIELD:PASS four departures and four open-end landings");
    }

    private void VerifyAirWorkVisuals()
    {
        var units = _catalog!.Units;
        int worker = units.ToList().FindIndex(t => t.Id == "engineering-tiltrotor");
        int target = units.ToList().FindIndex(t => t.Id == "power-plant");
        var at = new WorldPosition(16384, 16384, 3584);
        var building = new EntityPose(new(90002), target, at with { Elevation = 0 }, at with { Elevation = 0 });
        foreach (var kind in new[] { UnitActionKind.Build, UnitActionKind.Capture, UnitActionKind.UpgradeSupport })
        {
            var roaming = new WorkFlightVisual(units.Select(p => p.Flight?.WorkRoaming ?? false).ToArray());
            var fixedHover = new WorkFlightVisual(new bool[units.Count]);
            var actor = new EntityPose(new(90001), worker, at, at,
                Order: new(kind, OrderPhase.Engaging, at, building.Id, 0, 0))
                { Flight = new(FlightPhase.Hovering, 0, 0, default, -1, true) };
            var moving = actor with { Current = at with { X = at.X + 1024 } };
            var effects = _combatEffects!;
            var source0 = effects.WorkSource(moving, at with { Y = at.Y + 4096 }, 0);
            var source1 = effects.WorkSource(moving, at with { Y = at.Y + 4096 }, 1);
            if (source0 == source1 || source1.Elevation != moving.Current.Elevation)
                throw new InvalidOperationException("The work beam did not follow the moving aircraft at flight height.");
            EntityPose[] poses = [actor, building];
            roaming.Update(poses, 10); fixedHover.Update(poses, 10);
            float heading = roaming.Heading(actor, 80, 0);
            if (heading != roaming.Heading(actor, 85, 0) || heading == roaming.Heading(actor, 136, 0) ||
                fixedHover.Heading(actor, 80, 0.7f) != 0.7f || MathF.Abs(roaming.Heading(actor, 10, 0)) > 0.0001f)
                throw new InvalidOperationException($"Roof heading failed: hold={heading}/{roaming.Heading(actor, 85, 0)}, next={roaming.Heading(actor, 136, 0)}, disabled={fixedHover.Heading(actor, 80, 0.7f)}, start={roaming.Heading(actor, 10, 0)}.");
            var repair = actor with { Order = actor.Order with { Kind = UnitActionKind.Repair } };
            roaming.Update([repair, building], 150);
            if (MathF.Abs(Mathf.Wrap(roaming.Heading(repair, 162, 0), -MathF.PI, MathF.PI)) > 0.0001f)
                throw new InvalidOperationException("Repair kept roaming after its transition.");
        }
    }

    private void VerifyAirTransportInput()
    {
        var savedSelection = _selection.Ids.ToArray();
        var savedPointer = _pointer;
        float savedSize = _camera!.Size;
        var savedPosition = _camera.Position;
        foreach (var (carrierId, passengerId) in new[] { (15u, 18u), (5u, 7u) })
        {
            int index = Array.FindIndex(_displayPoses, 0, _displayCount, p => p.Id.Value == carrierId);
            var original = _displayPoses[index];
            var at = original.Current with { Elevation = 3584 };
            var flying = original with { Previous = at, Current = at, Flight = original.Flight with { Phase = FlightPhase.Hovering, Airborne = true } };
            _displayPoses[index] = flying;
            _selection.Change([new(passengerId)], OpenRTS.Godot.Controls.SelectionChange.Replace);
            RefreshSelection();
            foreach (float zoom in new[] { 24f, 48f })
            {
                _camera.Size = zoom;
                CenterCamera(new(at.X / 1024f, at.Y / 1024f));
                var center = _sprites!.DisplayPosition(flying, 1);
                float extent = carrierId == 15 ? 1.6f : 0.9f;
                _pointer = _camera.UnprojectPosition(center + new Vector3(extent, 0, 0));
                var point = CommandPoint(_pointer);
                if (point is null || Pick(point.Value)?.Id != flying.Id || !CanBoardSelected(flying))
                    throw new InvalidOperationException("Aircraft model bounds or airborne boarding selection failed at this zoom.");
                UpdateRtsCursor();
                if (_rtsOverlay!.Cursor != OpenRTS.Godot.Controls.RtsCursor.Enter)
                    throw new InvalidOperationException("Hovering transport did not offer its Enter cursor.");
            }
            _displayPoses[index] = original;
        }
        _selection.Change(savedSelection, OpenRTS.Godot.Controls.SelectionChange.Replace);
        _pointer = savedPointer; _camera.Size = savedSize; _camera.Position = savedPosition;
        RefreshSelection();
    }
}
