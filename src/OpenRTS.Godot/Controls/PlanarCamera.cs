using Godot;

namespace OpenRTS.Godot.Controls;

internal static class PlanarCamera
{
    public static Vector3? GroundPoint(Camera3D camera, Vector2 pixel)
    {
        var origin = camera.ProjectRayOrigin(pixel);
        var direction = camera.ProjectRayNormal(pixel);
        if (MathF.Abs(direction.Y) < 0.0001f) return null;
        float distance = -origin.Y / direction.Y;
        return distance < 0 ? null : origin + direction * distance;
    }

    public static void Pan(Camera3D camera, Vector3 offset, Vector2 centerPixel, Vector2 mapSize)
    {
        if (GroundPoint(camera, centerPixel) is not { } center) return;
        camera.Position += new Vector3(
            Math.Clamp(center.X + offset.X, 0, mapSize.X) - center.X, 0,
            Math.Clamp(center.Z + offset.Z, 0, mapSize.Y) - center.Z);
    }

    public static void ZoomAt(Camera3D camera, Vector2 pixel, float factor, float baseSize, Vector2 centerPixel, Vector2 mapSize)
    {
        var before = GroundPoint(camera, pixel);
        camera.Size = Math.Clamp(camera.Size * factor, Math.Min(4, baseSize), baseSize * 2);
        if (before is { } from && GroundPoint(camera, pixel) is { } to)
            Pan(camera, from - to, centerPixel, mapSize);
    }

    public static void Drag(Camera3D camera, Vector2 pixel, Vector2 relative, Vector2 centerPixel, Vector2 mapSize)
    {
        if (GroundPoint(camera, pixel - relative) is { } before && GroundPoint(camera, pixel) is { } after)
            Pan(camera, before - after, centerPixel, mapSize);
    }
}
