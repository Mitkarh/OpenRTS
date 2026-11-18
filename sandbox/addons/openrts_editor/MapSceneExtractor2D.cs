using DeclarativeContent;
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;

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
        var pixels = tiles.TileSize.X;
        if (tiles.TileShape != TileSet.TileShapeEnum.Square || pixels < 1 || tiles.TileSize.Y != pixels)
            throw Error(layer, "TileSet", "The pilot uses square top-down tiles.");
        if (tiles.GetCustomDataLayerByName("TerrainId") < 0 || tiles.GetCustomDataLayerByName("VisualId") < 0)
            throw Error(layer, "TileSet", "The TileSet requires string custom data fields TerrainId and VisualId.");
        foreach (var cell in layer.GetUsedCells())
            if ((uint)cell.X >= size.X || (uint)cell.Y >= size.Y)
                throw Error(layer, $"cell {cell}", "Tiles must lie inside the map bounds.");

        var terrain = new Dictionary<string, int>(StringComparer.Ordinal);
        var visual = new Dictionary<string, int>(StringComparer.Ordinal);
        var groundCells = new int[size.X * size.Y];
        var visualCells = new int[groundCells.Length];
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
        for (int i = 0; i < groundCells.Length; i++)
        {
            var cell = new Vector2I(i % size.X, i / size.X);
            var tile = layer.GetCellTileData(cell) ?? throw Error(layer, $"cell {cell}", "Every map cell needs an atlas tile.");
            groundCells[i] = ReadId(tile, cell, "TerrainId", terrain);
            visualCells[i] = ReadId(tile, cell, "VisualId", visual);
        }

        WorldPosition Position(Marker2D marker)
        {
            if (marker.GetParent() != root || marker.TopLevel || !marker.Transform.IsFinite() ||
                !marker.Transform.X.IsEqualApprox(Vector2.Right) || !marker.Transform.Y.IsEqualApprox(Vector2.Down))
                throw Error(marker, "Transform", "Use a direct child marker with translation only.");
            int Coordinate(float value)
            {
                var units = Math.Round((double)value * WorldPosition.UnitsPerCell / pixels, MidpointRounding.AwayFromZero);
                if (units < 0 || units > WorldPosition.MaximumExtent)
                    throw Error(marker, "Position", "Position lies outside the supported world range.");
                return (int)units;
            }
            return new(Coordinate(marker.Position.X), Coordinate(marker.Position.Y));
        }

        var slots = new List<MapSlotSource>();
        var objects = new SortedDictionary<int, MapObjectSource>();
        // shortcut: flat markers for the pilot, add prefab groups when a second 2D map needs them.
        foreach (var marker in root.FindChildren("*", "Marker2D", recursive: true, owned: false))
        {
            switch (marker)
            {
                case OpenRtsMapStart2D start:
                    slots.Add(new(start.StartId, Position(start), start.Reverse));
                    break;
                case OpenRtsMapObject2D obj:
                    var variant = obj.HealthVariant;
                    var entry = new MapObjectSource(obj.ObjectId, obj.TypeId, Position(obj), "neutral", Origin(obj),
                        obj.QuarterTurns, obj.InitialHealth == 0 ? null : obj.InitialHealth,
                        variant is null ? null : new(variant.VariantId, variant.MaxHealth));
                    if (obj.Order < 0 || !objects.TryAdd(obj.Order, entry))
                        throw Error(obj, "Order", "Expected a unique nonnegative order.");
                    break;
            }
        }
        return new(root.MapId, true, size.X, size.Y,
            new(terrain.OrderBy(item => item.Value).Select(item => item.Key).ToArray(), groundCells),
            new(visual.OrderBy(item => item.Value).Select(item => item.Key).ToArray(), visualCells),
            slots, objects.Values.ToArray(), origin: Origin(root));
    }
}
