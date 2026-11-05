using System.Drawing;

namespace RemoteDesk.Viewer;

internal sealed class ConnectPasswordDialog : Form
{
    private readonly TextBox _txtPassword;
    private readonly CheckBox _chkRemember;

    public string Password => _txtPassword.Text;
    public bool RememberPassword => _chkRemember.Checked;

    public ConnectPasswordDialog(string deviceName, string? prefilledPassword)
    {
        Text = $"連線至 {deviceName}";
        Size = new Size(380, 200);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(248, 249, 250);
        Font = new Font("Microsoft JhengHei UI", 9.5f, FontStyle.Regular);

        var lblPrompt = new Label
        {
            Text = $"請輸入目標電腦「{deviceName}」的遠端存取密碼：",
            Location = new Point(20, 16),
            Size = new Size(330, 36)
        };

        _txtPassword = new TextBox
        {
            Location = new Point(20, 58),
            Width = 325,
            UseSystemPasswordChar = true,
            Text = prefilledPassword ?? ""
        };

        _chkRemember = new CheckBox
        {
            Text = "記住此密碼（使用 Windows DPAPI 加密儲存）",
            Location = new Point(20, 92),
            Width = 325,
            Checked = !string.IsNullOrEmpty(prefilledPassword)
        };

        var btnOk = new Button
        {
            Text = "連線",
            DialogResult = DialogResult.OK,
            Location = new Point(170, 122),
            Size = new Size(85, 30),
            BackColor = Color.FromArgb(26, 115, 232),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };

        var btnCancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(265, 122),
            Size = new Size(80, 30)
        };

        Controls.AddRange(new Control[] { lblPrompt, _txtPassword, _chkRemember, btnOk, btnCancel });
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }
}
