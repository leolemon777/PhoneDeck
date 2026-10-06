using System.Text.Json.Serialization;

// 接收端接口应答的具名类型（原生编译不能序列化匿名对象）。字段名经 Web 选项转为 camelCase，
// 与原先的匿名对象逐字一致；可选字段为空时不输出，保持各分支原有的字段集合。

/// <summary>{ok:true} 或 {ok:false, error}。</summary>
internal sealed record ApiResult(
    bool Ok,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null)
{
    internal static readonly ApiResult Success = new(true);
    internal static ApiResult Fail(string? error) => new(false, error);
}

/// <summary>撤销成功 {ok, clientId}。</summary>
internal sealed record ApiClientResult(bool Ok, string ClientId);

/// <summary>扫码 / 附近连接签发的逐手机凭据（platform 仅附近连接返回）。</summary>
internal sealed record PairIssuedResponse(
    bool Ok, string ClientId, string ClientToken, IReadOnlyList<string> Scopes, string PairingId,
    string ComputerId, string DisplayName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Platform,
    string CertificateSha256);

internal sealed record PairingBeginResponse(
    bool Ok, string PairingId, string QrPayload, string ManualCode, string CheckCode, int ValidSeconds);

/// <summary>USB 回环签发旧共享令牌。</summary>
internal sealed record UsbPairResponse(
    bool Ok, int ProtocolVersion, string ComputerId, string DisplayName, string Platform,
    string[] Addresses, int Port, string CertificateSha256, string AccessToken, string UpgradeHint);

/// <summary>rotate 已升级过：冻结为 {ok,status,clientId} 三字段，绝无令牌。</summary>
internal sealed record RotateAlreadyUpgradedResponse(bool Ok, string Status, string ClientId);

/// <summary>rotate 签发：冻结契约 credentialRotateResponse（无 ok 字段）。</summary>
internal sealed record RotateIssuedResponse(
    string Status, string ClientId, string ClientToken, IReadOnlyList<string> Scopes, string PairingId,
    string ComputerId, string DisplayName, string CertificateSha256);

internal sealed record LegacyRevokeResponse(bool Ok, string? LegacyRevokedAt);

internal sealed record PairingPendingInfo(string ClientId, string ClientLabel);

/// <summary>
/// 回环配对状态三种形态：关闭 {ok,open:false}；附近请求 {…,nearby,pendings}；扫码窗口 {…,qrPayload,manualCode,…}。
/// 各分支不用的字段为空、不输出；pending 始终输出（无待确认时为 null，旧客户端据此轮询）。
/// </summary>
internal sealed record PairingStatusSnapshot(
    bool Ok,
    bool Open,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Nearby = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PairingId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? QrPayload = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ManualCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CheckCode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RemainingSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? FailuresRemaining = null,
    PairingPendingInfo? Pending = null);
