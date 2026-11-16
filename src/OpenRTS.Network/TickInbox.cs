using OpenRTS.Core.Orders;

namespace OpenRTS.Network;

/// <summary>Bounded sealed batches. Single consumer; I/O admission completes before the owner takes a batch.</summary>
internal sealed class TickInbox(Guid match, uint player, int delay, int? futureWindow = null)
{
    private readonly SortedDictionary<long, byte[]> _pending = [];
    private readonly Dictionary<long, byte[]> _recent = [];
    private long _sequence;
    internal void Add(byte[] bytes, long completed)
    {
        var b = Wire.ReadBatch(bytes);
        if (b.Match != match || b.Player != player || b.Tick > completed + (futureWindow ?? delay + 1))
            throw new InvalidDataException("Batch identity/future window mismatch.");
        if (_pending.TryGetValue(b.Tick, out var old) || _recent.TryGetValue(b.Tick, out old))
        {
            if (!old.AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Conflicting sealed batch.");
            return;
        }
        if (b.Tick <= completed) throw new InvalidDataException("Expired batch.");
        _pending.Add(b.Tick, bytes.ToArray());
    }
    internal bool Contains(long tick) => _pending.ContainsKey(tick);
    internal WorldCommand[] Take(long tick)
    {
        if (!_pending.Remove(tick, out var bytes)) throw new InvalidOperationException("Tick is not ready.");
        var commands = Wire.ReadBatch(bytes).Commands;
        foreach (var c in commands)
        {
            if (c.Sequence != checked(_sequence + 1)) throw new InvalidDataException("Command sequence gap/replay.");
            _sequence = c.Sequence;
        }
        _recent[tick] = bytes;
        _recent.Remove(tick - delay - 2);
        return commands;
    }
}
