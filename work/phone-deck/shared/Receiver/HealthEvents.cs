using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// 能力 healthEventsV1：<c>GET /api/events?since=&lt;stateVersion&gt;&amp;timeoutMs=&lt;n&gt;</c> 长轮询。
/// 请求挂起直到与语音会话相关的状态（音频流、听写会话、引擎采集、停止凭据、音频可用性）
/// 相对 since 发生变化，或等到超时；返回与 /api/health 相同的 JSON，并附加 stateVersion。
/// 手机在听写期间据此立即得知电脑端停止，不再每 500 ms 轮询；不支持时回退到 /api/health。
/// 判断变化只读内存快照（每 50 ms 一次），不触发额外的 Core Audio 枚举或文件 IO。
/// </summary>
internal static class HealthEvents
{
    internal const int DefaultTimeoutMs = 8_000;
    internal const int MaxTimeoutMs = 20_000;
    internal const int PollIntervalMs = 50;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>健康对象中决定 stateVersion 的字段（其余诊断字段变化不唤醒长轮询）。</summary>
    internal static string StateVersion(JsonNode health)
    {
        var audio = health["audio"];
        var dictation = health["dictation"];
        var engine = health["voiceEngine"] ?? health["typeless"];
        var material = string.Join("|",
            audio?["available"]?.ToJsonString(), audio?["streaming"]?.ToJsonString(),
            audio?["sessionId"]?.ToJsonString(), audio?["mode"]?.ToJsonString(),
            audio?["stopRequestedSessionId"]?.ToJsonString(),
            dictation?["active"]?.ToJsonString(), dictation?["sessionId"]?.ToJsonString(),
            engine?["capturing"]?.ToJsonString());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16].ToLowerInvariant();
    }

    internal static JsonNode Snapshot(object health)
    {
        if (health is JsonObject built)
        {
            // 原生编译：接收端直接组装 JsonObject（每次新建），就地加上 stateVersion。
            built["stateVersion"] = StateVersion(built);
            return built;
        }
        var node = JsonSerializer.SerializeToNode(health, Json)
            ?? throw new InvalidOperationException("健康快照为空");
        node["stateVersion"] = StateVersion(node);
        return node;
    }

    internal static int ClampTimeout(string? requested) =>
        int.TryParse(requested, out var value) ? Math.Clamp(value, 0, MaxTimeoutMs) : DefaultTimeoutMs;

    /// <summary>等到状态版本不同于 since（或超时/请求取消），返回最新快照。</summary>
    internal static async Task<JsonNode> WaitAsync(
        Func<object> health, string? since, int timeoutMs, CancellationToken cancellation)
    {
        var snapshot = Snapshot(health());
        if (string.IsNullOrEmpty(since) || timeoutMs <= 0
            || !string.Equals(snapshot["stateVersion"]?.GetValue<string>(), since, StringComparison.Ordinal))
        {
            return snapshot;
        }
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !cancellation.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollIntervalMs, cancellation);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            snapshot = Snapshot(health());
            if (!string.Equals(snapshot["stateVersion"]?.GetValue<string>(), since, StringComparison.Ordinal))
            {
                break;
            }
        }
        return snapshot;
    }
}
