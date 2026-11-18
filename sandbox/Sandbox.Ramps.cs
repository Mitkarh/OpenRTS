using Godot;
using OpenRTS.Content;
using OpenRTS.Core;

public partial class Sandbox
{
    private bool _ramps, _verifyRamps;
    private RampScenario? _rampScenario;
    private TerrainSurface? _rampTerrain;

    private UnitCatalog LoadRampCatalog()
    {
        var locomotion = LocomotionCatalog.Load(_manifest.Terrain.Append(ReadFile("res://content/ramp-locomotors.yaml")));
        var weapons = WeaponCatalog.Load(_manifest.Weapons);
        return UnitCatalog.Load(_manifest.Rules.Append(ReadFile("res://content/ramp-units.yaml")), locomotion, weapons);
    }

    private void AddRampTerrain()
    {
        Texture2D Tile(string name) => GD.Load<Texture2D>($"res://assets/terrain/{name}.png")
            ?? throw new InvalidOperationException($"Missing terrain texture: {name}");
        _rampTerrain = new(_rampScenario!.Grid, Tile("grass"), Tile("rock-ramp"), Tile("cliff-face"));
        AddChild(new MeshInstance3D { Name = "RockyPass", Mesh = _rampTerrain.Mesh });
    }

    private WorldPosition? RampPick(Vector2 pixel) =>
        _rampTerrain is { } terrain && _camera is { } camera ? terrain.Pick(camera, pixel) : null;
}
