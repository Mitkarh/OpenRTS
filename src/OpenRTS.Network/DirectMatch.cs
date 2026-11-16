using System.Net.Sockets;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Network;

/// <summary>Immutable identity of rules, map, scenario, seed and simulation settings supplied by the game.</summary>
public sealed record MatchDescriptor(string Rules, string ContentHash, string ScenarioHash, ulong Seed,
    int TickRate = 25, int InputDelay = 2, long EndTick = 100);

public sealed class DesyncException(long tick, string local, string remote)
    : IOException($"Desync at tick {tick}: local={local}, remote={remote}")
{
    public long Tick { get; } = tick;
}

/// <summary>NET1 trusted-peer LAN match. Run on a dedicated owner thread; owns socket and world until completion.</summary>
public static class DirectMatch
{
    internal static byte[] Descriptor(MatchDescriptor d) => Wire.Pack(w => {
        if (d.TickRate is < 1 or > 1000 || d.InputDelay is < 1 or > 16 || d.EndTick is < 0 or > 1000000 ||
            string.IsNullOrWhiteSpace(d.Rules) || string.IsNullOrWhiteSpace(d.ContentHash) || string.IsNullOrWhiteSpace(d.ScenarioHash))
            throw new ArgumentException("Invalid match descriptor.");
        Wire.Text(w, d.Rules); Wire.Text(w, d.ContentHash); Wire.Text(w, d.ScenarioHash);
        w.Write(d.Seed); w.Write(d.TickRate); w.Write(d.InputDelay); w.Write(d.EndTick);
    });

    public static SimulationReport Run(TcpClient connection, uint player, MatchDescriptor descriptor,
        Func<SimulationWorld> createWorld, Func<long, IReadOnlyList<WorldCommand>> collect,
        Stream journal, CancellationToken cancellationToken = default, TimeSpan? timeout = null,
        Action<SimulationWorld>? afterTick = null, bool realtime = false,
        Action<string>? status = null, Stream? transport = null, Func<bool>? leaveRequested = null)
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
            w.Write((byte)1); w.Write(2); w.Write(player); w.Write(match.ToByteArray());
            w.Write(settings.Length); w.Write(settings); Wire.Text(w, world.Report().StateHash);
        });
        byte[] other = Wire.Exchange(stream, hello, limit, cancellationToken);
        Wire.Unpack(other, r => {
            if (other.Length > 4096 || r.ReadByte() != 1 || r.ReadInt32() != 2 || r.ReadUInt32() != 3 - player)
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
        var own = new TickInbox(match, player, descriptor.InputDelay);
        var remoteInbox = new TickInbox(match, 3 - player, descriptor.InputDelay);
        if (realtime) afterTick?.Invoke(world);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long sequence = 0;
        byte[]? lastCheck = null;
        try
        {
            for (long n = 0; descriptor.EndTick == 0 || n < descriptor.EndTick; n++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (realtime)
                {
                    status?.Invoke("Игра");
                    double wait = 1000.0 / descriptor.TickRate - clock.Elapsed.TotalMilliseconds;
                    if (wait > 0 && cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(wait)))
                        cancellationToken.ThrowIfCancellationRequested();
                    clock.Restart(); // Do not accumulate catch-up debt during a network stall.
                }
                long target = n + descriptor.InputDelay;
                var input = descriptor.EndTick == 0 || target <= descriptor.EndTick ? collect(target) : Array.Empty<WorldCommand>();
                if (input.Count > 256) throw new InvalidDataException("Command count limit.");
                var stamped = input.Select(c => c.WithStamp(target, player, checked(++sequence))).ToArray();
                byte[] sent = Wire.Batch(match, player, target, stamped);
                own.Add(sent, n);
                status?.Invoke("Ожидание пакета соперника…");
                bool leaving = leaveRequested?.Invoke() == true;
                byte[] replyBatch = Wire.Exchange(stream, leaving ? new byte[] { 5 } : sent, limit, cancellationToken, bytes => {
                    if (bytes.AsSpan().SequenceEqual(new byte[] { 5 })) return true;
                    if (lastCheck is not null && bytes.AsSpan().SequenceEqual(lastCheck)) return false;
                    var batch = Wire.ReadBatch(bytes);
                    remoteInbox.Add(bytes, n);
                    return batch.Tick == target;
                });
                if (replyBatch.AsSpan().SequenceEqual(new byte[] { 5 }) || leaving) break;
                long tick = n + 1;
                WorldCommand[] commands = tick < descriptor.InputDelay ? [] :
                    own.Take(tick).Concat(remoteInbox.Take(tick)).OrderBy(c => c.Issuer).ThenBy(c => c.Sequence).ToArray();
                world.Step(commands, cancellationToken);
                string hash = world.Report().StateHash;
                log.Tick(match, tick, commands, hash);
                byte[] check = Wire.Pack(w => { w.Write((byte)4); w.Write(tick); Wire.Text(w, hash); });
                byte[] reply = Wire.Exchange(stream, check, limit, cancellationToken, bytes => {
                    if (bytes[0] != 3) return true;
                    remoteInbox.Add(bytes, n);
                    return false;
                });
                Wire.Unpack(reply, r => {
                    if (reply.Length > 4096 || r.ReadByte() != 4 || r.ReadInt64() != tick) throw new InvalidDataException("Expected tick hash.");
                    var h = Wire.Text(r); if (h != hash) throw new DesyncException(tick, hash, h);
                    return true;
                });
                lastCheck = check;
                status?.Invoke("Игра");
                // Publication follows validation of the completed tick, never network I/O mutation.
                afterTick?.Invoke(world);
            }
            log.Complete();
            return world.Report();
        }
        catch (Exception error) { log.Failure(error); throw; }
    }
}
