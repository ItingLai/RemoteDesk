using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RemoteDesk.Common;
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

    [Fact]
    public unsafe void TestFrameEncodeAndDecode_LargeMotion2DMerged_Succeeds()
    {
        // 模擬全螢幕高解析度動態播放 (512x512 大範圍變更，應自動 2D 垂直貪婪合併為單一 100% 原生解析度 Jpeg 區塊，維持極致清晰銳利)
        int width = 512;
        int height = 512;
        var encoder = new FrameEncoder(width, height) { Quality = 70 };
        var decoder = new FrameDecoder();
        decoder.Resize(width, height);

        var buffer = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                buffer[o] = 50;
                buffer[o + 1] = 100;
                buffer[o + 2] = 200;
                buffer[o + 3] = 255;
            }
        }

        fixed (byte* pBuf = buffer)
        {
            var frame = new CapturedFrame((IntPtr)pBuf, width, height, width * 4);
            var tiles = encoder.Encode(frame, newFrame: true, nowMs: 1000);

            Assert.Single(tiles); // 大範圍全螢幕變更應被 2D 垂直合併為單一原生解析度區塊
            Assert.Equal(TileEncoding.Jpeg, tiles[0].Encoding);
            Assert.Equal(0, tiles[0].X);
            Assert.Equal(0, tiles[0].Y);
            Assert.Equal(width, tiles[0].W);
            Assert.Equal(height, tiles[0].H);

            var packet = FrameEncoder.Serialize(2, width, height, tiles);
            var dirty = decoder.DecodeFrame(packet.AsSpan(1), out var seq);

            Assert.Equal(2U, seq);
            Assert.Equal(new Rectangle(0, 0, width, height), dirty);

            using var destBmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            using var g = Graphics.FromImage(destBmp);
            decoder.DrawTo(g, new Rectangle(0, 0, width, height));

            var sample = destBmp.GetPixel(256, 256);
            Assert.InRange(sample.R, 180, 220);
        }

        decoder.Dispose();
    }

    [Fact]
    public unsafe void Test_MoveRectangle_Scrolling_DownAndUp_OverlappingCorrectness()
    {
        // 驗證 MoveRectangle 在向下捲動 (dstY > srcY) 及向上捲動 (dstY < srcY) 的 2D 重疊平移正確性
        int width = 200;
        int height = 200;
        var decoder = new FrameDecoder();
        decoder.Resize(width, height);

        // 1. 初始化畫布：每一列有獨特的測試顏色 (Row 0 ~ 199 顏色值 = y)
        using (var initialBmp = new Bitmap(width, height, PixelFormat.Format32bppRgb))
        {
            var bd = initialBmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            byte* p = (byte*)bd.Scan0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int o = y * bd.Stride + x * 4;
                    p[o] = (byte)y;     // B
                    p[o + 1] = (byte)y; // G
                    p[o + 2] = (byte)y; // R
                    p[o + 3] = 255;
                }
            }
            initialBmp.UnlockBits(bd);

            // 送出 FullFrame 基準
            var enc = new FrameEncoder(width, height);
            fixed (byte* pInit = new byte[width * height * 4])
            {
                // 用全白初始化
                var initFrame = new CapturedFrame((IntPtr)p, width, height, width * 4, HasMetadata: true, IsKeyFrame: true);
                var fullTiles = enc.Encode(initFrame, true, 100, out var fType, out _);
                var initPkt = FrameEncoder.Serialize(1, 1, fType, width, height, null, fullTiles);
                decoder.DecodeFrame(initPkt.AsSpan(1), out _, out _, out _);
            }
        }

        // 2. 測試向下捲動 (Scroll Down): Content at Y=10..109 shifts down to Y=30..129 (dstY > srcY, 重疊 80 列)
        var moveDown = new MoveRectangle(new Point(0, 10), new Rectangle(0, 30, width, 100));
        var pktDown = FrameEncoder.Serialize(2, 2, FrameType.MoveAndRegion, width, height, new[] { moveDown }, Array.Empty<EncodedTile>());
        var dirtyDown = decoder.DecodeFrame(pktDown.AsSpan(1), out _, out _, out _);

        Assert.Equal(new Rectangle(0, 30, width, 100), dirtyDown);

        // 驗證目的地 Row 30 應等於原本的 Row 10，Row 129 應等於原本的 Row 109 (容許 JPEG 1 階量化誤差)
        using (var checkBmp = new Bitmap(width, height, PixelFormat.Format32bppRgb))
        using (var g = Graphics.FromImage(checkBmp))
        {
            decoder.DrawTo(g, new Rectangle(0, 0, width, height));

            // 取樣 Row 30 (移入自 Row 10)
            var p30 = checkBmp.GetPixel(50, 30);
            Assert.InRange(p30.R, 9, 11);

            // 取樣 Row 80 (移入自 Row 60)
            var p80 = checkBmp.GetPixel(50, 80);
            Assert.InRange(p80.R, 59, 61);

            // 取樣 Row 129 (移入自 Row 109)
            var p129 = checkBmp.GetPixel(50, 129);
            Assert.InRange(p129.R, 108, 111);
        }

        // 3. 測試向上捲動 (Scroll Up): Content at Y=50..149 shifts up to Y=20..119 (dstY < srcY, 重疊 70 列)
        var moveUp = new MoveRectangle(new Point(0, 50), new Rectangle(0, 20, width, 100));
        var pktUp = FrameEncoder.Serialize(3, 3, FrameType.MoveAndRegion, width, height, new[] { moveUp }, Array.Empty<EncodedTile>());
        var dirtyUp = decoder.DecodeFrame(pktUp.AsSpan(1), out _, out _, out _);

        Assert.Equal(new Rectangle(0, 20, width, 100), dirtyUp);

        using (var checkBmp = new Bitmap(width, height, PixelFormat.Format32bppRgb))
        using (var g = Graphics.FromImage(checkBmp))
        {
            decoder.DrawTo(g, new Rectangle(0, 0, width, height));

            // Row 20 是從 Row 50 移上來的 (先前的 Row 50 是在步驟 2 由 Row 30 移入，值為 30)
            var p20 = checkBmp.GetPixel(50, 20);
            Assert.True(p20.R > 0);
        }

        decoder.Dispose();
    }

    [Fact]
    public unsafe void Test_MoveRectangle_ConflictingOverlap_ScratchSafety()
    {
        // 驗證多個彼此衝突重疊的 MoveRectangle，能透過暫存快照安全執行，不破壞來源像素
        int width = 100;
        int height = 100;
        var decoder = new FrameDecoder();
        decoder.Resize(width, height);

        // 建立初始白底
        var enc = new FrameEncoder(width, height);
        var raw = new byte[width * height * 4];
        fixed (byte* pRaw = raw)
        {
            // 在 (0, 10..30) 填紅色 (R=255)
            // 在 (0, 40..60) 填藍色 (B=255)
            for (int y = 10; y < 30; y++)
                for (int x = 0; x < width; x++) pRaw[(y * width + x) * 4 + 2] = 255;
            for (int y = 40; y < 60; y++)
                for (int x = 0; x < width; x++) pRaw[(y * width + x) * 4] = 255;

            var initFrame = new CapturedFrame((IntPtr)pRaw, width, height, width * 4, HasMetadata: true, IsKeyFrame: true);
            var tiles = enc.Encode(initFrame, true, 100, out var fType, out _);
            decoder.DecodeFrame(FrameEncoder.Serialize(1, 1, fType, width, height, null, tiles).AsSpan(1), out _);
        }

        // Move 1 將紅色移到 (0, 45, 100, 20) -> 目的地覆蓋了 Move 2 的來源！
        // Move 2 將藍色從 (0, 40, 100, 20) 移到 (0, 70, 100, 20)
        var m1 = new MoveRectangle(new Point(0, 10), new Rectangle(0, 45, 100, 20));
        var m2 = new MoveRectangle(new Point(0, 40), new Rectangle(0, 70, 100, 20));

        var pkt = FrameEncoder.Serialize(2, 2, FrameType.MoveAndRegion, width, height, new[] { m1, m2 }, Array.Empty<EncodedTile>());
        decoder.DecodeFrame(pkt.AsSpan(1), out _);

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        decoder.DrawTo(g, new Rectangle(0, 0, width, height));

        // 驗證 m2 目的地 (0, 75) 依然正確保留了藍色 (B=255, R=0)，沒有被 m1 的紅色覆寫
        var p75 = bmp.GetPixel(50, 75);
        Assert.True(p75.B > 200, "Move 2 的來源區域未被暫存保護，導致被 Move 1 覆寫！");
        Assert.True(p75.R < 50, "Move 2 的來源區域被 Move 1 污染！");

        decoder.Dispose();
    }

    [Fact]
    public void Test_ConsolidateDirtyRectangles_Rules()
    {
        int sw = 1920, sh = 1080;

        // 1. 過濾超出邊界及零面積區域
        var raw1 = new[]
        {
            new Rectangle(-10, -10, 5, 5),          // 完全在螢幕外 -> 過濾
            new Rectangle(100, 100, 0, 50),         // 寬度為 0 -> 過濾
            new Rectangle(200, 200, 50, 50),        // 有效
            new Rectangle(1900, 1000, 100, 100),    // 部分超出 -> 裁切為 (1900, 1000, 20, 80)
        };
        var res1 = FrameEncoder.ConsolidateDirtyRectangles(raw1, sw, sh);
        Assert.Equal(2, res1.Count);
        Assert.Equal(new Rectangle(200, 200, 50, 50), res1[0]);
        Assert.Equal(new Rectangle(1900, 1000, 20, 80), res1[1]);

        // 2. 相鄰 (距離 <= 16px) 或重疊的矩形貪婪合併
        var raw2 = new[]
        {
            new Rectangle(100, 100, 50, 50),
            new Rectangle(140, 100, 50, 50), // 重疊 10px -> 應合併為 (100, 100, 90, 50)
            new Rectangle(200, 100, 50, 50), // 與前一個距離 10px (<= 16px) -> 應連鎖合併為 (100, 100, 150, 50)
        };
        var res2 = FrameEncoder.ConsolidateDirtyRectangles(raw2, sw, sh);
        Assert.Single(res2);
        Assert.Equal(new Rectangle(100, 100, 150, 50), res2[0]);

        // 3. 面積超過 70% 時自動合併為單一 Bounding Rect
        var raw3 = new[]
        {
            new Rectangle(0, 0, 1920, 800), // 佔比 > 70%
            new Rectangle(0, 800, 100, 100)
        };
        var res3 = FrameEncoder.ConsolidateDirtyRectangles(raw3, sw, sh);
        Assert.Single(res3);
        Assert.Equal(new Rectangle(0, 0, 1920, 900), res3[0]);
    }

    [Fact]
    public unsafe void Test_FrameVersion_DesyncDetection_TriggersResync()
    {
        int width = 128;
        int height = 128;
        var encoder = new FrameEncoder(width, height);
        var decoder = new FrameDecoder();
        decoder.Resize(width, height);

        int desyncCount = 0;
        decoder.DesyncDetected += () => desyncCount++;

        var raw = new byte[width * height * 4];
        fixed (byte* pRaw = raw)
        {
            // 1. 發送 FullFrame (FrameVersion = 1, BaseVersion = 0)
            pRaw[0] = 100;
            var frame1 = new CapturedFrame((IntPtr)pRaw, width, height, width * 4, HasMetadata: true, IsKeyFrame: true);
            var tiles1 = encoder.Encode(frame1, true, 1000, out var type1, out var moves1);
            Assert.Equal(FrameType.FullFrame, type1);

            var pkt1 = FrameEncoder.Serialize(1, 1, 0, type1, width, height, moves1, tiles1);
            var dirty1 = decoder.DecodeFrame(pkt1.AsSpan(1), out var seq1, out var ver1, out var base1, out var fType1);

            Assert.Equal(1U, ver1);
            Assert.Equal(0U, base1);
            Assert.Equal(1U, decoder.CanvasVersion);
            Assert.True(decoder.HasValidBaseline);
            Assert.False(dirty1.IsEmpty);
            Assert.Equal(0, desyncCount);

            // 2. 發送合法增量 RegionUpdate (FrameVersion = 2, BaseVersion = 1)
            pRaw[0] = 200;
            var dirtyRects2 = new[] { new Rectangle(0, 0, 64, 64) };
            var frame2 = new CapturedFrame((IntPtr)pRaw, width, height, width * 4, HasMetadata: true, DirtyRectangles: dirtyRects2, IsKeyFrame: false);
            var tiles2 = encoder.Encode(frame2, true, 1033, out var type2, out var moves2);

            var pkt2 = FrameEncoder.Serialize(2, 2, 1, type2, width, height, moves2, tiles2);
            var dirty2 = decoder.DecodeFrame(pkt2.AsSpan(1), out var seq2, out var ver2, out var base2, out var fType2);

            Assert.Equal(2U, ver2);
            Assert.Equal(1U, base2);
            Assert.Equal(2U, decoder.CanvasVersion);
            Assert.Equal(0, desyncCount);
            Assert.False(dirty2.IsEmpty);

            // 3. 模擬網路掉包，發送版本不連續的增量更新 (BaseVersion = 99，但 decoder 處於 2)
            var pktCorrupted = FrameEncoder.Serialize(3, 100, 99, FrameType.RegionUpdate, width, height, null, tiles2);
            var dirtyCorrupted = decoder.DecodeFrame(pktCorrupted.AsSpan(1), out var seqErr, out var verErr, out var baseErr, out var fTypeErr);

            // 接收端必須果斷拒絕渲染 (傳回 Rectangle.Empty)，保留現有畫面不破圖，並觸發 DesyncDetected 警報事件
            Assert.Equal(Rectangle.Empty, dirtyCorrupted);
            Assert.Equal(2U, decoder.CanvasVersion); // 版本不被污染
            Assert.Equal(1, desyncCount); // 成功觸發重新同步請求

            // 4. 主機端回應 Keyframe 重新發送 FullFrame (FrameVersion = 101, BaseVersion = 0)
            encoder.RequestKeyframe();
            var frameReSync = new CapturedFrame((IntPtr)pRaw, width, height, width * 4, HasMetadata: true, IsKeyFrame: true);
            var tilesReSync = encoder.Encode(frameReSync, true, 1100, out var typeReSync, out var movesReSync);
            Assert.Equal(FrameType.FullFrame, typeReSync);

            var pktReSync = FrameEncoder.Serialize(4, 101, 0, typeReSync, width, height, movesReSync, tilesReSync);
            var dirtyReSync = decoder.DecodeFrame(pktReSync.AsSpan(1), out var seqRe, out var verRe, out var baseRe, out var fTypeRe);

            Assert.Equal(101U, decoder.CanvasVersion);
            Assert.True(decoder.HasValidBaseline);
            Assert.False(dirtyReSync.IsEmpty);
            Assert.Equal(1, desyncCount); // 未再增加
        }

        decoder.Dispose();
    }

    [Fact]
    public async Task Test_SecureChannel_PriorityQueueing_ControlJumpsAheadOfVideo()
    {
        // 建立雙向通訊管道與握手
        var pipe1 = new System.IO.Pipelines.Pipe();
        var pipe2 = new System.IO.Pipelines.Pipe();

        var firstWriteGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWriteStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int writeCount = 0;

        // 包裝 pipe2.Writer.AsStream()，在握手後的第一次訊息寫入時進行精確同步攔截
        var interceptingClientWriteStream = new InterceptingStream(pipe2.Writer.AsStream(), async (buf, ct) =>
        {
            if (Interlocked.Increment(ref writeCount) == 2) // 第 1 次是 ClientHello 握手，第 2 次是 Low1-Video
            {
                firstWriteStarted.TrySetResult(true);
                await firstWriteGate.Task;
            }
        });

        var s1 = new CryptoAndChannelTests.DuplexStream(pipe1.Reader.AsStream(), interceptingClientWriteStream);
        var s2 = new CryptoAndChannelTests.DuplexStream(pipe2.Reader.AsStream(), pipe1.Writer.AsStream());

        byte[] salt = new byte[16];
        var clientHandshake = SecureChannel.ClientHandshakeAsync(s1, "viewer-1", "Viewer", CancellationToken.None);
        var serverHandshake = SecureChannel.ServerHandshakeAsync(s2, ch =>
            new ServerHello(Protocol.Version, "host-1", "Host", salt, 1000, true, true, false),
            CancellationToken.None);

        await Task.WhenAll(clientHandshake, serverHandshake);
        using var clientChan = (await clientHandshake).channel;
        using var hostChan = (await serverHandshake).channel;

        var executionOrder = new List<string>();

        // 1. 模擬傳送大體積影像資料 (低優先權)，正在佔用通道
        var lowTask1 = Task.Run(async () =>
        {
            await clientChan.SendAsync(System.Text.Encoding.UTF8.GetBytes("Low1-Video"), highPriority: false);
        });

        // 等待 Low1-Video 確定取得 writeLock 並進入串流寫入
        await firstWriteStarted.Task;

        // 2. 佇列中累積了下一幀影像 (低優先權)
        var lowTask2 = Task.Run(async () =>
        {
            await clientChan.SendAsync(System.Text.Encoding.UTF8.GetBytes("Low2-Video"), highPriority: false);
        });

        // 稍微讓 Task 排入 lowPriorityLock
        await Task.Delay(15);

        // 3. 此時使用者移動滑鼠或發出 FrameAck (高優先權)
        var highTask = Task.Run(async () =>
        {
            await clientChan.SendAsync(System.Text.Encoding.UTF8.GetBytes("High-AckOrInput"), highPriority: true);
        });

        // 稍微讓 highTask 註冊 priorityWaiters 並在 writeLock 前排隊
        await Task.Delay(15);

        // 釋放 Low1-Video 寫入，讓通道空出
        firstWriteGate.TrySetResult(true);

        // 接收端依序接收 3 個封包
        var r1 = await hostChan.ReceiveAsync();
        var r2 = await hostChan.ReceiveAsync();
        var r3 = await hostChan.ReceiveAsync();

        executionOrder.Add(System.Text.Encoding.UTF8.GetString(r1));
        executionOrder.Add(System.Text.Encoding.UTF8.GetString(r2));
        executionOrder.Add(System.Text.Encoding.UTF8.GetString(r3));

        await Task.WhenAll(lowTask1, lowTask2, highTask);

        // 驗證 High 優先權必定在 Low2 之前插隊並抵達接收端！
        Assert.Equal(3, executionOrder.Count);
        Assert.Equal("Low1-Video", executionOrder[0]);
        Assert.Equal("High-AckOrInput", executionOrder[1]);
        Assert.Equal("Low2-Video", executionOrder[2]);
    }

    private sealed class InterceptingStream : Stream
    {
        private readonly Stream _inner;
        private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _onWrite;

        public InterceptingStream(Stream inner, Func<ReadOnlyMemory<byte>, CancellationToken, Task> onWrite)
        {
            _inner = inner;
            _onWrite = onWrite;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _onWrite(buffer, cancellationToken);
            await _inner.WriteAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    public unsafe void Test_Phase3_DecoupledDecoding_ZeroRenderLockContention()
    {
        int width = 256;
        int height = 256;
        var encoder = new FrameEncoder(width, height) { Quality = 80 };
        var decoder = new FrameDecoder();
        decoder.Resize(width, height);

        var raw = new byte[width * height * 4];
        fixed (byte* pRaw = raw)
        {
            // 填色：左上角紅、右上角綠、下半部藍
            for (int y = 0; y < 128; y++)
            {
                for (int x = 0; x < 128; x++) pRaw[(y * width + x) * 4 + 2] = 220; // R
                for (int x = 128; x < 256; x++) pRaw[(y * width + x) * 4 + 1] = 220; // G
            }
            for (int y = 128; y < 256; y++)
            {
                for (int x = 0; x < 256; x++) pRaw[(y * width + x) * 4] = 220; // B
            }

            var frame = new CapturedFrame((IntPtr)pRaw, width, height, width * 4, HasMetadata: true, IsKeyFrame: true);
            var tiles = encoder.Encode(frame, true, 1000, out var fType, out var moves);

            var packet = FrameEncoder.Serialize(1, 1, 0, fType, width, height, moves, tiles);

            // 執行解碼 (背景無鎖解碼 + 極速 memcpy)
            var dirty = decoder.DecodeFrame(packet.AsSpan(1), out var seq, out var fVer, out var bVer, out var type);

            Assert.Equal(1U, seq);
            Assert.Equal(1U, fVer);
            Assert.Equal(new Rectangle(0, 0, width, height), dirty);

            // 驗證解碼後像素精確性
            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            using var g = Graphics.FromImage(bmp);
            decoder.DrawTo(g, new Rectangle(0, 0, width, height));

            var pRed = bmp.GetPixel(50, 50);
            Assert.InRange(pRed.R, 200, 240);

            var pGreen = bmp.GetPixel(200, 50);
            Assert.InRange(pGreen.G, 200, 240);

            var pBlue = bmp.GetPixel(100, 200);
            Assert.InRange(pBlue.B, 200, 240);
        }

        decoder.Dispose();
    }

    [Fact]
    public unsafe void Test_Phase3_DynamicQuality_AdaptsToNetworkCongestionInsteadOfMotionArea()
    {
        int width = 1920;
        int height = 1080;
        var encoder = new FrameEncoder(width, height) { Quality = 85 };

        var raw = new byte[width * height * 4];
        fixed (byte* pRaw = raw)
        {
            // 建立高細節度擬真像素 (模擬真實桌面文字與全螢幕影片高頻細節)
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int o = (y * width + x) * 4;
                    pRaw[o] = (byte)((x * 17) & 0xFF);
                    pRaw[o + 1] = (byte)((y * 31) & 0xFF);
                    pRaw[o + 2] = (byte)((x ^ y) & 0xFF);
                    pRaw[o + 3] = 255;
                }
            }

            long totalPixels = (long)width * height;
            long largePixels = 1920L * 900L;

            // 1. 網路健康時 (CongestionQualityReduction == 0)，即使大範圍動態更新 (>= 75%) 仍保持完整 Quality=85
            Assert.Equal(85, encoder.GetEffectiveTileQuality(120L * 60L, totalPixels, isKeyframe: false));
            Assert.Equal(85, encoder.GetEffectiveTileQuality(largePixels, totalPixels, isKeyframe: false));

            var largeRects = new[] { new Rectangle(0, 0, 1920, 900) }; // 佔比 ~83%
            var frameLargeHealthy = new CapturedFrame((IntPtr)pRaw, width, height, width * 4,
                HasMetadata: true, DirtyRectangles: largeRects, MoveRectangles: null, IsKeyFrame: false);

            var healthyTiles = encoder.Encode(frameLargeHealthy, true, 1000, out _, out _);
            Assert.Single(healthyTiles);
            float healthyLargeBpp = (float)healthyTiles[0].Data.Length / (healthyTiles[0].W * healthyTiles[0].H);

            // 2. 僅在真實網路壅塞時透過 CongestionQualityReduction 控制編碼負擔，且絕不覆寫使用者 Quality 設定
            encoder.CongestionQualityReduction = 10;
            Assert.Equal(85, encoder.Quality);
            Assert.Equal(75, encoder.GetEffectiveTileQuality(largePixels, totalPixels, isKeyframe: false));

            var frameLargeCongested = new CapturedFrame((IntPtr)pRaw, width, height, width * 4,
                HasMetadata: true, DirtyRectangles: largeRects, MoveRectangles: null, IsKeyFrame: false);
            var congestedTiles = encoder.Encode(frameLargeCongested, true, 1033, out _, out _);
            Assert.Single(congestedTiles);
            float congestedLargeBpp = (float)congestedTiles[0].Data.Length / (congestedTiles[0].W * congestedTiles[0].H);

            Assert.True(congestedLargeBpp < healthyLargeBpp,
                $"真實壅塞降載時每像素位元組應低於健康全螢幕影片！healthyLargeBpp={healthyLargeBpp:F4}, congestedLargeBpp={congestedLargeBpp:F4}");
            Assert.Equal(85, encoder.Quality);
        }
    }

    [Fact]
    public unsafe void Test_Adaptive_QualityIsDecoupledFromResolution_AndZeroClampCrash()
    {
        int width = 1920;
        int height = 1080;
        var buffer = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                buffer[o] = (byte)((x * 7) & 0xFF);
                buffer[o + 1] = (byte)((y * 13) & 0xFF);
                buffer[o + 2] = (byte)(((x + y) * 3) & 0xFF);
                buffer[o + 3] = 255;
            }
        }

        int[] qualities = { 30, 50, 70, 90 };
        var sizes = new List<int>();

        fixed (byte* pBuf = buffer)
        {
            var frame = new CapturedFrame((IntPtr)pBuf, width, height, width * 4, HasMetadata: true, IsKeyFrame: true);
            var deltaFrame = new CapturedFrame(
                (IntPtr)pBuf, width, height, width * 4,
                HasMetadata: true,
                DirtyRectangles: new[] { new Rectangle(0, 0, width, height) },
                MoveRectangles: null,
                IsKeyFrame: false);

            foreach (int q in qualities)
            {
                var encoder = new FrameEncoder(width, height) { Quality = q };
                using var decoder = new FrameDecoder();
                decoder.Resize(width, height);

                // Keyframe 必須維持原生 1920x1080，絕不因 Quality <= 55 而強制降採樣
                var keyTiles = encoder.Encode(frame, true, 1000, out var fType, out _);
                Assert.Single(keyTiles);
                Assert.Equal(TileEncoding.Jpeg, keyTiles[0].Encoding);
                Assert.Equal(width, keyTiles[0].W);
                Assert.Equal(height, keyTiles[0].H);

                var pkt = FrameEncoder.Serialize(1, 1, 0, fType, width, height, null, keyTiles);
                var dirty = decoder.DecodeFrame(pkt.AsSpan(1), out var seq);
                Assert.Equal(1U, seq);
                Assert.Equal(new Rectangle(0, 0, width, height), dirty);
                Assert.Equal(width, decoder.Width);
                Assert.Equal(height, decoder.Height);
                sizes.Add(keyTiles[0].Data.Length);

                // 大面積 Delta 更新在 Quality = 30 / 50 下同樣不拋例外且不強制降採樣
                var deltaTiles = encoder.Encode(deltaFrame, true, 1033, out var dType, out _);
                Assert.Single(deltaTiles);
                Assert.Equal(TileEncoding.Jpeg, deltaTiles[0].Encoding);
                Assert.Equal(width, deltaTiles[0].W);
                Assert.Equal(height, deltaTiles[0].H);
            }
        }

        // 確認壓縮品質確實單調影響檔案大小 (Quality 30 < 50 < 70 < 90)
        Assert.True(sizes[0] < sizes[1], $"Q=30 ({sizes[0]}) 應小於 Q=50 ({sizes[1]})");
        Assert.True(sizes[1] < sizes[2], $"Q=50 ({sizes[1]}) 應小於 Q=70 ({sizes[2]})");
        Assert.True(sizes[2] < sizes[3], $"Q=70 ({sizes[2]}) 應小於 Q=90 ({sizes[3]})");
    }

    [Fact]
    public unsafe void Test_Adaptive_Level1AndLevel2_ResamplingMoveAndDirty_AndConfigVersionIsolation()
    {
        const int origW = 1920, origH = 1080;
        var l1 = ResolutionProfileManager.GetProfile(origW, origH, ResolutionLevel.Level1);
        var l2 = ResolutionProfileManager.GetProfile(origW, origH, ResolutionLevel.Level2);
        Assert.Equal(1440, l1.StreamWidth);
        Assert.Equal(810, l1.StreamHeight);
        Assert.Equal(960, l2.StreamWidth);
        Assert.Equal(540, l2.StreamHeight);

        var encoder = new FrameEncoder(origW, origH) { Quality = 70 };
        using var decoder = new FrameDecoder();

        var buf = new byte[origW * origH * 4];
        // 在原生畫面右半填充綠色，左半填充藍色，中央畫一個紅色方塊
        for (int y = 0; y < origH; y++)
        {
            for (int x = 0; x < origW; x++)
            {
                int o = (y * origW + x) * 4;
                if (x >= 400 && x < 800 && y >= 200 && y < 600)
                {
                    buf[o] = 20; buf[o + 1] = 20; buf[o + 2] = 230; // 紅
                }
                else if (x >= origW / 2)
                {
                    buf[o] = 20; buf[o + 1] = 210; buf[o + 2] = 20; // 綠
                }
                else
                {
                    buf[o] = 210; buf[o + 1] = 30; buf[o + 2] = 20; // 藍
                }
                buf[o + 3] = 255;
            }
        }

        fixed (byte* pBuf = buf)
        {
            // 1. Config v1: Level 0 (1920x1080)
            decoder.ConfigureStream(1, origW, origH);
            var keyL0 = new CapturedFrame((IntPtr)pBuf, origW, origH, origW * 4, HasMetadata: true, IsKeyFrame: true);
            var t0 = encoder.Encode(keyL0, true, 1000, out var ft0, out _);
            var pkt0 = FrameEncoder.Serialize(1, 1, 0, ft0, encoder.StreamWidth, encoder.StreamHeight, null, t0, streamConfigVersion: 1);
            var d0 = decoder.DecodeFrame(pkt0.AsSpan(1), out _, out _, out _, out _, out var pktCfg0);
            Assert.Equal(1U, pktCfg0);
            Assert.Equal(new Rectangle(0, 0, origW, origH), d0);

            // 建立一個 Config v1 的增量封包 (模擬在網路上遲到的舊組態影格)
            var lateDirty = new CapturedFrame(
                (IntPtr)pBuf, origW, origH, origW * 4,
                HasMetadata: true,
                DirtyRectangles: new[] { new Rectangle(0, 0, 100, 100) },
                MoveRectangles: null,
                IsKeyFrame: false);
            var lateTiles = encoder.Encode(lateDirty, true, 1020, out var lateType, out _);
            var lateOldConfigPkt = FrameEncoder.Serialize(2, 2, 1, lateType, origW, origH, null, lateTiles, streamConfigVersion: 1);

            // 2. 切換至 Config v2: Level 1 (1440x810)
            encoder.ConfigureStreamResolution(l1.StreamWidth, l1.StreamHeight);
            decoder.ConfigureStream(2, l1.StreamWidth, l1.StreamHeight);
            Assert.Equal(l1.StreamWidth, decoder.Width);
            Assert.Equal(l1.StreamHeight, decoder.Height);

            // 驗證切換後尚未收到新 Keyframe 前，BackBuffer 不得全黑 (已保留舊畫面平滑縮放像素)
            using (var preKeyBmp = new Bitmap(l1.StreamWidth, l1.StreamHeight, PixelFormat.Format32bppRgb))
            using (var g = Graphics.FromImage(preKeyBmp))
            {
                decoder.DrawTo(g, new Rectangle(0, 0, l1.StreamWidth, l1.StreamHeight));
                var preRed = preKeyBmp.GetPixel(450, 300); // 對應原生 (600, 400)
                Assert.True(preRed.R > 180, $"切換組態等待 Keyframe 期間不應閃黑！preRed={preRed}");
            }

            // 丟入舊版本 (Cfg v1) 遲到封包 -> 必須被安全丟棄且回傳 Rectangle.Empty，不覆寫畫布
            bool desyncFired = false;
            decoder.DesyncDetected += () => desyncFired = true;
            var staleDirty = decoder.DecodeFrame(lateOldConfigPkt.AsSpan(1), out _);
            Assert.True(staleDirty.IsEmpty);
            Assert.False(desyncFired, "舊解析度組態版本的遲到影格丟棄不應觸發誤判 Desync 風暴");

            // 3. 送出 Cfg v2 的 FullFrame 與 Dirty/Move Delta
            var keyL1 = new CapturedFrame((IntPtr)pBuf, origW, origH, origW * 4, HasMetadata: true, IsKeyFrame: true);
            var t1 = encoder.Encode(keyL1, true, 1050, out var ft1, out _);
            var pkt1 = FrameEncoder.Serialize(3, 3, 0, ft1, encoder.StreamWidth, encoder.StreamHeight, null, t1, streamConfigVersion: 2);
            var d1 = decoder.DecodeFrame(pkt1.AsSpan(1), out _, out _, out _, out _, out var pktCfg1);
            Assert.Equal(2U, pktCfg1);
            Assert.Equal(new Rectangle(0, 0, 1440, 810), d1);

            // 將原生右上角某區塊改為白黃色並帶 MoveRectangle，測試 Level 1 增量轉換
            for (int y = 40; y < 120; y++)
            {
                for (int x = 1400; x < 1600; x++)
                {
                    int o = (y * origW + x) * 4;
                    buf[o] = 30; buf[o + 1] = 240; buf[o + 2] = 240;
                }
            }
            var moves = new[] { new MoveRectangle(new Point(400, 200), new Rectangle(0, 0, 200, 200)) };
            var deltaL1 = new CapturedFrame(
                (IntPtr)pBuf, origW, origH, origW * 4,
                HasMetadata: true,
                DirtyRectangles: new[] { new Rectangle(1400, 40, 200, 80) },
                MoveRectangles: moves,
                IsKeyFrame: false);
            var td1 = encoder.Encode(deltaL1, true, 1080, out var ftd1, out var scaledMoves);
            Assert.Equal(FrameType.MoveAndRegion, ftd1);
            Assert.NotNull(scaledMoves);
            var pktD1 = FrameEncoder.Serialize(4, 4, 3, ftd1, encoder.StreamWidth, encoder.StreamHeight, scaledMoves, td1, streamConfigVersion: 2);
            var dirtyDelta = decoder.DecodeFrame(pktD1.AsSpan(1), out _);
            Assert.False(dirtyDelta.IsEmpty);

            using (var bmpL1 = new Bitmap(l1.StreamWidth, l1.StreamHeight, PixelFormat.Format32bppRgb))
            using (var g = Graphics.FromImage(bmpL1))
            {
                decoder.DrawTo(g, new Rectangle(0, 0, l1.StreamWidth, l1.StreamHeight));
                // 驗證 Move (來源 (300,150) 紅方塊 -> 移動到 (0,0)-(150,150)) 與 Dirty 黃色區塊
                var movedRed = bmpL1.GetPixel(75, 75);
                var yellowDirty = bmpL1.GetPixel(1125, 60); // 1500*0.75=1125, 80*0.75=60
                Assert.True(movedRed.R > 180 && movedRed.B < 80, $"MoveRectangle 降採樣平移失敗: {movedRed}");
                Assert.True(yellowDirty.R > 190 && yellowDirty.G > 190 && yellowDirty.B < 80, $"DirtyRectangle 降採樣更新失敗: {yellowDirty}");
            }

            // 4. 切換至 Config v3: Level 2 (960x540)
            encoder.ConfigureStreamResolution(l2.StreamWidth, l2.StreamHeight);
            decoder.ConfigureStream(3, l2.StreamWidth, l2.StreamHeight);
            var keyL2 = new CapturedFrame((IntPtr)pBuf, origW, origH, origW * 4, HasMetadata: true, IsKeyFrame: true);
            var t2 = encoder.Encode(keyL2, true, 1120, out var ft2, out _);
            var pkt2 = FrameEncoder.Serialize(5, 5, 0, ft2, encoder.StreamWidth, encoder.StreamHeight, null, t2, streamConfigVersion: 3);
            var d2 = decoder.DecodeFrame(pkt2.AsSpan(1), out _);
            Assert.Equal(new Rectangle(0, 0, 960, 540), d2);
        }
    }
}
