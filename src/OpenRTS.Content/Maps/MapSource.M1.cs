using DeclarativeContent;
using OpenRTS.Core;
using YamlDotNet.RepresentationModel;

namespace OpenRTS.Content;

public sealed partial class MapSource
{
    public static MapSource FromM1(ContentSource source, UnitCatalog catalog, IEnumerable<ContentSource>? brushSources = null)
    {
        MapCompiler.ValidateDocument(source);
        ArgumentNullException.ThrowIfNull(catalog);
        var yaml = new MapYaml(source);
        var root = yaml.Root();
        var fields = yaml.Fields(root, source.Name, "version", "id", "kind", "size", "cellSize", "terrain", "objects", "slots", "starts");
        yaml.Integer(yaml.Required(fields, "version", root, source.Name), "version", 1, 1);
        string id = yaml.Text(yaml.Required(fields, "id", root, source.Name), "id");
        var kind = yaml.Required(fields, "kind", root, id);
        bool skirmish = yaml.Text(kind, "kind") switch
        {
            "skirmish" => true,
            "fixture" => false,
            _ => throw yaml.Error(kind, "kind", "Expected 'skirmish' or 'fixture'.")
        };
        string slotField = skirmish ? "starts" : "slots";
        string invalidSlotField = skirmish ? "slots" : "starts";
        if (fields.TryGetValue(invalidSlotField, out var invalid))
            throw yaml.Error(invalid, invalidSlotField, $"Use '{slotField}' for this map kind.");
        const int cellSize = WorldPosition.UnitsPerCell;
        yaml.Integer(yaml.Required(fields, "cellSize", root, id), "cellSize", cellSize, cellSize);
        var sizeNode = yaml.Required(fields, "size", root, id);
        var size = yaml.Pair(sizeNode, "size", 1, WorldPosition.MaximumExtent / cellSize);
        MapCompiler.ValidateDimensions(size.X, size.Y, cellSize, yaml.Location(sizeNode));

        var locomotion = catalog.Locomotion ?? throw yaml.Error(root, id, "Maps require a terrain catalog.");
        var terrainNode = yaml.Required(fields, "terrain", root, id);
        var terrain = yaml.Fields(terrainNode, "terrain", "palette", "rows", "brushes", "placements");
        var palette = new Dictionary<char, byte>();
        var paletteNode = yaml.Required(terrain, "palette", terrainNode, "terrain");
        foreach (var (symbol, value) in yaml.Fields(paletteNode, "terrain.palette"))
        {
            if (symbol.Length != 1 || !char.IsAsciiLetter(symbol[0]))
                throw yaml.Error(value, "terrain.palette", $"Expected an ASCII letter as a symbol, got '{symbol}'.");
            string name = yaml.Text(value, "terrain.palette." + symbol);
            int index = locomotion.Terrains.IndexOf(name);
            if (index < 0) throw yaml.Error(value, "terrain.palette." + symbol, $"Unknown terrain '{name}'.");
            palette.Add(symbol[0], (byte)index);
        }

        var rowsNode = yaml.Required(terrain, "rows", terrainNode, "terrain");
        var rows = yaml.Items(rowsNode, "terrain.rows");
        if (rows.Count != size.Y) throw yaml.Error(rowsNode, "terrain.rows", $"Expected {size.Y} rows.");
        var samples = new byte[size.X * size.Y];
        for (int y = 0; y < rows.Count; y++)
        {
            string path = $"terrain.rows[{y}]";
            string row = yaml.Text(rows[y], path);
            if (row.Length != size.X) throw yaml.Error(rows[y], path, $"Expected {size.X} symbols.");
            for (int x = 0; x < row.Length; x++)
            {
                if (!palette.TryGetValue(row[x], out byte value))
                    throw yaml.Error(rows[y], path, $"Unknown symbol '{row[x]}' at x={x}.");
                samples[y * size.X + x] = value;
            }
        }

        var layout = new TerrainLayout(size.X, size.Y, samples, samples.Select(value => locomotion.Terrains[value]));
        var brushes = new Dictionary<string, (int Width, int Height, TerrainBrushCell[] Cells)>(StringComparer.Ordinal);
        void ReadBrushes(MapYaml brushYaml, YamlNode brushesNode)
        {
            foreach (var (name, brushNode) in brushYaml.Fields(brushesNode, "brushes"))
            {
                string path = "brushes." + name;
                var brush = brushYaml.Fields(brushNode, path, "rows", "palette");
                var brushRows = brushYaml.Items(brushYaml.Required(brush, "rows", brushNode, path), path + ".rows");
                if (brushRows.Count == 0) throw brushYaml.Error(brushNode, path, "Brush rows cannot be empty.");
                var symbols = new Dictionary<char, (byte? Terrain, string? Visual)>();
                foreach (var (symbol, value) in brushYaml.Fields(brushYaml.Required(brush, "palette", brushNode, path), path + ".palette"))
                {
                    if (symbol.Length != 1 || symbol == ".") throw brushYaml.Error(value, path, "Brush symbols must be one character other than '.'.");
                    var entry = brushYaml.Fields(value, path + ".palette." + symbol, "terrain", "visual");
                    byte? type = null;
                    if (entry.TryGetValue("terrain", out var typeNode))
                    {
                        string typeName = brushYaml.Text(typeNode, path + ".terrain");
                        int typeIndex = locomotion.Terrains.IndexOf(typeName);
                        if (typeIndex < 0) throw brushYaml.Error(typeNode, path, $"Unknown terrain '{typeName}'.");
                        type = (byte)typeIndex;
                    }
                    string? visual = entry.TryGetValue("visual", out var visualNode) ? brushYaml.Text(visualNode, path + ".visual") : null;
                    if (type is null && visual is null) throw brushYaml.Error(value, path, "A brush cell needs terrain or visual.");
                    symbols.Add(symbol[0], (type, visual));
                }
                var cells = new List<TerrainBrushCell>();
                int brushWidth = 0;
                for (int y = 0; y < brushRows.Count; y++)
                {
                    string row = brushYaml.Text(brushRows[y], path + ".rows");
                    if (y == 0) brushWidth = row.Length;
                    if (row.Length != brushWidth || brushWidth == 0 || (long)brushWidth * brushRows.Count > NavigationGrid.MaximumCells)
                        throw brushYaml.Error(brushRows[y], path, "Brush rows must have equal nonzero length and fit the cell budget.");
                    for (int x = 0; x < row.Length; x++)
                    {
                        if (row[x] == '.') continue;
                        if (!symbols.TryGetValue(row[x], out var value)) throw brushYaml.Error(brushRows[y], path, $"Unknown brush symbol '{row[x]}'.");
                        cells.Add(new(x, y, value.Terrain, value.Visual));
                    }
                }
                if (!brushes.TryAdd(name, (brushWidth, brushRows.Count, cells.ToArray())))
                    throw brushYaml.Error(brushNode, path, $"Duplicate brush '{name}'.");
            }
        }
        if (brushSources is not null)
            foreach (var brushSource in brushSources)
            {
                MapCompiler.ValidateDocument(brushSource);
                var brushYaml = new MapYaml(brushSource);
                ReadBrushes(brushYaml, brushYaml.Root());
            }
        if (terrain.TryGetValue("brushes", out var inlineBrushes)) ReadBrushes(yaml, inlineBrushes);
        if (terrain.TryGetValue("placements", out var placementsNode))
        {
            foreach (var placementNode in yaml.Items(placementsNode, "terrain.placements"))
            {
                var placement = yaml.Fields(placementNode, "terrain.placements", "brush", "cell", "rotation");
                string name = yaml.Text(yaml.Required(placement, "brush", placementNode, "terrain.placements"), "terrain.placements.brush");
                if (!brushes.TryGetValue(name, out var brush)) throw yaml.Error(placementNode, "terrain.placements", $"Unknown brush '{name}'.");
                var cell = yaml.Pair(yaml.Required(placement, "cell", placementNode, "terrain.placements"), "terrain.placements.cell", 0, Math.Max(size.X, size.Y));
                int rotation = placement.TryGetValue("rotation", out var angle) ? (int)yaml.Integer(angle, "terrain.placements.rotation", 0, 3) : 0;
                try { layout.Apply(cell.X, cell.Y, brush.Width, brush.Height, brush.Cells, rotation); }
                catch (ArgumentException error) { throw yaml.Error(placementNode, "terrain.placements", error.Message); }
            }
        }

        WorldPosition Position(YamlNode node, string path)
        {
            var point = yaml.Pair(node, path, 0, WorldPosition.MaximumExtent);
            if (point.X >= size.X * cellSize || point.Y >= size.Y * cellSize)
                throw yaml.Error(node, path, $"Position ({point.X}, {point.Y}) is outside the map.");
            return point;
        }

        var slotNode = yaml.Required(fields, slotField, root, id);
        var slots = new List<MapSlotSource>();
        foreach (var node in yaml.Items(slotNode, slotField))
        {
            string name;
            WorldPosition? point = null; bool reverse = false;
            if (skirmish)
            {
                var entry = yaml.Fields(node, slotField, "id", "position", "reverse");
                name = yaml.Text(yaml.Required(entry, "id", node, slotField), slotField + ".id");
                string path = $"{slotField}.{name}";
                point = Position(yaml.Required(entry, "position", node, path), path + ".position");
                reverse = entry.TryGetValue("reverse", out var direction) && (yaml.Text(direction, path + ".reverse") switch
                {
                    "true" => true,
                    "false" => false,
                    _ => throw yaml.Error(direction, path + ".reverse", "Expected true or false.")
                });
            }
            else name = yaml.Text(node, slotField);

            slots.Add(new(name, point, reverse));
        }

        var objects = new List<MapObjectSource>();
        var objectsNode = yaml.Required(fields, "objects", root, id);
        foreach (var node in yaml.Items(objectsNode, "objects"))
        {
            var entry = yaml.Fields(node, "objects", "id", "type", "position", "owner");
            string name = yaml.Text(yaml.Required(entry, "id", node, "objects"), "objects.id");
            string path = $"objects.{name}";
            var typeNode = yaml.Required(entry, "type", node, path);
            string type = yaml.Text(typeNode, path + ".type");
            var ownerNode = yaml.Required(entry, "owner", node, path);
            string owner = yaml.Text(ownerNode, path + ".owner");
            objects.Add(new(name, type, Position(yaml.Required(entry, "position", node, path), path + ".position"),
                owner, yaml.Location(node)));
        }

        var terrainCells = new int[size.X * size.Y];
        var visualCells = new int[terrainCells.Length];
        var visualIds = layout.VisualIds();
        var visualIndices = visualIds.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index, StringComparer.Ordinal);
        for (int y = 0; y < size.Y; y++) for (int x = 0; x < size.X; x++)
        {
            int cell = y * size.X + x;
            terrainCells[cell] = layout.TerrainAt(x, y);
            visualCells[cell] = visualIndices[layout.VisualAt(x, y)];
        }
        return new(id, skirmish, size.X, size.Y, new(locomotion.Terrains, terrainCells), new(visualIds, visualCells),
            slots, objects, origin: yaml.Location(root));
    }

}
