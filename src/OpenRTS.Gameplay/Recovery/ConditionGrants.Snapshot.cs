using Frent;
namespace OpenRTS.Core;

internal sealed partial class ConditionGrants
{
    internal void Snapshot(RuntimeArchive a)
    { a.Layout<Grant>("Entity", "ExpiresAt"); a.Fields(this, "_nextId", "_grants", "_byEntity", "_expirations"); }
}
