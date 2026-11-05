using System.Drawing;

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
        Size = new Size(460, 310);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = Color.FromArgb(245, 245, 248);
        Font = new Font("Microsoft JhengHei UI", 9.5f, FontStyle.Regular);

        var lblTitle = new Label
        {
            Text = "收到遠端桌面連線請求",
            Font = new Font("Microsoft JhengHei UI", 13f, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 30, 30),
            Location = new Point(24, 20),
            AutoSize = true
        };

        _lblInfo = new Label
        {
            Text = $"來源名稱：{viewerName}\n裝置 ID  ：{viewerId}\n網路位址：{address}",
            Location = new Point(24, 60),
            Size = new Size(400, 65),
            ForeColor = Color.FromArgb(60, 60, 60)
        };

        var pnlCode = new Panel
        {
            Location = new Point(24, 130),
            Size = new Size(396, 55),
            BackColor = Color.FromArgb(232, 240, 254),
            BorderStyle = BorderStyle.FixedSingle
        };

        _lblCode = new Label
        {
            Text = $"安全確認碼：{code[..3]} {code[3..]}",
            Font = new Font("Consolas", 14f, FontStyle.Bold),
            ForeColor = Color.FromArgb(26, 115, 232),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        pnlCode.Controls.Add(_lblCode);

        _lblTimer = new Label
        {
            Text = "30 秒後自動拒絕...",
            Location = new Point(24, 195),
            AutoSize = true,
            ForeColor = Color.Gray
        };

        _btnAccept = new Button
        {
            Text = "接受 (允許控制)",
            DialogResult = DialogResult.OK,
            Location = new Point(160, 225),
            Size = new Size(130, 36),
            BackColor = Color.FromArgb(46, 125, 50),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft JhengHei UI", 10f, FontStyle.Bold)
        };
        _btnAccept.FlatAppearance.BorderSize = 0;
        _btnAccept.Click += (_, _) => { IsAccepted = true; Close(); };

        _btnReject = new Button
        {
            Text = "拒絕",
            DialogResult = DialogResult.Cancel,
            Location = new Point(300, 225),
            Size = new Size(120, 36),
            BackColor = Color.FromArgb(211, 47, 47),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft JhengHei UI", 10f, FontStyle.Bold)
        };
        _btnReject.FlatAppearance.BorderSize = 0;
        _btnReject.Click += (_, _) => { IsAccepted = false; Close(); };

        Controls.AddRange(new Control[] { lblTitle, _lblInfo, pnlCode, _lblTimer, _btnAccept, _btnReject });

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
