using System.Text;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Network;

/// <summary>NET3 ordered control/command history. Full worlds remain factory-created, not serialized.</summary>
public sealed class StarJournal : IDisposable
{
    private readonly BinaryWriter _writer;
    private long _bytes;
    internal StarJournal(Stream output, MatchDescriptor descriptor, string initialHash, byte[] initialState)
    {
        _writer = new(output, Encoding.UTF8, true);
        Write(Wire.Pack(w => { w.Write((byte)40); w.Write(2); Wire.Text(w, Wire.Hash(DirectMatch.Descriptor(descriptor))); Wire.Text(w, initialHash); }));
        Write(initialState);
    }
    internal void Write(byte[] frame)
    {
        if ((_bytes += frame.Length + 4) > 64 * 1024 * 1024) throw new IOException("Journal quota exceeded.");
        _writer.Write(frame.Length); _writer.Write(frame); _writer.Flush();
    }
    internal void Complete() => Write([StarProtocol.Complete]);
    public void Dispose() => _writer.Dispose();

    public static SimulationReport Replay(Stream input, MatchDescriptor descriptor, Func<SimulationWorld> createWorld)
    {
        using var world = createWorld();
        if (world.Tick != 0 || world.TickRate != descriptor.TickRate) throw new InvalidDataException("Expected tick-zero world.");
        world.RestrictCommandsToPlayers();
        using var reader = new BinaryReader(input, Encoding.UTF8, true);
        long bytes = 0;
        byte[] Read()
        {
            int n = reader.ReadInt32();
            if (n is < 1 or > Wire.MaximumFrame || (bytes += n + 4) > 64 * 1024 * 1024) throw new InvalidDataException("Journal limits.");
            var frame = reader.ReadBytes(n); if (frame.Length != n) throw new EndOfStreamException(); return frame;
        }
        Wire.Unpack(Read(), r => {
            if (r.ReadByte() != 40 || r.ReadInt32() != 2 || Wire.Text(r) != Wire.Hash(DirectMatch.Descriptor(descriptor)) ||
                Wire.Text(r) != world.Report().StateHash) throw new InvalidDataException("Journal initial state/descriptor mismatch.");
            return true;
        });
        var state = StarProtocol.ReadState(Read());
        if (state.Phase != MatchPhase.Playing || state.Tick != 0) throw new InvalidDataException("Missing match start.");
        long[] sequence = new long[state.Slots.Count]; bool final = false;
        while (true)
        {
            byte[] frame = Read();
            if (frame[0] == StarProtocol.State && !final)
            {
                state = ValidateTransition(state, StarProtocol.ReadState(frame), world.Tick);
            }
            else if (frame[0] == StarProtocol.Turn && !final)
            {
                long tick = StarProtocol.ReadTurn(frame, world.Tick + 1, state);
                if (descriptor.EndTick != 0 && tick > descriptor.EndTick) throw new InvalidDataException("Journal exceeds end tick.");
                var commands = new List<WorldCommand>();
                foreach (var slot in state.Slots)
                    commands.AddRange(ReadCommands(Read(), state, slot.Player, tick, sequence));
                world.Step(commands.ToArray());
                var check = StarProtocol.ReadCheck(Read(), StarProtocol.Hash);
                if (check.Tick != tick) throw new InvalidDataException("Journal checkpoint order.");
                if (check.Hash != world.Report().StateHash) throw new DesyncException(tick, world.Report().StateHash, check.Hash);
            }
            else if (frame[0] == StarProtocol.Finish && !final)
            {
                var check = StarProtocol.ReadCheck(frame, StarProtocol.Finish);
                if (check.Tick != world.Tick || check.Hash != world.Report().StateHash) throw new InvalidDataException("Journal final hash mismatch.");
                final = true;
            }
            else if (frame.AsSpan().SequenceEqual(new byte[] { StarProtocol.Complete }) && final)
            {
                if (input.ReadByte() != -1) throw new InvalidDataException("Trailing journal data.");
                return world.Report();
            }
            else throw new InvalidDataException("Incomplete or invalid journal history.");
        }
    }
    internal static MatchState ValidateTransition(MatchState previous, MatchState next, long tick)
    {
        if (next.Match != previous.Match || next.Slots.Count != previous.Slots.Count || next.Revision != previous.Revision + 1 ||
            next.Tick != tick || next.Phase == MatchPhase.Lobby || previous.Phase == MatchPhase.Finished)
            throw new InvalidDataException("Control revision/order mismatch.");
        // New human inputs require snapshot/rejoin support, which belongs to NET4.
        for (int i = 0; i < next.Slots.Count; i++)
            if (next.Slots[i].Controller == SlotController.Human && previous.Slots[i].Controller != SlotController.Human)
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
