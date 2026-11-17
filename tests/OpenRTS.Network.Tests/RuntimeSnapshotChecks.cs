using Frent;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Navigation;

internal static class RuntimeSnapshotChecks
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void Continue(Func<SimulationWorld> factory, WorldCommand[] commands, int ticks, params int[] boundaries)
    {
        using var source = factory();
        var hashes = new string[ticks + 1]; var events = new WorldEvent[ticks + 1][];
        var snapshots = new Dictionary<int, byte[]>();
        for (int tick = 0; tick <= ticks; tick++)
        {
            if (tick > 0) source.Step(commands.Where(c => c.Tick == tick).ToArray());
            hashes[tick] = source.Report().StateHash; events[tick] = source.Events.ToArray();
            if (boundaries.Contains(tick)) snapshots.Add(tick, source.CaptureSnapshot());
        }
        foreach (var (at, data) in snapshots)
        {
            using var restored = SimulationWorld.RestoreSnapshot(data, factory);
            for (int tick = at + 1; tick <= ticks; tick++)
            {
                restored.Step(commands.Where(c => c.Tick == tick).ToArray());
                Check(restored.Report().StateHash == hashes[tick], $"Snapshot continuation differs at {at}->{tick}");
                Check(restored.Events.SequenceEqual(events[tick]), $"Snapshot events differ at {at}->{tick}");
            }
        }
    }
    internal static void Run(Action<string, Action> test)
    {
        test("NET4.2 RNG, allocator and packed removal survive runtime restore", () => {
            SimulationWorld World()
            {
                var world = new SimulationWorld([new("unit", 100)], 32768, 32768, seed: 7331);
                world.AddInitialUnit(0, new(2048, 2048), 1); world.AddInitialUnit(0, new(4096, 2048), 2);
                world.AddInitialUnit(0, new(8192, 2048), 1); return world;
            }
            Continue(World, [new(1, 0, 1, CommandKind.Scatter, Position: new(20000, 20000)),
                new(2, 0, 1, CommandKind.Remove, new(1)), new(4, 0, 1, CommandKind.Spawn, Position: new(28000, 28000), Owner: 1),
                new(6, 0, 1, CommandKind.Scatter, Position: new(31000, 31000))], 20, 0, 1, 2, 4, 7);
        });
        test("NET4.2 transport, upgrade, condition expiry and subsequent events survive restore", () => {
            SimulationWorld World()
            {
                var world = new SimulationWorld([
                    new("apc", 100, states: ["armed"], transport: new(["infantry"], 4),
                        upgrades: [new("armed", 0, 6, support: true)]),
                    new("engineer", 100, constructionRange: 1536), new("infantry", 100, passenger: new("infantry"))], 20000, 18000);
                world.AddInitialUnit(0, new(8192, 8192), 1); world.AddInitialUnit(1, new(8192, 9472), 1);
                world.AddInitialUnit(2, new(8960, 8192), 1); return world;
            }
            Continue(World, [new(1, 0, 1, CommandKind.StartUpgrade, new(2), TargetEntity: new(1)),
                new(2, 0, 1, CommandKind.Enter, new(3), TargetEntity: new(1)),
                new(3, 0, 1, CommandKind.GrantCondition, new(1), DurationTicks: 4) { Condition = GameplayConditions.WeaponsDisabled },
                new(8, 0, 1, CommandKind.Unload, new(1))], 14, 0, 1, 2, 3, 5, 8);
        });
        test("NET4.2 capture work and actor transformations survive restore", () => {
            SimulationWorld World()
            {
                var world = SimulationWorld.CreateWithMechanics(new() { Transformations = [new("convert", "engineer", "scout", new("prepare", 3), new("finish", 3))] },
                    [new("engineer", 100, ["idle", "move", "prepare"], abilities: [new("capture", "", Kind: AbilityKind.Capture, Range: 2048)]),
                        new("objective", 100, movable: false, capturable: new(8)), new("scout", 80, ["idle", "move", "finish"])],
                    15360, 15360, navigationGrid: new(15, 15));
                world.AddInitialUnit(0, new(3072, 3072), 1); world.AddInitialUnit(1, new(4096, 3072), 2); return world;
            }
            Continue(World, [new(1, 0, 1, CommandKind.CaptureTarget, new(1), TargetEntity: new(2)),
                new(11, 0, 1, CommandKind.Transform, new(1), Action: "convert") { TransformHere = true }], 25, 0, 3, 7, 11, 12, 15);
        });
        test("NET4.2 navigation field budget and FIFO survive restore", () => {
            SimulationWorld World()
            {
                var blocked = Enumerable.Range(0, 64).Where(y => y != 50).Select(y => new GridCell(32, y));
                var world = new SimulationWorld([new("unit", 100)], 65536, 65536, navigationGrid: new(64, 64, blocked), fieldsPerTick: 1, cachedFields: 2);
                for (int i = 0; i < 8; i++) world.AddInitialUnit(0, new(4096, 4096 + i * 2048), 1);
                return world;
            }
            var orders = Enumerable.Range(0, 8).Select(i => new WorldCommand(1, 0, i + 1, CommandKind.Move,
                new((uint)i + 1), new(56000, 4096 + i * 2048))).ToArray();
            Continue(World, orders, 40, 1, 2, 7, 15);
        });
        test("NET4.2 supply stock, cargo, dock reservations and income survive restore", () => {
            SimulationWorld World()
            {
                var world = new SimulationWorld([
                    new("truck", 100, abilities: [new("harvest", "", AutoCast: true, Kind: AbilityKind.Harvest)], supply: new(SupplyRole.Truck, 8)),
                    new("crate", 20, movable: false, body: BodyShape.Circle(128), supply: new(SupplyRole.Mine, 8, 3, true)),
                    new("depot", 100, movable: false, footprint: new(2, 2, [new(0, 0, new(false)), new(1, 0, new(false)), new(0, 1, new(false)), new(1, 1, new(false))]), supply: new(SupplyRole.Depot))], 24576, 16384, navigationGrid: new(24, 16), speedPerTick: 512);
                world.AddInitialUnit(2, new(2048, 8192), 1); world.AddInitialUnit(0, new(8192, 8192), 1);
                world.AddInitialUnit(1, new(9216, 8192)); world.AddInitialUnit(1, new(11264, 8192)); return world;
            }
            Continue(World, [new(1, 0, 1, CommandKind.UseAbility, new(2), TargetEntity: new(3), Action: "harvest")], 100, 1, 8, 25, 50);
        });
        test("NET4.2 malformed allocator is bounded and partial worlds are disposed", () => {
            SimulationWorld World() => new([new("unit", 100)], 8192, 8192);
            using var source = World(); byte[] data = source.CaptureSnapshot();
            using (var stream = new MemoryStream(data, true))
            using (var reader = new BinaryReader(stream))
            {
                stream.Position = 53; reader.ReadString(); reader.ReadString(); reader.ReadInt64(); reader.ReadUInt32(); reader.ReadUInt64();
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan((int)stream.Position, 4), int.MaxValue);
            }
            System.Security.Cryptography.SHA256.HashData(data.AsSpan(53)).CopyTo(data, 21);
            SimulationWorld? partial = null;
            try { SimulationWorld.RestoreSnapshot(data, () => partial = World()); throw new Exception("Malformed allocator accepted"); }
            catch (InvalidDataException) { }
            Check(partial is not null, "Fixture failed before the owned world was created");
            try { partial!.Report(); throw new Exception("Partial restore leaked its world"); } catch (ObjectDisposedException) { }
        });
        test("NET4.2 registered mechanic has an explicit versioned snapshot reader", () => {
            SimulationWorld World() => SimulationWorld.CreateWithMechanics(new() {
                EndOfTick = new("test.snapshot-counter", 1, () => new Counter()) }, [new("unit", 100)], 8192, 8192);
            Continue(World, [], 18, 0, 7, 12);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            using var world = World();
            try { world.CaptureSnapshot(cancelled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
        });
    }
    private sealed class Counter : IEndOfTickRule, IRuntimeSnapshotMechanic
    {
        private long _work;
        public void Advance(GameplayTick tick) => _work += tick.Tick;
        public void WriteState(BinaryWriter writer) => writer.Write(_work);
        public void WriteSnapshot(BinaryWriter writer) => writer.Write(_work);
        public void ReadSnapshot(BinaryReader reader) => _work = reader.ReadInt64();
    }
}
