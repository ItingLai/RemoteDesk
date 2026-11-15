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
    private readonly ToolStripDropDownButton _btnResolutionMode;
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
    private int _quality;
    private int _fps;
    private bool _lossless;

    internal int SelectedQuality => _quality;
    internal int SelectedMaxFps => _fps;
    internal bool SelectedLossless => _lossless;
    internal string QualityButtonText => _btnQuality.Text ?? "";
    internal string ResolutionModeButtonText => _btnResolutionMode.Text ?? "";
    internal string LatencyLabelText => _lblLatency.Text ?? "";
    internal string FpsLabelText => _lblFps.Text ?? "";
    internal string ResolutionLabelText => _lblResolution.Text ?? "";
    internal ToolStripDropDownButton QualityDropDown => _btnQuality;
    internal ToolStripDropDownButton ResolutionModeDropDown => _btnResolutionMode;

    public ViewerForm(ViewerSession session)
    {
        _session = session;
        _quality = session.CurrentQuality;
        _fps = session.CurrentMaxFps;
        _lossless = session.CurrentLossless;

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

        // 畫質選項（純控制 JPEG Quality，與解析度完全解耦；初值直接從 Session/設定同步）
        _btnQuality = new ToolStripDropDownButton(FormatQualityButtonText(_quality));
        _btnQuality.DropDownItems.Add("低負載壓縮 (50% 畫質, 30 FPS)", null, (_, _) => SetQuality(50, 30, false, "低負載 (50%)"));
        _btnQuality.DropDownItems.Add("平衡推薦 (70% 畫質, 30 FPS, 無失真修補)", null, (_, _) => SetQuality(70, 30, true, "平衡 (70%)"));
        _btnQuality.DropDownItems.Add("極致超清 (90% 畫質, 60 FPS, 無失真修補)", null, (_, _) => SetQuality(90, 60, true, "極致 (90%)"));

        // 解析度自適應模式選項（與畫質獨立控制）
        _btnResolutionMode = new ToolStripDropDownButton(FormatResolutionModeLabel(session.CurrentResolutionMode));
        _btnResolutionMode.DropDownItems.Add("Auto 自動 (原生優先，僅壅塞降級)", null, (_, _) => SetResolutionMode(ResolutionMode.Auto));
        _btnResolutionMode.DropDownItems.Add("Original 原生鎖定 (強制維持原始解析度)", null, (_, _) => SetResolutionMode(ResolutionMode.Original));
        _btnResolutionMode.DropDownItems.Add("Performance 流暢優先 (積極確保操作順暢)", null, (_, _) => SetResolutionMode(ResolutionMode.Performance));

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
            _btnResolutionMode,
            _btnFullscreen,
            new ToolStripSeparator(),
            _btnDisconnect
        });

        // 3. 狀態列
        _statusStrip = new StatusStrip { BackColor = Color.FromArgb(32, 32, 32), ForeColor = Color.Silver };
        _lblHostInfo = new ToolStripStatusLabel($"主機: {session.HostDeviceName} ({session.HostDeviceId})");
        _lblResolution = new ToolStripStatusLabel("解析度: --");
        _lblFps = new ToolStripStatusLabel(FormatFpsLabel(0, _fps, _quality));
        _lblLatency = new ToolStripStatusLabel($"延遲: -- ({session.TransportTypeLabel})");
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

        // 5. 註冊 Session 事件（統一經 SafeUiInvoke 防護處置與無 Handle 例外）
        _session.ResolutionChanged += (w, h) => SafeUiInvoke(() =>
        {
            _canvas.RecalculateLayout();
            _canvas.Invalidate();
            UpdateResolutionStatusLabel();
        });
        _session.StreamConfigChanged += (origW, origH, streamW, streamH, level, mode, reason) => SafeUiInvoke(() =>
        {
            _btnResolutionMode.Text = FormatResolutionModeLabel(_session.CurrentResolutionMode);
            UpdateResolutionStatusLabel();
            _canvas.RecalculateLayout();
            _canvas.Invalidate();
        });
        _session.TelemetryUpdated += t => SafeUiInvoke(() => UpdateStatusTelemetry(t));
        _session.FrameReceived += OnSessionFrameReceived;
        _session.MonitorsUpdated += OnSessionMonitorsUpdated;
        _session.CursorShapeChanged += cur => SafeUiInvoke(() => _canvas.Cursor = cur ?? Cursors.Default);
        _session.ImeStateReceived += state => _imeClient.OnRemoteImeStateReceived(state);
        _session.CaretReceived += (x, y, h) => _imeClient.UpdateCaretPosition(x, y, h);
        _session.ClipboardReceived += OnSessionClipboardReceived;
        _session.RttUpdated += ms => SafeUiInvoke(() => UpdateLatencyFromRtt(ms));
        _session.Disconnected += reason => SafeUiInvoke(() =>
        {
            MessageBox.Show(this, $"連線已結束: {reason}", "RemoteDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        });

        // 6. 安裝鍵盤攔截器 (原生鍵盤傳遞)
        _keyboardHook = new KeyboardHook(
            () => Form.ActiveForm == this && _canvas.Focused,
            (vk, scan, flags) => _session.SendKey(vk, scan, flags),
            () => _isFullscreen,
            () => SafeUiInvoke(() => { if (_isFullscreen) ToggleFullscreen(); }));

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

    private void SafeUiInvoke(Action action)
    {
        if (IsDisposed || Disposing) return;
        try
        {
            if (!InvokeRequired)
            {
                action();
                return;
            }
            if (!IsHandleCreated) return;
            BeginInvoke(action);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void OnSessionClipboardReceived(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        SafeUiInvoke(() =>
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

    internal static string FormatQualityButtonText(int quality) => quality switch
    {
        <= 55 => $"畫質: 低負載 ({quality}%)",
        >= 85 => $"畫質: 極致 ({quality}%)",
        _ => $"畫質: 平衡 ({quality}%)"
    };

    private static string FormatFpsLabel(float renderFps, int targetFps, int quality) =>
        $"FPS: {renderFps:F0}/{targetFps} | 品質:{quality}%";

    private static string FormatTelemetryLatencyLabel(HostStreamTelemetry t, string transportLabel, int? overrideRttMs = null)
    {
        double rtt = overrideRttMs ?? t.RttMs;
        string warnTag = t.CongestionWarning ? " [原生壅塞警告]" : "";
        return $"{transportLabel} | RTT:{rtt:F0}ms E2E:{t.EndToEndDelayMs:F0}ms Q:{t.SendQueueWaitMs:F0}ms{warnTag}";
    }

    private void SetQuality(int quality, int fps, bool lossless, string label)
    {
        _session.SendVideoSettings(quality, fps, lossless, _session.CurrentResolutionMode);
        _quality = _session.CurrentQuality;
        _fps = _session.CurrentMaxFps;
        _lossless = _session.CurrentLossless;
        _btnQuality.Text = FormatQualityButtonText(_quality);
    }

    private void SetResolutionMode(ResolutionMode mode)
    {
        // P1-01: 切換解析度模式只更新 ResolutionMode，完整保留現有 Quality / MaxFps / Lossless 設定
        _session.SetResolutionMode(mode);
        _quality = _session.CurrentQuality;
        _fps = _session.CurrentMaxFps;
        _lossless = _session.CurrentLossless;
        _btnResolutionMode.Text = FormatResolutionModeLabel(_session.CurrentResolutionMode);
    }

    private static string FormatResolutionModeLabel(ResolutionMode mode) => mode switch
    {
        ResolutionMode.Original => "解析度: 原生鎖定",
        ResolutionMode.Performance => "解析度: 流暢優先",
        _ => "解析度: 自動"
    };

    private void UpdateResolutionStatusLabel()
    {
        int ow = _session.OriginalWidth > 0 ? _session.OriginalWidth : _session.RemoteWidth;
        int oh = _session.OriginalHeight > 0 ? _session.OriginalHeight : _session.RemoteHeight;
        int sw = _session.StreamWidth > 0 ? _session.StreamWidth : ow;
        int sh = _session.StreamHeight > 0 ? _session.StreamHeight : oh;
        int lvl = (int)_session.CurrentResolutionLevel;
        _lblResolution.Text = (sw == ow && sh == oh)
            ? $"解析度: {ow}x{oh} (原生 L0)"
            : $"解析度: 原{ow}x{oh} / 串流{sw}x{sh} (L{lvl})";
    }

    private void UpdateLatencyFromRtt(int ms)
    {
        if (_session.CurrentTelemetry is { } t)
        {
            _lblLatency.Text = FormatTelemetryLatencyLabel(t, _session.TransportTypeLabel, overrideRttMs: ms);
        }
        else
        {
            _lblLatency.Text = $"延遲: {ms} ms ({_session.TransportTypeLabel})";
        }
    }

    private void UpdateStatusTelemetry(HostStreamTelemetry t)
    {
        UpdateResolutionStatusLabel();
        _btnResolutionMode.Text = FormatResolutionModeLabel(_session.CurrentResolutionMode);
        _lblResolution.ForeColor = t.CongestionWarning ? Color.Orange : Color.Silver;
        _lblFps.Text = FormatFpsLabel(t.ViewerRenderFps, t.TargetFps, _session.CurrentQuality);
        _lblLatency.Text = FormatTelemetryLatencyLabel(t, _session.TransportTypeLabel);
        string bwText = t.EstimatedBandwidthMbps > 0f ? $"{t.EstimatedBandwidthMbps:F2} Mbps" : (t.IsUdpTransport ? "樣本不足/閒置" : "N/A(TCP)");
        string idleTag = t.IsStreamIdle ? " [靜態閒置]" : string.Empty;
        _lblLatency.ToolTipText =
            $"模式:{t.Mode} L{(int)t.Level} (Cfg v{t.ConfigVersion}) | 品質:{t.Quality}% (本機設定:{_session.CurrentQuality}%){idleTag} | 視窗:{t.TelemetryWindowMs}ms\n" +
            $"原:{t.OriginalWidth}x{t.OriginalHeight} 串流:{t.StreamWidth}x{t.StreamHeight} | 管線上限:{t.EffectiveMaxInflightFrames}幀\n" +
            $"FPS(Send/Render/Target): {t.ActualSendFps:F1}/{t.ViewerRenderFps:F1}/{t.TargetFps} Drops:{t.FrameDropCount}\n" +
            $"Goodput/Wire/Avg/Peak: {t.GoodputMbps:F2}/{t.WireThroughputMbps:F2}/{t.AvgThroughputMbps:F2}/{t.PeakThroughputMbps:F2} Mbps EstBW:{bwText}\n" +
            $"FrameAvg:{t.AverageFrameSizeBytes / 1024.0:F1}KB Enc/Dec/Ren: {t.AvgEncodeDurationMs:F1}/{t.AvgDecodeDurationMs:F1}/{t.AvgRenderDurationMs:F1}ms\n" +
            $"E2E({t.EndToEndDelayStages}): {t.EndToEndDelayMs:F1}ms\n" +
            $"Transport: {t.TransportSummary}\n" +
            $"SwitchReason: {t.LastSwitchReason}";
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
        SafeUiInvoke(() =>
        {
            string modeDesc = state.IsNative ? "中文" : "英文";
            _lblImeStatus.Text = $"輸入法綁定: {modeDesc} (配置: 0x{state.LangId:X4})";
        });
    }

    private void OnSessionMonitorsUpdated(List<MonitorInfo> monitors, string current)
    {
        SafeUiInvoke(() =>
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
            SafeUiInvoke(() =>
            {
                int targetFps = _session.CurrentTelemetry?.TargetFps ?? _session.CurrentMaxFps;
                _lblFps.Text = FormatFpsLabel(fps, targetFps, _session.CurrentQuality);
                UpdateResolutionStatusLabel();
            });
        }

        if (!dirtyRect.IsEmpty)
        {
            SafeUiInvoke(() => _canvas.InvalidateRemoteRect(dirtyRect));
        }
    }

    private void TrySaveThumbnail()
    {
        // 依據需求 4：設備代表圖片改採用設備桌面，不儲存螢幕截圖
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_isFullscreen && (keyData == Keys.Escape || keyData == Keys.F11))
        {
            ToggleFullscreen();
            return true;
        }
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

            _toolbar.Visible = false;
            _statusStrip.Visible = false;

            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = Screen.FromControl(this).Bounds;
            _btnFullscreen.Text = "視窗化 (ESC / F11)";
            _isFullscreen = true;
            _canvas.RecalculateLayout();
            _canvas.Focus();
        }
        else
        {
            _toolbar.Visible = true;
            _statusStrip.Visible = true;
            FormBorderStyle = _prevBorderStyle;
            WindowState = _prevWindowState;
            Bounds = _prevBounds;
            _btnFullscreen.Text = "全螢幕 (F11)";
            _isFullscreen = false;
            _canvas.RecalculateLayout();
        }
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _keyboardHook?.Dispose();
            _keyboardHook = null;
            _imeClient.Dispose();
        }
        base.Dispose(disposing);
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
