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

public class AdaptiveResolutionTests
{
    private readonly ITestOutputHelper _output;

    public AdaptiveResolutionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Test_ResolutionProfile_DynamicProportions_MultiDisplay()
    {
        var cases = new (int ow, int oh, int l1w, int l1h, int l2w, int l2h)[]
        {
            (1920, 1080, 1440, 810, 960, 540),
            (2560, 1440, 1920, 1080, 1280, 720),
            (3840, 2160, 2880, 1620, 1920, 1080),
            (1366, 768, 1024, 576, 684, 384),
            (2560, 1080, 1920, 810, 1280, 540),
        };

        foreach (var c in cases)
        {
            var profiles = ResolutionProfileManager.BuildProfiles(c.ow, c.oh);
            Assert.Equal(3, profiles.Length);
            Assert.Equal((c.ow, c.oh), (profiles[0].StreamWidth, profiles[0].StreamHeight));
            Assert.Equal((c.l1w, c.l1h), (profiles[1].StreamWidth, profiles[1].StreamHeight));
            Assert.Equal((c.l2w, c.l2h), (profiles[2].StreamWidth, profiles[2].StreamHeight));

            // 確認無任何硬編碼假設，且全部偶數對齊
            foreach (var p in profiles)
            {
                Assert.Equal(0, p.StreamWidth % 2);
                Assert.Equal(0, p.StreamHeight % 2);
            }
        }
    }

    [Fact]
    public void Test_BenignUdpLoss_Within2Percent_NeverDowngrades()
    {
        var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);

        for (long t = 0; t <= 12_000; t += 500)
        {
            var decision = ctrl.Evaluate(new AdaptiveSample(
                NowMs: t,
                SendQueueWaitMs: 8f,
                SendQueuePending: 0,
                UdpRawLossRatePercent: 1.8f,
                RetransmissionRatePercent: 1.8f,
                ArqFinalSuccessRatePercent: 100f,
                InflightRatio: 0.10f,
                RtoTimeoutsDelta: 0,
                FastRetransmitsDelta: 1,
                TargetFps: 30,
                ActualFps: 29.8f,
                FrameDropsDelta: 0,
                EndToEndDelayMs: 32f,
                IsStreamActive: true));

            Assert.False(decision.Changed, $"t={t}ms 輕微可恢復掉包 (1.8%) 不應觸發降級！");
            Assert.Equal(ResolutionLevel.Level0, ctrl.CurrentLevel);
        }
    }

    [Fact]
    public void Test_Congestion_StepwiseDowngrade_Cooldown_And_StaticDesktopRecovery()
    {
        var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
        Assert.Equal(ResolutionLevel.Level0, ctrl.CurrentLevel);

        // 1. 持續壅塞 2.5 秒 (< 3.0s) 尚未切換
        for (long t = 0; t <= 2500; t += 500)
        {
            var d = ctrl.Evaluate(new AdaptiveSample(
                NowMs: t, SendQueueWaitMs: 140f, SendQueuePending: 2,
                UdpRawLossRatePercent: 6.5f, RetransmissionRatePercent: 7.0f, ArqFinalSuccessRatePercent: 98f,
                InflightRatio: 0.55f, RtoTimeoutsDelta: 2, FastRetransmitsDelta: 2,
                TargetFps: 30, ActualFps: 14f, FrameDropsDelta: 2, EndToEndDelayMs: 420f, IsStreamActive: true));
            Assert.False(d.Changed);
        }

        // 2. 在 3.0s 時降為 Level 1 (1440x810)，不可直接跳至 Level 2
        var d1 = ctrl.Evaluate(new AdaptiveSample(
            NowMs: 3000, SendQueueWaitMs: 160f, SendQueuePending: 3,
            UdpRawLossRatePercent: 7.0f, RetransmissionRatePercent: 8.0f, ArqFinalSuccessRatePercent: 98f,
            InflightRatio: 0.60f, RtoTimeoutsDelta: 3, FastRetransmitsDelta: 2,
            TargetFps: 30, ActualFps: 12f, FrameDropsDelta: 2, EndToEndDelayMs: 450f, IsStreamActive: true));

        Assert.True(d1.Changed);
        Assert.Equal(ResolutionLevel.Level0, d1.PreviousLevel);
        Assert.Equal(ResolutionLevel.Level1, d1.NewLevel);
        Assert.Equal(1440, d1.Profile.StreamWidth);
        Assert.Equal(810, d1.Profile.StreamHeight);

        // 3. 冷卻期間 (3.5s ~ 7.5s) 即使仍為中度壅塞也不連續切換 (緊急保護 >= 2.5s 才會觸發)
        for (long t = 3500; t <= 5000; t += 500)
        {
            var dCooldown = ctrl.Evaluate(new AdaptiveSample(
                NowMs: t, SendQueueWaitMs: 160f, SendQueuePending: 3,
                UdpRawLossRatePercent: 7.0f, RetransmissionRatePercent: 8.0f, ArqFinalSuccessRatePercent: 98f,
                InflightRatio: 0.60f, RtoTimeoutsDelta: 2, FastRetransmitsDelta: 1,
                TargetFps: 30, ActualFps: 14f, FrameDropsDelta: 1, EndToEndDelayMs: 400f, IsStreamActive: true));
            Assert.False(dCooldown.Changed, $"冷卻期內不應在 t={t}ms 連續降級！");
        }

        // 4. 冷卻超過 5 秒 + 持續壅塞 3 秒後 (t = 8500ms)，逐步降為 Level 2 (960x540)
        AdaptiveDecision d2 = default;
        for (long t = 5500; t <= 9000; t += 500)
        {
            d2 = ctrl.Evaluate(new AdaptiveSample(
                NowMs: t, SendQueueWaitMs: 170f, SendQueuePending: 3,
                UdpRawLossRatePercent: 8.0f, RetransmissionRatePercent: 9.0f, ArqFinalSuccessRatePercent: 97f,
                InflightRatio: 0.65f, RtoTimeoutsDelta: 2, FastRetransmitsDelta: 2,
                TargetFps: 30, ActualFps: 12f, FrameDropsDelta: 2, EndToEndDelayMs: 460f, IsStreamActive: true));
            if (d2.Changed) break;
        }
        Assert.True(d2.Changed);
        Assert.Equal(ResolutionLevel.Level1, d2.PreviousLevel);
        Assert.Equal(ResolutionLevel.Level2, d2.NewLevel);
        Assert.Equal(960, d2.Profile.StreamWidth);
        Assert.Equal(540, d2.Profile.StreamHeight);

        // 5. 模擬停止看影片 → 切換至完全靜態桌面閱讀文字 (ActualFps = 0, IsStreamActive = false)
        // 驗證不會因為 FPS=0 而永遠卡在 540p，必須於穩定視窗 (>=12s) 逐步升回 L1 -> L0！
        long tCur = 10_000;
        bool upgradedToL1 = false, upgradedToL0 = false;
        for (; tCur <= 40_000; tCur += 500)
        {
            var dec = ctrl.Evaluate(new AdaptiveSample(
                NowMs: tCur, SendQueueWaitMs: 1f, SendQueuePending: 0,
                UdpRawLossRatePercent: 0f, RetransmissionRatePercent: 0f, ArqFinalSuccessRatePercent: 100f,
                InflightRatio: 0f, RtoTimeoutsDelta: 0, FastRetransmitsDelta: 0,
                TargetFps: 30, ActualFps: 0f, FrameDropsDelta: 0, EndToEndDelayMs: 22f, IsStreamActive: false));
            if (dec.Changed)
            {
                if (dec.NewLevel == ResolutionLevel.Level1)
                {
                    upgradedToL1 = true;
                    Assert.Equal(ResolutionLevel.Level2, dec.PreviousLevel);
                }
                else if (dec.NewLevel == ResolutionLevel.Level0)
                {
                    upgradedToL0 = true;
                    Assert.Equal(ResolutionLevel.Level1, dec.PreviousLevel);
                    break;
                }
            }
        }
        Assert.True(upgradedToL1, "靜態桌面恢復後未升回 Level 1");
        Assert.True(upgradedToL0, "靜態桌面恢復後未升回原生 Level 0");
        Assert.Equal(ResolutionLevel.Level0, ctrl.CurrentLevel);
    }

    [Fact]
    public void Test_Hysteresis_And_AntiPingPong_Backoff()
    {
        var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);

        // 1. 先降級一次到 L1
        long t = 0;
        for (; t <= 4000; t += 500)
        {
            ctrl.Evaluate(new AdaptiveSample(t, 150f, 2, 5f, 6f, 98f, 0.55f, 2, 1, 30, 14f, 2, 420f, true));
        }
        Assert.Equal(ResolutionLevel.Level1, ctrl.CurrentLevel);

        // 2. 處於中間模糊地帶（QueueWait = 45ms, Inflight = 0.28）：不該降為 L2 也不該立刻升回 L0
        for (int i = 0; i < 20; i++)
        {
            t += 500;
            var mid = ctrl.Evaluate(new AdaptiveSample(t, 45f, 1, 1.5f, 2.0f, 100f, 0.28f, 0, 0, 30, 26f, 0, 120f, true));
            Assert.False(mid.Changed);
            Assert.Equal(ResolutionLevel.Level1, ctrl.CurrentLevel);
        }

        // 3. 穩定 12 秒升回 L0，緊接著 5 秒內再次發生壅塞降級 -> 觸發防震盪退避（恢復視窗加倍至 24s）
        for (int i = 0; i < 30 && ctrl.CurrentLevel != ResolutionLevel.Level0; i++)
        {
            t += 500;
            ctrl.Evaluate(new AdaptiveSample(t, 4f, 0, 0f, 0f, 100f, 0.05f, 0, 0, 30, 30f, 0, 25f, true));
        }
        Assert.Equal(ResolutionLevel.Level0, ctrl.CurrentLevel);

        // 剛升級後迅速再次壅塞 (先經過冷卻期並持續壅塞)
        for (int i = 0; i < 16 && ctrl.CurrentLevel == ResolutionLevel.Level0; i++)
        {
            t += 500;
            ctrl.Evaluate(new AdaptiveSample(t, 180f, 3, 6f, 7f, 97f, 0.60f, 2, 2, 30, 12f, 2, 430f, true));
        }
        Assert.Equal(ResolutionLevel.Level1, ctrl.CurrentLevel);
        Assert.Equal(AdaptiveResolutionController.BaseRecoveryWindowMs * 2, ctrl.CurrentRecoveryWindowMs);
    }

    [Fact]
    public void Test_Mode_Original_And_Performance_Semantics()
    {
        // A. Original 模式：即使極端壅塞也鎖定 L0，僅發出 CongestionWarning
        var origCtrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Original);
        bool warned = false;
        for (long t = 0; t <= 5000; t += 500)
        {
            var d = origCtrl.Evaluate(new AdaptiveSample(t, 500f, 5, 12f, 15f, 90f, 0.85f, 4, 3, 30, 8f, 4, 800f, true));
            Assert.False(d.Changed);
            Assert.Equal(ResolutionLevel.Level0, origCtrl.CurrentLevel);
            if (d.CongestionWarning) warned = true;
        }
        Assert.True(warned);

        // 若從 Auto L1/L2 手動切換為 Original，必須立即跳回 L0
        var autoCtrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
        for (long t = 0; t <= 4000; t += 500)
        {
            autoCtrl.Evaluate(new AdaptiveSample(t, 180f, 3, 6f, 7f, 97f, 0.60f, 2, 2, 30, 12f, 2, 430f, true));
        }
        Assert.Equal(ResolutionLevel.Level1, autoCtrl.CurrentLevel);
        var switchBack = autoCtrl.SetMode(ResolutionMode.Original, 5000);
        Assert.True(switchBack.Changed);
        Assert.Equal(ResolutionLevel.Level0, switchBack.NewLevel);

        // B. Performance 模式：於 1.5s 即可反應降級（早於 Auto 的 3.0s）
        var perfCtrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Performance);
        AdaptiveDecision perfDec = default;
        long perfSwitchMs = -1;
        for (long t = 0; t <= 3000; t += 500)
        {
            perfDec = perfCtrl.Evaluate(new AdaptiveSample(t, 95f, 2, 4f, 4.5f, 98f, 0.40f, 1, 1, 30, 18f, 1, 280f, true));
            if (perfDec.Changed) { perfSwitchMs = t; break; }
        }
        Assert.True(perfDec.Changed);
        Assert.InRange(perfSwitchMs, 1000, 2000);
    }

    [Fact]
    public void Test_ViewerMouseCoordinateMapping_IndependentOfStreamDownscale()
    {
        // Host 原生 1920x1080，即使串流降至 Level 1 (1440x810) 或 Level 2 (960x540)，
        // 且 Viewer 視窗縮放至 1280x720 或帶黑邊 (Letterbox: X=100, Y=50, W=1280, H=720)，
        // 滑鼠映射結果必須嚴格等於 Host 原生 1920x1080 對應座標！
        var destRect = new Rectangle(100, 50, 1280, 720);

        // 中央點擊
        Assert.True(ResolutionProfileManager.TryMapViewerPointToHostOriginal(100 + 640, 50 + 360, destRect, 1920, 1080, out int cx, out int cy));
        Assert.InRange(cx, 959, 961);
        Assert.InRange(cy, 539, 541);

        // 右下邊緣點擊
        Assert.True(ResolutionProfileManager.TryMapViewerPointToHostOriginal(100 + 1279, 50 + 719, destRect, 1920, 1080, out int rx, out int ry));
        Assert.InRange(rx, 1916, 1919);
        Assert.InRange(ry, 1076, 1079);

        // 左上原點與越界檢查
        Assert.True(ResolutionProfileManager.TryMapViewerPointToHostOriginal(100, 50, destRect, 1920, 1080, out int lx, out int ly));
        Assert.Equal((0, 0), (lx, ly));
        Assert.False(ResolutionProfileManager.TryMapViewerPointToHostOriginal(50, 50, destRect, 1920, 1080, out _, out _));

        // 4K (3840x2160) 映射測試
        Assert.True(ResolutionProfileManager.TryMapViewerPointToHostOriginal(100 + 320, 50 + 180, destRect, 3840, 2160, out int u4x, out int u4y));
        Assert.InRange(u4x, 958, 962);
        Assert.InRange(u4y, 538, 542);
    }

    [Fact]
    public unsafe void Test_Benchmark_TextReadability_And_VideoCompression_Levels012()
    {
        const int w = 1920, h = 1080;
        var textScene = new byte[w * h * 4];
        var videoScene = new byte[w * h * 4];

        // 1. 建構擬真桌面文字/程式碼場景 (深色底 + 高對比細字 + 視窗外框)
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(30, 30, 36));
                using var panelBrush = new SolidBrush(Color.FromArgb(42, 45, 52));
                g.FillRectangle(panelBrush, 80, 60, 1760, 960);
                using var font = new Font("Consolas", 13f, FontStyle.Regular);
                using var txtBrush = new SolidBrush(Color.FromArgb(225, 232, 240));
                using var kwBrush = new SolidBrush(Color.FromArgb(110, 190, 255));
                for (int row = 0; row < 35; row++)
                {
                    int py = 90 + row * 25;
                    g.DrawString($"public async Task<ResolutionDecision> EvaluateAdaptive_{row}(int q, double rttMs)", font, row % 3 == 0 ? kwBrush : txtBrush, 120, py);
                }
            }
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try { System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, textScene, 0, textScene.Length); }
            finally { bmp.UnlockBits(bd); }
        }

        // 2. 建構高頻全螢幕動態影片場景
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                videoScene[o] = (byte)((x * 13 + y * 5) & 0xFF);
                videoScene[o + 1] = (byte)((x * 7 ^ y * 11) & 0xFF);
                videoScene[o + 2] = (byte)((y * 17 + (x >> 2)) & 0xFF);
                videoScene[o + 3] = 255;
            }
        }

        _output.WriteLine("=== RemoteDesk 自適應解析度實測基準 (1920x1080 合成桌面文字 vs 全動態影像) ===");
        _output.WriteLine("場景   | 解析度層級       | Quality | 編碼大小    | UDP包數 | 編碼(ms) | 解碼(ms) | 30FPS頻寬 | 原生畫布PSNR");
        _output.WriteLine("-------+------------------+---------+-------------+---------+----------+----------+-----------+-------------");

        var levels = new[] { ResolutionLevel.Level0, ResolutionLevel.Level1, ResolutionLevel.Level2 };
        var qualities = new[] { 50, 70, 90 };

        double psnrL0Q50Text = 0, psnrL1Q70Text = 0, psnrL2Q70Text = 0;
        int sizeL0Q70Video = 0, sizeL1Q70Video = 0, sizeL2Q70Video = 0;

        foreach (var (sceneName, rawBuf) in new[] { ("文字", textScene), ("影片", videoScene) })
        {
            foreach (var lvl in levels)
            {
                var prof = ResolutionProfileManager.GetProfile(w, h, lvl);
                foreach (int q in qualities)
                {
                    var enc = new FrameEncoder(w, h) { Quality = q };
                    enc.ConfigureStreamResolution(prof.StreamWidth, prof.StreamHeight);
                    using var dec = new FrameDecoder();
                    dec.ConfigureStream(1, prof.StreamWidth, prof.StreamHeight);

                    fixed (byte* pRaw = rawBuf)
                    {
                        var frame = new CapturedFrame((IntPtr)pRaw, w, h, w * 4, HasMetadata: true, IsKeyFrame: true);

                        // 預熱 JIT
                        enc.Encode(frame, true, 1000, out _, out _);
                        enc.RequestKeyframe();

                        var swEnc = Stopwatch.StartNew();
                        var tiles = enc.Encode(frame, true, 1033, out var ft, out _);
                        swEnc.Stop();

                        var pkt = FrameEncoder.Serialize(1, 1, 0, ft, enc.StreamWidth, enc.StreamHeight, null, tiles, streamConfigVersion: 1);

                        var swDec = Stopwatch.StartNew();
                        dec.DecodeFrame(pkt.AsSpan(1), out _);
                        swDec.Stop();

                        // 量測在 Viewer 視窗 1920x1080 還原後的真實視覺 PSNR (dB)
                        double psnrDb = ComputeUpscaledPsnr(rawBuf, w, h, dec);
                        int pktCount = (int)Math.Ceiling((double)pkt.Length / ReliableUdpStream.MaxPayloadSize);
                        double mbps30 = pkt.Length * 8.0 * 30.0 / 1_000_000.0;

                        _output.WriteLine(
                            $" {sceneName}  | L{(int)lvl} {prof.StreamWidth,4}x{prof.StreamHeight,-4} | {q,5}%  | {pkt.Length,9:N0} B | {pktCount,7} | {swEnc.Elapsed.TotalMilliseconds,8:F2} | {swDec.Elapsed.TotalMilliseconds,8:F2} | {mbps30,6:F2} M  | {psnrDb,7:F2} dB");

                        if (sceneName == "文字" && lvl == ResolutionLevel.Level0 && q == 50) psnrL0Q50Text = psnrDb;
                        if (sceneName == "文字" && lvl == ResolutionLevel.Level1 && q == 70) psnrL1Q70Text = psnrDb;
                        if (sceneName == "文字" && lvl == ResolutionLevel.Level2 && q == 70) psnrL2Q70Text = psnrDb;
                        if (sceneName == "影片" && q == 70)
                        {
                            if (lvl == ResolutionLevel.Level0) sizeL0Q70Video = pkt.Length;
                            if (lvl == ResolutionLevel.Level1) sizeL1Q70Video = pkt.Length;
                            if (lvl == ResolutionLevel.Level2) sizeL2Q70Video = pkt.Length;
                        }
                    }
                }
            }
        }

        // 驗證重要物理量測結果：
        // 1) Q=50 保留原生 1920x1080 時，文字清晰度 (PSNR) 顯著高於強制 2x 降採樣 (960x540)！
        Assert.True(psnrL0Q50Text > psnrL2Q70Text, $"原生 1080p Q=50 PSNR ({psnrL0Q50Text:F2}dB) 應優於 540p Q=70 ({psnrL2Q70Text:F2}dB)");
        // 2) Level 1 (1440x810) 文字 PSNR 顯著優於 Level 2 (960x540)
        Assert.True(psnrL1Q70Text > psnrL2Q70Text, $"Level 1 810p PSNR ({psnrL1Q70Text:F2}dB) 應優於 Level 2 540p ({psnrL2Q70Text:F2}dB)");
        // 3) 降採樣確實大幅減小影格大小 (Level 2 < Level 1 < Level 0)
        Assert.True(sizeL2Q70Video < sizeL1Q70Video && sizeL1Q70Video < sizeL0Q70Video);
    }

    private static double ComputeUpscaledPsnr(byte[] groundTruthBgra, int origW, int origH, FrameDecoder dec)
    {
        using var rendered = new Bitmap(origW, origH, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(rendered))
        {
            dec.DrawTo(g, new Rectangle(0, 0, origW, origH));
        }

        var bd = rendered.LockBits(new Rectangle(0, 0, origW, origH), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var actual = new byte[origW * origH * 4];
            System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, actual, 0, actual.Length);
            double mseSum = 0;
            // 取樣間隔 2 像素以兼顧全畫面精度與測試執行速度
            long count = 0;
            for (int y = 0; y < origH; y += 2)
            {
                int rowOff = y * origW * 4;
                for (int x = 0; x < origW; x += 2)
                {
                    int idx = rowOff + x * 4;
                    double db = groundTruthBgra[idx] - actual[idx];
                    double dg = groundTruthBgra[idx + 1] - actual[idx + 1];
                    double dr = groundTruthBgra[idx + 2] - actual[idx + 2];
                    mseSum += (db * db + dg * dg + dr * dr) / 3.0;
                    count++;
                }
            }
            double mse = mseSum / Math.Max(1, count);
            return mse <= 0.0001 ? 99.0 : 10.0 * Math.Log10((255.0 * 255.0) / mse);
        }
        finally
        {
            rendered.UnlockBits(bd);
        }
    }

    /// <summary>模擬受控頻寬限制雙工串流，用於本機 Loopback 測試高壓網路降速與恢復。</summary>
    private sealed class ThrottledDuplexStream : Stream
    {
        private readonly Stream _inner;
        private double _bytesPerMs;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private double _nextAllowedMs;
        private readonly object _paceLock = new();

        public ThrottledDuplexStream(Stream inner, double initialMbps)
        {
            _inner = inner;
            SetBandwidthMbps(initialMbps);
        }

        public void SetBandwidthMbps(double mbps)
        {
            lock (_paceLock)
            {
                _bytesPerMs = Math.Max(0.1, mbps * 1_000_000.0 / 8.0 / 1000.0);
            }
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int delayMs = 0;
            lock (_paceLock)
            {
                double rate = _bytesPerMs;
                double nowMs = _sw.Elapsed.TotalMilliseconds;
                double txDurationMs = buffer.Length / rate;
                double scheduledEnd = Math.Max(nowMs, _nextAllowedMs) + txDurationMs;
                _nextAllowedMs = scheduledEnd;
                delayMs = (int)Math.Max(0, scheduledEnd - nowMs);
            }
            if (delayMs >= 2)
            {
                await Task.Delay(Math.Min(delayMs, 400), cancellationToken).ConfigureAwait(false);
            }
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Test_ControlledLoopback_RateLimited_SecureChannel_DowngradeAndRecovery()
    {
        // 在受控 Loopback TCP + Rate-Limiter + SecureChannel AES-256-GCM 驗證完整協定：
        // StreamConfig -> Pause Delta -> Keyframe -> StreamConfigAck -> Resume Delta
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient();
        var acceptTask = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var server = await acceptTask;

        var hostThrottle = new ThrottledDuplexStream(server.GetStream(), initialMbps: 50.0);
        var hostHandshakeTask = SecureChannel.ServerHandshakeAsync(
            hostThrottle,
            _ => new ServerHello(Protocol.Version, "900000001", "HostSim", Array.Empty<byte>(), 1, false, false, false),
            CancellationToken.None);
        var viewerHandshakeTask = SecureChannel.ClientHandshakeAsync(client.GetStream(), "900000002", "ViewerSim", CancellationToken.None);

        var (hostCh, _) = await hostHandshakeTask;
        var (viewerCh, _) = await viewerHandshakeTask;
        using var hostChGuard = hostCh;
        using var viewerChGuard = viewerCh;

        var enc = new FrameEncoder(1280, 720) { Quality = 80 };
        using var dec = new FrameDecoder();
        var ctrl = new AdaptiveResolutionController(1280, 720, ResolutionMode.Performance); // 1.5s 觀察視窗利於測試執行在幾秒內完成
        uint cfgVer = 1;
        uint ackedCfgVer = 1;
        uint seq = 0, frameVer = 0;
        bool deltaPaused = false;

        // 傳送初始 StreamConfig v1
        await hostCh.SendAsync(MessageCodec.StreamConfig(cfgVer, 1280, 720, 1280, 720, ResolutionLevel.Level0, ResolutionMode.Performance, "Init"), highPriority: true, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int viewerDecodedFrames = 0;
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
                            var dirty = dec.DecodeFrame(msg.AsSpan(1), out _, out _, out _, out _, out _);
                            if (!dirty.IsEmpty) viewerDecodedFrames++;
                            break;
                        }
                    }
                }
                catch { break; }
            }
        });

        // 簡化 Host Ack 接收與畫面模擬產生
        var hostRecvLoop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var msg = await hostCh.ReceiveAsync(cts.Token);
                    if ((ViewerMsg)msg[0] == ViewerMsg.StreamConfigAck)
                    {
                        var r = new PacketReader(msg.AsSpan(1));
                        var (v, _, _) = MessageCodec.ReadStreamConfigAck(ref r);
                        Volatile.Write(ref ackedCfgVer, v);
                    }
                }
                catch { break; }
            }
        });

        var frameBuf = new byte[1280 * 720 * 4];
        bool desyncTriggered = false;
        dec.DesyncDetected += () => desyncTriggered = true;

        // 模擬：高頻寬 50Mbps (維持 L0) -> 低頻寬 1.8Mbps (觸發壅塞降級 L1)
        long simTimeMs = 0;
        bool switchedDown = false;
        for (int step = 0; step < 30; step++)
        {
            simTimeMs += 200;
            if (step == 3)
            {
                hostThrottle.SetBandwidthMbps(0.8); // 模擬外網頻寬驟降至 <1Mbps 導致 WriteAsync 與 QueueWait 累積
            }

            // 填寫動態高熵畫面（確保 JPEG 封包大小約 150KB+，在 0.8Mbps 下確實造成 ~150ms+ 排隊阻塞）
            for (int i = 0; i < frameBuf.Length; i += 4)
            {
                int px = (i >> 2);
                frameBuf[i] = (byte)((px * 7 + step * 31) & 0xFF);
                frameBuf[i + 1] = (byte)(((px / 1280) * 13 + step * 17) & 0xFF);
                frameBuf[i + 2] = (byte)((px ^ (px >> 7) ^ step) & 0xFF);
                frameBuf[i + 3] = 255;
            }

            if (Volatile.Read(ref ackedCfgVer) < cfgVer && deltaPaused)
            {
                await Task.Delay(25);
                continue;
            }
            deltaPaused = false;

            var pkt = EncodePinnedTestFrame(enc, frameBuf, 1280, 720, isKeyFrame: (step == 0), simTimeMs, cfgVer, ref seq, ref frameVer);
            var sendSw = Stopwatch.StartNew();
            await hostCh.SendAsync(pkt, highPriority: false, cts.Token);
            sendSw.Stop();

            float queueOrSendWait = Math.Max(hostCh.LastQueueWaitMs, (float)sendSw.Elapsed.TotalMilliseconds);
            var decResult = ctrl.Evaluate(new AdaptiveSample(
                NowMs: simTimeMs,
                SendQueueWaitMs: queueOrSendWait,
                SendQueuePending: hostCh.PendingLowPriorityCount,
                UdpRawLossRatePercent: 0f,
                RetransmissionRatePercent: 0f,
                ArqFinalSuccessRatePercent: 100f,
                InflightRatio: queueOrSendWait > 60 ? 0.55f : 0.10f,
                RtoTimeoutsDelta: 0,
                FastRetransmitsDelta: 0,
                TargetFps: 30,
                ActualFps: queueOrSendWait > 60 ? 10f : 30f,
                FrameDropsDelta: 0,
                EndToEndDelayMs: queueOrSendWait + 10f,
                IsStreamActive: true));

            if (decResult.Changed)
            {
                switchedDown = true;
                cfgVer++;
                deltaPaused = true;
                enc.ConfigureStreamResolution(decResult.Profile.StreamWidth, decResult.Profile.StreamHeight);
                await hostCh.SendAsync(MessageCodec.StreamConfig(cfgVer, 1280, 720, decResult.Profile.StreamWidth, decResult.Profile.StreamHeight, decResult.NewLevel, ResolutionMode.Performance, decResult.Reason), highPriority: true, cts.Token);
                var kpkt = EncodePinnedTestFrame(enc, frameBuf, 1280, 720, isKeyFrame: true, simTimeMs, cfgVer, ref seq, ref frameVer);
                await hostCh.SendAsync(kpkt, highPriority: false, cts.Token);
                for (int w = 0; w < 50 && Volatile.Read(ref ackedCfgVer) < cfgVer; w++) await Task.Delay(20);
                deltaPaused = false;

                // 切換確認完成後再繼續傳送 3 幀增量畫面，驗證後續增量完全同步不發生 Desync
                hostThrottle.SetBandwidthMbps(50.0);
                for (int post = 1; post <= 3; post++)
                {
                    var postPkt = EncodePinnedTestFrame(enc, frameBuf, 1280, 720, isKeyFrame: false, simTimeMs + post * 33, cfgVer, ref seq, ref frameVer);
                    await hostCh.SendAsync(postPkt, highPriority: false, cts.Token);
                }
                break;
            }
        }

        await Task.Delay(150);
        _output.WriteLine($"Loopback受控測試完成: switchedDown={switchedDown}, FinalCfgVer={cfgVer}, AckedCfgVer={ackedCfgVer}, Decoder={dec.Width}x{dec.Height}, DecodedFrames={viewerDecodedFrames}");

        Assert.True(switchedDown, "低頻寬觸發應透過真實加密傳輸鏈路切換至 Level 1");
        Assert.Equal(ResolutionLevel.Level1, ctrl.CurrentLevel);
        Assert.Equal(960, dec.Width);
        Assert.Equal(540, dec.Height);
        Assert.Equal(cfgVer, Volatile.Read(ref ackedCfgVer));
        Assert.False(desyncTriggered, "同步切換握手過程不應產生任何 Desync 錯誤！");

        cts.Cancel();
        await Task.WhenAll(viewerLoop, hostRecvLoop);
    }

    private static unsafe byte[] EncodePinnedTestFrame(
        FrameEncoder enc, byte[] buf, int w, int h, bool isKeyFrame, long ts, uint cfgVer, ref uint seq, ref uint frameVer)
    {
        fixed (byte* pBuf = buf)
        {
            var dirtyRects = isKeyFrame ? null : new[] { new Rectangle(0, 0, w, h) };
            var f = new CapturedFrame((IntPtr)pBuf, w, h, w * 4, HasMetadata: true, DirtyRectangles: dirtyRects, MoveRectangles: null, IsKeyFrame: isKeyFrame);
            var tiles = enc.Encode(f, true, ts, out var ft, out _);
            uint bVer = (ft == FrameType.FullFrame) ? 0 : frameVer;
            frameVer = ++seq;
            return FrameEncoder.Serialize(seq, frameVer, bVer, ft, enc.StreamWidth, enc.StreamHeight, null, tiles, cfgVer);
        }
    }

    [Fact]
    public async Task Test_ControlledReliableUdp_Loopback_TelemetryAndBenignLossTolerance()
    {
        using var sockA = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var sockB = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sockA.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        sockB.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var epA = (IPEndPoint)sockA.LocalEndPoint!;
        var epB = (IPEndPoint)sockB.LocalEndPoint!;

        using var udpA = new ReliableUdpStream(sockA, epB, ownsSocket: true);
        using var udpB = new ReliableUdpStream(sockB, epA, ownsSocket: true);

        // 傳送多幀測試資料 (約 180KB -> ~150 個 1200B UDP 分片)
        var payload = new byte[180 * 1024];
        new Random(42).NextBytes(payload);

        var recvBuf = new byte[payload.Length];
        var readTask = udpB.ReadExactlyAsync(recvBuf, 0, recvBuf.Length);
        await udpA.WriteAsync(payload);
        await readTask;

        Assert.Equal(payload, recvBuf);
        await udpA.FlushAsync();

        // 等待 ACK 回報統計更新
        for (int i = 0; i < 30 && udpA.UniquePacketsAcked < udpA.TotalPacketsSent; i++)
            await Task.Delay(10);

        _output.WriteLine($"ReliableUDP Loopback 實測: PacketsSent={udpA.TotalPacketsSent}, Acked={udpA.UniquePacketsAcked}, Lost={udpA.UniquePacketsLost}, Retrans={udpA.TotalRetransmissions}, SRTT={udpA.CurrentSrttMs:F1}ms, RTO={udpA.CurrentRtoMs}ms, LastSemWait={udpA.LastSemaphoreWaitMs}ms");
        Assert.True(udpA.TotalPacketsSent >= 150);
        Assert.Equal(udpA.TotalPacketsSent, udpA.UniquePacketsAcked);

        // 將真實 ReliableUDP 指標匯入自適應控制器，確認 Loopback 順暢環境維持 Level 0
        var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
        float lossPct = udpA.TotalPacketsSent > 0 ? (float)(udpA.UniquePacketsLost * 100.0 / udpA.TotalPacketsSent) : 0f;
        var dec = ctrl.Evaluate(new AdaptiveSample(
            NowMs: 3500,
            SendQueueWaitMs: udpA.LastSemaphoreWaitMs,
            SendQueuePending: 0,
            UdpRawLossRatePercent: lossPct,
            RetransmissionRatePercent: lossPct,
            ArqFinalSuccessRatePercent: 100f,
            InflightRatio: (float)udpA.CurrentInflightPackets / udpA.MaxInflightPackets,
            RtoTimeoutsDelta: (int)udpA.TotalRtoTimeouts,
            FastRetransmitsDelta: (int)udpA.TotalFastRetransmits,
            TargetFps: 30,
            ActualFps: 30f,
            FrameDropsDelta: 0,
            EndToEndDelayMs: (float)udpA.CurrentSrttMs,
            IsStreamActive: true));

        Assert.False(dec.Changed);
        Assert.Equal(ResolutionLevel.Level0, ctrl.CurrentLevel);
    }

    [Fact]
    public unsafe void Test_Stress_RapidSwitchCycle_ZeroDesyncAndStableMemory()
    {
        const int origW = 1920, origH = 1080;
        var enc = new FrameEncoder(origW, origH) { Quality = 70 };
        using var dec = new FrameDecoder();

        var buf = new byte[origW * origH * 4];
        for (int i = 0; i < buf.Length; i += 4)
        {
            buf[i] = 40; buf[i + 1] = 150; buf[i + 2] = 220; buf[i + 3] = 255;
        }

        bool anyDesync = false;
        dec.DesyncDetected += () => anyDesync = true;

        var profiles = ResolutionProfileManager.BuildProfiles(origW, origH);
        uint cfgVer = 0, seq = 0, frameVer = 0;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long memBefore = GC.GetTotalMemory(true);

        fixed (byte* pBuf = buf)
        {
            // 連續執行 36 次 (12 輪 L0 -> L1 -> L2 -> L0) 解析度切換與編解碼壓力迴圈
            for (int cycle = 0; cycle < 36; cycle++)
            {
                var p = profiles[cycle % 3];
                cfgVer++;
                enc.ConfigureStreamResolution(p.StreamWidth, p.StreamHeight);
                dec.ConfigureStream(cfgVer, p.StreamWidth, p.StreamHeight);

                // Keyframe
                var kf = new CapturedFrame((IntPtr)pBuf, origW, origH, origW * 4, HasMetadata: true, IsKeyFrame: true);
                var kt = enc.Encode(kf, true, cycle * 100L, out var kft, out _);
                frameVer = ++seq;
                var kpkt = FrameEncoder.Serialize(seq, frameVer, 0, kft, enc.StreamWidth, enc.StreamHeight, null, kt, cfgVer);
                var kdirty = dec.DecodeFrame(kpkt.AsSpan(1), out _);
                Assert.False(kdirty.IsEmpty);
                Assert.Equal(p.StreamWidth, dec.Width);

                // 後續 2 幀增量更新
                for (int d = 0; d < 2; d++)
                {
                    var df = new CapturedFrame(
                        (IntPtr)pBuf, origW, origH, origW * 4,
                        HasMetadata: true,
                        DirtyRectangles: new[] { new Rectangle(100 + d * 50, 100, 120, 80) },
                        MoveRectangles: null,
                        IsKeyFrame: false);
                    var dt = enc.Encode(df, true, cycle * 100L + d * 16L, out var dft, out _);
                    uint baseV = frameVer;
                    frameVer = ++seq;
                    var dpkt = FrameEncoder.Serialize(seq, frameVer, baseV, dft, enc.StreamWidth, enc.StreamHeight, null, dt, cfgVer);
                    var ddirty = dec.DecodeFrame(dpkt.AsSpan(1), out _);
                    Assert.False(ddirty.IsEmpty);
                }
            }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long memAfter = GC.GetTotalMemory(true);
        double memDeltaMb = (memAfter - memBefore) / (1024.0 * 1024.0);
        _output.WriteLine($"36次頻繁解析度切換壓測記憶體變化: {memDeltaMb:F2} MB, Desync次數: {(anyDesync ? 1 : 0)}, 解碼失敗數: {dec.DecodeFailureCount}");

        Assert.False(anyDesync);
        Assert.Equal(0, dec.DecodeFailureCount);
        Assert.True(memDeltaMb < 32.0, $"記憶體成長應受控 (<32MB)，實際={memDeltaMb:F2}MB");
    }

    [Fact]
    public unsafe void Test_ModerateRttPipelining_FastRecoveryWithIsolatedJitter_And_VideoQualityPreservation()
    {
        // 1. 中等跨網段 RTT (60ms @ 30fps, 45ms @ 60fps) 不得被硬鎖死在 MaxInflight=2，同時維持 45ms @ 30fps -> 2
        Assert.Equal(2, HostSession.ComputeEffectiveMaxInflight(45, 30, 0f, 0, 0f, 0f, 0, 100f, 0.05f, false, 30000, 50f));
        Assert.True(HostSession.ComputeEffectiveMaxInflight(60, 30, 0f, 0, 0f, 0f, 0, 100f, 0.05f, false, 45000, 50f) >= 3);
        Assert.True(HostSession.ComputeEffectiveMaxInflight(45, 60, 0f, 0, 0f, 0f, 0, 100f, 0.05f, false, 45000, 50f) >= 4);

        // 2. 頻寬充裕下從 L2 恢復至 L0，即使途中出現 1~2 次孤立輕微抖動，不應被重設回起點而卡在低解析度過久 (~12~14s 內回原生 L0)
        var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
        long t = 0;
        for (; t <= 9000 && ctrl.CurrentLevel != ResolutionLevel.Level2; t += 500)
        {
            ctrl.Evaluate(new AdaptiveSample(t, 160f, 3, 7f, 8f, 97f, 0.60f, 2, 2, 30, 12f, 2, 430f, true));
        }
        Assert.Equal(ResolutionLevel.Level2, ctrl.CurrentLevel);

        long recoverStartMs = t;
        bool recoveredToL0 = false;
        for (int step = 0; step < 32; step++)
        {
            t += 500;
            // 在第 6 與第 20 步注入 1 個孤立輕微抖動視窗 (QueueWait=28ms, 非壅塞)
            bool isolatedJitter = step == 6 || step == 20;
            var d = ctrl.Evaluate(new AdaptiveSample(
                NowMs: t,
                SendQueueWaitMs: isolatedJitter ? 28f : 3f,
                SendQueuePending: 0,
                UdpRawLossRatePercent: 0f,
                RetransmissionRatePercent: 0f,
                ArqFinalSuccessRatePercent: 100f,
                InflightRatio: isolatedJitter ? 0.20f : 0.05f,
                RtoTimeoutsDelta: 0,
                FastRetransmitsDelta: 0,
                TargetFps: 30,
                ActualFps: 30f,
                FrameDropsDelta: 0,
                EndToEndDelayMs: 45f,
                IsStreamActive: true,
                SourceFps: 30f,
                CaptureFps: 30f,
                ViewerRenderFps: 30f));
            if (d.Changed && ctrl.CurrentLevel == ResolutionLevel.Level0)
            {
                recoveredToL0 = true;
                break;
            }
        }
        Assert.True(recoveredToL0, "存在孤立單窗輕微抖動時，仍應於合理時間內快速恢復至原生解析度 Level 0");
        Assert.True((t - recoverStartMs) <= 15_000, $"恢復至 L0 耗時過長 ({t - recoverStartMs}ms)");

        // 3. 驗證 50% 畫面影片更新時 ComputeTileQuality 不會突降 -10 畫質（與 70% 基線編碼體積無劇烈劣化）
        const int w = 640, h = 360;
        var enc = new FrameEncoder(w, h) { Quality = 70, Lossless = false };
        byte[] bg = new byte[w * h * 4];
        for (int i = 0; i < bg.Length; i += 4)
        {
            int px = i / 4;
            bg[i] = (byte)((px * 13) & 0xFF);
            bg[i + 1] = (byte)(((px / w) * 19) & 0xFF);
            bg[i + 2] = (byte)(((px * 5) ^ (px >> 4)) & 0xFF);
            bg[i + 3] = 255;
        }
        fixed (byte* pBg = bg)
        {
            var kf = new CapturedFrame((IntPtr)pBg, w, h, w * 4, HasMetadata: true, IsKeyFrame: true);
            var kt = enc.Encode(kf, true, 0, out _, out _);
            // 更新半個螢幕 (50% 面積) 應維持 Quality=70（不突降至 Q60）
            var df50 = new CapturedFrame((IntPtr)pBg, w, h, w * 4, HasMetadata: true,
                DirtyRectangles: new[] { new Rectangle(0, 0, w / 2, h) }, IsKeyFrame: false);
            var dt50 = enc.Encode(df50, true, 33, out _, out _);
            Assert.Single(dt50);
            Assert.Single(kt);
            // 50% 相同高頻紋理於相同 Quality=70 下應為全畫面大小約 45%~58%（不會因 -10 壓縮突降至 < 38%）
            double sizeRatio = (double)dt50[0].Data.Length / kt[0].Data.Length;
            Assert.True(sizeRatio >= 0.42, $"50% 影片更新區域不應意外降質編碼 (ratio={sizeRatio:F2})");
        }
    }
}
