using Frent;
namespace OpenRTS.Core.Production;

internal sealed partial class ProductionSystem
{
    internal void Snapshot(RuntimeArchive a) => a.Fields(this, "_nextTaskId");
}
