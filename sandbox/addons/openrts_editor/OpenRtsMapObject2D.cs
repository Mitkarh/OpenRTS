using Godot;

namespace OpenRTS.Godot.Authoring;

[Tool, GlobalClass]
public partial class OpenRtsMapObject2D : Marker2D
{
    [Export] public string ObjectId { get; set; } = "";
    [Export] public int Order { get; set; }
    [Export] public string TypeId { get; set; } = "rifleman";

    [ExportGroup("Initial Properties")]
    [Export(PropertyHint.Enum, "North,East,South,West")] public int QuarterTurns { get; set; }
    [Export(PropertyHint.Range, "0,1000000,1,or_greater,suffix:HP (0 = full)")] public int InitialHealth { get; set; }
    [Export] public OpenRtsHealthVariant? HealthVariant { get; set; }
}
