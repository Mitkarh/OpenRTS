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

    private void AddRampTerrain(NavigationGrid grid, GameMap? map, Func<string, Texture2D> textureFor)
    {
        var covers = (map?.VisualIds ?? ["grass", "rock-ramp"]).ToDictionary(id => id, textureFor, StringComparer.Ordinal);
        var cliff = GD.Load<Texture2D>("res://assets/terrain/cliff-face.png")
            ?? throw new InvalidOperationException("Missing terrain texture: cliff-face");
        _rampTerrain = new(grid, (x, y) => covers[map?.VisualAt(x, y) ??
            (grid.SurfaceRiseAt(grid.Index(new(x, y))) == default ? "grass" : "rock-ramp")], cliff);
        AddChild(new MeshInstance3D { Name = "RaisedTerrain", Mesh = _rampTerrain.Mesh });
    }

    private WorldPosition? RampPick(Vector2 pixel) =>
        _rampTerrain is { } terrain && _camera is { } camera ? terrain.Pick(camera, pixel) : null;
}
