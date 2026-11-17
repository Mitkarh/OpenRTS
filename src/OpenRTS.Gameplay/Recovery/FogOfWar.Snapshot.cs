namespace OpenRTS.Core.Visibility;
public sealed partial class FogOfWar
{
    internal void Snapshot(RuntimeArchive a)
    {
        a.Layout<VisionSource>("Id", "Owner", "X", "Y", "Radius"); a.Layout<Strip>("Row", "Left", "Right");
        a.LayoutOmitting<VisionGroup>(["Pending", "Seen", "Sorted"], "Members", "Strips", "X", "Y", "Individual", "RigidTicks", "Cache");
        if (a.Reading) _next.Clear();
        a.Fields(this, "_tick", "_visibleUntil", "_wasCovered", "_coverage", "_explored", "_dirty", "_touched", "_sources", "_groups", "_cachedStrips", "_activeStrips");
    }
}
