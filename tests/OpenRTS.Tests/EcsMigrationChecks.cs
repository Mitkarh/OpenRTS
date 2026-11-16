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

    // Mechanics format 33, extended air support definitions and 6-slot vehicle passengers.
    // Ground mechanics are unchanged; catalog recipes, type ordinals and passenger components change the traces.
    private static readonly Dictionary<string, string> Expected = new()
    {
        ["direct"] = "B09FDD6CD85DCBFA45B8B6198C61E18E25A2D769C0F741928F7CB01022CC9732",
        ["navigation"] = "1B7CED67EE3FA1B9ADCC8FBCE1618C33A8E4C83AA3C6310B3B3251B639074578",
        ["combat"] = "DBFD2F7236195A4E89E09E5F30DB2D1DB6935F453323A0D23676EBCE862BFF59",
        ["buildings"] = "62E44FE29F62091FD28600DCA103C6009F9A80F3B0FC12974D9054C023A78833",
        ["production"] = "B875D7E99DDBE1E1BA883BE8A3ED75F7CD4F67AA1034AEB3AE1083C67B6C1DF9"
    };

    internal static readonly JsonSerializerOptions TraceOptions = new() { Converters = { new EntitySetWriter() } };
    private sealed class EntitySetWriter : JsonConverter<EntitySet>
    {
        public override EntitySet Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, EntitySet value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.Ids.ToArray(), options);
    }
}
