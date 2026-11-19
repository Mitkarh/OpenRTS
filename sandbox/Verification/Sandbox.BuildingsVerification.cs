using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

public partial class Sandbox
{
    private async void VerifyBuildings(FrameExchange.FrameLease frame)
    {
        try
        {
            _verificationDone = true;
            long tick = frame.Tick; int rate = frame.TickRate; var grid = frame.Grid!;
            var poses = frame.Poses.ToArray(); var weapons = frame.Weapons.ToArray(); var report = _session!.Completion.GetAwaiter().GetResult();
            Check(tick == BuildingScenario.VerificationTicks && poses.Length == 27 && _sprites!.WreckCount == 1, "Destroyed object must leave only an adapter wreck");
            Check(grid.TerrainAt(grid.CellAt(new(51 * 1024, 27 * 1024))) == _catalog!.Locomotion!.TerrainIndex("forest"), "Trees did not override road");
            Check(grid.TerrainAt(grid.CellAt(new(68 * 1024, 28 * 1024))) == _catalog.Locomotion.TerrainIndex("mountains"), "Cliff did not override terrain");
            Check(!grid.IsBlocked(grid.CellAt(new(51 * 1024, 42 * 1024))), "Wreck retained a blocking footprint");
            Check(_catalog.Units.Single(u => u.Id == "light-infantry").Body.HalfWidth == 160 && _catalog.Units.Single(u => u.Id == "large-tank").Body.HalfWidth == 640, "Mixed body sizes missing");
            Check(Pick(new(51 * 1024 + 100, 10 * 1024 + 100))?.Id.Value == 3, "Building picking still uses the center");
            _placeableId = "barracks"; var snapped = PlacementPoint(new(60 * 1024 + 137, 32 * 1024 + 50)); var fp = _catalog.Units[PlaceableType].Footprint!;
            Check((snapped.X - fp.HalfWidth) % 1024 == 0 && (snapped.Y - fp.HalfHeight) % 1024 == 0, "Placement anchor not aligned");
            HandleBuildingKey(Key.B); Check(_placing, "Placement mode missing"); HandleBuildingKey(Key.Escape); Check(!_placing, "Placement cancel missing");
            _sprites!.Apply(poses, 1, tick, rate, weapons);
            foreach (var (id, texture) in new[] { ("civilian-house", "civilian-house-states.png"), ("civilian-apartments", "civilian-apartments-states.png"), ("civilian-clinic", "city-clinic.png"), ("civilian-market", "city-market.png"), ("civilian-library", "city-library.png"), ("civilian-station", "city-station.png"), ("civilian-community", "city-community.png") })
            {
            var house = poses.Single(p => _catalog!.Units[p.UnitType].Id == id);
            uint garrison = _catalog!.Units[house.UnitType].StateMask("garrisoned");
            foreach (var (health, state, expected) in new[] { (1000, 0u, 0), (1000, garrison, 1), (500, 0u, 2), (500, garrison, 3), (250, garrison, 4) })
            {
                _sprites.Apply([house with { Health = health, Presentation = house.Presentation with { States = state } }], 1, tick, rate);
                var visible = _sprites.Node.GetChildren().OfType<MultiMeshInstance3D>().Where(n => n.Multimesh.VisibleInstanceCount > 0 &&
                    n.Multimesh.Mesh.SurfaceGetMaterial(0) is ShaderMaterial m && m.GetShaderParameter("sprite_texture").AsGodotObject() is Texture2D t && t.ResourcePath.EndsWith(id == "civilian-apartments" && expected is 1 or 3 ? "civilian-apartments-team-trim.png" : texture, StringComparison.Ordinal)).ToArray();
                Check(visible.Length == 1 && Math.Abs(((ShaderMaterial)visible[0].Multimesh.Mesh.SurfaceGetMaterial(0)).GetShaderParameter("uv_rect").AsVector4().X - expected / 5f) < 0.002f, "Civilian house state selected the wrong sprite");
            }
            }
            foreach (var (id, batch) in new[] { ("civilian-house", "ownership-flag"), ("civilian-apartments", "civilian-apartments-team-trim") })
            {
                var house = poses.Single(p => _catalog!.Units[p.UnitType].Id == id);
                uint garrison = _catalog!.Units[house.UnitType].StateMask("garrisoned");
                var blue = house with { Presentation = house.Presentation with { Owner = 1, States = garrison } };
                var red = blue with { Id = new(1000), Presentation = blue.Presentation with { Owner = 2 } };
                _sprites.Apply([blue, red], 1, tick, rate);
                var marker = _sprites.Node.GetChildren().OfType<MultiMeshInstance3D>().Where(n => n.Multimesh.VisibleInstanceCount > 0 && n.Multimesh.Mesh.SurfaceGetMaterial(0) is ShaderMaterial mat && mat.GetShaderParameter("sprite_texture").AsGodotObject() is Texture2D tex && tex.ResourcePath.EndsWith(batch + ".png", StringComparison.Ordinal)).Select(n => n.Multimesh).Single();
                var markerColors = marker.Buffer;
                Check(marker.VisibleInstanceCount == 2 && markerColors[16] < markerColors[18] && markerColors[36] > markerColors[38], "Ownership marker did not use each player's color");
                _sprites.Apply([blue with { Presentation = blue.Presentation with { States = 0 } }], 1, tick, rate);
                Check(marker.VisibleInstanceCount == 0, "Ownership marker appeared without a garrison");
            }
            foreach (string name in new[] { "clinic", "market", "library", "station", "community" })
            {
                using var art = Image.LoadFromFile(ProjectSettings.GlobalizePath($"res://assets/buildings/city-{name}.png"));
                foreach (int cell in new[] { 1, 3 })
                {
                    int pixels = 0, left = (int)Math.Round(cell * art.GetWidth() / 5.0), right = (int)Math.Round((cell + 1) * art.GetWidth() / 5.0);
                    for (int y = 85; y < 645; y += 2) for (int x = left; x < right; x += 2)
                    { var color = art.GetPixel(x, y); if (color.A > 0.5f && color.R > 0.7f && color.B > 0.7f && color.G < 0.35f) pixels++; }
                    Check(pixels > 20, $"Missing team-color trim: {name}, state {cell}");
                }
            }
            _sprites.Apply(poses, 1, tick, rate, weapons);
            var overrides = _manifest.Settings.Presentation.ModelOverrides;
            bool directionalRadar = overrides.TryGetValue("headquarters", out var radarModel) && radarModel == "iso-radar";
            bool directionalPower = overrides.TryGetValue("power-plant", out var powerModel) && powerModel == "iso-power-plant";
            if (directionalRadar)
            {
                var station = poses.Single(p => p.Id.Value == 4);
                var first = SpriteFrame("radar-dish");
                _sprites.Apply([station], 1, tick + rate, rate);
                Check(SpriteFrame("radar-dish").Y != first.Y, "Radar did not advance through the directional atlas");
                _sprites.Apply(poses, 1, tick, rate, weapons);
            }
            else
            {
                var radar = Batch("radar").Multimesh.Buffer; float angle = (float)((tick + 1) / (double)rate * 65 * Math.PI / 180);
                Check(Math.Abs(radar[0] - MathF.Cos(angle) * 1.45f) < 0.001f && Math.Abs(radar[8] - MathF.Sin(angle) * 1.45f) < 0.001f, "Radar did not rotate in simulation time");
            }
            var power = (directionalPower ? IsometricBatch("power-plant") : Batch("power-plant")).Multimesh;
            var colors = power.Buffer;
            Check(power.VisibleInstanceCount == 5 && colors[2 * 20 + 12] < 0.6f && colors[1 * 20 + 12] > 0.99f && colors[3 * 20 + 19] > 0.005f && colors[4 * 20 + 19] > 0.005f, "Normal/damaged/working/combined states are not distinct");
            bool directionalDoors = _manifest.Settings.Presentation.ModelOverrides.TryGetValue("barracks", out var barracksModel) && barracksModel == "iso-barracks";
            if (directionalDoors)
            {
                var barracks = poses.Single(p => p.Id.Value == 2);
                var factory = poses.Single(p => p.Id.Value == 3);
                Check(SpriteFrame("barracks").X > 0, "The scheduled barracks door did not advance");
                CheckDoor(barracks, "barracks", 0, 4);
                CheckDoor(factory, "factory", 0, 8);
                CheckDoor(factory, "factory", _catalog.Units[factory.UnitType].StateMask("heavy"), 8, 4);
                CheckFactoryExit(factory.UnitType);
            }
            else
            {
                bool openDoor = _sprites.Node.GetChildren().OfType<MultiMeshInstance3D>().Any(n => n.Multimesh.VisibleInstanceCount > 0 &&
                    n.Multimesh.Mesh.SurfaceGetMaterial(0) is ShaderMaterial m && m.GetShaderParameter("sprite_texture").AsGodotObject() is Texture2D t && t.ResourcePath.EndsWith("door-strip.png", StringComparison.Ordinal) && m.GetShaderParameter("uv_rect").AsVector4().X > 0.66f);
                Check(openDoor, "Door overlay did not advance to the open frame");
            }
            bool animatedCrane = overrides.TryGetValue("crane", out var craneModel) && craneModel == "iso-crane";
            if (animatedCrane) CheckCrane();
            var bunkerPreview = overrides.TryGetValue("bunker-upgradable", out var bunkerModel) && bunkerModel == "iso-bunker"
                ? CheckBunker() : [];
            var generatorPreview = overrides.TryGetValue("stealth-generator", out var stealthModel) &&
                stealthModel == "iso-stealth-generator" ? CheckGenerators() : [];
            var layerA = poses.Single(p => p.Id.Value == 17); var layerB = poses.Single(p => p.Id.Value == 18) with { Previous = layerA.Current, Current = layerA.Current };
            _sprites.Apply([layerB, layerA], 1, tick, rate);
            var overlap = (directionalPower ? IsometricBatch("power-plant") : Batch("power-plant")).Multimesh.Buffer;
            Check(overlap[20 + 7] > overlap[7] + 0.0009f, "Packed/input order changed visual stacking priority");
            _sprites.Apply(poses, 1, tick, rate, weapons);
            int nodes = _sprites.Node.GetChildCount(); var stress = new EntityPose[10000];
            for (int i = 0; i < stress.Length; i++)
            { var position = new WorldPosition((i % 100 * 5 + 5) * 1024, (i / 100 * 5 + 5) * 1024); stress[i] = layerA with { Id = new((uint)i + 1), Previous = position, Current = position }; }
            _sprites.Apply(stress, 1, tick, rate); Check(_sprites.Node.GetChildCount() == nodes && _sprites.SpriteCount == 10000, "Footprint renderer created per-building nodes or lost instances");
            _sprites.Apply(poses, 1, tick, rate, weapons);
            int liveSprites = _sprites.SpriteCount;
            _sprites.Accept(new(tick + 10, WorldEventKind.Destroyed, layerA.Id, layerA.Current, layerA.UnitType, Owner: 1));
            _sprites.Apply(poses, 1, tick, rate, weapons);
            Check(_sprites.SpriteCount == liveSprites, "A future death event replaced or duplicated a live frame");
            _sprites.Apply(poses.Where(p => p.Id != layerA.Id).ToArray(), 1, tick, rate, weapons);
            Check(_sprites.SpriteCount < liveSprites, "A future wreck appeared before its simulation tick");
            _sprites.Accept(new(tick + 11, WorldEventKind.Removed, layerA.Id));
            _sprites.Apply(poses, 1, tick, rate, weapons);
            foreach (var path in Directory.EnumerateFiles(ProjectSettings.GlobalizePath("res://assets/buildings"), "*.png"))
            { using var image = Image.LoadFromFile(path); Check(image.DetectAlpha() != Image.AlphaMode.None && image.GetPixel(0, 0).A < 0.02f, "Missing building alpha: " + path); }
            if (_capturePath is not null && DisplayServer.GetName() != "headless")
            {
                _selection.Change([], OpenRTS.Godot.Controls.SelectionChange.Replace); UpdateSelection(1); _footprintView!.Apply(null, default);
                _label!.Text = "OpenRTS · N2\nЗдания · размеры · переопределения навигации\nВнизу: обычное / повреждённое / работает / повреждённое-работает / руины";
                await CaptureBuildings("buildings-overview.png");
                _camera!.Size = 28; _camera.Position = new(31.5f, 80, 42.5f); await CaptureBuildings("buildings-states.png");
                _camera.Size = 12; _camera.Position = new(53.5f, 80, 12);
                _selection.Change([new(3)], OpenRTS.Godot.Controls.SelectionChange.Replace); UpdateFootprintPreview();
                _label.Text = "Завод · проходимый ремонтный двор\nСиний — запрет · зелёный — остановка · оранжевый — транзит";
                await CaptureBuildings("buildings-factory.png");
                if (!directionalDoors) await CaptureDoorOcclusion(poses, tick, rate);
                if (animatedCrane && directionalRadar && directionalPower && overrides.ContainsKey("helipad"))
                {
                    _selection.Change([], OpenRTS.Godot.Controls.SelectionChange.Replace);
                    UpdateSelection(1);
                    _footprintView.Apply(null, default);
                    foreach (var layer in GetChildren().OfType<CanvasLayer>()) layer.Hide();
                    var infrastructure = new[] { "power-plant", "headquarters", "crane", "helipad" }.Select((id, i) =>
                    {
                        var type = _catalog.Units.ToList().FindIndex(unit => unit.Id == id);
                        var at = new WorldPosition((22 + 4 * i) * 1024, (40 - 4 * i) * 1024);
                        var facing = FacingDirection.North;
                        return new EntityPose(new((uint)(i + 1)), type, at, at,
                            new(1, facing, facing, facing, facing, new(_catalog.Units[type].IdleAnimation, 0, 0), AnimationPlayback.None, 0),
                            Health: _catalog.Units[type].MaxHealth);
                    }).ToArray();
                    _camera.Size = 15;
                    _camera.Position = new Vector3(28, 0, 34) + _camera.Basis.Z * 60 + _camera.Basis.Y * 0.5f;
                    _sprites.Apply(infrastructure, 1, 0, rate);
                    await CaptureBuildings("buildings-infrastructure.png");
                }
                if (bunkerPreview.Length > 0)
                {
                    _camera!.Size = 7.65f;
                    _camera.Position = new Vector3(31, 0, 37) + _camera.Basis.Z * 60 + _camera.Basis.Y * 0.45f;
                    _sprites.Apply(bunkerPreview, 1, 0, rate);
                    await CaptureBuildings("buildings-bunker.png");
                }
                if (generatorPreview.Length > 0)
                {
                    _camera!.Size = 9.5f;
                    _camera.Position = new Vector3(34, 0, 35) + _camera.Basis.Z * 60 + _camera.Basis.Y * 0.75f;
                    _sprites.Apply(generatorPreview, 1, rate, rate);
                    await CaptureBuildings("buildings-generators.png");
                }
            }
            GD.Print($"OPENRTS_BUILDINGS:PASS footprint terrain, wrecks, sizes, states, radar, doors, stacking and 10000-building batches; nodes={nodes}");
            GD.Print("OPENRTS_SIMULATION:" + JsonSerializer.Serialize(report)); GetTree().Quit();

            MultiMeshInstance3D IsometricBatch(string file) => _sprites.Node.GetChildren().OfType<MultiMeshInstance3D>()
                .Single(node => node.Multimesh.VisibleInstanceCount > 0 &&
                    node.Multimesh.Mesh.SurfaceGetMaterial(0) is ShaderMaterial material &&
                    material.GetShaderParameter("sprite_texture").AsGodotObject() is Texture2D texture &&
                    texture.ResourcePath.EndsWith("/isometric/" + file + ".png", StringComparison.Ordinal));

            Vector4 SpriteFrame(string file)
            {
                var material = (ShaderMaterial)IsometricBatch(file).Multimesh.Mesh.SurfaceGetMaterial(0);
                return material.GetShaderParameter("uv_rect").AsVector4();
            }

            void CheckCrane()
            {
                int craneType = _catalog.Units.ToList().FindIndex(unit => unit.Id == "crane");
                var powerPlant = poses.Single(p => p.Id.Value == 1);
                var crane = powerPlant with { UnitType = craneType, Order = default,
                    Presentation = powerPlant.Presentation with { States = 0 } };
                _sprites.Apply([crane], 0, 0, rate);
                Check(SpriteFrame("crane-arm").Y == 0.5f, "Idle crane did not face its work bay");
                EntityId site = new(10001);
                var engaged = crane with { Order = new(UnitActionKind.Build, OrderPhase.Engaging, default, site, 0, 0) };
                var target = crane.Current with { X = crane.Current.X + 5 * 1024 };
                _sprites.Apply([engaged], 0, rate / 4, rate, projects: [new(site, 1, powerPlant.UnitType, target, false)]);
                var work = SpriteFrame("crane-arm");
                Check(work.Y == 0.25f && work.X > 0, "Crane did not face the site and lower its hook");
                _sprites.Apply([crane with { WeaponCount = 1 }], 0, tick + 6, rate,
                    [new(crane.Id, 0, FacingDirection.North, FacingDirection.North, new(_catalog.Units[craneType].AnimationIndex("produce"), tick, 15), 0, 0, site)]);
                var repair = SpriteFrame("crane-arm");
                Check(repair.Y == 0 && repair.X > 0, "Crane did not use the repair mount's facing and work animation");
            }

            EntityPose[] CheckBunker()
            {
                var units = _catalog!.Units.ToList();
                int bunkerType = units.FindIndex(u => u.Id == "bunker");
                int builderType = units.FindIndex(u => u.Id == "construction-engineer");
                var preview = new EntityPose[3];
                foreach (var (upgrade, index) in new[] { ("cannon", 1), ("mixed", 2) })
                {
                    using var world = new SimulationWorld(units, 64 * 1024, 64 * 1024, players: [new(1, "Blue", 10000)]);
                    var at = new WorldPosition((28 + index * 3) * 1024, (40 - index * 3) * 1024);
                    var bunker = world.AddInitialUnit(bunkerType, at, 1);
                    var builder = world.AddInitialUnit(builderType, at with { Y = at.Y + 2048 }, 1);
                    var actors = new EntityPose[world.Count];
                    world.CopyPoses(actors);
                    _sprites.Apply([actors[0]], 1, 0, rate);
                    Check(_sprites.SpriteCount == 1, "An unarmed bunker should show only the hexagonal hull.");
                    if (index == 1)
                    {
                        var start = new WorldPosition(28 * 1024, 40 * 1024);
                        preview[0] = actors[0] with { Id = new(100), Current = start, Previous = start };
                    }
                    world.Step([new(1, 0, 1, CommandKind.StartUpgrade, builder, TargetEntity: bunker, Action: upgrade)]);
                    for (int i = 0; i < 150; i++) world.Step();
                    world.CopyPoses(actors);
                    var upgraded = actors[0];
                    Check(upgraded.Upgrade.Completed == upgrade, $"The bunker {upgrade} upgrade failed.");
                    _sprites.Apply([upgraded], 1, 0, rate);
                    string module = $"bunker-{upgrade}-turret";
                    var first = SpriteFrame(module);
                    Check(_sprites.SpriteCount == 2, $"The {upgrade} upgrade must occupy one turret mount.");
                    var channels = new WeaponPose[world.WeaponCount];
                    world.CopyWeaponPoses(channels);
                    var east = FacingDirection.FromQuarterTurns(1);
                    for (int i = 0; i < channels.Length; i++)
                        channels[i] = channels[i] with { PreviousFacing = east, Facing = east };
                    _sprites.Apply([upgraded], 1, 0, rate, channels);
                    Check(Math.Abs(SpriteFrame(module).Y - 0.25f) < 0.001f && SpriteFrame(module).Y != first.Y,
                        $"The {upgrade} turret did not turn independently of the hull.");
                    var demo = index == 1 ? FacingDirection.FromQuarterTurns(2) : east;
                    preview[index] = upgraded with { Id = new((uint)(100 + index)), WeaponCount = 0,
                        Presentation = upgraded.Presentation with { PreviousWeaponFacing = demo, WeaponFacing = demo } };
                }
                _sprites.Apply(poses, 1, tick, rate, weapons);
                return preview;
            }

            EntityPose[] CheckGenerators()
            {
                var units = _catalog!.Units.ToList();
                int Type(string id) => units.FindIndex(unit => unit.Id == id);
                int shieldType = Type("shield-generator");
                var shield = units[shieldType];
                Check(shield.Footprint is { Width: 4, Height: 4 } && shield.VisionRadius == 0 &&
                    shield.Economy?.Energy == 0 && shield.Products.Count == 0 && shield.Weapons.Count == 0 &&
                    shield.Abilities.Count == 0 && shield.Sensors is { StealthRange: 0, JamRange: 0 },
                    "The shield generator must be a passive 4x4 structure.");
                using var world = new SimulationWorld(units, 40 * 1024, 40 * 1024,
                    navigationGrid: new(40, 40), players: [new(1, "Blue", 10000)]);
                var center = new WorldPosition(20 * 1024, 20 * 1024);
                var engineer = world.AddInitialUnit(Type("construction-engineer"), center with { X = 16 * 1024 }, 1);
                world.Step([new(1, 0, 1, CommandKind.PlanConstruction, engineer, center, Action: "site-shield")]);
                for (int i = 0; i < 360; i++) world.Step();
                var placed = new EntityPose[world.Count];
                world.CopyPoses(placed);
                var complete = placed.Single(p => p.UnitType == shieldType);
                Check(!complete.Construction.Active && complete.Current == center,
                    "The construction engineer did not complete the shield generator.");

                var at = new WorldPosition(31 * 1024, 38 * 1024);
                var stealth = new EntityPose(new(100), Type("stealth-generator"), at, at,
                    complete.Presentation with { States = 0 })
                    { Emitter = new(12288, 8192, 0, 50, false, true) };
                _sprites.Apply([stealth], 1, 0, rate);
                var initialFrame = SpriteFrame("stealth-emitter");
                var initialPivot = SpriteScreenPivot("stealth-emitter", stealth, "rotor");
                _sprites.Apply([stealth], 1, 50 + rate / 2, rate);
                Check(SpriteFrame("stealth-emitter").Y != initialFrame.Y, "Stealth rotor did not advance through the directional atlas.");
                var deployedPivot = SpriteScreenPivot("stealth-emitter", stealth, "rotor");
                Check(deployedPivot.IsEqualApprox(initialPivot), $"Stealth rotor mount moved from {initialPivot} to {deployedPivot}.");
                _sprites.Apply(poses, 1, tick, rate, weapons);
                var previewCenter = new WorldPosition(37 * 1024, 32 * 1024);
                return [stealth, complete with { Current = previewCenter, Previous = previewCenter }];
            }

            void CheckDoor(EntityPose pose, string file, uint state, int rows, int firstRow = 0)
            {
                int door = _catalog.Units[pose.UnitType].AnimationIndex("door");
                for (int turn = 0; turn < 4; turn++)
                {
                    var facing = FacingDirection.FromQuarterTurns(turn);
                    var presentation = pose.Presentation with { States = state, BodyFacing = facing, PreviousBodyFacing = facing,
                        Body = new(door, tick, 12), Weapon = AnimationPlayback.None };
                    var turned = pose with { QuarterTurns = turn, Presentation = presentation };
                    _sprites.Apply([turned], 1, tick, rate);
                    var frame = SpriteFrame(file);
                    Check(Math.Abs(frame.Y - (firstRow + turn) / (float)rows) < 0.001f && Math.Abs(frame.X - frame.Z) < 0.001f,
                        $"{file} selected the wrong door frame at quarter turn {turn}, state {state}.");
                    _sprites.Apply([turned], 1, tick + 8, rate);
                    Check(SpriteFrame(file).X == 0, $"{file} door did not open at quarter turn {turn}, state {state}.");
                    if (_manifest.Settings.Presentation.Camera != OpenRTS.Content.GameCamera.Isometric) continue;
                    CheckAnchor(file, turned, state == 0 ? "body" : "heavy");
                    if (state != 0) continue;
                    _sprites.Apply([], 1, tick, rate, preview: new(default, turned.Presentation.Owner, turned.UnitType,
                        turned.Current, false) { QuarterTurns = turn });
                    CheckAnchor(file, turned, "body");
                }
            }

            void CheckAnchor(string file, EntityPose pose, string partId)
            {
                var pivot = SpriteScreenPivot(file, pose, partId);
                var ground = new Vector3(pose.Current.X, pose.Current.Elevation, pose.Current.Y) / WorldPosition.UnitsPerCell;
                float error = pivot.DistanceTo(_camera!.UnprojectPosition(ground));
                Check(error < 0.05f, $"{file}.{partId} at quarter turn {pose.QuarterTurns} missed its ground anchor by {error:F2} pixels.");
            }

            Vector2 SpriteScreenPivot(string file, EntityPose pose, string partId)
            {
                var model = _modelCatalog!.Models[_catalog!.Presentations[pose.UnitType].Model!];
                var part = model.Parts.Single(p => p.Id == partId);
                var sprite = IsometricBatch(file).Multimesh.Buffer;
                float x = (float)part.PivotX - 0.5f, z = (float)part.PivotY - 0.5f;
                var pivot = new Vector3(sprite[3] + x * sprite[0] + z * sprite[2],
                    sprite[7] + x * sprite[4] + z * sprite[6], sprite[11] + x * sprite[8] + z * sprite[10]);
                return _camera!.UnprojectPosition(pivot);
            }

            void CheckFactoryExit(int factoryType)
            {
                var catalog = _catalog!;
                int Type(string id) => catalog.Units.ToList().FindIndex(unit => unit.Id == id);
                int powerPlant = Type("power-plant"), apc = Type("apc-mg");
                var ground = catalog.Locomotion!.TerrainIndex("grass");
                var center = new WorldPosition(10752, 10752);
                for (int turn = 0; turn < 4; turn++)
                {
                    var grid = new NavigationGrid(20, 20, terrain: Enumerable.Repeat(ground, 400));
                    using var world = new SimulationWorld(catalog.Units, 20 * 1024, 20 * 1024,
                        navigationGrid: grid, players: [new(1, "Blue", 10000)]);
                    var factory = world.AddInitialMapUnit(factoryType, center, 1, isStart: false, quarterTurns: turn);
                    world.AddInitialUnit(powerPlant, new(5632, 4096), 1);
                    var (x, y) = turn switch { 1 => (-3328, 0), 2 => (0, -3328), 3 => (3328, 0), _ => (0, 3328) };
                    int apron = grid.CellAt(new(center.X + x / 2, center.Y + y / 2));
                    Check(!world.NavigationGrid!.IsBlocked(apron) && !world.NavigationGrid.CanStop(apron)
                        && world.NavigationGrid.IsBlocked(grid.CellAt(center)), "Factory footprint does not match the assembly hall and apron.");
                    world.Step([new(1, 0, 1, CommandKind.EnqueueTask, factory, Action: "apc")]);
                    while (world.Count == 2 && world.Tick < 300) world.Step();
                    Check(world.Count == 3, $"APC production did not finish at quarter turn {turn}.");
                    var poses = new EntityPose[world.Count];
                    world.CopyPoses(poses);
                    Check(poses.Single(p => p.UnitType == apc).Current == new WorldPosition(center.X + x, center.Y + y),
                        $"APC did not emerge from the center of the apron at quarter turn {turn}.");
                }
            }
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
        static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        MultiMeshInstance3D Batch(string name) => _sprites!.Node.GetChildren().OfType<MultiMeshInstance3D>().First(n => n.Name == "Sprite_" + name);
    }
    private async Task CaptureBuildings(string name)
    {
        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_capturePath!))!, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        using var image = GetViewport().GetTexture().GetImage(); if (image.SavePng(path) != Error.Ok) throw new IOException("Building capture failed.");
    }
    private async Task CaptureDoorOcclusion(EntityPose[] poses, long tick, int rate)
    {
        var building = poses.Single(p => p.Id.Value == 2); var actor = poses.Single(p => p.Id.Value == 12);
        var position = building.Current with { Y = building.Current.Y + 1024 };
        actor = actor with { Previous = position, Current = position, Presentation = actor.Presentation with { Body = new(_catalog!.Units[actor.UnitType].IdleAnimation, 0, 0), Weapon = AnimationPlayback.None } };
        _camera!.Size = 4; _camera.Position = new(building.Current.X / 1024f, 80, position.Y / 1024f);
        _footprintView!.Apply(null, default); _selection.Change([], OpenRTS.Godot.Controls.SelectionChange.Replace); UpdateSelection(1);
        int closed = 0, opened = 0;
        for (int phase = 0; phase < 2; phase++)
        {
            var view = building.Presentation with { Weapon = phase == 0 ? AnimationPlayback.None : new(_catalog.Units[building.UnitType].AnimationIndex("door"), tick - 23, 40) };
            _sprites!.Apply([building with { Presentation = view }, actor], 1, tick, rate);
            _label!.Text = phase == 0 ? "Дверь закрыта · перекрывает юнита" : "Дверь открыта · юнит виден в проёме";
            await CaptureBuildings(phase == 0 ? "buildings-door-closed.png" : "buildings-door-open.png");
            using var screenshot = GetViewport().GetTexture().GetImage(); int pixels = TeamPixels(screenshot, actor, true);
            if (phase == 0) closed = pixels; else opened = pixels;
        }
        if (opened <= closed + 6) throw new InvalidOperationException($"Door overlay failed to occlude/reveal unit: {closed}/{opened} blue pixels.");
        GD.Print($"OPENRTS_DOOR_OCCLUSION:PASS closed={closed},open={opened}");
    }
}
