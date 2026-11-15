using System.Drawing;

namespace RemoteDesk.Common;

/// <summary>自適應解析度運作模式。</summary>
public enum ResolutionMode : byte
{
    /// <summary>自動模式（預設）：優先維持原生解析度，僅在持續傳輸壅塞時降級，網路穩定後逐步恢復。</summary>
    Auto = 0,
    /// <summary>原生鎖定模式：強制維持原始解析度，不允許自動降級（壅塞時提出警示）。</summary>
    Original = 1,
    /// <summary>流暢優先模式：優先確保操作順暢度，較積極降級並遵守遲滯與冷卻規範。</summary>
    Performance = 2
}

/// <summary>解析度階層定義。</summary>
public enum ResolutionLevel : byte
{
    /// <summary>Level 0：100% 原生解析度（例：1920×1080）。</summary>
    Level0 = 0,
    /// <summary>Level 1：75% 中度降採樣（例：1440×810），平衡文字可讀性與頻寬。</summary>
    Level1 = 1,
    /// <summary>Level 2：50% 高度壓縮（例：960×540），嚴重壅塞時保底流暢度。</summary>
    Level2 = 2
}

/// <summary>單一解析度層級配置。</summary>
public readonly record struct ResolutionProfile(
    ResolutionLevel Level,
    int OriginalWidth,
    int OriginalHeight,
    int StreamWidth,
    int StreamHeight)
{
    public bool IsNative => Level == ResolutionLevel.Level0 || (StreamWidth == OriginalWidth && StreamHeight == OriginalHeight);
    public double ScaleX => OriginalWidth > 0 ? (double)StreamWidth / OriginalWidth : 1.0;
    public double ScaleY => OriginalHeight > 0 ? (double)StreamHeight / OriginalHeight : 1.0;
    public string LevelLabel => Level switch
    {
        ResolutionLevel.Level0 => "Level 0 (原生 100%)",
        ResolutionLevel.Level1 => "Level 1 (中度 75%)",
        ResolutionLevel.Level2 => "Level 2 (低頻寬 50%)",
        _ => $"Level {(byte)Level}"
    };

    public override string ToString() => $"{StreamWidth}x{StreamHeight} [{LevelLabel}]";
}

/// <summary>
/// 統一的 Resolution Profile 與座標轉換管理機制。
/// 依照實際來源螢幕解析度與長寬比動態計算各層級尺寸，不對 1920×1080 做硬編碼假設。
/// </summary>
internal static class ResolutionProfileManager
{
    public const int MaxLevelIndex = 2;

    /// <summary>依據原始解析度建立完整三階 ResolutionProfile 集合。</summary>
    public static ResolutionProfile[] BuildProfiles(int originalWidth, int originalHeight)
    {
        int origW = Math.Max(2, originalWidth);
        int origH = Math.Max(2, originalHeight);

        return new[]
        {
            new ResolutionProfile(ResolutionLevel.Level0, origW, origH, origW, origH),
            CreateScaledProfile(ResolutionLevel.Level1, origW, origH, 0.75),
            CreateScaledProfile(ResolutionLevel.Level2, origW, origH, 0.50),
        };
    }

    public static ResolutionProfile GetProfile(int originalWidth, int originalHeight, ResolutionLevel level)
    {
        int origW = Math.Max(2, originalWidth);
        int origH = Math.Max(2, originalHeight);
        return level switch
        {
            ResolutionLevel.Level0 => new ResolutionProfile(ResolutionLevel.Level0, origW, origH, origW, origH),
            ResolutionLevel.Level1 => CreateScaledProfile(ResolutionLevel.Level1, origW, origH, 0.75),
            ResolutionLevel.Level2 => CreateScaledProfile(ResolutionLevel.Level2, origW, origH, 0.50),
            _ => new ResolutionProfile(ResolutionLevel.Level0, origW, origH, origW, origH),
        };
    }

    private static ResolutionProfile CreateScaledProfile(ResolutionLevel level, int origW, int origH, double scale)
    {
        int minDim = Math.Min(Math.Min(origW, origH), 64);
        int sw = AlignEvenNearest(origW * scale, minDim, origW);
        int sh = AlignEvenNearest(origH * scale, minDim, origH);
        return new ResolutionProfile(level, origW, origH, sw, sh);
    }

    /// <summary>
    /// 將目標尺寸四捨五入對齊至最接近的偶數像素（減少 YUV 4:2:0 色度子採樣邊界偏移），同時保留精確比例（如 1440×810、960×540）。
    /// </summary>
    public static int AlignEvenNearest(double value, int minVal, int maxVal)
    {
        if (maxVal <= 2) return maxVal;
        int rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        if ((rounded & 1) != 0)
        {
            // 朝最接近原始浮點值的偶數方向取整
            rounded = (value >= rounded) ? rounded + 1 : rounded - 1;
            if ((rounded & 1) != 0) rounded--;
        }
        rounded = Math.Clamp(rounded, minVal, maxVal);
        if ((rounded & 1) != 0 && rounded > 2) rounded--;
        return rounded;
    }

    /// <summary>
    /// 將原生螢幕座標系之 Dirty Rectangles 轉換至降採樣後的串流座標系。
    /// 採用保守外擴邊界（Floor 左上 / Ceiling 右下），確保邊界像素絕對不遺失。
    /// </summary>
    public static List<Rectangle> ScaleDirtyRectangles(
        IReadOnlyList<Rectangle> rawDirty,
        int origW, int origH,
        int streamW, int streamH)
    {
        if (rawDirty == null || rawDirty.Count == 0) return new List<Rectangle>();
        if (origW <= 0 || origH <= 0 || streamW <= 0 || streamH <= 0) return new List<Rectangle>();

        if (origW == streamW && origH == streamH)
        {
            var copy = new List<Rectangle>(rawDirty.Count);
            var bounds = new Rectangle(0, 0, origW, origH);
            foreach (var r in rawDirty)
            {
                var clipped = Rectangle.Intersect(r, bounds);
                if (clipped.Width > 0 && clipped.Height > 0) copy.Add(clipped);
            }
            return copy;
        }

        double sx = (double)streamW / origW;
        double sy = (double)streamH / origH;
        var origBounds = new Rectangle(0, 0, origW, origH);
        var result = new List<Rectangle>(rawDirty.Count);

        foreach (var raw in rawDirty)
        {
            var r = Rectangle.Intersect(raw, origBounds);
            if (r.Width <= 0 || r.Height <= 0) continue;

            int left = Math.Clamp((int)Math.Floor(r.Left * sx), 0, streamW - 1);
            int top = Math.Clamp((int)Math.Floor(r.Top * sy), 0, streamH - 1);
            int right = Math.Clamp((int)Math.Ceiling(r.Right * sx), left + 1, streamW);
            int bottom = Math.Clamp((int)Math.Ceiling(r.Bottom * sy), top + 1, streamH);

            result.Add(new Rectangle(left, top, right - left, bottom - top));
        }

        return result;
    }

    /// <summary>
    /// 對 MoveRectangle 執行來源與目的端對稱裁剪，確保兩者永遠維持完全一致的位移量、寬高與邊界合法性。
    /// 若負數或越界部分導致無效區域或無位移，回傳 false。
    /// </summary>
    public static bool TryClipMoveRectangle(MoveRectangle m, int width, int height, out MoveRectangle clipped)
    {
        clipped = default;
        if (width <= 0 || height <= 0) return false;
        int w = m.DestinationRect.Width;
        int h = m.DestinationRect.Height;
        if (w <= 0 || h <= 0) return false;

        int srcX = m.SourcePoint.X;
        int srcY = m.SourcePoint.Y;
        int dstX = m.DestinationRect.X;
        int dstY = m.DestinationRect.Y;

        int skipLeft = Math.Max(0, Math.Max(-srcX, -dstX));
        int skipTop = Math.Max(0, Math.Max(-srcY, -dstY));

        long srcRight = (long)srcX + w;
        long dstRight = (long)dstX + w;
        long srcBottom = (long)srcY + h;
        long dstBottom = (long)dstY + h;

        int trimRight = (int)Math.Max(0, Math.Max(srcRight - width, dstRight - width));
        int trimBottom = (int)Math.Max(0, Math.Max(srcBottom - height, dstBottom - height));

        int clippedW = w - skipLeft - trimRight;
        int clippedH = h - skipTop - trimBottom;
        if (clippedW <= 0 || clippedH <= 0) return false;

        int finalSrcX = srcX + skipLeft;
        int finalSrcY = srcY + skipTop;
        int finalDstX = dstX + skipLeft;
        int finalDstY = dstY + skipTop;
        if (finalSrcX == finalDstX && finalSrcY == finalDstY) return false;

        clipped = new MoveRectangle(
            new Point(finalSrcX, finalSrcY),
            new Rectangle(finalDstX, finalDstY, clippedW, clippedH));
        return true;
    }

    public static List<MoveRectangle>? ClipMoveRectangles(IReadOnlyList<MoveRectangle>? rawMoves, int width, int height)
    {
        if (rawMoves == null || rawMoves.Count == 0 || width <= 0 || height <= 0) return null;
        var list = new List<MoveRectangle>(rawMoves.Count);
        foreach (var m in rawMoves)
        {
            if (TryClipMoveRectangle(m, width, height, out var clipped))
            {
                list.Add(clipped);
            }
        }
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// 將原生螢幕座標系之 Move Rectangles 轉換至降採樣後的串流座標系，
    /// 嚴格保證 SourceRect 與 DestinationRect 具有完全一致之寬高且不越界（含負數與越界來源的對稱裁剪）。
    /// </summary>
    public static List<MoveRectangle>? ScaleMoveRectangles(
        IReadOnlyList<MoveRectangle>? rawMoves,
        int origW, int origH,
        int streamW, int streamH)
    {
        if (rawMoves == null || rawMoves.Count == 0) return null;
        if (origW <= 0 || origH <= 0 || streamW <= 0 || streamH <= 0) return null;

        if (origW == streamW && origH == streamH)
        {
            return ClipMoveRectangles(rawMoves, streamW, streamH);
        }

        double sx = (double)streamW / origW;
        double sy = (double)streamH / origH;
        var list = new List<MoveRectangle>(rawMoves.Count);

        foreach (var rawMove in rawMoves)
        {
            if (!TryClipMoveRectangle(rawMove, origW, origH, out var m)) continue;

            int srcX = Math.Clamp((int)Math.Round(m.SourcePoint.X * sx), 0, streamW - 1);
            int srcY = Math.Clamp((int)Math.Round(m.SourcePoint.Y * sy), 0, streamH - 1);
            int dstX = Math.Clamp((int)Math.Round(m.DestinationRect.X * sx), 0, streamW - 1);
            int dstY = Math.Clamp((int)Math.Round(m.DestinationRect.Y * sy), 0, streamH - 1);

            if (srcX == dstX && srcY == dstY) continue;

            int targetW = (int)Math.Round(m.DestinationRect.Width * sx);
            int targetH = (int)Math.Round(m.DestinationRect.Height * sy);

            int maxAvailW = Math.Min(streamW - srcX, streamW - dstX);
            int maxAvailH = Math.Min(streamH - srcY, streamH - dstY);

            int w = Math.Min(targetW, maxAvailW);
            int h = Math.Min(targetH, maxAvailH);
            if (w <= 0 || h <= 0) continue;

            list.Add(new MoveRectangle(new Point(srcX, srcY), new Rectangle(dstX, dstY, w, h)));
        }

        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// 將 Viewer 顯示視窗內的本機滑鼠座標，準確映射至 Host 原始原生解析度座標（不受串流降採樣影響）。
    /// </summary>
    public static bool TryMapViewerPointToHostOriginal(
        int clientX, int clientY,
        Rectangle displayDestRect,
        int hostOriginalWidth, int hostOriginalHeight,
        out int remoteX, out int remoteY)
    {
        remoteX = 0;
        remoteY = 0;
        if (displayDestRect.IsEmpty || displayDestRect.Width <= 0 || displayDestRect.Height <= 0) return false;
        if (hostOriginalWidth <= 0 || hostOriginalHeight <= 0) return false;
        if (!displayDestRect.Contains(clientX, clientY)) return false;

        double ratioX = (double)hostOriginalWidth / displayDestRect.Width;
        double ratioY = (double)hostOriginalHeight / displayDestRect.Height;

        remoteX = Math.Clamp((int)((clientX - displayDestRect.X) * ratioX), 0, hostOriginalWidth - 1);
        remoteY = Math.Clamp((int)((clientY - displayDestRect.Y) * ratioY), 0, hostOriginalHeight - 1);
        return true;
    }
}
