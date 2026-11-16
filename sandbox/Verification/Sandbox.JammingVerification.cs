using System;
using System.IO;
using Godot;
using OpenRTS.Core;
using OpenRTS.Content;

public partial class Sandbox
{
    private async void VerifyJammingVisuals(FrameExchange.FrameLease frame)
    {
        if (_verificationDone) return;
        _verificationDone = true;
        try
        {
            if (frame.Poses.Length < 5 || frame.Poses.ToArray().Any(p => p.Presentation.Owner == 2) || frame.Contacts.Length != 0)
                throw new InvalidOperationException("Deployed red generators must hide their units from Blue.");
            using var red = _session!.Frames.AcquireLatest(2)!;
            if (!red.Poses.ToArray().Any(p => p.Cloaked && p.Emitter.HasStealthRotor && !p.Emitter.Moving))
                throw new InvalidOperationException("The owner's visual frame must contain a deployed cloaked emitter.");
            _selection.Reset();
            UpdateRadarContacts(frame);
            if (_rtsOverlay!.FieldRingCount != 0) throw new InvalidOperationException("Unselected sources must not show rings.");
            _selected = frame.Poses.ToArray().Single(p => _catalog!.Units[p.UnitType].Id == "headquarters").Id;
            UpdateRadarContacts(frame);
            if (_rtsOverlay.FieldRingCount != 1) throw new InvalidOperationException("HQ must show one primary radar ring, without secondary detection rings.");
            var radarStyle = _rangeStyles[2];
            var restricted = GameSettings.Load([new("range-test.yaml", "SensorRanges: {Radar: {Owners: []}}")]);
            _rangeStyles[2] = new("radar", "Радар", radarStyle.Color, restricted.SensorRanges.Radar);
            UpdateRadarContacts(frame);
            if (_rtsOverlay.FieldRingCount != 0) throw new InvalidOperationException("Range owner filter failed.");
            _rangeStyles[2] = radarStyle;
            var preferences = new ConfigFile();
            preferences.SetValue("range_radar", "owners", 15); // Legacy local masks must not override game rules.
            preferences.SetValue("range_radar", "mode", (int)RangeMode.Always);
            preferences.SetValue("range_radar", "color", Colors.Magenta);
            LoadRangeSettings(preferences);
            if (_rangeStyles[2].Shows(true, RangeOwners.Enemies) || _rangeStyles[2].Color != Colors.Magenta)
                throw new InvalidOperationException("Local preferences may change color and mode, never owner policy.");
            var savedPreferences = new ConfigFile();
            SaveRangeSettings(savedPreferences);
            if (savedPreferences.HasSectionKey("range_radar", "owners"))
                throw new InvalidOperationException("Owner policy must not be saved as a local preference.");
            _selection.Reset();
            UpdateRadarContacts(frame);
            if (_rtsOverlay.FieldRingCount != 3) throw new InvalidOperationException("Always mode must show three blue primary radars.");
            _rangeStyles[2].Mode = RangeMode.Selected;
            _rangeStyles[2].Color = _rangeStyles[2].DefaultColor;
            _fogViewer = 2;
            _selected = red.Poses.ToArray().First(p => p.Emitter.HasStealthRotor).Id;
            UpdateRadarContacts(red);
            if (_rtsOverlay.FieldRingCount != 2) throw new InvalidOperationException("Selected stealth source must show jamming and stealth rings.");
            if (((CanvasLayer)_matchMenu!.GetParent()).Layer <= ((CanvasLayer)_rtsOverlay.GetParent()).Layer)
                throw new InvalidOperationException("Pause menu must render above battlefield overlays.");
            _sprites!.Apply(red.Poses, 1, red.Tick, red.TickRate, red.Weapons, contacts: red.Contacts);
            if (!_sprites.Node.GetChildren().OfType<MultiMeshInstance3D>().Any(node =>
                node.Multimesh.VisibleInstanceCount > 0 && Math.Abs(node.Multimesh.Buffer[15] - 0.42f) < 0.001f &&
                !((ShaderMaterial)node.Multimesh.Mesh.SurfaceGetMaterial(0)).Shader.Code.Contains("ALPHA_SCISSOR_THRESHOLD")))
                throw new InvalidOperationException("Concealed models must use alpha blending, not opaque cutout.");
            if (_capturePath is not null && DisplayServer.GetName() != "headless")
            {
                _fogViewer = 2;
                _displayAlpha = 1;
                _displayPoses = red.Poses.ToArray();
                _displayCount = _displayPoses.Length;
                RefreshSelection();
                _camera!.Size = 32;
                CenterCamera(new(39, 25));
                _focusStartingBase = false;
                _fogView!.Apply(red, _fogStyle);
                _sprites!.Apply(red.Poses, 1, red.Tick, red.TickRate, red.Weapons, contacts: red.Contacts);
                UpdateRadarContacts(red);
                UpdateTerminal(red, 1);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var picture = GetViewport().GetTexture().GetImage();
                if (picture.SavePng(Path.GetFullPath(_capturePath)) != Error.Ok) throw new IOException("Jamming capture failed.");
                _matchMenu.Visible = true;
                ((Control)_matchMenu.FindChild("SensorRangeSettings", true, false)).Visible = true;
                _rtsOverlay.ShowCursor = false;
                Input.MouseMode = Input.MouseModeEnum.Visible;
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                using var menu = GetViewport().GetTexture().GetImage();
                string menuPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_capturePath))!, Path.GetFileNameWithoutExtension(_capturePath) + "-menu.png");
                if (menu.SavePng(menuPath) != Error.Ok) throw new IOException("Pause-menu capture failed.");
            }
            GD.Print("OPENRTS_JAMMING:PASS concealment, selected/always/owner range settings, primary radar and pause overlay order");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
