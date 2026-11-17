using Godot;

namespace OpenRTS.Godot.Authoring;

/// <summary>A saved instance namespace; names and NodePaths never become gameplay IDs.</summary>
[Tool, GlobalClass]
public partial class OpenRtsMapGroup : Node3D
{
    [Export] public string InstanceId { get; set; } = "";
    [Export] public int Order { get; set; }
}
