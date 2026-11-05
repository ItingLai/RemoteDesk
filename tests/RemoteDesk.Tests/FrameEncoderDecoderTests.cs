using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RemoteDesk.Host;
using RemoteDesk.Viewer;
using Xunit;

namespace RemoteDesk.Tests;

public class FrameEncoderDecoderTests
{
    [Fact]
    public unsafe void TestFrameEncodeAndDecode()
    {
        int width = 128;
        int height = 128;
        var encoder = new FrameEncoder(width, height)
        {
            Quality = 90,
            Lossless = true
        };

        var decoder = new FrameDecoder();
        decoder.Resize(width, height);

        // 產生一張測試點陣圖 (填入特定顏色與漸層)
        var buffer = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                buffer[o] = (byte)(x * 2);      // B
                buffer[o + 1] = (byte)(y * 2);  // G
                buffer[o + 2] = 200;            // R
                buffer[o + 3] = 255;            // A
            }
        }

        fixed (byte* pBuf = buffer)
        {
            var frame = new CapturedFrame((IntPtr)pBuf, width, height, width * 4);
            var tiles = encoder.Encode(frame, newFrame: true, nowMs: 1000);

            Assert.NotEmpty(tiles);

            var packet = FrameEncoder.Serialize(1, width, height, tiles);
            Assert.True(packet.Length > 0);

            // 控制端解碼
            var dirty = decoder.DecodeFrame(packet.AsSpan(1), out var seq);
            Assert.Equal(1U, seq);
            Assert.False(dirty.IsEmpty);
            Assert.Equal(width, decoder.Width);
            Assert.Equal(height, decoder.Height);

            // 驗證解碼後繪製至 Bitmap 沒有例外
            using var destBmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            using var g = Graphics.FromImage(destBmp);
            decoder.DrawTo(g, new Rectangle(0, 0, width, height));

            // 取樣檢查中間像素值 (容許 JPEG 微幅有損誤差)
            var sampleColor = destBmp.GetPixel(64, 64);
            Assert.InRange(sampleColor.R, 180, 220);
        }

        decoder.Dispose();
    }
}
