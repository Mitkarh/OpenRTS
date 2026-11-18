using OpenRTS.Core.Simulation;
using OpenRTS.Core.Visibility;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private readonly SensorState? _sensors;
    private readonly Dictionary<EntityId, (WorldPosition Position, long StoppedAt)> _fieldDeployment = [];
    private readonly List<(uint Owner, WorldPosition Position, int Jam, int Stealth)> _sensorFields = [];

    private EmitterVisual ReadEmitterVisual(Frent.Entity entity)
    {
        var id = entity.Get<EntityIdentity>().Id;
        var definition = TypeOf(entity).Sensors;
        if (definition is null || !_fieldDeployment.TryGetValue(id, out var state) || !IsOnMap(entity) || IsConstructing(entity)) return default;
        var spatial = entity.Get<SpatialState>();
        return new(definition.JamRange, definition.StealthRange, state.StoppedAt, definition.DeployTicks,
            spatial.Previous != spatial.Position, definition.StealthRange > 0);
    }

    private bool IsOpticallyConcealed(Frent.Entity entity) => _sensors is { } sensors && IsOnMap(entity) &&
        !SignatureDetected(sensors, entity, sensors.Visual, sensors.Optical, []);

    private void RefreshSensorFields()
    {
        _sensorFields.Clear();
        for (int i = 0; i < Count; i++)
        {
            var entity = _entityHandles[i];
            var profile = TypeOf(entity).Sensors;
            if (profile is null || profile.JamRange == 0 && profile.StealthRange == 0) continue;
            var id = entity.Get<EntityIdentity>().Id;
            if (!IsOnMap(entity) || IsConstructing(entity)) { _fieldDeployment.Remove(id); continue; }
            uint owner = entity.Get<OwnershipState>().Owner;
            if (owner == 0) continue;
            var position = entity.Get<SpatialState>().Position;
            if (!_fieldDeployment.TryGetValue(id, out var prior) || prior.Position != position)
                prior = (position, Tick);
            _fieldDeployment[id] = prior;
            int stealth = Tick - prior.StoppedAt >= profile.DeployTicks ? profile.StealthRange : 0;
            _sensorFields.Add((owner, position, profile.JamRange, stealth));
        }
    }

    private bool FieldSuppresses(Frent.Entity target, string channel, bool detectsStealth)
    {
        uint owner = target.Get<OwnershipState>().Owner;
        var p = target.Get<SpatialState>().Position;
        foreach (var field in _sensorFields)
        {
            if (owner != field.Owner) continue;
            int radius = channel == "radar" ? field.Jam : channel == SensorDefinition.Optical && !detectsStealth ? field.Stealth : 0;
            if (radius == 0) continue;
            long dx = (long)p.X - field.Position.X, dy = (long)p.Y - field.Position.Y;
            if (dx * dx + dy * dy <= (long)radius * radius) return true;
        }
        return false;
    }

    internal bool UsesSensors => _sensors is not null;
    internal IReadOnlyList<uint> ObservationPlayers => _sensors?.Players ?? _fog?.Players ?? Array.Empty<uint>();

    /// <summary>Grants or suppresses a catalogued signature on the simulation owner thread. Zero duration is permanent.</summary>
    public long GrantSignature(EntityId target, string signature, bool suppress = false, int durationTicks = 0) =>
        AddSensorGrant(target, signature, suppress, null, durationTicks);

    /// <summary>Adds an independent requirement for observing the target through a channel.</summary>
    public long GrantConcealment(EntityId target, ConcealmentDefinition concealment, int durationTicks = 0)
    {
        ArgumentNullException.ThrowIfNull(concealment);
        return AddSensorGrant(target, null, false, concealment, durationTicks);
    }

    private long AddSensorGrant(EntityId target, string? signature, bool suppress, ConcealmentDefinition? concealment, int durationTicks)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("Incomplete world.");
        if (_sensors is not { } sensors) throw new InvalidOperationException("This world has no sensor recipes.");
        ArgumentOutOfRangeException.ThrowIfNegative(durationTicks);
        if (!_indices.ContainsKey(target)) throw new ArgumentException("Unknown target.", nameof(target));
        int signal = signature is null ? -1 : sensors.NameId(signature);
        if (signal == -1 && concealment is null) throw new ArgumentNullException(nameof(signature));
        var gate = concealment is null ? (SensorState.Concealment?)null : sensors.Compile(concealment);
        long expiresAt = durationTicks == 0 ? long.MaxValue : checked(Tick + durationTicks);
        long token = checked(sensors.NextGrant++);
        if (!sensors.Grants.TryGetValue(target, out var grants)) sensors.Grants.Add(target, grants = []);
        grants.Add(new(token, expiresAt, signal, suppress, gate));
        return token;
    }

    public bool RevokeSensorGrant(EntityId target, long token)
    {
        ThrowIfDisposed();
        if (_failed) throw new InvalidOperationException("Incomplete world.");
        if (_sensors is not { } sensors || !sensors.Grants.TryGetValue(target, out var grants)) return false;
        int index = grants.FindIndex(g => g.Token == token);
        if (index < 0) return false;
        grants.RemoveAt(index);
        if (grants.Count == 0) sensors.Grants.Remove(target);
        return true;
    }

    private bool SignatureDetected(SensorState sensors, Frent.Entity entity, int signature, int channel, ReadOnlySpan<int> counters)
    {
        var identity = entity.Get<EntityIdentity>();
        var recipe = sensors.Recipes[identity.Type];
        bool present = Array.BinarySearch(recipe.Signatures, signature) >= 0;
        if (sensors.Grants.TryGetValue(identity.Id, out var grants))
        {
            foreach (var grant in grants)
            {
                if (grant.Signature != signature) continue;
                if (grant.Suppress) return false;
                present = true;
            }
        }
        if (!present) return false;
        bool counter = sensors.Names.TryGetValue("stealth", out int stealth) && counters.Contains(stealth);
        if (FieldSuppresses(entity, channel == sensors.Optical ? SensorDefinition.Optical :
            sensors.Names.TryGetValue("radar", out int radar) && channel == radar ? "radar" : "", counter)) return false;
        foreach (var gate in recipe.Concealments)
            if (gate.Channel == channel && !counters.Contains(gate.Counter) && Applies(gate, entity)) return false;
        if (grants is not null)
            foreach (var grant in grants)
                if (grant.Concealment is { } gate && gate.Channel == channel && !counters.Contains(gate.Counter) && Applies(gate, entity)) return false;
        return true;
    }

    private bool Applies(SensorState.Concealment gate, Frent.Entity entity) =>
        gate.Terrain is not byte terrain || NavigationGrid is { } grid && GroundContact(entity) &&
        grid.TerrainAt(entity.Get<SpatialState>().Position) == terrain;

    private LiveObservationFields? OrdinaryObservation(uint viewer, Frent.Entity entity)
    {
        if (!InOpticalCoverage(viewer, entity)) return null;
        if (_sensors is { } sensors && !SignatureDetected(sensors, entity, sensors.Visual, sensors.Optical, [])) return null;
        return LiveObservationFields.All;
    }

    internal LiveObservationFields? ModelObservation(uint viewer, EntityId id)
    {
        if (!TryGetEntity(id, out var entity)) return null;
        if (ObservationPlayers.Count == 0) return LiveObservationFields.All;
        if (_sensors is { } sensors)
        {
            int player = sensors.PlayerSlot(viewer);
            if (player < 0) return null;
            if (entity.Get<OwnershipState>().Owner == viewer || OwnsUpgradeProject(entity, viewer)) return LiveObservationFields.All;
            if (!IsOnMap(entity)) return null;
            var ordinary = OrdinaryObservation(viewer, entity);
            if (sensors.Current.TryGetValue(new(player, id), out var observation) && observation.Display == SensorDisplay.Model)
                return (ordinary ?? LiveObservationFields.None) | observation.Fields;
            return ordinary;
        }
        if (_fog!.PlayerSlot(viewer) < 0) return null;
        return entity.Get<OwnershipState>().Owner == viewer || OwnsUpgradeProject(entity, viewer) || IsOnMap(entity) && InOpticalCoverage(viewer, entity)
            ? LiveObservationFields.All : null;
    }

    private void RefreshSensors(CancellationToken cancellationToken)
    {
        if (_sensors is not { } sensors) return;
        sensors.Pending.Clear();
        sensors.Targets.Clear();
        bool builtIndex = false;
        for (int i = 0; i < Count; i++)
        {
            var source = _entityHandles[i];
            var recipe = sensors.Recipes[source.Get<EntityIdentity>().Type];
            if (recipe.Detectors.Length == 0 || !IsOnMap(source) || IsConstructing(source)) continue;
            int player = sensors.PlayerSlot(source.Get<OwnershipState>().Owner);
            if (player < 0) continue;
            cancellationToken.ThrowIfCancellationRequested();
            if (!builtIndex)
            {
                sensors.Index.Clear(Count);
                for (int j = 0; j < Count; j++)
                {
                    var target = _entityHandles[j];
                    if (!IsOnMap(target)) continue;
                    var id = target.Get<EntityIdentity>().Id;
                    sensors.Targets.Add(id);
                    sensors.Index.Add(id, target.Get<SpatialState>().Position, BodyShape.None);
                }
                builtIndex = true;
            }
            var from = source.Get<SpatialState>().Position;
            foreach (var detector in recipe.Detectors)
            {
                long bins = 2L * detector.Range / 1024 + 3;
                var candidates = sensors.Targets;
                if (bins * bins <= 4L * candidates.Count)
                {
                    sensors.Index.Query(from, from, detector.Range, sensors.Candidates);
                    candidates = sensors.Candidates;
                }
                long rangeSquared = (long)detector.Range * detector.Range;
                foreach (var id in candidates)
                {
                    var target = _entityHandles[_indices[id]];
                    if (target.Get<OwnershipState>().Owner == sensors.Players[player]) continue;
                    var to = target.Get<SpatialState>().Position;
                    long dx = (long)from.X - to.X, dy = (long)from.Y - to.Y;
                    if (dx * dx + dy * dy > rangeSquared ||
                        detector.Channel == sensors.Optical && !InOpticalCoverage(sensors.Players[player], target) ||
                        !SignatureDetected(sensors, target, detector.Signature, detector.Channel, detector.Counters))
                        continue;
                    var key = new SensorState.Key(player, id);
                    var result = new SensorState.Observation(detector.Display, detector.Fields);
                    if (sensors.Pending.TryGetValue(key, out var prior))
                        result = new(prior.Display > result.Display ? prior.Display : result.Display, prior.Fields | result.Fields);
                    sensors.Pending[key] = result;
                }
            }
        }
        (sensors.Current, sensors.Pending) = (sensors.Pending, sensors.Current);
        RememberBuildings(sensors);
        ResolveContacts(sensors);
    }

    private void RememberBuildings(SensorState sensors)
    {
        for (int i = 0; i < Count; i++)
        {
            var entity = _entityHandles[i];
            var type = TypeOf(entity);
            if (type.Movable || type.Footprint is not { TargetBounds.Count: > 0 } || !IsOnMap(entity) || IsConstructing(entity)) continue;
            var identity = entity.Get<EntityIdentity>();
            for (int p = 0; p < sensors.Players.Count; p++)
            {
                uint viewer = sensors.Players[p];
                if (entity.Get<OwnershipState>().Owner == viewer)
                { sensors.Buildings.Remove(new(p, identity.Id)); continue; }
                if (ModelObservation(viewer, identity.Id).HasValue)
                    sensors.Buildings[new(p, identity.Id)] = new(entity.Get<SpatialState>().Position, identity.Type, RotationOf(entity), Tick);
            }
        }
        foreach (var (key, memory) in sensors.Buildings)
        {
            uint viewer = sensors.Players[key.Player];
            if (ModelObservation(viewer, key.Entity).HasValue) continue;
            if (_fog is null || FullyObservedBuilding(viewer, memory))
                sensors.Buildings.Remove(key);
        }
    }

    private bool FullyObservedBuilding(uint viewer, SensorState.BuildingMemory memory)
    {
        var footprint = _types[memory.UnitType].WithRotation(memory.QuarterTurns).Footprint!;
        foreach (var bounds in footprint.TargetBounds)
        {
            var b = bounds.Translate(memory.Position);
            int left = Math.Max(0, b.MinX) / _fog!.CellSize, top = Math.Max(0, b.MinY) / _fog.CellSize;
            int right = (Math.Min(Width, b.MaxX) - 1) / _fog.CellSize, bottom = (Math.Min(Depth, b.MaxY) - 1) / _fog.CellSize;
            for (int y = top; y <= bottom; y++) for (int x = left; x <= right; x++)
                if (_fog.At(viewer, y * _fog.Columns + x) != CellVisibility.Visible) return false;
        }
        return true;
    }

    private void ResolveContacts(SensorState sensors)
    {
        sensors.ContactKeys.Clear();
        foreach (var key in sensors.Knowledge.Keys)
            if (!sensors.Current.ContainsKey(key)) sensors.Knowledge.Remove(key);
        sensors.Keys.Clear();
        sensors.Keys.AddRange(sensors.Current.Keys);
        sensors.Keys.Sort(static (a, b) => a.Player != b.Player ? a.Player.CompareTo(b.Player) : a.Entity.Value.CompareTo(b.Entity.Value));
        foreach (var key in sensors.Keys)
        {
            var entity = _entityHandles[_indices[key.Entity]];
            var observation = sensors.Current[key];
            bool optical = OrdinaryObservation(sensors.Players[key.Player], entity).HasValue;
            bool identified = optical || observation.Display >= SensorDisplay.Identified;
            if (!sensors.Knowledge.TryGetValue(key, out var knowledge))
                knowledge = new(checked(sensors.NextContact[key.Player]++), default, null, Tick, -1);
            int type = entity.Get<EntityIdentity>().Type;
            var classification = identified || observation.Display == SensorDisplay.Classified
                ? ClassifyContact(_types[type]) : knowledge.Class;
            int? knownType = identified ? type : classification == knowledge.Class ? knowledge.UnitType : null;
            sensors.Knowledge[key] = knowledge with
            {
                Position = entity.Get<SpatialState>().Position,
                ObservedTick = Tick,
                Class = classification,
                UnitType = knownType,
                IdentifiedTick = identified ? Tick : knownType.HasValue ? knowledge.IdentifiedTick : -1
            };
            if (!knownType.HasValue && sensors.Buildings.TryGetValue(key, out var memory) &&
                memory.Position == sensors.Knowledge[key].Position)
                sensors.Knowledge[key] = sensors.Knowledge[key] with
                { UnitType = memory.UnitType, Class = ContactClass.Building, IdentifiedTick = memory.SeenTick };
            sensors.ContactKeys.Add((key.Player, knowledge.Id), key);
        }
    }

    private static ContactClass ClassifyContact(UnitDefinition type)
    {
        if (type.Flight is not null) return ContactClass.Aircraft;
        if (type.Footprint is not null || type.Armor == ArmorKind.Building || !type.Movable) return ContactClass.Building;
        return type.Armor == ArmorKind.Living ? ContactClass.Infantry : ContactClass.Vehicle;
    }

    private bool TryGetContact(uint viewer, long id, out ContactKnowledge knowledge)
    {
        if (_sensors is { } sensors && sensors.ContactKeys.TryGetValue((sensors.PlayerSlot(viewer), id), out var key))
            return sensors.Knowledge.TryGetValue(key, out knowledge);
        knowledge = default;
        return false;
    }

    internal void CopyContactPoses(int player, ReadOnlySpan<EntityPose> poses, List<ContactPose> destination)
    {
        destination.Clear();
        if (_sensors is not { } sensors) return;
        foreach (var key in sensors.Keys)
        {
            if (key.Player != player || ModelObservation(sensors.Players[player], key.Entity).HasValue) continue;
            var pose = poses[_indices[key.Entity]];
            var fields = sensors.Current[key].Fields;
            destination.Add(new(sensors.Knowledge[key],
                fields.HasFlag(LiveObservationFields.Health) ? pose.Health : null,
                fields.HasFlag(LiveObservationFields.States) ? pose.Presentation.States : null)
                { QuarterTurns = sensors.Buildings.TryGetValue(key, out var remembered) ? remembered.QuarterTurns : 0 });
        }
        foreach (var (key, memory) in sensors.Buildings.OrderBy(p => p.Key.Entity.Value))
        {
            if (key.Player != player || ModelObservation(sensors.Players[player], key.Entity).HasValue ||
                sensors.Current.ContainsKey(key) && sensors.Knowledge[key].Position == memory.Position) continue;
            destination.Add(new(new(0, memory.Position, memory.UnitType, memory.SeenTick, memory.SeenTick, ContactClass.Building), null, null)
                { QuarterTurns = memory.QuarterTurns });
        }
    }
}
