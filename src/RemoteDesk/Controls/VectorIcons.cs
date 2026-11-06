using System.Drawing;
using System.Drawing.Drawing2D;
using RemoteDesk.Common;

namespace RemoteDesk.Controls;

/// <summary>
/// 向量圖標渲染引擎：以純 GDI+ 向量繪製標準 UI 圖標，
/// 100% 杜絕 Windows 字型缺少 Emoji/符號導致「圖標空白」或出現「方塊豆腐字」之問題。
/// </summary>
public static class VectorIcons
{
    /// <summary>
    /// 繪製設定齒輪圖標 (Gear)
    /// </summary>
    public static void DrawGear(Graphics g, Rectangle bounds, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = bounds.X + bounds.Width / 2f;
        float cy = bounds.Y + bounds.Height / 2f;
        float rOuter = Math.Min(bounds.Width, bounds.Height) / 2f - 1f;
        float rInner = rOuter * 0.65f;
        float rHole = rOuter * 0.32f;
        int teeth = 6;
        double step = (2.0 * Math.PI) / teeth;

        var points = new PointF[teeth * 4];
        for (int i = 0; i < teeth; i++)
        {
            double baseA = i * step;
            double a0 = baseA + step * 0.10;
            double a1 = baseA + step * 0.25;
            double a2 = baseA + step * 0.55;
            double a3 = baseA + step * 0.70;

            points[i * 4 + 0] = new PointF(cx + (float)(rInner * Math.Cos(a0)), cy + (float)(rInner * Math.Sin(a0)));
            points[i * 4 + 1] = new PointF(cx + (float)(rOuter * Math.Cos(a1)), cy + (float)(rOuter * Math.Sin(a1)));
            points[i * 4 + 2] = new PointF(cx + (float)(rOuter * Math.Cos(a2)), cy + (float)(rOuter * Math.Sin(a2)));
            points[i * 4 + 3] = new PointF(cx + (float)(rInner * Math.Cos(a3)), cy + (float)(rInner * Math.Sin(a3)));
        }

        using var path = new GraphicsPath(FillMode.Alternate);
        path.AddPolygon(points);
        path.AddEllipse(cx - rHole, cy - rHole, rHole * 2, rHole * 2);

        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>
    /// 建立設定齒輪點陣圖 (含透明背景)
    /// </summary>
    public static Bitmap CreateGearBitmap(int width, int height, Color color)
    {
        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        DrawGear(g, new Rectangle(0, 0, width, height), color);
        return bmp;
    }

    /// <summary>
    /// 建立連線紀錄時鐘點陣圖 (含透明背景)
    /// </summary>
    public static Bitmap CreateClockBitmap(int width, int height, Color color)
    {
        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        DrawClock(g, new Rectangle(0, 0, width, height), color);
        return bmp;
    }

    /// <summary>
    /// 繪製首頁房屋圖標 (Home)
    /// </summary>
    public static void DrawHome(Graphics g, Rectangle bounds, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = bounds.X + bounds.Width / 2f;
        float top = bounds.Y + 2f;
        float bottom = bounds.Bottom - 2f;
        float left = bounds.X + 2f;
        float right = bounds.Right - 2f;

        // 屋頂三角形
        var roof = new PointF[]
        {
            new(cx, top),
            new(right, top + (bottom - top) * 0.45f),
            new(left, top + (bottom - top) * 0.45f)
        };
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, roof);

        // 房屋本體矩形
        float bodyW = (right - left) * 0.72f;
        float bodyH = (bottom - top) * 0.52f;
        float bodyX = cx - bodyW / 2f;
        float bodyY = top + (bottom - top) * 0.45f;
        g.FillRectangle(brush, bodyX, bodyY, bodyW, bodyH);

        // 門 (鏤空)
        using var doorBrush = new SolidBrush(Theme.SurfaceElevated);
        float doorW = bodyW * 0.36f;
        float doorH = bodyH * 0.65f;
        g.FillRectangle(doorBrush, cx - doorW / 2f, bodyY + bodyH - doorH, doorW, doorH);
    }

    /// <summary>
    /// 建立首頁圖標點陣圖
    /// </summary>
    public static Bitmap CreateHomeBitmap(int width, int height, Color color)
    {
        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        DrawHome(g, new Rectangle(0, 0, width, height), color);
        return bmp;
    }

    /// <summary>
    /// 繪製 AnyDesk 風格之螢幕數字圖標 (螢幕框體 + 內部標示 1, 2, 3 或 全)
    /// </summary>
    public static void DrawMonitorWithLabel(Graphics g, Rectangle bounds, string label, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float x = bounds.X + 2f;
        float y = bounds.Y + 2f;
        float w = bounds.Width - 4f;
        float h = bounds.Height - 7f;

        // 螢幕外框
        using (var pen = new Pen(color, 1.5f))
        {
            g.DrawRectangle(pen, x, y, w, h);
        }

        // 底座支架
        float cx = bounds.X + bounds.Width / 2f;
        using (var standPen = new Pen(color, 1.5f))
        {
            g.DrawLine(standPen, cx, y + h, cx, bounds.Bottom - 1f);
            g.DrawLine(standPen, cx - 4f, bounds.Bottom - 1f, cx + 4f, bounds.Bottom - 1f);
        }

        // 螢幕內文字 (如 1, 2, 全)
        using var font = Theme.CreateFont(7.5f, FontStyle.Bold);
        using var brush = new SolidBrush(color);
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(label, font, brush, new RectangleF(x, y - 0.5f, w, h), sf);
    }

    /// <summary>
    /// 建立 AnyDesk 螢幕數字圖標點陣圖
    /// </summary>
    public static Bitmap CreateMonitorNumberBitmap(int width, int height, string label, Color color)
    {
        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        DrawMonitorWithLabel(g, new Rectangle(0, 0, width, height), label, color);
        return bmp;
    }

    /// <summary>
    /// 繪製連線紀錄時鐘圖標 (History Clock)
    /// </summary>
    public static void DrawClock(Graphics g, Rectangle bounds, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = bounds.X + bounds.Width / 2f;
        float cy = bounds.Y + bounds.Height / 2f;
        float r = Math.Min(bounds.Width, bounds.Height) / 2f - 2f;

        // 圓圈外框
        using var pen = new Pen(color, 1.8f);
        g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);

        // 時針 (指向上方偏右) 與 分針 (指向右側)
        using var handPen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(handPen, cx, cy, cx, cy - r * 0.55f);
        g.DrawLine(handPen, cx, cy, cx + r * 0.5f, cy);
    }

    /// <summary>
    /// 繪製安全性鎖頭圖標 (Security Lock)
    /// </summary>
    public static void DrawLock(Graphics g, Rectangle bounds, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = bounds.X + bounds.Width / 2f;
        float cy = bounds.Y + bounds.Height / 2f;
        float w = bounds.Width * 0.7f;
        float h = bounds.Height * 0.5f;

        // 鎖身
        using var bodyBrush = new SolidBrush(color);
        g.FillRectangle(bodyBrush, cx - w / 2f, cy, w, h);

        // 鎖梁 (上方拱形)
        using var shacklePen = new Pen(color, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float shackleR = w * 0.35f;
        g.DrawArc(shacklePen, cx - shackleR, cy - shackleR * 1.5f, shackleR * 2, shackleR * 2, 180, 180);

        // 鎖孔
        using var holeBrush = new SolidBrush(Theme.Surface);
        g.FillEllipse(holeBrush, cx - 1.5f, cy + h * 0.35f, 3f, 3f);
    }

    /// <summary>
    /// 繪製顯示螢幕圖標 (Display Monitor)
    /// </summary>
    public static void DrawMonitor(Graphics g, Rectangle bounds, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float x = bounds.X + 1f;
        float y = bounds.Y + 2f;
        float w = bounds.Width - 2f;
        float h = (bounds.Height - 2f) * 0.68f;

        using var pen = new Pen(color, 1.6f);
        g.DrawRectangle(pen, x, y, w, h);

        // 底座支架
        float cx = bounds.X + bounds.Width / 2f;
        g.DrawLine(pen, cx, y + h, cx, y + h + 4f);
        g.DrawLine(pen, cx - 6f, y + h + 4f, cx + 6f, y + h + 4f);
    }

    /// <summary>
    /// 繪製音訊喇叭圖標 (Audio Speaker)
    /// </summary>
    public static void DrawAudio(Graphics g, Rectangle bounds, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float x = bounds.X + 2f;
        float cy = bounds.Y + bounds.Height / 2f;

        using var brush = new SolidBrush(color);
        using var pen = new Pen(color, 1.6f);

        // 喇叭身
        var pts = new PointF[]
        {
            new(x, cy - 3f),
            new(x + 4f, cy - 3f),
            new(x + 9f, cy - 7f),
            new(x + 9f, cy + 7f),
            new(x + 4f, cy + 3f),
            new(x, cy + 3f)
        };
        g.FillPolygon(brush, pts);

        // 音波
        g.DrawArc(pen, x + 6f, cy - 5f, 8f, 10f, -60, 120);
    }

    /// <summary>
    /// 繪製網路地球圖標 (Network Globe)
    /// </summary>
    public static void DrawNetwork(Graphics g, Rectangle bounds, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = bounds.X + bounds.Width / 2f;
        float cy = bounds.Y + bounds.Height / 2f;
        float r = Math.Min(bounds.Width, bounds.Height) / 2f - 2f;

        using var pen = new Pen(color, 1.5f);
        g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
        g.DrawLine(pen, cx - r, cy, cx + r, cy);
        g.DrawEllipse(pen, cx - r * 0.45f, cy - r, r * 0.9f, r * 2);
    }

    /// <summary>
    /// 繪製帳號人物圖標 (Account User)
    /// </summary>
    public static void DrawUser(Graphics g, Rectangle bounds, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = bounds.X + bounds.Width / 2f;
        float cy = bounds.Y + bounds.Height / 2f;
        float headR = bounds.Height * 0.22f;

        using var pen = new Pen(color, 1.6f);
        // 頭部
        g.DrawEllipse(pen, cx - headR, cy - headR * 1.8f, headR * 2, headR * 2);
        // 肩膀拱形
        g.DrawArc(pen, cx - bounds.Width * 0.4f, cy, bounds.Width * 0.8f, bounds.Height * 0.7f, 190, 160);
    }

    /// <summary>
    /// 繪製連線箭頭圖標 (Connect Arrow ➔)
    /// </summary>
    public static void DrawConnectArrow(Graphics g, Rectangle bounds, Color color)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = bounds.X + bounds.Width / 2f;
        float cy = bounds.Y + bounds.Height / 2f;
        float halfW = Math.Min(bounds.Width, bounds.Height) * 0.35f;

        using var pen = new Pen(color, 2.2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        // 箭身主幹
        g.DrawLine(pen, cx - halfW, cy, cx + halfW, cy);
        // 箭頭上下翼
        float headSize = halfW * 0.75f;
        g.DrawLine(pen, cx + halfW - headSize, cy - headSize, cx + halfW, cy);
        g.DrawLine(pen, cx + halfW - headSize, cy + headSize, cx + halfW, cy);
    }

    /// <summary>
    /// 建立連線箭頭點陣圖 (含透明背景)
    /// </summary>
    public static Bitmap CreateConnectArrowBitmap(int width, int height, Color color)
    {
        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        DrawConnectArrow(g, new Rectangle(0, 0, width, height), color);
        return bmp;
    }

    /// <summary>
    /// 繪製動態旋轉載入進度環 (Loading Spinner)
    /// </summary>
    public static void DrawSpinner(Graphics g, Rectangle bounds, float angle, Color color, float thickness = 2.4f)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float pad = thickness + 1.5f;
        float x = bounds.X + pad;
        float y = bounds.Y + pad;
        float w = bounds.Width - pad * 2f;
        float h = bounds.Height - pad * 2f;
        if (w <= 2f || h <= 2f) return;

        // 底色環軌道 (微透環)
        using var trackPen = new Pen(Color.FromArgb(40, color), thickness);
        g.DrawEllipse(trackPen, x, y, w, h);

        // 旋轉亮條 (270度圓角圓弧)
        using var arcPen = new Pen(color, thickness)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        g.DrawArc(arcPen, x, y, w, h, angle, 270f);
    }

    /// <summary>
    /// 建立動態載入進度環點陣圖 (含透明背景)
    /// </summary>
    public static Bitmap CreateSpinnerBitmap(int width, int height, float angle, Color color, float thickness = 2.4f)
    {
        var bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        DrawSpinner(g, new Rectangle(0, 0, width, height), angle, color, thickness);
        return bmp;
    }
}
