#if TOOLS
using Godot;
using OpenRTS.Content;
using OpenRTS.Godot.Authoring;

/// <summary>GPU smoke capture of the same Models/terrain batches and physical contour geometry used by the addon.</summary>
public partial class MapPreviewCapture : Node3D
{
    public override async void _Ready()
    {
        OpenRtsMap? root = null; MapEditorPreview? preview = null;
        try
        {
            root = GD.Load<PackedScene>("res://examples/maps/TerrainAuthoringMap.tscn").Instantiate<OpenRtsMap>(); AddChild(root);
            var manifest = GameManifest.Load(ProjectSettings.GlobalizePath(root.ContentManifest)); var catalog = manifest.LoadUnits();
            var source = MapSceneExtractor.Extract(root); var identity = MapCompiler.Compile(source, catalog).GameplayFingerprint;
            preview = new(root, catalog, ModelCatalog.Load(manifest.Models)); preview.Apply(root, source);
            var mesh = new ImmediateMesh();
            mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, new StandardMaterial3D
                { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, NoDepthTest = true,
                  Transparency = BaseMaterial3D.TransparencyEnum.Alpha, RenderPriority = 127 });
            foreach (var obj in source.Objects)
            {
                var unit = catalog.Units.Single(u => u.Id == obj.Type);
                void Lines(Vector3[] points, Color color)
                {
                    foreach (var point in points) { mesh.SurfaceSetColor(color); mesh.SurfaceAddVertex(point + new Vector3(obj.Position.X / 1024f, 0, obj.Position.Y / 1024f)); }
                }
                Lines(MapPreviewGeometry.Body(unit.Body), Colors.Cyan);
                if (unit.Footprint is { } footprint) foreach (var cell in footprint.Cells)
                    Lines(MapPreviewGeometry.Cell(footprint, cell), cell.Navigation.Traverse == false ? Colors.CornflowerBlue : cell.Navigation.CanStop == false ? Colors.Orange : Colors.LimeGreen);
            }
            mesh.SurfaceEnd(); root.AddChild(new MeshInstance3D { Mesh = mesh });
            var camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = 28, Position = new(16, 40, 12), Current = true };
            AddChild(camera); camera.LookAt(new(16, 0, 12), Vector3.Forward);
            var layer = new CanvasLayer(); AddChild(layer);
            var label = new Label { Text = "OpenRTS · B3 Models / terrain / physical contours\nCyan: body   Blue: blocked footprint   Amber: cannot stop", Position = new(24, 16) };
            label.AddThemeFontSizeOverride("font_size", 19); layer.AddChild(label);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var image = GetViewport().GetTexture().GetImage();
            if (image.IsEmpty() || preview.SpriteCount < 4) throw new InvalidOperationException("Preview did not render Models.");
            if (MapCompiler.Compile(MapSceneExtractor.Extract(root), catalog).GameplayFingerprint != identity)
                throw new InvalidOperationException("Preview/capture changed gameplay identity.");
            string path = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--preview-capture=", StringComparison.Ordinal))?.Split('=', 2)[1]
                ?? ProjectSettings.GlobalizePath("res://artifacts/maps/b3-preview.png");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            if (image.SavePng(path) != Error.Ok) throw new IOException("Could not save preview capture.");
            GD.Print($"OPENRTS_MAP_PREVIEW:{preview.SpriteCount} sprites; {preview.BatchCount} batches; {image.GetWidth()}x{image.GetHeight()}; {path}");
            GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
        finally { preview?.Dispose(); root?.Free(); }
    }
}
#endif
