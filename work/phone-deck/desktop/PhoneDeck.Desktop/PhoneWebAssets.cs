using System.Reflection;

namespace PhoneDeck.Desktop;

internal static class PhoneWebAssets
{
    private static readonly IReadOnlyDictionary<string, string> Types = new Dictionary<string, string>
    {
        ["index.html"] = "text/html; charset=utf-8", ["style.css"] = "text/css; charset=utf-8",
        ["app.js"] = "text/javascript; charset=utf-8", ["ui.js"] = "text/javascript; charset=utf-8",
        ["audio.js"] = "text/javascript; charset=utf-8", ["pcm-worklet.js"] = "text/javascript; charset=utf-8",
        ["session.js"] = "text/javascript; charset=utf-8",
        ["sw.js"] = "text/javascript; charset=utf-8", ["manifest.webmanifest"] = "application/manifest+json",
        ["icons/icon.svg"] = "image/svg+xml", ["icons/icon-192.png"] = "image/png",
        ["icons/icon-512.png"] = "image/png", ["icons/apple-touch-icon.png"] = "image/png"
    };
    internal static void Map(WebApplication app)
    {
        // ASP.NET matches a trailing slash on the same route; redirect only the
        // slash-less URL or /phone/ would redirect to itself indefinitely.
        app.MapGet("/phone", (HttpContext context) => context.Request.Path.Value!.EndsWith('/')
            ? Asset(context, "index.html") : Results.Redirect("/phone/"));
        app.MapGet("/phone/{**file}", (HttpContext context, string? file) => Asset(context, file));
    }
    private static IResult Asset(HttpContext context, string? file)
    {
        file = string.IsNullOrEmpty(file) ? "index.html" : file;
        if (!Types.TryGetValue(file, out var type)) return Results.NotFound();
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PhoneDeck.Desktop.PhoneWeb." + file.Replace('/', '.'));
        if (stream is null) return Results.NotFound();
        context.Response.Headers["Service-Worker-Allowed"] = "/phone/";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        return Results.Stream(stream, type);
    }
}
