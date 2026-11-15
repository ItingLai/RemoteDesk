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

public class FrameSeqAckSecurityTests
{
    private readonly ITestOutputHelper _output;

    public FrameSeqAckSecurityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // -------------------------------------------------------------------------
    // Helpers
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

        public string Name => "FrameSeqMockCapturer";
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
            Volatile.Write(ref _pendingDirty, 1);
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

    private sealed class WriteDelayStream : Stream
    {
        private readonly Stream _inner;
        private int _writeDelayMs;

        public WriteDelayStream(Stream inner) => _inner = inner;
        public void SetDelayMs(int ms) => Volatile.Write(ref _writeDelayMs, Math.Max(0, ms));

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
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            int d = Volatile.Read(ref _writeDelayMs);
            if (d > 0) Thread.Sleep(d);
            _inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            int d = Volatile.Read(ref _writeDelayMs);
            if (d > 0) await Task.Delay(d, ct).ConfigureAwait(false);
            await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int d = Volatile.Read(ref _writeDelayMs);
            if (d > 0) await Task.Delay(d, ct).ConfigureAwait(false);
            await _inner.WriteAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static async Task<(SecureChannel hostCh, ClientHello clientHello, SecureChannel viewerCh, ServerHello serverHello)> CreateTcpSecurePairAsync(
        ProtocolCapabilities viewerCaps = ProtocolCapabilities.FrameSeqAck,
        Func<ClientHello, ProtocolCapabilities>? serverCapNegotiator = null,
        Func<Stream, Stream>? hostStreamWrapper = null)
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

        Stream hostStream = hostStreamWrapper != null
            ? hostStreamWrapper(hostClient.GetStream())
            : hostClient.GetStream();

        var serverTask = SecureChannel.ServerHandshakeAsync(
            hostStream,
            ch => new ServerHello(
                Protocol.Version,
                "990000001",
                "TestHost",
                Array.Empty<byte>(),
                1,
                false,
                false,
                false,
                serverCapNegotiator != null ? serverCapNegotiator(ch) : (ch.Capabilities & ProtocolCapabilities.FrameSeqAck)),
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

    private static byte[] BuildSyntheticFullFrame(int w, int h, Color fill, uint seq, uint ver, uint cfgVer)
    {
        using var cap = new SyntheticCapturer(w, h, fill);
        Assert.True(cap.Capture(0));
        var enc = new FrameEncoder(w, h) { Quality = 90, Lossless = true };
        enc.RequestKeyframe();
        var tiles = enc.Encode(cap.Frame, true, 0, out var frameType, out var moves);
        return FrameEncoder.Serialize(seq, ver, 0, frameType, w, h, moves, tiles, cfgVer);
    }

    private static HostSession CreateSequenceSession(SecureChannel hostCh, ClientHello hello) =>
        new(hostCh, hello, "127.0.0.1", new NullHostUi());

    // =========================================================================
    // 矩陣 A — 基本正確性 (Basic FrameSeq ACK & RTT)
    // =========================================================================

    [Fact]
    public async Task MatrixA_SingleAndPipelinedFrameSeqAck_UpdatesInflightAndRttAccurately()
    {
        var (hostCh, clientHello, viewerCh, serverHello) = await CreateTcpSecurePairAsync();
        using var _h = hostCh;
        using var _v = viewerCh;
        using var host = CreateSequenceSession(hostCh, clientHello);

        Assert.Equal(ProtocolCapabilities.FrameSeqAck, clientHello.Capabilities);
        Assert.Equal(ProtocolCapabilities.FrameSeqAck, serverHello.Capabilities);
        Assert.Equal(FrameAckMode.Sequence, host.AckMode);
        Assert.True(host.SupportsFrameSeqAck);

        // 1. 單張 FrameSeq ACK + RTT 量測
        long now = host.ClockMsForTest;
        host.RegisterFrameSentForTest(1, now - 45);
        var snapBefore = host.GetFrameAckDiagnosticsSnapshot();
        Assert.Equal(1, snapBefore.InflightCount);

        var r1 = host.InjectFrameAckForTest(MessageCodec.FrameAck(1));
        Assert.True(r1.Accepted);
        Assert.Equal(1u, r1.FrameSeq);
        Assert.Equal(0, r1.InflightCountAfter);

        var snapAfter1 = host.GetFrameAckDiagnosticsSnapshot();
        Assert.Equal(1, snapAfter1.ValidAckCount);
        Assert.Equal(0, snapAfter1.InflightCount);
        Assert.InRange(snapAfter1.LastFrameRttMs, 35f, 150f);

        // 2. 兩張並發管線影格 (seq=2, seq=3) 精準扣除對應名額
        now = host.ClockMsForTest;
        host.RegisterFrameSentForTest(2, now - 50);
        host.RegisterFrameSentForTest(3, now - 40);
        Assert.Equal(2, host.GetFrameAckDiagnosticsSnapshot().InflightCount);

        var r2 = host.InjectFrameAckForTest(MessageCodec.FrameAck(2));
        Assert.True(r2.Accepted);
        Assert.Equal(1, r2.InflightCountAfter);

        var r3 = host.InjectFrameAckForTest(MessageCodec.FrameAck(3));
        Assert.True(r3.Accepted);
        Assert.Equal(0, r3.InflightCountAfter);
        Assert.Equal(3, host.GetFrameAckDiagnosticsSnapshot().ValidAckCount);
    }

    [Fact]
    public async Task MatrixA_OutOfOrderAck_PreservesExactInflightAndNonRegressingRtt()
    {
        var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
        using var _h = hostCh;
        using var _v = viewerCh;
        using var host = CreateSequenceSession(hostCh, clientHello);

        long baseNow = host.ClockMsForTest;
        host.RegisterFrameSentForTest(100, baseNow - 90);
        host.RegisterFrameSentForTest(101, baseNow - 25);

        // 較新的 101 先抵達：RTT 應採計 101 (~25ms)，HighestAckedSeq = 101
        var r101 = host.InjectFrameAckForTest(MessageCodec.FrameAck(101));
        Assert.True(r101.Accepted);
        Assert.Equal(1, r101.InflightCountAfter);

        var midSnap = host.GetFrameAckDiagnosticsSnapshot();
        float rttAfter101 = midSnap.LastFrameRttMs;
        Assert.True(midSnap.HasAckedInGeneration);
        Assert.Equal(101u, midSnap.HighestAckedSeqInGeneration);
        Assert.InRange(rttAfter101, 15f, 80f);
        Assert.Equal(new[] { 100u }, midSnap.InflightSeqs);

        // 較舊的 100 隨後亂序抵達：因仍在 _inflightFrames，應正確扣減 Inflight 至 0，但絕不可覆寫 101 的 RTT
        var r100 = host.InjectFrameAckForTest(MessageCodec.FrameAck(100));
        Assert.True(r100.Accepted);
        Assert.Equal(0, r100.InflightCountAfter);

        var endSnap = host.GetFrameAckDiagnosticsSnapshot();
        Assert.Equal(2, endSnap.ValidAckCount);
        Assert.Equal(rttAfter101, endSnap.LastFrameRttMs);
        Assert.Equal(101u, endSnap.HighestAckedSeqInGeneration);
    }

    [Fact]
    public async Task MatrixA_DuplicateAndUnknownFrameAck_RejectedWithoutCorruptingInflightOrRtt()
    {
        var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
        using var _h = hostCh;
        using var _v = viewerCh;
        using var host = CreateSequenceSession(hostCh, clientHello);

        long now = host.ClockMsForTest;
        host.RegisterFrameSentForTest(10, now - 40);
        Assert.True(host.InjectFrameAckForTest(MessageCodec.FrameAck(10)).Accepted);
        float baselineRtt = host.GetFrameAckDiagnosticsSnapshot().LastFrameRttMs;

        // 讓 seq=11 保持在途
        host.RegisterFrameSentForTest(11, host.ClockMsForTest);

        // 重複 ACK seq=10
        var dup = host.InjectFrameAckForTest(MessageCodec.FrameAck(10));
        Assert.False(dup.Accepted);
        Assert.Equal(FrameAckRejectReason.DuplicateFrameAck, dup.RejectReason);
        Assert.Equal(1, dup.InflightCountAfter);

        // 未發出過 / 未來 ACK seq=999999
        var unk = host.InjectFrameAckForTest(MessageCodec.FrameAck(999999));
        Assert.False(unk.Accepted);
        Assert.Equal(FrameAckRejectReason.UnknownFrameAck, unk.RejectReason);
        Assert.Equal(1, unk.InflightCountAfter);

        var snap = host.GetFrameAckDiagnosticsSnapshot();
        Assert.Equal(1, snap.ValidAckCount);
        Assert.Equal(1, snap.DuplicateAckCount);
        Assert.Equal(1, snap.UnknownAckCount);
        Assert.Equal(1, snap.InflightCount);
        Assert.Equal(baselineRtt, snap.LastFrameRttMs);
    }

    [Fact]
    public void MatrixA_ViewerSession_DecodeStatusContract_And_WireFormat()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"rd_frameseq_vs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var identity = DeviceIdentity.LoadOrCreate(tempRoot, "111222333", "SeqViewer");
            var cfg = new ViewerConfig { ViewerId = "111222333" };
            using var session = new ViewerSession("127.0.0.1", cfg, new LanDiscovery("111222333"), "111222333", identity);

            Assert.True(session.SupportsFrameSeqAck);
            int w = 160, h = 90;
            session.DispatchForTest(MessageCodec.StreamConfig(1, w, h, w, h, ResolutionLevel.Level0, ResolutionMode.Auto, "Init"));

            // 1. Applied -> 回傳精確 FrameSeq=11
            var f11 = BuildSyntheticFullFrame(w, h, Color.CornflowerBlue, seq: 11, ver: 1, cfgVer: 1);
            session.DispatchForTest(f11);
            Assert.Equal(FrameDecodeStatus.Applied, session.LastDecodeStatusForTest);
            Assert.Equal(1, session.FrameAckCountForTest);
            Assert.Equal(11u, session.LastAckedFrameSeqForTest);

            // 2. EmptyNoChange -> 仍回傳精確 FrameSeq=12
            var f12Empty = new PacketWriter((byte)HostMsg.VideoFrame, 64)
                .U32(12).I32(w).I32(h).U32(2).U32(1).U8((byte)FrameType.RegionUpdate | 0x80).U32(1)
                .U16(0).U16(0).ToArray();
            session.DispatchForTest(f12Empty);
            Assert.Equal(FrameDecodeStatus.EmptyNoChange, session.LastDecodeStatusForTest);
            Assert.Equal(2, session.FrameAckCountForTest);
            Assert.Equal(12u, session.LastAckedFrameSeqForTest);

            // 3. StaleConfigVersion -> 回傳過期幀本身 seq=13，絕不混入新幀 seq
            session.DispatchForTest(MessageCodec.StreamConfig(2, w, h, w, h, ResolutionLevel.Level0, ResolutionMode.Auto, "Switch2"));
            var f13Stale = BuildSyntheticFullFrame(w, h, Color.Crimson, seq: 13, ver: 13, cfgVer: 1);
            session.DispatchForTest(f13Stale);
            Assert.Equal(FrameDecodeStatus.StaleConfigVersion, session.LastDecodeStatusForTest);
            Assert.Equal(3, session.FrameAckCountForTest);
            Assert.Equal(13u, session.LastAckedFrameSeqForTest);

            // 4. Desynced -> 觸發 RequestKeyframe，不送 FrameAck
            var f14Desync = new PacketWriter((byte)HostMsg.VideoFrame, 64)
                .U32(14).I32(w).I32(h).U32(14).U32(999).U8((byte)FrameType.RegionUpdate | 0x80).U32(2)
                .U16(0).U16(0).ToArray();
            session.DispatchForTest(f14Desync);
            Assert.Equal(FrameDecodeStatus.Desynced, session.LastDecodeStatusForTest);
            Assert.Equal(3, session.FrameAckCountForTest);
            Assert.Equal(1, session.KeyframeRequestCountForTest);

            // 5. Failed -> 觸發 RequestKeyframe，不送 FrameAck
            var f15Bad = new PacketWriter((byte)HostMsg.VideoFrame, 64)
                .U32(15).I32(w).I32(h).U32(15).U32(2).U8((byte)FrameType.RegionUpdate | 0x80).U32(2)
                .U16(0).U16(1)
                .U16(0).U16(0).U16(16).U16(16).U8((byte)TileEncoding.Jpeg).Bytes(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF })
                .ToArray();
            session.DispatchForTest(f15Bad);
            Assert.Equal(FrameDecodeStatus.Failed, session.LastDecodeStatusForTest);
            Assert.Equal(3, session.FrameAckCountForTest);
            Assert.Equal(2, session.KeyframeRequestCountForTest);

            // 驗證線格式編碼長度與序號位元組順序 (5 bytes, Little Endian)
            var pkt = MessageCodec.FrameAck(0x11223344u);
            Assert.Equal(5, pkt.Length);
            Assert.Equal((byte)ViewerMsg.FrameAck, pkt[0]);
            Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(pkt.AsSpan(1)));
            Assert.Equal(new byte[] { (byte)ViewerMsg.FrameAck }, MessageCodec.LegacyFrameAck());
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    // =========================================================================
    // 矩陣 B — NACK 跨世代防護 (Live TCP Loopback)
    // =========================================================================

    [Fact]
    public async Task MatrixB_NackCrossGeneration_LiveHostSession_StaleOldAcksRejected_MaxInflightAndRttProtected()
    {
        var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
        using var _h = hostCh;
        using var _v = viewerCh;
        var capturer = new SyntheticCapturer(320, 180, Color.DarkSlateBlue);
        var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "Mon1", 1, 0, 0, 320, 180, true, 96) };

        using var host = new HostSession(hostCh, clientHello, "127.0.0.1", new NullHostUi())
        {
            AllowDxgi = false,
            MonitorsOverride = () => monList,
            CapturerOverride = (_, _) => capturer
        };
        Assert.Equal(FrameAckMode.Sequence, host.AckMode);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var hostTask = Task.Run(host.RunAsync);

        var preNackSeqs = new ConcurrentQueue<uint>();
        int preNackFrames = 0;
        int nackPhase = 0; // 0=收集2張舊世代幀並扣留ACK, 1=送出NACK後收到新Keyframe, 2=投遞舊FrameAck並鎖定新世代觀察窗
        uint newKeyframeSeq = 0;
        int framesReceivedWhileNewKfUnacked = 0;
        long allowGenuineAck = 0;
        long newKfAckDelivered = 0;

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
                            dec.DecodeFrame(msg.AsSpan(1), out var frameSeq, out _, out _, out _, out _, out _);
                            int phase = Volatile.Read(ref nackPhase);
                            if (phase == 0)
                            {
                                preNackSeqs.Enqueue(frameSeq);
                                Interlocked.Increment(ref preNackFrames);
                            }
                            else if (phase == 1)
                            {
                                newKeyframeSeq = frameSeq;
                                Interlocked.Exchange(ref nackPhase, 2);
                                Interlocked.Increment(ref framesReceivedWhileNewKfUnacked);

                                // 模擬僅隔 ~10ms 後遲到的「舊世代 FrameAck」，帶的是舊影格 seq
                                await Task.Delay(10, cts.Token);
                                while (preNackSeqs.TryDequeue(out uint staleSeq))
                                {
                                    await viewerCh.SendAsync(MessageCodec.FrameAck(staleSeq), highPriority: true, cts.Token);
                                }
                            }
                            else if (phase == 2)
                            {
                                if (Interlocked.Read(ref allowGenuineAck) == 0)
                                {
                                    Interlocked.Increment(ref framesReceivedWhileNewKfUnacked);
                                }
                                else if (Interlocked.CompareExchange(ref newKfAckDelivered, 1, 0) == 0)
                                {
                                    await Task.Delay(160, cts.Token);
                                    await viewerCh.SendAsync(MessageCodec.FrameAck(frameSeq), highPriority: true, cts.Token);
                                }
                            }
                            break;
                        }
                    }
                }
                catch { break; }
            }
        });

        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5).U8(75).U8(30).Bool(false).U8((byte)ResolutionMode.Auto).ToArray(), highPriority: true, cts.Token);

        // 1. 填滿 MaxInflight = 2 (不回 ACK)
        for (int i = 0; i < 20 && Volatile.Read(ref preNackFrames) < 2; i++)
        {
            capturer.PaintRect(new Rectangle(10 + i * 5, 10, 80, 80), Color.FromArgb(40 + i * 10, 120, 200));
            await Task.Delay(30);
        }
        Assert.Equal(2, Volatile.Read(ref preNackFrames));

        // 2. 送出 RequestKeyframe NACK -> 清空舊 2 筆並切換至 Generation 2
        long resetsBeforeNack = host.GetFrameAckDiagnosticsSnapshot().InflightResetCount;
        Interlocked.Exchange(ref nackPhase, 1);
        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.RequestKeyframe, 1).ToArray(), highPriority: true, cts.Token);

        for (int i = 0; i < 25 && Volatile.Read(ref nackPhase) < 2; i++)
        {
            capturer.PaintRect(new Rectangle(20, 20, 90, 90), Color.Gold);
            await Task.Delay(25);
        }
        Assert.Equal(2, Volatile.Read(ref nackPhase));

        // 3. 確保遲到舊 FrameAck(seq1, seq2) 已抵達 Host 後，嘗試連續推進多筆畫面變更
        await Task.Delay(80);
        for (int i = 0; i < 5; i++)
        {
            capturer.PaintRect(new Rectangle(30 + i * 8, 30, 80, 80), Color.FromArgb(200, 40 + i * 25, 90));
            await Task.Delay(35);
        }
        await Task.Delay(120);

        var snapMid = host.GetFrameAckDiagnosticsSnapshot();
        int receivedWhileNewUnacked = Volatile.Read(ref framesReceivedWhileNewKfUnacked);
        _output.WriteLine($"[Matrix B NACK 防護] OldAcksRejected={snapMid.StaleAckCount}, ValidAcks={snapMid.ValidAckCount}, Inflight={snapMid.InflightCount}, FramesWhileNewUnacked={receivedWhileNewUnacked}, RTT={snapMid.LastFrameRttMs:F1}ms");

        // 核心安全不變量：
        // - 兩張舊 ACK 必須全部識別為 StaleFrameAck 拒絕，不可誤配新世代 Keyframe
        // - RTT 保持 0 (未被 10ms 假延遲污染)
        // - 總在途新世代影格 <= MaxInflight(2)，不得因舊 ACK 而提前偷渡第 3 張
        Assert.Equal(2, snapMid.StaleAckCount);
        Assert.Equal(0, snapMid.ValidAckCount);
        Assert.Equal(0f, snapMid.LastFrameRttMs);
        Assert.True(receivedWhileNewUnacked <= 2, $"新世代尚未 ACK 前最多僅能放行 MaxInflight=2 張影格，實際={receivedWhileNewUnacked}");
        Assert.Equal(resetsBeforeNack + 1, snapMid.InflightResetCount);
        Assert.Equal("RequestKeyframe", snapMid.LastResetReason);

        // 4. 最後補上真正的新 Keyframe seq ACK (~160ms 後)，驗證 RTT 準確恢復且 Inflight 降回
        Interlocked.Exchange(ref allowGenuineAck, 1);
        await viewerCh.SendAsync(MessageCodec.FrameAck(newKeyframeSeq), highPriority: true, cts.Token);
        await Task.Delay(100);

        var snapAfterRealAck = host.GetFrameAckDiagnosticsSnapshot();
        Assert.True(snapAfterRealAck.ValidAckCount >= 1);
        Assert.True(snapAfterRealAck.LastFrameRttMs >= 100f, $"真實 Keyframe ACK 後 RTT 應真實反映延遲 (>=100ms)，實際={snapAfterRealAck.LastFrameRttMs:F1}ms");

        cts.Cancel();
        host.Dispose();
        try { await hostTask; await viewerLoop; } catch { }
    }

    // =========================================================================
    // 矩陣 C — 1200ms Inflight Timeout 跨世代防護
    // =========================================================================

    [Fact]
    public async Task MatrixC_Timeout1200msCrossGeneration_LiveHostSession_StaleOldAcksRejected()
    {
        var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
        using var _h = hostCh;
        using var _v = viewerCh;
        var capturer = new SyntheticCapturer(320, 180, Color.DarkOliveGreen);
        var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "Mon1", 1, 0, 0, 320, 180, true, 96) };

        using var host = new HostSession(hostCh, clientHello, "127.0.0.1", new NullHostUi())
        {
            AllowDxgi = false,
            MonitorsOverride = () => monList,
            CapturerOverride = (_, _) => capturer
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var hostTask = Task.Run(host.RunAsync);

        var timedOutSeqs = new ConcurrentQueue<uint>();
        int phase = 0; // 0=收前2張但不ACK使其逾時1200ms, 1=收到Timeout後第3張並回傳遲到舊ACK
        uint postTimeoutSeq = 0;
        int postTimeoutUnackedCount = 0;

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
                            dec.DecodeFrame(msg.AsSpan(1), out var frameSeq, out _, out _, out _, out _, out _);
                            if (Volatile.Read(ref phase) == 0)
                            {
                                timedOutSeqs.Enqueue(frameSeq);
                            }
                            else if (Volatile.Read(ref phase) == 1)
                            {
                                if (postTimeoutSeq == 0) postTimeoutSeq = frameSeq;
                                int c = Interlocked.Increment(ref postTimeoutUnackedCount);
                                if (c == 1)
                                {
                                    // 收到 Timeout 後新 Keyframe 後，故意投遞舊世代遲到 ACK
                                    await Task.Delay(10, cts.Token);
                                    while (timedOutSeqs.TryDequeue(out uint oldSeq))
                                    {
                                        await viewerCh.SendAsync(MessageCodec.FrameAck(oldSeq), highPriority: true, cts.Token);
                                    }
                                }
                            }
                            break;
                        }
                    }
                }
                catch { break; }
            }
        });

        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5).U8(75).U8(30).Bool(false).U8((byte)ResolutionMode.Auto).ToArray(), highPriority: true, cts.Token);

        // 1. 送出 2 張影像把 Inflight 填滿至 2，並不回任何 ACK
        for (int i = 0; i < 20 && timedOutSeqs.Count < 2; i++)
        {
            capturer.PaintRect(new Rectangle(10 + i * 6, 10, 80, 80), Color.FromArgb(40 + i * 10, 150, 90));
            await Task.Delay(30);
        }
        Assert.Equal(2, timedOutSeqs.Count);
        Assert.Equal(2, host.GetFrameAckDiagnosticsSnapshot().InflightCount);

        // 2. 等待 > 1200ms 觸發 HostSession Timeout1200ms 重置
        Volatile.Write(ref phase, 1);
        for (int i = 0; i < 40 && host.GetFrameAckDiagnosticsSnapshot().InflightResetCount == 0; i++)
        {
            await Task.Delay(50);
        }

        // 等新 Keyframe 送出並收到遲到舊 ACK
        for (int i = 0; i < 25 && postTimeoutSeq == 0; i++)
        {
            capturer.PaintRect(new Rectangle(30, 30, 80, 80), Color.Yellow);
            await Task.Delay(35);
        }
        await Task.Delay(120);

        // 嘗試多畫幾筆，確認不會被遲到舊 ACK 偷開門超過 MaxInflight=2
        for (int i = 0; i < 4; i++)
        {
            capturer.PaintRect(new Rectangle(40 + i * 5, 40, 60, 60), Color.Cyan);
            await Task.Delay(35);
        }
        await Task.Delay(80);

        var snap = host.GetFrameAckDiagnosticsSnapshot();
        _output.WriteLine($"[Matrix C 1200ms Timeout] ResetReason={snap.LastResetReason}, Resets={snap.InflightResetCount}, StaleRejected={snap.StaleAckCount}, PostTimeoutFramesUnacked={postTimeoutUnackedCount}, RTT={snap.LastFrameRttMs:F1}ms");

        Assert.True(snap.InflightResetCount >= 1);
        Assert.Equal("Timeout1200ms", snap.LastResetReason);
        Assert.Equal(2, snap.StaleAckCount);
        Assert.Equal(0, snap.ValidAckCount);
        Assert.Equal(0f, snap.LastFrameRttMs);
        Assert.True(postTimeoutUnackedCount <= 2, $"Timeout 新世代尚未收到新 ACK 前最多僅放行 <=2 張，實際={postTimeoutUnackedCount}");

        // 補上真正的新 Keyframe ACK
        await viewerCh.SendAsync(MessageCodec.FrameAck(postTimeoutSeq), highPriority: true, cts.Token);
        await Task.Delay(80);
        var snapEnd = host.GetFrameAckDiagnosticsSnapshot();
        Assert.True(snapEnd.ValidAckCount >= 1);

        cts.Cancel();
        host.Dispose();
        try { await hostTask; await viewerLoop; } catch { }
    }

    // =========================================================================
    // 矩陣 D — StreamConfig Switch 跨世代防護 (含 Partial Inflight=1 修補驗證)
    // =========================================================================

    [Fact]
    public async Task MatrixD_ConfigSwitch_PartialInflight_UnconditionallyReset_And_OldAckRejected()
    {
        WriteDelayStream? hostDelay = null;
        var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync(
            hostStreamWrapper: s =>
            {
                hostDelay = new WriteDelayStream(s);
                return hostDelay;
            });
        using var _h = hostCh;
        using var _v = viewerCh;
        // 使用 640x360，先因擁塞從 L0 切換為 L1 (cfgVer=2)，扣留 v2 單張影格使 Inflight==1 (<2)，
        // 再切換回 Original (L0, cfgVer=3) 驗證 partial inflight 必遭無條件重置並拒絕遲到 v2 FrameAck。
        var capturer = new SyntheticCapturer(640, 360, Color.DarkBlue);
        var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "Mon1", 1, 0, 0, 640, 360, true, 96) };

        using var host = new HostSession(hostCh, clientHello, "127.0.0.1", new NullHostUi())
        {
            AllowDxgi = false,
            MonitorsOverride = () => monList,
            CapturerOverride = (_, _) => capturer
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var hostTask = Task.Run(host.RunAsync);

        uint latestConfigVer = 0;
        int v1FrameSeen = 0;
        uint oldV2Seq = 0;
        uint switchedV3Seq = 0;

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
                            Volatile.Write(ref latestConfigVer, v);
                            if (v >= 2)
                                hostDelay!.SetDelayMs(0);
                            dec.ConfigureStream(v, sw, sh);
                            await viewerCh.SendAsync(MessageCodec.StreamConfigAck(v, sw, sh), highPriority: true, cts.Token);
                            break;
                        }
                        case HostMsg.VideoFrame:
                        {
                            dec.DecodeFrame(msg.AsSpan(1), out var frameSeq, out _, out _, out _, out _, out _);
                            uint cfg = Volatile.Read(ref latestConfigVer);
                            if (cfg <= 1)
                            {
                                Interlocked.Increment(ref v1FrameSeen);
                                await viewerCh.SendAsync(MessageCodec.FrameAck(frameSeq), highPriority: true, cts.Token);
                            }
                            else if (cfg == 2)
                            {
                                // 收到 L1 (v2) 首幀即扣住其 FrameAck，讓 Host _inflight == 1 (< MaxInflight == 2)
                                Interlocked.CompareExchange(ref oldV2Seq, frameSeq, 0);
                            }
                            else if (cfg >= 3)
                            {
                                Interlocked.CompareExchange(ref switchedV3Seq, frameSeq, 0);
                            }
                            break;
                        }
                    }
                }
                catch { break; }
            }
        });

        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5).U8(75).U8(30).Bool(false).U8((byte)ResolutionMode.Performance).ToArray(), highPriority: true, cts.Token);

        for (int i = 0; i < 40 && Volatile.Read(ref v1FrameSeen) == 0; i++)
            await Task.Delay(25);
        Assert.True(Volatile.Read(ref v1FrameSeen) >= 1);

        // 施加 WriteDelay=300ms 觸發 Performance 模式 hardStall (>=250ms)，約 ~1.5s 降至 L1 (cfgVer=2)
        hostDelay!.SetDelayMs(300);
        for (int i = 0; i < 100 && Volatile.Read(ref latestConfigVer) < 2; i++)
        {
            capturer.PaintRect(new Rectangle((i * 17) % 300, 10, 220, 220), Color.FromArgb(40 + (i * 19) % 200, 100, 220));
            await Task.Delay(50);
        }
        hostDelay.SetDelayMs(0);
        Assert.True(Volatile.Read(ref latestConfigVer) >= 2, "應因傳輸寫入壅塞降至 L1 (cfgVer=2)");

        // 停止製造額外變更，等待 v2 Keyframe (oldV2Seq) 送達，且因未回 ACK 使 Inflight == 1 (<2)
        for (int i = 0; i < 60 && Volatile.Read(ref oldV2Seq) == 0; i++)
            await Task.Delay(30);
        uint heldV2Seq = Volatile.Read(ref oldV2Seq);
        Assert.NotEqual(0u, heldV2Seq);
        await Task.Delay(50);
        Assert.Equal(1, host.GetFrameAckDiagnosticsSnapshot().InflightCount);

        var snapBeforeSwitch = host.GetFrameAckDiagnosticsSnapshot();
        long staleBeforeSwitch = snapBeforeSwitch.StaleAckCount;

        // 於 Inflight==1 (<MaxInflight=2) 狀態下切換回 Original (L0, cfgVer=3)
        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5).U8(75).U8(30).Bool(false).U8((byte)ResolutionMode.Original).ToArray(), highPriority: true, cts.Token);

        for (int i = 0; i < 50 && Volatile.Read(ref switchedV3Seq) == 0; i++)
            await Task.Delay(30);
        uint newV3Seq = Volatile.Read(ref switchedV3Seq);
        Assert.NotEqual(0u, newV3Seq);
        Assert.True(Volatile.Read(ref latestConfigVer) >= 3);

        var snapBeforeStaleAck = host.GetFrameAckDiagnosticsSnapshot();
        long validBeforeStale = snapBeforeStaleAck.ValidAckCount;
        float rttBeforeStale = snapBeforeStaleAck.LastFrameRttMs;

        // 送出舊設定 v2 影格 (oldV2Seq) 的遲到 FrameAck
        await viewerCh.SendAsync(MessageCodec.FrameAck(heldV2Seq), highPriority: true, cts.Token);
        await Task.Delay(80);

        var snapMid = host.GetFrameAckDiagnosticsSnapshot();
        _output.WriteLine($"[Matrix D ConfigSwitch Partial Inflight=1] ResetReason={snapMid.LastResetReason}, StaleDelta={snapMid.StaleAckCount - staleBeforeSwitch}, ValidBefore={validBeforeStale}, ValidAfter={snapMid.ValidAckCount}, Inflight={snapMid.InflightCount}, OldV2Seq={heldV2Seq}, NewV3Seq={newV3Seq}");

        Assert.Equal("ConfigSwitch", snapMid.LastResetReason);
        Assert.Equal(staleBeforeSwitch + 1, snapMid.StaleAckCount);
        Assert.Equal(validBeforeStale, snapMid.ValidAckCount);
        Assert.Equal(rttBeforeStale, snapMid.LastFrameRttMs);
        Assert.Equal(1, snapMid.InflightCount); // newV3Seq 仍在等待 ACK，未被 oldV2Seq 偷放行

        // 補回真正新 Keyframe (newV3Seq) ACK
        await viewerCh.SendAsync(MessageCodec.FrameAck(newV3Seq), highPriority: true, cts.Token);
        await Task.Delay(80);

        var snapEnd = host.GetFrameAckDiagnosticsSnapshot();
        Assert.Equal(validBeforeStale + 1, snapEnd.ValidAckCount);
        Assert.Equal(0, snapEnd.InflightCount);

        cts.Cancel();
        host.Dispose();
        try { await hostTask; await viewerLoop; } catch { }
    }

    // =========================================================================
    // 矩陣 E — 重連 / needRecreate 隔離與 Dispose 清理
    // =========================================================================

    [Fact]
    public async Task MatrixE_ReconnectAndCapturerRecreate_IsolatesOldAcksAndCleansUpOnDispose()
    {
        // E1: Monitor Switch (needRecreate -> CapturerRecreate 重置舊在途幀)
        {
            var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
            using var _h = hostCh;
            using var _v = viewerCh;
            var cap1 = new SyntheticCapturer(320, 180, Color.Navy);
            var cap2 = new SyntheticCapturer(640, 360, Color.Teal);
            var monList = new List<MonitorInfo>
            {
                new(@"\\.\DISPLAY1", "Mon1", 1, 0, 0, 320, 180, true, 96),
                new(@"\\.\DISPLAY2", "Mon2", 2, 320, 0, 640, 360, false, 96)
            };

            using var host = new HostSession(hostCh, clientHello, "127.0.0.1", new NullHostUi())
            {
                AllowDxgi = false,
                MonitorsOverride = () => monList,
                CapturerOverride = (mon, _) => mon?.DeviceName == @"\\.\DISPLAY2" ? cap2 : cap1
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var hostTask = Task.Run(host.RunAsync);

            uint oldMonSeq = 0;
            uint newMonSeq = 0;
            int streamInfoCount = 0;

            var viewerLoop = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var msg = await viewerCh.ReceiveAsync(cts.Token);
                        if ((HostMsg)msg[0] == HostMsg.StreamInfo)
                        {
                            Interlocked.Increment(ref streamInfoCount);
                        }
                        else if ((HostMsg)msg[0] == HostMsg.VideoFrame)
                        {
                            uint fseq = BinaryPrimitives.ReadUInt32LittleEndian(msg.AsSpan(1, 4));
                            if (Volatile.Read(ref streamInfoCount) <= 1 && oldMonSeq == 0)
                                Volatile.Write(ref oldMonSeq, fseq);
                            else if (Volatile.Read(ref streamInfoCount) >= 2 && newMonSeq == 0)
                                Volatile.Write(ref newMonSeq, fseq);
                        }
                    }
                    catch { break; }
                }
            });

            for (int i = 0; i < 40 && Volatile.Read(ref oldMonSeq) == 0; i++) await Task.Delay(25);
            Assert.NotEqual(0u, Volatile.Read(ref oldMonSeq));
            Assert.Equal(1, host.GetFrameAckDiagnosticsSnapshot().InflightCount);

            // 切換螢幕 -> 觸發 needRecreate + CapturerRecreate
            await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.SelectMonitor).Str(@"\\.\DISPLAY2").ToArray(), highPriority: true, cts.Token);
            for (int i = 0; i < 40 && Volatile.Read(ref newMonSeq) == 0; i++) await Task.Delay(30);
            Assert.NotEqual(0u, Volatile.Read(ref newMonSeq));

            // 送回舊螢幕幀 seq 的遲到 ACK -> 應被拒絕為 StaleFrameAck
            await viewerCh.SendAsync(MessageCodec.FrameAck(oldMonSeq), highPriority: true, cts.Token);
            await Task.Delay(80);
            var snapMid = host.GetFrameAckDiagnosticsSnapshot();
            Assert.Equal("CapturerRecreate", snapMid.LastResetReason);
            Assert.Equal(1, snapMid.StaleAckCount);
            Assert.Equal(0, snapMid.ValidAckCount);
            Assert.Equal(1, snapMid.InflightCount);

            // 送回新螢幕首幀 ACK -> 正常釋放
            await viewerCh.SendAsync(MessageCodec.FrameAck(newMonSeq), highPriority: true, cts.Token);
            await Task.Delay(80);
            Assert.Equal(0, host.GetFrameAckDiagnosticsSnapshot().InflightCount);

            cts.Cancel();
            host.Dispose();
            try { await hostTask; await viewerLoop; } catch { }
        }

        // E2: 舊會話 Dispose 清除未確認在途狀態 + 新會話重連完全隔離
        {
            var (hCh1, cHello1, vCh1, _) = await CreateTcpSecurePairAsync();
            using var _v1 = vCh1;
            var host1 = CreateSequenceSession(hCh1, cHello1);
            host1.RegisterFrameSentForTest(1);
            host1.RegisterFrameSentForTest(2);
            Assert.Equal(2, host1.GetFrameAckDiagnosticsSnapshot().InflightCount);

            host1.Dispose();
            var snapAfterDispose = host1.GetFrameAckDiagnosticsSnapshot();
            Assert.Equal(0, snapAfterDispose.InflightCount);
            Assert.Empty(snapAfterDispose.InflightSeqs);
            Assert.Equal("SessionEnded", snapAfterDispose.LastResetReason);

            var (hCh2, cHello2, vCh2, _) = await CreateTcpSecurePairAsync();
            using var _h2 = hCh2;
            using var _v2 = vCh2;
            using var host2 = CreateSequenceSession(hCh2, cHello2);
            Assert.Equal(1UL, host2.GetFrameAckDiagnosticsSnapshot().Generation);
            Assert.Equal(0, host2.GetFrameAckDiagnosticsSnapshot().InflightCount);

            // 若來自前次連線殘餘的 seq=2 ACK 被送到新連線（此時僅發出 seq=1），必遭 UnknownFrameAck 拒絕
            host2.RegisterFrameSentForTest(1);
            var orphanAck = host2.InjectFrameAckForTest(MessageCodec.FrameAck(2));
            Assert.False(orphanAck.Accepted);
            Assert.Equal(FrameAckRejectReason.UnknownFrameAck, orphanAck.RejectReason);
            Assert.Equal(1, orphanAck.InflightCountAfter);
        }
    }

    // =========================================================================
    // 矩陣 F — uint.MaxValue -> 0 Wraparound 邊界保護
    // =========================================================================

    [Fact]
    public async Task MatrixF_UintMaxWraparound_ExactMembershipModularRttAndWireRoundtrip()
    {
        var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
        using var _h = hostCh;
        using var _v = viewerCh;
        using var host = CreateSequenceSession(hostCh, clientHello);

        long now = host.ClockMsForTest;
        // 1. 跨 uint.MaxValue -> 0 亂序 ACK：較新的 seq=0 先抵達，較舊的 seq=uint.MaxValue 後抵達
        host.RegisterFrameSentForTest(uint.MaxValue - 1, now - 110);
        host.RegisterFrameSentForTest(uint.MaxValue, now - 85);
        host.RegisterFrameSentForTest(0u, now - 25);

        Assert.True(host.InjectFrameAckForTest(MessageCodec.FrameAck(uint.MaxValue - 1)).Accepted);

        var ackZero = host.InjectFrameAckForTest(MessageCodec.FrameAck(0u));
        Assert.True(ackZero.Accepted);
        var midSnap = host.GetFrameAckDiagnosticsSnapshot();
        float rttAtZero = midSnap.LastFrameRttMs;
        Assert.Equal(0u, midSnap.HighestAckedSeqInGeneration);

        // uint.MaxValue 與 0 相比，modular 差值 unchecked((int)(uint.MaxValue - 0)) == -1 <= 0，絕不倒退覆寫 RTT
        var ackMax = host.InjectFrameAckForTest(MessageCodec.FrameAck(uint.MaxValue));
        Assert.True(ackMax.Accepted);
        Assert.Equal(0, ackMax.InflightCountAfter);
        Assert.Equal(rttAtZero, host.GetFrameAckDiagnosticsSnapshot().LastFrameRttMs);

        // 2. 重置跨越 uint.MaxValue -> 0：seq=uint.MaxValue 與 0 失效後，seq=1 合法、舊 ACK 被 Stale 拒絕
        host.RegisterFrameSentForTest(uint.MaxValue, host.ClockMsForTest);
        host.RegisterFrameSentForTest(0u, host.ClockMsForTest);
        host.ResetInflightForTest("RequestKeyframe");
        host.RegisterFrameSentForTest(1u, host.ClockMsForTest - 30);

        var staleMax = host.InjectFrameAckForTest(MessageCodec.FrameAck(uint.MaxValue));
        var staleZero = host.InjectFrameAckForTest(MessageCodec.FrameAck(0u));
        Assert.False(staleMax.Accepted);
        Assert.Equal(FrameAckRejectReason.StaleFrameAck, staleMax.RejectReason);
        Assert.False(staleZero.Accepted);
        Assert.Equal(FrameAckRejectReason.StaleFrameAck, staleZero.RejectReason);
        Assert.Equal(1, host.GetFrameAckDiagnosticsSnapshot().InflightCount);

        var ackOne = host.InjectFrameAckForTest(MessageCodec.FrameAck(1u));
        Assert.True(ackOne.Accepted);
        Assert.Equal(0, ackOne.InflightCountAfter);

        // 3. 實測 FrameEncoder.Serialize + FrameDecoder.DecodeFrame 對 uint.MaxValue / 0 / 1 序號往返一致
        using var dec = new FrameDecoder();
        dec.ConfigureStream(1, 160, 90);
        uint[] wireSeqs = [uint.MaxValue, 0u, 1u];
        foreach (uint ws in wireSeqs)
        {
            byte[] pkt = BuildSyntheticFullFrame(160, 90, Color.Indigo, seq: ws, ver: ws + 10, cfgVer: 1);
            dec.DecodeFrame(pkt.AsSpan(1), out uint decodedSeq, out _, out _, out _, out _, out var st);
            Assert.Equal(FrameDecodeStatus.Applied, st);
            Assert.Equal(ws, decodedSeq);
            byte[] ackBytes = MessageCodec.FrameAck(decodedSeq);
            Assert.Equal(ws, BinaryPrimitives.ReadUInt32LittleEndian(ackBytes.AsSpan(1)));
        }
    }

    // =========================================================================
    // 矩陣 G — 執行緒安全與併發競爭測試
    // =========================================================================

    [Fact]
    public async Task MatrixG_ConcurrentRegisterAckResetAndSnapshot_MaintainsAllInvariants()
    {
        var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
        using var _h = hostCh;
        using var _v = viewerCh;
        using var host = CreateSequenceSession(hostCh, clientHello);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        var errors = new ConcurrentQueue<Exception>();
        int latestIssuedSeq = 0;

        var producer = Task.Run(() =>
        {
            try
            {
                uint seq = 0;
                while (!cts.IsCancellationRequested)
                {
                    seq = unchecked(seq + 1);
                    host.RegisterFrameSentForTest(seq);
                    Volatile.Write(ref latestIssuedSeq, unchecked((int)seq));
                    if ((seq & 0x3F) == 0) Thread.Yield();
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });

        var acker = Task.Run(() =>
        {
            try
            {
                int iter = 0;
                while (!cts.IsCancellationRequested)
                {
                    uint cur = unchecked((uint)Volatile.Read(ref latestIssuedSeq));
                    if (cur > 0)
                    {
                        uint target = (iter & 7) switch
                        {
                            0 => cur,
                            1 => unchecked(cur - 1),
                            2 => cur, // 重複/已知測試
                            3 => 0xDEAD0001u, // Unknown
                            _ => cur
                        };
                        if ((iter % 29) == 0)
                            host.InjectFrameAckForTest(MessageCodec.LegacyFrameAck());
                        else if ((iter % 31) == 0)
                            host.InjectFrameAckForTest(new byte[] { (byte)ViewerMsg.FrameAck, 1, 2 });
                        else
                            host.InjectFrameAckForTest(MessageCodec.FrameAck(target));
                    }
                    iter++;
                    if ((iter & 0x3F) == 0) Thread.Yield();
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });

        var resetter = Task.Run(() =>
        {
            try
            {
                int i = 0;
                while (!cts.IsCancellationRequested)
                {
                    string reason = (i++ % 3) switch
                    {
                        0 => "RequestKeyframe",
                        1 => "ConfigSwitch",
                        _ => "Timeout1200ms"
                    };
                    host.ResetInflightForTest(reason);
                    Thread.Sleep(4);
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });

        var observer = Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var snap = host.GetFrameAckDiagnosticsSnapshot();
                    Assert.True(snap.InflightCount >= 0);
                    Assert.Equal(snap.InflightSeqs.Length, snap.InflightCount);
                    Assert.True(snap.Generation >= 1);
                    Assert.False(float.IsNaN(snap.LastFrameRttMs) || snap.LastFrameRttMs < 0f);
                    Thread.Yield();
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });

        await Task.WhenAll(producer, acker, resetter, observer);
        Assert.Empty(errors);

        host.ResetInflightForTest("FinalCleanup");
        var finalSnap = host.GetFrameAckDiagnosticsSnapshot();
        Assert.Equal(0, finalSnap.InflightCount);
        Assert.Empty(finalSnap.InflightSeqs);
        Assert.True(finalSnap.ValidAckCount >= 0);
        Assert.True(finalSnap.InvalidLengthAckCount >= 1);
        Assert.True(finalSnap.LegacyInSequenceAckCount >= 1);
        _output.WriteLine($"[Matrix G Concurrency] Gen={finalSnap.Generation}, Valid={finalSnap.ValidAckCount}, Stale={finalSnap.StaleAckCount}, Dup={finalSnap.DuplicateAckCount}, Unknown={finalSnap.UnknownAckCount}, LegacyInSeq={finalSnap.LegacyInSequenceAckCount}, InvalidLen={finalSnap.InvalidLengthAckCount}, Resets={finalSnap.InflightResetCount}");
    }

    // =========================================================================
    // 7.3 相容性測試矩陣 (全部 7 種組合完整實測)
    // =========================================================================

    [Fact]
    public async Task Matrix73_CapabilityNegotiation_And_ProtocolErrors_AllSevenScenarios()
    {
        // 場景 1：New Viewer + New Host -> 握手協商啟用 FrameSeqAck (5 bytes)
        {
            var (hostCh, clientHello, viewerCh, serverHello) = await CreateTcpSecurePairAsync(
                ProtocolCapabilities.FrameSeqAck,
                ch => ch.Capabilities & ProtocolCapabilities.FrameSeqAck);
            using var _h = hostCh;
            using var _v = viewerCh;
            using var host = CreateSequenceSession(hostCh, clientHello);

            Assert.Equal(FrameAckMode.Sequence, host.AckMode);
            Assert.Equal(ProtocolCapabilities.FrameSeqAck, serverHello.Capabilities);

            host.RegisterFrameSentForTest(7, host.ClockMsForTest - 20);
            await viewerCh.SendAsync(MessageCodec.FrameAck(7), highPriority: true);
            byte[] ackWire = await hostCh.ReceiveAsync();
            var res = host.InjectFrameAckForTest(ackWire);
            Assert.True(res.Accepted);
            Assert.Equal(7u, res.FrameSeq);
            Assert.Equal(0, res.InflightCountAfter);
        }

        // 場景 2：New Host（Sequence 模式）收到誤送之舊式 1-byte ACK -> 拒絕 LegacyAckInSequenceMode，嚴禁降級或誤釋放
        {
            var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
            using var _h = hostCh;
            using var _v = viewerCh;
            using var host = CreateSequenceSession(hostCh, clientHello);

            Assert.Equal(FrameAckMode.Sequence, host.AckMode);
            host.RegisterFrameSentForTest(42, host.ClockMsForTest - 50);

            var rLegacyInSeq = host.InjectFrameAckForTest(MessageCodec.LegacyFrameAck());
            Assert.False(rLegacyInSeq.Accepted);
            Assert.Equal(FrameAckRejectReason.LegacyAckInSequenceMode, rLegacyInSeq.RejectReason);
            Assert.Equal(1, rLegacyInSeq.InflightCountAfter);

            var snap = host.GetFrameAckDiagnosticsSnapshot();
            Assert.Equal(1, snap.LegacyInSequenceAckCount);
            Assert.Equal(0, snap.ValidAckCount);
            Assert.Equal(0f, snap.LastFrameRttMs);
        }

        // 場景 3：Old Host (ServerHello 不含 FrameSeqAck 能力) + New ViewerSession -> Viewer 實機連線後降級回傳 1-byte LegacyFrameAck
        {
            await AssertViewerNegotiatesCapabilityOverTcpAsync(
                serverAdvertisesFrameSeqAck: false,
                expectedSupportsFrameSeqAck: false,
                expectedWireLength: 1);
        }

        // 同時對照：New Host (ServerHello 含 FrameSeqAck 能力) + New ViewerSession -> Viewer 實機連線後回傳 5-byte FrameAck(seq)
        {
            await AssertViewerNegotiatesCapabilityOverTcpAsync(
                serverAdvertisesFrameSeqAck: true,
                expectedSupportsFrameSeqAck: true,
                expectedWireLength: 5);
        }

        // 場景 4 & 5：Sequence 模式收到截斷 (1, 2, 3B payload) 或過長 (5, 8B payload) FrameAck -> 安全記錄並拒收，不拋例外
        {
            var (hostCh, clientHello, viewerCh, _) = await CreateTcpSecurePairAsync();
            using var _h = hostCh;
            using var _v = viewerCh;
            using var host = CreateSequenceSession(hostCh, clientHello);

            host.RegisterFrameSentForTest(50, host.ClockMsForTest - 20);

            int[] badPayloadLens = [1, 2, 3, 5, 8];
            foreach (int len in badPayloadLens)
            {
                var badMsg = new byte[1 + len];
                badMsg[0] = (byte)ViewerMsg.FrameAck;
                var badRes = host.InjectFrameAckForTest(badMsg);
                Assert.False(badRes.Accepted);
                Assert.Equal(FrameAckRejectReason.InvalidFrameAckLength, badRes.RejectReason);
                Assert.Equal(1, badRes.InflightCountAfter);
            }

            var snap = host.GetFrameAckDiagnosticsSnapshot();
            Assert.Equal(badPayloadLens.Length, snap.InvalidLengthAckCount);
            Assert.Equal(0, snap.ValidAckCount);
            Assert.Equal(0f, snap.LastFrameRttMs);
        }

        // 場景 6 & 7：Legacy 模式 (ClientHello.Capabilities == None)
        //   - 場景 6：Legacy 模式卻收到 5-byte FrameSeq ACK -> 嚴禁讀取或混入，回報 InvalidFrameAckLength
        //   - 場景 7：Legacy 模式收到正規 1-byte FrameAck -> 於獨立 Legacy FIFO 正確完成往返
        {
            var (hostCh, clientHello, viewerCh, serverHello) = await CreateTcpSecurePairAsync(
                viewerCaps: ProtocolCapabilities.None,
                serverCapNegotiator: ch => ch.Capabilities & ProtocolCapabilities.FrameSeqAck);
            using var _h = hostCh;
            using var _v = viewerCh;
            using var host = CreateSequenceSession(hostCh, clientHello);

            Assert.Equal(ProtocolCapabilities.None, clientHello.Capabilities);
            Assert.Equal(ProtocolCapabilities.None, serverHello.Capabilities);
            Assert.Equal(FrameAckMode.Legacy, host.AckMode);
            Assert.False(host.SupportsFrameSeqAck);

            host.RegisterFrameSentForTest(1, host.ClockMsForTest - 35);

            // 場景 6：Legacy 模式收到 5-byte ACK (4B payload)
            var badSeqAckInLegacy = host.InjectFrameAckForTest(MessageCodec.FrameAck(1));
            Assert.False(badSeqAckInLegacy.Accepted);
            Assert.Equal(FrameAckRejectReason.InvalidFrameAckLength, badSeqAckInLegacy.RejectReason);
            Assert.Equal(1, badSeqAckInLegacy.InflightCountAfter);

            // 場景 7：Legacy 模式收到正確 1-byte ACK
            var okLegacyAck = host.InjectFrameAckForTest(MessageCodec.LegacyFrameAck());
            Assert.True(okLegacyAck.Accepted);
            Assert.Null(okLegacyAck.FrameSeq);
            Assert.Equal(0, okLegacyAck.InflightCountAfter);

            var snap = host.GetFrameAckDiagnosticsSnapshot();
            Assert.Equal(1, snap.InvalidLengthAckCount);
            Assert.Equal(1, snap.ValidAckCount);
            Assert.InRange(snap.LastFrameRttMs, 20f, 150f);
        }
    }

    private static async Task AssertViewerNegotiatesCapabilityOverTcpAsync(
        bool serverAdvertisesFrameSeqAck,
        bool expectedSupportsFrameSeqAck,
        int expectedWireLength)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"rd_compat_vs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<byte[]>? hostSideTask = null;
        try
        {
            hostSideTask = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                SecureChannel.TuneSocket(client.Client);

                // 若為 Old Host 模擬，刻意構造不包含 FrameSeqAck 之 ServerHello
                var caps = serverAdvertisesFrameSeqAck ? ProtocolCapabilities.FrameSeqAck : ProtocolCapabilities.None;
                var (hostCh, clientHello) = await SecureChannel.ServerHandshakeAsync(
                    client.GetStream(),
                    ch => new ServerHello(
                        Protocol.Version,
                        "990000101",
                        "CompatHost",
                        Array.Empty<byte>(),
                        1,
                        HasPassword: false,
                        AllowInteractive: false,
                        Busy: false,
                        Capabilities: caps),
                    cts.Token);
                using var _hc = hostCh;
                Assert.Equal(ProtocolCapabilities.FrameSeqAck, clientHello.Capabilities);

                // 收取 DeviceIdentity 驗證並回應 OK
                var auth = await hostCh.ReceiveAsync(cts.Token);
                Assert.Equal((byte)AuthMethod.DeviceIdentity, auth[0]);
                await hostCh.SendAsync(new PacketWriter(0x00).Str("OK").ToArray(), cts.Token);

                // 送出 StreamConfig 與 seq=77 FullFrame
                await hostCh.SendAsync(MessageCodec.StreamConfig(1, 160, 90, 160, 90, ResolutionLevel.Level0, ResolutionMode.Auto, "Init"), highPriority: true, cts.Token);
                await hostCh.SendAsync(BuildSyntheticFullFrame(160, 90, Color.SeaGreen, seq: 77, ver: 1, cfgVer: 1), cts.Token);

                while (!cts.IsCancellationRequested)
                {
                    var msg = await hostCh.ReceiveAsync(cts.Token);
                    if (msg.Length > 0 && msg[0] == (byte)ViewerMsg.FrameAck)
                    {
                        return msg;
                    }
                }
                throw new InvalidOperationException("未收到 ViewerMsg.FrameAck");
            }, cts.Token);

            var identity = DeviceIdentity.LoadOrCreate(tempRoot, "990000202", "CompatViewer");
            var cfg = new ViewerConfig { ViewerId = "990000202" };
            using var viewerSession = new ViewerSession($"127.0.0.1:{port}", cfg, new LanDiscovery("990000202"), null, identity);
            await viewerSession.ConnectAsync(null, cts.Token, forceInteractive: false);

            byte[] ackWire = await hostSideTask;
            Assert.Equal(expectedSupportsFrameSeqAck, viewerSession.SupportsFrameSeqAck);
            Assert.Equal(expectedWireLength, ackWire.Length);
            Assert.Equal((byte)ViewerMsg.FrameAck, ackWire[0]);
            if (expectedSupportsFrameSeqAck)
            {
                Assert.Equal(77u, BinaryPrimitives.ReadUInt32LittleEndian(ackWire.AsSpan(1)));
            }
        }
        finally
        {
            cts.Cancel();
            if (hostSideTask != null)
            {
                try { await hostSideTask; } catch { }
            }
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    // =========================================================================
    // 矩陣 H — WAN UDP P2P (ReliableUdpStream) 實機迴圈 FrameSeq ACK 與 TypeAck 正交隔離
    // =========================================================================

    [Fact]
    public async Task MatrixH_ReliableUdpAndHostSession_FrameSeqAck_TypeAckOrthogonal_And_StaleRejected()
    {
        using var udpHostSock = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var udpViewerSock = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var hostEp = (IPEndPoint)udpHostSock.Client.LocalEndPoint!;
        var viewerEp = (IPEndPoint)udpViewerSock.Client.LocalEndPoint!;

        using var hostUdp = new ReliableUdpStream(udpHostSock.Client, viewerEp);
        using var viewerUdp = new ReliableUdpStream(udpViewerSock.Client, hostEp);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var hostChTask = SecureChannel.ServerHandshakeAsync(
            hostUdp,
            ch => new ServerHello(Protocol.Version, "990000301", "UdpSeqHost", Array.Empty<byte>(), 1, false, false, false, ch.Capabilities & ProtocolCapabilities.FrameSeqAck),
            cts.Token);
        var viewerChTask = SecureChannel.ClientHandshakeAsync(viewerUdp, "990000302", "UdpSeqViewer", cts.Token);
        var (hostCh, clientHello) = await hostChTask;
        var (viewerCh, serverHello) = await viewerChTask;
        using var _hc = hostCh;
        using var _vc = viewerCh;

        Assert.Equal(ProtocolCapabilities.FrameSeqAck, clientHello.Capabilities);
        Assert.Equal(ProtocolCapabilities.FrameSeqAck, serverHello.Capabilities);

        var capturer = new SyntheticCapturer(160, 90, Color.DarkCyan);
        var monList = new List<MonitorInfo> { new(@"\\.\DISPLAY1", "UdpMon", 1, 0, 0, 160, 90, true, 96) };
        using var host = new HostSession(hostCh, clientHello, viewerEp.ToString(), new NullHostUi())
        {
            AllowDxgi = false,
            MonitorsOverride = () => monList,
            CapturerOverride = (_, _) => capturer
        };
        Assert.Equal(FrameAckMode.Sequence, host.AckMode);

        var hostTask = Task.Run(host.RunAsync);
        var preNackSeqs = new ConcurrentQueue<uint>();
        int phase = 0; // 0=收前2幀不回ACK(傳輸層TypeAck仍自動運作!), 1=NACK後送遲到舊seq, 2=收真新ACK
        uint postNackKfSeq = 0;
        long allowNewAck = 0;

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
                            dec.DecodeFrame(msg.AsSpan(1), out var frameSeq, out _, out _, out _, out _, out _);
                            int p = Volatile.Read(ref phase);
                            if (p == 0)
                            {
                                preNackSeqs.Enqueue(frameSeq);
                            }
                            else if (p == 1)
                            {
                                Interlocked.CompareExchange(ref postNackKfSeq, frameSeq, 0);
                                Interlocked.Exchange(ref phase, 2);
                                await Task.Delay(10, cts.Token);
                                while (preNackSeqs.TryDequeue(out uint stale))
                                {
                                    await viewerCh.SendAsync(MessageCodec.FrameAck(stale), highPriority: true, cts.Token);
                                }
                            }
                            else if (p == 2 && Interlocked.Read(ref allowNewAck) == 1)
                            {
                                await viewerCh.SendAsync(MessageCodec.FrameAck(frameSeq), highPriority: true, cts.Token);
                            }
                            break;
                        }
                    }
                }
                catch { break; }
            }
        });

        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5).U8(75).U8(30).Bool(false).U8((byte)ResolutionMode.Auto).ToArray(), highPriority: true, cts.Token);

        for (int i = 0; i < 20 && preNackSeqs.Count < 2; i++)
        {
            capturer.PaintRect(new Rectangle(10 + i * 4, 10, 40, 40), Color.FromArgb(40 + i * 10, 180, 120));
            await Task.Delay(30);
        }
        Assert.Equal(2, preNackSeqs.Count);
        Assert.True(hostUdp.TotalAcksReceived >= 2, "ReliableUdpStream TypeAck 應已於傳輸層獨立確認封包送達，即使應用層刻意扣留 FrameAck");

        Interlocked.Exchange(ref phase, 1);
        await viewerCh.SendAsync(new PacketWriter((byte)ViewerMsg.RequestKeyframe, 1).ToArray(), highPriority: true, cts.Token);

        for (int i = 0; i < 25 && Volatile.Read(ref postNackKfSeq) == 0; i++)
        {
            capturer.PaintRect(new Rectangle(20, 20, 50, 50), Color.Orange);
            await Task.Delay(25);
        }
        Assert.NotEqual(0u, Volatile.Read(ref postNackKfSeq));
        await Task.Delay(120);

        var snapMid = host.GetFrameAckDiagnosticsSnapshot();
        Assert.Equal(2, snapMid.StaleAckCount);
        Assert.Equal(0, snapMid.ValidAckCount);
        Assert.Equal("RequestKeyframe", snapMid.LastResetReason);

        Interlocked.Exchange(ref allowNewAck, 1);
        await viewerCh.SendAsync(MessageCodec.FrameAck(Volatile.Read(ref postNackKfSeq)), highPriority: true, cts.Token);
        await Task.Delay(100);
        Assert.True(host.GetFrameAckDiagnosticsSnapshot().ValidAckCount >= 1);

        cts.Cancel();
        host.Dispose();
        try { await hostTask; await viewerLoop; } catch { }
    }
}
