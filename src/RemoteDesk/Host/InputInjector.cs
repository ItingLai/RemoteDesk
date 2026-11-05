using System.Drawing;
using System.Runtime.InteropServices;
using RemoteDesk.Common;
using RemoteDesk.Native;

namespace RemoteDesk.Host;

/// <summary>以 SendInput 注入滑鼠/鍵盤事件。鍵盤使用掃描碼，讓遠端輸入法、快捷鍵、遊戲都和實體鍵盤一樣運作。</summary>
internal sealed class InputInjector
{
    private static readonly int InputSize = Marshal.SizeOf<Win32.INPUT>();
    private readonly HashSet<(ushort vk, ushort scan, bool ext)> _pressedKeys = new();
    private readonly HashSet<MouseButtonId> _pressedButtons = new();
    private readonly object _gate = new();

    /// <summary>串流原點在虛擬桌面中的位置（目前選擇的螢幕左上角）。</summary>
    public Point Origin { get; set; }

    // 需要以虛擬鍵碼（而非掃描碼）注入的按鍵
    private static bool UseVirtualKey(ushort vk, ushort scan) =>
        scan == 0 || vk == 0x13 /* Pause */ || (vk >= 0xA6 && vk <= 0xB7) /* 瀏覽器/媒體鍵 */;

    public void Key(ushort vk, ushort scan, KeyFlags flags)
    {
        bool up = flags.HasFlag(KeyFlags.Up);
        bool ext = flags.HasFlag(KeyFlags.Extended);
        var ki = new Win32.KEYBDINPUT { dwExtraInfo = Protocol.InjectedMarker };
        if (UseVirtualKey(vk, scan))
        {
            ki.wVk = vk;
            ki.wScan = scan;
            ki.dwFlags = (ext ? Win32.KEYEVENTF_EXTENDEDKEY : 0) | (up ? Win32.KEYEVENTF_KEYUP : 0);
        }
        else
        {
            ki.wVk = 0;
            ki.wScan = scan;
            ki.dwFlags = Win32.KEYEVENTF_SCANCODE | (ext ? Win32.KEYEVENTF_EXTENDEDKEY : 0) | (up ? Win32.KEYEVENTF_KEYUP : 0);
        }
        lock (_gate)
        {
            if (up) _pressedKeys.Remove((vk, scan, ext));
            else _pressedKeys.Add((vk, scan, ext));
        }
        Send(new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.INPUTUNION { ki = ki } });
    }

    /// <summary>以 Unicode 字元輸入文字（本機輸入法模式使用）。</summary>
    public void Text(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var inputs = new List<Win32.INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                // Enter 以實體鍵送出，相容性較好
                inputs.Add(KeyInput(0x0D, 0x1C, false));
                inputs.Add(KeyInput(0x0D, 0x1C, true));
                continue;
            }
            inputs.Add(new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.INPUTUNION { ki = new Win32.KEYBDINPUT { wScan = ch, dwFlags = Win32.KEYEVENTF_UNICODE, dwExtraInfo = Protocol.InjectedMarker } } });
            inputs.Add(new Win32.INPUT { type = Win32.INPUT_KEYBOARD, u = new Win32.INPUTUNION { ki = new Win32.KEYBDINPUT { wScan = ch, dwFlags = Win32.KEYEVENTF_UNICODE | Win32.KEYEVENTF_KEYUP, dwExtraInfo = Protocol.InjectedMarker } } });
        }
        if (inputs.Count > 0) Win32.SendInput((uint)inputs.Count, inputs.ToArray(), InputSize);
    }

    private static Win32.INPUT KeyInput(ushort vk, ushort scan, bool up) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        u = new Win32.INPUTUNION { ki = new Win32.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = up ? Win32.KEYEVENTF_KEYUP : 0, dwExtraInfo = Protocol.InjectedMarker } },
    };

    /// <summary>移動到串流座標 (x, y)。</summary>
    public void MouseMove(int x, int y) => SendMouse(x, y, Win32.MOUSEEVENTF_MOVE, 0);

    public void MouseButton(MouseButtonId button, bool down, int x, int y)
    {
        uint flags; int data = 0;
        switch (button)
        {
            case MouseButtonId.Left: flags = down ? Win32.MOUSEEVENTF_LEFTDOWN : Win32.MOUSEEVENTF_LEFTUP; break;
            case MouseButtonId.Right: flags = down ? Win32.MOUSEEVENTF_RIGHTDOWN : Win32.MOUSEEVENTF_RIGHTUP; break;
            case MouseButtonId.Middle: flags = down ? Win32.MOUSEEVENTF_MIDDLEDOWN : Win32.MOUSEEVENTF_MIDDLEUP; break;
            case MouseButtonId.X1: flags = down ? Win32.MOUSEEVENTF_XDOWN : Win32.MOUSEEVENTF_XUP; data = (int)Win32.XBUTTON1; break;
            case MouseButtonId.X2: flags = down ? Win32.MOUSEEVENTF_XDOWN : Win32.MOUSEEVENTF_XUP; data = (int)Win32.XBUTTON2; break;
            default: return;
        }
        lock (_gate)
        {
            if (down) _pressedButtons.Add(button); else _pressedButtons.Remove(button);
        }
        SendMouse(x, y, flags | Win32.MOUSEEVENTF_MOVE, data);
    }

    public void Wheel(int delta, bool horizontal, int x, int y) =>
        SendMouse(x, y, (horizontal ? Win32.MOUSEEVENTF_HWHEEL : Win32.MOUSEEVENTF_WHEEL) | Win32.MOUSEEVENTF_MOVE, delta);

    private void SendMouse(int x, int y, uint flags, int data)
    {
        var vs = MonitorEnumerator.VirtualScreen;
        int ax = Origin.X + x, ay = Origin.Y + y;
        ax = Math.Clamp(ax, vs.Left, vs.Right - 1);
        ay = Math.Clamp(ay, vs.Top, vs.Bottom - 1);
        // 正規化到 0..65535（整個虛擬桌面），+0.5 像素避免捨入誤差造成偏移一格
        int nx = (int)Math.Round(((ax - vs.Left) + 0.5) * 65536.0 / vs.Width);
        int ny = (int)Math.Round(((ay - vs.Top) + 0.5) * 65536.0 / vs.Height);
        var mi = new Win32.MOUSEINPUT
        {
            dx = Math.Clamp(nx, 0, 65535),
            dy = Math.Clamp(ny, 0, 65535),
            mouseData = data,
            dwFlags = flags | Win32.MOUSEEVENTF_ABSOLUTE | Win32.MOUSEEVENTF_VIRTUALDESK,
            dwExtraInfo = Protocol.InjectedMarker,
        };
        Send(new Win32.INPUT { type = Win32.INPUT_MOUSE, u = new Win32.INPUTUNION { mi = mi } });
    }

    private static void Send(Win32.INPUT input)
    {
        var arr = new[] { input };
        if (Win32.SendInput(1, arr, InputSize) == 0)
        {
            var err = Marshal.GetLastWin32Error();
            if (err != 0) Log.Warn($"SendInput 失敗 (錯誤 {err})，可能目標視窗權限較高（請以系統管理員執行）或位於安全桌面");
        }
    }

    /// <summary>同步 CapsLock / NumLock / ScrollLock 狀態。</summary>
    public void SyncLockKeys(byte state)
    {
        void Toggle(int vk, ushort scan, bool want, bool ext)
        {
            bool on = (Win32.GetKeyState(vk) & 1) != 0;
            if (on == want) return;
            Key((ushort)vk, scan, ext ? KeyFlags.Extended : KeyFlags.None);
            Key((ushort)vk, scan, (ext ? KeyFlags.Extended : KeyFlags.None) | KeyFlags.Up);
        }
        Toggle(0x14, 0x3A, (state & 1) != 0, false); // CapsLock
        Toggle(0x90, 0x45, (state & 2) != 0, true);  // NumLock（擴充）
        Toggle(0x91, 0x46, (state & 4) != 0, false); // ScrollLock
    }

    /// <summary>放開所有仍按著的鍵與滑鼠鍵（斷線或切換視窗時，避免卡鍵）。</summary>
    public void ReleaseAll()
    {
        List<(ushort vk, ushort scan, bool ext)> keys;
        List<MouseButtonId> buttons;
        lock (_gate)
        {
            keys = _pressedKeys.ToList();
            buttons = _pressedButtons.ToList();
        }
        foreach (var k in keys) Key(k.vk, k.scan, (k.ext ? KeyFlags.Extended : KeyFlags.None) | KeyFlags.Up);
        if (Win32.GetCursorPos(out var p))
            foreach (var b in buttons) MouseButton(b, false, p.X - Origin.X, p.Y - Origin.Y);
    }

    public void Special(SpecialAction action)
    {
        switch (action)
        {
            case SpecialAction.LockScreen:
                Win32.LockWorkStation();
                break;
            case SpecialAction.TaskManager:
                Key(0x11, 0x1D, KeyFlags.None); Key(0x10, 0x2A, KeyFlags.None); Key(0x1B, 0x01, KeyFlags.None);
                Key(0x1B, 0x01, KeyFlags.Up); Key(0x10, 0x2A, KeyFlags.Up); Key(0x11, 0x1D, KeyFlags.Up);
                break;
            case SpecialAction.CtrlAltDel:
                if (!SasHelper.TrySendSas())
                {
                    // 非服務模式無法產生 SAS，改開工作管理員
                    Special(SpecialAction.TaskManager);
                }
                break;
        }
    }
}

internal static class SasHelper
{
    [DllImport("sas.dll")]
    private static extern void SendSAS(bool asUser);

    /// <summary>SendSAS 只有以 SYSTEM 執行（服務模式）時有效。</summary>
    public static bool TrySendSas()
    {
        try
        {
            if (!System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem) return false;
            SendSAS(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("SendSAS 失敗: " + ex.Message);
            return false;
        }
    }
}
