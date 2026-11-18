using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;

namespace OpenRTS.Samples;

/// <summary>Local editor-to-sandbox launch arguments. All referenced inputs must still match the validated match.</summary>
internal sealed record LocalMapPlaytest(string MapFingerprint, string CatalogFingerprint, string[] Slots,
    string? StartSetPath, string? StartSetFingerprint, int TickRate, int SpeedPerTick)
{
    internal string Write() => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this));

    internal static LocalMapPlaytest Read(string argument)
    {
        if (argument.Length > 32 * 1024) throw new ArgumentException("Local playtest settings are too large.");
        var result = JsonSerializer.Deserialize<LocalMapPlaytest>(Convert.FromBase64String(argument));
        if (result is null || result.Slots is null || result.Slots.Length > MapScenario.MaximumPlayers ||
            result.Slots.Any(string.IsNullOrWhiteSpace) || (result.StartSetPath is null) != (result.StartSetFingerprint is null))
            throw new ArgumentException("Invalid local playtest settings.");
        return result;
    }

    internal static LocalMapPlaytest Validate(CompiledMap compiled, GameManifest manifest,
        IReadOnlyList<string>? selectedSlots, string? selectedSet)
    {
        var map = compiled.Map;
        if (!map.IsSkirmish && (selectedSet is not null || selectedSlots is not null))
            throw new ArgumentException("Fixture maps use their fixed placements and slots.");
        if (map.IsSkirmish && selectedSlots is { Count: 0 })
            throw new ArgumentException("Select at least one start for the playtest.");
        string? path = map.IsSkirmish ? selectedSet ?? manifest.Settings.Match.DefaultStartSet : null;
        var source = path is null ? null : manifest.Files.Read(path);
        var simulation = manifest.Settings.Simulation;
        var result = new LocalMapPlaytest(compiled.DataFingerprint, compiled.ContentFingerprint,
            (selectedSlots ?? map.Slots).ToArray(), path, source is null ? null : Fingerprint(source),
            simulation.TickRate, simulation.SpeedPerTick);
        var match = MapScenario.Prepare(map, source is null ? null : StartingSet.Load(source), result.Slots);
        using var world = match.CreateWorld(new(), speedPerTick: result.SpeedPerTick, tickRate: result.TickRate);
        return result;
    }

    internal void CheckMap(CompiledMap map)
    {
        if (map.DataFingerprint != MapFingerprint || map.ContentFingerprint != CatalogFingerprint)
            throw new InvalidDataException("The validated playtest map or content has changed. Rebuild it in the editor.");
    }

    internal MapMatch Prepare(GameMap map, GameManifest manifest) =>
        MapScenario.Prepare(map, ReadStartingSet(manifest), Slots);

    internal MapMatch Load(string path, Func<string, ContentSource> read, UnitCatalog catalog, GameManifest manifest)
    {
        var source = read(path);
        var compiled = SceneMapBundle.IsPath(path)
            ? SceneMapBundle.Read(source).LoadMap(path, read, catalog)
            : MapCompiler.Read(source, catalog);
        CheckMap(compiled);
        return Prepare(compiled.Map, manifest);
    }

    internal StartingSet? ReadStartingSet(GameManifest manifest)
    {
        var simulation = manifest.Settings.Simulation;
        if (simulation.TickRate != TickRate || simulation.SpeedPerTick != SpeedPerTick)
            throw new InvalidDataException("The playtest simulation settings have changed. Rebuild it in the editor.");
        if (StartSetPath is null) return null;
        var source = manifest.Files.Read(StartSetPath);
        if (Fingerprint(source) != StartSetFingerprint)
            throw new InvalidDataException("The validated starting set has changed. Rebuild it in the editor.");
        return StartingSet.Load(source);
    }

    private static string Fingerprint(ContentSource source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Text)));
}
