using System.Diagnostics;
using PhoneDeck.Desktop;

if (args.Contains("--toggle") || args.Contains("--start") || args.Contains("--stop"))
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    var action = args.Contains("--toggle") ? "toggle" : args.Contains("--start") ? "start" : "stop";
    try { using var result = await http.PostAsync("http://127.0.0.1:8765/local/dictation/" + action, null); result.EnsureSuccessStatusCode(); }
    catch (HttpRequestException) { Console.Error.WriteLine("请先启动 PhoneDeck Desktop，并在手机开启共享麦克风"); Environment.ExitCode = 1; }
    return;
}

try
{
    var app = DesktopApp.Create(args);
    await app.StartAsync();
    if (!args.Contains("--no-browser"))
    {
        try { Process.Start(new ProcessStartInfo("http://127.0.0.1:8765") { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Console.WriteLine("请打开 http://127.0.0.1:8765 完成配对与模型准备"); }
    }
    await app.WaitForShutdownAsync();
    await app.DisposeAsync();
}
catch (Exception e) when (e is IOException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
{
    Console.Error.WriteLine("启动未完成：请检查是否已有 PhoneDeck 接收端运行，以及数据目录是否可写。");
    Environment.ExitCode = 1;
}
