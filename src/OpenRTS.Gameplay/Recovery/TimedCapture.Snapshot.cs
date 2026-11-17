using Frent;
namespace OpenRTS.Core.Capture;

internal sealed partial class TimedCapture
{
    internal void BeginSnapshotRestore() => _activeTargets.Clear();
    internal void SnapshotEntity(RuntimeArchive a, Entity entity)
    {
        a.Layout<SideProgress>("Work", "LastContribution"); a.Layout<CapturableState>("ChangedTick", "Sides");
        a.Component<CapturableState>(entity);
        if (a.Reading && entity.Has<CapturableState>() && entity.Get<CapturableState>().Sides is not null) _activeTargets.Add(entity);
    }
}
