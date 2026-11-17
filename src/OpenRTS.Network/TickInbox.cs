using OpenRTS.Core.Orders;

namespace OpenRTS.Network;

/// <summary>Bounded sealed batches. Single consumer; I/O admission completes before the owner takes a batch.</summary>
internal sealed class TickInbox(Guid match, uint player, int delay, int? futureWindow = null)
{
    private readonly SortedDictionary<long, (byte[] Frame, WorldCommand[] Commands)> _pending = [];
    private readonly Dictionary<long, byte[]> _recent = [];
    private long _sequence;
    internal void Add(byte[] bytes, long completed)
    {
        var b = Wire.ReadBatch(bytes);
        if (b.Match != match || b.Player != player || b.Tick > completed + (futureWindow ?? delay + 1))
            throw new InvalidDataException("Batch identity/future window mismatch.");
        byte[]? old = _pending.TryGetValue(b.Tick, out var pending) ? pending.Frame : _recent.GetValueOrDefault(b.Tick);
        if (old is not null)
        {
            if (!old.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Conflicting sealed batch.");
            return;
        }
        if (b.Tick <= completed) throw new InvalidDataException("Expired batch.");
        _pending.Add(b.Tick, (bytes.ToArray(), b.Commands));
    }
    internal bool Contains(long tick) => _pending.ContainsKey(tick);
    internal void Validate(long tick)
    {
        if (!_pending.TryGetValue(tick, out var batch)) throw new InvalidOperationException("Tick is not ready.");
        long sequence = _sequence;
        foreach (var c in batch.Commands)
        {
            if (c.Sequence != checked(++sequence)) throw new InvalidDataException("Command sequence gap/replay.");
        }
    }
    internal WorldCommand[] Take(long tick)
    {
        Validate(tick);
        _pending.Remove(tick, out var batch);
        if (batch.Commands.Length != 0) _sequence = batch.Commands[^1].Sequence;
        _recent[tick] = batch.Frame;
        _recent.Remove(tick - delay - 2);
        return batch.Commands;
    }
}
