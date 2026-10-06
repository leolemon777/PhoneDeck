using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 凭据文件与配对载荷的编译期 JSON 元数据。单独成文件，供只链接信任层（凭据、配对窗口）的
/// 2.0 桌面版使用，不必带上整个接收端 API 上下文。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<ClientCredentialRecord>))]
[JsonSerializable(typeof(ClientCredentialsFileEnvelope))]
internal sealed partial class CredentialsJsonContext : JsonSerializerContext;

/// <summary>配对载荷沿用 ASP.NET 默认 Web 选项（camelCase）。</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(QrPairingPayload))]
internal sealed partial class PairingPayloadJsonContext : JsonSerializerContext;

/// <summary>旧版手机扫码配对的二维码载荷（字段冻结，见 PairingWindowTests）。</summary>
internal sealed record QrPairingPayload(
    int Version, string ComputerId, string DisplayName, int HttpsPort, string CertificateSha256,
    string PairingId, string OneTimeMaterial, int ValidSeconds, int MaxFailuresPerWindow);
