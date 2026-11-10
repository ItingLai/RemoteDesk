using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using RemoteDesk.Native;

namespace RemoteDesk.Common;

/// <summary>
/// 應用程式品牌與圖標管理：
/// 提供去背透明的高解析度 Logo 與 Windows 多解析度圖標 (包含工作列、視窗標題列、系統匣與 UI 品牌標誌)。
/// </summary>
public static class AppBranding
{
    /// <summary>
    /// RemoteDesk 專屬固定且唯一之 Application User Model ID (AUMID)。
    /// 確保 Windows 10/11 系統通知中心 (Toast Notification) 頂部 Attribution Area 顯示正確的應用程式 Logo 與標題。
    /// </summary>
    public const string AppUserModelId = "RemoteDesk";

    private static Icon? _cachedIcon;
    private static Bitmap? _cachedLogoMaster;
    private static readonly Dictionary<int, Bitmap> _sizeCache = new();
    private static readonly object _lock = new();

    /// <summary>
    /// 取得應用程式多解析度圖標 (適用於 Form.Icon、NotifyIcon 等)。
    /// </summary>
    public static Icon AppIcon
    {
        get
        {
            if (_cachedIcon != null) return _cachedIcon;
            lock (_lock)
            {
                if (_cachedIcon != null) return _cachedIcon;

                // 1. 嘗試從嵌入資源載入
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using var stream = asm.GetManifestResourceStream("RemoteDesk.app.ico");
                    if (stream != null)
                    {
                        _cachedIcon = new Icon(stream);
                        return _cachedIcon;
                    }
                }
                catch { }

                // 2. 嘗試從執行檔目錄或源碼目錄載入
                string[] candidatePaths =
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "app.ico"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "app.ico"),
                    Path.Combine(AppConfig.DataDirectory, "app.ico"),
                    @"C:\Users\User\Desktop\研究專案\RemoteDesk\src\RemoteDesk\app.ico",
                    @"C:\Users\User\Desktop\研究專案\RemoteDesk\publish\app.ico"
                };

                foreach (var path in candidatePaths)
                {
                    if (File.Exists(path))
                    {
                        try
                        {
                            _cachedIcon = new Icon(path);
                            return _cachedIcon;
                        }
                        catch { }
                    }
                }

                // 3. 若皆不可用，從 Master Logo Bitmap 動態建立 Icon
                try
                {
                    using var bmp = GetLogoBitmap(32, 32);
                    IntPtr hIcon = bmp.GetHicon();
                    _cachedIcon = Icon.FromHandle(hIcon);
                    return _cachedIcon;
                }
                catch
                {
                    _cachedIcon = SystemIcons.Application;
                    return _cachedIcon;
                }
            }
        }
    }

    /// <summary>
    /// 取得主 Logo 點陣圖 (已去背透明)。
    /// </summary>
    public static Bitmap MasterLogo
    {
        get
        {
            if (_cachedLogoMaster != null) return _cachedLogoMaster;
            lock (_lock)
            {
                if (_cachedLogoMaster != null) return _cachedLogoMaster;

                // 1. 從嵌入資源載入
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using var stream = asm.GetManifestResourceStream("RemoteDesk.Resources.logo.png");
                    if (stream != null)
                    {
                        _cachedLogoMaster = new Bitmap(stream);
                        return _cachedLogoMaster;
                    }
                }
                catch { }

                // 2. 從候選檔案路徑載入
                string[] candidatePaths =
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "logo.png"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo_transparent.png"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "Resources", "logo.png"),
                    @"C:\Users\User\Desktop\研究專案\remote\src\RemoteDesk\Resources\logo.png",
                    @"C:\Users\User\Desktop\研究專案\remote\logo_transparent.png"
                };

                foreach (var path in candidatePaths)
                {
                    if (File.Exists(path))
                    {
                        try
                        {
                            using var fs = File.OpenRead(path);
                            _cachedLogoMaster = new Bitmap(fs);
                            return _cachedLogoMaster;
                        }
                        catch { }
                    }
                }

                // 3. Fallback: 建立一預設點陣圖
                _cachedLogoMaster = new Bitmap(64, 64);
                return _cachedLogoMaster;
            }
        }
    }

    /// <summary>
    /// 取得指定尺寸的高品質去背 Logo 點陣圖 (支援快取)。
    /// </summary>
    public static Bitmap GetLogoBitmap(int width, int height)
    {
        int key = (width << 16) | (height & 0xFFFF);
        lock (_lock)
        {
            if (_sizeCache.TryGetValue(key, out var cached))
                return cached;

            var result = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.Clear(Color.Transparent);

                var master = MasterLogo;
                g.DrawImage(master, new Rectangle(0, 0, width, height));
            }

            _sizeCache[key] = result;
            return result;
        }
    }

    /// <summary>
    /// 一鍵套用視窗圖標 (確保工作列與標題列顯示現代去背 Logo)。
    /// </summary>
    public static void ApplyToForm(Form form)
    {
        try
        {
            form.Icon = AppIcon;
            form.ShowIcon = true;
        }
        catch { }
    }

    /// <summary>
    /// 依指定尺寸取得 RemoteDesk 向量 Logo 之 Icon 物件。
    /// </summary>
    public static Icon GetLogoIcon(int size = 48)
    {
        using var bmp = GetLogoBitmap(size, size);
        IntPtr hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    /// <summary>
    /// 顯示系統匣氣球/浮動訊息提示 (僅純文字，視覺區域不附帶任何內嵌圖示，Logo 嚴格保留於 Attribution Area)。
    /// </summary>
    public static void ShowCustomBalloonTip(this NotifyIcon trayIcon, int timeout, string title, string text)
    {
        try
        {
            var type = typeof(NotifyIcon);
            var windowField = type.GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)
                           ?? type.GetField("window", BindingFlags.NonPublic | BindingFlags.Instance);
            var nativeWindow = windowField?.GetValue(trayIcon) as NativeWindow;
            IntPtr hWnd = nativeWindow?.Handle ?? IntPtr.Zero;

            var idField = type.GetField("_id", BindingFlags.NonPublic | BindingFlags.Instance)
                       ?? type.GetField("id", BindingFlags.NonPublic | BindingFlags.Instance);
            int id = idField != null ? Convert.ToInt32(idField.GetValue(trayIcon)) : 0;

            if (hWnd != IntPtr.Zero)
            {
                var data = new Win32.NOTIFYICONDATA
                {
                    cbSize = Marshal.SizeOf<Win32.NOTIFYICONDATA>(),
                    hWnd = hWnd,
                    uID = id,
                    uFlags = Win32.NIF_INFO,
                    hBalloonIcon = IntPtr.Zero,
                    szInfo = text ?? string.Empty,
                    szInfoTitle = title ?? string.Empty,
                    uTimeoutOrVersion = timeout,
                    dwInfoFlags = Win32.NIIF_NONE
                };

                if (Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref data))
                {
                    return;
                }
            }
        }
        catch { }

        // Fallback: ToolTipIcon.None 確保視覺內容區域絕不出現系統預設圖標
        try
        {
            trayIcon.ShowBalloonTip(timeout, title ?? string.Empty, text ?? string.Empty, ToolTipIcon.None);
        }
        catch { }
    }

    /// <summary>
    /// 發送原生 Windows 10/11 Toast Notification。
    /// 嚴格遵循規範：依據 AppUserModelId ("RemoteDesk") 自動由系統關聯開始功能表捷徑與應用程式圖示，
    /// 絕不使用 SetAppLogoOverride()，視覺內容區 (Visual Area) 不帶任何圖片，Logo 僅出現在頂部 Attribution Area。
    /// </summary>
    public static void ShowToastNotification(string title, string text)
    {
        Task.Run(() =>
        {
            try
            {
                string safeTitle = (title ?? "RemoteDesk").Replace("'", "''");
                string safeText = (text ?? string.Empty).Replace("'", "''");
                string script = $@"
$null = [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
$null = [Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime]
$null = [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]

$xml = New-Object Windows.Data.Xml.Dom.XmlDocument
$xml.LoadXml('<toast><visual><binding template=""ToastGeneric""><text>{safeTitle}</text><text>{safeText}</text></binding></visual></toast>')
$toast = New-Object Windows.UI.Notifications.ToastNotification $xml
$notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{AppUserModelId}')
$notifier.Show($toast)
";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{script.Replace("\r\n", " ").Replace("\"", "\\\"")}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p?.WaitForExit(3000);
            }
            catch { }
        });
    }

    /// <summary>
    /// 統一發送系統匣最小化提示 (優先使用 NotifyIcon 系統匣氣球提示，若未提供系統匣物件則以 WinRT Toast 提示)。
    /// 避免同時發送 WinRT Toast 與 Shell Notify 導致 Windows 彈出兩次重複通知。
    /// </summary>
    public static void ShowNotification(this NotifyIcon? trayIcon, string title, string text)
    {
        if (trayIcon != null)
        {
            trayIcon.ShowCustomBalloonTip(2000, title, text);
        }
        else
        {
            ShowToastNotification(title, text);
        }
    }

    /// <summary>
    /// 確保本機開始功能表捷徑與登錄檔具備完整的 AppUserModelID 識別與實體獨立圖標 (.ico) 關聯。
    /// 支援 Windows 10/11 Action Center 在 Attribution Area 正確解析並渲染自訂 Logo。
    /// </summary>
    public static void EnsureShortcutAndIdentity()
    {
        try
        {
            // 1. 綁定目前處理序 AUMID
            Win32.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

            string exePath = Environment.ProcessPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RemoteDesk.exe");
            string baseDir = Path.GetDirectoryName(exePath) ?? AppDomain.CurrentDomain.BaseDirectory;
            string icoPath = Path.Combine(baseDir, "app.ico");

            // 若實體 app.ico 檔案不存在於執行檔目錄，從內建資源導出確保實體存在
            if (!File.Exists(icoPath))
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using var stream = asm.GetManifestResourceStream("RemoteDesk.app.ico");
                    if (stream != null)
                    {
                        using var fs = File.Create(icoPath);
                        stream.CopyTo(fs);
                    }
                }
                catch
                {
                    // 若執行檔目錄無法寫入 (例如 Program Files 或 dotnet 測試主機目錄)，導出至 DataDirectory
                    icoPath = Path.Combine(AppConfig.DataDirectory, "app.ico");
                    try
                    {
                        Directory.CreateDirectory(AppConfig.DataDirectory);
                        if (!File.Exists(icoPath))
                        {
                            var asm = Assembly.GetExecutingAssembly();
                            using var stream = asm.GetManifestResourceStream("RemoteDesk.app.ico");
                            if (stream != null)
                            {
                                using var fs = File.Create(icoPath);
                                stream.CopyTo(fs);
                            }
                        }
                    }
                    catch { }
                }
            }

            if (!File.Exists(icoPath))
            {
                string fallbackPath = Path.Combine(AppConfig.DataDirectory, "app.ico");
                if (File.Exists(fallbackPath)) icoPath = fallbackPath;
                else
                {
                    string repoPath = @"C:\Users\User\Desktop\研究專案\RemoteDesk\publish\app.ico";
                    if (File.Exists(repoPath)) icoPath = repoPath;
                }
            }

            string iconLocation = File.Exists(icoPath) ? icoPath : exePath;

            // 2. 搜尋開始功能表捷徑路徑 (包含本機與所有使用者開始功能表)
            string userPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);

            string[] candidateShortcuts =
            {
                Path.Combine(commonPrograms, "RemoteDesk", "RemoteDesk.lnk"),
                Path.Combine(commonPrograms, "RemoteDesk.lnk"),
                Path.Combine(userPrograms, "RemoteDesk", "RemoteDesk.lnk"),
                Path.Combine(userPrograms, "RemoteDesk.lnk")
            };

            bool shortcutUpdated = false;
            foreach (var path in candidateShortcuts)
            {
                if (File.Exists(path))
                {
                    try
                    {
                        UpdateShortcut(path, exePath, baseDir, iconLocation);
                        shortcutUpdated = true;
                    }
                    catch { }
                }
            }

            // 若完全沒有任何捷徑，建立預設的使用者開始功能表捷徑
            if (!shortcutUpdated)
            {
                string defaultShortcut = Path.Combine(userPrograms, "RemoteDesk.lnk");
                try
                {
                    UpdateShortcut(defaultShortcut, exePath, baseDir, iconLocation);
                    shortcutUpdated = true;
                }
                catch { }
            }

            // 3. 註冊使用者端與系統端 AppUserModelId 登錄項目 (指向實體 app.ico 檔案)
            try
            {
                using var baseKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
                if (baseKey != null)
                {
                    baseKey.SetValue("DisplayName", "RemoteDesk");
                    baseKey.SetValue("IconUri", iconLocation);
                    baseKey.SetValue("ShowInSettings", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    baseKey.SetValue("IconBackgroundColor", "0");
                }
            }
            catch { }

            try
            {
                using var hklmKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
                if (hklmKey != null)
                {
                    hklmKey.SetValue("DisplayName", "RemoteDesk");
                    hklmKey.SetValue("IconUri", iconLocation);
                    hklmKey.SetValue("ShowInSettings", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    hklmKey.SetValue("IconBackgroundColor", "0");
                }
            }
            catch { }

            if (shortcutUpdated)
            {
                Win32.SHChangeNotify(Win32.SHCNE_ASSOCCHANGED, Win32.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch { }
    }

    private static void UpdateShortcut(string shortcutPath, string exePath, string workingDir, string iconLocation)
    {
        var link = (Win32.IPersistFile)new Win32.CShellLink();
        var shellLink = (Win32.IShellLinkW)link;

        if (File.Exists(shortcutPath))
        {
            try { link.Load(shortcutPath, 0); } catch { }
        }

        shellLink.SetPath(exePath);
        shellLink.SetWorkingDirectory(workingDir);
        shellLink.SetDescription("RemoteDesk");
        shellLink.SetIconLocation(iconLocation, 0);

        var propStore = (Win32.IPropertyStore)link;
        var key = Win32.PROPERTYKEY.AppUserModel_ID;
        using (var pv = Win32.PROPVARIANT.FromString(AppUserModelId))
        {
            propStore.SetValue(ref key, pv);
            propStore.Commit();
        }

        link.Save(shortcutPath, true);
    }
}
