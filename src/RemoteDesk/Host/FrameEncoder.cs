using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using RemoteDesk.Common;

namespace RemoteDesk.Host;

internal sealed record EncodedTile(int X, int Y, int W, int H, TileEncoding Encoding, byte[] Data);

/// <summary>
/// 區塊差異編碼器：把畫面切成 64x64 區塊，只送出有變動的部分（JPEG）；
/// 靜止一小段時間後再以無失真（Deflate）或高品質重送，讓文字像原生畫面一樣銳利。
/// </summary>
internal sealed unsafe class FrameEncoder
{
    public const int Tile = 64;
    private readonly int _w, _h, _tx, _ty;
    private int _streamW, _streamH;
    private bool _isScaled;
    private readonly byte[] _prev; // pinned
    private readonly byte* _prevPtr;
    private byte[]? _scaledBuffer; // pinned, lazily allocated when downscaled
    private byte* _scaledPtr;
    private int[] _mapX0 = Array.Empty<int>();
    private int[] _mapX1 = Array.Empty<int>();
    private int[] _mapWx = Array.Empty<int>();
    private int[] _mapY0 = Array.Empty<int>();
    private int[] _mapY1 = Array.Empty<int>();
    private int[] _mapWy = Array.Empty<int>();
    private readonly long[] _changedAt;
    private readonly bool[] _needRefine;
    private bool _forceKey = true;
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    [ThreadStatic] private static MemoryStream? t_encodeStream;
    [ThreadStatic] private static EncoderParameters? t_encoderParams;

    public int Quality { get; set; } = 70;
    public int CongestionQualityReduction { get; set; }
    public bool Lossless { get; set; } = false; // 外網預設採用 JPEG 高品質平滑補送，避免大量 Deflate 佔滿上傳頻寬
    public int RefineDelayMs { get; set; } = 350;
    public int MaxRefineTilesPerFrame { get; set; } = 32; // 每次補送預算降至 32 塊 (<50KB)，避免影格突發造成外網佇列延遲飆升

    public int OriginalWidth => _w;
    public int OriginalHeight => _h;
    public int StreamWidth => _streamW;
    public int StreamHeight => _streamH;
    public int Width => _streamW;
    public int Height => _streamH;

    public FrameEncoder(int width, int height)
    {
        _w = width;
        _h = height;
        _streamW = width;
        _streamH = height;
        _isScaled = false;
        _tx = (width + Tile - 1) / Tile;
        _ty = (height + Tile - 1) / Tile;
        _prev = GC.AllocateUninitializedArray<byte>(width * height * 4, pinned: true);
        _prevPtr = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(_prev));
        _changedAt = new long[_tx * _ty];
        _needRefine = new bool[_tx * _ty];
    }

    /// <summary>
    /// 配置目前的串流編碼解析度（必須 <= 原始擷取解析度）。
    /// 此設定與 JPEG Quality 完全解耦：僅由自適應解析度控制器或使用者模式切換呼叫。
    /// </summary>
    public void ConfigureStreamResolution(int streamWidth, int streamHeight)
    {
        int clampedW = Math.Clamp(streamWidth, 2, _w);
        int clampedH = Math.Clamp(streamHeight, 2, _h);
        if (clampedW == _streamW && clampedH == _streamH) return;

        _streamW = clampedW;
        _streamH = clampedH;
        _isScaled = (_streamW != _w || _streamH != _h);
        Array.Clear(_needRefine);
        Array.Clear(_changedAt);
        _forceKey = true;

        if (!_isScaled) return;

        if (_scaledBuffer == null)
        {
            _scaledBuffer = GC.AllocateUninitializedArray<byte>(_w * _h * 4, pinned: true);
            _scaledPtr = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(
                ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(_scaledBuffer));
        }

        BuildResampleLut(_w, _h, _streamW, _streamH);
    }

    private void BuildResampleLut(int srcW, int srcH, int dstW, int dstH)
    {
        _mapX0 = new int[dstW];
        _mapX1 = new int[dstW];
        _mapWx = new int[dstW];
        _mapY0 = new int[dstH];
        _mapY1 = new int[dstH];
        _mapWy = new int[dstH];

        for (int dx = 0; dx < dstW; dx++)
        {
            double fx = Math.Clamp(((dx + 0.5) * srcW / dstW) - 0.5, 0.0, srcW - 1.0);
            int x0 = (int)Math.Floor(fx);
            int x1 = Math.Min(srcW - 1, x0 + 1);
            int wx = (int)Math.Round((fx - x0) * 16.0);
            _mapX0[dx] = x0;
            _mapX1[dx] = x1;
            _mapWx[dx] = Math.Clamp(wx, 0, 16);
        }

        for (int dy = 0; dy < dstH; dy++)
        {
            double fy = Math.Clamp(((dy + 0.5) * srcH / dstH) - 0.5, 0.0, srcH - 1.0);
            int y0 = (int)Math.Floor(fy);
            int y1 = Math.Min(srcH - 1, y0 + 1);
            int wy = (int)Math.Round((fy - y0) * 16.0);
            _mapY0[dy] = y0;
            _mapY1[dy] = y1;
            _mapWy[dy] = Math.Clamp(wy, 0, 16);
        }
    }

    public void RequestKeyframe() => _forceKey = true;

    public int GetEffectiveTileQuality(long rectPixels, long totalPixels, bool isKeyframe) =>
        ComputeTileQuality(rectPixels, totalPixels, isKeyframe);

    private int ComputeTileQuality(long rectPixels, long totalPixels, bool isKeyframe)
    {
        int q = Math.Clamp(Quality, 10, 100);
        // 僅由真實網路壅塞遙測（CongestionQualityReduction）決定臨時壓縮降載，
        // 絕不將大面積畫面更新直接視為網路壅塞而無條件降質，亦不改動使用者 Quality 設定。
        int penalty = (Lossless || q >= 95) ? 0 : Math.Clamp(CongestionQualityReduction, 0, 20);
        if (isKeyframe)
        {
            return Math.Clamp(q - (penalty / 2), 10, 95);
        }
        return Math.Max(20, q - penalty);
    }

    private void ResampleRegionBgra(byte* src, int srcStride, byte* dst, int dstStride, int dx0, int dy0, int dw, int dh)
    {
        int[] x0Lut = _mapX0;
        int[] x1Lut = _mapX1;
        int[] wxLut = _mapWx;
        int[] y0Lut = _mapY0;
        int[] y1Lut = _mapY1;
        int[] wyLut = _mapWy;

        void ProcessRow(int dy)
        {
            int y0 = y0Lut[dy];
            int y1 = y1Lut[dy];
            int wy = wyLut[dy];
            int iwy = 16 - wy;
            byte* row0 = src + (long)y0 * srcStride;
            byte* row1 = src + (long)y1 * srcStride;
            uint* dstRow = (uint*)(dst + (long)dy * dstStride) + dx0;

            for (int i = 0; i < dw; i++)
            {
                int dx = dx0 + i;
                int x0 = x0Lut[dx];
                int x1 = x1Lut[dx];
                int wx = wxLut[dx];
                int iwx = 16 - wx;

                uint w00 = (uint)(iwx * iwy);
                uint w01 = (uint)(wx * iwy);
                uint w10 = (uint)(iwx * wy);
                uint w11 = (uint)(wx * wy);

                uint p00 = *(uint*)(row0 + (long)x0 * 4);
                uint p01 = *(uint*)(row0 + (long)x1 * 4);
                uint p10 = *(uint*)(row1 + (long)x0 * 4);
                uint p11 = *(uint*)(row1 + (long)x1 * 4);

                uint rb = ((p00 & 0x00FF00FFu) * w00)
                        + ((p01 & 0x00FF00FFu) * w01)
                        + ((p10 & 0x00FF00FFu) * w10)
                        + ((p11 & 0x00FF00FFu) * w11)
                        + 0x00800080u;

                uint g = (((p00 >> 8) & 0xFFu) * w00)
                       + (((p01 >> 8) & 0xFFu) * w01)
                       + (((p10 >> 8) & 0xFFu) * w10)
                       + (((p11 >> 8) & 0xFFu) * w11)
                       + 128u;

                dstRow[i] = ((rb >> 8) & 0x00FF00FFu) | (g & 0x0000FF00u) | 0xFF000000u;
            }
        }

        if (dh >= 64 && dw >= 128)
        {
            Parallel.For(dy0, dy0 + dh, ProcessRow);
        }
        else
        {
            for (int dy = dy0; dy < dy0 + dh; dy++) ProcessRow(dy);
        }
    }

    /// <summary>比對新畫面並產生要傳送的區塊。支援 DXGI 原生 Dirty/Move 元資料，避免 CPU 逐格記憶體比對。</summary>
    public List<EncodedTile> Encode(
        CapturedFrame frame,
        bool newFrame,
        long nowMs,
        out FrameType frameType,
        out IReadOnlyList<MoveRectangle>? outMoveRects)
    {
        bool key = _forceKey;
        _forceKey = false;
        outMoveRects = null;

        // ====== 1. DXGI 原生變動資訊處理路徑 ======
        if (frame.HasMetadata && (newFrame || key))
        {
            if (frame.Width != _w || frame.Height != _h) throw new ArgumentException("畫面尺寸改變");

            byte* rawSrc = (byte*)frame.Data;
            int rawStride = frame.Stride > 0 ? frame.Stride : _w * 4;

            if (key || frame.IsKeyFrame)
            {
                frameType = FrameType.FullFrame;
                int kQuality = ComputeTileQuality((long)_streamW * _streamH, (long)_streamW * _streamH, isKeyframe: true);

                EncodedTile fullTile;
                if (_isScaled)
                {
                    ResampleRegionBgra(rawSrc, rawStride, _scaledPtr, _streamW * 4, 0, 0, _streamW, _streamH);
                    fullTile = EncodeJpeg(0, 0, _streamW, _streamH, kQuality, _scaledPtr, _streamW * 4);
                }
                else
                {
                    // FullFrame (基準影格)：僅於原生 FullFrame 時維護本機背景緩衝區
                    if (rawSrc != null && (IntPtr)_prevPtr != (IntPtr)rawSrc)
                    {
                        Buffer.MemoryCopy(rawSrc, _prevPtr, (long)_w * _h * 4, (long)_w * _h * 4);
                    }
                    fullTile = EncodeJpeg(0, 0, _w, _h, kQuality, rawSrc, rawStride);
                }
                return new List<EncodedTile> { fullTile };
            }

            List<Rectangle> consolidated;
            byte* encSrc;
            int encStride;

            if (_isScaled)
            {
                outMoveRects = ResolutionProfileManager.ScaleMoveRectangles(frame.MoveRectangles, _w, _h, _streamW, _streamH);
                var scaledDirty = ResolutionProfileManager.ScaleDirtyRectangles(
                    frame.DirtyRectangles ?? Array.Empty<Rectangle>(), _w, _h, _streamW, _streamH);
                consolidated = ConsolidateDirtyRectangles(scaledDirty, _streamW, _streamH);
                encSrc = _scaledPtr;
                encStride = _streamW * 4;
            }
            else
            {
                outMoveRects = ResolutionProfileManager.ClipMoveRectangles(frame.MoveRectangles, _w, _h);
                consolidated = ConsolidateDirtyRectangles(frame.DirtyRectangles ?? Array.Empty<Rectangle>(), _w, _h);
                encSrc = rawSrc;
                encStride = rawStride;
            }

            if (consolidated.Count == 0 && (outMoveRects == null || outMoveRects.Count == 0))
            {
                frameType = FrameType.RegionUpdate;
                return new List<EncodedTile>();
            }

            frameType = (outMoveRects != null && outMoveRects.Count > 0)
                ? FrameType.MoveAndRegion
                : FrameType.RegionUpdate;

            var tiles = new List<EncodedTile>(consolidated.Count);
            long totalStreamPixels = (long)_streamW * _streamH;

            if (consolidated.Count == 1)
            {
                var r = consolidated[0];
                if (_isScaled)
                {
                    ResampleRegionBgra(rawSrc, rawStride, _scaledPtr, _streamW * 4, r.X, r.Y, r.Width, r.Height);
                }
                int q = ComputeTileQuality((long)r.Width * r.Height, totalStreamPixels, isKeyframe: false);
                tiles.Add(EncodeJpeg(r.X, r.Y, r.Width, r.Height, q, encSrc, encStride));
            }
            else if (consolidated.Count > 1)
            {
                var tileArray = new EncodedTile[consolidated.Count];
                Parallel.For(0, consolidated.Count, i =>
                {
                    var r = consolidated[i];
                    if (_isScaled)
                    {
                        ResampleRegionBgra(rawSrc, rawStride, _scaledPtr, _streamW * 4, r.X, r.Y, r.Width, r.Height);
                    }
                    int q = ComputeTileQuality((long)r.Width * r.Height, totalStreamPixels, isKeyframe: false);
                    tileArray[i] = EncodeJpeg(r.X, r.Y, r.Width, r.Height, q, encSrc, encStride);
                });
                tiles.AddRange(tileArray);
            }

            return tiles;
        }

        // ====== 2. GDI / 無元資料軟體比對降級路徑 ======
        frameType = key ? FrameType.FullFrame : FrameType.RegionUpdate;

        int stx = (_streamW + Tile - 1) / Tile;
        int sty = (_streamH + Tile - 1) / Tile;

        // 0 = 無, 1 = 有變動（有損）, 2 = 需補送高品質
        var kind = new byte[stx * sty];

        if (newFrame || key)
        {
            if (frame.Width != _w || frame.Height != _h) throw new ArgumentException("畫面尺寸改變");
            byte* rawSrc = (byte*)frame.Data;
            int rawStride = frame.Stride > 0 ? frame.Stride : _w * 4;
            byte* src;
            int srcStride;
            if (_isScaled)
            {
                ResampleRegionBgra(rawSrc, rawStride, _scaledPtr, _streamW * 4, 0, 0, _streamW, _streamH);
                src = _scaledPtr;
                srcStride = _streamW * 4;
            }
            else
            {
                src = rawSrc;
                srcStride = rawStride;
            }

            int dstStride = _streamW * 4;
            {
                byte* dst = _prevPtr;
                Parallel.For(0, sty, ty =>
                {
                    int y0 = ty * Tile, y1 = Math.Min(_streamH, y0 + Tile);
                    for (int tx = 0; tx < stx; tx++)
                    {
                        int x0 = tx * Tile, x1 = Math.Min(_streamW, x0 + Tile);
                        int bytes = (x1 - x0) * 4;
                        bool changed = key;
                        if (!changed)
                        {
                            for (int y = y0; y < y1; y++)
                            {
                                var a = new ReadOnlySpan<byte>(src + (long)y * srcStride + x0 * 4, bytes);
                                var b = new ReadOnlySpan<byte>(dst + (long)y * dstStride + x0 * 4, bytes);
                                if (!a.SequenceEqual(b)) { changed = true; break; }
                            }
                        }
                        if (changed)
                        {
                            for (int y = y0; y < y1; y++)
                                Buffer.MemoryCopy(src + (long)y * srcStride + x0 * 4, dst + (long)y * dstStride + x0 * 4, bytes, bytes);
                            int idx = ty * stx + tx;
                            kind[idx] = 1;
                            _changedAt[idx] = nowMs;
                            _needRefine[idx] = true;
                        }
                    }
                });
            }
        }

        int refineBudget = MaxRefineTilesPerFrame;
        for (int i = 0; i < kind.Length && refineBudget > 0; i++)
        {
            if (kind[i] == 0 && _needRefine[i] && nowMs - _changedAt[i] >= RefineDelayMs)
            {
                kind[i] = 2;
                _needRefine[i] = false;
                refineBudget--;
            }
        }

        // 1. 同一列中相鄰且同類型的區塊先水平合併成帶狀矩形
        var rects = new List<(int x, int y, int w, int h, byte kind)>();
        for (int ty = 0; ty < sty; ty++)
        {
            int tx = 0;
            while (tx < stx)
            {
                byte k = kind[ty * stx + tx];
                if (k == 0) { tx++; continue; }
                int start = tx;
                while (tx < stx && kind[ty * stx + tx] == k) tx++;
                int x = start * Tile, y = ty * Tile;
                rects.Add((x, y, Math.Min(_streamW, tx * Tile) - x, Math.Min(_streamH, y + Tile) - y, k));
            }
        }

        // 2. 2D 垂直貪婪合併
        if (rects.Count > 1)
        {
            for (int i = 0; i < rects.Count; i++)
            {
                var cur = rects[i];
                int j = i + 1;
                while (j < rects.Count)
                {
                    var next = rects[j];
                    if (cur.kind == next.kind && cur.x == next.x && cur.w == next.w && (cur.y + cur.h) == next.y)
                    {
                        cur = (cur.x, cur.y, cur.w, cur.h + next.h, cur.kind);
                        rects[i] = cur;
                        rects.RemoveAt(j);
                    }
                    else
                    {
                        j++;
                    }
                }
            }
        }

        if (rects.Count == 0) return new List<EncodedTile>();

        var result = new EncodedTile[rects.Count];
        long totalPixels = (long)_streamW * _streamH;

        Parallel.For(0, rects.Count, i =>
        {
            var r = rects[i];
            if (r.kind == 1)
            {
                int q = ComputeTileQuality((long)r.w * r.h, totalPixels, isKeyframe: key);
                result[i] = EncodeJpeg(r.x, r.y, r.w, r.h, q);
            }
            else
            {
                result[i] = Lossless ? EncodeDeflate(r.x, r.y, r.w, r.h) : EncodeJpeg(r.x, r.y, r.w, r.h, 92);
            }
        });
        return result.ToList();
    }

    public List<EncodedTile> Encode(CapturedFrame frame, bool newFrame, long nowMs) =>
        Encode(frame, newFrame, nowMs, out _, out _);

    /// <summary>
    /// 合併 Dirty Rectangles，過濾超出邊界及零面積區域，合併相鄰及重疊區塊。
    /// </summary>
    public static List<Rectangle> ConsolidateDirtyRectangles(IReadOnlyList<Rectangle> rawRects, int screenWidth, int screenHeight)
    {
        if (rawRects == null || rawRects.Count == 0) return new List<Rectangle>();

        var list = new List<Rectangle>(rawRects.Count);
        var screenBounds = new Rectangle(0, 0, screenWidth, screenHeight);
        long totalArea = 0;

        foreach (var r in rawRects)
        {
            var intersect = Rectangle.Intersect(r, screenBounds);
            if (intersect.Width > 0 && intersect.Height > 0)
            {
                list.Add(intersect);
                totalArea += (long)intersect.Width * intersect.Height;
            }
        }

        if (list.Count <= 1) return list;

        // 若變更總面積超過螢幕 70% 或矩形數量超過 32 個，直接合併為單一 Bounding Rect
        if (totalArea >= (long)screenWidth * screenHeight * 0.70 || list.Count > 32)
        {
            int minX = list.Min(r => r.Left);
            int minY = list.Min(r => r.Top);
            int maxX = list.Max(r => r.Right);
            int maxY = list.Max(r => r.Bottom);
            return new List<Rectangle> { new Rectangle(minX, minY, maxX - minX, maxY - minY) };
        }

        // 貪婪合併：重疊或相鄰 (邊界距離 <= 16 像素) 的矩形合併為聯集
        bool merged = true;
        while (merged && list.Count > 1)
        {
            merged = false;
            for (int i = 0; i < list.Count - 1 && !merged; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    var r1 = list[i];
                    var r2 = list[j];
                    var inflated = Rectangle.Inflate(r1, 16, 16);
                    if (inflated.IntersectsWith(r2))
                    {
                        list[i] = Rectangle.Union(r1, r2);
                        list.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
            }
        }

        return list;
    }

    private EncodedTile EncodeJpeg(int x, int y, int w, int h, int quality, byte* pSrc = null, int stride = 0)
    {
        byte* p = (pSrc != null) ? pSrc : _prevPtr;
        int s = (stride > 0) ? stride : _streamW * 4;
        using var bmp = new Bitmap(w, h, s, PixelFormat.Format32bppRgb, (IntPtr)(p + (long)y * s + x * 4));

        var ms = t_encodeStream;
        if (ms == null)
        {
            ms = new MemoryStream(Math.Max(16384, w * h / 4));
            t_encodeStream = ms;
        }
        else
        {
            ms.SetLength(0);
        }

        var ep = t_encoderParams;
        if (ep == null)
        {
            ep = new EncoderParameters(1);
            t_encoderParams = ep;
        }
        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);

        bmp.Save(ms, JpegCodec, ep);
        return new EncodedTile(x, y, w, h, TileEncoding.Jpeg, ms.ToArray());
    }

    private EncodedTile EncodeDeflate(int x, int y, int w, int h)
    {
        var row = new byte[w * 3];
        using var ms = new MemoryStream(w * h);
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            {
                byte* p = _prevPtr;
                int stride = _streamW * 4;
                for (int yy = 0; yy < h; yy++)
                {
                    byte* s = p + (long)(y + yy) * stride + x * 4;
                    for (int xx = 0, o = 0; xx < w; xx++, s += 4, o += 3)
                    {
                        row[o] = s[0]; row[o + 1] = s[1]; row[o + 2] = s[2];
                    }
                    z.Write(row, 0, row.Length);
                }
            }
        }
        return new EncodedTile(x, y, w, h, TileEncoding.Deflate, ms.ToArray());
    }

    public static byte[] Serialize(
        uint seq,
        uint frameVersion,
        uint baseVersion,
        FrameType frameType,
        int width,
        int height,
        IReadOnlyList<MoveRectangle>? moveRects,
        IReadOnlyList<EncodedTile> tiles,
        uint streamConfigVersion)
    {
        int moveCount = moveRects?.Count ?? 0;
        int size = 34 + (moveCount * 24) + tiles.Sum(t => t.Data.Length + 16);
        var w = new PacketWriter((byte)HostMsg.VideoFrame, size);
        byte rawFrameType = streamConfigVersion > 0
            ? (byte)((byte)frameType | 0x80)
            : (byte)frameType;
        w.U32(seq).I32(width).I32(height).U32(frameVersion).U32(baseVersion).U8(rawFrameType);
        if (streamConfigVersion > 0)
        {
            w.U32(streamConfigVersion);
        }

        w.U16((ushort)moveCount);
        if (moveRects != null)
        {
            foreach (var m in moveRects)
            {
                w.I32(m.SourcePoint.X).I32(m.SourcePoint.Y)
                 .I32(m.DestinationRect.X).I32(m.DestinationRect.Y)
                 .I32(m.DestinationRect.Width).I32(m.DestinationRect.Height);
            }
        }

        w.U16((ushort)tiles.Count);
        foreach (var t in tiles)
            w.U16((ushort)t.X).U16((ushort)t.Y).U16((ushort)t.W).U16((ushort)t.H).U8((byte)t.Encoding).Bytes(t.Data);

        return w.ToArray();
    }

    public static byte[] Serialize(
        uint seq,
        uint frameVersion,
        uint baseVersion,
        FrameType frameType,
        int width,
        int height,
        IReadOnlyList<MoveRectangle>? moveRects,
        IReadOnlyList<EncodedTile> tiles) =>
        Serialize(seq, frameVersion, baseVersion, frameType, width, height, moveRects, tiles, 0);

    public static byte[] Serialize(
        uint seq,
        uint frameVersion,
        FrameType frameType,
        int width,
        int height,
        IReadOnlyList<MoveRectangle>? moveRects,
        IReadOnlyList<EncodedTile> tiles) =>
        Serialize(seq, frameVersion, frameVersion > 1 ? frameVersion - 1 : 0, frameType, width, height, moveRects, tiles);

    public static byte[] Serialize(uint seq, int width, int height, IReadOnlyList<EncodedTile> tiles)
    {
        bool isFull = seq == 1 || (tiles.Count == 1 && tiles[0].X == 0 && tiles[0].Y == 0 && tiles[0].W == width && tiles[0].H == height);
        var fType = isFull ? FrameType.FullFrame : FrameType.RegionUpdate;
        uint baseVer = isFull ? 0 : (seq > 1 ? seq - 1 : 0);
        return Serialize(seq, seq, baseVer, fType, width, height, null, tiles);
    }
}

