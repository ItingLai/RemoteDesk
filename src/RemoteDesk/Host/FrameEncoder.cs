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
    private readonly byte[] _prev; // pinned
    private readonly byte* _prevPtr;
    private readonly long[] _changedAt;
    private readonly bool[] _needRefine;
    private bool _forceKey = true;
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public int Quality { get; set; } = 70;
    public bool Lossless { get; set; } = true;
    public int RefineDelayMs { get; set; } = 350;
    public int MaxRefineTilesPerFrame { get; set; } = 160;

    public int Width => _w;
    public int Height => _h;

    public FrameEncoder(int width, int height)
    {
        _w = width;
        _h = height;
        _tx = (width + Tile - 1) / Tile;
        _ty = (height + Tile - 1) / Tile;
        _prev = GC.AllocateUninitializedArray<byte>(width * height * 4, pinned: true);
        _prevPtr = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(_prev));
        _changedAt = new long[_tx * _ty];
        _needRefine = new bool[_tx * _ty];
    }

    public void RequestKeyframe() => _forceKey = true;

    /// <summary>比對新畫面並產生要傳送的區塊。newFrame=false 時只處理延遲的高品質補送。</summary>
    public List<EncodedTile> Encode(CapturedFrame frame, bool newFrame, long nowMs)
    {
        // 0 = 無, 1 = 有變動（有損）, 2 = 需補送高品質
        var kind = new byte[_tx * _ty];
        bool key = _forceKey;
        _forceKey = false;

        if (newFrame || key)
        {
            if (frame.Width != _w || frame.Height != _h) throw new ArgumentException("畫面尺寸改變");
            byte* src = (byte*)frame.Data;
            int srcStride = frame.Stride;
            int dstStride = _w * 4;
            {
                byte* dst = _prevPtr;
                Parallel.For(0, _ty, ty =>
                {
                    int y0 = ty * Tile, y1 = Math.Min(_h, y0 + Tile);
                    for (int tx = 0; tx < _tx; tx++)
                    {
                        int x0 = tx * Tile, x1 = Math.Min(_w, x0 + Tile);
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
                            int idx = ty * _tx + tx;
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

        // 同一列中相鄰且同類型的區塊合併成一個矩形
        var rects = new List<(int x, int y, int w, int h, byte kind)>();
        for (int ty = 0; ty < _ty; ty++)
        {
            int tx = 0;
            while (tx < _tx)
            {
                byte k = kind[ty * _tx + tx];
                if (k == 0) { tx++; continue; }
                int start = tx;
                while (tx < _tx && kind[ty * _tx + tx] == k) tx++;
                int x = start * Tile, y = ty * Tile;
                rects.Add((x, y, Math.Min(_w, tx * Tile) - x, Math.Min(_h, y + Tile) - y, k));
            }
        }
        if (rects.Count == 0) return new List<EncodedTile>();

        var result = new EncodedTile[rects.Count];
        Parallel.For(0, rects.Count, i =>
        {
            var r = rects[i];
            if (r.kind == 1) result[i] = EncodeJpeg(r.x, r.y, r.w, r.h, Quality);
            else result[i] = Lossless ? EncodeDeflate(r.x, r.y, r.w, r.h) : EncodeJpeg(r.x, r.y, r.w, r.h, 92);
        });
        return result.ToList();
    }

    private EncodedTile EncodeJpeg(int x, int y, int w, int h, int quality)
    {
        {
            byte* p = _prevPtr;
            int stride = _w * 4;
            using var bmp = new Bitmap(w, h, stride, PixelFormat.Format32bppRgb, (IntPtr)(p + (long)y * stride + x * 4));
            using var ms = new MemoryStream(w * h / 4);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
            bmp.Save(ms, JpegCodec, ep);
            return new EncodedTile(x, y, w, h, TileEncoding.Jpeg, ms.ToArray());
        }
    }

    private EncodedTile EncodeDeflate(int x, int y, int w, int h)
    {
        var row = new byte[w * 3];
        using var ms = new MemoryStream(w * h);
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            {
                byte* p = _prevPtr;
                int stride = _w * 4;
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

    public static byte[] Serialize(uint seq, int width, int height, IReadOnlyList<EncodedTile> tiles)
    {
        int size = 32 + tiles.Sum(t => t.Data.Length + 16);
        var w = new PacketWriter((byte)HostMsg.VideoFrame, size);
        w.U32(seq).I32(width).I32(height).U16((ushort)tiles.Count);
        foreach (var t in tiles)
            w.U16((ushort)t.X).U16((ushort)t.Y).U16((ushort)t.W).U16((ushort)t.H).U8((byte)t.Encoding).Bytes(t.Data);
        return w.ToArray();
    }
}

