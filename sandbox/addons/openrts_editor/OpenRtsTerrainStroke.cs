using Godot;

namespace OpenRTS.Godot.Authoring;

[Tool, GlobalClass]
public partial class OpenRtsTerrainStroke : Resource
{
    [Export] public string StrokeId { get; set; } = "";
    [Export] public OpenRtsTerrainBrush? Brush { get; set; }
    [Export] public int QuarterTurns { get; set; }
    [Export] public global::Godot.Collections.Array<Vector2I> Cells { get; set; } = new();
}
