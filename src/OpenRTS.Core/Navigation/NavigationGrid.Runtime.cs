namespace OpenRTS.Core.Navigation;

public sealed partial class NavigationGrid
{
    /// <summary>Logical runtime layers. Geometry is supplied by the matching immutable scenario.</summary>
    public void WriteRuntimeState(BinaryWriter writer)
    {
        writer.Write(Width); writer.Write(Height); writer.Write(CellSize); writer.Write(Revision);
        foreach (uint word in _blocked) writer.Write(word);
        writer.Write(_terrain is not null); if (_terrain is not null) writer.Write(_terrain);
        writer.Write(_noStop is not null); if (_noStop is not null) foreach (uint word in _noStop) writer.Write(word);
    }

    public NavigationGrid ReadRuntimeState(BinaryReader reader)
    {
        if (reader.ReadInt32() != Width || reader.ReadInt32() != Height || reader.ReadInt32() != CellSize)
            throw new InvalidDataException("Runtime grid dimensions differ.");
        int revision = reader.ReadInt32();
        if (revision < 0) throw new InvalidDataException("Invalid grid revision.");
        uint[] blocked = new uint[_blocked.Length];
        for (int i = 0; i < blocked.Length; i++) blocked[i] = reader.ReadUInt32();
        bool Flag() { byte value = reader.ReadByte(); if (value > 1) throw new InvalidDataException("Invalid grid flag."); return value != 0; }
        byte[]? terrain = Flag() ? reader.ReadBytes(CellCount) : null;
        if (terrain is not null && terrain.Length != CellCount) throw new EndOfStreamException();
        uint[]? noStop = Flag() ? new uint[_blocked.Length] : null;
        if (noStop is not null) for (int i = 0; i < noStop.Length; i++) noStop[i] = reader.ReadUInt32();
        return new(this, blocked, terrain, noStop, revision);
    }
}
