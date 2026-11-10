using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using Microsoft.Win32;
using RemoteDesk.Native;

namespace RemoteDesk.Host;

/// <summary>讓工作執行緒跟隨目前的輸入桌面（Default / Winlogon），以便在 UAC、鎖定畫面時（服務模式）仍可擷取與操作。</summary>
internal static class DesktopHelper
{
    [ThreadStatic] private static string? _currentName;

    /// <summary>若輸入桌面已改變，將目前執行緒切換過去。回傳是否發生切換。</summary>
    public static bool SyncThreadDesktop()
    {
        var hDesk = Win32.OpenInputDesktop(0, false, Win32.GENERIC_ALL);
        if (hDesk == IntPtr.Zero) return false; // 一般權限無法開啟安全桌面
        var name = Win32.GetDesktopName(hDesk);
        _currentName ??= Win32.GetDesktopName(Win32.GetThreadDesktop(Win32.GetCurrentThreadId()));
        if (string.Equals(name, _currentName, StringComparison.OrdinalIgnoreCase))
        {
            Win32.CloseDesktop(hDesk);
            return false;
        }
        if (Win32.SetThreadDesktop(hDesk))
        {
            Common.Log.Info($"執行緒切換到桌面 {name}");
            _currentName = name;
            return true; // 刻意不關閉 handle，執行緒仍在使用
        }
        Win32.CloseDesktop(hDesk);
        return false;
    }

    /// <summary>
    /// 依據需求 4 取得被控端電腦的「單純桌布 (Pure Wallpaper)」，
    /// 直接讀取 Windows 桌布檔案或底色並縮放為 16:9 縮圖 JPEG 位元組陣列，供檢視端設備卡片代表圖片使用。
    /// </summary>
    public static byte[]? GetDesktopWallpaperBytes(int maxW = 432, int maxH = 244)
    {
        try
        {
            string? wallpaperPath = null;

            // 1. 優先檢查 Windows 快取桌布 TranscodedWallpaper
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                string transcoded = Path.Combine(appData, @"Microsoft\Windows\Themes\TranscodedWallpaper");
                if (File.Exists(transcoded) && new FileInfo(transcoded).Length > 0)
                {
                    wallpaperPath = transcoded;
                }
            }

            // 2. 檢查登錄檔中的桌布路徑
            if (string.IsNullOrEmpty(wallpaperPath))
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                    string? regWp = key?.GetValue("Wallpaper") as string;
                    if (!string.IsNullOrWhiteSpace(regWp) && File.Exists(regWp) && new FileInfo(regWp).Length > 0)
                    {
                        wallpaperPath = regWp;
                    }
                }
                catch { }
            }

            // 3. 呼叫 Win32 API 取得桌布路徑
            if (string.IsNullOrEmpty(wallpaperPath))
            {
                try
                {
                    var sb = new StringBuilder(512);
                    if (Win32.SystemParametersInfo(Win32.SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0) != 0)
                    {
                        string apiWp = sb.ToString();
                        if (!string.IsNullOrWhiteSpace(apiWp) && File.Exists(apiWp) && new FileInfo(apiWp).Length > 0)
                        {
                            wallpaperPath = apiWp;
                        }
                    }
                }
                catch { }
            }

            // 4. 檢查 Windows 內建預設桌布候選
            if (string.IsNullOrEmpty(wallpaperPath))
            {
                string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string[] defaults =
                {
                    Path.Combine(winDir, @"Web\Wallpaper\Windows\img0.jpg"),
                    Path.Combine(winDir, @"Web\4K\Wallpaper\Windows\img0_1920x1200.jpg"),
                    Path.Combine(winDir, @"Web\4K\Wallpaper\Windows\img0_3840x2160.jpg"),
                    Path.Combine(winDir, @"Web\Wallpaper\Spotlight\img0.jpg")
                };
                foreach (var def in defaults)
                {
                    if (File.Exists(def) && new FileInfo(def).Length > 0)
                    {
                        wallpaperPath = def;
                        break;
                    }
                }
            }

            // 載入桌布影像並進行 16:9 裁剪縮放
            if (!string.IsNullOrEmpty(wallpaperPath))
            {
                var fileBytes = File.ReadAllBytes(wallpaperPath);
                using var msInput = new MemoryStream(fileBytes);
                using var srcImg = Image.FromStream(msInput);

                using var bmp = new Bitmap(maxW, maxH, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;

                    float srcAspect = (float)srcImg.Width / Math.Max(1, srcImg.Height);
                    float dstAspect = (float)maxW / maxH;
                    Rectangle srcRect;
                    if (srcAspect > dstAspect)
                    {
                        int cropW = (int)(srcImg.Height * dstAspect);
                        srcRect = new Rectangle((srcImg.Width - cropW) / 2, 0, cropW, srcImg.Height);
                    }
                    else
                    {
                        int cropH = (int)(srcImg.Width / dstAspect);
                        srcRect = new Rectangle(0, (srcImg.Height - cropH) / 2, srcImg.Width, cropH);
                    }

                    g.DrawImage(srcImg, new Rectangle(0, 0, maxW, maxH), srcRect, GraphicsUnit.Pixel);
                }

                using var msOutput = new MemoryStream();
                var jpegEncoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(e => e.FormatID == ImageFormat.Jpeg.Guid);
                if (jpegEncoder != null)
                {
                    using var encoderParams = new EncoderParameters(1);
                    encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                    bmp.Save(msOutput, jpegEncoder, encoderParams);
                    return msOutput.ToArray();
                }
                bmp.Save(msOutput, ImageFormat.Jpeg);
                return msOutput.ToArray();
            }

            // 5. 若無桌布圖檔（例如純色背景），讀取純色底色
            Color fallbackColor = Color.FromArgb(0, 99, 177); // 預設 Windows 經典藍
            try
            {
                using var colorKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
                string? rgbStr = colorKey?.GetValue("Background") as string;
                if (!string.IsNullOrWhiteSpace(rgbStr))
                {
                    var parts = rgbStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 3 &&
                        int.TryParse(parts[0], out int r) &&
                        int.TryParse(parts[1], out int g) &&
                        int.TryParse(parts[2], out int b))
                    {
                        fallbackColor = Color.FromArgb(r, g, b);
                    }
                }
            }
            catch { }

            using (var solidBmp = new Bitmap(maxW, maxH, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(solidBmp))
                {
                    using var brush = new SolidBrush(fallbackColor);
                    g.FillRectangle(brush, 0, 0, maxW, maxH);
                }
                using var msSolid = new MemoryStream();
                solidBmp.Save(msSolid, ImageFormat.Jpeg);
                return msSolid.ToArray();
            }
        }
        catch (Exception ex)
        {
            Common.Log.Warn("取得本機桌布失敗: " + ex.Message);
            return null;
        }
    }
}

/// <summary>專用的輸入注入執行緒：保持事件順序，且可獨立切換到輸入桌面。</summary>
internal sealed class InputThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastDesktopCheck;

    public InputThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "RemoteDesk-Input", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Post(Action a)
    {
        try { _queue.Add(a); } catch (InvalidOperationException) { }
    }

    private void Run()
    {
        DesktopHelper.SyncThreadDesktop();
        foreach (var a in _queue.GetConsumingEnumerable())
        {
            var now = _sw.ElapsedMilliseconds;
            if (now - _lastDesktopCheck > 500)
            {
                _lastDesktopCheck = now;
                DesktopHelper.SyncThreadDesktop();
            }
            try { a(); }
            catch (Exception ex) { Common.Log.Warn("注入輸入失敗: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(1000);
    }
}
