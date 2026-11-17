#if TOOLS
using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;

namespace OpenRTS.Godot.Authoring;

public static class MapPreviewGeometry
{
    public static Vector3[] Body(BodyShape body)
    {
        if (body.Kind == BodyKind.None) return [];
        float x = body.HalfWidth / 1024f, z = body.HalfHeight / 1024f;
        if (body.Kind == BodyKind.Box) return Box(-x, -z, x, z);
        var lines = new Vector3[64];
        for (int i = 0; i < 32; i++)
        {
            float a = i * Mathf.Tau / 32, b = (i + 1) * Mathf.Tau / 32;
            lines[i * 2] = new(x * Mathf.Cos(a), 0.07f, x * Mathf.Sin(a));
            lines[i * 2 + 1] = new(x * Mathf.Cos(b), 0.07f, x * Mathf.Sin(b));
        }
        return lines;
    }
    public static Vector3[] Box(float left, float top, float right, float bottom) =>
        [new(left, 0.07f, top), new(right, 0.07f, top), new(right, 0.07f, top), new(right, 0.07f, bottom),
         new(right, 0.07f, bottom), new(left, 0.07f, bottom), new(left, 0.07f, bottom), new(left, 0.07f, top)];

    public static Vector3[] Cell(FootprintDefinition footprint, FootprintCell cell)
    {
        var bounds = footprint.Bounds(cell, default);
        return Box(bounds.MinX / 1024f, bounds.MinY / 1024f, bounds.MaxX / 1024f, bounds.MaxY / 1024f);
    }
}
#endif
