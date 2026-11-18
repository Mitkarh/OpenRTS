using System;
using System.Diagnostics;
using Godot;

public partial class Sandbox
{
    private string? _verifyReplayPath;
    private async System.Threading.Tasks.Task VerifyReplayFlow()
    {
        try
        {
            ((Button)_matchSetup!.FindChild("MainReplays", owned: false)).EmitSignal(Button.SignalName.Pressed);
            var row = Descendants(_matchSetup).FirstOrDefault(node => node.HasMeta("ReplayPath") && (string)node.GetMeta("ReplayPath") == _verifyReplayPath)
                ?? throw new InvalidOperationException("The recorded match is missing from the replay browser.");
            var watch = row.GetChildren().OfType<Button>().Single(); CheckSkirmish(!watch.Disabled, "Replay bootstrap unavailable."); watch.EmitSignal(Button.SignalName.Pressed);
            async System.Threading.Tasks.Task Wait(Func<bool> ready)
            {
                var clock = Stopwatch.StartNew();
                while (!ready())
                {
                    if (_session?.Completion.IsFaulted == true) _session.Completion.GetAwaiter().GetResult();
                    if (clock.Elapsed.TotalSeconds > 15) throw new TimeoutException("Godot replay scenario stalled.");
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                }
            }
            await Wait(() => _matchSetup is null && _session?.Replay?.Info is not null);
            var replay = _session!.Replay!; _edgeScroll = false;
            CheckSkirmish(replay.IsPaused && replay.Position == 0 && !Controls(1) && !Controls(2), "Replay accepted player control or advanced before Play.");
            try { _session.Submit(OpenRTS.Core.Orders.CommandKind.Stop, entity: new(1)); throw new Exception("Replay Submit was accepted."); }
            catch (InvalidOperationException) { }
            ((Button)FindChild("ReplayStep", owned: false)).EmitSignal(Button.SignalName.Pressed); await Wait(() => replay.Position == 1);
            var timeline = (HSlider)FindChild("ReplayTimeline", owned: false);
            var seek = (Button)FindChild("ReplaySeek", owned: false);
            long middle = Math.Max(2, replay.Info!.EndTick / 2);
            timeline.Value = middle; seek.EmitSignal(Button.SignalName.Pressed); await Wait(() => replay.Position == middle && !replay.IsSeeking);
            timeline.Value = 1; seek.EmitSignal(Button.SignalName.Pressed); await Wait(() => replay.Position == 1 && !replay.IsSeeking);
            var viewer = (OptionButton)FindChild("ReplayViewer", owned: false); viewer.Select(1); viewer.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
            using (var frame = replay.Frames.AcquireLatest(_fogViewer)) CheckSkirmish(frame is not null && frame.Viewer == 1, "Replay POV does not use player fog.");
            var speed = (OptionButton)FindChild("ReplaySpeed", owned: false); speed.Select(5); speed.EmitSignal(OptionButton.SignalName.ItemSelected, 5);
            ((Button)FindChild("ReplayPause", owned: false)).EmitSignal(Button.SignalName.Pressed);
            await Wait(() => replay.Status == "Конец повтора"); CheckSkirmish(replay.Position == replay.Info.EndTick && replay.IsPaused, "Replay did not validate/pause at its end.");
            ((Button)FindChild("ReplayRestart", owned: false)).EmitSignal(Button.SignalName.Pressed);
            await Wait(() => replay.Position == 0 && !replay.IsSeeking && _replayGeneration == replay.Generation);
            timeline.Value = middle; seek.EmitSignal(Button.SignalName.Pressed); await Wait(() => replay.Position == middle && !replay.IsSeeking);
            if (_capturePath is not null && DisplayServer.GetName() != "headless") await CaptureNetworkFrame(_capturePath);
            GD.Print("OPENRTS_REPLAY_PASS:" + replay.Info.FinalHash);
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
