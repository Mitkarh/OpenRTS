using Godot;

namespace OpenRTS.Godot.Authoring;

/// <summary>Namespace and ordering for a reusable 2D placement group.</summary>
[Tool, GlobalClass]
public partial class OpenRtsMapGroup2D : Node2D
{
    [Export] public string InstanceId { get; set; } = "";
    [Export] public int Order { get; set; }
}
