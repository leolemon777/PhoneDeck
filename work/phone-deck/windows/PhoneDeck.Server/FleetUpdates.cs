using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

internal sealed class FleetUpdates
{
    internal const string Version = "1.6.0-dev.16";
    internal const long Sequence = 28;
    private readonly string root;
    private readonly SemaphoreSlim gate = new(1, 1);
    private UpdateManifest? manifest;
    private string? requestId;
    private volatile bool installing;
    private readonly object admission = new();
    private int activeRequests;
    internal bool EnterUse() { lock (admission) { if (installing) return false; activeRequests++; return true; } }
    internal void ExitUse() { lock (admission) activeRequests--; }
    internal bool BeginInstall(Func<bool> busy)
    {
        lock (admission)
        {
            if (activeRequests != 0 || busy()) return false;
            installing = true;
            return true;
        }
    }
    internal bool Installing => installing;
    internal object Health => new { supported = true, sequence = Sequence, requestId };

    internal FleetUpdates(string? dataDirectory = null)
    {
        root = Path.Combine(dataDirectory ?? PhoneDeckDataDirectory.Get(), "updates");
        Directory.CreateDirectory(root);
        try { manifest = UpdatePackage.Verify(Path.Combine(root, "bundle.zip")); } catch (Exception) { }
        if (File.Exists(Path.Combine(root, "request.txt"))) requestId = File.ReadAllText(Path.Combine(root, "request.txt"));
    }

    internal static void WriteState(string root, string state, string detail)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "status.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new { state, detail, at = DateTimeOffset.UtcNow }, UpdatePackage.Json));
        File.Move(path + ".tmp", path, true);
    }

    internal void Map(WebApplication app, string computerId, Func<bool> busy)
    {
        var bundle = Path.Combine(root, "bundle.zip");
        bool Authorized(HttpContext context)
        {
            if (context.Connection.LocalPort == 8765
                && context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "::1")) return false;
            return context.Request.Headers["X-PhoneDeck-Update"] == "1"
                && context.Request.Headers["X-PhoneDeck-Target"] == computerId;
        }
        app.MapGet("/updates", (HttpContext context) =>
            context.Connection.LocalPort == 8765 && IPAddress.IsLoopback(context.Connection.RemoteIpAddress!)
            ? Results.Content(Page, "text/html; charset=utf-8") : Results.NotFound());
        app.MapGet("/api/updates", () =>
        {
            JsonElement? status = null;
            try { status = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(root, "status.json"))); } catch (Exception) { }
            return Results.Ok(new { ok = true, computerId, version = Version, sequence = Sequence, manifest, requestId, busy = busy(), installing, status });
        });
        app.MapGet("/api/updates/bundle", () => manifest is null ? Results.NotFound() : Results.File(bundle, "application/zip"));
        app.MapPost("/api/updates/bundle", async (HttpContext context) =>
        {
            if (!Authorized(context)) return Results.StatusCode(403);
            if (!await gate.WaitAsync(0)) return Results.Conflict(new { ok = false, error = "已有更新操作进行中" });
            var temporary = Path.Combine(root, "incoming.zip");
            try
            {
                if (installing) return Results.Conflict(new { ok = false, error = "正在安装" });
                var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = UpdatePackage.MaxBundleBytes;
                await using (var output = File.Create(temporary))
                {
                    var buffer = new byte[65536];
                    long total = 0;
                    int read;
                    while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
                    {
                        total += read;
                        if (total > UpdatePackage.MaxBundleBytes) throw new InvalidDataException("更新包过大");
                        await output.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                    }
                }
                var candidate = UpdatePackage.Verify(temporary);
                if (candidate.Sequence < Sequence) throw new InvalidDataException("不允许安装旧版本");
                File.Move(temporary, bundle, true);
                manifest = candidate;
                requestId = null;
                File.Delete(Path.Combine(root, "request.txt"));
                WriteState(root, "ready", "更新包签名与文件校验通过");
                return Results.Ok(new { ok = true, manifest });
            }
            catch (Exception exception) { return Results.BadRequest(new { ok = false, error = exception.Message }); }
            finally { File.Delete(temporary); gate.Release(); }
        });
        app.MapPost("/api/updates/request", async (HttpContext context) =>
        {
            if (!Authorized(context)) return Results.StatusCode(403);
            await gate.WaitAsync();
            try
            {
                if (manifest is null || installing) return Results.Conflict(new { ok = false, error = "请先导入签名更新包" });
                requestId = Guid.NewGuid().ToString();
                File.WriteAllText(Path.Combine(root, "request.txt"), requestId);
                return Results.Ok(new { ok = true, requestId });
            }
            finally { gate.Release(); }
        });
        app.MapPost("/api/updates/apply", async (HttpContext context) =>
        {
            if (!Authorized(context)) return Results.StatusCode(403);
            if (!await gate.WaitAsync(0)) return Results.Conflict(new { ok = false, error = "更新操作进行中" });
            try
            {
                if (installing) return Results.Ok(new { ok = true, state = "installing" });
                if (manifest is null) return Results.Conflict(new { ok = false, error = "缺少更新包" });
                if (manifest.Sequence <= Sequence) return Results.Ok(new { ok = true, state = "current" });
                // Set the barrier before checking audio so new streams cannot race with shutdown.
                if (!BeginInstall(busy)) return Results.Ok(new { ok = true, state = "waiting-idle" });
                if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64)
                    throw new InvalidOperationException("此更新包仅支持 Windows x64");
                UpdatePackage.Verify(bundle);
                var helper = Path.Combine(root, "worker-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(Environment.ProcessPath!, helper);
                var info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                info.ArgumentList.Add("--apply-fleet-update");
                info.ArgumentList.Add(AppContext.BaseDirectory);
                info.ArgumentList.Add(PhoneDeckDataDirectory.Get());
                info.ArgumentList.Add(Environment.ProcessId.ToString());
                info.ArgumentList.Add(Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString());
                File.WriteAllText(Path.Combine(root, "installing"), DateTimeOffset.UtcNow.ToString("O"));
                WriteState(root, "installing", "正在更新，接收端将短暂重启");
                Process.Start(info)?.Dispose();
                context.Response.OnCompleted(() => { app.Lifetime.StopApplication(); return Task.CompletedTask; });
                return Results.Ok(new { ok = true, state = "installing" });
            }
            catch (Exception exception)
            {
                installing = false;
                File.Delete(Path.Combine(root, "installing"));
                return Results.BadRequest(new { ok = false, error = exception.Message });
            }
            finally { gate.Release(); }
        });
    }

    private const string Page = """
        <!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>PhoneDeck 设备更新</title><style>body{font:17px system-ui;max-width:760px;margin:60px auto;padding:24px;background:#f5f7fa;color:#172033}button,input{font:inherit;margin:12px 10px 12px 0;padding:12px}pre{white-space:pre-wrap}section{background:white;padding:28px;border-radius:18px}</style>
        <section><h1>设备统一更新</h1><p>导入发布者签名的更新包，再从这里发起。打开已配对手机的 PhoneDeck，手机会分发给其他电脑，最后更新自身。</p>
        <input id="file" type="file" accept=".zip"><button id="upload">导入更新包</button><button id="run">更新所有设备</button>
        <p>首次需给旧电脑安装支持更新的版本。正在使用麦克风时会等待；安卓安装按系统提示确认。每台设备的结果在手机「设置 → 设备更新」显示。</p><pre id="state">读取状态…</pre></section>
        <script>let id;const state=document.getElementById('state');async function status(){let r=await fetch('/api/updates');let s=await r.json();id=s.computerId;state.textContent='本机：'+s.version+'\n更新包：'+(s.manifest?s.manifest.windowsVersion:'未导入')+'\n'+(s.requestId?'已发起，请打开配对手机查看整批进度\n':'')+(s.status?s.status.detail:'');}
        async function post(path,body){if(!id)await status();let r=await fetch(path,{method:'POST',headers:{'X-PhoneDeck-Update':'1','X-PhoneDeck-Target':id,'Content-Type':'application/octet-stream'},body});let s=await r.json();if(!r.ok||!s.ok)throw Error(s.error||r.status);await status();}
        document.getElementById('upload').onclick=async()=>{try{let f=document.getElementById('file').files[0];if(!f)throw Error('请选择签名更新包');state.textContent='正在导入并校验…';await post('/api/updates/bundle',f);}catch(e){state.textContent=e.message;}};
        document.getElementById('run').onclick=async()=>{try{await post('/api/updates/request');}catch(e){state.textContent=e.message;}};status().catch(e=>state.textContent=e.message);</script></html>
        """;
}
