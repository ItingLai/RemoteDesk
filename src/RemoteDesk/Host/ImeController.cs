using RemoteDesk.Common;
using RemoteDesk.Native;

namespace RemoteDesk.Host;

/// <summary>
/// 讀取/設定「目前前景視窗」的輸入法狀態（鍵盤配置、開啟狀態、中/英轉換模式）。
/// 透過 WM_IME_CONTROL 對目標視窗的預設 IME 視窗下指令，可跨程序運作，相容 IMM32 與 TSF 輸入法（如微軟注音、新注音、微軟拼音）。
/// </summary>
internal sealed class ImeController
{
    private const uint Timeout = 150;

    public IntPtr LastForeground { get; private set; }

    private static IntPtr FocusWindow(out uint threadId)
    {
        var fg = Win32.GetForegroundWindow();
        threadId = fg == IntPtr.Zero ? 0 : Win32.GetWindowThreadProcessId(fg, out _);
        if (threadId != 0)
        {
            var gti = new Win32.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.GUITHREADINFO>() };
            if (Win32.GetGUIThreadInfo(threadId, ref gti) && gti.hwndFocus != IntPtr.Zero) return gti.hwndFocus;
        }
        return fg;
    }

    public ImeState? Read()
    {
        var hwnd = FocusWindow(out var tid);
        LastForeground = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        var hkl = Win32.GetKeyboardLayout(tid);
        var imeWnd = Win32.ImmGetDefaultIMEWnd(hwnd);
        bool open = false;
        int conv = 0;
        if (imeWnd != IntPtr.Zero)
        {
            if (Win32.SendMessageTimeout(imeWnd, Win32.WM_IME_CONTROL, Win32.IMC_GETOPENSTATUS, IntPtr.Zero,
                    Win32.SMTO_ABORTIFHUNG, Timeout, out var r1) != IntPtr.Zero)
                open = r1 != IntPtr.Zero;
            if (Win32.SendMessageTimeout(imeWnd, Win32.WM_IME_CONTROL, Win32.IMC_GETCONVERSIONMODE, IntPtr.Zero,
                    Win32.SMTO_ABORTIFHUNG, Timeout, out var r2) != IntPtr.Zero)
                conv = (int)r2;
        }
        return new ImeState(hkl.ToInt64(), open, conv);
    }

    /// <summary>將目前前景視窗切換為指定的鍵盤配置與中/英狀態。</summary>
    public void Apply(ImeState target)
    {
        var hwnd = FocusWindow(out var tid);
        if (hwnd == IntPtr.Zero) return;
        var cur = Win32.GetKeyboardLayout(tid).ToInt64();

        if ((cur & 0xFFFF) != target.LangId || (cur != target.KeyboardLayout && HasLayout(target.KeyboardLayout)))
        {
            var hkl = FindLayout(target);
            if (hkl != IntPtr.Zero && hkl.ToInt64() != cur)
            {
                var top = Win32.GetForegroundWindow();
                Win32.PostMessage(top, Win32.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
                if (hwnd != top) Win32.PostMessage(hwnd, Win32.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
                // 等待切換生效後再設定中/英
                Thread.Sleep(60);
            }
        }

        var imeWnd = Win32.ImmGetDefaultIMEWnd(hwnd);
        if (imeWnd == IntPtr.Zero) return;
        Win32.SendMessageTimeout(imeWnd, Win32.WM_IME_CONTROL, Win32.IMC_SETOPENSTATUS, target.Open ? 1 : 0,
            Win32.SMTO_ABORTIFHUNG, Timeout, out _);
        if (target.Open)
            Win32.SendMessageTimeout(imeWnd, Win32.WM_IME_CONTROL, Win32.IMC_SETCONVERSIONMODE, target.Conversion,
                Win32.SMTO_ABORTIFHUNG, Timeout, out _);
    }

    /// <summary>本機輸入法模式：讓遠端輸入法維持英數，避免文字被二次組字。</summary>
    public void ForceAlphanumeric()
    {
        var s = Read();
        if (s == null || !s.Value.IsNative) return;
        var hwnd = FocusWindow(out _);
        var imeWnd = Win32.ImmGetDefaultIMEWnd(hwnd);
        if (imeWnd == IntPtr.Zero) return;
        Win32.SendMessageTimeout(imeWnd, Win32.WM_IME_CONTROL, Win32.IMC_SETCONVERSIONMODE,
            s.Value.Conversion & ~(Win32.IME_CMODE_NATIVE | Win32.IME_CMODE_FULLSHAPE), Win32.SMTO_ABORTIFHUNG, Timeout, out _);
    }

    private static IntPtr[] Layouts()
    {
        int n = Win32.GetKeyboardLayoutList(0, null);
        var arr = new IntPtr[Math.Max(n, 0)];
        if (n > 0) Win32.GetKeyboardLayoutList(n, arr);
        return arr;
    }

    private static bool HasLayout(long hkl) => Layouts().Any(h => h.ToInt64() == hkl);

    /// <summary>找出最接近的已安裝鍵盤配置：完全相同 → 同語系 → null。</summary>
    public static IntPtr FindLayout(ImeState s)
    {
        var all = Layouts();
        var exact = all.FirstOrDefault(h => h.ToInt64() == s.KeyboardLayout);
        if (exact != IntPtr.Zero) return exact;
        return all.FirstOrDefault(h => (h.ToInt64() & 0xFFFF) == s.LangId);
    }

    /// <summary>取得遠端插入點（文字游標）的螢幕座標，供本機輸入法候選視窗對齊。</summary>
    public static Win32.RECT? GetCaretScreenRect()
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        var tid = Win32.GetWindowThreadProcessId(fg, out _);
        var gti = new Win32.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.GUITHREADINFO>() };
        if (!Win32.GetGUIThreadInfo(tid, ref gti) || gti.hwndCaret == IntPtr.Zero) return null;
        var p = new Win32.POINT { X = gti.rcCaret.Left, Y = gti.rcCaret.Top };
        Win32.ClientToScreen(gti.hwndCaret, ref p);
        return new Win32.RECT { Left = p.X, Top = p.Y, Right = p.X + Math.Max(1, gti.rcCaret.Width), Bottom = p.Y + Math.Max(1, gti.rcCaret.Height) };
    }
}
