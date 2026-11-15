using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RemoteDesk.Common;
using RemoteDesk.Host;
using RemoteDesk.Net;
using RemoteDesk.Viewer;
using Xunit;
using Xunit.Abstractions;

namespace RemoteDesk.Tests;

public sealed class Phase2VideoTransportVerificationTests
{
    private readonly ITestOutputHelper _output;

    public Phase2VideoTransportVerificationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// 精準 UDP 頻寬塑形 / 單向延遲 / 確定性掉包中繼器（僅供本機受控模擬，不可宣稱等同真實跨 ISP WAN）。
    /// </summary>
    private sealed class ShapedUdpRelay : IDisposable
    {
        private readonly Socket _sockA;
        private readonly Socket _sockB;
        private readonly IPEndPoint _epA;
        private readonly IPEndPoint _epB;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentQueue<(long ArrivedTs, byte[] Data)> _qA2B = new();
        private readonly ConcurrentQueue<(long ArrivedTs, byte[] Data)> _qB2A = new();
        private readonly Task _recvATask;
        private readonly Task _recvBTask;
        private readonly Thread _fwdThread;

        private int _qA2BBytes;
        private long _a2bDataCount;
        private long _a2bDroppedCount;

        private double _targetMbps; // <= 0 代表不限速
        public double TargetMbps { get => Volatile.Read(ref _targetMbps); set => Volatile.Write(ref _targetMbps, value); }
        public volatile int OneWayDelayMs;
        public volatile int DropDataEveryN; // e.g. 100 = 1%, 33 = ~3%, 0 = 0%
        public int RouterQueueCapBytes = 1024 * 1024; // 大於 ReliableUdpStream MaxWindowSize (307KB)，確保 0% 掉包測試不會被中繼佇列尾端誤丟

        public IPEndPoint EndPointForA { get; }
        public IPEndPoint EndPointForB { get; }
        public long DroppedDataPackets => Interlocked.Read(ref _a2bDroppedCount);

        public ShapedUdpRelay(IPEndPoint epA, IPEndPoint epB, double targetMbps = 0, int oneWayDelayMs = 0, int dropDataEveryN = 0)
        {
            _epA = epA;
            _epB = epB;
            TargetMbps = targetMbps;
            OneWayDelayMs = oneWayDelayMs;
            DropDataEveryN = dropDataEveryN;

            _sockA = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _sockA.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _sockB = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _sockB.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            _sockA.ReceiveBufferSize = 4 * 1024 * 1024;
            _sockB.ReceiveBufferSize = 4 * 1024 * 1024;
            _sockA.SendBufferSize = 4 * 1024 * 1024;
            _sockB.SendBufferSize = 4 * 1024 * 1024;

            EndPointForA = (IPEndPoint)_sockA.LocalEndPoint!;
            EndPointForB = (IPEndPoint)_sockB.LocalEndPoint!;

            _recvATask = Task.Run(() => ReceiveSideAsync(_sockA, _qA2B, isA2B: true));
            _recvBTask = Task.Run(() => ReceiveSideAsync(_sockB, _qB2A, isA2B: false));

            _fwdThread = new Thread(ForwardLoop)
            {
                IsBackground = true,
                Name = "ShapedUdpRelayForwarder"
            };
            _fwdThread.Start();
        }

        private async Task ReceiveSideAsync(Socket sock, ConcurrentQueue<(long, byte[])> q, bool isA2B)
        {
            byte[] buf = new byte[4096];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var res = await sock.ReceiveFromAsync(buf, SocketFlags.None, remote, _cts.Token).ConfigureAwait(false);
                    if (res.ReceivedBytes <= 0) continue;

                    if (isA2B && res.ReceivedBytes >= 5 && buf[0] == 0x52 && buf[1] == 0x44 && buf[2] == 0x53 && buf[3] == 0x4B && buf[4] == ReliableUdpStream.TypeData)
                    {
                        int dropN = DropDataEveryN;
                        long idx = Interlocked.Increment(ref _a2bDataCount);
                        if (dropN > 0 && (idx % dropN) == 0)
                        {
                            Interlocked.Increment(ref _a2bDroppedCount);
                            continue;
                        }

                        if (Volatile.Read(ref _qA2BBytes) + res.ReceivedBytes > RouterQueueCapBytes)
                        {
                            Interlocked.Increment(ref _a2bDroppedCount);
                            continue;
                        }

                        Interlocked.Add(ref _qA2BBytes, res.ReceivedBytes);
                    }

                    byte[] copy = new byte[res.ReceivedBytes];
                    Buffer.BlockCopy(buf, 0, copy, 0, res.ReceivedBytes);
                    q.Enqueue((Stopwatch.GetTimestamp(), copy));
                }
                catch { break; }
            }
        }

        private void ForwardLoop()
        {
            long lastRefillTs = Stopwatch.GetTimestamp();
            double tokensBytes = 24_576.0;

            while (!_cts.IsCancellationRequested)
            {
                bool didWork = false;
                double mbps = TargetMbps;
                double rateBps = mbps > 0 ? mbps * 125_000.0 : 0.0;
                double maxTokens = rateBps > 0 ? Math.Max(8_192.0, rateBps * 0.015) : double.MaxValue;
                int oneWayMs = OneWayDelayMs;

                long nowTs = Stopwatch.GetTimestamp();
                if (rateBps > 0)
                {
                    double dtSec = Math.Clamp(Stopwatch.GetElapsedTime(lastRefillTs, nowTs).TotalSeconds, 0.0, 0.25);
                    tokensBytes = Math.Min(maxTokens, tokensBytes + dtSec * rateBps);
                }
                lastRefillTs = nowTs;

                while (_qA2B.TryPeek(out var itemA))
                {
                    double ageMs = Stopwatch.GetElapsedTime(itemA.ArrivedTs, nowTs).TotalMilliseconds;
                    if (oneWayMs > 0 && ageMs < oneWayMs) break;

                    int wireLen = itemA.Data.Length + ReliableUdpStream.UdpIpOverheadBytes;
                    if (rateBps > 0 && tokensBytes < wireLen) break;

                    if (_qA2B.TryDequeue(out itemA))
                    {
                        if (itemA.Data.Length >= 5 && itemA.Data[4] == ReliableUdpStream.TypeData)
                        {
                            Interlocked.Add(ref _qA2BBytes, -itemA.Data.Length);
                        }
                        if (rateBps > 0) tokensBytes -= wireLen;
                        try { _sockB.SendTo(itemA.Data, _epB); } catch { }
                        didWork = true;
                    }
                }

                while (_qB2A.TryPeek(out var itemB))
                {
                    double ageMs = Stopwatch.GetElapsedTime(itemB.ArrivedTs, nowTs).TotalMilliseconds;
                    if (oneWayMs > 0 && ageMs < oneWayMs) break;

                    if (_qB2A.TryDequeue(out itemB))
                    {
                        try { _sockA.SendTo(itemB.Data, _epA); } catch { }
                        didWork = true;
                    }
                }

                if (!didWork)
                {
                    Thread.SpinWait(50);
                    if (_qA2B.IsEmpty && _qB2A.IsEmpty)
                    {
                        Thread.Sleep(1);
                    }
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _sockA.Dispose(); } catch { }
            try { _sockB.Dispose(); } catch { }
            try { _fwdThread.Join(300); } catch { }
            try { Task.WaitAll(new[] { _recvATask, _recvBTask }, 300); } catch { }
            _cts.Dispose();
        }
    }

    private sealed record UdpScenarioMetrics(
        string Scenario,
        double TargetMbps,
        string LossDesc,
        double ElapsedMs,
        double GoodputMbps,
        double WireMbps,
        double PacingWaitMs,
        float MaxSemWaitMs,
        double SockWriteMs,
        double AvgSockWritePerFrameMs,
        double RttMs,
        double EndPaceBytesPerSec,
        double EndTokenBucketBytes,
        long Retransmissions,
        long RtoTimeouts);

    private static (ReliableUdpStream A, ReliableUdpStream B, ShapedUdpRelay Relay) CreateShapedPair(
        double targetMbps, int oneWayDelayMs, int dropDataEveryN)
    {
        var sockA = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sockA.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var sockB = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sockB.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var epA = (IPEndPoint)sockA.LocalEndPoint!;
        var epB = (IPEndPoint)sockB.LocalEndPoint!;
        var relay = new ShapedUdpRelay(epA, epB, targetMbps, oneWayDelayMs, dropDataEveryN);

        var streamA = new ReliableUdpStream(sockA, relay.EndPointForA, ownsSocket: true);
        var streamB = new ReliableUdpStream(sockB, relay.EndPointForB, ownsSocket: true);
        return (streamA, streamB, relay);
    }

    private static async Task<UdpScenarioMetrics> RunBurstVideoTransferAsync(
        string scenario,
        ReliableUdpStream streamA,
        ReliableUdpStream streamB,
        double targetMbps,
        string lossDesc,
        int frameCount,
        int frameBytes,
        CancellationToken ct)
    {
        long totalExpectedBytes = (long)frameCount * frameBytes;
        long totalReceived = 0;

        long basePayloadAcked = streamA.TotalPayloadBytesAcked;
        long baseWireSent = streamA.TotalWireBytesSent;
        double basePacingWait = streamA.TotalPacingWaitMs;
        double baseSockWrite = streamA.TotalSocketWriteDurationMs;
        long baseRetrans = streamA.TotalRetransmissions;
        long baseRto = streamA.TotalRtoTimeouts;
        _ = streamA.ConsumeWaitWindowSnapshot();

        var recvTask = Task.Run(async () =>
        {
            byte[] buf = new byte[64 * 1024];
            while (Volatile.Read(ref totalReceived) < totalExpectedBytes && !ct.IsCancellationRequested)
            {
                int remaining = (int)Math.Min(buf.Length, totalExpectedBytes - Volatile.Read(ref totalReceived));
                if (remaining <= 0) break;
                int n = await streamB.ReadAsync(buf.AsMemory(0, remaining), ct).ConfigureAwait(false);
                if (n <= 0) break;
                Interlocked.Add(ref totalReceived, n);
            }
        }, ct);

        byte[] frame = new byte[frameBytes];
        RandomNumberGenerator.Fill(frame);

        var sw = Stopwatch.StartNew();
        float maxQueueWait = 0f;
        for (int i = 0; i < frameCount; i++)
        {
            await streamA.WriteAsync(frame, ct).ConfigureAwait(false);
            var qSnap = streamA.ConsumeWaitWindowSnapshot();
            maxQueueWait = Math.Max(maxQueueWait, qSnap.EffectiveWaitMs);
            Assert.True(streamA.CurrentTokenBucketBytes >= 0.0 && !double.IsNaN(streamA.CurrentTokenBucketBytes),
                "TokenBucket 不得為負值或 NaN");
        }
        await streamA.FlushAsync(ct).ConfigureAwait(false);
        await recvTask.ConfigureAwait(false);
        sw.Stop();

        Assert.Equal(totalExpectedBytes, Volatile.Read(ref totalReceived));
        Assert.Equal(0, streamA.CurrentInflightPackets);

        var endQSnap = streamA.ConsumeWaitWindowSnapshot();
        maxQueueWait = Math.Max(maxQueueWait, endQSnap.EffectiveWaitMs);

        double sec = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        double dPayloadAcked = streamA.TotalPayloadBytesAcked - basePayloadAcked;
        double dWireSent = streamA.TotalWireBytesSent - baseWireSent;
        double goodputMbps = (dPayloadAcked * 8.0) / sec / 1_000_000.0;
        double wireMbps = (dWireSent * 8.0) / sec / 1_000_000.0;

        double pacingDeltaMs = streamA.TotalPacingWaitMs - basePacingWait;
        double sockWrDeltaMs = streamA.TotalSocketWriteDurationMs - baseSockWrite;

        return new UdpScenarioMetrics(
            Scenario: scenario,
            TargetMbps: targetMbps,
            LossDesc: lossDesc,
            ElapsedMs: sw.Elapsed.TotalMilliseconds,
            GoodputMbps: goodputMbps,
            WireMbps: wireMbps,
            PacingWaitMs: pacingDeltaMs,
            MaxSemWaitMs: maxQueueWait,
            SockWriteMs: sockWrDeltaMs,
            AvgSockWritePerFrameMs: sockWrDeltaMs / frameCount,
            RttMs: streamA.CurrentSrttMs,
            EndPaceBytesPerSec: streamA.CurrentEffectivePaceBytesPerSec,
            EndTokenBucketBytes: streamA.CurrentTokenBucketBytes,
            Retransmissions: streamA.TotalRetransmissions - baseRetrans,
            RtoTimeouts: streamA.TotalRtoTimeouts - baseRto);
    }

    // =========================================================================
    // 二、UDP Token Bucket 驗證 (10/50/100/500 Mbps + 0%/1%/3% + 掉包恢復 + 長時間持續 + 取消安全)
    // =========================================================================

    [Fact]
    public async Task SectionII_TokenBucket_BandwidthMatrix_LossRecovery_TelemetrySeparation_And_Cancellation()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var rows = new List<UdpScenarioMetrics>();

        // 1. 10 Mbps (0% 掉包, RTT ~10ms, 15 x 32KB = 480KB; 模擬低頻寬攝影機/桌面每幀間隔 ~28ms 以免 0ms 瞬灌 384KB 觸發 Router Queue 緩衝 >100ms 造成虛假 RTO)
        {
            var (a, b, r) = CreateShapedPair(targetMbps: 10.0, oneWayDelayMs: 5, dropDataEveryN: 0);
            using (a) using (b) using (r)
            {
                long totalBytes = 15L * 32 * 1024;
                long rx = 0;
                var recvT = Task.Run(async () =>
                {
                    byte[] buf = new byte[64 * 1024];
                    while (Volatile.Read(ref rx) < totalBytes && !cts.Token.IsCancellationRequested)
                    {
                        int n = await b.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, totalBytes - Volatile.Read(ref rx))), cts.Token);
                        if (n <= 0) break;
                        Interlocked.Add(ref rx, n);
                    }
                }, cts.Token);

                byte[] f = new byte[32 * 1024];
                RandomNumberGenerator.Fill(f);
                var sw10 = Stopwatch.StartNew();
                for (int i = 0; i < 15; i++)
                {
                    await a.WriteAsync(f, cts.Token);
                    while (a.CurrentInflightPackets > 40) await Task.Delay(4, cts.Token); // 依 10Mbps BDP 節流管線 (~48KB in flight)
                }
                await a.FlushAsync(cts.Token);
                await recvT;
                sw10.Stop();
                double gp10 = (totalBytes * 8.0) / Math.Max(0.001, sw10.Elapsed.TotalSeconds) / 1_000_000.0;
                double wr10 = (a.TotalWireBytesSent * 8.0) / Math.Max(0.001, sw10.Elapsed.TotalSeconds) / 1_000_000.0;
                var q10 = a.ConsumeWaitWindowSnapshot();
                rows.Add(new UdpScenarioMetrics("Shaped-10Mbps-0%", 10, "0%", sw10.Elapsed.TotalMilliseconds, gp10, wr10, a.TotalPacingWaitMs, q10.EffectiveWaitMs, a.TotalSocketWriteDurationMs, a.TotalSocketWriteDurationMs / 15.0, a.CurrentSrttMs, a.CurrentEffectivePaceBytesPerSec, a.CurrentTokenBucketBytes, a.TotalRetransmissions, a.TotalRtoTimeouts));
                _output.WriteLine($"[Row-10M-0%] Goodput={gp10:F2}, Wire={wr10:F2}, Retrans={a.TotalRetransmissions}, RTO={a.TotalRtoTimeouts}, Drops={r.DroppedDataPackets}, Inflight={a.CurrentInflightPackets}, RTT={a.CurrentSrttMs:F1}ms");
                Assert.InRange(gp10, 6.0, 12.0);
                Assert.Equal(0, a.TotalRtoTimeouts);
            }
        }

        // 2. 50 Mbps (0% 掉包, RTT ~6ms, 24 x 48KB = 1.15MB)
        {
            var (a, b, r) = CreateShapedPair(targetMbps: 50.0, oneWayDelayMs: 3, dropDataEveryN: 0);
            using (a) using (b) using (r)
            {
                var m = await RunBurstVideoTransferAsync("Shaped-50Mbps-0%", a, b, 50, "0%", frameCount: 24, frameBytes: 48 * 1024, cts.Token);
                rows.Add(m);
                _output.WriteLine($"[Row-50M-0%] Goodput={m.GoodputMbps:F2}, Wire={m.WireMbps:F2}, Pace={m.EndPaceBytesPerSec:N0}, Retrans={m.Retransmissions}, RTO={m.RtoTimeouts}, Drops={r.DroppedDataPackets}, Inflight={a.CurrentInflightPackets}");
                Assert.InRange(m.GoodputMbps, 32.0, 55.0);
                Assert.Equal(0, m.Retransmissions);
                Assert.Equal(0, m.RtoTimeouts);
            }
        }

        // 3. 100 Mbps (0% 掉包, RTT ~4ms, 40 x 64KB = 2.56MB) -> 不得被舊版 HealthyPace=8MB/s (64Mbps) 鎖死
        {
            var (a, b, r) = CreateShapedPair(targetMbps: 100.0, oneWayDelayMs: 2, dropDataEveryN: 0);
            using (a) using (b) using (r)
            {
                var m = await RunBurstVideoTransferAsync("Shaped-100Mbps-0%", a, b, 100, "0%", frameCount: 40, frameBytes: 64 * 1024, cts.Token);
                rows.Add(m);
                _output.WriteLine($"[Row-100M-0%] Goodput={m.GoodputMbps:F2}, Wire={m.WireMbps:F2}, Pace={m.EndPaceBytesPerSec:N0}, Retrans={m.Retransmissions}, RTO={m.RtoTimeouts}, Fast={a.TotalFastRetransmits}, RelayDrops={r.DroppedDataPackets}, Inflight={a.CurrentInflightPackets}, RTT={m.RttMs:F2}");
                Assert.Equal(0, r.DroppedDataPackets);
                Assert.Equal(0, m.RtoTimeouts);
                Assert.True(m.EndPaceBytesPerSec > 8_000_000,
                    $"100Mbps 無掉包環境 HealthyPace 應自動解鎖高於 8,000,000 B/s，實際={m.EndPaceBytesPerSec:N0}");
                Assert.True(m.GoodputMbps >= 65.0,
                    $"100Mbps 環境 Goodput 不應受固定 64Mbps 限制卡在 <65Mbps，實際={m.GoodputMbps:F2} Mbps");
            }
        }

        // 4. 500 Mbps 等級本機無中繼直連迴路 (0% 掉包, 64 x 120KB = 7.68MB) -> 驗證大型高碼率影片與指標分離
        {
            var sock1 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var sock2 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock1.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            sock2.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            using var s1 = new ReliableUdpStream(sock1, (IPEndPoint)sock2.LocalEndPoint!, ownsSocket: true);
            using var s2 = new ReliableUdpStream(sock2, (IPEndPoint)sock1.LocalEndPoint!, ownsSocket: true);

            var m = await RunBurstVideoTransferAsync("Direct-500MbpsCap-SustainedLarge", s1, s2, 500, "0%", frameCount: 64, frameBytes: 120 * 1024, cts.Token);
            rows.Add(m);
            _output.WriteLine($"[Row-500M-0%] Goodput={m.GoodputMbps:F2}, Wire={m.WireMbps:F2}, Pace={m.EndPaceBytesPerSec:N0}, Retrans={m.Retransmissions}, RTO={m.RtoTimeouts}, Fast={s1.TotalFastRetransmits}, Inflight={s1.CurrentInflightPackets}, RTT={m.RttMs:F2}");
            Assert.Equal(0, m.RtoTimeouts);
            Assert.True(m.EndPaceBytesPerSec > 16_000_000,
                $"無壅塞高速直連應逐步突破初始 64Mbps 上限至高階 Pacing，實際 Pace={m.EndPaceBytesPerSec:N0} B/s");
            Assert.True(m.GoodputMbps > 120.0,
                $"高速迴路大型影格持續傳輸 Goodput 應明顯超越舊版 64Mbps 上限，實際={m.GoodputMbps:F2} Mbps");
            Assert.True(m.AvgSockWritePerFrameMs < 5.0,
                $"健康迴路 SocketWriteDurationMs 應極低 (<5ms)，不得混入 PacingWaitMs，實際={m.AvgSockWritePerFrameMs:F2} ms");
        }

        // 5. 50 Mbps + 1% 掉包 (deterministic 1/100)
        {
            var (a, b, r) = CreateShapedPair(targetMbps: 50.0, oneWayDelayMs: 5, dropDataEveryN: 100);
            using (a) using (b) using (r)
            {
                var m = await RunBurstVideoTransferAsync("Shaped-50Mbps-1%Loss", a, b, 50, "1%", frameCount: 24, frameBytes: 48 * 1024, cts.Token);
                rows.Add(m);
                _output.WriteLine($"[Row-50M-1%] Goodput={m.GoodputMbps:F2}, Wire={m.WireMbps:F2}, Pace={m.EndPaceBytesPerSec:N0}, Retrans={m.Retransmissions}, RTO={m.RtoTimeouts}, Fast={a.TotalFastRetransmits}, RelayDrops={r.DroppedDataPackets}, Inflight={a.CurrentInflightPackets}, RTT={m.RttMs:F2}");
                Assert.True(m.Retransmissions > 0, "1% 掉包情境應觸發並統計重傳");
                Assert.True(m.RtoTimeouts <= 1);
                Assert.Equal(4_500_000.0, m.EndPaceBytesPerSec, precision: 0);
            }
        }

        // 6. 先 3% 掉包 -> 後恢復 0% 掉包 -> 確認 LossyPaceBytesPerSec (4,500,000) 會於恢復後回升，不滯留 _lastLossObservedTs
        {
            var (a, b, r) = CreateShapedPair(targetMbps: 100.0, oneWayDelayMs: 4, dropDataEveryN: 33);
            using (a) using (b) using (r)
            {
                var mLoss = await RunBurstVideoTransferAsync("Shaped-100Mbps-3%Loss", a, b, 100, "3%", frameCount: 20, frameBytes: 48 * 1024, cts.Token);
                rows.Add(mLoss);
                _output.WriteLine($"[Row-100M-3%] Goodput={mLoss.GoodputMbps:F2}, Wire={mLoss.WireMbps:F2}, Pace={mLoss.EndPaceBytesPerSec:N0}, Retrans={mLoss.Retransmissions}, RTO={mLoss.RtoTimeouts}, Fast={a.TotalFastRetransmits}, RelayDrops={r.DroppedDataPackets}, Inflight={a.CurrentInflightPackets}, Elapsed={mLoss.ElapsedMs:F0}ms, RTT={mLoss.RttMs:F2}");
                Assert.True(mLoss.Retransmissions > 0);
                Assert.Equal(4_500_000.0, mLoss.EndPaceBytesPerSec, precision: 0);
                Assert.True(mLoss.RttMs < 100.0, $"Karn's Rule 抗污染後 SRTT 不可暴衝至 2000ms，實際={mLoss.RttMs:F2}ms");

                // 網路恢復 0% 掉包：等待超過觀察視窗後持續高速傳輸
                r.DropDataEveryN = 0;
                await Task.Delay(2150, cts.Token);

                var mRec = await RunBurstVideoTransferAsync("Shaped-100Mbps-Recovered", a, b, 100, "Recovered 0%", frameCount: 30, frameBytes: 64 * 1024, cts.Token);
                rows.Add(mRec);
                _output.WriteLine($"[Row-100M-Rec] Goodput={mRec.GoodputMbps:F2}, Wire={mRec.WireMbps:F2}, Pace={mRec.EndPaceBytesPerSec:N0}, Retrans={mRec.Retransmissions}, RTO={mRec.RtoTimeouts}, Fast={a.TotalFastRetransmits}, Inflight={a.CurrentInflightPackets}, RTT={mRec.RttMs:F2}");
                Assert.True(mRec.EndPaceBytesPerSec > 8_000_000.0,
                    $"網路恢復且逾 2 秒無遺失後，應退出 Lossy 4.5MB/s 並自動回升，實際={mRec.EndPaceBytesPerSec:N0} B/s");
                Assert.True(mRec.GoodputMbps >= 65.0,
                    $"恢復無掉包後應重返高速 Goodput，實際={mRec.GoodputMbps:F2} Mbps");
                Assert.Equal(0, mRec.RtoTimeouts);
            }
        }

        // 7. 統計指標嚴格分離驗證 (主動 PacingWait 不得計入 _semWaitTracker QueueWaitMs 或 SocketWriteDurationMs)
        {
            // 以受控 30 Mbps (< 35 Mbps 解鎖門檻) + 6ms 單向延遲排除無延遲 loopback 瞬時將健康步調跳升至 1Gbps/262KB Burst
            var (s1, s2, relay) = CreateShapedPair(targetMbps: 30.0, oneWayDelayMs: 6, dropDataEveryN: 0);
            using (s1) using (s2) using (relay)
            {
                const int transferBytes = 200 * 1024; // ~171 packets (< 256 MaxWindowSize -> Semaphore 必不阻塞，QueueWait 應為 ~0)
                var recvDone = Task.Run(async () =>
                {
                    byte[] buf = new byte[64 * 1024];
                    int got = 0;
                    while (got < transferBytes)
                    {
                        int n = await s2.ReadAsync(buf, cts.Token);
                        if (n <= 0) break;
                        got += n;
                    }
                }, cts.Token);

                byte[] largeBuf = new byte[transferBytes];
                await s1.WriteAsync(largeBuf, cts.Token);
                await s1.FlushAsync(cts.Token);
                await recvDone;

                var semSnap = s1.ConsumeWaitWindowSnapshot();
                _output.WriteLine($"[指標分離檢查] TotalPacingWait={s1.TotalPacingWaitMs:F2}ms, LastPacingWait={s1.LastPacingWaitMs:F2}ms, " +
                                  $"SemQueueWaitMax={semSnap.EffectiveWaitMs:F2}ms, TotalSockWr={s1.TotalSocketWriteDurationMs:F2}ms");
                Assert.True(s1.TotalPacingWaitMs > 0.0, "連續大於 PacingBurstBytes 之傳輸應產生正數 PacingWaitMs");
                Assert.True(semSnap.EffectiveWaitMs < 10f, "健康通道主動 Pacing 等待絕不可污染 QueueWaitMs");
                Assert.True(s1.TotalSocketWriteDurationMs < s1.TotalPacingWaitMs * 5.0 + 10.0);
            }
        }

        // 8. 取消傳輸與連線中斷時，等待中的 WriteAsync 必須迅速取消不死鎖
        {
            var sock1 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock1.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            // 送往黑洞 port，讓 256 視窗迅速塞滿進入 _sendWindowSemaphore.WaitAsync
            var blackHoleEp = new IPEndPoint(IPAddress.Loopback, 65432);
            using var sCancel = new ReliableUdpStream(sock1, blackHoleEp, ownsSocket: true);

            using var opCts = new CancellationTokenSource(120);
            byte[] bigPayload = new byte[400 * 1024]; // > 307KB MaxWindowSize -> 必然阻塞等待
            var cancelSw = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sCancel.WriteAsync(bigPayload, opCts.Token).AsTask());
            cancelSw.Stop();
            Assert.True(cancelSw.ElapsedMilliseconds < 1000, $"取消等待中傳輸耗時過長: {cancelSw.ElapsedMilliseconds}ms");
        }

        {
            var sock1 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock1.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var blackHoleEp = new IPEndPoint(IPAddress.Loopback, 65433);
            var sDispose = new ReliableUdpStream(sock1, blackHoleEp, ownsSocket: true);

            byte[] bigPayload = new byte[400 * 1024];
            var writeTask = sDispose.WriteAsync(bigPayload, CancellationToken.None).AsTask();
            await Task.Delay(80, cts.Token);
            var dispSw = Stopwatch.StartNew();
            sDispose.Dispose();
            await Assert.ThrowsAnyAsync<Exception>(() => writeTask);
            dispSw.Stop();
            Assert.True(dispSw.ElapsedMilliseconds < 1000, $"Dispose 中斷阻塞寫入耗時過長: {dispSw.ElapsedMilliseconds}ms");
        }

        _output.WriteLine("=== Section II UDP Token Bucket 實測數據表 (本機受控模擬與迴路) ===");
        _output.WriteLine("Scenario | CapMbps | Loss | ElapsedMs | GoodputMbps | WireMbps | PacingWaitMs | SemQWaitMs | SockWrTotalMs | SockWr/FrameMs | RTT_ms | EndPaceBps | Retrans | RTO");
        foreach (var r in rows)
        {
            _output.WriteLine($"{r.Scenario} | {r.TargetMbps:F0} | {r.LossDesc} | {r.ElapsedMs:F1} | {r.GoodputMbps:F2} | {r.WireMbps:F2} | {r.PacingWaitMs:F2} | {r.MaxSemWaitMs:F2} | {r.SockWriteMs:F2} | {r.AvgSockWritePerFrameMs:F2} | {r.RttMs:F1} | {r.EndPaceBytesPerSec:N0} | {r.Retransmissions} | {r.RtoTimeouts}");
        }
    }

    // =========================================================================
    // 三、MaxInflight 與 BDP 驗證 (6 組 RTT/頻寬矩陣 + 影格大小變化 + 低 BDP TxQueue 防膨脹)
    // =========================================================================

    [Fact]
    public async Task SectionIII_MaxInflight_BdpMatrix_FrameSizeScaling_And_LowBdpTxQueueStability()
    {
        var cases = new (float RttMs, float BwMbps, int FrameBytes, int Fps, int MinExpected, int MaxExpected, string Note)[]
        {
            (20f,  100f, 32 * 1024, 60, 2, 2, "低 RTT (<25ms) 維持基礎 MaxInflight=2"),
            (60f,  100f, 32 * 1024, 60, 4, 6, "中等 RTT + 高頻寬解鎖 60fps 管線 (5幀)"),
            (100f, 100f, 32 * 1024, 60, 5, 6, "高 BDP 100ms@100Mbps 上限不得超過 6"),
            (200f,  20f, 32 * 1024, 30, 5, 6, "高 BDP 200ms@20Mbps (BDP=500KB) 適度擴展"),
            (200f,   2f, 32 * 1024, 30, 2, 2, "低 BDP 200ms@2Mbps (0.75*BDP=37.5KB) 嚴格鎖回 2"),
            (300f,   2f, 32 * 1024, 30, 2, 2, "低 BDP 300ms@2Mbps (0.75*BDP=56.25KB, >28KB幀) 不得放寬至高排隊"),
            (200f,   2f,         0, 30, 2, 2, "低 BDP 2Mbps 閒置後無近期影格均長 -> 採 32KB 保守估算鎖回 2"),
            (300f,   0f,         0, 30, 2, 2, "冷啟動零頻寬且零影格估算時，不得僅因高 RTT 無條件增加 Inflight"),
            (100f, 100f, 96 * 1024, 60, 2, 2, "平均影格放大至 96KB 時 MaxInflight 自動收斂為 2 (192KB budget)"),
            (100f, 100f, 48 * 1024, 60, 3, 4, "平均影格為 48KB 時 MaxInflight 自動調整為 4"),
        };

        _output.WriteLine("=== Section III ComputeEffectiveMaxInflight BDP 矩陣 ===");
        foreach (var c in cases)
        {
            int win = HostSession.ComputeEffectiveMaxInflight(
                rttMs: c.RttMs,
                targetFps: c.Fps,
                effectiveWaitMs: 0f,
                sendQueuePending: 0,
                udpLossRatePercent: 0f,
                retransRatePercent: 0f,
                rtoDelta: 0,
                arqFinalSuccessRatePercent: 100f,
                inflightRatio: 0.05f,
                isCongested: false,
                avgFrameSizeBytes: c.FrameBytes,
                estimatedBandwidthMbps: c.BwMbps);

            _output.WriteLine($"RTT={c.RttMs,3}ms | BW={c.BwMbps,3}Mbps | Frame={c.FrameBytes / 1024.0,5:F1}KB | FPS={c.Fps} => MaxInflight={win} ({c.Note})");
            Assert.InRange(win, c.MinExpected, c.MaxExpected);
            Assert.InRange(win, 2, 6);
        }

        // 驗證真實低頻寬高 RTT (2 Mbps, 200ms RTT) 受控 UDP 迴路不會產生失控 TxQueue，且 FrameSeq 世代不破功
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (a, b, relay) = CreateShapedPair(targetMbps: 2.0, oneWayDelayMs: 100, dropDataEveryN: 0);
        using (a) using (b) using (relay)
        {
            var serverTask = SecureChannel.ServerHandshakeAsync(
                a,
                _ => new ServerHello(1, "HOST00001", "Host-PC", new byte[16], 1000, true, true, false),
                cts.Token);
            var clientTask = SecureChannel.ClientHandshakeAsync(b, "VIEWER001", "Viewer-PC", cts.Token);
            await Task.WhenAll(serverTask, clientTask);

            using var chA = (await serverTask).channel;
            using var chB = (await clientTask).channel;

            var receivedSeqs = new List<uint>();
            using var recvDone = new ManualResetEventSlim(false);
            var clientRecvTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        byte[] msg = await chB.ReceiveAsync(cts.Token).ConfigureAwait(false);
                        if (msg.Length >= 5 && msg[0] == (byte)HostMsg.VideoFrame)
                        {
                            uint fSeq = BitConverter.ToUInt32(msg, 1);
                            lock (receivedSeqs) { receivedSeqs.Add(fSeq); }
                            await chB.TrySendAsync(MessageCodec.FrameAck(fSeq), highPriority: true, cts.Token).ConfigureAwait(false);
                            if (receivedSeqs.Count >= 10)
                            {
                                recvDone.Set();
                                break;
                            }
                        }
                    }
                    catch { break; }
                }
            }, cts.Token);

            var inflightLock = new object();
            var ackedSeqs = new HashSet<uint>();
            int activeInflight = 0;
            var hostRecvTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        byte[] msg = await chA.ReceiveAsync(cts.Token).ConfigureAwait(false);
                        if (msg.Length == 5 && msg[0] == (byte)ViewerMsg.FrameAck)
                        {
                            uint ackSeq = BitConverter.ToUInt32(msg, 1);
                            lock (inflightLock)
                            {
                                if (ackedSeqs.Add(ackSeq)) activeInflight = Math.Max(0, activeInflight - 1);
                                if (ackedSeqs.Count >= 10) break;
                            }
                        }
                    }
                    catch { break; }
                }
            }, cts.Token);

            // 用 2Mbps @ 200ms RTT BDP 限制管線上限 (<= 2) 傳送 10 張 30KB 模擬影格，驗證 PendingLowPriorityCount 不暴衝
            int maxPendingSeen = 0;
            byte[] tilePayload = new byte[30 * 1024];
            for (uint s = 1; s <= 10; s++)
            {
                int pipe = HostSession.ComputeEffectiveMaxInflight(
                    Math.Max(200f, (float)a.CurrentSrttMs), 30, chA.ConsumeWaitWindowSnapshot().EffectiveWaitMs,
                    chA.PendingLowPriorityCount, 0f, 0f, 0, 100f, 0.1f, false, tilePayload.Length, 2.0f);
                Assert.Equal(2, pipe);

                while (true)
                {
                    lock (inflightLock)
                    {
                        if (activeInflight < pipe)
                        {
                            activeInflight++;
                            break;
                        }
                    }
                    await Task.Delay(5, cts.Token);
                }

                var msg = new byte[1 + 28 + tilePayload.Length];
                msg[0] = (byte)HostMsg.VideoFrame;
                BitConverter.TryWriteBytes(msg.AsSpan(1, 4), s);
                BitConverter.TryWriteBytes(msg.AsSpan(5, 4), s);
                BitConverter.TryWriteBytes(msg.AsSpan(9, 4), s - 1);
                await chA.SendAsync(msg, highPriority: false, cts.Token);
                maxPendingSeen = Math.Max(maxPendingSeen, chA.PendingLowPriorityCount);
            }

            Assert.True(recvDone.Wait(TimeSpan.FromSeconds(10)), "2Mbps@200ms 應完整交付全部 10 幀");
            await hostRecvTask;
            Assert.Equal(10, receivedSeqs.Count);
            Assert.Equal(10, ackedSeqs.Count);
            Assert.True(maxPendingSeen <= 2, $"低 BDP BDP-gated 管線 TxQueue 不應膨脹，maxPending={maxPendingSeen}");
            _output.WriteLine($"[Section III 2Mbps@200ms 實機封包迴路] MaxPending={maxPendingSeen}, Acked={ackedSeqs.Count}/10, FinalInflight={activeInflight}");
            cts.Cancel();
            try { await Task.WhenAll(clientRecvTask, hostRecvTask); } catch { }
        }
    }

    // =========================================================================
    // 四、自適應解析度驗證 (排除 PacingWait / 捕捉真 UDP Socket 背壓 / 孤立容忍 / Anti-PingPong)
    // =========================================================================

    [Fact]
    public async Task SectionIV_AdaptiveResolution_PacingExclusion_RealSocketBackpressure_And_StateStability()
    {
        // 1. 排除主動 PacingWaitMs 後，無真實阻塞時絕不誤降級；真實 UDP 背壓發生時必須降級
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        {
            var sock1 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var sock2 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock1.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            sock2.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            using var s1 = new ReliableUdpStream(sock1, (IPEndPoint)sock2.LocalEndPoint!, ownsSocket: true);
            using var s2 = new ReliableUdpStream(sock2, (IPEndPoint)sock1.LocalEndPoint!, ownsSocket: true);

            var serverTask = SecureChannel.ServerHandshakeAsync(
                s1,
                _ => new ServerHello(1, "HOST00001", "Host-PC", new byte[16], 1000, true, true, false),
                cts.Token);
            var clientTask = SecureChannel.ClientHandshakeAsync(s2, "VIEWER001", "Viewer-PC", cts.Token);
            await Task.WhenAll(serverTask, clientTask);

            using var chHost = (await serverTask).channel;
            using var chViewer = (await clientTask).channel;

            int viewerRecvFrames = 0;
            var viewerDrainTask = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var msg = await chViewer.ReceiveAsync(cts.Token).ConfigureAwait(false);
                        if (msg.Length > 0 && Interlocked.Increment(ref viewerRecvFrames) >= 8) break;
                    }
                    catch { break; }
                }
            }, cts.Token);

            var ctrlHealthy = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
            byte[] payload = new byte[120 * 1024];
            RandomNumberGenerator.Fill(payload);

            // 連送 8 個大型影格使 ReliableUdpStream 觸發 Token Bucket PacingWaitMs > 0
            for (int i = 0; i < 8; i++)
            {
                await chHost.SendAsync(payload, highPriority: false, cts.Token);
                var chSnap = chHost.ConsumeWaitWindowSnapshot();
                var udpSnap = s1.ConsumeWaitWindowSnapshot();
                float effectiveWait = Math.Max(chSnap.EffectiveWaitMs, udpSnap.EffectiveWaitMs);

                // 模擬 HostSession 不將 PacingWaitMs 視為 SendQueueWaitMs，僅看真實 QueueWait + InflightRatio
                var decision = ctrlHealthy.Evaluate(new AdaptiveSample(
                    NowMs: i * 500L,
                    SendQueueWaitMs: effectiveWait,
                    SendQueuePending: chHost.PendingLowPriorityCount,
                    UdpRawLossRatePercent: 0f,
                    RetransmissionRatePercent: 0f,
                    ArqFinalSuccessRatePercent: 100f,
                    InflightRatio: (float)s1.CurrentInflightPackets / s1.MaxInflightPackets,
                    RtoTimeoutsDelta: 0,
                    FastRetransmitsDelta: 0,
                    TargetFps: 30,
                    ActualFps: 30f,
                    FrameDropsDelta: 0,
                    EndToEndDelayMs: effectiveWait + 12f,
                    IsStreamActive: true,
                    IsTransportConstrained: false));
                Assert.False(decision.Changed);
            }

            await viewerDrainTask;
            Assert.True(s1.TotalPacingWaitMs > 0.0, "應有實際 PacingWait 發生，以證明排除邏輯有效而非沒測到 Pacing");
            Assert.Equal(ResolutionLevel.Level0, ctrlHealthy.CurrentLevel);
            _output.WriteLine($"[Section IV Pacing 隔離驗證] TotalPacingWait={s1.TotalPacingWaitMs:F2}ms, FinalLevel={ctrlHealthy.CurrentLevel}");
            cts.Cancel();
            try { await viewerDrainTask; } catch { }
        }

        // 2. 真實 Socket 背壓（IsTransportConstrained=true 經由 SocketWriteDurationMs >= 30f / winStallMs 傳導）必須觸發降級
        {
            var ctrlBackpressure = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
            for (long t = 0; t <= 4500; t += 500)
            {
                ctrlBackpressure.Evaluate(new AdaptiveSample(
                    NowMs: t,
                    SendQueueWaitMs: 3f, // 即使 Semaphore QueueWait 尚未暴衝，但 SocketWriteDuration >= 30ms + winStall 使 IsTransportConstrained=true
                    SendQueuePending: 0,
                    UdpRawLossRatePercent: 0f,
                    RetransmissionRatePercent: 0f,
                    ArqFinalSuccessRatePercent: 100f,
                    InflightRatio: 0.35f,
                    RtoTimeoutsDelta: 0,
                    FastRetransmitsDelta: 0,
                    TargetFps: 30,
                    ActualFps: 14f,
                    FrameDropsDelta: 0,
                    EndToEndDelayMs: 65f,
                    IsStreamActive: true,
                    SourceFps: 30f,
                    CaptureFps: 30f,
                    IsTransportConstrained: true));
            }
            Assert.Equal(ResolutionLevel.Level1, ctrlBackpressure.CurrentLevel);
        }

        // 3. 恢復期孤立 2 個非健康視窗容忍 + 第 3 個重置，以及真壅塞立即歸零
        {
            var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
            long t = 0;
            for (; t <= 4000; t += 500)
            {
                ctrl.Evaluate(new AdaptiveSample(t, 120f, 3, 6f, 7f, 97f, 0.55f, 1, 2, 30, 12f, 1, 250f, true, IsTransportConstrained: true));
            }
            Assert.Equal(ResolutionLevel.Level1, ctrl.CurrentLevel);

            // 進入恢復觀察，先給 5 秒健康視窗 (t 到 t+5000)
            long recoverBase = t;
            t += 5500; // 越過 5 秒切換冷卻
            for (int i = 0; i < 10; i++, t += 500)
            {
                ctrl.Evaluate(new AdaptiveSample(t, 1f, 0, 0f, 0f, 100f, 0.05f, 0, 0, 30, 30f, 0, 20f, true));
            }
            // 插入恰好 2 個孤立輕微非健康視窗 (SendQueueWait=22ms 未達嚴重壅塞，但不滿足 <=8ms 健康門檻) -> 容忍暫停、不重設進度
            ctrl.Evaluate(new AdaptiveSample(t, 22f, 0, 1.5f, 1.8f, 100f, 0.25f, 0, 0, 30, 27f, 0, 45f, true));
            t += 500;
            ctrl.Evaluate(new AdaptiveSample(t, 22f, 0, 1.5f, 1.8f, 100f, 0.25f, 0, 0, 30, 27f, 0, 45f, true));
            t += 500;

            // 繼續健康視窗，應在約 12s 健康累計 (~13-14s total) 內升回 L0
            bool recoveredWith2Jitters = false;
            long deadline = t + 9000;
            for (; t <= deadline; t += 500)
            {
                var d = ctrl.Evaluate(new AdaptiveSample(t, 1f, 0, 0f, 0f, 100f, 0.05f, 0, 0, 30, 30f, 0, 20f, true));
                if (d.Changed && ctrl.CurrentLevel == ResolutionLevel.Level0)
                {
                    recoveredWith2Jitters = true;
                    break;
                }
            }
            Assert.True(recoveredWith2Jitters, "恢復期間 2 個孤立輕微非健康視窗不應重置累計進度");
            _output.WriteLine($"[Section IV 恢復容忍] 2個孤立抖動容忍總耗時={t - recoverBase}ms 成功恢復 Level0");

            // 再測試：降級至 L1 -> 恢復一半時遭遇連續真壅塞 -> 必須立即重置恢復計時（不可假升回）
            t += 6000;
            for (int i = 0; i < 8; i++, t += 500)
            {
                ctrl.Evaluate(new AdaptiveSample(t, 130f, 4, 7f, 9f, 95f, 0.70f, 2, 3, 30, 10f, 2, 290f, true, IsTransportConstrained: true));
            }
            Assert.Equal(ResolutionLevel.Level1, ctrl.CurrentLevel);

            t += 5500;
            // 健康 6 秒 (未滿 12/24 秒退避)
            for (int i = 0; i < 12; i++, t += 500)
            {
                ctrl.Evaluate(new AdaptiveSample(t, 1f, 0, 0f, 0f, 100f, 0.05f, 0, 0, 30, 30f, 0, 20f, true));
            }
            Assert.Equal(ResolutionLevel.Level1, ctrl.CurrentLevel);

            // 遭遇 1 秒真實重度壅塞 -> 立即重設恢復進度
            ctrl.Evaluate(new AdaptiveSample(t, 180f, 5, 10f, 12f, 94f, 0.85f, 3, 2, 30, 8f, 3, 350f, true, IsTransportConstrained: true));
            t += 500;
            ctrl.Evaluate(new AdaptiveSample(t, 180f, 5, 10f, 12f, 94f, 0.85f, 3, 2, 30, 8f, 3, 350f, true, IsTransportConstrained: true));
            t += 500;

            // 緊接著若只有 7 秒健康視窗 (之前那 6 秒必須已被歸零！) 絕不能湊成 6+7=13 秒偷跑回 L0
            for (int i = 0; i < 14; i++, t += 500)
            {
                var d = ctrl.Evaluate(new AdaptiveSample(t, 1f, 0, 0f, 0f, 100f, 0.05f, 0, 0, 30, 30f, 0, 20f, true));
                Assert.False(d.Changed && ctrl.CurrentLevel == ResolutionLevel.Level0, "連續真壅塞必須立即重置恢復計時，不可累加舊進度");
            }
        }

        // 4. 震盪網路 (Anti-Ping-Pong) 與純高 RTT / 健康全螢幕影片情境
        {
            var ctrl = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
            int transitions = 0;
            long t = 0;
            // 在臨界震盪邊緣交替 60 秒
            for (int step = 0; step < 120; step++, t += 500)
            {
                bool blip = (step % 4 == 0);
                var d = ctrl.Evaluate(new AdaptiveSample(
                    NowMs: t,
                    SendQueueWaitMs: blip ? 95f : 3f,
                    SendQueuePending: blip ? 2 : 0,
                    UdpRawLossRatePercent: blip ? 3.5f : 0.5f,
                    RetransmissionRatePercent: blip ? 4.0f : 0.5f,
                    ArqFinalSuccessRatePercent: blip ? 98f : 100f,
                    InflightRatio: blip ? 0.5f : 0.1f,
                    RtoTimeoutsDelta: 0,
                    FastRetransmitsDelta: blip ? 1 : 0,
                    TargetFps: 30,
                    ActualFps: blip ? 24f : 30f,
                    FrameDropsDelta: 0,
                    EndToEndDelayMs: blip ? 150f : 35f,
                    IsStreamActive: true,
                    IsTransportConstrained: false));
                if (d.Changed) transitions++;
            }
            Assert.True(transitions <= 2, $"震盪邊緣不應發生 L0<->L1 反覆頻繁切換 (Ping-Pong)，transitions={transitions}");

            // 健康全螢幕高 FPS 影片 + 純高 RTT (160ms) 但無 QueueWait / Loss -> 必須全年維持 L0
            var ctrlHealthyHighRtt = new AdaptiveResolutionController(1920, 1080, ResolutionMode.Auto);
            for (long ms = 0; ms <= 30_000; ms += 500)
            {
                var d = ctrlHealthyHighRtt.Evaluate(new AdaptiveSample(ms, 1.5f, 0, 0.5f, 0.5f, 100f, 0.10f, 0, 0, 30, 30f, 0, 95f, true));
                Assert.False(d.Changed);
            }
            Assert.Equal(ResolutionLevel.Level0, ctrlHealthyHighRtt.CurrentLevel);
        }
    }

    // =========================================================================
    // 五、影格編碼與畫質控制驗證 (健康全螢幕影片保留 Quality / 壅塞降載 / 模式隔離)
    // =========================================================================

    [Fact]
    public unsafe void SectionV_EncoderQualityControl_FullscreenHealthyVsCongested_And_ModeIsolation()
    {
        int w = 1920, h = 1080;
        var raw = new byte[w * h * 4];
        var encoder = new FrameEncoder(w, h) { Quality = 80 };
        var ctrl = new AdaptiveResolutionController(w, h, ResolutionMode.Auto);
        var trace = new List<string>();

        static void PaintVideoFrame(byte* ptr, int width, int height, int seed)
        {
            for (int y = 0; y < height; y += 2)
            {
                int row = y * width;
                for (int x = 0; x < width; x += 2)
                {
                    int o = (row + x) * 4;
                    ptr[o] = (byte)(((x * 13) + seed * 19) & 0xFF);
                    ptr[o + 1] = (byte)(((y * 29) + seed * 7) & 0xFF);
                    ptr[o + 2] = (byte)(((x ^ y) + seed * 11) & 0xFF);
                    ptr[o + 3] = 255;
                }
            }
        }

        fixed (byte* pRaw = raw)
        {
            long totalPx = (long)w * h;
            var fullDirty = new[] { new Rectangle(0, 0, w, h) };

            // 階段 1：健康網路全螢幕影片連續 10 幀 (100% Dirty) -> 不得因面積 >= 75% 而降 Quality，Resolution=L0
            for (int i = 0; i < 10; i++)
            {
                PaintVideoFrame(pRaw, w, h, i + 1);
                var cap = new CapturedFrame((IntPtr)pRaw, w, h, w * 4, true, fullDirty, null, false);
                encoder.CongestionQualityReduction = 0;
                var d = ctrl.Evaluate(new AdaptiveSample(i * 500L, 1f, 0, 0f, 0f, 100f, 0.08f, 0, 0, 30, 30f, 0, 22f, true));
                Assert.False(d.Changed);

                var tiles = encoder.Encode(cap, true, 1000 + i * 33L, out var ft, out _);
                int effQ = encoder.GetEffectiveTileQuality(totalPx, totalPx, ft == FrameType.FullFrame);
                Assert.NotEmpty(tiles);
                Assert.Equal(80, encoder.Quality);
                Assert.Equal(80, effQ);
                Assert.Equal(ResolutionLevel.Level0, ctrl.CurrentLevel);
                if (i == 0 || i == 9)
                {
                    trace.Add($"Stage1 HealthyFrame#{i}: Mode={ctrl.Mode}, Level={ctrl.CurrentLevel}, UserQ={encoder.Quality}, EffQ={effQ}, Tiles={tiles.Count}, Bytes={tiles.Sum(t => t.Data.Length)}");
                }
            }

            // 階段 2：模擬真實傳輸壅塞 (QueueWait=160ms, Retrans=9%, RTO=3) -> 觸發畫質節流但不覆寫 Quality=80 與模式
            encoder.CongestionQualityReduction = 10;
            PaintVideoFrame(pRaw, w, h, 50);
            var congestedCap = new CapturedFrame((IntPtr)pRaw, w, h, w * 4, true, fullDirty, null, false);
            var congestedTiles = encoder.Encode(congestedCap, true, 3000, out var cft, out _);
            int congestedEffQ = encoder.GetEffectiveTileQuality(totalPx, totalPx, cft == FrameType.FullFrame);
            Assert.Equal(80, encoder.Quality);
            Assert.Equal(70, congestedEffQ);
            trace.Add($"Stage2 Congested: Mode={ctrl.Mode}, Level={ctrl.CurrentLevel}, UserQ={encoder.Quality}, EffQ={congestedEffQ}, Bytes={congestedTiles.Sum(t => t.Data.Length)}");

            // 階段 3：手動切換 Auto -> Performance -> Original，固定 Quality=80 絕不受污染
            encoder.CongestionQualityReduction = 0;
            foreach (var mode in new[] { ResolutionMode.Performance, ResolutionMode.Original, ResolutionMode.Auto })
            {
                var d = ctrl.SetMode(mode, 10_000);
                encoder.ConfigureStreamResolution(d.Profile.StreamWidth, d.Profile.StreamHeight);
                PaintVideoFrame(pRaw, w, h, 80 + (int)mode);
                var modeCap = new CapturedFrame((IntPtr)pRaw, w, h, w * 4, true, fullDirty, null, false);
                var modeTiles = encoder.Encode(modeCap, true, 10_500 + ((int)mode * 100), out var mft, out _);
                int effQ = encoder.GetEffectiveTileQuality(totalPx, totalPx, mft == FrameType.FullFrame);
                Assert.Equal(80, encoder.Quality);
                Assert.Equal(80, effQ);
                trace.Add($"Stage3 ModeSwitch({mode}): Level={ctrl.CurrentLevel} ({encoder.StreamWidth}x{encoder.StreamHeight}), UserQ={encoder.Quality}, EffQ={effQ}, Bytes={modeTiles.Sum(t => t.Data.Length)}");
            }
        }

        _output.WriteLine("=== Section V Resolution Level & JPEG Quality 變化追蹤 ===");
        foreach (var line in trace) _output.WriteLine(line);
    }
}
