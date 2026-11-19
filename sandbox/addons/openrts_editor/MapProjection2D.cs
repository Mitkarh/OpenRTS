using Godot;
using OpenRTS.Core;

namespace OpenRTS.Godot.Authoring;

internal readonly record struct MapProjection2D(Vector2 Origin, Vector2 Column, Vector2 Row)
{
    internal static MapProjection2D From(TileMapLayer layer)
    {
        var center = layer.MapToLocal(Vector2I.Zero);
        var column = layer.MapToLocal(Vector2I.Right) - center;
        var row = layer.MapToLocal(Vector2I.Down) - center;
        return new(center - (column + row) * 0.5f, column, row);
    }

    internal Vector2 Project(WorldPosition point) => Origin +
        (Column * point.X + Row * point.Y) / WorldPosition.UnitsPerCell;

    internal (double X, double Y) Unproject(Vector2 point)
    {
        double determinant = (double)Column.X * Row.Y - (double)Column.Y * Row.X;
        double x = point.X - (double)Origin.X, y = point.Y - (double)Origin.Y;
        return ((x * Row.Y - y * Row.X) / determinant, (Column.X * y - Column.Y * x) / determinant);
    }
}
