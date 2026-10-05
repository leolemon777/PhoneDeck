using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using QRCoder;

namespace PhoneDeck.Desktop;

internal sealed record WebPhonePeerRequest(string? QrPayload, string? Host);
internal sealed record WebPhoneRemoveRequest(string? TargetId);
internal sealed record WebPhoneCommand(string? Type, string? RequestId, string? SessionId, string? Mode,
    string[]? TargetIds, string? TargetId, string? Action, bool Cancel = false);

// The browser surface is a separate, same-origin capability: its cookie never authenticates
// /api or /local. DesktopApp additionally isolates this surface on its browser TLS listener.
internal sealed class WebPhoneGateway : IDisposable
{
    internal const string CookieName = "__Secure-YanduPhone";
    // 8s audio response + 6s engine stop, with transport margin. The browser
    // allows 16s for its ACK and releases its microphone before waiting.
    private static readonly TimeSpan TargetStopTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private WebPhoneIdentity identity;
    private readonly Func<string, IWebPhoneTarget> localTarget;
    private readonly Action<string>? revokeLocal;
    private readonly Func<HttpContext, bool> originAllowed;
    private readonly Func<string, IWebPhoneTarget[]>? testTargets;
    private readonly Func<string[]>? browserAddresses;
    private readonly ClientCredentialsStore credentials;
    private readonly WebPhonePeerStore peers;
    private PairingWindowManager pairing;
    private readonly string certificateSha256;
    private readonly int webPort;
    private readonly string privateDirectory;
    private readonly ConcurrentDictionary<string, BrowserPhone> phones = new();
    private readonly SemaphoreSlim inputGate = new(1);
    private readonly RequestDeduplicator inputs = new();
    private readonly CancellationTokenSource lifetime = new();

    internal WebPhoneGateway(WebPhoneIdentity identity, Func<string, IWebPhoneTarget> localTarget, string dataDirectory,
        int webPort, string certificateSha256, Func<HttpContext, bool> originAllowed,
        Func<string, IWebPhoneTarget[]>? testTargets = null, Func<string[]>? browserAddresses = null, Action<string>? revokeLocal = null)
    {
        this.identity = identity; this.certificateSha256 = certificateSha256; this.localTarget = localTarget; this.revokeLocal = revokeLocal; this.webPort = webPort;
        this.originAllowed = originAllowed; this.testTargets = testTargets; this.browserAddresses = browserAddresses;
        privateDirectory = Path.Combine(dataDirectory, "web-phone"); Directory.CreateDirectory(privateDirectory); PrivateFiles.RestrictDirectory(privateDirectory);
        credentials = new ClientCredentialsStore(Path.Combine(privateDirectory, "clients.json"));
        peers = new WebPhonePeerStore(Path.Combine(privateDirectory, "peers.json"));
        pairing = new(identity.ComputerId, identity.DisplayName, certificateSha256, webPort);
    }
    internal void UpdateDisplayName(string name)
    {
        identity = identity with { DisplayName = name };
        pairing.Cancel(); pairing = new(identity.ComputerId, name, certificateSha256, webPort);
        foreach (var phone in phones.Values)
            lock (phone.Gate) if (phone.Targets.TryGetValue(identity.ComputerId, out var local)) local.UpdateName(name);
    }
    internal void Map(WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(15) });
        app.MapPost("/local/web/begin", () =>
        {
            if (pairing.HasPendingConfirmation()) return Results.Conflict(new { ok = false, error = "请先确认或拒绝待连接的手机" });
            var addresses = browserAddresses?.Invoke() ?? WebPhoneNetwork.Addresses();
            if (browserAddresses is not null && addresses.Length == 0)
            {
                pairing.Cancel();
                return Results.Conflict(new { ok = false, error = "网络地址已变化或未连接局域网，请重启接收端后重新打开手机入口" });
            }
            // The shared pairing manager can return its consumed window; a user-requested
            // browser link must always contain fresh one-time material after an earlier approval.
            pairing.Cancel(); var p = pairing.Begin();
            var fragment = Uri.EscapeDataString(JsonSerializer.Serialize(new { pairingId = p.PairingId, oneTimeMaterial = p.Material, checkCode = p.MaterialCheckCode }, Json));
            var urls = addresses.Select(address => new UriBuilder("https", address, webPort) { Path = "/phone/", Fragment = "pair=" + fragment }.Uri.AbsoluteUri).ToArray();
            var primary = urls.FirstOrDefault() ?? $"https://localhost:{webPort}/phone/#pair={fragment}";
            using var qr = QRCodeGenerator.GenerateQrCode(primary, QRCodeGenerator.ECCLevel.L); using var png = new PngByteQRCode(qr);
            return Results.Ok(new { ok = true, pairingId = p.PairingId, checkCode = p.MaterialCheckCode, urls, qr = Convert.ToBase64String(png.GetGraphic(5)), expiresSeconds = PairingWindowManager.ValidSeconds });
        });
        app.MapGet("/local/web/status", () => Results.Ok(new { ok = true, pairing = pairing.StatusSnapshot(), clients = credentials.ListRedacted() }));
        app.MapPost("/local/web/confirm", (AdminRequest r) => pairing.Confirm(r.PairingId) ? Results.Ok(new { ok = true }) : Results.NotFound());
        app.MapPost("/local/web/deny", (AdminRequest r) => pairing.Deny(r.PairingId) ? Results.Ok(new { ok = true }) : Results.NotFound());
        app.MapPost("/local/web/cancel", () => { pairing.Cancel(); return Results.Ok(new { ok = true }); });
        app.MapPost("/local/web/revoke", (AdminRequest r) =>
        {
            if (!Guid.TryParse(r.ClientId, out _)) throw new ArgumentException("手机编号无效");
            credentials.Revoke(r.ClientId!);
            if (phones.TryGetValue(r.ClientId!, out var phone)) phone.Revoked.Cancel();
            revokeLocal?.Invoke("web:" + r.ClientId);
            peers.Remove(r.ClientId!);
            return Results.Ok(new { ok = true });
        });
        app.MapPost("/phone/pair", PairAsync);
        app.MapGet("/phone/api/state", async (HttpContext context) =>
        {
            var phone = Authenticate(context, "control"); if (phone is null) return Results.Unauthorized();
            await RefreshAsync(phone, context.RequestAborted); return Results.Ok(Snapshot(phone));
        });
        app.MapPost("/phone/api/peers", async (WebPhonePeerRequest r, HttpContext context) =>
        {
            if (!originAllowed(context)) return Results.StatusCode(403);
            var phone = Authenticate(context, "control"); if (phone is null) return Results.Unauthorized();
            if (r.QrPayload is null || r.Host is null) throw new ArgumentException("请填写电脑地址并粘贴配对资料");
            lock (phone.Gate) if (phone.Session is not null || phone.Pairing) throw new InvalidOperationException("请先结束语音或等待当前配对完成"); else phone.Pairing = true;
            try
            {
                using var canceled = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, phone.Revoked.Token);
                var record = await WebPhoneRemoteTarget.PairAsync(phone.Id, phone.Label, r.QrPayload, r.Host, canceled.Token);
                if (record.ComputerId == identity.ComputerId) throw new ArgumentException("这台主电脑已经连接");
                canceled.Token.ThrowIfCancellationRequested(); peers.Save(record);
                lock (phone.Gate)
                {
                    if (phone.Targets.Remove(record.ComputerId, out var previous)) previous.Dispose();
                    phone.Targets[record.ComputerId] = new WebPhoneRemoteTarget(record);
                }
                await RefreshAsync(phone, canceled.Token); return Results.Ok(Snapshot(phone));
            }
            finally { lock (phone.Gate) phone.Pairing = false; }
        });
        app.MapPost("/phone/api/peers/remove", (WebPhoneRemoveRequest r, HttpContext context) =>
        {
            if (!originAllowed(context)) return Results.StatusCode(403);
            var phone = Authenticate(context, "control"); if (phone is null) return Results.Unauthorized();
            if (r.TargetId == identity.ComputerId || !Guid.TryParse(r.TargetId, out _)) throw new ArgumentException("电脑编号无效");
            lock (phone.Gate)
            {
                if (phone.Session is not null || phone.Pairing) throw new InvalidOperationException("请先结束语音");
                peers.Remove(phone.Id, r.TargetId);
                if (phone.Targets.Remove(r.TargetId!, out var removed)) removed.Dispose();
                phone.States.Remove(r.TargetId!);
                if (phone.Selected == r.TargetId) phone.Selected = identity.ComputerId;
            }
            return Results.Ok(Snapshot(phone));
        });
        app.MapGet("/phone/socket", SocketAsync);
    }
    private async Task<IResult> PairAsync(PairRequest request, HttpContext context)
    {
        if (!originAllowed(context)) return Results.StatusCode(403);
        if (!Guid.TryParse(request.ClientId, out _) || request.ClientLabel?.Length > 40) throw new ArgumentException("手机身份或名称无效");
        var status = pairing.TryBeginSubmit(request.PairingId, request.OneTimeMaterial, request.ClientId!, request.ClientLabel ?? "网页手机", out var pending);
        if (pending is null) return Results.Json(new { ok = false, error = "连接链接已失效，请在电脑重新开启" }, statusCode: status == PairingSubmitStatus.WindowNotOpen ? 404 : status == PairingSubmitStatus.FailureLimit ? 429 : 401);
        bool accepted;
        try { accepted = await pending.Decision.Task.WaitAsync(TimeSpan.FromSeconds(30), context.RequestAborted); }
        catch (TimeoutException) { pairing.Deny(request.PairingId); return Results.Json(new { ok = false, error = "等待电脑确认超时" }, statusCode: 408); }
        catch (OperationCanceledException) { pairing.Deny(request.PairingId); throw; }
        if (!accepted) return Results.Json(new { ok = false, error = "电脑已拒绝连接" }, statusCode: 403);
        // Re-pairing is an explicit, locally approved identity rotation. End the old
        // browser connection before issuing a new cookie, including a previously revoked identity.
        if (credentials.Find(pending.ClientId) is not null)
        {
            credentials.Revoke(pending.ClientId);
            if (phones.TryRemove(pending.ClientId, out var previous))
            {
                previous.Revoked.Cancel();
                AudioSession? active; lock (previous.Gate) active = previous.Session;
                if (active is not null) await EndAsync(previous, active, true);
                foreach (var target in previous.Targets.Values) target.Dispose();
            }
            peers.Remove(pending.ClientId);
        }
        var record = credentials.Issue(pending.ClientLabel, ["audio", "control"], pending.PairingId, out var token, pending.ClientId);
        PrivateFiles.RestrictFile(Path.Combine(privateDirectory, "clients.json"));
        context.Response.Cookies.Append(CookieName, token, new CookieOptions { Path = "/phone", Secure = true, HttpOnly = true,
            SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(180), IsEssential = true });
        return Results.Ok(new { ok = true, clientId = record.ClientId, computerId = identity.ComputerId });
    }
    private BrowserPhone? Authenticate(HttpContext context, string scope)
    {
        var token = context.Request.Cookies[CookieName];
        var record = token?.Length <= 256 ? credentials.Authenticate(token) : null;
        if (record is null || !record.Scopes.Contains(scope)) return null;
        return phones.GetOrAdd(record.ClientId, _ =>
        {
            var phone = new BrowserPhone(record.ClientId, record.Label, identity.ComputerId);
            foreach (var target in testTargets?.Invoke(record.ClientId) ??
                new IWebPhoneTarget[] { localTarget(record.ClientId) }
                    .Concat(peers.ForOwner(record.ClientId).Select(x => (IWebPhoneTarget)new WebPhoneRemoteTarget(x))))
                phone.Targets[target.Id] = target;
            return phone;
        });
    }
    private bool Authorized(BrowserPhone phone, string scope) => credentials.Find(phone.Id) is { RevokedAtUtc: null } record && record.Scopes.Contains(scope) && !phone.Revoked.IsCancellationRequested;
    private object Snapshot(BrowserPhone phone)
    {
        lock (phone.Gate) return new
        {
            type = "state", ok = true, clientId = phone.Id, gatewayId = identity.ComputerId, selectedTargetId = phone.Selected,
            targets = phone.Targets.Values.Select(target =>
            {
                var state = phone.States.GetValueOrDefault(target.Id)
                    ?? new WebPhoneTargetState(target.Id, target.Name, false, false, false, false, null, null, "正在连接");
                return phone.Session?.Failed.ContainsKey(target.Id) == true
                    ? state with { Streaming = false, Recording = false, Error = "这台电脑供音未连接，其他电脑继续工作" } : state;
            }).ToArray(),
            session = phone.Session is { Ended: false } session ? new { id = session.Id, mode = session.Mode, targetIds = session.Targets.Select(x => x.Id).ToArray(), phase = session.Ready ? "active" : "starting" } : null,
            // 主电脑离线时的备用入口：每台附加电脑自己的手机网页（需在那台电脑单独确认一次）。
            entries = phone.Targets.Values.Where(target => target.EntryHost is not null).Select(target => new
            {
                id = target.Id, name = target.Name,
                url = new UriBuilder("https", target.EntryHost, webPort) { Path = "/phone/" }.Uri.AbsoluteUri
            }).ToArray()
        };
    }
    private async Task RefreshAsync(BrowserPhone phone, CancellationToken cancellation)
    {
        IWebPhoneTarget[] targets; lock (phone.Gate) targets = phone.Targets.Values.ToArray();
        await Task.WhenAll(targets.Select(target => RefreshTargetAsync(phone, target, cancellation)));
    }
    private static async Task RefreshTargetAsync(BrowserPhone phone, IWebPhoneTarget target, CancellationToken cancellation)
    {
        WebPhoneTargetState state;
        try { state = await target.HealthAsync(cancellation); }
        catch (Exception e) when (Expected(e))
        {
            var message = e is InvalidOperationException && e.Message == "请更新电脑接收端以支持网页手机" ? e.Message : "连接中断或授权失效";
            state = new(target.Id, target.Name, false, false, false, false, null, null, message);
        }
        lock (phone.Gate) if (phone.Targets.TryGetValue(target.Id, out var current) && ReferenceEquals(target, current)) phone.States[target.Id] = state;
    }
    private async Task SocketAsync(HttpContext context)
    {
        if (!originAllowed(context)) { context.Response.StatusCode = 403; return; }
        var phone = Authenticate(context, "audio");
        if (phone is null) { context.Response.StatusCode = 401; return; }
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        lock (phone.Gate) { if (phone.Socket is not null) { context.Response.StatusCode = 409; return; } phone.Socket = new SocketConnection(); }
        var connection = phone.Socket;
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, phone.Revoked.Token, lifetime.Token);
        WebSocket socket;
        try { socket = await context.WebSockets.AcceptWebSocketAsync(); }
        catch
        {
            lock (phone.Gate) if (ReferenceEquals(phone.Socket, connection)) phone.Socket = null;
            throw;
        }
        using var socketLifetime = socket; connection.Socket = socket;
        var commands = new List<Task>();
        var monitor = MonitorAsync(phone, connection, canceled.Token);
        try
        {
            var buffer = new byte[8193];
            while (!canceled.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), canceled.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                var messageType = result.MessageType; var count = result.Count;
                var maximum = messageType == WebSocketMessageType.Binary ? 1920 : 8192;
                if (count > maximum) throw new ArgumentException("单条消息超出大小限制");
                while (!result.EndOfMessage)
                {
                    // An empty final continuation frame is valid even at the exact size limit.
                    result = await socket.ReceiveAsync(buffer.AsMemory(count, maximum + 1 - count), canceled.Token);
                    if (result.MessageType != messageType) throw new ArgumentException("消息分段无效");
                    count += result.Count;
                    if (count > maximum) throw new ArgumentException("单条消息超出大小限制");
                }
                if (!Authorized(phone, "audio")) break;
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (count is < 2 or > 1920 || count % 2 != 0) throw new ArgumentException("音频帧无效");
                    AudioSession? session; lock (phone.Gate) session = phone.Session;
                    if (session is null || session.Ended) continue;
                    session.LastAudio = Environment.TickCount64;
                    if (!session.Ready) continue; // Browser sends its bounded preroll only after the start ACK.
                    var bytes = buffer.AsSpan(0, count).ToArray();
                    foreach (var target in session.Targets) if (session.Started.ContainsKey(target.Id) && !session.Failed.ContainsKey(target.Id) && !target.Feed(session.Id, bytes)) session.Failed.TryAdd(target.Id, true);
                    continue;
                }
                WebPhoneCommand command;
                try { command = JsonSerializer.Deserialize<WebPhoneCommand>(buffer.AsSpan(0, count), Json) ?? throw new JsonException(); }
                catch (JsonException) { throw new ArgumentException("消息格式无效"); }
                if (command.Type == "ping") { await SendAsync(connection, new { type = "pong", requestId = command.RequestId }, canceled.Token); continue; }
                if (string.IsNullOrWhiteSpace(command.RequestId) || command.RequestId.Length > 128) throw new ArgumentException("请求编号无效");
                commands.RemoveAll(x => x.IsCompleted);
                if (commands.Count >= 16) throw new ArgumentException("请求过于频繁");
                // Do not await a network start here: a finger-up/stop must cancel it immediately.
                commands.Add(HandleAsync(phone, connection, command, canceled.Token));
            }
        }
        catch (Exception e) when (Expected(e) || e is WebSocketException)
        {
            if (!canceled.IsCancellationRequested) await TrySendAsync(connection, new { type = "error", error = SafeError(e) }, CancellationToken.None);
        }
        finally
        {
            canceled.Cancel();
            AudioSession? session; lock (phone.Gate) session = phone.Session;
            if (session is not null) await EndAsync(phone, session, true);
            try { await Task.WhenAll(commands.Append(monitor)).WaitAsync(TimeSpan.FromSeconds(4)); } catch (Exception e) when (Expected(e) || e is WebSocketException) { }
            lock (phone.Gate) if (ReferenceEquals(phone.Socket, connection)) phone.Socket = null;
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "connection ended", CancellationToken.None); } catch (WebSocketException) { }
        }
    }
    private async Task HandleAsync(BrowserPhone phone, SocketConnection connection, WebPhoneCommand command, CancellationToken cancellation)
    {
        try
        {
            if (!Authorized(phone, command.Type == "input" || command.Type == "select" ? "control" : "audio")) throw new InvalidOperationException("授权已撤销，请重新连接");
            switch (command.Type)
            {
                case "start": await StartAsync(phone, connection, command, cancellation); break;
                case "stop":
                    if (!Guid.TryParse(command.SessionId, out _)) throw new ArgumentException("语音编号无效");
                    AudioSession? session;
                    lock (phone.Gate)
                    {
                        PruneStopped(phone); phone.Stopped[command.SessionId!] = Environment.TickCount64;
                        session = phone.Session?.Id == command.SessionId ? phone.Session : null;
                    }
                    if (session is not null) await EndAsync(phone, session, command.Cancel);
                    if (!command.Cancel && session?.StopError is { } stopError)
                        throw new InvalidOperationException(stopError);
                    await SendAsync(connection, new { type = "ack", requestId = command.RequestId, sessionId = command.SessionId, state = "stopped" }, cancellation);
                    break;
                case "select":
                    IWebPhoneTarget target;
                    lock (phone.Gate)
                    {
                        if (phone.Session is not null || phone.Pairing) throw new InvalidOperationException("请先结束当前语音");
                        target = FindTarget(phone, command.TargetId);
                    }
                    var health = await target.HealthAsync(cancellation);
                    if (!health.Online) throw new InvalidOperationException("电脑尚未连接");
                    lock (phone.Gate)
                    {
                        if (phone.Session is not null || phone.Pairing) throw new InvalidOperationException("请先结束当前语音");
                        phone.Selected = target.Id; phone.States[target.Id] = health;
                    }
                    await SendAsync(connection, new { type = "ack", requestId = command.RequestId, targetId = target.Id }, cancellation);
                    break;
                case "input": await InputAsync(phone, connection, command, cancellation); break;
                default: throw new ArgumentException("不支持的操作");
            }
        }
        catch (Exception e) when (Expected(e))
        { await TrySendAsync(connection, new { type = "error", requestId = command.RequestId, sessionId = command.SessionId, targetId = command.TargetId, error = SafeError(e) }, cancellation); }
    }
    private async Task StartAsync(BrowserPhone phone, SocketConnection connection, WebPhoneCommand command, CancellationToken cancellation)
    {
        if (!Guid.TryParse(command.SessionId, out _) || command.Mode is not ("managed" or "shared") || command.TargetIds is not { Length: >= 1 and <= 5 }
            || command.TargetIds.Distinct().Count() != command.TargetIds.Length || command.Mode == "managed" && command.TargetIds.Length != 1)
            throw new ArgumentException("语音模式、目标或编号无效");
        AudioSession session;
        lock (phone.Gate)
        {
            PruneStopped(phone);
            if (phone.Stopped.ContainsKey(command.SessionId!)) throw new InvalidOperationException("本段已经停止，请重新开始");
            if (phone.Session is not null || phone.Pairing) throw new InvalidOperationException("请先结束当前语音");
            if (command.Mode == "managed" && phone.Selected != command.TargetIds[0]) throw new InvalidOperationException("请先确认连接该电脑");
            session = new AudioSession(command.SessionId!, command.Mode!, command.TargetIds.Select(id => FindTarget(phone, id)).ToArray());
            phone.Session = session; Wake(phone);
        }
        try
        {
            var starts = session.Targets.Select(target => StartTargetAsync(session, target, cancellation)).ToList();
            session.Starts = Task.WhenAll(starts);
            var success = false;
            while (starts.Count > 0)
            {
                var completed = await Task.WhenAny(starts); starts.Remove(completed);
                if (await completed) { success = true; break; }
            }
            if (!success) throw new InvalidOperationException("电脑未能开始供音，请检查连接、输入法和音频设备");
            lock (phone.Gate)
            {
                if (session.Ended || phone.Session != session) throw new OperationCanceledException();
                session.Ready = true; session.LastAudio = Environment.TickCount64; Wake(phone);
            }
            await SendAsync(connection, new { type = "ack", requestId = command.RequestId, sessionId = session.Id }, cancellation);
        }
        catch { await EndAsync(phone, session, true); throw; }
    }
    private static async Task<bool> StartTargetAsync(AudioSession session, IWebPhoneTarget target, CancellationToken cancellation)
    {
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(cancellation, session.Canceled.Token); canceled.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            var health = await target.HealthAsync(canceled.Token);
            canceled.Token.ThrowIfCancellationRequested();
            if (!health.Online || !health.AudioReady) throw new InvalidOperationException("请先检查电脑的输入法与音频设备");
            await target.StartAsync(session.Id, session.Mode, canceled.Token);
            canceled.Token.ThrowIfCancellationRequested(); session.Started.TryAdd(target.Id, true); return true;
        }
        catch (Exception e) when (Expected(e))
        {
            session.Failed.TryAdd(target.Id, true);
            using var timeout = new CancellationTokenSource(TargetStopTimeout);
            try { await target.StopAsync(session.Id, true, timeout.Token); } catch (Exception cleanup) when (Expected(cleanup)) { }
            return false;
        }
    }
    private async Task InputAsync(BrowserPhone phone, SocketConnection connection, WebPhoneCommand command, CancellationToken cancellation)
    {
        if (command.Action is not ("goal" or "backspace" or "enter")) throw new ArgumentException("不支持的按键");
        IWebPhoneTarget target;
        lock (phone.Gate)
        {
            if (phone.Selected != command.TargetId) throw new InvalidOperationException("请先确认连接该电脑");
            target = FindTarget(phone, command.TargetId);
        }
        await inputGate.WaitAsync(cancellation);
        try
        {
            if (!Authorized(phone, "control")) throw new InvalidOperationException("授权已撤销");
            lock (phone.Gate) if (phone.Selected != command.TargetId) throw new InvalidOperationException("当前电脑已切换，请重试");
            var payload = command.TargetId + ":" + command.Action;
            if (!inputs.Duplicate(phone.Id, command.RequestId!, payload))
            {
                inputs.Begin(phone.Id, command.RequestId!, payload);
                await target.InputAsync(command.RequestId!, command.Action!, cancellation);
                inputs.Confirm(phone.Id, command.RequestId!, payload);
            }
        }
        finally { inputGate.Release(); }
        await SendAsync(connection, new { type = "ack", requestId = command.RequestId, targetId = command.TargetId }, cancellation);
    }
    private async Task MonitorAsync(BrowserPhone phone, SocketConnection connection, CancellationToken cancellation)
    {
        string? previous = null;
        var probes = new Dictionary<string, (Task Task, long At)>();
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                AudioSession? session; WebPhoneTargetState[] states; IWebPhoneTarget[] targets;
                lock (phone.Gate) { session = phone.Session; targets = phone.Targets.Values.ToArray(); }
                var now = Environment.TickCount64;
                foreach (var target in targets)
                {
                    var active = session?.Targets.Contains(target) == true;
                    if (!probes.TryGetValue(target.Id, out var probe) || probe.Task.IsCompleted && now - probe.At >= (active ? 100 : 1500))
                        probes[target.Id] = (RefreshTargetAsync(phone, target, cancellation), now);
                }
                lock (phone.Gate) states = phone.States.Values.ToArray();
                if (session is not null && !session.Ended)
                {
                    var remoteStop = session.Mode == "managed" && states.Any(x => x.Id == session.Targets[0].Id && x.StopRequestedSessionId == session.Id);
                    var noAudio = Environment.TickCount64 - session.LastAudio > 5000;
                    var allFailed = session.Ready && session.Starts.IsCompleted && session.Targets.All(x => session.Failed.ContainsKey(x.Id) || states.Any(s => s.Id == x.Id && !s.Online));
                    if (remoteStop || noAudio || allFailed)
                    {
                        var cleanup = EndAsync(phone, session, !remoteStop);
                        // Turn off the microphone promptly; draining a slow peer is not a
                        // reason to keep capturing audio after the desktop has stopped.
                        await SendAsync(connection, new { type = "stopped", sessionId = session.Id, reason = remoteStop ? "电脑已停止" : noAudio ? "手机音频中断，请重新开始" : "电脑连接已断开" }, cancellation);
                        await cleanup;
                    }
                }
                var state = JsonSerializer.Serialize(Snapshot(phone), Json);
                if (state != previous) { await SendTextAsync(connection, state, cancellation); previous = state; }
                await phone.Changed.WaitAsync(session is null ? 1500 : 100, cancellation);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }
    private Task EndAsync(BrowserPhone phone, AudioSession session, bool cancel)
    {
        lock (phone.Gate)
        {
            if (session.Cleanup is not null) return session.Cleanup;
            session.Ended = true; session.Canceled.Cancel(); phone.Stopped[session.Id] = Environment.TickCount64;
            // Keep the slot until teardown finishes: a delayed cleanup must never detach a newer stream.
            return session.Cleanup = EndTargetsAsync(phone, session, cancel);
        }
    }
    private static async Task EndTargetsAsync(BrowserPhone phone, AudioSession session, bool cancel)
    {
        var failures = await Task.WhenAll(session.Targets.Select(async target =>
        {
            using var timeout = new CancellationTokenSource(TargetStopTimeout);
            try { await target.StopAsync(session.Id, cancel, timeout.Token); return null; }
            catch (Exception e) when (Expected(e)) { return target.Name + "：语音收尾未确认；" + SafeError(e); }
        }));
        session.StopError = failures.FirstOrDefault(failure => failure is not null);
        lock (phone.Gate) if (phone.Session == session) { phone.Session = null; Wake(phone); }
    }
    private static void Wake(BrowserPhone phone)
    {
        try { phone.Changed.Release(); } catch (SemaphoreFullException) { }
    }
    private static IWebPhoneTarget FindTarget(BrowserPhone phone, string? id) => id is not null && phone.Targets.TryGetValue(id, out var target) ? target : throw new ArgumentException("请选择已配对的电脑");
    private static void PruneStopped(BrowserPhone phone)
    {
        foreach (var id in phone.Stopped.Where(x => Environment.TickCount64 - x.Value > 60000).Select(x => x.Key).ToArray()) phone.Stopped.Remove(id);
        if (phone.Stopped.Count >= 1024) throw new InvalidOperationException("操作过于频繁，请稍后重试");
    }
    private static bool Expected(Exception e) => e is ArgumentException or InvalidOperationException or IOException or HttpRequestException or OperationCanceledException or TimeoutException or JsonException;
    private static string SafeError(Exception e) => e is ArgumentException or InvalidOperationException ? e.Message : e is OperationCanceledException ? "操作已取消或连接超时" : "连接未完成，请检查电脑状态后重试";
    private static Task SendAsync(SocketConnection connection, object value, CancellationToken cancellation) => SendTextAsync(connection, JsonSerializer.Serialize(value, Json), cancellation);
    private static async Task TrySendAsync(SocketConnection connection, object value, CancellationToken cancellation)
    { try { await SendAsync(connection, value, cancellation); } catch (Exception e) when (Expected(e) || e is WebSocketException) { } }
    private static async Task SendTextAsync(SocketConnection connection, string text, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await connection.SendGate.WaitAsync(timeout.Token);
        try { if (connection.Socket?.State == WebSocketState.Open) await connection.Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, timeout.Token); }
        finally { connection.SendGate.Release(); }
    }
    public void Dispose()
    {
        pairing.Cancel(); lifetime.Cancel();
        foreach (var phone in phones.Values)
        {
            phone.Revoked.Cancel(); IWebPhoneTarget[] targets;
            lock (phone.Gate) targets = phone.Targets.Values.ToArray();
            foreach (var target in targets) target.Dispose();
        }
    }
    private sealed class BrowserPhone(string id, string label, string selected)
    {
        internal readonly object Gate = new();
        internal readonly SemaphoreSlim Changed = new(0, 1);
        internal readonly string Id = id, Label = label;
        internal readonly Dictionary<string, IWebPhoneTarget> Targets = new();
        internal readonly Dictionary<string, WebPhoneTargetState> States = new();
        internal readonly Dictionary<string, long> Stopped = new();
        internal readonly CancellationTokenSource Revoked = new();
        internal string Selected = selected;
        internal AudioSession? Session;
        internal SocketConnection? Socket;
        internal bool Pairing;
    }
    private sealed class AudioSession(string id, string mode, IWebPhoneTarget[] targets)
    {
        internal readonly string Id = id, Mode = mode;
        internal readonly IWebPhoneTarget[] Targets = targets;
        internal readonly CancellationTokenSource Canceled = new();
        internal readonly ConcurrentDictionary<string, bool> Failed = new(), Started = new();
        internal Task Starts = Task.CompletedTask;
        internal volatile bool Ready, Ended;
        internal long LastAudio = Environment.TickCount64;
        internal Task? Cleanup;
        internal string? StopError;
    }
    private sealed class SocketConnection
    {
        internal WebSocket? Socket;
        internal readonly SemaphoreSlim SendGate = new(1);
    }
}
