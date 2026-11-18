using System.Text.Json;
using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Network;

try
{
    string? journal = null, mod = null, mapPath = null, setPath = null;
    foreach (string arg in args)
    {
        if (arg.StartsWith("--mod=", StringComparison.Ordinal) && mod is null) mod = arg[6..];
        else if (arg.StartsWith("--map=", StringComparison.Ordinal) && mapPath is null) mapPath = arg[6..];
        else if (arg.StartsWith("--start-set=", StringComparison.Ordinal) && setPath is null) setPath = arg[12..];
        else if (!arg.StartsWith("--", StringComparison.Ordinal) && journal is null) journal = arg;
        else throw new ArgumentException("Usage: tools/dev.sh replay journal [--mod=sandbox/game.yaml] [--map=path] [--start-set=path]");
    }
    if (journal is null) throw new ArgumentException("Specify a .journal file to verify.");
    var manifest = GameManifest.Load(mod ?? "sandbox/game.yaml");
    var catalog = manifest.LoadUnits();
    ContentSource Read(string file) => Path.IsPathRooted(file)
        ? new(file, File.ReadAllText(file))
        : manifest.Files.Read(file.StartsWith("res://", StringComparison.Ordinal) ? file[6..] : file);
    GameMap LoadMap(string file)
    {
        var input = Read(file);
        return SceneMapBundle.IsPath(file) ? SceneMapBundle.Read(input).LoadMap(file, Read, catalog).Map : GameMap.Load(input, catalog);
    }
    using var history = File.OpenRead(journal);
    var report = StarJournal.Replay(history, info =>
    {
        var options = MatchSetupOptions.ReadReplay(info, catalog.Summary.Fingerprint);
        var settings = manifest.Settings.Match;
        GameMap? map = mapPath is null ? null : LoadMap(mapPath);
        if (map is null)
        {
            foreach (string candidate in settings.Maps.Select(m => m.Path).Prepend(settings.DefaultMap).Distinct(StringComparer.Ordinal))
            {
                GameMap installed;
                try { installed = LoadMap(candidate); }
                catch (Exception e) when (e is IOException or InvalidDataException or ContentException or JsonException) { continue; }
                if (installed.Id != options.Map || options.MapFingerprint is not null && installed.GameplayFingerprint != options.MapFingerprint) continue;
                map = installed;
                break;
            }
        }
        if (map is null) throw new InvalidDataException($"Map '{options.Map}' with gameplay fingerprint {options.MapFingerprint} is unavailable. Supply --map.");
        StartingSet? set = setPath is not null ? StartingSet.Load(Read(setPath)) : null;
        if (set is null && options.Mode.Length != 0)
        {
            set = settings.StartingSets.Select(m => m.Path).Prepend(settings.DefaultStartSet).Distinct(StringComparer.Ordinal)
                .Select(path => StartingSet.Load(Read(path)))
                .FirstOrDefault(candidate => candidate.Id == options.Mode &&
                    (options.StartingSetFingerprint is null || candidate.Fingerprint == options.StartingSetFingerprint));
            if (set is null) throw new InvalidDataException($"Starting set '{options.Mode}' with fingerprint {options.StartingSetFingerprint} is unavailable. Supply --start-set.");
        }
        var match = options.Prepare(map, set);
        if (match.Seed != info.Descriptor.Seed) throw new InvalidDataException("Replay seed does not match its map factory.");
        return options.CreateWorld(match, workers: 1, restrict: true);
    });
    Console.WriteLine("OPENRTS_REPLAY:" + JsonSerializer.Serialize(report));
    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or ContentException or JsonException or OpenRTS.Network.DesyncException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
