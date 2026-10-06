using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhoneDeck.MacReceiver;

/// <summary>局域网发现 UDP 应答（字段与 Windows、mDNS TXT 同一集合，不含令牌/指纹）。</summary>
internal sealed record LanDiscoveryReply(
    bool Ok, string Service, int ProtocolVersion, string ComputerId, string DisplayName,
    int Port, string Platform, IReadOnlyCollection<string> Capabilities);

internal sealed record AudioStreamResult(bool Ok, string SessionId, string Mode);
internal sealed record DictationResult(bool Ok, bool Duplicate, bool Active, string? SessionId);
internal sealed record InputResult(bool Ok, bool Duplicate, string? RequestId, string ComputerId, string Message, string? Text);

/// <summary>手机「多电脑配置」里的连接设置快照。</summary>
internal sealed record MacConnectionState(bool UsbWatchdog, bool LanDiscovery, bool AutoStart);

/// <summary>读取 server-settings.json / 引擎档案 / 引擎设置：大小写不敏感、允许注释与尾逗号（与旧选项一致）。</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(MacReceiverSettings))]
[JsonSerializable(typeof(MacVoiceEngineProfile))]
[JsonSerializable(typeof(MacVoiceEngineSettings))]
internal sealed partial class MacFileReadJsonContext : JsonSerializerContext;

/// <summary>写回设置文件：缩进、默认命名（与旧 WriteAtomic 输出一致）。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(MacReceiverSettings))]
[JsonSerializable(typeof(MacVoiceEngineSettings))]
internal sealed partial class MacFileWriteJsonContext : JsonSerializerContext;

/// <summary>Mac 接口请求体与应答（Web 选项：camelCase）。</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(InputCommand))]
[JsonSerializable(typeof(DictationCommand))]
[JsonSerializable(typeof(LanDiscoveryReply))]
[JsonSerializable(typeof(MacConnectionState))]
[JsonSerializable(typeof(AudioStreamResult))]
[JsonSerializable(typeof(DictationResult))]
[JsonSerializable(typeof(InputResult))]
internal sealed partial class MacApiJsonContext : JsonSerializerContext;
