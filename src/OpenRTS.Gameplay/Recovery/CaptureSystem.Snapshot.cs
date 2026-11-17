using Frent;
namespace OpenRTS.Core.Capture;

internal sealed partial class CaptureSystem
{
    internal void Snapshot(RuntimeArchive a) => a.Custom(_mechanic);
    internal void BeginSnapshotRestore() { if (_mechanic is TimedCapture capture) capture.BeginSnapshotRestore(); }
    internal bool SupportsSnapshot => _mechanic is TimedCapture or IRuntimeSnapshotMechanic;
    internal void SnapshotEntity(RuntimeArchive a, Entity entity)
    { if (_mechanic is TimedCapture capture) capture.SnapshotEntity(a, entity); else a.Custom(_mechanic, entity); }
}
