using System;
using System.IO;
using Godot;
using OpenRTS.Core;

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
            if (_capturePath is not null && DisplayServer.GetName() != "headless")
            {
                _fogViewer = 2;
                _selection.Reset();
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
            }
            GD.Print("OPENRTS_JAMMING:PASS hidden opponents, translucent own emitters and field visual data");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
    }
}
