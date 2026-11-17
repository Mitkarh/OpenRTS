using System;
using System.IO;
using Godot;
using OpenRTS.Core;
using OpenRTS.Network;

public partial class Sandbox
{
    private Label? _replayLabel;
    private HSlider? _replayTimeline;
    private Button? _replayPause;
    private long _replayGeneration;
    private bool _replayDragging;
    private void BeginReplay(string path)
    {
        if (_session is not null) return;
        try
        {
            using var input = File.OpenRead(path); var info = StarJournal.ReadInfo(input);
            if (info.Descriptor.Rules is not ("openrts-lobby-v2" or "openrts-lobby-v3") || info.Descriptor.ContentHash != _catalog!.Summary.Fingerprint)
                throw new InvalidDataException("Правила или версия контента повтора не совпадают с установленными.");
            _configuredOptions = MatchSetupOptions.Parse(info.Recipe); _mapMatch = PrepareMap(_configuredOptions);
            _networkMode = 0; _fogViewer = 0; _constructionViewer = 0; _opponentProfile = null; _opponent = null;
            _matchFogMode = (OpenRTS.Core.Visibility.FogMode)_configuredOptions.Fog;
            _matchPlayers = _mapMatch.Map.Slots.Count; _count = _mapMatch.UnitCount; _paused = true; _playbackSpeed = 1;
            var map = _mapMatch; var options = _configuredOptions;
            _session = new SandboxSession(new ReplaySession(path, _ => CreateConfiguredWorld(map, options)));
            _matchSetup!.SetLaunching(true, "Проверка и загрузка повтора…");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException)
        { _matchSetup!.SetLaunching(false, "Не удалось открыть повтор: " + error.Message); }
    }
    private void AddReplayControls()
    {
        if (_session?.Replay is not { } replay) return;
        var layer = new CanvasLayer { Layer = 21 }; AddChild(layer);
        var panel = new PanelContainer { Position = new(12, 10), Theme = TerminalTheme() }; layer.AddChild(panel);
        _rtsPanels.Add(panel);
        panel.AddThemeStyleboxOverride("panel", TerminalBox(new("#101712ee"), new("#697657"), 8));
        var box = new VBoxContainer(); panel.AddChild(box);
        _replayLabel = new Label(); box.AddChild(_replayLabel);
        var controls = new HBoxContainer(); box.AddChild(controls);
        _replayPause = new Button { Name = "ReplayPause", Text = "Воспроизвести" };
        _replayPause.Pressed += () => replay.SetPlayback(replay.PlaybackSpeed, !replay.IsPaused); controls.AddChild(_replayPause);
        var step = new Button { Name = "ReplayStep", Text = "+1 тик" }; step.Pressed += replay.StepOnce; controls.AddChild(step);
        var speed = new OptionButton { Name = "ReplaySpeed" }; foreach (string value in new[] { "0.25×", "0.5×", "1×", "2×", "4×", "8×" }) speed.AddItem(value); speed.Select(2);
        speed.ItemSelected += value => replay.SetPlayback(new double[] { .25, .5, 1, 2, 4, 8 }[value], replay.IsPaused); controls.AddChild(speed);
        var viewer = new OptionButton { Name = "ReplayViewer" }; viewer.AddItem("Весь мир", 0);
        foreach (var seat in replay.Info!.InitialState.Slots.Where(s => s.Controller != SlotController.None)) viewer.AddItem("Игрок " + seat.Player, (int)seat.Player);
        viewer.ItemSelected += value => { _fogViewer = (uint)viewer.GetItemId((int)value); _constructionViewer = _fogViewer; ResetReplayPresentation(); _focusStartingBase = _fogViewer != 0; }; controls.AddChild(viewer);
        _replayTimeline = new HSlider { Name = "ReplayTimeline", MinValue = 0, MaxValue = replay.Info.EndTick, Step = 1, CustomMinimumSize = new(390, 20) };
        _replayTimeline.DragStarted += () => _replayDragging = true;
        _replayTimeline.DragEnded += changed => { _replayDragging = false; if (changed) replay.Seek((long)_replayTimeline.Value); }; box.AddChild(_replayTimeline);
        var seek = new Button { Name = "ReplaySeek", Text = "Перейти к выбранному тику" }; seek.Pressed += () => replay.Seek((long)_replayTimeline.Value); box.AddChild(seek);
        _replayGeneration = replay.Generation;
    }
    private void ResetReplayPresentation()
    {
        _selection.Reset(); _panelDirty = true; _panelTick = -1; _visibleProjectsTick = -1;
        _combatEffects?.Clear(); _sprites?.ClearWrecks(); _rtsOverlay?.RadarContacts.Clear(); _fogView?.ResetTimeline();
        _productionPlacement = null; _sitePlacement = null; _armedAction = null; _armedCustom = null;
    }
    private void UpdateReplayControls()
    {
        if (_session?.Replay is not { } replay) return;
        _paused = replay.IsPaused; _playbackSpeed = replay.PlaybackSpeed;
        if (_replayGeneration != replay.Generation) { _replayGeneration = replay.Generation; ResetReplayPresentation(); }
        if (_replayLabel is not null) _replayLabel.Text = $"Повтор · {replay.Position} / {replay.Info!.EndTick} · {replay.Status}";
        if (_replayPause is not null) _replayPause.Text = replay.IsPaused ? "Воспроизвести" : "Пауза";
        if (_replayTimeline is not null && !_replayDragging && !_replayTimeline.HasFocus()) _replayTimeline.SetValueNoSignal(replay.Position);
    }
    private bool HandleReplayKey(Key key)
    {
        if (_session?.Replay is not { } replay) return false;
        if (key == Key.P) replay.SetPlayback(replay.PlaybackSpeed, !replay.IsPaused);
        else if (key == Key.N) replay.StepOnce();
        else if (key is Key.Equal or Key.Plus or Key.KpAdd or Key.Minus or Key.KpSubtract)
            replay.SetPlayback(Math.Clamp(replay.PlaybackSpeed * (key is Key.Minus or Key.KpSubtract ? .5 : 2), .125, 8), replay.IsPaused);
        else if (key is Key.Escape or Key.F10 or Key.F11 or Key.F12 or Key.F8 or Key.F9 or Key.F7) return false;
        return true;
    }
}
