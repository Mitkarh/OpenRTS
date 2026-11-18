using OpenRTS.Core.Simulation;
using OpenRTS.Core.Visibility;

namespace OpenRTS.Core;

public sealed partial class SimulationWorld
{
    private void ApplyContactAttack(WorldCommand command)
    {
        foreach (int i in Recipients(command))
        {
            var actor = _entityHandles[i];
            uint owner = actor.Get<OwnershipState>().Owner;
            uint viewer = command.Owner != 0 ? command.Owner : command.Issuer != 0 ? command.Issuer : owner;
            if (viewer != owner || !TryGetContact(viewer, command.ContactId, out var contact))
            {
                EmitEvent(new(Tick, WorldEventKind.CommandDenied, actor.Get<EntityIdentity>().Id, Owner: viewer));
                continue;
            }
            if (!HasContactWeapon(i, contact))
            {
                EmitEvent(new(Tick, WorldEventKind.InvalidOrder, actor.Get<EntityIdentity>().Id));
                continue;
            }
            IssueOrder(i, new(UnitActionKind.AttackContact, contact.Position) { ContactId = contact.Id }, command.Queued);
        }
    }

    private bool HasContactWeapon(int index, ContactKnowledge contact, bool inRange = false)
    {
        var actor = _entityHandles[index];
        var type = TypeOf(actor);
        int enabled = type.EnabledMounts(CompletedUpgrade(actor));
        var mounts = actor.Get<CombatState>().Weapons;
        for (int m = 0; m < type.Weapons.Count; m++)
        {
            var weapon = type.Weapons[m].Weapon;
            if ((enabled & (1 << m)) != 0 && weapon.CanAttackContact(contact.Level) &&
                (MountAbility(actor.Get<EntityIdentity>().Type, m) < 0 || mounts[m].AutoCast) &&
                (!inRange || ContactInRange(index, contact.Position, weapon)))
                return true;
        }
        return false;
    }

    private bool ContactInRange(int index, WorldPosition point, WeaponDefinition weapon)
    {
        var actor = _entityHandles[index];
        var from = actor.Get<SpatialState>().Position;
        var muzzle = WeaponPoint(ObjectGeometry.ClosestPoint(TypeOf(actor), from, point) with { Elevation = from.Elevation }, weapon);
        point = WeaponPoint(point, weapon);
        bool spatial = weapon.SpatialTrajectory || IsAircraft(actor);
        return ProjectileDistanceSquared(muzzle, point, spatial) <= (long)weapon.Range * weapon.Range &&
            (_baseGrid is null || (spatial ? SpatialTerrainClear(muzzle, point) : TerrainGeometry.Clear(_baseGrid, muzzle, point, 0)));
    }

    private void UpdateContactOrder(int index)
    {
        var actor = _entityHandles[index];
        ref var orders = ref actor.Get<OrderState>();
        if (!TryGetContact(actor.Get<OwnershipState>().Owner, orders.ActiveOrder.ContactId, out var contact) ||
            !HasContactWeapon(index, contact))
        {
            EndContactOrder(index);
            return;
        }
        orders.ActiveOrder = orders.ActiveOrder with { Position = contact.Position };
        ref var view = ref actor.Get<PresentationState>();
        view.HasAim = true;
        view.AimEntity = default;
        view.AimPoint = contact.Position;
        if (HasContactWeapon(index, contact, inRange: true) &&
            (TypeOf(actor).Footprint is not null || StopAllowed(actor.Get<EntityIdentity>().Type, actor.Get<SpatialState>().Position)))
        {
            Halt(index);
            orders.OrderPhase = OrderPhase.Engaging;
        }
        else if (TypeOf(actor).Movable)
        {
            var motion = actor.Get<MotionState>();
            if (!motion.Moving || (Tick + actor.Get<EntityIdentity>().Id.Value) % 4 == 0)
                SetTarget(index, contact.Position);
            orders.OrderPhase = actor.Get<MotionState>().Moving ? OrderPhase.Approaching : OrderPhase.Waiting;
        }
        else orders.OrderPhase = OrderPhase.Waiting;
    }

    private void EndContactOrder(int index)
    {
        var actor = _entityHandles[index];
        ref var orders = ref actor.Get<OrderState>();
        orders.HasOrder = false;
        orders.OrderPhase = OrderPhase.Idle;
        actor.Get<PresentationState>().HasAim = false;
        Halt(index);
        CancelWindups(index);
    }
}
