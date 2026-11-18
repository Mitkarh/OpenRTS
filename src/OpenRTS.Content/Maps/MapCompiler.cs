using System.Security.Cryptography;
using System.Text;
using DeclarativeContent;
using OpenRTS.Core;

namespace OpenRTS.Content;

/// <summary>Validated map data, bound to installed gameplay content; no runtime caches or scene objects.</summary>
public sealed class CompiledMap
{
    public MapSource Source { get; }
    public GameMap Map { get; }
    public string ContentFingerprint { get; }
    public string EffectiveContentFingerprint => Map.Catalog.Summary.Fingerprint;
    public string GameplayFingerprint { get; }
    public string DataFingerprint { get; }

    internal CompiledMap(MapSource source, UnitCatalog effective, string content, CancellationToken stop)
    {
        Source = source; ContentFingerprint = content;
        GameplayFingerprint = Digest(MapJson.Write(source, gameplayOnly: true, stop));
        DataFingerprint = Digest(MapJson.Write(source, stop: stop));
        Map = new GameMap(source, effective, GameplayFingerprint);
    }
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public string Write(CancellationToken cancellationToken = default) => MapJson.Write(this, cancellationToken);
}

public static class MapCompiler
{
    public const int Version = 3;
    public const string Geometry = "ground-plane-v1";
    internal const string SurfaceGeometry = "single-surface-v1";
    public const int MaximumDocumentBytes = 64 * 1024 * 1024;
    public const int MaximumPaletteEntries = 65536;
    public const int MaximumObjects = 1000000;
    public const int MaximumSlots = 256;
    public const int MaximumTextBytes = 1024;

    /// <summary>Checks static data; physical placement and chosen starting sets are validated by Map.Prepare(...).CreateWorld(...).</summary>
    public static CompiledMap Compile(MapSource source, UnitCatalog catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(catalog);
        cancellationToken.ThrowIfCancellationRequested();
        ContentException Error(string path, string message, Source? origin = null) => new(origin ?? source.Origin, path, message);
        void Text(string? text, string path, Source? origin = null)
        {
            if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > MaximumTextBytes)
                throw Error(path, "Expected nonempty text of at most 1024 UTF-8 bytes.", origin);
        }
        void Position(WorldPosition point, string path, Source? origin = null)
        {
            if (point.Elevation != 0 || point.X < 0 || point.Y < 0 ||
                point.X >= (long)source.Width * source.CellSize || point.Y >= (long)source.Height * source.CellSize)
                throw Error(path, "Expected an exact XY anchor inside the map. Elevation is derived from the surface.", origin);
        }
        Text(source.Id, "id");
        ValidateDimensions(source.Width, source.Height, source.CellSize, source.Origin);
        int cells = checked(source.Width * source.Height);
        if (source.Surface is { } surface)
        {
            if (surface.Count != cells) throw Error("surface", "The surface must cover every map cell.");
            for (int i = 0; i < cells; i++)
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                var cell = surface[i];
                if (cell.RiseX != 0 && cell.RiseY != 0 ||
                    2 * Math.Abs((long)cell.Elevation) + Math.Abs((long)cell.RiseX) + Math.Abs((long)cell.RiseY) > 2 * 1048576)
                    throw Error($"surface[{i}]", "Use a bounded elevation and a rise along at most one axis.");
            }
        }
        var locomotion = catalog.Locomotion ?? throw Error("terrain", "Maps require a terrain catalog.");
        MapLayer Normalize(MapLayer layer, string path)
        {
            if (layer.Cells.Count != cells) throw Error(path, "Layer must cover every map cell.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in layer.Palette)
            {
                Text(name, path + ".palette");
                if (!names.Add(name)) throw Error(path + ".palette", "Duplicate palette ID.");
            }
            var used = new bool[layer.Palette.Count];
            for (int i = 0; i < cells; i++)
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                int index = layer.Cells[i];
                if ((uint)index >= layer.Palette.Count) throw Error(path + $".cells[{i}]", "Unknown palette index.");
                used[index] = true;
            }
            var palette = layer.Palette.Where((_, i) => used[i]).Order(StringComparer.Ordinal).ToArray();
            var indices = palette.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index, StringComparer.Ordinal);
            var remap = layer.Palette.Select(name => indices.GetValueOrDefault(name, -1)).ToArray();
            var values = new int[cells];
            for (int i = 0; i < cells; i++)
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                values[i] = remap[layer.Cells[i]];
            }
            return new(palette, values);
        }
        // Validate all declared references, including unused palette entries.
        foreach (string id in source.Terrain.Palette)
            if (locomotion.Terrains.IndexOf(id) < 0) throw Error("terrain.palette", $"Unknown terrain '{id}'.");
        var terrain = Normalize(source.Terrain, "terrain");
        var visual = Normalize(source.Visual, "visual");
        var slots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in source.Slots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (slot is null) throw Error("slots", "Null slot.");
            Text(slot.Id, "slots.id");
            if (slot.Id == "neutral" || !slots.Add(slot.Id)) throw Error("slots", "Reserved or duplicate slot ID.");
            if (source.IsSkirmish)
            {
                if (slot.Position is not { } position) throw Error($"starts.{slot.Id}", "A start needs a position.");
                Position(position, $"starts.{slot.Id}.position");
            }
            else if (slot.Position.HasValue || slot.Reverse) throw Error($"slots.{slot.Id}", "Fixture slots have no start position or reverse flag.");
        }
        if (source.IsSkirmish && slots.Count == 0) throw Error("starts", "Skirmish needs at least one start.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obj in source.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (obj is null) throw Error("objects", "Null object.");
            string path = $"objects.{obj.Id}";
            Text(obj.Id, path, obj.Origin);
            Text(obj.Type, path + ".type", obj.Origin);
            Text(obj.Owner, path + ".owner", obj.Origin);
            if (!ids.Add(obj.Id)) throw Error(path, "Duplicate object ID.", obj.Origin);
            if (obj.Owner != "neutral" && (source.IsSkirmish || !slots.Contains(obj.Owner)))
                throw Error(path + ".owner", $"Owner '{obj.Owner}' is not allowed on this map.", obj.Origin);
            Position(obj.Position, path + ".position", obj.Origin);
        }
        var normalized = new MapSource(source.Id, source.IsSkirmish, source.Width, source.Height, terrain, visual,
            source.Slots.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray(), source.Objects, source.CellSize, source.Origin, source.Surface);
        var effective = catalog.PrepareMapUnits(normalized, cancellationToken);
        var result = new CompiledMap(normalized, effective, catalog.Summary.Fingerprint, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    internal static void ValidateDimensions(int width, int height, int cellSize, Source origin)
    {
        if (cellSize != WorldPosition.UnitsPerCell || width < 1 || height < 1 ||
            (long)width * height > NavigationGrid.MaximumCells ||
            width > WorldPosition.MaximumExtent / cellSize || height > WorldPosition.MaximumExtent / cellSize)
            throw new ContentException(origin, "size", "Expected bounded positive dimensions, at most 16 Mi cells, and cellSize 1024.");
    }

    internal static void ValidateDocument(ContentSource input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Text.Length > MaximumDocumentBytes || Encoding.UTF8.GetByteCount(input.Text) > MaximumDocumentBytes)
            throw new ContentException(new(input.Name, 1, 1), "map", "Map document exceeds 64 MiB.");
    }

    public static CompiledMap Read(ContentSource input, UnitCatalog catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        cancellationToken.ThrowIfCancellationRequested();
        var data = MapJson.ReadCompiled(input);
        if (data.Content != catalog.Summary.Fingerprint)
            throw new ContentException(new(input.Name, 1, 1), "contentFingerprint", "Compiled map requires different gameplay content.");
        var compiled = Compile(data.Source, catalog, cancellationToken);
        if (compiled.GameplayFingerprint != data.Gameplay || compiled.DataFingerprint != data.Data ||
            compiled.EffectiveContentFingerprint != data.EffectiveContent)
            throw new ContentException(new(input.Name, 1, 1), "fingerprint", "Compiled map data is damaged or its fingerprints do not match.");
        return compiled;
    }

    public static void Save(CompiledMap map, string path, CancellationToken cancellationToken = default)
    {
        string text = map.Write(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string target = Path.GetFullPath(path), temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
