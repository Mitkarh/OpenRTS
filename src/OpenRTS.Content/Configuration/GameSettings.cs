using System.ComponentModel.DataAnnotations;
using DeclarativeContent;

namespace OpenRTS.Content;

public sealed class SimulationSettings
{
    [Range(1, 1000)] public int TickRate { get; init; } = 25;
    [Range(1, 1024)] public int SpeedPerTick { get; init; } = 128;
}

public sealed class MatchSettings
{
    [MinLength(1)] public string DefaultMap { get; init; } = "content/maps/skirmish.yaml";
    [MinLength(1)] public string DefaultStartSet { get; init; } = "content/start-sets/supply.yaml";
    [MinLength(1)] public string AiConfig { get; init; } = "content/skirmish-ai.yaml";
    [MinLength(1)] public string AiProfile { get; init; } = "infantry-skirmish";
    public MatchChoice[] Maps { get; init; } = [];
    public MatchChoice[] StartingSets { get; init; } = [];
}

public sealed class MatchChoice
{
    [Required, MinLength(1)] public string Path { get; init; } = "";
    [Required, MinLength(1)] public string Title { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>Prepared settings. Runtime worlds receive typed values and never read YAML.</summary>
public sealed record GameSettings(SimulationSettings Simulation, MatchSettings Match)
{
    public SensorRangeSettings SensorRanges { get; init; } = new();
    public PresentationSettings Presentation { get; init; } = new();
    public static GameSettings Load(IEnumerable<ContentSource> sources)
    {
        var definitions = DefinitionSet.Load(sources);
        foreach (var (id, node) in definitions.All)
            if (id is not ("Simulation" or "Match" or "SensorRanges" or "Presentation"))
                throw node.Error(id, "Unknown settings section. Expected Simulation, Match, SensorRanges or Presentation.");
        T Read<T>(string id) where T : new() => definitions.All.TryGetValue(id, out var node) ? Options.Bind<T>(node, id) : new();
        var result = new GameSettings(Read<SimulationSettings>("Simulation"), Read<MatchSettings>("Match"))
        {
            SensorRanges = Read<SensorRangeSettings>("SensorRanges"),
            Presentation = Read<PresentationSettings>("Presentation")
        };
        foreach (string path in new[] { result.Match.DefaultMap, result.Match.DefaultStartSet, result.Match.AiConfig }
            .Concat(result.Match.Maps.Select(choice => choice.Path)).Concat(result.Match.StartingSets.Select(choice => choice.Path)))
        {
            try { ContentFiles.Normalize(path); }
            catch (ArgumentException error) { throw definitions.All["Match"].Error("Match", error.Message); }
        }
        foreach (var (choices, defaultPath) in new[] { (result.Match.Maps, result.Match.DefaultMap), (result.Match.StartingSets, result.Match.DefaultStartSet) })
        {
            if (choices.Select(choice => choice.Path).Distinct(StringComparer.Ordinal).Count() != choices.Length)
                throw definitions.All["Match"].Error("Match", "Duplicate choice path.");
            if (choices.Length != 0 && !choices.Any(choice => choice.Path == defaultPath))
                throw definitions.All["Match"].Error("Match", $"Default '{defaultPath}' must be present in its choice list.");
        }
        return result;
    }
}
