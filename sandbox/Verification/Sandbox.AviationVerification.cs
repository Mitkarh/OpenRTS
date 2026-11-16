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
            var repair = actor with { Order = actor.Order with { Kind = UnitActionKind.Repair } };
            roaming.Update([repair, building], 110);
            if (roaming.Offset(repair, 122) != Vector2.Zero) throw new InvalidOperationException("Repair kept roaming after its transition.");
        }
    }
}
