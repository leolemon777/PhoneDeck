using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PhoneDeck.Desktop;

// Browser TLS is separate from the pinned native-client certificate. Only the public
// root is exported for installation; no endpoint returns either private key.
internal sealed class BrowserTrust : IDisposable
{
    private readonly X509Certificate2 root;
    internal X509Certificate2 Certificate { get; }
    internal byte[] PublicRoot => root.Export(X509ContentType.Cert);
    internal string RootSha256 => Convert.ToHexString(root.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();
    internal string CertificateSha256 => Convert.ToHexString(Certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();
    internal string[] Hosts { get; }

    internal BrowserTrust(string directory, string computerId, IEnumerable<string>? addresses = null)
    {
        directory = Path.Combine(directory, "browser-trust");
        Directory.CreateDirectory(directory); PrivateFiles.RestrictDirectory(directory);
        var passwordPath = Path.Combine(directory, "password.txt");
        var password = File.Exists(passwordPath) ? File.ReadAllText(passwordPath) : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        if (!File.Exists(passwordPath)) Save(passwordPath, System.Text.Encoding.UTF8.GetBytes(password));
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet
            : OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        var rootPath = Path.Combine(directory, "root.pfx");
        if (!File.Exists(rootPath))
        {
            using var key = RSA.Create(3072);
            var request = new CertificateRequest("CN=Yandu Personal " + computerId, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(3));
            Save(rootPath, created.Export(X509ContentType.Pfx, password));
        }
        root = X509CertificateLoader.LoadPkcs12FromFile(rootPath, password, flags);
        if (root.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(2))
            throw new InvalidOperationException("手机网页证书已到期，请重新建立浏览器信任；不会静默更换已信任根证书");
        Hosts = (addresses ?? WebPhoneNetwork.Addresses()).Concat(["127.0.0.1", "::1", "localhost"])
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var leafPath = Path.Combine(directory, "server.pfx");
        var hostsPath = Path.Combine(directory, "hosts.txt");
        var hostList = string.Join('\n', Hosts);
        X509Certificate2? leaf = File.Exists(leafPath) ? X509CertificateLoader.LoadPkcs12FromFile(leafPath, password, flags) : null;
        if (leaf is null || leaf.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(30)
            || !File.Exists(hostsPath) || File.ReadAllText(hostsPath) != hostList)
        {
            leaf?.Dispose();
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Yandu Phone " + computerId, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            var names = new SubjectAlternativeNameBuilder();
            foreach (var host in Hosts)
                if (IPAddress.TryParse(host, out var ip)) names.AddIpAddress(ip); else names.AddDnsName(host);
            request.CertificateExtensions.Add(names.Build());
            var expiry = new DateTimeOffset(root.NotAfter.ToUniversalTime()).AddMinutes(-1);
            if (expiry > DateTimeOffset.UtcNow.AddDays(365)) expiry = DateTimeOffset.UtcNow.AddDays(365);
            using var signed = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-2), expiry, RandomNumberGenerator.GetBytes(16));
            using var withKey = signed.CopyWithPrivateKey(key);
            var bytes = withKey.Export(X509ContentType.Pfx, password);
            Save(leafPath, bytes); Save(hostsPath, System.Text.Encoding.UTF8.GetBytes(hostList));
            leaf = X509CertificateLoader.LoadPkcs12(bytes, password, flags);
        }
        Certificate = leaf;
    }

    internal bool AcceptsHost(HostString host, int port) => host.Port == port
        && Hosts.Contains(host.Host.Trim('[', ']'), StringComparer.OrdinalIgnoreCase);

    internal bool SameOrigin(HttpContext context, int port)
    {
        if (!context.Request.IsHttps || !AcceptsHost(context.Request.Host, port)) return false;
        var origins = context.Request.Headers.Origin;
        if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)) return false;
        return origin.Scheme == "https" && origin.Port == port && origin.AbsolutePath == "/"
            && string.IsNullOrEmpty(origin.Query) && string.IsNullOrEmpty(origin.Fragment) && string.IsNullOrEmpty(origin.UserInfo)
            && string.Equals(origin.Host.Trim('[', ']'), context.Request.Host.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)
            && context.Request.Headers["Sec-Fetch-Site"].ToString() is not ("cross-site" or "none");
    }

    private static void Save(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { PrivateFiles.RestrictFile(temporary); stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, path, true); PrivateFiles.RestrictFile(path);
    }
    public void Dispose() { Certificate.Dispose(); root.Dispose(); }
}
