using System.Text.RegularExpressions;
using RemoteDesk.Common;
using RemoteDesk.Native;

namespace RemoteDesk.Host;

internal static class MonitorEnumerator
{
    /// <summary>列舉目前所有螢幕（實體像素座標，程序需為 Per-Monitor DPI Aware）。依位置由左至右排序。</summary>
    public static List<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref Win32.RECT rc, IntPtr data) =>
        {
            var mi = new Win32.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFOEX>() };
            if (!Win32.GetMonitorInfo(hMon, ref mi)) return true;
            int dpi = 96;
            try { if (Win32.GetDpiForMonitor(hMon, 0, out var dx, out _) == 0) dpi = (int)dx; } catch { }
            var friendly = GetFriendlyName(mi.szDevice);
            list.Add(new MonitorInfo(mi.szDevice, friendly, 0,
                mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Width, mi.rcMonitor.Height,
                (mi.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0, dpi));
            return true;
        }, IntPtr.Zero);

        // 依螢幕水平座標由左至右統一依序編號為 1, 2, 3... (避免 Windows 裝置名稱如 \\.\DISPLAY5 導致編號跳號)
        var ordered = list.OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i] = ordered[i] with { Number = i + 1 };
        }
        return ordered;
    }

    private static string GetFriendlyName(string deviceName)
    {
        try
        {
            var dd = new Win32.DISPLAY_DEVICE { cb = System.Runtime.InteropServices.Marshal.SizeOf<Win32.DISPLAY_DEVICE>() };
            if (Win32.EnumDisplayDevices(deviceName, 0, ref dd, 0) && !string.IsNullOrWhiteSpace(dd.DeviceString))
                return dd.DeviceString.Trim();
        }
        catch { }
        return deviceName;
    }

    public static System.Drawing.Rectangle VirtualScreen => new(
        Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN), Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN),
        Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN), Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN));

    public static bool SameLayout(IReadOnlyList<MonitorInfo> a, IReadOnlyList<MonitorInfo> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First == p.Second);
}
