/// <summary>
/// Receiver.Core：原生手机的逐手机凭据与配对接口，Windows 与 macOS 接收端共用。
/// - 8766 /api/lan/pair/qr：一次性材料 + 本机确认后签发独立凭据（Bearer）；
/// - 8765 /api/lan/pair：USB 回环签发旧共享令牌（旧入口撤销后 410）；
/// - 8766 /api/lan/credential/rotate：旧共享令牌升级为逐手机凭据；
/// - 8765 /api/admin/*：开启/确认/拒绝配对、列出与撤销手机、应急撤销旧入口；
/// - 8765 /admin/pairing：本机状态页（连接状态、检查清单、新手机确认、撤销），见 ReceiverStatusPage。
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
    Action<string> RevokeInput)
{
    /// <summary>最近来过请求的手机（本机状态页、托盘显示用）。</summary>
    internal PhonePresence Presence { get; init; } = new();
}

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
                    return Results.NotFound(ApiResult.Fail("配对窗口未开启"));
                case PairingSubmitStatus.MaterialInvalid:
                    return Results.Json(ApiResult.Fail("配对材料无效或已使用"), ReceiverApiJsonContext.Default.ApiResult, statusCode: StatusCodes.Status401Unauthorized);
                case PairingSubmitStatus.FailureLimit:
                    return Results.Json(ApiResult.Fail("失败次数过多，请在电脑上重新开启配对"), ReceiverApiJsonContext.Default.ApiResult, statusCode: StatusCodes.Status429TooManyRequests);
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
                return Results.Json(ApiResult.Fail("本机确认超时，请重试"), ReceiverApiJsonContext.Default.ApiResult, statusCode: StatusCodes.Status408RequestTimeout);
            }
            if (!confirmed)
            {
                return Results.Json(ApiResult.Fail("电脑端已拒绝本次配对"), ReceiverApiJsonContext.Default.ApiResult, statusCode: StatusCodes.Status403Forbidden);
            }
            var record = host.Credentials.Issue(
                pending.ClientLabel,
                new[] { "control", "audio", "settings", "update-request" },
                pending.PairingId,
                out var clientToken,
                pending.ClientId);
            Console.WriteLine($"配对完成：clientId={record.ClientId} pairingId={pending.PairingId}（材料与令牌不落日志）");
            return Results.Ok(new PairIssuedResponse(true, record.ClientId, clientToken, record.Scopes,
                pending.PairingId, host.ComputerId, host.DisplayName(), null, host.CertificateSha256));
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
                    return Results.BadRequest(ApiResult.Fail("请求格式无效"));
                case NearbyRequestStatus.Busy:
                    return Results.Json(ApiResult.Fail("电脑正在确认另一台手机，请稍后再试"), ReceiverApiJsonContext.Default.ApiResult, statusCode: StatusCodes.Status409Conflict);
                case NearbyRequestStatus.RateLimited:
                    return Results.Json(ApiResult.Fail("请求过于频繁，请稍后再试"), ReceiverApiJsonContext.Default.ApiResult, statusCode: StatusCodes.Status429TooManyRequests);
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
                return Results.Json(ApiResult.Fail("电脑上没有人确认，请重试"), ReceiverApiJsonContext.Default.ApiResult, statusCode: StatusCodes.Status408RequestTimeout);
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
                return Results.Json(ApiResult.Fail("电脑端已拒绝本次连接"), ReceiverApiJsonContext.Default.ApiResult, statusCode: StatusCodes.Status403Forbidden);
            }
            var record = host.Credentials.Issue(
                pending.ClientLabel,
                new[] { "control", "audio", "settings", "update-request" },
                pending.PairingId,
                out var clientToken,
                pending.ClientId);
            Console.WriteLine($"附近连接完成：clientId={record.ClientId}（令牌不落日志）");
            return Results.Ok(new PairIssuedResponse(true, record.ClientId, clientToken, record.Scopes,
                pending.PairingId, host.ComputerId, host.DisplayName(), host.Platform, host.CertificateSha256));
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
                return Results.Conflict(ApiResult.Fail("有待确认的配对提交"));
            }
            var session = host.PairingWindows.Begin();
            return Results.Ok(new PairingBeginResponse(true, session.PairingId, session.QrPayloadJson,
                session.ManualCode, session.MaterialCheckCode, PairingWindowManager.ValidSeconds));
        });
        app.MapPost("/api/admin/pairing/cancel", (HttpContext context) =>
        {
            if (context.Connection.LocalPort != 8765)
            {
                return Results.NotFound();
            }
            host.PairingWindows.Cancel();
            return Results.Ok(ApiResult.Success);
        });
        app.MapPost("/api/admin/pairing/confirm", (HttpContext context, PairingAdminRequest body) =>
            context.Connection.LocalPort != 8765
                ? Results.NotFound()
                : host.PairingWindows.Confirm(body.PairingId)
                    ? Results.Ok(ApiResult.Success)
                    : Results.NotFound(ApiResult.Fail("没有待确认的配对")));
        app.MapPost("/api/admin/pairing/deny", (HttpContext context, PairingAdminRequest body) =>
            context.Connection.LocalPort != 8765
                ? Results.NotFound()
                : host.PairingWindows.Deny(body.PairingId)
                    ? Results.Ok(ApiResult.Success)
                    : Results.NotFound(ApiResult.Fail("没有待确认的配对")));

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
                return Results.Json(ApiResult.Fail("旧共享令牌入口已关闭，请在电脑上扫码配对获取独立凭据"), ReceiverApiJsonContext.Default.ApiResult, statusCode: StatusCodes.Status410Gone);
            }
            // 设计 §5.6/§5.2：提示旧共享令牌可在手机端升级为独立凭据，无需重新扫码。
            return Results.Ok(new UsbPairResponse(true, 2, host.ComputerId, host.DisplayName(), host.Platform,
                host.CandidateAddresses(), host.HttpsPort, host.CertificateSha256, host.SharedAccessToken,
                "共享令牌仅供旧版本使用；升级手机 App 后可用它换领该手机独立凭据，无需重新扫码"));
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
                return Results.Json(ApiResult.Fail("请使用旧共享令牌升级"), ReceiverApiJsonContext.Default.ApiResult, statusCode: StatusCodes.Status403Forbidden);
            }
            var clientId = body.ClientId?.Trim();
            if (string.IsNullOrWhiteSpace(clientId) || !Guid.TryParse(clientId, out _))
            {
                return Results.BadRequest(ApiResult.Fail("clientId 必须为手机生成的 GUID"));
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
                    return Results.Json(ApiResult.Fail("该 clientId 已撤销，凭据找回需重新扫码配对"), ReceiverApiJsonContext.Default.ApiResult, statusCode: StatusCodes.Status403Forbidden);
                case RotateOutcome.RateLimited:
                    return Results.Json(ApiResult.Fail("rotate 请求过于频繁，请稍后重试"), ReceiverApiJsonContext.Default.ApiResult, statusCode: StatusCodes.Status429TooManyRequests);
                case RotateOutcome.AlreadyUpgraded:
                    // G-1 处置：该形态仅 {ok,status,clientId} 三字段，绝无令牌本体。
                    return Results.Ok(new RotateAlreadyUpgradedResponse(true, "already-upgraded", rotation.Record!.ClientId));
                default:
                    var record = rotation.Record!;
                    // issued 形态逐字对齐冻结契约 credentialRotateResponse（additionalProperties:false，
                    // 属性表无 ok 字段）；already-upgraded 形态则冻结为 {ok,status,clientId} 三字段。
                    return Results.Ok(new RotateIssuedResponse("issued", record.ClientId, rotation.Token!,
                        record.Scopes, record.PairingId, host.ComputerId, host.DisplayName(), host.CertificateSha256));
            }
        });

        // M1-A A1/A4 回环管理端点（设计 §3 不变量：撤销属本机信任操作；仅 8765 回环可达，无鉴权面扩大）。
        app.MapGet("/api/admin/clients", (HttpContext context) =>
        {
            if (context.Connection.LocalPort != 8765)
            {
                return Results.NotFound();
            }
            // 记录与合成的 legacy 条目字段不同，用 JsonObject 组装（原生编译可序列化）。
            var clients = new System.Text.Json.Nodes.JsonArray();
            foreach (var record in host.Credentials.ListRedacted())
            {
                clients.Add(System.Text.Json.JsonSerializer.SerializeToNode(
                    record, ReceiverApiJsonContext.Default.ClientCredentialRecord));
            }
            if (!host.Credentials.LegacyRevoked)
            {
                // 设计 §5.3（M-4 处置）：legacy 未撤销时附加合成条目，显式列出旧入口，
                // 供托盘"应急撤销旧入口"定位；撤销后该条目消失（入口已关闭）。
                clients.Add((System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                {
                    ["clientId"] = ClientCredentialsStore.LegacySharedClientId,
                    ["label"] = "未升级旧凭据（legacy 入口）",
                    ["legacy"] = true,
                });
            }
            return Results.Ok(new System.Text.Json.Nodes.JsonObject
            {
                ["ok"] = true,
                ["clients"] = clients,
                ["legacyRevokedAt"] = host.Credentials.LegacyRevokedAtUtc,
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
                return Results.BadRequest(ApiResult.Fail("缺少 clientId"));
            }
            try
            {
                if (!host.Credentials.Revoke(clientId))
                {
                    return Results.NotFound(ApiResult.Fail("未知 clientId"));
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
            return Results.Ok(new ApiClientResult(true, clientId));
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
            return Results.Ok(new LegacyRevokeResponse(true, host.Credentials.LegacyRevokedAtUtc));
        });

#if !PHONEDECK_LITE
        // 旧版手机扫码用的二维码图片；原生精简版（PHONEDECK_LITE）不含 QRCoder，管理页仍显示手动码。
        app.MapGet("/api/admin/pairing/qr", (HttpContext context) =>
        {
            if (context.Connection.LocalPort != 8765)
            {
                return Results.NotFound();
            }
            if (host.PairingWindows.StatusSnapshot().QrPayload is not { Length: > 0 } json)
            {
                return Results.NotFound(ApiResult.Fail("配对窗口未开启"));
            }
            using var data = QRCoder.QRCodeGenerator.GenerateQrCode(json, QRCoder.QRCodeGenerator.ECCLevel.M);
            using var png = new QRCoder.PngByteQRCode(data);
            return Results.File(png.GetGraphic(6), "image/png");
        });

#endif
        // 本机状态页：最近来过请求的手机（仅 8765 回环）。
        app.MapGet("/api/admin/presence", (HttpContext context) =>
        {
            if (context.Connection.LocalPort != 8765)
            {
                return Results.NotFound();
            }
            var labels = host.Credentials.ListRedacted()
                .Where(record => record.RevokedAtUtc is null)
                .ToDictionary(record => record.ClientId, record => record.Label, StringComparer.Ordinal);
            return Results.Ok(new System.Text.Json.Nodes.JsonObject
            {
                ["ok"] = true,
                ["phones"] = host.Presence.Snapshot(clientId =>
                    clientId is not null && labels.TryGetValue(clientId, out var label) ? label : null),
            });
        });

        app.MapGet("/admin/pairing", (HttpContext context) =>
            context.Connection.LocalPort != 8765
                ? Results.NotFound()
                : Results.Content(ReceiverStatusPage.Html, "text/html; charset=utf-8"));
#if PHONEDECK_LITE
        // 精简版没有 iPhone 浏览器网关占用根路径：本机打开 http://127.0.0.1:8765/ 直接到状态页。
        app.MapGet("/", (HttpContext context) =>
            context.Connection.LocalPort != 8765 ? Results.NotFound() : Results.Redirect("/admin/pairing"));
#endif
    }
}
