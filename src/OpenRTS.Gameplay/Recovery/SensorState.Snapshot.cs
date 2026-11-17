using Frent;
namespace OpenRTS.Core.Visibility;

internal sealed partial class SensorState
{
    internal void Snapshot(RuntimeArchive a)
    {
        a.Layout<Concealment>("Channel", "Counter", "Terrain"); a.Layout<Grant>("Token", "ExpiresAt", "Signature", "Suppress", "Concealment");
        a.Layout<Key>("Player", "Entity"); a.Layout<Observation>("Display", "Fields");
        a.Layout<ContactKnowledge>("Id", "Position", "UnitType", "ObservedTick", "IdentifiedTick", "Class");
        a.Layout<BuildingMemory>("Position", "UnitType", "QuarterTurns", "SeenTick");
        if (a.Reading) Pending.Clear();
        a.Fields(this, "Grants", "Current", "Knowledge", "Buildings", "ContactKeys", "NextContact", "NextGrant");
    }
}
