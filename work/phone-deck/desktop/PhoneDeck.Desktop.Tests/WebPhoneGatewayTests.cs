using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhoneDeck.Desktop;

namespace PhoneDeck.Desktop.Tests;

[TestClass]
public sealed class WebPhoneGatewayTests
{
    [TestMethod]
    public async Task PairingNeedsLocalConfirmationAndOriginCookieIsPrivateAndRevocationEndsManagedAudio()
    {
        await using var fixture = await Fixture.Create();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await fixture.Http.GetAsync("/phone/api/state")).StatusCode);
        using var cross = new HttpRequestMessage(HttpMethod.Post, "/phone/pair") { Content = JsonContent.Create(new { clientId = Guid.NewGuid().ToString() }) };
        cross.Headers.Add("Origin", "https://attacker.example");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await fixture.Http.SendAsync(cross)).StatusCode);
        var cookie = await fixture.Pair();
        StringAssert.Contains(cookie.Full, "secure"); StringAssert.Contains(cookie.Full, "httponly"); StringAssert.Contains(cookie.Full, "samesite=strict"); StringAssert.Contains(cookie.Full, "path=/phone");
        using var socket = await fixture.Connect(cookie.Value);
        var session = Guid.NewGuid().ToString(); await Send(socket, new { type = "start", requestId = "start", sessionId = session, mode = "managed", targetIds = new[] { fixture.Identity.ComputerId } });
        await Receive(socket, m => Is(m, "ack", "start")); Assert.IsTrue(fixture.Speech.Recording);
        await socket.SendAsync(new byte[1920], WebSocketMessageType.Binary, true, CancellationToken.None);
        var clientId = (await fixture.State(cookie.Value)).GetProperty("clientId").GetString();
        Assert.AreEqual(HttpStatusCode.OK, (await fixture.Http.PostAsJsonAsync("/local/web/revoke", new { clientId })).StatusCode);
        await Until(() => !fixture.Speech.Streaming && !fixture.Speech.Recording);
        using var revoked = new HttpRequestMessage(HttpMethod.Get, "/phone/api/state"); revoked.Headers.Add("Cookie", cookie.Value);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await fixture.Http.SendAsync(revoked)).StatusCode);
    }
    [TestMethod]
    public async Task DesktopManagedStopPushesStopBeforeNextPhoneCommandAndOldStopCannotKillNewRecording()
    {
        await using var fixture = await Fixture.Create(); var cookie = await fixture.Pair(); using var socket = await fixture.Connect(cookie.Value);
        var first = Guid.NewGuid().ToString();
        await Send(socket, new { type = "start", requestId = "start-1", sessionId = first, mode = "managed", targetIds = new[] { fixture.Identity.ComputerId } });
        await Receive(socket, m => Is(m, "ack", "start-1"));
        await fixture.Speech.StopLocalAsync();
        var stopped = await Receive(socket, m => m.GetProperty("type").GetString() == "stopped");
        Assert.AreEqual(first, stopped.GetProperty("sessionId").GetString()); Assert.IsFalse(fixture.Speech.Streaming);
        await fixture.Speech.WaitForResultAsync();
        var next = Guid.NewGuid().ToString();
        await Send(socket, new { type = "start", requestId = "start-2", sessionId = next, mode = "managed", targetIds = new[] { fixture.Identity.ComputerId } });
        await Receive(socket, m => Is(m, "ack", "start-2"));
        await Send(socket, new { type = "stop", requestId = "old-stop", sessionId = first }); await Receive(socket, m => Is(m, "ack", "old-stop"));
        Assert.AreEqual(next, fixture.Speech.StreamSession); Assert.IsTrue(fixture.Speech.Recording);
        await Send(socket, new { type = "stop", requestId = "end", sessionId = next, cancel = true }); await Receive(socket, m => Is(m, "ack", "end"));
    }
    [TestMethod]
    public async Task SharedThreePeersKeepSupplyAfterEachDesktopStopAndOneBrokenPeerDoesNotBlockOthers()
    {
        var targets = Enumerable.Range(0, 3).Select(i => new FakeTarget()).ToArray();
        await using var fixture = await Fixture.Create(_ => targets); var cookie = await fixture.Pair(); using var socket = await fixture.Connect(cookie.Value);
        var session = Guid.NewGuid().ToString();
        await Send(socket, new { type = "start", requestId = "start", sessionId = session, mode = "shared", targetIds = targets.Select(x => x.Id).ToArray() }); await Receive(socket, m => Is(m, "ack", "start"));
        foreach (var target in targets)
        {
            target.Recording = true; await socket.SendAsync(new byte[1920], WebSocketMessageType.Binary, true, CancellationToken.None);
            await Until(() => target.Frames > 0); target.Recording = false; Assert.AreEqual(session, target.Session);
        }
        targets[1].FailFeed = true;
        for (var i = 0; i < 8; i++) await socket.SendAsync(new byte[1920], WebSocketMessageType.Binary, true, CancellationToken.None);
        await Until(() => targets[0].Frames >= 11 && targets[2].Frames >= 11);
        Assert.AreEqual(session, targets[0].Session); Assert.AreEqual(session, targets[2].Session);
        await Send(socket, new { type = "stop", requestId = "end", sessionId = session }); await Receive(socket, m => Is(m, "ack", "end"));
        Assert.IsTrue(targets.All(x => x.Session is null));
    }
    [TestMethod]
    public async Task FingerReleaseCancelsSlowStartAndLateAcknowledgementCannotRestoreEndedSession()
    {
        var target = new FakeTarget { BlockHealth = true };
        await using var fixture = await Fixture.Create(_ => [target]); var cookie = await fixture.Pair(); using var socket = await fixture.Connect(cookie.Value);
        var session = Guid.NewGuid().ToString();
        await Send(socket, new { type = "start", requestId = "start", sessionId = session, mode = "shared", targetIds = new[] { target.Id } });
        await target.HealthEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Send(socket, new { type = "stop", requestId = "stop", sessionId = session }); await Receive(socket, m => Is(m, "ack", "stop"));
        target.BlockHealth = false; target.ReleaseHealth.TrySetResult();
        await Task.Delay(100); Assert.IsNull(target.Session);
        await Send(socket, new { type = "start", requestId = "replay", sessionId = session, mode = "shared", targetIds = new[] { target.Id } });
        await Receive(socket, m => Is(m, "error", "replay")); Assert.IsNull(target.Session);
    }
    [TestMethod]
    public async Task SharedActualSpeechSessionSupportsRepeatedDesktopSegmentsAndSocketLossCancelsOnlyWebOwner()
    {
        await using var fixture = await Fixture.Create(); var cookie = await fixture.Pair(); using var socket = await fixture.Connect(cookie.Value);
        var session = Guid.NewGuid().ToString();
        await Send(socket, new { type = "start", requestId = "start", sessionId = session, mode = "shared", targetIds = new[] { fixture.Identity.ComputerId } }); await Receive(socket, m => Is(m, "ack", "start"));
        for (var i = 0; i < 3; i++)
        {
            fixture.Speech.StartLocal(false); await socket.SendAsync(new byte[1920], WebSocketMessageType.Binary, true, CancellationToken.None);
            await Task.Delay(30); await fixture.Speech.StopLocalAsync(); await fixture.Speech.WaitForResultAsync();
            Assert.IsTrue(fixture.Speech.Streaming); Assert.AreEqual(session, fixture.Speech.StreamSession);
        }
        fixture.Speech.StartLocal(false); socket.Abort(); await Until(() => !fixture.Speech.Streaming && !fixture.Speech.Recording);
        // A revoked browser identity must not match the native phone's owner namespace.
        var native = Guid.NewGuid().ToString(); fixture.Speech.Attach("native-phone", native, "managed"); fixture.Speech.Start("native-phone", native);
        var clientId = (await fixture.State(cookie.Value)).GetProperty("clientId").GetString();
        await fixture.Http.PostAsJsonAsync("/local/web/revoke", new { clientId });
        Assert.IsTrue(fixture.Speech.Recording); Assert.AreEqual(native, fixture.Speech.StreamSession);
    }
    [TestMethod]
    public async Task SelectionRequiresKnownOnlineTargetAndInputIsBoundedAndDeduplicated()
    {
        var target = new FakeTarget(); await using var fixture = await Fixture.Create(_ => [target]); var cookie = await fixture.Pair(); using var socket = await fixture.Connect(cookie.Value);
        await Send(socket, new { type = "select", requestId = "bad", targetId = Guid.NewGuid().ToString() }); await Receive(socket, m => Is(m, "error", "bad"));
        await Send(socket, new { type = "select", requestId = "select", targetId = target.Id }); await Receive(socket, m => Is(m, "ack", "select"));
        for (var i = 0; i < 2; i++) { await Send(socket, new { type = "input", requestId = "input", targetId = target.Id, action = "backspace" }); await Receive(socket, m => Is(m, "ack", "input")); }
        Assert.AreEqual(1, target.Inputs);
        await Send(socket, new { type = "input", requestId = "script", targetId = target.Id, action = "shell" }); await Receive(socket, m => Is(m, "error", "script")); Assert.AreEqual(1, target.Inputs);
    }
    [TestMethod]
    public async Task SlowSharedPeerDoesNotDelayReadyPeerOrManagedDesktopStop()
    {
        var fast = new FakeTarget(); var slow = new FakeTarget { BlockHealth = true };
        await using var fixture = await Fixture.Create(_ => [fast, slow]); var cookie = await fixture.Pair(); using var socket = await fixture.Connect(cookie.Value);
        var session = Guid.NewGuid().ToString(); var startedAt = Environment.TickCount64;
        await Send(socket, new { type = "start", requestId = "start", sessionId = session, mode = "shared", targetIds = new[] { fast.Id, slow.Id } });
        await Receive(socket, m => Is(m, "ack", "start")); Assert.IsLessThan(1200L, Environment.TickCount64 - startedAt);
        await socket.SendAsync(new byte[1920], WebSocketMessageType.Binary, true, CancellationToken.None); await Until(() => fast.Frames == 1);
        await Send(socket, new { type = "stop", requestId = "stop", sessionId = session }); await Receive(socket, m => Is(m, "ack", "stop"));
        Assert.IsNull(fast.Session); slow.ReleaseHealth.TrySetResult();
    }
    [TestMethod]
    public async Task FragmentedCommandsAndPcmAreAcceptedAndIdleAudioHasBoundedLifetime()
    {
        await using var fixture = await Fixture.Create(); var cookie = await fixture.Pair(); using var socket = await fixture.Connect(cookie.Value);
        var session = Guid.NewGuid().ToString(); var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "start", requestId = "start", sessionId = session, mode = "shared", targetIds = new[] { fixture.Identity.ComputerId } }));
        await socket.SendAsync(bytes.AsMemory(0, 17), WebSocketMessageType.Text, false, CancellationToken.None);
        await socket.SendAsync(bytes.AsMemory(17), WebSocketMessageType.Text, true, CancellationToken.None);
        await Receive(socket, m => Is(m, "ack", "start")); Assert.IsTrue(fixture.Speech.Streaming);
        await socket.SendAsync(new byte[730], WebSocketMessageType.Binary, false, CancellationToken.None);
        await socket.SendAsync(new byte[1190], WebSocketMessageType.Binary, false, CancellationToken.None);
        await socket.SendAsync(Array.Empty<byte>(), WebSocketMessageType.Binary, true, CancellationToken.None);
        var stopped = await Receive(socket, m => m.GetProperty("type").GetString() == "stopped");
        Assert.AreEqual(session, stopped.GetProperty("sessionId").GetString()); Assert.IsFalse(fixture.Speech.Streaming);
    }
    [TestMethod]
    public async Task UnrelatedStalledPeerCannotDelayManagedDesktopStop()
    {
        var target = new FakeTarget(); var stalled = new FakeTarget { BlockHealth = true };
        await using var fixture = await Fixture.Create(_ => [target, stalled]); var cookie = await fixture.Pair(); using var socket = await fixture.Connect(cookie.Value);
        await Send(socket, new { type = "select", requestId = "select", targetId = target.Id }); await Receive(socket, m => Is(m, "ack", "select"));
        var session = Guid.NewGuid().ToString();
        await Send(socket, new { type = "start", requestId = "start", sessionId = session, mode = "managed", targetIds = new[] { target.Id } }); await Receive(socket, m => Is(m, "ack", "start"));
        var at = Environment.TickCount64; target.StopReceipt = session;
        await Receive(socket, m => m.GetProperty("type").GetString() == "stopped");
        Assert.IsLessThan(650L, Environment.TickCount64 - at); Assert.IsNull(target.Session);
        stalled.ReleaseHealth.TrySetResult();
    }
    [TestMethod]
    public async Task LocallyApprovedRepairRevokesOldCookieAndSocketAndCreatesUsableNewIdentity()
    {
        await using var fixture = await Fixture.Create(); var cookie = await fixture.Pair(); using var socket = await fixture.Connect(cookie.Value);
        var owner = (await fixture.State(cookie.Value)).GetProperty("clientId").GetString(); var session = Guid.NewGuid().ToString();
        await Send(socket, new { type = "start", requestId = "start", sessionId = session, mode = "managed", targetIds = new[] { fixture.Identity.ComputerId } }); await Receive(socket, m => Is(m, "ack", "start"));
        var repaired = await fixture.Pair(owner); Assert.AreNotEqual(cookie.Value, repaired.Value);
        await Until(() => !fixture.Speech.Streaming && !fixture.Speech.Recording);
        using var old = new HttpRequestMessage(HttpMethod.Get, "/phone/api/state"); old.Headers.Add("Cookie", cookie.Value);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await fixture.Http.SendAsync(old)).StatusCode);
        using var fresh = await fixture.Connect(repaired.Value);
        await Send(fresh, new { type = "ping", requestId = "ping" }); await Receive(fresh, m => Is(m, "pong", "ping"));
        using var attacker = new ClientWebSocket(); attacker.Options.SetRequestHeader("Cookie", repaired.Value); attacker.Options.SetRequestHeader("Origin", "https://attacker.example");
        await Assert.ThrowsExactlyAsync<WebSocketException>(() => attacker.ConnectAsync(new Uri(fixture.Origin.Replace("http:", "ws:") + "/phone/socket"), CancellationToken.None));
    }
    [TestMethod]
    public async Task MalformedUnrelatedHealthIsIsolatedAndLocalStopLatencyStaysBounded()
    {
        var broken = new FakeTarget { MalformedHealth = true }; var local = new FakeTarget();
        await using var fixture = await Fixture.Create(_ => [local, broken]); var cookie = await fixture.Pair(); using var socket = await fixture.Connect(cookie.Value);
        await Send(socket, new { type = "select", requestId = "select", targetId = local.Id }); await Receive(socket, m => Is(m, "ack", "select"));
        var samples = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            var session = Guid.NewGuid().ToString();
            await Send(socket, new { type = "start", requestId = "start-" + i, sessionId = session, mode = "managed", targetIds = new[] { local.Id } }); await Receive(socket, m => Is(m, "ack", "start-" + i));
            var at = Environment.TickCount64; local.StopReceipt = session;
            await Receive(socket, m => m.GetProperty("type").GetString() == "stopped"); samples.Add(Environment.TickCount64 - at);
        }
        Console.WriteLine("In-process target receipt to real WebSocket stop samples (ms): " + string.Join(",", samples));
        Assert.IsLessThan(250L, samples.Order().ElementAt(2));
        var state = await fixture.State(cookie.Value); var rejected = state.GetProperty("targets").EnumerateArray().Single(x => x.GetProperty("id").GetString() == broken.Id);
        Assert.IsFalse(rejected.GetProperty("online").GetBoolean());
        Assert.AreEqual("连接中断或授权失效", rejected.GetProperty("error").GetString());
    }
    [TestMethod]
    public async Task ActualLocalSpeechStopToBrowserNotificationHasSub250msMedian()
    {
        await using var fixture = await Fixture.Create(); var cookie = await fixture.Pair(); using var socket = await fixture.Connect(cookie.Value);
        var samples = new List<long>();
        for (var i = 0; i < 4; i++)
        {
            var session = Guid.NewGuid().ToString();
            await Send(socket, new { type = "start", requestId = "start-" + i, sessionId = session, mode = "managed", targetIds = new[] { fixture.Identity.ComputerId } }); await Receive(socket, m => Is(m, "ack", "start-" + i));
            await socket.SendAsync(new byte[1920], WebSocketMessageType.Binary, true, CancellationToken.None);
            var at = Environment.TickCount64; await fixture.Speech.StopLocalAsync();
            await Receive(socket, m => m.GetProperty("type").GetString() == "stopped"); samples.Add(Environment.TickCount64 - at);
            Assert.IsFalse(fixture.Speech.Streaming); await fixture.Speech.WaitForResultAsync();
        }
        Console.WriteLine("Actual SpeechSession desktop stop to real WebSocket samples with generated PCM/fake engine (ms): " + string.Join(",", samples));
        Assert.IsLessThan(250L, samples.Order().ElementAt(2));
    }
    [TestMethod]
    public async Task DesktopStopNotifiesBrowserBeforeSlowPeerTeardownCompletes()
    {
        var target = new FakeTarget { BlockStop = true }; await using var fixture = await Fixture.Create(_ => [target]); var cookie = await fixture.Pair(); using var socket = await fixture.Connect(cookie.Value);
        await Send(socket, new { type = "select", requestId = "select", targetId = target.Id }); await Receive(socket, m => Is(m, "ack", "select"));
        var session = Guid.NewGuid().ToString();
        await Send(socket, new { type = "start", requestId = "start", sessionId = session, mode = "managed", targetIds = new[] { target.Id } }); await Receive(socket, m => Is(m, "ack", "start"));
        var at = Environment.TickCount64; target.StopReceipt = session;
        await Receive(socket, m => m.GetProperty("type").GetString() == "stopped");
        Assert.IsLessThan(650L, Environment.TickCount64 - at); Assert.IsFalse(target.ReleaseStop.Task.IsCompleted);
        target.ReleaseStop.TrySetResult();
    }
    [TestMethod]
    public async Task InputCannotUseUnconfirmedPairedTarget()
    {
        var target = new FakeTarget(); await using var fixture = await Fixture.Create(_ => [target]); var cookie = await fixture.Pair(); using var socket = await fixture.Connect(cookie.Value);
        await Send(socket, new { type = "input", requestId = "unselected", targetId = target.Id, action = "enter" }); await Receive(socket, m => Is(m, "error", "unselected"));
        Assert.AreEqual(0, target.Inputs);
    }
    [TestMethod]
    public async Task PairingLinksUseOnlyCurrentCertificateAddressesAndRejectKnownEmptyNetwork()
    {
        string[] certificateHosts = ["192.168.0.2", "localhost"];
        string[] currentAddresses = [];
        await using var fixture = await Fixture.Create(browserAddresses: () => currentAddresses.Intersect(certificateHosts).ToArray());
        using (var empty = await fixture.Http.PostAsJsonAsync("/local/web/begin", new { }))
        {
            Assert.AreEqual(HttpStatusCode.Conflict, empty.StatusCode);
            var error = await empty.Content.ReadFromJsonAsync<JsonElement>();
            Assert.AreEqual("网络地址已变化或未连接局域网，请重启接收端后重新打开手机入口", error.GetProperty("error").GetString());
        }
        currentAddresses = ["192.168.0.2", "192.168.0.99"];
        using (var ready = await fixture.Http.PostAsJsonAsync("/local/web/begin", new { }))
        {
            ready.EnsureSuccessStatusCode(); var body = await ready.Content.ReadFromJsonAsync<JsonElement>();
            var urls = body.GetProperty("urls").EnumerateArray().Select(x => new Uri(x.GetString()!)).ToArray();
            Assert.AreEqual(1, urls.Length); Assert.AreEqual("192.168.0.2", urls[0].Host);
            StringAssert.StartsWith(urls[0].Fragment, "#pair=");
            using var material = JsonDocument.Parse(Uri.UnescapeDataString(urls[0].Fragment[6..]));
            Assert.AreEqual(body.GetProperty("pairingId").GetString(), material.RootElement.GetProperty("pairingId").GetString());
        }
        currentAddresses = ["192.168.0.99"];
        using var changed = await fixture.Http.PostAsJsonAsync("/local/web/begin", new { }); Assert.AreEqual(HttpStatusCode.Conflict, changed.StatusCode);
        var status = await fixture.Http.GetFromJsonAsync<JsonElement>("/local/web/status");
        Assert.IsFalse(status.GetProperty("pairing").GetProperty("open").GetBoolean());
    }
    [TestMethod]
    public void PeerAddressPolicyRejectsUrlsPublicLoopbackAndMetadataEndpoints()
    {
        foreach (var address in new[] { "127.0.0.1", "::1", "169.254.169.254", "8.8.8.8", "https://192.168.0.1", "localhost", "example.com", "224.0.0.1" }) Assert.IsFalse(WebPhoneRemoteTarget.AllowedAddress(address));
        foreach (var address in new[] { "192.168.1.23", "10.0.0.2", "172.16.0.2", "fd01::2" }) Assert.IsTrue(WebPhoneRemoteTarget.AllowedAddress(address));
    }
    private static bool Is(JsonElement message, string type, string request) => message.GetProperty("type").GetString() == type && message.TryGetProperty("requestId", out var r) && r.GetString() == request;
    private static Task Send(ClientWebSocket socket, object value) => socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)), WebSocketMessageType.Text, true, CancellationToken.None);
    private static async Task<JsonElement> Receive(ClientWebSocket socket, Func<JsonElement, bool> match)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6)); var buffer = new byte[32768];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, deadline.Token); Assert.AreEqual(WebSocketMessageType.Text, result.MessageType);
            using var json = JsonDocument.Parse(buffer.AsMemory(0, result.Count)); if (match(json.RootElement)) return json.RootElement.Clone();
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 150; i++) { if (condition()) return; await Task.Delay(20); }
        Assert.IsTrue(condition());
    }
    private sealed class FakeTarget : IWebPhoneTarget
    {
        public string Id { get; } = Guid.NewGuid().ToString(); public string Name => "Test computer";
        internal string? Session, StopReceipt; internal bool Recording, FailFeed, BlockHealth, MalformedHealth, BlockStop;
        internal int Frames, Inputs;
        internal readonly TaskCompletionSource HealthEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseHealth = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<WebPhoneTargetState> HealthAsync(CancellationToken cancellation)
        {
            if (MalformedHealth) throw new JsonException("malformed response must not leak remote content");
            HealthEntered.TrySetResult(); if (BlockHealth) await ReleaseHealth.Task.WaitAsync(cancellation);
            return new(Id, Name, true, true, Session is not null, Recording, Session, StopReceipt);
        }
        public Task StartAsync(string session, string mode, CancellationToken cancellation) { cancellation.ThrowIfCancellationRequested(); Session = session; return Task.CompletedTask; }
        public bool Feed(string session, byte[] pcm) { if (FailFeed || Session != session) return false; Interlocked.Increment(ref Frames); return true; }
        public async Task StopAsync(string session, bool cancel, CancellationToken cancellation)
        {
            if (Session == session) { Session = null; Recording = false; if (BlockStop) await ReleaseStop.Task.WaitAsync(cancellation); }
        }
        public Task InputAsync(string request, string action, CancellationToken cancellation) { Inputs++; return Task.CompletedTask; }
        public void Dispose() { }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal required WebApplication App; internal required HttpClient Http; internal required string Directory, Origin;
        internal required ReceiverIdentity Identity; internal required SpeechSession Speech; internal required WebPhoneGateway Gateway;
        internal static async Task<Fixture> Create(Func<string, IWebPhoneTarget[]>? targets = null, Func<string[]>? browserAddresses = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), "PhoneDeck-Web-Test-" + Guid.NewGuid()); System.IO.Directory.CreateDirectory(directory);
            var identity = new ReceiverIdentity(Guid.NewGuid().ToString(), "Fixture", "test", "test");
            var speech = new SpeechSession(new FakeEngine(), new TranscriptStore(identity.ComputerId), identity.ComputerId);
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var origin = $"http://127.0.0.1:{port}"; var builder = WebApplication.CreateBuilder(Array.Empty<string>()); builder.Logging.ClearProviders(); builder.WebHost.UseUrls(origin);
            var app = builder.Build(); var gateway = new WebPhoneGateway(identity, speech, new PlatformInput(), directory, port, new string('a', 64), () => true, context => context.Request.Headers.Origin == origin, targets, browserAddresses);
            gateway.Map(app); await app.StartAsync();
            return new Fixture { Directory = directory, Identity = identity, Speech = speech, Gateway = gateway, Origin = origin, App = app, Http = new HttpClient { BaseAddress = new Uri(origin) } };
        }
        internal async Task<(string Value, string Full)> Pair(string? clientId = null)
        {
            using var begun = await Http.PostAsJsonAsync("/local/web/begin", new { }); begun.EnsureSuccessStatusCode();
            var status = await Http.GetFromJsonAsync<JsonElement>("/local/web/status"); var pairing = status.GetProperty("pairing");
            using var payload = JsonDocument.Parse(pairing.GetProperty("qrPayload").GetString()!);
            var id = pairing.GetProperty("pairingId").GetString();
            var request = new HttpRequestMessage(HttpMethod.Post, "/phone/pair") { Content = JsonContent.Create(new { pairingId = id, oneTimeMaterial = payload.RootElement.GetProperty("oneTimeMaterial").GetString(), clientId = clientId ?? Guid.NewGuid().ToString(), clientLabel = "Test browser" }) };
            request.Headers.Add("Origin", Origin); var submitting = Http.SendAsync(request);
            for (var i = 0; i < 100; i++)
            {
                var pending = (await Http.GetFromJsonAsync<JsonElement>("/local/web/status")).GetProperty("pairing");
                if (pending.GetProperty("pending").ValueKind != JsonValueKind.Null) break; await Task.Delay(10);
            }
            Assert.IsFalse(submitting.IsCompleted, "Pairing must wait for local confirmation");
            (await Http.PostAsJsonAsync("/local/web/confirm", new { pairingId = id })).EnsureSuccessStatusCode();
            using var response = await submitting; response.EnsureSuccessStatusCode(); var full = response.Headers.GetValues("Set-Cookie").Single(); return (full.Split(';')[0], full);
        }
        internal async Task<JsonElement> State(string cookie)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/phone/api/state"); request.Headers.Add("Cookie", cookie);
            using var response = await Http.SendAsync(request); response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<JsonElement>();
        }
        internal async Task<ClientWebSocket> Connect(string cookie)
        {
            var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Cookie", cookie); socket.Options.SetRequestHeader("Origin", Origin);
            await socket.ConnectAsync(new Uri(Origin.Replace("http:", "ws:") + "/phone/socket"), CancellationToken.None); return socket;
        }
        public async ValueTask DisposeAsync()
        {
            Gateway.Dispose(); await App.StopAsync(); await App.DisposeAsync(); Speech.Dispose(); Http.Dispose(); System.IO.Directory.Delete(Directory, true);
        }
    }
}
