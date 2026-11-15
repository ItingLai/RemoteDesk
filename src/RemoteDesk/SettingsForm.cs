using System.Drawing;
using System.Drawing.Drawing2D;
using RemoteDesk.Common;
using RemoteDesk.Controls;
using RemoteDesk.Net;

namespace RemoteDesk;

/// <summary>
/// 左側分類導覽按鈕：純 GDI+ 向量繪製圖標與標題。
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

        // 3. 純 GDI+ 向量圖標
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
/// 現代化設定介面 (Settings)：
/// 整合無帳號裝置身分識別、ECDSA 公私鑰指紋、本機可信裝置 (Trust Management) 與無人值守模式。
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly HostConfig _hostConfig;
    private readonly ViewerConfig _viewerConfig;
    private readonly Action _onConfigChanged;
    private readonly TrustStore _trustStore;
    private readonly DeviceIdentity _identity;
    private readonly bool _ownsIdentity;

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

    // 2. Security & Trust Management
    private readonly CheckBox _chkUnattendedAccess;
    private readonly CheckBox _chkAutoAcceptTrusted;
    private readonly CheckBox _chkAllowInteractive;
    private readonly CheckBox _chkAllowClipboard;
    private readonly TextBox _txtPassword;
    private readonly TextBox _txtConfirm;
    private readonly ListView _lvTrustedDevices;

    // 3. Display
    private readonly ComboBox _cboDefaultQuality;
    private readonly ComboBox _cboResolutionMode;
    private readonly ComboBox _cboDefaultIme;
    private readonly CheckBox _chkFitToWindow;

    // 4. Audio
    private readonly CheckBox _chkAudioEnabled;

    // 5. Network
    private readonly Label _lblLocalIps;
    private readonly TextBox _txtSignalingUrl;

    // 6. Account & Device Identity
    private readonly Label _lblDeviceId;
    private readonly Label _lblDeviceName;
    private readonly Label _lblFingerprint;

    public SettingsForm(HostConfig hostConfig, ViewerConfig viewerConfig, Action onConfigChanged, TrustStore? trustStore = null, DeviceIdentity? identity = null)
    {
        _hostConfig = hostConfig;
        _viewerConfig = viewerConfig;
        _onConfigChanged = onConfigChanged;
        _trustStore = trustStore ?? new TrustStore();
        if (identity != null)
        {
            _identity = identity;
            _ownsIdentity = false;
        }
        else
        {
            _identity = DeviceIdentity.LoadOrCreate(AppConfig.SharedDirectory, hostConfig.DeviceId, hostConfig.DeviceName);
            _ownsIdentity = true;
        }

        Text = "RemoteDesk Settings";
        Size = new Size(840, 640);
        MinimumSize = new Size(760, 560);
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

        // 2. Security Panel (Trust Management & Unattended Access)
        var pnlSecurity = CreateContentPanel("Security & Trust Management (安全性與信任管理)");
        var lblSecNote = new Label
        {
            Text = "RemoteDesk 採無帳號密碼的裝置公私鑰與信任授權機制。\n只有經過本機人工首次配對核准之「可信裝置」，才可在開啟無人值守時直接連入本機。",
            ForeColor = Theme.TextSecondary,
            Font = Theme.SmallFont,
            Location = new Point(0, 40),
            Size = new Size(580, 36)
        };
        pnlSecurity.Controls.Add(lblSecNote);

        _chkUnattendedAccess = CreateCheckBox("啟用無人值守存取模式 (Unattended Access - 預設關閉)", _hostConfig.UnattendedAccessEnabled, 84);
        _chkAutoAcceptTrusted = CreateCheckBox("可信裝置自動接受 (Auto-accept trusted devices - 無須每次人工確認)", _hostConfig.AutoAcceptTrustedDevices, 114);
        _chkAllowInteractive = CreateCheckBox("允許未授權裝置發起「互動授權請求」（畫面上跳窗核准）", _hostConfig.AllowInteractive, 144);
        _chkAllowClipboard = CreateCheckBox("連線時允許雙向同步剪貼簿文字", _hostConfig.AllowClipboard, 174);
        pnlSecurity.Controls.AddRange(new Control[] { _chkUnattendedAccess, _chkAutoAcceptTrusted, _chkAllowInteractive, _chkAllowClipboard });

        // 無人值守連線密碼區塊
        var lblPwdSection = new Label
        {
            Text = "無人值守固定連線密碼 (Unattended Password - 選用)：",
            Font = Theme.CreateFont(9.5f, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            Location = new Point(0, 206),
            AutoSize = true
        };
        var lblPwdStatus = new Label
        {
            Text = _hostConfig.HasPassword
                ? "狀態：● 已設定固定連線密碼保護 (若不需變更請留空)"
                : "狀態：○ 未設定密碼 (僅允許手動核准或已配對可信裝置)",
            ForeColor = _hostConfig.HasPassword ? Theme.Success : Theme.TextSecondary,
            Font = Theme.SmallFont,
            Location = new Point(0, 228),
            AutoSize = true
        };

        var lblPwd = new Label
        {
            Text = "設定密碼：",
            ForeColor = Theme.TextSecondary,
            Font = Theme.SmallFont,
            Location = new Point(0, 252),
            AutoSize = true
        };
        _txtPassword = CreatePasswordBox(0, 274, 200);

        var lblConfirm = new Label
        {
            Text = "確認密碼：",
            ForeColor = Theme.TextSecondary,
            Font = Theme.SmallFont,
            Location = new Point(215, 252),
            AutoSize = true
        };
        _txtConfirm = CreatePasswordBox(215, 274, 200);

        var btnClearPassword = new Button
        {
            Text = "🗑️ 清除密碼",
            Location = new Point(430, 273),
            Size = new Size(110, 30),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.Danger,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.SmallFont,
            Cursor = Cursors.Hand,
            Enabled = _hostConfig.HasPassword
        };
        btnClearPassword.FlatAppearance.BorderColor = Theme.Border;
        btnClearPassword.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "確定要清除無人值守連線密碼嗎？清除後未加入可信清單的控制端將無法透過密碼連入。", "清除密碼確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _hostConfig.SetPassword(null);
                _hostConfig.TrySave();
                _txtPassword.Clear();
                _txtConfirm.Clear();
                lblPwdStatus.Text = "狀態：○ 未設定密碼 (僅允許手動核准或已配對可信裝置)";
                lblPwdStatus.ForeColor = Theme.TextSecondary;
                btnClearPassword.Enabled = false;
                MessageBox.Show(this, "連線密碼已成功清除！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        var lblPwdTip = new Label
        {
            Text = "※ 設定密碼後，未加入可信裝置清單的控制端可輸入此密碼登入；控制端連線時可勾選「記住密碼」。",
            ForeColor = Theme.TextSecondary,
            Font = Theme.SmallFont,
            Location = new Point(0, 312),
            Size = new Size(580, 24)
        };

        pnlSecurity.Controls.AddRange(new Control[]
        {
            lblPwdSection, lblPwdStatus, lblPwd, _txtPassword,
            lblConfirm, _txtConfirm, btnClearPassword, lblPwdTip
        });

        var lblTrustList = new Label
        {
            Text = "已授權可信裝置清單 (Trusted Devices Management)：",
            Font = Theme.CreateFont(9.5f, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            Location = new Point(0, 344),
            AutoSize = true
        };
        pnlSecurity.Controls.Add(lblTrustList);

        _lvTrustedDevices = new ListView
        {
            Location = new Point(0, 370),
            Size = new Size(580, 150),
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.SmallFont
        };
        _lvTrustedDevices.Columns.Add("裝置 ID", 90);
        _lvTrustedDevices.Columns.Add("裝置名稱", 110);
        _lvTrustedDevices.Columns.Add("公鑰指紋", 160);
        _lvTrustedDevices.Columns.Add("配對時間", 90);
        _lvTrustedDevices.Columns.Add("狀態", 60);
        pnlSecurity.Controls.Add(_lvTrustedDevices);

        var pnlTrustBtns = new FlowLayoutPanel
        {
            Location = new Point(0, 528),
            Size = new Size(580, 36),
            FlowDirection = FlowDirection.LeftToRight
        };

        var btnRevokeDevice = new Button
        {
            Text = "🚫 撤銷信任",
            Size = new Size(110, 30),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.Warning,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.SmallFont,
            Cursor = Cursors.Hand
        };
        btnRevokeDevice.FlatAppearance.BorderColor = Theme.Border;
        btnRevokeDevice.Click += (_, _) =>
        {
            if (_lvTrustedDevices.SelectedItems.Count > 0)
            {
                string id = _lvTrustedDevices.SelectedItems[0].Text;
                _trustStore.RevokeDevice(id);
                RefreshTrustedDevicesList();
            }
        };

        var btnRemoveDevice = new Button
        {
            Text = "🗑️ 移除裝置",
            Size = new Size(110, 30),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.Danger,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.SmallFont,
            Cursor = Cursors.Hand
        };
        btnRemoveDevice.FlatAppearance.BorderColor = Theme.Border;
        btnRemoveDevice.Click += (_, _) =>
        {
            if (_lvTrustedDevices.SelectedItems.Count > 0)
            {
                string id = _lvTrustedDevices.SelectedItems[0].Text;
                _trustStore.RemoveDevice(id);
                RefreshTrustedDevicesList();
            }
        };

        var btnRevokeAll = new Button
        {
            Text = "⚠️ 撤銷所有可信裝置",
            Size = new Size(160, 30),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.Danger,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.SmallFont,
            Cursor = Cursors.Hand
        };
        btnRevokeAll.FlatAppearance.BorderColor = Theme.Border;
        btnRevokeAll.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "確定要撤銷所有已授權的可信裝置嗎？撤銷後所有裝置均需重新手動確認。", "撤銷確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                _trustStore.RevokeAll();
                RefreshTrustedDevicesList();
            }
        };

        pnlTrustBtns.Controls.AddRange(new Control[] { btnRevokeDevice, btnRemoveDevice, btnRevokeAll });
        pnlSecurity.Controls.Add(pnlTrustBtns);

        RefreshTrustedDevicesList();

        // 3. Display Panel
        var pnlDisplay = CreateContentPanel("Display Settings (顯示與輸入設定)");
        pnlDisplay.Controls.Add(CreateLabel("Default Quality 預設遠端畫質 (與解析度獨立)：", 44));
        _cboDefaultQuality = CreateComboBox(new[] { "低負載壓縮 (50% 畫質 - 保持設定解析度)", "平衡推薦 (70% 畫質 - 推薦標準)", "極致超清 (90% 畫質 - 內網無損細節)" },
            _viewerConfig.Quality <= 55 ? 0 : (_viewerConfig.Quality <= 75 ? 1 : 2), 70);
        pnlDisplay.Controls.Add(_cboDefaultQuality);

        pnlDisplay.Controls.Add(CreateLabel("Resolution Mode 解析度自適應控制模式：", 114));
        _cboResolutionMode = CreateComboBox(new[]
        {
            "Auto 自動模式 (原生解析度優先，僅壅塞時漸進降採樣)",
            "Original 原生鎖定 (強制維持原始解析度，壅塞時警示)",
            "Performance 流暢優先 (操作順暢優先，較積極適應頻寬)"
        }, Math.Clamp((int)_viewerConfig.ResolutionMode, 0, 2), 140);
        pnlDisplay.Controls.Add(_cboResolutionMode);

        pnlDisplay.Controls.Add(CreateLabel("Input Method 輸入法連線預設：", 186));
        _cboDefaultIme = CreateComboBox(new[] { "雙向鎖定綁定 (推薦 - 中英同步一致)", "本機獨立組字 (傳統模式)" },
            _viewerConfig.ImeMode == ImeSyncMode.Bound ? 0 : 1, 212);
        pnlDisplay.Controls.Add(_cboDefaultIme);

        _chkFitToWindow = CreateCheckBox("預設自動縮放畫面以符合視窗大小 (Fit to Window)", _viewerConfig.FitToWindow, 262);
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
        _chkAudioEnabled.Enabled = false;
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

        pnlNetwork.Controls.Add(CreateLabel("中央協調伺服器網址 (Signaling Server URL)：", 230));
        _txtSignalingUrl = new TextBox
        {
            Location = new Point(0, 256),
            Size = new Size(420, Theme.InputHeight),
            Text = _hostConfig.SignalingServerUrl,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.BodyFont
        };
        var lblSignalingHint = new Label
        {
            Text = "預設伺服器：https://signal.remotedesk.ink\n留空表示純內網原生模式 (LAN Native Mode，使用 UDP 廣播探索)。",
            Location = new Point(0, 292),
            Size = new Size(500, 36),
            ForeColor = Theme.TextSecondary,
            Font = Theme.SmallFont
        };
        pnlNetwork.Controls.AddRange(new Control[] { _txtSignalingUrl, lblSignalingHint });

        // 6. Account Panel (Device Identity & Credentials)
        var pnlAccount = CreateContentPanel("Account & Device Identity (裝置身分與金鑰)");
        _lblDeviceId = new Label
        {
            Text = $"本機 Device ID: {AppConfig.FormatId(_identity.DeviceId)}",
            Font = Theme.CreateMonoFont(11f, FontStyle.Bold),
            ForeColor = Theme.Accent,
            Location = new Point(0, 44),
            AutoSize = true
        };
        _lblDeviceName = new Label
        {
            Text = $"電腦名稱 (Device Name): {_identity.DeviceName}",
            Font = Theme.BodyFont,
            ForeColor = Theme.TextPrimary,
            Location = new Point(0, 78),
            AutoSize = true
        };
        _lblFingerprint = new Label
        {
            Text = $"ECDSA 公鑰指紋:\n{_identity.PublicKeyFingerprint}",
            Font = Theme.CreateMonoFont(9f),
            ForeColor = Theme.TextSecondary,
            Location = new Point(0, 110),
            Size = new Size(580, 44)
        };

        var btnRegenerate = new Button
        {
            Text = "🔄 重新產生裝置身分 (Regenerate Identity)",
            Location = new Point(0, 168),
            Size = new Size(260, 34),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.Warning,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.BodyFont,
            Cursor = Cursors.Hand
        };
        btnRegenerate.FlatAppearance.BorderColor = Theme.Border;
        btnRegenerate.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "重新產生裝置身分將更換本機 ECDSA 金鑰對。\n先前已將本機加入信任的其他電腦將會要求重新配對確認，是否確定重新產生？", "身分重新產生確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                _identity.Regenerate(AppConfig.SharedDirectory);
                _hostConfig.DeviceId = _identity.DeviceId;
                _hostConfig.TrySave();
                _lblDeviceId.Text = $"本機 Device ID: {AppConfig.FormatId(_identity.DeviceId)}";
                _lblFingerprint.Text = $"ECDSA 公鑰指紋:\n{_identity.PublicKeyFingerprint}";
                MessageBox.Show(this, "裝置身分金鑰已成功重新產生！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        var lblAccountMode = new Label
        {
            Text = "身分架構：Device ID + ECDSA P-256 公私鑰 + Windows DPAPI 安全保護 (無帳號密碼)\n安全傳輸協定：ECDH P-256 臨時密鑰協商 + HKDF + AES-256-GCM 端對端認證加密",
            Font = Theme.SmallFont,
            ForeColor = Theme.TextSecondary,
            Location = new Point(0, 220),
            Size = new Size(580, 44)
        };
        pnlAccount.Controls.AddRange(new Control[] { _lblDeviceId, _lblDeviceName, _lblFingerprint, btnRegenerate, lblAccountMode });

        // 註冊分類面板
        _categoryPanels["General"] = pnlGeneral;
        _categoryPanels["Security"] = pnlSecurity;
        _categoryPanels["Display"] = pnlDisplay;
        _categoryPanels["Audio"] = pnlAudio;
        _categoryPanels["Network"] = pnlNetwork;
        _categoryPanels["Account"] = pnlAccount;

        // 建立左側導覽按鈕
        AddNavButton("General", "一般設定");
        AddNavButton("Security", "安全性與信任");
        AddNavButton("Display", "顯示與輸入");
        AddNavButton("Audio", "音訊設定");
        AddNavButton("Network", "網路與連接埠");
        AddNavButton("Account", "裝置身分金鑰");

        Controls.Add(_pnlContent);
        Controls.Add(_pnlSidebar);
        Controls.Add(pnlBottom);
        Controls.Add(pnlHeader);

        // 預設選中 General
        SelectCategory("General");
    }

    private void RefreshTrustedDevicesList()
    {
        _lvTrustedDevices.Items.Clear();
        foreach (var dev in _trustStore.GetAll())
        {
            var item = new ListViewItem(dev.TrustedDeviceId);
            item.SubItems.Add(dev.TrustedDeviceName);
            item.SubItems.Add(dev.TrustedPublicKeyFingerprint.Length > 20 ? dev.TrustedPublicKeyFingerprint[..20] + "..." : dev.TrustedPublicKeyFingerprint);
            item.SubItems.Add(dev.PairedAt.ToLocalTime().ToString("yyyy/MM/dd"));
            item.SubItems.Add(dev.TrustStatus.ToString());
            _lvTrustedDevices.Items.Add(item);
        }
    }

    private void AddNavButton(string key, string title)
    {
        var btn = new CategoryNavButton(key, title)
        {
            Location = new Point(8, 12 + _categoryButtons.Count * 46),
            Width = _pnlSidebar.Width - 16
        };
        btn.Click += (_, _) => SelectCategory(key);
        _categoryButtons.Add(btn);
        _pnlSidebar.Controls.Add(btn);
    }

    private void SelectCategory(string key)
    {
        foreach (var btn in _categoryButtons)
        {
            btn.IsActive = btn.CategoryKey == key;
            btn.Invalidate();
        }

        _pnlContent.SuspendLayout();
        _pnlContent.Controls.Clear();
        if (_categoryPanels.TryGetValue(key, out var pnl))
        {
            pnl.Dock = DockStyle.Fill;
            _pnlContent.Controls.Add(pnl);
        }
        _pnlContent.ResumeLayout();
    }

    private static Panel CreateContentPanel(string title)
    {
        var pnl = new Panel { AutoScroll = true, BackColor = Theme.Background };
        var lbl = new Label
        {
            Text = title,
            Font = Theme.CreateFont(12f, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            Location = new Point(0, 4),
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
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(0, top),
            Size = new Size(340, Theme.InputHeight),
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            Font = Theme.BodyFont,
            FlatStyle = FlatStyle.Flat
        };
        cbo.Items.AddRange(items);
        if (selectedIndex >= 0 && selectedIndex < items.Length) cbo.SelectedIndex = selectedIndex;
        return cbo;
    }

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

    private static TextBox CreatePasswordBox(int left, int top, int width) =>
        new()
        {
            UseSystemPasswordChar = true,
            Location = new Point(left, top),
            Size = new Size(width, Theme.InputHeight),
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.BodyFont
        };

    private void SaveSettings()
    {
        // 密碼變更檢查
        if (!string.IsNullOrEmpty(_txtPassword.Text) || !string.IsNullOrEmpty(_txtConfirm.Text))
        {
            if (_txtPassword.Text != _txtConfirm.Text)
            {
                MessageBox.Show(this, "兩次輸入的連線密碼不一致，請重新確認！", "密碼錯誤", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_txtPassword.Text.Length < 6)
            {
                MessageBox.Show(this, "連線密碼長度建議至少 6 個字元！", "密碼長度不足", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _hostConfig.SetPassword(_txtPassword.Text);
            if (!_hostConfig.UnattendedAccessEnabled)
            {
                _hostConfig.UnattendedAccessEnabled = true;
                _chkUnattendedAccess.Checked = true;
            }
        }

        // 1. 安全性設定更新
        _hostConfig.UnattendedAccessEnabled = _chkUnattendedAccess.Checked;
        _hostConfig.AutoAcceptTrustedDevices = _chkAutoAcceptTrusted.Checked;
        _hostConfig.AllowInteractive = _chkAllowInteractive.Checked;
        _hostConfig.AllowClipboard = _chkAllowClipboard.Checked;
        _hostConfig.SignalingServerUrl = _txtSignalingUrl.Text.Trim();
        _hostConfig.TrySave();

        // 2. 檢視端設定
        _viewerConfig.Quality = _cboDefaultQuality.SelectedIndex switch
        {
            0 => 50,
            1 => 70,
            _ => 90
        };
        _viewerConfig.ResolutionMode = (ResolutionMode)Math.Clamp(_cboResolutionMode.SelectedIndex, 0, 2);
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

        // 3. 設定 Windows 開機自動啟動註冊表
        AppConfig.SetStartupWithWindows(_chkStartWithWindows.Checked);

        _onConfigChanged();

        MessageBox.Show(this, "所有設定已成功儲存！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_ownsIdentity) _identity.Dispose();
        base.OnFormClosed(e);
    }
}
