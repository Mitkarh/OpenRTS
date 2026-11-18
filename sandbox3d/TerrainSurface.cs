using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;

internal sealed class TerrainSurface
{
    private readonly record struct Face(Vector3 A, Vector3 B, Vector3 C, int Cell);
    private readonly NavigationGrid _grid;
    private readonly Face[] _faces;
    public ArrayMesh Mesh { get; }

    public TerrainSurface(NavigationGrid grid)
    {
        _grid = grid;
        int size = grid.CellSize;
        var faces = new List<Face>(2 * grid.CellCount);
        using var mesh = new SurfaceTool();
        mesh.Begin(Godot.Mesh.PrimitiveType.Triangles);
        mesh.SetSmoothGroup(uint.MaxValue);
        for (int y = 0; y < grid.Height; y++)
            for (int x = 0; x < grid.Width; x++)
            {
                int cell = grid.Index(new(x, y)), left = x * size, top = y * size, right = left + size, bottom = top + size;
                var a = Vertex(cell, left, top);
                var b = Vertex(cell, right, top);
                var c = Vertex(cell, left, bottom);
                var d = Vertex(cell, right, bottom);
                var color = grid.SurfaceRiseAt(cell) != default ? new Color("#64854c") :
                    grid.SurfaceAt(cell).Elevation > 0 ? new Color("#527256") : new Color("#394950");
                Add(a, b, c, cell, color);
                Add(b, d, c, cell, color);
                if (x + 1 < grid.Width)
                    AddCliff(b, d, Vertex(cell + 1, right, top), Vertex(cell + 1, right, bottom));
                if (y + 1 < grid.Height)
                    AddCliff(c, d, Vertex(cell + grid.Width, left, bottom), Vertex(cell + grid.Width, right, bottom));
            }
        mesh.GenerateNormals();
        Mesh = mesh.Commit();
        _faces = faces.ToArray();

        Vector3 Vertex(int cell, int x, int y) => new Vector3(x, grid.SurfaceElevationAt(cell, x, y), y) / WorldPosition.UnitsPerCell;

        void Add(Vector3 a, Vector3 b, Vector3 c, int cell, Color color)
        {
            if (a == b || b == c || a == c) return;
            faces.Add(new(a, b, c, cell));
            mesh.SetColor(color);
            mesh.AddVertex(a);
            mesh.AddVertex(b);
            mesh.AddVertex(c);
        }

        void AddCliff(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            var rock = new Color("#a47351");
            float start = a.Y - c.Y, end = b.Y - d.Y;
            if (start * end < 0)
            {
                var crossing = a.Lerp(b, start / (start - end));
                Add(a, c, crossing, -1, rock);
                Add(crossing, d, b, -1, rock);
            }
            else
            {
                Add(a, c, b, -1, rock);
                Add(b, c, d, -1, rock);
            }
        }
    }

    public Vector3 NormalAt(WorldPosition point)
    {
        var rise = _grid.SurfaceRiseAt(_grid.CellAt(point));
        return new Vector3(-rise.X, _grid.CellSize, -rise.Y).Normalized();
    }

    public WorldPosition? Pick(Camera3D camera, Vector2 pixel)
    {
        var origin = camera.ProjectRayOrigin(pixel);
        var direction = camera.ProjectRayNormal(pixel);
        float nearest = float.PositiveInfinity;
        Vector3 hit = default;
        int cell = -1;
        foreach (var face in _faces)
        {
            var intersection = Geometry3D.RayIntersectsTriangle(origin, direction, face.A, face.B, face.C);
            if (intersection.VariantType != Variant.Type.Vector3) continue;
            var point = intersection.AsVector3();
            float distance = origin.DistanceSquaredTo(point);
            if (distance > nearest || (distance == nearest && cell >= 0)) continue;
            nearest = distance;
            hit = point;
            cell = face.Cell;
        }
        if (cell < 0) return null;

        int size = _grid.CellSize, left = cell % _grid.Width * size, top = cell / _grid.Width * size;
        // Keep rounding on the surface we hit even when the next cell starts at a cliff.
        int x = Math.Clamp((int)MathF.Round(hit.X * WorldPosition.UnitsPerCell), left, left + size - 1);
        int y = Math.Clamp((int)MathF.Round(hit.Z * WorldPosition.UnitsPerCell), top, top + size - 1);
        return new WorldPosition(x, y, _grid.SurfaceElevationAt(cell, x, y));
    }
}
