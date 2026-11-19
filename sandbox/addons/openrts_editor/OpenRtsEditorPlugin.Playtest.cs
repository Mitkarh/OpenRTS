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

    private void SetupPlaytest()
    {
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
