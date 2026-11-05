using System.Runtime.InteropServices;
using RemoteDesk.Common;
using RemoteDesk.Native;

namespace RemoteDesk.Host;

/// <summary>追蹤遠端滑鼠游標的形狀，讓控制端直接以本機游標顯示（零延遲、與原生操作一致）。</summary>
internal sealed class CursorTracker
{
    private static readonly int[] StandardIds =
    {
        32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651, 32671, 32672,
    };
    private readonly Dictionary<IntPtr, int> _standard = new();
    private IntPtr _lastHandle = new(-1);
    private bool _lastVisible = true;

    public CursorTracker()
    {
        foreach (var id in StandardIds)
        {
            var h = Win32.LoadCursor(IntPtr.Zero, id);
            if (h != IntPtr.Zero) _standard.TryAdd(h, id);
        }
    }

    public void Reset() => _lastHandle = new IntPtr(-1);

    /// <summary>若游標形狀有變化則回傳 CursorShape 訊息，否則回傳 null。</summary>
    public byte[]? Poll()
    {
        var ci = new Win32.CURSORINFO { cbSize = Marshal.SizeOf<Win32.CURSORINFO>() };
        if (!Win32.GetCursorInfo(ref ci)) return null;
        bool visible = (ci.flags & Win32.CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero;
        if (ci.hCursor == _lastHandle && visible == _lastVisible) return null;
        _lastHandle = ci.hCursor;
        _lastVisible = visible;

        var w = new PacketWriter((byte)HostMsg.CursorShape, 64);
        w.Bool(visible);
        if (!visible) return w.I32(0).I32(0).I32(0).I32(0).I32(0).Bytes(ReadOnlySpan<byte>.Empty).ToArray();
        if (_standard.TryGetValue(ci.hCursor, out var id))
            return w.I32(id).I32(0).I32(0).I32(0).I32(0).Bytes(ReadOnlySpan<byte>.Empty).ToArray();

        var shape = Extract(ci.hCursor);
        if (shape == null) return w.I32(32512).I32(0).I32(0).I32(0).I32(0).Bytes(ReadOnlySpan<byte>.Empty).ToArray();
        var (hx, hy, cw, ch, bgra) = shape.Value;
        return w.I32(0).I32(hx).I32(hy).I32(cw).I32(ch).Bytes(bgra).ToArray();
    }

    /// <summary>將游標分別畫在黑底與白底上，反推出含透明度的 BGRA 圖（支援彩色、單色與反相游標）。</summary>
    private static unsafe (int hx, int hy, int w, int h, byte[] bgra)? Extract(IntPtr hCursor)
    {
        if (!Win32.GetIconInfo(hCursor, out var ii)) return null;
        try
        {
            int w, h;
            if (ii.hbmColor != IntPtr.Zero && Win32.GetObject(ii.hbmColor, Marshal.SizeOf<Win32.BITMAP>(), out var bm) != 0)
            { w = bm.bmWidth; h = bm.bmHeight; }
            else if (ii.hbmMask != IntPtr.Zero && Win32.GetObject(ii.hbmMask, Marshal.SizeOf<Win32.BITMAP>(), out var bm2) != 0)
            { w = bm2.bmWidth; h = bm2.bmHeight / 2; }
            else return null;
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;

            var black = Render(hCursor, w, h, 0x00000000);
            var white = Render(hCursor, w, h, 0x00FFFFFF);
            if (black == null || white == null) return null;
            var result = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                int o = i * 4;
                int diff = Math.Max(white[o] - black[o], Math.Max(white[o + 1] - black[o + 1], white[o + 2] - black[o + 2]));
                int alpha = 255 - diff;
                if (diff < 0) alpha = 255; // 反相像素：以黑色不透明近似
                if (alpha <= 0) continue;
                if (black[o] > white[o])
                {
                    // XOR 反相區：畫成黑色加白邊效果
                    result[o] = 0; result[o + 1] = 0; result[o + 2] = 0; result[o + 3] = 255;
                    continue;
                }
                result[o] = (byte)Math.Min(255, black[o] * 255 / alpha);
                result[o + 1] = (byte)Math.Min(255, black[o + 1] * 255 / alpha);
                result[o + 2] = (byte)Math.Min(255, black[o + 2] * 255 / alpha);
                result[o + 3] = (byte)alpha;
            }
            return (ii.xHotspot, ii.yHotspot, w, h, result);
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) Win32.DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) Win32.DeleteObject(ii.hbmMask);
        }
    }

    private static unsafe byte[]? Render(IntPtr hCursor, int w, int h, uint bg)
    {
        var screen = Win32.GetDC(IntPtr.Zero);
        var dc = Win32.CreateCompatibleDC(screen);
        Win32.ReleaseDC(IntPtr.Zero, screen);
        var bih = new Win32.BITMAPINFOHEADER { biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var dib = Win32.CreateDIBSection(dc, ref bih, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) { Win32.DeleteDC(dc); return null; }
        var old = Win32.SelectObject(dc, dib);
        try
        {
            var span = new Span<uint>((void*)bits, w * h);
            span.Fill(bg);
            Win32.DrawIconEx(dc, 0, 0, hCursor, w, h, 0, IntPtr.Zero, Win32.DI_NORMAL);
            return new Span<byte>((void*)bits, w * h * 4).ToArray();
        }
        finally
        {
            Win32.SelectObject(dc, old);
            Win32.DeleteObject(dib);
            Win32.DeleteDC(dc);
        }
    }
}
