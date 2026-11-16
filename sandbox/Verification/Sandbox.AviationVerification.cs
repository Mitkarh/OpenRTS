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

    private void VerifyAirWorkVisuals()
    {
        var units = _catalog!.Units;
        int worker = units.ToList().FindIndex(t => t.Id == "engineering-tiltrotor");
        int target = units.ToList().FindIndex(t => t.Id == "power-plant");
        var at = new WorldPosition(16384, 16384, 3584);
        var building = new EntityPose(new(90002), target, at with { Elevation = 0 }, at with { Elevation = 0 });
        foreach (var kind in new[] { UnitActionKind.Build, UnitActionKind.Capture, UnitActionKind.UpgradeSupport })
        {
            var roaming = new WorkFlightVisual(_catalog.Presentations.Select(p => p.WorkRoaming).ToArray(), units);
            var fixedHover = new WorkFlightVisual(new bool[units.Count], units);
            var actor = new EntityPose(new(90001), worker, at, at,
                Order: new(kind, OrderPhase.Engaging, at, building.Id, 0, 0))
                { Flight = new(FlightPhase.Hovering, 0, 0, default, -1, true) };
            EntityPose[] poses = [actor, building];
            roaming.Update(poses, 10); fixedHover.Update(poses, 10);
            if (roaming.Offset(actor, 10) != Vector2.Zero || fixedHover.Offset(actor, 80) != Vector2.Zero ||
                roaming.Offset(actor, 80) == Vector2.Zero || roaming.Offset(actor, 80) != roaming.Offset(actor, 80))
                throw new InvalidOperationException("Work roaming moved the initial pose, ignored its flag or was not repeatable.");
            var held = roaming.Offset(actor, 80);
            if (held != roaming.Offset(actor, 85)) throw new InvalidOperationException("The worker did not pause between roof flights.");
            if (held == roaming.Offset(actor, 136)) throw new InvalidOperationException("Consecutive roof work cells did not change.");
            float heading = roaming.Heading(actor, 80, 0);
            if (heading != roaming.Heading(actor, 85, 0) || heading == roaming.Heading(actor, 136, 0) ||
                fixedHover.Heading(actor, 80, 0.7f) != 0.7f || MathF.Abs(roaming.Heading(actor, 10, 0)) > 0.0001f)
                throw new InvalidOperationException($"Roof heading failed: hold={heading}/{roaming.Heading(actor, 85, 0)}, next={roaming.Heading(actor, 136, 0)}, disabled={fixedHover.Heading(actor, 80, 0.7f)}, start={roaming.Heading(actor, 10, 0)}.");
            var repair = actor with { Order = actor.Order with { Kind = UnitActionKind.Repair } };
            roaming.Update([repair, building], 150);
            if (roaming.Offset(repair, 162) != Vector2.Zero || MathF.Abs(Mathf.Wrap(roaming.Heading(repair, 162, 0), -MathF.PI, MathF.PI)) > 0.0001f)
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
