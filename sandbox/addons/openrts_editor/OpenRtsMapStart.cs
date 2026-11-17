using Godot;

namespace OpenRTS.Godot.Authoring;

[Tool, GlobalClass]
public partial class OpenRtsMapStart : Node3D
{
    [Export] public string StartId { get; set; } = "";
    [Export] public bool Reverse { get; set; }
}
