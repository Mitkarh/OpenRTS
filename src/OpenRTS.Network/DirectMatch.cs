using System.Net.Sockets;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Network;

/// <summary>Immutable identity of rules, map, scenario, seed and simulation settings supplied by the game.</summary>
public sealed record MatchDescriptor(string Rules, string ContentHash, string ScenarioHash, ulong Seed,
    int TickRate = 25, int InputDelay = 2, long EndTick = 100, int HashInterval = 8);

public sealed class DesyncException(long tick, string local, string remote)
    : IOException($"Desync at tick {tick}: local={local}, remote={remote}")
{
    public long Tick { get; } = tick;
}

/// <summary>Bounded pipelined direct lockstep match. Run on a dedicated owner thread; owns socket and world until completion.</summary>
public static class DirectMatch
{
    internal static byte[] Descriptor(MatchDescriptor d) => Wire.Pack(w => {
        if (d.TickRate is < 1 or > 1000 || d.InputDelay is < 1 or > 16 || d.EndTick is < 0 or > 1000000 || d.HashInterval is < 1 or > 64 ||
            string.IsNullOrWhiteSpace(d.Rules) || string.IsNullOrWhiteSpace(d.ContentHash) || string.IsNullOrWhiteSpace(d.ScenarioHash))
            throw new ArgumentException("Invalid match descriptor.");
        Wire.Text(w, d.Rules); Wire.Text(w, d.ContentHash); Wire.Text(w, d.ScenarioHash);
        w.Write(d.Seed); w.Write(d.TickRate); w.Write(d.InputDelay); w.Write(d.EndTick); w.Write(d.HashInterval);
    });

    public static SimulationReport Run(TcpClient connection, uint player, MatchDescriptor descriptor,
        Func<SimulationWorld> createWorld, Func<long, IReadOnlyList<WorldCommand>> collect,
        Stream journal, CancellationToken cancellationToken = default, TimeSpan? timeout = null,
        Action<SimulationWorld>? afterTick = null, bool realtime = false,
        Action<string>? status = null, Stream? transport = null, Func<bool>? leaveRequested = null, Action<NetworkTickSample>? observe = null)
    {
        if (player is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(player));
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(10);
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var socket = connection;
        socket.NoDelay = true;
        using var world = createWorld();
        if (world.Tick != 0 || world.TickRate != descriptor.TickRate) throw new ArgumentException("Expected matching tick-zero world.");
        world.RestrictCommandsToPlayers();
        byte[] settings = Descriptor(descriptor);
        using var stream = transport ?? socket.GetStream();
        status?.Invoke("Согласование матча…");
        Guid match = player == 1 ? Guid.NewGuid() : Guid.Empty;
        byte[] hello = Wire.Pack(w => {
            w.Write((byte)1); w.Write(3); w.Write(player); w.Write(match.ToByteArray());
            w.Write(settings.Length); w.Write(settings); Wire.Text(w, world.Report().StateHash);
        });
        byte[] other = Wire.Exchange(stream, hello, limit, cancellationToken);
        Wire.Unpack(other, r => {
            if (other.Length > 4096 || r.ReadByte() != 1 || r.ReadInt32() != 3 || r.ReadUInt32() != 3 - player)
                throw new InvalidDataException("Protocol/player mismatch.");
            var remote = new Guid(r.ReadBytes(16));
            if (player == 2) { if (remote == Guid.Empty) throw new InvalidDataException("Missing match ID."); match = remote; }
            else if (remote != Guid.Empty) throw new InvalidDataException("Only host assigns match ID.");
            int length = r.ReadInt32();
            if (length != settings.Length || !r.ReadBytes(length).AsSpan().SequenceEqual(settings))
                throw new InvalidDataException("Match descriptor mismatch.");
            string hash = Wire.Text(r);
            if (hash != world.Report().StateHash) throw new DesyncException(0, world.Report().StateHash, hash);
            return true;
        });
        // Both Ready messages bind the immutable descriptor and host-assigned match identity.
        byte[] ready = Wire.Pack(w => { w.Write((byte)2); w.Write(match.ToByteArray()); Wire.Text(w, Wire.Hash(settings)); });
        if (!Wire.Exchange(stream, ready, limit, cancellationToken).AsSpan().SequenceEqual(ready))
            throw new InvalidDataException("Ready mismatch.");
        using var log = new MatchJournal(journal, settings, world.Report().StateHash);
        using var pipeline = new PeerPipeline(stream, limit, cancellationToken);
        int window = 2 * descriptor.InputDelay + descriptor.HashInterval;
        var own = new TickInbox(match, player, descriptor.InputDelay, window);
        var remoteInbox = new TickInbox(match, 3 - player, descriptor.InputDelay, window);
        var localHashes = new Dictionary<long, string>();
        var remoteHashes = new Dictionary<long, string>();
        var sealedAt = new Dictionary<long, long>();
        long sequence = 0, verified = 0, nextRemoteHash = descriptor.HashInterval;
        long stopTick = descriptor.EndTick == 0 ? long.MaxValue : descriptor.EndTick;
        long? remoteFinish = null;
        string? remoteFinishHash = null, verifiedHash = null;
        bool proposedLeave = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        double period = 1000.0 / descriptor.TickRate, nextTickDue = period;
        double Milliseconds(long timestamp) => System.Diagnostics.Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
        byte[] Checkpoint(byte kind, long tick, string hash) => Wire.Pack(w => { w.Write(kind); w.Write(tick); Wire.Text(w, hash); });
        void VerifyHashes()
        {
            while (localHashes.TryGetValue(verified + descriptor.HashInterval, out var a) &&
                remoteHashes.TryGetValue(verified + descriptor.HashInterval, out var b))
            {
                long tick = verified + descriptor.HashInterval;
                if (a != b) throw new DesyncException(tick, a, b);
                verified = tick; verifiedHash = a; localHashes.Remove(tick); remoteHashes.Remove(tick);
            }
        }
        void Accept(byte[] bytes)
        {
            if (bytes[0] == 3) { remoteInbox.Add(bytes, world.Tick); return; }
            if (bytes[0] == 5)
            {
                long stop = Wire.Unpack(bytes, r => { r.ReadByte(); return r.ReadInt64(); });
                if (stop < 1 || stop < world.Tick || stop > world.Tick + window) throw new InvalidDataException("Invalid leave boundary.");
                stopTick = Math.Min(stopTick, stop); return;
            }
            if (bytes[0] is not (4 or 6) || bytes.Length > 4096) throw new InvalidDataException("Unknown pipeline control.");
            var check = Wire.Unpack(bytes, r => { byte kind = r.ReadByte(); long tick = r.ReadInt64(); return (Kind: kind, Tick: tick, Hash: Wire.Text(r)); });
            if (check.Tick < 1 || check.Tick > world.Tick + window) throw new InvalidDataException("Hash outside bounded window.");
            if (check.Kind == 6)
            {
                if (check.Tick != stopTick || remoteFinish is not null || nextRemoteHash != (stopTick / descriptor.HashInterval + 1) * descriptor.HashInterval) throw new InvalidDataException("Unexpected final hash.");
                remoteFinish = check.Tick; remoteFinishHash = check.Hash; return;
            }
            if (check.Tick == verified && check.Hash == verifiedHash) return;
            if (remoteHashes.TryGetValue(check.Tick, out var old))
            {
                if (old != check.Hash) throw new InvalidDataException("Conflicting checkpoint.");
                return;
            }
            if (check.Tick != nextRemoteHash) throw new InvalidDataException("Checkpoint sequence gap/replay.");
            nextRemoteHash += descriptor.HashInterval;
            remoteHashes.Add(check.Tick, check.Hash); VerifyHashes();
        }
        void Drain()
        {
            for (int i = 0; i < PeerPipeline.Capacity && pipeline.TryRead(out var frame); i++) Accept(frame);
        }
        void Await(Func<bool> ready)
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            while (!ready())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Accept(pipeline.Read(limit - TimeSpan.FromMilliseconds(Milliseconds(began))));
            }
        }
        try
        {
            // Bootstrap ticks are explicit empty history; close real batches D ticks ahead of execution.
            if (realtime) afterTick?.Invoke(world);
            while (world.Tick < stopTick)
            {
                cancellationToken.ThrowIfCancellationRequested(); Drain(); pipeline.Check();
                if (world.Tick >= stopTick) break;
                if (!proposedLeave && leaveRequested?.Invoke() == true)
                {
                    proposedLeave = true; stopTick = Math.Min(stopTick, checked(world.Tick + descriptor.InputDelay));
                    pipeline.Send(Wire.Pack(w => { w.Write((byte)5); w.Write(stopTick); }));
                }
                long target = checked(world.Tick + descriptor.InputDelay);
                if (target <= stopTick)
                {
                    var input = collect(target);
                    if (input.Count > 256) throw new InvalidDataException("Command count limit.");
                    var stamped = input.Select(c => c.WithStamp(target, player, checked(++sequence))).ToArray();
                    byte[] sent = Wire.Batch(match, player, target, stamped);
                    own.Add(sent, world.Tick); sealedAt[target] = System.Diagnostics.Stopwatch.GetTimestamp();
                    pipeline.Send(sent);
                }
                if (realtime)
                {
                    status?.Invoke("Игра");
                    double now = clock.Elapsed.TotalMilliseconds;
                    if (now > nextTickDue + period) nextTickDue = now; // discard long stall debt, allow at most one catch-up tick
                    double wait = nextTickDue - now;
                    if (wait > 0 && cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(wait))) cancellationToken.ThrowIfCancellationRequested();
                    nextTickDue += period;
                }
                long waiting = System.Diagnostics.Stopwatch.GetTimestamp();
                status?.Invoke("Ожидание команд / проверки…");
                Await(() => world.Tick >= stopTick || world.Tick - verified < window);
                if (world.Tick >= stopTick) break;
                long tick = world.Tick + 1;
                if (tick >= descriptor.InputDelay) Await(() => world.Tick >= stopTick || remoteInbox.Contains(tick));
                Drain(); // A leave proposal is ordered before the sender's last required batch.
                if (tick > stopTick) break;
                var commands = tick < descriptor.InputDelay ? Array.Empty<WorldCommand>() :
                    own.Take(tick).Concat(remoteInbox.Take(tick)).OrderBy(c => c.Issuer).ThenBy(c => c.Sequence).ToArray();
                double waited = Milliseconds(waiting);
                world.Step(commands, cancellationToken);
                long appliedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                string hash = world.Report().StateHash;
                log.Tick(match, tick, commands, hash);
                if (tick % descriptor.HashInterval == 0)
                {
                    localHashes.Add(tick, hash); VerifyHashes();
                    pipeline.Send(Checkpoint(4, tick, hash));
                }
                status?.Invoke("Игра"); afterTick?.Invoke(world);
                double? latency = sealedAt.Remove(tick, out var stamp) ? System.Diagnostics.Stopwatch.GetElapsedTime(stamp, appliedAt).TotalMilliseconds : null;
                observe?.Invoke(new(tick, verified, waited, latency, pipeline.SendHighWater, pipeline.ReceiveHighWater, Milliseconds(started)));
            }
            string finalHash = world.Report().StateHash;
            pipeline.Send(Checkpoint(6, world.Tick, finalHash));
            Await(() => remoteFinish.HasValue);
            if (remoteFinish != world.Tick || remoteFinishHash != finalHash) throw new DesyncException(world.Tick, finalHash, remoteFinishHash!);
            // Finish acknowledgement ensures both owners have consumed the terminal hash before EOF.
            pipeline.Send(Checkpoint(7, world.Tick, finalHash));
            pipeline.Flush();
            AwaitFinalAck();
            log.Complete(); return world.Report();
            void AwaitFinalAck()
            {
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                while (true)
                {
                    var frame = pipeline.Read(limit - TimeSpan.FromMilliseconds(Milliseconds(began)));
                    if (frame.AsSpan().SequenceEqual(Checkpoint(7, world.Tick, finalHash))) return;
                    Accept(frame);
                }
            }
        }
        catch (Exception error) { log.Failure(error); throw; }

    }
}

/// <summary>Local wall-clock diagnostics only; excluded from world state and replay.</summary>
public sealed record NetworkTickSample(long Tick, long VerifiedTick, double WaitMilliseconds,
    double? SealedToAppliedMilliseconds, int SendQueueHighWater, int ReceiveQueueHighWater, double ElapsedMilliseconds = 0);
