using System.Drawing;
using System.Drawing.Drawing2D;
using RemoteDesk.Common;

namespace RemoteDesk.Controls;

public static class RoundedRectangle
{
    public static GraphicsPath Create(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (d > bounds.Width) d = bounds.Width;
        if (d > bounds.Height) d = bounds.Height;
        if (d <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new Rectangle(bounds.X, bounds.Y, d, d);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - d;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - d;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// 依據規格 2.2 與 2.4：通用圓角容器面板 (Surface = #1F2937, Border = #374151, Radius = 10px)。
/// </summary>
internal class RoundedCardPanel : Panel
{
    public int CornerRadius { get; set; } = Theme.CornerRadius;
    public Color FillColor { get; set; } = Theme.Surface;
    public Color BorderColor { get; set; } = Theme.Border;
    public float BorderWidth { get; set; } = 1f;

    public RoundedCardPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRectangle.Create(rect, CornerRadius);

        using var brush = new SolidBrush(FillColor);
        g.FillPath(brush, path);

        if (BorderColor != Color.Transparent && BorderWidth > 0)
        {
            using var pen = new Pen(BorderColor, BorderWidth);
            g.DrawPath(pen, path);
        }
    }
}

/// <summary>
/// <summary>
/// 依據規格 4.4：Remote Address 遠端連線輸入框。
/// 位於首頁最上方，高度 50px，單一輸入框，右側為純向量圖標按鈕 (連線箭頭 / 動畫載入環)，按 Enter 立即連線。
/// </summary>
internal sealed class RemoteAddressInput : Panel
{
    private readonly TextBox _textBox;
    private readonly Label _lblIcon;
    private readonly ConnectActionButton _btnAction;
    private readonly System.Windows.Forms.Timer _spinnerTimer;
    private readonly ToolTip _toolTip;
    private float _spinnerAngle;
    private bool _isFocused;
    private bool _isLoading;

    public event Action<string>? ConnectRequested;

    public string TextValue
    {
        get => _textBox.Text;
        set => _textBox.Text = value;
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            _isLoading = value;
            _textBox.Enabled = !value;
            _btnAction.IsLoading = value;
            if (value)
            {
                _spinnerAngle = 0f;
                _btnAction.SpinnerAngle = 0f;
                _spinnerTimer.Start();
                _toolTip.SetToolTip(_btnAction, "正在連線中...");
            }
            else
            {
                _spinnerTimer.Stop();
                _toolTip.SetToolTip(_btnAction, "連線至此設備 (Enter)");
            }
            Invalidate();
        }
    }

    public RemoteAddressInput()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = Theme.InputHeight; // 50px
        BackColor = Color.Transparent;

        _toolTip = new ToolTip();

        _lblIcon = new Label
        {
            Text = "🌐",
            Font = new Font("Segoe UI Symbol", 12f),
            ForeColor = Theme.Accent,
            Location = new Point(14, 13),
            Size = new Size(26, 24),
            BackColor = Color.Transparent
        };

        // 右側連線按鈕：純向量圖標 (平時為箭頭 ➔，連線中為順滑旋轉載入環，杜絕文字)
        _btnAction = new ConnectActionButton
        {
            Location = new Point(Width - 44, 7),
            Size = new Size(36, 36),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        _btnAction.Click += (_, _) => TriggerConnect();
        _toolTip.SetToolTip(_btnAction, "連線至此設備 (Enter)");

        _spinnerTimer = new System.Windows.Forms.Timer { Interval = 30 };
        _spinnerTimer.Tick += (_, _) =>
        {
            _spinnerAngle = (_spinnerAngle + 18f) % 360f;
            _btnAction.SpinnerAngle = _spinnerAngle;
        };

        _textBox = new TextBox
        {
            PlaceholderText = "Enter Remote Device ID or Alias (按 Enter 或點擊圖標連線)",
            Font = Theme.CreateFont(11.5f),
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.None,
            Location = new Point(48, 14),
            Size = new Size(Width - 100, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        _textBox.Enter += (_, _) => { _isFocused = true; Invalidate(); };
        _textBox.Leave += (_, _) => { _isFocused = false; Invalidate(); };
        _textBox.KeyDown += (_, ke) =>
        {
            if (ke.KeyCode == Keys.Enter)
            {
                ke.SuppressKeyPress = true;
                TriggerConnect();
            }
        };

        Controls.AddRange(new Control[] { _lblIcon, _textBox, _btnAction });
    }

    private void TriggerConnect()
    {
        if (_isLoading) return;
        var text = _textBox.Text.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            ConnectRequested?.Invoke(text);
        }
    }

    public void FocusInput() => _textBox.Focus();

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRectangle.Create(rect, Theme.CornerRadius);

        using var brush = new SolidBrush(Theme.Surface);
        g.FillPath(brush, path);

        var borderClr = _isFocused ? Theme.Primary : Theme.Border;
        using var pen = new Pen(borderClr, _isFocused ? 1.8f : 1f);
        g.DrawPath(pen, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _spinnerTimer.Stop();
            _spinnerTimer.Dispose();
            _toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// 輸入框右側之純圖標按鈕：繪製連線箭頭或旋轉載入進度環，完全不使用文字。
    /// </summary>
    private sealed class ConnectActionButton : Control
    {
        private bool _isHovered;
        private bool _isLoading;
        private float _spinnerAngle;

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    Cursor = value ? Cursors.WaitCursor : Cursors.Hand;
                    Invalidate();
                }
            }
        }

        public float SpinnerAngle
        {
            get => _spinnerAngle;
            set
            {
                _spinnerAngle = value;
                Invalidate();
            }
        }

        public ConnectActionButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Size = new Size(36, 36);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _isHovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _isHovered = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Hover 效果 (非載入中時顯示半透明高亮底色)
            if (_isHovered && !_isLoading)
            {
                using var hoverBrush = new SolidBrush(Color.FromArgb(40, Theme.Primary));
                using var path = RoundedRectangle.Create(new Rectangle(2, 2, Width - 5, Height - 5), 8);
                g.FillPath(hoverBrush, path);
            }

            if (_isLoading)
            {
                // 動畫旋轉載入進度環 (純向量，無文字)
                var spinnerRect = new Rectangle(7, 7, Width - 14, Height - 14);
                VectorIcons.DrawSpinner(g, spinnerRect, _spinnerAngle, Theme.Accent, 2.4f);
            }
            else
            {
                // 連線箭頭向量圖標 (純向量，無文字)
                Color iconColor = _isHovered ? Theme.Accent : Theme.TextSecondary;
                var arrowRect = new Rectangle(5, 5, Width - 10, Height - 10);
                VectorIcons.DrawConnectArrow(g, arrowRect, iconColor);
            }
        }
    }
}

/// <summary>
/// 重新命名遠端設備名稱對話盒
/// </summary>
internal sealed class RenameDeviceDialog : Form
{
    private readonly TextBox _txtNewName;
    public string NewName => _txtNewName.Text.Trim();

    public RenameDeviceDialog(string currentName, string deviceId)
    {
        Text = "重新命名設備";
        Size = new Size(380, 200);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        Font = Theme.BodyFont;

        var lblPrompt = new Label
        {
            Text = $"為設備 {AppConfig.FormatId(deviceId)} 設定別名或名稱：",
            Location = new Point(20, 18),
            Size = new Size(330, 22),
            ForeColor = Theme.TextSecondary
        };

        _txtNewName = new TextBox
        {
            Text = currentName,
            Location = new Point(20, 46),
            Size = new Size(325, 30),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.CreateFont(10f)
        };

        var btnOk = new Button
        {
            Text = "確定儲存",
            DialogResult = DialogResult.OK,
            Location = new Point(155, 100),
            Size = new Size(95, 34),
            BackColor = Theme.Primary,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.ButtonFont,
            Cursor = Cursors.Hand
        };
        btnOk.FlatAppearance.BorderSize = 0;

        var btnCancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(260, 100),
            Size = new Size(85, 34),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.BodyFont,
            Cursor = Cursors.Hand
        };
        btnCancel.FlatAppearance.BorderColor = Theme.Border;

        Controls.AddRange(new Control[] { lblPrompt, _txtNewName, btnOk, btnCancel });
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RemoteDesk.Native.Win32.EnableImmersiveDarkMode(Handle);
    }
}

/// <summary>
/// 具備暗黑主題本體風格、圓角邊框與密碼隱藏/顯示切換按鈕之密碼輸入框。
/// 支援 Enter 鍵觸發連線與外部事件回調。
/// </summary>
internal sealed class DarkPasswordInput : Panel
{
    private readonly TextBox _textBox;
    private readonly Label _lblIcon;
    private readonly Label _btnEye;
    private bool _isFocused;
    private bool _isPasswordVisible;

    public event Action? ReturnPressed;

    public string Password
    {
        get => _textBox.Text;
        set => _textBox.Text = value;
    }

    public TextBox InnerTextBox => _textBox;

    public DarkPasswordInput(string placeholder = "請輸入遠端存取密碼")
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 44;
        BackColor = Color.Transparent;

        _lblIcon = new Label
        {
            Text = "🔒",
            Font = new Font("Segoe UI Symbol", 11.5f),
            ForeColor = Theme.Accent,
            Location = new Point(12, 10),
            Size = new Size(24, 24),
            BackColor = Color.Transparent
        };

        _textBox = new TextBox
        {
            PlaceholderText = placeholder,
            Font = Theme.CreateFont(11f),
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.None,
            UseSystemPasswordChar = true,
            Location = new Point(40, 12),
            Size = new Size(Width - 80, 22),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        _btnEye = new Label
        {
            Text = "👁",
            Font = new Font("Segoe UI Symbol", 11f),
            ForeColor = Theme.TextSecondary,
            Size = new Size(32, 26),
            Location = new Point(Width - 36, 9),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent,
            Cursor = Cursors.Hand
        };
        _btnEye.Click += (_, _) =>
        {
            _isPasswordVisible = !_isPasswordVisible;
            _textBox.UseSystemPasswordChar = !_isPasswordVisible;
            _btnEye.ForeColor = _isPasswordVisible ? Theme.Accent : Theme.TextSecondary;
        };

        _textBox.Enter += (_, _) => { _isFocused = true; Invalidate(); };
        _textBox.Leave += (_, _) => { _isFocused = false; Invalidate(); };
        _textBox.KeyDown += (_, ke) =>
        {
            if (ke.KeyCode == Keys.Enter)
            {
                ke.SuppressKeyPress = true;
                ReturnPressed?.Invoke();
            }
        };

        Controls.AddRange(new Control[] { _lblIcon, _textBox, _btnEye });
    }

    public void FocusInput()
    {
        _textBox.Focus();
        _textBox.SelectAll();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRectangle.Create(rect, Theme.CornerRadius);

        using var brush = new SolidBrush(Theme.Surface);
        g.FillPath(brush, path);

        var borderClr = _isFocused ? Theme.Primary : Theme.Border;
        using var pen = new Pen(borderClr, _isFocused ? 1.8f : 1f);
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// 依據規格 4.6 與 11：桌面縮圖設備卡片 (Desktop Thumbnail Card)。
/// 16:9 縮圖，下方只顯示 Device ID，整張卡片為連線熱區，右鍵選單提供收藏與管理。
/// </summary>
internal sealed class DeviceThumbnailCard : Panel
{
    private readonly RecentEntry _entry;
    private readonly Action<string> _onConnect;
    private readonly Action<string> _onToggleFavorite;
    private readonly Action<string> _onRemove;
    private readonly Action<string, string>? _onRename;
    private Image? _thumbnail;
    private bool _isHovered;
    private bool _isOnline;
    private readonly ToolTip _statusToolTip = new() { InitialDelay = 150 };
    private const int Radius = Theme.CornerRadius; // 10px

    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (_isOnline != value)
            {
                _isOnline = value;
                Invalidate();
            }
        }
    }

    public DeviceThumbnailCard(RecentEntry entry, bool isOnline, Action<string> onConnect, Action<string> onToggleFavorite, Action<string> onRemove, Action<string, string>? onRename = null)
    {
        _entry = entry;
        _isOnline = isOnline;
        _onConnect = onConnect;
        _onToggleFavorite = onToggleFavorite;
        _onRemove = onRemove;
        _onRename = onRename;

        // 16:9 縮圖 (216x122) + 下方 Device ID 區域 (44px) = 216x166
        Size = new Size(216, 166);
        Margin = new Padding(8, 8, 8, 12);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;

        LoadThumbnail();

        MouseEnter += (_, _) => { _isHovered = true; Invalidate(); };
        MouseLeave += (_, _) => { _isHovered = false; Invalidate(); };
        MouseMove += (s, e) =>
        {
            var statusRect = new Rectangle(6, 6, 22, 22);
            if (statusRect.Contains(e.Location))
            {
                string text = _isOnline ? "可連線 (就緒)" : "設備離線";
                if (_statusToolTip.GetToolTip(this) != text)
                {
                    _statusToolTip.SetToolTip(this, text);
                }
            }
            else
            {
                string idText = !string.IsNullOrEmpty(_entry.DeviceId) ? AppConfig.FormatId(_entry.DeviceId) : _entry.Key;
                string tip = $"連線至 {idText}";
                if (_statusToolTip.GetToolTip(this) != tip)
                {
                    _statusToolTip.SetToolTip(this, tip);
                }
            }
        };
        Click += (_, _) => _onConnect(_entry.Key);

        // 右鍵管理選單 (規格 4.6)
        ContextMenuStrip = CreateContextMenu();
    }

    public DeviceThumbnailCard(RecentEntry entry, Action<string> onConnect, Action<string> onToggleFavorite, Action<string> onRemove, Action<string, string>? onRename = null)
        : this(entry, false, onConnect, onToggleFavorite, onRemove, onRename)
    {
    }

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.TextPrimary,
            ShowImageMargin = false
        };

        if (_onRename != null)
        {
            menu.Items.Add(new ToolStripMenuItem("✏️ 重新命名設備", null, (_, _) =>
            {
                using var dlg = new RenameDeviceDialog(_entry.Name ?? "", _entry.DeviceId ?? _entry.Key);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _onRename(_entry.Key, dlg.NewName);
                }
            }));
            menu.Items.Add(new ToolStripSeparator());
        }

        var favText = _entry.IsFavorite ? "★ 從我的最愛移除" : "⭐ 加入我的最愛";
        menu.Items.Add(new ToolStripMenuItem(favText, null, (_, _) => _onToggleFavorite(_entry.Key)));

        menu.Items.Add(new ToolStripMenuItem("📋 複製 Device ID", null, (_, _) =>
        {
            var id = !string.IsNullOrEmpty(_entry.DeviceId) ? _entry.DeviceId : _entry.Key;
            Clipboard.SetText(id);
        }));

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("✕ 從紀錄中移除", null, (_, _) => _onRemove(_entry.Key)));

        return menu;
    }

    private void LoadThumbnail()
    {
        string? path = _entry.ThumbnailPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            string id = !string.IsNullOrEmpty(_entry.DeviceId) ? _entry.DeviceId : _entry.Key;
            string candidate = Path.Combine(AppConfig.DataDirectory, "thumbnails", $"{id}.jpg");
            if (File.Exists(candidate))
            {
                path = candidate;
            }
        }

        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                using var ms = new MemoryStream(bytes);
                _thumbnail = Image.FromStream(ms);
            }
            catch { }
        }
    }

    /// <summary>
    /// 依據需求 4：若尚未自被控端取得單純桌布，繪製簡潔俐落的純色桌布底色，不自行繪製任何假圖示或假工作列。
    /// </summary>
    private void DrawPureDefaultWallpaper(Graphics g, Rectangle bounds)
    {
        int hash = Math.Abs((_entry.DeviceId ?? _entry.Key).GetHashCode());
        Color cTop, cBot;
        switch (hash % 4)
        {
            case 0:
                cTop = Color.FromArgb(15, 23, 42);   // Slate 900
                cBot = Color.FromArgb(30, 58, 138);  // Blue 900
                break;
            case 1:
                cTop = Color.FromArgb(17, 24, 39);   // Gray 900
                cBot = Color.FromArgb(49, 46, 129);  // Indigo 900
                break;
            case 2:
                cTop = Color.FromArgb(19, 21, 26);
                cBot = Color.FromArgb(15, 76, 92);
                break;
            default:
                cTop = Color.FromArgb(24, 24, 27);   // Zinc 900
                cBot = Color.FromArgb(39, 39, 42);   // Zinc 800
                break;
        }

        using var brush = new LinearGradientBrush(bounds, cTop, cBot, 55f);
        g.FillRectangle(brush, bounds);
    }

    /// <summary>
    /// 在設備圖片的左上角繪製設備狀態燈號（依據需求 5：綠色燈號為可連線(就緒)，紅色燈號帶有禁止符號為(設備離線)）。
    /// </summary>
    private void DrawStatusLight(Graphics g)
    {
        int x = 8;
        int y = 8;
        int size = 16;
        var rect = new Rectangle(x, y, size, size);

        if (_isOnline)
        {
            // 綠色燈號：就緒 / 可連線
            using var glowBrush = new SolidBrush(Color.FromArgb(70, 34, 197, 94));
            g.FillEllipse(glowBrush, x - 2, y - 2, size + 4, size + 4);

            using var greenBrush = new SolidBrush(Color.FromArgb(255, 34, 197, 94));
            g.FillEllipse(greenBrush, rect);

            using var hlPen = new Pen(Color.FromArgb(180, 255, 255, 255), 1.2f);
            g.DrawArc(hlPen, x + 2, y + 2, size - 4, size - 4, 200, 70);

            using var borderPen = new Pen(Color.FromArgb(120, 0, 0, 0), 1f);
            g.DrawEllipse(borderPen, rect);
        }
        else
        {
            // 紅色燈號帶有禁止符號：設備離線
            using var bgBrush = new SolidBrush(Color.FromArgb(210, 24, 24, 27));
            g.FillEllipse(bgBrush, rect);

            using var redPen = new Pen(Color.FromArgb(239, 68, 68), 2f);
            g.DrawEllipse(redPen, x + 1, y + 1, size - 2, size - 2);

            float cx = x + size / 2f;
            float cy = y + size / 2f;
            float r = (size - 2) / 2f;
            float offset = r * 0.7071f;
            g.DrawLine(redPen, cx - offset, cy - offset, cx + offset, cy + offset);

            using var borderPen = new Pen(Color.FromArgb(120, 0, 0, 0), 1f);
            g.DrawEllipse(borderPen, rect);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var cardBounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var cardPath = RoundedRectangle.Create(cardBounds, Radius);

        // 1. 卡片底色 (Surface: #1F2937)
        using var bgBrush = new SolidBrush(Theme.Surface);
        g.FillPath(bgBrush, cardPath);

        // 2. 16:9 桌面代表圖片 (寬 216，高 122)
        var thumbBounds = new Rectangle(0, 0, Width, 122);
        var oldClip = g.Clip;
        using var thumbClipPath = new GraphicsPath();
        thumbClipPath.AddArc(0, 0, Radius * 2, Radius * 2, 180, 90);
        thumbClipPath.AddArc(Width - Radius * 2 - 1, 0, Radius * 2, Radius * 2, 270, 90);
        thumbClipPath.AddLine(Width - 1, 122, 0, 122);
        thumbClipPath.CloseFigure();
        g.SetClip(thumbClipPath);

        // 依據需求 4：設備代表圖片改採用設備的桌面，直接取用被控端的單純桌布，而不是自己繪製
        if (_thumbnail != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_thumbnail, thumbBounds);
        }
        else
        {
            DrawPureDefaultWallpaper(g, thumbBounds);
        }

        // 繪製設備狀態燈號（依據需求 5）
        DrawStatusLight(g);

        g.Clip = oldClip;

        // 若為我的最愛，於縮圖右上角顯示精巧星號標記
        if (_entry.IsFavorite)
        {
            using var starBrush = new SolidBrush(Theme.Warning);
            using var starFont = new Font("Segoe UI Symbol", 10.5f, FontStyle.Bold);
            g.DrawString("⭐", starFont, starBrush, Width - 24, 6);
        }

        // 3. 分隔線
        using var sepPen = new Pen(Theme.Border);
        g.DrawLine(sepPen, 0, 122, Width, 122);

        // 4. 縮圖下方：如果有自訂名稱/主機名稱，第一行顯示名稱，第二行顯示 Device ID
        string idText = !string.IsNullOrEmpty(_entry.DeviceId) ? AppConfig.FormatId(_entry.DeviceId) : _entry.Key;
        string? displayName = !string.IsNullOrWhiteSpace(_entry.Name) ? _entry.Name.Trim() : null;

        if (displayName != null && displayName != _entry.DeviceId && displayName != _entry.Key)
        {
            // 兩行顯示：名稱 (Bold) + Device ID (Secondary)
            using var nameFont = Theme.CreateFont(9f, FontStyle.Bold);
            using var nameBrush = new SolidBrush(Theme.TextPrimary);
            var nameRect = new RectangleF(6, 126, Width - 12, 19);
            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
            g.DrawString(displayName, nameFont, nameBrush, nameRect, format);

            using var subFont = Theme.CreateFont(8.5f);
            using var subBrush = new SolidBrush(Theme.TextSecondary);
            var idSz = g.MeasureString(idText, subFont);
            g.DrawString(idText, subFont, subBrush, (Width - idSz.Width) / 2, 146);
        }
        else
        {
            // 單行置中顯示 Device ID
            using var idFont = Theme.CardIdFont;
            using var idBrush = new SolidBrush(Theme.TextPrimary);
            var textSz = g.MeasureString(idText, idFont);
            g.DrawString(idText, idFont, idBrush, (Width - textSz.Width) / 2, 134);
        }

        // 5. 外框 (Hover 時切換為 Primary / Accent 高亮)
        var borderClr = _isHovered ? Theme.Accent : Theme.Border;
        using var borderPen = new Pen(borderClr, _isHovered ? 1.5f : 1f);
        g.DrawPath(borderPen, cardPath);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnail?.Dispose();
            _thumbnail = null;
            _statusToolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// 依據規格 4.6 與需求 3：連線紀錄 / 我的最愛 / 區域網內設備 頁籤切換器。
/// 整合在同一個內容區域，使用頁籤無縫切換。
/// </summary>
internal sealed class DeviceTabsControl : Panel
{
    private int _selectedIndex;
    private int _recentCount;
    private int _favCount;
    private int _lanCount;

    public event Action<int>? TabChanged;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex != value)
            {
                _selectedIndex = value;
                Invalidate();
                TabChanged?.Invoke(_selectedIndex);
            }
        }
    }

    public void UpdateCounts(int recentCount, int favCount, int lanCount)
    {
        _recentCount = recentCount;
        _favCount = favCount;
        _lanCount = lanCount;
        Invalidate();
    }

    public void UpdateCounts(int recentCount, int favCount) => UpdateCounts(recentCount, favCount, 0);

    public DeviceTabsControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 38;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    private (Rectangle rect, string text)[] GetTabs(Graphics g, Font activeFont, Font inactiveFont)
    {
        string tab1 = $"連線紀錄 ({_recentCount})";
        string tab2 = $"我的最愛 ({_favCount})";
        string tab3 = $"區域網內設備 ({_lanCount})";

        string[] titles = { tab1, tab2, tab3 };
        var list = new (Rectangle rect, string text)[3];
        int x = 8;
        for (int i = 0; i < 3; i++)
        {
            var font = i == _selectedIndex ? activeFont : inactiveFont;
            var sz = g.MeasureString(titles[i], font);
            int w = (int)Math.Ceiling(sz.Width);
            list[i] = (new Rectangle(x, 0, w + 16, Height), titles[i]);
            x += w + 24;
        }
        return list;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        using var g = CreateGraphics();
        using var fontActive = Theme.CreateFont(10.5f, FontStyle.Bold);
        using var fontInactive = Theme.CreateFont(10.5f, FontStyle.Regular);
        var tabs = GetTabs(g, fontActive, fontInactive);
        for (int i = 0; i < tabs.Length; i++)
        {
            if (e.X >= tabs[i].rect.Left && e.X <= tabs[i].rect.Right)
            {
                SelectedIndex = i;
                break;
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var fontActive = Theme.CreateFont(10.5f, FontStyle.Bold);
        using var fontInactive = Theme.CreateFont(10.5f, FontStyle.Regular);

        var tabs = GetTabs(g, fontActive, fontInactive);

        for (int i = 0; i < tabs.Length; i++)
        {
            bool active = i == _selectedIndex;
            using var brush = new SolidBrush(active ? Theme.TextPrimary : Theme.TextSecondary);
            var font = active ? fontActive : fontInactive;
            g.DrawString(tabs[i].text, font, brush, tabs[i].rect.Left + 8, 8);
        }

        // 底部指示條
        using var linePen = new Pen(Theme.Border, 1f);
        g.DrawLine(linePen, 0, Height - 1, Width, Height - 1);

        if (_selectedIndex >= 0 && _selectedIndex < tabs.Length)
        {
            using var activePen = new Pen(Theme.Primary, 3f);
            var activeRect = tabs[_selectedIndex].rect;
            g.DrawLine(activePen, activeRect.Left + 8, Height - 2, activeRect.Right - 8, Height - 2);
        }
    }
}

public enum HeaderIconKind
{
    Gear,
    Clock
}

/// <summary>
/// 頂部列按鈕：以純 GDI+ 向量繪製齒輪或時鐘圖標，搭配高對比文字與圓角外框，
/// 100% 杜絕圖標為空、遺漏或字型編碼缺失之現象。
/// </summary>
internal sealed class HeaderActionButton : Control
{
    public HeaderIconKind IconKind { get; set; }
    public string Title { get; set; }
    private bool _isHovered;

    public HeaderActionButton(HeaderIconKind iconKind, string title, int width = 95)
    {
        IconKind = iconKind;
        Title = title;
        Size = new Size(width, 34);
        Cursor = Cursors.Hand;
        BackColor = Theme.SurfaceElevated;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);

        MouseEnter += (_, _) => { _isHovered = true; Invalidate(); };
        MouseLeave += (_, _) => { _isHovered = false; Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 1. 圓角按鈕外觀 (Radius = 6px)
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = RoundedRectangle.Create(rect, 6))
        {
            Color bg = _isHovered ? Color.FromArgb(45, 57, 76) : Theme.SurfaceElevated;
            using (var brush = new SolidBrush(bg))
            {
                g.FillPath(brush, path);
            }

            Color borderClr = _isHovered ? Theme.Accent : Theme.Border;
            using (var pen = new Pen(borderClr, 1f))
            {
                g.DrawPath(pen, path);
            }
        }

        // 2. 向量圖標 (100% 向量保證繪出，無字型缺少問題)
        var iconBounds = new Rectangle(10, 8, 18, 18);
        Color iconClr = _isHovered ? Theme.Accent : Theme.TextPrimary;
        if (IconKind == HeaderIconKind.Gear)
        {
            VectorIcons.DrawGear(g, iconBounds, iconClr);
        }
        else
        {
            VectorIcons.DrawClock(g, iconBounds, iconClr);
        }

        // 3. 標題文字
        using var font = Theme.CreateFont(9f, FontStyle.Regular);
        using var textBrush = new SolidBrush(_isHovered ? Theme.TextPrimary : Theme.TextPrimary);
        g.DrawString(Title, font, textBrush, 32, 9);
    }
}
