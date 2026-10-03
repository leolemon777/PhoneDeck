using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace PhoneDeck.Desktop;

internal sealed record WebPhoneTargetState(string Id, string Name, bool Online, bool AudioReady,
    bool Streaming, bool Recording, string? SessionId, string? StopRequestedSessionId, string? Error = null);

internal interface IWebPhoneTarget : IDisposable
{
    string Id { get; }
    string Name { get; }
    Task<WebPhoneTargetState> HealthAsync(CancellationToken cancellation);
    Task StartAsync(string session, string mode, CancellationToken cancellation);
    bool Feed(string session, byte[] pcm);
    Task StopAsync(string session, bool cancel, CancellationToken cancellation);
    Task InputAsync(string request, string action, CancellationToken cancellation);
}

internal sealed class WebPhoneLocalTarget(ReceiverIdentity identity, SpeechSession speech,
    PlatformInput input, string owner, Func<bool> audioReady, Action<string>? testInput = null) : IWebPhoneTarget
{
    private readonly object gate = new();
    private string? activeSession;
    private string? mode;
    private string name = identity.DisplayName;
    public string Id => identity.ComputerId;
    public string Name => name;
    internal void UpdateName(string value) => name = value;
    public Task<WebPhoneTargetState> HealthAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var health = speech.HealthForPhone(owner);
        string? session; lock (gate) session = activeSession;
        var ours = session is not null && health.StreamSession == session;
        return Task.FromResult(new WebPhoneTargetState(Id, Name, true, audioReady(), ours && health.Streaming,
            ours && health.Recording, ours ? session : null, health.StopRequestedSessionId));
    }
    public Task StartAsync(string session, string requestedMode, CancellationToken cancellation)
    {
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (activeSession is not null) throw new InvalidOperationException("请先结束上一段供音");
            speech.Attach(owner, session, requestedMode); activeSession = session; mode = requestedMode;
            try { if (mode == "managed") speech.Start(owner, session); }
            catch { speech.EndStream(session, false); activeSession = null; mode = null; throw; }
        }
        return Task.CompletedTask;
    }
    public bool Feed(string session, byte[] pcm)
    {
        lock (gate)
        {
            if (activeSession != session) return false;
            speech.Feed(session, pcm); return true;
        }
    }
    public async Task StopAsync(string session, bool cancel, CancellationToken cancellation)
    {
        bool managed;
        lock (gate)
        {
            if (activeSession != session) return;
            managed = mode == "managed"; activeSession = null; mode = null;
            if (managed) speech.EndStream(session, !cancel);
        }
        if (managed) await speech.StopAsync(owner, session, cancel: cancel);
        // Closing the shared microphone cancels a desktop segment that was not yet submitted.
        else await speech.StopSupplyAsync(owner, session, true);
    }
    public Task InputAsync(string request, string action, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (testInput is not null) testInput(action);
        else if (action == "goal") { input.Execute("text", "/goal", null, null); input.Execute("enter", null, null, null); }
        else input.Execute(action, null, null, null);
        return Task.CompletedTask;
    }
    public void Dispose() { lock (gate) if (activeSession is { } session) { speech.EndStream(session, false); activeSession = null; } }
}

internal sealed record WebPhonePeerRecord(string OwnerId, string ComputerId, string Name, string Host, int Port,
    string CertificateSha256, string ClientId, string Token, string[] Scopes);

// Outgoing credentials are the gateway's per-browser phone credentials. They are never returned
// to the browser, logged or stored in the ordinary phone-credentials hash table.
internal sealed class WebPhonePeerStore
{
    private readonly object gate = new();
    private readonly string path;
    private List<WebPhonePeerRecord> records;
    internal WebPhonePeerStore(string path)
    {
        this.path = path;
        records = File.Exists(path) ? JsonSerializer.Deserialize<List<WebPhonePeerRecord>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("网页手机配对文件损坏，请恢复备份") : [];
        if (records.Count > 500 || records.Any(r => !Guid.TryParse(r.OwnerId, out _) || !Guid.TryParse(r.ComputerId, out _)))
            throw new InvalidDataException("网页手机配对文件无效");
    }
    internal WebPhonePeerRecord[] ForOwner(string owner) { lock (gate) return records.Where(x => x.OwnerId == owner).ToArray(); }
    internal void Save(WebPhonePeerRecord peer)
    {
        lock (gate)
        {
            var next = records.Where(x => x.OwnerId != peer.OwnerId || x.ComputerId != peer.ComputerId).Append(peer).ToList();
            if (next.Count(x => x.OwnerId == peer.OwnerId) > 4) throw new ArgumentException("每台手机最多连接五台电脑（含主电脑）");
            Write(next); records = next;
        }
    }
    internal void Remove(string owner, string? target = null)
    {
        lock (gate)
        {
            var next = records.Where(x => x.OwnerId != owner || target is not null && x.ComputerId != target).ToList();
            Write(next); records = next;
        }
    }
    private void Write(List<WebPhonePeerRecord> next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); PrivateFiles.RestrictDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                PrivateFiles.RestrictFile(temporary); JsonSerializer.Serialize(stream, next); stream.Flush(true);
            }
            File.Move(temporary, path, true); PrivateFiles.RestrictFile(path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class WebPhoneRemoteTarget : IWebPhoneTarget
{
    private readonly WebPhonePeerRecord peer;
    private readonly HttpClient http;
    private readonly object gate = new();
    private RemoteAudio? audio;
    public string Id => peer.ComputerId;
    public string Name => peer.Name;
    internal WebPhoneRemoteTarget(WebPhonePeerRecord peer, HttpClient? testTransport = null)
    {
        this.peer = peer;
        http = testTransport ?? CreateHttp(peer.Host, peer.Port, peer.CertificateSha256);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", peer.Token);
        http.DefaultRequestHeaders.Add("X-PhoneDeck-Client", peer.ClientId);
    }
    internal static HttpClient CreateHttp(string host, int port, string certificateSha256)
    {
        if (!AllowedAddress(host) || port is < 1024 or > 65535 || certificateSha256.Length != 64 || !certificateSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("电脑地址、端口或证书指纹无效");
        var expected = Convert.FromHexString(certificateSha256);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                certificate is not null && CryptographicOperations.FixedTimeEquals(expected, certificate.GetCertHash(HashAlgorithmName.SHA256)) },
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        return new HttpClient(handler) { BaseAddress = new UriBuilder("https", host, port).Uri, Timeout = Timeout.InfiniteTimeSpan };
    }
    internal static bool AllowedAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (b.Length == 4) return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168;
        return b.Length == 16 && (b[0] & 0xfe) == 0xfc; // ULA only; no zone, multicast, loopback or metadata addresses.
    }
    internal static async Task<WebPhonePeerRecord> PairAsync(string owner, string label, string qrPayload, string host, CancellationToken cancellation)
    {
        if (qrPayload.Length > 8192) throw new ArgumentException("配对资料过长");
        using var qr = ParseObject(qrPayload); var root = qr.RootElement;
        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1) throw new ArgumentException("配对资料版本不支持");
        var computer = String(root, "computerId"); var pin = String(root, "certificateSha256");
        var pairingId = String(root, "pairingId"); var material = String(root, "oneTimeMaterial");
        if (!Guid.TryParse(computer, out _) || !Guid.TryParse(pairingId, out _) || material.Length != 32
            || !root.TryGetProperty("httpsPort", out var portValue) || portValue.ValueKind != JsonValueKind.Number || !portValue.TryGetInt32(out var port)) throw new ArgumentException("请粘贴电脑生成的完整配对资料");
        using var client = CreateHttp(host, port, pin);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(35));
        var requestedId = Guid.NewGuid().ToString();
        using var response = await client.PostAsJsonAsync("api/lan/pair/qr", new { pairingId, oneTimeMaterial = material, clientId = requestedId, clientLabel = "网页手机 · " + label }, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("电脑未批准配对，请重新打开配对窗口并在该电脑确认");
        using var result = await ReadJsonAsync(response, deadline.Token); var value = result.RootElement;
        var token = String(value, "clientToken"); var clientId = String(value, "clientId");
        var scopes = StringArray(value, "scopes");
        if (String(value, "computerId") != computer || !String(value, "certificateSha256").Equals(pin, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(clientId, out _) || token.Length is < 32 or > 256 || token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')) || !scopes.Contains("audio") || !scopes.Contains("control"))
            throw new InvalidOperationException("电脑身份或授权范围与配对资料不一致");
        return new(owner, computer, String(value, "displayName")[..Math.Min(64, String(value, "displayName").Length)], host, port, pin, clientId, token, scopes);
    }
    public async Task<WebPhoneTargetState> HealthAsync(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromMilliseconds(900));
        using var response = await http.GetAsync("api/health", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode(); using var json = await ReadJsonAsync(response, deadline.Token);
        var root = json.RootElement;
        if (String(root, "computerId") != Id) throw new InvalidOperationException("电脑身份不匹配");
        var a = Object(root, "audio"); var d = Object(root, "dictation");
        var capabilities = StringArray(root, "capabilities");
        if (!capabilities.Contains("builtInSpeechV1") || !capabilities.Contains("phoneStopV1") || !capabilities.Contains("audioStopV1")) throw new InvalidOperationException("请更新电脑接收端以支持网页手机");
        var available = Boolean(a, "available"); var streaming = Boolean(a, "streaming"); var recording = Boolean(d, "active");
        string? session; lock (gate) session = audio?.Session;
        var ours = session is not null && OptionalString(a, "sessionId") == session;
        return new(Id, Name, true, available,
            ours && streaming, ours && recording,
            ours ? session : null, OptionalString(a, "stopRequestedSessionId"));
    }
    public async Task StartAsync(string session, string mode, CancellationToken cancellation)
    {
        if (!peer.Scopes.Contains("audio")) throw new InvalidOperationException("这台电脑未授权供音");
        RemoteAudio stream;
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (audio is not null) throw new InvalidOperationException("上一段供音尚未结束");
            audio = stream = new RemoteAudio(http, peer.ComputerId, session, mode);
        }
        try
        {
            var ready = await Task.WhenAny(stream.SentHeaders.Task, stream.Transfer).WaitAsync(TimeSpan.FromSeconds(3), cancellation);
            await ready; cancellation.ThrowIfCancellationRequested();
            if (mode == "managed") await PostAsync("api/dictation/start", new { protocolVersion = 2, requestId = Guid.NewGuid().ToString(), sessionId = session, targetComputerId = Id, mode = "dictation" }, cancellation);
            var until = Environment.TickCount64 + 2500;
            while (true)
            {
                if (stream.Transfer.IsCompleted) throw new IOException("电脑未能接收音频");
                var health = await HealthAsync(cancellation);
                if (health.StopRequestedSessionId == session) throw new InvalidOperationException("电脑已停止本段听写");
                if (health.Streaming) break;
                if (Environment.TickCount64 >= until) throw new IOException("音频连接超时");
                await Task.Delay(30, cancellation);
            }
        }
        catch { await StopAsync(session, true, CancellationToken.None); throw; }
    }
    public bool Feed(string session, byte[] pcm)
    {
        lock (gate) return audio is { } current && current.Session == session && current.Feed(pcm);
    }
    public async Task StopAsync(string session, bool cancel, CancellationToken cancellation)
    {
        RemoteAudio? stream;
        lock (gate) { if (audio?.Session != session) return; stream = audio; audio = null; }
        try
        {
            if (stream.Mode == "shared")
                await PostAsync("api/audio/stop", new { protocolVersion = 2, requestId = Guid.NewGuid().ToString(), sessionId = session, targetComputerId = Id, cancel = true }, cancellation);
            await stream.FinishAsync(cancel);
            if (stream.Mode == "managed") await PostAsync("api/dictation/stop", new { protocolVersion = 2, requestId = Guid.NewGuid().ToString(), sessionId = session, targetComputerId = Id, mode = "dictation", cancel }, cancellation);
        }
        finally { stream.Dispose(); }
    }
    public Task InputAsync(string request, string action, CancellationToken cancellation)
    {
        if (!peer.Scopes.Contains("control")) throw new InvalidOperationException("这台电脑未授权输入");
        object body = action == "goal"
            ? new { protocolVersion = 2, requestId = request, sessionId = Guid.NewGuid().ToString(), targetComputerId = Id, action = "macro", steps = new[] { new { type = "text", text = "/goal", submit = true } } }
            : new { protocolVersion = 2, requestId = request, sessionId = Guid.NewGuid().ToString(), targetComputerId = Id, action };
        return PostAsync("api/input", body, cancellation);
    }
    private async Task PostAsync(string path, object body, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var response = await http.PostAsJsonAsync(path, body, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "这台电脑的授权已失效，请重新配对" : "电脑未完成操作，请检查是否忙碌或尚未就绪");
    }
    private static string String(JsonElement value, string key) => OptionalString(value, key) ?? throw new ArgumentException("配对资料缺少 " + key);
    private static string? OptionalString(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static JsonElement Object(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Object
        ? item : throw new InvalidDataException("电脑响应格式无效");
    private static bool Boolean(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? item.GetBoolean() : throw new InvalidDataException("电脑响应格式无效");
    internal static string[] StringArray(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var item) || item.ValueKind != JsonValueKind.Array
            || item.GetArrayLength() > 128 || item.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String)) throw new InvalidDataException("电脑响应格式无效");
        return item.EnumerateArray().Select(x => x.GetString()!).ToArray();
    }
    private static JsonDocument ParseObject(string text)
    {
        JsonDocument json;
        try { json = JsonDocument.Parse(text); } catch (JsonException) { throw new ArgumentException("配对资料格式无效"); }
        if (json.RootElement.ValueKind == JsonValueKind.Object) return json;
        json.Dispose(); throw new ArgumentException("配对资料格式无效");
    }
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        await using var body = await response.Content.ReadAsStreamAsync(cancellation);
        using var data = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await body.ReadAsync(buffer, cancellation)) > 0)
        {
            if (data.Length + count > 32768) throw new InvalidDataException("电脑响应过大");
            data.Write(buffer, 0, count);
        }
        try
        {
            var json = JsonDocument.Parse(data.ToArray());
            if (json.RootElement.ValueKind == JsonValueKind.Object) return json;
            json.Dispose(); throw new InvalidDataException("电脑响应格式无效");
        }
        catch (JsonException) { throw new InvalidDataException("电脑响应格式无效"); }
    }
    public void Dispose() { lock (gate) { audio?.Dispose(); audio = null; } http.Dispose(); }

    private sealed class RemoteAudio : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly Channel<byte[]> queue;
        internal readonly TaskCompletionSource SentHeaders = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Task Transfer;
        internal string Session { get; }
        internal string Mode { get; }
        internal RemoteAudio(HttpClient http, string target, string session, string mode)
        {
            Session = session; Mode = mode;
            queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(mode == "shared" ? 6 : 50) { SingleReader = true, FullMode = mode == "shared" ? BoundedChannelFullMode.DropOldest : BoundedChannelFullMode.Wait });
            var content = new AudioContent(queue.Reader, SentHeaders, cancellation.Token);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var request = new HttpRequestMessage(HttpMethod.Post, "api/audio/stream") { Content = content };
            request.Headers.Add("X-PhoneDeck-Protocol", "2"); request.Headers.Add("X-PhoneDeck-Computer-Id", target);
            request.Headers.Add("X-PhoneDeck-Session", session); request.Headers.Add("X-PhoneDeck-Audio-Mode", mode);
            Transfer = SendAsync(http, request);
        }
        private async Task SendAsync(HttpClient http, HttpRequestMessage request)
        {
            using (request)
            using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token))
                response.EnsureSuccessStatusCode();
        }
        internal bool Feed(byte[] bytes)
        {
            if (Transfer.IsCompleted) return false;
            if (queue.Writer.TryWrite(bytes)) return true;
            cancellation.Cancel(); return false; // Managed mode never silently discards speech.
        }
        internal async Task FinishAsync(bool cancel)
        {
            queue.Writer.TryComplete(); if (cancel) cancellation.Cancel();
            try { await Transfer.WaitAsync(TimeSpan.FromMilliseconds(1200)); }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException or HttpRequestException or IOException) { cancellation.Cancel(); }
        }
        public void Dispose() { queue.Writer.TryComplete(); cancellation.Cancel(); }
    }
    private sealed class AudioContent(ChannelReader<byte[]> queue, TaskCompletionSource sent, CancellationToken cancellation) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            // One silent frame flushes request headers so the receiver can acknowledge attachment.
            await stream.WriteAsync(new byte[1920], cancellation); await stream.FlushAsync(cancellation); sent.TrySetResult();
            await foreach (var bytes in queue.ReadAllAsync(cancellation))
            { await stream.WriteAsync(bytes, cancellation); await stream.FlushAsync(cancellation); }
        }
    }
}
