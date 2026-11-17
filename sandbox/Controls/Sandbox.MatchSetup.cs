using System;
using DeclarativeContent;
using Godot;
using OpenRTS.AI;
using OpenRTS.Content;
using OpenRTS.Core;

public partial class Sandbox
{
    private System.Collections.Generic.Dictionary<string, GameMap> _setupMaps = new();
    private System.Collections.Generic.Dictionary<string, StartingSet> _setupSets = new();
    private MatchSetupOptions? _configuredOptions;
    private string? _pendingLobbySettings;
    private MatchSetupScreen? _matchSetup;
    private bool _startedFromSetup, _verifyMatchSetup;
    private bool _verifyAiMatch;
    private string? _aiProfileId;
    private SkirmishAiProfile? _opponentProfile;
    private SkirmishAi? _opponent;
    private string[]? _opponentStarts;

    private void OpenMatchSetup(UnitCatalog catalog)
    {
        GameMap Map(string path) => SceneMapBundle.IsPath(path) ? LoadSceneMap(path, catalog) : GameMap.Load(ReadMapSource(path), catalog);
        StartingSet Set(string path) => StartingSet.Load(ReadMapSource(path));
        var settings = _manifest.Settings.Match;
        var mapChoices = settings.Maps.Length == 0 ? [new MatchChoice { Path = settings.DefaultMap, Title = settings.DefaultMap }] : settings.Maps;
        var setChoices = settings.StartingSets.Length == 0 ? [new MatchChoice { Path = settings.DefaultStartSet, Title = settings.DefaultStartSet }] : settings.StartingSets;
        var availableMaps = new List<(MatchChoice Choice, GameMap Map)>();
        foreach (var choice in mapChoices)
        {
            try { availableMaps.Add((choice, Map(choice.Path))); }
            catch (Exception error) when (SceneMapBundle.IsPath(choice.Path) && error is IOException or InvalidDataException or ContentException or System.Text.Json.JsonException)
            {
                GD.PushWarning($"Карта {choice.Title} недоступна. {error.Message}");
            }
        }
        if (availableMaps.Count == 0) throw new InvalidDataException("Нет доступных карт матча.");
        var maps = availableMaps.Select(item => (item.Choice.Title, item.Choice.Description, item.Map)).ToArray();
        if (maps.Any(choice => !choice.Item3.IsSkirmish)) throw new ArgumentException("Экран настройки принимает только skirmish-карты.");
        var modes = setChoices.Select(choice => (choice.Title, Set(choice.Path))).ToArray();
        _setupMaps = maps.ToDictionary(m => m.Item3.Id, m => m.Item3);
        _setupSets = modes.ToDictionary(m => m.Item2.Id, m => m.Item2);
        _matchSetup = new();
        AddChild(_matchSetup);
        _matchSetup.Build(TerminalTheme(), catalog.Locomotion!, maps, modes,
            availableMaps.FindIndex(item => item.Choice.Path == settings.DefaultMap),
            Array.FindIndex(setChoices, choice => choice.Path == settings.DefaultStartSet), _manifest.Settings.Simulation.SpeedPerTick, _fogHoldTicks, catalog.Summary.Fingerprint);
        _matchSetup.Launch += BeginMatchFromSetup;
        _matchSetup.ReplayRequested += BeginReplay;
        _matchSetup.ConfigurationChanged += () => _pendingLobbySettings = _matchSetup.Options.Serialize();
        _matchSetup.CancelNetwork += () => { if (_session?.Network is not null || _session?.Replay is not null) MatchSetupFailed(new OperationCanceledException("Подключение отменено."), true); };
        if (_verifyReplayPath is not null) { _ = VerifyReplayFlow(); }
        else if (_verifyMenu) { _ = VerifyMenuFlow(); }
        else if (_verifyNetwork) SelectNetworkVerificationMatch();
        else if (_verifyAiMatch) SelectAiVerificationMatch();
        else if (_verifySceneMapMenu) SelectSceneMapVerification();
        else if (_verifyMatchSetup) SelectVerificationMatch();
    }

    private void BeginMatchFromSetup(GameMap map, StartingSet set, string[] slots, bool aiOpponent)
    {
        if (_session is not null) return;
        try
        {
            _networkMode = _matchSetup!.NetworkMode;
            if (_networkMode != 0 && slots.Length < 2)
                throw new ArgumentException("Прямая игра требует хотя бы двух игровых сторон.");
            _networkAddress = _matchSetup.NetworkAddress; _networkPort = _matchSetup.NetworkPort;
            _networkPin = _matchSetup.NetworkPin; _networkDelay = _matchSetup.NetworkInputDelay;
            _networkPlayer = _matchSetup.NetworkPlayer; _networkAiPlayers = _matchSetup.NetworkAiPlayers;
            _fogViewer = _networkMode == 2 ? _networkPlayer : 1u;
            _matchFogMode = _matchSetup!.SelectedFog;
            _fogStyle = _matchSetup.SelectedFogStyle;
            _radarStyle = _matchSetup.SelectedRadarStyle;
            _matchPlayers = slots.Length;
            _configuredOptions = _matchSetup.Options;
            _mapMatch = PrepareMap(_configuredOptions);
            bool canPlanAi = aiOpponent || _networkMode != 0;
            _opponentProfile = canPlanAi ? SkirmishAiCatalog.Load(ReadMapSource(_manifest.Settings.Match.AiConfig), _catalog!, _aiProfileId ?? _manifest.Settings.Match.AiProfile) : null;
            _opponentStarts = canPlanAi ? slots : null;
            _count = _mapMatch.UnitCount;
            _matchSetup!.SetLaunching(true, "Подготовка поля боя…");
            StartSession();
        }
        catch (Exception error) when (error is ContentException or ArgumentException or System.IO.IOException or System.Security.Cryptography.CryptographicException or OpenRTS.Network.MatchDirectoryException)
        {
            MatchSetupFailed(error, canRetry: true);
        }
    }

    private void UpdateMatchSetup()
    {
        if (_session is null) return;
        if (_session.Completion.IsCompleted)
        {
            try { _session.Completion.GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                MatchSetupFailed(error, canRetry: true);
                return;
            }
        }

        if (_session.Network is { } network)
        {
            if (network.IsHost && _pendingLobbySettings is { } pending)
            {
                try { network.ConfigureLobby(pending, MatchSetupOptions.Parse(pending).Seats.Select(s => s.Controller).ToArray()); _pendingLobbySettings = null; }
                catch (InvalidOperationException) { }
            }
            if (_directoryPublication is { } publication) _matchSetup!.ShowDirectoryPublication(publication.Status, publication.Invite);
            _matchSetup!.SetNetworkStatus(NetworkStatusText(network), network.Fingerprint);
            _matchSetup.UpdateNetworkLobby(network);
            if (_verifyNetwork && network.State.Match != Guid.Empty && network.State.Phase == OpenRTS.Network.MatchPhase.Lobby)
            {
                VerifyDirectoryCapacities(network);
                if (network.Player != 0 && _networkReadyRevision != network.State.Revision) { network.Ready(network.State.Revision); _networkReadyRevision = network.State.Revision; }
                if (network.IsHost && network.State.Spectators == _networkVerifySpectators && network.State.Slots.All(p => p.Controller != OpenRTS.Network.SlotController.Human || p.Connected && p.Ready)) network.Start();
            }
        }
        using var frame = _session.Frames.AcquireLatest();
        if (frame is null) return;
        try
        {
            if (_session.Network is { } joined)
            {
                _configuredOptions = MatchSetupOptions.Parse(joined.LobbySettings);
                _mapMatch = PrepareMap(_configuredOptions); _matchFogMode = (OpenRTS.Core.Visibility.FogMode)_configuredOptions.Fog;
                _fogViewer = joined.Player; _networkPlayer = joined.Player;
                _opponentStarts = _configuredOptions.Seats.Take(_mapMatch.Map.Slots.Count).Select(s => _mapMatch.Map.Slots[s.Start]).ToArray();
            }
            _startedFromSetup = true;
            BuildBattleScene(_catalog!);
            AddNetworkStatus(); AddReplayControls();
            _focusStartingBase = true;
            _matchSetup!.Hide();
            _matchSetup.QueueFree();
            _matchSetup = null;
            _session.SetPlayback(_playbackSpeed, _paused || _verifyMatchSetup);
        }
        catch (Exception error)
        {
            MatchSetupFailed(error, canRetry: false);
        }
    }

    private MapMatch PrepareMap(MatchSetupOptions options)
    {
        if (!_setupMaps.TryGetValue(options.Map, out var map) || !_setupSets.TryGetValue(options.Mode, out var set)) throw new ArgumentException("Карта или стартовый набор не установлены.");
        if (_networkMode != 0 && _sceneMaps.ContainsKey(map.Id))
            throw new ArgumentException("Scene-map bundles are available for local matches.");
        int capacity = map.Slots.Count;
        if (options.Seats.Where((seat, i) => seat.Controller != OpenRTS.Network.SlotController.None && (i >= capacity || seat.Start >= capacity)).Any() ||
            options.Seats.Any(s => s.Controller == OpenRTS.Network.SlotController.Ai) && set.Id != "supply") throw new ArgumentException("Недоступные места или ИИ для этой карты/режима.");
        var active = options.Seats.Take(capacity).Select(s => s.Controller != OpenRTS.Network.SlotController.None).ToArray();
        return MapScenario.Prepare(map, set, options.Seats.Take(capacity).Select(s => map.Slots[s.Start]).ToArray(), active);
    }
    private SimulationWorld CreateConfiguredWorld(MapMatch map, MatchSetupOptions options)
    {
        var fog = (OpenRTS.Core.Visibility.FogMode)options.Fog;
        var world = map.CreateWorld(new GameplayMechanics { FogCellSize = fog == OpenRTS.Core.Visibility.FogMode.Visible ? null : 1024,
            FogMode = fog, FogVisibilityHoldTicks = options.FogHoldTicks }, speedPerTick: options.SpeedPerTick,
            workers: _workers, restrictPlayerCommands: _networkMode != 0 || options.Seats.Any(s => s.Controller == OpenRTS.Network.SlotController.Ai), tickRate: options.TickRate);
        try { world.SetInitialTeams(options.Seats.Select((s, i) => (s, i)).Where(p => p.s.Controller != OpenRTS.Network.SlotController.None).ToDictionary(p => (uint)p.i + 1, p => p.s.Team)); return world; }
        catch { world.Dispose(); throw; }
    }

    private void MatchSetupFailed(Exception error, bool canRetry)
    {
        if (_session?.Network?.ResumeCredential is null) _matchSetup?.CancelDirectoryJoin();
        _session?.Dispose();
        _directoryPublication?.Dispose(); _directoryPublication = null;
        _networkAdvertiser?.Dispose(); _networkAdvertiser = null;
        _session = null;
        _pendingLobbySettings = null;
        _networkMode = 0;
        _mapMatch = null;
        _opponentProfile = null;
        _opponent = null;
        _opponentStarts = null;
        if (_verifyMatchSetup || !canRetry) GD.PushError(error.ToString());
        if (_verifyMatchSetup) GetTree().Quit(1);
        else _matchSetup?.SetLaunching(!canRetry, "Матч не запущен. " + error.Message);
    }
}
