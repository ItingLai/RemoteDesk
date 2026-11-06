using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using RemoteDesk.Common;

namespace RemoteDesk.Viewer;

/// <summary>
/// 影像區塊解碼器：接收遠端傳來的 JPEG / Deflate 壓縮區塊，
/// 並直接渲染到本機點陣圖 (32bpp BGRA) 上，支援區域性髒污更新以達成極致效能。
/// </summary>
internal sealed unsafe class FrameDecoder : IDisposable
{
    private Bitmap? _bitmap;
    private byte[]? _buffer;
    private byte* _bufPtr;
    private readonly object _renderLock = new();
    private int _width;
    private int _height;
    private bool _disposed;

    public int Width => _width;
    public int Height => _height;

    public void Resize(int width, int height)
    {
        lock (_renderLock)
        {
            if (_width == width && _height == height && _bitmap != null) return;

            Cleanup();
            _width = width;
            _height = height;
            _buffer = GC.AllocateUninitializedArray<byte>(width * height * 4, pinned: true);
            _bufPtr = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(
                ref MemoryMarshal.GetArrayDataReference(_buffer));
            _bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb, (IntPtr)_bufPtr);
        }
    }

    /// <summary>
    /// 解碼收到的 VideoFrame 封包，並回傳有變動的區域矩形。
    /// </summary>
    public Rectangle DecodeFrame(ReadOnlySpan<byte> packet, out uint sequence)
    {
        var r = new PacketReader(packet);
        sequence = r.U32();
        int frameW = r.I32();
        int frameH = r.I32();
        int tileCount = r.U16();

        Resize(frameW, frameH);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

        lock (_renderLock)
        {
            if (_bufPtr == null) return Rectangle.Empty;

            for (int i = 0; i < tileCount; i++)
            {
                int x = r.U16();
                int y = r.U16();
                int w = r.U16();
                int h = r.U16();
                var enc = (TileEncoding)r.U8();
                var data = r.Bytes();

                if (enc == TileEncoding.Jpeg)
                {
                    DecodeJpegTile(x, y, w, h, data);
                }
                else if (enc == TileEncoding.Deflate)
                {
                    DecodeDeflateTile(x, y, w, h, data);
                }

                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x + w > maxX) maxX = x + w;
                if (y + h > maxY) maxY = y + h;
            }
        }

        if (minX <= maxX && minY <= maxY)
        {
            return new Rectangle(minX, minY, maxX - minX, maxY - minY);
        }

        return Rectangle.Empty;
    }

    private void DecodeJpegTile(int x, int y, int w, int h, ReadOnlySpan<byte> data)
    {
        fixed (byte* pData = data)
        {
            using var ms = new UnmanagedMemoryStream(pData, data.Length);
            using var tileBmp = (Bitmap)Image.FromStream(ms);

            var bd = tileBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                byte* src = (byte*)bd.Scan0;
                int srcStride = bd.Stride;
                int dstStride = _width * 4;

                for (int row = 0; row < h; row++)
                {
                    byte* s = src + (long)row * srcStride;
                    byte* d = _bufPtr + (long)(y + row) * dstStride + (long)x * 4;
                    Buffer.MemoryCopy(s, d, w * 4, w * 4);
                }
            }
            finally
            {
                tileBmp.UnlockBits(bd);
            }
        }
    }

    private void DecodeDeflateTile(int x, int y, int w, int h, ReadOnlySpan<byte> data)
    {
        fixed (byte* pData = data)
        {
            using var ms = new UnmanagedMemoryStream(pData, data.Length);
            using var z = new ZLibStream(ms, CompressionMode.Decompress);

            int rowBytes = w * 3;
            var rowBuf = new byte[rowBytes];
            int dstStride = _width * 4;

            for (int row = 0; row < h; row++)
            {
                int read = 0;
                while (read < rowBytes)
                {
                    int n = z.Read(rowBuf, read, rowBytes - read);
                    if (n == 0) break;
                    read += n;
                }

                byte* d = _bufPtr + (long)(y + row) * dstStride + (long)x * 4;
                for (int col = 0, so = 0; col < w; col++, so += 3, d += 4)
                {
                    d[0] = rowBuf[so];     // B
                    d[1] = rowBuf[so + 1]; // G
                    d[2] = rowBuf[so + 2]; // R
                    d[3] = 255;            // A
                }
            }
        }
    }

    /// <summary>
    /// 在指定的 Graphics 上繪製目前畫面（可縮放與保持比例）。
    /// </summary>
    public void DrawTo(Graphics g, Rectangle destRect)
    {
        lock (_renderLock)
        {
            if (_bitmap != null)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(_bitmap, destRect, 0, 0, _width, _height, GraphicsUnit.Pixel);
            }
            else
            {
                g.Clear(Color.FromArgb(30, 30, 30));
            }
        }
    }

    /// <summary>
    /// 截取目前遠端桌面畫面作為縮圖（用於辨識裝置與最近連線記錄）。
    /// </summary>
    public Bitmap? CreateThumbnail(int thumbWidth = 240, int thumbHeight = 135)
    {
        lock (_renderLock)
        {
            if (_bitmap == null || _width <= 0 || _height <= 0) return null;
            var thumb = new Bitmap(thumbWidth, thumbHeight, PixelFormat.Format32bppRgb);
            using var g = Graphics.FromImage(thumb);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(_bitmap, new Rectangle(0, 0, thumbWidth, thumbHeight), 0, 0, _width, _height, GraphicsUnit.Pixel);
            return thumb;
        }
    }

    private void Cleanup()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _buffer = null;
        _bufPtr = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_renderLock)
        {
            Cleanup();
        }
    }
}
