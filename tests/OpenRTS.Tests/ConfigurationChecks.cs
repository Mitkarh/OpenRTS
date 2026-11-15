using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;
using static NavigationChecks;

internal static class ConfigurationChecks
{
    private static void Check(bool value, string message) => NavigationChecks.Check(value, message);

    public static void Run(Action<string, Action> test)
    {
        test("Configuration: recursive directories have stable order, deduplicate paths and share inheritance", () =>
        {
            InDirectory((root, write) =>
            {
                write("rules/z/tank.yml", "tank: {Inherits: ^unit, Health: {HP: 200}}\n");
                write("rules/a.yaml", "^unit: {Health: {HP: 100}}\nscout: {Inherits: ^unit}\n");
                write("rules/notes.txt", "not yaml");
                write("game.yaml", "Version: 1\nTitle: Test\nRules: [rules, rules/./a.yaml]\n");
                var manifest = GameManifest.Load(ContentFiles.FromDirectory(root));
                Check(manifest.Rules.Select(source => source.Name).SequenceEqual(["rules/a.yaml", "rules/z/tank.yml"]), "Directory order or deduplication");
                var expected = UnitCatalog.Load([new("combined.yaml", "^unit: {Health: {HP: 100}}\nscout: {Inherits: ^unit}\ntank: {Inherits: ^unit, Health: {HP: 200}}\n")]);
                Check(manifest.LoadUnits().Summary == expected.Summary, "Moving definitions across directories changed gameplay");
                write("rules/z/duplicate.yaml", "scout: {Health: {HP: 1}}\n");
                ExpectContent(() => GameManifest.Load(Path.Combine(root, "game.yaml")).LoadUnits(), "rules/z/duplicate.yaml");
            });
        });
        test("Configuration: virtual filesystem enumeration order does not affect expansion", () =>
        {
            var files = new ContentFiles(path => path, path => path switch
            {
                "rules" => ["rules/z.yaml", "rules/nested", "rules/a.yml", "rules/image.png"],
                "rules/nested" => ["rules/nested/b.yaml"],
                _ => null
            });
            Check(files.Expand(["rules", "rules/a.yml"]).Select(source => source.Name)
                .SequenceEqual(["rules/a.yml", "rules/nested/b.yaml", "rules/z.yaml"]), "Virtual filesystem order leaked");
        });
        test("Configuration: manifest errors keep source context", () =>
        {
            InDirectory((root, write) =>
            {
                write("unit.yaml", "unit: {Health: {HP: 1}}\n");
                Directory.CreateDirectory(Path.Combine(root, "empty"));
                foreach (string text in new[]
                {
                    "Version: 2\nTitle: Test\nRules: [unit.yaml]",
                    "Version: 1\nTitle: Test\nRulse: [unit.yaml]",
                    "Version: 1\nTitle: Test\nRules: unit.yaml",
                    "Version: 1\nTitle: Test\nRules: []",
                    "Version: 1\nTitle: Test\nRules: [missing.yaml]",
                    "Version: 1\nTitle: Test\nRules: [empty]",
                    "Version: 1\nTitle: Test\nRules: [../outside.yaml]",
                    "Version: 1\nTitle: Test\nRules: &files [unit.yaml]"
                })
                {
                    write("game.yaml", text);
                    ExpectContent(() => GameManifest.Load(Path.Combine(root, "game.yaml")), "game.yaml:");
                }
            });
        });
        test("Configuration: settings reject invalid fields, ranges and menu defaults", () =>
        {
            foreach (string text in new[]
            {
                "Simulation: {TickRate: 0}", "Simulation: {SpeedPerTick: 1025}",
                "Simulation: {Tickrate: 25}", "Unknown: {}", "Match: {DefaultMap: ../outside.yaml}",
                "Match: {Maps: [{Path: a.yaml}]}",
                "Match: {Maps: [{Path: a.yaml, Title: A}]}",
                "Match: {DefaultMap: a.yaml, Maps: [{Path: a.yaml, Title: A}, {Path: a.yaml, Title: B}]}"
            }) ExpectContent(() => GameSettings.Load([new("settings.yaml", text)]), "settings.yaml:");
        });
        test("Configuration: sandbox manifests preserve catalogs and feed map world settings", () =>
        {
            var manifest = GameManifest.Load("sandbox/game.yaml");
            var catalog = manifest.LoadUnits();
            var files = manifest.Files;
            var old = UnitCatalog.Load([files.Read("content/units.yaml"), files.Read("content/buildings.yaml")],
                LocomotionCatalog.Load([files.Read("content/terrain.yaml")]), WeaponCatalog.Load([files.Read("content/weapons.yaml")]));
            Check(catalog.Summary == old.Summary, "Manifest changed the full catalog");
            var diagnostic = GameManifest.Load("sandbox/diagnostic-game.yaml");
            Check(diagnostic.LoadUnits().Summary == UnitCatalog.Load([files.Read("content/units.yaml")], old.Locomotion,
                WeaponCatalog.Load([files.Read("content/weapons.yaml")])).Summary, "Diagnostic catalog changed");
            _ = ModelCatalog.Load(manifest.Models);
            _ = EffectCatalog.Load(manifest.Effects);
            var match = MapScenario.Load(catalog, files.Read, manifest.Settings.Match.DefaultMap,
                defaultStartSetPath: manifest.Settings.Match.DefaultStartSet);
            var settings = GameSettings.Load([new("custom.yaml", "Simulation: {TickRate: 40, SpeedPerTick: 64}")]).Simulation;
            using var world = match.CreateWorld(new(), speedPerTick: settings.SpeedPerTick, tickRate: settings.TickRate);
            using var original = match.CreateWorld();
            Check(world.TickRate == 40 && world.SpeedPerTick == 64 && original.TickRate == 25 && original.SpeedPerTick == 128, "Settings did not reach world");
            Check(world.Report().StateHash != original.Report().StateHash, "World settings are absent from state hash");
            var fixedMatch = MapScenario.Load(catalog, files.Read, "content/maps/fixture.yaml", defaultStartSetPath: "unused.yaml");
            Check(fixedMatch.UnitCount > 0, "Default starting set was forced on a fixed map");
        });
    }

    private static void ExpectContent(Action action, string context)
    {
        try { action(); }
        catch (ContentException error)
        {
            Check(error.Message.Contains(context, StringComparison.Ordinal), "Missing diagnostic context: " + error.Message);
            return;
        }
        throw new Exception("Expected ContentException: " + context);
    }

    private static void InDirectory(Action<string, Action<string, string>> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "openrts-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            action(root, (path, text) =>
            {
                string full = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            });
        }
        finally { Directory.Delete(root, true); }
    }
}
