using System.Drawing;
using System.Windows.Forms;
using RemoteDesk.Common;

namespace RemoteDesk.Host;

/// <summary>
/// 被控端互動授權請求對話盒：
/// 當無密碼連線請求進來時，在被控端畫面上跳出確認視窗，
/// 顯示控制端名稱、來源 IP 及 6 位數安全驗證碼，並倒數計時。
/// </summary>
internal sealed class AccessRequestDialog : Form
{
    private readonly Label _lblInfo;
    private readonly Label _lblCode;
    private readonly Label _lblTimer;
    private readonly Button _btnAccept;
    private readonly Button _btnReject;
    private readonly System.Windows.Forms.Timer _timer;
    private int _remainingSeconds = 30;

    public bool IsAccepted { get; private set; }

    public AccessRequestDialog(string viewerName, string viewerId, string address, string code)
    {
        Text = "RemoteDesk - 連線授權請求";
        Size = new Size(480, 320);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        Font = Theme.BodyFont;
        AppBranding.ApplyToForm(this);

        var lblTitle = new Label
        {
            Text = "收到遠端桌面連線請求",
            Font = Theme.CreateFont(13f, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            Location = new Point(24, 20),
            AutoSize = true
        };

        _lblInfo = new Label
        {
            Text = $"來源名稱：{viewerName}\n裝置 ID  ：{viewerId}\n網路位址：{address}",
            Location = new Point(24, 55),
            Size = new Size(416, 65),
            ForeColor = Theme.TextSecondary,
            Font = Theme.CreateFont(9.5f)
        };

        var pnlCode = new Panel
        {
            Location = new Point(24, 125),
            Size = new Size(416, 52),
            BackColor = Theme.SurfaceElevated
        };

        _lblCode = new Label
        {
            Text = $"安全確認碼：{code[..3]} {code[3..]}",
            Font = Theme.CreateFont(13f, FontStyle.Bold),
            ForeColor = Theme.Accent,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        pnlCode.Controls.Add(_lblCode);

        _lblTimer = new Label
        {
            Text = "30 秒後自動拒絕...",
            Location = new Point(24, 190),
            AutoSize = true,
            ForeColor = Theme.TextSecondary,
            Font = Theme.SmallFont
        };

        _btnAccept = new Button
        {
            Text = "✓ 接受 (允許控制)",
            DialogResult = DialogResult.OK,
            Location = new Point(165, 225),
            Size = new Size(165, 38),
            BackColor = Theme.Success,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnAccept.FlatAppearance.BorderSize = 0;
        _btnAccept.Click += (_, _) => { IsAccepted = true; Close(); };

        _btnReject = new Button
        {
            Text = "✕ 拒絕",
            DialogResult = DialogResult.Cancel,
            Location = new Point(340, 225),
            Size = new Size(100, 38),
            BackColor = Theme.Danger,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnReject.FlatAppearance.BorderSize = 0;
        _btnReject.Click += (_, _) => { IsAccepted = false; Close(); };

        Controls.AddRange(new Control[] { lblTitle, _lblInfo, pnlCode, _lblTimer, _btnAccept, _btnReject });
        AcceptButton = _btnAccept;
        CancelButton = _btnReject;

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) =>
        {
            _remainingSeconds--;
            if (_remainingSeconds <= 0)
            {
                _timer.Stop();
                IsAccepted = false;
                Close();
            }
            else
            {
                _lblTimer.Text = $"{_remainingSeconds} 秒後自動拒絕...";
            }
        };
        _timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}
