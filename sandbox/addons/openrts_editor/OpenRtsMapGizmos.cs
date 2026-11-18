#if TOOLS
using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;
using OpenRTS.Content;

namespace OpenRTS.Godot.Authoring;

[Tool]
public partial class OpenRtsMapGizmos : EditorNode3DGizmoPlugin
{
    internal UnitCatalog? Catalog;
    public OpenRtsMapGizmos()
    {
        CreateMaterial("object", new Color(0.3f, 0.8f, 1));
        CreateMaterial("start", new Color(0.4f, 1, 0.4f));
        CreateMaterial("group", new Color(1, 0.7f, 0.3f));
        CreateMaterial("body", new Color(0.2f, 1, 1), onTop: true);
        CreateMaterial("blocked", new Color(0.3f, 0.55f, 1), onTop: true);
        CreateMaterial("no-stop", new Color(1, 0.65f, 0.15f), onTop: true);
        CreateMaterial("passable", new Color(0.3f, 1, 0.4f), onTop: true);
        CreateMaterial("untargetable", new Color(1, 0.3f, 0.8f), onTop: true);
    }
    public override string _GetGizmoName() => "OpenRTS map markers";
    public override bool _HasGizmo(Node3D node) => node is OpenRtsMap or OpenRtsMapObject or OpenRtsMapStart or OpenRtsMapGroup;
    public override void _Redraw(EditorNode3DGizmo gizmo)
    {
        gizmo.Clear();
        var node = gizmo.GetNode3D();
        Vector3[] lines;
        string material = "object";
        if (node is OpenRtsMap map)
        {
            int limit = WorldPosition.MaximumExtent / WorldPosition.UnitsPerCell;
            float x = Math.Clamp(map.Size.X, 1, limit), z = Math.Clamp(map.Size.Y, 1, limit);
            lines = [new(0, 0, 0), new(x, 0, 0), new(x, 0, 0), new(x, 0, z), new(x, 0, z), new(0, 0, z), new(0, 0, z), new(0, 0, 0)];
        }
        else
        {
            float radius = node is OpenRtsMapStart ? 0.8f : 0.35f;
            material = node is OpenRtsMapStart ? "start" : node is OpenRtsMapGroup ? "group" : "object";
            lines = [new(-radius, 0, 0), new(radius, 0, 0), new(0, 0, -radius), new(0, 0, radius),
                new(0, 0, 0), new(0, radius * 2, 0), new(-radius, 0, 0), new(0, radius * 2, 0),
                new(radius, 0, 0), new(0, radius * 2, 0)];
        }
        gizmo.AddLines(lines, GetMaterial(material, gizmo));
        gizmo.AddCollisionSegments(lines);
        if (node is OpenRtsMapObject obj && Catalog?.Units.FirstOrDefault(unit => unit.Id == obj.TypeId) is { } unit)
        {
            void Add(Vector3[] geometry, string name)
            {
                if (geometry.Length == 0) return;
                gizmo.AddLines(geometry, GetMaterial(name, gizmo)); gizmo.AddCollisionSegments(geometry);
            }
            Add(MapPreviewGeometry.Body(unit.Body), "body");
            if (unit.Footprint is not null && (uint)obj.QuarterTurns <= 3 && unit.WithRotation(obj.QuarterTurns).Footprint is { } footprint)
                foreach (var cell in footprint.Cells)
                {
                    var box = MapPreviewGeometry.Cell(footprint, cell);
                    Add(box, cell.Navigation.Traverse == false ? "blocked" : cell.Navigation.CanStop == false ? "no-stop" : "passable");
                    if (!cell.Targetable) Add([box[0], box[3]], "untargetable");
                }
        }
    }
}
#endif
