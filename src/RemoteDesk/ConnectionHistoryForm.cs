using System.Drawing;
using RemoteDesk.Common;
using RemoteDesk.Controls;

namespace RemoteDesk;

/// <summary>
/// 依據規格第 8 節：獨立完整連線紀錄管理視窗 (Connection History)。
/// 支援搜尋、依設備重新連線、清空紀錄，不干擾首頁縮圖式呈現。
/// </summary>
internal sealed class ConnectionHistoryForm : Form
{
    private readonly ViewerConfig _viewerConfig;
    private readonly Action<string> _onReconnect;
    private readonly Action _onHistoryChanged;

    private readonly TextBox _txtSearch;
    private readonly ListView _lvHistory;
    private readonly Label _lblEmpty;

    public ConnectionHistoryForm(ViewerConfig viewerConfig, Action<string> onReconnect, Action onHistoryChanged)
    {
        _viewerConfig = viewerConfig;
        _onReconnect = onReconnect;
        _onHistoryChanged = onHistoryChanged;

        Text = "Connection History - 連線紀錄";
        Size = new Size(780, 520);
        MinimumSize = new Size(650, 400);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        Font = Theme.BodyFont;
        AppBranding.ApplyToForm(this);

        // 頂部搜尋列
        var pnlTop = new Panel
        {
            Dock = DockStyle.Top,
            Height = 60,
            BackColor = Theme.Surface,
            Padding = new Padding(16, 12, 16, 12)
        };

        var lblTitle = new Label
        {
            Text = "🕒 連線歷史明細 (Connection History)",
            Font = Theme.CreateFont(12f, FontStyle.Bold),
            ForeColor = Theme.TextPrimary,
            Location = new Point(16, 18),
            AutoSize = true
        };

        _txtSearch = new TextBox
        {
            PlaceholderText = "搜尋設備名稱或 ID...",
            Font = Theme.BodyFont,
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(pnlTop.Width - 280, 15),
            Size = new Size(260, 26),
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        _txtSearch.TextChanged += (_, _) => PopulateList();

        pnlTop.Controls.AddRange(new Control[] { lblTitle, _txtSearch });

        // 底部按鈕列
        var pnlBottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            BackColor = Theme.Surface,
            Padding = new Padding(16, 8, 16, 8)
        };

        var btnReconnect = new Button
        {
            Text = "⚡ 重新連線所選設備",
            Size = new Size(175, Theme.ButtonHeight),
            Location = new Point(16, 8),
            BackColor = Theme.Primary,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.ButtonFont,
            Cursor = Cursors.Hand
        };
        btnReconnect.FlatAppearance.BorderSize = 0;
        btnReconnect.Click += (_, _) =>
        {
            var selected = _lvHistory?.SelectedItems?.Cast<ListViewItem>()?.FirstOrDefault();
            if (selected?.Tag is string key)
            {
                Close();
                _onReconnect(key);
            }
            else
            {
                MessageBox.Show(this, "請先選擇要連線的設備紀錄！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        var btnRename = new Button
        {
            Text = "✏️ 重新命名",
            Size = new Size(110, Theme.ButtonHeight),
            Location = new Point(200, 8),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.BodyFont,
            Cursor = Cursors.Hand
        };
        btnRename.FlatAppearance.BorderColor = Theme.Border;
        btnRename.Click += (_, _) =>
        {
            var selected = _lvHistory?.SelectedItems?.Cast<ListViewItem>()?.FirstOrDefault();
            if (selected?.Tag is string key)
            {
                var entry = _viewerConfig.Recent.FirstOrDefault(x => x.Key == key);
                if (entry != null)
                {
                    using var dlg = new Controls.RenameDeviceDialog(entry.Name ?? "", entry.DeviceId ?? entry.Key);
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        entry.Name = string.IsNullOrWhiteSpace(dlg.NewName) ? null : dlg.NewName.Trim();
                        _viewerConfig.Save();
                        _onHistoryChanged();
                        PopulateList();
                    }
                }
            }
            else
            {
                MessageBox.Show(this, "請先選擇要重新命名的設備紀錄！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        var btnClear = new Button
        {
            Text = "🗑️ 清空歷史紀錄",
            Size = new Size(130, Theme.ButtonHeight),
            Location = new Point(320, 8),
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.TextSecondary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.BodyFont,
            Cursor = Cursors.Hand
        };
        btnClear.FlatAppearance.BorderColor = Theme.Border;
        btnClear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "確定要清空所有連線歷史紀錄嗎？", "確認清空", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _viewerConfig.Recent.Clear();
                _viewerConfig.Save();
                _onHistoryChanged();
                PopulateList();
            }
        };

        var btnClose = new Button
        {
            Text = "關閉",
            DialogResult = DialogResult.Cancel,
            Size = new Size(80, Theme.ButtonHeight),
            Location = new Point(pnlBottom.Width - 96, 8),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Theme.SurfaceElevated,
            ForeColor = Theme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.BodyFont,
            Cursor = Cursors.Hand
        };
        btnClose.FlatAppearance.BorderColor = Theme.Border;
        btnClose.Click += (_, _) => Close();

        pnlBottom.Controls.AddRange(new Control[] { btnReconnect, btnRename, btnClear, btnClose });

        // 中間列表
        _lvHistory = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            BackColor = Theme.Background,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.None,
            Font = Theme.BodyFont
        };
        _lvHistory.Columns.Add("設備名稱 (Device Name)", 180);
        _lvHistory.Columns.Add("設備 ID (Device ID)", 150);
        _lvHistory.Columns.Add("最後連線時間 (Last Used)", 180);
        _lvHistory.Columns.Add("最後位址 (Address)", 140);
        _lvHistory.Columns.Add("最愛狀態", 80);

        _lvHistory.DoubleClick += (_, _) =>
        {
            var selected = _lvHistory.SelectedItems.Cast<ListViewItem>().FirstOrDefault();
            if (selected?.Tag is string key)
            {
                Close();
                _onReconnect(key);
            }
        };

        var lvMenu = new ContextMenuStrip { BackColor = Theme.SurfaceElevated, ForeColor = Theme.TextPrimary, ShowImageMargin = false };
        lvMenu.Items.Add(new ToolStripMenuItem("⚡ 立即連線", null, (_, _) =>
        {
            var selected = _lvHistory?.SelectedItems?.Cast<ListViewItem>()?.FirstOrDefault();
            if (selected?.Tag is string key) { Close(); _onReconnect(key); }
        }));
        lvMenu.Items.Add(new ToolStripMenuItem("✏️ 重新命名設備", null, (_, _) => btnRename.PerformClick()));
        lvMenu.Items.Add(new ToolStripSeparator());
        lvMenu.Items.Add(new ToolStripMenuItem("✕ 移除此紀錄", null, (_, _) =>
        {
            var selected = _lvHistory?.SelectedItems?.Cast<ListViewItem>()?.FirstOrDefault();
            if (selected?.Tag is string key)
            {
                _viewerConfig.RemoveRecent(key);
                _onHistoryChanged();
                PopulateList();
            }
        }));
        _lvHistory.ContextMenuStrip = lvMenu;

        _lblEmpty = new Label
        {
            Text = "尚無任何連線紀錄。",
            ForeColor = Theme.TextSecondary,
            Font = Theme.CreateFont(11f),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Visible = false
        };

        Controls.Add(_lvHistory);
        Controls.Add(_lblEmpty);
        Controls.Add(pnlBottom);
        Controls.Add(pnlTop);

        PopulateList();
    }

    private void PopulateList()
    {
        _lvHistory.Items.Clear();
        var filter = _txtSearch.Text.Trim();
        var list = _viewerConfig.GetRecent();

        if (!string.IsNullOrEmpty(filter))
        {
            list = list.Where(r =>
                (r.Name != null && r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                (r.DeviceId != null && r.DeviceId.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                r.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        if (list.Count == 0)
        {
            _lblEmpty.Visible = true;
            _lblEmpty.BringToFront();
            return;
        }

        _lblEmpty.Visible = false;
        foreach (var entry in list)
        {
            var item = new ListViewItem(string.IsNullOrEmpty(entry.Name) ? entry.Key : entry.Name)
            {
                Tag = entry.Key,
                ForeColor = Theme.TextPrimary,
                BackColor = Theme.Surface
            };

            var idText = !string.IsNullOrEmpty(entry.DeviceId) ? AppConfig.FormatId(entry.DeviceId) : entry.Key;
            item.SubItems.Add(idText);
            item.SubItems.Add(entry.LastUsed.ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add(string.IsNullOrEmpty(entry.LastAddress) ? "區網直連" : $"{entry.LastAddress}:{entry.Port}");
            item.SubItems.Add(entry.IsFavorite ? "⭐ 最愛" : "-");

            _lvHistory.Items.Add(item);
        }
    }
}
