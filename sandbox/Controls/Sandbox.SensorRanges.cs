using System;
using Godot;
using OpenRTS.Content;

public partial class Sandbox
{
    private enum RangeMode { Hidden, Selected, Always }
    [Flags]
    private enum RangeOwners { Own = 1, Allies = 2, Neutral = 4, Enemies = 8 }
    private sealed class RangeStyle(string id, string label, Color fallback, SensorRangeStyleSettings defaults)
    {
        public readonly string Id = id, Label = label;
        public readonly Color DefaultColor = defaults.Color is null ? fallback : new(defaults.Color);
        public readonly RangeMode DefaultMode = (RangeMode)defaults.Mode;
        public Color Color = defaults.Color is null ? fallback : new(defaults.Color);
        public RangeMode Mode = (RangeMode)defaults.Mode;
        public readonly RangeOwners Owners = defaults.Owners.Aggregate((RangeOwners)0, (mask, owner) => mask | (RangeOwners)owner);
        public bool Shows(bool selected, RangeOwners owner) => Mode != RangeMode.Hidden &&
            (Mode == RangeMode.Always || selected) && (Owners & owner) != 0;
    }
    private RangeStyle[] _rangeStyles = [];
    private void LoadRangeDefaults(SensorRangeSettings defaults) => _rangeStyles =
    [
        new("jam", "Глушение", new(1f, 0.72f, 0.25f, 0.65f), defaults.Jam),
        new("stealth", "Невидимость", new(0.2f, 0.95f, 1f, 0.75f), defaults.Stealth),
        new("radar", "Радар", new(0.45f, 1f, 0.4f, 0.65f), defaults.Radar)
    ];

    private void LoadRangeSettings(ConfigFile settings)
    {
        foreach (var style in _rangeStyles)
        {
            string section = "range_" + style.Id;
            style.Mode = (RangeMode)Math.Clamp((int)settings.GetValue(section, "mode", (int)style.Mode), 0, 2);
            style.Color = (Color)settings.GetValue(section, "color", style.Color);
        }
    }

    private void SaveRangeSettings(ConfigFile settings)
    {
        foreach (var style in _rangeStyles)
        {
            string section = "range_" + style.Id;
            if (style.Mode != style.DefaultMode) settings.SetValue(section, "mode", (int)style.Mode);
            if (style.Color != style.DefaultColor) settings.SetValue(section, "color", style.Color);
        }
    }

    private void AddRangeSettings(VBoxContainer menu)
    {
        var grid = new GridContainer { Name = "SensorRangeSettings", Columns = 3, Visible = false };
        menu.AddChild(TerminalButton("Радиусы действия…", "Цвета и режим показа", () => grid.Visible = !grid.Visible));
        menu.AddChild(grid);
        foreach (var style in _rangeStyles)
        {
            grid.AddChild(new Label { Text = style.Label });
            var mode = new OptionButton { FocusMode = Control.FocusModeEnum.None };
            foreach (string label in new[] { "Скрыты", "Выбранные", "Всегда" }) mode.AddItem(label);
            mode.Select((int)style.Mode);
            mode.ItemSelected += value => { style.Mode = (RangeMode)value; SaveTerminalSettings(); };
            grid.AddChild(mode);
            var color = new ColorPickerButton { Color = style.Color, CustomMinimumSize = new(44, 28) };
            color.ColorChanged += value => { style.Color = value; SaveTerminalSettings(); };
            grid.AddChild(color);
        }
    }
}
