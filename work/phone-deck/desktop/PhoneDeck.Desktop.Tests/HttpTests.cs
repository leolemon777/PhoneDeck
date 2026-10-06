using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhoneDeck.Desktop;

namespace PhoneDeck.Desktop.Tests;
[TestClass]
public sealed class HttpTests
{
    [TestMethod] public async Task RealHttpsAuthenticatesScopesTargetsAndSyncAndLoopbackDeniesCrossOrigin()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PhoneDeck-test-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        var variables = new[] { "PHONEDECK_DATA_DIR", "PHONEDECK_LOCAL_PORT", "PHONEDECK_LAN_PORT" };
        var previous = variables.Select(Environment.GetEnvironmentVariable).ToArray();
        var localPort = FreePort(); var lanPort = FreePort(); while (lanPort == localPort) lanPort = FreePort();
        Environment.SetEnvironmentVariable(variables[0], directory); Environment.SetEnvironmentVariable(variables[1], localPort.ToString()); Environment.SetEnvironmentVariable(variables[2], lanPort.ToString());
        try
        {
            var identity = ReceiverIdentity.LoadOrCreate();
            var credentials = new ClientCredentialsStore(Path.Combine(directory, "clients.json"));
            var phone = credentials.Issue("test", ["control", "audio", "transcript-sync"], Guid.NewGuid().ToString(), out var token);
            var scoped = credentials.Issue("limited", ["control"], Guid.NewGuid().ToString(), out var limited);
            await using var app = DesktopApp.Create(["--no-web-phone", "--no-browser", "--no-discovery", "--no-hotkeys"], new FakeEngine()); await app.StartAsync();
            try
            {
                using var local = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{localPort}") };
                var spoof = new HttpRequestMessage(HttpMethod.Post, "/local/dictation/start") { Content = JsonContent.Create(new { }) }; spoof.Headers.Add("Origin", "https://attacker.example");
                Assert.AreEqual(HttpStatusCode.Forbidden, (await local.SendAsync(spoof)).StatusCode);
                using var cert = new DesktopTrust(identity.ComputerId);
                var pin = cert.CertificateSha256;
                using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c is not null && Convert.ToHexString(c.GetCertHash(HashAlgorithmName.SHA256)).Equals(pin, StringComparison.OrdinalIgnoreCase) };
                using var lan = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{lanPort}") };
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await lan.GetAsync("/api/health")).StatusCode);
                lan.DefaultRequestHeaders.Authorization = new("Bearer", limited); lan.DefaultRequestHeaders.Add("X-PhoneDeck-Client", scoped.ClientId);
                Assert.AreEqual(HttpStatusCode.Forbidden, (await lan.GetAsync($"/api/transcripts?targetComputerId={identity.ComputerId}")).StatusCode);
                lan.DefaultRequestHeaders.Authorization = new("Bearer", token); lan.DefaultRequestHeaders.Remove("X-PhoneDeck-Client"); lan.DefaultRequestHeaders.Add("X-PhoneDeck-Client", phone.ClientId);
                Assert.AreEqual(HttpStatusCode.NotFound, (await lan.GetAsync("/local/status")).StatusCode);
                Assert.AreEqual(HttpStatusCode.BadRequest, (await lan.GetAsync($"/api/transcripts?targetComputerId={Guid.NewGuid()}")).StatusCode);
                var session = Guid.NewGuid().ToString(); var envelope = new { protocolVersion = 2, requestId = Guid.NewGuid().ToString(), sessionId = session, targetComputerId = identity.ComputerId, mode = "dictation" };
                Assert.AreEqual(HttpStatusCode.OK, (await lan.PostAsJsonAsync("/api/dictation/start", envelope)).StatusCode);
                using var audio = new HttpRequestMessage(HttpMethod.Post, "/api/audio/stream") { Content = new ByteArrayContent(new byte[9600]) };
                audio.Headers.Add("X-PhoneDeck-Protocol", "2"); audio.Headers.Add("X-PhoneDeck-Computer-Id", identity.ComputerId); audio.Headers.Add("X-PhoneDeck-Session", session);
                Assert.AreEqual(HttpStatusCode.OK, (await lan.SendAsync(audio)).StatusCode);
                // Stop on the desktop before the phone has ever polled a recording state.
                Assert.AreEqual(HttpStatusCode.OK, (await local.PostAsJsonAsync("/local/dictation/stop", new { })).StatusCode);
                var stoppedHealth = await lan.GetFromJsonAsync<JsonElement>("/api/health");
                Assert.IsTrue(stoppedHealth.GetProperty("capabilities").EnumerateArray().Any(x => x.GetString() == "phoneStopV1"));
                Assert.AreEqual(session, stoppedHealth.GetProperty("audio").GetProperty("stopRequestedSessionId").GetString());
                Assert.IsFalse(stoppedHealth.GetProperty("dictation").GetProperty("active").GetBoolean());
                lan.DefaultRequestHeaders.Authorization = new("Bearer", limited); lan.DefaultRequestHeaders.Remove("X-PhoneDeck-Client"); lan.DefaultRequestHeaders.Add("X-PhoneDeck-Client", scoped.ClientId);
                var otherHealth = await lan.GetFromJsonAsync<JsonElement>("/api/health");
                Assert.AreEqual(JsonValueKind.Null, otherHealth.GetProperty("audio").GetProperty("stopRequestedSessionId").ValueKind);
                lan.DefaultRequestHeaders.Authorization = new("Bearer", token); lan.DefaultRequestHeaders.Remove("X-PhoneDeck-Client"); lan.DefaultRequestHeaders.Add("X-PhoneDeck-Client", phone.ClientId);
                Assert.AreEqual(HttpStatusCode.OK, (await lan.PostAsJsonAsync("/api/dictation/stop", envelope)).StatusCode);
                JsonElement results = default;
                for (var i = 0; i < 100; i++) { results = await lan.GetFromJsonAsync<JsonElement>($"/api/transcripts?targetComputerId={identity.ComputerId}"); if (results.GetProperty("results").GetArrayLength() == 1) break; await Task.Delay(30); }
                Assert.AreEqual(1, results.GetProperty("results").GetArrayLength());
                var imported = new Transcript(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "另一台电脑的最终文字", DateTimeOffset.UtcNow);
                for (var i = 0; i < 2; i++) Assert.AreEqual(HttpStatusCode.OK, (await lan.PostAsJsonAsync("/api/transcripts/receive", new ReceiveRequest(identity.ComputerId, imported))).StatusCode);
                var status = await local.GetFromJsonAsync<JsonElement>("/local/status"); Assert.AreEqual(2, status.GetProperty("history").GetArrayLength());
                Assert.AreEqual(HttpStatusCode.OK, (await local.PostAsJsonAsync("/local/clients/revoke", new { clientId = phone.ClientId })).StatusCode);
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await lan.GetAsync("/api/health")).StatusCode);
            }
            finally { await app.StopAsync(); }
        }
        finally { for (var i = 0; i < variables.Length; i++) Environment.SetEnvironmentVariable(variables[i], previous[i]); Directory.Delete(directory, true); }
    }
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
}
