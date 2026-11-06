using System.Drawing;
using System.Drawing.Drawing2D;
using RemoteDesk.Common;
using RemoteDesk.Controls;
using RemoteDesk.Net;

namespace RemoteDesk;

/// <summary>
/// 左側分類導覽按鈕：純 GDI+ 向量繪製圖標與標題，100% 杜絕圖標為空、遺漏或字型缺失之問題。
/// </summary>
internal sealed class CategoryNavButton : Control
{
    public string CategoryKey { get; }
    public string Title { get; set; }
    public bool IsActive { get; set; }
    private bool _isHovered;

    public CategoryNavButton(string key, string title)
    {
        CategoryKey = key;
        Title = title;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(164, 40);
        Cursor = Cursors.Hand;
        BackColor = Theme.Surface;

        MouseEnter += (_, _) => { _isHovered = true; Invalidate(); };
        MouseLeave += (_, _) => { _isHovered = false; Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 1. 背景底色
        var rect = new Rectangle(0, 0, Width, Height);
        Color bg = IsActive ? Theme.SurfaceElevated : (_isHovered ? Color.FromArgb(37, 49, 67) : Theme.Surface);
        using (var brush = new SolidBrush(bg))
        {
            g.FillRectangle(brush, rect);
        }

        // 2. 作用中左側指示條
        if (IsActive)
        {
            using var activeBar = new SolidBrush(Theme.Primary);
            g.FillRectangle(activeBar, 0, 0, 3, Height);
        }

        // 3. 純 GDI+ 向量圖標 (100% 向量繪製，絕不因字型缺失而出現空白)
        var iconBounds = new Rectangle(12, 11, 18, 18);
        Color iconClr = IsActive ? Theme.Accent : (_isHovered ? Theme.TextPrimary : Theme.TextSecondary);
        switch (CategoryKey)
        {
            case "General":
                VectorIcons.DrawGear(g, iconBounds, iconClr);
                break;
            case "Security":
                VectorIcons.DrawLock(g, iconBounds, iconClr);
                break;
            case "Display":
                VectorIcons.DrawMonitor(g, iconBounds, iconClr);
                break;
            case "Audio":
                VectorIcons.DrawAudio(g, iconBounds, iconClr);
                break;
            case "Network":
                VectorIcons.DrawNetwork(g, iconBounds, iconClr);
                break;
            case "Account":
                VectorIcons.DrawUser(g, iconBounds, iconClr);
                break;
        }

        // 4. 文字標題
        using var textFont = Theme.CreateFont(9.5f, IsActive ? FontStyle.Bold : FontStyle.Regular);
        Color textClr = IsActive ? Theme.TextPrimary : (_isHovered ? Theme.TextPrimary : Theme.TextSecondary);
        using (var textBrush = new SolidBrush(textClr))
        {
            g.DrawString(Title, textFont, textBrush, 38, 11);
        }
    }
}

/// <summary>
/// 依據規格第 6 節：現代化設定介面 (Settings)。
/// 採用左側分類導覽、右側設定內容配置，支援深淺色主題、安全性、顯示、音訊、網路與帳號設定。
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly HostConfig _hostConfig;
    private readonly ViewerConfig _viewerConfig;
    private readonly Action _onConfigChanged;

    // 左側分類按鈕
    private readonly Panel _pnlSidebar;
    private readonly Panel _pnlContent;
    private readonly List<CategoryNavButton> _categoryButtons = new();
    private readonly Dictionary<string, Panel> _categoryPanels = new();

    // 控制項
    // 1. General
    private readonly ComboBox _cboTheme;
    private readonly ComboBox _cboLanguage;
    private readonly CheckBox _chkStartWithWindows;
    private readonly CheckBox _chkMinimizeToTray;

    // 2. Security
    private readonly TextBox _txtPassword;
    private readonly TextBox _txtConfirm;
    private readonly CheckBox _chkAllowInteractive;
    private readonly CheckBox _chkAllowClipboard;

    // 3. Display
    private readonly ComboBox _cboDefaultQuality;
    private readonly ComboBox _cboDefaultIme;
    private readonly CheckBox _chkFitToWindow;

    // 4. Audio
    private readonly CheckBox _chkAudioEnabled;

    // 5. Network
    private readonly Label _lblLocalIps;

    // 6. Account
    private readonly Label _lblDeviceId;
    private readonly Label _lblDeviceName;

    public SettingsForm(HostConfig hostConfig, ViewerConfig viewerConfig, Action onConfigChanged)
    {
        _hostConfig = hostConfig;
        _viewerConfig = viewerConfig;
        _onConfigChanged = onConfigChanged;

        Text = "RemoteDesk Settings";
        Size = new Size(760, 560);
        MinimumSize = new Size(680, 500);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        Font = Theme.BodyFont;
        AppBranding.ApplyToForm(this);

        // ---------------- 頂部標題 ----------------
        var pnlHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.HeaderHeight,
            BackColor = Theme.Surface,
            Padding = new Padding(20, 14, 20, 10)
        };
        pnlHeader.Paint += (_, pe) =>
        {
            int iconSize = 22;
            int iconY = (pnlHeader.ClientSize.Height - iconSize) / 2;
            pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            pe.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            pe.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            pe.Graphics.DrawImage(AppBranding.GetLogoBitmap(iconSize, iconSize), new Rectangle(18, iconY, iconSize, iconSize));
        };
        var lblTitle = new Label
        {
            Text = "Settings 設定",
            Font = Theme.CreateFont(13.5f, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            Location = new Point(48, (Theme.HeaderHeight - 24) / 2),
            AutoSize = true
        };
        pnlHeader.Controls.Add(lblTitle);

        // ---------------- 底部操作列 ----------------
        var pnlBottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            BackColor = Theme.Surface,
            Padding = new Padding(20, 10, 20, 10)
        };

        var btnSave = new Button
        {
            Text = "儲存所有設定",
            Size = new Size(130, Theme.ButtonHeight),
            Location = new Point(pnlBottom.Width - 245, 9),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Theme.Primary,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.ButtonFont,
            Cursor = Cursors.Hand
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += (_, _) => SaveSettings();

        var btnCancel = new Button
        {
            Text = "關閉",
            DialogResult = DialogResult.Cancel,
            Size = new Size(85, Theme.ButtonHeight),
            Location = new Point(pnlBottom.Width - 105, 9),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.BodyFont,
            Cursor = Cursors.Hand
        };
        btnCancel.FlatAppearance.BorderColor = Theme.Border;
        btnCancel.Click += (_, _) => Close();

        pnlBottom.Controls.AddRange(new Control[] { btnSave, btnCancel });

        // ---------------- 主體：左側分類導覽列 + 右側內容面板 ----------------
        _pnlSidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = 180,
            BackColor = Theme.Surface,
            Padding = new Padding(8, 12, 8, 12)
        };

        _pnlContent = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Background,
            Padding = new Padding(24, 16, 24, 16),
            AutoScroll = true
        };

        // ---------------- 初始各分類面板 ----------------
        // 1. General Panel
        var pnlGeneral = CreateContentPanel("General Settings (一般設定)");
        pnlGeneral.Controls.Add(CreateLabel("Language 介面語言：", 44));
        _cboLanguage = CreateComboBox(new[] { "繁體中文 (Traditional Chinese)", "English (US)" }, _viewerConfig.Language == "en-US" ? 1 : 0, 70);
        pnlGeneral.Controls.Add(_cboLanguage);

        pnlGeneral.Controls.Add(CreateLabel("Theme 主題風格：", 120));
        _cboTheme = CreateComboBox(new[] { "Dark (深色模式 - 推薦)", "Light (淺色模式)", "System (跟隨系統)" }, _viewerConfig.ThemeMode == "Light" ? 1 : 0, 146);
        pnlGeneral.Controls.Add(_cboTheme);

        bool isStartup = AppConfig.IsStartupWithWindows() || _viewerConfig.StartWithWindows;
        _chkStartWithWindows = CreateCheckBox("開機自動啟動 (Start with Windows)", isStartup, 198);
        _chkMinimizeToTray = CreateCheckBox("關閉時最小化至系統匣 (Minimize to Tray)", _viewerConfig.MinimizeToTray, 232);
        pnlGeneral.Controls.AddRange(new Control[] { _chkStartWithWindows, _chkMinimizeToTray });

        // 2. Security Panel
        var pnlSecurity = CreateContentPanel("Security Settings (安全性設定)");
        var lblSecNote = new Label
        {
            Text = "設定無人值守密碼後，控制端可直接輸入密碼存取本機，無須手動同意。\n兩欄留空並儲存，將清除固定密碼並強制改回「互動確認連線」模式。",
            ForeColor = Theme.TextSecondary,
            Font = Theme.SmallFont,
            Location = new Point(0, 42),
            Size = new Size(500, 38)
        };
        pnlSecurity.Controls.Add(lblSecNote);

        pnlSecurity.Controls.Add(CreateLabel("新密碼 (New Password)：", 96));
        _txtPassword = CreatePasswordBox(120);
        pnlSecurity.Controls.Add(_txtPassword);

        pnlSecurity.Controls.Add(CreateLabel("確認密碼 (Confirm Password)：", 162));
        _txtConfirm = CreatePasswordBox(186);
        pnlSecurity.Controls.Add(_txtConfirm);

        _chkAllowInteractive = CreateCheckBox("允許無密碼時發起「互動授權連線」（畫面上跳窗核准）", _hostConfig.AllowInteractive, 230);
        _chkAllowClipboard = CreateCheckBox("連線時允許雙向同步剪貼簿文字", _hostConfig.AllowClipboard, 264);
        pnlSecurity.Controls.AddRange(new Control[] { _chkAllowInteractive, _chkAllowClipboard });

        // 3. Display Panel
        var pnlDisplay = CreateContentPanel("Display Settings (顯示與輸入設定)");
        pnlDisplay.Controls.Add(CreateLabel("Default Quality 預設遠端畫質：", 44));
        _cboDefaultQuality = CreateComboBox(new[] { "極速流暢 (50% 畫質 - 網路頻寬有限時)", "平衡推薦 (70% 畫質 - 推薦標準)", "極致超清 (90% 畫質 - 內網無損細節)" },
            _viewerConfig.Quality <= 55 ? 0 : (_viewerConfig.Quality <= 75 ? 1 : 2), 70);
        pnlDisplay.Controls.Add(_cboDefaultQuality);

        pnlDisplay.Controls.Add(CreateLabel("Input Method 輸入法連線預設：", 120));
        _cboDefaultIme = CreateComboBox(new[] { "雙向鎖定綁定 (推薦 - 中英同步一致)", "本機獨立組字 (傳統模式)" },
            _viewerConfig.ImeMode == ImeSyncMode.Bound ? 0 : 1, 146);
        pnlDisplay.Controls.Add(_cboDefaultIme);

        _chkFitToWindow = CreateCheckBox("預設自動縮放畫面以符合視窗大小 (Fit to Window)", _viewerConfig.FitToWindow, 198);
        pnlDisplay.Controls.Add(_chkFitToWindow);

        // 4. Audio Panel
        var pnlAudio = CreateContentPanel("Audio Settings (音訊設定)");
        var lblAudioBadge = new Label
        {
            Text = "※ 遠端音訊傳輸功能目前為實驗性模組 (Coming Soon)",
            ForeColor = Theme.Warning,
            Font = Theme.SmallFont,
            Location = new Point(0, 44),
            AutoSize = true
        };
        _chkAudioEnabled = CreateCheckBox("啟用遠端電腦音訊同步播放 (實驗性)", false, 76);
        _chkAudioEnabled.Enabled = false; // 依規格 11/15 明確標註為 Disabled
        pnlAudio.Controls.AddRange(new Control[] { lblAudioBadge, _chkAudioEnabled });

        // 5. Network Panel
        var pnlNetwork = CreateContentPanel("Network Settings (網路與通訊埠)");
        var ips = LanDiscovery.LocalIPv4().Select(x => x.ip.ToString()).ToList();
        _lblLocalIps = new Label
        {
            Text = "區域網路 IP 位址：\n" + (ips.Count > 0 ? string.Join("\n", ips.Select(ip => "  • " + ip)) : "  • 未偵測到可用網路") + $"\n\n被控通訊埠 (TCP Port)：{_hostConfig.TcpPort}",
            Location = new Point(0, 44),
            Size = new Size(500, 130),
            ForeColor = Theme.TextPrimary,
            Font = Theme.CreateMonoFont(9.5f)
        };
        var btnCopyIps = new Button
        {
            Text = "📋 複製本機 IP",
            Location = new Point(0, 186),
            Size = new Size(130, 32),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.BodyFont,
            Cursor = Cursors.Hand
        };
        btnCopyIps.FlatAppearance.BorderColor = Theme.Border;
        btnCopyIps.Click += (_, _) =>
        {
            if (ips.Count > 0)
            {
                Clipboard.SetText(string.Join(", ", ips));
                btnCopyIps.Text = "✓ 已複製！";
            }
        };
        pnlNetwork.Controls.AddRange(new Control[] { _lblLocalIps, btnCopyIps });

        // 6. Account Panel
        var pnlAccount = CreateContentPanel("Account & Device Info (帳號與設備資訊)");
        _lblDeviceId = new Label
        {
            Text = $"本機 Device ID: {AppConfig.FormatId(_hostConfig.DeviceId)}",
            Font = Theme.CreateMonoFont(11f, FontStyle.Bold),
            ForeColor = Theme.Accent,
            Location = new Point(0, 44),
            AutoSize = true
        };
        _lblDeviceName = new Label
        {
            Text = $"電腦名稱 (Device Name): {_hostConfig.DeviceName}",
            Font = Theme.BodyFont,
            ForeColor = Theme.TextPrimary,
            Location = new Point(0, 80),
            AutoSize = true
        };
        var lblAccountMode = new Label
        {
            Text = "連線模式：內網原生專用 (LAN Native Mode - 不需要固定外部 IP)\n安全協定：ECDH P-256 金鑰交換 + AES-256-GCM 傳輸加密",
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSecondary,
            Location = new Point(0, 120),
            Size = new Size(500, 44)
        };
        pnlAccount.Controls.AddRange(new Control[] { _lblDeviceId, _lblDeviceName, lblAccountMode });

        // 註冊分類面板
        _categoryPanels["General"] = pnlGeneral;
        _categoryPanels["Security"] = pnlSecurity;
        _categoryPanels["Display"] = pnlDisplay;
        _categoryPanels["Audio"] = pnlAudio;
        _categoryPanels["Network"] = pnlNetwork;
        _categoryPanels["Account"] = pnlAccount;

        // 建立左側分類按鈕 (帶有純 GDI+ 向量圖標)
        AddCategoryButton("General", "一般設定");
        AddCategoryButton("Security", "安全性");
        AddCategoryButton("Display", "顯示與輸入");
        AddCategoryButton("Audio", "音訊");
        AddCategoryButton("Network", "網路");
        AddCategoryButton("Account", "帳號與設備");

        // 預設切換至 General
        SelectCategory("General");

        Controls.Add(_pnlContent);
        Controls.Add(_pnlSidebar);
        Controls.Add(pnlBottom);
        Controls.Add(pnlHeader);
    }

    private void AddCategoryButton(string key, string title)
    {
        var btn = new CategoryNavButton(key, title)
        {
            Location = new Point(8, 12 + _categoryButtons.Count * 44)
        };
        btn.Click += (_, _) => SelectCategory(key);
        _categoryButtons.Add(btn);
        _pnlSidebar.Controls.Add(btn);
    }

    private void SelectCategory(string key)
    {
        foreach (var btn in _categoryButtons)
        {
            btn.IsActive = (btn.CategoryKey == key);
            btn.Invalidate();
        }

        _pnlContent.Controls.Clear();
        if (_categoryPanels.TryGetValue(key, out var panel))
        {
            _pnlContent.Controls.Add(panel);
        }
    }

    private static Panel CreateContentPanel(string title)
    {
        var pnl = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var lbl = new Label
        {
            Text = title,
            Font = Theme.SectionTitleFont,
            ForeColor = Theme.TextPrimary,
            Location = new Point(0, 0),
            AutoSize = true
        };
        pnl.Controls.Add(lbl);
        return pnl;
    }

    private static Label CreateLabel(string text, int top) =>
        new()
        {
            Text = text,
            Location = new Point(0, top),
            AutoSize = true,
            ForeColor = Theme.TextSecondary,
            Font = Theme.BodyFont
        };

    private static ComboBox CreateComboBox(string[] items, int selectedIndex, int top)
    {
        var cbo = new ComboBox
        {
            Location = new Point(0, top),
            Size = new Size(320, 32),
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.BodyFont
        };
        cbo.Items.AddRange(items);
        if (selectedIndex >= 0 && selectedIndex < items.Length) cbo.SelectedIndex = selectedIndex;
        return cbo;
    }

    private static TextBox CreatePasswordBox(int top) =>
        new()
        {
            Location = new Point(0, top),
            Size = new Size(260, 28),
            UseSystemPasswordChar = true,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.CreateFont(10.5f)
        };

    private static CheckBox CreateCheckBox(string text, bool isChecked, int top) =>
        new()
        {
            Text = text,
            Checked = isChecked,
            Location = new Point(0, top),
            AutoSize = true,
            ForeColor = Theme.TextPrimary,
            Font = Theme.BodyFont
        };

    private void SaveSettings()
    {
        // 1. 密碼檢查
        if (!string.IsNullOrEmpty(_txtPassword.Text) || !string.IsNullOrEmpty(_txtConfirm.Text))
        {
            if (_txtPassword.Text != _txtConfirm.Text)
            {
                MessageBox.Show(this, "兩次輸入的無人值守密碼不一致，請重新檢查！", "密碼錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _hostConfig.SetPassword(_txtPassword.Text);
        }
        else if (string.IsNullOrEmpty(_txtPassword.Text) && string.IsNullOrEmpty(_txtConfirm.Text))
        {
            _hostConfig.SetPassword(null);
        }

        _hostConfig.AllowInteractive = _chkAllowInteractive.Checked;
        _hostConfig.AllowClipboard = _chkAllowClipboard.Checked;
        _hostConfig.TrySave();

        // 2. 檢視端設定
        _viewerConfig.Quality = _cboDefaultQuality.SelectedIndex switch
        {
            0 => 50,
            1 => 70,
            _ => 90
        };
        _viewerConfig.ImeMode = _cboDefaultIme.SelectedIndex == 0 ? ImeSyncMode.Bound : ImeSyncMode.Local;
        _viewerConfig.FitToWindow = _chkFitToWindow.Checked;
        _viewerConfig.ThemeMode = _cboTheme.SelectedIndex switch
        {
            0 => "Dark",
            1 => "Light",
            _ => "System"
        };
        _viewerConfig.Language = _cboLanguage.SelectedIndex == 1 ? "en-US" : "zh-TW";
        _viewerConfig.StartWithWindows = _chkStartWithWindows.Checked;
        _viewerConfig.MinimizeToTray = _chkMinimizeToTray.Checked;
        _viewerConfig.Save();

        // 3. 設定 Windows 開機自動啟動註冊表 (實際生效)
        AppConfig.SetStartupWithWindows(_chkStartWithWindows.Checked);

        _onConfigChanged();

        MessageBox.Show(this, "所有設定已成功儲存！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Close();
    }
}
