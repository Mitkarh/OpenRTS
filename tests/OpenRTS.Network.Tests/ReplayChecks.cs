using System.Diagnostics;
using DeclarativeContent;
using OpenRTS.Content;
using OpenRTS.Core;
using OpenRTS.Core.Orders;
using OpenRTS.Core.Presentation;
using OpenRTS.Network;
using OpenRTS.Samples;

internal static class ReplayChecks
{
    private static readonly Lazy<UnitCatalog> Catalog = new(() => GameManifest.Load("sandbox/game.yaml").LoadUnits());
    private static ContentSource Read(string path) => new(path, File.ReadAllText(Path.Combine("sandbox", path)));
    private static SimulationWorld World(string recipe, int workers = 1) => recipe switch {
        "production" => BuildingScenario.Create(Catalog.Value, workers),
        "aviation" => AviationScenario.Load(Catalog.Value, Read).CreateWorld(workers, restrictPlayerCommands: true),
        "sensors" => FogScenario.Create(Catalog.Value, holdTicks: 12, sensors: true),
        _ => throw new InvalidDataException("Unknown replay fixture.")
    };
    private sealed record History(byte[] Journal, MatchDescriptor Descriptor, string[] Hashes, WorldCommand[][] Commands);
    private static History Create(string recipe, int ticks)
    {
        using var world = World(recipe); world.RestrictCommandsToPlayers();
        var descriptor = new MatchDescriptor("replay-fixture", Catalog.Value.Summary.Fingerprint, recipe, 1, EndTick: ticks);
        var state = new MatchState(Guid.NewGuid(), 1, 0, MatchPhase.Playing, [new(1, SlotController.Human, true, true), new(2, SlotController.Human, true, true)], "start");
        using var output = new MemoryStream(); using var log = new StarJournal(output, descriptor, world.Report().StateHash, StarProtocol.WriteState(state), recipe);
        WorldCommand[] input = recipe switch { "production" => ProductionScenario.Commands(Catalog.Value), "aviation" => AviationScenario.Commands, _ => FogScenario.RadarCommands };
        var hashes = new string[ticks + 1]; hashes[0] = world.Report().StateHash;
        var commands = new WorldCommand[ticks + 1][]; commands[0] = [];
        long[] sequence = new long[3];
        for (long tick = 1; tick <= ticks; tick++)
        {
            var poses = new EntityPose[world.Count]; world.CopyPoses(poses);
            commands[tick] = input.Where(c => c.Tick == tick && Wire.Kinds.Contains(c.Kind)).Select(c => {
                var entity = c.Entity != default ? c.Entity : c.Recipients?.Ids[0] ?? default;
                uint owner = poses.FirstOrDefault(p => p.Id == entity).Presentation.Owner;
                if (owner is < 1 or > 2) owner = 1;
                return c.WithStamp(tick, owner, ++sequence[owner]);
            }).OrderBy(c => c.Issuer).ThenBy(c => c.Sequence).ToArray();
            world.Step(commands[tick]); hashes[tick] = world.Report().StateHash;
            log.Write(StarProtocol.TurnFrame(tick, state.Revision));
            for (uint p = 1; p <= 2; p++) log.Write(Wire.Batch(state.Match, p, tick, commands[tick].Where(c => c.Issuer == p).ToArray()));
            log.Write(StarProtocol.Check(StarProtocol.Hash, tick, hashes[tick]));
        }
        log.Write(StarProtocol.Check(StarProtocol.Finish, ticks, hashes[ticks])); log.Complete();
        return new(output.ToArray(), descriptor, hashes, commands);
    }
    private static void Check(bool value, string message = "Replay assertion failed") { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException or TimeoutException or OperationCanceledException) { return; }
        throw new Exception("Expected replay rejection");
    }
    internal static bool Tool(string[] args)
    {
        string? path = args.FirstOrDefault(a => a.StartsWith("--restore-checkpoint="))?[21..];
        if (path is null) return false;
        using var input = File.OpenRead(path);
        using var restored = ReplayCheckpoint.Restore(input, info => World(info.Recipe, 2));
        Console.WriteLine($"RESTORED {restored.World.Tick} {restored.World.Report().StateHash}"); return true;
    }
    internal static void Run(Action<string, Action> test)
    {
        foreach (var (recipe, ticks, checkpoint) in new[] { ("production", 100, 12), ("aviation", 420, 185), ("sensors", 48, 12) })
            test("NET4 replay recovery continues every hash: " + recipe, () => {
                var source = Create(recipe, ticks);
                using var journal = new MemoryStream(source.Journal);
                var info = StarJournal.Inspect(journal); Check(info.Recipe == recipe && info.Descriptor == source.Descriptor && info.EndTick == ticks);
                foreach (long boundary in new long[] { 0, checkpoint, ticks })
                {
                    journal.Position = 0; using var saved = new MemoryStream(); ReplayCheckpoint.Capture(journal, boundary, saved); saved.Position = 0;
                    using var restored = ReplayCheckpoint.Restore(saved, metadata => World(metadata.Recipe, 2));
                    Check(restored.World.Report().StateHash == source.Hashes[boundary]);
                    Console.WriteLine($"REPLAY_CHECKPOINT {recipe} tick={boundary} bytes={saved.Length} restoreMs={restored.RestoreTime.TotalMilliseconds:F2}");
                    for (long tick = boundary + 1; tick <= ticks; tick++) { restored.World.Step(source.Commands[tick]); Check(restored.World.Report().StateHash == source.Hashes[tick], "Recovered continuation differs at " + tick); }
                }
                Console.WriteLine($"REPLAY_RECOVERY {recipe} ticks={ticks} bytes={source.Journal.Length} hash={source.Hashes[ticks]}");
            });
        test("NET4 restart in another process restores the selected boundary", () => {
            var history = Create("production", 32); string path = Path.Combine(Path.GetTempPath(), "openrts-recovery-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var source = new MemoryStream(history.Journal)) using (var target = File.Create(path)) ReplayCheckpoint.Capture(source, 12, target);
                var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(typeof(ReplayChecks).Assembly.Location);
                start.ArgumentList.Add("--restore-checkpoint=" + path);
                using var child = Process.Start(start)!;
                if (!child.WaitForExit(15000)) { child.Kill(entireProcessTree: true); throw new TimeoutException("Recovery process timed out"); }
                string output = child.StandardOutput.ReadToEnd(), errors = child.StandardError.ReadToEnd();
                Check(child.ExitCode == 0 && output.Contains($"RESTORED 12 {history.Hashes[12]}"), errors + output);
            }
            finally { File.Delete(path); }
        });
        test("NET4 rejects truncation, corruption, quotas, wrong world and cancelled recovery", () => {
            var history = Create("production", 20);
            using var source = new MemoryStream(history.Journal); using var target = new MemoryStream(); ReplayCheckpoint.Capture(source, 12, target);
            byte[] data = target.ToArray();
            foreach (var bad in new[] { data[..^1], data[..20], data.Concat(new byte[] { 1 }).ToArray() }) Reject(() => ReplayCheckpoint.Restore(new MemoryStream(bad), info => World(info.Recipe)));
            var corrupt = data.ToArray(); corrupt[^10] ^= 1; Reject(() => ReplayCheckpoint.Restore(new MemoryStream(corrupt), info => World(info.Recipe)));
            Reject(() => ReplayCheckpoint.Restore(new MemoryStream(data), _ => World("aviation")));
            Reject(() => ReplayCheckpoint.Restore(new MemoryStream(data), info => World(info.Recipe), new(MaximumBytes: 64)));
            Reject(() => StarJournal.Inspect(new MemoryStream(history.Journal), new(MaximumTicks: 10)));
            Reject(() => StarJournal.Inspect(new MemoryStream(history.Journal[..^5])));
            using var stop = new CancellationTokenSource(); stop.Cancel(); Reject(() => ReplayCheckpoint.Restore(new MemoryStream(data), info => World(info.Recipe), cancellationToken: stop.Token));
            Reject(() => ReplayCheckpoint.Restore(new MemoryStream(data), info => { Thread.Sleep(30); return World(info.Recipe); }, new(CatchUpTimeout: TimeSpan.FromMilliseconds(5))));
        });
        test("NET4 retains explicit v2 replay and detects a changed middle checkpoint", () => {
            var history = Create("production", 16);
            using var input = new MemoryStream(history.Journal); using var reader = new BinaryReader(input);
            int header = reader.ReadInt32(); _ = reader.ReadBytes(header);
            using var legacy = new MemoryStream(); using var writer = new BinaryWriter(legacy, System.Text.Encoding.UTF8, true);
            var oldHeader = Wire.Pack(w => { w.Write((byte)40); w.Write(2); Wire.Text(w, Wire.Hash(DirectMatch.Descriptor(history.Descriptor))); Wire.Text(w, history.Hashes[0]); });
            writer.Write(oldHeader.Length); writer.Write(oldHeader); writer.Write(history.Journal.AsSpan((int)input.Position));
            legacy.Position = 0; Check(StarJournal.Replay(legacy, history.Descriptor, () => World("production")).StateHash == history.Hashes[16]);
            legacy.Position = 0; Reject(() => StarJournal.ReadInfo(legacy));
            input.Position = 0; using var tampered = new MemoryStream(); using var changed = new BinaryWriter(tampered, System.Text.Encoding.UTF8, true);
            while (input.Position < input.Length)
            {
                int size = reader.ReadInt32(); byte[] frame = reader.ReadBytes(size);
                if (frame[0] == StarProtocol.Hash && StarProtocol.ReadCheck(frame, StarProtocol.Hash).Tick == 5) frame = StarProtocol.Check(StarProtocol.Hash, 5, new string('A', 64));
                changed.Write(frame.Length); changed.Write(frame);
            }
            tampered.Position = 0;
            try { StarJournal.Replay(tampered, history.Descriptor, () => World("production")); throw new Exception("Changed world hash was accepted"); }
            catch (DesyncException error) { Check(error.Tick == 5); }
        });
        test("NET4 owner playback pauses, steps, seeks backwards and validates end", () => {
            var history = Create("production", 24); string path = Path.Combine(Path.GetTempPath(), "openrts-playback-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(path, history.Journal);
            try
            {
                using var session = new ReplaySession(path, info => World(info.Recipe));
                void Wait(Func<bool> ready)
                {
                    var clock = Stopwatch.StartNew();
                    while (!ready())
                    {
                        if (session.Completion.IsFaulted) session.Completion.GetAwaiter().GetResult();
                        while (session.TryReadEvent(out _)) { }
                        if (clock.Elapsed.TotalSeconds > 10) throw new TimeoutException("Playback stalled: " + session.Status);
                        Thread.Sleep(5);
                    }
                }
                Wait(() => session.Info is not null && session.Frames.AcquireLatest() is { } frame && Dispose(frame));
                Check(session.IsPaused && session.Position == 0); session.StepOnce(); Wait(() => session.Position == 1);
                session.Seek(15); Wait(() => session.Position == 15 && !session.IsSeeking);
                session.Seek(3); Wait(() => session.Position == 3 && !session.IsSeeking); Check(session.IsPaused);
                session.SetPlayback(8, false); Wait(() => session.Status == "Конец повтора"); Check(session.Position == 24 && session.IsPaused);
                session.Seek(0); Wait(() => session.Position == 0 && !session.IsSeeking);
                Reject(() => session.Seek(25));
                static bool Dispose(FrameExchange.FrameLease frame) { frame.Dispose(); return true; }
            }
            finally { File.Delete(path); }
        });
    }
}
