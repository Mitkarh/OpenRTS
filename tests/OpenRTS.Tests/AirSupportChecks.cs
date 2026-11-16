using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Combat;
using OpenRTS.Core.Orders;
using static NavigationChecks;

internal static class AirSupportChecks
{
    private static FootprintDefinition Stamp(int width = 2, int height = 2) => new(width, height,
        from y in Enumerable.Range(0, height) from x in Enumerable.Range(0, width) select new FootprintCell(x, y, new(false, false)));
    private static EntityPose Pose(SimulationWorld w, EntityId id) => Poses(w).Single(p => p.Id == id);
    private static void Step(SimulationWorld w, int ticks) { for (int i = 0; i < ticks; i++) w.Step(); }
    private static UnitDefinition Worker(bool air = true) => new("worker", 100, constructionRange: 1536,
        flight: air ? new(cruiseAltitude: 3584) : null, workInterval: air ? 2 : 1,
        abilities: [new("capture", "", Kind: AbilityKind.Capture, Range: 4096)],
        products: [new("build", "site", "", OnSite: true)]);
    private static UnitDefinition Site(int ticks = 40) => new("site", 500, movable: false, footprint: Stamp(8, 10),
        build: new(ticks, 50, ProductionKind.PlaceBuilding));

    public static void Run(Action<string, Action> test)
    {
        test("Air support: hover workers load from YAML, reject forward workers and keep roaming out of gameplay", () =>
        {
            const string yaml = "u: {Health: {HP: 100}, Aircraft: {Mode: Hover}, Builder: {Range: 1536, WorkInterval: 2}, Presentation: {Model: air, WorkRoaming: true}}";
            var on = UnitCatalog.Load([new("air", yaml)]);
            var off = UnitCatalog.Load([new("air", yaml.Replace("WorkRoaming: true", "WorkRoaming: false"))]);
            NavigationChecks.Check(on.Units[0].WorkInterval == 2 && on.Presentations[0].WorkRoaming && !off.Presentations[0].WorkRoaming &&
                on.Summary == off.Summary, "Visual work flights affected gameplay or the interval was lost");
            NavigationChecks.Reject<ArgumentException>(() => new UnitDefinition("plane", 100, constructionRange: 1536, flight: new(FlightMode.ForwardFlight)));
            NavigationChecks.Reject<ArgumentException>(() => new UnitDefinition("plane", 100, supply: new(SupplyRole.Truck, 10), flight: new(FlightMode.ForwardFlight)));
            NavigationChecks.Reject<ArgumentOutOfRangeException>(() => new UnitDefinition("worker", 100, workInterval: 0));
        });

        test("Air support: a worker takes off vertically and builds a large footprint underneath at half rate", () =>
        {
            SimulationReport? reference = null;
            foreach (int workers in new[] { 1, 4 })
            {
                using var w = new SimulationWorld([Worker(), Site()], 32768, 32768,
                    navigationGrid: new(32, 32), navigationWorkers: workers, reverseNavigationJobs: workers > 1,
                    players: [new(1, "Blue", 500)]);
                var center = new WorldPosition(16384, 16384);
                var engineer = w.AddInitialUnit(0, center, 1);
                w.Step([new(1, 0, 1, CommandKind.PlanConstruction, engineer, center, Action: "build")]);
                NavigationChecks.Check(w.ConstructionProjectCount == 1 && w.Count == 1, "Construction began before takeoff");
                for (int i = 0; i < 80 && w.Count == 1; i++) w.Step();
                var foundation = Poses(w).Single(p => p.UnitType == 1).Id;
                int before = Pose(w, foundation).Construction.Work;
                Step(w, 8);
                NavigationChecks.Check(Pose(w, foundation).Construction.Work - before == 8 && Pose(w, engineer).Current == center with { Elevation = 3584 },
                    "The worker was evacuated, grounded or contributed at the ground rate");
                Step(w, 100);
                NavigationChecks.Check(!Pose(w, foundation).Construction.Active && w.ConstructionProjectCount == 0, "Aerial construction never finished");
                var report = w.Report(); NavigationChecks.Check(reference is null || report == reference, "Air construction differs by worker count"); reference = report;
            }
        });

        test("Air support: two airborne builders can share a project without avoidance interrupting their work", () =>
        {
            using var w = new SimulationWorld([Worker(), Site()], 32768, 32768,
                navigationGrid: new(32, 32), players: [new(1, "Blue", 500)]);
            var first = w.AddInitialUnit(0, new(15360, 16384, 3584), 1);
            var second = w.AddInitialUnit(0, new(17408, 16384, 3584), 1);
            w.Step([new(1, 0, 1, CommandKind.PlanConstruction, Position: new(16384, 16384),
                Recipients: new([first, second]), Action: "build")]);
            Step(w, 24);
            var site = Poses(w).Single(p => p.UnitType == 1);
            NavigationChecks.Check(site.Construction.Work > 24 && w.MovingCount == 0 &&
                Pose(w, first).Order.Phase == OrderPhase.Engaging && Pose(w, second).Order.Phase == OrderPhase.Engaging,
                "Soft avoidance prevented a builder from joining overhead work");
        });

        test("Air support: capture and upgrade use half work without decaying on intervening ticks", () =>
        {
            foreach (bool air in new[] { false, true })
            foreach (bool upgrade in new[] { false, true })
            {
                var target = new UnitDefinition("site", 100, states: ["expanded"], movable: false, footprint: Stamp(),
                    build: new(40, 50, ProductionKind.PlaceBuilding), capturable: upgrade ? null : new(40),
                    upgrades: upgrade ? [new("expanded", 0, 40, support: true, @default: true)] : []);
                using var w = new SimulationWorld([Worker(air), target], 24576, 24576, navigationGrid: new(24, 24), players: [new(1, "Blue", 100)]);
                var engineer = w.AddInitialUnit(0, air ? new(12288, 12288, 3584) : new(10240, 12288), 1);
                var building = w.AddInitialUnit(1, new(12288, 12288), upgrade ? 1u : 0u);
                w.Step([new(1, 0, 1, upgrade ? CommandKind.StartUpgrade : CommandKind.CaptureTarget, engineer, TargetEntity: building)]);
                Step(w, 9);
                var p = Pose(w, building);
                NavigationChecks.Check((upgrade ? p.Upgrade.Work : p.Capture.Progress) == (air ? 5 : 10), "Work rate or capture activity is wrong");
                Step(w, 80);
                NavigationChecks.Check(upgrade ? Pose(w, building).Upgrade.Completed == "expanded" : Pose(w, building).Presentation.Owner == 1,
                    "An aerial interaction never completed");
                if (air) NavigationChecks.Check(Pose(w, engineer).Current == new WorldPosition(12288, 12288, 3584), "The worker left its overhead anchor");
            }
        });

        test("Air support: cancelling capture before the first work pulse cannot create negative progress", () =>
        {
            using var w = new SimulationWorld([Worker(), new("site", 100, movable: false, footprint: Stamp(),
                capturable: new(40), build: new(40, 50, ProductionKind.PlaceBuilding))], 24576, 24576,
                navigationGrid: new(24, 24), players: [new(1, "Blue")]);
            var building = w.AddInitialUnit(1, new(12288, 12288));
            var engineer = w.AddInitialUnit(0, new(12288, 12288, 3584), 1);
            w.Step([new(1, 0, 1, CommandKind.CaptureTarget, engineer, TargetEntity: building)]);
            NavigationChecks.Check(Pose(w, building).Capture.Progress == 0, "The zero-work fixture contributed too early");
            w.Step([new(2, 0, 1, CommandKind.Stop, engineer)]); Step(w, 10);
            w.Step([new(w.Tick + 1, 0, 1, CommandKind.CaptureTarget, engineer, TargetEntity: building)]); Step(w, 7);
            NavigationChecks.Check(Pose(w, building).Capture.Progress == 4, "An interrupted zero-work side accumulated negative capture debt");
        });

        test("Air support: manual repair works above the building at the ordinary weapon rate", () =>
        {
            foreach (var command in new[] { CommandKind.RepairTarget, CommandKind.UseAbility })
            {
                var repair = new WeaponDefinition(2048, 2048, 10, 2, 0, effect: WeaponEffect.Repair);
                UnitDefinition[] types = [new("worker", 100, ["idle", "move", "repair"], constructionRange: 1536,
                    workInterval: 2, flight: new(cruiseAltitude: 3584), weapons: [new("main", repair, "repair")],
                    abilities: [new("repair", "main")]),
                    new("site", 500, movable: false, footprint: Stamp(), armor: ArmorKind.Building),
                    new("gun", 100, ["idle", "move", "fire"], weapon: new(8192, 8192, 100, 1000, 0))];
                using var w = new SimulationWorld(types, 24576, 24576, navigationGrid: new(24, 24));
                var engineer = w.AddInitialUnit(0, new(12288, 12288, 3584), 1);
                var building = w.AddInitialUnit(1, new(12288, 12288), 1);
                var gun = w.AddInitialUnit(2, new(18432, 12288), 2);
                w.Step([new(1, 0, 1, CommandKind.AttackTarget, gun, TargetEntity: building)]);
                NavigationChecks.Check(Pose(w, building).Health == 400, "The repair fixture was not damaged");
                for (int i = 0; i < 6; i++)
                    w.Step([new(w.Tick + 1, 0, 1, command, engineer, TargetEntity: building, Action: "repair")]);
                NavigationChecks.Check(Pose(w, building).Health == 430 && Pose(w, engineer).Current == new WorldPosition(12288, 12288, 3584),
                    "Repair was slowed by WorkInterval or failed at cruise altitude");
            }
        });

        test("Air support: supply crosses an impassable strip and conserves stock, cargo and credits", () =>
        {
            SimulationReport? reference = null;
            foreach (int workers in new[] { 1, 4 })
            {
                UnitDefinition[] types = [new("air-truck", 100, flight: new(cruiseAltitude: 1536), supply: new(SupplyRole.Truck, 10),
                    abilities: [new("harvest", "", Kind: AbilityKind.Harvest, AutoCast: true)]),
                    new("mine", 100, movable: false, footprint: Stamp(), supply: new(SupplyRole.Mine, 30), destruction: DestructionPolicy.Neutralize),
                    new("depot", 100, movable: false, footprint: Stamp(), supply: new(SupplyRole.Depot))];
                using var w = new SimulationWorld(types, 32768, 16384, navigationGrid: new(32, 16,
                    Enumerable.Range(0, 16).Select(y => new GridCell(16, y))), navigationWorkers: workers, players: [new(1, "Blue")]);
                var truck = w.AddInitialUnit(0, new(8192, 8192, 1536), 1);
                var mine = w.AddInitialUnit(1, new(8192, 8192));
                w.AddInitialUnit(2, new(24576, 8192), 1);
                bool crossed = false;
                for (int i = 0; i < 1200; i++)
                {
                    w.Step(); var p = Pose(w, truck); crossed |= p.Current.X > 16384;
                    var players = new PlayerPose[w.PlayerCount]; w.CopyPlayers(players);
                    NavigationChecks.Check(players.Single(p => p.Id == 1).Credits + p.Supply.Amount + Pose(w, mine).Supply.Amount == 30,
                        "A supply flight created or lost resources");
                }
                var finalPlayers = new PlayerPose[w.PlayerCount]; w.CopyPlayers(finalPlayers);
                NavigationChecks.Check(crossed && Pose(w, mine).Supply.Amount == 0 && Pose(w, truck).Supply.Amount == 0 && finalPlayers.Single(p => p.Id == 1).Credits == 30,
                    "A resource flight failed to deliver its complete stock");
                var report = w.Report(); NavigationChecks.Check(reference is null || report == reference, "Air supply differs by worker count"); reference = report;
            }
        });

        test("Air support: a manual harvest assignment cancels landing and returns to overhead loading", () =>
        {
            using var w = new SimulationWorld([new("truck", 100, flight: new(cruiseAltitude: 1536), supply: new(SupplyRole.Truck, 10),
                abilities: [new("harvest", "", Kind: AbilityKind.Harvest)]),
                new("mine", 100, movable: false, footprint: Stamp(), supply: new(SupplyRole.Mine, 30), destruction: DestructionPolicy.Neutralize)],
                24576, 24576, navigationGrid: new(24, 24), players: [new(1, "Blue")]);
            var truck = w.AddInitialUnit(0, new(12288, 12288, 1536), 1);
            var mine = w.AddInitialUnit(1, new(12288, 12288));
            w.Step([new(1, 0, 1, CommandKind.Land, truck, new(20480, 12288))]); Step(w, 8);
            w.Step([new(w.Tick + 1, 0, 1, CommandKind.UseAbility, truck, TargetEntity: mine, Action: "harvest")]);
            Step(w, 120);
            NavigationChecks.Check(Pose(w, truck).Supply.Amount == 10 && Pose(w, truck).Current.Elevation == 1536,
                "A stale landing intent prevented the new harvest assignment");
        });

        test("Air support: a heavy transport carries twelve infantry, two vehicles or mixed sizes and lands to unload", () =>
        {
            foreach (var (vehicles, infantry, expected) in new[] { (0, 13, 12), (3, 0, 2), (1, 7, 7) })
            {
                using var w = new SimulationWorld([new("carrier", 800, body: BodyShape.Circle(480), flight: new(),
                    transport: new(["infantry", "vehicle"], 12, unloadDistance: 1024)),
                    new("tank", 400, passenger: new("vehicle", 6)), new("infantry", 100, passenger: new("infantry"))],
                    32768, 24576, navigationGrid: new(32, 24), speedPerTick: 256);
                var carrier = w.AddInitialUnit(0, new(8192, 8192), 1);
                var passengers = Enumerable.Range(0, vehicles + infantry).Select(i => w.AddInitialUnit(i < vehicles ? 1 : 2,
                    new(3072 + i % 6 * 768, 4096 + i / 6 * 768), 1)).ToArray();
                if (vehicles > 0)
                {
                    w.Step([new(1, 0, 1, CommandKind.Enter, Recipients: new(passengers.Take(vehicles)), TargetEntity: carrier)]);
                    Step(w, 240);
                }
                if (infantry > 0) w.Step([new(w.Tick + 1, 0, 1, CommandKind.Enter,
                    Recipients: new(passengers.Skip(vehicles)), TargetEntity: carrier)]);
                Step(w, 240);
                var loaded = Poses(w).Where(p => p.Carrier == carrier).ToArray();
                NavigationChecks.Check(loaded.Length == expected && loaded.Sum(p => p.UnitType == 1 ? 6 : 1) == 12,
                    $"Weighted capacity failed for {vehicles} vehicles / {infantry} infantry");
                w.Step([new(w.Tick + 1, 0, 1, CommandKind.Move, carrier, new(16384, 8192))]); Step(w, 120);
                NavigationChecks.Check(Pose(w, carrier).Flight.Airborne && loaded.All(p => Pose(w, p.Id).Carrier == carrier), "Cargo escaped during takeoff");
                w.Step([new(w.Tick + 1, 0, 1, CommandKind.UnloadAt, carrier, new(16384, 8192))]);
                NavigationChecks.Check(loaded.All(p => Pose(w, p.Id).Carrier == carrier), "Cargo was unloaded before landing");
                Step(w, 160);
                NavigationChecks.Check(loaded.All(p => Pose(w, p.Id) is { Carrier.Value: 0, Current.Elevation: 0 }), "Weighted passengers failed to unload on the ground");
            }
        });
    }
}
