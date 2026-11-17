using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using OpenRTS.Core;

namespace OpenRTS.Network;

/// <summary>A bounded history prefix, not a serialized runtime snapshot. Restore replays from tick zero.</summary>
public static class ReplayCheckpoint
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ORTS-RCP");
    public static void Capture(Stream journal, long tick, Stream output, ReplayLimits? limits = null, CancellationToken cancellationToken = default)
    {
        if (!journal.CanSeek) throw new ArgumentException("Checkpoint capture requires a seekable journal.");
        long start = journal.Position;
        limits ??= new(); limits.Validate();
        var info = StarJournal.Inspect(journal, limits, cancellationToken);
        if (tick < 0 || tick > info.EndTick) throw new ArgumentOutOfRangeException(nameof(tick));
        journal.Position = start;
        using var prefix = new MemoryStream(); using var writer = new BinaryWriter(prefix, Encoding.UTF8, true);
        void Write(byte[] frame)
        {
            if (prefix.Length + frame.Length + 4 > limits.MaximumBytes) throw new InvalidDataException("Checkpoint history quota.");
            writer.Write(frame.Length); writer.Write(frame);
        }
        using (var reader = new StarJournalReader(journal, limits: limits, stop: cancellationToken, record: Write))
        {
            while (reader.Tick < tick) _ = reader.Next() ?? throw new EndOfStreamException();
            Write(StarProtocol.Check(StarProtocol.Finish, tick, reader.Hash)); Write([StarProtocol.Complete]);
        }
        byte[] data = prefix.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        using var target = new BinaryWriter(output, Encoding.UTF8, true);
        target.Write(Magic); target.Write(1); target.Write(tick); target.Write(data.Length); target.Write(SHA256.HashData(data)); target.Write(data);
    }
    public static RecoveredReplay Restore(Stream input, Func<ReplayInfo, SimulationWorld> createWorld,
        ReplayLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new(); limits.Validate(); var clock = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(limits.Timeout);
        SimulationWorld? world = null;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            using var source = new BinaryReader(input, Encoding.UTF8, true);
            if (!source.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || source.ReadInt32() != 1) throw new InvalidDataException("Unknown replay checkpoint.");
            long tick = source.ReadInt64(); int size = source.ReadInt32();
            if (tick < 0 || tick > limits.MaximumTicks || size < 1 || size > limits.MaximumBytes) throw new InvalidDataException("Replay checkpoint limits.");
            byte[] digest = source.ReadBytes(32), data = source.ReadBytes(size);
            if (digest.Length != 32 || data.Length != size) throw new EndOfStreamException();
            if (input.ReadByte() != -1 || !CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(data))) throw new InvalidDataException("Replay checkpoint digest/trailing data.");
            deadline.Token.ThrowIfCancellationRequested();
            using var history = new MemoryStream(data, false);
            using var reader = new StarJournalReader(history, limits: limits, stop: deadline.Token);
            world = createWorld(reader.Info); StarJournal.ValidateWorld(world, reader.Info);
            while (reader.Next() is { } turn) StarJournal.Apply(world, turn, deadline.Token);
            if (world.Tick != tick) throw new InvalidDataException("Replay checkpoint boundary mismatch.");
            deadline.Token.ThrowIfCancellationRequested();
            return new(world, reader.Info with { EndTick = tick, FinalHash = reader.Hash }, data.Length, clock.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { world?.Dispose(); throw new TimeoutException("Replay recovery exceeded its catch-up budget."); }
        catch { world?.Dispose(); throw; }
    }
}

/// <summary>Caller owns this live world and must continue using it on the restoring thread.</summary>
public sealed class RecoveredReplay : IDisposable
{
    public SimulationWorld World { get; }
    public ReplayInfo Info { get; }
    public long HistoryBytes { get; }
    public TimeSpan RestoreTime { get; }
    internal RecoveredReplay(SimulationWorld world, ReplayInfo info, long bytes, TimeSpan elapsed)
    { World = world; Info = info; HistoryBytes = bytes; RestoreTime = elapsed; }
    public void Dispose() => World.Dispose();
}
