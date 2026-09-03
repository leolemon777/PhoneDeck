using System.Text;

/// <summary>
/// 把 Console.WriteLine 同时落到 server-console.log（512KB 自动清空），
/// 用于事后追溯 [audio]/[dictation] 等控制台诊断日志的完整时间线。
/// </summary>
internal sealed class ConsoleTeeWriter : TextWriter
{
    private readonly TextWriter console;
    private readonly string logPath;
    private readonly object logLock = new();

    public ConsoleTeeWriter(string logPath)
    {
        this.console = Console.Out;
        this.logPath = logPath;
    }

    public override Encoding Encoding => console.Encoding;

    public override void Write(char value) => console.Write(value);

    public override void Write(string? value) => console.Write(value);

    public override void WriteLine(string? value)
    {
        console.WriteLine(value);
        try
        {
            lock (logLock)
            {
                const long maxLogBytes = 512 * 1024;
                if (File.Exists(logPath) && new FileInfo(logPath).Length >= maxLogBytes)
                {
                    File.WriteAllText(logPath, string.Empty);
                }
                File.AppendAllText(
                    logPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ")
                    + value + Environment.NewLine);
            }
        }
        catch
        {
            // 日志只用于事后追溯，任何写入失败都不能影响接收端运行。
        }
    }
}
