using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

internal sealed class LanIdentity : IDisposable
{
    private const int Port = 8766;
    private readonly X509Certificate2 certificate;

    private LanIdentity(X509Certificate2 certificate, string accessToken)
    {
        this.certificate = certificate;
        AccessToken = accessToken;
        CertificateSha256 = Convert.ToHexString(
            certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();
    }

    internal X509Certificate2 Certificate => certificate;
    internal string AccessToken { get; }
    internal string CertificateSha256 { get; }
    internal int HttpsPort => Port;

    internal static LanIdentity LoadOrCreate(string computerId)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhoneDeck");
        Directory.CreateDirectory(directory);
        var certificatePath = Path.Combine(directory, "lan-certificate.pfx");
        var passwordPath = Path.Combine(directory, "lan-certificate-password.txt");
        var tokenPath = Path.Combine(directory, "lan-access-token.txt");

        var password = ReadOrCreateSecret(passwordPath, 24);
        var accessToken = ReadOrCreateSecret(tokenPath, 32);
        X509Certificate2 certificate;
        try
        {
            certificate = File.Exists(certificatePath)
                ? new X509Certificate2(
                    certificatePath,
                    password,
                    X509KeyStorageFlags.UserKeySet
                    | X509KeyStorageFlags.PersistKeySet
                    | X509KeyStorageFlags.Exportable)
                : CreateCertificate(certificatePath, password, computerId);
        }
        catch (CryptographicException)
        {
            certificate = CreateCertificate(certificatePath, password, computerId);
        }
        return new LanIdentity(certificate, accessToken);
    }

    internal string[] GetCandidateAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up
                && network.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                && network.NetworkInterfaceType is not NetworkInterfaceType.Tunnel)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(address.Address)
                && !address.Address.ToString().StartsWith("169.254.",
                    StringComparison.Ordinal))
            .Select(address => address.Address.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string ReadOrCreateSecret(string path, int byteCount)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }
        }
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount));
        File.WriteAllText(path, secret);
        return secret;
    }

    private static X509Certificate2 CreateCertificate(
        string path,
        string password,
        string computerId)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN=PhoneDeck-{computerId}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(
            request.PublicKey, false));
        var alternativeNames = new SubjectAlternativeNameBuilder();
        alternativeNames.AddDnsName("phonedeck.local");
        alternativeNames.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(alternativeNames.Build());
        using var created = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(5));
        var exported = created.Export(X509ContentType.Pfx, password);
        File.WriteAllBytes(path, exported);
        return new X509Certificate2(
            exported,
            password,
            X509KeyStorageFlags.UserKeySet
            | X509KeyStorageFlags.PersistKeySet
            | X509KeyStorageFlags.Exportable);
    }

    public void Dispose() => certificate.Dispose();
}
