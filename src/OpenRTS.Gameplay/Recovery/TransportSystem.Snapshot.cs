using Frent;
namespace OpenRTS.Core.Transport;

internal sealed partial class TransportSystem
{
    internal void Snapshot(RuntimeArchive a) { a.Fields(this, "_storedCount"); a.Custom(_mechanic); }
    internal bool SupportsSnapshot => _mechanic is StandardTransport or IRuntimeSnapshotMechanic;
    internal void SnapshotEntity(RuntimeArchive a, Entity entity)
    { if (_mechanic is StandardTransport transport) transport.SnapshotEntity(a, entity); else a.Custom(_mechanic, entity); }
}
