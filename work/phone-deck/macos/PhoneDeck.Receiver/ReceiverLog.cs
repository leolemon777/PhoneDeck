using System.Text;

namespace PhoneDeck.MacReceiver;

/// <summary>
/// 把控制台输出同时写入 ~/Library/Logs/PhoneDeck/receiver.log（逐行时间戳，超过上限轮换一份旧文件）。
/// launchd 启动时标准输出被丢弃，语音失败后没有任何记录可查；文件写入失败只停用文件，不影响接收端。
/// </summary>
internal sealed class ReceiverLog : TextWriter
{
    internal const long MaxBytes = 2 * 1024 * 1024;
    private readonly TextWriter console;
    private readonly string path;
    private readonly long maxBytes;
    private readonly object gate = new();
    private readonly StringBuilder line = new();
    private FileStream? file;

    internal ReceiverLog(TextWriter console, string path, long maxBytes = MaxBytes)
    {
        this.console = console;
        this.path = path;
        this.maxBytes = maxBytes;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        file = Open();
    }

    internal string FilePath => path;

    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>为标准输出与错误输出安装日志；失败时保持原控制台不变。</summary>
    internal static void Install()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Logs", "PhoneDeck");
            Console.SetOut(new ReceiverLog(Console.Out, Path.Combine(directory, "receiver.log")));
            Console.SetError(new ReceiverLog(Console.Error, Path.Combine(directory, "receiver-error.log")));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"无法写入接收端日志：{exception.Message}");
        }
    }

    public override void Write(char value)
    {
        lock (gate)
        {
            console.Write(value);
            Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }
        lock (gate)
        {
            console.Write(value);
            foreach (var character in value)
            {
                Append(character);
            }
        }
    }

    public override void Write(char[] buffer, int index, int count) =>
        Write(new string(buffer, index, count));

    public override void Flush()
    {
        lock (gate)
        {
            console.Flush();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (gate)
            {
                file?.Dispose();
                file = null;
            }
        }
        base.Dispose(disposing);
    }

    private void Append(char character)
    {
        if (character == '\r')
        {
            return;
        }
        if (character != '\n')
        {
            line.Append(character);
            return;
        }
        AppendToFile(line.ToString());
        line.Clear();
    }

    private void AppendToFile(string text)
    {
        if (file is null)
        {
            return;
        }
        try
        {
            var bytes = Encoding.UTF8.GetBytes(
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {text}\n");
            if (file.Length + bytes.Length > maxBytes)
            {
                Rotate();
            }
            file!.Write(bytes);
            file.Flush();
        }
        catch (IOException)
        {
            file?.Dispose();
            file = null;
        }
        catch (UnauthorizedAccessException)
        {
            file?.Dispose();
            file = null;
        }
    }

    private void Rotate()
    {
        file?.Dispose();
        File.Move(path, Path.ChangeExtension(path, ".1.log"), overwrite: true);
        file = Open();
    }

    private FileStream Open() => new(
        path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
}
