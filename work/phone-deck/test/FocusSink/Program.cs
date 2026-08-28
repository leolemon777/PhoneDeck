using System.Runtime.InteropServices;

ApplicationConfiguration.Initialize();

var form = new Form
{
    Text = "PhoneDeck 自动测试输入框",
    Width = 520,
    Height = 180,
    StartPosition = FormStartPosition.CenterScreen,
    TopMost = true
};
var input = new TextBox
{
    Multiline = true,
    Dock = DockStyle.Fill,
    Font = new Font("Microsoft YaHei UI", 14),
    AccessibleName = "PhoneDeck test input"
};
form.Controls.Add(input);
input.TextChanged += (_, _) => form.Text = "PhoneDeck 自动测试输入框 · " + input.Text;
form.Shown += (_, _) =>
{
    form.Activate();
    input.Focus();
    SetForegroundWindow(form.Handle);
};
form.FormClosed += (_, _) =>
{
    var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(input.Text));
    Console.WriteLine(encoded);
};

var timeout = new System.Windows.Forms.Timer { Interval = 600000 };
timeout.Tick += (_, _) => form.Close();
timeout.Start();
Application.Run(form);

[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool SetForegroundWindow(IntPtr windowHandle);
