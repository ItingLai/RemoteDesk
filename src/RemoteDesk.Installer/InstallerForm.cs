using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace RemoteDesk.Installer;

/// <summary>
/// RemoteDesk 現代化安裝精靈介面：
/// 提供自訂安裝路徑挑選、桌面捷徑勾選、開始功能表與安裝進度呈現。
/// </summary>
public sealed class InstallerForm : Form
{
    private static readonly Color Surface = Color.FromArgb(31, 41, 55);         // #1F2937
    private static readonly Color Background = Color.FromArgb(17, 24, 39);       // #111827
    private static readonly Color SurfaceElevated = Color.FromArgb(45, 55, 72);  // #2D3748
    private static readonly Color Border = Color.FromArgb(55, 65, 81);          // #374151
    private static readonly Color TextPrimary = Color.FromArgb(243, 244, 246);   // #F3F4F6
    private static readonly Color TextSecondary = Color.FromArgb(156, 163, 175); // #9CA3AF
    private static readonly Color Accent = Color.FromArgb(59, 130, 246);        // #3B82F6
    private static readonly Color Success = Color.FromArgb(16, 185, 129);       // #10B981

    private readonly TextBox _txtPath = null!;
    private readonly CheckBox _chkDesktopShortcut = null!;
    private readonly CheckBox _chkStartMenuShortcut = null!;
    private readonly CheckBox _chkLaunch = null!;
    private readonly Label _lblSpace = null!;
    private readonly Label _lblStatus = null!;
    private readonly CustomProgressBar _progressBar = null!;
    private readonly Button _btnInstall = null!;
    private readonly Button _btnCancel = null!;
    private readonly Button _btnBrowse = null!;

    private bool _isInstalling;
    private bool _isCompleted;
    private Bitmap? _cachedLogo;

    public InstallerForm()
    {
        Text = "RemoteDesk 安裝精靈";
        Size = new Size(560, 480);
        MinimumSize = Size;
        MaximumSize = Size;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        BackColor = Background;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

        LoadAppIcon();

        // ---------------- 1. 頂部品牌標題列 ----------------
        var pnlHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 72,
            BackColor = Surface,
            Padding = new Padding(24, 12, 24, 12)
        };
        pnlHeader.Paint += (_, pe) =>
        {
            var g = pe.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            if (_cachedLogo != null)
            {
                g.DrawImage(_cachedLogo, new Rectangle(20, 16, 40, 40));
            }

            using var pen = new Pen(Border, 1);
            g.DrawLine(pen, 0, pnlHeader.Height - 1, pnlHeader.Width, pnlHeader.Height - 1);
        };

        var lblHeaderTitle = new Label
        {
            Text = "RemoteDesk 遠端桌面 安裝精靈",
            Font = new Font("Segoe UI", 12.5f, FontStyle.Bold),
            ForeColor = TextPrimary,
            Location = new Point(72, 14),
            AutoSize = true
        };

        var lblHeaderSub = new Label
        {
            Text = "版本 1.0.1 · 高速內網遠端控制 · 繁體中文",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
            ForeColor = TextSecondary,
            Location = new Point(73, 40),
            AutoSize = true
        };
        pnlHeader.Controls.AddRange(new Control[] { lblHeaderTitle, lblHeaderSub });

        // ---------------- 2. 底部操作按鈕列 ----------------
        var pnlFooter = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 60,
            BackColor = Surface,
            Padding = new Padding(20, 12, 24, 12)
        };
        pnlFooter.Paint += (_, pe) =>
        {
            using var pen = new Pen(Border, 1);
            pe.Graphics.DrawLine(pen, 0, 0, pnlFooter.Width, 0);
        };

        _btnInstall = new Button
        {
            Text = "立即安裝",
            Size = new Size(120, 36),
            Location = new Point(pnlFooter.Width - 144, 12),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Accent,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnInstall.FlatAppearance.BorderSize = 0;
        _btnInstall.Click += (_, _) => OnInstallOrFinish();

        _btnCancel = new Button
        {
            Text = "取消",
            Size = new Size(90, 36),
            Location = new Point(pnlFooter.Width - 244, 12),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = SurfaceElevated,
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        _btnCancel.FlatAppearance.BorderColor = Border;
        _btnCancel.Click += (_, _) => Close();

        pnlFooter.Controls.AddRange(new Control[] { _btnInstall, _btnCancel });

        // ---------------- 3. 中間內容區 ----------------
        var pnlBody = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Background,
            Padding = new Padding(28, 20, 28, 16)
        };

        // 3A. 安裝路徑區塊
        var lblPathTitle = new Label
        {
            Text = "安裝目的地資料夾 (Destination Folder)：",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = TextPrimary,
            Location = new Point(28, 16),
            AutoSize = true
        };

        _txtPath = new TextBox
        {
            Text = InstallerEngine.GetDefaultInstallDirectory(),
            Location = new Point(28, 42),
            Size = new Size(385, 30),
            BackColor = Surface,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular)
        };
        _txtPath.TextChanged += (_, _) => UpdateFreeSpaceInfo();

        _btnBrowse = new Button
        {
            Text = "瀏覽...",
            Location = new Point(420, 41),
            Size = new Size(88, 29),
            BackColor = SurfaceElevated,
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular),
            Cursor = Cursors.Hand
        };
        _btnBrowse.FlatAppearance.BorderColor = Border;
        _btnBrowse.Click += (_, _) => OnBrowseDirectory();

        _lblSpace = new Label
        {
            Location = new Point(28, 76),
            Size = new Size(480, 20),
            ForeColor = TextSecondary,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Regular)
        };
        UpdateFreeSpaceInfo();

        // 3B. 自訂安裝選項
        var lblOptionsTitle = new Label
        {
            Text = "安裝自訂選項 (Setup Options)：",
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            ForeColor = TextPrimary,
            Location = new Point(28, 110),
            AutoSize = true
        };

        _chkDesktopShortcut = new CheckBox
        {
            Text = "建立桌面捷徑 (Create Desktop Shortcut)",
            Location = new Point(32, 136),
            Size = new Size(450, 24),
            Checked = true,
            ForeColor = TextPrimary,
            Cursor = Cursors.Hand
        };

        _chkStartMenuShortcut = new CheckBox
        {
            Text = "建立「開始」功能表捷徑 (Create Start Menu Shortcut)",
            Location = new Point(32, 166),
            Size = new Size(450, 24),
            Checked = true,
            ForeColor = TextPrimary,
            Cursor = Cursors.Hand
        };

        _chkLaunch = new CheckBox
        {
            Text = "安裝完成後立即啟動 RemoteDesk",
            Location = new Point(32, 196),
            Size = new Size(450, 24),
            Checked = true,
            ForeColor = TextPrimary,
            Cursor = Cursors.Hand
        };

        // 3C. 進度條與狀態文字
        _lblStatus = new Label
        {
            Text = "準備就緒，點擊「立即安裝」開始安裝程式。",
            Location = new Point(28, 240),
            Size = new Size(480, 20),
            ForeColor = TextSecondary,
            Font = new Font("Segoe UI", 9f, FontStyle.Regular)
        };

        _progressBar = new CustomProgressBar
        {
            Location = new Point(28, 264),
            Size = new Size(480, 10),
            Value = 0,
            Maximum = 100,
            BarColor = Accent,
            BgColor = Surface
        };

        pnlBody.Controls.AddRange(new Control[]
        {
            lblPathTitle, _txtPath, _btnBrowse, _lblSpace,
            lblOptionsTitle, _chkDesktopShortcut, _chkStartMenuShortcut, _chkLaunch,
            _lblStatus, _progressBar
        });

        Controls.Add(pnlBody);
        Controls.Add(pnlFooter);
        Controls.Add(pnlHeader);
    }

    private void LoadAppIcon()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var icoStream = asm.GetManifestResourceStream("RemoteDesk.Installer.app.ico");
            if (icoStream != null)
            {
                Icon = new Icon(icoStream);
            }

            using var logoStream = asm.GetManifestResourceStream("RemoteDesk.Installer.Resources.logo.png");
            if (logoStream != null)
            {
                _cachedLogo = new Bitmap(logoStream);
            }
        }
        catch { }
    }

    private void UpdateFreeSpaceInfo()
    {
        try
        {
            string path = _txtPath.Text.Trim();
            if (string.IsNullOrEmpty(path))
            {
                _lblSpace.Text = "所需空間：約 15 MB";
                return;
            }

            long free = InstallerEngine.GetAvailableFreeSpace(path);
            if (free >= 0)
            {
                double gb = (double)free / (1024 * 1024 * 1024);
                _lblSpace.Text = $"所需磁碟空間：約 15 MB   |   磁碟可用空間：{gb:F1} GB";
            }
            else
            {
                _lblSpace.Text = "所需磁碟空間：約 15 MB";
            }
        }
        catch
        {
            _lblSpace.Text = "所需磁碟空間：約 15 MB";
        }
    }

    private void OnBrowseDirectory()
    {
        using var fbd = new FolderBrowserDialog
        {
            Description = "請挑選 RemoteDesk 安裝目的地資料夾：",
            UseDescriptionForTitle = true,
            SelectedPath = _txtPath.Text.Trim()
        };

        if (fbd.ShowDialog(this) == DialogResult.OK)
        {
            string path = fbd.SelectedPath;
            if (!path.EndsWith("RemoteDesk", StringComparison.OrdinalIgnoreCase))
            {
                path = Path.Combine(path, "RemoteDesk");
            }
            _txtPath.Text = path;
        }
    }

    private async void OnInstallOrFinish()
    {
        if (_isCompleted)
        {
            if (_chkLaunch.Checked)
            {
                string exePath = Path.Combine(_txtPath.Text.Trim(), $"{InstallerEngine.AppName}.exe");
                if (File.Exists(exePath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                    }
                    catch { }
                }
            }
            Close();
            return;
        }

        if (_isInstalling) return;

        string targetDir = _txtPath.Text.Trim();
        if (string.IsNullOrEmpty(targetDir))
        {
            MessageBox.Show(this, "請指定有效的安裝路徑！", "安裝提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _isInstalling = true;
        _txtPath.Enabled = false;
        _btnBrowse.Enabled = false;
        _chkDesktopShortcut.Enabled = false;
        _chkStartMenuShortcut.Enabled = false;
        _btnCancel.Enabled = false;
        _btnInstall.Enabled = false;

        bool createDesktop = _chkDesktopShortcut.Checked;
        bool createStartMenu = _chkStartMenuShortcut.Checked;

        try
        {
            await Task.Run(() =>
            {
                InstallerEngine.Install(targetDir, createDesktop, createStartMenu, (percent, msg) =>
                {
                    Invoke(() =>
                    {
                        _progressBar.Value = Math.Min(100, Math.Max(0, percent));
                        _lblStatus.Text = msg;
                    });
                });
            });

            _isCompleted = true;
            _lblStatus.Text = "✓ RemoteDesk 安裝成功！";
            _lblStatus.ForeColor = Success;
            _progressBar.Value = 100;
            _progressBar.BarColor = Success;

            _btnInstall.Text = "完成";
            _btnInstall.BackColor = Success;
            _btnInstall.Enabled = true;
            _btnCancel.Visible = false;
        }
        catch (Exception ex)
        {
            _isInstalling = false;
            _txtPath.Enabled = true;
            _btnBrowse.Enabled = true;
            _chkDesktopShortcut.Enabled = true;
            _chkStartMenuShortcut.Enabled = true;
            _btnCancel.Enabled = true;
            _btnInstall.Enabled = true;

            MessageBox.Show(this, $"安裝過程發生錯誤：\n{ex.Message}", "安裝失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _lblStatus.Text = "安裝中斷，請重試。";
            _lblStatus.ForeColor = Color.FromArgb(239, 68, 68);
        }
    }

    private sealed class CustomProgressBar : Control
    {
        private int _value;
        public int Value
        {
            get => _value;
            set { _value = value; Invalidate(); }
        }

        public int Maximum { get; set; } = 100;
        public Color BarColor { get; set; } = Color.DodgerBlue;
        public Color BgColor { get; set; } = Color.FromArgb(31, 41, 55);

        public CustomProgressBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Background
            using var bgBrush = new SolidBrush(BgColor);
            using var bgPath = CreateRoundedRect(ClientRectangle, 5);
            g.FillPath(bgBrush, bgPath);

            // Foreground Fill
            if (Value > 0 && Maximum > 0)
            {
                int fillWidth = (int)((float)Value / Maximum * ClientRectangle.Width);
                if (fillWidth > 6)
                {
                    var fillRect = new Rectangle(0, 0, fillWidth, ClientRectangle.Height);
                    using var fillBrush = new SolidBrush(BarColor);
                    using var fillPath = CreateRoundedRect(fillRect, 5);
                    g.FillPath(fillBrush, fillPath);
                }
            }
        }

        private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int diameter = radius * 2;
            var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

            path.AddArc(arc, 180, 90);
            arc.X = bounds.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = bounds.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
