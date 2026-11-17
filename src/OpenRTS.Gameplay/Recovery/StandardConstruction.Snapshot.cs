using Frent;
namespace OpenRTS.Core.Construction;

internal sealed partial class StandardConstruction
{
    internal void SnapshotEntity(RuntimeArchive a, Entity entity)
    { a.Layout<ConstructionState>("Required", "StartingHealth", "StartedAt", "Work"); a.Component<ConstructionState>(entity); }
}
