using System.Text;
using System.Text.Json;

namespace PhoneDeck.ControlCenter;

/// <summary>
/// M1-A A2 配对窗口（docs/M1A_PAIRING_DESIGN.md §3）：按需弹出的本机信任操作窗。
/// 展示 QR + 手工码 + 材料校验码；手机提交后出现确认卡（30s）；
/// 全部经回环 127.0.0.1:8765 管理端点，不引入常驻 UI 轮询（窗口关闭即停表）。
/// </summary>
internal sealed class PairingForm : Form
{
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly PictureBox qrBox = new();
    private readonly Label intro = new();
    private readonly Label manualCode = new();
    private readonly Label checkCode = new();
    private readonly Label countdown = new();
    private readonly Label status = new();
    private readonly Button confirm = new();
    private readonly Button deny = new();
    private readonly Button regenerate = new();
    private string? pairingId;
    private bool completed;

    internal PairingForm()
    {
        Text = "PhoneDeck · 配对新手机";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(400, 560);
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9.75f);

        intro.Text = "在手机 PhoneDeck 上选择「扫码配对」，对准下方二维码。\n相机不可用时，在手机上输入手工码。";
        intro.AutoSize = false;
        intro.Size = new Size(370, 40);
        intro.Location = new Point(15, 12);
        Controls.Add(intro);

        qrBox.Size = new Size(280, 280);
        qrBox.Location = new Point(60, 58);
        qrBox.SizeMode = PictureBoxSizeMode.Zoom;
        Controls.Add(qrBox);

        manualCode.Text = "手工码：—";
        manualCode.AutoSize = false;
        manualCode.Size = new Size(370, 24);
        manualCode.Location = new Point(15, 350);
        manualCode.Font = new Font(FontFamily.GenericMonospace, 10.5f, FontStyle.Bold);
        manualCode.TextAlign = ContentAlignment.MiddleCenter;
        Controls.Add(manualCode);

        checkCode.Text = "校验码：—";
        checkCode.AutoSize = false;
        checkCode.Size = new Size(370, 20);
        checkCode.Location = new Point(15, 376);
        checkCode.TextAlign = ContentAlignment.MiddleCenter;
        Controls.Add(checkCode);

        countdown.Text = "窗口剩余 120 秒";
        countdown.AutoSize = false;
        countdown.Size = new Size(370, 20);
        countdown.Location = new Point(15, 398);
        countdown.TextAlign = ContentAlignment.MiddleCenter;
        Controls.Add(countdown);

        status.Text = "等待手机提交…";
        status.AutoSize = false;
        status.Size = new Size(370, 40);
        status.Location = new Point(15, 420);
        status.TextAlign = ContentAlignment.MiddleCenter;
        Controls.Add(status);

        confirm.Text = "确认配对";
        confirm.Size = new Size(110, 34);
        confirm.Location = new Point(35, 470);
        confirm.Enabled = false;
        confirm.Click += async (_, _) => await ConfirmAsync();
        Controls.Add(confirm);

        deny.Text = "拒绝";
        deny.Size = new Size(90, 34);
        deny.Location = new Point(155, 470);
        deny.Enabled = false;
        deny.Click += async (_, _) => await DecideAsync("deny", "已拒绝本次配对");
        Controls.Add(deny);

        regenerate.Text = "重新生成";
        regenerate.Size = new Size(110, 34);
        regenerate.Location = new Point(255, 470);
        regenerate.Click += async (_, _) => await BeginAsync();
        Controls.Add(regenerate);

        timer.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) => { await BeginAsync(); timer.Start(); };
        FormClosing += async (_, _) =>
        {
            timer.Stop();
            if (!completed && pairingId is not null)
            {
                await PostAsync("api/admin/pairing/cancel", new { });
            }
        };
    }

    private async Task BeginAsync()
    {
        try
        {
            using var response = await http.PostAsync(
                "http://127.0.0.1:8765/api/admin/pairing/begin",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                status.Text = ExtractError(body) ?? "无法开启配对窗口";
                return;
            }
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            pairingId = root.GetProperty("pairingId").GetString();
            manualCode.Text = "手工码：" + root.GetProperty("manualCode").GetString();
            checkCode.Text = "校验码（与手机侧比对）：" + root.GetProperty("checkCode").GetString();
            RenderQr(root.GetProperty("qrPayload").GetString());
            status.Text = "等待手机提交…";
            confirm.Enabled = false;
            deny.Enabled = false;
        }
        catch (Exception exception)
        {
            status.Text = "连接接收端失败：" + exception.Message;
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var response = await http.GetAsync("http://127.0.0.1:8765/api/admin/pairing/status");
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (!root.GetProperty("open").GetBoolean())
            {
                countdown.Text = "配对窗口已关闭";
                confirm.Enabled = false;
                deny.Enabled = false;
                return;
            }
            countdown.Text = $"窗口剩余 {root.GetProperty("remainingSeconds").GetInt32()} 秒";
            if (root.TryGetProperty("pending", out var pending) && pending.ValueKind == JsonValueKind.Object)
            {
                var label = pending.GetProperty("clientLabel").GetString();
                status.Text = $"「{label}」请求配对\n核对手机侧校验码后确认";
                confirm.Enabled = true;
                deny.Enabled = true;
            }
        }
        catch (Exception)
        {
            // 轮询失败不打扰用户；下次轮询重试。
        }
    }

    private async Task ConfirmAsync()
    {
        completed = await DecideAsync("confirm", "配对完成，回到手机继续");
        if (completed)
        {
            confirm.Enabled = false;
            deny.Enabled = false;
            await Task.Delay(1500);
            Close();
        }
    }

    private async Task<bool> DecideAsync(string action, string doneMessage)
    {
        if (pairingId is null)
        {
            return false;
        }
        try
        {
            using var response = await PostAsync(
                "api/admin/pairing/" + action, new { pairingId });
            if (response.IsSuccessStatusCode)
            {
                status.Text = doneMessage;
                return true;
            }
            status.Text = "操作失败，请重试";
            return false;
        }
        catch (Exception exception)
        {
            status.Text = "连接接收端失败：" + exception.Message;
            return false;
        }
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object payload)
    {
        return await http.PostAsync(
            "http://127.0.0.1:8765/" + path,
            new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"));
    }

    private void RenderQr(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return;
        }
        try
        {
            using var generator = new QRCoder.QRCodeGenerator();
            using var data = generator.CreateQrCode(
                payload, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(pixelsPerModule: 8);
            qrBox.Image?.Dispose();
            qrBox.Image = new Bitmap(new MemoryStream(png));
        }
        catch (Exception exception)
        {
            status.Text = "二维码生成失败（可用手工码）：" + exception.Message;
        }
    }

    private static string? ExtractError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
