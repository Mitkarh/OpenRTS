using DeclarativeContent;
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Combat;
using OpenRTS.Godot.Authoring;
using OpenRTS.Godot.Rendering;

/// <summary>Executed by Godot itself: tests scene serialization, transforms and headless independent output.</summary>
public partial class MapAuthoringChecks : Node
{
    internal const string Sample = "res://examples/maps/AuthoringMap.tscn";
    private int _checks;

    public override void _Ready()
    {
        try
        {
            Run();
            MapTerrainChecks.Run(Check);
            CheckDiagonalRelief();
            CheckDiagonalCliffMap();
            CheckAircraftOnReliefMaps();
            CheckIndirectFireMap();
            GD.Print($"OPENRTS_AUTHORING_CHECKS:{_checks} passed"); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
        finally { foreach (Node child in GetChildren()) child.Free(); }
    }

    private void CheckIndirectFireMap()
    {
        var root = GD.Load<PackedScene>("res://examples/maps/IndirectFire.tscn").Instantiate<OpenRtsMap2D>();
        AddChild(root);
        var manifest = GameManifest.Load(ProjectSettings.GlobalizePath(root.ContentManifest));
        var catalog = manifest.LoadUnits();
        var compiled = MapCompiler.Compile(MapSceneExtractor2D.Extract(root), catalog);
        var map = MapCompiler.Read(new("indirect-fire", compiled.Write()), catalog).Map;
        var players = new Dictionary<string, PlayerDefinition> { ["blue"] = new(1, "Blue"), ["red"] = new(2, "Red") };
        using var world = map.Prepare(players).CreateWorld();
        var effects = EffectCatalog.Load(manifest.Effects);
        var view = new CombatEffectsView(catalog, effects, ModelCatalog.Load(manifest.Models));
        AddChild(view.Node);
        var rounds = new ProjectilePose[128];
        var actors = new EntityPose[world.Count];
        var mounts = new WeaponPose[world.WeaponCount];
        var batches = view.Node.GetChildren().OfType<MultiMeshInstance3D>()
            .Select(batch => (batch.Multimesh, Texture: (batch.Multimesh.Mesh.SurfaceGetMaterial(0) as StandardMaterial3D)?.AlbedoTexture))
            .Where(batch => !string.IsNullOrEmpty(batch.Texture?.ResourcePath))
            .ToDictionary(batch => batch.Texture!.ResourcePath, batch => batch.Multimesh);
        var smoke = view.Node.GetNode<MultiMeshInstance3D>("RocketSmoke").Multimesh;
        var flashes = view.Node.GetNode<MultiMeshInstance3D>("Impacts").Multimesh;
        var kinds = new HashSet<ProjectileTrajectory>();
        var hits = new HashSet<string>();
        var loaded = new HashSet<string>();
        var emptied = new HashSet<string>();
        var departures = new HashSet<string>();
        var raised = new Dictionary<string, (Vector3 Position, Vector3 Direction)>();
        var pitched = new HashSet<string>();
        int texturedBatches = 0;
        bool descending = false;
        bool checkedMuzzle = false;
        for (int tick = 0; tick < 300; tick++)
        {
            if (tick > 0) world.Step();
            foreach (var e in world.Events) view.Accept(e);
            var active = rounds.AsSpan(0, world.ProjectileCount);
            world.CopyPoses(actors); world.CopyWeaponPoses(mounts); world.CopyProjectilePoses(active);
            view.Apply(active, 1, world.Tick, reliefProjection: true, actors: actors, weapons: mounts);
            foreach (var actor in actors)
            {
                if (actor.WeaponCount == 0) continue;
                var weapon = catalog.Units[actor.UnitType].Weapons[0].Weapon;
                var effect = effects.Weapons[weapon.Id];
                if (effect.MountedRocket is null) continue;
                var channel = mounts[actor.WeaponOffset];
                var batch = batches[effect.ProjectileTexture!];
                if (channel.WindupEndTick == world.Tick + 1)
                {
                    Check(batch.VisibleInstanceCount == 1, weapon.Id + ": ready projectile missing during windup.");
                    raised[weapon.Id] = Instance(batch);
                }
                if (!channel.Reloading && channel.WindupEndTick == 0 && batch.VisibleInstanceCount == 1)
                {
                    Check(MathF.Abs(Instance(batch).Direction.Y) < 0.001f, weapon.Id + ": ready projectile should rest flat.");
                    loaded.Add(weapon.Id);
                }
                if (channel.Reloading && batch.VisibleInstanceCount == 0) emptied.Add(weapon.Id);
            }
            foreach (var shot in active)
            {
                var weapon = catalog.Units[shot.UnitType].Weapons[shot.Mount].Weapon;
                var effect = effects.Weapons[weapon.Id];
                var batch = batches[effect.ProjectileTexture!];
                if (shot.Current.Elevation > 1536) kinds.Add(weapon.Trajectory);
                if (!checkedMuzzle && shot.Departure.Tick == world.Tick && weapon.Id == "howitzer")
                {
                    var shotPosition = Instance(batch).Position;
                    var launch = shot.Departure;
                    float body = SpriteWorldView.Angle(launch.BodyFacing, launch.BodyFacing, 1);
                    float aim = SpriteWorldView.Angle(launch.MountFacing, launch.MountFacing, 1);
                    var offset = new Vector2(0, 0.18f).Rotated(body) + new Vector2(0, (0.10f - 0.71f) * 1.65f).Rotated(aim);
                    var expected = new Vector3(launch.Origin.X / 1024f + offset.X,
                        launch.Origin.Elevation / 1024f + SpriteWorldView.OverlayHeight, launch.Origin.Y / 1024f + offset.Y);
                    bool flashAtMuzzle = Enumerable.Range(0, flashes.VisibleInstanceCount)
                        .Any(i => Instance(flashes, i).Position.DistanceTo(expected) < 0.001f);
                    Check(shotPosition.DistanceTo(expected) < 0.001f && flashAtMuzzle,
                        "The first projectile sprite and the flash must share the rotated muzzle position.");
                    checkedMuzzle = true;
                }
                if (shot.Departure.Tick == world.Tick && effect.MountedRocket is { } mount)
                {
                    Check(batch.VisibleInstanceCount == 1 && raised.TryGetValue(weapon.Id, out _), weapon.Id + ": launch did not replace the mounted sprite.");
                    var before = raised[weapon.Id]; var after = Instance(batch);
                    Check(before.Position.DistanceTo(after.Position) < 0.001f && before.Direction.Dot(after.Direction) > 0.9999f &&
                        Mathf.IsEqualApprox(after.Direction.Y, MathF.Sin(Mathf.DegToRad((float)mount.LaunchPitch))),
                        weapon.Id + ": the first flying sprite must match the raised rocket.");
                    var tail = after.Position - after.Direction * (float)effect.Size;
                    var smokeBuffer = smoke.Buffer;
                    bool exhaust = false;
                    for (int i = 0; i < smoke.VisibleInstanceCount; i++)
                    {
                        int n = 16 * i;
                        exhaust |= new Vector3(smokeBuffer[n + 3], smokeBuffer[n + 7], smokeBuffer[n + 11]).DistanceTo(tail) < 0.001f &&
                            Mathf.IsEqualApprox(smokeBuffer[n], (float)effect.SmokeSize);
                    }
                    Check(exhaust, weapon.Id + ": smoke should originate at the rocket tail, with its own scale.");
                    if (departures.Count == 0)
                    {
                        using var restored = SimulationWorld.RestoreSnapshot(world.CaptureSnapshot(), () => map.Prepare(players).CreateWorld());
                        var restoredShots = new ProjectilePose[restored.ProjectileCount];
                        restored.CopyProjectilePoses(restoredShots);
                        Check(active.SequenceEqual(restoredShots), "A runtime snapshot should keep the exact launch anchor.");
                    }
                    departures.Add(weapon.Id);
                }
                if (batch.VisibleInstanceCount == 1 && shot.Previous != shot.Current && world.Tick - shot.Departure.Tick >= 5)
                {
                    var delta = new Vector3(shot.Current.X - shot.Previous.X, shot.Current.Elevation - shot.Previous.Elevation,
                        shot.Current.Y - shot.Previous.Y).Normalized();
                    var actual = Instance(batch).Direction;
                    Check(actual.Dot(delta) > 0.9999f, weapon.Id + ": projectile sprite did not follow the flight tangent.");
                    if (actual.Y > 0.1f) pitched.Add(weapon.Id);
                    descending |= actual.Y < -0.1f;
                }
            }
            foreach (var e in world.Events)
                if (e.Kind == WorldEventKind.ProjectileImpact)
                {
                    var weapon = catalog.Units[e.UnitType].Weapons[e.Mount].Weapon;
                    if (e.TargetEntity != default || weapon.ScatterRadius > 0 && actors.Any(actor =>
                        actor.Presentation.Owner == 2 &&
                        Math.Abs(actor.Current.Elevation - e.Position.Elevation) <= weapon.SplashRadius + 256 &&
                        ObjectGeometry.WithinRadius(catalog.Units[actor.UnitType], actor.Current, e.Position, weapon.SplashRadius)))
                        hits.Add(weapon.Id);
                }
            texturedBatches = Math.Max(texturedBatches, batches.Values.Count(batch => batch.VisibleInstanceCount > 0));
        }
        string[] launchers = ["ballistic-missile", "cruise-missile"];
        Check(checkedMuzzle, "The indirect-fire fixture never exercised a muzzle anchor.");
        Check(loaded.SetEquals(launchers) && departures.SetEquals(launchers) && emptied.SetEquals(launchers),
            "Both launchers must render the loaded, launch and empty states.");
        Check(pitched.Contains("howitzer") && pitched.Contains("ballistic-missile") && descending,
            "Elevated flight must exercise both climbing and descending sprite pitch.");
        Check(kinds.SetEquals([ProjectileTrajectory.Arc, ProjectileTrajectory.Ballistic, ProjectileTrajectory.Cruise]) &&
            hits.SetEquals(["howitzer", "salvo-rocket", "ballistic-missile", "cruise-missile"]) && texturedBatches == 4,
            $"Indirect Fire did not render and hit across the cliff with all four units. Flew {string.Join(", ", kinds)}, hit {string.Join(", ", hits)}, textured {texturedBatches}.");
        view.Apply([], 1, world.Tick + 120);
        Check(smoke.VisibleInstanceCount == 0, "Rocket smoke should expire after flight.");

        static (Vector3 Position, Vector3 Direction) Instance(MultiMesh batch, int index = 0)
        {
            var data = batch.Buffer;
            int n = index * 16;
            return (new(data[n + 3], data[n + 7], data[n + 11]), -new Vector3(data[n + 2], data[n + 6], data[n + 10]).Normalized());
        }
    }

    private void CheckAircraftOnReliefMaps()
    {
        const int size = WorldPosition.UnitsPerCell;
        (string Scene, WorldPosition Start, WorldPosition Target)[] routes =
        [
            ("RockyPass", new(15 * size + 512, 24 * size + 512), new(15 * size + 512, 8 * size + 512)),
            ("DiagonalRamps", new(2 * size + 512, 2 * size + 512), new(14 * size + 512, 14 * size + 512)),
            ("DiagonalCliffs", new(size / 2, 8 * size + 512), new(8 * size + 512, 8 * size + 512)),
        ];
        var poses = new EntityPose[1];
        foreach (var (scene, start, target) in routes)
        {
            var map = GD.Load<PackedScene>($"res://examples/maps/{scene}.tscn").Instantiate<OpenRtsMap2D>();
            AddChild(map);
            var catalog = GameManifest.Load(ProjectSettings.GlobalizePath(map.ContentManifest)).LoadUnits();
            var compiled = MapCompiler.Compile(MapSceneExtractor2D.Extract(map), catalog);
            var grid = MapCompiler.Read(new(scene, compiled.Write()), catalog).Map.Grid;
            var aircraft = catalog.Units.Single(unit => unit.Id == "tiltrotor");
            var flight = aircraft.Flight!;
            using var world = new SimulationWorld([aircraft], grid.Width * size, grid.Height * size, navigationGrid: grid);
            var departure = start with { Elevation = grid.SurfaceElevationAt(start) + flight.CruiseAltitude };
            var id = world.AddInitialUnit(0, departure, owner: 1);
            FlyTo(target);
            FlyTo(departure);
            Check(poses[0].Current == departure, $"{scene}: aircraft did not settle back at its original altitude.");
            map.Free();

            void FlyTo(WorldPosition goal)
            {
                world.Step([new(world.Tick + 1, 0, 1, CommandKind.Move, id, goal)]);
                var expected = goal with { Elevation = grid.SurfaceElevationAt(goal) + flight.CruiseAltitude };
                bool safe = true;
                for (int tick = 0; tick < 240; tick++)
                {
                    world.CopyPoses(poses);
                    var pose = poses[0];
                    safe &= pose.Current.Elevation - flight.HalfHeight >= grid.SurfaceElevationAt(pose.Current) &&
                        Math.Abs(pose.Current.Elevation - pose.Previous.Elevation) <= flight.VerticalSpeed;
                    if (pose.Current == expected) break;
                    world.Step();
                }
                Check(safe && poses[0].Current == expected, $"{scene}: relief flight failed approaching {expected}, current {poses[0].Current}.");
            }
        }
    }

    private void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); _checks++; }
    private void Reject(Action action, string message)
    {
        try { action(); } catch (ContentException) { _checks++; return; }
        throw new InvalidOperationException(message);
    }

    private void CheckDiagonalRelief()
    {
        var map = GD.Load<PackedScene>("res://examples/maps/RockyPass.tscn").Instantiate<OpenRtsMap2D>();
        AddChild(map);
        var layer = map.Terrain!;
        var atlas = (TileSetAtlasSource)layer.TileSet!.GetSource(0);
        var ramp = new Vector2I(2, 0);
        var cell = new Vector2I(28, 21);
        int alternative = atlas.CreateAlternativeTile(ramp);
        try
        {
            var tile = atlas.GetTileData(ramp, alternative);
            tile.SetCustomData("TerrainId", "road");
            tile.SetCustomData("VisualId", "rock-ramp");
            tile.SetCustomData("Elevation", 1024);
            tile.SetCustomData("Rise", new Vector2I(128, -256));
            layer.SetCell(cell, 0, ramp, alternative);
            var catalog = GameManifest.Load(ProjectSettings.GlobalizePath(map.ContentManifest)).LoadUnits();
            var compiled = MapCompiler.Compile(MapSceneExtractor2D.Extract(map), catalog);
            var grid = MapCompiler.Read(new("diagonal relief", compiled.Write()), catalog).Map.Grid;
            int index = grid.Index(new(cell.X, cell.Y));
            Check(grid.SurfaceRiseAt(index) == new OpenRTS.Core.Navigation.SurfaceRise(128, -256) &&
                grid.SurfaceAt(index).Slope == 287, "The TileMapLayer diagonal relief was lost in compilation.");

            var terrain = new TerrainSurface(grid);
            using var mesh = terrain.Mesh;
            Check(mesh.GetSurfaceCount() == 2 && mesh.SurfaceGetMaterial(1) is BaseMaterial3D
                { CullMode: BaseMaterial3D.CullModeEnum.Disabled }, "Rock faces must be visible from both sides.");
            var camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = 8 };
            AddChild(camera);
            var center = new Vector3(28.5f, 0.5f, 21.5f);
            camera.Position = center + new Vector3(0, 4, 6);
            camera.LookAt(center);
            const int size = WorldPosition.UnitsPerCell;
            var point = new WorldPosition(28 * size + 256, 21 * size + 768, 928);
            Check(terrain.Pick(camera, camera.UnprojectPosition(new(28.25f, 928f / size, 21.75f))) == point,
                "Picking the diagonal surface lost exact XY or height.");
            Check(terrain.Pick(camera, camera.UnprojectPosition(new(28.5f, 0.5f, 22))) is null,
                "An exposed diagonal ramp cliff accepted a ground order.");
        }
        finally
        {
            layer.EraseCell(cell);
            atlas.RemoveAlternativeTile(ramp, alternative);
        }
    }

    private void CheckDiagonalCliffMap()
    {
        var map = GD.Load<PackedScene>("res://examples/maps/DiagonalCliffs.tscn").Instantiate<OpenRtsMap2D>();
        AddChild(map);
        var catalog = GameManifest.Load(ProjectSettings.GlobalizePath(map.ContentManifest)).LoadUnits();
        var source = MapSceneExtractor2D.Extract(map);
        var compiled = MapCompiler.Compile(MapSource.Read(new("diagonal cliffs source", source.Write())), catalog);
        var game = MapCompiler.Read(new("diagonal cliffs artifact", compiled.Write()), catalog).Map;
        var grid = game.Grid;
        Check(source.Surface!.Where(cell => cell.Cut != default).Select(cell => cell.Cut.UpperCorner).Distinct().Count() == 4 &&
            grid.HasSurfaceCuts && !grid.HasSlopedSurface, "The four diagonal cliff tiles did not round-trip.");

        var terrain = new TerrainSurface(grid);
        using var mesh = terrain.Mesh;
        var camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = 20 };
        AddChild(camera);
        camera.Position = new(8, 15, 8);
        camera.RotationDegrees = new(-90, 0, 0);
        const int size = WorldPosition.UnitsPerCell;
        var low = new WorldPosition(6 * size + 256, 5 * size + 256);
        var high = new WorldPosition(6 * size + 768, 5 * size + 768, size);
        Check(Pick(low) == low && Pick(high) == high, "Picking the two halves lost XY or selected the wrong floor.");
        var shore = new WorldPosition(size + 256, size + 768);
        var water = new WorldPosition(size + 768, size + 256);
        var infantry = catalog.Units.Single(type => type.Id == "ramp-rifleman");
        var resolver = new OpenRTS.Core.Navigation.DestinationResolver(grid, infantry.Locomotor);
        Check(grid.TerrainAt(low) == catalog.Locomotion!.TerrainIndex("grass") &&
            grid.TerrainAt(high) == catalog.Locomotion.TerrainIndex("mud") && game.UpperVisualAt(6, 5) == "mud" &&
            grid.SurfaceElevationAt(shore) == grid.SurfaceElevationAt(water) && resolver.CanStop(shore) &&
            !resolver.CanStop(water) && game.UpperVisualAt(1, 1) == "water",
            "The two terrain IDs and visual IDs did not survive the compiler or flat shore clearance.");
        var wall = new Vector3(6.5f, 0.5f, 5.5f);
        camera.Position = wall + new Vector3(-5, 4, -5);
        camera.LookAt(wall);
        Check(terrain.Pick(camera, camera.UnprojectPosition(wall)) is null, "The diagonal rock face accepted a ground order.");

        var players = new Dictionary<string, PlayerDefinition> { ["blue"] = new(1, "Blue") };
        using var world = game.Prepare(players).CreateWorld();
        using var replay = game.Prepare(players).CreateWorld(4);
        var poses = new EntityPose[2];
        world.CopyPoses(poses);
        Check(poses[0].Current.Elevation == 0 && poses[1].Current.Elevation == size, "The two scouts loaded at incorrect elevations.");
        var goal = new WorldPosition(13 * size + 512, poses[0].Current.Y);
        WorldCommand[] orders = [new(1, 0, 1, CommandKind.Move, poses[0].Id, goal),
            new(1, 1, 1, CommandKind.Move, poses[1].Id, high)];
        world.Step(orders);
        replay.Step(orders);
        bool detoured = false, grounded = true;
        for (int t = 0; t < 350 && world.MovingCount != 0; t++)
        {
            world.Step();
            replay.Step();
            world.CopyPoses(poses);
            grounded &= poses[0].Current.Elevation == 0 && poses[1].Current.Elevation == size;
            detoured |= Math.Abs(poses[0].Current.Y - goal.Y) >= 3 * size;
        }
        Check(grounded && detoured && poses[0].Current == goal && poses[1].Current == high &&
            world.Report() == replay.Report(), "Movement across the plateau outline, either half, or replay was incorrect.");
        CheckDiagonalCliffCombat(grid, infantry.Locomotor);

        WorldPosition? Pick(WorldPosition position) => terrain.Pick(camera,
            camera.UnprojectPosition(new Vector3(position.X, position.Elevation, position.Y) / size));
    }

    private void CheckDiagonalCliffCombat(NavigationGrid grid, OpenRTS.Core.Navigation.Locomotor locomotor)
    {
        var low = new WorldPosition(4608, 4608);
        var highNearEdge = new WorldPosition(6710, 5686);
        var highBehindRock = new WorldPosition(7680, 6656);
        var direct = new WeaponDefinition(6144, 6144, 25, 1000, 0, projectile: ProjectileKind.Cannon,
            projectileSpeed: 512, trajectory: ProjectileTrajectory.Direct);
        foreach (bool uphill in new[] { true, false })
        {
            var blocked = uphill ? Fire(direct, low, highBehindRock) : Fire(direct, highBehindRock, low);
            var clear = uphill ? Fire(direct, low, highNearEdge) : Fire(direct, highNearEdge, low);
            Check(!blocked.Fired && blocked.Health == 100 && clear.Fired && clear.Health == 75,
                $"Direct fire {(uphill ? "uphill" : "downhill")} failed to distinguish rock from a clear ray.");
        }
        var planar = new WeaponDefinition(6144, 6144, 25, 1000, 0, projectile: ProjectileKind.Cannon);
        Check(Fire(planar, low, highBehindRock).Health == 75, "Planar weapon behavior changed on the cliff map.");
        Check(Fire(direct, new(6400, 5376), new(5632, 5632)).Health == 75,
            "The upper half of a cut incorrectly obstructed a ray entirely on low ground.");
        var missile = new WeaponDefinition(6144, 6144, 25, 1000, 0, projectile: ProjectileKind.Missile,
            projectileSpeed: 96, trajectory: ProjectileTrajectory.Direct);
        var redirected = Fire(missile, low, highNearEdge, moveTarget: true);
        Check(redirected.Fired && redirected.Health == 100 && redirected.Impact is { } impact &&
            impact.TargetEntity == default && impact.Position.Elevation is > 0 and < 1024 &&
            Math.Abs(impact.Position.X + impact.Position.Y - 12 * 1024) <= 2,
            $"A missile steered into rock instead of striking the diagonal wall: {redirected}.");

        (bool Fired, int Health, WorldEvent? Impact) Fire(WeaponDefinition weapon, WorldPosition from, WorldPosition to,
            bool moveTarget = false)
        {
            var body = BodyShape.Circle(64);
            using var shotWorld = new SimulationWorld([
                new("test-gun", 100, ["idle", "fire"], body: body, weapon: weapon, movable: false, locomotor: locomotor),
                new("test-target", 100, body: body, locomotor: locomotor)
            ], grid.Width * grid.CellSize, grid.Height * grid.CellSize, navigationGrid: grid);
            shotWorld.AddInitialUnit(0, from, owner: 1);
            shotWorld.AddInitialUnit(1, to, owner: 2);
            shotWorld.Step([new(1, 0, 1, CommandKind.AttackTarget, new(1), TargetEntity: new(2))]);
            bool fired = false;
            WorldEvent? impact = null;
            for (int tick = 1; tick <= 96; tick++)
            {
                foreach (var e in shotWorld.Events)
                    if (e.Kind == WorldEventKind.WeaponFired) fired = true;
                    else if (e.Kind == WorldEventKind.ProjectileImpact) impact = e;
                if (impact.HasValue) break;
                if (tick == 1 && moveTarget)
                    shotWorld.Step([new(2, 0, 2, CommandKind.Move, new(2), new(8704, 7680, 1024))]);
                else shotWorld.Step();
            }
            var poses = new EntityPose[2];
            shotWorld.CopyPoses(poses);
            return (fired, poses[1].Health, impact);
        }
    }

    private void Run()
    {
        var catalog = GameManifest.Load(ProjectSettings.GlobalizePath("res://game.yaml")).LoadUnits();
        var root = GD.Load<PackedScene>(Sample).Instantiate<OpenRtsMap>();
        AddChild(root);
        var source = MapSceneExtractor.Extract(root);
        var compiled = MapCompiler.Compile(source, catalog);
        Check(source.Objects.Select(o => o.Type).SequenceEqual(new[] { "rifleman", "tank", "engineer" }), "Three catalog types lost or reordered.");
        Check(source.Objects[0].Id == "patrol/rifle" && source.Objects[1].Position == new WorldPosition(14336, 12288), "Subscene ID/transform was not extracted.");
        Check(source.Slots.Single(s => s.Id == "east").Reverse, "Inspector start properties were not loaded.");
        string baseline = compiled.Write();
        var group = root.GetNode<OpenRtsMapGroup>("Patrol");
        group.Name = "RenamedPrefab";
        root.MoveChild(group, 0);
        root.GetNode<Node3D>("Decoration").Scale = new(3, 4, 5);
        Check(MapCompiler.Compile(MapSceneExtractor.Extract(root), catalog).Write() == baseline, "Rename/tree order/decoration changed semantic data.");

        var packed = new PackedScene();
        Check(packed.Pack(root) == Error.Ok, "Pack failed.");
        var reloaded = packed.Instantiate<OpenRtsMap>(); AddChild(reloaded);
        Check(MapCompiler.Compile(MapSceneExtractor.Extract(reloaded), catalog).Write() == baseline, "Pack/reopen changed stable ID/order/positions.");
        RemoveChild(reloaded); reloaded.Free(); packed.Dispose();

        var other = GD.Load<PackedScene>("res://examples/maps/AuthoringSquad.tscn").Instantiate<OpenRtsMapGroup>();
        other.InstanceId = "second"; other.Order = 3; other.Position = new(12, 0, 16); root.AddChild(other);
        var instanced = MapSceneExtractor.Extract(root);
        Check(instanced.Objects.Count == 6 && instanced.Objects[0].Id == "second/rifle", "Instance context or explicit group order was lost.");
        other.InstanceId = group.InstanceId;
        Reject(() => MapSceneExtractor.Extract(root), "Duplicate prefab namespace accepted.");
        root.RemoveChild(other); other.Free();

        var unit = group.GetNode<OpenRtsMapObject>("Rifleman");
        var duplicate = (OpenRtsMapObject)unit.Duplicate(); group.AddChild(duplicate);
        Reject(() => MapSceneExtractor.Extract(root), "Native duplicate ID/order accepted.");
        MapAuthoringIdentity.Repair(root, (node, field, value) => node.Set(field, value));
        Check(duplicate.ObjectId != unit.ObjectId && duplicate.Order > 2 && MapSceneExtractor.Extract(root).Objects.Count == 4, "Explicit identity repair failed.");
        group.RemoveChild(duplicate); duplicate.Free();

        unit.Position = new(0.00048828125f, 0, 0.00048828125f);
        Check(MapSceneExtractor.Extract(root).Objects.Single(o => o.Id == "patrol/rifle").Position == new WorldPosition(12289, 12289), "Half-unit rounding rule changed.");
        unit.Position = Vector3.Zero;
        var parent = new Node3D { Name = "Organization" }; group.AddChild(parent);
        unit.Reparent(parent);
        Check(MapCompiler.Compile(MapSceneExtractor.Extract(root), catalog).Write() == baseline, "Reparent within context changed semantic data.");
        unit.Reparent(group); group.RemoveChild(parent); parent.Free();

        unit.Scale = new(2, 1, 1); Reject(() => MapSceneExtractor.Extract(root), "Scale accepted."); unit.Scale = Vector3.One;
        unit.Rotation = new(0, 0.1f, 0); Reject(() => MapSceneExtractor.Extract(root), "Rotation accepted."); unit.Rotation = Vector3.Zero;
        unit.Position = new(0, 0.1f, 0); Reject(() => MapSceneExtractor.Extract(root), "Height accepted."); unit.Position = Vector3.Zero;
        unit.TopLevel = true; Reject(() => MapSceneExtractor.Extract(root), "TopLevel accepted."); unit.TopLevel = false;
        unit.Position = new(float.NaN, 0, 0); Reject(() => MapSceneExtractor.Extract(root), "Nonfinite position accepted."); unit.Position = Vector3.Zero;
        unit.Order = 1; Reject(() => MapSceneExtractor.Extract(root), "Duplicate order accepted."); unit.Order = 0;
        unit.TypeId = "missing"; Reject(() => MapCompiler.Compile(MapSceneExtractor.Extract(root), catalog), "Missing type accepted."); unit.TypeId = "rifleman";
        root.IsSkirmish = false; Reject(() => MapSceneExtractor.Extract(root), "Start on fixture accepted."); root.IsSkirmish = true;
        var nested = new OpenRtsMap(); root.AddChild(nested);
        Reject(() => MapSceneExtractor.Extract(root), "Nested map root accepted."); root.RemoveChild(nested); nested.Free();
        root.Scale = new(2, 1, 1); Reject(() => MapSceneExtractor.Extract(root), "Root transform accepted."); root.Scale = Vector3.One;
        var tank = group.GetNode<OpenRtsMapObject>("Tank");
        var engineer = group.GetNode<OpenRtsMapObject>("Engineer");
        unit.Order = tank.Order = int.MaxValue - 1; engineer.Order = -1;
        MapAuthoringIdentity.Repair(root, (node, field, value) => node.Set(field, value));
        Check(new[] { unit.Order, tank.Order, engineer.Order }.All(o => o >= 0) &&
            new[] { unit.Order, tank.Order, engineer.Order }.Distinct().Count() == 3, "Order repair overflowed its Int32 key space.");
        unit.Order = 0; tank.Order = 1; engineer.Order = 2;

        var fixture = new OpenRtsMap { Name = "Fixture", IsSkirmish = false, Size = new(8, 8) }; AddChild(fixture);
        fixture.AddChild(new OpenRtsMapObject { Name = "Owned", ObjectId = "owned", TypeId = "rifleman", OwnerSlot = "blue", Position = new(2, 0, 2) });
        var owned = MapCompiler.Compile(MapSceneExtractor.Extract(fixture), catalog);
        using (var fixtureWorld = owned.Map.Prepare(new Dictionary<string, PlayerDefinition> { ["blue"] = new(1, "Blue"), ["red"] = new(2, "Red") }).CreateWorld())
            Check(owned.Source.Objects[0].Owner == "blue" && owned.Source.Slots.All(s => !s.Position.HasValue), "Fixture owner/slots were not extracted.");
        RemoveChild(fixture); fixture.Free();

        var players = new Dictionary<string, PlayerDefinition> { ["west"] = new(1, "Blue"), ["east"] = new(2, "Red") };
        var set = StartingSet.Load(new("infantry", File.ReadAllText(ProjectSettings.GlobalizePath("res://content/start-sets/infantry.yaml"))));
        var loaded = MapCompiler.Read(new("sample.ortmap.json", baseline), catalog);
        using var world = compiled.Map.Prepare(players, set).CreateWorld();
        using var roundtrip = loaded.Map.Prepare(players, set).CreateWorld(4);
        Check(world.Report() == roundtrip.Report() && compiled.Map.Prepare(players, set).UnitCount == 9, "Scene compiled world differs from independent reader.");
        string output = OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--authoring-output=", StringComparison.Ordinal))?[19..]
            ?? ProjectSettings.GlobalizePath("res://artifacts/maps/authoring-check.ortmap.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!); MapCompiler.Save(compiled, output);
        GD.Print("OPENRTS_AUTHORING_WORLD:" + System.Text.Json.JsonSerializer.Serialize(world.Report()));
        RemoveChild(root); root.Free();
    }
}
