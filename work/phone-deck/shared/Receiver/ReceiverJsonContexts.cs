using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 原生编译（Native AOT）用的编译期 JSON 元数据。各上下文的选项与原先运行时 JsonSerializerOptions
/// 一一对应，文件格式与接口字段不变；JIT 构建同样使用，避免两条路径行为不一致。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<ClientCredentialRecord>))]
[JsonSerializable(typeof(ClientCredentialsFileEnvelope))]
internal sealed partial class CredentialsJsonContext : JsonSerializerContext;

/// <summary>手机请求体（ASP.NET 默认 Web 选项：camelCase、大小写不敏感）。</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(QrPairRequest))]
[JsonSerializable(typeof(NearbyPairRequest))]
[JsonSerializable(typeof(PairingAdminRequest))]
[JsonSerializable(typeof(RotateRequest))]
[JsonSerializable(typeof(ClientRevokeRequest))]
[JsonSerializable(typeof(DesktopVoiceRequest))]
[JsonSerializable(typeof(DesktopConnectionRequest))]
[JsonSerializable(typeof(DesktopTargetRequest))]
[JsonSerializable(typeof(QrPairingPayload))]
[JsonSerializable(typeof(ApiResult))]
[JsonSerializable(typeof(ApiClientResult))]
[JsonSerializable(typeof(PairIssuedResponse))]
[JsonSerializable(typeof(PairingBeginResponse))]
[JsonSerializable(typeof(UsbPairResponse))]
[JsonSerializable(typeof(RotateAlreadyUpgradedResponse))]
[JsonSerializable(typeof(RotateIssuedResponse))]
[JsonSerializable(typeof(LegacyRevokeResponse))]
[JsonSerializable(typeof(PairingStatusSnapshot))]
[JsonSerializable(typeof(ClientCredentialRecord))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
[JsonSerializable(typeof(System.Text.Json.Nodes.JsonNode))]
internal sealed partial class ReceiverApiJsonContext : JsonSerializerContext;

/// <summary>旧版手机扫码配对的二维码载荷（字段冻结，见 PairingWindowTests）。</summary>
internal sealed record QrPairingPayload(
    int Version, string ComputerId, string DisplayName, int HttpsPort, string CertificateSha256,
    string PairingId, string OneTimeMaterial, int ValidSeconds, int MaxFailuresPerWindow);
