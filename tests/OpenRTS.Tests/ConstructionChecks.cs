using OpenRTS.Core;

internal static class ConstructionChecks
{
    private static readonly WorldPosition Site = new(8192, 8192);
    private static readonly FootprintDefinition Stamp = new(2, 2,
        [new(0, 0, new(false)), new(1, 0, new(false)), new(0, 1, new(false)), new(1, 1, new(false))]);

    private static UnitDefinition[] Types() =>
    [
        new("engineer", 100, constructionRange: 1536, products:
            [new("power", "plant", "", OnSite: true), new("other", "other", "", OnSite: true)]),
        new("plant", 100, ["idle", "fire"], ["lowpower", "constructing"], movable: false, footprint: Stamp,
            weapon: new(3072, 3072, 10, 1000, 0), economy: new(100, 5, 2),
            build: new(6, 50, ProductionKind.PlaceBuilding, initialHealthPercent: 30)),
        new("yard", 100, movable: false, constructionRange: 8192,
            products: [new("power", "plant", "", OnSite: true)]),
        new("enemy", 100, ["idle", "move", "fire"], weapon: new(3072, 3072, 7, 1000, 0)),
        new("other", 100, movable: false, footprint: Stamp, build: new(4, 25, ProductionKind.PlaceBuilding))
    ];

    private static SimulationWorld World(int workers = 1) =>
        new(Types(), 24 * 1024, 16 * 1024, speedPerTick: 256, navigationGrid: new(24, 16),
            navigationWorkers: workers, reverseNavigationJobs: workers > 1, players: [new(1, "Blue", 500), new(2, "Red", 500)]);

    private static EntityPose Pose(SimulationWorld world, EntityId id)
    {
        var poses = new EntityPose[world.Count];
        world.CopyPoses(poses);
        return poses.Single(p => p.Id == id);
    }

    private static ConstructionProjectPose[] Projects(SimulationWorld world)
    {
        var poses = new ConstructionProjectPose[world.ConstructionProjectCount];
        world.CopyConstructionProjects(poses);
        return poses;
    }

    private static PlayerPose Player(SimulationWorld world, uint owner)
    {
        var poses = new PlayerPose[world.PlayerCount];
        world.CopyPlayers(poses);
        return poses.Single(p => p.Id == owner);
    }

    public static void Run(Action<string, Action> test)
    {
        test("Construction: player frames preserve their own blueprint targets without exposing enemy projects", () =>
        {
            using var world = SimulationWorld.CreateWithMechanics(new() { FogCellSize = 1024 }, Types(), 24576, 16384,
                navigationGrid: new(24, 16), players: [new(1, "Blue", 500), new(2, "Red", 500)]);
            var blue = world.AddInitialUnit(0, new(2048, 2048), 1);
            var red = world.AddInitialUnit(0, new(18432, 12288), 2);
            world.Step([new(1, 0, 1, CommandKind.PlanConstruction, blue, Site, Action: "other"),
                new(1, 0, 2, CommandKind.PlanConstruction, red, Site, Action: "other")]);
            var projects = Projects(world);
            var frames = new FrameExchange();
            frames.TryPublish(world);
            using var pinned = frames.AcquireLatest(1)!;
            foreach (uint owner in new uint[] { 1, 2 })
            {
                using var frame = frames.AcquireLatest(owner)!;
                var project = projects.Single(p => p.Owner == owner);
                NavigationChecks.Check(frame.ConstructionProjects.Length == 1 && frame.ConstructionProjects[0] == project &&
                    frame.Poses.ToArray().Single().Order.Target == project.Id, "The player's blueprint target was lost or an enemy project leaked");
            }
            world.Step([new(2, 0, 1, CommandKind.CancelConstruction, Owner: 1, TargetEntity: projects[0].Id)]);
            frames.TryPublish(world);
            using var latest = frames.AcquireLatest(1)!;
            NavigationChecks.Check(latest.ConstructionProjects.IsEmpty && latest.Poses[0].Order.Target == default &&
                pinned.Poses[0].Order.Target == projects[0].Id, "Cancellation changed a pinned target or retained it in the new frame");
        });

        test("Construction: insufficient credits wait without cloning the map and resume on income", () =>
        {
            using var world = new SimulationWorld(Types(), 524288, 524288, navigationGrid: new(512, 512),
                players: [new(1, "Blue", 0)]);
            var engineer = world.AddInitialUnit(0, new(5376, 8192), 1);
            var grid = world.NavigationGrid;
            world.Step([new(1, 0, 1, CommandKind.PlanConstruction, engineer, Site, Action: "other")]);
            var project = Projects(world).Single().Id;
            for (int i = 0; i < 8; i++) world.Step();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 8; i++) world.Step();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            NavigationChecks.Check(allocated < 32768 && ReferenceEquals(grid, world.NavigationGrid) &&
                Projects(world).Single().Id == project && Player(world, 1).Credits == 0,
                $"Waiting for credits rebuilt the map or changed the project: {allocated} bytes for eight ticks");
            world.Step([new(world.Tick + 1, 0, 1, CommandKind.Spawn, Position: new(16384, 16384), UnitType: 1, Owner: 1)]);
            int started = 0;
            for (int i = 0; i < 30; i++)
            {
                world.Step();
                started += world.Events.ToArray().Count(e => e.Entity == project && e.Kind == WorldEventKind.ConstructionStarted);
            }
            NavigationChecks.Check(started == 1 && Projects(world).Length == 0 && !Pose(world, project).Construction.Active,
                "The waiting project did not start exactly once when income paid for it");
        });

        test("Construction: an engineer starts after a temporarily blocked evacuation exit clears", () =>
        {
            SimulationReport? reference = null;
            foreach (int workers in new[] { 1, 4 })
            {
                var ring = from y in Enumerable.Range(6, 4) from x in Enumerable.Range(6, 4)
                    where (x is 6 or 9 || y is 6 or 9) && (x != 6 || y != 8)
                    select new GridCell(x, y);
                using var world = new SimulationWorld(Types(), 24576, 16384, speedPerTick: 256,
                    navigationGrid: new(24, 16, ring), navigationWorkers: workers,
                    reverseNavigationJobs: workers > 1, players: [new(1, "Blue", 500)]);
                var engineer = world.AddInitialUnit(0, new(4096, 8704), 1);
                var occupant = world.AddInitialUnit(0, Site, 1);
                var blocker = world.AddInitialUnit(0, new(6656, 8704), 1);
                world.Step([new(1, 0, 1, CommandKind.PlanConstruction, engineer, Site, Action: "other")]);
                var project = Projects(world).Single().Id;
                for (int i = 0; i < 39; i++) world.Step();
                NavigationChecks.Check(Projects(world).Single().Clearing && Pose(world, occupant).Current == Site &&
                    Pose(world, engineer).Current != new WorldPosition(4096, 8704) && Player(world, 1).Credits == 500,
                    "The engineer did not approach and wait for the occupied exit without spending");

                world.Step([new(world.Tick + 1, 0, 1, CommandKind.Move, blocker, new(4096, 10752))]);
                int started = 0, completed = 0, cancelled = 0;
                for (int i = 0; i < 120; i++)
                {
                    world.Step();
                    foreach (var value in world.Events)
                    {
                        if (value.Entity != project) continue;
                        if (value.Kind == WorldEventKind.ConstructionStarted) started++;
                        if (value.Kind == WorldEventKind.ConstructionCompleted) completed++;
                        if (value.Kind == WorldEventKind.ConstructionCancelled) cancelled++;
                    }
                }
                NavigationChecks.Check(started == 1 && completed == 1 && cancelled == 0 && Projects(world).Length == 0 &&
                    Pose(world, occupant).Current != Site && !Pose(world, project).Construction.Active &&
                    Player(world, 1).Credits == 475, $"Construction did not resume once after clearance: starts={started}, completed={completed}, cancelled={cancelled}, credits={Player(world, 1).Credits}");
                var report = world.Report();
                NavigationChecks.Check(reference is null || reference == report, "Evacuation retry differs by worker scheduling");
                reference = report;
            }
        });

        test("Construction: a reachable perimeter point works when all four initial approaches are blocked", () =>
        {
            using var world = new SimulationWorld(Types(), 24576, 16384, speedPerTick: 256,
                navigationGrid: new(24, 16, [new(6, 8), new(9, 8), new(6, 6), new(6, 9)]),
                players: [new(1, "Blue", 500)]);
            var engineer = world.AddInitialUnit(0, new(2048, 8192), 1);
            NavigationChecks.Check(world.CanAccessBuildingSite(engineer, 4, Site, 0), "Static preview missed a reachable perimeter point");
            world.Step([new(1, 0, 1, CommandKind.PlanConstruction, engineer, Site, Action: "other")]);
            var project = Projects(world).Single().Id;
            for (int i = 0; i < 160; i++) world.Step();
            NavigationChecks.Check(Projects(world).Length == 0 && !Pose(world, project).Construction.Active &&
                Player(world, 1).Credits == 475, "The engineer did not build through the alternative approach");
        });

        test("Construction: a builder inside the project evacuates around four occupied exits", () =>
        {
            using var world = World();
            var engineer = world.AddInitialUnit(0, Site, 1);
            foreach (var point in new WorldPosition[] { new(6911, 8192), new(9473, 8192), new(8192, 6911), new(8192, 9473) })
                world.AddInitialUnit(0, point, 1);
            world.Step([new(1, 0, 1, CommandKind.PlanConstruction, engineer, Site, Action: "power")]);
            var project = Projects(world).Single().Id;
            for (int i = 0; i < 120; i++) world.Step();
            NavigationChecks.Check(Projects(world).Length == 0 && !Pose(world, project).Construction.Active &&
                Pose(world, engineer).Current != Site, "The builder ignored free perimeter exits or stopped its own evacuation");
        });

        test("Construction: evacuation retries preserve a healthy exit and its queued order", () =>
        {
            var large = new FootprintDefinition(4, 4, from y in Enumerable.Range(0, 4) from x in Enumerable.Range(0, 4)
                select new FootprintCell(x, y, new(false)));
            using var world = new SimulationWorld([
                new("yard", 100, movable: false, constructionRange: 8192, products: [new("build", "site", "", OnSite: true)]),
                new("site", 100, movable: false, footprint: large, build: new(2, kind: ProductionKind.PlaceBuilding)),
                new("occupant", 100)], 24576, 16384, speedPerTick: 128, navigationGrid: new(24, 16));
            var yard = world.AddInitialUnit(0, new(2048, 8192), 1);
            var occupant = world.AddInitialUnit(2, Site, 1);
            world.Step([new(1, 0, 1, CommandKind.PlanConstruction, yard, Site, Action: "build")]);
            var project = Projects(world).Single().Id;
            var exit = Pose(world, occupant).Order.Destination;
            world.Step([new(2, 0, 1, CommandKind.Move, occupant, new(4096, 12288), Queued: true)]);
            for (int i = 0; i < 8; i++) world.Step();
            NavigationChecks.Check(Projects(world).Single().Clearing && Pose(world, occupant).Order.Destination == exit &&
                Pose(world, occupant).Order.QueuedCount == 1, "A retry replaced a healthy evacuation or erased its queued order");
            for (int i = 0; i < 80; i++) world.Step();
            NavigationChecks.Check(Projects(world).Length == 0 && !Pose(world, project).Construction.Active &&
                Pose(world, occupant).Current == new WorldPosition(4096, 12288), "Evacuation did not finish with the queued order intact");
        });

        test("Construction: rotated blueprint, sparse foundation and removal share the same grid cells", () =>
        {
            var footprint = new FootprintDefinition(3, 2,
                [new(0, 0, new(false)), new(2, 0, new(true, false), false), new(1, 1, new(false))]);
            var types = new[]
            {
                new UnitDefinition("builder", 100, movable: false, constructionRange: 8192,
                    products: [new("build", "site", "", OnSite: true)]),
                new UnitDefinition("site", 100, movable: false, footprint: footprint,
                    build: new(2, kind: ProductionKind.PlaceBuilding)),
                new UnitDefinition("unit", 100)
            };
            using var world = new SimulationWorld(types, 20480, 16384, navigationGrid: new(20, 16));
            var builder = world.AddInitialUnit(0, new(4096, 8192), 1);
            world.AddInitialUnit(2, new(7680, 7680), 2); // Inside the bounding box but outside the rotated cells.
            var center = new WorldPosition(8192, 8704);
            world.Step([
                new(1, 0, 1, CommandKind.HoldPosition, builder),
                new WorldCommand(1, 0, 2, CommandKind.PlanConstruction, builder, center, Action: "build", Queued: true) { QuarterTurns = 1 }]);
            var project = Projects(world).Single();
            NavigationChecks.Check(project.QuarterTurns == 1 && project.Position == center, "Queued blueprint lost rotation or alignment");
            world.Step([new(2, 0, 1, CommandKind.BuildSite, builder, TargetEntity: project.Id)]);
            var foundation = Pose(world, project.Id);
            NavigationChecks.Check(foundation.QuarterTurns == 1 && foundation.Presentation.BodyFacing == new FacingDirection(1, 0),
                "The foundation did not keep the blueprint orientation");

            var grid = world.NavigationGrid!;
            int blocked = grid.Index(new(8, 7)), ramp = grid.Index(new(8, 9));
            NavigationChecks.Check(grid.IsBlocked(blocked) && grid.IsBlocked(grid.Index(new(7, 8))) &&
                !grid.IsBlocked(grid.Index(new(7, 7))) && !grid.IsBlocked(ramp) && !grid.CanStop(ramp),
                "The sparse footprint or its ramp did not rotate");
            world.Step(); world.Step();
            NavigationChecks.Check(!Pose(world, project.Id).Construction.Active && Pose(world, project.Id).QuarterTurns == 1,
                "Completion reset rotation");
            world.Step([new(5, 0, 1, CommandKind.Destroy, project.Id)]);
            NavigationChecks.Check(world.Events.ToArray().Single(e => e.Kind == WorldEventKind.Destroyed).QuarterTurns == 1 &&
                !world.NavigationGrid!.IsBlocked(blocked) && world.NavigationGrid.CanStop(ramp) && grid.IsBlocked(blocked),
                "Removal or the wreck lost the rotated footprint");
        });

        test("Construction: readiness controls weapons, production and boarding independently of visual states", () =>
        {
            var factory = new UnitDefinition("factory", 100, ["idle", "fire"], ["constructing", "lowpower"], movable: false,
                footprint: Stamp, weapon: new(3072, 3072, 1, 1000, 0), economy: new(100, 5, 2),
                taskQueues: [new("train")], products: [new("train", "worker", "train")],
                transport: new(["worker"], 1), build: new(2, kind: ProductionKind.PlaceBuilding));
            using var world = new SimulationWorld([
                new("yard", 100, movable: false, constructionRange: 8192, products: [new("build", "factory", "", OnSite: true)]),
                factory, new("worker", 100, passenger: new("worker"), constructionRange: 1536, build: new(50)), new("enemy", 100)],
                24 * 1024, 16 * 1024, navigationGrid: new(24, 16), players: [new(1, "Blue", 500)]);
            var yard = world.AddInitialUnit(0, new(4096, 8192), 1);
            var worker = world.AddInitialUnit(2, new(6656, 8192), 1);
            var enemy = world.AddInitialUnit(3, new(11264, 8192), 2);
            world.Step([new(1, 0, 1, CommandKind.PlanConstruction, yard, Site, Action: "build")]);
            var site = new EntityId(4);
            var frames = new FrameExchange();
            frames.TryPublish(world);
            using var pinned = frames.AcquireLatest()!;
            NavigationChecks.Check(world.TryGetConditions(site, out var conditions) && conditions == GameplayConditions.Constructing,
                "The foundation did not expose its readiness condition");

            world.Step([
                new(2, 0, 1, CommandKind.SetVisualStates, site, States: 0),
                new(2, 0, 2, CommandKind.EnqueueTask, site, Action: "train"),
                new(2, 0, 3, CommandKind.Enter, worker, TargetEntity: site),
                new(2, 0, 4, CommandKind.Destroy, yard)]);
            NavigationChecks.Check(Pose(world, site).Construction.Work == 0 && Pose(world, worker).Carrier.Value == 0 &&
                Pose(world, enemy).Health == 100 && world.TaskCount == 0 && Player(world, 1).Energy == 0,
                "An unfinished foundation became active or continued without its builder");
            NavigationChecks.Check(world.TryGetConditions(site, out conditions) && conditions.HasFlag(GameplayConditions.Constructing),
                "A visual command cleared a gameplay condition");
            world.Step([new(3, 0, 1, CommandKind.BuildSite, worker, TargetEntity: site)]);
            NavigationChecks.Check(Pose(world, site).Construction.Work == 2, "A replacement builder could not resume after packed removal");
            world.Step([new(4, 0, 1, CommandKind.SetVisualStates, site, States: factory.StateMask("constructing"))]);
            NavigationChecks.Check(world.TryGetConditions(site, out conditions) && conditions == GameplayConditions.None &&
                !Pose(world, site).Construction.Active && Pose(world, enemy).Health == 100 && Player(world, 1).Energy == 100,
                "Completion did not update readiness and power after the weapon phase");
            world.Step([new(5, 0, 1, CommandKind.Enter, worker, TargetEntity: site),
                new(5, 0, 2, CommandKind.EnqueueTask, site, Action: "train"),
                new(5, 0, 3, CommandKind.AttackTarget, site, TargetEntity: enemy)]);
            NavigationChecks.Check(Pose(world, worker).Carrier == site && world.TaskCount == 1 && Pose(world, enemy).Health < 100 &&
                world.TryGetConditions(site, out conditions) && conditions == GameplayConditions.None,
                "The completed factory did not enable consumers independently of its visual state");
            NavigationChecks.Check(pinned.Poses.ToArray().Single(p => p.Id == site).Construction.Active,
                "Completion changed the pinned foundation frame");
        });

        test("Construction: project overlap and cancellation are owner-scoped and leave navigation untouched", () =>
        {
            using var world = World();
            var blue = world.AddInitialUnit(0, new(2048, 2048), 1);
            var red = world.AddInitialUnit(0, new(16384, 12288), 2);
            var grid = world.NavigationGrid;
            world.Step([
                new(1, 0, 1, CommandKind.PlanConstruction, blue, Site, Action: "power"),
                new(1, 0, 2, CommandKind.PlanConstruction, red, Site, Action: "power")]);
            var original = Projects(world);
            NavigationChecks.Check(original.Length == 2 && world.Count == 2 && ReferenceEquals(grid, world.NavigationGrid), "Ghosts occupied cells or removed an enemy plan");
            var frames = new FrameExchange();
            frames.TryPublish(world);
            using var pinned = frames.AcquireLatest()!;
            world.Step([
                new(2, 0, 1, CommandKind.CancelConstruction, Owner: 2, TargetEntity: original[0].Id),
                new(2, 0, 2, CommandKind.PlanConstruction, blue, Site, Action: "other", Queued: true),
                new(2, 0, 3, CommandKind.Stop, blue)]);
            var plans = Projects(world);
            NavigationChecks.Check(plans.Length == 2 && plans[0].Id != original[0].Id && plans[0].UnitType == 4 && plans[1] == original[1], "Replacement or owner validation failed");
            NavigationChecks.Check(pinned.ConstructionProjects.ToArray().SequenceEqual(original), "Pinned project frame changed");
            world.Step([new(3, 0, 1, CommandKind.CancelConstruction, Owner: 1, TargetEntity: plans[0].Id)]);
            NavigationChecks.Check(Projects(world).SequenceEqual([original[1]]) && Player(world, 1).Credits == 500, "Cancelling an unstarted project spent money");
        });

        test("Construction: a foundation starts at configured health, pauses, retains damage and accepts two builders", () =>
        {
            using var world = World();
            var yard = world.AddInitialUnit(2, new(4096, 8192), 1);
            var engineer = world.AddInitialUnit(0, new(8192, 6144), 1);
            world.Step([new(1, 0, 1, CommandKind.PlanConstruction, yard, Site, Action: "power")]);
            var id = new EntityId(3);
            var start = Pose(world, id);
            NavigationChecks.Check(start.Health == 30 && start.Construction == new ConstructionProgress(0, 12) && Projects(world).Length == 0,
                "Initial foundation health or progress is wrong");
            NavigationChecks.Check(Player(world, 1).Credits == 450 && Player(world, 1).Energy == 0 && world.NavigationGrid!.IsBlocked(world.NavigationGrid.CellAt(Site)),
                "Starting work did not charge once, stamp the site, or leave power disabled");

            var enemy = new EntityId(4);
            world.Step([
                new(2, 0, 1, CommandKind.Stop, yard),
                new(2, 0, 2, CommandKind.Spawn, Position: new(10240, 8192), UnitType: 3, Owner: 2),
                new(2, 0, 3, CommandKind.AttackTarget, enemy, TargetEntity: id)]);
            NavigationChecks.Check(Pose(world, id).Health == 23 && Pose(world, id).Construction.Work == 0 && Pose(world, enemy).Health == 100,
                "Paused foundation fired, progressed, or did not take damage");
            world.Step([new(3, 0, 1, CommandKind.BuildSite, TargetEntity: id, Recipients: new([yard, engineer]))]);
            NavigationChecks.Check(Pose(world, id).Construction.Work == 4, "Builders did not combine their work");
            world.Step();
            world.Step();
            var done = Pose(world, id);
            NavigationChecks.Check(!done.Construction.Active && done.Health == 93 && Player(world, 1).Energy == 100 && Player(world, 1).Credits == 450,
                "Completion lost combat damage or activated economy early");
            world.Step();
            world.Step();
            NavigationChecks.Check(Player(world, 1).Credits == 455, "Income timer did not start after completion");
        });

        test("Construction: idle allies help automatically and manual orders take priority", () =>
        {
            AbilityDefinition build = new("build", "", AutoCast: true, Kind: AbilityKind.Build);
            UnitDefinition[] types =
            [
                new("starter", 100, movable: false, constructionRange: 8192,
                    products: [new("power", "plant", "", OnSite: true)]),
                new("yard", 100, movable: false, constructionRange: 8192, abilities: [build]),
                new("engineer", 100, constructionRange: 1536, abilities: [build]),
                new("plant", 100, movable: false, footprint: Stamp, build: new(10, 0, ProductionKind.PlaceBuilding))
            ];
            using var world = new SimulationWorld(types, 24000, 16000);
            var starter = world.AddInitialUnit(0, new(4096, 4096), 1);
            var yard = world.AddInitialUnit(1, new(4096, 6144), 1);
            var engineer = world.AddInitialUnit(2, new(6400, 8192), 1);
            world.AddInitialUnit(2, new(9984, 8192), 2);
            world.Step([new(1, 0, 1, CommandKind.PlanConstruction, starter, Site, Action: "power")]);
            var site = world.Events.ToArray().Single(e => e.Kind == WorldEventKind.ConstructionStarted).Entity;
            world.Step();
            NavigationChecks.Check(Pose(world, site).Construction.Work == 6, "Both idle allies should contribute and the enemy should not");
            world.Step([
                new(3, 0, 1, CommandKind.HoldPosition, engineer),
                new(3, 0, 2, CommandKind.SetAutoCast, yard, Action: "build", Enabled: false)]);
            NavigationChecks.Check(Pose(world, site).Construction.Work == 8 && !Pose(world, yard).AutoBuild,
                "Hold or disabled autocast did not suspend construction help");
            world.Step([new(4, 0, 1, CommandKind.UseAbility, yard, TargetEntity: site, Action: "build")]);
            NavigationChecks.Check(Pose(world, site).Construction.Work == 12, "Manual construction did not work with autocast off");
        });

        test("Construction: multiple queued sites finish in order with worker parity", () =>
        {
            using var sequential = World();
            using var parallel = World(4);
            foreach (var world in new[] { sequential, parallel })
            {
                world.AddInitialUnit(0, new(2048, 4096), 1);
                world.AddInitialUnit(0, new(2048, 5120), 1);
            }
            var group = new EntitySet([new(1), new(2)]);
            WorldCommand[] journal =
            [
                new(1, 0, 1, CommandKind.PlanConstruction, Position: Site, Recipients: group, Action: "power", Queued: true),
                new(1, 0, 2, CommandKind.PlanConstruction, Position: new(12288, 8192), Recipients: group, Action: "power", Queued: true),
                new(1, 0, 3, CommandKind.PlanConstruction, Position: new(16384, 8192), Recipients: group, Action: "other", Queued: true)
            ];
            var completed = new List<uint>();
            for (int tick = 1; tick <= 200; tick++)
            {
                sequential.Step(tick == 1 ? journal : []);
                parallel.Step(tick == 1 ? journal : []);
                NavigationChecks.Check(sequential.Events.SequenceEqual(parallel.Events), "Construction event order differs by worker scheduling");
                foreach (var e in sequential.Events) if (e.Kind == WorldEventKind.ConstructionCompleted) completed.Add(e.Entity.Value);
            }
            NavigationChecks.Check(completed.SequenceEqual(new uint[] { 3, 4, 5 }) && Projects(sequential).Length == 0,
                $"Shift queue did not complete in order: {string.Join(", ", completed)}");
            NavigationChecks.Check(sequential.Report() == parallel.Report(), "Construction differs by navigation workers");
        });

        test("Construction: Shift between orders appends behind earlier yard placements", () =>
        {
            using var world = World();
            var yard = world.AddInitialUnit(2, new(4096, 8192), 1);
            world.Step([
                new(1, 0, 1, CommandKind.PlanConstruction, yard, new(7168, 8192), Queued: true, Action: "power"),
                new(1, 0, 2, CommandKind.PlanConstruction, yard, new(10240, 8192), Queued: true, Action: "power")]);
            var completed = new List<uint>();
            for (int tick = 2; tick <= 30; tick++)
            {
                world.Step(tick == 9
                    ? [new(9, 0, 1, CommandKind.PlanConstruction, yard, new(4096, 12288), Queued: true, Action: "power")]
                    : []);
                foreach (var e in world.Events)
                    if (e.Kind == WorldEventKind.ConstructionCompleted) completed.Add(e.Entity.Value);
            }
            NavigationChecks.Check(completed.SequenceEqual(new uint[] { 2, 3, 4 }),
                "The new Shift placement overtook an earlier pending yard order");
        });

        test("Construction: friendly and neutral units evacuate, but enemy or immobile obstacles cancel", () =>
        {
            using var world = World();
            var yard = world.AddInitialUnit(2, new(4096, 8192), 1);
            var neutral = world.AddInitialUnit(0, Site);
            var ally = world.AddInitialUnit(0, new(8704, 8704), 1);
            world.Step([new(1, 0, 1, CommandKind.PlanConstruction, yard, Site, Action: "power")]);
            NavigationChecks.Check(Projects(world).Single().Clearing && world.Count == 3 && !world.NavigationGrid!.IsBlocked(world.NavigationGrid.CellAt(Site)),
                "Foundation trapped the occupants during clearance");
            for (int i = 0; i < 20; i++) world.Step();
            NavigationChecks.Check(world.Count == 4 && Projects(world).Length == 0 && Pose(world, neutral).Current != Site && Pose(world, ally).Current != new WorldPosition(8704, 8704),
                "Friendly/neutral evacuation did not clear the footprint");

            foreach (int blocker in new[] { 0, 4 })
            {
                using var blocked = World();
                blocked.AddInitialUnit(2, new(4096, 8192), 1);
                blocked.AddInitialUnit(blocker, Site, blocker == 0 ? 2u : 1u);
                blocked.Step([new(1, 0, 1, CommandKind.PlanConstruction, new(1), Site, Action: "power")]);
                NavigationChecks.Check(blocked.Count == 2 && Projects(blocked).Length == 0 && Player(blocked, 1).Credits == 500 &&
                    blocked.Events.ToArray().Any(e => e.Kind == WorldEventKind.ConstructionCancelled),
                    "Blocked foundation did not cancel cleanly");
            }
        });

        test("Construction: fixed builders cannot plan outside their radius", () =>
        {
            using var world = World();
            var yard = world.AddInitialUnit(2, new(2048, 2048), 1);
            world.Step([new(1, 0, 1, CommandKind.PlanConstruction, yard, Site, Action: "power")]);
            NavigationChecks.Check(Projects(world).Length == 0 && world.Count == 1 && Player(world, 1).Credits == 500, "Out-of-range yard accepted a project");
        });
    }
}
