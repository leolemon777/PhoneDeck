using System.Security.Cryptography;
using System.Text.Json;

namespace PhoneDeck.Desktop;

internal sealed class ModelAssets
{
    // Multilingual model. Exact LFS hash/size verified against upstream on 2026-09-30.
    internal const string FileName = "ggml-small-q5_1.bin";
    internal const string Sha256 = "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb";
    internal const long Size = 190085487;
    internal const string DownloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1/ggml-small-q5_1.bin";
    private readonly SemaphoreSlim gate = new(1);
    private readonly string directory;
    private readonly string? bundledDirectory;
    private string? verifiedPath;
    private volatile string state = "missing";
    private long downloaded;
    private string? error;
    internal ModelAssets(string directory, string? bundledDirectory = null) { this.directory = directory; this.bundledDirectory = bundledDirectory; }
    private string DownloadPath => Path.Combine(directory, FileName);
    internal string ModelPath => verifiedPath ?? DownloadPath;
    internal bool Ready => state == "ready";
    internal object Snapshot => new { state, downloaded = Interlocked.Read(ref downloaded), total = Size, error };

    internal async Task VerifyAsync(CancellationToken cancellation = default)
    {
        await gate.WaitAsync(cancellation);
        try
        {
            if (Ready) return;
            state = "checking";
            var bundled = bundledDirectory is null ? null : Path.Combine(bundledDirectory, FileName);
            if (File.Exists(DownloadPath) && await MatchesAsync(DownloadPath, cancellation)) { verifiedPath = DownloadPath; state = "ready"; }
            else if (bundled is not null && File.Exists(bundled) && await MatchesAsync(bundled, cancellation)) { verifiedPath = bundled; state = "ready"; }
            else state = "missing";
        }
        catch (OperationCanceledException) { state = "missing"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { state = "failed"; error = "模型无法读取，请检查存储权限或重新下载"; }
        finally { gate.Release(); }
    }

    internal async Task DownloadAsync(CancellationToken cancellation)
    {
        if (!await gate.WaitAsync(0, cancellation)) return;
        var partial = DownloadPath + ".partial";
        try
        {
            if (Ready) return;
            Directory.CreateDirectory(directory);
            PrivateFiles.RestrictDirectory(directory);
            error = null; state = "downloading"; Interlocked.Exchange(ref downloaded, 0);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != Size)
                throw new InvalidDataException("模型大小与固定版本不一致");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                PrivateFiles.RestrictFile(partial);
                var buffer = new byte[65536];
                int count;
                while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
                {
                    if (Interlocked.Add(ref downloaded, count) > Size) throw new InvalidDataException("模型下载超过大小上限");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                }
            }
            state = "checking";
            if (!await MatchesAsync(partial, cancellation)) throw new InvalidDataException("模型校验失败，请重新下载");
            File.Move(partial, DownloadPath, true); PrivateFiles.RestrictFile(DownloadPath); verifiedPath = DownloadPath; state = "ready";
        }
        catch (OperationCanceledException) { state = "missing"; }
        catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException)
        { state = "failed"; error = e is InvalidDataException ? e.Message : "下载未完成，请检查网络或可用空间后重试"; }
        finally { try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { } gate.Release(); }
    }

    internal static async Task<bool> MatchesAsync(string path, CancellationToken cancellation)
    {
        if (new FileInfo(path).Length != Size) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)).Equals(Sha256, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class PrivateFiles
{
    internal static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    internal static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
