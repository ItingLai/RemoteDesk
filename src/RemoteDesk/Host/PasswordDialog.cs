using System.Drawing;
using System.Windows.Forms;
using RemoteDesk.Common;
using RemoteDesk.Controls;
using RemoteDesk.Native;

namespace RemoteDesk.Host;

/// <summary>
/// 設定無人值守連線密碼對話盒 (完全遵循本體暗黑風格)。
/// </summary>
internal sealed class PasswordDialog : Form
{
    private readonly DarkPasswordInput _inputPassword;
    private readonly DarkPasswordInput _inputConfirm;
    public string? NewPassword { get; private set; }

    public PasswordDialog(bool hasExistingPassword)
    {
        Text = "RemoteDesk - 設定無人值守存取密碼";
        ClientSize = new Size(450, 276);
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
            Text = "設定無人值守存取密碼",
            Font = Theme.CreateFont(12.5f, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            Location = new Point(22, 16),
            AutoSize = true
        };

        var lblPrompt = new Label
        {
            Text = "設定固定密碼後，控制端可直接輸入密碼進行遠端連線，無須本機點擊確認。\n若兩欄皆留空並儲存，將清除密碼（改為互動授權確認模式）。",
            Location = new Point(22, 42),
            Size = new Size(406, 36),
            ForeColor = Theme.TextSecondary,
            Font = Theme.CreateFont(8.5f)
        };

        _inputPassword = new DarkPasswordInput("請輸入新密碼 (留空則清除)")
        {
            Location = new Point(22, 86),
            Size = new Size(406, 42)
        };

        _inputConfirm = new DarkPasswordInput("請再次輸入確認新密碼")
        {
            Location = new Point(22, 138),
            Size = new Size(406, 42)
        };
        _inputConfirm.ReturnPressed += SaveAndClose;

        var btnSave = new Button
        {
            Text = "儲存設定",
            Location = new Point(218, 210),
            Size = new Size(102, 36),
            BackColor = Theme.Primary,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += (_, _) => SaveAndClose();

        var btnCancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(328, 210),
            Size = new Size(100, 36),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9.5f),
            Cursor = Cursors.Hand
        };
        btnCancel.FlatAppearance.BorderColor = Theme.Border;

        Controls.AddRange(new Control[] { lblTitle, lblPrompt, _inputPassword, _inputConfirm, btnSave, btnCancel });
        AcceptButton = btnSave;
        CancelButton = btnCancel;
    }

    private void SaveAndClose()
    {
        if (_inputPassword.Password != _inputConfirm.Password)
        {
            MessageBox.Show(this, "兩次輸入的密碼不一致，請重新檢查！", "密碼錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        NewPassword = string.IsNullOrWhiteSpace(_inputPassword.Password) ? null : _inputPassword.Password;
        DialogResult = DialogResult.OK;
        Close();
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
