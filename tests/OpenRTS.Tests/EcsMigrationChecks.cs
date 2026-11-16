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
                var units = UnitCatalog.Load([new("units", LegacyContent("sandbox/content/units/units.yaml"))], LocomotionChecks.Catalog(), LocomotionChecks.Weapons());
                var buildings = UnitCatalog.Load([new("units", LegacyContent("sandbox/content/units/units.yaml")),
                    new("buildings", LegacyContent("sandbox/content/units/buildings.yaml"))], LocomotionChecks.Catalog(), LocomotionChecks.Weapons());
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

    // Mechanics format 34 includes carrier/order revisions for automatic air boarding.
    // Cancellation state is canonical; root-sprite picking and work headings remain adapter-only.
    private static readonly Dictionary<string, string> Expected = new()
    {
        ["direct"] = "A01C791FC0A327F7B768878B71F988B4C7ACC73F598FD6DD99EB7A001C2A9094",
        ["navigation"] = "A357B233A187257A8F805023D87E11CDBFB237B9BFC891E6A90236765362E16C",
        ["combat"] = "2C91518D2024CB854E07870BA280888B606DF73EAB123331659B4CF2114B0305",
        ["buildings"] = "8027C0EBEDBB02237285AEBDE3CAF35BC4FD4E2A7D838F2128A4E44D2874DA73",
        ["production"] = "8066499158EDD8BCC59A879FFCB73F49E945AA7FBB404CAD9F0C59BC4CB59857"
    };

    internal static readonly JsonSerializerOptions TraceOptions = new() { Converters = { new EntitySetWriter() } };
    private sealed class EntitySetWriter : JsonConverter<EntitySet>
    {
        public override EntitySet Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, EntitySet value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.Ids.ToArray(), options);
    }
}
