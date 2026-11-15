using System.Drawing;
using System.Windows.Forms;
using RemoteDesk.Common;

namespace RemoteDesk.Host;

/// <summary>
/// 被控端裝置配對與連線授權對話盒：
/// 當連線請求進來時，在被控端畫面上跳出確認視窗，
/// 顯示控制端名稱、裝置 ID、公鑰指紋、來源位址與 6 位數安全驗證碼，
/// 提供「僅接受本次」與「接受並信任此裝置」兩種授權選項。
/// </summary>
internal sealed class AccessRequestDialog : Form
{
    private readonly Label _lblInfo;
    private readonly Label _lblFingerprint;
    private readonly Label _lblWarning;
    private readonly Label _lblCode;
    private readonly Label _lblTimer;
    private readonly Button _btnAcceptOnce;
    private readonly Button _btnAcceptAndTrust;
    private readonly Button _btnReject;
    private readonly System.Windows.Forms.Timer _timer;
    private int _remainingSeconds = 30;

    public bool IsAccepted { get; private set; }
    public bool TrustDevice { get; private set; }

    public AccessRequestDialog(string viewerName, string viewerId, string address, string code)
        : this(viewerName, viewerId, address, code, null, false)
    {
    }

    public AccessRequestDialog(string viewerName, string viewerId, string address, string code, string? fingerprint, bool keyMismatch = false)
    {
        Text = "RemoteDesk - 連線授權與配對請求";
        Size = new Size(540, 390);
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
            Location = new Point(24, 16),
            AutoSize = true
        };

        _lblInfo = new Label
        {
            Text = $"來源名稱：{viewerName}\n裝置 ID  ：{viewerId}\n網路位址：{address}",
            Location = new Point(24, 46),
            Size = new Size(480, 56),
            ForeColor = Theme.TextSecondary,
            Font = Theme.CreateFont(9.5f)
        };

        _lblFingerprint = new Label
        {
            Text = !string.IsNullOrEmpty(fingerprint) ? $"公鑰指紋：{fingerprint}" : "公鑰指紋：尚未提供",
            Location = new Point(24, 104),
            Size = new Size(480, 20),
            ForeColor = Theme.TextSecondary,
            Font = Theme.CreateMonoFont(8.5f)
        };

        _lblWarning = new Label
        {
            Text = "⚠️ 警示：此裝置公鑰與歷史配對紀錄不符！可能為重灌或偽冒裝置！",
            Location = new Point(24, 126),
            Size = new Size(480, 20),
            ForeColor = Theme.Warning,
            Font = Theme.CreateFont(8.5f, FontStyle.Bold),
            Visible = keyMismatch
        };

        var pnlCode = new Panel
        {
            Location = new Point(24, 150),
            Size = new Size(480, 50),
            BackColor = Theme.SurfaceElevated
        };

        _lblCode = new Label
        {
            Text = $"安全核對碼：{code[..3]} {code[3..]}",
            Font = Theme.CreateFont(13f, FontStyle.Bold),
            ForeColor = Theme.Accent,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        pnlCode.Controls.Add(_lblCode);

        _lblTimer = new Label
        {
            Text = "30 秒後自動拒絕...",
            Location = new Point(24, 212),
            AutoSize = true,
            ForeColor = Theme.TextSecondary,
            Font = Theme.SmallFont
        };

        // 按鈕區
        _btnAcceptOnce = new Button
        {
            Text = "✓ 接受 (僅本次)",
            DialogResult = DialogResult.OK,
            Location = new Point(24, 280),
            Size = new Size(155, 38),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnAcceptOnce.FlatAppearance.BorderColor = Theme.Border;
        _btnAcceptOnce.Click += (_, _) => { IsAccepted = true; TrustDevice = false; Close(); };

        _btnAcceptAndTrust = new Button
        {
            Text = "🛡️ 接受並信任此裝置",
            DialogResult = DialogResult.OK,
            Location = new Point(190, 280),
            Size = new Size(195, 38),
            BackColor = Theme.Success,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnAcceptAndTrust.FlatAppearance.BorderSize = 0;
        _btnAcceptAndTrust.Click += (_, _) => { IsAccepted = true; TrustDevice = true; Close(); };

        _btnReject = new Button
        {
            Text = "✕ 拒絕",
            DialogResult = DialogResult.Cancel,
            Location = new Point(398, 280),
            Size = new Size(106, 38),
            BackColor = Theme.Danger,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnReject.FlatAppearance.BorderSize = 0;
        _btnReject.Click += (_, _) => { IsAccepted = false; TrustDevice = false; Close(); };

        Controls.AddRange(new Control[]
        {
            lblTitle, _lblInfo, _lblFingerprint, _lblWarning, pnlCode, _lblTimer,
            _btnAcceptOnce, _btnAcceptAndTrust, _btnReject
        });

        AcceptButton = _btnAcceptAndTrust;
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
