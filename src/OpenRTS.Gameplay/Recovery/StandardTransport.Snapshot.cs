using Frent;
namespace OpenRTS.Core.Transport;

internal sealed partial class StandardTransport
{
    internal void SnapshotEntity(RuntimeArchive a, Entity entity)
    {
        a.Layout<PassengerState>("Carrier"); a.Layout<TransportState>("Occupants", "UsedSlots", "ClaimedNeutral");
        a.Component<PassengerState>(entity); a.Component<TransportState>(entity);
    }
}
