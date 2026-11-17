using DeclarativeContent;
using Godot;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Godot.Authoring;

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
            GD.Print($"OPENRTS_AUTHORING_CHECKS:{_checks} passed"); GetTree().Quit();
        }
        catch (Exception error) { GD.PushError(error.ToString()); GetTree().Quit(1); }
        finally { foreach (Node child in GetChildren()) child.Free(); }
    }

    private void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); _checks++; }
    private void Reject(Action action, string message)
    {
        try { action(); } catch (ContentException) { _checks++; return; }
        throw new InvalidOperationException(message);
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
