using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhoneDeck.Desktop;

namespace PhoneDeck.Desktop.Tests;

[TestClass]
public sealed class BrowserTrustTests
{
    [TestMethod] public void PublicRootValidatesBrowserLeafAndRemainsStableWhenAddressChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PhoneDeck-browser-cert-" + Guid.NewGuid());
        try
        {
            string rootHash, leafHash;
            using (var trust = new BrowserTrust(directory, Guid.NewGuid().ToString(), ["192.168.20.1"]))
            {
                rootHash = trust.RootSha256; leafHash = trust.CertificateSha256;
                using var publicRoot = X509CertificateLoader.LoadCertificate(trust.PublicRoot);
                Assert.IsFalse(publicRoot.HasPrivateKey); Assert.IsTrue(trust.Certificate.HasPrivateKey);
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(publicRoot);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                Assert.IsTrue(chain.Build(trust.Certificate), string.Join(",", chain.ChainStatus.Select(status => status.StatusInformation)));
                Assert.IsTrue(trust.AcceptsHost(new HostString("192.168.20.1", 8768), 8768));
                Assert.IsFalse(trust.AcceptsHost(new HostString("attacker.invalid", 8768), 8768));
                Assert.IsFalse(trust.AcceptsHost(new HostString("192.168.20.1", 8766), 8768));
            }
            using (var unchanged = new BrowserTrust(directory, "same-id", ["192.168.20.1"]))
            { Assert.AreEqual(rootHash, unchanged.RootSha256); Assert.AreEqual(leafHash, unchanged.CertificateSha256); }
            using var changed = new BrowserTrust(directory, "same-id", ["192.168.20.2"]);
            Assert.AreEqual(rootHash, changed.RootSha256); Assert.AreNotEqual(leafHash, changed.CertificateSha256);
            Assert.IsFalse(changed.AcceptsHost(new HostString("192.168.20.1", 8768), 8768));
            if (!OperatingSystem.IsWindows())
                foreach (var path in Directory.GetFiles(Path.Combine(directory, "browser-trust")))
                    Assert.AreEqual((UnixFileMode)0, File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod] public void BrowserMutationsRequireTheExactSecureOrigin()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PhoneDeck-origin-" + Guid.NewGuid());
        try
        {
            using var trust = new BrowserTrust(directory, Guid.NewGuid().ToString(), ["192.168.20.1"]);
            var context = new DefaultHttpContext(); context.Request.Scheme = "https";
            context.Request.Host = new HostString("192.168.20.1", 8768);
            foreach (var invalid in new[] { "", "null", "https://attacker.invalid:8768", "http://192.168.20.1:8768", "https://192.168.20.1:8766", "https://192.168.20.1:8768/path", "https://user@192.168.20.1:8768", "https://192.168.20.1:8768?q=x" })
            { context.Request.Headers.Origin = invalid; Assert.IsFalse(trust.SameOrigin(context, 8768), invalid); }
            context.Request.Headers.Origin = "https://192.168.20.1:8768";
            Assert.IsTrue(trust.SameOrigin(context, 8768));
            context.Request.Headers["Sec-Fetch-Site"] = "cross-site";
            Assert.IsFalse(trust.SameOrigin(context, 8768));
            context.Request.Headers.Remove("Sec-Fetch-Site");
            context.Request.Headers.Origin = new Microsoft.Extensions.Primitives.StringValues(["https://192.168.20.1:8768", "https://attacker.invalid"]);
            Assert.IsFalse(trust.SameOrigin(context, 8768));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod] public async Task BrowserListenerServesOnlyPublicShellAndAuthenticatedPhoneEndpoints()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PhoneDeck-web-host-" + Guid.NewGuid());
        var names = new[] { "PHONEDECK_DATA_DIR", "PHONEDECK_LOCAL_PORT", "PHONEDECK_LAN_PORT", "PHONEDECK_WEB_PORT" };
        var previous = names.Select(Environment.GetEnvironmentVariable).ToArray();
        var ports = new HashSet<int>(); while (ports.Count < 3) ports.Add(FreePort()); var port = ports.ToArray();
        Environment.SetEnvironmentVariable(names[0], directory);
        for (var i = 1; i < 4; i++) Environment.SetEnvironmentVariable(names[i], port[i - 1].ToString());
        try
        {
            await using var app = DesktopApp.Create(["--no-discovery", "--no-hotkeys", "--history-only"], new FakeEngine());
            await app.StartAsync();
            try
            {
                using var local = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port[0]}") };
                using var publicRoot = X509CertificateLoader.LoadCertificate(await local.GetByteArrayAsync("/local/web/certificate"));
                Assert.IsFalse(publicRoot.HasPrivateKey);
                using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                {
                    if (certificate is null || (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                    using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(publicRoot); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    return chain.Build(certificate);
                }};
                using var phone = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port[2]}") };
                foreach (var asset in new[] { "", "app.js", "ui.js", "style.css", "session.js", "audio.js", "pcm-worklet.js", "sw.js", "manifest.webmanifest", "icons/icon.svg", "icons/icon-192.png", "icons/icon-512.png", "icons/apple-touch-icon.png" })
                {
                    using var response = await phone.GetAsync("/phone/" + asset);
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, asset);
                    Assert.IsFalse(response.Headers.GetValues("Content-Security-Policy").Single().Contains("unsafe-inline"));
                }
                foreach (var forbidden in new[] { "/", "/local/status", "/local/web/certificate", "/api/health", "/phone/root.pfx", "/phone/../local/status" })
                    Assert.AreEqual(HttpStatusCode.NotFound, (await phone.GetAsync(forbidden)).StatusCode, forbidden);
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await phone.GetAsync("/phone/api/state")).StatusCode);
                Assert.AreEqual(HttpStatusCode.Forbidden, (await phone.PostAsJsonAsync("/phone/pair", new { })).StatusCode);
                Assert.AreEqual(HttpStatusCode.NotFound, (await local.GetAsync("/phone/")).StatusCode);
                using var spoof = new HttpRequestMessage(HttpMethod.Get, "/phone/"); spoof.Headers.Host = $"attacker.invalid:{port[2]}";
                try { Assert.AreEqual(HttpStatusCode.NotFound, (await phone.SendAsync(spoof)).StatusCode); }
                catch (HttpRequestException error)
                {
                    // HttpClient can derive SNI from the spoofed Host and reject it
                    // during TLS, before the HTTP Host guard can return 404.
                    Assert.IsInstanceOfType<System.Security.Authentication.AuthenticationException>(error.InnerException);
                }
            }
            finally { await app.StopAsync(); }
        }
        finally { for (var i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], previous[i]); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
}
