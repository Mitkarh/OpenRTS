namespace OpenRTS.Core.Orders;

internal struct OrderState
{
    public UnitOrder ActiveOrder;
    public UnitOrder? SuspendedOrder;
    public bool HasOrder, AutoBuild;
    public OrderPhase OrderPhase;
    public Queue<UnitOrder>? PendingOrders;
    public EntityId CombatTarget;
    public long OrderStarted;
}
