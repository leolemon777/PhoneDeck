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
        SelectCandidateAddresses(NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up
                && network.NetworkInterfaceType is not NetworkInterfaceType.Loopback
                && network.NetworkInterfaceType is not NetworkInterfaceType.Tunnel)
            .SelectMany(network => network
                .GetIPProperties().UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(address.Address)
                    && !IsExcludedAddress(address.Address))
                .Select(address => new AddressCandidate(
                    address.Address,
                    network.GetIPProperties().GatewayAddresses.Count > 0,
                    network.NetworkInterfaceType))));

    internal readonly record struct AddressCandidate(
        IPAddress Address,
        bool InterfaceHasGateway,
        NetworkInterfaceType InterfaceType);

    /// <summary>按“有网关的真实接口优先”排序，并排除回环、APIPA 和
    /// 198.18.0.0/15 基准/TUN 网段；独立成纯函数以便单元测试。</summary>
    internal static string[] SelectCandidateAddresses(
        IEnumerable<AddressCandidate> candidates) =>
        candidates
            .Where(candidate => !IsExcludedAddress(candidate.Address))
            .OrderByDescending(candidate => candidate.InterfaceHasGateway)
            .ThenBy(candidate => InterfaceTypeRank(candidate.InterfaceType))
            .Select(candidate => candidate.Address.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>排除回环 127.0.0.0/8、APIPA 169.254.0.0/16 与
    /// RFC 2544 基准网段 198.18.0.0/15（Meta/Clash 等 TUN 虚拟网卡常用）。</summary>
    internal static bool IsExcludedAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return true;
        }
        return bytes[0] == 127
            || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19));
    }

    private static int InterfaceTypeRank(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 => 0,
        _ => 1
    };

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
