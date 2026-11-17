using Godot;

namespace OpenRTS.Godot.Authoring;

/// <summary>Row-major symbolic cells. Empty strings retain the corresponding previous layer.</summary>
[Tool, GlobalClass]
public partial class OpenRtsTerrainBrush : Resource
{
    [Export] public Vector2I Size { get; set; } = Vector2I.One;
    [Export] public string[] Terrain { get; set; } = ["grass"];
    [Export] public string[] Visual { get; set; } = ["grass"];

    public OpenRtsTerrainBrush Snapshot() => new() { Size = Size, Terrain = (string[])Terrain.Clone(), Visual = (string[])Visual.Clone(), ResourceLocalToScene = true };
}
