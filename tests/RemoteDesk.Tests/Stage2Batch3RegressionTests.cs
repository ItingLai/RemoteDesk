using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows.Forms;
using RemoteDesk.Common;
using RemoteDesk.Host;
using RemoteDesk.Net;
using RemoteDesk.Viewer;
using Xunit;
using Xunit.Abstractions;

namespace RemoteDesk.Tests;

public class Stage2Batch3RegressionTests
{
    private readonly ITestOutputHelper _output;

    public Stage2Batch3RegressionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // -------------------------------------------------------------------------
    // Local Test Helpers
    // -------------------------------------------------------------------------

    private sealed class NullHostUi : IHostUi
    {
        public Task<bool> ConfirmAccessAsync(string viewerName, string viewerId, string address, string code, CancellationToken ct) =>
            Task.FromResult(true);
        public void SessionStarted(HostSession session) { }
        public void SessionEnded(HostSession session) { }
        public void SetClipboardText(string text) { }
    }

    private sealed class SyntheticCapturer : IScreenCapturer
    {
        private readonly byte[] _frame;
        private readonly GCHandle _pin;
        private readonly IntPtr _ptr;
        private int _pendingDirty = 1;
        private Rectangle _dirtyRect;
        private bool _first = true;

        public string Name => "Batch3MockCapturer";
        public int Width { get; }
        public int Height { get; }
        public CapturedFrame Frame { get; private set; }

        public SyntheticCapturer(int w, int h, Color fill)
        {
            Width = w;
            Height = h;
            _frame = new byte[w * h * 4];
            _pin = GCHandle.Alloc(_frame, GCHandleType.Pinned);
            _ptr = _pin.AddrOfPinnedObject();
            _dirtyRect = new Rectangle(0, 0, w, h);
            FillSolid(fill);
        }

        public void FillSolid(Color c)
        {
            for (int i = 0; i < _frame.Length; i += 4)
            {
                _frame[i] = c.B;
                _frame[i + 1] = c.G;
                _frame[i + 2] = c.R;
                _frame[i + 3] = 255;
            }
            _dirtyRect = new Rectangle(0, 0, Width, Height);
            _pendingDirty = 1;
        }

        public void PaintRect(Rectangle rect, Color c)
        {
            int x0 = Math.Max(0, rect.X);
            int y0 = Math.Max(0, rect.Y);
            int x1 = Math.Min(Width, rect.Right);
            int y1 = Math.Min(Height, rect.Bottom);
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    int off = (y * Width + x) * 4;
                    _frame[off] = c.B;
                    _frame[off + 1] = c.G;
                    _frame[off + 2] = c.R;
                    _frame[off + 3] = 255;
                }
            }
            _dirtyRect = new Rectangle(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
            _pendingDirty = 1;
        }

        public bool Capture(int timeoutMs)
        {
            if (Interlocked.Exchange(ref _pendingDirty, 0) == 0)
            {
                if (timeoutMs > 0) Thread.Sleep(Math.Min(timeoutMs, 8));
                return false;
            }
            bool isKey = _first;
            _first = false;
            Frame = new CapturedFrame(
                _ptr,
                Width,
                Height,
                Width * 4,
                HasMetadata: true,
                DirtyRectangles: new[] { isKey ? new Rectangle(0, 0, Width, Height) : _dirtyRect },
                MoveRectangles: null,
                IsKeyFrame: isKey);
            return true;
        }

        public void Dispose()
        {
            if (_pin.IsAllocated) _pin.Free();
        }
    }

    /// <summary>可程式化延遲/限速 Stream 包裝：以有序非阻塞管線精確模擬網路傳播延遲 (Propagation Delay) 與頻寬序列排隊，不人為鎖死發送迴圈。</summary>
    private sealed class DelayedThrottledStream : Stream
    {
        private readonly Stream _inner;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly Channel<(byte[] Data, long DeliverAtMs)> _pipe = Channel.CreateUnbounded<(byte[], long)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pumpTask;
        private int _oneWayDelayMs;
        private long _bitsPerSecondLimit;
        private int _disposed;

        public DelayedThrottledStream(Stream inner, int oneWayDelayMs = 0, double bandwidthMbps = 0)
        {
            _inner = inner;
            _oneWayDelayMs = oneWayDelayMs;
            SetBandwidthMbps(bandwidthMbps);
            _pumpTask = Task.Run(PumpWritesAsync);
        }

        public void SetDelayMs(int ms) => Volatile.Write(ref _oneWayDelayMs, Math.Max(0, ms));
        public void SetBandwidthMbps(double mbps) =>
            Interlocked.Exchange(ref _bitsPerSecondLimit, mbps > 0 ? (long)(mbps * 1_000_000) : 0);

        private async Task PumpWritesAsync()
        {
            try
            {
                await foreach (var (data, deliverAtMs) in _pipe.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    long waitMs = deliverAtMs - _sw.ElapsedMilliseconds;
                    if (waitMs > 0)
                        await Task.Delay((int)waitMs, _cts.Token).ConfigureAwait(false);
                    await _inner.WriteAsync(data, _cts.Token).ConfigureAwait(false);
                }
            }
            catch { }
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => _inner.ReadAsync(buffer, offset, count, ct);
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            long bps = Interlocked.Read(ref _bitsPerSecondLimit);
            if (bps > 0)
            {
                double serializeMs = (buffer.Length * 8.0 * 1000.0) / bps;
                if (serializeMs >= 1.0)
                    await Task.Delay((int)Math.Min(serializeMs, 2000), ct).ConfigureAwait(false);
            }
            int d = Volatile.Read(ref _oneWayDelayMs);
            long deliverAt = _sw.ElapsedMilliseconds + d;
            _pipe.Writer.TryWrite((buffer.ToArray(), deliverAt));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _pipe.Writer.TryComplete();
                try { _cts.Cancel(); } catch { }
                try { _pumpTask.Wait(200); } catch { }
                _cts.Dispose();
                try { _inner.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>受控 UDP 轉發代理：可注入封包遺失率（對初始資料包）與單向延遲/限速，供 P1-04 與 ARQ 實測。</summary>
    private sealed class ControlledUdpProxy : IDisposable
    {
        private readonly Socket _sockA;
        private readonly Socket _sockB;
        private readonly CancellationTokenSource _cts = new();
        private volatile EndPoint? _epA;
        private volatile EndPoint? _epB;
        private double _dropDataRate;
        private volatile bool _dropAllDataFromA;
        private volatile int _oneWayDelayMs;
        private int _seqCounter;
        private int _disposed;

        public IPEndPoint ProxyEndPointForA => (IPEndPoint)_sockA.LocalEndPoint!;
        public IPEndPoint ProxyEndPointForB => (IPEndPoint)_sockB.LocalEndPoint!;

        public void Configure(double dropDataRate = 0, bool dropAllFromA = false, int oneWayDelayMs = 0)
        {
            Volatile.Write(ref _dropDataRate, dropDataRate);
            _dropAllDataFromA = dropAllFromA;
            _oneWayDelayMs = oneWayDelayMs;
        }

        public ControlledUdpProxy()
        {
            _sockA = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _sockB = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _sockA.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _sockB.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            _ = Task.Run(() => PumpAsync(_sockA, _sockB, fromA: true));
            _ = Task.Run(() => PumpAsync(_sockB, _sockA, fromA: false));
        }

        private async Task PumpAsync(Socket ingress, Socket egress, bool fromA)
        {
            var buf = new byte[4096];
            EndPoint anyEp = new IPEndPoint(IPAddress.Any, 0);
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var res = await ingress.ReceiveFromAsync(buf, SocketFlags.None, anyEp, _cts.Token).ConfigureAwait(false);
                    if (fromA) _epA = res.RemoteEndPoint;
                    else _epB = res.RemoteEndPoint;

                    EndPoint? dst = fromA ? _epB : _epA;
                    if (dst == null) continue;

                    int n = res.ReceivedBytes;
                    byte msgType = n >= 5 ? buf[4] : (byte)0;
                    // TypeData = 0x01; 僅對 Data 封包依條件掉包，允許控制包或依 _dropDataRate
                    if (fromA && msgType == 0x01)
                    {
                        if (_dropAllDataFromA) continue;
                        double dropRate = Volatile.Read(ref _dropDataRate);
                        if (dropRate > 0)
                        {
                            int mod = Math.Max(1, (int)Math.Round(1.0 / dropRate));
                            if (Interlocked.Increment(ref _seqCounter) % mod == 0)
                            {
                                continue;
                            }
                        }
                    }

                    byte[] copy = new byte[n];
                    Buffer.BlockCopy(buf, 0, copy, 0, n);
                    int delay = _oneWayDelayMs;
                    if (delay > 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(delay, _cts.Token).ConfigureAwait(false);
                                await egress.SendToAsync(copy, SocketFlags.None, dst, _cts.Token).ConfigureAwait(false);
                            }
                            catch { }
                        });
                    }
                    else
                    {
                        await egress.SendToAsync(copy, SocketFlags.None, dst, _cts.Token).ConfigureAwait(false);
                    }
                }
                catch { if (_cts.IsCancellationRequested) break; }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _cts.Cancel(); } catch { }
            try { _sockA.Dispose(); } catch { }
            try { _sockB.Dispose(); } catch { }
            _cts.Dispose();
        }
    }

    private static byte[] BuildSyntheticFullFramePacket(int w, int h, Color c, uint seq, uint ver, uint cfgVer)
    {
        int stride = w * 4;
        var raw = new byte[h * stride];
        for (int i = 0; i < raw.Length; i += 4)
        {
            raw[i] = c.B; raw[i + 1] = c.G; raw[i + 2] = c.R; raw[i + 3] = 255;
        }
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(raw, y * stride, bd.Scan0 + y * bd.Stride, stride);
        }
        finally { bmp.UnlockBits(bd); }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Jpeg);
        var tile = new EncodedTile(0, 0, w, h, TileEncoding.Jpeg, ms.ToArray());
        return FrameEncoder.Serialize(seq, ver, 0, FrameType.FullFrame, w, h, null, new[] { tile }, cfgVer);
    }

    private static byte[] SnapshotCanvasBgra(FrameDecoder dec, int w, int h)
    {
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            dec.DrawTo(g, new Rectangle(0, 0, w, h));
        }
        var buf = new byte[w * h * 4];
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < h; y++)
                Marshal.Copy(bd.Scan0 + y * bd.Stride, buf, y * w * 4, w * 4);
        }
        finally { bmp.UnlockBits(bd); }
        return buf;
    }

    private static void PaintSolidBgra(byte[] buf, int strideW, Rectangle rect, Color c)
    {
        for (int y = Math.Max(0, rect.Y); y < rect.Bottom; y++)
        {
            for (int x = Math.Max(0, rect.X); x < rect.Right; x++)
            {
                int off = (y * strideW + x) * 4;
                buf[off] = c.B;
                buf[off + 1] = c.G;
                buf[off + 2] = c.R;
                buf[off + 3] = 255;
            }
        }
    }

    private static void ApplyGroundTruthMovesBgra(byte[] buf, int w, int h, IReadOnlyList<MoveRectangle> moves)
    {
        int stride = w * 4;
        var snapshot = (byte[])buf.Clone();
        foreach (var m in moves)
        {
            for (int row = 0; row < m.DestinationRect.Height; row++)
            {
                int srcOff = (m.SourcePoint.Y + row) * stride + m.SourcePoint.X * 4;
                int dstOff = (m.DestinationRect.Y + row) * stride + m.DestinationRect.X * 4;
                Buffer.BlockCopy(snapshot, srcOff, buf, dstOff, m.DestinationRect.Width * 4);
            }
        }
    }

    private static async Task<(SecureChannel hostCh, SecureChannel viewerCh)> CreateTcpSecurePairAsync(
        Stream? hostWrapOrNull = null,
        Func<Stream, Stream>? hostWrapper = null,
        Func<Stream, Stream>? viewerWrapper = null)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var client = new TcpClient();
        var acceptTask = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var server = await acceptTask;

        Stream hostStream = hostWrapOrNull ?? (hostWrapper != null ? hostWrapper(server.GetStream()) : server.GetStream());
        Stream viewerStream = viewerWrapper != null ? viewerWrapper(client.GetStream()) : client.GetStream();
        var hostHs = SecureChannel.ServerHandshakeAsync(
            hostStream,
            _ => new ServerHello(Protocol.Version, "910000001", "Batch3Host", Array.Empty<byte>(), 1, false, false, false),
            CancellationToken.None);
        var viewerHs = SecureChannel.ClientHandshakeAsync(viewerStream, "910000002", "Batch3Viewer", CancellationToken.None);

        var (hostCh, _) = await hostHs;
        var (viewerCh, _) = await viewerHs;
        return (hostCh, viewerCh);
    }

    // =========================================================================
    // A. P1-04：遙測校正（視窗標記、靜態桌面歸零、WM_PAINT 去重、ARQ/Goodput/E2E）
    // =========================================================================

    [Fact]
    public async Task P1_04_TelemetryWindow_ProtocolRoundTrip_And_StaticDesktopClearsLatchedStats()
    {
        // 1. 驗證 HostStreamTelemetry 與 ViewerTelemetrySnapshot 序列/反序列往返與明確視窗標記
        var tx = new HostStreamTelemetry
        {
            ConfigVersion = 3,
            Mode = ResolutionMode.Auto,
            Level = ResolutionLevel.Level1,
            OriginalWidth = 1920,
            OriginalHeight = 1080,
            StreamWidth = 1440,
            StreamHeight = 810,
            Quality = 75,
            TargetFps = 60,
            TelemetryWindowMs = 500,
            IsStreamIdle = false,
            GoodputMbps = 4.25f,
            WireThroughputMbps = 4.72f,
            EffectiveMaxInflightFrames = 4,
            EndToEndDelayStages = "QueueWait+Encode+RTT/2+Decode+Render (不含Capture/Display)",
            ArqFinalSuccessRate = 98.5f,
            EndToEndDelayMs = 42.5f
        };
        byte[] wire = MessageCodec.StreamTelemetry(tx);
        var pr = new PacketReader(wire.AsSpan(1));
        var rx = MessageCodec.ReadStreamTelemetry(ref pr);

        Assert.Equal(500, rx.TelemetryWindowMs);
        Assert.False(rx.IsStreamIdle);
        Assert.Equal(4.25f, rx.GoodputMbps, 2);
        Assert.Equal(4.72f, rx.WireThroughputMbps, 2);
        Assert.Equal(4, rx.EffectiveMaxInflightFrames);
        Assert.Equal("QueueWait+Encode+RTT/2+Decode+Render (不含Capture/Display)", rx.EndToEndDelayStages);

        // 2. 端對端 HostSession 實機迴路：活躍串流 -> 靜態桌面 -> 禁止永久鎖存上一幀 AverageFrameSize/EncodeMs/Bandwidth
        var (hostCh, viewerCh) = await CreateTcpSecurePairAsync();
        using var _hCh = hostCh;
        using var _vCh = viewerCh;

        var capturer = new SyntheticCapturer(320, 180, Color.FromArgb(40, 80, 160));
        var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "Mon1", 1, 0, 0, 320, 180, true, 96) };
        using var host = new HostSession(hostCh, new ClientHello(Protocol.Version, "910000002", "V"), "127.0.0.1", new NullHostUi())
        {
            AllowDxgi = false,
            MonitorsOverride = () => monList,
            CapturerOverride = (_, _) => capturer
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var hostTask = Task.Run(host.RunAsync);
        using var dec = new FrameDecoder();

        var viewerRecv = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var msg = await viewerCh.ReceiveAsync(cts.Token);
                    var r = new PacketReader(msg.AsSpan(1));
                    switch ((HostMsg)msg[0])
                    {
                        case HostMsg.StreamConfig:
                        {
                            var (v, _, _, sw, sh, _, _, _) = MessageCodec.ReadStreamConfig(ref r);
                            dec.ConfigureStream(v, sw, sh);
                            await viewerCh.SendAsync(MessageCodec.StreamConfigAck(v, sw, sh), highPriority: true, cts.Token);
                            break;
                        }
                        case HostMsg.VideoFrame:
                        {
                            dec.DecodeFrame(msg.AsSpan(1), out _, out _, out _, out _, out _, out _);
                            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.FrameAck, 1).ToArray(), highPriority: true, cts.Token);
                            break;
                        }
                    }
                }
                catch { break; }
            }
        });

        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5).U8(75).U8(30).Bool(false).U8((byte)ResolutionMode.Auto).ToArray(), highPriority: true, cts.Token);

        // 活躍期間：每秒繪製多次大型變更，讓 Host 記錄活躍 AverageFrameSizeBytes / EncodeDuration / Goodput
        HostStreamTelemetry? activeSnap = null;
        for (int i = 0; i < 20; i++)
        {
            capturer.PaintRect(new Rectangle((i * 11) % 160, 0, 140, 140), Color.FromArgb((i * 29) % 255, (i * 53) % 255, 200));
            await Task.Delay(55);
            if (host.LatestTelemetry is { AverageFrameSizeBytes: > 0, GoodputMbps: > 0f } t)
                activeSnap = t;
        }
        Assert.NotNull(activeSnap);
        Assert.False(activeSnap!.IsStreamIdle);
        Assert.Equal(500, activeSnap.TelemetryWindowMs);
        Assert.Equal(0f, activeSnap.EstimatedBandwidthMbps); // TCP 不得把發送流量誤標為可用頻寬估計

        // 靜態桌面期：完全停止畫面更新並等 2 個以上 500ms 視窗，驗證所有活躍平均指標清潔歸零
        HostStreamTelemetry? idleSnap = null;
        for (int i = 0; i < 25; i++)
        {
            await Task.Delay(120);
            if (host.LatestTelemetry is { IsStreamIdle: true, AverageFrameSizeBytes: 0, AvgEncodeDurationMs: 0f } it)
            {
                idleSnap = it;
                break;
            }
        }

        Assert.NotNull(idleSnap);
        Assert.True(idleSnap!.IsStreamIdle);
        Assert.Equal(0, idleSnap.AverageFrameSizeBytes);
        Assert.Equal(0f, idleSnap.AvgEncodeDurationMs);
        Assert.Equal(0f, idleSnap.AvgDecodeDurationMs);
        Assert.Equal(0f, idleSnap.AvgRenderDurationMs);
        Assert.Equal(0f, idleSnap.ActualSendFps);
        Assert.Equal(0f, idleSnap.ViewerRenderFps);
        Assert.Equal(0f, idleSnap.EstimatedBandwidthMbps);

        cts.Cancel();
        host.Dispose();
        try { await hostTask; await viewerRecv; } catch { }
    }

    [Fact]
    public void P1_04_DesktopCanvas_RepeatedWmPaintWithoutNewFrame_DoesNotInflateRenderFps()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"rd_p104_paint_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var identity = DeviceIdentity.LoadOrCreate(tempRoot, "111222334", "CanvasViewer");
            var cfg = new ViewerConfig { ViewerId = "111222334", Quality = 75, MaxFps = 60 };
            using var session = new ViewerSession("910000001", cfg, new LanDiscovery("111222334"), "111222334", identity);
            using var canvas = new DesktopCanvas(session) { Size = new Size(640, 360) };

            int w = 320, h = 180;
            session.DispatchForTest(MessageCodec.StreamConfig(1, w, h, w, h, ResolutionLevel.Level0, ResolutionMode.Auto, "Init"));
            canvas.RecalculateLayout();

            using var paintBmp = new Bitmap(640, 360);
            using var g = Graphics.FromImage(paintBmp);

            // 未收到任何影格前觸發 10 次 WM_PAINT -> RenderFps 必為 0
            for (int i = 0; i < 10; i++)
            {
                Assert.False(canvas.RenderFrameForPaint(g));
            }
            var snap0 = session.CollectViewerTelemetrySnapshotForTest();
            Assert.Equal(0f, snap0.RenderFps);
            Assert.Equal(0f, snap0.AvgDecodeDurationMs);
            Assert.Equal(0f, snap0.AvgRenderDurationMs);
            Assert.True(snap0.IsIdle);

            // 解碼 1 幀並連續觸發 25 次 WM_PAINT (模擬滑鼠移動/重疊視窗重繪) -> 只允許計入 1 次真實 Render!
            session.DispatchForTest(BuildSyntheticFullFramePacket(w, h, Color.FromArgb(20, 140, 220), seq: 1, ver: 1, cfgVer: 1));
            int countedPaints = 0;
            for (int i = 0; i < 25; i++)
            {
                if (canvas.RenderFrameForPaint(g)) countedPaints++;
            }
            Assert.Equal(1, countedPaints);

            Thread.Sleep(150);
            var snap1 = session.CollectViewerTelemetrySnapshotForTest();
            Assert.Equal(500, snap1.TelemetryWindowMs);
            Assert.False(snap1.IsIdle);
            Assert.InRange(snap1.RenderFps, 1.0f, 10.0f); // 1 幀 / ~0.15s = ~6.6 FPS，絕非 25/0.15 = 166 FPS!

            // 下一個採樣視窗：即使重複 20 次 WM_PAINT，因無新解碼影格，RenderFps 與平均耗時皆須歸零
            for (int i = 0; i < 20; i++)
            {
                Assert.False(canvas.RenderFrameForPaint(g));
            }
            Thread.Sleep(120);
            var snap2 = session.CollectViewerTelemetrySnapshotForTest();
            Assert.True(snap2.IsIdle);
            Assert.Equal(0f, snap2.RenderFps);
            Assert.Equal(0f, snap2.AvgDecodeDurationMs);
            Assert.Equal(0f, snap2.AvgRenderDurationMs);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task P1_04_ReliableUdp_SeparatesRawLoss_ArqSuccess_Goodput_Wire_And_DeliveryBw()
    {
        using var proxy = new ControlledUdpProxy();
        using var sockA = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var sockB = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sockA.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        sockB.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        // B 送一次握手封包給 Proxy B，使 Proxy 知道 epB；A 送往 Proxy A
        var probe = new byte[] { 0 };
        await sockB.SendToAsync(probe, SocketFlags.None, proxy.ProxyEndPointForB);
        await Task.Delay(20);

        using var streamA = new ReliableUdpStream(sockA, proxy.ProxyEndPointForA, ownsSocket: true);
        using var streamB = new ReliableUdpStream(sockB, proxy.ProxyEndPointForB, ownsSocket: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        long totalBReceived = 0;
        var recvTask = Task.Run(async () =>
        {
            var buf = new byte[8192];
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    int r = await streamB.ReadAsync(buf, cts.Token);
                    if (r == 0) break;
                    Interlocked.Add(ref totalBReceived, r);
                }
                catch { break; }
            }
        });

        // 情境 1：注入 ~5% 可恢復封包遺失 (every 20th data packet dropped once)，傳送 60KB 突發資料
        proxy.Configure(dropDataRate: 0.05, dropAllFromA: false, oneWayDelayMs: 5);
        byte[] payload = new byte[60 * 1024];
        Random.Shared.NextBytes(payload);
        await streamA.WriteAsync(payload, cts.Token);

        for (int i = 0; i < 60 && (streamA.CurrentInflightPackets > 0 || Interlocked.Read(ref totalBReceived) < payload.Length); i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(payload.Length, Interlocked.Read(ref totalBReceived));
        Assert.True(streamA.UniquePacketsLost > 0 || streamA.TotalRetransmissions > 0, "應實際觸發原始 UDP 封包遺失與重傳");
        Assert.Equal(0, streamA.CurrentInflightPackets);
        Assert.Equal(0, streamA.GetAgedUnackedCount(minRetries: 2));
        Assert.True(streamA.TotalWireBytesSent > streamA.TotalPayloadBytesAcked, "Wire bytes 必大於應用 Payload (含 16B+28B 標頭與重傳)");

        float deliveryBw = streamA.ConsumeDeliveryBandwidthSampleMbps();
        _output.WriteLine($"[P1-04 UDP 5% 可恢復掉包] PayloadAcked={streamA.TotalPayloadBytesAcked}B, WireSent={streamA.TotalWireBytesSent}B, UniqueLost={streamA.UniquePacketsLost}, Retrans={streamA.TotalRetransmissions}, DeliveryBW={deliveryBw:F2} Mbps");

        // 情境 2：完全黑洞 (All data dropped) -> RTO 超時與高重試卡死封包必令 GetAgedUnackedCount > 0，ARQ 最終成功數必須低於 100%
        proxy.Configure(dropDataRate: 0, dropAllFromA: true, oneWayDelayMs: 5);
        var small = new byte[2400];
        await streamA.WriteAsync(small, cts.Token);

        for (int i = 0; i < 30 && streamA.GetAgedUnackedCount(minRetries: 2) == 0; i++)
        {
            await Task.Delay(100);
        }

        int stuckCount = streamA.GetAgedUnackedCount(minRetries: 2);
        _output.WriteLine($"[P1-04 UDP 持續黑洞] AgedStuck(Retries>=2)={stuckCount}, TotalRtoTimeouts={streamA.TotalRtoTimeouts}");
        Assert.True(stuckCount > 0, "持續無法送達的封包應累計至 AgedUnackedCount，供 ArqFinalSuccessRate 真實扣分（不得假回報 100%）");

        cts.Cancel();
        proxy.Dispose();
        try { await recvTask; } catch { }
    }

    // =========================================================================
    // B. Stage 2C：MaxInflight=2 與高 RTT (150/200/300ms) / BDP 模擬與受控迴路驗證
    // =========================================================================

    [Fact]
    public async Task Stage2C_HighRtt_150_200_300ms_Analysis_FixedMax2_vs_BoundedDynamicWindow()
    {
        // 1. 離散事件網路管線模擬：比較 [Fixed MaxInflight=2] 與 [Bounded BDP Dynamic Window (2~6)]
        //    測試 RTT = 150ms / 200ms / 300ms，情境包含：
        //    (a) 高 BDP 20Mbps (L0 120KB vs L2 30KB)
        //    (b) 低頻寬受限 2Mbps @ 200ms RTT (驗證若不顧 BDP 盲目放大視窗會造成 Queue 膨脹，而有界 BDP 視窗能保底低延遲)
        static (float fps, float avgQueueDelayMs) SimulatePipeline(int rttMs, double bwMbps, int frameSizeBytes, int targetFps, int maxInflightCap)
        {
            double simClockMs = 0;
            double wireFreeAtMs = 0;
            double totalQueueWaitMs = 0;
            int sentFrames = 0;
            var ackReadyTimes = new PriorityQueue<double, double>();
            double frameIntervalMs = 1000.0 / targetFps;
            double nextCaptureMs = 0;
            double serializeMs = (frameSizeBytes * 8.0 * 1000.0) / (bwMbps * 1_000_000.0);

            const double durationMs = 4000.0;
            while (simClockMs < durationMs)
            {
                while (ackReadyTimes.Count > 0 && ackReadyTimes.Peek() <= simClockMs)
                    ackReadyTimes.Dequeue();

                if (simClockMs >= nextCaptureMs && ackReadyTimes.Count < maxInflightCap)
                {
                    double startTx = Math.Max(simClockMs, wireFreeAtMs);
                    double txQueueDelay = startTx - simClockMs;
                    totalQueueWaitMs += txQueueDelay;
                    wireFreeAtMs = startTx + serializeMs;
                    double ackAt = wireFreeAtMs + rttMs;
                    ackReadyTimes.Enqueue(ackAt, ackAt);
                    sentFrames++;
                    nextCaptureMs = simClockMs + frameIntervalMs;
                }
                else
                {
                    nextCaptureMs = Math.Max(nextCaptureMs, simClockMs + 2.0);
                }
                simClockMs += 2.0;
            }

            return ((float)(sentFrames / (durationMs / 1000.0)), sentFrames > 0 ? (float)(totalQueueWaitMs / sentFrames) : 0f);
        }

        _output.WriteLine("=== Stage 2C 模擬驗證：RTT 150/200/300ms 下 MaxInflight=2 與 BDP 有界視窗表現 ===");
        foreach (int rtt in new[] { 150, 200, 300 })
        {
            var (fpsL0Fixed, _) = SimulatePipeline(rtt, bwMbps: 20.0, frameSizeBytes: 120 * 1024, targetFps: 30, maxInflightCap: 2);
            var (fpsL2Fixed, _) = SimulatePipeline(rtt, bwMbps: 20.0, frameSizeBytes: 30 * 1024, targetFps: 30, maxInflightCap: 2);

            // 若 BDP 允許（20Mbps * 200ms = 500KB），ComputeEffectiveMaxInflight 可安全放寬到 5~6 幀
            int boundedWin = HostSession.ComputeEffectiveMaxInflight(
                rttMs: rtt, targetFps: 30, effectiveWaitMs: 2f, sendQueuePending: 0,
                udpLossRatePercent: 0f, retransRatePercent: 0f, rtoDelta: 0, arqFinalSuccessRatePercent: 100f,
                inflightRatio: 0.08f, isCongested: false, avgFrameSizeBytes: 30 * 1024, estimatedBandwidthMbps: 20f);
            var (fpsL2Bounded, qWaitBounded) = SimulatePipeline(rtt, bwMbps: 20.0, frameSizeBytes: 30 * 1024, targetFps: 30, maxInflightCap: boundedWin);

            _output.WriteLine($"RTT={rtt}ms @ 20Mbps | Fixed(2) L0={fpsL0Fixed:F1}FPS, L2={fpsL2Fixed:F1}FPS | Bounded({boundedWin}) L2={fpsL2Bounded:F1}FPS, TxQueue={qWaitBounded:F1}ms");

            // 證明：固定 MaxInflight=2 在高 BDP 20Mbps 下，即使從 120KB (L0) 降至 30KB (L2)，仍被 2/RTT 卡在 ~13/10/6.7 FPS
            float theoreticalMax2 = 2000f / rtt;
            Assert.True(fpsL2Fixed <= theoreticalMax2 + 1.0f);
            // 證明：有界動態視窗在高 BDP 下實質解除瓶頸且無顯著排隊延遲
            Assert.True(boundedWin >= 5 && boundedWin <= 6);
            Assert.True(fpsL2Bounded > fpsL2Fixed * 1.6f);
            Assert.True(qWaitBounded < 15f);
        }

        // 2. 低頻寬受限場景 (2Mbps @ 200ms RTT, BDP=50KB) 對比驗證：
        var (fpsLowFixed2, qLowFixed2) = SimulatePipeline(200, bwMbps: 2.0, frameSizeBytes: 30 * 1024, targetFps: 30, maxInflightCap: 2);
        var (fpsLowBlind6, qLowBlind6) = SimulatePipeline(200, bwMbps: 2.0, frameSizeBytes: 30 * 1024, targetFps: 30, maxInflightCap: 6);
        int boundedLowBw = HostSession.ComputeEffectiveMaxInflight(
            rttMs: 200, targetFps: 30, effectiveWaitMs: 0f, sendQueuePending: 0,
            udpLossRatePercent: 0f, retransRatePercent: 0f, rtoDelta: 0, arqFinalSuccessRatePercent: 100f,
            inflightRatio: 0.10f, isCongested: false, avgFrameSizeBytes: 30 * 1024, estimatedBandwidthMbps: 2.0f);

        _output.WriteLine($"RTT=200ms @ 2Mbps (BDP=50KB) | Fixed(2) Q={qLowFixed2:F1}ms | Blind(6) Q={qLowBlind6:F1}ms | ComputeEffectiveMaxInflight={boundedLowBw}");
        Assert.True(qLowBlind6 > 200f, "不檢查 BDP 盲目放大 MaxInflight=6 在低頻寬下將造成嚴重排隊延遲膨脹");
        Assert.Equal(2, boundedLowBw); // BDP * 0.75 = 37.5KB -> 嚴格鎖回 MaxInflight=2，零排隊累積

        // 任何掉包 / 排隊 / RTO 立即縮回 MaxInflight=2
        Assert.Equal(2, HostSession.ComputeEffectiveMaxInflight(200, 30, 18f, 0, 0f, 0f, 0, 100f, 0.1f, false, 30000, 20f));
        Assert.Equal(2, HostSession.ComputeEffectiveMaxInflight(200, 30, 0f, 0, 1.5f, 1.8f, 0, 100f, 0.1f, false, 30000, 20f));
        Assert.Equal(2, HostSession.ComputeEffectiveMaxInflight(45, 30, 0f, 0, 0f, 0f, 0, 100f, 0.05f, false, 30000, 50f));

        // 3. 實機 HostSession + Viewer 延遲迴路驗證：高 RTT (雙向各 80ms = ~160ms RTT) 健康高頻寬連線不得誤降級，並擴展管線突破 12 FPS
        DelayedThrottledStream? hostDelay = null;
        DelayedThrottledStream? viewerDelay = null;
        var (hostCh, viewerCh) = await CreateTcpSecurePairAsync(
            hostWrapper: s =>
            {
                hostDelay = new DelayedThrottledStream(s, oneWayDelayMs: 0, bandwidthMbps: 0);
                return hostDelay;
            },
            viewerWrapper: s =>
            {
                viewerDelay = new DelayedThrottledStream(s, oneWayDelayMs: 0, bandwidthMbps: 0);
                return viewerDelay;
            });
        using var _hCh = hostCh;
        using var _vCh = viewerCh;

        // 握手完成後立即注入雙向各 80ms 傳播延遲 (~160ms RTT, 無頻寬限制)
        hostDelay!.SetDelayMs(80);
        viewerDelay!.SetDelayMs(80);

        var capturer = new SyntheticCapturer(320, 180, Color.DarkSlateBlue);
        var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "Mon1", 1, 0, 0, 320, 180, true, 96) };
        using var host = new HostSession(hostCh, new ClientHello(Protocol.Version, "910000002", "HighRttViewer"), "127.0.0.1", new NullHostUi())
        {
            AllowDxgi = false,
            MonitorsOverride = () => monList,
            CapturerOverride = (_, _) => capturer
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var hostTask = Task.Run(host.RunAsync);
        using var dec = new FrameDecoder();

        var viewerLoop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var msg = await viewerCh.ReceiveAsync(cts.Token);
                    var r = new PacketReader(msg.AsSpan(1));
                    switch ((HostMsg)msg[0])
                    {
                        case HostMsg.StreamConfig:
                        {
                            var (v, _, _, sw, sh, _, _, _) = MessageCodec.ReadStreamConfig(ref r);
                            dec.ConfigureStream(v, sw, sh);
                            await viewerCh.SendAsync(MessageCodec.StreamConfigAck(v, sw, sh), highPriority: true, cts.Token);
                            break;
                        }
                        case HostMsg.VideoFrame:
                        {
                            dec.DecodeFrame(msg.AsSpan(1), out _, out _, out _, out _, out _, out _);
                            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.FrameAck, 1).ToArray(), highPriority: true, cts.Token);
                            break;
                        }
                    }
                }
                catch { break; }
            }
        });

        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5).U8(60).U8(30).Bool(false).U8((byte)ResolutionMode.Auto).ToArray(), highPriority: true, cts.Token);

        for (int i = 0; i < 65; i++)
        {
            capturer.PaintRect(new Rectangle((i * 7) % 160, 0, 120, 120), Color.FromArgb((i * 41) % 255, 180, 90));
            await Task.Delay(35);
        }

        var tel = host.LatestTelemetry;
        Assert.NotNull(tel);
        _output.WriteLine($"[實機高 RTT ~160ms 迴路] RTT={tel!.RttMs:F0}ms, Level={tel.Level}, EffectiveMaxInflight={tel.EffectiveMaxInflightFrames}, SendFPS={tel.ActualSendFps:F1}");
        Assert.True(tel.RttMs >= 100f, $"FIFO RTT 量測應反映真實 ~160ms 延遲 (實際={tel.RttMs:F1}ms)");
        Assert.Equal(ResolutionLevel.Level0, tel.Level); // 健康高 RTT 不得被 _ackEvent 空等誤判壅塞而降解析度
        Assert.True(tel.EffectiveMaxInflightFrames > 2, "健康高 RTT (>80ms) 且無壅塞時應自動擴展動態管線額度 > 2");
        Assert.True(tel.ActualSendFps > 14f, $"動態管線擴展後 SendFPS 應突破 Fixed(2) ~12.5fps 上限 (實際={tel.ActualSendFps:F1})");

        cts.Cancel();
        host.Dispose();
        try { await hostTask; await viewerLoop; } catch { }
    }

    // =========================================================================
    // C. Stage 2D：完整邊界與整合回歸矩陣
    // =========================================================================

    [Theory]
    [InlineData(1365, 767)]
    [InlineData(1600, 900)]
    [InlineData(1280, 1024)]
    [InlineData(2560, 1080)]
    public void Stage2D_Resolutions_ProfileAlignment_AspectRatio_RoundTrip_And_MouseMap(int origW, int origH)
    {
        var profiles = ResolutionProfileManager.BuildProfiles(origW, origH);
        Assert.Equal(3, profiles.Length);

        foreach (var p in profiles)
        {
            Assert.Equal(origW, p.OriginalWidth);
            Assert.Equal(origH, p.OriginalHeight);
            Assert.InRange(p.StreamWidth, 2, origW);
            Assert.InRange(p.StreamHeight, 2, origH);

            if (!p.IsNative)
            {
                Assert.Equal(0, p.StreamWidth % 2);
                Assert.Equal(0, p.StreamHeight % 2);
            }

            // 長寬比誤差在偶數取整 2px 容忍範圍內 (< 1.5%)
            double origRatio = (double)origW / origH;
            double streamRatio = (double)p.StreamWidth / p.StreamHeight;
            Assert.True(Math.Abs(origRatio - streamRatio) / origRatio < 0.015,
                $"{origW}x{origH} -> {p.StreamWidth}x{p.StreamHeight} 長寬比偏差過大");

            // 在該解析度層級執行完整 FullFrame + RegionUpdate + MoveAndRegion 編碼/解碼往返
            var enc = new FrameEncoder(origW, origH) { Quality = 75, Lossless = false };
            using var dec = new FrameDecoder();
            enc.ConfigureStreamResolution(p.StreamWidth, p.StreamHeight);
            dec.ConfigureStream(1, p.StreamWidth, p.StreamHeight);

            var raw = new byte[origW * origH * 4];
            PaintSolidBgra(raw, origW, new Rectangle(0, 0, origW, origH), Color.FromArgb(32, 64, 128));
            PaintSolidBgra(raw, origW, new Rectangle(20, 20, 180, 120), Color.FromArgb(240, 200, 40));

            GCHandle pin = GCHandle.Alloc(raw, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = pin.AddrOfPinnedObject();
                var kf = new CapturedFrame(ptr, origW, origH, origW * 4, HasMetadata: true, DirtyRectangles: new[] { new Rectangle(0, 0, origW, origH) }, MoveRectangles: null, IsKeyFrame: true);
                var kfTiles = enc.Encode(kf, true, 100, out var kfType, out var kfMoves);
                byte[] kfBytes = FrameEncoder.Serialize(1, 1, 0, kfType, enc.StreamWidth, enc.StreamHeight, kfMoves, kfTiles, 1);
                var dirty1 = dec.DecodeFrame(kfBytes.AsSpan(1), out _, out _, out _, out _, out _, out var st1);
                Assert.Equal(FrameDecodeStatus.Applied, st1);
                Assert.Equal(new Rectangle(0, 0, p.StreamWidth, p.StreamHeight), dirty1);

                // 增量位移 + 局部變動
                var move = new MoveRectangle(new Point(20, 20), new Rectangle(60, 40, 180, 120));
                ApplyGroundTruthMovesBgra(raw, origW, origH, new[] { move });
                var dirtyPatch = new Rectangle(origW - 101, origH - 61, 80, 40);
                PaintSolidBgra(raw, origW, dirtyPatch, Color.FromArgb(220, 50, 50));

                var df = new CapturedFrame(ptr, origW, origH, origW * 4, HasMetadata: true, DirtyRectangles: new[] { dirtyPatch }, MoveRectangles: new[] { move }, IsKeyFrame: false);
                var dfTiles = enc.Encode(df, true, 133, out var dfType, out var dfMoves);
                byte[] dfBytes = FrameEncoder.Serialize(2, 2, 1, dfType, enc.StreamWidth, enc.StreamHeight, dfMoves, dfTiles, 1);
                var dirty2 = dec.DecodeFrame(dfBytes.AsSpan(1), out _, out _, out _, out _, out _, out var st2);
                Assert.Equal(FrameDecodeStatus.Applied, st2);
                Assert.Equal(0, dec.DecodeFailureCount);
            }
            finally { pin.Free(); }

            // 滑鼠反算永遠對應回 origW x origH 原生座標（與 StreamWidth/StreamHeight 完全無關）
            var destRect = new Rectangle(10, 20, 800, 450);
            Assert.True(ResolutionProfileManager.TryMapViewerPointToHostOriginal(10, 20, destRect, origW, origH, out int rx0, out int ry0));
            Assert.Equal((0, 0), (rx0, ry0));
            Assert.True(ResolutionProfileManager.TryMapViewerPointToHostOriginal(809, 469, destRect, origW, origH, out int rx1, out int ry1));
            Assert.InRange(rx1, origW - 4, origW - 1);
            Assert.InRange(ry1, origH - 4, origH - 1);
        }
    }

    [Fact]
    public void Stage2D_MoveAndDirty_PixelLevelGroundTruth_AllBoundaryCases()
    {
        const int w = 320;
        const int h = 240;

        static void AssertMoveOnlyBitExact(byte[] actualCanvas, byte[] gtCanvas, int wPix, int hPix, string label)
        {
            int mismatches = 0;
            for (int y = 0; y < hPix; y++)
            {
                for (int x = 0; x < wPix; x++)
                {
                    int off = (y * wPix + x) * 4;
                    if (actualCanvas[off] != gtCanvas[off]
                        || actualCanvas[off + 1] != gtCanvas[off + 1]
                        || actualCanvas[off + 2] != gtCanvas[off + 2])
                    {
                        mismatches++;
                    }
                }
            }
            Assert.True(mismatches == 0, $"{label}: Move-only 區域應逐像素 Bit-Exact 相符，實際錯誤像素數={mismatches}");
        }

        // 建立共用的初始化編碼/解碼夾具（Baseline 為真實解碼後的平滑紋理與高對比幾何區塊）
        (FrameEncoder enc, FrameDecoder dec, byte[] workBuf, byte[] gtBuf) CreateAlignedPipeline()
        {
            var enc = new FrameEncoder(w, h) { Quality = 100, Lossless = true };
            var dec = new FrameDecoder();
            enc.ConfigureStreamResolution(w, h);
            dec.ConfigureStream(1, w, h);

            var init = new byte[w * h * 4];
            PaintSolidBgra(init, w, new Rectangle(0, 0, w, h), Color.FromArgb(24, 32, 48));
            PaintSolidBgra(init, w, new Rectangle(40, 40, 95, 73), Color.FromArgb(230, 180, 30));
            PaintSolidBgra(init, w, new Rectangle(120, 90, 81, 61), Color.FromArgb(30, 210, 120));
            PaintSolidBgra(init, w, new Rectangle(17, 151, 53, 47), Color.FromArgb(210, 50, 90));

            GCHandle pin = GCHandle.Alloc(init, GCHandleType.Pinned);
            try
            {
                var cf = new CapturedFrame(pin.AddrOfPinnedObject(), w, h, w * 4, HasMetadata: true, DirtyRectangles: new[] { new Rectangle(0, 0, w, h) }, MoveRectangles: null, IsKeyFrame: true);
                var tiles = enc.Encode(cf, true, 100, out var ft, out var mv);
                byte[] pkt = FrameEncoder.Serialize(1, 1, 0, ft, w, h, mv, tiles, 1);
                Assert.False(dec.DecodeFrame(pkt.AsSpan(1), out _, out _, out _, out _, out _, out var st).IsEmpty);
                Assert.Equal(FrameDecodeStatus.Applied, st);
            }
            finally { pin.Free(); }

            // 取解碼後精準畫布作為 Ground Truth 與下一幀基準（Eliminate JPEG keyframe quantization difference）
            byte[] baseline = SnapshotCanvasBgra(dec, w, h);
            return (enc, dec, (byte[])baseline.Clone(), (byte[])baseline.Clone());
        }

        // 1. 1px 水平左移與右移 (重疊區逐像素 Bit-Exact 驗證)
        {
            var (enc, dec, work, gt) = CreateAlignedPipeline();
            using var _d = dec;
            var moveRight1 = new MoveRectangle(new Point(40, 40), new Rectangle(41, 40, 95, 73));
            ApplyGroundTruthMovesBgra(gt, w, h, new[] { moveRight1 });
            byte[] movePkt = FrameEncoder.Serialize(2, 2, 1, FrameType.MoveAndRegion, w, h, new[] { moveRight1 }, Array.Empty<EncodedTile>(), 1);
            var dirtyR = dec.DecodeFrame(movePkt.AsSpan(1), out _, out _, out _, out _, out _, out var st);
            Assert.Equal(FrameDecodeStatus.Applied, st);
            Assert.False(dirtyR.IsEmpty);
            AssertMoveOnlyBitExact(SnapshotCanvasBgra(dec, w, h), gt, w, h, "1px水平右移(+1)");

            var moveLeft1 = new MoveRectangle(new Point(41, 40), new Rectangle(40, 40, 95, 73));
            ApplyGroundTruthMovesBgra(gt, w, h, new[] { moveLeft1 });
            byte[] movePkt2 = FrameEncoder.Serialize(3, 3, 2, FrameType.MoveAndRegion, w, h, new[] { moveLeft1 }, Array.Empty<EncodedTile>(), 1);
            dec.DecodeFrame(movePkt2.AsSpan(1), out _, out _, out _, out _, out _, out var st2);
            Assert.Equal(FrameDecodeStatus.Applied, st2);
            AssertMoveOnlyBitExact(SnapshotCanvasBgra(dec, w, h), gt, w, h, "1px水平左移(-1)");
        }

        // 2. 1px 垂直下移 (dy=+1 倒序掃描) 與上移 (dy=-1 正序掃描)
        {
            var (enc, dec, _, gt) = CreateAlignedPipeline();
            using var _d = dec;
            var down1 = new MoveRectangle(new Point(40, 40), new Rectangle(40, 41, 95, 73));
            ApplyGroundTruthMovesBgra(gt, w, h, new[] { down1 });
            byte[] pkt1 = FrameEncoder.Serialize(2, 2, 1, FrameType.MoveAndRegion, w, h, new[] { down1 }, Array.Empty<EncodedTile>(), 1);
            dec.DecodeFrame(pkt1.AsSpan(1), out _, out _, out _, out _, out _, out _);
            AssertMoveOnlyBitExact(SnapshotCanvasBgra(dec, w, h), gt, w, h, "1px垂直下移(+1)");

            var up1 = new MoveRectangle(new Point(40, 41), new Rectangle(40, 40, 95, 73));
            ApplyGroundTruthMovesBgra(gt, w, h, new[] { up1 });
            byte[] pkt2 = FrameEncoder.Serialize(3, 3, 2, FrameType.MoveAndRegion, w, h, new[] { up1 }, Array.Empty<EncodedTile>(), 1);
            dec.DecodeFrame(pkt2.AsSpan(1), out _, out _, out _, out _, out _, out _);
            AssertMoveOnlyBitExact(SnapshotCanvasBgra(dec, w, h), gt, w, h, "1px垂直上移(-1)");
        }

        // 3. 奇數座標 Move + 降採樣 ScaleMoveRectangles 邊界檢查
        {
            var (enc, dec, _, gt) = CreateAlignedPipeline();
            using var _d = dec;
            var oddMove = new MoveRectangle(new Point(17, 151), new Rectangle(33, 145, 53, 47));
            ApplyGroundTruthMovesBgra(gt, w, h, new[] { oddMove });
            byte[] pkt = FrameEncoder.Serialize(2, 2, 1, FrameType.MoveAndRegion, w, h, new[] { oddMove }, Array.Empty<EncodedTile>(), 1);
            dec.DecodeFrame(pkt.AsSpan(1), out _, out _, out _, out _, out _, out _);
            AssertMoveOnlyBitExact(SnapshotCanvasBgra(dec, w, h), gt, w, h, "奇數座標位移");

            // 降採樣至 L1 (240x180) 不得溢位或發生寬高不一致
            var scaledOdd = ResolutionProfileManager.ScaleMoveRectangles(new[] { oddMove }, w, h, 240, 180);
            Assert.NotNull(scaledOdd);
            Assert.Single(scaledOdd!);
            var sm = scaledOdd![0];
            Assert.InRange(sm.SourcePoint.X, 0, 239);
            Assert.InRange(sm.DestinationRect.X, 0, 239);
            Assert.True(sm.SourcePoint.X + sm.DestinationRect.Width <= 240);
            Assert.True(sm.DestinationRect.Right <= 240 && sm.DestinationRect.Bottom <= 180);
        }

        // 4. 多組重疊位移 (DXGI Simultaneous Scratch Semantics：M1 Dst 重疊 M2 Src)
        {
            var (enc, dec, _, gt) = CreateAlignedPipeline();
            using var _d = dec;
            var moves = new[]
            {
                new MoveRectangle(new Point(40, 40), new Rectangle(120, 90, 65, 55)),
                new MoveRectangle(new Point(120, 90), new Rectangle(40, 40, 65, 55))
            };
            ApplyGroundTruthMovesBgra(gt, w, h, moves);
            byte[] pkt = FrameEncoder.Serialize(2, 2, 1, FrameType.MoveAndRegion, w, h, moves, Array.Empty<EncodedTile>(), 1);
            dec.DecodeFrame(pkt.AsSpan(1), out _, out _, out _, out _, out _, out var st);
            Assert.Equal(FrameDecodeStatus.Applied, st);
            AssertMoveOnlyBitExact(SnapshotCanvasBgra(dec, w, h), gt, w, h, "多組重疊位移同時快照互換");
        }

        // 5. Move 與 Dirty 重疊 (先 Move 再 Dirty 覆蓋，Move-only 區 Bit-Exact，Dirty 區無色差與撕裂)
        {
            var (enc, dec, work, gt) = CreateAlignedPipeline();
            using var _d = dec;
            var mv = new MoveRectangle(new Point(40, 40), new Rectangle(80, 60, 95, 73));
            var overlapDirty = new Rectangle(100, 75, 48, 36);
            ApplyGroundTruthMovesBgra(work, w, h, new[] { mv });
            ApplyGroundTruthMovesBgra(gt, w, h, new[] { mv });
            PaintSolidBgra(work, w, overlapDirty, Color.FromArgb(15, 225, 240));
            PaintSolidBgra(gt, w, overlapDirty, Color.FromArgb(15, 225, 240));

            GCHandle pin = GCHandle.Alloc(work, GCHandleType.Pinned);
            try
            {
                var cf = new CapturedFrame(pin.AddrOfPinnedObject(), w, h, w * 4, HasMetadata: true, DirtyRectangles: new[] { overlapDirty }, MoveRectangles: new[] { mv }, IsKeyFrame: false);
                var tiles = enc.Encode(cf, true, 133, out var ft, out var outMoves);
                byte[] pkt = FrameEncoder.Serialize(2, 2, 1, ft, w, h, outMoves, tiles, 1);
                dec.DecodeFrame(pkt.AsSpan(1), out _, out _, out _, out var decType, out _, out var st);
                Assert.Equal(FrameType.MoveAndRegion, decType);
                Assert.Equal(FrameDecodeStatus.Applied, st);
            }
            finally { pin.Free(); }

            var actual = SnapshotCanvasBgra(dec, w, h);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int off = (y * w + x) * 4;
                    if (!overlapDirty.Contains(x, y))
                    {
                        Assert.Equal(gt[off], actual[off]);
                        Assert.Equal(gt[off + 1], actual[off + 1]);
                        Assert.Equal(gt[off + 2], actual[off + 2]);
                    }
                    else
                    {
                        Assert.InRange(Math.Abs(actual[off] - gt[off]), 0, 4);
                        Assert.InRange(Math.Abs(actual[off + 1] - gt[off + 1]), 0, 4);
                        Assert.InRange(Math.Abs(actual[off + 2] - gt[off + 2]), 0, 4);
                    }
                }
            }
        }

        // 6. 不規則矩形 (1x1 單像素、17x1 細長水平線、1x29 細長垂直線、>32 碎裂矩形合併)
        {
            var (enc, dec, work, gt) = CreateAlignedPipeline();
            using var _d = dec;
            var irregular = new List<Rectangle>
            {
                new(5, 5, 1, 1),
                new(10, 200, 97, 1),
                new(299, 10, 1, 59)
            };
            foreach (var r in irregular)
            {
                PaintSolidBgra(work, w, r, Color.FromArgb(250, 240, 250));
                PaintSolidBgra(gt, w, r, Color.FromArgb(250, 240, 250));
            }
            GCHandle pin = GCHandle.Alloc(work, GCHandleType.Pinned);
            try
            {
                var cf = new CapturedFrame(pin.AddrOfPinnedObject(), w, h, w * 4, HasMetadata: true, DirtyRectangles: irregular, MoveRectangles: null, IsKeyFrame: false);
                var tiles = enc.Encode(cf, true, 166, out var ft, out var mvs);
                byte[] pkt = FrameEncoder.Serialize(2, 2, 1, ft, w, h, mvs, tiles, 1);
                dec.DecodeFrame(pkt.AsSpan(1), out _, out _, out _, out _, out _, out var st);
                Assert.Equal(FrameDecodeStatus.Applied, st);
            }
            finally { pin.Free(); }

            var actualIrreg = SnapshotCanvasBgra(dec, w, h);
            Assert.InRange(Math.Abs(actualIrreg[(5 * w + 5) * 4 + 1] - 240), 0, 5);
            Assert.InRange(Math.Abs(actualIrreg[(200 * w + 60) * 4 + 2] - 250), 0, 5);
            Assert.InRange(Math.Abs(actualIrreg[(30 * w + 299) * 4 + 0] - 250), 0, 5);

            // > 32 個分散矩形應自動收斂合併為單一 Bounding Box，且不超界
            var many = Enumerable.Range(0, 40).Select(i => new Rectangle((i * 7) % 300, (i * 5) % 220, 4, 4)).ToList();
            var consolidated = FrameEncoder.ConsolidateDirtyRectangles(many, w, h);
            Assert.Single(consolidated);
            Assert.True(consolidated[0].Right <= w && consolidated[0].Bottom <= h);
        }

        // 7. 越界與負數輸入防護（編碼前對稱裁剪 + 降採樣防錯 + 解碼端惡意輸入拒收）
        {
            var negMoves = new[]
            {
                new MoveRectangle(new Point(-10, 20), new Rectangle(20, 20, 40, 30)),  // 左側負座標需對稱切掉前 10 行 -> src(0,20) dst(30,20) 30x30
                new MoveRectangle(new Point(w - 15, 10), new Rectangle(10, 10, 40, 20)), // 右側超界 -> src(w-15,10) dst(10,10) 15x20
                new MoveRectangle(new Point(-50, -50), new Rectangle(-40, -40, 10, 10))  // 完全在畫面外 -> 捨棄
            };
            var clippedNative = ResolutionProfileManager.ClipMoveRectangles(negMoves, w, h);
            Assert.NotNull(clippedNative);
            Assert.Equal(2, clippedNative!.Count);
            Assert.Equal(new MoveRectangle(new Point(0, 20), new Rectangle(30, 20, 30, 30)), clippedNative[0]);
            Assert.Equal(new MoveRectangle(new Point(w - 15, 10), new Rectangle(10, 10, 15, 20)), clippedNative[1]);

            var scaledSafe = ResolutionProfileManager.ScaleMoveRectangles(negMoves, w, h, 240, 180);
            Assert.NotNull(scaledSafe);
            foreach (var sm in scaledSafe!)
            {
                Assert.True(sm.SourcePoint.X >= 0 && sm.SourcePoint.Y >= 0);
                Assert.True(sm.DestinationRect.X >= 0 && sm.DestinationRect.Y >= 0);
                Assert.True(sm.SourcePoint.X + sm.DestinationRect.Width <= 240);
                Assert.True(sm.DestinationRect.Right <= 240 && sm.DestinationRect.Bottom <= 180);
            }

            var badDirty = new[] { new Rectangle(-20, -10, 30, 20), new Rectangle(w - 5, h - 5, 50, 50), new Rectangle(-100, -100, 10, 10), Rectangle.Empty };
            var scaledDirty = ResolutionProfileManager.ScaleDirtyRectangles(badDirty, w, h, 240, 180);
            Assert.Equal(2, scaledDirty.Count);
            Assert.All(scaledDirty, r =>
            {
                Assert.True(r.X >= 0 && r.Y >= 0 && r.Right <= 240 && r.Bottom <= 180 && r.Width > 0 && r.Height > 0);
            });

            // 解碼端收到未裁剪的惡意負數/超界 Move 封包仍須安全拒收並觸發 DesyncDetected
            var (enc, dec, _, _) = CreateAlignedPipeline();
            using var _d = dec;
            int desyncs = 0;
            dec.DesyncDetected += () => desyncs++;
            byte[] evil = new PacketWriter((byte)HostMsg.VideoFrame, 64)
                .U32(9).I32(w).I32(h).U32(2).U32(1).U8((byte)FrameType.MoveAndRegion | 0x80).U32(1)
                .U16(1).I32(-8).I32(10).I32(20).I32(20).I32(30).I32(30)
                .U16(0).ToArray();
            Assert.Equal(Rectangle.Empty, dec.DecodeFrame(evil.AsSpan(1), out _, out _, out _, out _, out _, out var badStatus));
            Assert.Equal(FrameDecodeStatus.Failed, badStatus);
            Assert.True(desyncs > 0);
        }
    }

    [Fact]
    public void Stage2D_Controller_All7Scenarios_BehavioralMatrix()
    {
        static AdaptiveSample HealthySample(long ts, bool active = true, float srcFps = 60f, float actualFps = 60f, float rttE2e = 25f) => new(
            NowMs: ts, SendQueueWaitMs: 2f, SendQueuePending: 0,
            UdpRawLossRatePercent: 0f, RetransmissionRatePercent: 0f, ArqFinalSuccessRatePercent: 100f,
            InflightRatio: 0.04f, RtoTimeoutsDelta: 0, FastRetransmitsDelta: 0,
            TargetFps: 60, ActualFps: actualFps, FrameDropsDelta: 0, EndToEndDelayMs: rttE2e,
            IsStreamActive: active, SendQueueWaitAvgMs: 1.5f, SendQueueWaitLastMs: 2f,
            SourceFps: active ? srcFps : 0f, CaptureFps: 60f, ViewerRenderFps: active ? actualFps : 0f,
            IsTransportConstrained: false);

        // 1. 靜態桌面恢復 (Active L1 -> Static Desktop 持續健康 12s -> 恢復 L0)
        {
            var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
            long t = 0;
            for (int i = 0; i < 8; i++)
            {
                t += 500;
                ctrl.Evaluate(new AdaptiveSample(t, 180f, 3, 0f, 0f, 100f, 0.5f, 0, 0, 60, 20f, 2, 220f, true));
            }
            Assert.Equal(ResolutionLevel.Level1, ctrl.CurrentLevel);

            for (int i = 0; i < 30 && ctrl.CurrentLevel != ResolutionLevel.Level0; i++)
            {
                t += 500;
                ctrl.Evaluate(HealthySample(t, active: false));
            }
            Assert.Equal(ResolutionLevel.Level0, ctrl.CurrentLevel);
        }

        // 2. 24/30 FPS 內容於 60 FPS 上限順暢播放後恢復至 L0
        {
            var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
            long t = 0;
            for (int i = 0; i < 8; i++)
            {
                t += 500;
                ctrl.Evaluate(new AdaptiveSample(t, 160f, 2, 0f, 0f, 100f, 0.4f, 0, 0, 60, 14f, 1, 200f, true, IsTransportConstrained: true));
            }
            Assert.Equal(ResolutionLevel.Level1, ctrl.CurrentLevel);

            for (int i = 0; i < 30 && ctrl.CurrentLevel != ResolutionLevel.Level0; i++)
            {
                t += 500;
                ctrl.Evaluate(HealthySample(t, active: true, srcFps: 24f, actualFps: 24f));
            }
            Assert.Equal(ResolutionLevel.Level0, ctrl.CurrentLevel);
        }

        // 3. TCP 壅塞降級與恢復
        {
            var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
            long t = 0;
            for (int i = 0; i < 20; i++)
            {
                t += 500;
                ctrl.Evaluate(new AdaptiveSample(t, 450f, 4, 0f, 0f, 100f, 0.8f, 0, 0, 30, 8f, 3, 500f, true, IsTransportConstrained: true));
            }
            Assert.Equal(ResolutionLevel.Level2, ctrl.CurrentLevel);
        }

        // 4. UDP 輕微可恢復掉包 (<=2%, ARQ=100%, RTO=0, QueueWait<100ms) 絕不誤降
        {
            var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Performance);
            long t = 0;
            for (int i = 0; i < 20; i++)
            {
                t += 500;
                ctrl.Evaluate(new AdaptiveSample(t, 5f, 0, 1.8f, 2.0f, 99.6f, 0.08f, 0, 1, 30, 30f, 0, 45f, true, ViewerRenderFps: 30f));
            }
            Assert.Equal(ResolutionLevel.Level0, ctrl.CurrentLevel);
        }

        // 5. UDP 嚴重掉包 (Loss=7%, ARQ=92%, RTO=3) 必須逐階降至 L2
        {
            var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
            long t = 0;
            for (int i = 0; i < 22; i++)
            {
                t += 500;
                ctrl.Evaluate(new AdaptiveSample(t, 120f, 2, 7.0f, 8.5f, 92.0f, 0.65f, 3, 2, 30, 12f, 2, 390f, true, IsTransportConstrained: true));
            }
            Assert.Equal(ResolutionLevel.Level2, ctrl.CurrentLevel);
        }

        // 6. 高 RTT (200ms -> E2E=115ms) 但無排隊、無掉包、無 TransportConstrained -> 維持原生 L0
        {
            var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
            long t = 0;
            for (int i = 0; i < 20; i++)
            {
                t += 500;
                // 模擬 Stage 2C 擴展視窗後可跑滿 25~30fps 且 queue wait / loss = 0
                ctrl.Evaluate(HealthySample(t, active: true, srcFps: 30f, actualFps: 27f, rttE2e: 115f));
            }
            Assert.Equal(ResolutionLevel.Level0, ctrl.CurrentLevel);
        }

        // 7. 頻寬驟降（緊急冷卻 2.5s 快速降級）-> 恢復 -> 再惡化指數拉長恢復門檻 (>12s)
        {
            var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
            long t = 0;
            for (int i = 0; i < 16; i++)
            {
                t += 500;
                ctrl.Evaluate(new AdaptiveSample(t, 900f, 5, 0f, 0f, 100f, 0.90f, 0, 0, 30, 5f, 4, 950f, true, IsTransportConstrained: true));
            }
            Assert.Equal(ResolutionLevel.Level2, ctrl.CurrentLevel);

            // 恢復至 L1 (12s 穩定後)
            for (int i = 0; i < 26 && ctrl.CurrentLevel == ResolutionLevel.Level2; i++)
            {
                t += 500;
                ctrl.Evaluate(HealthySample(t, active: false));
            }
            Assert.Equal(ResolutionLevel.Level1, ctrl.CurrentLevel);

            // 若剛升級後 15s 內立刻惡化 (Ping-Pong 再發)，需度過 DefaultCooldownMs (5000ms) 後降級並使恢復視窗指數倍增 (>= 24000ms)
            for (int i = 0; i < 11; i++)
            {
                t += 500;
                ctrl.Evaluate(new AdaptiveSample(t, 250f, 3, 0f, 0f, 100f, 0.65f, 0, 0, 30, 12f, 2, 360f, true));
            }
            Assert.Equal(ResolutionLevel.Level2, ctrl.CurrentLevel);
            Assert.True(ctrl.CurrentRecoveryWindowMs >= AdaptiveResolutionController.BaseRecoveryWindowMs * 2);
        }
    }

    [Fact]
    public async Task Stage2D_StreamConfig_AckLoss_Dup_Delay_OldVersion_Interleaved_And_Reconnect()
    {
        // 1. 單元階層：逾期舊版本影格 (v1 在 v2 配置後才抵達) -> StaleConfigVersion 不毀損畫布、不增 DecodeFailureCount
        using (var dec = new FrameDecoder())
        {
            int failCount = 0;
            dec.DesyncDetected += () => failCount++;

            dec.ConfigureStream(1, 320, 180);
            var kf1 = BuildSyntheticFullFramePacket(320, 180, Color.FromArgb(20, 40, 200), seq: 1, ver: 1, cfgVer: 1);
            var r1 = dec.DecodeFrame(kf1.AsSpan(1), out _, out _, out _, out _, out _, out var st1);
            Assert.Equal(FrameDecodeStatus.Applied, st1);
            Assert.False(r1.IsEmpty);

            // 切換至 cfgVer=2 (240x136)
            dec.ConfigureStream(2, 240, 136);
            var kf2 = BuildSyntheticFullFramePacket(240, 136, Color.FromArgb(40, 200, 60), seq: 2, ver: 2, cfgVer: 2);
            dec.DecodeFrame(kf2.AsSpan(1), out _, out _, out _, out _, out _, out var st2);
            Assert.Equal(FrameDecodeStatus.Applied, st2);
            byte[] v2SnapshotBeforeStale = SnapshotCanvasBgra(dec, 240, 136);

            // 舊 cfgVer=1 遲到封包抵達
            var stalePkt = BuildSyntheticFullFramePacket(320, 180, Color.FromArgb(250, 10, 10), seq: 3, ver: 3, cfgVer: 1);
            var staleRect = dec.DecodeFrame(stalePkt.AsSpan(1), out _, out _, out _, out _, out _, out var staleStatus);
            Assert.Equal(FrameDecodeStatus.StaleConfigVersion, staleStatus);
            Assert.Equal(Rectangle.Empty, staleRect);
            Assert.Equal(0, dec.DecodeFailureCount);
            Assert.Equal(0, failCount);
            Assert.Equal(v2SnapshotBeforeStale, SnapshotCanvasBgra(dec, 240, 136));
        }

        // 2. HostSession 實機迴路驗證 StreamConfig 切換時 ACK 遺失重送 (2000ms)、重複 ACK、逾期/未來偽造 ACK、連續切換插隊(v3未完成接v4)、斷線重連
        {
            DelayedThrottledStream? hostThrottle = null;
            var (hostCh, viewerCh) = await CreateTcpSecurePairAsync(hostWrapper: s =>
            {
                hostThrottle = new DelayedThrottledStream(s, oneWayDelayMs: 0, bandwidthMbps: 0);
                return hostThrottle;
            });
            using var _hCh1 = hostCh;
            using var _vCh1 = viewerCh;
            var capturer1 = new SyntheticCapturer(320, 180, Color.SteelBlue);
            var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "Mon1", 1, 0, 0, 320, 180, true, 96) };

            using var host1 = new HostSession(hostCh, new ClientHello(Protocol.Version, "910000002", "V1"), "127.0.0.1", new NullHostUi())
            {
                AllowDxgi = false,
                MonitorsOverride = () => monList,
                CapturerOverride = (_, _) => capturer1
            };

            using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var hostTask1 = Task.Run(host1.RunAsync);

            int cfgV2Count = 0;
            int cfgV3Seen = 0;
            int cfgV4Seen = 0;
            int dropFirstV2Ack = 1; // v2 首次故意遺失 ACK，測試 Host 2000ms 重送與恢復
            int holdV3Ack = 1;      // v3 扣住 ACK，測試立即切換 Original (v4) 插隊與逾期 v3 ACK 隔離
            var v2FrameCount = 0;
            var v4FrameCount = 0;
            var observedFrameCfgVersions = new ConcurrentBag<uint>();

            var viewerLoop1 = Task.Run(async () =>
            {
                using var dec = new FrameDecoder();
                while (!cts1.IsCancellationRequested)
                {
                    try
                    {
                        var msg = await viewerCh.ReceiveAsync(cts1.Token);
                        var r = new PacketReader(msg.AsSpan(1));
                        switch ((HostMsg)msg[0])
                        {
                            case HostMsg.StreamConfig:
                            {
                                var (cfgVer, _, _, sw, sh, _, _, _) = MessageCodec.ReadStreamConfig(ref r);
                                if (cfgVer == 2)
                                {
                                    int c = Interlocked.Increment(ref cfgV2Count);
                                    if (c == 1 && Interlocked.Exchange(ref dropFirstV2Ack, 0) == 1)
                                    {
                                        // 首次 v2 ACK 故意遺失，等待 HostSession >= 2000ms 重送
                                        hostThrottle!.SetBandwidthMbps(0);
                                        break;
                                    }
                                    dec.ConfigureStream(cfgVer, sw, sh);
                                    // 送出過期舊 ACK(1)、偽造未來 ACK(999)，再送兩份重複 ACK(2)
                                    await viewerCh.SendAsync(MessageCodec.StreamConfigAck(1, sw, sh), highPriority: true, cts1.Token);
                                    await viewerCh.SendAsync(MessageCodec.StreamConfigAck(999, sw, sh), highPriority: true, cts1.Token);
                                    await viewerCh.SendAsync(MessageCodec.StreamConfigAck(2, sw, sh), highPriority: true, cts1.Token);
                                    await viewerCh.SendAsync(MessageCodec.StreamConfigAck(2, sw, sh), highPriority: true, cts1.Token);
                                    break;
                                }
                                if (cfgVer == 3)
                                {
                                    Interlocked.Exchange(ref cfgV3Seen, 1);
                                    if (Volatile.Read(ref holdV3Ack) == 1)
                                    {
                                        // 扣住 v3 ACK，並立刻發起插隊切換 ResolutionMode.Original (產生 v4)
                                        hostThrottle!.SetBandwidthMbps(0);
                                        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5).U8(70).U8(30).Bool(false).U8((byte)ResolutionMode.Original).ToArray(), highPriority: true, cts1.Token);
                                        break;
                                    }
                                }
                                if (cfgVer == 4)
                                {
                                    // 先故意延遲送出已失效的 v3 ACK 與 v0 ACK，稍後再送正確 v4 ACK
                                    await viewerCh.SendAsync(MessageCodec.StreamConfigAck(3, sw, sh), highPriority: true, cts1.Token);
                                    await viewerCh.SendAsync(MessageCodec.StreamConfigAck(0, sw, sh), highPriority: true, cts1.Token);
                                    await Task.Delay(120, cts1.Token);
                                    dec.ConfigureStream(cfgVer, sw, sh);
                                    await viewerCh.SendAsync(MessageCodec.StreamConfigAck(cfgVer, sw, sh), highPriority: true, cts1.Token);
                                    await viewerCh.SendAsync(MessageCodec.StreamConfigAck(cfgVer, sw, sh), highPriority: true, cts1.Token);
                                    Interlocked.Exchange(ref cfgV4Seen, 1);
                                    break;
                                }

                                dec.ConfigureStream(cfgVer, sw, sh);
                                await viewerCh.SendAsync(MessageCodec.StreamConfigAck(cfgVer, sw, sh), highPriority: true, cts1.Token);
                                break;
                            }
                            case HostMsg.VideoFrame:
                            {
                                var dirty = dec.DecodeFrame(msg.AsSpan(1), out _, out _, out uint frameCfgVer, out _, out _, out var st);
                                if (frameCfgVer > 0) observedFrameCfgVersions.Add(frameCfgVer);
                                if (frameCfgVer == 2 && st == FrameDecodeStatus.Applied) Interlocked.Increment(ref v2FrameCount);
                                if (frameCfgVer == 4 && st == FrameDecodeStatus.Applied) Interlocked.Increment(ref v4FrameCount);
                                Assert.NotEqual(FrameDecodeStatus.Failed, st);
                                await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.FrameAck, 1).ToArray(), highPriority: true, cts1.Token);
                                break;
                            }
                        }
                    }
                    catch { break; }
                }
            });

            // 初始 cfgVer=1 建立後，注入高頻寬壅塞迫使 Host 觸發首次切換 cfgVer=2 (L0->L1)
            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5).U8(70).U8(30).Bool(false).U8((byte)ResolutionMode.Performance).ToArray(), highPriority: true, cts1.Token);
            for (int i = 0; i < 6 && observedFrameCfgVersions.IsEmpty; i++)
            {
                capturer1.PaintRect(new Rectangle(0, 0, 200, 150), Color.FromArgb(40 + i * 20, 100, 180));
                await Task.Delay(40);
            }
            Assert.Contains(1u, observedFrameCfgVersions);

            // 限速 0.02 Mbps + 塗色觸發 isSevere -> ~1.5s 降至 L1 (cfgVer=2)
            hostThrottle!.SetBandwidthMbps(0.02);
            for (int i = 0; i < 120 && Volatile.Read(ref cfgV2Count) == 0; i++)
            {
                capturer1.PaintRect(new Rectangle((i * 11) % 160, 0, 150, 150), Color.FromArgb((i * 53) % 255, (i * 29) % 255, 160));
                await Task.Delay(50);
            }
            Assert.True(Volatile.Read(ref cfgV2Count) >= 1, "應觸發 cfgVer=2 解析度降級切換");

            // 等待 Host >= 2000ms 未見 v2 ACK 而自動重送 cfgVer=2，並恢復輸送 v2 影格
            for (int i = 0; i < 70 && (Volatile.Read(ref cfgV2Count) < 2 || Volatile.Read(ref v2FrameCount) == 0); i++)
            {
                capturer1.PaintRect(new Rectangle((i * 13) % 160, 20, 120, 120), Color.FromArgb((i * 37) % 255, 120, 200));
                await Task.Delay(60);
            }
            Assert.True(cfgV2Count >= 2, $"ACK 遺失後 Host 應在 ~2000ms 重送 StreamConfig v2 (實際收到={cfgV2Count})");
            Assert.True(Volatile.Read(ref v2FrameCount) >= 1, "重複 ACK(2) 與舊版/未來無效 ACK 後應順利接收 cfgVer=2 影格");

            // 再次限速觸發 cfgVer=3 (L1->L2)，測試 v3 未獲 ACK 時被 Original(v4) 連續插隊切換
            hostThrottle.SetBandwidthMbps(0.02);
            for (int i = 0; i < 120 && Volatile.Read(ref cfgV4Seen) == 0; i++)
            {
                capturer1.PaintRect(new Rectangle((i * 17) % 160, 0, 150, 150), Color.FromArgb((i * 61) % 255, 200, (i * 41) % 255));
                await Task.Delay(50);
            }
            Assert.Equal(1, Volatile.Read(ref cfgV3Seen));
            Assert.Equal(1, Volatile.Read(ref cfgV4Seen));

            for (int i = 0; i < 30 && Volatile.Read(ref v4FrameCount) == 0; i++)
            {
                capturer1.PaintRect(new Rectangle(10, 10, 120, 120), Color.FromArgb(20 + i * 5, 220, 90));
                await Task.Delay(40);
            }
            Assert.True(Volatile.Read(ref v4FrameCount) >= 1, "連續插隊切換至 v4 且夾雜逾期 v3/v0 ACK 後應穩定解碼 v4 原生影格");

            // 斷線 cleanly
            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.Disconnect, 1).ToArray(), highPriority: true, cts1.Token);
            cts1.Cancel();
            host1.Dispose();
            try { await hostTask1; await viewerLoop1; } catch { }
        }

        // 3. 斷線後立刻重連新工作階段：必須回到乾淨初始狀態 (CfgVer=1, Level0, 無殘留狀態與解碼失敗)
        {
            var (hostCh2, viewerCh2) = await CreateTcpSecurePairAsync();
            using var _hCh2 = hostCh2;
            using var _vCh2 = viewerCh2;
            var capturer2 = new SyntheticCapturer(320, 180, Color.ForestGreen);
            var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "Mon1", 1, 0, 0, 320, 180, true, 96) };

            using var host2 = new HostSession(hostCh2, new ClientHello(Protocol.Version, "910000002", "V2"), "127.0.0.1", new NullHostUi())
            {
                AllowDxgi = false,
                MonitorsOverride = () => monList,
                CapturerOverride = (_, _) => capturer2
            };

            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var hostTask2 = Task.Run(host2.RunAsync);
            uint reconnectCfgVer = 0;
            ResolutionLevel reconnectLevel = ResolutionLevel.Level2;
            bool gotValidReconnectFrame = false;

            var viewerLoop2 = Task.Run(async () =>
            {
                using var dec2 = new FrameDecoder();
                while (!cts2.IsCancellationRequested)
                {
                    try
                    {
                        var msg = await viewerCh2.ReceiveAsync(cts2.Token);
                        var r = new PacketReader(msg.AsSpan(1));
                        switch ((HostMsg)msg[0])
                        {
                            case HostMsg.StreamConfig:
                            {
                                var (cfgVer, _, _, sw, sh, lvl, _, _) = MessageCodec.ReadStreamConfig(ref r);
                                reconnectCfgVer = cfgVer;
                                reconnectLevel = lvl;
                                dec2.ConfigureStream(cfgVer, sw, sh);
                                await viewerCh2.SendAsync(MessageCodec.StreamConfigAck(cfgVer, sw, sh), highPriority: true, cts2.Token);
                                break;
                            }
                            case HostMsg.VideoFrame:
                            {
                                var dirty = dec2.DecodeFrame(msg.AsSpan(1), out _, out _, out _, out _, out _, out var st);
                                if (st == FrameDecodeStatus.Applied && !dirty.IsEmpty)
                                    gotValidReconnectFrame = true;
                                await viewerCh2.SendAsync(new PacketWriter((byte)ViewerMsg.FrameAck, 1).ToArray(), highPriority: true, cts2.Token);
                                break;
                            }
                        }
                    }
                    catch { break; }
                }
            });

            await viewerCh2.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5).U8(75).U8(30).Bool(false).U8((byte)ResolutionMode.Auto).ToArray(), highPriority: true, cts2.Token);
            for (int i = 0; i < 50 && !gotValidReconnectFrame; i++)
            {
                await Task.Delay(30);
            }

            Assert.True(gotValidReconnectFrame);
            Assert.Equal(1u, reconnectCfgVer);
            Assert.Equal(ResolutionLevel.Level0, reconnectLevel);

            cts2.Cancel();
            host2.Dispose();
            try { await hostTask2; await viewerLoop2; } catch { }
        }
    }

    // =========================================================================
    // D. Stage 2 凍結階段：HostSession _inflightFrameSentAt FIFO + NACK 安全性實證與協定限制表徵
    // =========================================================================

    [Fact]
    public async Task Stage2_FifoNack_And_StaleFrameAck_FrozenProtocolLimitationCharacterization()
    {
        // A. 純順序 NACK 重置驗證：兩張影格在途滿載 (MaxInflight=2) 時送出 NACK，無逾時掉幀且不卡死 1200ms
        {
            var (hostCh, viewerCh) = await CreateTcpSecurePairAsync();
            using var _hCh = hostCh;
            using var _vCh = viewerCh;
            var capturer = new SyntheticCapturer(320, 180, Color.Navy);
            var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "Mon1", 1, 0, 0, 320, 180, true, 96) };

            using var host = new HostSession(hostCh, new ClientHello(Protocol.Version, "910000002", "NackCleanViewer"), "127.0.0.1", new NullHostUi())
            {
                AllowDxgi = false,
                MonitorsOverride = () => monList,
                CapturerOverride = (_, _) => capturer
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var hostTask = Task.Run(host.RunAsync);

            int preNackFrames = 0;
            int nackSent = 0;
            int postNackKeyframes = 0;
            var viewerLoop = Task.Run(async () =>
            {
                using var dec = new FrameDecoder();
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var msg = await viewerCh.ReceiveAsync(cts.Token);
                        var r = new PacketReader(msg.AsSpan(1));
                        switch ((HostMsg)msg[0])
                        {
                            case HostMsg.StreamConfig:
                            {
                                var (v, _, _, sw, sh, _, _, _) = MessageCodec.ReadStreamConfig(ref r);
                                dec.ConfigureStream(v, sw, sh);
                                await viewerCh.SendAsync(MessageCodec.StreamConfigAck(v, sw, sh), highPriority: true, cts.Token);
                                break;
                            }
                            case HostMsg.VideoFrame:
                            {
                                var dirty = dec.DecodeFrame(msg.AsSpan(1), out _, out _, out _, out _, out _, out var st);
                                if (Volatile.Read(ref nackSent) == 0)
                                {
                                    Interlocked.Increment(ref preNackFrames);
                                    // 故意扣住前 2 張影格不回 ACK，讓 MaxInflight=2 滿載在途
                                }
                                else
                                {
                                    if (st == FrameDecodeStatus.Applied && dirty.Width == 320 && dirty.Height == 180)
                                        Interlocked.Increment(ref postNackKeyframes);
                                    await Task.Delay(160, cts.Token);
                                    await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.FrameAck, 1).ToArray(), highPriority: true, cts.Token);
                                }
                                break;
                            }
                        }
                    }
                    catch { break; }
                }
            });

            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5).U8(75).U8(30).Bool(false).U8((byte)ResolutionMode.Auto).ToArray(), highPriority: true, cts.Token);

            // 塗 3 次更新，因未回 ACK，MaxInflight=2 僅能放行 2 張在途，第 3 張必被卡住
            for (int i = 0; i < 15 && Volatile.Read(ref preNackFrames) < 2; i++)
            {
                capturer.PaintRect(new Rectangle(10 + i * 5, 10, 80, 80), Color.FromArgb(40 + i * 10, 120, 200));
                await Task.Delay(30);
            }
            await Task.Delay(150);
            int blockedCount = Volatile.Read(ref preNackFrames);
            Assert.Equal(2, blockedCount);

            // 發送 NACK (RequestKeyframe) 清除舊 Inflight 與 _inflightFrameSentAt FIFO，並立即塗色送出新 Keyframe
            Interlocked.Exchange(ref nackSent, 1);
            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.RequestKeyframe, 1).ToArray(), highPriority: true, cts.Token);
            for (int i = 0; i < 25 && Volatile.Read(ref postNackKeyframes) == 0; i++)
            {
                capturer.PaintRect(new Rectangle(40, 40, 90, 90), Color.Orange);
                await Task.Delay(30);
            }
            await Task.Delay(600);

            Assert.True(Volatile.Read(ref postNackKeyframes) >= 1, "NACK 後應立即清除舊 2 張在途額度並送出新 Keyframe");
            Assert.NotNull(host.LatestTelemetry);
            Assert.Equal(0L, host.LatestTelemetry!.FrameDropCount);
            Assert.InRange(host.LatestTelemetry.RttMs, 110f, 250f);
            _output.WriteLine($"[順序 NACK 清除驗證] PreNackInflight={blockedCount}, PostNackKeyframes={postNackKeyframes}, Drop={host.LatestTelemetry.FrameDropCount}, RTT={host.LatestTelemetry.RttMs:F1}ms");

            cts.Cancel();
            host.Dispose();
            try { await hostTask; await viewerLoop; } catch { }
        }

        // B. 跨代延遲舊 ACK 交錯實測（現行凍結協定之限制表徵）：
        //    兩張在途 -> NACK 清除 -> 新 Keyframe(#3) 進入 FIFO -> 舊 FrameAck 於 #3 送出後才抵達
        {
            var (hostCh, viewerCh) = await CreateTcpSecurePairAsync();
            using var _hCh = hostCh;
            using var _vCh = viewerCh;
            var capturer = new SyntheticCapturer(320, 180, Color.DarkGreen);
            var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "Mon1", 1, 0, 0, 320, 180, true, 96) };

            using var host = new HostSession(hostCh, new ClientHello(Protocol.Version, "910000002", "StaleAckRaceViewer"), "127.0.0.1", new NullHostUi())
            {
                AllowDxgi = false,
                MonitorsOverride = () => monList,
                CapturerOverride = (_, _) => capturer
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var hostTask = Task.Run(host.RunAsync);

            int preNackFrames = 0;
            int nackPhase = 0; // 0=收前2張, 1=已送NACK等新Keyframe, 2=收到新Keyframe並扣留其真實ACK同時模擬2張舊ACK遲到
            int framesReceivedWhileNewKfUnacked = 0;
            long realAckNotYetSent = 1;

            var viewerLoop = Task.Run(async () =>
            {
                using var dec = new FrameDecoder();
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var msg = await viewerCh.ReceiveAsync(cts.Token);
                        var r = new PacketReader(msg.AsSpan(1));
                        switch ((HostMsg)msg[0])
                        {
                            case HostMsg.StreamConfig:
                            {
                                var (v, _, _, sw, sh, _, _, _) = MessageCodec.ReadStreamConfig(ref r);
                                dec.ConfigureStream(v, sw, sh);
                                await viewerCh.SendAsync(MessageCodec.StreamConfigAck(v, sw, sh), highPriority: true, cts.Token);
                                break;
                            }
                            case HostMsg.VideoFrame:
                            {
                                dec.DecodeFrame(msg.AsSpan(1), out _, out _, out _, out _, out _, out _);
                                int phase = Volatile.Read(ref nackPhase);
                                if (phase == 0)
                                {
                                    Interlocked.Increment(ref preNackFrames);
                                }
                                else if (phase == 1)
                                {
                                    // 新 Keyframe (t3) 已進入 Host FIFO；刻意不回任何新幀之 ACK，
                                    // 而在 ~10ms 後投遞先前屬於被 NACK 作廢影格的 2 個遲到舊 FrameAck (1-byte 0x48)
                                    Interlocked.Exchange(ref nackPhase, 2);
                                    Interlocked.Increment(ref framesReceivedWhileNewKfUnacked);
                                    await Task.Delay(10, cts.Token);
                                    await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.FrameAck, 1).ToArray(), highPriority: true, cts.Token);
                                    await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.FrameAck, 1).ToArray(), highPriority: true, cts.Token);
                                }
                                else if (phase == 2 && Interlocked.Read(ref realAckNotYetSent) == 1)
                                {
                                    Interlocked.Increment(ref framesReceivedWhileNewKfUnacked);
                                    // 持續扣留新世代影格的 ACK，驗證舊 ACK 是否錯誤提前釋放 _inflight 放行 >=3 張影格
                                }
                                break;
                            }
                        }
                    }
                    catch { break; }
                }
            });

            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5).U8(75).U8(30).Bool(false).U8((byte)ResolutionMode.Auto).ToArray(), highPriority: true, cts.Token);

            for (int i = 0; i < 15 && Volatile.Read(ref preNackFrames) < 2; i++)
            {
                capturer.PaintRect(new Rectangle(10 + i * 5, 10, 70, 70), Color.FromArgb(60 + i * 10, 140, 210));
                await Task.Delay(30);
            }
            Assert.Equal(2, Volatile.Read(ref preNackFrames));

            // 送出 NACK 並觸發新 Keyframe (#3) 進入 FIFO
            Interlocked.Exchange(ref nackPhase, 1);
            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.RequestKeyframe, 1).ToArray(), highPriority: true, cts.Token);

            // 等待 Viewer 收到 #3 並已投遞 2 個遲到的舊 FrameAck (~10ms)，期間再塗色 2 次測試 MaxInflight=2 是否被舊 ACK 偷釋放
            for (int i = 0; i < 20 && Volatile.Read(ref nackPhase) < 2; i++)
            {
                capturer.PaintRect(new Rectangle(20, 20, 100, 100), Color.Gold);
                await Task.Delay(20);
            }
            await Task.Delay(60); // 確保 2 個舊 FrameAck 已抵達 HostSession
            for (int i = 0; i < 6; i++)
            {
                capturer.PaintRect(new Rectangle(30 + i * 8, 30, 80, 80), Color.FromArgb(200, 40 + i * 25, 90));
                await Task.Delay(35);
            }
            await Task.Delay(550);

            var telBeforeRealAck = host.LatestTelemetry;
            Assert.NotNull(telBeforeRealAck);
            int leakedFrames = Volatile.Read(ref framesReceivedWhileNewKfUnacked);
            float mispairedRtt = telBeforeRealAck!.RttMs;

            // 表徵凍結協定的真實限制：現行 1-byte FrameAck (0x48) 無法辨識影格序號/Generation ID，
            // 因此舊 ACK 會錯誤配對剛進入 FIFO 的新影格並提前釋放 _inflight，導致 RTT 失真 (<50ms) 且允許放行 > MaxInflight(2) 張新影格。
            _output.WriteLine($"[凍結協定跨代舊 ACK 表徵] StaleAck後未經新ACK竟提前送出新影格數={leakedFrames} (理論應<=2), 遭舊ACK誤配污染後RTT={mispairedRtt:F1}ms (真實新ACK尚未送出), Drops={telBeforeRealAck.FrameDropCount}");
            Assert.True(leakedFrames >= 3 || mispairedRtt < 60f,
                "表徵驗證：現行 1-byte FrameAck 未帶 FrameSeq/GenerationId 時確實無法阻擋跨代舊 ACK 誤配新影格");

            // 最後才遲補發送真正的 ACK，確認不會拋出例外或崩潰
            Interlocked.Exchange(ref realAckNotYetSent, 0);
            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.FrameAck, 1).ToArray(), highPriority: true, cts.Token);
            await Task.Delay(80);

            cts.Cancel();
            host.Dispose();
            try { await hostTask; await viewerLoop; } catch { }
        }
    }
}
