using Godot;
using OpenRTS.Core;

public partial class Sandbox
{
    private bool _verifySceneMapMenu;

    private void SelectSceneMapVerification()
    {
        var maps = (OptionButton)_matchSetup!.FindChild("Maps", owned: false);
        var modes = (OptionButton)_matchSetup.FindChild("Modes", owned: false);
        CheckSkirmish(maps.ItemCount == 4, "Published scene map is missing from the menu.");
        maps.Select(3);
        maps.EmitSignal(OptionButton.SignalName.ItemSelected, 3);
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
        var entry = GetNodeOrNull<Node3D>("TerrainAuthoringMap");
        var decoration = entry?.GetNodeOrNull<Node>("Decoration");
        CheckSkirmish(frame.Tick == 0 && frame.Poses.Length == 10 && _mapMatch?.Map.Id == "terrain-authoring-sample",
             $"Scene-map selection did not launch its compiled projection ({_mapMatch?.Map.Id}, tick {frame.Tick}, {frame.Poses.Length} poses).");
        CheckSkirmish(decoration is not null && decoration.IsInGroup("scene_map_decoration") &&
            decoration.HasMeta("scene_map_ready") && decoration.GetNodeOrNull<WorldEnvironment>("Environment") is not null,
            "Scene-map hierarchy, persistent group, script or environment was lost.");
        GD.Print("OPENRTS_SCENE_MAP_MENU:PASS scene and compiled simulation");
        _verificationDone = true;
        GetTree().Quit();
    }
}
