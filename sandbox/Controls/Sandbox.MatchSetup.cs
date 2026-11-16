using System;
using DeclarativeContent;
using Godot;
using OpenRTS.AI;
using OpenRTS.Content;
using OpenRTS.Core;

public partial class Sandbox
{
    private MatchSetupScreen? _matchSetup;
    private bool _startedFromSetup, _verifyMatchSetup;
    private bool _verifyAiMatch;
    private string? _aiProfileId;
    private SkirmishAiProfile? _opponentProfile;
    private SkirmishAi? _opponent;
    private string[]? _opponentStarts;

    private void OpenMatchSetup(UnitCatalog catalog)
    {
        GameMap Map(string path) => GameMap.Load(ReadMapSource(path), catalog);
        StartingSet Set(string path) => StartingSet.Load(ReadMapSource(path));
        var settings = _manifest.Settings.Match;
        var mapChoices = settings.Maps.Length == 0 ? [new MatchChoice { Path = settings.DefaultMap, Title = settings.DefaultMap }] : settings.Maps;
        var setChoices = settings.StartingSets.Length == 0 ? [new MatchChoice { Path = settings.DefaultStartSet, Title = settings.DefaultStartSet }] : settings.StartingSets;
        var maps = mapChoices.Select(choice => (choice.Title, choice.Description, Map(choice.Path))).ToArray();
        if (maps.Any(choice => !choice.Item3.IsSkirmish)) throw new ArgumentException("Экран настройки принимает только skirmish-карты.");
        var modes = setChoices.Select(choice => (choice.Title, Set(choice.Path))).ToArray();
        _matchSetup = new();
        AddChild(_matchSetup);
        _matchSetup.Build(TerminalTheme(), catalog.Locomotion!, maps, modes,
            Array.FindIndex(mapChoices, choice => choice.Path == settings.DefaultMap),
            Array.FindIndex(setChoices, choice => choice.Path == settings.DefaultStartSet));
        _matchSetup.Launch += BeginMatchFromSetup;
        _matchSetup.CancelNetwork += () => { if (_session?.Network is not null) MatchSetupFailed(new OperationCanceledException("Подключение отменено."), true); };
        if (_verifyNetwork) SelectNetworkVerificationMatch();
        else if (_verifyAiMatch) SelectAiVerificationMatch();
        else if (_verifyMatchSetup) SelectVerificationMatch();
    }

    private void BeginMatchFromSetup(GameMap map, StartingSet set, string[] slots, bool aiOpponent)
    {
        if (_session is not null) return;
        try
        {
            _networkMode = _matchSetup!.NetworkMode;
            if (_networkMode != 0 && (slots.Length != 2 || aiOpponent))
                throw new ArgumentException("Прямая игра требует двух игроков без ИИ.");
            _networkAddress = _matchSetup.NetworkAddress; _networkPort = _matchSetup.NetworkPort;
            _networkPin = _matchSetup.NetworkPin;
            _fogViewer = _networkMode == 2 ? 2u : 1u;
            _matchFogMode = _matchSetup!.SelectedFog;
            _fogStyle = _matchSetup.SelectedFogStyle;
            _radarStyle = _matchSetup.SelectedRadarStyle;
            _matchPlayers = slots.Length;
            _mapMatch = MapScenario.Prepare(map, set, slots);
            _opponentProfile = aiOpponent ? SkirmishAiCatalog.Load(ReadMapSource(_manifest.Settings.Match.AiConfig), _catalog!, _aiProfileId ?? _manifest.Settings.Match.AiProfile) : null;
            _opponentStarts = aiOpponent ? slots : null;
            _count = _mapMatch.UnitCount;
            _matchSetup!.SetLaunching(true, "Подготовка поля боя…");
            StartSession();
        }
        catch (Exception error) when (error is ContentException or ArgumentException or System.IO.IOException or System.Security.Cryptography.CryptographicException)
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
            _matchSetup!.SetNetworkStatus(network.Status, network.Fingerprint);
        using var frame = _session.Frames.AcquireLatest();
        if (frame is null) return;
        try
        {
            _startedFromSetup = true;
            BuildBattleScene(_catalog!);
            AddNetworkStatus();
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

    private void MatchSetupFailed(Exception error, bool canRetry)
    {
        _session?.Dispose();
        _session = null;
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
