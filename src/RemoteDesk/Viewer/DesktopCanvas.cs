using System.Drawing;
using System.Drawing.Drawing2D;
using RemoteDesk.Common;

namespace RemoteDesk.Viewer;

/// <summary>
/// 高效能遠端桌面畫布：
/// 支援硬體雙緩衝、座標縮放映射、零延遲本機游標、滑鼠平滑跟隨。
/// </summary>
internal sealed class DesktopCanvas : Control
{
    private readonly ViewerSession _session;
    private bool _fitToWindow = true;
    private Rectangle _destRect;
    private int _lastMouseX = -1, _lastMouseY = -1;
    private long _lastRenderedFrameVersion;

    public bool FitToWindow
    {
        get => _fitToWindow;
        set
        {
            _fitToWindow = value;
            RecalculateLayout();
            Invalidate();
        }
    }

    public DesktopCanvas(ViewerSession session)
    {
        _session = session;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Opaque |
            ControlStyles.Selectable, true);

        UpdateStyles();
        BackColor = Color.FromArgb(24, 24, 24);
        Cursor = Cursors.Default;
    }

    public void RecalculateLayout()
    {
        int rw = _session.OriginalWidth > 0 ? _session.OriginalWidth : _session.StreamWidth;
        int rh = _session.OriginalHeight > 0 ? _session.OriginalHeight : _session.StreamHeight;
        if (rw <= 0 || rh <= 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            _destRect = Rectangle.Empty;
            return;
        }

        if (!_fitToWindow)
        {
            // 1:1 置中
            int x = Math.Max(0, (ClientSize.Width - rw) / 2);
            int y = Math.Max(0, (ClientSize.Height - rh) / 2);
            _destRect = new Rectangle(x, y, rw, rh);
            return;
        }

        // 保持比例等比縮放
        float scaleX = (float)ClientSize.Width / rw;
        float scaleY = (float)ClientSize.Height / rh;
        float scale = Math.Min(scaleX, scaleY);

        int dw = (int)(rw * scale);
        int dh = (int)(rh * scale);
        int dx = (ClientSize.Width - dw) / 2;
        int dy = (ClientSize.Height - dh) / 2;
        _destRect = new Rectangle(dx, dy, dw, dh);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RecalculateLayout();
        Invalidate();
    }

    /// <summary>
    /// 依據遠端變動矩形 (Dirty Rectangle) 局部重繪畫布，避免全螢幕重畫造成的 GDI+ UI 執行緒卡頓。
    /// </summary>
    public void InvalidateRemoteRect(Rectangle remoteRect)
    {
        if (remoteRect.IsEmpty) return;
        int sw = _session.StreamWidth;
        int sh = _session.StreamHeight;
        if (_destRect.IsEmpty || sw <= 0 || sh <= 0)
        {
            Invalidate();
            return;
        }

        if (remoteRect.Width >= sw && remoteRect.Height >= sh)
        {
            Invalidate(_destRect);
            return;
        }

        // 映射串流解析度座標 (StreamWidth × StreamHeight) 至本機 Client 顯示矩形
        float scaleX = (float)_destRect.Width / sw;
        float scaleY = (float)_destRect.Height / sh;

        int cx = _destRect.X + (int)Math.Floor(remoteRect.X * scaleX);
        int cy = _destRect.Y + (int)Math.Floor(remoteRect.Y * scaleY);
        int cw = (int)Math.Ceiling(remoteRect.Width * scaleX) + 2; // +2 避免浮點數捨入造成的邊緣微小縫隙
        int ch = (int)Math.Ceiling(remoteRect.Height * scaleY) + 2;

        var clientDirty = Rectangle.Intersect(new Rectangle(cx, cy, cw, ch), _destRect);
        if (!clientDirty.IsEmpty)
        {
            Invalidate(clientDirty);
        }
        else
        {
            Invalidate(_destRect);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_destRect.IsEmpty || _session.StreamWidth <= 0)
        {
            g.Clear(Color.FromArgb(24, 24, 24));
            using var font = new Font("Microsoft JhengHei UI", 12f, FontStyle.Regular);
            using var brush = new SolidBrush(Color.FromArgb(160, 160, 160));
            var text = "等待遠端畫面傳輸中...";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, (ClientSize.Width - size.Width) / 2, (ClientSize.Height - size.Height) / 2);
            return;
        }

        // 只有當重繪區域跨出 _destRect (有黑色邊框留白) 時才清除黑底邊界
        if (!_destRect.Contains(e.ClipRectangle))
        {
            if (_destRect.X > 0 || _destRect.Y > 0 || _destRect.Right < ClientSize.Width || _destRect.Bottom < ClientSize.Height)
            {
                g.Clear(Color.FromArgb(20, 20, 20));
            }
        }

        // 繪製遠端影像 (GDI+ 自動根據 e.ClipRectangle 裁剪，僅 Blit 局部變更區域，並量測真實 UI 渲染耗時)
        RenderFrameForPaint(g);
    }

    /// <summary>
    /// 實際繪製影像並依最新解碼影格版本去重回報渲染事件，
    /// 避免無新影格的 WM_PAINT (移動視窗/滑過/遮蔽重繪) 被誤算為 RenderFps。
    /// </summary>
    internal bool RenderFrameForPaint(Graphics g)
    {
        if (_destRect.IsEmpty || _session.StreamWidth <= 0) return false;

        long decodedVer = _session.CurrentDecodedFrameVersion;
        var renderSw = System.Diagnostics.Stopwatch.StartNew();
        _session.Decoder.DrawTo(g, _destRect);
        renderSw.Stop();

        if (decodedVer > _lastRenderedFrameVersion)
        {
            _lastRenderedFrameVersion = decodedVer;
            _session.ReportFrameRendered(renderSw.Elapsed.TotalMilliseconds, decodedVer);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 將本機滑鼠座標轉換為被控端原始原生解析度座標（Strict decoupling from StreamWidth/Height）。
    /// </summary>
    private bool TryMapToRemote(int clientX, int clientY, out int remoteX, out int remoteY)
    {
        int origW = _session.OriginalWidth > 0 ? _session.OriginalWidth : _session.RemoteWidth;
        int origH = _session.OriginalHeight > 0 ? _session.OriginalHeight : _session.RemoteHeight;
        return ResolutionProfileManager.TryMapViewerPointToHostOriginal(
            clientX, clientY, _destRect, origW, origH, out remoteX, out remoteY);
    }

    private long _lastMouseMoveTime;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (TryMapToRemote(e.X, e.Y, out int rx, out int ry))
        {
            if (rx != _lastMouseX || ry != _lastMouseY)
            {
                long now = Environment.TickCount64;
                if (now - _lastMouseMoveTime >= 12) // 限速最高 ~80 Hz，防電競滑鼠高回報率 (500~1000Hz) 阻塞 UDP 頻道
                {
                    _lastMouseMoveTime = now;
                    _lastMouseX = rx;
                    _lastMouseY = ry;
                    _session.SendMouseMove(rx, ry);
                }
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (TryMapToRemote(e.X, e.Y, out int rx, out int ry))
        {
            _lastMouseX = rx;
            _lastMouseY = ry;
            _session.SendMouseMove(rx, ry);
            var btn = MapButton(e.Button);
            if (btn.HasValue) _session.SendMouseButton(btn.Value, true, rx, ry);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (TryMapToRemote(e.X, e.Y, out int rx, out int ry))
        {
            var btn = MapButton(e.Button);
            if (btn.HasValue) _session.SendMouseButton(btn.Value, false, rx, ry);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (TryMapToRemote(e.X, e.Y, out int rx, out int ry))
        {
            _session.SendMouseWheel(e.Delta, horizontal: false, rx, ry);
        }
    }

    private static MouseButtonId? MapButton(MouseButtons btn) => btn switch
    {
        MouseButtons.Left => MouseButtonId.Left,
        MouseButtons.Right => MouseButtonId.Right,
        MouseButtons.Middle => MouseButtonId.Middle,
        MouseButtons.XButton1 => MouseButtonId.X1,
        MouseButtons.XButton2 => MouseButtonId.X2,
        _ => null
    };
}
