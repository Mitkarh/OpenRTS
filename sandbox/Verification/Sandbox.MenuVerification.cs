using System;
using System.IO;
using System.Threading.Tasks;
using Godot;

public partial class Sandbox
{
    private bool _verifyMenu;
    private string? _menuCaptures;
    private async Task VerifyMenuFlow()
    {
        try
        {
            var screen = _matchSetup!;
            void Click(string name) => ((Button)screen.FindChild(name, owned: false)).EmitSignal(Button.SignalName.Pressed);
            void Choose(string name, int value)
            {
                var option = (OptionButton)screen.FindChild(name, owned: false); option.Select(value); option.EmitSignal(OptionButton.SignalName.ItemSelected, value);
            }
            void Back(string page)
            {
                var node = screen.FindChild(page + "Page", owned: false);
                var button = Descendants(node).OfType<Button>().First(b => b.Text == "‹ Назад"); button.EmitSignal(Button.SignalName.Pressed);
            }
            CheckSkirmish(_session is null && ((Control)screen.FindChild("MainMenu", owned: false)).Visible, "Main menu started a world.");
            await CaptureMenu("main"); Click("MainSkirmish"); Click("ChooseMap"); await CaptureMenu("maps"); Click("SelectMap1");
            Choose("Role2", 1); Choose("Role3", 1); Choose("Role4", 2); Choose("Team1", 1); Choose("Team2", 1);
            Choose("Start1", 1);
            CheckSkirmish(((OptionButton)screen.FindChild("Start2", owned: false)).Selected == 0, "Occupied starts did not swap.");
            await CaptureMenu("lobby"); Click("AdvancedOptions"); await CaptureMenu("advanced"); Back("advanced"); Back("lobby");
            Click("MainSettings"); await CaptureMenu("settings"); Back("settings");
            Click("MainMultiplayer"); await CaptureMenu("multiplayer"); Back("browser"); Click("MainSkirmish");
            Click("Launch");
            for (int i = 0; i < 120 && _matchSetup is not null; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            CheckSkirmish(_matchSetup is null && _session is not null && _configuredOptions!.Seats[2].Controller == OpenRTS.Network.SlotController.Ai, "Shell did not launch the selected setup.");
            using var frame = _session!.Frames.AcquireLatest();
            CheckSkirmish(frame is not null && !frame.Poses.ToArray().Any(p => p.Presentation.Owner == 4) && frame.Poses.ToArray().Any(p => p.Presentation.Owner == 3), "Closed slot spawned units or the third AI was lost.");
            GD.Print("OPENRTS_MENU_PASS: main, skirmish, multiplayer, map catalog, advanced/settings, start exchange, AI and closed slots");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
    private static System.Collections.Generic.IEnumerable<Node> Descendants(Node parent)
    {
        foreach (var child in parent.GetChildren()) { yield return child; foreach (var next in Descendants(child)) yield return next; }
    }
    private async Task CaptureMenu(string name)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (_menuCaptures is null || DisplayServer.GetName() == "headless") return;
        Directory.CreateDirectory(_menuCaptures); DisplayServer.WindowMoveToForeground(); RenderingServer.ForceDraw(false);
        using var image = GetViewport().GetTexture().GetImage();
        if (image.SavePng(Path.Combine(_menuCaptures, name + ".png")) != Error.Ok) throw new IOException("Menu capture failed.");
    }
}
