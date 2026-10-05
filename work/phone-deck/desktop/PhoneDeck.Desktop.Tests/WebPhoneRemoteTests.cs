using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PhoneDeck.Desktop;

namespace PhoneDeck.Desktop.Tests;

[TestClass]
public sealed class WebPhoneRemoteTests
{
    [TestMethod]
    public async Task PinnedHttpsStreamingUsesAuthenticatedIdentityAndDesktopStopReceipt()
    {
        await using var receiver = await Receiver.Create(); using var target = receiver.CreateTarget();
        var session = Guid.NewGuid().ToString();
        await target.StartAsync(session, "managed", CancellationToken.None);
        Assert.IsTrue(receiver.Speech.Streaming); Assert.IsTrue(receiver.Speech.Recording);
        Assert.IsTrue(target.Feed(session, new byte[1920])); await Wait(() => receiver.Bytes >= 3840);
        await receiver.Speech.StopLocalAsync();
        var health = await target.HealthAsync(CancellationToken.None); Assert.AreEqual(session, health.StopRequestedSessionId);
        await target.StopAsync(session, false, CancellationToken.None); Assert.IsFalse(receiver.Speech.Streaming);
        Assert.IsTrue(receiver.AuthenticatedRequests >= 4);
    }
    [TestMethod]
    public async Task ThreeRealTlsStreamsAllowIndependentLocalSegmentsAndIsolateOneDisconnectedReceiver()
    {
        await using var first = await Receiver.Create(); await using var second = await Receiver.Create(); await using var third = await Receiver.Create();
        using var a = first.CreateTarget(); using var b = second.CreateTarget(); using var c = third.CreateTarget();
        var targets = new[] { a, b, c }; var receivers = new[] { first, second, third }; var session = Guid.NewGuid().ToString();
        await Task.WhenAll(targets.Select(target => target.StartAsync(session, "shared", CancellationToken.None)));
        foreach (var receiver in receivers)
        {
            receiver.Speech.StartLocal(false);
            foreach (var target in targets) Assert.IsTrue(target.Feed(session, new byte[1920]));
            await Wait(() => receiver.Bytes >= 3840); await receiver.Speech.StopLocalAsync(); await receiver.Speech.WaitForResultAsync();
            Assert.IsTrue(receiver.Speech.Streaming); Assert.AreEqual(session, receiver.Speech.StreamSession);
        }
        second.StreamContext!.Abort(); await Wait(() => !second.Speech.Streaming);
        var beforeA = first.Bytes; var beforeC = third.Bytes;
        for (var i = 0; i < 3; i++) { Assert.IsTrue(a.Feed(session, new byte[1920])); _ = b.Feed(session, new byte[1920]); Assert.IsTrue(c.Feed(session, new byte[1920])); }
        await Wait(() => first.Bytes >= beforeA + 5760 && third.Bytes >= beforeC + 5760);
        first.Speech.StartLocal(false); third.Speech.StartLocal(false);
        await Task.WhenAll(new[] { a, c }.Select(target => target.StopAsync(session, false, CancellationToken.None)));
        try { await b.StopAsync(session, true, CancellationToken.None); } catch (HttpRequestException) { }
        Assert.IsFalse(first.Speech.Streaming); Assert.IsFalse(third.Speech.Streaming);
        Assert.IsFalse(first.Speech.Recording); Assert.IsFalse(third.Speech.Recording);
    }
    [TestMethod]
    public async Task MalformedPeerHealthAndPairingShapesFailSafely()
    {
        var id = Guid.NewGuid().ToString();
        var valid = "\"capabilities\":[\"builtInSpeechV1\",\"phoneStopV1\",\"audioStopV1\"]";
        foreach (var body in new[]
        {
            "[]", "{\"computerId\":\"" + id + "\"," + valid + ",\"dictation\":{\"active\":false}}",
            "{\"computerId\":\"" + id + "\",\"audio\":{},\"dictation\":{},\"capabilities\":null}",
            "{\"computerId\":\"" + id + "\",\"audio\":{},\"dictation\":{},\"capabilities\":[7]}",
            "{\"computerId\":\"" + id + "\",\"audio\":{\"available\":true,\"streaming\":\"false\"},\"dictation\":{\"active\":false}," + valid + "}"
        })
        {
            using var http = new HttpClient(new JsonHandler(body)) { BaseAddress = new Uri("https://192.168.0.2:8766") };
            using var target = new WebPhoneRemoteTarget(new(Guid.NewGuid().ToString(), id, "malformed", "192.168.0.2", 8766, new string('a',64), Guid.NewGuid().ToString(), "test-token", ["audio","control"]), http);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => target.HealthAsync(CancellationToken.None));
        }
        foreach (var body in new[] { "{}", "[]", "{\"scopes\":null}", "{\"scopes\":[7]}" })
        {
            using var document = JsonDocument.Parse(body);
            Assert.ThrowsExactly<InvalidDataException>(() => WebPhoneRemoteTarget.StringArray(document.RootElement, "scopes"));
        }
        foreach (var payload in new[] { "[]", "null", "{", "{\"version\":\"1\"}" })
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => WebPhoneRemoteTarget.PairAsync(Guid.NewGuid().ToString(), "test", payload, "192.168.0.2", CancellationToken.None));
    }
    [TestMethod]
    public async Task ExternalEngineStopsOnlyForFreshMatchingPostStartObservation()
    {
        await using var receiver = await Receiver.Create(external: true); using var target = receiver.CreateTarget();
        var session = Guid.NewGuid().ToString();
        await target.StartAsync(session, "managed", CancellationToken.None);
        Assert.AreEqual("dictation", receiver.RequestedMode);
        Assert.AreEqual("pcm-s16le", receiver.AudioFormat);
        receiver.Capturing = false; receiver.ProbeAge = 5000;
        Assert.IsNull((await target.HealthAsync(CancellationToken.None)).StopRequestedSessionId, "Pre-start cached false must not stop new speech");
        receiver.ProbeAge = 0; receiver.Stale = true;
        Assert.IsNull((await target.HealthAsync(CancellationToken.None)).StopRequestedSessionId);
        receiver.Stale = false; receiver.Capturing = null;
        Assert.IsNull((await target.HealthAsync(CancellationToken.None)).StopRequestedSessionId);
        receiver.Capturing = false; receiver.DictationOverride = Guid.NewGuid().ToString();
        Assert.IsNull((await target.HealthAsync(CancellationToken.None)).StopRequestedSessionId, "Another engine session cannot stop ours");
        receiver.DictationOverride = null;
        Assert.AreEqual(session, (await target.HealthAsync(CancellationToken.None)).StopRequestedSessionId);
        await target.StopAsync(session, false, CancellationToken.None);
    }
    [TestMethod]
    public async Task ExternalStopUsesModeConfirmedForThisSession()
    {
        await using var receiver = await Receiver.Create(external: true); using var target = receiver.CreateTarget();
        var session = Guid.NewGuid().ToString(); await target.StartAsync(session, "managed", CancellationToken.None);
        receiver.ConfiguredMode = "translation"; await target.HealthAsync(CancellationToken.None);
        await target.StopAsync(session, false, CancellationToken.None);
        Assert.AreEqual("dictation", receiver.StoppedMode);
    }
    [TestMethod]
    public async Task RejectedAudioTailStillStopsExternalEngineAndReportsFailure()
    {
        await using var receiver = await Receiver.Create(external: true);
        using var target = receiver.CreateTarget();
        var session = Guid.NewGuid().ToString();
        await target.StartAsync(session, "managed", CancellationToken.None);
        receiver.RejectTail = true;
        var error = await Assert.ThrowsExactlyAsync<IOException>(() => target.StopAsync(session, false, CancellationToken.None));
        StringAssert.Contains(error.Message, "尾音");
        Assert.AreEqual(1, receiver.StopCalls);
        Assert.IsFalse(receiver.Capturing);
        receiver.RejectTail = false;
        await target.StartAsync(Guid.NewGuid().ToString(), "managed", CancellationToken.None);
    }
    [TestMethod]
    public async Task RepeatedStopJoinsDrainAndNewStartCannotReplaceItsOwner()
    {
        await using var receiver = await Receiver.Create(external: true);
        using var target = receiver.CreateTarget();
        var session = Guid.NewGuid().ToString();
        await target.StartAsync(session, "managed", CancellationToken.None);
        receiver.BlockTail = true;
        var stopping = target.StopAsync(session, false, CancellationToken.None);
        try
        {
            await receiver.TailEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var repeated = target.StopAsync(session, false, CancellationToken.None);
            Assert.IsFalse(repeated.IsCompleted);
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                target.StartAsync(Guid.NewGuid().ToString(), "managed", CancellationToken.None));
            StringAssert.Contains(error.Message, "上一段供音尚未结束");
            receiver.ReleaseTail.TrySetResult();
            await Task.WhenAll(stopping, repeated);
            Assert.AreEqual(1, receiver.StopCalls);
        }
        finally { receiver.ReleaseTail.TrySetResult(); await stopping; }
    }
    [TestMethod]
    public async Task ExternalSharedEngineStopsIndependentlyAndClosingSupplyDoesNotToggleIt()
    {
        await using var first = await Receiver.Create(external: true); await using var second = await Receiver.Create(external: true);
        using var a = first.CreateTarget(); using var b = second.CreateTarget(); var session = Guid.NewGuid().ToString();
        await Task.WhenAll(a.StartAsync(session, "shared", CancellationToken.None), b.StartAsync(session, "shared", CancellationToken.None));
        first.Capturing = true; second.Capturing = true;
        Assert.IsTrue((await a.HealthAsync(CancellationToken.None)).Recording);
        first.Capturing = false;
        var h = await a.HealthAsync(CancellationToken.None);
        Assert.IsTrue(h.Streaming); Assert.IsFalse(h.Recording); Assert.IsNull(h.StopRequestedSessionId);
        Assert.IsTrue((await b.HealthAsync(CancellationToken.None)).Recording);
        await a.StopAsync(session, false, CancellationToken.None);
        Assert.IsTrue((await b.HealthAsync(CancellationToken.None)).Streaming);
        await b.StopAsync(session, true, CancellationToken.None);
        Assert.AreEqual(0, first.StartCalls + first.StopCalls + first.AudioStopCalls);
        Assert.AreEqual(0, second.StartCalls + second.StopCalls + second.AudioStopCalls);
    }
    [TestMethod]
    public async Task ExternalOldStopCannotEndReplacementAndDrainedDesktopStopStillReachesPhone()
    {
        await using var receiver = await Receiver.Create(external: true); using var target = receiver.CreateTarget();
        var previous = Guid.NewGuid().ToString(); await target.StartAsync(previous, "managed", CancellationToken.None);
        await target.StopAsync(previous, false, CancellationToken.None);
        var session = Guid.NewGuid().ToString(); await target.StartAsync(session, "managed", CancellationToken.None);
        await target.StopAsync(previous, true, CancellationToken.None);
        Assert.AreEqual(session, (await target.HealthAsync(CancellationToken.None)).SessionId);
        receiver.Capturing = false; receiver.StreamContext!.Abort();
        await Wait(() => !receiver.Speech.Streaming);
        Assert.AreEqual(session, (await target.HealthAsync(CancellationToken.None)).StopRequestedSessionId);
        await target.StopAsync(session, true, CancellationToken.None);
    }
    [TestMethod]
    public async Task InputRetriesRemainStableWithoutCollidingAcrossApprovedPhones()
    {
        using var handler = new InputHandler();
        WebPhoneRemoteTarget Target(string owner) => new(new(owner, Guid.NewGuid().ToString(), "input", "192.168.0.2", 8766,
            new string('a', 64), Guid.NewGuid().ToString(), "test-token", ["control"]),
            new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("https://192.168.0.2:8766/") });
        using var first = Target(Guid.NewGuid().ToString()); using var second = Target(Guid.NewGuid().ToString());
        await first.InputAsync("same-request", "backspace", CancellationToken.None);
        await first.InputAsync("same-request", "backspace", CancellationToken.None);
        await second.InputAsync("same-request", "backspace", CancellationToken.None);
        Assert.AreEqual(handler.Ids[0], handler.Ids[1]); Assert.AreNotEqual(handler.Ids[0], handler.Ids[2]);
        Assert.IsTrue(handler.Ids.All(id => id.Length <= 128));
    }
    private sealed class InputHandler : HttpMessageHandler
    {
        internal List<string> Ids = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Ids.Add(json.RootElement.GetProperty("requestId").GetString()!);
            return new(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }
    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
    private static async Task Wait(Func<bool> condition)
    {
        for (var i = 0; i < 150; i++) { if (condition()) return; await Task.Delay(20); }
        Assert.IsTrue(condition());
    }
    private sealed class Receiver : IAsyncDisposable
    {
        internal required WebApplication App; internal required SpeechSession Speech; internal required string Id, Token, ClientId, Base;
        internal required X509Certificate2 Certificate; internal int Bytes, AuthenticatedRequests; internal HttpContext? StreamContext;
        internal string ConfiguredMode = "dictation"; internal string? StoppedMode;
        internal bool? Capturing; internal long ProbeAge; internal bool Stale; internal string? DictationOverride, RequestedMode, AudioFormat;
        internal int StartCalls, StopCalls, AudioStopCalls;
        internal bool RejectTail, BlockTail;
        internal readonly TaskCompletionSource TailEntered = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseTail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal static async Task<Receiver> Create(bool external = false)
        {
            var id = Guid.NewGuid().ToString(); var owner = Guid.NewGuid().ToString(); var token = ClientCredentialsStore.NewToken();
            var speech = new SpeechSession(new FakeEngine(), new TranscriptStore(id), id);
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            using var rsa = RSA.Create(2048); var request = new CertificateRequest("CN=WebPhoneTest", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build());
            using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            var cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
            var builder = WebApplication.CreateBuilder(Array.Empty<string>()); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listen => { listen.Protocols = HttpProtocols.Http1; listen.UseHttps(cert); }));
            var app = builder.Build(); var receiver = new Receiver { App = app, Speech = speech, Id = id, Token = token, ClientId = owner, Base = $"https://127.0.0.1:{port}/", Certificate = cert };
            app.Use(async (context, next) =>
            {
                if (context.Request.Headers.Authorization != "Bearer " + token || context.Request.Headers["X-PhoneDeck-Client"] != owner) { context.Response.StatusCode = 401; return; }
                Interlocked.Increment(ref receiver.AuthenticatedRequests); await next();
            });
            app.MapGet("/api/health", () =>
            {
                var h = speech.HealthForPhone(owner);
                if (external) return Results.Ok(new { computerId = id,
                    capabilities = new[] { "phoneAudio", "managedDictation", "sharedMicrophone" },
                    audio = new { available = true, streaming = h.Streaming, sessionId = h.StreamSession },
                    dictation = new { active = h.Recording, sessionId = receiver.DictationOverride ?? h.RecordingSession },
                    voiceEngine = new { displayName = "Typeless fixture", capturing = receiver.Capturing, ageMs = receiver.ProbeAge, stale = receiver.Stale,
                        modes = new[] { new { id = receiver.ConfiguredMode, configured = true } } } });
                return Results.Ok(new { computerId = id, capabilities = new[] { "builtInSpeechV1", "phoneStopV1", "audioStopV1" }, audio = new { available = true, streaming = h.Streaming, sessionId = h.StreamSession, stopRequestedSessionId = h.StopRequestedSessionId }, dictation = new { active = h.Recording, sessionId = h.RecordingSession } });
            });
            app.MapPost("/api/dictation/start", (DictationRequest command) => { DesktopApp.ValidateEnvelope(command.ProtocolVersion, command.TargetComputerId, id, command.SessionId, command.RequestId); speech.Start(owner, command.SessionId!); receiver.StartCalls++; receiver.RequestedMode = command.Mode; receiver.Capturing = true; return Results.Ok(); });
            app.MapPost("/api/dictation/stop", async (DictationRequest command) => { DesktopApp.ValidateEnvelope(command.ProtocolVersion, command.TargetComputerId, id, command.SessionId, command.RequestId); await speech.StopAsync(owner, command.SessionId!, cancel: command.Cancel); receiver.StopCalls++; receiver.StoppedMode = command.Mode; receiver.Capturing = false; return Results.Ok(); });
            app.MapPost("/api/audio/stop", async (DictationRequest command) => { DesktopApp.ValidateEnvelope(command.ProtocolVersion, command.TargetComputerId, id, command.SessionId, command.RequestId); await speech.StopSupplyAsync(owner, command.SessionId!, command.Cancel); receiver.AudioStopCalls++; return Results.Ok(); });
            app.MapPost("/api/audio/stream", async (HttpContext context) =>
            {
                receiver.AudioFormat = context.Request.Headers["X-PhoneDeck-Audio"].ToString();
                var session = context.Request.Headers["X-PhoneDeck-Session"].ToString();
                if (context.Request.Headers["X-PhoneDeck-Computer-Id"] != id || context.Request.Headers["X-PhoneDeck-Protocol"] != "2") return Results.BadRequest();
                speech.Attach(owner, session, context.Request.Headers["X-PhoneDeck-Audio-Mode"].ToString()); receiver.StreamContext = context; var orderly = false;
                try
                {
                    var buffer = new byte[8192]; int count;
                    while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                    { Interlocked.Add(ref receiver.Bytes, count); speech.Feed(session, buffer.AsSpan(0, count)); }
                    receiver.TailEntered.TrySetResult();
                    if (receiver.BlockTail) await receiver.ReleaseTail.Task.WaitAsync(context.RequestAborted);
                    orderly = true;
                    return receiver.RejectTail ? Results.StatusCode(500) : Results.Ok();
                }
                catch (OperationCanceledException) { return Results.StatusCode(408); }
                catch (IOException) { return Results.StatusCode(408); }
                finally { speech.EndStream(session, orderly); }
            });
            await app.StartAsync(); return receiver;
        }
        internal WebPhoneRemoteTarget CreateTarget()
        {
            var pin = Certificate.GetCertHash(HashAlgorithmName.SHA256);
            var handler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && CryptographicOperations.FixedTimeEquals(pin, cert.GetCertHash(HashAlgorithmName.SHA256)) };
            var client = new HttpClient(handler) { BaseAddress = new Uri(Base), Timeout = Timeout.InfiniteTimeSpan };
            return new WebPhoneRemoteTarget(new(Guid.NewGuid().ToString(), Id, "TLS fixture", "192.168.0.1", new Uri(Base).Port, Convert.ToHexString(pin), ClientId, Token, ["control", "audio"]), client);
        }
        public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); Speech.Dispose(); Certificate.Dispose(); }
    }
}
