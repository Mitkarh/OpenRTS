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
        RequireString(layer, tiles, "TerrainId", required: true);
        int baseVisual = RequireString(layer, tiles, "VisualId");
        int RequireString(TileMapLayer source, TileSet set, string name, bool required = false)
        {
            int index = set.GetCustomDataLayerByName(name);
            if (index < 0 ? required : set.GetCustomDataLayerType(index) != Variant.Type.String)
                throw Error(source, "TileSet." + name, "Expected a string custom data field.");
            return index;
        }
        foreach (var cell in layer.GetUsedCells())
            if ((uint)cell.X >= size.X || (uint)cell.Y >= size.Y)
                throw Error(layer, $"cell {cell}", "Tiles must lie inside the map bounds.");

        var terrain = new Dictionary<string, int>(StringComparer.Ordinal);
        var visual = new Dictionary<string, int>(StringComparer.Ordinal);
        var groundCells = new int[size.X * size.Y];
        var visualIds = new string[groundCells.Length];
        int directionalLayer = tiles.GetCustomDataLayerByName("DirectionalVisual");
        if (directionalLayer >= 0 && tiles.GetCustomDataLayerType(directionalLayer) != Variant.Type.Bool)
            throw Error(layer, "TileSet", "DirectionalVisual custom data must be boolean.");
        int elevationLayer = tiles.GetCustomDataLayerByName("Elevation");
        int riseLayer = tiles.GetCustomDataLayerByName("Rise");
        int upperCornerLayer = tiles.GetCustomDataLayerByName("UpperCorner");
        int upperElevationLayer = tiles.GetCustomDataLayerByName("UpperElevation");
        int upperTerrainLayer = tiles.GetCustomDataLayerByName("UpperTerrainId");
        int upperVisualLayer = tiles.GetCustomDataLayerByName("UpperVisualId");
        bool hasCuts = upperCornerLayer >= 0 || upperElevationLayer >= 0;
        if (hasCuts && (upperCornerLayer < 0 || upperElevationLayer < 0 ||
            tiles.GetCustomDataLayerType(upperCornerLayer) != Variant.Type.Int ||
            tiles.GetCustomDataLayerType(upperElevationLayer) != Variant.Type.Int))
            throw Error(layer, "TileSet", "Diagonal cliff tiles require integer UpperCorner and UpperElevation custom data.");
        if ((upperTerrainLayer >= 0 || upperVisualLayer >= 0) && (!hasCuts || upperTerrainLayer < 0 ||
            tiles.GetCustomDataLayerType(upperTerrainLayer) != Variant.Type.String ||
            upperVisualLayer >= 0 && tiles.GetCustomDataLayerType(upperVisualLayer) != Variant.Type.String))
            throw Error(layer, "TileSet", "Split terrain requires a string UpperTerrainId layer and optional string UpperVisualId layer.");
        MapSurfaceCell[]? surface = null;
        if (elevationLayer >= 0 || riseLayer >= 0 || hasCuts)
        {
            if (elevationLayer < 0 || riseLayer < 0 || tiles.GetCustomDataLayerType(elevationLayer) != Variant.Type.Int ||
                tiles.GetCustomDataLayerType(riseLayer) != Variant.Type.Vector2I)
                throw Error(layer, "TileSet", "A relief TileSet requires integer Elevation and Vector2i Rise custom data.");
            surface = new MapSurfaceCell[groundCells.Length];
        }
        static int PaletteId(string id, Dictionary<string, int> palette)
        {
            if (!palette.TryGetValue(id, out int index))
            {
                index = palette.Count;
                palette.Add(id, index);
            }
            return index;
        }
        string? ReadId(TileMapLayer source, TileData tile, Vector2I cell, string field, int index, bool required = false)
        {
            if (index < 0) return null;
            var value = tile.GetCustomDataByLayerId(index);
            string id = value.AsString();
            if (id.Length == 0 && !required) return null;
            if (string.IsNullOrWhiteSpace(id)) throw Error(source, $"cell {cell}.{field}", "Expected a nonempty string ID.");
            return id;
        }
        static bool HasTransform(TileMapLayer source, TileData tile, Vector2I cell)
        {
            const long mask = TileSetAtlasSource.TransformFlipH | TileSetAtlasSource.TransformFlipV | TileSetAtlasSource.TransformTranspose;
            return (source.GetCellAlternativeTile(cell) & mask) != 0 || tile.FlipH || tile.FlipV || tile.Transpose;
        }

        int terrainId = tiles.GetCustomDataLayerByName("TerrainId");
        for (int i = 0; i < groundCells.Length; i++)
        {
            var cell = new Vector2I(i % size.X, i / size.X);
            var tile = layer.GetCellTileData(cell) ?? throw Error(layer, $"cell {cell}", "Every map cell needs an atlas tile.");
            string ground = ReadId(layer, tile, cell, "TerrainId", terrainId, required: true)!;
            groundCells[i] = PaletteId(ground, terrain);
            visualIds[i] = ReadId(layer, tile, cell, "VisualId", baseVisual) ?? ground;
            if (directionalLayer >= 0 && tile.GetCustomDataByLayerId(directionalLayer).AsBool() && HasTransform(layer, tile, cell))
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
                if ((rise != default || cut != default) && HasTransform(layer, tile, cell))
                    throw Error(layer, $"cell {cell}.Elevation", "Use an explicit relief tile for each direction instead of flipping or transposing it.");
                if (elevation is < int.MinValue or > int.MaxValue)
                    throw Error(layer, $"cell {cell}.Elevation", "Expected an Int32 height.");
                string? upperTerrain = OptionalId(upperTerrainLayer);
                string? upperVisual = OptionalId(upperVisualLayer);
                if (upperTerrain is not null && cut == default || upperVisual is not null && upperTerrain is null)
                    throw Error(layer, $"cell {cell}.UpperTerrainId", "Upper terrain requires a cut; upper visuals require upper terrain.");
                surface[i] = new((int)elevation, rise.X, rise.Y, cut, upperTerrain, upperVisual);

                string? OptionalId(int index)
                {
                    if (index < 0) return null;
                    string value = tile.GetCustomDataByLayerId(index).AsString();
                    return value.Length == 0 ? null : value;
                }
            }
        }

        var projection = MapProjection2D.From(layer);
        var declared = new HashSet<TileMapLayer> { layer };
        foreach (var overlay in root.VisualLayers)
        {
            if (overlay is null) throw Error(root, "VisualLayers", "Assign a TileMapLayer to each entry.");
            if (!declared.Add(overlay) || overlay.GetParent() != root || overlay.TopLevel ||
                !overlay.Transform.IsEqualApprox(Transform2D.Identity) || overlay.TileSet is not { } set ||
                set.TileShape != tiles.TileShape || set.TileLayout != tiles.TileLayout || set.TileSize != tileSize ||
                MapProjection2D.From(overlay) != projection)
                throw Error(overlay, "VisualLayers", "Use distinct direct layers with the same projection and identity transform as Terrain.");
            int lower = RequireString(overlay, set, "VisualId");
            int upper = RequireString(overlay, set, "UpperVisualId");
            if (lower < 0 && upper < 0)
                throw Error(overlay, "TileSet", "A visual layer requires VisualId or UpperVisualId.");
            int directional = set.GetCustomDataLayerByName("DirectionalVisual");
            if (directional >= 0 && set.GetCustomDataLayerType(directional) != Variant.Type.Bool)
                throw Error(overlay, "TileSet", "DirectionalVisual custom data must be boolean.");
            foreach (var cell in overlay.GetUsedCells())
            {
                if ((uint)cell.X >= size.X || (uint)cell.Y >= size.Y)
                    throw Error(overlay, $"cell {cell}", "Tiles must lie inside the map bounds.");
                var tile = overlay.GetCellTileData(cell) ?? throw Error(overlay, $"cell {cell}", "Expected an atlas tile.");
                if (directional >= 0 && tile.GetCustomDataByLayerId(directional).AsBool() && HasTransform(overlay, tile, cell))
                    throw Error(overlay, $"cell {cell}.VisualId", "Use an explicit atlas tile for this direction.");
                int i = cell.Y * size.X + cell.X;
                if (ReadId(overlay, tile, cell, "VisualId", lower) is { } id) visualIds[i] = id;
                if (ReadId(overlay, tile, cell, "UpperVisualId", upper) is not { } upperId) continue;
                if (surface is null || surface[i].UpperTerrainId is null)
                    throw Error(overlay, $"cell {cell}.UpperVisualId", "The gameplay layer must declare upper terrain in this cell.");
                surface[i] = surface[i] with { UpperVisualId = upperId };
            }
        }

        var (slots, objects) = ExtractMarkers(root, projection);
        var visualCells = visualIds.Select(id => PaletteId(id, visual)).ToArray();
        return new(root.MapId, root.IsSkirmish, size.X, size.Y,
            new(terrain.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray(), groundCells),
            new(visual.OrderBy(pair => pair.Value).Select(pair => pair.Key).ToArray(), visualCells),
            slots, objects, origin: Origin(root), surface: surface);
    }

    private static (List<MapSlotSource> Slots, MapObjectSource[] Objects) ExtractMarkers(OpenRtsMap2D root, MapProjection2D projection)
    {
        Source Origin(Node node) => new($"{root.SceneFilePath}::{root.GetPathTo(node)}", 1, 1);
        ContentException Error(Node node, string field, string message) => new(Origin(node), field, message);
        string Id(Node node, string id, HashSet<string> ids, string prefix)
        {
            bool local = prefix.Length != 0 || node is OpenRtsMapGroup2D;
            if (string.IsNullOrWhiteSpace(id) || local && (id.Contains('/') || id is "." or "..") || !ids.Add(id))
                throw Error(node, "ID", "Expected a nonempty unique local ID without '/'. Use Repair IDs and order.");
            return prefix + id;
        }
        int[] Order(Node node, int value, MapSceneExtractor.Context context)
        {
            if (value < 0 || !context.Orders.Add(value))
                throw Error(node, "Order", "Object/group order must be nonnegative and unique within its context.");
            return [.. context.Order, value];
        }
        WorldPosition Position(Node node, Vector2 point)
        {
            var (x, y) = projection.Unproject(point);
            int Coordinate(double cells)
            {
                var units = Math.Round(cells * WorldPosition.UnitsPerCell, MidpointRounding.AwayFromZero);
                if (!double.IsFinite(units) || units < 0 || units > WorldPosition.MaximumExtent)
                    throw Error(node, "Position", "Position lies outside the supported world range.");
                return (int)units;
            }
            return new(Coordinate(x), Coordinate(y));
        }

        var slots = new List<MapSlotSource>();
        var startIds = new HashSet<string>(StringComparer.Ordinal);
        var objectIds = new HashSet<string>(StringComparer.Ordinal);
        var objects = new List<(int[] Order, MapObjectSource Object)>();
        int visited = 0;
        void Visit(Node node, Vector2 translation, MapSceneExtractor.Context context, bool invalid, int depth)
        {
            if (++visited > MapCompiler.MaximumObjects || depth > 128)
                throw Error(node, "Scene", "Authoring scene exceeds the node/depth budget.");
            if (node is Node2D spatial)
            {
                translation += spatial.Position;
                invalid |= spatial.TopLevel || !spatial.Transform.IsFinite() ||
                    !spatial.Transform.X.IsEqualApprox(Vector2.Right) || !spatial.Transform.Y.IsEqualApprox(Vector2.Down);
            }
            else invalid = true;
            if (node is OpenRtsMap2D) throw Error(node, "Scene", "Instance an OpenRtsMapGroup2D, not another map.");
            if (node is OpenRtsMapGroup2D or OpenRtsMapStart2D or OpenRtsMapObject2D && invalid)
                throw Error(node, "Transform", "Use Node2D ancestors with translation only and TopLevel disabled.");
            switch (node)
            {
                case OpenRtsMapGroup2D group:
                    context = new(Id(group, group.InstanceId, context.Ids, context.Prefix) + "/", Order(group, group.Order, context));
                    break;
                case OpenRtsMapStart2D start:
                    if (!root.IsSkirmish || slots.Count >= MapCompiler.MaximumSlots)
                        throw Error(start, "Start", "Start markers require skirmish and must fit the slot budget.");
                    if (start.StartId == "neutral") throw Error(start, "StartId", "Reserved start ID.");
                    string startId = Id(start, start.StartId, context.StartIds, context.Prefix);
                    if (!startIds.Add(startId)) throw Error(start, "StartId", "Duplicate composed start ID. Use Repair IDs and order.");
                    slots.Add(new(startId, Position(start, translation), start.Reverse));
                    break;
                case OpenRtsMapObject2D obj:
                    var variant = obj.HealthVariant;
                    string objectId = Id(obj, obj.ObjectId, context.Ids, context.Prefix);
                    if (!objectIds.Add(objectId)) throw Error(obj, "ObjectId", "Duplicate composed object ID. Use Repair IDs and order.");
                    objects.Add((Order(obj, obj.Order, context), new(objectId,
                        obj.TypeId, Position(obj, translation), obj.OwnerSlot, Origin(obj), obj.QuarterTurns,
                        obj.InitialHealth == 0 ? null : obj.InitialHealth, variant is null ? null : new(variant.VariantId, variant.MaxHealth))));
                    break;
            }
            foreach (Node child in node.GetChildren()) Visit(child, translation, context, invalid, depth + 1);
        }
        var initial = new MapSceneExtractor.Context("", []);
        foreach (Node child in root.GetChildren()) Visit(child, Vector2.Zero, initial, false, 1);
        if (!root.IsSkirmish) slots.AddRange(root.FixtureSlots.Select(id => new MapSlotSource(id)));
        objects.Sort((a, b) => MapSceneExtractor.CompareOrder(a.Order, b.Order));
        return (slots, objects.Select(entry => entry.Object).ToArray());
    }
}
