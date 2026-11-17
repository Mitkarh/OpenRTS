using Frent;

namespace OpenRTS.Core;

/// <summary>Explicit logical save schema for a registered stateful mechanic. Registration version binds the schema.</summary>
/// <remarks>No native handles may be persisted. Readers run on the world owner and must consume their bounded section exactly.</remarks>
public interface IRuntimeSnapshotMechanic
{
    void WriteSnapshot(BinaryWriter writer);
    void ReadSnapshot(BinaryReader reader);
    void WriteEntitySnapshot(BinaryWriter writer, Entity entity) { }
    void ReadEntitySnapshot(BinaryReader reader, Entity entity) { }
}
