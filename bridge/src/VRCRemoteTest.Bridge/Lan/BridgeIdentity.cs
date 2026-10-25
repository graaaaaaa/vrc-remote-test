using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace VRCRemoteTest.Bridge.Lan;

/// <summary>Machine-local HTTPS identity. Neither file is placed in a shared directory.</summary>
public sealed class BridgeIdentity
{
    private const string CertificateFile = "lan-identity.pfx";
    private const string TokenFile = "lan-token.txt";

    private BridgeIdentity(X509Certificate2 certificate, string token)
    {
        Certificate = certificate;
        Token = token;
    }

    public X509Certificate2 Certificate { get; }
    public string Token { get; }
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Certificate.RawData)).ToLowerInvariant();

    public static BridgeIdentity LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var certificatePath = Path.Combine(directory, CertificateFile);
        var tokenPath = Path.Combine(directory, TokenFile);

        if (!File.Exists(certificatePath))
        {
            using var key = RSA.Create(3072);
            var request = new CertificateRequest(
                "CN=VRC Remote Test Bridge", key, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            using var created = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
            WriteNewPrivateFile(certificatePath, created.Export(X509ContentType.Pfx));
        }

        if (!File.Exists(tokenPath))
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            WriteNewPrivateFile(tokenPath, Encoding.ASCII.GetBytes(token));
        }

        var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, null);
        if (!certificate.HasPrivateKey || certificate.NotAfter <= DateTime.UtcNow)
        {
            throw new InvalidOperationException("LAN certificate is unusable or expired; rotate it and pair Unity again.");
        }

        var storedToken = File.ReadAllText(tokenPath).Trim();
        if (storedToken.Length != 64 || storedToken.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new InvalidOperationException("LAN token file is invalid; rotate it and pair Unity again.");
        }

        return new BridgeIdentity(certificate, storedToken);
    }

    public bool Authenticate(string? authorization)
    {
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = authorization[7..];
        if (candidate.Length != Token.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(Token));
    }

    private static void WriteNewPrivateFile(string path, byte[] content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }
}
