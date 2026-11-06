using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using RemoteDesk.Common;
using RemoteDesk.Native;

namespace RemoteDesk.Viewer;

internal sealed class ViewerForm : Form
{
    private readonly ViewerSession _session;
    private readonly DesktopCanvas _canvas;
    private readonly ImeSyncClient _imeClient;
    private KeyboardHook? _keyboardHook;

    // UI Controls
    private readonly ToolStrip _toolbar;
    private readonly ToolStripComboBox _cboMonitors;
    private readonly ToolStripButton _btnImeMode;
    private readonly ToolStripDropDownButton _btnSpecialKeys;
    private readonly ToolStripButton _btnFitWindow;
    private readonly ToolStripDropDownButton _btnQuality;
    private readonly ToolStripButton _btnFullscreen;
    private readonly ToolStripButton _btnDisconnect;

    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _lblHostInfo;
    private readonly ToolStripStatusLabel _lblResolution;
    private readonly ToolStripStatusLabel _lblFps;
    private readonly ToolStripStatusLabel _lblLatency;
    private readonly ToolStripStatusLabel _lblImeStatus;

    private bool _isFullscreen;
    private FormWindowState _prevWindowState;
    private FormBorderStyle _prevBorderStyle;
    private Rectangle _prevBounds;

    private int _frameCount;
    private long _lastFpsTime;
    private bool _clipboardUpdating;
    private bool _thumbnailSaved;

    public ViewerForm(ViewerSession session)
    {
        _session = session;

        Text = $"RemoteDesk - {session.HostDeviceName} ({AppConfig.FormatId(session.HostDeviceId)})";
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;
        AppBranding.ApplyToForm(this);
        KeyPreview = true;

        // 1. 桌面畫布
        _canvas = new DesktopCanvas(_session) { Dock = DockStyle.Fill };

        // 2. 工具列
        _toolbar = new ToolStrip
        {
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = Color.FromArgb(40, 40, 40),
            ForeColor = Color.White,
            Padding = new Padding(6, 4, 6, 4),
            Renderer = new DarkToolStripRenderer()
        };

        // 螢幕切換下拉選單
        var lblMon = new ToolStripLabel("螢幕:") { ForeColor = Color.LightGray };
        _cboMonitors = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 150 };
        _cboMonitors.SelectedIndexChanged += OnMonitorSelectedIndexChanged;

        // 輸入法綁定按鈕
        _btnImeMode = new ToolStripButton("輸入法: 雙向綁定 (中/英同步)")
        {
            BackColor = Color.FromArgb(30, 80, 140),
            ForeColor = Color.White,
            ToolTipText = "點擊切換：雙向鎖定綁定 (推薦) 或 本機組字模式"
        };
        _btnImeMode.Click += OnImeModeToggleClick;

        // 特殊快捷鍵選單
        _btnSpecialKeys = new ToolStripDropDownButton("傳送指令");
        _btnSpecialKeys.DropDownItems.Add("傳送 Ctrl+Alt+Del", null, (_, _) => _session.SendSpecial(SpecialAction.CtrlAltDel));
        _btnSpecialKeys.DropDownItems.Add("開啟工作管理員 (Ctrl+Shift+Esc)", null, (_, _) => _session.SendSpecial(SpecialAction.TaskManager));
        _btnSpecialKeys.DropDownItems.Add("鎖定遠端電腦 (Win+L)", null, (_, _) => _session.SendSpecial(SpecialAction.LockScreen));
        _btnSpecialKeys.DropDownItems.Add(new ToolStripSeparator());
        _btnSpecialKeys.DropDownItems.Add("切換遠端中/英文 (Shift)", null, (_, _) =>
        {
            _session.SendKey(0x10, 0x2A, KeyFlags.None);
            _session.SendKey(0x10, 0x2A, KeyFlags.Up);
        });

        // 顯示模式
        _btnFitWindow = new ToolStripButton("視窗縮放") { Checked = true, CheckOnClick = true };
        _btnFitWindow.Click += (_, _) => _canvas.FitToWindow = _btnFitWindow.Checked;

        // 畫質選項
        _btnQuality = new ToolStripDropDownButton("畫質: 平衡 (70%)");
        _btnQuality.DropDownItems.Add("極速流暢 (50% 畫質, 30 FPS)", null, (_, _) => SetQuality(50, 30, false, "極速流暢"));
        _btnQuality.DropDownItems.Add("平衡推薦 (70% 畫質, 30 FPS, 無失真修補)", null, (_, _) => SetQuality(70, 30, true, "平衡"));
        _btnQuality.DropDownItems.Add("極致超清 (90% 畫質, 60 FPS, 無失真修補)", null, (_, _) => SetQuality(90, 60, true, "極致超清"));

        // 全螢幕按鈕
        _btnFullscreen = new ToolStripButton("全螢幕 (F11)");
        _btnFullscreen.Click += (_, _) => ToggleFullscreen();

        // 中斷連線
        _btnDisconnect = new ToolStripButton("結束連線") { ForeColor = Color.LightPink };
        _btnDisconnect.Click += (_, _) => Close();

        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            lblMon, _cboMonitors,
            new ToolStripSeparator(),
            _btnImeMode,
            new ToolStripSeparator(),
            _btnSpecialKeys,
            _btnFitWindow,
            _btnQuality,
            _btnFullscreen,
            new ToolStripSeparator(),
            _btnDisconnect
        });

        // 3. 狀態列
        _statusStrip = new StatusStrip { BackColor = Color.FromArgb(32, 32, 32), ForeColor = Color.Silver };
        _lblHostInfo = new ToolStripStatusLabel($"主機: {session.HostDeviceName} ({session.HostDeviceId})");
        _lblResolution = new ToolStripStatusLabel("解析度: --");
        _lblFps = new ToolStripStatusLabel("FPS: 0");
        _lblLatency = new ToolStripStatusLabel("延遲: --");
        _lblImeStatus = new ToolStripStatusLabel("輸入法狀態: 同步中");
        _statusStrip.Items.AddRange(new ToolStripItem[] { _lblHostInfo, _lblResolution, _lblFps, _lblLatency, _lblImeStatus });

        Controls.Add(_canvas);
        Controls.Add(_toolbar);
        Controls.Add(_statusStrip);

        // 4. 輸入法客戶端初始化
        _imeClient = new ImeSyncClient(
            _canvas,
            state => _session.SendImeSet(state),
            text => _session.SendText(text));

        _imeClient.StateChanged += OnImeClientStateChanged;

        // 5. 註冊 Session 事件
        _session.FrameReceived += OnSessionFrameReceived;
        _session.MonitorsUpdated += OnSessionMonitorsUpdated;
        _session.CursorShapeChanged += cur => BeginInvoke(() => _canvas.Cursor = cur ?? Cursors.Default);
        _session.ImeStateReceived += state => _imeClient.OnRemoteImeStateReceived(state);
        _session.CaretReceived += (x, y, h) => _imeClient.UpdateCaretPosition(x, y, h);
        _session.ClipboardReceived += OnSessionClipboardReceived;
        _session.RttUpdated += ms => BeginInvoke(() => _lblLatency.Text = $"延遲: {ms} ms (區網直連)");
        _session.Disconnected += reason => BeginInvoke(() =>
        {
            MessageBox.Show(this, $"連線已結束: {reason}", "RemoteDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        });

        // 6. 安裝鍵盤攔截器 (原生鍵盤傳遞)
        _keyboardHook = new KeyboardHook(
            () => Form.ActiveForm == this && _canvas.Focused,
            (vk, scan, flags) => _session.SendKey(vk, scan, flags));

        _lastFpsTime = Environment.TickCount64;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32.AddClipboardFormatListener(Handle);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Win32.RemoveClipboardFormatListener(Handle);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32.WM_CLIPBOARDUPDATE)
        {
            OnLocalClipboardUpdated();
        }
        base.WndProc(ref m);
    }

    private void OnLocalClipboardUpdated()
    {
        if (_clipboardUpdating || !ContainsFocus) return;
        try
        {
            if (Clipboard.ContainsText())
            {
                string text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text))
                {
                    _session.SendClipboard(text);
                }
            }
        }
        catch { }
    }

    private void OnSessionClipboardReceived(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        BeginInvoke(() =>
        {
            try
            {
                _clipboardUpdating = true;
                Clipboard.SetText(text);
            }
            catch { }
            finally
            {
                _clipboardUpdating = false;
            }
        });
    }

    private void SetQuality(int quality, int fps, bool lossless, string label)
    {
        _session.SendVideoSettings(quality, fps, lossless);
        _btnQuality.Text = $"畫質: {label}";
    }

    private void OnImeModeToggleClick(object? sender, EventArgs e)
    {
        if (_imeClient.Mode == ImeSyncMode.Bound)
        {
            _imeClient.Mode = ImeSyncMode.Local;
            _session.SendImeMode(ImeSyncMode.Local);
            _btnImeMode.Text = "輸入法: 本機組字模式 (獨立)";
            _btnImeMode.BackColor = Color.FromArgb(120, 80, 20);
        }
        else
        {
            _imeClient.Mode = ImeSyncMode.Bound;
            _session.SendImeMode(ImeSyncMode.Bound);
            _btnImeMode.Text = "輸入法: 雙向綁定 (中/英同步)";
            _btnImeMode.BackColor = Color.FromArgb(30, 80, 140);
        }
    }

    private void OnImeClientStateChanged(ImeState state)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnImeClientStateChanged(state)));
            return;
        }

        string modeDesc = state.IsNative ? "中文" : "英文";
        _lblImeStatus.Text = $"輸入法綁定: {modeDesc} (配置: 0x{state.LangId:X4})";
    }

    private void OnSessionMonitorsUpdated(List<MonitorInfo> monitors, string current)
    {
        BeginInvoke(() =>
        {
            _cboMonitors.Items.Clear();
            int selectedIndex = 0;

            for (int i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i];
                var name = $"{m.FriendlyName} ({m.Width}x{m.Height}){(m.Primary ? " [主]" : "")}";
                _cboMonitors.Items.Add(new MonitorItem(m.DeviceName, name));
                if (m.DeviceName == current) selectedIndex = i;
            }

            if (monitors.Count > 1)
            {
                _cboMonitors.Items.Add(new MonitorItem(Protocol.AllMonitors, "所有螢幕 (合併檢視)"));
                if (current == Protocol.AllMonitors) selectedIndex = _cboMonitors.Items.Count - 1;
            }

            if (_cboMonitors.Items.Count > 0)
            {
                _cboMonitors.SelectedIndex = selectedIndex;
            }
        });
    }

    private void OnMonitorSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_cboMonitors.SelectedItem is MonitorItem item && item.DeviceName != _session.SelectedMonitor)
        {
            _session.SelectMonitor(item.DeviceName);
        }
    }

    private void OnSessionFrameReceived(Rectangle dirtyRect, uint seq)
    {
        _frameCount++;
        long now = Environment.TickCount64;
        if (now - _lastFpsTime >= 1000)
        {
            int fps = (int)(_frameCount * 1000.0f / (now - _lastFpsTime));
            _frameCount = 0;
            _lastFpsTime = now;
            BeginInvoke(() =>
            {
                _lblFps.Text = $"FPS: {fps}";
                _lblResolution.Text = $"解析度: {_session.RemoteWidth}x{_session.RemoteHeight}";
            });
        }

        BeginInvoke(() =>
        {
            _canvas.RecalculateLayout();
            _canvas.Invalidate();

            if (seq >= 2 && !_thumbnailSaved)
            {
                _thumbnailSaved = true;
                TrySaveThumbnail();
            }
        });
    }

    private void TrySaveThumbnail()
    {
        try
        {
            using var thumb = _session.Decoder.CreateThumbnail(280, 158);
            if (thumb != null)
            {
                var dir = Path.Combine(AppConfig.DataDirectory, "thumbnails");
                Directory.CreateDirectory(dir);
                var safeId = !string.IsNullOrEmpty(_session.HostDeviceId) ? _session.HostDeviceId : _session.TargetIdOrHost.Replace(":", "_");
                var path = Path.Combine(dir, $"{safeId}.jpg");
                thumb.Save(path, System.Drawing.Imaging.ImageFormat.Jpeg);

                var cfg = ViewerConfig.Load();
                cfg.UpdateThumbnail(_session.TargetIdOrHost, _session.HostDeviceId, path);
            }
        }
        catch { }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F11)
        {
            ToggleFullscreen();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _prevWindowState = WindowState;
            _prevBorderStyle = FormBorderStyle;
            _prevBounds = Bounds;

            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
            _btnFullscreen.Text = "視窗化 (F11)";
            _isFullscreen = true;
        }
        else
        {
            FormBorderStyle = _prevBorderStyle;
            WindowState = _prevWindowState;
            Bounds = _prevBounds;
            _btnFullscreen.Text = "全螢幕 (F11)";
            _isFullscreen = false;
        }
        _canvas.RecalculateLayout();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _canvas.Focus();
        // 取得焦點時，將本機 Lock Keys 狀態同步給遠端
        byte lockKeys = 0;
        if ((Win32.GetKeyState(0x14) & 1) != 0) lockKeys |= 1; // CapsLock
        if ((Win32.GetKeyState(0x90) & 1) != 0) lockKeys |= 2; // NumLock
        if ((Win32.GetKeyState(0x91) & 1) != 0) lockKeys |= 4; // ScrollLock
        _session.SendLockKeys(lockKeys);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // 失去焦點時放開所有在遠端按下的按鍵，避免卡鍵
        _session.SendKey(0, 0, KeyFlags.Up);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        TrySaveThumbnail();
        _keyboardHook?.Dispose();
        _keyboardHook = null;
        _imeClient.Dispose();
        _session.Dispose();
        base.OnFormClosing(e);
    }

    private sealed record MonitorItem(string DeviceName, string Title)
    {
        public override string ToString() => Title;
    }

    private sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Color.FromArgb(40, 40, 40));
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var p = new Pen(Color.FromArgb(70, 70, 70));
            int mid = e.Item.Width / 2;
            e.Graphics.DrawLine(p, mid, 4, mid, e.Item.Height - 4);
        }
    }
}
