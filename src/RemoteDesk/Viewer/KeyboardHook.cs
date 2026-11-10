using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteDesk.Common;
using RemoteDesk.Native;

namespace RemoteDesk.Viewer;

/// <summary>
/// 全域低階鍵盤攔截器 (WH_KEYBOARD_LL)：
/// 當控制端視窗擁有焦點時，攔截 Windows 鍵、Alt+Tab 等系統組合鍵，
/// 防止本機攔截，將其完整重定向至遠端電腦，達成 100% 原生鍵盤體驗。
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private IntPtr _hHook = IntPtr.Zero;
    private Win32.LowLevelProc? _proc;
    private readonly Func<bool> _isViewerActive;
    private readonly Action<ushort, ushort, KeyFlags> _onKeyEvent;
    private readonly Func<bool>? _isFullscreen;
    private readonly Action? _onExitFullscreen;
    private bool _disposed;

    public KeyboardHook(
        Func<bool> isViewerActive,
        Action<ushort, ushort, KeyFlags> onKeyEvent,
        Func<bool>? isFullscreen = null,
        Action? onExitFullscreen = null)
    {
        _isViewerActive = isViewerActive;
        _onKeyEvent = onKeyEvent;
        _isFullscreen = isFullscreen;
        _onExitFullscreen = onExitFullscreen;
        Install();
    }

    private void Install()
    {
        _proc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var hMod = Win32.GetModuleHandle(curModule?.ModuleName);
        _hHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, hMod, 0);
        if (_hHook == IntPtr.Zero)
        {
            Log.Warn($"無法安裝低階鍵盤 Hook，錯誤代碼: {Marshal.GetLastWin32Error()}");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isViewerActive())
        {
            var kbd = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);

            // 忽略本機由程式注入的事件，避免回饋迴圈
            if (kbd.dwExtraInfo == Protocol.InjectedMarker)
            {
                return Win32.CallNextHookEx(_hHook, nCode, wParam, lParam);
            }

            int msg = (int)wParam;
            bool isUp = msg == Win32.WM_KEYUP || msg == Win32.WM_SYSKEYUP;
            bool isExt = (kbd.flags & Win32.LLKHF_EXTENDED) != 0;

            var flags = KeyFlags.None;
            if (isUp) flags |= KeyFlags.Up;
            if (isExt) flags |= KeyFlags.Extended;

            ushort vk = (ushort)kbd.vkCode;
            ushort scan = (ushort)kbd.scanCode;

            // 全螢幕狀態下按下 ESC 退出全螢幕，不轉發給遠端主機
            if (vk == 0x1B && _isFullscreen?.Invoke() == true)
            {
                if (!isUp)
                {
                    _onExitFullscreen?.Invoke();
                }
                return (IntPtr)1; // 吞噬事件
            }

            // 特殊系統鍵判斷：Windows 鍵、Alt+Tab、Alt+Esc 等
            bool isWinKey = vk == 0x5B || vk == 0x5C;
            bool isAltKey = vk == 0x12 || vk == 0xA4 || vk == 0xA5;
            bool isTabKey = vk == 0x09;
            bool isAltDown = (Win32.GetAsyncKeyState(0x12) & 0x8000) != 0;

            // 轉發給遠端
            _onKeyEvent(vk, scan, flags);

            // 若為 Windows 鍵或 Alt+Tab，攔截本機回應 (不讓本機彈出開始功能表或本地切換視窗)
            if (isWinKey || (isAltDown && isTabKey))
            {
                return (IntPtr)1; // 吞噬事件
            }
        }

        return Win32.CallNextHookEx(_hHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hHook != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_hHook);
            _hHook = IntPtr.Zero;
        }
    }
}
