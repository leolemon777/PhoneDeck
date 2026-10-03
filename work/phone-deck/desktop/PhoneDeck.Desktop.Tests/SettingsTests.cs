using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhoneDeck.Desktop;

namespace PhoneDeck.Desktop.Tests;

[TestClass]
public sealed class SettingsTests
{
    private static string Id() => Guid.NewGuid().ToString();
    private static Action NoRuntimeChange(DesktopSettings value) => () => { };
    [TestMethod] public void TwoComputersPersistIndependentSettingsAndRejectCrossTargetAndStaleDrafts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Yandu-settings-" + Id());
        try
        {
            var oneId = Id(); var twoId = Id();
            var one = new DesktopSettingsStore(Path.Combine(directory, "one"), oneId, "one");
            var two = new DesktopSettingsStore(Path.Combine(directory, "two"), twoId, "two");
            var oldOneRevision = one.Revision;
            var first = one.Current with { DisplayName = "书房 Windows", Language = "zh", AutoInsert = false,
                TapShortcut = "Ctrl+Alt+F8", HoldShortcut = "Ctrl+Alt+F9" };
            one.Save(new(oneId, one.Revision, first), NoRuntimeChange);
            var second = two.Current with { DisplayName = "办公室 Linux", Language = "en", TapShortcut = "Ctrl+Alt+B", HoldShortcut = "Ctrl+Alt+N" };
            two.Save(new(twoId, two.Revision, second), NoRuntimeChange);
            Assert.ThrowsExactly<ArgumentException>(() => one.Save(new(twoId, one.Revision, second), NoRuntimeChange));
            Assert.ThrowsExactly<InvalidOperationException>(() => one.Save(new(oneId, oldOneRevision, second), NoRuntimeChange));
            var reloadedOne = new DesktopSettingsStore(Path.Combine(directory, "one"), oneId, "changed-system-name");
            var reloadedTwo = new DesktopSettingsStore(Path.Combine(directory, "two"), twoId, "changed-system-name");
            Assert.AreEqual(first, reloadedOne.Current); Assert.AreEqual(second, reloadedTwo.Current);
            Assert.AreEqual(one.Revision, reloadedOne.Revision); Assert.AreNotEqual(one.Revision, two.Revision);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [TestMethod] public void FailedAtomicWriteRollsBackRuntimeAndKeepsPreviousSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Yandu-settings-" + Id());
        Directory.CreateDirectory(Path.Combine(directory, "desktop-settings.json"));
        try
        {
            var id = Id(); var store = new DesktopSettingsStore(directory, id, "original"); var revision = store.Revision;
            var runtimeName = "original";
            Assert.ThrowsExactly<IOException>(() => store.Save(new(id, revision, store.Current with { DisplayName = "new" }), value =>
            { runtimeName = value.DisplayName; return () => runtimeName = "original"; }));
            Assert.AreEqual("original", runtimeName); Assert.AreEqual("original", store.Current.DisplayName);
            Assert.AreEqual(revision, store.Revision); Assert.IsEmpty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [TestMethod] public void InvalidOrForeignSavedConfigurationIsNotSilentlyReset()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Yandu-settings-" + Id()); Directory.CreateDirectory(directory);
        try
        {
            var one = Id(); var store = new DesktopSettingsStore(directory, one, "one");
            store.Save(new(one, store.Revision, store.Current), NoRuntimeChange);
            Assert.ThrowsExactly<InvalidDataException>(() => new DesktopSettingsStore(directory, Id(), "another"));
            File.WriteAllText(Path.Combine(directory, "desktop-settings.json"), "{ broken }");
            Assert.ThrowsExactly<InvalidDataException>(() => new DesktopSettingsStore(directory, one, "one"));
            Assert.AreEqual("{ broken }", File.ReadAllText(Path.Combine(directory, "desktop-settings.json")));
        }
        finally { Directory.Delete(directory, true); }
    }
    [TestMethod] public void CandidateValidationRejectsCommandsDuplicatedKeysAndInvalidLanguages()
    {
        var value = new DesktopSettings("one", "auto", true, "Ctrl+Alt+Space", "Ctrl+Alt+V");
        foreach (var bad in new[] { value with { TapShortcut = "cmd /c calc" }, value with { HoldShortcut = value.TapShortcut },
            value with { Language = "zh; command" }, value with { DisplayName = "one\nline" }, value with { DisplayName = new string('a', 65) } })
            Assert.ThrowsExactly<ArgumentException>(() => DesktopSettingsStore.Validate(bad));
        foreach (var option in VoiceShortcutOptions.All) { Assert.IsGreaterThan(0U, VoiceShortcutOptions.WindowsKey(option.Id)); Assert.IsTrue(VoiceShortcutOptions.X11Keysym(option.Id) != 0); }
    }
    [TestMethod] public void ConfigurationTransactionCannotStartSpeechOrApplyDuringAStream()
    {
        var id = Id(); using var speech = new SpeechSession(new FakeEngine(), new TranscriptStore(id), id);
        speech.WhileIdle(() =>
        {
            Assert.IsTrue(speech.Busy);
            Assert.ThrowsExactly<InvalidOperationException>(() => speech.Start("phone", Id()));
            Assert.ThrowsExactly<InvalidOperationException>(() => speech.Attach("phone", Id(), "shared"));
        });
        Assert.IsFalse(speech.Busy);
        var session = Id(); speech.Attach("phone", session, "shared");
        Assert.ThrowsExactly<InvalidOperationException>(() => speech.WhileIdle(() => Assert.Fail("Must not apply")));
        speech.EndStream(session, true); speech.WhileIdle(() => { });
    }
    [TestMethod] public async Task AutoInsertOptOutStillProducesFinalSyncResult()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Yandu-settings-" + Id());
        try
        {
            var computer = Id(); var store = new DesktopSettingsStore(directory, computer, "one"); var history = new TranscriptStore(computer); var insertions = 0;
            using var speech = new SpeechSession(new FakeEngine(), history, computer, () => store.Current.AutoInsert ? _ => insertions++ : null);
            store.Save(new(computer, store.Revision, store.Current with { AutoInsert = false }), NoRuntimeChange);
            var session = Id(); speech.Start("phone", session); speech.Attach("phone", session, "managed"); speech.Feed(session, new byte[9600]);
            speech.EndStream(session, true); await speech.StopAsync("phone", session); await speech.WaitForResultAsync();
            Assert.AreEqual(0, insertions); Assert.HasCount(1, history.LocalHistory());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [TestMethod] public async Task RealHttpSettingsEnforceScopeTargetRevisionAndBusyAndReflectSavedName()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Yandu-http-settings-" + Id()); Directory.CreateDirectory(directory);
        var variables = new[] { "PHONEDECK_DATA_DIR", "PHONEDECK_LOCAL_PORT", "PHONEDECK_LAN_PORT" };
        var previous = variables.Select(Environment.GetEnvironmentVariable).ToArray();
        var localPort = FreePort(); var lanPort = FreePort(); while (lanPort == localPort) lanPort = FreePort();
        Environment.SetEnvironmentVariable(variables[0], directory); Environment.SetEnvironmentVariable(variables[1], localPort.ToString()); Environment.SetEnvironmentVariable(variables[2], lanPort.ToString());
        try
        {
            var identity = ReceiverIdentity.LoadOrCreate(); var credentials = new ClientCredentialsStore(Path.Combine(directory, "clients.json"));
            var phone = credentials.Issue("settings phone", ["control", "audio", "settings"], Id(), out var token);
            var limited = credentials.Issue("control only", ["control"], Id(), out var limitedToken);
            await using var app = DesktopApp.Create(["--no-web-phone", "--no-browser", "--no-discovery", "--no-hotkeys"], new FakeEngine()); await app.StartAsync();
            try
            {
                using var local = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{localPort}") };
                using var trust = new DesktopTrust(identity.ComputerId);
                using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) => c is not null &&
                    Convert.ToHexString(c.GetCertHash(System.Security.Cryptography.HashAlgorithmName.SHA256)).Equals(trust.CertificateSha256, StringComparison.OrdinalIgnoreCase) };
                using var lan = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{lanPort}") };
                var endpoint = "/api/settings?targetComputerId=" + identity.ComputerId;
                Assert.AreEqual(HttpStatusCode.Unauthorized, (await lan.GetAsync(endpoint)).StatusCode);
                lan.DefaultRequestHeaders.Authorization = new("Bearer", limitedToken); lan.DefaultRequestHeaders.Add("X-PhoneDeck-Client", limited.ClientId);
                Assert.AreEqual(HttpStatusCode.Forbidden, (await lan.GetAsync(endpoint)).StatusCode);
                lan.DefaultRequestHeaders.Authorization = new("Bearer", token); lan.DefaultRequestHeaders.Remove("X-PhoneDeck-Client"); lan.DefaultRequestHeaders.Add("X-PhoneDeck-Client", phone.ClientId);
                Assert.AreEqual(HttpStatusCode.BadRequest, (await lan.GetAsync("/api/settings?targetComputerId=" + Id())).StatusCode);
                var initial = await lan.GetFromJsonAsync<JsonElement>(endpoint); var revision = initial.GetProperty("revision").GetString();
                var value = initial.GetProperty("settings").Deserialize<DesktopSettings>(new JsonSerializerOptions(JsonSerializerDefaults.Web))! with
                    { DisplayName = "电脑一", Language = "zh", AutoInsert = false, TapShortcut = "Ctrl+Alt+F8", HoldShortcut = "Ctrl+Alt+F9" };
                var request = new SettingsRequest(identity.ComputerId, revision, value);
                Assert.AreEqual(HttpStatusCode.BadRequest, (await lan.PostAsJsonAsync("/api/settings", request with { TargetComputerId = Id() })).StatusCode);
                Assert.AreEqual(HttpStatusCode.Forbidden, (await SendCrossOrigin(local, request)).StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, (await lan.PostAsJsonAsync("/api/settings", request)).StatusCode);
                Assert.AreEqual(HttpStatusCode.Conflict, (await lan.PostAsJsonAsync("/api/settings", request)).StatusCode);
                var saved = await lan.GetFromJsonAsync<JsonElement>(endpoint);
                Assert.AreEqual("disabled", saved.GetProperty("hotkeysStatus").GetString());
                var health = await local.GetFromJsonAsync<JsonElement>("/api/health"); Assert.AreEqual("电脑一", health.GetProperty("displayName").GetString());
                Assert.IsTrue(health.GetProperty("capabilities").EnumerateArray().Any(x => x.GetString() == "desktopSettingsV1"));
                var session = Id(); var envelope = new { protocolVersion = 2, requestId = Id(), sessionId = session, targetComputerId = identity.ComputerId, mode = "dictation" };
                Assert.AreEqual(HttpStatusCode.OK, (await lan.PostAsJsonAsync("/api/dictation/start", envelope)).StatusCode);
                var next = request with { Revision = saved.GetProperty("revision").GetString(), Settings = value with { DisplayName = "must not save" } };
                Assert.AreEqual(HttpStatusCode.Conflict, (await lan.PostAsJsonAsync("/api/settings", next)).StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, (await lan.PostAsJsonAsync("/api/dictation/stop", new { protocolVersion = 2, requestId = Id(), sessionId = session, targetComputerId = identity.ComputerId, cancel = true })).StatusCode);
                Assert.AreEqual("电脑一", new DesktopSettingsStore(directory, identity.ComputerId, "system").Current.DisplayName);
                Assert.AreEqual(HttpStatusCode.BadRequest, (await lan.PostAsJsonAsync("/api/settings", new { targetComputerId = identity.ComputerId, revision = next.Revision, settings = new { displayName = "missing-fields" } })).StatusCode);
            }
            finally { await app.StopAsync(); }
        }
        finally { for (var i = 0; i < variables.Length; i++) Environment.SetEnvironmentVariable(variables[i], previous[i]); Directory.Delete(directory, true); }
    }
    [TestMethod] public async Task WindowsNativeShortcutConflictRestoresPreviousRegistrationAndRevision()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Requires the Windows native RegisterHotKey backend");
        var directory = Path.Combine(Path.GetTempPath(), "Yandu-native-settings-" + Id()); Directory.CreateDirectory(directory);
        var variables = new[] { "PHONEDECK_DATA_DIR", "PHONEDECK_LOCAL_PORT", "PHONEDECK_LAN_PORT" };
        var previous = variables.Select(Environment.GetEnvironmentVariable).ToArray();
        var localPort = FreePort(); var lanPort = FreePort(); while (lanPort == localPort) lanPort = FreePort();
        Environment.SetEnvironmentVariable(variables[0], directory); Environment.SetEnvironmentVariable(variables[1], localPort.ToString()); Environment.SetEnvironmentVariable(variables[2], lanPort.ToString());
        try
        {
            var identity = ReceiverIdentity.LoadOrCreate(); var store = new DesktopSettingsStore(directory, identity.ComputerId, "native test");
            store.Save(new(identity.ComputerId, store.Revision, store.Current with { TapShortcut = "Ctrl+Alt+F8", HoldShortcut = "Ctrl+Alt+F9" }), NoRuntimeChange);
            using var otherSpeech = new SpeechSession(new FakeEngine(), new TranscriptStore(Id()), Id());
            using var blocked = new DesktopHotkeys(otherSpeech, "Ctrl+Alt+F10", "Ctrl+Alt+F11");
            if (blocked.WaitUntilReady() != "ready") Assert.Inconclusive("Isolated native test keys are already occupied by another application");
            await using var app = DesktopApp.Create(["--no-web-phone", "--no-browser", "--no-discovery"], new FakeEngine()); await app.StartAsync();
            try
            {
                using var local = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{localPort}") };
                JsonElement before = default;
                for (var i = 0; i < 50; i++)
                { before = await local.GetFromJsonAsync<JsonElement>("/local/settings"); if (before.GetProperty("hotkeysStatus").GetString() != "checking") break; await Task.Delay(20); }
                if (before.GetProperty("hotkeysStatus").GetString() != "ready") Assert.Inconclusive("Isolated native original keys are already occupied by another application");
                var value = store.Current with { DisplayName = "must not persist", TapShortcut = "Ctrl+Alt+F10", HoldShortcut = "Ctrl+Alt+F11" };
                Assert.AreEqual(HttpStatusCode.Conflict, (await local.PostAsJsonAsync("/local/settings", new SettingsRequest(identity.ComputerId, store.Revision, value))).StatusCode);
                var after = await local.GetFromJsonAsync<JsonElement>("/local/settings");
                Assert.AreEqual("ready", after.GetProperty("hotkeysStatus").GetString());
                Assert.AreEqual(before.GetProperty("revision").GetString(), after.GetProperty("revision").GetString());
                Assert.AreEqual("Ctrl+Alt+F8", after.GetProperty("settings").GetProperty("tapShortcut").GetString());
                Assert.AreEqual(store.Current, new DesktopSettingsStore(directory, identity.ComputerId, "unused").Current);
            }
            finally { await app.StopAsync(); }
        }
        finally { for (var i = 0; i < variables.Length; i++) Environment.SetEnvironmentVariable(variables[i], previous[i]); Directory.Delete(directory, true); }
    }
    private static async Task<HttpResponseMessage> SendCrossOrigin(HttpClient client, SettingsRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/local/settings") { Content = JsonContent.Create(request) }; message.Headers.Add("Origin", "https://attacker.example");
        return await client.SendAsync(message);
    }
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
}
