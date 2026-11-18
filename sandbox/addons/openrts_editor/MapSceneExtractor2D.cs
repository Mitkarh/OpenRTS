using DeclarativeContent;
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Navigation;

namespace OpenRTS.Godot.Authoring;

internal static class MapSceneExtractor2D
{
    internal static MapSource Extract(OpenRtsMap2D root)
    {
        Source Origin(Node node) => new($"{root.SceneFilePath}::{root.GetPathTo(node)}", 1, 1);
        ContentException Error(Node node, string field, string message) => new(Origin(node), field, message);

        var size = root.Size;
        if (size.X is < 1 or > 32768 || size.Y is < 1 or > 32768 || (long)size.X * size.Y > NavigationGrid.MaximumCells)
            throw Error(root, "Size", "Map size exceeds the TileMapLayer or simulation cell budget.");
        if (root.TopLevel || !root.Transform.IsEqualApprox(Transform2D.Identity))
            throw Error(root, "Transform", "The map root requires an identity transform.");
        if (root.Terrain is not { TileSet: {} tiles } layer || layer.GetParent() != root ||
            layer.TopLevel || !layer.Transform.IsEqualApprox(Transform2D.Identity))
            throw Error(root, "Terrain", "Assign a direct TileMapLayer child with a TileSet and identity transform.");
        var tileSize = tiles.TileSize;
        bool square = tiles.TileShape == TileSet.TileShapeEnum.Square && tileSize.X == tileSize.Y;
        bool diamond = tiles.TileShape == TileSet.TileShapeEnum.Isometric && tiles.TileLayout == TileSet.TileLayoutEnum.DiamondDown;
        if (tileSize.X < 1 || tileSize.Y < 1 || (!square && !diamond))
            throw Error(layer, "TileSet", "Use square top-down tiles or an isometric Diamond Down layout.");
        if (tiles.GetCustomDataLayerByName("TerrainId") < 0 || tiles.GetCustomDataLayerByName("VisualId") < 0)
            throw Error(layer, "TileSet", "The TileSet requires string custom data fields TerrainId and VisualId.");
        foreach (var cell in layer.GetUsedCells())
            if ((uint)cell.X >= size.X || (uint)cell.Y >= size.Y)
                throw Error(layer, $"cell {cell}", "Tiles must lie inside the map bounds.");

        var terrain = new Dictionary<string, int>(StringComparer.Ordinal);
        var visual = new Dictionary<string, int>(StringComparer.Ordinal);
        var groundCells = new int[size.X * size.Y];
        var visualCells = new int[groundCells.Length];
        int directionalLayer = tiles.GetCustomDataLayerByName("DirectionalVisual");
        if (directionalLayer >= 0 && tiles.GetCustomDataLayerType(directionalLayer) != Variant.Type.Bool)
            throw Error(layer, "TileSet", "DirectionalVisual custom data must be boolean.");
        int elevationLayer = tiles.GetCustomDataLayerByName("Elevation");
        int riseLayer = tiles.GetCustomDataLayerByName("Rise");
        int upperCornerLayer = tiles.GetCustomDataLayerByName("UpperCorner");
        int upperElevationLayer = tiles.GetCustomDataLayerByName("UpperElevation");
        bool hasCuts = upperCornerLayer >= 0 || upperElevationLayer >= 0;
        if (hasCuts && (upperCornerLayer < 0 || upperElevationLayer < 0 ||
            tiles.GetCustomDataLayerType(upperCornerLayer) != Variant.Type.Int ||
            tiles.GetCustomDataLayerType(upperElevationLayer) != Variant.Type.Int))
            throw Error(layer, "TileSet", "Diagonal cliff tiles require integer UpperCorner and UpperElevation custom data.");
        MapSurfaceCell[]? surface = null;
        if (elevationLayer >= 0 || riseLayer >= 0 || hasCuts)
        {
            if (elevationLayer < 0 || riseLayer < 0 || tiles.GetCustomDataLayerType(elevationLayer) != Variant.Type.Int ||
                tiles.GetCustomDataLayerType(riseLayer) != Variant.Type.Vector2I)
                throw Error(layer, "TileSet", "A relief TileSet requires integer Elevation and Vector2i Rise custom data.");
            surface = new MapSurfaceCell[groundCells.Length];
        }
        int ReadId(TileData tile, Vector2I cell, string field, Dictionary<string, int> palette)
        {
            Variant value = tile.GetCustomData(field);
            if (value.VariantType != Variant.Type.String || string.IsNullOrWhiteSpace(value.AsString()))
                throw Error(layer, $"cell {cell}.{field}", "Expected a nonempty string ID.");
            var id = value.AsString();
            if (!palette.TryGetValue(id, out int index))
            {
                index = palette.Count;
                palette.Add(id, index);
            }
            return index;
        }
        bool HasTransform(TileData tile, Vector2I cell)
        {
            const long mask = TileSetAtlasSource.TransformFlipH | TileSetAtlasSource.TransformFlipV | TileSetAtlasSource.TransformTranspose;
            return (layer.GetCellAlternativeTile(cell) & mask) != 0 || tile.FlipH || tile.FlipV || tile.Transpose;
        }

        for (int i = 0; i < groundCells.Length; i++)
        {
            var cell = new Vector2I(i % size.X, i / size.X);
            var tile = layer.GetCellTileData(cell) ?? throw Error(layer, $"cell {cell}", "Every map cell needs an atlas tile.");
            groundCells[i] = ReadId(tile, cell, "TerrainId", terrain);
            visualCells[i] = ReadId(tile, cell, "VisualId", visual);
            if (directionalLayer >= 0 && tile.GetCustomDataByLayerId(directionalLayer).AsBool() && HasTransform(tile, cell))
                throw Error(layer, $"cell {cell}.VisualId", "Use the atlas tile for the required direction instead of flipping or transposing it.");
            if (surface is not null)
            {
                long elevation = tile.GetCustomData("Elevation").AsInt64();
                var rise = tile.GetCustomData("Rise").AsVector2I();
                SurfaceCut cut = default;
                if (hasCuts)
                {
                    long corner = tile.GetCustomDataByLayerId(upperCornerLayer).AsInt64();
                    long upper = tile.GetCustomDataByLayerId(upperElevationLayer).AsInt64();
                    if (corner < 0 || corner > 4 || upper is < int.MinValue or > int.MaxValue || corner == 0 && upper != 0)
                        throw Error(layer, $"cell {cell}.UpperCorner", "Expected an upper corner from 1 to 4 with an Int32 height, or two zeroes.");
                    cut = new((SurfaceCorner)corner, (int)upper);
                }
                if ((rise != default || cut != default) && HasTransform(tile, cell))
                    throw Error(layer, $"cell {cell}.Elevation", "Use an explicit relief tile for each direction instead of flipping or transposing it.");
                if (elevation is < int.MinValue or > int.MaxValue)
                    throw Error(layer, $"cell {cell}.Elevation", "Expected an Int32 height.");
                surface[i] = new((int)elevation, rise.X, rise.Y, cut);
            }
        }

        // Godot addresses tile centers. Simulation cell coordinates start at the corner of cell (0, 0).
        var center = layer.MapToLocal(Vector2I.Zero);
        var column = layer.MapToLocal(Vector2I.Right) - center;
        var row = layer.MapToLocal(Vector2I.Down) - center;
        var origin = center - (column + row) * 0.5f;
        double determinant = (double)column.X * row.Y - (double)column.Y * row.X;

        WorldPosition Position(Marker2D marker)
        {
            if (marker.GetParent() != root || marker.TopLevel || !marker.Transform.IsFinite() ||
                !marker.Transform.X.IsEqualApprox(Vector2.Right) || !marker.Transform.Y.IsEqualApprox(Vector2.Down))
                throw Error(marker, "Transform", "Use a direct child marker with translation only.");
            int Coordinate(double cells)
            {
                var units = Math.Round(cells * WorldPosition.UnitsPerCell, MidpointRounding.AwayFromZero);
                if (units < 0 || units > WorldPosition.MaximumExtent)
                    throw Error(marker, "Position", "Position lies outside the supported world range.");
                return (int)units;
            }
            double x = (double)marker.Position.X - origin.X;
            double y = (double)marker.Position.Y - origin.Y;
            return new(Coordinate((x * row.Y - y * row.X) / determinant),
                Coordinate((column.X * y - column.Y * x) / determinant));
        }

        var slots = new List<MapSlotSource>();
        var startIds = new HashSet<string>(StringComparer.Ordinal);
        var objects = new SortedDictionary<int, MapObjectSource>();
        // shortcut: flat markers for the pilot, add prefab groups when a second 2D map needs them.
        foreach (var marker in root.FindChildren("*", "Marker2D", recursive: true, owned: false))
        {
            switch (marker)
            {
                case OpenRtsMapStart2D start:
                    if (!root.IsSkirmish) throw Error(start, "Start", "Fixture maps use FixtureSlots instead of start markers.");
                    if (string.IsNullOrWhiteSpace(start.StartId) || start.StartId == "neutral" || !startIds.Add(start.StartId))
                        throw Error(start, "StartId", "Expected a unique start ID other than 'neutral'. Use Repair IDs and order.");
                    slots.Add(new(start.StartId, Position(start), start.Reverse));
                    break;
                case OpenRtsMapObject2D obj:
                    var variant = obj.HealthVariant;
                    var entry = new MapObjectSource(obj.ObjectId, obj.TypeId, Position(obj), obj.OwnerSlot, Origin(obj),
                        obj.QuarterTurns, obj.InitialHealth == 0 ? null : obj.InitialHealth,
                        variant is null ? null : new(variant.VariantId, variant.MaxHealth));
                    if (obj.Order < 0 || !objects.TryAdd(obj.Order, entry))
                        throw Error(obj, "Order", "Expected a unique nonnegative order.");
                    break;
            }
        }
        if (!root.IsSkirmish) slots.AddRange(root.FixtureSlots.Select(id => new MapSlotSource(id)));
        return new(root.MapId, root.IsSkirmish, size.X, size.Y,
            new(terrain.OrderBy(item => item.Value).Select(item => item.Key).ToArray(), groundCells),
            new(visual.OrderBy(item => item.Value).Select(item => item.Key).ToArray(), visualCells),
            slots, objects.Values.ToArray(), origin: Origin(root), surface: surface);
    }
}
