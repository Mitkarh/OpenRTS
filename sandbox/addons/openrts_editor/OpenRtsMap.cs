using Godot;

namespace OpenRTS.Godot.Authoring;

[Tool, GlobalClass]
public partial class OpenRtsMap : Node3D
{
    [Export] public string MapId { get; set; } = "new-map";
    [Export] public bool IsSkirmish { get; set; } = true;
    private Vector2I _size = new(32, 24);
    [Export] public Vector2I Size
    {
        get => _size;
        set { _size = value; if (Engine.IsEditorHint() && IsInsideTree()) UpdateGizmos(); }
    }
    [Export] public string TerrainId { get; set; } = "grass";
    [Export] public string VisualId { get; set; } = "grass";
    [Export] public global::Godot.Collections.Array<OpenRtsTerrainStroke> TerrainStrokes { get; set; } = new();
    [Export] public global::Godot.Collections.Dictionary<string, Texture2D> VisualTextures { get; set; } = new();
    [Export] public string[] FixtureSlots { get; set; } = ["blue", "red"];
    [Export(PropertyHint.File, "*.yaml")] public string ContentManifest { get; set; } = "res://game.yaml";
    [Export(PropertyHint.SaveFile, "*.ortmap.json")] public string CompiledPath { get; set; } = "res://artifacts/maps/new-map.ortmap.json";
}
