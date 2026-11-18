using Godot;

namespace OpenRTS.Godot.Authoring;

[Tool, GlobalClass]
public partial class OpenRtsMapObject : Node3D
{
    [Export] public string ObjectId { get; set; } = "";
    [Export] public int Order { get; set; }
    [Export] public string TypeId { get; set; } = "rifleman";
    [Export] public string OwnerSlot { get; set; } = "neutral";

    [ExportGroup("Initial Properties")]
    [Export(PropertyHint.Enum, "North,East,South,West")] public int QuarterTurns { get; set; }
    // Zero is the Inspector's "full health" sentinel.
    [Export(PropertyHint.Range, "0,1000000,1,or_greater,suffix:HP (0 = full)")] public int InitialHealth { get; set; }
    [Export] public OpenRtsHealthVariant? HealthVariant { get; set; }
}
