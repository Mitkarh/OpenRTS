using System.Text;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Network;

/// <summary>Versioned bounded NET1 journal, not a saved world. Caller owns the stream.</summary>
public sealed class MatchJournal : IDisposable
{
    private readonly BinaryWriter _writer;
    private long _bytes;
    internal MatchJournal(Stream output, byte[] descriptor, string initialHash)
    {
        _writer = new(output, Encoding.UTF8, true);
        Write(Wire.Pack(w => { w.Write((byte)6); w.Write(3); w.Write(descriptor.Length); w.Write(descriptor); Wire.Text(w, initialHash); }));
    }
    private void Write(byte[] record)
    {
        if ((_bytes += record.Length + 4) > 64 * 1024 * 1024) throw new IOException("Journal quota exceeded.");
        _writer.Write(record.Length); _writer.Write(record); _writer.Flush();
    }
    internal void Tick(Guid match, long tick, WorldCommand[] commands, string hash)
    {
        // Two records keep the combined batch below the per-frame limit.
        foreach (uint player in new uint[] { 1, 2 })
            Write(Wire.Batch(match, player, tick, commands.Where(c => c.Issuer == player).ToArray()));
        Write(Wire.Pack(w => { w.Write((byte)7); w.Write(tick); Wire.Text(w, hash); }));
    }
    internal void Complete() => Write([8]);
    internal void Failure(Exception e)
    {
        try { Write(Wire.Pack(w => { w.Write((byte)9); Wire.Text(w, e.GetType().Name + ": " + e.Message[..Math.Min(300, e.Message.Length)]); })); }
        catch (IOException) { /* Preserve the original failure if the journal is full or unavailable. */ }
    }
    public void Dispose() => _writer.Dispose();

    public static SimulationReport Replay(Stream input, MatchDescriptor descriptor, Func<SimulationWorld> createWorld)
    {
        using var world = createWorld();
        world.RestrictCommandsToPlayers();
        using var r = new BinaryReader(input, Encoding.UTF8, true);
        long bytes = 0;
        byte[] Read()
        {
            int n = r.ReadInt32();
            if (n is < 1 or > Wire.MaximumFrame || (bytes += n + 4) > 64 * 1024 * 1024) throw new InvalidDataException("Journal limits.");
            var b = r.ReadBytes(n); if (b.Length != n) throw new EndOfStreamException(); return b;
        }
        Wire.Unpack(Read(), h => {
            var expected = DirectMatch.Descriptor(descriptor);
            if (h.ReadByte() != 6 || h.ReadInt32() != 3 || h.ReadInt32() != expected.Length ||
                !h.ReadBytes(expected.Length).AsSpan().SequenceEqual(expected) || Wire.Text(h) != world.Report().StateHash || world.Tick != 0)
                throw new InvalidDataException("Journal initial state/descriptor mismatch.");
            return true;
        });
        long[] sequences = [0, 0]; Guid? match = null;
        for (long tick = 1; ; tick++)
        {
            byte[] first = Read();
            if (first.AsSpan().SequenceEqual(new byte[] { 8 }))
            {
                if (input.ReadByte() != -1) throw new InvalidDataException("Trailing journal data.");
                return world.Report();
            }
            if (descriptor.EndTick != 0 && tick > descriptor.EndTick) throw new InvalidDataException("Journal exceeds end tick.");
            var commands = new List<WorldCommand>();
            for (uint player = 1; player <= 2; player++)
            {
                var b = Wire.ReadBatch(player == 1 ? first : Read()); match ??= b.Match;
                if (b.Tick != tick || b.Player != player || b.Match != match) throw new InvalidDataException("Journal order mismatch.");
                foreach (var c in b.Commands)
                {
                    if (c.Sequence != ++sequences[player - 1]) throw new InvalidDataException("Journal sequence gap.");
                    commands.Add(c);
                }
            }
            world.Step(commands.ToArray());
            Wire.Unpack(Read(), h => {
                if (h.ReadByte() != 7 || h.ReadInt64() != tick) throw new InvalidDataException("Missing journal hash.");
                var hash = Wire.Text(h);
                if (hash != world.Report().StateHash) throw new DesyncException(tick, world.Report().StateHash, hash);
                return true;
            });
        }
    }
}
