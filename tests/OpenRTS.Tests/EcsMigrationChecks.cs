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

    // Mechanics format 35 includes canonical work flight state.
    // Cancellation state is canonical; root-sprite picking and work headings remain adapter-only.
    private static readonly Dictionary<string, string> Expected = new()
    {
        ["direct"] = "6DC4AEA0C7FCA10A2E455924B88F020F936165D2D9FA8ACC818459F0269077BD",
        ["navigation"] = "C86BCFA878DB1AD80D74DF09BFFF2081F20F72C0DB604529CBA2E05EA49B5A68",
        ["combat"] = "F65A0E4EDB6CB3EE9C39F2107164CCEC813F6B9ACFECF60591B81D562D58C25F",
        ["buildings"] = "D871A5246CDCD5E50E6FD4F90A02C8D358A97E7D6165B070F3EFA111E0585E5A",
        ["production"] = "DA8D7AC48346CFC17F631E44C0414913A5FDD38D9550C2080C5FFCEC0C7A817E"
    };

    internal static readonly JsonSerializerOptions TraceOptions = new() { Converters = { new EntitySetWriter() } };
    private sealed class EntitySetWriter : JsonConverter<EntitySet>
    {
        public override EntitySet Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, EntitySet value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.Ids.ToArray(), options);
    }
}
