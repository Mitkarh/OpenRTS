using Godot;
using OpenRTS.Core;
using OpenRTS.Core.Orders;

namespace OpenRTS.Godot.Rendering;

/// <summary>Cosmetic roof flights. No simulation RNG, positions or work progress are changed.</summary>
internal sealed class WorkFlightVisual(bool[] enabled, IReadOnlyList<UnitDefinition> units)
{
    private sealed class Flight(EntityId target, FootprintDefinition? footprint, WorldPosition anchor, double start, float heading)
    {
        internal readonly EntityId Target = target;
        internal readonly FootprintDefinition? Footprint = footprint;
        internal readonly WorldPosition Anchor = anchor;
        internal readonly double Start = start;
        internal readonly float StartHeading = heading;
        internal float LastHeading = heading, EndHeading;
        internal double End = -1;
        internal Vector2 Last, EndOffset;
    }
    private readonly Dictionary<EntityId, Flight> _flights = [];
    private readonly Dictionary<EntityId, EntityPose> _targets = [];
    private readonly List<EntityId> _remove = [];

    internal void Update(ReadOnlySpan<EntityPose> poses, long tick)
    {
        bool active = false;
        foreach (var pose in poses) if (Eligible(pose)) { active = true; break; }
        if (!active && _flights.Count == 0) return;
        _targets.Clear();
        foreach (var pose in poses) _targets[pose.Id] = pose;
        foreach (var pose in poses)
        {
            if (!Eligible(pose) || !_targets.TryGetValue(pose.Order.Target, out var target)) continue;
            if (_flights.TryGetValue(pose.Id, out var old) && old.Target == target.Id && old.End < 0) continue;
            _flights[pose.Id] = new(target.Id, units[target.UnitType].WithRotation(target.QuarterTurns).Footprint, target.Current, tick,
                SpriteWorldView.Angle(pose.Presentation.BodyFacing, pose.Presentation.BodyFacing, 1));
        }
        _remove.Clear();
        foreach (var (id, flight) in _flights)
        {
            if (!_targets.TryGetValue(id, out var pose)) { _remove.Add(id); continue; }
            if (Eligible(pose) && pose.Order.Target == flight.Target) continue;
            if (flight.End < 0) { flight.End = tick; flight.EndOffset = flight.Last; flight.EndHeading = flight.LastHeading; }
            if (tick - flight.End >= 12) _remove.Add(id);
        }
        foreach (var id in _remove) _flights.Remove(id);
    }

    private bool Eligible(in EntityPose pose) => enabled[pose.UnitType] && pose.Flight.Airborne && pose.Carrier == default &&
        pose.Order.Phase == OrderPhase.Engaging && pose.Order.Kind is UnitActionKind.Build or UnitActionKind.Capture or UnitActionKind.UpgradeSupport;

    internal Vector2 Offset(in EntityPose pose, double time)
    {
        if (!_flights.TryGetValue(pose.Id, out var flight)) return Vector2.Zero;
        if (flight.End >= 0) return flight.EndOffset * (float)Math.Clamp(1 - (time - flight.End) / 12, 0, 1);
        var (segment, travel) = Leg(flight, time);
        var from = segment == 0 ? Vector2.Zero : Point(pose.Id, flight, segment - 1);
        var to = Point(pose.Id, flight, segment);
        var anchor = new Vector2((flight.Anchor.X - pose.Current.X) / 1024f, (flight.Anchor.Y - pose.Current.Y) / 1024f);
        flight.Last = anchor + from.Lerp(to, travel);
        return flight.Last;
    }

    internal float Heading(in EntityPose pose, double time, float original)
    {
        if (!_flights.TryGetValue(pose.Id, out var flight)) return original;
        if (flight.End >= 0) return Mathf.LerpAngle(flight.EndHeading, original, (float)Math.Clamp((time - flight.End) / 12, 0, 1));
        var (segment, travel) = Leg(flight, time);
        float from = segment == 0 ? flight.StartHeading : WorkHeading(pose.Id, flight, segment - 1);
        flight.LastHeading = travel == 0 ? from : Mathf.LerpAngle(from, WorkHeading(pose.Id, flight, segment), travel);
        return flight.LastHeading;
    }

    private static (long Segment, float Travel) Leg(Flight flight, double time)
    {
        double elapsed = Math.Max(0, time - flight.Start);
        const int period = 56, hold = 32;
        long segment = (long)(elapsed / period);
        float travel = (float)Math.Clamp((elapsed % period - hold) / (period - hold), 0, 1);
        travel = travel * travel * (3 - 2 * travel);
        return (segment, travel);
    }

    private static float WorkHeading(EntityId id, Flight flight, long segment) =>
        (Seed(id, flight, segment) ^ 0x9E3779B9u) % 1024 * (MathF.Tau / 1024);

    private static uint Seed(EntityId id, Flight flight, long segment)
    {
        uint seed = unchecked(id.Value * 2654435761u ^ flight.Target.Value * 2246822519u ^ (uint)segment * 3266489917u);
        seed ^= seed >> 16; seed *= 2246822519u; seed ^= seed >> 13;
        return seed;
    }

    private static Vector2 Point(EntityId id, Flight flight, long segment)
    {
        if (flight.Footprint is not { Cells.Count: > 0 } footprint) return Vector2.Zero;
        uint seed = Seed(id, flight, segment);
        // Alternate disjoint cell sets so consecutive work points differ when the footprint has multiple cells.
        int parity = footprint.Cells.Count == 1 ? 0 : (int)(segment & 1);
        int choices = (footprint.Cells.Count + 1 - parity) / 2;
        var cell = footprint.Cells[parity + 2 * (int)(seed % (uint)choices)];
        var box = footprint.Bounds(cell, default);
        return new((box.MinX + box.MaxX) / 2048f, (box.MinY + box.MaxY) / 2048f);
    }
}
