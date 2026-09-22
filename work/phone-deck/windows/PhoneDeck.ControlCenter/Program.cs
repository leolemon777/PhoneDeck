namespace PhoneDeck.ControlCenter;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "PhoneDeck.ControlCenter.Slim.Singleton", out var first);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, "PhoneDeck.ControlCenter.Slim.Activate");
        if (!first) { activate.Set(); return; }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var tray = new ReceiverTray(args.Contains("--tray"), activate);
        Application.Run(tray);
    }
}
