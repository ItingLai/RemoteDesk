using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using RemoteDesk.Common;
using RemoteDesk.Host;
using RemoteDesk.Net;
using RemoteDesk.Viewer;
using Xunit;
using Xunit.Abstractions;

namespace RemoteDesk.Tests;

public class P0RegressionTests
{
    private readonly ITestOutputHelper _output;

    public P0RegressionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed class NullHostUi : IHostUi
    {
        public Task<bool> ConfirmAccessAsync(string viewerName, string viewerId, string address, string code, CancellationToken ct) =>
            Task.FromResult(true);
        public void SessionStarted(HostSession session) { }
        public void SessionEnded(HostSession session) { }
        public void SetClipboardText(string text) { }
    }

    /// <summary>
    /// 擬真擷取器：嚴格還原 DXGI (由 DWM 累積 DirtyRects 並在 Capture 時一次性消耗清除) 與 GDI (無元資料但每次拷貝畫面) 之行為差異。
    /// </summary>
    private sealed unsafe class MutableTestCapturer : IScreenCapturer
    {
        private readonly bool _dxgiMode;
        private readonly byte[] _surface;
        private readonly byte[] _exposedBuffer;
        private readonly System.Runtime.InteropServices.GCHandle _pinHandle;
        private readonly IntPtr _exposedPtr;
        private readonly object _lock = new();
        private readonly List<Rectangle> _pendingDirty = new();
        private bool _first = true;
        private int _captureCalls;

        public string Name => _dxgiMode ? "FakeDXGI" : "FakeGDI";
        public int Width { get; }
        public int Height { get; }
        public int CaptureCallCount => Volatile.Read(ref _captureCalls);
        public CapturedFrame Frame { get; private set; }

        public MutableTestCapturer(int width, int height, bool dxgiMode, Color initialColor)
        {
            Width = width;
            Height = height;
            _dxgiMode = dxgiMode;
            _surface = new byte[width * height * 4];
            _exposedBuffer = new byte[width * height * 4];
            _pinHandle = System.Runtime.InteropServices.GCHandle.Alloc(_exposedBuffer, System.Runtime.InteropServices.GCHandleType.Pinned);
            _exposedPtr = _pinHandle.AddrOfPinnedObject();

            FillRectInternal(new Rectangle(0, 0, width, height), initialColor);
            Array.Copy(_surface, _exposedBuffer, _surface.Length);
        }

        public void PaintRect(Rectangle rect, Color color)
        {
            lock (_lock)
            {
                FillRectInternal(rect, color);
                if (_dxgiMode)
                {
                    var clipped = Rectangle.Intersect(rect, new Rectangle(0, 0, Width, Height));
                    if (!clipped.IsEmpty) _pendingDirty.Add(clipped);
                }
            }
        }

        public void PaintPatternRect(Rectangle rect, int seed)
        {
            lock (_lock)
            {
                var r = Rectangle.Intersect(rect, new Rectangle(0, 0, Width, Height));
                if (r.IsEmpty) return;
                for (int y = r.Top; y < r.Bottom; y++)
                {
                    int rowOff = y * Width * 4;
                    for (int x = r.Left; x < r.Right; x++)
                    {
                        int o = rowOff + x * 4;
                        int h = ((x * 73 + y * 139 + seed * 31) ^ (x >> 2)) & 0xFF;
                        _surface[o] = (byte)h;
                        _surface[o + 1] = (byte)((h * 3 + y) & 0xFF);
                        _surface[o + 2] = (byte)((255 - h + seed * 17) & 0xFF);
                        _surface[o + 3] = 255;
                    }
                }
                if (_dxgiMode) _pendingDirty.Add(r);
            }
        }

        private void FillRectInternal(Rectangle rect, Color color)
        {
            var r = Rectangle.Intersect(rect, new Rectangle(0, 0, Width, Height));
            if (r.IsEmpty) return;
            for (int y = r.Top; y < r.Bottom; y++)
            {
                int rowOff = y * Width * 4;
                for (int x = r.Left; x < r.Right; x++)
                {
                    int o = rowOff + x * 4;
                    _surface[o] = color.B;
                    _surface[o + 1] = color.G;
                    _surface[o + 2] = color.R;
                    _surface[o + 3] = 255;
                }
            }
        }

        public bool Capture(int timeoutMs)
        {
            Interlocked.Increment(ref _captureCalls);
            lock (_lock)
            {
                if (_dxgiMode)
                {
                    bool isKey = _first;
                    if (!isKey && _pendingDirty.Count == 0)
                    {
                        if (timeoutMs > 0) Thread.Sleep(Math.Min(timeoutMs, 8));
                        return false;
                    }
                    _first = false;
                    Array.Copy(_surface, _exposedBuffer, _surface.Length);
                    var dirty = isKey
                        ? new[] { new Rectangle(0, 0, Width, Height) }
                        : _pendingDirty.ToArray();
                    _pendingDirty.Clear(); // DXGI 消耗並重設待取 Dirty Rects
                    Frame = new CapturedFrame(
                        _exposedPtr, Width, Height, Width * 4,
                        HasMetadata: true,
                        DirtyRectangles: dirty,
                        MoveRectangles: null,
                        IsKeyFrame: isKey);
                    return true;
                }
                else
                {
                    if (timeoutMs > 0 && !_first) Thread.Sleep(Math.Min(timeoutMs, 8));
                    _first = false;
                    Array.Copy(_surface, _exposedBuffer, _surface.Length);
                    Frame = new CapturedFrame(_exposedPtr, Width, Height, Width * 4, HasMetadata: false);
                    return true;
                }
            }
        }

        public void Dispose()
        {
            if (_pinHandle.IsAllocated) _pinHandle.Free();
        }
    }

    private sealed class GatedDelayStream : Stream
    {
        private readonly Stream _inner;
        private double _bytesPerMs;
        private readonly object _gate = new();
        private double _nextAllowedMs;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        public GatedDelayStream(Stream inner, double initialMbps)
        {
            _inner = inner;
            SetMbps(initialMbps);
        }

        public void SetMbps(double mbps)
        {
            lock (_gate) { _bytesPerMs = Math.Max(0.05, mbps * 1_000_000.0 / 8.0 / 1000.0); }
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            int delayMs = 0;
            lock (_gate)
            {
                double now = _sw.Elapsed.TotalMilliseconds;
                double dur = buffer.Length / _bytesPerMs;
                double end = Math.Max(now, _nextAllowedMs) + dur;
                _nextAllowedMs = end;
                delayMs = (int)Math.Max(0, end - now);
            }
            if (delayMs >= 2) await Task.Delay(Math.Min(delayMs, 350), ct).ConfigureAwait(false);
            await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        }
    }

    private static async Task<(SecureChannel hostCh, SecureChannel viewerCh, TcpListener listener, TcpClient client, TcpClient server, GatedDelayStream gated)>
        CreateLoopbackChannelsAsync(double initialMbps = 50.0)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var client = new TcpClient();
        var acceptTask = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var server = await acceptTask;
        var gated = new GatedDelayStream(server.GetStream(), initialMbps);

        var hTask = SecureChannel.ServerHandshakeAsync(
            gated,
            _ => new ServerHello(Protocol.Version, "900111222", "TestHost", Array.Empty<byte>(), 1, false, false, false),
            CancellationToken.None);
        var vTask = SecureChannel.ClientHandshakeAsync(client.GetStream(), "900333444", "TestViewer", CancellationToken.None);

        var (hostCh, _) = await hTask;
        var (viewerCh, _) = await vTask;
        return (hostCh, viewerCh, listener, client, server, gated);
    }

    private static Color SampleDecoderPixel(FrameDecoder dec, int x, int y)
    {
        using var bmp = new Bitmap(dec.Width, dec.Height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        dec.DrawTo(g, new Rectangle(0, 0, dec.Width, dec.Height));
        return bmp.GetPixel(x, y);
    }

    [Fact]
    public async Task P0_01_StaticDesktop_RealChannelAndUdp_QueueWaitClearsAndAllowsRecovery()
    {
        // A. 真實 SecureChannel 採樣視窗統計 (Last / WindowMax / WindowAverage) 與靜態桌面清除驗證
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        var acceptTask = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var server = await acceptTask;

        var gated = new GatedDelayStream(server.GetStream(), initialMbps: 0.4);
        var hTask = SecureChannel.ServerHandshakeAsync(
            gated,
            _ => new ServerHello(Protocol.Version, "900111222", "Host", Array.Empty<byte>(), 1, false, false, false),
            CancellationToken.None);
        var vTask = SecureChannel.ClientHandshakeAsync(client.GetStream(), "900333444", "Viewer", CancellationToken.None);
        var (hostCh, _) = await hTask;
        var (viewerCh, _) = await vTask;
        using var _h = hostCh;
        using var _v = viewerCh;

        using var recvCts = new CancellationTokenSource();
        var drainTask = Task.Run(async () =>
        {
            while (!recvCts.IsCancellationRequested)
            {
                try { await viewerCh.ReceiveAsync(recvCts.Token); } catch { break; }
            }
        });

        // 先建立排隊阻塞 (> 40ms wait)，使採樣視窗記錄實際等待時間
        var bigPayload = new byte[8 * 1024];
        var smallLow = new byte[128];
        var t1 = hostCh.SendAsync(bigPayload, highPriority: true, CancellationToken.None);
        await Task.Delay(5);
        var t2 = hostCh.SendAsync(smallLow, highPriority: false, CancellationToken.None);
        await Task.WhenAll(t1, t2);

        var activeWin = hostCh.ConsumeWaitWindowSnapshot();
        _output.WriteLine($"[SecureChannel 壅塞視窗] Last={activeWin.LastMs:F1}ms, WindowMax={activeWin.WindowMaxMs:F1}ms, WindowAvg={activeWin.WindowAverageMs:F1}ms, LastQueueWaitMs={hostCh.LastQueueWaitMs:F1}ms, Pending={hostCh.PendingLowPriorityCount}");
        Assert.True(activeWin.WindowMaxMs >= 20f && activeWin.LastMs >= 20f && activeWin.WindowAverageMs > 0f,
            $"視窗應正確捕捉阻塞等待，實測 Max={activeWin.WindowMaxMs:F1}, Last={activeWin.LastMs:F1}, Avg={activeWin.WindowAverageMs:F1}");

        // 下一個無發送事件的視窗必須完整歸零，不得沿用舊值
        var resetWin = hostCh.ConsumeWaitWindowSnapshot();
        Assert.Equal(0f, resetWin.LastMs);
        Assert.Equal(0f, resetWin.WindowMaxMs);
        Assert.Equal(0f, resetWin.WindowAverageMs);

        // 停止影片 -> 恢復高速網路並維持完全靜態桌面，驗證 LastQueueWaitMs 超過 500ms staleMs 也自然清除且狀態機恢復 Level 0
        gated.SetMbps(100.0);
        var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
        for (long t = 0; t <= 4000; t += 500)
        {
            ctrl.Evaluate(new AdaptiveSample(t, 160f, 3, 0f, 0f, 100f, 0.55f, 0, 0, 30, 12f, 2, 400f, true));
        }
        Assert.Equal(ResolutionLevel.Level1, ctrl.CurrentLevel);

        long simMs = 4500;
        for (int step = 0; step < 50; step++, simMs += 500)
        {
            await Task.Delay(50);
            float realWait = hostCh.LastQueueWaitMs;
            if (step == 15 || step == 35)
            {
                _output.WriteLine($"[SecureChannel 靜態閒置 step={step}] LastQueueWaitMs={realWait:F1}ms, Pending={hostCh.PendingLowPriorityCount}, Level={ctrl.CurrentLevel}");
            }
            ctrl.Evaluate(new AdaptiveSample(
                NowMs: simMs,
                SendQueueWaitMs: realWait,
                SendQueuePending: hostCh.PendingLowPriorityCount,
                UdpRawLossRatePercent: 0f,
                RetransmissionRatePercent: 0f,
                ArqFinalSuccessRatePercent: 100f,
                InflightRatio: 0f,
                RtoTimeoutsDelta: 0,
                FastRetransmitsDelta: 0,
                TargetFps: 30,
                ActualFps: 0f,
                FrameDropsDelta: 0,
                EndToEndDelayMs: 20f + realWait,
                IsStreamActive: false));
            if (ctrl.CurrentLevel == ResolutionLevel.Level0) break;
        }

        recvCts.Cancel();
        await drainTask;

        Assert.Equal(0f, hostCh.LastQueueWaitMs);
        Assert.Equal(ResolutionLevel.Level0, ctrl.CurrentLevel);
    }

    [Fact]
    public async Task P0_01_ReliableUdp_OngoingWaitPreservedWhilePendingZero_ThenRecoversLevel2ToLevel0()
    {
        using var sockA = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var sockB = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sockA.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        sockB.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        sockB.ReceiveBufferSize = 4 * 1024 * 1024;

        using var udpA = new ReliableUdpStream(sockA, sockB.LocalEndPoint!);
        // 故意先寫入超過 MaxWindowSize (256 * 1200 bytes) 的封包，此時 udpB 尚未啟動 ACK，#257 會卡在 _sendWindowSemaphore
        var oversized = new byte[260 * ReliableUdpStream.MaxPayloadSize];
        var blockedWriteTask = udpA.WriteAsync(oversized, CancellationToken.None).AsTask();

        // 等待 256 個視窗切片全數送出並確認進入第 257 個切片的 _sendWindowSemaphore 阻塞狀態
        for (int i = 0; i < 160 && udpA.CurrentInflightPackets < ReliableUdpStream.MaxWindowSize; i++)
        {
            await Task.Delay(25);
        }
        Assert.Equal(ReliableUdpStream.MaxWindowSize, udpA.CurrentInflightPackets);
        _ = udpA.ConsumeWaitWindowSnapshot(); // 清空前 256 個 0ms 樣本，單獨觀察第 257 個進行中阻塞

        await Task.Delay(120);
        Assert.False(blockedWriteTask.IsCompleted, "第 257 個切片應阻塞在發送滑動視窗信號量");

        // 即便通道上無額外排隊等待計數且尚未有已完成的等待樣本，仍在進行中的 Wait 必須在多次視窗消費中持續保留並累計
        var win1 = udpA.ConsumeWaitWindowSnapshot();
        await Task.Delay(70);
        var win2 = udpA.ConsumeWaitWindowSnapshot();
        _output.WriteLine($"[ReliableUdp OngoingWait Pending=0] Win1(Ongoing={win1.OngoingWaitMs:F1}ms, Eff={win1.EffectiveWaitMs:F1}ms, Samples={win1.SampleCount}), Win2(Ongoing={win2.OngoingWaitMs:F1}ms, Eff={win2.EffectiveWaitMs:F1}ms)");

        Assert.Equal(0, win1.SampleCount);
        Assert.True(win1.OngoingWaitMs >= 40f && win1.EffectiveWaitMs >= 40f);
        Assert.True(win2.OngoingWaitMs > win1.OngoingWaitMs && win2.EffectiveWaitMs >= 90f);

        // 建立 udpB 接收端清空並回傳 ACK，釋放卡住的傳送視窗
        using var udpB = new ReliableUdpStream(sockB, sockA.LocalEndPoint!);
        var recvBuf = new byte[64 * 1024];
        using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var readAllTask = Task.Run(async () =>
        {
            int total = 0;
            while (total < oversized.Length && !drainCts.IsCancellationRequested)
            {
                int n = await udpB.ReadAsync(recvBuf, drainCts.Token);
                if (n <= 0) break;
                total += n;
            }
            return total;
        });

        await blockedWriteTask.WaitAsync(TimeSpan.FromSeconds(10));
        int received = await readAllTask;
        Assert.Equal(oversized.Length, received);

        // 消費剛完成的樣本視窗，緊接著再消費下一個靜態閒置視窗 -> 必須重置為 0
        _ = udpA.ConsumeWaitWindowSnapshot();
        var idleWin = udpA.ConsumeWaitWindowSnapshot();
        _output.WriteLine($"[ReliableUdp Idle Reset] Last={idleWin.LastMs:F1}ms, Max={idleWin.WindowMaxMs:F1}ms, Avg={idleWin.WindowAverageMs:F1}ms, Ongoing={idleWin.OngoingWaitMs:F1}ms");
        Assert.Equal(0f, idleWin.EffectiveWaitMs);
        Assert.Equal(0f, idleWin.LastMs);
        Assert.Equal(0f, idleWin.WindowAverageMs);

        // 驗證狀態機可由 Level 2 -> Level 1 -> Level 0 完整階梯恢復
        var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
        for (long t = 0; t <= 12_000; t += 500)
        {
            ctrl.Evaluate(new AdaptiveSample(t, 450f, 3, 6f, 8f, 95f, 0.90f, 3, 2, 30, 10f, 2, 500f, true));
        }
        Assert.Equal(ResolutionLevel.Level2, ctrl.CurrentLevel);

        for (long t = 12_500; t <= 42_000; t += 500)
        {
            var snap = udpA.ConsumeWaitWindowSnapshot();
            ctrl.Evaluate(new AdaptiveSample(
                NowMs: t,
                SendQueueWaitMs: snap.EffectiveWaitMs,
                SendQueuePending: 0,
                UdpRawLossRatePercent: 0f,
                RetransmissionRatePercent: 0f,
                ArqFinalSuccessRatePercent: 100f,
                InflightRatio: 0f,
                RtoTimeoutsDelta: 0,
                FastRetransmitsDelta: 0,
                TargetFps: 30,
                ActualFps: 0f,
                FrameDropsDelta: 0,
                EndToEndDelayMs: 25f + snap.EffectiveWaitMs,
                IsStreamActive: false,
                SendQueueWaitAvgMs: snap.EffectiveAverageWaitMs,
                SendQueueWaitLastMs: snap.LastMs));
            if (ctrl.CurrentLevel == ResolutionLevel.Level0) break;
        }
        _output.WriteLine($"[ReliableUdp Level2->0 恢復] FinalLevel={ctrl.CurrentLevel}, LastReason={ctrl.LastReason}");
        Assert.Equal(ResolutionLevel.Level0, ctrl.CurrentLevel);
    }

    [Fact]
    public async Task P0_01_HostSession_EndToEnd_StaticDesktopClearsQueueWaitTelemetry()
    {
        const int w = 640, h = 360;
        var (hostCh, viewerCh, listener, client, server, gated) = await CreateLoopbackChannelsAsync(100.0);
        using var _1 = listener;
        using var _2 = client;
        using var _3 = server;
        using var _4 = hostCh;
        using var _5 = viewerCh;

        var capturer = new MutableTestCapturer(w, h, dxgiMode: false, Color.FromArgb(20, 40, 180));
        var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "TestMon", 1, 0, 0, w, h, true, 96) };
        var hello = new ClientHello(Protocol.Version, "900333444", "TestViewer");
        using var hostSession = new HostSession(hostCh, hello, "127.0.0.1", new NullHostUi())
        {
            AllowDxgi = false,
            MonitorsOverride = () => monList,
            CapturerOverride = (_, _) => capturer
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var hostTask = Task.Run(hostSession.RunAsync);
        var viewerTask = Task.Run(async () =>
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
                            var (ver, _, _, sw, sh, _, _, _) = MessageCodec.ReadStreamConfig(ref r);
                            await viewerCh.SendAsync(MessageCodec.StreamConfigAck(ver, sw, sh), highPriority: true, cts.Token);
                            break;
                        }
                        case HostMsg.VideoFrame:
                            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.FrameAck, 1).ToArray(), highPriority: true, cts.Token);
                            break;
                    }
                }
                catch { break; }
            }
        });

        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5)
            .U8(75).U8(30).Bool(false).U8((byte)ResolutionMode.Performance).ToArray(), highPriority: true, cts.Token);
        await Task.Delay(250);

        // 先用低頻寬 + 高變動畫面產生真實 QueueWait (> 20ms)
        gated.SetMbps(0.12);
        bool sawElevatedWait = false;
        for (int i = 0; i < 45; i++)
        {
            capturer.PaintRect(new Rectangle(0, 0, w, h), Color.FromArgb((i * 41) % 250, (i * 73) % 250, 90));
            await Task.Delay(40);
            var tel = hostSession.LatestTelemetry;
            if (tel != null && tel.SendQueueWaitMaxMs >= 20f)
            {
                sawElevatedWait = true;
                _output.WriteLine($"[HostSession 壅塞遙測] WaitLast={tel.SendQueueWaitLastMs:F1}ms, WaitMax={tel.SendQueueWaitMaxMs:F1}ms, WaitAvg={tel.SendQueueWaitAvgMs:F1}ms, Pending={tel.SendQueuePending}");
                break;
            }
        }
        Assert.True(sawElevatedWait, "HostSession 應在頻寬受限期間觀測到 >=20ms 的 SendQueueWaitMaxMs");

        // 恢復高速網路並完全停止畫面更新 (靜態桌面)
        gated.SetMbps(100.0);
        await Task.Delay(1300);

        var idleTel = hostSession.LatestTelemetry;
        Assert.NotNull(idleTel);
        _output.WriteLine($"[HostSession 靜態恢復後] WaitLast={idleTel!.SendQueueWaitLastMs:F1}ms, WaitMax={idleTel.SendQueueWaitMaxMs:F1}ms, WaitAvg={idleTel.SendQueueWaitAvgMs:F1}ms, WaitMs={idleTel.SendQueueWaitMs:F1}ms, Pending={idleTel.SendQueuePending}");

        cts.Cancel();
        hostSession.Dispose();
        await Task.WhenAny(Task.WhenAll(hostTask, viewerTask), Task.Delay(1000));

        Assert.Equal(0f, idleTel.SendQueueWaitMaxMs);
        Assert.Equal(0f, idleTel.SendQueueWaitLastMs);
        Assert.Equal(0f, idleTel.SendQueueWaitAvgMs);
        Assert.Equal(0f, idleTel.SendQueueWaitMs);
    }

    [Fact]
    public void P0_02_MaxFps60_With_24And30Fps_SourceContent_AllowsRecoveryAndNoFalseDrop()
    {
        var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);

        // 先模擬壅塞降至 Level 1
        for (long t = 0; t <= 4000; t += 500)
        {
            ctrl.Evaluate(new AdaptiveSample(t, 160f, 2, 5f, 6f, 97f, 0.60f, 2, 1, 60, 12f, 2, 420f, true, IsTransportConstrained: true));
        }
        Assert.Equal(ResolutionLevel.Level1, ctrl.CurrentLevel);

        // 切換為正常順暢播放 24 FPS 影片 (設定上限 TargetFps = 60, 通道完全順暢無堵塞)
        for (long t = 5000; t <= 22_000; t += 500)
        {
            ctrl.Evaluate(new AdaptiveSample(
                NowMs: t,
                SendQueueWaitMs: 2f,
                SendQueuePending: 0,
                UdpRawLossRatePercent: 0f,
                RetransmissionRatePercent: 0f,
                ArqFinalSuccessRatePercent: 100f,
                InflightRatio: 0.05f,
                RtoTimeoutsDelta: 0,
                FastRetransmitsDelta: 0,
                TargetFps: 60,
                ActualFps: 24f,
                FrameDropsDelta: 0,
                EndToEndDelayMs: 25f,
                IsStreamActive: true,
                SourceFps: 24f,
                CaptureFps: 60f,
                ViewerRenderFps: 24f,
                IsTransportConstrained: false));
        }

        _output.WriteLine($"[MaxFps=60 / 24FPS 正常影片] FinalLevel={ctrl.CurrentLevel}, LastReason={ctrl.LastReason}");
        Assert.Equal(ResolutionLevel.Level0, ctrl.CurrentLevel);
    }

    [Theory]
    [InlineData(60, 24f, 60f, 24f, 24f, false, ResolutionLevel.Level0)] // 60上限 / 24fps 正常影片 -> 不誤判且恢復 L0
    [InlineData(60, 30f, 60f, 30f, 30f, false, ResolutionLevel.Level0)] // 60上限 / 30fps 正常影片 -> 不誤判且恢復 L0
    [InlineData(60, 60f, 60f, 60f, 60f, false, ResolutionLevel.Level0)] // 60上限 / 60fps 全速順暢 -> 恢復 L0
    [InlineData(30, 24f, 30f, 24f, 24f, false, ResolutionLevel.Level0)] // 30上限 / 24fps 正常影片 -> 恢復 L0
    [InlineData(30, 60f, 30f, 30f, 30f, false, ResolutionLevel.Level0)] // 30上限 / 60fps 來源受上限節流 -> 恢復 L0
    [InlineData(60, 60f, 12f, 12f, 12f, true, ResolutionLevel.Level2)]  // 真實傳輸瓶頸(Send=12 vs Target=60) -> 持續卡死且可退至 L2
    [InlineData(60, 24f, 60f, 24f, 8f, false, ResolutionLevel.Level1)]  // Viewer 解碼渲染嚴重落後(Render=8 vs Send=24) -> 阻止誤恢復
    public void P0_02_FrameRateMatrix_AllowsHealthyRecoveryAndDetectsTrueBottleneck(
        int targetFps,
        float sourceFps,
        float captureFps,
        float sendFps,
        float viewerRenderFps,
        bool transportConstrained,
        ResolutionLevel expectedFinalLevel)
    {
        var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);

        // 先因初期壅塞進入 Level 1
        for (long t = 0; t <= 4000; t += 500)
        {
            ctrl.Evaluate(new AdaptiveSample(t, 160f, 2, 5f, 6f, 97f, 0.60f, 2, 1, targetFps, 12f, 2, 420f, true, IsTransportConstrained: true));
        }
        Assert.Equal(ResolutionLevel.Level1, ctrl.CurrentLevel);

        for (long t = 5000; t <= 25_000; t += 500)
        {
            float wait = transportConstrained ? 120f : 2f;
            float inflight = transportConstrained ? 0.50f : 0.05f;
            float e2e = transportConstrained ? 280f : 25f;
            ctrl.Evaluate(new AdaptiveSample(
                NowMs: t,
                SendQueueWaitMs: wait,
                SendQueuePending: 0,
                UdpRawLossRatePercent: 0f,
                RetransmissionRatePercent: 0f,
                ArqFinalSuccessRatePercent: 100f,
                InflightRatio: inflight,
                RtoTimeoutsDelta: 0,
                FastRetransmitsDelta: 0,
                TargetFps: targetFps,
                ActualFps: sendFps,
                FrameDropsDelta: 0,
                EndToEndDelayMs: e2e,
                IsStreamActive: true,
                SendQueueWaitAvgMs: wait * 0.8f,
                SendQueueWaitLastMs: wait,
                SourceFps: sourceFps,
                CaptureFps: captureFps,
                ViewerRenderFps: viewerRenderFps,
                IsTransportConstrained: transportConstrained));
        }

        _output.WriteLine($"[Matrix Target={targetFps}, Src={sourceFps}, Cap={captureFps}, Send={sendFps}, Render={viewerRenderFps}, Constrained={transportConstrained}] FinalLevel={ctrl.CurrentLevel}, Reason={ctrl.LastReason}");
        Assert.Equal(expectedFinalLevel, ctrl.CurrentLevel);
    }

    [Fact]
    public async Task P0_02_HostSession_Real24FpsSourceUnderMaxFps60_SeparatesRatesAndDoesNotFalseDowngrade()
    {
        const int w = 640, h = 360;
        var (hostCh, viewerCh, listener, client, server, _) = await CreateLoopbackChannelsAsync(100.0);
        using var _1 = listener;
        using var _2 = client;
        using var _3 = server;
        using var _4 = hostCh;
        using var _5 = viewerCh;

        var capturer = new MutableTestCapturer(w, h, dxgiMode: false, Color.FromArgb(40, 40, 140));
        var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "TestMon", 1, 0, 0, w, h, true, 96) };
        var hello = new ClientHello(Protocol.Version, "900333444", "TestViewer");
        using var hostSession = new HostSession(hostCh, hello, "127.0.0.1", new NullHostUi())
        {
            AllowDxgi = false,
            MonitorsOverride = () => monList,
            CapturerOverride = (_, _) => capturer
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var hostTask = Task.Run(hostSession.RunAsync);
        var viewerTask = Task.Run(async () =>
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
                            var (ver, _, _, sw, sh, _, _, _) = MessageCodec.ReadStreamConfig(ref r);
                            await viewerCh.SendAsync(MessageCodec.StreamConfigAck(ver, sw, sh), highPriority: true, cts.Token);
                            break;
                        }
                        case HostMsg.VideoFrame:
                            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.FrameAck, 1).ToArray(), highPriority: true, cts.Token);
                            break;
                    }
                }
                catch { break; }
            }
        });

        // 設定 TargetFps = 60 + Performance 模式，並以 ~24 FPS (每 42ms 更新一次局部) 播放 2.5 秒
        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5)
            .U8(75).U8(60).Bool(false).U8((byte)ResolutionMode.Performance).ToArray(), highPriority: true, cts.Token);

        for (int i = 0; i < 60; i++)
        {
            capturer.PaintRect(new Rectangle(20, 20, 120, 80), Color.FromArgb((i * 19) % 240, 160, (i * 31) % 240));
            await Task.Delay(42);
        }

        var tel = hostSession.LatestTelemetry;
        Assert.NotNull(tel);
        _output.WriteLine($"[HostSession 60/24 實測] Target={tel!.TargetFrameRate}, Capture={tel.CaptureFrameRate:F1}, Source={tel.SourceFrameRate:F1}, Send={tel.SendFrameRate:F1}, Level={tel.Level}, Reason={tel.LastSwitchReason}");

        cts.Cancel();
        hostSession.Dispose();
        await Task.WhenAny(Task.WhenAll(hostTask, viewerTask), Task.Delay(1000));

        Assert.Equal(60, tel.TargetFrameRate);
        Assert.True(tel.CaptureFrameRate > tel.SendFrameRate + 3f,
            $"CaptureFPS({tel.CaptureFrameRate:F1}) 應明確高於或分離於實際影片 Source/Send FPS({tel.SendFrameRate:F1})");
        Assert.InRange(tel.SourceFrameRate, 12f, 34f);
        Assert.Equal(ResolutionLevel.Level0, tel.Level);
    }

    [Theory]
    [InlineData(true)]  // DXGI 路徑
    [InlineData(false)] // GDI Fallback 路徑
    public async Task P0_03_WaitingForStreamConfigAck_PreservesUpdatesOnDxgiAndGdi(bool useDxgi)
    {
        const int w = 640, h = 360;
        var (hostCh, viewerCh, listener, client, server, gated) = await CreateLoopbackChannelsAsync(100.0);
        using var _1 = listener;
        using var _2 = client;
        using var _3 = server;
        using var _4 = hostCh;
        using var _5 = viewerCh;

        var capturer = new MutableTestCapturer(w, h, useDxgi, Color.FromArgb(30, 30, 200)); // 初始藍色
        var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "TestMon", 1, 0, 0, w, h, true, 96) };

        var hello = new ClientHello(Protocol.Version, "900333444", "TestViewer");
        using var hostSession = new HostSession(hostCh, hello, "127.0.0.1", new NullHostUi())
        {
            AllowDxgi = useDxgi,
            MonitorsOverride = () => monList,
            CapturerOverride = (_, _) => capturer
        };

        using var decoder = new FrameDecoder();
        uint ackedSwitchVer = 0;
        var configV2ArrivedTcs = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var v2KeyframeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ackV2SentTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int framesDecodedAfterAck = 0;
        bool initialKeyframeDecoded = false;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var hostTask = Task.Run(hostSession.RunAsync);

        var viewerTask = Task.Run(async () =>
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
                            var (ver, _, _, sw, sh, _, _, _) = MessageCodec.ReadStreamConfig(ref r);
                            decoder.ConfigureStream(ver, sw, sh);
                            if (ver >= 2)
                            {
                                configV2ArrivedTcs.TrySetResult(ver);
                                // 等收到 v2 首張 Keyframe 後刻意延遲 550ms (> FrameEncoder RefineDelayMs=350ms) 才回 ACK
                                _ = Task.Run(async () =>
                                {
                                    await v2KeyframeTcs.Task;
                                    await Task.Delay(550);
                                    await viewerCh.SendAsync(MessageCodec.StreamConfigAck(ver, sw, sh), highPriority: true, cts.Token);
                                    Volatile.Write(ref ackedSwitchVer, ver);
                                    ackV2SentTcs.TrySetResult(true);
                                });
                            }
                            else
                            {
                                await viewerCh.SendAsync(MessageCodec.StreamConfigAck(ver, sw, sh), highPriority: true, cts.Token);
                            }
                            break;
                        }
                        case HostMsg.VideoFrame:
                        {
                            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.FrameAck, 1).ToArray(), highPriority: true, cts.Token);
                            var dirty = decoder.DecodeFrame(msg.AsSpan(1), out _, out _, out _, out _, out var pktCfgVer);
                            if (!dirty.IsEmpty)
                            {
                                if (pktCfgVer == 1) initialKeyframeDecoded = true;
                                if (pktCfgVer >= 2) v2KeyframeTcs.TrySetResult(true);
                                if (Volatile.Read(ref ackedSwitchVer) >= 2 && pktCfgVer >= 2)
                                    Interlocked.Increment(ref framesDecodedAfterAck);
                            }
                            break;
                        }
                    }
                }
                catch { break; }
            }
        });

        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5)
            .U8(70).U8(30).Bool(false).U8((byte)ResolutionMode.Performance).ToArray(), highPriority: true, cts.Token);
        for (int i = 0; i < 60 && !initialKeyframeDecoded; i++) await Task.Delay(25);
        Assert.True(initialKeyframeDecoded);

        gated.SetMbps(0.12);
        for (int tick = 0; tick < 80 && !configV2ArrivedTcs.Task.IsCompleted; tick++)
        {
            capturer.PaintPatternRect(new Rectangle(0, 0, w, 140), tick + 1);
            await Task.Delay(50);
        }
        if (!configV2ArrivedTcs.Task.IsCompleted)
            await Task.WhenAny(configV2ArrivedTcs.Task, Task.Delay(4000));
        Assert.True(configV2ArrivedTcs.Task.IsCompleted, $"[{capturer.Name}] 應觸發降級發出 StreamConfig(v2)");
        gated.SetMbps(100.0);

        // 精確對齊：確認 Host 已送出 v2 Keyframe 並進入等待 ACK 暫停狀態後，於 ACK 抵達前不得再有任何 Capture()
        await v2KeyframeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        int capturesAtStartOfWait = capturer.CaptureCallCount;
        capturer.PaintRect(new Rectangle(120, 200, 160, 100), Color.FromArgb(20, 235, 20));
        await Task.Delay(320);
        int capturesDuringWaitDelta = capturer.CaptureCallCount - capturesAtStartOfWait;

        await ackV2SentTcs.Task;
        for (int i = 0; i < 60 && Volatile.Read(ref framesDecodedAfterAck) == 0; i++) await Task.Delay(25);
        await Task.Delay(250);

        int sampleX = (int)(200 * (decoder.Width / (double)w));
        int sampleY = (int)(250 * (decoder.Height / (double)h));
        Color px = SampleDecoderPixel(decoder, sampleX, sampleY);
        _output.WriteLine($"[{capturer.Name}] WaitCaptureDelta={capturesDuringWaitDelta}, Decoder={decoder.Width}x{decoder.Height}, Pixel({sampleX},{sampleY})={px}");

        cts.Cancel();
        hostSession.Dispose();
        await Task.WhenAny(Task.WhenAll(hostTask, viewerTask), Task.Delay(1000));

        Assert.True(capturesDuringWaitDelta == 0, $"[{capturer.Name}] 等待 StreamConfigAck 期間不應繼續呼叫 Capture() 空轉或消耗 DirtyRect，實際多呼叫次數={capturesDuringWaitDelta}");
        Assert.True(px.G > 180 && px.R < 80 && px.B < 80,
            $"[{capturer.Name}] 等待 StreamConfigAck 期間的畫面變更應完整呈現於 Viewer，但實際取得像素={px}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task P0_03_LostStreamConfigAck_RetriesAt2sAndPreservesContinuousUpdates(bool useDxgi)
    {
        const int w = 640, h = 360;
        var (hostCh, viewerCh, listener, client, server, gated) = await CreateLoopbackChannelsAsync(100.0);
        using var _1 = listener;
        using var _2 = client;
        using var _3 = server;
        using var _4 = hostCh;
        using var _5 = viewerCh;

        var capturer = new MutableTestCapturer(w, h, useDxgi, Color.FromArgb(30, 30, 200));
        var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "TestMon", 1, 0, 0, w, h, true, 96) };
        var hello = new ClientHello(Protocol.Version, "900333444", "TestViewer");
        using var hostSession = new HostSession(hostCh, hello, "127.0.0.1", new NullHostUi())
        {
            AllowDxgi = useDxgi,
            MonitorsOverride = () => monList,
            CapturerOverride = (_, _) => capturer
        };

        using var decoder = new FrameDecoder();
        int v2ConfigCount = 0;
        uint ackedSwitchVer = 0;
        var firstV2Tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstV2KeyframeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryV2AckedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int framesDecodedAfterRetryAck = 0;
        bool initialDecoded = false;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(18));
        var hostTask = Task.Run(hostSession.RunAsync);
        var viewerTask = Task.Run(async () =>
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
                            var (ver, _, _, sw, sh, _, _, _) = MessageCodec.ReadStreamConfig(ref r);
                            decoder.ConfigureStream(ver, sw, sh);
                            if (ver >= 2)
                            {
                                int seen = Interlocked.Increment(ref v2ConfigCount);
                                if (seen == 1)
                                {
                                    // 模擬首次 StreamConfigAck(v2) 遺失：不回覆 ACK
                                    firstV2Tcs.TrySetResult(true);
                                }
                                else
                                {
                                    // 收到 Host 2000ms 重送的 StreamConfig(v2) 後正常回覆 ACK
                                    await viewerCh.SendAsync(MessageCodec.StreamConfigAck(ver, sw, sh), highPriority: true, cts.Token);
                                    Volatile.Write(ref ackedSwitchVer, ver);
                                    retryV2AckedTcs.TrySetResult(true);
                                }
                            }
                            else
                            {
                                await viewerCh.SendAsync(MessageCodec.StreamConfigAck(ver, sw, sh), highPriority: true, cts.Token);
                            }
                            break;
                        }
                        case HostMsg.VideoFrame:
                        {
                            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.FrameAck, 1).ToArray(), highPriority: true, cts.Token);
                            var dirty = decoder.DecodeFrame(msg.AsSpan(1), out _, out _, out _, out _, out var pktCfgVer);
                            if (!dirty.IsEmpty)
                            {
                                if (pktCfgVer == 1) initialDecoded = true;
                                if (pktCfgVer >= 2) firstV2KeyframeTcs.TrySetResult(true);
                                if (Volatile.Read(ref ackedSwitchVer) >= 2 && pktCfgVer >= 2)
                                    Interlocked.Increment(ref framesDecodedAfterRetryAck);
                            }
                            break;
                        }
                    }
                }
                catch { break; }
            }
        });

        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5)
            .U8(70).U8(30).Bool(false).U8((byte)ResolutionMode.Performance).ToArray(), highPriority: true, cts.Token);
        for (int i = 0; i < 60 && !initialDecoded; i++) await Task.Delay(25);
        Assert.True(initialDecoded);

        gated.SetMbps(0.12);
        for (int tick = 0; tick < 80 && !firstV2Tcs.Task.IsCompleted; tick++)
        {
            capturer.PaintPatternRect(new Rectangle(0, 0, w, 140), tick + 101);
            await Task.Delay(50);
        }
        if (!firstV2Tcs.Task.IsCompleted)
            await Task.WhenAny(firstV2Tcs.Task, Task.Delay(4000));
        Assert.True(firstV2Tcs.Task.IsCompleted);
        gated.SetMbps(100.0);

        // 等待首張 v2 Keyframe 抵達 Viewer 並確認 Host 進入 2000ms 等待暫停，期間連續修改畫面且不得空轉 Capture()
        await firstV2KeyframeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        int capturesAtPauseStart = capturer.CaptureCallCount;
        for (int step = 0; step < 5; step++)
        {
            await Task.Delay(160);
            int cVal = Math.Min(230, 110 + step * 30);
            capturer.PaintRect(new Rectangle(120, 200, 160, 100), Color.FromArgb(cVal, 230, 30)); // 最終為亮黃色 (230,230,30)
        }
        int capturesDuring1200msPause = capturer.CaptureCallCount - capturesAtPauseStart;

        // 2000ms 重送後應收到重送 StreamConfig 並恢復推流
        await retryV2AckedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 60 && Volatile.Read(ref framesDecodedAfterRetryAck) == 0; i++) await Task.Delay(25);
        await Task.Delay(300);

        int sampleX = (int)(200 * (decoder.Width / (double)w));
        int sampleY = (int)(250 * (decoder.Height / (double)h));
        Color px = SampleDecoderPixel(decoder, sampleX, sampleY);
        _output.WriteLine($"[{capturer.Name} ACK遺失重送] Pause1200msCaptures={capturesDuring1200msPause}, v2Configs={v2ConfigCount}, Pixel={px}");

        cts.Cancel();
        hostSession.Dispose();
        await Task.WhenAny(Task.WhenAll(hostTask, viewerTask), Task.Delay(1000));

        Assert.Equal(0, capturesDuring1200msPause);
        Assert.True(v2ConfigCount >= 2, "2000ms 逾時後應重送 StreamConfig");
        Assert.True(px.R > 180 && px.G > 180 && px.B < 80,
            $"[{capturer.Name}] ACK 遺失重送期間連續畫面變更最終應完整呈現亮黃色，實測={px}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task P0_03_ConsecutiveSecondConfigSwitch_WhileFirstAckPending_IgnoresStaleAckAndConverges(bool useDxgi)
    {
        const int w = 640, h = 360;
        var (hostCh, viewerCh, listener, client, server, gated) = await CreateLoopbackChannelsAsync(100.0);
        using var _1 = listener;
        using var _2 = client;
        using var _3 = server;
        using var _4 = hostCh;
        using var _5 = viewerCh;

        var capturer = new MutableTestCapturer(w, h, useDxgi, Color.FromArgb(30, 30, 200));
        var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "TestMon", 1, 0, 0, w, h, true, 96) };
        var hello = new ClientHello(Protocol.Version, "900333444", "TestViewer");
        using var hostSession = new HostSession(hostCh, hello, "127.0.0.1", new NullHostUi())
        {
            AllowDxgi = useDxgi,
            MonitorsOverride = () => monList,
            CapturerOverride = (_, _) => capturer
        };

        using var decoder = new FrameDecoder();
        var v2ArrivedTcs = new TaskCompletionSource<(uint ver, int sw, int sh)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var v3ArrivedTcs = new TaskCompletionSource<(uint ver, int sw, int sh)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var v3KeyframeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseV3AckTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        uint v3Acked = 0;
        int v3DecodedCount = 0;
        bool initialDecoded = false;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var hostTask = Task.Run(hostSession.RunAsync);
        var viewerTask = Task.Run(async () =>
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
                            var (ver, _, _, sw, sh, _, _, _) = MessageCodec.ReadStreamConfig(ref r);
                            decoder.ConfigureStream(ver, sw, sh);
                            if (ver == 1)
                            {
                                await viewerCh.SendAsync(MessageCodec.StreamConfigAck(ver, sw, sh), highPriority: true, cts.Token);
                            }
                            else if (ver == 2)
                            {
                                // 暫緩 ACK v2，製造第一次切換尚未收到 ACK 就觸發第二次切換的情境
                                v2ArrivedTcs.TrySetResult((ver, sw, sh));
                            }
                            else if (ver >= 3)
                            {
                                v3ArrivedTcs.TrySetResult((ver, sw, sh));
                                _ = Task.Run(async () =>
                                {
                                    await releaseV3AckTcs.Task;
                                    await viewerCh.SendAsync(MessageCodec.StreamConfigAck(ver, sw, sh), highPriority: true, cts.Token);
                                    Volatile.Write(ref v3Acked, ver);
                                });
                            }
                            break;
                        }
                        case HostMsg.VideoFrame:
                        {
                            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.FrameAck, 1).ToArray(), highPriority: true, cts.Token);
                            var dirty = decoder.DecodeFrame(msg.AsSpan(1), out _, out _, out _, out _, out var pktCfgVer);
                            if (!dirty.IsEmpty)
                            {
                                if (pktCfgVer == 1) initialDecoded = true;
                                if (pktCfgVer >= 3) v3KeyframeTcs.TrySetResult(true);
                                if (Volatile.Read(ref v3Acked) >= 3 && pktCfgVer >= 3)
                                    Interlocked.Increment(ref v3DecodedCount);
                            }
                            break;
                        }
                    }
                }
                catch { break; }
            }
        });

        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5)
            .U8(70).U8(30).Bool(false).U8((byte)ResolutionMode.Performance).ToArray(), highPriority: true, cts.Token);
        for (int i = 0; i < 60 && !initialDecoded; i++) await Task.Delay(25);
        Assert.True(initialDecoded);

        gated.SetMbps(0.12);
        for (int tick = 0; tick < 80 && !v2ArrivedTcs.Task.IsCompleted; tick++)
        {
            capturer.PaintPatternRect(new Rectangle(0, 0, w, 140), tick + 201);
            await Task.Delay(50);
        }
        var v2Info = await v2ArrivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gated.SetMbps(100.0);

        // 在 Ack(v2) 尚未送達前，立即發起第二次切換 (Performance -> Original, 回到原生 640x360 = v3)
        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5)
            .U8(70).U8(30).Bool(false).U8((byte)ResolutionMode.Original).ToArray(), highPriority: true, cts.Token);
        var v3Info = await v3ArrivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(w, v3Info.sw);
        Assert.Equal(h, v3Info.sh);

        // 等待 v3 Keyframe 送抵 Viewer 後，故意先送回過期的 StreamConfigAck(v2)，確認 Host 不得誤判為 v3 已確認或繼續 Capture
        await v3KeyframeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        int capturesBeforeStaleAck = capturer.CaptureCallCount;
        await viewerCh.SendAsync(MessageCodec.StreamConfigAck(v2Info.ver, v2Info.sw, v2Info.sh), highPriority: true, cts.Token);
        capturer.PaintRect(new Rectangle(200, 180, 140, 120), Color.FromArgb(245, 130, 15)); // 亮橘色
        await Task.Delay(250);
        int capturesAfterStaleAck = capturer.CaptureCallCount - capturesBeforeStaleAck;

        // 再送出正確的 StreamConfigAck(v3)
        releaseV3AckTcs.TrySetResult(true);
        for (int i = 0; i < 60 && Volatile.Read(ref v3DecodedCount) == 0; i++) await Task.Delay(25);
        await Task.Delay(250);

        Color px = SampleDecoderPixel(decoder, 270, 240);
        _output.WriteLine($"[{capturer.Name} 連續切換] v2={v2Info.sw}x{v2Info.sh}, v3={v3Info.sw}x{v3Info.sh}, CapturesAfterStaleV2Ack={capturesAfterStaleAck}, FinalDecoder={decoder.Width}x{decoder.Height}, Pixel(270,240)={px}");

        cts.Cancel();
        hostSession.Dispose();
        await Task.WhenAny(Task.WhenAll(hostTask, viewerTask), Task.Delay(1000));

        Assert.Equal(0, capturesAfterStaleAck);
        Assert.Equal(w, decoder.Width);
        Assert.Equal(h, decoder.Height);
        Assert.True(px.R > 190 && px.G > 80 && px.G < 180 && px.B < 70,
            $"[{capturer.Name}] 連續第二次切換期間的亮橘色變更最終應於原生解析度完整收斂，實測={px}");
    }
}
