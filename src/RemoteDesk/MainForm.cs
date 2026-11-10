using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RemoteDesk.Common;
using RemoteDesk.Controls;
using RemoteDesk.Host;
using RemoteDesk.Native;
using RemoteDesk.Net;
using RemoteDesk.Viewer;

namespace RemoteDesk;

/// <summary>
/// RemoteDesk 現代化遠端桌面主介面 (依據規格第 1-5 節)：
/// 
/// ┌──────────────────────────────────────────────────────────────┐
/// │ Logo / RemoteDesk                       History   ⚙ Settings │
/// ├──────────────────────────────────────────────────────────────┤
/// │ Remote Address                                              │
/// │ ┌────────────────────────────────────────────────────────┐ │
/// │ │ Enter Remote Device ID or Alias                     ↵  │ │
/// │ └────────────────────────────────────────────────────────┘ │
/// │                                                              │
/// │ This Device                                                 │
/// │ ┌────────────────────────────────────────────────────────┐ │
/// │ │                   Your Device ID                       │ │
/// │ │                    123 456 789                         │ │
/// │ │                    [ Copy ID ]                         │ │
/// │ │                  ● Ready (21119)                       │ │
/// │ └────────────────────────────────────────────────────────┘ │
/// │                                                              │
/// │ [ Recent Sessions (3) ]   [ Favorites (1) ]                  │
/// │ ─────────────────────────────────────────────────────────── │
/// │  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐       │
/// │  │  Thumbnail   │  │  Thumbnail   │  │  Thumbnail   │       │
/// │  │ 123 456 789  │  │ 987 654 321  │  │ 456 789 123  │       │
/// │  └──────────────┘  └──────────────┘  └──────────────┘       │
/// ├──────────────────────────────────────────────────────────────┤
/// │ ● Server Status: Online                 Version: 1.0.1      │
/// └──────────────────────────────────────────────────────────────┘
/// </summary>
internal sealed class MainForm : Form, IHostUi
{
    private readonly HostConfig _hostConfig;
    private readonly ViewerConfig _viewerConfig;
    private readonly LanDiscovery _discovery;
    private readonly HostServer _server;

    // ---------------- 1. 頂部區域 (AppHeader / Custom Window Chrome - 規格 4.3) ----------------
    private Panel _pnlHeader = null!;
    private Label _lblLogo = null!;
    private Label _lblHomeStatusBadge = null!;
    private FlowLayoutPanel _pnlHeaderTabs = null!;
    private Button _btnHeaderSettings = null!;

    // Custom Window Chrome 控制按鈕 (最小化、最大化/還原、關閉)
    private Button _btnWinMinimize = null!;
    private Button _btnWinMaximize = null!;
    private Button _btnWinClose = null!;

    // ---------------- 遠端工作階段次行操作工具列 (規格：螢幕與設定等搬到下一行靠右擺放) ----------------
    private Panel _pnlSessionToolbar = null!;
    private Label _lblSessionToolbarInfo = null!;

    // 被控端連入警示橫幅
    private Panel _pnlIncomingBanner = null!;
    private Label _lblIncomingBannerText = null!;
    private Button _btnDisconnectIncoming = null!;

    // 連線中動態提示橫幅 (含旋轉載入環、即時狀態、取消按鈕)
    private Panel _pnlConnectingBanner = null!;
    private SpinnerBox _connectingSpinner = null!;
    private Label _lblConnectingText = null!;
    private Button _btnCancelConnect = null!;
    private readonly System.Windows.Forms.Timer _connectingTimer = new() { Interval = 30 };
    private float _connectingSpinnerAngle;
    private int _connectingDotCount;
    private int _connectingTickCount;
    private string _connectingTarget = "";
    private CancellationTokenSource? _connectingCts;

    // ---------------- 2. 主體內容視圖容器 ----------------
    private Panel _pnlBodyContainer = null!;
    private Panel _pnlHomeView = null!;
    private Panel _pnlSessionView = null!;

    // ---------------- 3. 首頁 Home 元件 (規格 4.1 - 4.6) ----------------
    // 3A. Remote Address (位於本機 ID 上方，規格 4.1 & 4.4)
    private RemoteAddressInput _inputRemoteAddress = null!;

    // 3B. This Device (本機 ID 獨立呈現，規格 4.5)
    private RoundedCardPanel _pnlThisDevice = null!;
    private Label _lblMyId = null!;
    private Button _btnCopyId = null!;
    private Label _lblMyStatus = null!;

    // 3C. Recent Sessions / Favorites 頁籤與縮圖網格 (規格 4.6)
    private DeviceTabsControl _tabsDevices = null!;
    private FlowLayoutPanel _pnlDevicesGrid = null!;
    private Label _lblDevicesEmpty = null!;

    // ---------------- 4. 遠端工作階段 Remote Session 操作元件 (集成於頂部列，最大化桌面視野) ----------------
    private Label _lblSessionTitle = null!;
    private Label _lblSessionStatusBadge = null!;
    private Panel _sepSession1 = null!;
    private FlowLayoutPanel _pnlMonitors = null!;
    private Button _btnImeMode = null!;
    private Button _btnSpecialKeys = null!;
    private Button _btnClipboardSync = null!;
    private Button _btnFullscreen = null!;
    private Button _btnSessionSettings = null!;
    private Button _btnSessionDisconnect = null!;

    private Panel _pnlSessionCanvasContainer = null!;
    private readonly ToolTip _sessionToolTip = new()
    {
        InitialDelay = 150,
        ReshowDelay = 100,
        AutoPopDelay = 8000,
        ShowAlways = true
    };

    // ---------------- 工作階段管理 ----------------
    private sealed class SessionTabItem
    {
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        public ViewerSession Session { get; set; } = null!;
        public DesktopCanvas Canvas { get; set; } = null!;
        public ImeSyncClient ImeClient { get; set; } = null!;
        public Button TabButton { get; set; } = null!;
        public int FrameCount { get; set; }
        public long LastFpsTime { get; set; }
        public int Fps { get; set; }
        public bool ThumbnailSaved { get; set; }
        public string SelectedMonitor { get; set; } = "";
        public List<MonitorInfo> Monitors { get; set; } = new();
    }

    /// <summary>
    /// 視窗邊緣命中穿透濾鏡：將滑鼠位於視窗外圍 8px 感應區的 WM_NCHITTEST 訊息轉發至母視窗 MainForm，
    /// 解決子控制項 (Panel) 阻擋非客戶區游標偵測，達成全視窗 8 方向自由拖曳縮放。
    /// </summary>
    private sealed class BorderHitTestFilter : NativeWindow
    {
        private readonly MainForm _form;

        public BorderHitTestFilter(Control control, MainForm form)
        {
            _form = form;
            if (control.IsHandleCreated) AssignHandle(control.Handle);
            control.HandleCreated += (_, _) =>
            {
                if (Handle == IntPtr.Zero) AssignHandle(control.Handle);
            };
            control.HandleDestroyed += (_, _) => ReleaseHandle();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Win32.WM_NCHITTEST && _form.WindowState == FormWindowState.Normal && !_form._isFullscreen)
            {
                int lParam = (int)m.LParam.ToInt64();
                short screenX = (short)(lParam & 0xFFFF);
                short screenY = (short)((lParam >> 16) & 0xFFFF);
                Point formPt = _form.PointToClient(new Point(screenX, screenY));
                int b = 8;

                bool isBorder = formPt.X <= b || formPt.X >= _form.ClientSize.Width - b ||
                                formPt.Y <= b || formPt.Y >= _form.ClientSize.Height - b;

                if (isBorder)
                {
                    m.Result = (IntPtr)(-1); // HTTRANSPARENT: 交由母視窗 MainForm 計算邊界拖曳
                    return;
                }
            }
            base.WndProc(ref m);
        }
    }

    private readonly List<SessionTabItem> _sessionTabs = new();
    private SessionTabItem? _currentSession;
    private KeyboardHook? _keyboardHook;
    private bool _clipboardUpdating;
    private NotifyIcon? _trayIcon;
    private bool _forceClose;

    // 全螢幕切換暫存
    private bool _isFullscreen;
    private FormWindowState _prevWindowState;
    private FormBorderStyle _prevBorderStyle;
    private Rectangle _prevBounds;

    public MainForm()
    {
        _hostConfig = HostConfig.Load();
        _viewerConfig = ViewerConfig.Load();

        _discovery = new LanDiscovery(_hostConfig.DeviceId);
        _server = new HostServer(_hostConfig, _discovery, this);

        InitializeComponent();
        InitializeTrayIcon();

        _keyboardHook = new KeyboardHook(
            () => Form.ActiveForm == this && _currentSession != null && _currentSession.Canvas.Focused,
            (vk, scan, flags) => _currentSession?.Session.SendKey(vk, scan, flags),
            () => _isFullscreen,
            () => BeginInvoke(ExitFullscreen));
    }

    private System.Windows.Forms.Timer? _deviceRefreshTimer;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        _discovery.Start();
        _discovery.DeviceUpdated += _ => BeginInvoke(() => { if (!IsDisposed) RefreshDeviceCards(); });

        _deviceRefreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _deviceRefreshTimer.Tick += (_, _) => { if (!IsDisposed && _pnlHomeView.Visible) RefreshDeviceCards(); };
        _deviceRefreshTimer.Start();

        try
        {
            _server.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"啟動被控服務失敗：{ex.Message}\n本機仍可作為控制端連線至其他電腦。", "服務提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Task.Run(() =>
        {
            try
            {
                var wp = DesktopHelper.GetDesktopWallpaperBytes();
                if (wp != null && wp.Length > 0)
                {
                    var thumbDir = Path.Combine(AppConfig.DataDirectory, "thumbnails");
                    Directory.CreateDirectory(thumbDir);
                    string selfThumb = Path.Combine(thumbDir, $"{_hostConfig.DeviceId}.jpg");
                    File.WriteAllBytes(selfThumb, wp);
                }
            }
            catch { }
        });

        UpdateLocalNetworkInfo();
        RefreshDeviceCards();
        UpdateHeaderLayout();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32.AddClipboardFormatListener(Handle);

        try
        {
            // 1. 啟用 Windows 10/11 沉浸式深色模式
            Win32.EnableImmersiveDarkMode(Handle);

            // 2. Windows 11 強制停用系統圓角遮罩 (DWMWCP_DONOTROUND = 1)，
            // 徹底消除無邊框視窗在四個角落因 DWM 圓角裁剪與透明通道產生的「三角形透明外框縫隙」
            Win32.DisableWindowRounding(Handle);

            // 3. 設定 Windows 11 DWM 邊界外框色彩為深色邊界 Theme.Border，讓系統外框與主程式完美融合
            Win32.SetDwmBorderColor(Handle, Theme.Border);
            Win32.SetDwmCaptionColor(Handle, Theme.SurfaceElevated);

            // 4. 強制通知 Windows 立即刷新非客戶區 (SWP_FRAMECHANGED)，
            // 確保 WM_NCCALCSIZE 在視窗初始化階段即完全生效，避免初始非客戶區殘留預設白框 (bug.png)
            Win32.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
        }
        catch { }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Win32.RemoveClipboardFormatListener(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX | Win32.WS_THICKFRAME;
            cp.ClassStyle |= Win32.CS_DROPSHADOW;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        // 1. 攔截 WM_NCCALCSIZE：消除 WS_THICKFRAME 預設產生的 ~8px 透明非客戶區邊框，
        // 將客戶區擴展至視窗 100% 範圍，徹底根除「周圍多了一圈透明外框」問題，同時保留原生邊界拖曳縮放與 Aero Snap
        if (m.Msg == Win32.WM_NCCALCSIZE)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        // 2. 攔截 WM_NCPAINT：阻止 Windows DefWindowProc 在客戶區上方繪製預設原生非客戶區邊框 (徹底消除白框 bug.png)
        if (m.Msg == Win32.WM_NCPAINT)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        // 3. 攔截 WM_NCACTIVATE：當視窗切換焦點 (啟動/失去焦點) 時，
        // 傳遞 lParam = -1 告知 DefWindowProc 更新內部狀態但「不要重繪」非客戶區，
        // 徹底根除失去/獲得焦點時邊框突然浮現白框與右上角原生按鈕的現象 (bug.png)
        if (m.Msg == Win32.WM_NCACTIVATE)
        {
            m.Result = Win32.DefWindowProc(m.HWnd, Win32.WM_NCACTIVATE, m.WParam, (IntPtr)(-1));
            return;
        }

        // 4. 攔截 WM_NCUAHDRAWCAPTION (0x00AE) 與 WM_NCUAHDRAWFRAME (0x00AF)：
        // 抑制 Windows Vista/10/11 主題引擎非同步強制重繪原生 Aero/Fluent 標題列與邊框 (Chromium / VS Code 核心防護機制)
        if (m.Msg == Win32.WM_NCUAHDRAWCAPTION || m.Msg == Win32.WM_NCUAHDRAWFRAME)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        // 5. 攔截 WM_THEMECHANGED 與 WM_DWMCOLORIZATIONCOLORCHANGED：
        // 系統主題更換或 DWM 顏色調整時重新鎖定沉浸式深色模式、直角與深色邊框設定
        if (m.Msg == Win32.WM_THEMECHANGED || m.Msg == Win32.WM_DWMCOLORIZATIONCOLORCHANGED)
        {
            Win32.EnableImmersiveDarkMode(Handle);
            Win32.DisableWindowRounding(Handle);
            Win32.SetDwmBorderColor(Handle, Theme.Border);
            Win32.SetDwmCaptionColor(Handle, Theme.SurfaceElevated);
        }

        // 6. 攔截 WM_GETMINMAXINFO：當無邊框視窗最大化時，精確對齊當前螢幕工作區 (WorkingArea)，
        // 防止標準厚邊框視窗向外溢出 8 像素或遮蓋 Windows 工作列
        if (m.Msg == Win32.WM_GETMINMAXINFO)
        {
            base.WndProc(ref m);
            if (IsHandleCreated)
            {
                var mmi = Marshal.PtrToStructure<Win32.MINMAXINFO>(m.LParam);
                var screen = Screen.FromHandle(Handle);
                var workArea = screen.WorkingArea;
                var bounds = screen.Bounds;

                mmi.ptMaxPosition.X = Math.Abs(workArea.Left - bounds.Left);
                mmi.ptMaxPosition.Y = Math.Abs(workArea.Top - bounds.Top);
                mmi.ptMaxSize.X = workArea.Width;
                mmi.ptMaxSize.Y = workArea.Height;
                mmi.ptMinTrackSize.X = MinimumSize.Width > 0 ? MinimumSize.Width : Theme.MinWindowWidth;
                mmi.ptMinTrackSize.Y = MinimumSize.Height > 0 ? MinimumSize.Height : Theme.MinWindowHeight;

                Marshal.StructureToPtr(mmi, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }
        }

        // 7. 自訂視窗邊界命中測試 (WM_NCHITTEST)：
        // 自行計算邊緣 8px 感應區，不呼叫 base.WndProc，避免 DefWindowProc 誤回傳 HTCLOSE/HTCAPTION 導致 Windows 強制繪製原生標題列與白框
        if (m.Msg == Win32.WM_NCHITTEST && !_isFullscreen)
        {
            if (WindowState == FormWindowState.Normal)
            {
                int lParam = (int)m.LParam.ToInt64();
                short screenX = (short)(lParam & 0xFFFF);
                short screenY = (short)((lParam >> 16) & 0xFFFF);
                Point clientPt = PointToClient(new Point(screenX, screenY));
                int border = 8;

                bool isTop = clientPt.Y <= border;
                bool isBottom = clientPt.Y >= ClientSize.Height - border;
                bool isLeft = clientPt.X <= border;
                bool isRight = clientPt.X >= ClientSize.Width - border;

                if (isTop && isLeft) { m.Result = (IntPtr)Win32.HTTOPLEFT; return; }
                if (isTop && isRight) { m.Result = (IntPtr)Win32.HTTOPRIGHT; return; }
                if (isBottom && isLeft) { m.Result = (IntPtr)Win32.HTBOTTOMLEFT; return; }
                if (isBottom && isRight) { m.Result = (IntPtr)Win32.HTBOTTOMRIGHT; return; }
                if (isTop) { m.Result = (IntPtr)Win32.HTTOP; return; }
                if (isBottom) { m.Result = (IntPtr)Win32.HTBOTTOM; return; }
                if (isLeft) { m.Result = (IntPtr)Win32.HTLEFT; return; }
                if (isRight) { m.Result = (IntPtr)Win32.HTRIGHT; return; }
            }
            m.Result = (IntPtr)Win32.HTCLIENT;
            return;
        }

        if (m.Msg == Win32.WM_CLIPBOARDUPDATE)
        {
            if (!_clipboardUpdating && _currentSession != null && ContainsFocus && _viewerConfig.SyncClipboard)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        var text = Clipboard.GetText();
                        if (!string.IsNullOrEmpty(text))
                            _currentSession.Session.SendClipboard(text);
                    }
                }
                catch { }
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (WindowState == FormWindowState.Normal && !_isFullscreen)
        {
            using var pen = new Pen(Theme.Border, 1);
            e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Normal)
        {
            MaximizedBounds = Rectangle.Empty;
            if (IsHandleCreated) Win32.DisableWindowRounding(Handle);
        }
        if (_pnlHomeView != null)
        {
            _pnlHomeView.AutoScroll = false;
            _pnlHomeView.AutoScroll = true;
        }
        if (_btnWinMaximize != null)
        {
            _btnWinMaximize.Text = WindowState == FormWindowState.Maximized ? "🗗" : "🗖";
            _sessionToolTip.SetToolTip(_btnWinMaximize, WindowState == FormWindowState.Maximized ? "向下還原" : "最大化");
        }
        Invalidate();
    }

    private void ToggleMaximize()
    {
        if (_isFullscreen) return;

        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
            MaximizedBounds = Rectangle.Empty;
            if (_pnlHomeView != null)
            {
                _pnlHomeView.AutoScroll = false;
                _pnlHomeView.AutoScroll = true;
            }
            _btnWinMaximize.Text = "🗖";
            _sessionToolTip.SetToolTip(_btnWinMaximize, "最大化");
        }
        else
        {
            MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
            WindowState = FormWindowState.Maximized;
            _btnWinMaximize.Text = "🗗";
            _sessionToolTip.SetToolTip(_btnWinMaximize, "向下還原");
        }
        Invalidate();
    }

    private void InitializeComponent()
    {
        Text = "RemoteDesk";
        FormBorderStyle = FormBorderStyle.None;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
        Size = new Size(Theme.DefaultWindowWidth, Theme.DefaultWindowHeight);
        MinimumSize = new Size(Theme.MinWindowWidth, Theme.MinWindowHeight);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        Font = Theme.BodyFont;
        KeyPreview = true;
        AppBranding.ApplyToForm(this);

        // =========================================================================
        // 1. 頂部區域 (Custom Window Chrome & 標題列 - 規格 4.3)
        // =========================================================================
        _pnlHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.TitleBarHeight,
            BackColor = Theme.Surface,
            Padding = new Padding(0)
        };
        _pnlHeader.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && !_isFullscreen)
            {
                if (e.Clicks == 2)
                {
                    ToggleMaximize();
                }
                else
                {
                    Win32.ReleaseCapture();
                    Win32.SendMessage(Handle, Win32.WM_NCLBUTTONDOWN, (IntPtr)Win32.HT_CAPTION, IntPtr.Zero);
                }
            }
        };
        _pnlHeader.Paint += (_, e) =>
        {
            if (WindowState == FormWindowState.Normal && !_isFullscreen)
            {
                using var borderPen = new Pen(Theme.Border, 1);
                e.Graphics.DrawLine(borderPen, 0, 0, _pnlHeader.Width - 1, 0); // Top
                e.Graphics.DrawLine(borderPen, 0, 0, 0, _pnlHeader.Height);    // Left
                e.Graphics.DrawLine(borderPen, _pnlHeader.Width - 1, 0, _pnlHeader.Width - 1, _pnlHeader.Height); // Right
            }
            using var sepPen = new Pen(Theme.Border, 1);
            e.Graphics.DrawLine(sepPen, 0, _pnlHeader.Height - 1, _pnlHeader.Width, _pnlHeader.Height - 1);
        };

        // 左側品牌 Logo (套用去背透明之現代 RemoteDesk 向量圖標與標題)
        // 寬度動態根據文字測量 + 22x22 Logo + 間距，保證文字不被遮蔽截斷；高度 32 配合 Header (52px) 精準垂直置中
        int initialLogoW = 185;
        try
        {
            using var initFont = Theme.CreateFont(13.5f, FontStyle.Bold);
            var measured = TextRenderer.MeasureText("RemoteDesk", initFont, new Size(int.MaxValue, 32), TextFormatFlags.NoPadding);
            initialLogoW = Math.Max(180, 22 + 8 + measured.Width + 12);
        }
        catch { }

        _lblLogo = new Label
        {
            Location = new Point(14, (Theme.HeaderHeight - 32) / 2),
            Size = new Size(initialLogoW, 32),
            Cursor = Cursors.Hand,
            BackColor = Color.Transparent
        };
        _lblLogo.Paint += (_, pe) =>
        {
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int clientW = _lblLogo.ClientSize.Width;
            int clientH = _lblLogo.ClientSize.Height;

            int iconSize = 22;
            int gap = 8;
            int iconX = 0;
            int iconY = (clientH - iconSize) / 2;

            // 1. 繪製去背品牌 Logo (與標題精準垂直置中且視覺比例協調)
            var logo = AppBranding.GetLogoBitmap(iconSize, iconSize);
            g.DrawImage(logo, new Rectangle(iconX, iconY, iconSize, iconSize));

            // 2. 繪製品牌標題 "RemoteDesk" (精準垂直置中，文字完整不被遮蔽)
            using var font = Theme.CreateFont(13.5f, FontStyle.Bold);
            int textX = iconX + iconSize + gap;
            int textW = Math.Max(0, clientW - textX);
            var textRect = new Rectangle(textX, 0, textW, clientH);
            
            TextRenderer.DrawText(g, "RemoteDesk", font, textRect, Theme.TextPrimary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        };
        _lblLogo.Click += (_, _) => SwitchToHomeTab();
        _sessionToolTip.SetToolTip(_lblLogo, "點擊返回首頁 (連線在後台保持)");

        // 首頁服務就緒狀態標籤 (點擊或 Hover 顯示監聽 Port、本機 IP、伺服端狀態)
        _lblHomeStatusBadge = new Label
        {
            Text = "● 服務就緒",
            Font = Theme.CreateFont(8.5f, FontStyle.Bold),
            ForeColor = Theme.Success,
            BackColor = Color.FromArgb(20, 50, 35),
            Padding = new Padding(6, 2, 6, 2),
            AutoSize = true,
            Visible = true,
            Cursor = Cursors.Hand
        };
        _lblHomeStatusBadge.Click += (_, _) =>
        {
            string? tip = _sessionToolTip.GetToolTip(_lblHomeStatusBadge);
            if (!string.IsNullOrEmpty(tip))
            {
                _sessionToolTip.Show(tip, _lblHomeStatusBadge, 0, _lblHomeStatusBadge.Height + 4, 6000);
            }
        };
        _sessionToolTip.SetToolTip(_lblHomeStatusBadge, $"【本機伺服端狀態】\n• 監聽連接埠：Port {_hostConfig.TcpPort}\n• 服務狀態：已就緒 (等待遠端連入)\n\n※ 點擊可顯示即時伺服端狀態");

        // 遠端設備名稱 (連線時顯示於 Logo 右側)
        _lblSessionTitle = new Label
        {
            Text = "遠端連線",
            Font = Theme.CreateFont(9.5f, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            MaximumSize = new Size(200, 24),
            AutoEllipsis = true,
            AutoSize = true,
            Visible = false,
            Cursor = Cursors.Default
        };

        // 連線品質與延遲標籤 (點擊或 Hover 顯示即時延遲、FPS、解析度、Port)
        _lblSessionStatusBadge = new Label
        {
            Text = "● 0ms",
            Font = Theme.CreateFont(8.5f, FontStyle.Bold),
            ForeColor = Theme.Success,
            BackColor = Color.FromArgb(20, 50, 35),
            Padding = new Padding(6, 2, 6, 2),
            AutoSize = true,
            Visible = false,
            Cursor = Cursors.Hand
        };
        _lblSessionStatusBadge.MouseEnter += (_, _) =>
        {
            if (_currentSession != null) UpdateSessionMetrics(_currentSession);
        };
        _lblSessionStatusBadge.Click += (_, _) =>
        {
            if (_currentSession != null)
            {
                UpdateSessionMetrics(_currentSession);
                string? tip = _sessionToolTip.GetToolTip(_lblSessionStatusBadge);
                if (!string.IsNullOrEmpty(tip))
                {
                    _sessionToolTip.Show(tip, _lblSessionStatusBadge, 0, _lblSessionStatusBadge.Height + 4, 6000);
                }
            }
        };

        // 頂部多工作階段標籤列 (連線時動態顯示)
        _pnlHeaderTabs = new FlowLayoutPanel
        {
            Location = new Point(180, 5),
            Size = new Size(300, 32),
            AutoScroll = false,
            WrapContents = false,
            BackColor = Color.Transparent
        };

        // 首頁模式右側設定按鈕 (純單行防折行排版：AutoEllipsis 啟用，保證圖標與「設定」文字同在一行絕不上下換行成兩行)
        _btnHeaderSettings = new Button
        {
            Image = VectorIcons.CreateGearBitmap(16, 16, Theme.Accent),
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ImageAlign = ContentAlignment.MiddleCenter,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "設定",
            Size = new Size(88, 30),
            MinimumSize = new Size(88, 30),
            MaximumSize = new Size(88, 30),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            AutoEllipsis = true,
            Padding = new Padding(6, 0, 6, 0)
        };
        _btnHeaderSettings.FlatAppearance.BorderColor = Theme.Border;
        _btnHeaderSettings.Click += (_, _) => OpenSettingsDialog();
        _sessionToolTip.SetToolTip(_btnHeaderSettings, "設定 (連線、安全性與顯示品質)");

        // ---------------- Custom Window Chrome 控制按鈕 (最小化、最大化/還原、關閉) ----------------
        _btnWinClose = new Button
        {
            Text = "✕",
            Size = new Size(44, Theme.TitleBarHeight),
            BackColor = Color.Transparent,
            ForeColor = Theme.TextSecondary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9f),
            Cursor = Cursors.Hand
        };
        _btnWinClose.FlatAppearance.BorderSize = 0;
        _btnWinClose.MouseEnter += (_, _) =>
        {
            _btnWinClose.BackColor = Color.FromArgb(232, 17, 35);
            _btnWinClose.ForeColor = Color.White;
        };
        _btnWinClose.MouseLeave += (_, _) =>
        {
            _btnWinClose.BackColor = Color.Transparent;
            _btnWinClose.ForeColor = Theme.TextSecondary;
        };
        _btnWinClose.Click += (_, _) => Close();
        _sessionToolTip.SetToolTip(_btnWinClose, "關閉");

        _btnWinMaximize = new Button
        {
            Text = "🗖",
            Size = new Size(44, Theme.TitleBarHeight),
            BackColor = Color.Transparent,
            ForeColor = Theme.TextSecondary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(8.5f),
            Cursor = Cursors.Hand
        };
        _btnWinMaximize.FlatAppearance.BorderSize = 0;
        _btnWinMaximize.MouseEnter += (_, _) =>
        {
            _btnWinMaximize.BackColor = Theme.SurfaceElevated;
            _btnWinMaximize.ForeColor = Theme.TextPrimary;
        };
        _btnWinMaximize.MouseLeave += (_, _) =>
        {
            _btnWinMaximize.BackColor = Color.Transparent;
            _btnWinMaximize.ForeColor = Theme.TextSecondary;
        };
        _btnWinMaximize.Click += (_, _) => ToggleMaximize();
        _sessionToolTip.SetToolTip(_btnWinMaximize, "最大化");

        _btnWinMinimize = new Button
        {
            Text = "—",
            Size = new Size(44, Theme.TitleBarHeight),
            BackColor = Color.Transparent,
            ForeColor = Theme.TextSecondary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(8.5f),
            Cursor = Cursors.Hand
        };
        _btnWinMinimize.FlatAppearance.BorderSize = 0;
        _btnWinMinimize.MouseEnter += (_, _) =>
        {
            _btnWinMinimize.BackColor = Theme.SurfaceElevated;
            _btnWinMinimize.ForeColor = Theme.TextPrimary;
        };
        _btnWinMinimize.MouseLeave += (_, _) =>
        {
            _btnWinMinimize.BackColor = Color.Transparent;
            _btnWinMinimize.ForeColor = Theme.TextSecondary;
        };
        _btnWinMinimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _sessionToolTip.SetToolTip(_btnWinMinimize, "最小化");

        _pnlHeader.Controls.AddRange(new Control[]
        {
            _lblLogo, _lblHomeStatusBadge, _lblSessionTitle, _lblSessionStatusBadge, _pnlHeaderTabs,
            _btnHeaderSettings,
            _btnWinMinimize, _btnWinMaximize, _btnWinClose
        });
        _pnlHeader.Resize += (_, _) => UpdateHeaderLayout();

        // =========================================================================
        // 遠端工作階段次行操作工具列 (Row 2: 規格要求螢幕與設定等搬到下一行靠右擺放)
        // =========================================================================
        _pnlSessionToolbar = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.ToolbarHeight,
            BackColor = Theme.Surface,
            Padding = new Padding(12, 0, 12, 0)
        };
        _pnlSessionToolbar.Paint += (_, pe) =>
        {
            using var borderPen = new Pen(Theme.Border, 1f);
            pe.Graphics.DrawLine(borderPen, 0, _pnlSessionToolbar.Height - 1, _pnlSessionToolbar.Width, _pnlSessionToolbar.Height - 1);
        };
        _pnlSessionToolbar.Resize += (_, _) => UpdateSessionToolbarLayout();

        _lblSessionToolbarInfo = new Label
        {
            Text = "● TLS 1.3 端對端安全加密 • 畫面即時串流中",
            Font = Theme.CreateFont(8.5f),
            ForeColor = Theme.TextSecondary,
            Location = new Point(14, (Theme.ToolbarHeight - 20) / 2),
            AutoSize = true,
            AutoEllipsis = true
        };

        // AnyDesk 風格螢幕切換按鈕群 [ 1 ] [ 2 ] ...
        _pnlMonitors = new FlowLayoutPanel
        {
            Height = 30,
            AutoSize = false,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };

        // 分隔線
        _sepSession1 = new Panel
        {
            Size = new Size(1, 20),
            BackColor = Theme.Border
        };

        // 雙向輸入法同步 (圖標 ⌨)
        _btnImeMode = new Button
        {
            Text = "⌨",
            Size = new Size(34, 30),
            BackColor = Theme.Primary,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnImeMode.FlatAppearance.BorderSize = 0;
        _btnImeMode.Click += OnToggleImeModeClick;
        _sessionToolTip.SetToolTip(_btnImeMode, "雙向輸入法同步 (點擊切換 雙向綁定 / 本機組字)");

        // 快捷指令 (圖標 ⚡)
        _btnSpecialKeys = new Button
        {
            Text = "⚡",
            Size = new Size(34, 30),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9f),
            Cursor = Cursors.Hand
        };
        _btnSpecialKeys.FlatAppearance.BorderColor = Theme.Border;
        _btnSpecialKeys.Click += OnSpecialKeysMenuClick;
        _sessionToolTip.SetToolTip(_btnSpecialKeys, "發送系統快捷指令 (Ctrl+Alt+Del、Win+L、工作管理員)");

        // 剪貼簿同步 (圖標 📋)
        _btnClipboardSync = new Button
        {
            Text = "📋",
            Size = new Size(34, 30),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.Success,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9f),
            Cursor = Cursors.Hand
        };
        _btnClipboardSync.FlatAppearance.BorderColor = Theme.Border;
        _btnClipboardSync.Click += (_, _) =>
        {
            _viewerConfig.SyncClipboard = !_viewerConfig.SyncClipboard;
            _viewerConfig.Save();
            UpdateClipboardButtonState();
        };
        UpdateClipboardButtonState();

        // 全螢幕切換 (圖標 ⛶)
        _btnFullscreen = new Button
        {
            Text = "⛶",
            Size = new Size(34, 30),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9f),
            Cursor = Cursors.Hand
        };
        _btnFullscreen.FlatAppearance.BorderColor = Theme.Border;
        _btnFullscreen.Click += (_, _) => ToggleFullscreen();
        _sessionToolTip.SetToolTip(_btnFullscreen, "全螢幕 / 視窗化切換 (F11)");

        // 連線設定 (圖標 ⚙)
        _btnSessionSettings = new Button
        {
            Image = VectorIcons.CreateGearBitmap(15, 15, Theme.TextPrimary),
            ImageAlign = ContentAlignment.MiddleCenter,
            Size = new Size(34, 30),
            BackColor = Theme.SurfaceElevated,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        _btnSessionSettings.FlatAppearance.BorderColor = Theme.Border;
        _btnSessionSettings.Click += (_, _) => OpenSettingsDialog();
        _sessionToolTip.SetToolTip(_btnSessionSettings, "遠端連線與系統設定 (畫面品質、解析度、安全性)");

        // 中斷連線按鈕 (圖標 ✕，靠右對齊)
        _btnSessionDisconnect = new Button
        {
            Text = "✕",
            Size = new Size(34, 30),
            BackColor = Theme.Danger,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnSessionDisconnect.FlatAppearance.BorderSize = 0;
        _btnSessionDisconnect.Click += (_, _) =>
        {
            if (_currentSession != null) CloseSessionTab(_currentSession);
        };
        _sessionToolTip.SetToolTip(_btnSessionDisconnect, "中斷並結束目前遠端連線");

        _pnlSessionToolbar.Controls.AddRange(new Control[]
        {
            _lblSessionToolbarInfo,
            _pnlMonitors, _sepSession1,
            _btnImeMode, _btnSpecialKeys, _btnClipboardSync, _btnFullscreen, _btnSessionSettings,
            _btnSessionDisconnect
        });

        // ---------------- 被控端連入警示橫幅 ----------------
        _pnlIncomingBanner = new Panel
        {
            Dock = DockStyle.Top,
            Height = 38,
            BackColor = Color.FromArgb(20, 60, 40),
            Visible = false,
            Padding = new Padding(16, 4, 16, 4)
        };
        _lblIncomingBannerText = new Label
        {
            Text = "● 遠端使用者已連線",
            Font = Theme.CreateFont(9.5f, FontStyle.Bold),
            ForeColor = Theme.Success,
            Location = new Point(16, 8),
            AutoSize = true,
            AutoEllipsis = true
        };
        _btnDisconnectIncoming = new Button
        {
            Text = "中斷此連線",
            Size = new Size(110, 28),
            Location = new Point(_pnlIncomingBanner.Width - 126, 5),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Theme.Danger,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(8.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnDisconnectIncoming.FlatAppearance.BorderSize = 0;
        _btnDisconnectIncoming.Click += (_, _) => _server.DisconnectActiveSession();
        _pnlIncomingBanner.Controls.AddRange(new Control[] { _lblIncomingBannerText, _btnDisconnectIncoming });
        _pnlIncomingBanner.Resize += (_, _) =>
        {
            _btnDisconnectIncoming.Location = new Point(_pnlIncomingBanner.ClientSize.Width - 126, 5);
            _lblIncomingBannerText.MaximumSize = new Size(Math.Max(60, _pnlIncomingBanner.ClientSize.Width - 146), 26);
        };

        // ---------------- 連線中動態提示橫幅 (含旋轉進度環、即時狀態、取消按鈕) ----------------
        _pnlConnectingBanner = new Panel
        {
            Dock = DockStyle.Top,
            Height = 42,
            BackColor = Color.FromArgb(20, 32, 54),
            Visible = false,
            Padding = new Padding(16, 0, 16, 0)
        };
        _pnlConnectingBanner.Paint += (_, pe) =>
        {
            using var borderPen = new Pen(Color.FromArgb(40, 80, 140), 1f);
            pe.Graphics.DrawLine(borderPen, 0, _pnlConnectingBanner.Height - 1, _pnlConnectingBanner.Width, _pnlConnectingBanner.Height - 1);
        };

        _connectingSpinner = new SpinnerBox
        {
            Location = new Point(16, 10),
            Size = new Size(22, 22)
        };

        _lblConnectingText = new Label
        {
            Text = "正在連線...",
            Font = Theme.CreateFont(9.5f, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            Location = new Point(48, 11),
            AutoSize = true,
            AutoEllipsis = true,
            BackColor = Color.Transparent
        };

        _btnCancelConnect = new Button
        {
            Text = "✕ 取消連線",
            Size = new Size(96, 26),
            Location = new Point(_pnlConnectingBanner.Width - 112, 8),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(50, 24, 30),
            ForeColor = Color.FromArgb(255, 140, 140),
            Font = Theme.CreateFont(8.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnCancelConnect.FlatAppearance.BorderColor = Color.FromArgb(120, 45, 55);
        _btnCancelConnect.MouseEnter += (_, _) =>
        {
            _btnCancelConnect.BackColor = Theme.Danger;
            _btnCancelConnect.ForeColor = Color.White;
        };
        _btnCancelConnect.MouseLeave += (_, _) =>
        {
            _btnCancelConnect.BackColor = Color.FromArgb(50, 24, 30);
            _btnCancelConnect.ForeColor = Color.FromArgb(255, 140, 140);
        };
        _btnCancelConnect.Click += (_, _) =>
        {
            _lblConnectingText.Text = "連線已由使用者取消中...";
            _connectingCts?.Cancel();
        };

        _pnlConnectingBanner.Controls.AddRange(new Control[] { _connectingSpinner, _lblConnectingText, _btnCancelConnect });
        _pnlConnectingBanner.Resize += (_, _) =>
        {
            _btnCancelConnect.Location = new Point(_pnlConnectingBanner.ClientSize.Width - 112, 8);
            _lblConnectingText.MaximumSize = new Size(Math.Max(60, _pnlConnectingBanner.ClientSize.Width - 130), 24);
        };

        _connectingTimer.Tick += (_, _) =>
        {
            _connectingSpinnerAngle = (_connectingSpinnerAngle + 18f) % 360f;
            _connectingSpinner.Angle = _connectingSpinnerAngle;
            _connectingTickCount++;
            if (_connectingTickCount % 12 == 0)
            {
                _connectingDotCount = (_connectingDotCount % 3) + 1;
                string dots = new string('.', _connectingDotCount);
                _lblConnectingText.Text = $"正在連線至 {_connectingTarget} {dots,-3} 正在建立安全連線與交握";
            }
        };

        // =========================================================================
        // 2. 主體內容視圖容器 (首頁 vs 遠端連線工作階段)
        // =========================================================================
        _pnlBodyContainer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Background
        };
        _pnlBodyContainer.Paint += (_, e) =>
        {
            if (WindowState == FormWindowState.Normal && !_isFullscreen)
            {
                using var borderPen = new Pen(Theme.Border, 1);
                e.Graphics.DrawLine(borderPen, 0, 0, 0, _pnlBodyContainer.Height - 1); // Left
                e.Graphics.DrawLine(borderPen, _pnlBodyContainer.Width - 1, 0, _pnlBodyContainer.Width - 1, _pnlBodyContainer.Height - 1); // Right
                e.Graphics.DrawLine(borderPen, 0, _pnlBodyContainer.Height - 1, _pnlBodyContainer.Width - 1, _pnlBodyContainer.Height - 1); // Bottom
            }
        };

        // ---------------- 3A. 首頁視圖 (Home View - 規格第 4 節) ----------------
        _pnlHomeView = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.Background,
            Padding = new Padding(32, 20, 32, 24)
        };

        // 1. Remote Address 遠端連線輸入框 (位於本機 ID 上方！規格 4.1 & 4.4)
        var lblRemoteAddressTitle = new Label
        {
            Text = "Remote Address 遠端設備連線",
            Font = Theme.SectionTitleFont,
            ForeColor = Theme.TextPrimary,
            Location = new Point(32, 18),
            Size = new Size(400, 26)
        };

        _inputRemoteAddress = new RemoteAddressInput
        {
            Location = new Point(32, 56),
            Width = _pnlHomeView.ClientSize.Width - 64,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        _inputRemoteAddress.ConnectRequested += id => DoConnect(id);

        // 2. This Device 本機設備 ID 獨立區塊 (位於 Remote Address 下方，規格 4.5)
        var lblThisDeviceTitle = new Label
        {
            Text = "This Device 本機設備資訊",
            Font = Theme.SectionTitleFont,
            ForeColor = Theme.TextPrimary,
            Location = new Point(32, 132),
            Size = new Size(400, 26)
        };

        _pnlThisDevice = new RoundedCardPanel
        {
            Location = new Point(32, 170),
            Width = _pnlHomeView.ClientSize.Width - 64,
            Height = 160,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            CornerRadius = Theme.CornerRadius,
            FillColor = Theme.Surface,
            BorderColor = Theme.Border,
            Padding = new Padding(20, 12, 20, 12)
        };

        var lblYourIdLabel = new Label
        {
            Text = "Your Device ID (本機連線識別碼)",
            Font = Theme.CreateFont(10f),
            ForeColor = Theme.TextSecondary,
            Dock = DockStyle.Top,
            Height = 22,
            TextAlign = ContentAlignment.MiddleCenter
        };

        _lblMyId = new Label
        {
            Text = AppConfig.FormatId(_hostConfig.DeviceId),
            Font = Theme.DeviceIdDisplayFont,
            ForeColor = Theme.TextPrimary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand
        };
        _lblMyId.Click += (_, _) => CopyMyId();

        var pnlIdActions = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            BackColor = Color.Transparent
        };

        _btnCopyId = new Button
        {
            Text = "📋 Copy ID",
            Size = new Size(110, 28),
            Location = new Point((_pnlThisDevice.Width - 110) / 2, 2),
            Anchor = AnchorStyles.Top,
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(8.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnCopyId.FlatAppearance.BorderColor = Theme.Border;
        _btnCopyId.Click += (_, _) => CopyMyId();

        _lblMyStatus = new Label
        {
            Text = "● Ready (Port " + _hostConfig.TcpPort + ")",
            Font = Theme.SmallFont,
            ForeColor = Theme.Success,
            Dock = DockStyle.Bottom,
            Height = 22,
            TextAlign = ContentAlignment.MiddleCenter
        };

        pnlIdActions.Controls.AddRange(new Control[] { _btnCopyId, _lblMyStatus });
        _pnlThisDevice.Controls.AddRange(new Control[] { _lblMyId, lblYourIdLabel, pnlIdActions });
        _pnlThisDevice.Resize += (_, _) =>
        {
            _btnCopyId.Location = new Point((_pnlThisDevice.ClientSize.Width - _btnCopyId.Width) / 2, 2);
        };

        // 3. Recent Sessions / Favorites 頁籤與設備縮圖網格 (規格 4.6)
        _tabsDevices = new DeviceTabsControl
        {
            Location = new Point(32, 356),
            Width = _pnlHomeView.ClientSize.Width - 64,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        _tabsDevices.TabChanged += _ => RefreshDeviceCards();

        _pnlDevicesGrid = new FlowLayoutPanel
        {
            Location = new Point(32, 408),
            Width = _pnlHomeView.ClientSize.Width - 64,
            Height = 280,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            AutoScroll = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 4, 0, 16)
        };

        _lblDevicesEmpty = new Label
        {
            Text = "No recent connections yet. Enter a Remote Device ID above to connect.",
            ForeColor = Theme.TextSecondary,
            Font = Theme.BodyFont,
            AutoSize = false,
            Size = new Size(600, 36),
            Margin = new Padding(12, 24, 0, 0)
        };
        _pnlDevicesGrid.Controls.Add(_lblDevicesEmpty);

        _pnlHomeView.Controls.AddRange(new Control[]
        {
            lblRemoteAddressTitle, _inputRemoteAddress,
            lblThisDeviceTitle, _pnlThisDevice,
            _tabsDevices, _pnlDevicesGrid
        });

        _pnlHomeView.Resize += (_, _) =>
        {
            int contentW = Math.Max(200, _pnlHomeView.ClientSize.Width - 64);
            _inputRemoteAddress.Width = contentW;
            _pnlThisDevice.Width = contentW;
            _tabsDevices.Width = contentW;
            _pnlDevicesGrid.Width = contentW;
            _pnlHomeView.AutoScrollMinSize = Size.Empty;
        };

        // ---------------- 3B. 遠端連線工作階段視圖 (Remote Session View) ----------------
        _pnlSessionView = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Background,
            Visible = false
        };

        // 遠端桌面畫布容器 (填滿主要視野)
        _pnlSessionCanvasContainer = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Black
        };

        // 先加入畫布容器 Dock=Fill，再加入次行工具列 Dock=Top；在 WinForms 中 Dock=Top 需 SendToBack 才能正確優先佔據頂部空間
        _pnlSessionView.Controls.Add(_pnlSessionCanvasContainer);
        _pnlSessionView.Controls.Add(_pnlSessionToolbar);
        _pnlSessionToolbar.SendToBack();

        // 加入主要容器
        _pnlBodyContainer.Controls.Add(_pnlSessionView);
        _pnlBodyContainer.Controls.Add(_pnlHomeView);

        Controls.Add(_pnlBodyContainer);
        Controls.Add(_pnlConnectingBanner);
        Controls.Add(_pnlIncomingBanner);
        Controls.Add(_pnlHeader);

        // 註冊邊界穿透濾鏡，確保子面板在視窗外圍 8px 感應區透明，讓 MainForm 接收 WM_NCHITTEST 達成 8 方向自由拉取縮放
        _ = new BorderHitTestFilter(_pnlHeader, this);
        _ = new BorderHitTestFilter(_pnlBodyContainer, this);
        _ = new BorderHitTestFilter(_pnlHomeView, this);
        _ = new BorderHitTestFilter(_pnlSessionView, this);
        _ = new BorderHitTestFilter(_pnlSessionToolbar, this);
        _ = new BorderHitTestFilter(_pnlSessionCanvasContainer, this);
        _ = new BorderHitTestFilter(_pnlConnectingBanner, this);
        _ = new BorderHitTestFilter(_pnlIncomingBanner, this);

        UpdateHeaderLayout();
    }

    private void UpdateHeaderLayout()
    {
        if (_pnlHeader == null || _lblLogo == null || _btnHeaderSettings == null) return;

        int headerH = _pnlHeader.ClientSize.Height > 0 ? _pnlHeader.ClientSize.Height : Theme.TitleBarHeight;
        int headerW = _pnlHeader.ClientSize.Width > 0 ? _pnlHeader.ClientSize.Width : Width;

        // 1. 最右側：自訂視窗控制按鈕 [ — ] [ 🗖 ] [ ✕ ] (高 40，寬 44)
        if (_btnWinClose != null && _btnWinMaximize != null && _btnWinMinimize != null)
        {
            _btnWinClose.Size = new Size(44, headerH);
            _btnWinClose.Location = new Point(headerW - 44, 0);

            _btnWinMaximize.Size = new Size(44, headerH);
            _btnWinMaximize.Location = new Point(_btnWinClose.Left - 44, 0);

            _btnWinMinimize.Size = new Size(44, headerH);
            _btnWinMinimize.Location = new Point(_btnWinMaximize.Left - 44, 0);
        }

        int winControlsLeft = _btnWinMinimize?.Left ?? (headerW - 132);

        // 2. 左上角：品牌 Logo (頂部區域精準垂直置中)
        _lblLogo.Location = new Point(14, (headerH - _lblLogo.Height) / 2);

        if (_currentSession == null)
        {
            // ==================== 首頁模式 (Home Mode) ====================
            _lblSessionTitle.Visible = false;
            _lblSessionStatusBadge.Visible = false;

            // 首頁設定按鈕 (位於視窗控制按鈕左側，尺寸與單行圖標文字固定，視窗縮小時絕不折行)
            _btnHeaderSettings.Visible = true;
            _btnHeaderSettings.Size = new Size(88, 30);
            _btnHeaderSettings.Text = "設定";
            _btnHeaderSettings.Location = new Point(winControlsLeft - 10 - _btnHeaderSettings.Width, (headerH - _btnHeaderSettings.Height) / 2);

            // 左側服務就緒燈號與中間多標籤列
            int availHome = _btnHeaderSettings.Left - 12 - (_lblLogo.Right + 12);
            if (availHome >= 90)
            {
                _lblHomeStatusBadge.Visible = true;
                _lblHomeStatusBadge.Location = new Point(_lblLogo.Right + 12, (headerH - _lblHomeStatusBadge.Height) / 2);

                int tabsLeft = _lblHomeStatusBadge.Right + 14;
                int tabsWidth = (_btnHeaderSettings.Left - 12) - tabsLeft;
                _pnlHeaderTabs.Visible = _sessionTabs.Count > 0 && tabsWidth >= 120;
                _pnlHeaderTabs.Location = new Point(tabsLeft, (headerH - 30) / 2);
                _pnlHeaderTabs.Size = new Size(Math.Max(0, tabsWidth), 30);
            }
            else
            {
                _lblHomeStatusBadge.Visible = false;
                _pnlHeaderTabs.Visible = false;
            }
        }
        else
        {
            // ==================== 遠端連線模式 (Session Mode) ====================
            _lblHomeStatusBadge.Visible = false;
            _btnHeaderSettings.Visible = false;

            // 標題、Logo 還有連線資訊移到左上角位置 (規格要求)
            int rightBoundary = winControlsLeft - 12;
            int availableLeftSpace = rightBoundary - (_lblLogo.Right + 12);

            if (availableLeftSpace >= 180)
            {
                // 寬度充足：顯示完整 Logo + 電腦名稱 (省略號) + 延遲燈號 + 多連線標籤列
                int titleMaxW = Math.Min(220, availableLeftSpace - 80);
                _lblSessionTitle.MaximumSize = new Size(Math.Max(60, titleMaxW), 24);
                _lblSessionTitle.AutoEllipsis = true;
                _lblSessionTitle.Visible = true;
                _lblSessionTitle.Location = new Point(_lblLogo.Right + 12, (headerH - _lblSessionTitle.Height) / 2);

                _lblSessionStatusBadge.Visible = true;
                _lblSessionStatusBadge.Location = new Point(_lblSessionTitle.Right + 8, (headerH - _lblSessionStatusBadge.Height) / 2);

                int tabsLeft = _lblSessionStatusBadge.Right + 12;
                int tabsWidth = rightBoundary - tabsLeft;
                _pnlHeaderTabs.Visible = _sessionTabs.Count > 1 && tabsWidth >= 120;
                _pnlHeaderTabs.Location = new Point(tabsLeft, (headerH - 30) / 2);
                _pnlHeaderTabs.Size = new Size(Math.Max(0, tabsWidth), 30);
            }
            else if (availableLeftSpace >= 70)
            {
                // 寬度較緊湊：隱藏電腦名稱與多標籤，在 Logo 旁保留延遲圓點
                _lblSessionTitle.Visible = false;
                _pnlHeaderTabs.Visible = false;
                _lblSessionStatusBadge.Visible = true;
                _lblSessionStatusBadge.Location = new Point(_lblLogo.Right + 8, (headerH - _lblSessionStatusBadge.Height) / 2);
            }
            else
            {
                // 極度狹窄：只顯示 Logo
                _lblSessionTitle.Visible = false;
                _lblSessionStatusBadge.Visible = false;
                _pnlHeaderTabs.Visible = false;
            }

            UpdateSessionToolbarLayout();
        }
    }

    private void UpdateSessionToolbarLayout()
    {
        if (_pnlSessionToolbar == null) return;

        int toolbarH = _pnlSessionToolbar.ClientSize.Height > 0 ? _pnlSessionToolbar.ClientSize.Height : Theme.ToolbarHeight;
        int btnH = 30;
        int btnY = (toolbarH - btnH) / 2;

        int right = _pnlSessionToolbar.ClientSize.Width - 12;

        // 右側：操作功能選單 (嚴格從次行視窗右緣向左依序排布，靠右擺放)
        // [ ✕ ] 結束連線 (最右邊)
        _btnSessionDisconnect.Size = new Size(34, btnH);
        _btnSessionDisconnect.Location = new Point(right - _btnSessionDisconnect.Width, btnY);
        right -= _btnSessionDisconnect.Width + 8;

        // [ ⚙ ] 設定 (緊鄰結束連線左側)
        _btnSessionSettings.Size = new Size(34, btnH);
        _btnSessionSettings.Location = new Point(right - _btnSessionSettings.Width, btnY);
        right -= _btnSessionSettings.Width + 4;

        // [ ⛶ ] 全螢幕
        _btnFullscreen.Size = new Size(34, btnH);
        _btnFullscreen.Location = new Point(right - _btnFullscreen.Width, btnY);
        right -= _btnFullscreen.Width + 4;

        // [ 📋 ] 剪貼簿同步
        _btnClipboardSync.Size = new Size(34, btnH);
        _btnClipboardSync.Location = new Point(right - _btnClipboardSync.Width, btnY);
        right -= _btnClipboardSync.Width + 4;

        // [ ⚡ ] 指令選單
        _btnSpecialKeys.Size = new Size(34, btnH);
        _btnSpecialKeys.Location = new Point(right - _btnSpecialKeys.Width, btnY);
        right -= _btnSpecialKeys.Width + 4;

        // [ ⌨ ] 雙向輸入法同步
        _btnImeMode.Size = new Size(34, btnH);
        _btnImeMode.Location = new Point(right - _btnImeMode.Width, btnY);
        right -= _btnImeMode.Width + 8;

        // 分隔線
        _sepSession1.Size = new Size(1, 20);
        _sepSession1.Location = new Point(right - _sepSession1.Width, (toolbarH - 20) / 2);
        right -= _sepSession1.Width + 8;

        // AnyDesk 螢幕切換按鈕群 [ 1 ] [ 2 ] ...
        int monWidth = 0;
        foreach (Control c in _pnlMonitors.Controls)
        {
            monWidth += c.Width + c.Margin.Horizontal;
        }
        if (monWidth == 0) monWidth = 40;

        _pnlMonitors.Size = new Size(monWidth, btnH);
        _pnlMonitors.Location = new Point(right - monWidth, btnY);
        right -= monWidth;

        // 左側：連線安全性與即時串流資訊
        _lblSessionToolbarInfo.Location = new Point(14, (toolbarH - _lblSessionToolbarInfo.Height) / 2);
        int availToolbarInfo = right - 14 - 10;
        if (availToolbarInfo >= 100)
        {
            _lblSessionToolbarInfo.Visible = true;
            _lblSessionToolbarInfo.MaximumSize = new Size(availToolbarInfo, 22);
        }
        else
        {
            _lblSessionToolbarInfo.Visible = false;
        }
    }

    private Button CreateNavTabButton(string text, bool isActive)
    {
        var btn = new Button
        {
            Text = text,
            Size = new Size(130, 32),
            MinimumSize = new Size(110, 32),
            MaximumSize = new Size(150, 32),
            Margin = new Padding(0, 0, 4, 0),
            BackColor = isActive ? Theme.SurfaceElevated : Theme.Surface,
            ForeColor = isActive ? Theme.Accent : Theme.TextSecondary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.CreateFont(9f, isActive ? FontStyle.Bold : FontStyle.Regular),
            Cursor = Cursors.Hand,
            AutoEllipsis = true
        };
        btn.FlatAppearance.BorderColor = isActive ? Theme.Accent : Theme.Border;
        return btn;
    }

    private void UpdateClipboardButtonState()
    {
        _btnClipboardSync.Text = "📋";
        _btnClipboardSync.ForeColor = _viewerConfig.SyncClipboard ? Theme.Success : Theme.TextSecondary;
        _btnClipboardSync.BackColor = _viewerConfig.SyncClipboard ? Color.FromArgb(20, 50, 35) : Theme.SurfaceElevated;
        _sessionToolTip.SetToolTip(_btnClipboardSync, _viewerConfig.SyncClipboard ? "雙向剪貼簿同步：開啟 (點擊停用)" : "雙向剪貼簿同步：關閉 (點擊啟用)");
    }

    private void SwitchToHomeTab()
    {
        ExitFullscreen();
        _currentSession = null;

        foreach (var item in _sessionTabs)
        {
            item.TabButton.BackColor = Theme.Surface;
            item.TabButton.ForeColor = Theme.TextSecondary;
            item.TabButton.FlatAppearance.BorderColor = Theme.Border;
        }

        _pnlHomeView.Visible = true;
        _pnlSessionView.Visible = false;
        _pnlHomeView.BringToFront();

        UpdateHeaderLayout();

        _inputRemoteAddress.FocusInput();
        RefreshDeviceCards();
    }

    private void SwitchToSessionTab(SessionTabItem item)
    {
        _currentSession = item;

        foreach (var s in _sessionTabs)
        {
            bool active = s == item;
            s.TabButton.BackColor = active ? Theme.SurfaceElevated : Theme.Surface;
            s.TabButton.ForeColor = active ? Theme.Accent : Theme.TextSecondary;
            s.TabButton.FlatAppearance.BorderColor = active ? Theme.Accent : Theme.Border;
        }

        _lblSessionTitle.Text = $"{item.Title} ({AppConfig.FormatId(item.Session.HostDeviceId)})";
        _lblSessionToolbarInfo.Text = $"● TLS 1.3 端對端安全加密 • 正在串流 {item.Title}";
        UpdateSessionMetrics(item);
        UpdateRemoteControlsBar(item);

        _pnlSessionCanvasContainer.Controls.Clear();
        _pnlSessionCanvasContainer.Controls.Add(item.Canvas);
        item.Canvas.Dock = DockStyle.Fill;

        _pnlSessionView.Visible = true;
        _pnlHomeView.Visible = false;
        _pnlSessionView.BringToFront();

        UpdateHeaderLayout();
        UpdateSessionToolbarLayout();

        item.Canvas.Focus();
    }

    private void UpdateSessionMetrics(SessionTabItem item)
    {
        string qualityName = _viewerConfig.Quality >= 80 ? "高品質 (90%)" : (_viewerConfig.Quality >= 65 ? "標準 (70%)" : "極速 (50%)");
        string statusText = item.Session.CurrentRttMs < 50 ? "良好 (流暢)" : (item.Session.CurrentRttMs < 100 ? "普通" : "延遲較高");
        string metricsText = 
            $"【遠端連線品質與效能資訊】\n" +
            $"• 往返延遲 (RTT)：{item.Session.CurrentRttMs} ms\n" +
            $"• 畫面更新率 (FPS)：{item.Fps} FPS\n" +
            $"• 畫面編碼品質：{qualityName}\n" +
            $"• 遠端螢幕解析度：{item.Session.RemoteWidth} × {item.Session.RemoteHeight}\n" +
            $"• 傳輸連接埠：Port {_hostConfig.TcpPort}\n" +
            $"• 網路狀態：{statusText}\n\n" +
            $"※ 點擊此按鈕可即時重新整理診斷資訊";

        _lblSessionStatusBadge.Text = $"● {item.Session.CurrentRttMs}ms";
        _lblSessionStatusBadge.ForeColor = item.Session.CurrentRttMs < 50 ? Theme.Success : (item.Session.CurrentRttMs < 100 ? Theme.Warning : Theme.Danger);
        _sessionToolTip.SetToolTip(_lblSessionStatusBadge, metricsText);
        _sessionToolTip.SetToolTip(_lblSessionTitle, metricsText);
    }

    private void UpdateRemoteControlsBar(SessionTabItem item)
    {
        _pnlMonitors.SuspendLayout();
        _pnlMonitors.Controls.Clear();

        var monitorList = item.Monitors.Count > 0 ? item.Monitors : (item.Session?.Monitors ?? new List<MonitorInfo>());
        if (monitorList.Count == 0)
        {
            var btn = CreateMonitorButton("1", true, "螢幕 1 (主顯示器)");
            _pnlMonitors.Controls.Add(btn);
        }
        else
        {
            for (int i = 0; i < monitorList.Count; i++)
            {
                var m = monitorList[i];
                int displayNumber = (m.Number > 0 && m.Number <= monitorList.Count) ? m.Number : (i + 1);
                bool isActive = (m.DeviceName == item.SelectedMonitor) || (string.IsNullOrEmpty(item.SelectedMonitor) && m.Primary);
                string tip = $"切換至螢幕 {displayNumber}{(m.Primary ? " [主螢幕]" : "")} ({m.Width}×{m.Height})";
                var btn = CreateMonitorButton(displayNumber.ToString(), isActive, tip);
                string devName = m.DeviceName;
                btn.Click += (_, _) =>
                {
                    if (_currentSession == null) return;
                    _currentSession.SelectedMonitor = devName;
                    _currentSession.Session.SelectMonitor(devName);
                    UpdateRemoteControlsBar(_currentSession);
                };
                _pnlMonitors.Controls.Add(btn);
            }

            if (monitorList.Count > 1)
            {
                bool isAllActive = item.SelectedMonitor == Protocol.AllMonitors;
                var btnAll = CreateMonitorButton("全", isAllActive, "所有螢幕 (合併檢視)");
                btnAll.Click += (_, _) =>
                {
                    if (_currentSession == null) return;
                    _currentSession.SelectedMonitor = Protocol.AllMonitors;
                    _currentSession.Session.SelectMonitor(Protocol.AllMonitors);
                    UpdateRemoteControlsBar(_currentSession);
                };
                _pnlMonitors.Controls.Add(btnAll);
            }
        }

        _pnlMonitors.ResumeLayout(true);
        UpdateImeButtonState(item.ImeClient.Mode);
        UpdateHeaderLayout();
        UpdateSessionToolbarLayout();
    }

    private Button CreateMonitorButton(string label, bool isActive, string tooltip)
    {
        Color iconColor = isActive ? Color.White : Theme.TextSecondary;
        var btn = new Button
        {
            Image = VectorIcons.CreateMonitorNumberBitmap(24, 18, label, iconColor),
            ImageAlign = ContentAlignment.MiddleCenter,
            Size = new Size(34, 30),
            MinimumSize = new Size(34, 30),
            MaximumSize = new Size(34, 30),
            Margin = new Padding(0, 0, 4, 0),
            BackColor = isActive ? Theme.Primary : Theme.SurfaceElevated,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderColor = isActive ? Theme.Accent : Theme.Border;
        _sessionToolTip.SetToolTip(btn, tooltip);
        return btn;
    }

    private void OnToggleImeModeClick(object? sender, EventArgs e)
    {
        if (_currentSession == null) return;
        if (_currentSession.ImeClient.Mode == ImeSyncMode.Bound)
        {
            _currentSession.ImeClient.Mode = ImeSyncMode.Local;
            _currentSession.Session.SendImeMode(ImeSyncMode.Local);
            UpdateImeButtonState(ImeSyncMode.Local);
        }
        else
        {
            _currentSession.ImeClient.Mode = ImeSyncMode.Bound;
            _currentSession.Session.SendImeMode(ImeSyncMode.Bound);
            UpdateImeButtonState(ImeSyncMode.Bound);
        }
    }

    private void UpdateImeButtonState(ImeSyncMode mode)
    {
        _btnImeMode.Text = "⌨";
        if (mode == ImeSyncMode.Bound)
        {
            _btnImeMode.BackColor = Theme.Primary;
            _btnImeMode.ForeColor = Color.White;
            _sessionToolTip.SetToolTip(_btnImeMode, "雙向輸入法同步：開啟 (點擊切換為本機組字)");
        }
        else
        {
            _btnImeMode.BackColor = Theme.SurfaceElevated;
            _btnImeMode.ForeColor = Theme.TextSecondary;
            _sessionToolTip.SetToolTip(_btnImeMode, "雙向輸入法同步：已關閉 (點擊切換為雙向綁定)");
        }
    }

    private void OnSpecialKeysMenuClick(object? sender, EventArgs e)
    {
        if (_currentSession == null) return;
        var menu = new ContextMenuStrip { BackColor = Theme.SurfaceElevated, ForeColor = Theme.TextPrimary };
        menu.Items.Add("傳送 Ctrl+Alt+Del", null, (_, _) => _currentSession?.Session.SendSpecial(SpecialAction.CtrlAltDel));
        menu.Items.Add("開啟工作管理員 (Ctrl+Shift+Esc)", null, (_, _) => _currentSession?.Session.SendSpecial(SpecialAction.TaskManager));
        menu.Items.Add("鎖定遠端電腦 (Win+L)", null, (_, _) => _currentSession?.Session.SendSpecial(SpecialAction.LockScreen));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("切換遠端中/英 (Shift)", null, (_, _) =>
        {
            _currentSession?.Session.SendKey(0x10, 0x2A, KeyFlags.None);
            _currentSession?.Session.SendKey(0x10, 0x2A, KeyFlags.Up);
        });
        menu.Show(_btnSpecialKeys, new Point(0, _btnSpecialKeys.Height));
    }

    private void CloseSessionTab(SessionTabItem item)
    {
        ExitFullscreen();
        try { item.Session.SendDisconnect(); } catch { }
        item.ImeClient.Dispose();
        item.Session.Dispose();

        _sessionTabs.Remove(item);
        _pnlHeaderTabs.Controls.Remove(item.TabButton);

        if (_currentSession == item)
        {
            SwitchToHomeTab();
        }
        else
        {
            UpdateHeaderLayout();
        }
    }

    private void SaveThumbnail(SessionTabItem item)
    {
        // 依據需求 4：設備代表圖片改採用設備桌面，不儲存螢幕截圖
    }

    private void CopyMyId()
    {
        Clipboard.SetText(_hostConfig.DeviceId);
        _btnCopyId.Text = "✓ Copied!";
        _btnCopyId.ForeColor = Theme.Success;

        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (_, _) =>
        {
            _btnCopyId.Text = "📋 Copy ID";
            _btnCopyId.ForeColor = Theme.TextPrimary;
            t.Stop();
            t.Dispose();
        };
        t.Start();
    }

    private void OpenSettingsDialog()
    {
        using var dlg = new SettingsForm(_hostConfig, _viewerConfig, () =>
        {
            RefreshDeviceCards();
            UpdateLocalNetworkInfo();
        });
        dlg.ShowDialog(this);
    }

    private void OpenHistoryDialog()
    {
        using var dlg = new ConnectionHistoryForm(_viewerConfig, id => DoConnect(id), () => RefreshDeviceCards());
        dlg.ShowDialog(this);
    }

    private void UpdateLocalNetworkInfo()
    {
        var ips = LanDiscovery.LocalIPv4().Select(x => x.ip.ToString()).ToList();
        string ipStr = ips.Count > 0 ? string.Join(", ", ips) : "127.0.0.1";
        string info = $"【本機伺服端狀態】\n• 監聽連接埠：Port {_hostConfig.TcpPort}\n• 本機 IP 位址：{ipStr}\n• 本機名稱：{Environment.MachineName}\n• 服務狀態：已就緒 (隨時可接受遠端連線)";
        _lblHomeStatusBadge.Text = "● 服務就緒";
        _lblMyStatus.Text = ips.Count > 0
            ? $"● 服務就緒  •  Port {_hostConfig.TcpPort}  •  IP: {string.Join(", ", ips)}"
            : $"● 服務就緒  •  Port {_hostConfig.TcpPort}";
        _sessionToolTip.SetToolTip(_lblHomeStatusBadge, info);
        _sessionToolTip.SetToolTip(_lblMyStatus, info);
    }

    private void RefreshDeviceCards()
    {
        var recent = _viewerConfig.GetRecent();
        var favs = _viewerConfig.GetFavorites();

        var lanDiscovered = _discovery.Devices
            .Where(d => !NetworkHelper.IsSelfTarget(d.Id, _hostConfig.DeviceId, _viewerConfig.ViewerId) &&
                        !NetworkHelper.IsSelfTarget(d.Address.ToString(), _hostConfig.DeviceId, _viewerConfig.ViewerId) &&
                        (DateTime.Now - d.LastSeen < TimeSpan.FromSeconds(30)))
            .OrderByDescending(d => d.LastSeen)
            .ToList();

        _tabsDevices.UpdateCounts(recent.Count, favs.Count, lanDiscovered.Count);

        _pnlDevicesGrid.Controls.Clear();

        List<RecentEntry> list;
        if (_tabsDevices.SelectedIndex == 0)
        {
            list = recent;
        }
        else if (_tabsDevices.SelectedIndex == 1)
        {
            list = favs;
        }
        else
        {
            list = lanDiscovered.Select(d =>
            {
                var existing = _viewerConfig.Recent.FirstOrDefault(r => r.DeviceId == d.Id || r.Key == d.Id);
                string? thumbPath = existing?.ThumbnailPath;
                if (string.IsNullOrEmpty(thumbPath) || !File.Exists(thumbPath))
                {
                    string candidate = Path.Combine(AppConfig.DataDirectory, "thumbnails", $"{d.Id}.jpg");
                    if (File.Exists(candidate)) thumbPath = candidate;
                }
                return new RecentEntry
                {
                    Key = d.Id,
                    DeviceId = d.Id,
                    Name = !string.IsNullOrEmpty(d.Name) ? d.Name : (!string.IsNullOrEmpty(d.User) ? d.User : d.Id),
                    LastAddress = d.Address.ToString(),
                    Port = d.Port,
                    LastUsed = d.LastSeen,
                    IsFavorite = existing?.IsFavorite ?? false,
                    ThumbnailPath = thumbPath
                };
            }).ToList();
        }

        if (list.Count == 0)
        {
            _lblDevicesEmpty.Text = _tabsDevices.SelectedIndex switch
            {
                0 => "尚無連線紀錄。請在上方輸入遠端設備 ID 或 IP 進行連線。",
                1 => "尚無我的最愛設備。可在連線紀錄或區域網設備卡片上按右鍵加入我的最愛。",
                _ => "區域網路內目前未發現其他 RemoteDesk 設備。\n請確認其他電腦已開啟 RemoteDesk 並連上同一網路。"
            };
            _lblDevicesEmpty.Visible = true;
            _pnlDevicesGrid.Controls.Add(_lblDevicesEmpty);
            return;
        }

        _lblDevicesEmpty.Visible = false;
        foreach (var entry in list)
        {
            bool isOnline = _sessionTabs.Any(s => s.Key == entry.Key || (!string.IsNullOrEmpty(entry.DeviceId) && s.Session.HostDeviceId == entry.DeviceId))
                || _discovery.Devices.Any(d =>
                    ((!string.IsNullOrEmpty(entry.DeviceId) && d.Id == entry.DeviceId) ||
                     d.Id == entry.Key ||
                     d.Address.ToString() == entry.Key ||
                     d.EndPoint.ToString() == entry.Key)
                    && (DateTime.Now - d.LastSeen < TimeSpan.FromSeconds(25)));

            var card = new DeviceThumbnailCard(
                entry,
                isOnline,
                id => DoConnect(id),
                key =>
                {
                    _viewerConfig.ToggleFavorite(key);
                    RefreshDeviceCards();
                },
                key =>
                {
                    _viewerConfig.RemoveRecent(key);
                    RefreshDeviceCards();
                },
                (key, newName) =>
                {
                    var item = _viewerConfig.Recent.FirstOrDefault(x => x.Key == key || x.DeviceId == key);
                    if (item != null)
                    {
                        item.Name = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
                        _viewerConfig.Save();
                        RefreshDeviceCards();
                    }
                });

            _pnlDevicesGrid.Controls.Add(card);
        }
    }

    private async void DoConnect(string target)
    {
        string clean = target.Replace(" ", "").Trim();
        if (string.IsNullOrEmpty(clean))
        {
            MessageBox.Show(this, "請輸入遠端設備 9 位數 ID 或 IP 位址！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (NetworkHelper.IsSelfTarget(clean, _hostConfig.DeviceId, _viewerConfig.ViewerId))
        {
            MessageBox.Show(this, "禁止連線到本機自己的 IP 或 ID！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var existingSession = _sessionTabs.FirstOrDefault(s => s.Key == clean || s.Session.HostDeviceId == clean);
        if (existingSession != null)
        {
            SwitchToSessionTab(existingSession);
            return;
        }

        _inputRemoteAddress.IsLoading = true;
        StartConnectingAnimation(clean);

        _connectingCts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var token = _connectingCts.Token;

        ViewerSession? session = null;
        try
        {
            string? savedPwd = _viewerConfig.GetSavedPassword(clean);
            string? pwd = savedPwd;
            bool forceInteractive = false;
            string? errorHint = null;

            while (true)
            {
                session?.Dispose();
                session = new ViewerSession(clean, _viewerConfig, _discovery, _hostConfig.DeviceId);

                try
                {
                    await session.ConnectAsync(pwd, token, forceInteractive).ConfigureAwait(true);
                    if (session.HostDeviceId == _hostConfig.DeviceId || session.HostDeviceId == _viewerConfig.ViewerId)
                    {
                        session.Dispose();
                        StopConnectingAnimation();
                        MessageBox.Show(this, "禁止連線到本機自己的 IP 或 ID！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    break; // 身份驗證通過，連線建立完成
                }
                catch (UnauthorizedAccessException ex)
                {
                    StopConnectingAnimation();

                    bool isFirstPrompt = ex is NeedPasswordException;
                    bool allowInteractive = ex is NeedPasswordException np && np.AllowInteractive;

                    if (!isFirstPrompt)
                    {
                        errorHint = "⚠️ 連線密碼錯誤，請重新輸入：";
                    }

                    string hostDisplayName = !string.IsNullOrEmpty(session.HostDeviceName)
                        ? session.HostDeviceName
                        : clean;

                    using var pwdDlg = new ConnectPasswordDialog(
                        hostDisplayName,
                        clean,
                        pwd,
                        allowInteractive,
                        errorHint);

                    if (pwdDlg.ShowDialog(this) == DialogResult.OK)
                    {
                        forceInteractive = false;
                        pwd = pwdDlg.Password;
                        if (pwdDlg.RememberPassword)
                        {
                            _viewerConfig.RememberPassword(clean, pwd);
                            _viewerConfig.Save();
                        }

                        StartConnectingAnimation(clean);
                    }
                    else
                    {
                        session.Dispose();
                        return;
                    }
                }
            }

            // 連線成功，記錄到最近連線
            _viewerConfig.TouchRecent(clean, session.HostDeviceId, session.HostDeviceName, null, Protocol.DefaultTcpPort);
            _viewerConfig.Save();

            // 建立畫布與輸入法客戶端
            var canvas = new DesktopCanvas(session) { Dock = DockStyle.Fill };
            _ = new BorderHitTestFilter(canvas, this);
            var imeClient = new ImeSyncClient(canvas, s => session.SendImeSet(s), t => session.SendText(t));

            var tabItem = new SessionTabItem
            {
                Key = clean,
                Title = string.IsNullOrEmpty(session.HostDeviceName) ? clean : session.HostDeviceName,
                Session = session,
                Canvas = canvas,
                ImeClient = imeClient,
                SelectedMonitor = session.SelectedMonitor,
                Monitors = session.Monitors != null ? new List<MonitorInfo>(session.Monitors) : new List<MonitorInfo>(),
                LastFpsTime = Environment.TickCount64
            };

            // 建立頂部工作階段標籤
            var tabBtn = CreateNavTabButton($"🖥️ {tabItem.Title} ✕", isActive: false);
            tabBtn.Click += (_, _) =>
            {
                var pt = tabBtn.PointToClient(Cursor.Position);
                if (pt.X >= tabBtn.Width - 28)
                {
                    CloseSessionTab(tabItem);
                }
                else
                {
                    SwitchToSessionTab(tabItem);
                }
            };
            tabItem.TabButton = tabBtn;

            _sessionTabs.Add(tabItem);
            _pnlHeaderTabs.Controls.Add(tabBtn);

            // 註冊 Session 事件
            session.FrameReceived += (rect, seq) =>
            {
                tabItem.FrameCount++;
                long now = Environment.TickCount64;
                if (now - tabItem.LastFpsTime >= 1000)
                {
                    tabItem.Fps = (int)(tabItem.FrameCount * 1000.0f / (now - tabItem.LastFpsTime));
                    tabItem.FrameCount = 0;
                    tabItem.LastFpsTime = now;
                    if (_currentSession == tabItem)
                    {
                        BeginInvoke(() => UpdateSessionMetrics(tabItem));
                    }
                }

                BeginInvoke(() =>
                {
                    canvas.RecalculateLayout();
                    canvas.Invalidate();
                });
            };

            session.MonitorsUpdated += (monitors, sel) =>
            {
                tabItem.Monitors = new List<MonitorInfo>(monitors);
                tabItem.SelectedMonitor = sel;
                if (_currentSession == tabItem)
                {
                    BeginInvoke(() => UpdateRemoteControlsBar(tabItem));
                }
            };

            if (session.Monitors != null && session.Monitors.Count > 0)
            {
                tabItem.Monitors = new List<MonitorInfo>(session.Monitors);
            }

            session.CursorShapeChanged += cur => BeginInvoke(() => canvas.Cursor = cur ?? Cursors.Default);
            session.ImeStateReceived += state => imeClient.OnRemoteImeStateReceived(state);
            session.CaretReceived += (cx, cy, ch) => imeClient.UpdateCaretPosition(cx, cy, ch);
            session.ClipboardReceived += text =>
            {
                if (string.IsNullOrEmpty(text) || !_viewerConfig.SyncClipboard) return;
                BeginInvoke(() =>
                {
                    try
                    {
                        _clipboardUpdating = true;
                        Clipboard.SetText(text);
                    }
                    catch { }
                    finally { _clipboardUpdating = false; }
                });
            };

            session.RttUpdated += ms =>
            {
                if (_currentSession == tabItem)
                {
                    BeginInvoke(() => UpdateSessionMetrics(tabItem));
                }
            };

            session.WallpaperReceived += bytes =>
            {
                try
                {
                    var thumbDir = Path.Combine(AppConfig.DataDirectory, "thumbnails");
                    Directory.CreateDirectory(thumbDir);
                    string hostId = !string.IsNullOrEmpty(session.HostDeviceId) ? session.HostDeviceId : clean;
                    string safeKey = hostId.Replace(":", "_").Replace("/", "_").Replace("\\", "_");
                    string thumbPath = Path.Combine(thumbDir, $"{safeKey}.jpg");
                    File.WriteAllBytes(thumbPath, bytes);
                    _viewerConfig.UpdateThumbnail(clean, session.HostDeviceId, thumbPath);
                    BeginInvoke(() => { if (!IsDisposed) RefreshDeviceCards(); });
                }
                catch { }
            };

            session.Disconnected += reason => BeginInvoke(() =>
            {
                ExitFullscreen();
                if (_sessionTabs.Contains(tabItem))
                {
                    CloseSessionTab(tabItem);
                }
            });

            _inputRemoteAddress.TextValue = "";
            SwitchToSessionTab(tabItem);
        }
        catch (OperationCanceledException)
        {
            session?.Dispose();
            // 使用者手動取消連線，不彈出錯誤對話盒
        }
        catch (Exception ex)
        {
            session?.Dispose();
            if (_connectingCts == null || !_connectingCts.IsCancellationRequested)
            {
                MessageBox.Show(this, $"無法連線至遠端設備：\n{ex.Message}", "連線失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            StopConnectingAnimation();
            _inputRemoteAddress.IsLoading = false;
            _connectingCts?.Dispose();
            _connectingCts = null;
        }
    }

    private void StartConnectingAnimation(string target)
    {
        _connectingTarget = AppConfig.FormatId(target);
        _connectingSpinnerAngle = 0f;
        _connectingDotCount = 0;
        _connectingTickCount = 0;
        _connectingSpinner.Angle = 0f;
        _lblConnectingText.Text = $"正在連線至 {_connectingTarget} ... 正在建立安全連線與交握";
        _pnlConnectingBanner.Visible = true;
        _pnlConnectingBanner.BringToFront();
        _connectingTimer.Start();
    }

    private void StopConnectingAnimation()
    {
        _connectingTimer.Stop();
        _pnlConnectingBanner.Visible = false;
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _prevWindowState = WindowState;
            _prevBorderStyle = FormBorderStyle;
            _prevBounds = Bounds;

            _pnlHeader.Visible = false;
            _pnlSessionToolbar.Visible = false;
            _pnlIncomingBanner.Visible = false;
            _pnlConnectingBanner.Visible = false;

            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
            _btnFullscreen.Text = "⛶";
            _sessionToolTip.SetToolTip(_btnFullscreen, "離開全螢幕 (ESC / F11)");
            _isFullscreen = true;

            if (_currentSession != null)
            {
                _currentSession.Canvas.FitToWindow = true;
                _currentSession.Canvas.RecalculateLayout();
                _currentSession.Canvas.Focus();
            }
        }
        else
        {
            ExitFullscreen();
        }
    }

    private void ExitFullscreen()
    {
        if (_isFullscreen)
        {
            _pnlHeader.Visible = true;
            _pnlSessionToolbar.Visible = true;

            FormBorderStyle = FormBorderStyle.None;
            if (_prevWindowState == FormWindowState.Maximized)
            {
                MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
                WindowState = FormWindowState.Maximized;
            }
            else
            {
                WindowState = FormWindowState.Normal;
                if (!_prevBounds.IsEmpty && _prevBounds.Width > 200 && _prevBounds.Height > 200)
                {
                    Bounds = _prevBounds;
                }
            }
            _btnFullscreen.Text = "⛶";
            _sessionToolTip.SetToolTip(_btnFullscreen, "進入全螢幕 (F11)");
            _isFullscreen = false;
            _currentSession?.Canvas.RecalculateLayout();
            UpdateHeaderLayout();
            UpdateSessionToolbarLayout();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_isFullscreen && (keyData == Keys.Escape || keyData == Keys.F11))
        {
            ExitFullscreen();
            return true;
        }
        if (keyData == Keys.F11 && _currentSession != null)
        {
            ToggleFullscreen();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------------- IHostUi 實作 ----------------
    public Task<bool> ConfirmAccessAsync(string viewerName, string viewerId, string address, string code, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>();
        BeginInvoke(() =>
        {
            using var dlg = new AccessRequestDialog(viewerName, viewerId, address, code);
            var reg = ct.Register(() => dlg.BeginInvoke(dlg.Close));
            dlg.ShowDialog(this);
            reg.Dispose();
            tcs.TrySetResult(dlg.IsAccepted);
        });
        return tcs.Task;
    }

    public void SessionStarted(HostSession session)
    {
        BeginInvoke(() =>
        {
            _lblIncomingBannerText.Text = $"🟢 遠端使用者已連入本機: {session.ViewerName} (ID: {AppConfig.FormatId(session.ViewerId)}) | 來源 IP: {session.RemoteAddress}";
            _pnlIncomingBanner.Visible = true;
        });
    }

    public void SessionEnded(HostSession session)
    {
        BeginInvoke(() =>
        {
            _pnlIncomingBanner.Visible = false;
        });
    }

    public void SetClipboardText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        BeginInvoke(() =>
        {
            try { Clipboard.SetText(text); } catch { }
        });
    }

    private void InitializeTrayIcon()
    {
        try
        {
            _trayIcon = new NotifyIcon
            {
                Icon = AppBranding.AppIcon,
                Text = "RemoteDesk",
                Visible = true
            };
            var trayMenu = new ContextMenuStrip { BackColor = Theme.SurfaceElevated, ForeColor = Theme.TextPrimary };
            trayMenu.Items.Add("開啟 RemoteDesk", null, (_, _) =>
            {
                Show();
                WindowState = FormWindowState.Normal;
                BringToFront();
            });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("結束", null, (_, _) =>
            {
                _forceClose = true;
                _trayIcon.Visible = false;
                Close();
            });
            _trayIcon.ContextMenuStrip = trayMenu;
            _trayIcon.DoubleClick += (_, _) =>
            {
                Show();
                WindowState = FormWindowState.Normal;
                BringToFront();
            };
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_forceClose && _viewerConfig.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _trayIcon?.ShowNotification("RemoteDesk", "已最小化至系統匣，雙擊可隨時重新開啟。");
            return;
        }

        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        foreach (var s in _sessionTabs.ToList())
        {
            SaveThumbnail(s);
            s.ImeClient.Dispose();
            s.Session.Dispose();
        }
        _sessionTabs.Clear();

        _keyboardHook?.Dispose();
        _keyboardHook = null;

        _deviceRefreshTimer?.Stop();
        _deviceRefreshTimer?.Dispose();
        _deviceRefreshTimer = null;

        _connectingTimer.Stop();
        _connectingTimer.Dispose();
        _connectingCts?.Cancel();
        _connectingCts?.Dispose();

        _discovery.Dispose();
        _server.Dispose();
        base.OnFormClosing(e);
    }

    private sealed class SpinnerBox : Control
    {
        private float _angle;
        public float Angle
        {
            get => _angle;
            set { _angle = value; Invalidate(); }
        }

        public SpinnerBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            VectorIcons.DrawSpinner(e.Graphics, ClientRectangle, _angle, Theme.Accent, 2.4f);
        }
    }

    private sealed record MonitorComboItem(string DeviceName, string Title)
    {
        public override string ToString() => Title;
    }
}
