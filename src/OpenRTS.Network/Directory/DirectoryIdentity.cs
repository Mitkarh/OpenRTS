using System.Security.Cryptography;
using System.Text;

namespace OpenRTS.Network;

/// <summary>Account identity is possession of a P-256 key, independent of its untrusted display name.</summary>
public sealed class DirectoryIdentity : IDisposable
{
    private readonly ECDsa _key;
    private readonly object _gate = new();
    public string PublicKey { get; }
    public string AccountId => Id(PublicKey);
    public DirectoryIdentity()
    { _key = ECDsa.Create(ECCurve.NamedCurves.nistP256); PublicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()); }
    public string SignLogin(AccountChallenge challenge) => Sign(LoginBytes(challenge));
    internal string SignAdmission(string ticket, string nonce) => Sign(AdmissionBytes(ticket, nonce));
    private string Sign(byte[] bytes)
    { lock (_gate) return Convert.ToBase64String(_key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)); }
    internal static byte[] LoginBytes(AccountChallenge challenge) => Encoding.UTF8.GetBytes($"OpenRTS.Directory.Login.v1\n{challenge.Id:D}\n{challenge.Nonce}");
    internal static byte[] AdmissionBytes(string ticket, string nonce) => Encoding.UTF8.GetBytes($"OpenRTS.Match.Admission.v1\n{nonce}\n{ticket}");
    internal static string Id(string key) => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(key)));
    internal static ECDsa ReadKey(string value)
    {
        try
        {
            if (value is null || value.Length > 256) throw new CryptographicException();
            byte[] data = Convert.FromBase64String(value); var key = ECDsa.Create();
            try
            {
                key.ImportSubjectPublicKeyInfo(data, out int read);
                if (read != data.Length || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new CryptographicException();
                return key;
            }
            catch { key.Dispose(); throw; }
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        { throw new MatchDirectoryException("invalid_key", "Нужен открытый ключ P-256."); }
    }
    internal static bool Verify(string publicKey, byte[] data, string signature)
    {
        try
        {
            if (signature is null || signature.Length != 88) return false;
            using var key = ReadKey(publicKey); byte[] bytes = Convert.FromBase64String(signature);
            return bytes.Length == 64 && key.VerifyData(data, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or MatchDirectoryException) { return false; }
    }
    internal static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal static bool SecretEquals(string? first, string? second) => first is not null && second is not null &&
        first.Length == second.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(first), Encoding.ASCII.GetBytes(second));
    public void Dispose() { lock (_gate) _key.Dispose(); }
}
