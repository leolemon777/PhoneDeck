/// <summary>
/// Receiver.Core：原生手机的逐手机凭据与配对接口，Windows 与 macOS 接收端共用。
/// - 8766 /api/lan/pair/qr：一次性材料 + 本机确认后签发独立凭据（Bearer）；
/// - 8765 /api/lan/pair：USB 回环签发旧共享令牌（旧入口撤销后 410）；
/// - 8766 /api/lan/credential/rotate：旧共享令牌升级为逐手机凭据；
/// - 8765 /api/admin/*：开启/确认/拒绝配对、列出与撤销手机、应急撤销旧入口；
/// - 8765 /admin/pairing：本机配对管理页（二维码、确认、撤销），macOS 无托盘时使用。
/// 撤销会通过 <see cref="ClientSessionRegistry"/> 立即终止该手机的音频长流。
/// </summary>
internal sealed record NativePairingHost(
    string ComputerId,
    Func<string> DisplayName,
    string Platform,
    string CertificateSha256,
    int HttpsPort,
    Func<string[]> CandidateAddresses,
    string SharedAccessToken,
    ClientCredentialsStore Credentials,
    ClientSessionRegistry Sessions,
    PairingWindowManager PairingWindows,
    Action<string> RevokeInput);

internal sealed record ClientRevokeRequest(string? ClientId);
internal sealed record RotateRequest(string? ClientId);
internal sealed record QrPairRequest(
    string? PairingId,
    string? OneTimeMaterial,
    string? ClientId,
    string? ClientLabel);
internal sealed record PairingAdminRequest(string? PairingId);
internal sealed record NearbyPairRequest(string? ClientId, string? ClientLabel, string? Nonce);

internal static class NativePairingEndpoints
{
    internal static void Map(WebApplication app, NativePairingHost host)
    {
        app.MapPost("/api/lan/pair/qr", async (QrPairRequest body) =>
        {
            var clientLabel = body.ClientLabel?.Trim();
            if (clientLabel is { Length: > 64 })
            {
                clientLabel = clientLabel[..64];
            }
            var status = host.PairingWindows.TryBeginSubmit(
                body.PairingId, body.OneTimeMaterial, body.ClientId?.Trim() ?? "", clientLabel ?? "",
                out var pending);
            switch (status)
            {
                case PairingSubmitStatus.WindowNotOpen:
                    return Results.NotFound(new { ok = false, error = "配对窗口未开启" });
                case PairingSubmitStatus.MaterialInvalid:
                    return Results.Json(
                        new { ok = false, error = "配对材料无效或已使用" },
                        statusCode: StatusCodes.Status401Unauthorized);
                case PairingSubmitStatus.FailureLimit:
                    return Results.Json(
                        new { ok = false, error = "失败次数过多，请在电脑上重新开启配对" },
                        statusCode: StatusCodes.Status429TooManyRequests);
            }
            if (pending is null)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
            bool confirmed;
            try
            {
                // 挂起等待本机确认/拒绝/超时（30s），随请求取消一并中断。
                confirmed = await pending.Decision.Task.WaitAsync(
                    TimeSpan.FromSeconds(PairingWindowManager.ConfirmationTimeoutSeconds));
            }
            catch (TimeoutException)
            {
                return Results.Json(
                    new { ok = false, error = "本机确认超时，请重试" },
                    statusCode: StatusCodes.Status408RequestTimeout);
            }
            if (!confirmed)
            {
                return Results.Json(
                    new { ok = false, error = "电脑端已拒绝本次配对" },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            var record = host.Credentials.Issue(
                pending.ClientLabel,
                new[] { "control", "audio", "settings", "update-request" },
                pending.PairingId,
                out var clientToken,
                pending.ClientId);
            Console.WriteLine($"配对完成：clientId={record.ClientId} pairingId={pending.PairingId}（材料与令牌不落日志）");
            return Results.Ok(new
            {
                ok = true,
                clientId = record.ClientId,
                clientToken,
                scopes = record.Scopes,
                pairingId = pending.PairingId,
                computerId = host.ComputerId,
                displayName = host.DisplayName(),
                certificateSha256 = host.CertificateSha256,
            });
        });

        // 同一 Wi-Fi 免扫码连接：手机发现电脑后提交请求，电脑本机允许后签发逐手机凭据。
        app.MapPost("/api/lan/pair/request", async (HttpContext context, NearbyPairRequest body) =>
        {
            if (context.Connection.LocalPort != host.HttpsPort)
            {
                return Results.NotFound();
            }
            var status = host.PairingWindows.TryBeginNearbyRequest(
                body.ClientId, body.ClientLabel, body.Nonce?.Trim().ToLowerInvariant(),
                out var pending, out _);
            switch (status)
            {
                case NearbyRequestStatus.Invalid:
                    return Results.BadRequest(new { ok = false, error = "请求格式无效" });
                case NearbyRequestStatus.Busy:
                    return Results.Json(new { ok = false, error = "电脑正在确认另一台手机，请稍后再试" },
                        statusCode: StatusCodes.Status409Conflict);
                case NearbyRequestStatus.RateLimited:
                    return Results.Json(new { ok = false, error = "请求过于频繁，请稍后再试" },
                        statusCode: StatusCodes.Status429TooManyRequests);
            }
            if (pending is null)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
            bool confirmed;
            try
            {
                confirmed = await pending.Decision.Task.WaitAsync(
                    TimeSpan.FromSeconds(PairingWindowManager.NearbyConfirmationTimeoutSeconds),
                    context.RequestAborted);
            }
            catch (TimeoutException)
            {
                return Results.Json(new { ok = false, error = "电脑上没有人确认，请重试" },
                    statusCode: StatusCodes.Status408RequestTimeout);
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499);
            }
            finally
            {
                host.PairingWindows.FinishNearby(pending.PairingId);
            }
            if (!confirmed)
            {
                return Results.Json(new { ok = false, error = "电脑端已拒绝本次连接" },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            var record = host.Credentials.Issue(
                pending.ClientLabel,
                new[] { "control", "audio", "settings", "update-request" },
                pending.PairingId,
                out var clientToken,
                pending.ClientId);
            Console.WriteLine($"附近连接完成：clientId={record.ClientId}（令牌不落日志）");
            return Results.Ok(new
            {
                ok = true,
                clientId = record.ClientId,
                clientToken,
                scopes = record.Scopes,
                pairingId = pending.PairingId,
                computerId = host.ComputerId,
                displayName = host.DisplayName(),
                platform = host.Platform,
                certificateSha256 = host.CertificateSha256,
            });
        });

        // M1-A A2 回环配对管理（设计 §3/§10：配对窗口只经本地 UI 开启，无 HTTP 外部入口）。
        app.MapGet("/api/admin/pairing/status", (HttpContext context) =>
            context.Connection.LocalPort != 8765
                ? Results.NotFound()
                : Results.Ok(host.PairingWindows.StatusSnapshot()));
        app.MapPost("/api/admin/pairing/begin", (HttpContext context) =>
        {
            if (context.Connection.LocalPort != 8765)
            {
                return Results.NotFound();
            }
            if (host.PairingWindows.HasPendingConfirmation())
            {
                return Results.Conflict(new { ok = false, error = "有待确认的配对提交" });
            }
            var session = host.PairingWindows.Begin();
            return Results.Ok(new
            {
                ok = true,
                pairingId = session.PairingId,
                qrPayload = session.QrPayloadJson,
                manualCode = session.ManualCode,
                checkCode = session.MaterialCheckCode,
                validSeconds = PairingWindowManager.ValidSeconds,
            });
        });
        app.MapPost("/api/admin/pairing/cancel", (HttpContext context) =>
        {
            if (context.Connection.LocalPort != 8765)
            {
                return Results.NotFound();
            }
            host.PairingWindows.Cancel();
            return Results.Ok(new { ok = true });
        });
        app.MapPost("/api/admin/pairing/confirm", (HttpContext context, PairingAdminRequest body) =>
            context.Connection.LocalPort != 8765
                ? Results.NotFound()
                : host.PairingWindows.Confirm(body.PairingId)
                    ? Results.Ok(new { ok = true })
                    : Results.NotFound(new { ok = false, error = "没有待确认的配对" }));
        app.MapPost("/api/admin/pairing/deny", (HttpContext context, PairingAdminRequest body) =>
            context.Connection.LocalPort != 8765
                ? Results.NotFound()
                : host.PairingWindows.Deny(body.PairingId)
                    ? Results.Ok(new { ok = true })
                    : Results.NotFound(new { ok = false, error = "没有待确认的配对" }));

        app.MapPost("/api/lan/pair", (HttpContext context) =>
        {
            if (!LanRequestAuthenticator.IsUsbPairingRequest(
                    context.Connection.LocalPort,
                    context.Connection.RemoteIpAddress))
            {
                return Results.NotFound();
            }
            if (host.Credentials.LegacyRevoked)
            {
                // 设计 §5.6：旧入口关闭后此端点停止签发共享令牌（410），不再制造 legacy 凭据。
                return Results.Json(
                    new { ok = false, error = "旧共享令牌入口已关闭，请在电脑上扫码配对获取独立凭据" },
                    statusCode: StatusCodes.Status410Gone);
            }
            return Results.Ok(new
            {
                ok = true,
                protocolVersion = 2,
                computerId = host.ComputerId,
                displayName = host.DisplayName(),
                platform = host.Platform,
                addresses = host.CandidateAddresses(),
                port = host.HttpsPort,
                certificateSha256 = host.CertificateSha256,
                accessToken = host.SharedAccessToken,
                // 设计 §5.6/§5.2：提示旧共享令牌可在手机端升级为独立凭据，无需重新扫码。
                upgradeHint = "共享令牌仅供旧版本使用；升级手机 App 后可用它换领该手机独立凭据，无需重新扫码"
            });
        });

        // M1-A A4 旧共享令牌升级（设计 §5.2）：仅 8766、仅旧共享令牌鉴权可调；手机无需重新扫码
        // 即获得该手机专属凭据。rotate 永不重发（G-1 处置）：同 clientId 重放只回 already-upgraded
        // 三字段状态，绝无令牌；revoked clientId 永不重发（403）；简单限速（同 clientId ≥3s）。
        app.MapPost("/api/lan/credential/rotate", (HttpContext context, RotateRequest body) =>
        {
            if (context.Connection.LocalPort != 8766)
            {
                return Results.NotFound();
            }
            // Bearer/逐手机凭据调用者不获新能力（设计 §5.1）：新头不得重入签发。
            if (!LanRequestAuthenticator.IsLegacySharedCaller(context.Items["ClientId"] as string))
            {
                return Results.Json(
                    new { ok = false, error = "请使用旧共享令牌升级" },
                    statusCode: StatusCodes.Status403Forbidden);
            }
            var clientId = body.ClientId?.Trim();
            if (string.IsNullOrWhiteSpace(clientId) || !Guid.TryParse(clientId, out _))
            {
                return Results.BadRequest(new { ok = false, error = "clientId 必须为手机生成的 GUID" });
            }
            RotateResult rotation;
            try
            {
                rotation = host.Credentials.Rotate(clientId, "旧共享令牌升级手机");
            }
            catch (Exception exception)
            {
                // 签发持久化失败：中止，内存与磁盘保持一致，可安全重试（设计 §6 M-2 纪律）。
                Console.Error.WriteLine($"rotate 签发持久化失败：{exception.Message}");
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
            switch (rotation.Outcome)
            {
                case RotateOutcome.Revoked:
                    return Results.Json(
                        new { ok = false, error = "该 clientId 已撤销，凭据找回需重新扫码配对" },
                        statusCode: StatusCodes.Status403Forbidden);
                case RotateOutcome.RateLimited:
                    return Results.Json(
                        new { ok = false, error = "rotate 请求过于频繁，请稍后重试" },
                        statusCode: StatusCodes.Status429TooManyRequests);
                case RotateOutcome.AlreadyUpgraded:
                    // G-1 处置：该形态仅 {ok,status,clientId} 三字段，绝无令牌本体。
                    return Results.Ok(new
                    {
                        ok = true,
                        status = "already-upgraded",
                        clientId = rotation.Record!.ClientId,
                    });
                default:
                    var record = rotation.Record!;
                    // issued 形态逐字对齐冻结契约 credentialRotateResponse（additionalProperties:false，
                    // 属性表无 ok 字段）；already-upgraded 形态则冻结为 {ok,status,clientId} 三字段。
                    return Results.Ok(new
                    {
                        status = "issued",
                        clientId = record.ClientId,
                        clientToken = rotation.Token,
                        scopes = record.Scopes,
                        pairingId = record.PairingId,
                        computerId = host.ComputerId,
                        displayName = host.DisplayName(),
                        certificateSha256 = host.CertificateSha256,
                    });
            }
        });

        // M1-A A1/A4 回环管理端点（设计 §3 不变量：撤销属本机信任操作；仅 8765 回环可达，无鉴权面扩大）。
        app.MapGet("/api/admin/clients", (HttpContext context) =>
        {
            if (context.Connection.LocalPort != 8765)
            {
                return Results.NotFound();
            }
            var clients = host.Credentials.ListRedacted().Select(record => (object)record).ToList();
            if (!host.Credentials.LegacyRevoked)
            {
                // 设计 §5.3（M-4 处置）：legacy 未撤销时附加合成条目，显式列出旧入口，
                // 供托盘"应急撤销旧入口"定位；撤销后该条目消失（入口已关闭）。
                clients.Add(new
                {
                    clientId = ClientCredentialsStore.LegacySharedClientId,
                    label = "未升级旧凭据（legacy 入口）",
                    legacy = true,
                });
            }
            return Results.Ok(new
            {
                ok = true,
                clients,
                legacyRevokedAt = host.Credentials.LegacyRevokedAtUtc,
            });
        });
        app.MapPost("/api/admin/clients/revoke", (HttpContext context, ClientRevokeRequest body) =>
        {
            if (context.Connection.LocalPort != 8765)
            {
                return Results.NotFound();
            }
            var clientId = body.ClientId?.Trim();
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return Results.BadRequest(new { ok = false, error = "缺少 clientId" });
            }
            try
            {
                if (!host.Credentials.Revoke(clientId))
                {
                    return Results.NotFound(new { ok = false, error = "未知 clientId" });
                }
            }
            catch (Exception exception)
            {
                // 持久化失败：撤销中止，内存与磁盘保持一致，可安全重试（设计 §6 M-2 处置）。
                Console.Error.WriteLine($"撤销持久化失败：{exception.Message}");
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
            host.Sessions.Cancel(clientId);
            host.RevokeInput(clientId);
            return Results.Ok(new { ok = true, clientId });
        });

        // M1-A A4 legacy 应急撤销（设计 §5.3，M-4 处置）：仅 8765 回环。store 内先原子持久化
        // legacyRevokedAt 再生效，此后旧共享令牌一切请求 401（LanRequestAuthenticator legacy 分支拒签），
        // 逐手机凭据不受影响。一次性操作：无反向开关，重新打开旧入口只能手工删改 data/clients.json。
        app.MapPost("/api/admin/legacy/revoke", (HttpContext context) =>
        {
            if (context.Connection.LocalPort != 8765)
            {
                return Results.NotFound();
            }
            try
            {
                host.Credentials.RevokeLegacy();
            }
            catch (Exception exception)
            {
                // 持久化失败：撤销中止，内存与磁盘保持一致，可安全重试（设计 §6 M-2 处置）。
                Console.Error.WriteLine($"legacy 撤销持久化失败：{exception.Message}");
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
            // 与逐手机撤销同一编排（设计 §6）：持久化成功后终止旧入口的长流与持有键。
            host.Sessions.Cancel(ClientCredentialsStore.LegacySharedClientId);
            host.RevokeInput(ClientCredentialsStore.LegacySharedClientId);
            return Results.Ok(new
            {
                ok = true,
                legacyRevokedAt = host.Credentials.LegacyRevokedAtUtc,
            });
        });

        app.MapGet("/api/admin/pairing/qr", (HttpContext context) =>
        {
            if (context.Connection.LocalPort != 8765)
            {
                return Results.NotFound();
            }
            var status = System.Text.Json.JsonSerializer.SerializeToElement(host.PairingWindows.StatusSnapshot());
            if (!status.TryGetProperty("qrPayload", out var payload) || payload.GetString() is not { Length: > 0 } json)
            {
                return Results.NotFound(new { ok = false, error = "配对窗口未开启" });
            }
            using var data = QRCoder.QRCodeGenerator.GenerateQrCode(json, QRCoder.QRCodeGenerator.ECCLevel.M);
            using var png = new QRCoder.PngByteQRCode(data);
            return Results.File(png.GetGraphic(6), "image/png");
        });

        app.MapGet("/admin/pairing", (HttpContext context) =>
            context.Connection.LocalPort != 8765
                ? Results.NotFound()
                : Results.Content(AdminPage, "text/html; charset=utf-8"));
    }

    /// <summary>本机配对管理页：只经 8765 回环提供，写操作受 LoopbackOriginGuard 同源校验。</summary>
    private const string AdminPage = """
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>手机配对</title><style>
:root{color-scheme:light dark;--bg:#fff;--fg:#111;--muted:#666;--line:#ddd;--accent:#111}
@media (prefers-color-scheme:dark){:root{--bg:#111;--fg:#eee;--muted:#999;--line:#333;--accent:#eee}}
body{margin:0;padding:24px 16px;font:15px/1.5 -apple-system,system-ui,sans-serif;background:var(--bg);color:var(--fg);max-width:560px;margin-inline:auto}
h1{font-size:22px;margin:0 0 4px}p{color:var(--muted);margin:4px 0 16px}button{font:inherit;padding:8px 14px;border-radius:10px;border:1px solid var(--line);background:transparent;color:var(--fg);cursor:pointer}
button.primary{background:var(--accent);color:var(--bg);border-color:var(--accent)}section{border-top:1px solid var(--line);padding:16px 0}
img{width:240px;height:240px;image-rendering:pixelated;background:#fff;padding:8px;border-radius:12px}code{font-size:18px;letter-spacing:1px}
li{display:flex;justify-content:space-between;align-items:center;gap:8px;padding:6px 0}ul{list-style:none;padding:0;margin:0}
</style></head><body>
<h1>手机配对</h1><p>手机和这台电脑连同一个 Wi-Fi 后会自动发现它；手机上点「连接」时，这里和系统弹框都会请你确认，并显示与手机一致的校验码。旧版手机仍可开启下方扫码窗口。每台手机获得独立凭据，可单独撤销。</p>
<section id="window"><button class="primary" id="begin">开启 2 分钟配对窗口</button></section>
<section><h2 style="font-size:17px;margin:0 0 8px">已配对手机</h2><ul id="clients"></ul></section>
<script>
const $=id=>document.getElementById(id);
const post=(path,body)=>fetch(path,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body||{})}).then(r=>r.json().catch(()=>({})));
async function refresh(){
  const s=await fetch('/api/admin/pairing/status').then(r=>r.json());
  const w=$('window');
  if(!s.open){w.innerHTML='<button class="primary" id="begin">开启 2 分钟配对窗口</button>';$('begin').onclick=()=>post('/api/admin/pairing/begin').then(refresh);}
  else if(s.pending){w.innerHTML=`<p>手机「${(s.pending.clientLabel||'未命名').replace(/[<>&"]/g,'')}」请求${s.nearby?'连接':'配对'}，校验码 <code>${s.checkCode}</code></p><button class="primary" id="ok">确认</button> <button id="no">拒绝</button>`;
    $('ok').onclick=()=>post('/api/admin/pairing/confirm',{pairingId:s.pairingId}).then(refresh);$('no').onclick=()=>post('/api/admin/pairing/deny',{pairingId:s.pairingId}).then(refresh);}
  else{w.innerHTML=`<img alt="配对二维码" src="/api/admin/pairing/qr?t=${s.pairingId}"><p>手动码 <code>${s.manualCode}</code> · 剩余 ${s.remainingSeconds} 秒</p><button id="cancel">取消</button>`;
    $('cancel').onclick=()=>post('/api/admin/pairing/cancel').then(refresh);}
  const c=await fetch('/api/admin/clients').then(r=>r.json());
  $('clients').innerHTML='';
  for(const x of c.clients||[]){if(x.revokedAtUtc)continue;const li=document.createElement('li');li.textContent=x.label||x.clientId;
    const b=document.createElement('button');b.textContent='撤销';b.onclick=()=>post(x.legacy?'/api/admin/legacy/revoke':'/api/admin/clients/revoke',{clientId:x.clientId}).then(refresh);li.append(b);$('clients').append(li);}
}
refresh();setInterval(refresh,1500);
</script></body></html>
""";
}
