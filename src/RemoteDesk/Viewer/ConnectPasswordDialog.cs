using System.Drawing;
using System.Windows.Forms;
using RemoteDesk.Common;
using RemoteDesk.Controls;
using RemoteDesk.Native;

namespace RemoteDesk.Viewer;

/// <summary>
/// 遠端連線密碼輸入對話盒 (風格完全與本體暗黑風格統一，杜絕多餘無人值守字樣與請求按鈕)。
/// </summary>
internal sealed class ConnectPasswordDialog : Form
{
    private readonly DarkPasswordInput _inputPassword;
    private readonly CheckBox _chkRemember;
    private readonly Label _lblPrompt;
    private readonly Button _btnConnect;
    private readonly Button _btnCancel;

    public string Password => _inputPassword.Password;
    public bool RememberPassword => _chkRemember.Checked;
    public bool IsInteractiveRequested => false;

    public ConnectPasswordDialog(string deviceName, string? prefilledPassword)
        : this(deviceName, deviceName, prefilledPassword, allowInteractive: false, errorMessage: null)
    {
    }

    public ConnectPasswordDialog(
        string deviceName,
        string deviceId,
        string? prefilledPassword,
        bool allowInteractive = false,
        string? errorMessage = null)
    {
        Text = $"RemoteDesk - 連線至 {deviceName}";
        ClientSize = new Size(460, 236);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        Font = Theme.BodyFont;
        AppBranding.ApplyToForm(this);

        var lblTitle = new Label
        {
            Text = "🔒 輸入連線密碼",
            Font = Theme.CreateFont(12.5f, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            Location = new Point(22, 18),
            AutoSize = true
        };

        var lblDevice = new Label
        {
            Text = $"目標電腦：{deviceName} ({AppConfig.FormatId(deviceId)})",
            Font = Theme.CreateFont(9.5f, FontStyle.Bold),
            ForeColor = Theme.Accent,
            Location = new Point(22, 46),
            AutoSize = true
        };

        bool hasError = !string.IsNullOrEmpty(errorMessage);
        _lblPrompt = new Label
        {
            Text = hasError ? errorMessage! : "請輸入目標電腦的遠端存取密碼以進行連線：",
            ForeColor = hasError ? Theme.Warning : Theme.TextSecondary,
            Font = Theme.CreateFont(9f),
            Location = new Point(22, 72),
            Size = new Size(416, 22)
        };

        _inputPassword = new DarkPasswordInput("請輸入連線密碼")
        {
            Location = new Point(22, 98),
            Size = new Size(416, 44),
            Password = prefilledPassword ?? ""
        };
        _inputPassword.ReturnPressed += () =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };

        _chkRemember = new CheckBox
        {
            Text = "記住此密碼（使用 Windows DPAPI 本機加密儲存）",
            Location = new Point(24, 150),
            Size = new Size(410, 24),
            ForeColor = Theme.TextSecondary,
            Font = Theme.CreateFont(9f),
            Checked = !string.IsNullOrEmpty(prefilledPassword)
        };

        int btnY = 182;

        _btnCancel = new Button
        {
            Text = "取消",
            Location = new Point(246, btnY),
            Size = new Size(90, 36),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9.5f),
            Cursor = Cursors.Hand,
            DialogResult = DialogResult.Cancel
        };
        _btnCancel.FlatAppearance.BorderColor = Theme.Border;

        _btnConnect = new Button
        {
            Text = "連線",
            Location = new Point(346, btnY),
            Size = new Size(92, 36),
            BackColor = Theme.Primary,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            DialogResult = DialogResult.OK
        };
        _btnConnect.FlatAppearance.BorderSize = 0;

        Controls.AddRange(new Control[] { lblTitle, lblDevice, _lblPrompt, _inputPassword, _chkRemember, _btnCancel, _btnConnect });
        AcceptButton = _btnConnect;
        CancelButton = _btnCancel;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32.EnableImmersiveDarkMode(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _inputPassword.FocusInput();
    }
}
