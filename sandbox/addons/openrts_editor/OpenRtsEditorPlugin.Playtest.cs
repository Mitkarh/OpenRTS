#if TOOLS
using Godot;
using OpenRTS.Content;

namespace OpenRTS.Godot.Authoring;

public partial class OpenRtsEditorPlugin
{
    private ConfirmationDialog _playtestDialog = null!;
    private ItemList _playtestStarts = null!;
    private OptionButton _playtestSets = null!;
    private bool _verifyPlaytest;
    private bool _recordPlaytest;
    private Button _stopPlaytest = null!, _restartPlaytest = null!;
    private Label _playtestStatus = null!;
    private TextEdit _playtestLog = null!;
    private CancellationTokenSource? _playtestBuild;
    private PlaytestRequest? _lastPlaytest;
    internal PlaytestProcess? Playtest { get; private set; }

    private sealed record PlaytestRequest(ulong Scene, string[]? Starts, string? StartSet, bool Record);

    private void SetupPlaytest()
    {
        _stopPlaytest = Button("Stop playtest", StopPlaytest);
        _restartPlaytest = Button("Restart playtest", () => _ = RestartPlaytest());
        _playtestStatus = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _dock.AddChild(_playtestStatus);
        Button("Playtest output", () => { _playtestLog.Visible = !_playtestLog.Visible; UpdatePlaytestControls(); });
        _playtestLog = new() { Editable = false, Visible = false, CustomMinimumSize = new(0, 160), WrapMode = TextEdit.LineWrappingMode.Boundary };
        _dock.AddChild(_playtestLog);
        UpdatePlaytestControls();
        _playtestDialog = new() { Title = "Local skirmish playtest", OkButtonText = "Validate and play" };
        _editorDock.AddChild(_playtestDialog);
        var layout = new VBoxContainer { CustomMinimumSize = new(360, 0) };
        _playtestDialog.AddChild(layout);
        layout.AddChild(new Label { Text = $"Select 1–{MapScenario.MaximumPlayers} starts in player order:" });
        _playtestStarts = new() { SelectMode = ItemList.SelectModeEnum.Multi, CustomMinimumSize = new(0, 140) };
        layout.AddChild(_playtestStarts);
        layout.AddChild(new Label { Text = "Starting set" });
        _playtestSets = new();
        layout.AddChild(_playtestSets);
        _playtestSets.ItemSelected += _ => SyncPlaytestPreview();
        _playtestStarts.MultiSelected += (_, _) => SyncPlaytestPreview();
        _playtestDialog.Confirmed += () =>
        {
            string[] slots = _playtestStarts.GetSelectedItems().Order().Select(index => _playtestStarts.GetItemText(index)).ToArray();
            string path = _playtestSets.GetItemMetadata(_playtestSets.Selected).AsString();
            StartPlaytest(_verifyPlaytest, _recordPlaytest, slots, path);
        };
    }

    internal void StopPlaytest()
    {
        if (_playtestBuild is not null && _build == _playtestBuild) Invalidate("Playtest launch cancelled.");
        Playtest?.Stop();
    }

    internal Task<MapBuild?> RestartPlaytest() =>
        _lastPlaytest is { } request && EditorInterface.Singleton.GetEditedSceneRoot()?.GetInstanceId() == request.Scene
            ? BuildAsync(play: true, selectedStarts: request.Starts, selectedStartSet: request.StartSet, record: request.Record)
            : Task.FromResult<MapBuild?>(null);

    private void LaunchPlaytest(string map, string mod, LocalMapPlaytest receipt, PlaytestRequest request)
    {
        string[] mode = DisplayServer.GetName() == "headless" ? ["--headless"] : [];
        string[] args = [.. mode, .. PlayMapArguments(map, mod, receipt), "--managed-playtest"];
        Playtest = new(OS.GetExecutablePath(), request.Record ? [.. args, "--record-playtest"] : args);
        _lastPlaytest = request;
    }

    private void UpdatePlaytestControls()
    {
        bool compiling = _playtestBuild is not null && _build == _playtestBuild && !_playtestBuild.IsCancellationRequested;
        _restartPlaytest.Disabled = compiling || _lastPlaytest is not { } request ||
            EditorInterface.Singleton.GetEditedSceneRoot()?.GetInstanceId() != request.Scene;
        var process = Playtest;
        _stopPlaytest.Disabled = !compiling;
        if (process is null)
        {
            _playtestStatus.Text = compiling ? "Building playtest…" : "No playtest started.";
            return;
        }
        var (ready, stopping, forced, exit) = process.State;
        _stopPlaytest.Disabled &= stopping || exit.HasValue;
        string state = exit is { } code
            ? forced ? $"Terminated (exit {code})" :
                code != 0 ? $"Failed (exit {code})" :
                stopping ? "Stopped (exit 0)" : ready ? "Exited (exit 0)" : "Exited before ready (exit 0)"
            : stopping ? "Stopping…" : ready ? "Running" : "Starting…";
        _playtestStatus.Text = $"{state} · PID {process.Id}" + (compiling ? "\nBuilding playtest…" : "");
        if (_playtestLog.Visible && process.Diagnostics is var output && _playtestLog.Text != output)
            _playtestLog.Text = output;
    }

    private void StartPlaytest(bool verify, bool record, string[]? slots = null, string? set = null) =>
        _ = BuildAsync(play: !verify, verify: verify, bundle: verify && EditedMap() is OpenRtsMap,
            selectedStarts: slots, selectedStartSet: set, record: record);

    private void ChoosePlaytest(bool verify, bool record = false)
    {
        CommitStroke();
        var root = EditedMap();
        var source = Extract(root);
        if (!source.IsSkirmish)
        {
            StartPlaytest(verify, record);
            return;
        }
        _verifyPlaytest = verify;
        _recordPlaytest = record;
        _playtestDialog.OkButtonText = verify ? "Build and verify" : record ? "Validate and record" : "Validate and play";
        var match = GameManifest.Load(ProjectFile(ContentPath(root), ".yaml")).Settings.Match;
        _playtestStarts.Clear();
        foreach (var slot in source.Slots.OrderBy(slot => slot.Id, StringComparer.Ordinal))
        {
            int index = _playtestStarts.AddItem(slot.Id);
            if (index < MapScenario.MaximumPlayers) _playtestStarts.Select(index, single: false);
        }
        _playtestSets.Clear();
        var choices = match.StartingSets.Length == 0
            ? [new MatchChoice { Path = match.DefaultStartSet, Title = match.DefaultStartSet }]
            : match.StartingSets;
        foreach (var choice in choices)
        {
            _playtestSets.AddItem(choice.Title);
            int index = _playtestSets.ItemCount - 1;
            _playtestSets.SetItemMetadata(index, choice.Path);
            if (choice.Path == match.DefaultStartSet) _playtestSets.Select(index);
        }
        if (root is OpenRtsMap2D map && (_map2D != map || _manifest2D is null)) RefreshPreview2D(map);
        SyncPlaytestPreview();
        _playtestDialog.PopupCentered();
    }
}
#endif
