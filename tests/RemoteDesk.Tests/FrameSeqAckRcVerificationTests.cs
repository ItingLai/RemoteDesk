using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using RemoteDesk.Common;
using RemoteDesk.Host;
using RemoteDesk.Net;
using RemoteDesk.Viewer;
using Xunit;
using Xunit.Abstractions;

namespace RemoteDesk.Tests;

/// <summary>
/// FrameSeq ACK Cross-Generation Safety — Final RC Verification Tests
/// Covers Item B (BoundedSeqWindow LRU eviction & wraparound) and Item A (Register-to-Send generation race).
/// </summary>
public class FrameSeqAckRcVerificationTests
{
    private readonly ITestOutputHelper _output;

    public FrameSeqAckRcVerificationTests(ITestOutputHelper output)
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

    private static async Task<(SecureChannel hostCh, ClientHello clientHello, SecureChannel viewerCh, ServerHello serverHello)> CreateTcpSecurePairAsync(
        ProtocolCapabilities viewerCaps = ProtocolCapabilities.FrameSeqAck)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var acceptTask = listener.AcceptTcpClientAsync(cts.Token).AsTask();
        var viewerClient = new TcpClient();
        var connectTask = viewerClient.ConnectAsync(IPAddress.Loopback, port, cts.Token).AsTask();

        await Task.WhenAll(acceptTask, connectTask);
        var hostClient = acceptTask.Result;
        SecureChannel.TuneSocket(hostClient.Client);
        SecureChannel.TuneSocket(viewerClient.Client);

        var serverTask = SecureChannel.ServerHandshakeAsync(
            hostClient.GetStream(),
            ch => new ServerHello(
                Protocol.Version,
                "990000001",
                "TestHost",
                Array.Empty<byte>(),
                1,
                false,
                false,
                false,
                ch.Capabilities & ProtocolCapabilities.FrameSeqAck),
            cts.Token);

        var viewerTask = SecureChannel.ClientHandshakeAsync(
            viewerClient.GetStream(),
            "990000002",
            "TestViewer",
            cts.Token,
            viewerCaps);

        var (hostCh, clientHello) = await serverTask;
        var (viewerCh, serverHello) = await viewerTask;
        return (hostCh, clientHello, viewerCh, serverHello);
    }

    private static HostSession CreateSequenceSession(SecureChannel hostCh, ClientHello hello) =>
        new(hostCh, hello, "127.0.0.1", new NullHostUi());

    // =========================================================================
    // Verification Item B: BoundedSeqWindow(128) LRU Eviction & Sequence Reuse
    // =========================================================================

    [Fact]
    public async Task ItemB_B1_StaleLruEvictionOver128_LateOldFrameAckStillRejected_DoesNotAffectNewInflightOrRtt()
    {
        var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
        using var _h = hostCh;
        using var _v = viewerCh;
        using var host = CreateSequenceSession(hostCh, clientHello);

        long now = host.ClockMsForTest;
        const uint frameA = 1;
        host.RegisterFrameSentForTest(frameA, now - 80);

        // 先建立一筆真實 RTT 樣本在 gen=1，或於重設後在目標世代建立基準 RTT
        int cleared = host.ResetInflightForTest("B1_ResetAfterFrameA");
        Assert.Equal(1, cleared);

        // 連續讓超過 128 個新序號進入 stale LRU（擠出 frameA）
        uint seqCursor = 2;
        for (int i = 0; i < 140; i++)
        {
            now = host.ClockMsForTest;
            uint s = seqCursor++;
            host.RegisterFrameSentForTest(s, now - 30);
            int c = host.ResetInflightForTest($"B1_Evict_{i}");
            Assert.Equal(1, c);
        }

        // 目前處於最新世代，註冊新世代 Frame B（故意選未重疊序號；並測試高 inflight 與基準 RTT 保護）
        // 首先送出一張 frameBaseline 並 ACK，建立 55ms 基準 RTT
        now = host.ClockMsForTest;
        uint baselineSeq = seqCursor++;
        host.RegisterFrameSentForTest(baselineSeq, now - 55);
        var ackBase = host.InjectFrameAckForTest(MessageCodec.FrameAck(baselineSeq));
        Assert.True(ackBase.Accepted);
        float baselineRtt = host.GetFrameAckDiagnosticsSnapshot().LastFrameRttMs;
        Assert.InRange(baselineRtt, 45f, 150f);

        // 接著讓新世代有 Frame B 在途 (sent 150ms ago)
        now = host.ClockMsForTest;
        uint frameB = seqCursor++;
        host.RegisterFrameSentForTest(frameB, now - 150);

        var snapBeforeStaleAck = host.GetFrameAckDiagnosticsSnapshot();
        Assert.Equal(1, snapBeforeStaleAck.InflightCount);
        Assert.Equal(new[] { frameB }, snapBeforeStaleAck.InflightSeqs);

        // 投遞已被 _staleSeqs (128) 淘汰的舊 Frame A ACK
        Assert.False(host.IsSeqInStaleLruForTest(frameA));
        var staleAckRes = host.InjectFrameAckForTest(MessageCodec.FrameAck(frameA));
        Assert.False(staleAckRes.Accepted);
        Assert.Equal(FrameAckRejectReason.UnknownFrameAck, staleAckRes.RejectReason);
        Assert.Equal(1, staleAckRes.InflightCountAfter);

        var snapAfterStaleAck = host.GetFrameAckDiagnosticsSnapshot();
        Assert.Equal(1, snapAfterStaleAck.InflightCount);
        Assert.Equal(new[] { frameB }, snapAfterStaleAck.InflightSeqs);
        Assert.Equal(baselineRtt, snapAfterStaleAck.LastFrameRttMs);
        Assert.Equal(snapBeforeStaleAck.ValidAckCount, snapAfterStaleAck.ValidAckCount);
        Assert.Equal(snapBeforeStaleAck.UnknownAckCount + 1, snapAfterStaleAck.UnknownAckCount);
        Assert.Equal(snapBeforeStaleAck.StaleAckCount, snapAfterStaleAck.StaleAckCount);
        Assert.Equal(snapBeforeStaleAck.AckEventSignalCount, snapAfterStaleAck.AckEventSignalCount);

        // 新 Frame B 真實 ACK 到達後仍正常配對並更新 RTT（EWMA: prev*0.7 + sample*0.3）
        var ackB = host.InjectFrameAckForTest(MessageCodec.FrameAck(frameB));
        Assert.True(ackB.Accepted);
        Assert.Equal(0, ackB.InflightCountAfter);
        var snapFinal = host.GetFrameAckDiagnosticsSnapshot();
        Assert.True(
            snapFinal.LastFrameRttMs > baselineRtt + 20f,
            $"Expected Frame B EWMA RTT > baseline({baselineRtt:F1})+20ms, got {snapFinal.LastFrameRttMs:F1}ms");

        _output.WriteLine($"B1 passed: frameA evicted from stale LRU -> rejected as UnknownFrameAck; baselineRtt={baselineRtt:F1}ms preserved; frameBRtt={snapFinal.LastFrameRttMs:F1}ms");
    }

    [Fact]
    public async Task ItemB_B2_CompletelyUnknownFrameSeq_RejectedAsUnknownWithoutSideEffects()
    {
        var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
        using var _h = hostCh;
        using var _v = viewerCh;
        using var host = CreateSequenceSession(hostCh, clientHello);

        long now = host.ClockMsForTest;
        host.RegisterFrameSentForTest(10, now - 60);
        Assert.True(host.InjectFrameAckForTest(MessageCodec.FrameAck(10)).Accepted);
        float baselineRtt = host.GetFrameAckDiagnosticsSnapshot().LastFrameRttMs;

        host.RegisterFrameSentForTest(11, now - 20);
        var snapBefore = host.GetFrameAckDiagnosticsSnapshot();

        var res = host.InjectFrameAckForTest(MessageCodec.FrameAck(999999u));
        Assert.False(res.Accepted);
        Assert.Equal(FrameAckRejectReason.UnknownFrameAck, res.RejectReason);
        Assert.Equal(999999u, res.FrameSeq);
        Assert.Equal(1, res.InflightCountAfter);

        var snapAfter = host.GetFrameAckDiagnosticsSnapshot();
        Assert.Equal(snapBefore.ValidAckCount, snapAfter.ValidAckCount);
        Assert.Equal(snapBefore.UnknownAckCount + 1, snapAfter.UnknownAckCount);
        Assert.Equal(baselineRtt, snapAfter.LastFrameRttMs);
        Assert.Equal(new[] { 11u }, snapAfter.InflightSeqs);
    }

    [Fact]
    public async Task ItemB_B3_DuplicateFrameAck_RejectedWithoutDoubleInflightDecrementOrRttOverwrite()
    {
        var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
        using var _h = hostCh;
        using var _v = viewerCh;
        using var host = CreateSequenceSession(hostCh, clientHello);

        long now = host.ClockMsForTest;
        host.RegisterFrameSentForTest(20, now - 75);
        host.RegisterFrameSentForTest(21, now - 35);

        // 先 ACK 20
        var firstAck = host.InjectFrameAckForTest(MessageCodec.FrameAck(20));
        Assert.True(firstAck.Accepted);
        Assert.Equal(1, firstAck.InflightCountAfter);
        float rtt20 = host.GetFrameAckDiagnosticsSnapshot().LastFrameRttMs;
        var snapBeforeDup = host.GetFrameAckDiagnosticsSnapshot();

        // 重複 ACK 20 不得再扣除 inflight=1（應維持 seq=21 在途）
        var dupAck = host.InjectFrameAckForTest(MessageCodec.FrameAck(20));
        Assert.False(dupAck.Accepted);
        Assert.Equal(FrameAckRejectReason.DuplicateFrameAck, dupAck.RejectReason);
        Assert.Equal(1, dupAck.InflightCountAfter);

        var snapAfterDup = host.GetFrameAckDiagnosticsSnapshot();
        Assert.Equal(snapBeforeDup.ValidAckCount, snapAfterDup.ValidAckCount);
        Assert.Equal(snapBeforeDup.DuplicateAckCount + 1, snapAfterDup.DuplicateAckCount);
        Assert.Equal(rtt20, snapAfterDup.LastFrameRttMs);
        Assert.Equal(new[] { 21u }, snapAfterDup.InflightSeqs);

        // seq=21 仍能正常被 ACK
        var ack21 = host.InjectFrameAckForTest(MessageCodec.FrameAck(21));
        Assert.True(ack21.Accepted);
        Assert.Equal(0, ack21.InflightCountAfter);
    }

    [Fact]
    public async Task ItemB_B4_AckedLruEvictionOver128_LateOldGenerationAckStillRejected()
    {
        var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
        using var _h = hostCh;
        using var _v = viewerCh;
        using var host = CreateSequenceSession(hostCh, clientHello);

        // 舊世代送出 seq=1，接著重設 (seq=1 進 _staleSeqs)
        const uint oldGenSeq = 1;
        long now = host.ClockMsForTest;
        host.RegisterFrameSentForTest(oldGenSeq, now - 90);
        Assert.Equal(1, host.ResetInflightForTest("B4_ResetOldGen"));

        // 新世代大量送出並 ACK 150 個新序號（同時把 _ackedSeqs 128 窗撐滿並淘汰最舊 ACK，也在長跑下觀察 _staleSeqs 不會造成接受誤判）
        uint cursor = 2;
        for (int i = 0; i < 150; i++)
        {
            now = host.ClockMsForTest;
            uint s = cursor++;
            host.RegisterFrameSentForTest(s, now - 25);
            var ok = host.InjectFrameAckForTest(MessageCodec.FrameAck(s));
            Assert.True(ok.Accepted);
        }

        // 在第 150 輪的同一新世代，掛上第 151 張在途影格並鎖定基準 RTT
        float rttBefore = host.GetFrameAckDiagnosticsSnapshot().LastFrameRttMs;
        now = host.ClockMsForTest;
        uint inflightNew = cursor++;
        host.RegisterFrameSentForTest(inflightNew, now - 160);
        var snapBefore = host.GetFrameAckDiagnosticsSnapshot();

        // 1) 舊世代的 oldGenSeq=1 遲到（仍可能存在 _staleSeqs 因僅 reset 1 次，或先測試重覆舊 ACK）
        var resOldGen = host.InjectFrameAckForTest(MessageCodec.FrameAck(oldGenSeq));
        Assert.False(resOldGen.Accepted);
        Assert.Equal(FrameAckRejectReason.StaleFrameAck, resOldGen.RejectReason);
        Assert.Equal(1, resOldGen.InflightCountAfter);

        // 2) 新世代早期已被 _ackedSeqs(128) LRU 淘汰的 seq=2 重複遲到 ACK（>128 筆後已不在 _ackedSeqs）
        Assert.False(host.IsSeqInAckedLruForTest(2u));
        var resEvictedAck = host.InjectFrameAckForTest(MessageCodec.FrameAck(2u));
        Assert.False(resEvictedAck.Accepted);
        Assert.Equal(FrameAckRejectReason.UnknownFrameAck, resEvictedAck.RejectReason);
        Assert.Equal(1, resEvictedAck.InflightCountAfter);

        var snapAfter = host.GetFrameAckDiagnosticsSnapshot();
        Assert.Equal(rttBefore, snapAfter.LastFrameRttMs);
        Assert.Equal(new[] { inflightNew }, snapAfter.InflightSeqs);
        Assert.Equal(snapBefore.ValidAckCount, snapAfter.ValidAckCount);
        Assert.Equal(snapBefore.StaleAckCount + 1, snapAfter.StaleAckCount);
        Assert.Equal(snapBefore.UnknownAckCount + 1, snapAfter.UnknownAckCount);
        Assert.Equal(snapBefore.AckEventSignalCount, snapAfter.AckEventSignalCount);

        _output.WriteLine($"B4 passed: oldGenSeq=1 rejected ({resOldGen.RejectReason}); acked-evicted seq=2 rejected ({resEvictedAck.RejectReason}); inflightNew={inflightNew} preserved.");
    }

    [Fact]
    public async Task ItemB_B5_UInt32Wraparound_NormalOutOfOrderDuplicateResetStaleUnknownAndCrossGen()
    {
        var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
        using var _h = hostCh;
        using var _v = viewerCh;
        using var host = CreateSequenceSession(hostCh, clientHello);

        // 1. uint32 邊界連號 + 亂序 ACK: uint.MaxValue - 1, uint.MaxValue, 0, 1, 2
        uint s0 = uint.MaxValue - 1;
        uint s1 = uint.MaxValue;
        uint s2 = 0u;
        uint s3 = 1u;
        uint s4 = 2u;

        long now = host.ClockMsForTest;
        host.RegisterFrameSentForTest(s0, now - 70);
        host.RegisterFrameSentForTest(s1, now - 60);
        host.RegisterFrameSentForTest(s2, now - 50);

        // 亂序先 ACK 0 (cross-boundary wraparound!)
        var ackS2 = host.InjectFrameAckForTest(MessageCodec.FrameAck(s2));
        Assert.True(ackS2.Accepted);
        Assert.Equal(2, ackS2.InflightCountAfter);
        var snap = host.GetFrameAckDiagnosticsSnapshot();
        Assert.True(snap.HasAckedInGeneration);
        Assert.Equal(0u, snap.HighestAckedSeqInGeneration);

        // 再 ACK uint.MaxValue (不得退回 highestAckedSeq!)
        var ackS1 = host.InjectFrameAckForTest(MessageCodec.FrameAck(s1));
        Assert.True(ackS1.Accepted);
        Assert.Equal(1, ackS1.InflightCountAfter);
        Assert.Equal(0u, host.GetFrameAckDiagnosticsSnapshot().HighestAckedSeqInGeneration);

        // 再 ACK uint.MaxValue - 1
        var ackS0 = host.InjectFrameAckForTest(MessageCodec.FrameAck(s0));
        Assert.True(ackS0.Accepted);
        Assert.Equal(0, ackS0.InflightCountAfter);
        Assert.Equal(0u, host.GetFrameAckDiagnosticsSnapshot().HighestAckedSeqInGeneration);

        // 正常往前到 1, 2
        host.RegisterFrameSentForTest(s3, now - 30);
        host.RegisterFrameSentForTest(s4, now - 20);
        Assert.True(host.InjectFrameAckForTest(MessageCodec.FrameAck(s3)).Accepted);
        Assert.Equal(1u, host.GetFrameAckDiagnosticsSnapshot().HighestAckedSeqInGeneration);
        Assert.True(host.InjectFrameAckForTest(MessageCodec.FrameAck(s4)).Accepted);
        Assert.Equal(2u, host.GetFrameAckDiagnosticsSnapshot().HighestAckedSeqInGeneration);

        // 重複 ACK 在 wraparound 邊界 (seq=0 與 seq=uint.MaxValue)
        var dupZero = host.InjectFrameAckForTest(MessageCodec.FrameAck(0u));
        Assert.False(dupZero.Accepted);
        Assert.Equal(FrameAckRejectReason.DuplicateFrameAck, dupZero.RejectReason);
        var dupMax = host.InjectFrameAckForTest(MessageCodec.FrameAck(uint.MaxValue));
        Assert.False(dupMax.Accepted);
        Assert.Equal(FrameAckRejectReason.DuplicateFrameAck, dupMax.RejectReason);

        // 2. 跨世代 Reset 與交錯於 wraparound 邊界
        host.RegisterFrameSentForTest(uint.MaxValue, now - 80);
        host.RegisterFrameSentForTest(0u, now - 70);
        Assert.Equal(2, host.ResetInflightForTest("B5_WrapReset"));

        // 新世代送 1u, 2u（與舊世代相鄰且跨 0 邊界）
        host.RegisterFrameSentForTest(1u, now - 120);
        host.RegisterFrameSentForTest(2u, now - 110);

        // 舊世代 uint.MaxValue 與 0u ACK 遲到
        var staleMax = host.InjectFrameAckForTest(MessageCodec.FrameAck(uint.MaxValue));
        Assert.False(staleMax.Accepted);
        Assert.Equal(FrameAckRejectReason.StaleFrameAck, staleMax.RejectReason);
        Assert.Equal(2, staleMax.InflightCountAfter);

        var staleZero = host.InjectFrameAckForTest(MessageCodec.FrameAck(0u));
        Assert.False(staleZero.Accepted);
        Assert.Equal(FrameAckRejectReason.StaleFrameAck, staleZero.RejectReason);
        Assert.Equal(2, staleZero.InflightCountAfter);

        // 未知序號於邊界
        var unk = host.InjectFrameAckForTest(MessageCodec.FrameAck(3u));
        Assert.False(unk.Accepted);
        Assert.Equal(FrameAckRejectReason.UnknownFrameAck, unk.RejectReason);
        Assert.Equal(2, unk.InflightCountAfter);

        // 新世代 2u 先回、1u 後回仍精確配對
        var ack2New = host.InjectFrameAckForTest(MessageCodec.FrameAck(2u));
        Assert.True(ack2New.Accepted);
        Assert.Equal(1, ack2New.InflightCountAfter);
        Assert.Equal(2u, host.GetFrameAckDiagnosticsSnapshot().HighestAckedSeqInGeneration);

        var ack1New = host.InjectFrameAckForTest(MessageCodec.FrameAck(1u));
        Assert.True(ack1New.Accepted);
        Assert.Equal(0, ack1New.InflightCountAfter);
        Assert.Equal(2u, host.GetFrameAckDiagnosticsSnapshot().HighestAckedSeqInGeneration);

        _output.WriteLine("B5 passed: uint32 wraparound normal/OOO/dup/reset+stale/unknown/cross-gen verified cleanly.");
    }

    // =========================================================================
    // Verification Item A: RegisterSentFrameInflight vs Send Generation Race
    // =========================================================================

    private sealed class SyntheticCapturer : IScreenCapturer
    {
        private readonly byte[] _frame;
        private readonly GCHandle _pin;
        private readonly IntPtr _ptr;
        private int _pendingDirty = 1;
        private Rectangle _dirtyRect;
        private bool _first = true;

        public string Name => "RcVerifyCapturer";
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
            Volatile.Write(ref _pendingDirty, 1);
        }

        public bool Capture(int timeoutMs)
        {
            if (Interlocked.Exchange(ref _pendingDirty, 0) == 0)
            {
                if (timeoutMs > 0) Thread.Sleep(Math.Min(timeoutMs, 5));
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

    private static byte[] BuildSyntheticFullFrame(int w, int h, Color fill, uint seq, uint cfgVer)
    {
        using var cap = new SyntheticCapturer(w, h, fill);
        Assert.True(cap.Capture(0));
        var enc = new FrameEncoder(w, h) { Quality = 90, Lossless = true };
        enc.RequestKeyframe();
        var tiles = enc.Encode(cap.Frame, true, 0, out var frameType, out var moves);
        return FrameEncoder.Serialize(seq, seq, 0, frameType, w, h, moves, tiles, cfgVer);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failMessage)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed >= timeout)
            {
                throw new TimeoutException(failMessage);
            }
            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task ItemA_A1_NackBetweenRegisterAndSend_LiveCaptureLoop_MustAbortStaleFrameBeforeSend()
    {
        var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
        using var _h = hostCh;
        using var _v = viewerCh;
        using var capturer = new SyntheticCapturer(320, 180, Color.SteelBlue);
        var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "Mon1", 1, 0, 0, 320, 180, true, 96) };

        using var frame1AtHook = new ManualResetEventSlim(false);
        using var allowFrame1Proceed = new ManualResetEventSlim(false);
        ulong pausedFrameGen = 0;
        var receivedSeqs = new ConcurrentQueue<uint>();
        var receivedTypes = new ConcurrentQueue<FrameType>();
        var firstPostResetKeyframeReceived = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var host = new HostSession(hostCh, clientHello, "127.0.0.1", new NullHostUi())
        {
            AllowDxgi = false,
            MonitorsOverride = () => monList,
            CapturerOverride = (_, _) => capturer,
            BeforeVideoFrameSendHookForTest = (seq, gen) =>
            {
                if (seq == 1)
                {
                    Volatile.Write(ref pausedFrameGen, gen);
                    frame1AtHook.Set();
                    allowFrame1Proceed.Wait(TimeSpan.FromSeconds(10));
                }
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var hostTask = Task.Run(host.RunAsync);

        var viewerTask = Task.Run(async () =>
        {
            using var dec = new FrameDecoder();
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var msg = await viewerCh.ReceiveAsync(cts.Token).ConfigureAwait(false);
                    var r = new PacketReader(msg.AsSpan(1));
                    switch ((HostMsg)msg[0])
                    {
                        case HostMsg.StreamConfig:
                        {
                            var (v, _, _, sw, sh, _, _, _) = MessageCodec.ReadStreamConfig(ref r);
                            dec.ConfigureStream(v, sw, sh);
                            // 故意送 StreamConfigAck 或等待亦可；此處依真實流程先回覆 StreamConfigAck
                            await viewerCh.SendAsync(MessageCodec.StreamConfigAck(v, sw, sh), highPriority: true, cts.Token).ConfigureAwait(false);
                            break;
                        }
                        case HostMsg.VideoFrame:
                        {
                            uint wireSeq = BinaryPrimitives.ReadUInt32LittleEndian(msg.AsSpan(1));
                            FrameType fType = (FrameType)(msg[21] & 0x7F);
                            receivedSeqs.Enqueue(wireSeq);
                            receivedTypes.Enqueue(fType);
                            dec.DecodeFrame(msg.AsSpan(1), out _, out _, out _, out _, out _, out _);
                            await viewerCh.SendAsync(MessageCodec.FrameAck(wireSeq), highPriority: true, cts.Token).ConfigureAwait(false);
                            if (wireSeq >= 2 && fType == FrameType.FullFrame)
                            {
                                firstPostResetKeyframeReceived.TrySetResult(wireSeq);
                            }
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        });

        try
        {
            // 1. 等候 CaptureLoop 註冊 Frame 1 並停在 Register -> Send 視窗之間
            Assert.True(frame1AtHook.Wait(TimeSpan.FromSeconds(8)), "Timed out waiting for CaptureLoop at BeforeVideoFrameSendHook(seq=1)");
            ulong genBeforeNack = Volatile.Read(ref pausedFrameGen);

            // 2. 在尚未 Send(Frame 1) 時，由 Viewer 送出 RequestKeyframe (NACK) 並等候 HostSession 完成 Reset
            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.RequestKeyframe, 1).ToArray(), highPriority: true, cts.Token);
            await WaitUntilAsync(
                () => host.GetFrameAckDiagnosticsSnapshot().Generation >= genBeforeNack + 1,
                TimeSpan.FromSeconds(5),
                $"HostSession generation did not advance past {genBeforeNack} after RequestKeyframe");

            // 3. 放行已被世代淘汰的 CaptureLoop (seq=1) 並提供新畫面變動供新世代 Keyframe 擷取
            allowFrame1Proceed.Set();
            capturer.FillSolid(Color.Crimson);

            // 4. 等候新世代 Keyframe (seq >= 2) 送達並被 ACK
            uint kf2Seq = await firstPostResetKeyframeReceived.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await WaitUntilAsync(
                () => host.GetFrameAckDiagnosticsSnapshot().InflightCount == 0 &&
                      host.GetFrameAckDiagnosticsSnapshot().ValidAckCount >= 1,
                TimeSpan.FromSeconds(5),
                "New keyframe ACK did not settle");

            var wireSeqArray = receivedSeqs.ToArray();
            var snap = host.GetFrameAckDiagnosticsSnapshot();

            Assert.DoesNotContain(1u, wireSeqArray);
            Assert.Equal(kf2Seq, wireSeqArray[0]);
            Assert.Equal(FrameType.FullFrame, receivedTypes.ToArray()[0]);
            Assert.True(snap.SupersededSendSkippedCount >= 1,
                $"Expected SupersededSendSkippedCount >= 1, got {snap.SupersededSendSkippedCount}");

            _output.WriteLine($"A1 passed: seq=1 (gen=1) aborted before Send; first wire frame seq={kf2Seq}; SupersededSkipped={snap.SupersededSendSkippedCount}");
        }
        finally
        {
            allowFrame1Proceed.Set();
            cts.Cancel();
            host.Disconnect();
            try { await hostTask; } catch { }
            try { await viewerTask; } catch { }
        }
    }

    private async Task RunConfigOrCapturerResetBetweenRegisterAndSendAsync(string resetReason, uint oldSeq, uint newKeyframeSeq)
    {
        var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
        using var _h = hostCh;
        using var _v = viewerCh;
        using var host = CreateSequenceSession(hostCh, clientHello);

        var transmittedSeqs = new ConcurrentQueue<uint>();
        using var atHook = new ManualResetEventSlim(false);
        using var allowProceed = new ManualResetEventSlim(false);

        host.VideoFrameSendTransportForTest = bytes =>
        {
            transmittedSeqs.Enqueue(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1)));
            return true;
        };
        host.BeforeVideoFrameSendHookForTest = (seq, gen) =>
        {
            if (seq == oldSeq)
            {
                atHook.Set();
                allowProceed.Wait(TimeSpan.FromSeconds(5));
            }
        };

        byte[] oldBytes = BuildSyntheticFullFrame(128, 96, Color.Red, oldSeq, 1);
        byte[] newBytes = BuildSyntheticFullFrame(128, 96, Color.Blue, newKeyframeSeq, 2);

        ulong oldGen = host.RegisterFrameSentForTest(oldSeq, host.ClockMsForTest - 20);
        var sendOldTask = Task.Run(() => host.SendRegisteredFrameForTest(oldSeq, oldGen, oldBytes));

        Assert.True(atHook.Wait(TimeSpan.FromSeconds(5)));

        // 在 Register -> Send 視窗內發生 Reset (ConfigSwitch / CapturerRecreate) 並產生新世代 Keyframe B
        int cleared = host.ResetInflightForTest(resetReason);
        Assert.Equal(1, cleared);

        ulong newGen = host.RegisterFrameSentForTest(newKeyframeSeq, host.ClockMsForTest - 10);
        Assert.True(newGen > oldGen);
        var sendNewOutcome = host.SendRegisteredFrameForTest(newKeyframeSeq, newGen, newBytes);
        Assert.Equal(VideoFrameSendOutcome.Sent, sendNewOutcome);

        // 放行 Thread A
        allowProceed.Set();
        var oldOutcome = await sendOldTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(VideoFrameSendOutcome.SkippedStaleGeneration, oldOutcome);
        Assert.Equal(new[] { newKeyframeSeq }, transmittedSeqs.ToArray());

        var snap = host.GetFrameAckDiagnosticsSnapshot();
        Assert.Equal(newGen, snap.Generation);
        Assert.Equal(new[] { newKeyframeSeq }, snap.InflightSeqs);
        Assert.True(snap.SupersededSendSkippedCount >= 1);
    }

    [Fact]
    public async Task ItemA_A2_ConfigSwitchBetweenRegisterAndSend_MustAbortOldGenerationFrame()
    {
        await RunConfigOrCapturerResetBetweenRegisterAndSendAsync("ConfigSwitch", 10, 11);
    }

    [Fact]
    public async Task ItemA_A3_CapturerRecreateBetweenRegisterAndSend_MustAbortOldGenerationFrame()
    {
        await RunConfigOrCapturerResetBetweenRegisterAndSendAsync("CapturerRecreate", 20, 21);
    }

    [Fact]
    public async Task ItemA_A4_SendFailureWithConcurrentResetSameSeq_RollbackMustPreserveNewGeneration()
    {
        var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
        using var _h = hostCh;
        using var _v = viewerCh;
        using var host = CreateSequenceSession(hostCh, clientHello);

        const uint reusedSeq = 30;
        using var insideSend = new ManualResetEventSlim(false);
        using var allowSendReturnFailure = new ManualResetEventSlim(false);

        host.VideoFrameSendTransportForTest = _ =>
        {
            insideSend.Set();
            allowSendReturnFailure.Wait(TimeSpan.FromSeconds(5));
            return false;
        };

        byte[] frameBytes = BuildSyntheticFullFrame(128, 96, Color.Green, reusedSeq, 1);
        ulong gen1 = host.RegisterFrameSentForTest(reusedSeq, host.ClockMsForTest - 40);
        var failSendTask = Task.Run(() => host.SendRegisteredFrameForTest(reusedSeq, gen1, frameBytes));

        Assert.True(insideSend.Wait(TimeSpan.FromSeconds(5)));

        // 在 Send(false) 返回前發生世代 Reset，且新世代又重用了相同 seq=30
        Assert.Equal(1, host.ResetInflightForTest("A4_ConcurrentResetBeforeRollback"));
        ulong gen2 = host.RegisterFrameSentForTest(reusedSeq, host.ClockMsForTest - 15);
        Assert.True(gen2 > gen1);

        allowSendReturnFailure.Set();
        var outcome = await failSendTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VideoFrameSendOutcome.TransportFailed, outcome);

        // RollbackUnqueuedFrameInflight(reusedSeq, gen1) 絕不能把 gen2 的 reusedSeq 刪掉！
        var snap = host.GetFrameAckDiagnosticsSnapshot();
        Assert.Equal(gen2, snap.Generation);
        Assert.Equal(1, snap.InflightCount);
        Assert.Equal(new[] { reusedSeq }, snap.InflightSeqs);

        var ackGen2 = host.InjectFrameAckForTest(MessageCodec.FrameAck(reusedSeq));
        Assert.True(ackGen2.Accepted);
        Assert.Equal(gen2, ackGen2.Generation);
        Assert.Equal(0, ackGen2.InflightCountAfter);
    }

    [Fact]
    public async Task ItemA_A5_InflightSendOverlappingResetAndNewKeyframe_NonBlockingResetAndStrictWireOrder()
    {
        var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
        using var _h = hostCh;
        using var _v = viewerCh;
        using var host = CreateSequenceSession(hostCh, clientHello);

        const uint seqA = 50;
        const uint seqB = 51;
        var transmittedSeqs = new ConcurrentQueue<uint>();
        using var sendAEnteredTransport = new ManualResetEventSlim(false);
        using var allowSendAFinish = new ManualResetEventSlim(false);

        host.VideoFrameSendTransportForTest = bytes =>
        {
            uint s = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1));
            transmittedSeqs.Enqueue(s);
            if (s == seqA)
            {
                sendAEnteredTransport.Set();
                allowSendAFinish.Wait(TimeSpan.FromSeconds(5));
            }
            return true;
        };

        byte[] bytesA = BuildSyntheticFullFrame(128, 96, Color.Orange, seqA, 1);
        byte[] bytesB = BuildSyntheticFullFrame(128, 96, Color.Purple, seqB, 2);

        ulong gen1 = host.RegisterFrameSentForTest(seqA, host.ClockMsForTest - 20);
        var taskA = Task.Run(() => host.SendRegisteredFrameForTest(seqA, gen1, bytesA));

        Assert.True(sendAEnteredTransport.Wait(TimeSpan.FromSeconds(5)));

        // 當 seqA 正在執行傳輸 I/O 時，Reset 必須立即完成（證明未持有 _inflightLock 做 I/O）
        int cleared = host.ResetInflightForTest("A5_ResetWhileSendingA");
        Assert.Equal(1, cleared);

        ulong gen2 = host.RegisterFrameSentForTest(seqB, host.ClockMsForTest - 10);
        var taskB = Task.Run(() => host.SendRegisteredFrameForTest(seqB, gen2, bytesB));

        // 給 taskB 一小段排程機會；若沒有 _videoSendOrderLock，seqB 會插隊在 allowSendAFinish.Set() 前就先送出！
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 25)
        {
            Assert.Equal(new[] { seqA }, transmittedSeqs.ToArray());
            await Task.Delay(2);
        }

        allowSendAFinish.Set();
        var outcomes = await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VideoFrameSendOutcome.Sent, outcomes[0]);
        Assert.Equal(VideoFrameSendOutcome.Sent, outcomes[1]);

        // 線路順序必須嚴格是 Frame A 先完成，Keyframe B 後送出（絕不允許 Frame A 晚於 Keyframe B 抵達）
        Assert.Equal(new[] { seqA, seqB }, transmittedSeqs.ToArray());
    }

    [Fact]
    public async Task ItemA_Stress100_RegisterToSendGenerationRaces_ZeroLeaksAndZeroOutOfOrder()
    {
        for (int iter = 0; iter < 100; iter++)
        {
            string reason = (iter % 2 == 0) ? "ConfigSwitch" : "CapturerRecreate";
            await RunConfigOrCapturerResetBetweenRegisterAndSendAsync(reason, (uint)(1000 + iter * 2), (uint)(1001 + iter * 2));
        }

        _output.WriteLine("ItemA Stress 100/100 completed with zero stale leaks.");
    }
}
