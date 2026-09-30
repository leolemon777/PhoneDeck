using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using QRCoder;

namespace PhoneDeck.Desktop;

internal sealed record DictationRequest(int ProtocolVersion, string? RequestId, string? SessionId, string? TargetComputerId, string? Mode, bool Cancel = false);
internal sealed record PairRequest(string? PairingId, string? OneTimeMaterial, string? ClientId, string? ClientLabel);
internal sealed record AdminRequest(string? PairingId, string? ClientId);
internal sealed record ReceiveRequest(string? TargetComputerId, Transcript Result);
internal sealed record InputRequest(int? ProtocolVersion, string? RequestId, string? SessionId, string? TargetComputerId, string? Action, string? Text, string[]? Keys, int? HoldMs, InputStep[]? Steps);
internal sealed record InputStep(string? Type, string? Text, string[]? Keys, int? HoldMs, int? DelayBeforeMs, bool? Submit);

internal static class DesktopApp
{
    internal const string Version = "2.0.0-alpha.1";
    internal static readonly string[] Capabilities = ["fixedAction", "keyChord", "text", "macro", "phoneAudio", "sharedMicrophone", "managedDictation", "secureLan", "transcriptSyncV1", "builtInSpeechV1"];
    internal static WebApplication Create(string[] args, ISpeechEngine? testEngine = null)
    {
        var identity = ReceiverIdentity.LoadOrCreate();
        var trust = new DesktopTrust(identity.ComputerId);
        var credentials = new ClientCredentialsStore(Path.Combine(PhoneDeckDataDirectory.Get(), "clients.json"));
        var localPort = int.TryParse(Environment.GetEnvironmentVariable("PHONEDECK_LOCAL_PORT"), out var lp) ? lp : 8765;
        var lanPort = int.TryParse(Environment.GetEnvironmentVariable("PHONEDECK_LAN_PORT"), out var sp) ? sp : 8766;
        var pairing = new PairingWindowManager(identity.ComputerId, identity.DisplayName, trust.CertificateSha256, lanPort);
        var model = new ModelAssets(Path.Combine(PhoneDeckDataDirectory.Get(), "models"), Path.Combine(AppContext.BaseDirectory, "models"));
        var engine = testEngine ?? new WhisperEngine(model, Path.Combine(AppContext.BaseDirectory, "speech-runtime"));
        var history = new TranscriptStore(identity.ComputerId);
        var input = new PlatformInput();
        var speech = new SpeechSession(engine, history, identity.ComputerId, testEngine is null && !args.Contains("--history-only") ? input.PrepareVoiceInsertion : null);
        var hotkeys = args.Contains("--no-hotkeys") ? null : new DesktopHotkeys(speech);
        var recentInputs = new RequestDeduplicator();
        var clientCancellation = new Dictionary<string, CancellationTokenSource>();
        var clientGate = new object();
        var inputGate = new SemaphoreSlim(1);
        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o => o.SerializerOptions.PropertyNameCaseInsensitive = true);
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false; o.Limits.MaxRequestBodySize = 32 * 1024;
            o.ListenLocalhost(localPort, x => x.Protocols = HttpProtocols.Http1);
            o.ListenAnyIP(lanPort, x => { x.Protocols = HttpProtocols.Http1; x.UseHttps(trust.Certificate); });
        });
        var app = builder.Build();
        app.Lifetime.ApplicationStopped.Register(trust.Dispose);
        app.Lifetime.ApplicationStopped.Register(() => hotkeys?.Dispose());
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            pairing.Cancel();
            speech.Dispose();
            lock (clientGate) foreach (var c in clientCancellation.Values) c.Cancel();
        });
        _ = model.VerifyAsync(app.Lifetime.ApplicationStopping);
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'";
            var local = context.Connection.LocalPort == localPort;
            if (local && !LocalOriginAllowed(context, localPort)) { context.Response.StatusCode = 403; return; }
            if (!local && (context.Request.Path == "/" || context.Request.Path.StartsWithSegments("/local"))) { context.Response.StatusCode = 404; return; }
            if (!local && context.Request.Path != "/api/lan/pair/qr")
            {
                var authorization = context.Request.Headers.Authorization.ToString();
                var record = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? credentials.Authenticate(authorization[7..].Trim()) : null;
                if (record is null || context.Request.Headers["X-PhoneDeck-Client"].ToString() != record.ClientId)
                { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { ok = false, error = "请重新扫码配对" }); return; }
                var scope = context.Request.Path.StartsWithSegments("/api/audio") || context.Request.Path.StartsWithSegments("/api/dictation") ? "audio"
                    : context.Request.Path.StartsWithSegments("/api/transcripts") ? "transcript-sync" : "control";
                if (!record.Scopes.Contains(scope)) { context.Response.StatusCode = 403; return; }
                context.Items["Owner"] = record.ClientId;
                lock (clientGate)
                {
                    if (!clientCancellation.TryGetValue(record.ClientId, out var source)) clientCancellation[record.ClientId] = source = new();
                    context.Items["Revocation"] = source.Token;
                }
            }
            else context.Items["Owner"] = "usb-local";
            try { await next(); }
            catch (ArgumentException e) when (!context.Response.HasStarted) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { ok = false, error = e.Message }); }
            catch (InvalidOperationException e) when (!context.Response.HasStarted) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { ok = false, error = e.Message }); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (IOException) when (!context.Response.HasStarted) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { ok = false, error = "操作未完成，请重试或检查本机权限" }); }
        });
        app.MapGet("/", () =>
        {
            using var resource = typeof(DesktopApp).Assembly.GetManifestResourceStream("PhoneDeck.Desktop.Ui.html")!;
            using var reader = new StreamReader(resource); return Results.Content(reader.ReadToEnd(), "text/html", Encoding.UTF8);
        });
        app.MapGet("/api/health", () => Results.Ok(new
        {
            ok = true, name = "PhoneDeck", version = Version, protocolVersion = 2, computerId = identity.ComputerId, displayName = identity.DisplayName,
            platform = identity.Platform, architecture = identity.Architecture, capabilities = Capabilities,
            input = new { available = input.Available, backend = input.Backend },
            audio = new { available = engine.Ready, streaming = speech.Streaming, sessionId = speech.StreamSession, mode = speech.Mode, device = "PhoneDeck 内置识别" },
            dictation = new { active = speech.Recording, sessionId = speech.RecordingSession },
            typeless = new { capturing = speech.Recording, virtualCableSelected = (bool?)null },
            voiceEngine = new { id = "phonedeck-whisper", displayName = "PhoneDeck 本地语音", experimental = true, capturing = speech.Recording,
                virtualCableSelected = (bool?)null, modes = new[] { new { id = "dictation", label = "听写", configured = engine.Ready, trigger = "toggle", keys = Array.Empty<string>() } } },
            shared = new { requested = false }
        }));
        app.MapGet("/local/status", () => Results.Ok(new { ok = true, identity, model = model.Snapshot, runtimePresent = File.Exists(Path.Combine(AppContext.BaseDirectory, "speech-runtime", OperatingSystem.IsWindows() ? "whisper-cli.exe" : "whisper-cli")),
            speech = speech.Snapshot, audioStreaming = speech.Streaming, hotkeys = hotkeys?.Status ?? "disabled", inputReady = input.Available, inputBackend = input.Backend, addresses = DesktopTrust.Addresses(), history = history.LocalHistory(), clients = credentials.ListRedacted() }));
        app.MapPost("/local/model/download", () => { _ = model.DownloadAsync(app.Lifetime.ApplicationStopping); return Results.Accepted(value: new { ok = true }); });
        app.MapPost("/local/history/clear", () => { history.Clear(); return Results.Ok(new { ok = true }); });
        app.MapPost("/local/quit", () => { _ = Task.Run(async () => { await Task.Delay(200); app.Lifetime.StopApplication(); }); return Results.Ok(new { ok = true }); });
        app.MapPost("/local/dictation/start", () => { speech.StartLocal(false); return Results.Ok(new { ok = true }); });
        app.MapPost("/local/dictation/stop", async () => { await speech.StopLocalAsync(); return Results.Ok(new { ok = true }); });
        app.MapPost("/local/dictation/toggle", async () => { if (speech.Recording) await speech.StopLocalAsync(); else speech.StartLocal(false); return Results.Ok(new { ok = true }); });
        app.MapPost("/local/dictation/cancel", async () => { await speech.StopLocalAsync(true); return Results.Ok(new { ok = true }); });
        app.MapPost("/local/pairing/begin", () =>
        {
            if (pairing.HasPendingConfirmation()) return Results.Conflict(new { ok = false, error = "请先处理待确认的手机" });
            var p = pairing.Begin(); using var qr = QRCodeGenerator.GenerateQrCode(p.QrPayloadJson, QRCodeGenerator.ECCLevel.L);
            using var png = new PngByteQRCode(qr);
            return Results.Ok(new { ok = true, p.PairingId, qr = Convert.ToBase64String(png.GetGraphic(5)), checkCode = p.MaterialCheckCode, addresses = DesktopTrust.Addresses() });
        });
        app.MapGet("/local/pairing/status", () => Results.Ok(pairing.StatusSnapshot()));
        app.MapPost("/local/pairing/cancel", () => { pairing.Cancel(); return Results.Ok(new { ok = true }); });
        app.MapPost("/local/pairing/confirm", (AdminRequest r) => pairing.Confirm(r.PairingId) ? Results.Ok(new { ok = true }) : Results.NotFound(new { ok = false }));
        app.MapPost("/local/pairing/deny", (AdminRequest r) => pairing.Deny(r.PairingId) ? Results.Ok(new { ok = true }) : Results.NotFound(new { ok = false }));
        app.MapPost("/local/clients/revoke", (AdminRequest r) =>
        {
            if (string.IsNullOrEmpty(r.ClientId)) throw new ArgumentException("缺少手机编号");
            credentials.Revoke(r.ClientId);
            lock (clientGate) if (clientCancellation.TryGetValue(r.ClientId, out var source)) source.Cancel();
            speech.Revoke(r.ClientId); return Results.Ok(new { ok = true });
        });
        app.MapPost("/api/lan/pair/qr", async (PairRequest r, HttpContext context) =>
        {
            if (!Guid.TryParse(r.ClientId, out _) || r.ClientLabel?.Length > 64) throw new ArgumentException("手机身份或名称无效");
            var status = pairing.TryBeginSubmit(r.PairingId, r.OneTimeMaterial, r.ClientId!, r.ClientLabel ?? "手机", out var pending);
            if (pending is null) return Results.Json(new { ok = false, error = "配对材料无效、已使用或窗口未开启" }, statusCode: status == PairingSubmitStatus.WindowNotOpen ? 404 : status == PairingSubmitStatus.FailureLimit ? 429 : 401);
            bool allowed;
            try { allowed = await pending.Decision.Task.WaitAsync(TimeSpan.FromSeconds(30), context.RequestAborted); }
            catch (TimeoutException) { pairing.Deny(r.PairingId); return Results.Json(new { ok = false, error = "电脑确认超时" }, statusCode: 408); }
            catch (OperationCanceledException) { pairing.Deny(r.PairingId); throw; }
            if (!allowed) return Results.Json(new { ok = false, error = "电脑已拒绝配对" }, statusCode: 403);
            var record = credentials.Issue(pending.ClientLabel, ["control", "audio", "settings", "transcript-sync"], pending.PairingId, out var token, pending.ClientId);
            lock (clientGate)
            {
                if (clientCancellation.TryGetValue(record.ClientId, out var previous)) previous.Cancel();
                speech.Revoke(record.ClientId); clientCancellation[record.ClientId] = new();
            }
            PrivateFiles.RestrictFile(Path.Combine(PhoneDeckDataDirectory.Get(), "clients.json"));
            return Results.Ok(new { ok = true, clientId = record.ClientId, clientToken = token, scopes = record.Scopes, pairingId = pending.PairingId,
                computerId = identity.ComputerId, displayName = identity.DisplayName, certificateSha256 = trust.CertificateSha256 });
        });
        app.MapPost("/api/dictation/start", (DictationRequest r, HttpContext context) =>
        {
            ValidateEnvelope(r.ProtocolVersion, r.TargetComputerId, identity.ComputerId, r.SessionId, r.RequestId);
            if (r.Mode is not (null or "dictation")) throw new ArgumentException("内置识别仅支持听写");
            var duplicate = speech.Start(Owner(context), r.SessionId!); return Results.Ok(new { ok = true, duplicate, sessionId = r.SessionId });
        });
        app.MapPost("/api/dictation/stop", async (DictationRequest r, HttpContext context) =>
        {
            ValidateEnvelope(r.ProtocolVersion, r.TargetComputerId, identity.ComputerId, r.SessionId, r.RequestId);
            await speech.StopAsync(Owner(context), r.SessionId!, cancel: r.Cancel);
            return Results.Ok(new { ok = true, sessionId = r.SessionId, state = "stopped" });
        });
        app.MapPost("/api/audio/stream", async (HttpContext context) =>
        {
            var session = context.Request.Headers["X-PhoneDeck-Session"].ToString();
            if (context.Request.Headers["X-PhoneDeck-Protocol"] != "2" || context.Request.Headers["X-PhoneDeck-Computer-Id"] != identity.ComputerId || !Guid.TryParse(session, out _)) throw new ArgumentException("音频目标或协议无效");
            var mode = context.Request.Headers["X-PhoneDeck-Audio-Mode"].FirstOrDefault() ?? "managed";
            var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>(); if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = null;
            speech.Attach(Owner(context), session, mode);
            var orderly = false; long received = 0;
            using var revoked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, context.Items["Revocation"] is CancellationToken token ? token : CancellationToken.None);
            try
            {
                var buffer = new byte[8192]; int count;
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(revoked.Token); idle.CancelAfter(TimeSpan.FromSeconds(5));
                    count = await context.Request.Body.ReadAsync(buffer, idle.Token);
                    if (count == 0) break;
                    received += count; speech.Feed(session, buffer.AsSpan(0, count));
                }
                if (received % 2 != 0) throw new ArgumentException("音频 PCM 帧不完整");
                orderly = true;
            }
            catch (OperationCanceledException) { return Results.Json(new { ok = false, error = "音频超时、中断或授权撤销" }, statusCode: 408); }
            finally { speech.EndStream(session, orderly); }
            return Results.Ok(new { ok = true, receivedBytes = received });
        });
        app.MapGet("/api/transcripts", (HttpContext context, long? after, string? targetComputerId) =>
        {
            if (targetComputerId != identity.ComputerId || after is < 0) throw new ArgumentException("同步目标或游标无效");
            return Results.Ok(history.ForPhone(Owner(context), after ?? 0));
        });
        app.MapPost("/api/transcripts/receive", (ReceiveRequest r, HttpContext context) =>
        {
            if (r.Result is null || r.TargetComputerId != identity.ComputerId || r.Result.SourceComputerId == identity.ComputerId) throw new ArgumentException("同步目标或来源无效");
            var result = history.Add(Owner(context), r.Result, true); return Results.Ok(new { ok = true, resultId = result.ResultId });
        });
        app.MapPost("/api/input", async (InputRequest r, HttpContext context) =>
        {
            if (r.ProtocolVersion == 2) ValidateEnvelope(2, r.TargetComputerId, identity.ComputerId, r.SessionId, r.RequestId);
            else if (r.ProtocolVersion is not null && r.ProtocolVersion != 1) throw new ArgumentException("输入协议不支持");
            if (string.IsNullOrEmpty(r.RequestId) || r.RequestId.Length > 128) throw new ArgumentException("requestId 无效");
            using var canceled = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, context.Items["Revocation"] is CancellationToken token ? token : CancellationToken.None);
            await inputGate.WaitAsync(canceled.Token);
            try
            {
                var duplicate = recentInputs.Duplicate(Owner(context), r.RequestId, JsonSerializer.Serialize(r));
                if (!duplicate)
                {
                    if (r.Action == "macro")
                    {
                        if (r.Steps is null || r.Steps.Length is < 1 or > 8) throw new ArgumentException("宏步骤数量无效");
                        foreach (var step in r.Steps)
                        {
                            if (step is null) throw new ArgumentException("宏步骤无效");
                            if ((step.DelayBeforeMs ?? 0) is < 0 or > 2000 || (step.HoldMs ?? 45) is < 20 or > 500 || step.Type is not ("text" or "keyChord") || step.Text?.Length > 4096) throw new ArgumentException("宏步骤无效");
                            PlatformInput.ValidateAction(step.Type!, step.Text, step.Keys, step.HoldMs);
                        }
                        recentInputs.Begin(Owner(context), r.RequestId, JsonSerializer.Serialize(r));
                        foreach (var step in r.Steps)
                        {
                            canceled.Token.ThrowIfCancellationRequested(); await Task.Delay(step.DelayBeforeMs ?? 0, canceled.Token);
                            input.Execute(step.Type!, step.Text, step.Keys, step.HoldMs);
                            if (step.Type == "text" && step.Submit == true) input.Execute("enter", null, null, null);
                        }
                    }
                    else
                    {
                        PlatformInput.ValidateAction(r.Action ?? "", r.Text, r.Keys, r.HoldMs);
                        recentInputs.Begin(Owner(context), r.RequestId, JsonSerializer.Serialize(r));
                        canceled.Token.ThrowIfCancellationRequested(); input.Execute(r.Action ?? "", r.Text, r.Keys, r.HoldMs);
                    }
                    recentInputs.Confirm(Owner(context), r.RequestId, JsonSerializer.Serialize(r));
                }
                return Results.Ok(new { ok = true, requestId = r.RequestId, computerId = identity.ComputerId, duplicate });
            }
            finally { inputGate.Release(); }
        });
        if (!args.Contains("--no-discovery"))
        {
            var udp = new LanDiscoveryResponder(identity, lanPort, Capabilities); udp.Start();
            var mdns = new MdnsAdvertiser(); mdns.Start(identity, lanPort, Capabilities);
            app.Lifetime.ApplicationStopped.Register(() => { udp.Dispose(); mdns.Dispose(); });
        }
        return app;
    }
    private static string Owner(HttpContext context) => (string)context.Items["Owner"]!;
    internal static void ValidateEnvelope(int protocol, string? target, string actual, string? session, string? request)
    {
        if (protocol != 2 || target != actual || !Guid.TryParse(session, out _) || string.IsNullOrWhiteSpace(request) || request.Length > 128) throw new ArgumentException("请求协议、目标或会话无效");
    }
    private static bool LocalOriginAllowed(HttpContext context, int port)
    {
        if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress!)) return false;
        var host = context.Request.Host;
        if (host.Port != port || host.Host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1")) return false;
        foreach (var raw in new[] { context.Request.Headers.Origin.FirstOrDefault(), context.Request.Headers.Referer.FirstOrDefault() })
            if (raw is not null && (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Port != port || !string.Equals(uri.Host.Trim('[', ']'), host.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))) return false;
        return context.Request.Headers["Sec-Fetch-Site"].ToString() != "cross-site";
    }
}

internal sealed class RequestDeduplicator
{
    private readonly Dictionary<string, (long At, string Hash, bool Complete)> recent = new();
    internal bool Duplicate(string owner, string id, string payload)
    {
        foreach (var key in recent.Where(x => Environment.TickCount64 - x.Value.At > 30000).Select(x => x.Key).ToArray()) recent.Remove(key);
        if (!recent.TryGetValue(owner + ":" + id, out var record)) return false;
        if (record.Hash != Hash(payload)) throw new ArgumentException("相同请求编号对应不同内容");
        if (!record.Complete) throw new InvalidOperationException("该请求已尝试但未确认完成，请检查电脑结果；不会自动重复执行");
        return true;
    }
    internal void Begin(string owner, string id, string payload) => Save(owner, id, payload, false);
    internal void Confirm(string owner, string id, string payload)
        => Save(owner, id, payload, true);
    private void Save(string owner, string id, string payload, bool complete)
    {
        if (recent.Count >= 4096) recent.Remove(recent.MinBy(x => x.Value.At).Key);
        recent[owner + ":" + id] = (Environment.TickCount64, Hash(payload), complete);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
