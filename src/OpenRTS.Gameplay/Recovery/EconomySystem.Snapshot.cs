using Frent;
namespace OpenRTS.Core.Economy;

internal sealed partial class EconomySystem
{
    internal void Snapshot(RuntimeArchive a)
    {
        a.Layout<Account>("Id", "Name", "Credits", "Produced", "Consumed");
        a.Fields(this, "_players");
    }
    internal void SnapshotEntity(RuntimeArchive a, Entity entity)
    { a.Layout<IncomeState>("NextTick"); a.Component<IncomeState>(entity); }
}
