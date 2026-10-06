using PhoneDeck.Desktop;

// Only the integration runner starts this fixture. It must never use a real user's
// data directory or receiver ports. No global hotkeys, speech model, or OS insertion.
if (Environment.GetEnvironmentVariable("PHONEDECK_INTEGRATION_HARNESS") != "1"
    || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PHONEDECK_DATA_DIR")))
    throw new InvalidOperationException("Run this fixture through browser.integration.mjs.");
foreach (var name in new[] { "PHONEDECK_LOCAL_PORT", "PHONEDECK_LAN_PORT", "PHONEDECK_WEB_PORT" })
    if (!int.TryParse(Environment.GetEnvironmentVariable(name), out var port) || port < 1024 || port is 8765 or 8766 or 8768)
        throw new InvalidOperationException("The integration fixture requires isolated ports.");

var engine = new SilentTestEngine();
var app = DesktopApp.Create(["--no-browser", "--no-discovery", "--no-hotkeys", "--history-only"], engine);
app.MapGet("/local/integration/stats", () => new { engine.Calls, engine.Bytes });
await app.RunAsync();

sealed class SilentTestEngine : ISpeechEngine
{
    private long calls, bytes;
    public bool Ready => true;
    public long Calls => Interlocked.Read(ref calls);
    public long Bytes => Interlocked.Read(ref bytes);
    public Task<string> TranscribeAsync(byte[] pcm, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        Interlocked.Increment(ref calls);
        Interlocked.Add(ref bytes, pcm.Length);
        // Audio remains in the production in-memory path and is cleared there.
        // Empty output also prevents even history-only synthetic transcript output.
        return Task.FromResult("");
    }
}
