using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Content;
using DeclarativeContent;

namespace OpenRTS.Samples;

public static class AviationScenario
{
    public const string Map = "content/maps/aviation.yaml";
    public const int Ticks = 1200;
    public static MapMatch Load(UnitCatalog catalog, Func<string, ContentSource> read)
    {
        var source = MapSource.FromM1(read(Map), catalog);
        var field = source.Objects.Single(obj => obj.Id == "airfield");
        var fighter = source.Objects.Single(obj => obj.Id == "fighter");
        var stand = catalog.Units.Single(unit => unit.Id == field.Type).Airbase!.Slots[0];
        var parking = new WorldPosition(field.Position.X + stand.X, field.Position.Y + stand.Y);
        if (fighter.Position != parking)
            source = new(source.Id, source.IsSkirmish, source.Width, source.Height, source.Terrain, source.Visual,
                source.Slots, source.Objects.Select(obj => obj == fighter ? obj with { Position = parking } : obj).ToArray(),
                source.CellSize, source.Origin, source.Surface);
        return MapCompiler.Compile(source, catalog).Map.Prepare(new Dictionary<string, PlayerDefinition>(StringComparer.Ordinal)
        {
            ["blue"] = new(1, "Синий", 12000), ["red"] = new(2, "Красный", 12000)
        });
    }
    public static WorldCommand[] Commands =>
    [
        new(1, 0, 1, CommandKind.Move, new(4), new(48 * 1024, 20 * 1024)),
        new(1, 0, 2, CommandKind.Move, new(5), new(46 * 1024, 36 * 1024)),
        new(1, 0, 3, CommandKind.SetRally, new(3), new(30 * 1024, 25 * 1024)),
        new(1, 0, 4, CommandKind.PlanConstruction, new(13), new(29184, 28672), Action: "site-power"),
        new(1, 0, 5, CommandKind.Enter, Recipients: new([new(18), new(19)]), TargetEntity: new(15)),
        new(2, 0, 1, CommandKind.EnqueueTask, new(2), Action: "fighter", Amount: 4),
        new(2, 0, 2, CommandKind.EnqueueTask, new(3), Action: "tiltrotor"),
        new(2, 0, 3, CommandKind.UseAbility, new(14), TargetEntity: new(16), Action: "harvest"),
        new(2, 0, 4, CommandKind.StartUpgrade, new(13), TargetEntity: new(16), Queued: true),
        new(3, 0, 1, CommandKind.CaptureTarget, new(13), TargetEntity: new(20), Queued: true),
        new(180, 0, 1, CommandKind.AttackTarget, new(4), TargetEntity: new(10)),
        new(180, 0, 2, CommandKind.AttackTarget, new(5), TargetEntity: new(10)),
        new(180, 0, 3, CommandKind.Move, new(15), new(26624, 41984)),
        new(360, 0, 1, CommandKind.UnloadAt, new(15), new(26624, 41984)),
        new(400, 0, 1, CommandKind.ReturnToBase, new(5))
    ];
}
