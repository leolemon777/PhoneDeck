using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Runs the real visual tree with startup service management detached. It never
// starts/stops the receiver, sends input, saves settings, or toggles recording.
internal static class ControlCenterProbe
{
    private static readonly List<object> samples = new();
    private static readonly List<double> restores = new();
    private static Type windowType = null!;
    private static Window window = null!;
    private static string output = null!;
    private static long started = Stopwatch.GetTimestamp();
    private static bool verify;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    public static void Main(string[] args)
    {
        output = Path.GetFullPath(args[0]);
        verify = args.Length > 1 && args[1] == "--verify";
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("PHONEDECK_DATA_DIR", Path.Combine(output, "fixture-data"));
        var assembly = Assembly.GetExecutingAssembly();
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PhoneDeck.ControlCenter;component/Themes/Generic.xaml")
        });
        windowType = assembly.GetType("PhoneDeck.ControlCenter.MainWindow", throwOnError: true)!;
        window = (Window)Activator.CreateInstance(windowType)!;
        var loaded = windowType.GetMethod("MainWindow_Loaded", Private)!;
        window.Loaded -= (RoutedEventHandler)loaded.CreateDelegate(typeof(RoutedEventHandler), window);
        window.ContentRendered += Run;
        app.Run(window);
    }

    private static object? Invoke(string name, params object[] args) =>
        windowType.GetMethod(name, Private)!.Invoke(window, args);

    private static DispatcherTimer Timer => (DispatcherTimer)windowType.GetField("refreshTimer", Private)!.GetValue(window)!;

    private static void Sample(string phase)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        samples.Add(new
        {
            phase, elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            workingSet = process.WorkingSet64, privateBytes = process.PrivateMemorySize64,
            cpuMs = process.TotalProcessorTime.TotalMilliseconds, handles = process.HandleCount,
            threads = process.Threads.Count, allocated = GC.GetTotalAllocatedBytes(),
            managedBytes = GC.GetTotalMemory(false), timerEnabled = Timer.IsEnabled
        });
    }

    private static async void Run(object? sender, EventArgs e)
    {
        window.ContentRendered -= Run;
        // A probe must not take over the user's global microphone hotkey when
        // the ordinary control center is not running.
        windowType.GetMethod("UnregisterHotKey", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { new WindowInteropHelper(window).Handle, 0x504D });
        var firstFrameMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        string? error = null;
        try
        {
            // Only read-only health/config APIs. No startup service management.
            await (Task)Invoke("RefreshStatusAsync")!;
            await (Task)Invoke("LoadEngineConfigAsync")!;
            Timer.Start();
            await Task.Delay(5000);
            Sample("visible");
            window.Close(); // The real close-to-tray handler cancels closing.
            await Task.Delay(1000);
            Sample("hidden-start");
            await Task.Delay(11000);
            Sample("hidden-end");
            if (verify) Require(!Timer.IsEnabled, "Hidden UI must stop polling");
            for (int i = 0; i < 10; i++)
            {
                var watch = Stopwatch.StartNew();
                Invoke("RestoreFromTray");
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                restores.Add(watch.Elapsed.TotalMilliseconds);
                await Task.Delay(250);
                window.Hide();
                await Task.Delay(100);
            }
            Invoke("RestoreFromTray");
            await Task.Delay(3000);
            Sample("restored");
            if (verify) Require(Timer.IsEnabled, "Restored UI must resume polling");
            window.WindowState = WindowState.Minimized;
            await Task.Delay(1000);
            Sample("minimized");
            if (verify) Require(!Timer.IsEnabled, "Minimized UI must stop polling");
            window.WindowState = WindowState.Normal;
            Invoke("ShowPage", "settings");
            await Task.Delay(500);
            Capture("settings.png");
            Invoke("ShowPage", "overview");
            await Task.Delay(500);
            Capture("overview.png");
            // Exercise the remaining pages and editor without saving or actions.
            foreach (var page in new[] { "connection", "devices", "logs" })
                Invoke("ShowPage", page);
            var editorType = windowType.Assembly.GetType("PhoneDeck.ControlCenter.AgentShortcutEditorWindow")!;
            var editor = (Window)Activator.CreateInstance(editorType, Path.Combine(output, "fixture-data"))!;
            editor.Owner = window;
            editor.Show();
            await Task.Delay(200);
            editor.Close();
            if (verify) await VerifyBehavior();
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            Environment.ExitCode = 1;
        }
        finally
        {
            File.WriteAllText(Path.Combine(output, "metrics.json"), JsonSerializer.Serialize(
                new { firstFrameMs, restores, samples, verified = verify && error is null, error }, new JsonSerializerOptions { WriteIndented = true }));
            Invoke("ExitApplication");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task VerifyBehavior()
    {
        window.Hide();
        // Replace HTTP before invoking any hotkey: these requests never leave
        // memory and cannot open the real phone microphone.
        var field = windowType.GetField("http", Private)!;
        var original = (HttpClient)field.GetValue(window)!;
        using var handler = new SharedRequestHandler();
        using var client = new HttpClient(handler);
        field.SetValue(window, client);
        Require(ReferenceEquals(field.GetValue(window), client), "HTTP fixture injection failed");
        try
        {
            handler.Requested = true;
            windowType.GetField("lastSharedRequested", Private)!.SetValue(window, false);
            await (Task)Invoke("ToggleSharedLinkAsync")!;
            Require(handler.Posts == 1 && !handler.Requested, "Hotkey must use fresh server state");
            await (Task)Invoke("ToggleSharedLinkAsync")!;
            Require(handler.Posts == 2 && handler.Requested, "A second hotkey must reverse the previous request");
            handler.FailRead = true;
            await (Task)Invoke("ToggleSharedLinkAsync")!;
            Require(handler.Posts == 2, "Failed reads must not send a guessed toggle");
            handler.FailRead = false;
            handler.PendingRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = (Task)Invoke("ToggleSharedLinkAsync")!;
            await (Task)Invoke("ToggleSharedLinkAsync")!;
            handler.PendingRead.SetResult();
            await pending;
            Require(handler.Posts == 3 && !handler.Requested, "Overlapping hotkeys must not double-toggle");
        }
        finally
        {
            field.SetValue(window, original);
        }

        for (var i = 0; i < 500; i++) Invoke("Log", "Bounded log fixture " + i, Brushes.Gray);
        foreach (var name in new[] { "ActivityLog", "ActivityLogFull" })
        {
            var log = (RichTextBox)window.FindName(name);
            Require(log.Document.Blocks.Count == 180 && !log.IsUndoEnabled && !log.CanUndo,
                "Log history must be bounded, including the undo store");
        }
    }

    private sealed class SharedRequestHandler : HttpMessageHandler
    {
        internal bool Requested;
        internal bool FailRead;
        internal int Posts;
        internal TaskCompletionSource? PendingRead;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                if (FailRead) throw new HttpRequestException("Fixture offline");
                if (PendingRead is not null) await PendingRead.Task;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { shared = new { requested = Requested } }))
                };
            }
            Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/api/shared/request",
                "Unexpected write in fixture");
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requested = json.RootElement.GetProperty("requested").GetBoolean();
            Posts++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private static void Capture(string name)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name));
        encoder.Save(stream);
    }
}
