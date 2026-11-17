using System.Diagnostics;
using System.Security.Cryptography;
using OpenRTS.Core;

namespace OpenRTS.Network;

/// <summary>Keep private. Bound to one match, player slot and connection epoch; never an account credential.</summary>
public sealed record MatchResumeCredential(Guid Match, uint Player, string Token, long Epoch)
{
    public override string ToString() => $"Match={Match}, Player={Player}, Epoch={Epoch}, Token=<redacted>";
}

public sealed partial class StarSession
{
    private MatchResumeCredential? _resumeCredential;
    public MatchResumeCredential? ResumeCredential => Volatile.Read(ref _resumeCredential);
    private static byte[] CredentialFrame(MatchResumeCredential credential) => Wire.Pack(w => {
        w.Write(StarProtocol.Credential); w.Write(credential.Match.ToByteArray()); w.Write(credential.Player);
        w.Write(credential.Epoch); Wire.Text(w, credential.Token);
    });
    private static MatchResumeCredential ReadCredential(byte[] bytes) => Wire.Unpack(bytes, r => {
        if (r.ReadByte() != StarProtocol.Credential) throw new InvalidDataException("Expected resume credential.");
        var match = new Guid(r.ReadBytes(16)); uint player = r.ReadUInt32(); long epoch = r.ReadInt64(); string token = Wire.Text(r);
        if (match == Guid.Empty || player is < 2 or > 8 || epoch < 1 || token.Length != 64 || !token.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid resume credential.");
        return new MatchResumeCredential(match, player, token, epoch);
    });
    private static MatchResumeCredential NewCredential(Guid match, uint player, long epoch) =>
        new(match, player, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), epoch);
    private static bool SameCredential(MatchResumeCredential actual, MatchResumeCredential? expected) =>
        expected is not null && actual.Match == expected.Match && actual.Player == expected.Player && actual.Epoch == expected.Epoch &&
        CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(actual.Token), System.Text.Encoding.ASCII.GetBytes(expected.Token));

    internal sealed class RecoveryTransfer
    {
        internal const int ChunkSize = 32768;
        internal static void Send(PeerPipeline pipe, byte[] snapshot, byte[] history, CancellationToken cancellation)
        {
            pipe.Send(Wire.Pack(w => {
                w.Write(StarProtocol.Recovery); w.Write(snapshot.Length); w.Write(history.Length);
                w.Write(SHA256.HashData(snapshot)); w.Write(SHA256.HashData(history));
            }));
            var clock = Stopwatch.StartNew();
            for (int artifact = 0; artifact < 2; artifact++)
            {
                byte[] data = artifact == 0 ? snapshot : history;
                for (int offset = 0; offset < data.Length; offset += ChunkSize)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Recovery transfer deadline exceeded.");
                    int length = Math.Min(ChunkSize, data.Length - offset), at = offset;
                    pipe.Send(Wire.Pack(w => { w.Write(StarProtocol.RecoveryChunk); w.Write((byte)artifact); w.Write(at); w.Write(length); w.Write(data, at, length); }));
                }
            }
        }
        internal static (byte[] Snapshot, byte[] History) Receive(byte[] begin, PeerPipeline pipe, TimeSpan timeout, CancellationToken cancellation)
        {
            var sizes = Wire.Unpack(begin, r => {
                if (r.ReadByte() != StarProtocol.Recovery) throw new InvalidDataException("Expected recovery transfer.");
                int snapshot = r.ReadInt32(), history = r.ReadInt32();
                if (snapshot is < 1 or > SimulationWorld.MaximumSnapshotBytes + 128 || history is < 1 or > 64 * 1024 * 1024)
                    throw new InvalidDataException("Recovery transfer quota exceeded.");
                return (snapshot, history, first: r.ReadBytes(32), second: r.ReadBytes(32));
            });
            byte[][] data = [new byte[sizes.snapshot], new byte[sizes.history]];
            var clock = Stopwatch.StartNew();
            for (int artifact = 0; artifact < 2; artifact++)
            {
                for (int offset = 0; offset < data[artifact].Length;)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var remaining = TimeSpan.FromSeconds(15) - clock.Elapsed;
                    if (remaining <= TimeSpan.Zero) throw new TimeoutException("Recovery transfer deadline exceeded.");
                    int copied = Wire.Unpack(pipe.Read(remaining < timeout ? remaining : timeout), r => {
                        if (r.ReadByte() != StarProtocol.RecoveryChunk || r.ReadByte() != artifact || r.ReadInt32() != offset)
                            throw new InvalidDataException("Recovery chunk order mismatch.");
                        int n = r.ReadInt32();
                        if (n != Math.Min(ChunkSize, data[artifact].Length - offset)) throw new InvalidDataException("Invalid recovery chunk length.");
                        byte[] chunk = r.ReadBytes(n); if (chunk.Length != n) throw new EndOfStreamException();
                        chunk.CopyTo(data[artifact], offset); return n;
                    });
                    offset += copied;
                }
            }
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(data[0]), sizes.first) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(data[1]), sizes.second)) throw new InvalidDataException("Recovery transfer digest mismatch.");
            return (data[0], data[1]);
        }
    }
}
