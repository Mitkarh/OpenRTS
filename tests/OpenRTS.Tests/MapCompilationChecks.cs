using System.Text.Json.Nodes;
using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;

internal static class MapCompilationChecks
{
    public static void Run(Action<string, Action> test)
    {
        var catalog = GameManifest.Load("sandbox/game.yaml").LoadUnits();
        static ContentSource Read(string name) => new(name, File.ReadAllText("sandbox/" + name));
        var source = MapSource.FromM1(Read("content/maps/fixture.yaml"), catalog);
        var players = new Dictionary<string, PlayerDefinition> { ["blue"] = new(1, "Blue"), ["red"] = new(2, "Red") };
        static MapSource Copy(MapSource s, MapLayer? terrain = null, MapLayer? visual = null,
            IReadOnlyList<MapSlotSource>? slots = null, IReadOnlyList<MapObjectSource>? objects = null) =>
            new(s.Id, s.IsSkirmish, s.Width, s.Height, terrain ?? s.Terrain, visual ?? s.Visual,
                slots ?? s.Slots, objects ?? s.Objects, s.CellSize);

        test("Maps compiled: independent input and artifact preserve exact world and every journal hash", () =>
        {
            var baseline = JsonNode.Parse(File.ReadAllText("docs/verification/047-map-compilation/baseline.json"))!;
            NavigationChecks.Check(baseline["catalog"]!["Fingerprint"]!.GetValue<string>() == catalog.Summary.Fingerprint, "M1 baseline content changed; explicitly verify and rebase the compatibility trace.");
            foreach (string path in new[] { "fixture", "skirmish", "crossroads", "riverside-station" })
            {
                var hashes = baseline["entries"]!.AsArray().Single(e => e!["name"]!.GetValue<string>() == path)!["hashes"]!.AsArray();
                var input = Read($"content/maps/{path}.yaml");
                var legacy = GameMap.Load(input, catalog);
                var compiled = MapCompiler.Compile(MapSource.Read(new("source.mapsource.json", MapSource.FromM1(input, catalog).Write())), catalog);
                var loaded = GameMap.Load(new("test.ortmap.json", compiled.Write()), catalog);
                var assignments = legacy.Slots.Select((id, i) => (id, player: new PlayerDefinition((uint)i + 1, "Player " + (i + 1))))
                    .ToDictionary(p => p.id, p => p.player);
                var set = legacy.IsSkirmish ? StartingSet.Load(Read(MapScenario.DefaultStartSet)) : null;
                using var first = legacy.Prepare(assignments, set, 77).CreateWorld(1);
                using var second = loaded.Prepare(assignments.Reverse().ToDictionary(), set, 77).CreateWorld(4);
                for (int tick = 0; tick <= 32; tick++)
                {
                    NavigationChecks.Check(first.Report() == second.Report(), $"Compiled map diverged at {path}/{tick}.");
                    NavigationChecks.Check(first.Report().StateHash == hashes[tick]!.GetValue<string>(), $"Original M1 compatibility changed at {path}/{tick}.");
                    if (tick == 32) break;
                    WorldCommand[] commands = tick == 0 ? [new(1, 0, 1, CommandKind.MoveAll, Position: new(12288, 6144))] : [];
                    first.Step(commands); second.Step(commands);
                }
                NavigationChecks.Check(loaded.Grid.Width == legacy.Grid.Width && loaded.VisualIds.SequenceEqual(legacy.VisualIds), "Map layers changed.");
                for (int y = 0; y < legacy.Grid.Height; y++) for (int x = 0; x < legacy.Grid.Width; x++)
                    NavigationChecks.Check(legacy.TerrainAt(x, y) == loaded.TerrainAt(x, y) && legacy.VisualAt(x, y) == loaded.VisualAt(x, y), "Cell data changed.");
            }
        });

        test("Maps compiled: canonical palettes and slots ignore authoring layout but retain placement order", () =>
        {
            var compiled = MapCompiler.Compile(source, catalog);
            static MapLayer Reverse(MapLayer layer) => new(layer.Palette.Reverse().ToArray(), layer.Cells.Select(i => layer.Palette.Count - 1 - i).ToArray());
            var equivalent = MapCompiler.Compile(Copy(source, Reverse(source.Terrain), Reverse(source.Visual), source.Slots.Reverse().ToArray()), catalog);
            NavigationChecks.Check(compiled.Write() == equivalent.Write(), "Palette or slot list order changed canonical output.");
            var reordered = MapCompiler.Compile(Copy(source, objects: source.Objects.Reverse().ToArray()), catalog);
            NavigationChecks.Check(compiled.GameplayFingerprint != reordered.GameplayFingerprint, "Object order was erased from identity.");
            using var first = compiled.Map.Prepare(players).CreateWorld();
            using var second = reordered.Map.Prepare(players).CreateWorld();
            NavigationChecks.Check(first.Report().StateHash != second.Report().StateHash, "The test did not exercise spawn order.");
            NavigationChecks.Check(MapCompiler.Compile(MapSource.Read(new("formatted", JsonNode.Parse(source.Write())!.ToJsonString(new() { WriteIndented = true }))), catalog).Write() == compiled.Write(), "Formatting changed compilation.");
        });

        test("Maps compiled: visuals change integrity but not gameplay identity or simulation", () =>
        {
            var compiled = MapCompiler.Compile(source, catalog);
            var changed = MapCompiler.Compile(Copy(source, visual: new(source.Visual.Palette.Select(p => "art-" + p).ToArray(), source.Visual.Cells)), catalog);
            NavigationChecks.Check(compiled.GameplayFingerprint == changed.GameplayFingerprint && compiled.DataFingerprint != changed.DataFingerprint, "Visual and gameplay identity were mixed.");
            using var first = compiled.Map.Prepare(players).CreateWorld();
            using var second = changed.Map.Prepare(players).CreateWorld();
            NavigationChecks.Check(first.Report() == second.Report(), "Visual data changed the world.");
            var tampered = JsonNode.Parse(compiled.Write())!;
            tampered["map"]!["visual"]!["palette"]![0] = "different-art";
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Read(new("tampered", tampered.ToJsonString()), catalog));
            var changedCatalog = UnitCatalog.Load([Read("content/units/units.yaml") with { Text = Read("content/units/units.yaml").Text.Replace("HP: 100", "HP: 101") }, Read("content/units/buildings.yaml")],
                LocomotionChecks.Catalog(), LocomotionChecks.Weapons());
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Read(new("wrong-content", compiled.Write()), changedCatalog));
        });

        test("Maps compiled: invalid input, unsupported geometry, duplicate keys and damaged artifacts fail closed", () =>
        {
            var compiled = MapCompiler.Compile(source, catalog);
            foreach (var mutate in new Action<JsonNode>[]
            {
                n => n["geometry"] = "heightfield",
                n => n["version"] = 2,
                n => n["compilerVersion"] = 2,
                n => n["extra"] = true,
                n => n["map"]!["cellSize"] = 512,
                n => n["map"]!["size"]![0] = int.MaxValue,
                n => n["map"]!["terrain"]!["cells"]![0] = -1,
                n => n["map"]!["terrain"]!["palette"]![0] = "missing-terrain",
                n => n["map"]!["objects"]![0]!["type"] = "missing-type",
                n => n["map"]!["objects"]![0]!["owner"] = "missing-owner",
                n => n["map"]!["objects"]![1]!["id"] = n["map"]!["objects"]![0]!["id"]!.GetValue<string>(),
                n => n["map"]!["objects"]![0]!["position"] = new JsonArray(1, 2, 3),
                n => n["map"]!["objects"]![0]!["position"]![0] = 1.5,
                n => n["map"]!["id"] = new string('x', MapCompiler.MaximumTextBytes + 1),
                n => n["map"]!["slots"] = null,
                n => n["gameplayFingerprint"] = new string('0', 64)
            })
            {
                var node = JsonNode.Parse(compiled.Write())!; mutate(node);
                NavigationChecks.Reject<ContentException>(() => MapCompiler.Read(new("invalid", node.ToJsonString()), catalog));
            }
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Read(new("duplicate", compiled.Write().Replace("\"version\":1", "\"version\":1,\"version\":1")), catalog));
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Read(new("trailing", compiled.Write() + " {}"), catalog));
            var raised = source.Objects.ToArray(); raised[0] = raised[0] with { Position = new(4608, 5632, 1) };
            NavigationChecks.Reject<ContentException>(() => MapCompiler.Compile(Copy(source, objects: raised), catalog));
        });

        test("Maps compiled: source ownership, late placement failure and cancellation preserve callers and files", () =>
        {
            var palette = source.Terrain.Palette.ToArray(); var cells = source.Terrain.Cells.ToArray();
            var layer = new MapLayer(palette, cells); palette[0] = "changed"; cells[0] = -1;
            var owned = MapCompiler.Compile(Copy(source, terrain: layer), catalog);
            NavigationChecks.Check(owned.GameplayFingerprint == MapCompiler.Compile(source, catalog).GameplayFingerprint, "Source retained mutable caller arrays.");
            var objects = source.Objects.ToArray(); objects[1] = objects[1] with { Position = objects[0].Position };
            var overlap = MapCompiler.Compile(Copy(source, objects: objects), catalog);
            NavigationChecks.Reject<ContentException>(() => overlap.Map.Prepare(players).CreateWorld());
            using var retry = owned.Map.Prepare(players).CreateWorld();
            NavigationChecks.Check(retry.Count == 3, "Rejected world leaked into the next initialization.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            NavigationChecks.Reject<OperationCanceledException>(() => MapCompiler.Compile(source, catalog, cancelled.Token));
            string path = Path.Combine(Path.GetTempPath(), "openrts-map-" + Guid.NewGuid().ToString("N") + ".ortmap.json");
            try
            {
                MapCompiler.Save(owned, path); string before = File.ReadAllText(path);
                NavigationChecks.Reject<OperationCanceledException>(() => MapCompiler.Save(owned, path, cancelled.Token));
                NavigationChecks.Check(File.ReadAllText(path) == before && MapCompiler.Read(new(path, before), catalog).DataFingerprint == owned.DataFingerprint, "Cancelled save replaced the installed artifact.");
            }
            finally { File.Delete(path); }
            NavigationChecks.Check(!typeof(MapCompiler).Assembly.GetReferencedAssemblies().Any(a => a.Name!.Contains("Godot") || a.Name == "OpenRTS.Network"), "Map compiler acquired a host dependency.");
        });

        test("Maps compiled: tiny independent fixture and rejected start configuration remain separate contracts", () =>
        {
            var tiny = MapCompiler.Compile(MapSource.Read(Read("content/maps/compilation-fixture.mapsource.json")), catalog);
            using var world = tiny.Map.Prepare(players).CreateWorld();
            NavigationChecks.Check(world.Count == 2 && world.Width == 4096 && world.Depth == 3072, "Independent fixture was not usable.");
            var skirmish = MapCompiler.Compile(MapSource.FromM1(Read(MapScenario.DefaultMap), catalog), catalog).Map;
            var invalid = StartingSet.Load(new("invalid-start.yaml", "version: 1\nid: invalid\ncredits: 0\nunits:\n  - {type: rifleman, offset: [536870912, 0]}\n"));
            NavigationChecks.Reject<ContentException>(() => skirmish.Prepare(players, invalid));
            using var valid = skirmish.Prepare(players, StartingSet.Load(Read(MapScenario.DefaultStartSet))).CreateWorld();
            NavigationChecks.Check(valid.Count > 2, "A rejected configuration invalidated the reusable map.");
        });
    }
}
