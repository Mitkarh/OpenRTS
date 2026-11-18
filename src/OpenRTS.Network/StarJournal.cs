using System.Text;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Network;

/// <summary>NET3 ordered control/command history. Full worlds remain factory-created, not serialized.</summary>
public sealed class StarJournal : IDisposable
{
    private readonly BinaryWriter _writer;
    private long _bytes;
    internal StarJournal(Stream output, MatchDescriptor descriptor, string initialHash, byte[] initialState, string recipe = "")
    {
        _writer = new(output, Encoding.UTF8, true);
        byte[] settings = Encoding.UTF8.GetBytes(recipe);
        if (settings.Length > 16384) throw new ArgumentException("Recipe limit.");
        Write(Wire.Pack(w => { w.Write((byte)40); w.Write(3); byte[] data = DirectMatch.Descriptor(descriptor);
            w.Write(data.Length); w.Write(data); Wire.Text(w, initialHash); w.Write((ushort)settings.Length); w.Write(settings); }));
        Write(initialState);
    }
    internal StarJournal(Stream output, long existingBytes) { _writer = new(output, Encoding.UTF8, true); _bytes = existingBytes; }
    internal void Write(byte[] frame)
    {
        if ((_bytes += frame.Length + 4) > 64 * 1024 * 1024) throw new IOException("Journal quota exceeded.");
        _writer.Write(frame.Length); _writer.Write(frame); _writer.Flush();
    }
    internal void Complete() => Write([StarProtocol.Complete]);
    public void Dispose() => _writer.Dispose();

    public static ReplayInfo ReadInfo(Stream input)
    {
        using var reader = new StarJournalReader(input); return reader.Info;
    }

    public static ReplayInfo Inspect(Stream input, ReplayLimits? limits = null, CancellationToken cancellationToken = default)
    {
        using var reader = new StarJournalReader(input, limits: limits, stop: cancellationToken);
        return reader.Inspect();
    }

    public static SimulationReport Replay(Stream input, MatchDescriptor descriptor, Func<SimulationWorld> createWorld,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StarJournalReader(input, descriptor, stop: cancellationToken);
        using var world = createWorld(); ValidateWorld(world, reader.Info);
        while (reader.Next() is { } turn) Apply(world, turn, cancellationToken);
        return world.Report();
    }

    public static SimulationReport Replay(Stream input, Func<ReplayInfo, SimulationWorld> createWorld,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StarJournalReader(input, stop: cancellationToken);
        using var world = createWorld(reader.Info); ValidateWorld(world, reader.Info);
        while (reader.Next() is { } turn) Apply(world, turn, cancellationToken);
        return world.Report();
    }
    internal static void ValidateWorld(SimulationWorld world, ReplayInfo info)
    {
        if (world.Tick != 0 || world.TickRate != info.Descriptor.TickRate) throw new InvalidDataException("Expected matching tick-zero world.");
        world.RestrictCommandsToPlayers();
        if (world.Report().StateHash != info.InitialHash) throw new InvalidDataException("Journal initial state/content mismatch.");
    }
    internal static void Apply(SimulationWorld world, RecordedTurn turn, CancellationToken cancellationToken)
    {
        world.Step(turn.Commands, cancellationToken);
        string hash = world.Report().StateHash;
        if (hash != turn.Hash) throw new DesyncException(turn.Tick, hash, turn.Hash);
    }
    internal static MatchState ValidateTransition(MatchState previous, MatchState next, long tick)
    {
        if (next.Match != previous.Match || next.Slots.Count != previous.Slots.Count || next.Revision != previous.Revision + 1 ||
            next.Tick != tick || next.Phase == MatchPhase.Lobby || previous.Phase == MatchPhase.Finished)
            throw new InvalidDataException("Control revision/order mismatch.");
        // Runtime admission occurs at a paused boundary; only the coordinator can emit this transition.
        for (int i = 0; i < next.Slots.Count; i++)
            if (next.Slots[i].Controller == SlotController.Human && previous.Slots[i].Controller != SlotController.Human &&
                (previous.Phase != MatchPhase.Paused || next.Phase != MatchPhase.Paused || !next.Slots[i].Connected || !next.Slots[i].Ready))
                throw new InvalidDataException("Mid-game human join is unsupported.");
        return next;
    }
    internal static WorldCommand[] ReadCommands(byte[] frame, MatchState state, uint player, long tick, long[] sequences)
    {
        var batch = Wire.ReadBatch(frame);
        if (batch.Match != state.Match || batch.Player != player || batch.Tick != tick ||
            state.Slots[(int)player - 1].Controller == SlotController.None && batch.Commands.Length != 0)
            throw new InvalidDataException("Committed batch identity/controller mismatch.");
        foreach (var command in batch.Commands)
            if (command.Sequence != ++sequences[player - 1]) throw new InvalidDataException("Committed command sequence gap.");
        return batch.Commands;
    }
}
