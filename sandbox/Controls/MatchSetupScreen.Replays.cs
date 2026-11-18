using System;
using System.IO;
using Godot;
using OpenRTS.Network;

public sealed partial class MatchSetupScreen
{
    public event Action<string>? ReplayRequested;
    private VBoxContainer _replayRows = null!;
    private readonly Label _replayMessage = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart };
    internal void OpenReplayPage() => ShowPage("replays");
    private void BuildReplays()
    {
        var box = Page("replays", "Повторы матчей");
        box.AddChild(new Label { Text = "Записанные матчи и локальные тесты · просмотр без управления игроками", Modulate = new("#aab89b") });
        var scroll = new ScrollContainer { CustomMinimumSize = new(0, 300) }; box.AddChild(scroll);
        _replayRows = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; scroll.AddChild(_replayRows);
        box.AddChild(_replayMessage); box.AddChild(Button("Обновить", RefreshReplays));
    }
    private void RefreshReplays()
    {
        foreach (var child in _replayRows.GetChildren()) { _replayRows.RemoveChild(child); child.QueueFree(); }
        string directory = ProjectSettings.GlobalizePath("user://network"); Directory.CreateDirectory(directory);
        var files = Directory.EnumerateFiles(directory, "*.journal").Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc).Take(100).ToArray();
        _replayMessage.Text = files.Length == 0 ? "Повторов пока нет. Завершённые записи появятся здесь." : "Исходные карта и контент должны быть установлены. Каждый игровой тик проверяется по записанному хешу.";
        foreach (var file in files)
        {
            string title = file.Name, detail = "", error = "";
            try
            {
                if (file.Length > 64 * 1024 * 1024) throw new InvalidDataException("Превышен лимит размера.");
                using var input = file.OpenRead(); var info = StarJournal.ReadInfo(input);
                if (info.Recipe.Length == 0) throw new InvalidDataException("Нет параметров исходного сценария.");
                var options = MatchSetupOptions.Parse(info.Recipe);
                title = _mapChoices.FirstOrDefault(m => m.Map.Id == options.Map).Title ?? options.Map;
                detail = $"{file.LastWriteTime:dd.MM.yyyy HH:mm} · {info.Descriptor.TickRate} Гц · {info.InitialState.Slots.Count(s => s.Controller != SlotController.None)} стороны";
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or System.Text.Json.JsonException) { error = e.Message; }
            var row = new HBoxContainer(); row.SetMeta("ReplayPath", file.FullName); _replayRows.AddChild(row);
            row.AddChild(new Label { Text = title + "\n" + (error == "" ? detail : "Недоступен: " + error), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new(600, 46), ClipText = true, TooltipText = file.Name });
            var open = Button("Смотреть", () => ReplayRequested?.Invoke(file.FullName)); open.Disabled = error != ""; row.AddChild(open);
        }
    }
}
