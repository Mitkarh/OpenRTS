using Godot;

namespace OpenRTS.Godot.Authoring;

[Tool, GlobalClass]
public partial class OpenRtsHealthVariant : Resource
{
    [Export] public string VariantId { get; set; } = "";
    [Export(PropertyHint.Range, "1,1000000,1,or_greater")] public int MaxHealth { get; set; } = 100;
}
