using Godot;
using OpenRTS.Core;

public partial class Sandbox
{
    private bool _verifySceneMapMenu, _verifyInitialProperties;

    private void SelectSceneMapVerification()
    {
        var maps = (OptionButton)_matchSetup!.FindChild("Maps", owned: false);
        var modes = (OptionButton)_matchSetup.FindChild("Modes", owned: false);
        int mapIndex = _verifyInitialProperties ? 4 : 3;
        CheckSkirmish(maps.ItemCount > mapIndex, "Published scene map is missing from the menu.");
        maps.Select(mapIndex);
        maps.EmitSignal(OptionButton.SignalName.ItemSelected, mapIndex);
        var players = (OptionButton)_matchSetup.FindChild("PlayerCount", owned: false);
        players.Select(1);
        players.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
        var fog = (OptionButton)_matchSetup.FindChild("Fog", owned: false);
        fog.Select((int)OpenRTS.Core.Visibility.FogMode.Visible);
        fog.EmitSignal(OptionButton.SignalName.ItemSelected, fog.Selected);
        modes.Select(1);
        modes.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
        ((Button)_matchSetup.FindChild("Launch", owned: false)).EmitSignal(Button.SignalName.Pressed);
    }

    private void VerifySceneMapMenu(FrameExchange.FrameLease frame)
    {
        string mapId = _verifyInitialProperties ? "initial-properties-sample" : "terrain-authoring-sample";
        var entry = GetNodeOrNull<Node3D>(_verifyInitialProperties ? "InitialPropertiesMap" : "TerrainAuthoringMap");
        var decoration = entry?.GetNodeOrNull<Node>("Decoration");
        CheckSkirmish(frame.Tick == 0 && frame.Poses.Length == (_verifyInitialProperties ? 13 : 10) && _mapMatch?.Map.Id == mapId,
             $"Scene-map selection did not launch its compiled projection ({_mapMatch?.Map.Id}, tick {frame.Tick}, {frame.Poses.Length} poses).");
        CheckSkirmish(decoration is not null && decoration.IsInGroup("scene_map_decoration") &&
            decoration.HasMeta("scene_map_ready") && decoration.GetNodeOrNull<WorldEnvironment>("Environment") is not null,
            "Scene-map hierarchy, persistent group, script or environment was lost.");
        if (_verifyInitialProperties)
        {
            var poses = frame.Poses.ToArray();
            var full = poses.Single(p => p.Current == new WorldPosition(12288, 12288));
            var wounded = poses.Single(p => p.Current == new WorldPosition(12288, 16384));
            var veteran = poses.Single(p => p.Current == new WorldPosition(14336, 16384));
            var plant = poses.Single(p => p.Current == new WorldPosition(8192, 16896));
            CheckSkirmish(full.Health == 100 && wounded.Health == 35 && full.UnitType == wounded.UnitType &&
                veteran.Health == 180 && veteran.UnitType != full.UnitType && _catalog!.Units[veteran.UnitType].MaxHealth == 180 &&
                plant.QuarterTurns == 1, "Published tick-zero frame lost initial properties or effective catalog.");
            GD.Print("OPENRTS_MAP_PROPERTIES:PASS tick-zero HP, health variant and rotation");
        }
        GD.Print("OPENRTS_SCENE_MAP_MENU:PASS scene and compiled simulation");
        _verificationDone = true;
        GetTree().Quit();
    }
}
