using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OpenRTS.Network;

internal static class MatchTlsCertificate
{
    public static X509Certificate2 Create()
    {
        const string subject = "CN=openrts.local";
        var now = DateTimeOffset.UtcNow;
        var notBefore = now.AddMinutes(-5);
        var notAfter = now.AddDays(1);
        if (!OperatingSystem.IsWindows())
        {
            using var ecdsa = ECDsa.Create();
            return new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256)
                .CreateSelfSigned(notBefore, notAfter);
        }

        // Schannel needs a private key it can acquire from the Windows key provider.
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(notBefore, notAfter);
        byte[] pkcs12 = certificate.Export(X509ContentType.Pkcs12, "");
        try
        {
            return X509CertificateLoader.LoadPkcs12(pkcs12, "", X509KeyStorageFlags.UserKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }
}
