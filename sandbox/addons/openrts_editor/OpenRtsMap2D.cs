using Godot;

namespace OpenRTS.Godot.Authoring;

[Tool, GlobalClass]
public partial class OpenRtsMap2D : Node2D
{
    [Export] public string MapId { get; set; } = "new-map";
    [Export] public bool IsSkirmish { get; set; } = true;
    [Export] public string[] FixtureSlots { get; set; } = ["blue", "red"];
    [Export] public Vector2I Size { get; set; } = new(32, 24);
    [Export] public TileMapLayer? Terrain { get; set; }
    [Export] public global::Godot.Collections.Array<TileMapLayer> VisualLayers { get; set; } = [];
    [Export(PropertyHint.File, "*.yaml")] public string ContentManifest { get; set; } = "res://game.yaml";
    [Export(PropertyHint.SaveFile, "*.ortmap.json")] public string CompiledPath { get; set; } = "res://artifacts/maps/map-2d.ortmap.json";
}
