using System.Diagnostics;

namespace PhoneDeck.MacReceiver;

/// <summary>
/// 同一 Wi-Fi 免扫码连接的本机确认：附近请求到达时用系统对话框询问「允许 / 拒绝」并显示校验码。
/// 手机名称只作为 osascript 参数传入（不拼进脚本），不能注入 AppleScript；确认结果走
/// PairingWindowManager.Confirm/Deny，与 /admin/pairing 管理页共用。请求在别处收束时关闭对话框。
/// </summary>
internal static class MacNearbyPrompt
{
    private static readonly string[] Script =
    {
        "on run argv",
        "set theLabel to item 1 of argv",
        "set theCode to item 2 of argv",
        "set answer to display dialog (\"手机「\" & theLabel & \"」请求连接这台电脑。\" & return & return & \"校验码 \" & theCode & return & \"确认手机上显示的校验码相同，再点「允许」。\") with title \"言渡 · 新手机连接\" buttons {\"拒绝\", \"允许\"} default button \"允许\" giving up after 60 with icon caution",
        "if gave up of answer then return \"timeout\"",
        "return button returned of answer",
        "end run",
    };

    private static readonly object Gate = new();
    private static Process? current;
    private static string? currentPairingId;

    internal static void Attach(PairingWindowManager windows)
    {
        windows.NearbyRequested += (request, code) =>
            Task.Run(() => Show(windows, request.PairingId, request.ClientLabel, code));
        windows.NearbyFinished += Close;
    }

    private static void Show(PairingWindowManager windows, string pairingId, string label, string code)
    {
        var start = new ProcessStartInfo("/usr/bin/osascript")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var line in Script)
        {
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add(line);
        }
        start.ArgumentList.Add(SafeLabel(label));
        start.ArgumentList.Add(code);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return;
            }
            lock (Gate)
            {
                current = process;
                currentPairingId = pairingId;
            }
            var answer = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            lock (Gate)
            {
                if (currentPairingId == pairingId)
                {
                    current = null;
                    currentPairingId = null;
                }
            }
            if (answer == "允许")
            {
                windows.Confirm(pairingId);
            }
            else if (answer == "拒绝")
            {
                windows.Deny(pairingId);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"无法显示连接确认框，请在 http://127.0.0.1:8765/admin/pairing 确认：{exception.Message}");
        }
    }

    private static void Close(string pairingId)
    {
        lock (Gate)
        {
            if (currentPairingId != pairingId || current is null)
            {
                return;
            }
            try
            {
                if (!current.HasExited)
                {
                    current.Kill();
                }
            }
            catch (InvalidOperationException)
            {
                // 对话框已自行关闭。
            }
        }
    }

    /// <summary>去掉控制字符与开头的连字符（避免被 osascript 当成选项），并限制长度。</summary>
    internal static string SafeLabel(string label)
    {
        var cleaned = new string(label.Where(character => !char.IsControl(character)).ToArray()).Trim().TrimStart('-');
        if (cleaned.Length > 40)
        {
            cleaned = cleaned[..40];
        }
        return cleaned.Length == 0 ? "Android 手机" : cleaned;
    }
}
