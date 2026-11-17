using System;
using System.IO;
using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

public partial class Sandbox
{
    private bool _networkVerifyReconnect, _networkBreakRequested, _networkReconnectAttempted, _networkReconnectConfirmed;
    private long _networkRejoinedTick = -1;
    private void VerifyReconnectButton(Button button)
    {
        if (!_verifyNetwork || !_networkVerifyReconnect || _networkVerifyRole != "join" || _networkReconnectAttempted) return;
        if (_matchSetup is not null) throw new InvalidOperationException("Reconnect did not use the in-game failure screen.");
        _networkReconnectAttempted = true; GD.Print("OPENRTS_NETWORK_RECONNECT_BUTTON:pressed");
        button.CallDeferred(GodotObject.MethodName.EmitSignal, Button.SignalName.Pressed);
    }
    private bool _verifyNetwork;
    private string? _networkVerifyDirectoryUrl;
    private bool _networkVerifyNet3, _networkVerifyPaused, _networkPauseRequested;
    private ulong _networkPauseBegan;
    private long _networkReadyRevision;
    private long _networkPauseTick;
    private readonly System.Collections.Generic.HashSet<uint> _networkPauseSeen = new();
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
        if (_networkVerifyNet3 && _networkVerifyRole == "host") Choose("Maps", Array.FindIndex(_manifest.Settings.Match.Maps, map => map.Path == MapScenario.CrossroadsMap));
        Choose("PlayerCount", _networkVerifyNet3 && _networkVerifyRole == "host" ? 3 : 1);
        ((SpinBox)_matchSetup!.FindChild("NetworkPlayer", owned: false)).Value = _networkVerifyRole == "join3" ? 3 : 2;
        if (_networkVerifyRole == "watch") Choose("JoinRole", 1);
        if (_networkVerifyNet3 && _networkVerifyRole == "host") ((SpinBox)_matchSetup.FindChild("NetworkAi", owned: false)).Value = 1;
        ((SpinBox)_matchSetup!.FindChild("NetworkPort", owned: false)).Value = _networkVerifyPort;
        if (_networkVerifyDirectoryUrl is not null)
        {
            ((LineEdit)_matchSetup.FindChild("DirectoryUrl", owned: false)).Text = _networkVerifyDirectoryUrl;
            if (_networkVerifyRole == "host")
            {
                ((CheckButton)_matchSetup.FindChild("PublishDirectory", owned: false)).ButtonPressed = true;
                ((Button)_matchSetup.FindChild("CreateGame", owned: false)).EmitSignal(Button.SignalName.Pressed);
            }
            else _ = VerifyDirectoryJoin();
            return;
        }
        if (_networkVerifyRole != "host")
            ((LineEdit)_matchSetup.FindChild("NetworkPin", owned: false)).Text = File.ReadAllText(_networkVerifyPinFile).Trim();
        ((Button)_matchSetup.FindChild("Launch", owned: false)).EmitSignal(Button.SignalName.Pressed);
        if (_session?.Network is { } session && _networkVerifyRole == "host")
            File.WriteAllText(_networkVerifyPinFile, session.Fingerprint);
    }
    private async System.Threading.Tasks.Task VerifyDirectoryJoin()
    {
        try
        {
            ((Button)_matchSetup!.FindChild("MainMultiplayer", owned: false)).EmitSignal(Button.SignalName.Pressed);
            ((Button)_matchSetup.FindChild("BrowseDirectory", owned: false)).EmitSignal(Button.SignalName.Pressed);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (_matchSetup.FindChild("DirectoryJoinMatch", owned: false) is Button { Disabled: false } join)
                {
                    if (_capturePath is not null && DisplayServer.GetName() != "headless") await CaptureNetworkFrame(_capturePath + ".directory.png");
                    ((Button)_matchSetup.FindChild("DirectoryJoinMatch", owned: false)).EmitSignal(Button.SignalName.Pressed);
                    GD.Print("OPENRTS_DIRECTORY_UI_JOIN:requested"); return;
                }
                if (clock.Elapsed.TotalSeconds > 20) throw new TimeoutException("Directory UI did not list the hosted game");
            }
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
    private void VerifyNetworkFrame(FrameExchange.FrameLease frame)
    {
        if (!_verifyNetwork) return;
        var network = _session!.Network!;
        if (!_networkVerifyIssued && frame.Tick >= 2)
        {
            if (network.Player == 0)
            {
                if (Controls(1) || Controls(2)) throw new InvalidOperationException("Spectator can issue commands.");
                _networkInputTick = frame.Tick; _networkVerifyIssued = true; GD.Print("OPENRTS_NETWORK_INPUT:0");
            }
            else
            {
            if (Controls(network.Player == 1 ? 2u : 1u) || !Controls(network.Player) || CommandOwner != network.Player)
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
        if (_networkVerifyNet3 && _networkVerifyMenuClosed)
        {
            if (network.IsHost && !_networkPauseRequested) { network.Pause(true); _networkPauseRequested = true; }
            if (network.State.Phase == OpenRTS.Network.MatchPhase.Paused)
            {
                if (frame.Tick != network.State.Tick) throw new InvalidOperationException("Paused frame/control tick differs.");
                if (!_networkVerifyPaused)
                {
                    _networkVerifyPaused = true; _networkPauseBegan = Time.GetTicksMsec(); _networkPauseTick = frame.Tick;
                    network.SendChat("verify-pause-seen");
                    GD.Print($"OPENRTS_NETWORK_PAUSE:{frame.Tick}");
                }
                if (network.IsHost && _networkPauseSeen.Count == 3 + network.State.Spectators && Time.GetTicksMsec() - _networkPauseBegan >= 500) network.Pause(false);
            }
        }
        if (_networkVerifyReconnect)
        {
            if (!network.IsHost && !_networkBreakRequested && frame.Tick >= _networkInputTick + 12)
            { _networkBreakRequested = true; GD.Print("OPENRTS_NETWORK_BREAK:requested"); }
            if (network.IsHost && network.State.Phase == OpenRTS.Network.MatchPhase.Paused && network.State.Slots[1].Connected) network.Pause(false);
            if (!network.IsHost && _networkReconnectAttempted && !_networkReconnectConfirmed && network.ResumeCredential?.Epoch > 1 &&
                network.State.Phase == OpenRTS.Network.MatchPhase.Playing && frame.Tick > network.State.Tick)
            {
                _networkReconnectConfirmed = true; network.SendChat("verify-rejoined");
                GD.Print("OPENRTS_NETWORK_REJOIN:" + network.ResumeCredential.Epoch);
            }
            if (_networkReconnectConfirmed && _networkRejoinedTick < 0) _networkRejoinedTick = frame.Tick;
        }
        if (!_networkVerifyLeaving && _networkVerifyMenuClosed && (!_networkVerifyReconnect || _networkRejoinedTick >= 0 && frame.Tick >= _networkRejoinedTick + 8) && (!_networkVerifyNet3 || _networkVerifyPaused &&
            network.State.Phase == OpenRTS.Network.MatchPhase.Playing && frame.Tick >= _networkPauseTick + 4) &&
            frame.Tick >= _networkInputTick + (_networkVerifyNet3 ? 30 : 16))
        {
            _networkVerifyLeaving = true;
            if (_networkVerifyRole == "host") network.Stop();
        }
        if (network.Completion.IsCompletedSuccessfully)
        {
            using var log = File.OpenRead(network.JournalPath);
            var replay = OpenRTS.Network.StarJournal.Replay(log, network.Descriptor, () => CreateNetworkWorld(_mapMatch!));
            if (replay != network.Completion.Result) throw new InvalidOperationException("Godot match replay differs.");
            GD.Print("OPENRTS_NETWORK_JOURNAL:" + network.JournalPath);
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
