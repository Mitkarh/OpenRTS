using System.Diagnostics;

namespace OpenRTS.Network;

public sealed record NetworkPeerLatency(uint Player, double RoundTripMilliseconds);

/// <summary>Local observation of an uncommitted tick; never part of the world or journal.</summary>
public sealed class NetworkWaitStatus
{
    private readonly long _started;
    public long Tick { get; }
    public IReadOnlyList<uint> Players { get; }
    public double Milliseconds => Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
    internal NetworkWaitStatus(long tick, uint[] players, int elapsedMilliseconds)
    {
        Tick = tick; Players = Array.AsReadOnly(players.ToArray());
        _started = Stopwatch.GetTimestamp() - (long)(elapsedMilliseconds / 1000.0 * Stopwatch.Frequency);
    }
}

// One outstanding probe; old or unsolicited echoes cannot replace the last measurement.
internal sealed class LatencyProbe(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _next, _pending, _sent;
    internal bool Pending => _pending != 0;
    internal long Start()
    {
        if (Pending) throw new InvalidOperationException("A latency probe is already pending.");
        _sent = _clock.GetTimestamp(); return _pending = ++_next;
    }
    internal double? Receive(long nonce)
    {
        if (!Pending || nonce != _pending) return null;
        _pending = 0; return _clock.GetElapsedTime(_sent).TotalMilliseconds;
    }
}
