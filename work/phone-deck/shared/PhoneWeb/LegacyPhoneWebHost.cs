using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace PhoneDeck.Desktop;

// Browser UI for the existing external-input-method receivers. No speech model,
// transcription runtime or additional process is created by this host.
internal sealed class LegacyPhoneWebHost : IDisposable
{
    internal const int Port = 8768;
    private readonly BrowserTrust trust;
    private readonly WebPhoneGateway gateway;
    internal LegacyPhoneWebHost(string computerId, string name, string dataDirectory)
    {
        trust = new BrowserTrust(dataDirectory, computerId);
        var identity = new WebPhoneIdentity(computerId, name);
        gateway = new WebPhoneGateway(identity, owner => WebPhoneRemoteTarget.LocalExternal(identity, owner), dataDirectory,
            Port, trust.CertificateSha256, context => trust.SameOrigin(context, Port), browserAddresses: Addresses);
    }
    private string[] Addresses() => WebPhoneNetwork.Addresses().Where(address => trust.Hosts.Contains(address)).ToArray();
    internal void Listen(KestrelServerOptions options) => options.ListenAnyIP(Port, listener =>
    { listener.Protocols = HttpProtocols.Http1; listener.UseHttps(trust.Certificate); });

    internal void Map(WebApplication app)
    {
        // Runs before legacy authentication. Only the dedicated browser listener
        // may reach /phone, and only loopback may reach browser administration.
        app.Use(async (context, next) =>
        {
            var browser = context.Connection.LocalPort == Port;
            var localUi = context.Request.Path == "/" || context.Request.Path.StartsWithSegments("/local")
                || context.Request.Path == "/brand/web-setup.js";
            if (browser)
            {
                if (!context.Request.Path.StartsWithSegments("/phone") || !trust.AcceptsHost(context.Request.Host, Port))
                { context.Response.StatusCode = 404; return; }
                context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; worker-src 'self'; manifest-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
                context.Response.Headers["Permissions-Policy"] = "microphone=(self), camera=(), geolocation=()";
            }
            else if (context.Request.Path.StartsWithSegments("/phone")) { context.Response.StatusCode = 404; return; }
            else if (localUi)
            {
                if (!LocalAllowed(context)) { context.Response.StatusCode = 403; return; }
                context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'";
            }
            if (browser || localUi)
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
            }
            try { await next(); }
            catch (ArgumentException e) when ((browser || localUi) && !context.Response.HasStarted)
            { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { ok = false, error = e.Message }); }
            catch (InvalidOperationException e) when ((browser || localUi) && !context.Response.HasStarted)
            { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { ok = false, error = e.Message }); }
        });
        gateway.Map(app);
        PhoneWebAssets.Map(app);
        app.MapGet("/", () => Resource("PhoneDeck.PhoneWeb.Setup.html", "text/html; charset=utf-8"));
        app.MapGet("/brand/web-setup.js", () => Resource("PhoneDeck.PhoneWeb.Setup.js", "text/javascript; charset=utf-8"));
        app.MapGet("/local/web/certificate", () => Results.File(trust.PublicRoot, "application/x-x509-ca-cert", "Yandu-Personal-Root.cer"));
        app.MapGet("/local/web/setup", () => Results.Ok(new { ok = true, port = Port, certificateSha256 = trust.RootSha256,
            urls = Addresses().Select(address => $"https://{address}:{Port}/phone/").ToArray(),
            message = "请连接局域网后重启接收端；此入口使用电脑现有输入法，无需下载模型" }));
        app.MapPost("/local/quit", () =>
        { _ = Task.Run(async () => { await Task.Delay(200); app.Lifetime.StopApplication(); }); return Results.Ok(new { ok = true }); });
        app.Lifetime.ApplicationStopping.Register(gateway.Dispose);
    }
    private static IResult Resource(string name, string contentType) => Results.Stream(
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!, contentType);
    internal static bool LocalAllowed(HttpContext context)
    {
        if (context.Connection.LocalPort != 8765 || context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip)) return false;
        var host = context.Request.Host;
        if (host.Port != 8765 || host.Host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1")) return false;
        foreach (var raw in new[] { context.Request.Headers.Origin.FirstOrDefault(), context.Request.Headers.Referer.FirstOrDefault() })
            if (raw is not null && (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Port != 8765
                || !string.Equals(uri.Host.Trim('[', ']'), host.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))) return false;
        return context.Request.Headers["Sec-Fetch-Site"].ToString() != "cross-site";
    }
    public void Dispose() { gateway.Dispose(); trust.Dispose(); }
}
