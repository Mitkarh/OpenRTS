using System.ComponentModel.DataAnnotations;

namespace OpenRTS.Content;

public enum SensorRangeMode { Hidden, Selected, Always }
public enum SensorRangeOwner { Own = 1, Allies = 2, Neutral = 4, Enemies = 8 }

public sealed class SensorRangeStyleSettings
{
    public SensorRangeMode Mode { get; init; } = SensorRangeMode.Selected;
    [RegularExpression("^#[0-9a-fA-F]{8}$"), StringLength(9, MinimumLength = 9)]
    public string? Color { get; init; }
    public SensorRangeOwner[] Owners { get; init; } = [SensorRangeOwner.Own, SensorRangeOwner.Allies];
}

public sealed class SensorRangeSettings
{
    public SensorRangeStyleSettings Jam { get; init; } = new();
    public SensorRangeStyleSettings Stealth { get; init; } = new();
    public SensorRangeStyleSettings Radar { get; init; } = new();
}
