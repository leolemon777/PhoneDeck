using System.Diagnostics;
using System.Text;
using System.Runtime.Intrinsics.X86;

namespace PhoneDeck.Desktop;

internal interface ISpeechEngine
{
    bool Ready { get; }
    Task<string> TranscribeAsync(byte[] pcm, CancellationToken cancellation);
}

internal sealed class WhisperEngine(ModelAssets assets, string runtimeDirectory, Func<string>? language = null) : ISpeechEngine
{
    private readonly SemaphoreSlim gate = new(1);
    internal string Executable => SelectExecutable(runtimeDirectory);
    internal static string SelectExecutable(string directory)
    {
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var accelerated = Path.Combine(directory, "whisper-cli-avx2" + suffix);
        // MSVC's AVX2 target may also emit FMA, F16C, BMI2 and SSE4.2 instructions.
        // Require the complete feature set, including OS AVX state support, before selecting it.
        if (Avx2.IsSupported && Fma.IsSupported && Bmi2.IsSupported && Sse42.IsSupported
            && (X86Base.CpuId(1, 0).Ecx & (1 << 29)) != 0 && File.Exists(accelerated)) return accelerated;
        return Path.Combine(directory, "whisper-cli" + suffix);
    }
    public bool Ready => assets.Ready && File.Exists(Executable);

    public async Task<string> TranscribeAsync(byte[] pcm, CancellationToken cancellation)
    {
        if (!Ready) throw new InvalidOperationException("请先完成语音模型和运行包准备");
        if (pcm.Length < 9600 || pcm.Length > SpeechSession.MaxBytes || pcm.Length % 2 != 0)
            throw new InvalidDataException("语音长度或格式无效");
        await gate.WaitAsync(cancellation);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var start = new ProcessStartInfo(Executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = runtimeDirectory
            };
            // In v1.9.4 stdin's default output basename '-' suppresses segment callbacks.
            // A fixed non-stdout basename restores text on stdout; with no output-format flags it opens no file.
            foreach (var arg in new[] { "-m", assets.ModelPath, "-f", "-", "-of", "PhoneDeck-memory", "-l", language?.Invoke() ?? "auto", "-nt", "-np", "-ng", "-t", Math.Clamp(Environment.ProcessorCount / 2, 1, 6).ToString() })
                start.ArgumentList.Add(arg);
            using var process = StartProcess(start);
            using var cancel = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var output = ReadLimitedAsync(process.StandardOutput, timeout.Token);
            var errors = ReadLimitedAsync(process.StandardError, timeout.Token);
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(Wave(pcm), timeout.Token);
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                var text = NormalizeTranscript(await output); await errors;
                if (process.ExitCode != 0) throw new IOException("内置识别失败，请重试或检查模型");
                return text;
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            }
        }
        finally { gate.Release(); }
    }
    internal static string NormalizeTranscript(string output)
    {
        // CLI segment boundaries are presentation wrapping, not commands to press Enter.
        // Normalize once before storing/syncing so all computers receive identical text.
        var text = string.Join(' ', output.Split(['\r', '\n', '\t', '\u0085', '\u2028', '\u2029'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > 4096) throw new InvalidDataException("识别内容超过长度限制");
        if (text.Any(char.IsControl)) throw new InvalidDataException("识别结果含无效控制字符，请重试");
        return text;
    }
    private static Process StartProcess(ProcessStartInfo start)
    {
        try { return Process.Start(start) ?? throw new IOException("无法启动内置识别"); }
        catch (System.ComponentModel.Win32Exception e) { throw new IOException("内置识别组件无法运行，请使用完整安装包", e); }
    }

    internal static byte[] Wave(byte[] pcm)
    {
        // whisper.cpp decodes/resamples 48 kHz WAV from stdin in memory. No temporary audio file.
        using var stream = new MemoryStream(44 + pcm.Length);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write("RIFF"u8); writer.Write(36 + pcm.Length); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(48000);
        writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm);
        return stream.ToArray();
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken cancellation)
    {
        var result = new StringBuilder(); var buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer, cancellation)) > 0)
        {
            if (result.Length + count > 65536) throw new IOException("识别输出超过上限");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
