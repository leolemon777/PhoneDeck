internal static class DesktopConfigurationEndpoints
{
    internal static void Map(WebApplication app, string computerId, ConfigurationGate gate,
        Func<bool> busy, Func<ServerSettings> read, Action<ServerSettings> replace,
        UsbWatchdog usb, LanDiscoveryResponder lan, DiagnosticsMonitor diagnostics)
    {
        object Connection() => new { usbWatchdog = read().UsbWatchdog,
            lanDiscovery = read().LanDiscovery, autoStart = DesktopAutoStart.Enabled };
        object Snapshot()
        {
            lock (VoiceEngines.ConfigurationLock)
            {
                var catalog = VoiceEngines.Catalog;
                var connection = Connection();
                return new { ok = true, computerId, busy = busy(),
                    voiceRevision = DesktopConfiguration.Revision(catalog.Settings),
                    connectionRevision = DesktopConfiguration.Revision(connection), connection,
                    activeEngine = catalog.Active.Id, shortcutOverrides = catalog.Settings.ShortcutOverrides,
                    engines = catalog.Profiles.Select(p => new { id = p.Id, displayName = p.DisplayName,
                        experimental = p.Experimental, verifiesMicrophone = p.VerifiesMicrophone,
                        modes = p.Modes.Select(m => new { id = m.Id, label = m.Label ?? m.Id,
                            trigger = m.Trigger, defaultKeys = m.Keys }).ToArray() }).ToArray() };
            }
        }
        IResult Apply(string? target, Action change)
        {
            try
            {
                DesktopConfiguration.CheckTarget(target, computerId);
                if (!gate.Apply(busy, change))
                    return Results.Json(new { ok = false, error = "正在输入或供音，请停止后保存" }, statusCode: 409);
                return Results.Ok(Snapshot());
            }
            catch (ArgumentException e) { return Results.BadRequest(new { ok = false, error = e.Message }); }
            catch (InvalidOperationException e) { return Results.Json(new { ok = false, error = e.Message }, statusCode: 409); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { return Results.Json(new { ok = false, error = "保存失败，请检查电脑权限或磁盘后重试" }, statusCode: 500); }
        }
        app.MapGet("/api/config/desktop", () => Results.Ok(Snapshot()));
        app.MapPost("/api/config/desktop/voice", (DesktopVoiceRequest request) => Apply(request.TargetComputerId, () =>
        {
            DesktopConfiguration.CheckRevision(request.Revision, DesktopConfiguration.Revision(VoiceEngines.Catalog.Settings));
            VoiceEngines.ApplySettings(DesktopConfiguration.ValidateVoice(VoiceEngines.Catalog, request));
        }));
        app.MapPost("/api/config/desktop/connection", (DesktopConnectionRequest request) => Apply(request.TargetComputerId, () =>
        {
            DesktopConfiguration.CheckRevision(request.Revision, DesktopConfiguration.Revision(Connection()));
            var old = read();
            var next = new ServerSettings { UsbWatchdog = request.UsbWatchdog ?? old.UsbWatchdog,
                LanDiscovery = request.LanDiscovery ?? old.LanDiscovery, AdbPath = old.AdbPath,
                SharedRequested = old.SharedRequested };
            var oldAutoStart = DesktopAutoStart.Enabled;
            if (request.AutoStart.HasValue) DesktopAutoStart.Set(request.AutoStart.Value);
            try { DesktopConfiguration.WriteAtomic(Path.Combine(PhoneDeckDataDirectory.Get(), "server-settings.json"), next); }
            catch { if (request.AutoStart.HasValue) DesktopAutoStart.Set(oldAutoStart); throw; }
            replace(next);
            usb.SetEnabled(next.UsbWatchdog);
            lan.SetEnabled(next.LanDiscovery);
        }));
    }
}
