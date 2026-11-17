using System.Security.Cryptography;
using System.Text.Json;

namespace OpenRTS.Network;

internal sealed record AdmissionClaims(int Version, Guid Nonce, Guid Match, string Account, string PublicKey,
    MatchAccessRole Role, uint Player, int PlayerCount, DateTimeOffset Issued, DateTimeOffset Expires, int Protocol,
    string Rules, string ContentHash, string Fingerprint, MatchEndpoint[] Endpoints);

public sealed class MatchAdmissionPolicy
{
    public string SigningKey { get; }
    public MatchAdmissionPolicy(string signingKey)
    { using var key = DirectoryIdentity.ReadKey(signingKey); SigningKey = signingKey; }
}

internal static class AdmissionTicket
{
    internal static string Sign(ECDsa key, AdmissionClaims claims)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(claims, DirectoryJson.Options);
        byte[] signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(payload) + "." + Convert.ToBase64String(signature);
    }
    internal static AdmissionClaims Verify(string signingKey, string ticket, DateTimeOffset now)
    {
        try
        {
            if (ticket is null || ticket.Length is < 100 or > 8192) throw new InvalidDataException();
            string[] parts = ticket.Split('.'); if (parts.Length != 2) throw new InvalidDataException();
            byte[] payload = Convert.FromBase64String(parts[0]), signature = Convert.FromBase64String(parts[1]);
            using var key = DirectoryIdentity.ReadKey(signingKey);
            if (payload.Length > 4096 || signature.Length != 64 || !key.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) throw new InvalidDataException();
            var claims = JsonSerializer.Deserialize<AdmissionClaims>(payload, DirectoryJson.Options) ?? throw new InvalidDataException();
            if (claims.Version != 2 || claims.Nonce == Guid.Empty || claims.Match == Guid.Empty || !Enum.IsDefined(claims.Role) || claims.PlayerCount is < 2 or > 8 ||
                claims.Role == MatchAccessRole.Player && (claims.Player < 2 || claims.Player > claims.PlayerCount) || claims.Role == MatchAccessRole.Spectator && claims.Player != 0 ||
                claims.Issued > now.AddSeconds(5) || claims.Expires <= now || claims.Expires <= claims.Issued || claims.Expires - claims.Issued > TimeSpan.FromSeconds(30) ||
                claims.Account != DirectoryIdentity.Id(claims.PublicKey) || claims.Protocol != StarProtocol.Version ||
                claims.Endpoints is null || claims.Endpoints.Length is < 1 or > 3) throw new InvalidDataException();
            DirectoryValidation.Hash(claims.Fingerprint, "отпечаток"); DirectoryValidation.Text(claims.Rules, 128, "правила");
            DirectoryValidation.Text(claims.ContentHash, 128, "контент");
            foreach (var endpoint in claims.Endpoints) DirectoryValidation.Endpoint(endpoint);
            return claims;
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or CryptographicException or JsonException or MatchDirectoryException or ArgumentException)
        { throw new InvalidDataException("Билет допуска недействителен или истёк."); }
    }
}

internal sealed class MatchAdmissionGate(MatchAdmissionPolicy policy, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<Guid, DateTimeOffset> _spent = [];
    internal AdmissionClaims Inspect(string ticket) => AdmissionTicket.Verify(policy.SigningKey, ticket, _clock.GetUtcNow());
    internal void CheckProof(string ticket, string nonce, string signature)
    {
        var claims = Inspect(ticket);
        if (!DirectoryIdentity.Verify(claims.PublicKey, DirectoryIdentity.AdmissionBytes(ticket, nonce), signature))
            throw new InvalidDataException("Не подтверждён ключ владельца билета.");
    }
    // Called only by the owner after transport proof, compatibility and live-slot checks.
    internal MatchAdmissionReceipt Redeem(string ticket, Guid match, uint player, string pin, string rules, string content, int playerCount)
    {
        var claims = Inspect(ticket);
        if (claims.Match != match || claims.Player != player || claims.PlayerCount != playerCount || claims.Fingerprint != pin || claims.Rules != rules || claims.ContentHash != content)
            throw new InvalidDataException("Билет выдан для другого матча, места или контента.");
        foreach (var nonce in _spent.Where(p => p.Value <= _clock.GetUtcNow()).Select(p => p.Key).ToArray()) _spent.Remove(nonce);
        if (_spent.Count >= 4096 || !_spent.TryAdd(claims.Nonce, claims.Expires)) throw new InvalidDataException("Билет уже использован либо превышен лимит допуска.");
        return new(claims.Nonce, claims.Account);
    }
}
