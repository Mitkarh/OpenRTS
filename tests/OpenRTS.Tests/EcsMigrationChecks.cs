using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;
using static NavigationChecks;

internal static class EcsMigrationChecks
{
    public static void Run(Action<string, Action> test)
    {
        foreach (var scenario in new[] { "direct", "navigation", "combat", "buildings", "production" })
            test("ECS baseline: " + scenario, () =>
            {
                var units = UnitCatalog.Load([new("units", LegacyContent("sandbox/content/units.yaml"))], LocomotionChecks.Catalog(), LocomotionChecks.Weapons());
                var buildings = UnitCatalog.Load([new("units", LegacyContent("sandbox/content/units.yaml")),
                    new("buildings", LegacyContent("sandbox/content/buildings.yaml"))], LocomotionChecks.Catalog(), LocomotionChecks.Weapons());
                using var world = scenario switch
                {
                    "direct" => DiagnosticScenario.Create(units, 100),
                    "navigation" => NavigationScenario.Create(units, 100, 1),
                    "combat" => ShowcaseScenario.Create(units),
                    _ => BuildingScenario.Create(buildings)
                };
                int ticks = scenario == "production" ? 160 : scenario == "navigation" ? 32 : 24;
                WorldCommand[] commands = scenario switch
                {
                    "direct" => DiagnosticScenario.Commands(100, ticks),
                    "navigation" => NavigationScenario.Commands(100, ticks),
                    "combat" => ShowcaseScenario.VerificationCommands(units),
                    "production" => ProductionScenario.Commands(buildings),
                    _ => BuildingScenario.Commands(buildings)
                };
                using var trace = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var frames = new FrameExchange();
                for (int tick = 0; tick <= ticks; tick++)
                {
                    if (tick != 0) world.Step(commands.Where(c => c.Tick == tick).ToArray());
                    frames.TryPublish(world);
                    using var frame = frames.AcquireLatest()!;
                    trace.AppendData(JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        State = world.Report(), Commands = world.AppliedCommands.ToArray(), Events = world.Events.ToArray(),
                        Entities = frame.Poses.ToArray(), Weapons = frame.Weapons.ToArray(),
                        Projectiles = frame.Projectiles.ToArray(), Tasks = frame.Tasks.ToArray(), Players = frame.Players.ToArray(), Projects = frame.ConstructionProjects.ToArray()
                    }, TraceOptions));
                }
                string actual = Convert.ToHexString(trace.GetHashAndReset());
                NavigationChecks.Check(actual == Expected[scenario], $"{scenario} trace changed: {actual}");
            });
    }

    // Preserve the pre-observer mechanical baseline; fog, sensors and radar construction have separate checks.
    private static string LegacyContent(string path)
    {
        var result = new System.Text.StringBuilder();
        int skippedIndent = -1;
        foreach (string line in File.ReadLines(path))
        {
            string content = line.TrimStart(' ');
            int indent = line.Length - content.Length;
            if (skippedIndent >= 0 && (content.Length == 0 || indent > skippedIndent)) continue;
            skippedIndent = -1;
            if (indent == 2 && (content.StartsWith("Vision:") || content.StartsWith("Sensors:")) ||
                indent == 6 && (content.StartsWith("site-radar:") || content.StartsWith("site-stealth:") || content.StartsWith("stealth-truck:") || content.StartsWith("radar-buggy:"))) { skippedIndent = indent; continue; }
            result.AppendLine(line);
        }
        var text = System.Text.RegularExpressions.Regex.Replace(result.ToString(),
            @"(?ms)^(stealth-truck|radar-buggy|stealth-generator):\n.*?(?=^[a-zA-Z^]|\z)", "");
        return text.Replace("main: { Weapon: rifle, Animation: fire }", "main: { Weapon: cannon, Animation: fire }");
    }

    // Mechanics format 33: paid projects and suspended clearance orders; producer exit lanes forbid stopping.
    private static readonly Dictionary<string, string> Expected = new()
    {
        ["direct"] = "BC64411A3027697D47A269DC4FC0ACC33C16DF13FE4939626ABE45954223C699",
        ["navigation"] = "3761CA22F5F87D8D88B5F927348B3761BCCD4465F2EA070D23461B7110BFA57B",
        ["combat"] = "5C8B30FF1CF6F931184ECEE41CDD16B3F3BDF3E175427E80322AC59C3FA67E52",
        ["buildings"] = "A78761E08AD173E62B23C5ADB35EEE53A14248DFE9B15F42DFED97EC1427A373",
        ["production"] = "9F9327A08D92CF9B48F299938FFD1FA3F339C457190CBCBB34A5F540E0B2D0B8"
    };

    internal static readonly JsonSerializerOptions TraceOptions = new() { Converters = { new EntitySetWriter() } };
    private sealed class EntitySetWriter : JsonConverter<EntitySet>
    {
        public override EntitySet Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, EntitySet value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.Ids.ToArray(), options);
    }
}
