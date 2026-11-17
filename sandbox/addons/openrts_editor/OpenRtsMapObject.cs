using Godot;

namespace OpenRTS.Godot.Authoring;

[Tool, GlobalClass]
public partial class OpenRtsMapObject : Node3D
{
    [Export] public string ObjectId { get; set; } = "";
    [Export] public int Order { get; set; }
    [Export] public string TypeId { get; set; } = "rifleman";
    [Export] public string OwnerSlot { get; set; } = "neutral";
}
