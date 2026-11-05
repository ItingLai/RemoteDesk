using System.Drawing;
using System.Net;
using RemoteDesk.Common;
using RemoteDesk.Host;
using RemoteDesk.Net;
using RemoteDesk.Viewer;

namespace RemoteDesk;

internal sealed class MainForm : Form, IHostUi
{
    private readonly HostConfig _hostConfig;
    private readonly ViewerConfig _viewerConfig;
    private readonly LanDiscovery _discovery;
    private readonly HostServer _server;

    // UI Controls
    private Label _lblDeviceId = null!;
    private Label _lblLocalIps = null!;
    private Label _lblServiceStatus = null!;
    private Panel _pnlActiveSession = null!;
    private Label _lblActiveSessionInfo = null!;
    private Button _btnDisconnectSession = null!;

    private TextBox _txtTargetId = null!;
    private Button _btnConnect = null!;
    private ListView _lvLanDevices = null!;
    private ListView _lvRecent = null!;

    public MainForm()
    {
        _hostConfig = HostConfig.Load();
        _viewerConfig = ViewerConfig.Load();

        _discovery = new LanDiscovery(_hostConfig.DeviceId);
        _server = new HostServer(_hostConfig, _discovery, this);

        InitializeComponent();

        _discovery.DeviceUpdated += OnLanDeviceUpdated;
        _discovery.Start();

        try
        {
            _server.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"啟動被控服務失敗：{ex.Message}\n本機仍可作為控制端連線至其他電腦。", "服務警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        UpdateLocalNetworkInfo();
        UpdateRecentList();
    }

    private void InitializeComponent()
    {
        Text = "RemoteDesk - 內網原生遠端桌面";
        Size = new Size(960, 640);
        MinimumSize = new Size(880, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(243, 244, 246);
        Font = new Font("Microsoft JhengHei UI", 9.5f, FontStyle.Regular);

        // 主分割面板
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterDistance = 340,
            IsSplitterFixed = true,
            SplitterWidth = 2,
            BackColor = Color.FromArgb(220, 224, 230)
        };

        // ---------------- 左側：此裝置 (被控端資訊) ----------------
        var pnlLeft = split.Panel1;
        pnlLeft.BackColor = Color.White;
        pnlLeft.Padding = new Padding(24, 20, 24, 20);

        var lblLeftTitle = new Label
        {
            Text = "此裝置 (被控端)",
            Font = new Font("Microsoft JhengHei UI", 12f, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 30, 30),
            Dock = DockStyle.Top,
            Height = 32
        };

        var pnlIdCard = new Panel
        {
            Dock = DockStyle.Top,
            Height = 110,
            BackColor = Color.FromArgb(245, 247, 252),
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(12)
        };

        var lblIdTip = new Label
        {
            Text = "您的 RemoteDesk 9 位數 ID",
            ForeColor = Color.FromArgb(100, 100, 100),
            Dock = DockStyle.Top,
            Height = 20
        };

        _lblDeviceId = new Label
        {
            Text = AppConfig.FormatId(_hostConfig.DeviceId),
            Font = new Font("Consolas", 20f, FontStyle.Bold),
            ForeColor = Color.FromArgb(26, 115, 232),
            Dock = DockStyle.Top,
            Height = 38,
            Cursor = Cursors.Hand
        };
        _lblDeviceId.Click += (_, _) =>
        {
            Clipboard.SetText(_hostConfig.DeviceId);
            MessageBox.Show(this, $"ID 已複製至剪貼簿：{_hostConfig.DeviceId}", "已複製", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        var btnCopyId = new Button
        {
            Text = "複製 ID",
            Dock = DockStyle.Bottom,
            Height = 26,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White
        };
        btnCopyId.Click += (_, _) =>
        {
            Clipboard.SetText(_hostConfig.DeviceId);
            MessageBox.Show(this, "ID 已複製至剪貼簿", "已複製", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        pnlIdCard.Controls.Add(btnCopyId);
        pnlIdCard.Controls.Add(_lblDeviceId);
        pnlIdCard.Controls.Add(lblIdTip);

        _lblLocalIps = new Label
        {
            Text = "區網 IP: 偵測中...",
            Dock = DockStyle.Top,
            Height = 40,
            ForeColor = Color.FromArgb(70, 70, 70),
            Padding = new Padding(0, 8, 0, 0)
        };

        _lblServiceStatus = new Label
        {
            Text = "● 被控服務：監聽中 (連接埠 21119)",
            ForeColor = Color.FromArgb(46, 125, 50),
            Dock = DockStyle.Top,
            Height = 28
        };

        var btnSetPassword = new Button
        {
            Text = _hostConfig.HasPassword ? "變更 / 清除無人值守密碼" : "設定無人值守連線密碼",
            Dock = DockStyle.Top,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(240, 242, 245)
        };
        btnSetPassword.Click += (_, _) =>
        {
            using var dlg = new PasswordDialog(_hostConfig.HasPassword);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _hostConfig.SetPassword(dlg.NewPassword);
                _hostConfig.TrySave();
                btnSetPassword.Text = _hostConfig.HasPassword ? "變更 / 清除無人值守密碼" : "設定無人值守連線密碼";
                _server.UpdateAdvert();
                MessageBox.Show(this, _hostConfig.HasPassword ? "無人值守密碼已儲存！" : "已清除密碼，改為互動確認授權模式。", "設定完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        // 目前連入中的工作階段卡片
        _pnlActiveSession = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 110,
            BackColor = Color.FromArgb(232, 245, 233),
            BorderStyle = BorderStyle.FixedSingle,
            Visible = false,
            Padding = new Padding(10)
        };
        var lblActiveTitle = new Label { Text = "● 遠端使用者已連線", Font = new Font("Microsoft JhengHei UI", 9.5f, FontStyle.Bold), ForeColor = Color.FromArgb(46, 125, 50), Dock = DockStyle.Top, Height = 22 };
        _lblActiveSessionInfo = new Label { Text = "連線者: --", Dock = DockStyle.Top, Height = 40, ForeColor = Color.FromArgb(40, 40, 40) };
        _btnDisconnectSession = new Button { Text = "強制中斷連線", Dock = DockStyle.Bottom, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(211, 47, 47), ForeColor = Color.White };
        _btnDisconnectSession.Click += (_, _) => _server.DisconnectActiveSession();
        _pnlActiveSession.Controls.Add(_btnDisconnectSession);
        _pnlActiveSession.Controls.Add(_lblActiveSessionInfo);
        _pnlActiveSession.Controls.Add(lblActiveTitle);

        pnlLeft.Controls.Add(_pnlActiveSession);
        pnlLeft.Controls.Add(btnSetPassword);
        pnlLeft.Controls.Add(_lblServiceStatus);
        pnlLeft.Controls.Add(_lblLocalIps);
        pnlLeft.Controls.Add(pnlIdCard);
        pnlLeft.Controls.Add(lblLeftTitle);

        // ---------------- 右側：連線至遠端電腦 ----------------
        var pnlRight = split.Panel2;
        pnlRight.BackColor = Color.FromArgb(248, 249, 251);
        pnlRight.Padding = new Padding(24, 20, 24, 20);

        var lblRightTitle = new Label
        {
            Text = "連線至遠端電腦 (控制端)",
            Font = new Font("Microsoft JhengHei UI", 12f, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 30, 30),
            Dock = DockStyle.Top,
            Height = 32
        };

        var pnlConnectBar = new Panel { Dock = DockStyle.Top, Height = 45 };
        _txtTargetId = new TextBox
        {
            PlaceholderText = "輸入遠端電腦 9 位數 ID 或 IP 地址 (例: 123 456 789 或 192.168.1.50)",
            Font = new Font("Microsoft JhengHei UI", 11f),
            Dock = DockStyle.Fill
        };
        _txtTargetId.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) DoConnect(_txtTargetId.Text); };

        _btnConnect = new Button
        {
            Text = "開始連線",
            Dock = DockStyle.Right,
            Width = 110,
            BackColor = Color.FromArgb(26, 115, 232),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Microsoft JhengHei UI", 10.5f, FontStyle.Bold)
        };
        _btnConnect.Click += (_, _) => DoConnect(_txtTargetId.Text);

        pnlConnectBar.Controls.Add(_txtTargetId);
        pnlConnectBar.Controls.Add(_btnConnect);

        var lblLanTitle = new Label
        {
            Text = "區域網路中已偵測到的電腦 (自動搜尋，點擊兩下立即連線)：",
            Dock = DockStyle.Top,
            Height = 28,
            ForeColor = Color.FromArgb(70, 70, 70),
            Padding = new Padding(0, 10, 0, 0)
        };

        _lvLanDevices = new ListView
        {
            Dock = DockStyle.Top,
            Height = 170,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            BorderStyle = BorderStyle.FixedSingle
        };
        _lvLanDevices.Columns.Add("電腦名稱", 160);
        _lvLanDevices.Columns.Add("裝置 ID", 110);
        _lvLanDevices.Columns.Add("目前 IP", 130);
        _lvLanDevices.Columns.Add("登入使用者", 90);
        _lvLanDevices.Columns.Add("狀態", 70);
        _lvLanDevices.DoubleClick += (_, _) =>
        {
            if (_lvLanDevices.SelectedItems.Count > 0)
            {
                var id = _lvLanDevices.SelectedItems[0].SubItems[1].Text.Replace(" ", "");
                DoConnect(id);
            }
        };

        var lblRecentTitle = new Label
        {
            Text = "最近連線紀錄：",
            Dock = DockStyle.Top,
            Height = 28,
            ForeColor = Color.FromArgb(70, 70, 70),
            Padding = new Padding(0, 10, 0, 0)
        };

        _lvRecent = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            BorderStyle = BorderStyle.FixedSingle
        };
        _lvRecent.Columns.Add("目標名稱", 160);
        _lvRecent.Columns.Add("裝置 ID / 位址", 130);
        _lvRecent.Columns.Add("最後連線 IP", 130);
        _lvRecent.Columns.Add("時間", 120);
        _lvRecent.DoubleClick += (_, _) =>
        {
            if (_lvRecent.SelectedItems.Count > 0)
            {
                var target = _lvRecent.SelectedItems[0].SubItems[1].Text.Replace(" ", "");
                DoConnect(target);
            }
        };

        pnlRight.Controls.Add(_lvRecent);
        pnlRight.Controls.Add(lblRecentTitle);
        pnlRight.Controls.Add(_lvLanDevices);
        pnlRight.Controls.Add(lblLanTitle);
        pnlRight.Controls.Add(pnlConnectBar);
        pnlRight.Controls.Add(lblRightTitle);

        Controls.Add(split);
    }

    private void UpdateLocalNetworkInfo()
    {
        var ips = LanDiscovery.LocalIPv4().Select(x => x.ip.ToString()).ToList();
        _lblLocalIps.Text = ips.Count > 0
            ? $"區網 IP: {string.Join(", ", ips)}"
            : "區網 IP: 未偵測到可用網路介面";
    }

    private void OnLanDeviceUpdated(DiscoveredDevice dev)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            // 排除自己
            if (dev.Id == _hostConfig.DeviceId) return;

            ListViewItem? existing = null;
            foreach (ListViewItem item in _lvLanDevices.Items)
            {
                if (item.Tag is string id && id == dev.Id)
                {
                    existing = item;
                    break;
                }
            }

            if (dev.LastSeen == DateTime.MinValue)
            {
                // 裝置離線
                existing?.Remove();
                return;
            }

            string status = dev.Busy ? "使用中" : "空閒";
            if (existing != null)
            {
                existing.Text = dev.Name;
                existing.SubItems[1].Text = AppConfig.FormatId(dev.Id);
                existing.SubItems[2].Text = dev.Address.ToString();
                existing.SubItems[3].Text = dev.User;
                existing.SubItems[4].Text = status;
            }
            else
            {
                var item = new ListViewItem(new[]
                {
                    dev.Name,
                    AppConfig.FormatId(dev.Id),
                    dev.Address.ToString(),
                    dev.User,
                    status
                }) { Tag = dev.Id };
                _lvLanDevices.Items.Add(item);
            }
        });
    }

    private void UpdateRecentList()
    {
        _lvRecent.Items.Clear();
        foreach (var r in _viewerConfig.Recent)
        {
            var item = new ListViewItem(new[]
            {
                r.Name ?? r.Key,
                AppConfig.FormatId(r.Key),
                r.LastAddress ?? "--",
                r.LastUsed.ToString("yyyy/MM/dd HH:mm")
            }) { Tag = r.Key };
            _lvRecent.Items.Add(item);
        }
    }

    private async void DoConnect(string target)
    {
        string clean = target.Replace(" ", "").Trim();
        if (string.IsNullOrEmpty(clean))
        {
            MessageBox.Show(this, "請輸入遠端電腦 9 位數 ID 或 IP 位址！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (clean == _hostConfig.DeviceId)
        {
            MessageBox.Show(this, "不能連線到本機自己的 ID！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _btnConnect.Enabled = false;
        _btnConnect.Text = "連線中...";

        ViewerSession? session = null;
        try
        {
            session = new ViewerSession(clean, _viewerConfig, _discovery);

            string? savedPwd = _viewerConfig.GetSavedPassword(clean);
            string? pwd = savedPwd;

            try
            {
                // 嘗試以已知密碼或互動授權連線
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                await session.ConnectAsync(pwd, cts.Token).ConfigureAwait(true);
            }
            catch (UnauthorizedAccessException)
            {
                // 若密碼錯誤或需要密碼，彈出輸入密碼對話盒
                using var pwdDlg = new ConnectPasswordDialog(clean, savedPwd);
                if (pwdDlg.ShowDialog(this) == DialogResult.OK)
                {
                    pwd = pwdDlg.Password;
                    if (pwdDlg.RememberPassword)
                    {
                        _viewerConfig.RememberPassword(clean, pwd);
                        _viewerConfig.Save();
                    }

                    session.Dispose();
                    session = new ViewerSession(clean, _viewerConfig, _discovery);
                    using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                    await session.ConnectAsync(pwd, cts2.Token).ConfigureAwait(true);
                }
                else
                {
                    session.Dispose();
                    return;
                }
            }

            // 連線成功，記錄到最近連線
            _viewerConfig.TouchRecent(clean, session.HostDeviceId, session.HostDeviceName, null, Protocol.DefaultTcpPort);
            _viewerConfig.Save();
            UpdateRecentList();

            // 開啟遠端視窗
            var viewerForm = new ViewerForm(session);
            viewerForm.Show();
        }
        catch (Exception ex)
        {
            session?.Dispose();
            MessageBox.Show(this, $"無法連線至遠端電腦：\n{ex.Message}", "連線失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _btnConnect.Enabled = true;
            _btnConnect.Text = "開始連線";
        }
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
            _lblActiveSessionInfo.Text = $"連線者：{session.ViewerName}\nID：{AppConfig.FormatId(session.ViewerId)}\n位址：{session.RemoteAddress}";
            _pnlActiveSession.Visible = true;
        });
    }

    public void SessionEnded(HostSession session)
    {
        BeginInvoke(() =>
        {
            _pnlActiveSession.Visible = false;
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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _discovery.Dispose();
        _server.Dispose();
        base.OnFormClosing(e);
    }
}
