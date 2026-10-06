using System.Text.Json.Nodes;
using System.Security;

namespace PhoneDeck.MacReceiver;

/// <summary>
/// phoneManagedSettingsV1（macOS）：手机「设置 → 多电脑配置」可直接修改这台 Mac 的输入法、
/// 快捷键、USB 恢复、局域网发现与登录启动。请求/响应格式与 Windows 接收端一致，
/// 写入必须带目标电脑 ID 与读取时的修订号，正在输入或供音时拒绝保存。
/// </summary>
internal static class MacDesktopConfigurationEndpoints
{
    internal static void Map(WebApplication app, string computerId, ConfigurationGate gate,
        Func<bool> busy, Func<MacReceiverSettings> read, Action<MacReceiverSettings> replace,
        UsbWatchdog usb, LanDiscoveryResponder lan)
    {
        MacConnectionState Connection() => new(read().UsbWatchdog, read().LanDiscovery, MacAutoStart.Enabled);
        string ConnectionRevision(MacConnectionState state) =>
            DesktopConfiguration.Revision(state, MacApiJsonContext.Default.MacConnectionState);
        string VoiceRevision(MacVoiceEngineSettings settings) =>
            DesktopConfiguration.Revision(settings, MacFileWriteJsonContext.Default.MacVoiceEngineSettings);
        // 配置快照用 JsonObject 组装（原生编译不能序列化匿名对象），字段与旧版一致。
        JsonObject Snapshot()
        {
            lock (MacVoiceEngines.ConfigurationLock)
            {
                var catalog = MacVoiceEngines.Catalog;
                var connection = Connection();
                var overrides = new JsonObject();
                foreach (var (engineId, bindings) in catalog.Settings.ShortcutOverrides
                    ?? new Dictionary<string, Dictionary<string, string>>())
                {
                    var modes = new JsonObject();
                    foreach (var (mode, binding) in bindings)
                    {
                        modes[mode] = binding;
                    }
                    overrides[engineId] = modes;
                }
                var engines = new JsonArray();
                foreach (var profile in catalog.Profiles)
                {
                    var modes = new JsonArray();
                    foreach (var mode in profile.Modes)
                    {
                        modes.Add((JsonNode)new JsonObject
                        {
                            ["id"] = mode.Id,
                            ["label"] = mode.Label ?? mode.Id,
                            ["trigger"] = mode.Trigger,
                            ["defaultKeys"] = mode.MacKeys ?? mode.Keys
                        });
                    }
                    engines.Add((JsonNode)new JsonObject
                    {
                        ["id"] = profile.Id,
                        ["displayName"] = profile.DisplayName,
                        ["experimental"] = profile.Experimental,
                        ["verifiesMicrophone"] = profile.VerifiesMicrophone,
                        ["modes"] = modes
                    });
                }
                return new JsonObject
                {
                    ["ok"] = true,
                    ["computerId"] = computerId,
                    ["busy"] = busy(),
                    ["voiceRevision"] = VoiceRevision(catalog.Settings),
                    ["connectionRevision"] = ConnectionRevision(connection),
                    ["connection"] = new JsonObject
                    {
                        ["usbWatchdog"] = connection.UsbWatchdog,
                        ["lanDiscovery"] = connection.LanDiscovery,
                        ["autoStart"] = connection.AutoStart
                    },
                    ["activeEngine"] = catalog.Active.Id,
                    ["shortcutOverrides"] = overrides,
                    ["engines"] = engines
                };
            }
        }
        IResult Apply(string? target, Action change)
        {
            try
            {
                DesktopConfiguration.CheckTarget(target, computerId);
                if (!gate.Apply(busy, change))
                {
                    return Results.Json(ApiResult.Fail("正在输入或供音，请停止后保存"), ReceiverApiJsonContext.Default.ApiResult, statusCode: 409);
                }
                return Results.Ok(Snapshot());
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(ApiResult.Fail(exception.Message));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Json(ApiResult.Fail(exception.Message), ReceiverApiJsonContext.Default.ApiResult, statusCode: 409);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                return Results.Json(ApiResult.Fail("保存失败，请检查电脑权限或磁盘后重试"), ReceiverApiJsonContext.Default.ApiResult, statusCode: 500);
            }
        }

        app.MapGet("/api/config/desktop", () => Results.Ok(Snapshot()));
        app.MapPost("/api/config/desktop/voice", (DesktopVoiceRequest request) => Apply(request.TargetComputerId, () =>
        {
            DesktopConfiguration.CheckRevision(request.Revision, VoiceRevision(MacVoiceEngines.Catalog.Settings));
            MacVoiceEngines.ApplySettings(MacVoiceSettingsValidator.ValidateVoice(MacVoiceEngines.Catalog, request));
        }));
        app.MapPost("/api/config/desktop/connection", (DesktopConnectionRequest request) => Apply(request.TargetComputerId, () =>
        {
            DesktopConfiguration.CheckRevision(request.Revision, ConnectionRevision(Connection()));
            var old = read();
            var next = new MacReceiverSettings
            {
                UsbWatchdog = request.UsbWatchdog ?? old.UsbWatchdog,
                LanDiscovery = request.LanDiscovery ?? old.LanDiscovery,
                AdbPath = old.AdbPath,
                AudioDeviceUid = old.AudioDeviceUid,
                TypelessSettingsPath = old.TypelessSettingsPath,
                TypelessShortcuts = old.TypelessShortcuts
            };
            var oldAutoStart = MacAutoStart.Enabled;
            if (request.AutoStart.HasValue)
            {
                MacAutoStart.Set(request.AutoStart.Value);
            }
            try
            {
                DesktopConfiguration.WriteAtomic(MacReceiverSettings.SettingsPath, next,
                    MacFileWriteJsonContext.Default.MacReceiverSettings);
            }
            catch
            {
                if (request.AutoStart.HasValue)
                {
                    MacAutoStart.Set(oldAutoStart);
                }
                throw;
            }
            replace(next);
            usb.SetEnabled(next.UsbWatchdog);
            lan.SetEnabled(next.LanDiscovery);
        }));
    }
}

/// <summary>校验手机提交的输入法与快捷键覆盖（键名按 macOS CGEvent 映射）。</summary>
internal static class MacVoiceSettingsValidator
{
    internal static MacVoiceEngineSettings ValidateVoice(MacVoiceEngineCatalog catalog, DesktopVoiceRequest request)
    {
        var id = request.ActiveEngine?.Trim().ToLowerInvariant();
        if (id is null || catalog.Find(id) is null)
        {
            throw new ArgumentException("请选择这台电脑支持的输入法");
        }
        if (request.ShortcutOverrides?.Count > catalog.Profiles.Count)
        {
            throw new ArgumentException("输入法配置数量超出限制");
        }
        var overrides = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (engineId, bindings) in request.ShortcutOverrides ?? new())
        {
            var profile = catalog.Find(engineId) ?? throw new ArgumentException("未知输入法配置");
            if (bindings is null || bindings.Count > profile.Modes.Length)
            {
                throw new ArgumentException("无效的输入法模式");
            }
            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (mode, binding) in bindings)
            {
                if (profile.FindMode(mode) is null)
                {
                    throw new ArgumentException("未知的语音模式");
                }
                var value = binding?.Trim() ?? "";
                if (value.Length > 100)
                {
                    throw new ArgumentException("快捷键过长");
                }
                if (value.Length > 0)
                {
                    try
                    {
                        MacKeyboardInput.ParseEngineBinding(value);
                    }
                    catch (ArgumentException)
                    {
                        throw new ArgumentException("快捷键无法识别，请使用如 Control+Option+V 的组合");
                    }
                }
                copy.Add(mode, value);
            }
            overrides.Add(profile.Id, copy);
        }
        return new MacVoiceEngineSettings { ActiveEngine = id, ShortcutOverrides = overrides };
    }
}

/// <summary>登录启动：在 ~/Library/LaunchAgents 写入只启动当前接收端的 LaunchAgent，下次登录生效。</summary>
internal static class MacAutoStart
{
    internal const string Label = "com.phonedeck.receiver";

    internal static string PlistPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", Label + ".plist");

    internal static string? Executable { get; set; } = Environment.ProcessPath;

    internal static bool Enabled =>
        Executable is not null && File.Exists(PlistPath)
        && File.ReadAllText(PlistPath).Contains(SecurityElement.Escape(Executable), StringComparison.Ordinal);

    internal static void Set(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(PlistPath))
            {
                File.Delete(PlistPath);
            }
            return;
        }
        if (Executable is null || !File.Exists(Executable))
        {
            throw new IOException("未找到接收端可执行文件，无法启用登录启动");
        }
        DesktopConfiguration.WriteText(PlistPath, Plist(Executable));
    }

    internal static string Plist(string executable) => $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>{Label}</string>
  <key>ProgramArguments</key><array><string>{SecurityElement.Escape(executable)}</string></array>
  <key>RunAtLoad</key><true/>
  <key>ProcessType</key><string>Interactive</string>
</dict>
</plist>
""";
}
