using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RemoteDesk.Common;
using RemoteDesk.Host;
using RemoteDesk.Net;
using RemoteDesk.Viewer;
using Xunit;
using Xunit.Abstractions;

namespace RemoteDesk.Tests;

public class P1RegressionTests
{
    private readonly ITestOutputHelper _output;

    public P1RegressionTests(ITestOutputHelper output)
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

    private sealed class SimpleMutableCapturer : IScreenCapturer
    {
        private readonly byte[] _frame;
        private readonly GCHandle _pin;
        private readonly IntPtr _ptr;
        private int _pendingDirty = 1;
        private Rectangle _dirtyRect;
        private bool _first = true;

        public string Name => "P1MockCapturer";
        public int Width { get; }
        public int Height { get; }
        public CapturedFrame Frame { get; private set; }

        public SimpleMutableCapturer(int w, int h, Color fill)
        {
            Width = w;
            Height = h;
            _frame = new byte[w * h * 4];
            _pin = GCHandle.Alloc(_frame, GCHandleType.Pinned);
            _ptr = _pin.AddrOfPinnedObject();
            _dirtyRect = new Rectangle(0, 0, w, h);
            Fill(fill);
        }

        public void Fill(Color c)
        {
            for (int i = 0; i < _frame.Length; i += 4)
            {
                _frame[i] = c.B;
                _frame[i + 1] = c.G;
                _frame[i + 2] = c.R;
                _frame[i + 3] = 255;
            }
            _pendingDirty = 1;
        }

        public void PaintRect(Rectangle rect, Color c)
        {
            for (int y = rect.Y; y < rect.Bottom && y < Height; y++)
            {
                for (int x = rect.X; x < rect.Right && x < Width; x++)
                {
                    int off = (y * Width + x) * 4;
                    _frame[off] = c.B;
                    _frame[off + 1] = c.G;
                    _frame[off + 2] = c.R;
                    _frame[off + 3] = 255;
                }
            }
            _dirtyRect = rect;
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

    // -------------------------------------------------------------------------
    // P1-01: ViewerForm 設定初始化 / 切換互斥 / Telemetry 不覆寫 / 傳輸標籤區分
    // -------------------------------------------------------------------------

    [Fact]
    public void P1_01_ViewerForm_InitializesFromSessionConfig_WithoutHardcoded70Percent()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"rd_p101_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var identity = DeviceIdentity.LoadOrCreate(tempRoot, "111222333", "TestViewer");
            var cfg50 = new ViewerConfig
            {
                ViewerId = "111222333",
                Quality = 50,
                MaxFps = 60,
                Lossless = false,
                ResolutionMode = ResolutionMode.Performance
            };
            using var session50 = new ViewerSession("192.168.1.50", cfg50, new LanDiscovery("111222333"), "111222333", identity);
            using var form50 = new ViewerForm(session50);

            Assert.Equal(50, form50.SelectedQuality);
            Assert.Equal(60, form50.SelectedMaxFps);
            Assert.False(form50.SelectedLossless);
            Assert.Equal("畫質: 低負載 (50%)", form50.QualityButtonText);
            Assert.Equal("解析度: 流暢優先", form50.ResolutionModeButtonText);
            Assert.Contains("品質:50%", form50.FpsLabelText);
            Assert.Contains("/60", form50.FpsLabelText);

            var cfg90 = new ViewerConfig
            {
                ViewerId = "111222333",
                Quality = 90,
                MaxFps = 60,
                Lossless = true,
                ResolutionMode = ResolutionMode.Original
            };
            using var session90 = new ViewerSession("192.168.1.50", cfg90, new LanDiscovery("111222333"), "111222333", identity);
            using var form90 = new ViewerForm(session90);

            Assert.Equal(90, form90.SelectedQuality);
            Assert.Equal(60, form90.SelectedMaxFps);
            Assert.True(form90.SelectedLossless);
            Assert.Equal("畫質: 極致 (90%)", form90.QualityButtonText);
            Assert.Equal("解析度: 原生鎖定", form90.ResolutionModeButtonText);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Fact]
    public void P1_01_SwitchingResolutionMode_DoesNotResetQualityFpsOrLossless()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"rd_p101_sw_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var identity = DeviceIdentity.LoadOrCreate(tempRoot, "111222333", "TestViewer");
            var cfg = new ViewerConfig
            {
                ViewerId = "111222333",
                Quality = 50,
                MaxFps = 60,
                Lossless = false,
                ResolutionMode = ResolutionMode.Auto
            };
            using var session = new ViewerSession("192.168.1.50", cfg, new LanDiscovery("111222333"), "111222333", identity);
            using var form = new ViewerForm(session);

            // 依序點擊 UI 下拉選單：Performance -> Original -> Auto
            form.ResolutionModeDropDown.DropDownItems[2].PerformClick();
            Assert.Equal((50, 60, false, ResolutionMode.Performance), session.LastSentVideoSettingsForTest);
            Assert.Equal("畫質: 低負載 (50%)", form.QualityButtonText);
            Assert.Equal(50, form.SelectedQuality);
            Assert.Equal(60, form.SelectedMaxFps);
            Assert.False(form.SelectedLossless);

            form.ResolutionModeDropDown.DropDownItems[1].PerformClick();
            Assert.Equal((50, 60, false, ResolutionMode.Original), session.LastSentVideoSettingsForTest);
            Assert.Equal("解析度: 原生鎖定", form.ResolutionModeButtonText);
            Assert.Equal("畫質: 低負載 (50%)", form.QualityButtonText);

            form.ResolutionModeDropDown.DropDownItems[0].PerformClick();
            Assert.Equal((50, 60, false, ResolutionMode.Auto), session.LastSentVideoSettingsForTest);
            Assert.Equal("解析度: 自動", form.ResolutionModeButtonText);
            Assert.Equal("畫質: 低負載 (50%)", form.QualityButtonText);

            // 再切換畫質為極致 (90%, 60 FPS, Lossless=true)，應保留當前 ResolutionMode=Auto
            form.QualityDropDown.DropDownItems[2].PerformClick();
            Assert.Equal((90, 60, true, ResolutionMode.Auto), session.LastSentVideoSettingsForTest);
            Assert.Equal("畫質: 極致 (90%)", form.QualityButtonText);
            Assert.Equal(ResolutionMode.Auto, session.CurrentResolutionMode);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Fact]
    public void P1_01_TelemetryAndRttUpdates_PreserveSettingsAndDistinguishLanTcpVsWanUdpP2p()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"rd_p101_tel_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var identity = DeviceIdentity.LoadOrCreate(tempRoot, "111222333", "TestViewer");
            var cfg = new ViewerConfig
            {
                ViewerId = "111222333",
                Quality = 50,
                MaxFps = 60,
                Lossless = false,
                ResolutionMode = ResolutionMode.Auto
            };

            // Case A: WAN UDP P2P
            using (var udpSession = new ViewerSession("999888777", cfg, new LanDiscovery("111222333"), "111222333", identity))
            {
                udpSession.TransportModeOverrideForTest = true;
                using var form = new ViewerForm(udpSession);

                Assert.Contains("WAN UDP P2P", form.LatencyLabelText);
                Assert.DoesNotContain("區網直連", form.LatencyLabelText);

                // 收到 Telemetry 前先收到 Ping/Pong RTT 更新
                long sentTs = Stopwatch.GetTimestamp() - (long)(0.042 * Stopwatch.Frequency);
                udpSession.DispatchForTest(new PacketWriter((byte)HostMsg.Pong, 16).I64(sentTs).ToArray());
                Assert.Contains("WAN UDP P2P", form.LatencyLabelText);
                Assert.DoesNotContain("區網直連", form.LatencyLabelText);

                // 切換成 Performance 後，收到在途舊 Auto Telemetry：不應覆寫剛選擇的模式與畫質
                form.ResolutionModeDropDown.DropDownItems[2].PerformClick();
                var staleTel = new HostStreamTelemetry
                {
                    ConfigVersion = 1,
                    Mode = ResolutionMode.Auto,
                    Level = ResolutionLevel.Level0,
                    OriginalWidth = 1920,
                    OriginalHeight = 1080,
                    StreamWidth = 1920,
                    StreamHeight = 1080,
                    Quality = 70, // 模擬尚未同步完的舊 Telemetry 品質值
                    TargetFps = 60,
                    ViewerRenderFps = 58f,
                    RttMs = 45f,
                    EndToEndDelayMs = 68f,
                    SendQueueWaitMs = 4f,
                    IsUdpTransport = true,
                    TransportSummary = "UDP P2P | Loss=0.5%"
                };
                udpSession.DispatchForTest(MessageCodec.StreamTelemetry(staleTel));

                Assert.Equal(ResolutionMode.Performance, udpSession.CurrentResolutionMode);
                Assert.Equal("解析度: 流暢優先", form.ResolutionModeButtonText);
                Assert.Equal("畫質: 低負載 (50%)", form.QualityButtonText);
                Assert.Equal(50, form.SelectedQuality);
                Assert.Contains("WAN UDP P2P", form.LatencyLabelText);
                Assert.Contains("E2E:68ms", form.LatencyLabelText);
                Assert.DoesNotContain("區網直連", form.LatencyLabelText);

                // Telemetry 到達後的下一次 RttUpdated 不得把 E2E/Q 洗掉或硬寫成 (區網直連)
                udpSession.DispatchForTest(new PacketWriter((byte)HostMsg.Pong, 16)
                    .I64(Stopwatch.GetTimestamp() - (long)(0.055 * Stopwatch.Frequency)).ToArray());
                Assert.Contains("WAN UDP P2P", form.LatencyLabelText);
                Assert.Contains("E2E:68ms", form.LatencyLabelText);
                Assert.DoesNotContain("區網直連", form.LatencyLabelText);

                // StreamConfigChanged 到達更新解析度標籤時亦不得覆寫 Quality / FPS / Lossless
                udpSession.DispatchForTest(MessageCodec.StreamConfig(2, 1920, 1080, 1280, 720, ResolutionLevel.Level1, ResolutionMode.Performance, "BW"));
                Assert.Contains("1280x720 (L1)", form.ResolutionLabelText);
                Assert.Equal("畫質: 低負載 (50%)", form.QualityButtonText);
                Assert.Equal(50, form.SelectedQuality);
                Assert.Equal(60, form.SelectedMaxFps);
                Assert.False(form.SelectedLossless);
            }

            // Case B: LAN TCP 直連 (IsUdpTransport = false)
            using (var tcpSession = new ViewerSession("192.168.1.99", cfg, new LanDiscovery("111222333"), "111222333", identity))
            {
                tcpSession.TransportModeOverrideForTest = false;
                using var form = new ViewerForm(tcpSession);

                Assert.Contains("LAN TCP 直連", form.LatencyLabelText);
                tcpSession.DispatchForTest(new PacketWriter((byte)HostMsg.Pong, 16)
                    .I64(Stopwatch.GetTimestamp() - (long)(0.004 * Stopwatch.Frequency)).ToArray());
                Assert.Contains("LAN TCP 直連", form.LatencyLabelText);

                var tcpTel = new HostStreamTelemetry
                {
                    ConfigVersion = 1,
                    Mode = ResolutionMode.Auto,
                    Level = ResolutionLevel.Level0,
                    OriginalWidth = 1920,
                    OriginalHeight = 1080,
                    StreamWidth = 1920,
                    StreamHeight = 1080,
                    Quality = 50,
                    TargetFps = 60,
                    ViewerRenderFps = 60f,
                    RttMs = 3f,
                    EndToEndDelayMs = 12f,
                    SendQueueWaitMs = 1f,
                    IsUdpTransport = false,
                    TransportSummary = "TCP LAN"
                };
                tcpSession.DispatchForTest(MessageCodec.StreamTelemetry(tcpTel));
                Assert.Contains("LAN TCP 直連", form.LatencyLabelText);
                Assert.Contains("E2E:12ms", form.LatencyLabelText);
            }
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // P1-02: AdaptiveResolutionController isBenignUdpLoss 作用域邊界與 TCP/UDP × Auto/Perf 交叉矩陣
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(ResolutionMode.Auto)]
    [InlineData(ResolutionMode.Performance)]
    public void P1_02_BenignUdpLoss_NeverDowngradesOnLossAlone(ResolutionMode mode)
    {
        var ctrl = new AdaptiveResolutionController(1920, 1080, mode);
        AdaptiveDecision last = default;
        for (long t = 0; t <= 6000; t += 500)
        {
            last = ctrl.Evaluate(new AdaptiveSample(
                NowMs: t,
                SendQueueWaitMs: 12f,
                SendQueuePending: 0,
                UdpRawLossRatePercent: 1.8f,
                RetransmissionRatePercent: 5.5f,
                ArqFinalSuccessRatePercent: 99.6f,
                InflightRatio: 0.12f,
                RtoTimeoutsDelta: 0,
                FastRetransmitsDelta: 1,
                TargetFps: 30,
                ActualFps: 30f,
                FrameDropsDelta: 0,
                EndToEndDelayMs: 65f,
                IsStreamActive: true,
                SendQueueWaitAvgMs: 8f,
                SendQueueWaitLastMs: 10f,
                SourceFps: 30f,
                CaptureFps: 30f,
                ViewerRenderFps: 30f,
                IsTransportConstrained: false));
            Assert.False(last.Changed);
        }
        Assert.Equal(ResolutionLevel.Level0, ctrl.CurrentLevel);
    }

    [Theory]
    [InlineData(false, ResolutionMode.Auto)]
    [InlineData(false, ResolutionMode.Performance)]
    [InlineData(true, ResolutionMode.Auto)]
    [InlineData(true, ResolutionMode.Performance)]
    public void P1_02_CrossMatrix_TcpAndUdp_AutoAndPerformance_StillDowngradesOnQueuePendingFpsLatencyAndInflight(
        bool isUdpBenignLoss,
        ResolutionMode mode)
    {
        float benignLoss = isUdpBenignLoss ? 1.2f : 0f;
        float benignRetrans = isUdpBenignLoss ? 4.0f : 0f;
        float benignArq = isUdpBenignLoss ? 99.7f : 100f;

        // 情境 1：QueueWait=65ms (< 80ms!) 或 SendQueuePending=3 + 高延遲 E2E + 掉幀率
        // 即使滿足 UdpRawLoss<=2% 與 SendQueueWaitMs<80ms，Performance 的 60ms 門檻與 Pending>=2 / FPS / Latency 仍必須觸發降級
        var ctrl1 = new AdaptiveResolutionController(1920, 1080, mode);
        float queueWait1 = mode == ResolutionMode.Performance ? 65f : 35f;
        int pending1 = mode == ResolutionMode.Performance ? 1 : 3;
        bool downgraded1 = false;
        for (long t = 0; t <= 4500; t += 500)
        {
            var dec = ctrl1.Evaluate(new AdaptiveSample(
                NowMs: t,
                SendQueueWaitMs: queueWait1,
                SendQueuePending: pending1,
                UdpRawLossRatePercent: benignLoss,
                RetransmissionRatePercent: benignRetrans,
                ArqFinalSuccessRatePercent: benignArq,
                InflightRatio: 0.15f,
                RtoTimeoutsDelta: 0,
                FastRetransmitsDelta: 0,
                TargetFps: 30,
                ActualFps: 14f,
                FrameDropsDelta: 0,
                EndToEndDelayMs: 400f,
                IsStreamActive: true,
                SourceFps: 30f,
                CaptureFps: 30f,
                ViewerRenderFps: 14f,
                IsTransportConstrained: true));
            if (dec.Changed && dec.NewLevel == ResolutionLevel.Level1)
            {
                downgraded1 = true;
                break;
            }
        }
        Assert.True(downgraded1, $"情境1應降級: udpBenign={isUdpBenignLoss}, mode={mode}");

        // 情境 2：SendQueueWaitMs 低 (<80ms)，但 InflightRatio 超過各模式門檻 (Auto>=0.45, Perf>=0.30) + E2E>=門檻，絕不得被良性遺失邏輯跳過
        var ctrl2 = new AdaptiveResolutionController(1920, 1080, mode);
        float highInflight = mode == ResolutionMode.Performance ? 0.35f : 0.50f;
        bool downgraded2 = false;
        for (long t = 0; t <= 4500; t += 500)
        {
            var dec = ctrl2.Evaluate(new AdaptiveSample(
                NowMs: t,
                SendQueueWaitMs: 25f,
                SendQueuePending: 0,
                UdpRawLossRatePercent: benignLoss,
                RetransmissionRatePercent: benignRetrans,
                ArqFinalSuccessRatePercent: benignArq,
                InflightRatio: highInflight,
                RtoTimeoutsDelta: 0,
                FastRetransmitsDelta: 0,
                TargetFps: 30,
                ActualFps: 28f,
                FrameDropsDelta: 0,
                EndToEndDelayMs: 380f,
                IsStreamActive: true,
                SourceFps: 30f,
                CaptureFps: 30f,
                ViewerRenderFps: 28f,
                IsTransportConstrained: true));
            if (dec.Changed && dec.NewLevel == ResolutionLevel.Level1)
            {
                downgraded2 = true;
                break;
            }
        }
        Assert.True(downgraded2, $"情境2(Inflight)應降級: udpBenign={isUdpBenignLoss}, mode={mode}");

        // 情境 3：UDP 高丟包 (>minLossForRetrans) 且 ARQ 失敗 (<99%) 應於 UDP 模式下計入 sigWindow
        if (isUdpBenignLoss)
        {
            var ctrl3 = new AdaptiveResolutionController(1920, 1080, mode);
            bool downgraded3 = false;
            for (long t = 0; t <= 4500; t += 500)
            {
                var dec = ctrl3.Evaluate(new AdaptiveSample(
                    NowMs: t,
                    SendQueueWaitMs: 30f,
                    SendQueuePending: 0,
                    UdpRawLossRatePercent: 5.0f,
                    RetransmissionRatePercent: 7.5f,
                    ArqFinalSuccessRatePercent: 94.0f,
                    InflightRatio: 0.20f,
                    RtoTimeoutsDelta: 0,
                    FastRetransmitsDelta: 3,
                    TargetFps: 30,
                    ActualFps: 15f,
                    FrameDropsDelta: 1,
                    EndToEndDelayMs: 200f,
                    IsStreamActive: true,
                    SourceFps: 30f,
                    CaptureFps: 30f,
                    ViewerRenderFps: 15f,
                    IsTransportConstrained: true));
                if (dec.Changed && dec.NewLevel == ResolutionLevel.Level1)
                {
                    downgraded3 = true;
                    break;
                }
            }
            Assert.True(downgraded3, $"UDP 非良性丟包應降級: mode={mode}");
        }
    }

    // -------------------------------------------------------------------------
    // P1-03: FrameDecoder 邊界溢位防護 + SendFrameAck / NACK / StaleConfig 恢復語意
    // -------------------------------------------------------------------------

    private static byte[] BuildSyntheticFullFrame(int w, int h, Color fill, uint seq = 1, uint ver = 1, uint cfgVer = 1)
    {
        using var cap = new SimpleMutableCapturer(w, h, fill);
        Assert.True(cap.Capture(0));
        var enc = new FrameEncoder(w, h) { Quality = 90, Lossless = true };
        enc.RequestKeyframe();
        var tiles = enc.Encode(cap.Frame, true, 0, out var frameType, out var moves);
        return FrameEncoder.Serialize(seq, ver, 0, frameType, w, h, moves, tiles, cfgVer);
    }

    private static Color SampleCanvasPixel(FrameDecoder decoder, int x, int y)
    {
        using var bmp = new Bitmap(decoder.Width, decoder.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            decoder.DrawTo(g, new Rectangle(0, 0, decoder.Width, decoder.Height));
        }
        return bmp.GetPixel(x, y);
    }

    [Fact]
    public void P1_03_ApplyMoveRectangles_GuardsAllBranches_AgainstNegativeZeroOutOfBoundsAndOverflow()
    {
        using var dec = new FrameDecoder();
        int w = 160, h = 96;
        var kf = BuildSyntheticFullFrame(w, h, Color.FromArgb(20, 120, 210), seq: 1, ver: 1, cfgVer: 1);
        dec.ConfigureStream(1, w, h);
        var dirty = dec.DecodeFrame(kf.AsSpan(1), out _, out _, out _, out _, out _, out var status);
        Assert.Equal(FrameDecodeStatus.Applied, status);
        Assert.False(dirty.IsEmpty);

        // 驗證所有分支均對惡意 MoveRectangle 傳回 false 且不拋擲溢位/越界/AccessViolation
        Assert.False(dec.ApplyMoveRectangles(new[] { new MoveRectangle(new Point(-1, 0), new Rectangle(0, 0, 16, 16)) }));
        Assert.False(dec.ApplyMoveRectangles(new[] { new MoveRectangle(new Point(0, 0), new Rectangle(-4, 0, 16, 16)) }));
        Assert.False(dec.ApplyMoveRectangles(new[] { new MoveRectangle(new Point(0, 0), new Rectangle(0, 0, 0, 16)) }));
        Assert.False(dec.ApplyMoveRectangles(new[] { new MoveRectangle(new Point(0, 0), new Rectangle(0, 0, 16, -2)) }));
        Assert.False(dec.ApplyMoveRectangles(new[] { new MoveRectangle(new Point(w - 8, 0), new Rectangle(0, 0, 16, 16)) }));
        Assert.False(dec.ApplyMoveRectangles(new[] { new MoveRectangle(new Point(0, 0), new Rectangle(w - 8, 0, 16, 16)) }));
        // 32-bit 加法溢位防護 (int.MaxValue - 2 + 10 < 0 會繞過 naive int 检查)
        Assert.False(dec.ApplyMoveRectangles(new[] { new MoveRectangle(new Point(int.MaxValue - 2, 0), new Rectangle(0, 0, 10, 10)) }));
        // 32-bit 乘法溢位防護 (w * h * 4)
        Assert.False(dec.ApplyMoveRectangles(new[] { new MoveRectangle(new Point(0, 0), new Rectangle(0, 0, 32768, 32768)) }));

        // 衝突分支 (hasConflict == true: move[0].Dst intersects move[1].Src) 中混入惡意越界/負座標/溢位 move[1]
        Assert.False(dec.ApplyMoveRectangles(new[]
        {
            new MoveRectangle(new Point(0, 0), new Rectangle(16, 16, 32, 32)),
            new MoveRectangle(new Point(20, 20), new Rectangle(w - 4, 20, 32, 32))
        }));
        Assert.False(dec.ApplyMoveRectangles(new[]
        {
            new MoveRectangle(new Point(0, 0), new Rectangle(16, 16, 32, 32)),
            new MoveRectangle(new Point(20, 20), new Rectangle(0, 0, 32768, 32768))
        }));

        // 合法衝突分支 (相互交換重疊區域) 應安全成功
        Assert.True(dec.ApplyMoveRectangles(new[]
        {
            new MoveRectangle(new Point(0, 0), new Rectangle(16, 16, 32, 32)),
            new MoveRectangle(new Point(16, 16), new Rectangle(0, 0, 32, 32))
        }));

        var sampled = SampleCanvasPixel(dec, 10, 10);
        Assert.InRange(sampled.R, 10, 35);
        Assert.InRange(sampled.G, 105, 135);
        Assert.InRange(sampled.B, 195, 225);
    }

    [Fact]
    public void P1_03_DecodeFrame_GuardsInvalidDimensionsTilesTruncatedAndCorruptPayloads()
    {
        using var dec = new FrameDecoder();
        int desyncs = 0;
        dec.DesyncDetected += () => desyncs++;

        int w = 128, h = 96;
        dec.ConfigureStream(1, w, h);
        var validKf = BuildSyntheticFullFrame(w, h, Color.FromArgb(40, 180, 90), seq: 1, ver: 1, cfgVer: 1);
        var okRect = dec.DecodeFrame(validKf.AsSpan(1), out _, out _, out _, out _, out _, out var okStatus);
        Assert.Equal(FrameDecodeStatus.Applied, okStatus);
        Assert.False(okRect.IsEmpty);
        Assert.Equal(0, dec.DecodeFailureCount);

        // 1. Overflow / Zero 解析度封包 (32768 x 32768 * 4 overflows int)
        var badDimPkt = new PacketWriter((byte)HostMsg.VideoFrame, 64)
            .U32(2).I32(32768).I32(32768).U32(2).U32(0).U8((byte)FrameType.FullFrame | 0x80).U32(1)
            .U16(0).U16(0).ToArray();
        Assert.Equal(Rectangle.Empty, dec.DecodeFrame(badDimPkt.AsSpan(1), out _, out _, out _, out _, out _, out var st1));
        Assert.Equal(FrameDecodeStatus.Failed, st1);

        // 2. 越界與 w*h*4 溢位 Tile (ushort w=65535, h=65535 -> 65535*65535*4 overflow int)
        var overflowTilePkt = new PacketWriter((byte)HostMsg.VideoFrame, 64)
            .U32(3).I32(w).I32(h).U32(3).U32(1).U8((byte)FrameType.RegionUpdate | 0x80).U32(1)
            .U16(0) // moveCount
            .U16(1) // tileCount
            .U16(0).U16(0).U16(65535).U16(65535).U8((byte)TileEncoding.Jpeg).Bytes(new byte[] { 0xFF, 0xD8, 0xFF })
            .ToArray();
        Assert.Equal(Rectangle.Empty, dec.DecodeFrame(overflowTilePkt.AsSpan(1), out _, out _, out _, out _, out _, out var st2));
        Assert.Equal(FrameDecodeStatus.Failed, st2);

        // 3. 含惡意 MoveRectangle 的封包
        var badMovePkt = new PacketWriter((byte)HostMsg.VideoFrame, 64)
            .U32(4).I32(w).I32(h).U32(4).U32(1).U8((byte)FrameType.MoveAndRegion | 0x80).U32(1)
            .U16(1)
            .I32(int.MaxValue - 1).I32(0).I32(0).I32(0).I32(8).I32(8)
            .U16(0).ToArray();
        Assert.Equal(Rectangle.Empty, dec.DecodeFrame(badMovePkt.AsSpan(1), out _, out _, out _, out _, out _, out var st3));
        Assert.Equal(FrameDecodeStatus.Failed, st3);

        // 4. 損毀的 JPEG Payload + 截斷封包 + 未知編碼
        var badJpegPkt = new PacketWriter((byte)HostMsg.VideoFrame, 64)
            .U32(5).I32(w).I32(h).U32(5).U32(1).U8((byte)FrameType.RegionUpdate | 0x80).U32(1)
            .U16(0).U16(1)
            .U16(0).U16(0).U16(16).U16(16).U8((byte)TileEncoding.Jpeg).Bytes(new byte[] { 0x00, 0x11, 0x22, 0x33 })
            .ToArray();
        Assert.Equal(Rectangle.Empty, dec.DecodeFrame(badJpegPkt.AsSpan(1), out _, out _, out _, out _, out _, out var st4));
        Assert.Equal(FrameDecodeStatus.Failed, st4);

        var truncatedPkt = new byte[] { (byte)HostMsg.VideoFrame, 1, 2, 3 };
        Assert.Equal(Rectangle.Empty, dec.DecodeFrame(truncatedPkt.AsSpan(1), out _, out _, out _, out _, out _, out var st5));
        Assert.Equal(FrameDecodeStatus.Failed, st5);

        // 5. 未知 TileEncoding 與截斷的 Deflate 資料
        var unknownEncPkt = new PacketWriter((byte)HostMsg.VideoFrame, 64)
            .U32(6).I32(w).I32(h).U32(6).U32(1).U8((byte)FrameType.RegionUpdate | 0x80).U32(1)
            .U16(0).U16(1)
            .U16(0).U16(0).U16(16).U16(16).U8(0x7F).Bytes(new byte[] { 1, 2, 3, 4 })
            .ToArray();
        Assert.Equal(Rectangle.Empty, dec.DecodeFrame(unknownEncPkt.AsSpan(1), out _, out _, out _, out _, out _, out var st6));
        Assert.Equal(FrameDecodeStatus.Failed, st6);

        var truncatedDeflatePkt = new PacketWriter((byte)HostMsg.VideoFrame, 64)
            .U32(7).I32(w).I32(h).U32(7).U32(1).U8((byte)FrameType.RegionUpdate | 0x80).U32(1)
            .U16(0).U16(1)
            .U16(0).U16(0).U16(16).U16(16).U8((byte)TileEncoding.Deflate).Bytes(new byte[] { 0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01 })
            .ToArray();
        Assert.Equal(Rectangle.Empty, dec.DecodeFrame(truncatedDeflatePkt.AsSpan(1), out _, out _, out _, out _, out _, out var st7));
        Assert.Equal(FrameDecodeStatus.Failed, st7);

        Assert.Equal(7, dec.DecodeFailureCount);
        Assert.Equal(7, desyncs);
        Assert.Equal(1u, dec.CanvasVersion);
        Assert.True(dec.HasValidBaseline);
        var px = SampleCanvasPixel(dec, 20, 20);
        Assert.InRange(px.G, 165, 195);
    }

    [Fact]
    public void P1_03_ViewerSession_ExplicitAckNackAndStaleConfigBehavior()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"rd_p103_ack_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var identity = DeviceIdentity.LoadOrCreate(tempRoot, "111222333", "TestViewer");
            var cfg = new ViewerConfig { ViewerId = "111222333" };
            using var session = new ViewerSession("127.0.0.1", cfg, new LanDiscovery("111222333"), "111222333", identity);

            int w = 160, h = 90;
            session.DispatchForTest(MessageCodec.StreamConfig(1, w, h, w, h, ResolutionLevel.Level0, ResolutionMode.Auto, "Init"));

            // 1. 合法影格：必須先解碼完成，再送 FrameAck，且不觸發 Keyframe
            var kf1 = BuildSyntheticFullFrame(w, h, Color.FromArgb(50, 90, 200), seq: 1, ver: 1, cfgVer: 1);
            session.DispatchForTest(kf1);
            Assert.Equal(FrameDecodeStatus.Applied, session.LastDecodeStatusForTest);
            Assert.Equal(1, session.FrameAckCountForTest);
            Assert.Equal(0, session.KeyframeRequestCountForTest);
            Assert.Equal(0, session.Decoder.DecodeFailureCount);
            Assert.True(session.LastFrameAckSentTicks >= session.LastFrameDecodeCompletedTicks);

            // 2. 失步增量影格 (baseVersion != 1)：觸發 DesyncDetected + Keyframe NACK，不得誤送 FrameAck，不增加 DecodeFailureCount
            var desyncedDelta = new PacketWriter((byte)HostMsg.VideoFrame, 64)
                .U32(2).I32(w).I32(h).U32(99).U32(55).U8((byte)FrameType.RegionUpdate | 0x80).U32(1)
                .U16(0).U16(0).ToArray();
            session.DispatchForTest(desyncedDelta);
            Assert.Equal(FrameDecodeStatus.Desynced, session.LastDecodeStatusForTest);
            Assert.Equal(0, session.Decoder.DecodeFailureCount);
            Assert.Equal(1, session.KeyframeRequestCountForTest);
            Assert.Equal(1, session.FrameAckCountForTest); // 維持 1，不對失步影格回 FrameAck

            // 3. 損毀 JPEG 影格：增加 DecodeFailureCount + 觸發 Keyframe NACK，不得誤送 FrameAck
            var badJpegFrame = new PacketWriter((byte)HostMsg.VideoFrame, 64)
                .U32(3).I32(w).I32(h).U32(2).U32(1).U8((byte)FrameType.RegionUpdate | 0x80).U32(1)
                .U16(0).U16(1)
                .U16(0).U16(0).U16(16).U16(16).U8((byte)TileEncoding.Jpeg).Bytes(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF })
                .ToArray();
            session.DispatchForTest(badJpegFrame);
            Assert.Equal(FrameDecodeStatus.Failed, session.LastDecodeStatusForTest);
            Assert.Equal(1, session.Decoder.DecodeFailureCount);
            Assert.Equal(2, session.KeyframeRequestCountForTest);
            Assert.Equal(1, session.FrameAckCountForTest); // 仍維持 1，不對壞影格誤回 FrameAck

            // 4. 切換至組態 v2 後收到過期 cfgVer=1 影格：不破圖、不計 DecodeFailure、不觸發多餘 Keyframe，並回 FrameAck 釋放主機舊槽位
            session.DispatchForTest(MessageCodec.StreamConfig(2, w, h, 128, 72, ResolutionLevel.Level1, ResolutionMode.Auto, "Switch"));
            var staleOldFrame = BuildSyntheticFullFrame(w, h, Color.Red, seq: 4, ver: 4, cfgVer: 1);
            session.DispatchForTest(staleOldFrame);
            Assert.Equal(FrameDecodeStatus.StaleConfigVersion, session.LastDecodeStatusForTest);
            Assert.Equal(1, session.Decoder.DecodeFailureCount); // 未增加
            Assert.Equal(2, session.KeyframeRequestCountForTest); // 未增加
            Assert.Equal(2, session.FrameAckCountForTest); // 釋放舊組態管線額度
            Assert.Equal(2u, session.Decoder.StreamConfigVersion);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Fact]
    public async Task P1_03_ReliableUdpAndHostSession_RequestKeyframeNackClearsInflightAndRecoversWithout1200msDeadlock()
    {
        // 建立真實回環 ReliableUdpStream 配對，驗證：
        // 1. Reliable UDP TypeAck (0x02) 獨立運作於應用層 FrameAck 之外
        // 2. HostSession 滿載 _inflight == MaxInflight 時，收到 Viewer RequestKeyframe (顯式 NACK，無 FrameAck)
        //    能立即歸零 _inflight 並於 500ms 內送出重同步 FullFrame，免除 1200ms 死等超時。
        using var udpHostSock = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var udpViewerSock = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var hostEp = (IPEndPoint)udpHostSock.Client.LocalEndPoint!;
        var viewerEp = (IPEndPoint)udpViewerSock.Client.LocalEndPoint!;

        using var hostUdp = new ReliableUdpStream(udpHostSock.Client, viewerEp);
        using var viewerUdp = new ReliableUdpStream(udpViewerSock.Client, hostEp);
        var hostChTask = SecureChannel.ServerHandshakeAsync(
            hostUdp,
            _ => new ServerHello(Protocol.Version, "900111222", "UdpHost", Array.Empty<byte>(), 1, false, false, false),
            CancellationToken.None);
        var viewerChTask = SecureChannel.ClientHandshakeAsync(viewerUdp, "900333444", "UdpNackTester", CancellationToken.None);
        using var hostCh = (await hostChTask).channel;
        using var viewerCh = (await viewerChTask).channel;

        int w = 160, h = 90;
        var capturer = new SimpleMutableCapturer(w, h, Color.FromArgb(30, 80, 180));
        var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "UdpTestMon", 1, 0, 0, w, h, true, 96) };

        var hello = new ClientHello(Protocol.Version, "900111222", "UdpNackTester");
        using var hostSession = new HostSession(hostCh, hello, viewerEp.ToString(), new NullHostUi())
        {
            AllowDxgi = false,
            MonitorsOverride = () => monList,
            CapturerOverride = (_, _) => capturer
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var hostTask = Task.Run(hostSession.RunAsync);

        using var decoder = new FrameDecoder();
        int validFrames = 0;
        int nacksSent = 0;
        var recoveredAfterNack = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        long nackSentAtMs = 0;

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
                            await viewerCh.SendAsync(MessageCodec.StreamConfigAck(ver, sw, sh), highPriority: true, cts.Token);
                            break;
                        }
                        case HostMsg.VideoFrame:
                        {
                            // 當收到前兩張增量影格且尚未送 NACK 時，故意扣住 FrameAck 使 Host 達 MaxInflight=2，
                            // 接著改發 RequestKeyframe (NACK) 不回 FrameAck，驗證是否立刻出 FullFrame 且無需苦等 1200ms！
                            if (validFrames >= 1 && Interlocked.Read(ref nackSentAtMs) == 0 && nacksSent < 2)
                            {
                                int count = Interlocked.Increment(ref nacksSent);
                                // 第 1 張不回 FrameAck 也不送 Keyframe；第 2 張 (Host _inflight=2 卡住點) 才發 RequestKeyframe
                                if (count == 2)
                                {
                                    Interlocked.Exchange(ref nackSentAtMs, Environment.TickCount64);
                                    await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.RequestKeyframe, 1).ToArray(), highPriority: true, cts.Token);
                                }
                                break;
                            }

                            var dirty = decoder.DecodeFrame(msg.AsSpan(1), out _, out _, out _, out var fType, out _, out var st);
                            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.FrameAck, 1).ToArray(), highPriority: true, cts.Token);
                            if (st == FrameDecodeStatus.Applied && !dirty.IsEmpty)
                            {
                                Interlocked.Increment(ref validFrames);
                                long sentAt = Interlocked.Read(ref nackSentAtMs);
                                if (sentAt > 0 && fType == FrameType.FullFrame)
                                {
                                    recoveredAfterNack.TrySetResult(Environment.TickCount64 - sentAt);
                                }
                            }
                            break;
                        }
                    }
                }
                catch { break; }
            }
        });

        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5)
            .U8(70).U8(30).Bool(false).U8((byte)ResolutionMode.Auto).ToArray(), highPriority: true, cts.Token);

        // 等待首張 Keyframe，再連續製造兩次畫面變更以把 Host _inflight 填滿至 MaxInflight=2
        for (int i = 0; i < 60 && Volatile.Read(ref validFrames) < 1; i++)
        {
            await Task.Delay(25);
        }
        Assert.True(Volatile.Read(ref validFrames) >= 1, "未收到初始 Keyframe");

        capturer.PaintRect(new Rectangle(0, 0, 40, 40), Color.FromArgb(200, 40, 40));
        await Task.Delay(50);
        capturer.PaintRect(new Rectangle(40, 0, 40, 40), Color.FromArgb(40, 200, 40));

        var completed = await Task.WhenAny(recoveredAfterNack.Task, Task.Delay(3000));
        Assert.Same(recoveredAfterNack.Task, completed);
        double recoveryMs = await recoveredAfterNack.Task;
        _output.WriteLine($"UDP P2P MaxInflight=2 滿載下 RequestKeyframe (NACK) 恢復 FullFrame 耗時: {recoveryMs:F1} ms (ReliableUDP TypeAck={hostUdp.TotalAcksReceived})");

        Assert.True(recoveryMs < 700, $"預期 RequestKeyframe NACK 立即清除 _inflight (<700ms 遠低於 1200ms 逾時)，實際耗時 {recoveryMs:F1} ms");
        Assert.True(hostUdp.TotalAcksReceived > 0, "Reliable UDP 傳輸層 TypeAck 應持續獨立運作");

        cts.Cancel();
        hostSession.Dispose();
        try { await hostTask; } catch { }
        try { await viewerTask; } catch { }
    }
}
