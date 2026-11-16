using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Network;
using OpenRTS.AI;
using OpenRTS.Core.Orders;

public partial class Sandbox
{
    private int _networkMode;
    private string _networkAddress = "127.0.0.1", _networkPin = "";
    private int _networkPort = 17771, _networkDelay = 6;
    private uint _networkPlayer = 2;
    private uint[] _networkAiPlayers = [];
    private Label? _networkLabel;
    private Label? _networkChatLabel;
    private readonly System.Collections.Generic.Queue<string> _networkMessages = new();
    private void UpdateNetworkChat(StarSession network)
    {
        while (network.TryReadChat(out var message))
        {
            if (_verifyNetwork && _networkVerifyNet3 && message.Text == "verify-pause-seen") { _networkPauseSeen.Add(message.Player); continue; }
            _networkMessages.Enqueue($"{(message.Player == 0 ? "Зритель" : message.Player.ToString())}: {message.Text}");
            while (_networkMessages.Count > 4) _networkMessages.Dequeue();
        }
        if (_networkChatLabel is not null) _networkChatLabel.Text = string.Join("\n", _networkMessages);
    }
    private LanGames? _networkAdvertiser;
    private void StartNetworkSession(MapMatch map, UnitCatalog catalog)
    {
        var directory = ProjectSettings.GlobalizePath("user://network"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.journal");
        var ownerOptions = _configuredOptions!; var ownerMap = map;
        var policies = new System.Collections.Generic.Dictionary<uint, (SkirmishAi Ai, long Tick, WorldCommand[] Commands)>();
        MatchDescriptor Descriptor(MatchSetupOptions options, MapMatch match) => new("openrts-lobby-v2", catalog.Summary.Fingerprint,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.Serialize() + "|" + _manifest.Settings.Simulation.SpeedPerTick + "|" + _fogHoldTicks))),
            match.Seed, TickRate: options.TickRate, InputDelay: options.Delay, EndTick: 0);
        (MatchDescriptor, SimulationWorld) Prepare(string data)
        {
            var options = MatchSetupOptions.Parse(data); var preparedMap = PrepareMap(options);
            var world = CreateConfiguredWorld(preparedMap, options);
            ownerOptions = options; ownerMap = preparedMap; policies.Clear();
            return (Descriptor(options, preparedMap), world);
        }
        System.Collections.Generic.IReadOnlyList<WorldCommand> Plan(SimulationWorld world, uint player)
        {
            policies.TryGetValue(player, out var previous);
            if (previous.Ai is not null && previous.Tick == world.Tick) return previous.Commands;
            var ai = previous.Ai is not null && previous.Tick + 1 == world.Tick ? previous.Ai :
                SkirmishAiScenario.Create(catalog, _opponentProfile!, ownerMap.Map, player,
                    ownerOptions.Seats.Take(ownerMap.Map.Slots.Count).Select(s => ownerMap.Map.Slots[s.Start]).ToArray(), initialTick: world.Tick, opponents: ownerOptions.Opponents(player));
            var commands = ai.Plan(world); policies[player] = (ai, world.Tick, commands); return commands;
        }
        string gameName = _matchSetup!.NetworkGameName;
        var network = new StarSession(_networkMode == 1, _networkAddress, _networkPort, _networkPin, Descriptor(ownerOptions, map),
            () => CreateConfiguredWorld(map, ownerOptions), path, playerCount: 4, player: _networkPlayer,
            aiPlayers: _networkAiPlayers, planAi: Plan, lobbySettings: ownerOptions.Serialize(), prepareSettings: Prepare,
            closedPlayers: ownerOptions.Seats.Select((s, i) => (s, i)).Where(p => p.s.Controller == SlotController.None).Select(p => (uint)p.i + 1).ToArray());
        _session = new SandboxSession(network);
        _networkAdvertiser?.Dispose();
        if (network.IsHost)
        {
            try { _networkAdvertiser = new LanGames(() => network.State.Phase != MatchPhase.Lobby ? null :
                new LanGame("", _networkPort, gameName, MatchSetupOptions.Parse(network.LobbySettings).Map,
                    network.State.Slots.Count(s => s.Connected), network.State.Slots.Count(s => s.Controller == SlotController.Human && !s.Connected), network.State.Spectators, network.Fingerprint, network.State.Slots.Where(s => s.Controller == SlotController.Human && !s.Connected).Select(s => s.Player).ToArray())); }
            catch (System.Net.Sockets.SocketException error) { GD.Print("LAN discovery: " + error.Message); }
        }
    }
    private SimulationWorld CreateNetworkWorld(MapMatch map) => CreateConfiguredWorld(map, _configuredOptions!);
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
