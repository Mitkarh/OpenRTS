using System.Text;
using OpenRTS.Core;

namespace OpenRTS.Network;

public sealed partial class StarSession
{
    private readonly Func<string, (MatchDescriptor Descriptor, SimulationWorld World)>? _prepare;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Data, SlotController[] Slots)> _configuration = new();
    private string _settings = "", _initialHash = "";
    private SimulationWorld? _ownedWorld;
    private MatchDescriptor _descriptor = null!;
    public string LobbySettings => Volatile.Read(ref _settings);

    /// <summary>Game-owned bounded recipe. Only the host can change it, only before Start.</summary>
    public void ConfigureLobby(string settings, IReadOnlyList<SlotController> slots)
    {
        RequireHost();
        if (State.Phase != MatchPhase.Lobby || _prepare is null) throw new InvalidOperationException("Lobby configuration is unavailable.");
        ValidateSettings(settings);
        if (slots.Count != State.Slots.Count || slots[0] != SlotController.Human || slots.Any(s => !Enum.IsDefined(s)))
            throw new ArgumentException("Invalid lobby controllers.");
        if (!_configuration.IsEmpty) throw new InvalidOperationException("A configuration update is pending.");
        _configuration.Enqueue((settings, slots.ToArray()));
    }
    private static void ValidateSettings(string data)
    {
        if (Encoding.UTF8.GetByteCount(data) is < 1 or > 16384) throw new ArgumentException("Lobby recipe must contain 1..16384 UTF-8 bytes.");
    }
    private byte[] SettingsFrame() => Wire.Pack(w => {
        byte[] data = Encoding.UTF8.GetBytes(LobbySettings);
        w.Write(StarProtocol.Settings); w.Write((ushort)data.Length); w.Write(data);
        Wire.Text(w, Wire.Hash(DirectMatch.Descriptor(Descriptor))); Wire.Text(w, _initialHash);
    });
    private static (string Data, string Digest, string Hash) ReadSettings(byte[] bytes) => Wire.Unpack(bytes, r => {
        if (r.ReadByte() != StarProtocol.Settings) throw new InvalidDataException("Expected lobby recipe.");
        int size = r.ReadUInt16();
        if (size is < 1 or > 16384) throw new InvalidDataException("Lobby recipe limit.");
        var data = r.ReadBytes(size); if (data.Length != size) throw new EndOfStreamException();
        return (new UTF8Encoding(false, true).GetString(data), Wire.Text(r), Wire.Text(r));
    });
    private SimulationWorld PrepareSettings(string data)
    {
        ValidateSettings(data);
        var prepared = _prepare!(data);
        try
        {
            _ = DirectMatch.Descriptor(prepared.Descriptor);
            if (prepared.Descriptor.Rules != Descriptor.Rules || prepared.Descriptor.ContentHash != Descriptor.ContentHash ||
                prepared.World.Tick != 0 || prepared.World.TickRate != prepared.Descriptor.TickRate)
                throw new InvalidDataException("Lobby rules/content/world mismatch.");
            prepared.World.RestrictCommandsToPlayers();
            string hash = prepared.World.Report().StateHash;
            _ownedWorld?.Dispose(); _ownedWorld = prepared.World;
            Volatile.Write(ref _descriptor, prepared.Descriptor); Volatile.Write(ref _settings, data); Volatile.Write(ref _initialHash, hash);
            return prepared.World;
        }
        catch { prepared.World.Dispose(); throw; }
    }
    private string SettingsDigest => Wire.Hash(Encoding.UTF8.GetBytes(LobbySettings));
}
