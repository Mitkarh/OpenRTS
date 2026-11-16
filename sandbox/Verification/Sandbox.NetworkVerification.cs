using System;
using System.IO;
using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

public partial class Sandbox
{
    private bool _verifyNetwork;
    private bool _networkVerifyIssued, _networkVerifyMenu, _networkVerifyMenuClosed, _networkVerifyLeaving;
    private System.Threading.Tasks.Task? _networkCapture;
    private long _networkInputTick, _networkMenuTick;
    private string _networkVerifyRole = "", _networkVerifyPinFile = "";
    private int _networkVerifyPort = 17771;
    private double _networkVerifyElapsed;
    private void SelectNetworkVerificationMatch()
    {
        void Choose(string name, int value)
        {
            var button = (OptionButton)_matchSetup!.FindChild(name, owned: false);
            button.Select(value); button.EmitSignal(OptionButton.SignalName.ItemSelected, value);
        }
        Choose("NetworkMode", _networkVerifyRole == "host" ? 1 : 2);
        Choose("PlayerCount", 1); // exactly two players
        ((SpinBox)_matchSetup!.FindChild("NetworkPort", owned: false)).Value = _networkVerifyPort;
        if (_networkVerifyRole == "join")
            ((LineEdit)_matchSetup.FindChild("NetworkPin", owned: false)).Text = File.ReadAllText(_networkVerifyPinFile).Trim();
        ((Button)_matchSetup.FindChild("Launch", owned: false)).EmitSignal(Button.SignalName.Pressed);
        if (_session?.Network is { } session && _networkVerifyRole == "host")
            File.WriteAllText(_networkVerifyPinFile, session.Fingerprint);
    }
    private void VerifyNetworkFrame(FrameExchange.FrameLease frame)
    {
        if (!_verifyNetwork) return;
        var network = _session!.Network!;
        if (!_networkVerifyIssued && frame.Tick >= 2)
        {
            if (Controls(3 - network.Player) || !Controls(network.Player) || CommandOwner != network.Player)
                throw new InvalidOperationException("Network ownership is not bound to local player.");
            var actor = frame.Poses.ToArray().First(p => p.Presentation.Owner == network.Player);
            _session.Submit(CommandKind.SetRally, actor.Current, actor.Id);
            _session.Submit(CommandKind.Stop, entity: actor.Id);
            var producer = frame.Poses.ToArray().FirstOrDefault(p => p.Presentation.Owner == network.Player && _catalog!.Units[p.UnitType].Products.Count > 0);
            if (producer.Id.Value != 0)
                _session.Submit(CommandKind.EnqueueTask, entity: producer.Id, action: _catalog!.Units[producer.UnitType].Products[0].Id);
            _networkInputTick = frame.Tick;
            // Exercise the real panel with the local base selected.
            _selected = actor.Id; RefreshSelection(); UpdateCommandPanel(frame);
            _networkVerifyIssued = true;
            GD.Print($"OPENRTS_NETWORK_INPUT:{network.Player}");
        }
        if (!_networkVerifyMenu && frame.Tick >= _networkInputTick + 4)
        {
            ToggleMatchMenu(); _networkVerifyMenu = true; _networkMenuTick = frame.Tick;
        }
        if (_networkVerifyMenu && _matchMenu!.Visible && frame.Tick >= _networkMenuTick + 4)
        {
            if (_capturePath is not null && DisplayServer.GetName() != "headless")
            {
                _networkCapture ??= CaptureNetworkFrame(_capturePath);
                if (!_networkCapture.IsCompleted) return;
                _networkCapture.GetAwaiter().GetResult();
            }
            _networkVerifyMenuClosed = true;
            ToggleMatchMenu(); // local menu must not stop synchronized time
            GD.Print($"OPENRTS_NETWORK_MENU:{frame.Tick}");
        }
        if (!_networkVerifyLeaving && _networkVerifyMenuClosed && frame.Tick >= _networkInputTick + 16)
        {
            _networkVerifyLeaving = true;
            if (_networkVerifyRole == "host") network.Leave();
        }
        if (network.Completion.IsCompletedSuccessfully)
        {
            using var log = File.OpenRead(network.JournalPath);
            var replay = OpenRTS.Network.MatchJournal.Replay(log, network.Descriptor, () => CreateNetworkWorld(_mapMatch!));
            if (replay != network.Completion.Result) throw new InvalidOperationException("Godot match replay differs.");
            GD.Print("OPENRTS_NETWORK_PASS:" + network.Completion.Result.StateHash);
            _verificationDone = true; GetTree().Quit();
        }
    }
    private async System.Threading.Tasks.Task CaptureNetworkFrame(string path)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        DisplayServer.WindowMoveToForeground();
        RenderingServer.ForceDraw(false);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(path) != Error.Ok) throw new IOException("Network screenshot failed.");
    }

}
