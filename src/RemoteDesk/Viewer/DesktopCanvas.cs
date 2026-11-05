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
        int rw = _session.RemoteWidth;
        int rh = _session.RemoteHeight;
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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_destRect.IsEmpty || _session.RemoteWidth <= 0)
        {
            g.Clear(Color.FromArgb(24, 24, 24));
            using var font = new Font("Microsoft JhengHei UI", 12f, FontStyle.Regular);
            using var brush = new SolidBrush(Color.FromArgb(160, 160, 160));
            var text = "等待遠端畫面傳輸中...";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, brush, (ClientSize.Width - size.Width) / 2, (ClientSize.Height - size.Height) / 2);
            return;
        }

        // 繪製黑底留白邊界
        if (_destRect.X > 0 || _destRect.Y > 0 || _destRect.Right < ClientSize.Width || _destRect.Bottom < ClientSize.Height)
        {
            g.Clear(Color.FromArgb(20, 20, 20));
        }

        // 繪製遠端影像
        _session.Decoder.DrawTo(g, _destRect);
    }

    /// <summary>
    /// 將本機滑鼠座標轉換為遠端螢幕真實像素座標。
    /// </summary>
    private bool TryMapToRemote(int clientX, int clientY, out int remoteX, out int remoteY)
    {
        remoteX = 0; remoteY = 0;
        if (_destRect.IsEmpty || _destRect.Width <= 0 || _destRect.Height <= 0) return false;
        if (!_destRect.Contains(clientX, clientY)) return false;

        float ratioX = (float)_session.RemoteWidth / _destRect.Width;
        float ratioY = (float)_session.RemoteHeight / _destRect.Height;

        remoteX = Math.Clamp((int)((clientX - _destRect.X) * ratioX), 0, _session.RemoteWidth - 1);
        remoteY = Math.Clamp((int)((clientY - _destRect.Y) * ratioY), 0, _session.RemoteHeight - 1);
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (TryMapToRemote(e.X, e.Y, out int rx, out int ry))
        {
            if (rx != _lastMouseX || ry != _lastMouseY)
            {
                _lastMouseX = rx;
                _lastMouseY = ry;
                _session.SendMouseMove(rx, ry);
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (TryMapToRemote(e.X, e.Y, out int rx, out int ry))
        {
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
