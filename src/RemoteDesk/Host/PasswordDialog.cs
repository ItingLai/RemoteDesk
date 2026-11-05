using System.Drawing;

namespace RemoteDesk.Host;

/// <summary>
/// 設定無人值守連線密碼對話盒。
/// </summary>
internal sealed class PasswordDialog : Form
{
    private readonly TextBox _txtPassword;
    private readonly TextBox _txtConfirm;
    public string? NewPassword { get; private set; }

    public PasswordDialog(bool hasExistingPassword)
    {
        Text = "RemoteDesk - 設定無人值守存取密碼";
        Size = new Size(420, 260);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(248, 249, 250);
        Font = new Font("Microsoft JhengHei UI", 9.5f, FontStyle.Regular);

        var lblPrompt = new Label
        {
            Text = "設定固定密碼後，控制端可直接輸入密碼進行遠端連線，無須本機點擊確認。\n若留空則清除密碼（改為互動授權模式）。",
            Location = new Point(20, 16),
            Size = new Size(365, 45),
            ForeColor = Color.FromArgb(80, 80, 80)
        };

        var lblPwd = new Label { Text = "新密碼：", Location = new Point(20, 75), AutoSize = true };
        _txtPassword = new TextBox { Location = new Point(110, 72), Width = 260, UseSystemPasswordChar = true };

        var lblConfirm = new Label { Text = "確認密碼：", Location = new Point(20, 115), AutoSize = true };
        _txtConfirm = new TextBox { Location = new Point(110, 112), Width = 260, UseSystemPasswordChar = true };

        var btnSave = new Button
        {
            Text = "儲存設定",
            Location = new Point(180, 165),
            Size = new Size(100, 32),
            BackColor = Color.FromArgb(26, 115, 232),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnSave.Click += (_, _) =>
        {
            if (_txtPassword.Text != _txtConfirm.Text)
            {
                MessageBox.Show(this, "兩次輸入的密碼不一致，請重新輸入！", "密碼錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            NewPassword = string.IsNullOrWhiteSpace(_txtPassword.Text) ? null : _txtPassword.Text;
            DialogResult = DialogResult.OK;
            Close();
        };

        var btnCancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(290, 165),
            Size = new Size(80, 32)
        };

        Controls.AddRange(new Control[] { lblPrompt, lblPwd, _txtPassword, lblConfirm, _txtConfirm, btnSave, btnCancel });
    }
}
