using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PhoneDeck.Desktop;

internal static class PhoneDeckDataDirectory
{
    internal static string Get()
    {
        var configured = Environment.GetEnvironmentVariable("PHONEDECK_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        var root = OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            : OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(root, "PhoneDeck-Desktop");
    }
}

internal sealed record ReceiverIdentity(string ComputerId, string DisplayName, string Platform, string Architecture)
{
    internal static ReceiverIdentity LoadOrCreate()
    {
        var directory = PhoneDeckDataDirectory.Get(); Directory.CreateDirectory(directory); PrivateFiles.RestrictDirectory(directory);
        var path = Path.Combine(directory, "computer-id.txt");
        var id = File.Exists(path) ? File.ReadAllText(path).Trim() : Guid.NewGuid().ToString();
        if (!Guid.TryParse(id, out _)) throw new InvalidDataException("电脑身份文件损坏，请恢复备份；不会自动创建另一身份");
        if (!File.Exists(path)) { File.WriteAllText(path, id); PrivateFiles.RestrictFile(path); }
        return new(id, Environment.MachineName, OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux", RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
    }
}

internal sealed class DesktopTrust : IDisposable
{
    internal X509Certificate2 Certificate { get; }
    internal string CertificateSha256 => Convert.ToHexString(Certificate.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant();
    internal DesktopTrust(string computerId)
    {
        var directory = PhoneDeckDataDirectory.Get(); var path = Path.Combine(directory, "desktop-tls.pfx");
        var passwordPath = Path.Combine(directory, "desktop-tls-password.txt");
        var password = File.Exists(passwordPath) ? File.ReadAllText(passwordPath) : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        if (!File.Exists(passwordPath)) { File.WriteAllText(passwordPath, password); PrivateFiles.RestrictFile(passwordPath); }
        // Schannel needs a named user key. Apple's keychain cannot load EphemeralKeySet;
        // DefaultKeySet allows its temporary key, which is released with the certificate.
        var flags = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet
            : OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
        if (File.Exists(path)) Certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, flags);
        else
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=PhoneDeck-" + computerId, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("phonedeck.local"); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build());
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(3));
            var bytes = created.Export(X509ContentType.Pfx, password); File.WriteAllBytes(path, bytes); PrivateFiles.RestrictFile(path);
            Certificate = X509CertificateLoader.LoadPkcs12(bytes, password, flags);
        }
    }
    internal static string[] Addresses() => NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up)
        .SelectMany(x => x.GetIPProperties().UnicastAddresses).Select(x => x.Address)
        .Where(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x) && !x.ToString().StartsWith("169.254.", StringComparison.Ordinal)
            && !x.ToString().StartsWith("198.18.", StringComparison.Ordinal) && !x.ToString().StartsWith("198.19.", StringComparison.Ordinal))
        .Select(x => x.ToString()).Distinct().ToArray();
    public void Dispose() => Certificate.Dispose();
}
