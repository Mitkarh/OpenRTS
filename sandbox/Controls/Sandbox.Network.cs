using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Network;

public partial class Sandbox
{
    private int _networkMode;
    private string _networkAddress = "127.0.0.1", _networkPin = "";
    private int _networkPort = 17771, _networkDelay = 6;
    private Label? _networkLabel;
    private void StartNetworkSession(MapMatch map, UnitCatalog catalog)
    {
        var descriptor = new MatchDescriptor("openrts-net2-v2", catalog.Summary.Fingerprint,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{map.Map.Id}|{map.Seed}|{_manifest.Settings.Simulation.SpeedPerTick}|{_matchFogMode}|{_fogHoldTicks}"))), map.Seed, TickRate: _manifest.Settings.Simulation.TickRate, InputDelay: _networkDelay, EndTick: 0);
        // The handshake also compares the complete initialized world hash, including map/seed/settings.
        var directory = ProjectSettings.GlobalizePath("user://network"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.journal");
        var network = new DirectSession(_networkMode == 1, _networkAddress, _networkPort, _networkPin, descriptor,
            () => CreateNetworkWorld(map), path);
        _session = new SandboxSession(network);
    }
    private SimulationWorld CreateNetworkWorld(MapMatch map) => map.CreateWorld(new GameplayMechanics
        { FogCellSize = _matchFogMode == OpenRTS.Core.Visibility.FogMode.Visible ? null : 1024,
            FogMode = _matchFogMode, FogVisibilityHoldTicks = _fogHoldTicks },
        speedPerTick: _manifest.Settings.Simulation.SpeedPerTick, workers: _workers,
        restrictPlayerCommands: true, tickRate: _manifest.Settings.Simulation.TickRate);
    private void ShowNetworkFailure(string reason)
    {
        var layer = new CanvasLayer { Layer = 40 }; AddChild(layer);
        var backdrop = new ColorRect { Color = new(0, 0, 0, 0.85f), Theme = TerminalTheme() };
        layer.AddChild(backdrop); backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var center = new CenterContainer(); backdrop.AddChild(center);
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var panel = new VBoxContainer { CustomMinimumSize = new(460, 0) }; center.AddChild(panel);
        panel.AddChild(new Label { Text = "Соединение прервано", HorizontalAlignment = HorizontalAlignment.Center });
        panel.AddChild(new Label { Text = reason, AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var retry = new Button { Text = "К настройке матча" };
        retry.Pressed += () => GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene); panel.AddChild(retry);
        var quit = new Button { Text = "Выйти" }; quit.Pressed += () => GetTree().Quit(); panel.AddChild(quit);
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }
    private void AddNetworkStatus()
    {
        if (_networkMode == 0 || _networkLabel is not null) return;
        var layer = new CanvasLayer { Layer = 20 }; AddChild(layer);
        _networkLabel = new Label { Position = new(18, 8), Text = "Прямой матч · TLS", MouseFilter = Control.MouseFilterEnum.Ignore };
        layer.AddChild(_networkLabel);
    }
}
