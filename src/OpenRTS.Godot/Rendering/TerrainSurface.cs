using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;

namespace OpenRTS.Godot.Rendering;

internal sealed class TerrainSurface
{
    private readonly record struct Face(Vector3 A, Vector3 B, Vector3 C, int Cell);
    private readonly NavigationGrid _grid;
    private readonly Face[] _faces;
    public ArrayMesh Mesh { get; }

    public TerrainSurface(NavigationGrid grid, Texture2D? ground = null, Texture2D? ramp = null, Texture2D? cliff = null)
    {
        _grid = grid;
        int size = grid.CellSize;
        var faces = new List<Face>(2 * grid.CellCount);
        using var deck = Builder(ground);
        using var slopes = Builder(ramp);
        using var walls = Builder(cliff);
        bool hasDeck = false, hasRamps = false, hasWalls = false;
        for (int y = 0; y < grid.Height; y++)
            for (int x = 0; x < grid.Width; x++)
            {
                int cell = grid.Index(new(x, y)), left = x * size, top = y * size, right = left + size, bottom = top + size;
                var a = Vertex(cell, left, top);
                var b = Vertex(cell, right, top);
                var c = Vertex(cell, left, bottom);
                var d = Vertex(cell, right, bottom);
                bool rising = grid.SurfaceRiseAt(cell) != default;
                bool raised = grid.SurfaceAt(cell).Elevation > 0;
                var color = ground is null ? rising ? new Color("#64854c") : raised ? new Color("#527256") : new Color("#394950") :
                    raised && !rising ? new Color("#c3cdaf") : Colors.White;
                var surface = rising && ramp is not null ? slopes : deck;
                hasDeck |= surface == deck;
                hasRamps |= surface == slopes;
                Add(surface, a, b, c, cell, color);
                Add(surface, b, d, c, cell, color);
                if (x + 1 < grid.Width)
                    AddCliff(b, d, Vertex(cell + 1, right, top), Vertex(cell + 1, right, bottom));
                if (y + 1 < grid.Height)
                    AddCliff(c, d, Vertex(cell + grid.Width, left, bottom), Vertex(cell + grid.Width, right, bottom));
            }
        Mesh = new ArrayMesh();
        if (hasDeck) Finish(deck);
        if (hasRamps) Finish(slopes);
        if (hasWalls) Finish(walls);
        _faces = faces.ToArray();

        Vector3 Vertex(int cell, int x, int y) => new Vector3(x, grid.SurfaceElevationAt(cell, x, y), y) / WorldPosition.UnitsPerCell;

        void Finish(SurfaceTool builder)
        {
            builder.GenerateNormals();
            builder.Commit(Mesh);
        }

        void Add(SurfaceTool builder, Vector3 a, Vector3 b, Vector3 c, int cell, Color color, bool wallAlongX = false)
        {
            if (a == b || b == c || a == c) return;
            faces.Add(new(a, b, c, cell));
            builder.SetColor(color);
            Vertex(a); Vertex(b); Vertex(c);

            void Vertex(Vector3 point)
            {
                var uv = cell >= 0 ? new Vector2(point.X, point.Z)
                    : new Vector2((wallAlongX ? point.X : point.Z) * 0.5f, -point.Y * 0.5f);
                builder.SetUV(uv);
                builder.AddVertex(point);
            }
        }

        void AddCliff(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            float start = a.Y - c.Y, end = b.Y - d.Y;
            if (start == 0 && end == 0) return;
            var surface = cliff is null ? deck : walls;
            hasDeck |= surface == deck;
            hasWalls |= surface == walls;
            bool alongX = a.X != b.X;
            var rock = cliff is null ? new Color("#a47351") : alongX ? new Color("#e3d6c3") : new Color("#b9b6ac");
            if (start * end < 0)
            {
                var crossing = a.Lerp(b, start / (start - end));
                Add(surface, a, c, crossing, -1, rock, alongX);
                Add(surface, crossing, d, b, -1, rock, alongX);
            }
            else
            {
                Add(surface, a, c, b, -1, rock, alongX);
                Add(surface, b, c, d, -1, rock, alongX);
            }
        }
    }

    private static SurfaceTool Builder(Texture2D? texture)
    {
        var builder = new SurfaceTool();
        builder.Begin(global::Godot.Mesh.PrimitiveType.Triangles);
        builder.SetSmoothGroup(uint.MaxValue);
        if (texture is not null)
            builder.SetMaterial(new StandardMaterial3D
            {
                AlbedoTexture = texture,
                VertexColorUseAsAlbedo = true,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            });
        return builder;
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
        // shortcut: scan this small terrain; index the faces before using this picker on large maps.
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
