using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;

/// <summary>
/// M0-B 跨语言契约消费：contracts/ 的同一批样本驱动 Windows 接收端真实校验器。
/// 覆盖：全部样本严格 JSON 解析；合法请求样本通过信封校验；非法信封样本被拒绝；
/// 协议头 0 的“记录在案的契约收紧差异”按当前接收端语义放行；宏样本与真实宏校验一致。
/// 样本路径由测试向上查找仓库根的 contracts/ 目录，任何样本增删都直接改变本测试输入。
/// </summary>
[TestClass]
public sealed class ContractsConformanceTests
{
    private static string ContractsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "contracts");
            if (Directory.Exists(Path.Combine(candidate, "samples", "valid")))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new AssertFailedException(
            "未找到 contracts/ 目录（从 " + AppContext.BaseDirectory + " 向上查找）");
    }

    private static byte[] LoadSampleBytes(string folder, string name)
    {
        return File.ReadAllBytes(Path.Combine(ContractsRoot(), "samples", folder, name));
    }

    private static string[] SampleFiles(string folder)
    {
        return Directory.GetFiles(
            Path.Combine(ContractsRoot(), "samples", folder),
            "*.json",
            SearchOption.TopDirectoryOnly);
    }

    private static string[] ValidRequestSamples()
    {
        return SampleFiles("valid")
            .Where(path =>
                Path.GetFileName(path).StartsWith("request-confirmation-input-",
                    StringComparison.Ordinal)
                || Path.GetFileName(path).StartsWith("request-confirmation-dictation-",
                    StringComparison.Ordinal))
            .ToArray();
    }

    private static string GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? GetInt32(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
    }

    private static void ValidateEnvelope(JsonElement root)
    {
        TargetEnvelopeValidator.Validate(
            GetInt32(root, "protocolVersion"),
            GetString(root, "requestId"),
            GetString(root, "sessionId"),
            GetString(root, "targetComputerId"),
            GetString(root, "targetComputerId") ?? "computer-placeholder-01");
    }

    [TestMethod]
    public void AllContractSamplesParseAsStrictJson()
    {
        var valid = SampleFiles("valid");
        var invalid = SampleFiles("invalid");
        Assert.IsTrue(valid.Length >= 20, "valid 样本数量异常：" + valid.Length);
        Assert.IsTrue(invalid.Length >= 30, "invalid 样本数量异常：" + invalid.Length);
        foreach (var path in valid.Concat(invalid))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            Assert.AreEqual(JsonValueKind.Object, document.RootElement.ValueKind,
                Path.GetFileName(path) + " 应为 JSON 对象");
        }
    }

    [TestMethod]
    public void ValidRequestSamplesPassEnvelopeValidation()
    {
        var samples = ValidRequestSamples();
        Assert.IsTrue(samples.Length >= 6, "请求样本数量异常：" + samples.Length);
        foreach (var path in samples)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            ValidateEnvelope(document.RootElement);
        }
    }

    [TestMethod]
    public void InvalidEnvelopeSamplesAreRejected()
    {
        var rejected = new[]
        {
            "input-missing-request-id.json",
            "input-future-protocol-version.json",
            "input-empty-target-computer-id.json",
            "dictation-missing-session-id.json",
        };
        foreach (var name in rejected)
        {
            using var document = JsonDocument.Parse(LoadSampleBytes("invalid", name));
            try
            {
                ValidateEnvelope(document.RootElement);
                Assert.Fail(name + " 应被信封校验拒绝");
            }
            catch (ArgumentException)
            {
                // 预期：非法信封被当前接收端拒绝。
            }
        }
    }

    private static string? Header(JsonElement root, string name)
    {
        return root.GetProperty("headers").EnumerateObject()
            .Where(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Value.GetString())
            .FirstOrDefault();
    }

    /// <summary>
    /// 与 Program.cs 音频流端点一致的最小校验链：协议头解析 + 目标校验（真实校验器）
    /// + 会话头 GUID 规则。端点本身是内联 lambda，无法直接单测，故此处复用其校验调用序列。
    /// </summary>
    private static void ValidateAudioStreamHeaders(JsonElement root, string receiverComputerId)
    {
        var protocolHeader = Header(root, "X-PhoneDeck-Protocol");
        int? protocolVersion = null;
        if (!string.IsNullOrWhiteSpace(protocolHeader))
        {
            if (!int.TryParse(protocolHeader, out var parsed))
            {
                throw new ArgumentException("无效的 protocolVersion");
            }
            protocolVersion = parsed;
        }
        TargetEnvelopeValidator.ValidateProtocolAndTarget(
            protocolVersion,
            Header(root, "X-PhoneDeck-Computer-Id"),
            receiverComputerId);
        var sessionId = Header(root, "X-PhoneDeck-Session");
        if (!string.IsNullOrWhiteSpace(sessionId)
            && (sessionId.Length > 128 || !Guid.TryParse(sessionId, out _)))
        {
            throw new ArgumentException("无效的音频 sessionId");
        }
    }

    [TestMethod]
    public void AudioStreamHeaderSamplesMatchReceiverChain()
    {
        // 合法音频流样本通过完整校验链。
        foreach (var name in new[] { "audio-stream-managed.json", "audio-stream-shared.json" })
        {
            using var valid = JsonDocument.Parse(LoadSampleBytes("valid", name));
            ValidateAudioStreamHeaders(valid.RootElement, "computer-placeholder-01");
        }
        // 会话头非 GUID：被会话规则拒绝。
        using var notGuid = JsonDocument.Parse(
            LoadSampleBytes("invalid", "audio-stream-session-id-not-guid.json"));
        try
        {
            ValidateAudioStreamHeaders(notGuid.RootElement, "computer-placeholder-01");
            Assert.Fail("audio-stream-session-id-not-guid.json 应被音频流校验链拒绝");
        }
        catch (ArgumentException)
        {
            // 预期。
        }
        // 协议头 0：契约目标态收紧为 1–2，当前接收端按遗留语义放行。
        // 本用例固化该“记录在案的契约收紧差异”，接收端实现收紧时必须同步更新。
        using var protocolZero = JsonDocument.Parse(
            LoadSampleBytes("invalid", "audio-stream-protocol-header-zero.json"));
        ValidateAudioStreamHeaders(protocolZero.RootElement, "computer-placeholder-01");
    }

    private static MacroStep[] ReadMacroSteps(JsonElement root)
    {
        return root.GetProperty("steps").EnumerateArray().Select(step => new MacroStep(
            GetString(step, "type"),
            step.TryGetProperty("keys", out var keys)
                ? keys.EnumerateArray().Select(key => key.GetString()).ToArray()
                : null,
            GetInt32(step, "holdMs"),
            GetString(step, "text"),
            step.TryGetProperty("submit", out var submit)
                ? submit.ValueKind == JsonValueKind.True
                : null,
            GetInt32(step, "delayBeforeMs"))).ToArray();
    }

    [TestMethod]
    public void ValidMacroSampleMatchesRealValidator()
    {
        using var document = JsonDocument.Parse(
            LoadSampleBytes("valid", "request-confirmation-input-macro.json"));
        KeyboardInput.ValidateMacroSteps(ReadMacroSteps(document.RootElement));
    }

    [TestMethod]
    public void InvalidMacroSamplesAreRejected()
    {
        var rejected = new[]
        {
            "input-macro-nine-steps.json",
            "input-macro-step-delay-above-range.json",
            "input-macro-step-unknown-type.json",
            "input-macro-step-empty-text.json",
        };
        foreach (var name in rejected)
        {
            using var document = JsonDocument.Parse(LoadSampleBytes("invalid", name));
            try
            {
                KeyboardInput.ValidateMacroSteps(ReadMacroSteps(document.RootElement));
                Assert.Fail(name + " 应被宏校验拒绝");
            }
            catch (ArgumentException)
            {
                // 预期：越界宏被当前接收端拒绝。
            }
        }
    }
}
