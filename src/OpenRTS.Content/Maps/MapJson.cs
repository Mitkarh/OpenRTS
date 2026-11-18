using System.Text;
using System.Text.Json;
using DeclarativeContent;
using OpenRTS.Core;

namespace OpenRTS.Content;

internal static class MapJson
{
    private const string SourceFormat = "openrts-map-source", CompiledFormat = "openrts-compiled-map";

    internal static string Write(MapSource source, bool gameplayOnly = false, CancellationToken stop = default) =>
        Encode(writer => { Header(writer, SourceFormat, source.FormatVersion); WriteMap(writer, source, gameplayOnly, stop); writer.WriteEndObject(); });

    internal static string Write(CompiledMap map, CancellationToken stop = default) => Encode(writer =>
    {
        int version = map.Source.FormatVersion;
        Header(writer, CompiledFormat, version);
        writer.WriteNumber("compilerVersion", version);
        writer.WriteString("contentFingerprint", map.ContentFingerprint);
        if (version == 2) writer.WriteString("effectiveContentFingerprint", map.EffectiveContentFingerprint);
        writer.WriteString("gameplayFingerprint", map.GameplayFingerprint);
        writer.WriteString("dataFingerprint", map.DataFingerprint);
        WriteMap(writer, map.Source, false, stop); writer.WriteEndObject();
    });

    private static string Encode(Action<Utf8JsonWriter> write)
    {
        using var stream = new LimitedBuffer();
        using (var writer = new Utf8JsonWriter(stream)) { write(writer); writer.Flush(); }
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private sealed class LimitedBuffer : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> bytes)
        {
            if (Length + bytes.Length > MapCompiler.MaximumDocumentBytes) throw new InvalidDataException("Map document exceeds 64 MiB.");
            base.Write(bytes);
        }
        public override void Write(byte[] bytes, int offset, int count)
        {
            if (Length + count > MapCompiler.MaximumDocumentBytes) throw new InvalidDataException("Map document exceeds 64 MiB.");
            base.Write(bytes, offset, count);
        }
    }

    private static void Header(Utf8JsonWriter writer, string format, int version)
    {
        writer.WriteStartObject(); writer.WriteString("format", format); writer.WriteNumber("version", version);
        writer.WriteString("geometry", MapCompiler.Geometry);
    }

    private static void WriteMap(Utf8JsonWriter writer, MapSource source, bool gameplayOnly, CancellationToken stop)
    {
        if (source.Objects.Any(o => o.Position.Elevation != 0) || source.Slots.Any(s =>
            s.Position is { Elevation: not 0 } || !s.Position.HasValue && s.Reverse))
            throw new ArgumentException("Map source cannot serialize unsupported elevation or a reverse flag without a start.");
        writer.WriteStartObject("map"); writer.WriteString("id", source.Id);
        writer.WriteString("kind", source.IsSkirmish ? "skirmish" : "fixture");
        writer.WriteStartArray("size"); writer.WriteNumberValue(source.Width); writer.WriteNumberValue(source.Height); writer.WriteEndArray();
        writer.WriteNumber("cellSize", source.CellSize);
        void Layer(string name, MapLayer layer)
        {
            writer.WriteStartObject(name); writer.WriteStartArray("palette");
            foreach (string id in layer.Palette) { stop.ThrowIfCancellationRequested(); writer.WriteStringValue(id); writer.Flush(); }
            writer.WriteEndArray(); writer.WriteStartArray("cells");
            for (int i = 0; i < layer.Cells.Count; i++)
            {
                if ((i & 1023) == 0) { stop.ThrowIfCancellationRequested(); writer.Flush(); }
                writer.WriteNumberValue(layer.Cells[i]);
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        Layer("terrain", source.Terrain);
        if (!gameplayOnly) Layer("visual", source.Visual);
        writer.WriteStartArray("slots");
        foreach (var slot in source.Slots)
        {
            stop.ThrowIfCancellationRequested(); writer.WriteStartObject(); writer.WriteString("id", slot.Id);
            if (slot.Position is { } position)
            {
                writer.WriteStartArray("position"); writer.WriteNumberValue(position.X); writer.WriteNumberValue(position.Y); writer.WriteEndArray();
                writer.WriteBoolean("reverse", slot.Reverse);
            }
            writer.WriteEndObject(); writer.Flush();
        }
        writer.WriteEndArray(); writer.WriteStartArray("objects");
        foreach (var obj in source.Objects)
        {
            stop.ThrowIfCancellationRequested(); writer.WriteStartObject(); writer.WriteString("id", obj.Id); writer.WriteString("type", obj.Type);
            writer.WriteStartArray("position"); writer.WriteNumberValue(obj.Position.X); writer.WriteNumberValue(obj.Position.Y); writer.WriteEndArray();
            writer.WriteString("owner", obj.Owner);
            if (obj.QuarterTurns != 0) writer.WriteNumber("quarterTurns", obj.QuarterTurns);
            if (obj.InitialHealth is { } hp) writer.WriteNumber("initialHealth", hp);
            if (obj.HealthVariant is { } variant)
            {
                writer.WriteStartObject("healthVariant");
                writer.WriteString("id", variant.Id); writer.WriteNumber("maxHealth", variant.MaxHealth);
                writer.WriteEndObject();
            }
            writer.WriteEndObject(); writer.Flush();
        }
        writer.WriteEndArray(); writer.WriteEndObject();
    }

    internal static MapSource ReadSource(ContentSource source) => Parse(source, false).Source;
    internal static (MapSource Source, string Content, string Gameplay, string Data, string EffectiveContent) ReadCompiled(ContentSource source) => Parse(source, true);

    private static (MapSource Source, string Content, string Gameplay, string Data, string EffectiveContent) Parse(ContentSource source, bool compiled)
    {
        ContentException Error(string path, string message) => new(new(source.Name, 1, 1), path, message);
        MapCompiler.ValidateDocument(source);
        try
        {
            using var document = JsonDocument.Parse(source.Text, new JsonDocumentOptions { MaxDepth = 16 });
            Dictionary<string, JsonElement> Fields(JsonElement node, string path, params string[] allowed)
            {
                if (node.ValueKind != JsonValueKind.Object) throw Error(path, "Expected an object.");
                var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var field in node.EnumerateObject())
                    if (!allowed.Contains(field.Name) || !fields.TryAdd(field.Name, field.Value))
                        throw Error(path, $"Unknown or duplicate field '{field.Name}'.");
                return fields;
            }
            JsonElement Required(Dictionary<string, JsonElement> fields, string name) =>
                fields.TryGetValue(name, out var field) ? field : throw Error(name, "Missing field.");
            string Text(JsonElement field, string path)
            {
                if (field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString()) ||
                    Encoding.UTF8.GetByteCount(field.GetString()!) > MapCompiler.MaximumTextBytes)
                    throw Error(path, "Expected nonempty text of at most 1024 UTF-8 bytes.");
                return field.GetString()!;
            }
            int Number(JsonElement field, string path)
            {
                if (!field.TryGetInt32(out int value)) throw Error(path, "Expected an Int32 integer.");
                return value;
            }
            JsonElement Array(JsonElement field, string path, int maximum)
            {
                if (field.ValueKind != JsonValueKind.Array || field.GetArrayLength() > maximum) throw Error(path, "Invalid array or count limit.");
                return field;
            }
            WorldPosition Pair(JsonElement field, string path)
            {
                var array = Array(field, path, 2);
                if (array.GetArrayLength() != 2) throw Error(path, "Expected exactly two coordinates.");
                return new(Number(array[0], path), Number(array[1], path));
            }
            var root = Fields(document.RootElement, "JSON", compiled
                ? ["format", "version", "geometry", "compilerVersion", "contentFingerprint", "effectiveContentFingerprint", "gameplayFingerprint", "dataFingerprint", "map"]
                : ["format", "version", "geometry", "map"]);
            int version = Number(Required(root, "version"), "version");
            if (Text(Required(root, "format"), "format") != (compiled ? CompiledFormat : SourceFormat) ||
                version is not (1 or 2) || Text(Required(root, "geometry"), "geometry") != MapCompiler.Geometry)
                throw Error("format", "Unsupported map format, version or geometry.");
            string content = "", gameplay = "", data = "", effectiveContent = "";
            if (compiled)
            {
                if (Number(Required(root, "compilerVersion"), "compilerVersion") != version) throw Error("compilerVersion", "Unsupported compiler version.");
                string Fingerprint(string name)
                {
                    string hash = Text(Required(root, name), name);
                    if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw Error(name, "Expected a SHA-256 digest.");
                    return hash.ToUpperInvariant();
                }
                if (version == 1 && root.ContainsKey("effectiveContentFingerprint"))
                    throw Error("effectiveContentFingerprint", "Effective content fingerprint requires version 2.");
                content = Fingerprint("contentFingerprint");
                effectiveContent = version == 2 ? Fingerprint("effectiveContentFingerprint") : content;
                gameplay = Fingerprint("gameplayFingerprint"); data = Fingerprint("dataFingerprint");
            }
            var map = Fields(Required(root, "map"), "map", "id", "kind", "size", "cellSize", "terrain", "visual", "slots", "objects");
            var size = Pair(Required(map, "size"), "size");
            int cellSize = Number(Required(map, "cellSize"), "cellSize");
            MapCompiler.ValidateDimensions(size.X, size.Y, cellSize, new(source.Name, 1, 1));
            int count = checked(size.X * size.Y);
            MapLayer Layer(string name)
            {
                var layer = Fields(Required(map, name), name, "palette", "cells");
                var palette = Array(Required(layer, "palette"), name + ".palette", MapCompiler.MaximumPaletteEntries);
                var cells = Array(Required(layer, "cells"), name + ".cells", count);
                if (cells.GetArrayLength() != count) throw Error(name, "Layer must cover every map cell.");
                return new(palette.EnumerateArray().Select(p => Text(p, name + ".palette")).ToArray(),
                    cells.EnumerateArray().Select(p => Number(p, name + ".cells")).ToArray());
            }
            bool skirmish = Text(Required(map, "kind"), "kind") switch
            {
                "skirmish" => true, "fixture" => false, _ => throw Error("kind", "Expected skirmish or fixture.")
            };
            var slots = Array(Required(map, "slots"), "slots", MapCompiler.MaximumSlots).EnumerateArray().Select(node =>
            {
                var slot = Fields(node, "slots", "id", "position", "reverse");
                bool reverse = slot.TryGetValue("reverse", out var flag) && flag.GetBoolean();
                return new MapSlotSource(Text(Required(slot, "id"), "slots.id"),
                    slot.TryGetValue("position", out var position) ? Pair(position, "slots.position") : null, reverse);
            }).ToArray();
            var objects = Array(Required(map, "objects"), "objects", MapCompiler.MaximumObjects).EnumerateArray().Select(node =>
            {
                var obj = Fields(node, "objects", version == 2
                    ? ["id", "type", "position", "owner", "quarterTurns", "initialHealth", "healthVariant"]
                    : ["id", "type", "position", "owner"]);
                MapHealthVariant? variant = null;
                if (obj.TryGetValue("healthVariant", out var healthVariant))
                {
                    var fields = Fields(healthVariant, "objects.healthVariant", "id", "maxHealth");
                    variant = new(Text(Required(fields, "id"), "healthVariant.id"), Number(Required(fields, "maxHealth"), "healthVariant.maxHealth"));
                }
                return new MapObjectSource(Text(Required(obj, "id"), "objects.id"), Text(Required(obj, "type"), "objects.type"),
                    Pair(Required(obj, "position"), "objects.position"), Text(Required(obj, "owner"), "objects.owner"),
                    QuarterTurns: obj.TryGetValue("quarterTurns", out var rotation) ? Number(rotation, "objects.quarterTurns") : 0,
                    InitialHealth: obj.TryGetValue("initialHealth", out var hp) ? Number(hp, "objects.initialHealth") : null,
                    HealthVariant: variant);
            }).ToArray();
            var result = new MapSource(Text(Required(map, "id"), "id"), skirmish, size.X, size.Y, Layer("terrain"), Layer("visual"),
                slots, objects, cellSize, new(source.Name, 1, 1));
            if (result.FormatVersion != version) throw Error("version", "Map version must match its initializers.");
            return (result, content, gameplay, data, effectiveContent);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or OverflowException)
        { throw Error("JSON", error.Message); }
    }
}
