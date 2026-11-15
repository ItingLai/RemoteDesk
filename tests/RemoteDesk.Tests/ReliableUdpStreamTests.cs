using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RemoteDesk.Common;
using RemoteDesk.Net;
using Xunit;

namespace RemoteDesk.Tests;

public class ReliableUdpStreamTests
{
    private static (ReliableUdpStream clientStream, ReliableUdpStream serverStream) CreatePair()
    {
        var s1 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s1.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var s2 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s2.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var ep1 = (IPEndPoint)s1.LocalEndPoint!;
        var ep2 = (IPEndPoint)s2.LocalEndPoint!;

        var stream1 = new ReliableUdpStream(s1, ep2, ownsSocket: true);
        var stream2 = new ReliableUdpStream(s2, ep1, ownsSocket: true);

        return (stream1, stream2);
    }

    [Fact]
    public async Task TransferSmallData_Succeeds()
    {
        var (s1, s2) = CreatePair();
        using (s1)
        using (s2)
        {
            byte[] sentData = Encoding.UTF8.GetBytes("Hello Reliable UDP!");
            await s1.WriteAsync(sentData);

            byte[] recvBuf = new byte[256];
            int read = await s2.ReadAsync(recvBuf);

            Assert.Equal(sentData.Length, read);
            Assert.Equal("Hello Reliable UDP!", Encoding.UTF8.GetString(recvBuf, 0, read));
        }
    }

    [Fact]
    public async Task TransferLargeMultiFragmentData_Succeeds()
    {
        var (s1, s2) = CreatePair();
        using (s1)
        using (s2)
        {
            // 測試 64 KB 大小 (大於 MTU 1200 bytes，需要切分為 50+ 個 UDP 片段)
            byte[] sentData = new byte[65536];
            RandomNumberGenerator.Fill(sentData);

            var writeTask = Task.Run(async () =>
            {
                await s1.WriteAsync(sentData);
                await s1.FlushAsync();
            });

            byte[] recvData = new byte[sentData.Length];
            int totalRead = 0;
            while (totalRead < recvData.Length)
            {
                int r = await s2.ReadAsync(recvData.AsMemory(totalRead, recvData.Length - totalRead));
                if (r <= 0) break;
                totalRead += r;
            }

            await writeTask;

            Assert.Equal(sentData.Length, totalRead);
            Assert.True(sentData.AsSpan().SequenceEqual(recvData));
        }
    }

    [Fact]
    public async Task BidirectionalTransfer_SimultaneousStreams_Succeeds()
    {
        var (s1, s2) = CreatePair();
        using (s1)
        using (s2)
        {
            byte[] d1 = RandomNumberGenerator.GetBytes(16384); // 16 KB
            byte[] d2 = RandomNumberGenerator.GetBytes(16384); // 16 KB

            var t1 = Task.Run(async () =>
            {
                await s1.WriteAsync(d1);
                byte[] r1 = new byte[d2.Length];
                int total = 0;
                while (total < r1.Length)
                {
                    int n = await s1.ReadAsync(r1.AsMemory(total, r1.Length - total));
                    if (n <= 0) break;
                    total += n;
                }
                return r1;
            });

            var t2 = Task.Run(async () =>
            {
                await s2.WriteAsync(d2);
                byte[] r2 = new byte[d1.Length];
                int total = 0;
                while (total < r2.Length)
                {
                    int n = await s2.ReadAsync(r2.AsMemory(total, r2.Length - total));
                    if (n <= 0) break;
                    total += n;
                }
                return r2;
            });

            await Task.WhenAll(t1, t2);
            var res1 = await t1;
            var res2 = await t2;

            Assert.True(d2.AsSpan().SequenceEqual(res1));
            Assert.True(d1.AsSpan().SequenceEqual(res2));
        }
    }

    [Fact]
    public async Task SecureChannel_OverReliableUdpStream_HandshakeAndCryptoSucceeds()
    {
        var (clientStream, serverStream) = CreatePair();
        using (clientStream)
        using (serverStream)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            var serverTask = Task.Run(async () =>
            {
                var (serverChannel, clientHello) = await SecureChannel.ServerHandshakeAsync(
                    serverStream,
                    ch => new ServerHello(1, "HOST00001", "Host-PC", new byte[16], 1000, true, true, false),
                    cts.Token);

                // 接收客戶端加密訊息
                var msg = await serverChannel.ReceiveAsync(cts.Token);
                string text = Encoding.UTF8.GetString(msg);

                // 回傳加密回覆
                await serverChannel.SendAsync(Encoding.UTF8.GetBytes("Echo: " + text), cts.Token);
                return (serverChannel, clientHello);
            });

            var clientTask = Task.Run(async () =>
            {
                var (clientChannel, serverHello) = await SecureChannel.ClientHandshakeAsync(
                    clientStream, "VIEWER001", "Viewer-PC", cts.Token);

                // 送出加密訊息
                await clientChannel.SendAsync(Encoding.UTF8.GetBytes("Secret Desktop Payload"), cts.Token);

                // 接收回覆
                var resp = await clientChannel.ReceiveAsync(cts.Token);
                return (clientChannel, serverHello, Text: Encoding.UTF8.GetString(resp));
            });

            await Task.WhenAll(serverTask, clientTask);
            var serverRes = await serverTask;
            var clientRes = await clientTask;

            using (serverRes.serverChannel)
            using (clientRes.clientChannel)
            {
                Assert.Equal("VIEWER001", serverRes.clientHello.ViewerId);
                Assert.Equal("HOST00001", clientRes.serverHello.HostId);
                Assert.Equal("Echo: Secret Desktop Payload", clientRes.Text);
                Assert.Equal(serverRes.serverChannel.VerificationCode, clientRes.clientChannel.VerificationCode);
            }
        }
    }

    [Fact]
    public async Task TransferBurstVideoFrame_LargeWindow_Succeeds()
    {
        var (s1, s2) = CreatePair();
        using (s1)
        using (s2)
        {
            // 模擬 256 KB 高畫質桌面影像影格突發傳輸 (超過 210 個 UDP 封包)
            byte[] sentData = new byte[262144];
            RandomNumberGenerator.Fill(sentData);

            var writeTask = Task.Run(async () =>
            {
                await s1.WriteAsync(sentData);
                await s1.FlushAsync();
            });

            byte[] recvData = new byte[sentData.Length];
            int totalRead = 0;
            while (totalRead < recvData.Length)
            {
                int r = await s2.ReadAsync(recvData.AsMemory(totalRead, recvData.Length - totalRead));
                if (r <= 0) break;
                totalRead += r;
            }

            await writeTask;

            Assert.Equal(sentData.Length, totalRead);
            Assert.True(sentData.AsSpan().SequenceEqual(recvData));
        }
    }

    [Fact]
    public async Task Telemetry_TracksPacketsAndAcks()
    {
        var (s1, s2) = CreatePair();
        using (s1)
        using (s2)
        {
            Assert.Equal(256, s1.MaxInflightPackets);
            Assert.Equal(0, s1.CurrentInflightPackets);

            byte[] sentData = new byte[10000]; // ~9 packets
            await s1.WriteAsync(sentData);
            await s1.FlushAsync();

            byte[] recvData = new byte[sentData.Length];
            int read = 0;
            while (read < sentData.Length)
            {
                read += await s2.ReadAsync(recvData.AsMemory(read, sentData.Length - read));
            }

            Assert.True(s1.TotalPacketsSent >= 8);
            Assert.True(s1.LastFrameWriteDurationMs >= 0);
        }
    }

    [Fact]
    public async Task VideoStream_ContinuousMultiFrameThroughput_DoesNotStallOnMicroPacing()
    {
        var (s1, s2) = CreatePair();
        using (s1)
        using (s2)
        {
            // 模擬連續 20 幀 80KB 1080p 影片影格 (~1.6MB, ~1340 個 UDP 封包)
            // 舊版固定每 4 封包 Task.Delay(1) 會產生 320+ 次執行緒休眠導致投影片卡頓；
            // 權杖桶微步調應可在 < 1500ms 內平滑連續送達且無未恢復封包遺失。
            const int frameSize = 80 * 1024;
            const int frameCount = 20;
            byte[] frame = new byte[frameSize];
            RandomNumberGenerator.Fill(frame);
            int totalBytes = frameSize * frameCount;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var recvTask = Task.Run(async () =>
            {
                byte[] recvBuf = new byte[frameSize];
                for (int f = 0; f < frameCount; f++)
                {
                    int got = 0;
                    while (got < frameSize)
                    {
                        int n = await s2.ReadAsync(recvBuf.AsMemory(got, frameSize - got));
                        if (n <= 0) throw new EndOfStreamException();
                        got += n;
                    }
                    Assert.True(frame.AsSpan().SequenceEqual(recvBuf));
                }
            });

            for (int f = 0; f < frameCount; f++)
            {
                await s1.WriteAsync(frame);
            }
            await s1.FlushAsync();
            await recvTask.WaitAsync(TimeSpan.FromSeconds(10));
            sw.Stop();

            Assert.Equal(0, s1.TotalUnrecoverableLost);
            Assert.True(sw.ElapsedMilliseconds < 1500,
                $"連續 20 幀 80KB UDP 影片傳輸耗時過長 ({sw.ElapsedMilliseconds}ms, total={totalBytes}B)，微步調可能造成畫面頓挫！");
        }
    }

    [Fact]
    public async Task CumulativeAckHole_DroppedFirstFastRetransmit_RecoversWithoutMassRtoStorm()
    {
        // 模擬實際 WAN 日誌 (20261114-16368.log) 中的累積 ACK 缺口現象：
        // 若 Seq=2 初次遺失且其 dup==3 Fast Retransmit 也遺失（或 RTT > minRto=100ms），
        // 舊版只觸發一次 dup==3 且 RetransmitLoopAsync 會對後方 48 個已被接收端暫存的封包同時引爆 RTO=29~49 洪峰。
        // 修正後應透過 dup>=6 快速重傳再試 + 左緣閘門，使 RTO 維持 <= 2 且完整無損交付。
        using var sockA = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var sockB = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var proxySock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sockA.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        sockB.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        proxySock.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var epA = (IPEndPoint)sockA.LocalEndPoint!;
        var epB = (IPEndPoint)sockB.LocalEndPoint!;
        var epProxy = (IPEndPoint)proxySock.LocalEndPoint!;

        using var proxyCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        long dropSpecificSeqTarget = 2;
        int seq2DropCount = 0;
        var proxyTask = Task.Run(async () =>
        {
            byte[] buf = new byte[4096];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (!proxyCts.IsCancellationRequested)
            {
                try
                {
                    var res = await proxySock.ReceiveFromAsync(buf, SocketFlags.None, remote, proxyCts.Token);
                    var src = (IPEndPoint)res.RemoteEndPoint;
                    byte[] copy = buf.AsSpan(0, res.ReceivedBytes).ToArray();
                    if (src.Port == epA.Port)
                    {
                        if (copy.Length >= ReliableUdpStream.HeaderSize && copy[4] == ReliableUdpStream.TypeData)
                        {
                            uint seq = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(6, 4));
                            long target = Interlocked.Read(ref dropSpecificSeqTarget);
                            // 故意丟棄目標序號前 2 次傳送（初次發送 + 第一次 dup==3 Fast Retransmit）
                            if (seq == (uint)target && Interlocked.Increment(ref seq2DropCount) <= 2)
                                continue;
                        }
                        await proxySock.SendToAsync(copy, SocketFlags.None, epB, proxyCts.Token);
                    }
                    else if (src.Port == epB.Port)
                    {
                        await proxySock.SendToAsync(copy, SocketFlags.None, epA, proxyCts.Token);
                    }
                }
                catch { break; }
            }
        });

        using var streamA = new ReliableUdpStream(sockA, epProxy, ownsSocket: false);
        using var streamB = new ReliableUdpStream(sockB, epProxy, ownsSocket: false);

        // 情境 A：模擬連續兩幀 30KB (~26封包/幀) 以 ~22fps (間隔 45ms) 串流；
        // Frame 1 中 Seq=2 初次遺失且 dup==3 Fast Retransmit 亦遭丟棄，Frame 2 抵達時應由 dup>=6 機制在 1 RTT 內救回，RTO=0
        byte[] frame1 = new byte[30 * 1024];
        byte[] frame2 = new byte[30 * 1024];
        RandomNumberGenerator.Fill(frame1);
        RandomNumberGenerator.Fill(frame2);
        byte[] expected = new byte[frame1.Length + frame2.Length];
        frame1.CopyTo(expected, 0);
        frame2.CopyTo(expected, frame1.Length);
        byte[] recv = new byte[expected.Length];

        var recvTask = Task.Run(async () =>
        {
            int got = 0;
            while (got < recv.Length)
            {
                int n = await streamB.ReadAsync(recv.AsMemory(got, recv.Length - got), proxyCts.Token);
                if (n <= 0) break;
                got += n;
            }
            return got;
        });

        await streamA.WriteAsync(frame1, proxyCts.Token);
        await Task.Delay(45, proxyCts.Token);
        await streamA.WriteAsync(frame2, proxyCts.Token);
        await streamA.FlushAsync(proxyCts.Token);
        int totalRead = await recvTask;

        Assert.Equal(expected.Length, totalRead);
        Assert.True(expected.AsSpan().SequenceEqual(recv));
        Assert.Equal(0, streamA.CurrentInflightPackets);
        Assert.True(streamA.TotalFastRetransmits >= 2,
            $"遺失的首次 Fast Retransmit 應透過後續 dup ACK 再次快速補送，實際 FastRetrans={streamA.TotalFastRetransmits}");
        Assert.True(streamA.TotalRtoTimeouts <= 1,
            $"單一累積 ACK 缺口不應對後方數十個已在途封包引爆 RTO 洪峰，實際 TotalRtoTimeouts={streamA.TotalRtoTimeouts}");

        // 情境 B：若無後續影格觸發 dup ACK 而必須等到 RTO 逾時，對 48 個後方已暫存封包亦僅重傳左緣缺口，絕不重演 RTO=29~49
        long rtoBefore = streamA.TotalRtoTimeouts;
        long fastBefore = streamA.TotalFastRetransmits;
        Interlocked.Exchange(ref seq2DropCount, 0); // 重置計數，準備攔截後續單一高序號缺口
        byte[] frame3 = new byte[50 * 1024];
        RandomNumberGenerator.Fill(frame3);
        byte[] recv3 = new byte[frame3.Length];
        // 指定目前下一個即將送出的第二個封包 (nextSeq + 1) 連丟 2 次並無後續影格
        dropSpecificSeqTarget = streamA.TotalPacketsSent + 2; // +1 為下一包, +2 保證前方至少 1 包先抵達

        var recvTask3 = Task.Run(async () =>
        {
            int got = 0;
            while (got < recv3.Length)
            {
                int n = await streamB.ReadAsync(recv3.AsMemory(got, recv3.Length - got), proxyCts.Token);
                if (n <= 0) break;
                got += n;
            }
            return got;
        });

        await streamA.WriteAsync(frame3, proxyCts.Token);
        await streamA.FlushAsync(proxyCts.Token);
        int totalRead3 = await recvTask3;

        long rtoDeltaB = streamA.TotalRtoTimeouts - rtoBefore;
        Assert.Equal(frame3.Length, totalRead3);
        Assert.True(frame3.AsSpan().SequenceEqual(recv3));
        Assert.Equal(0, streamA.CurrentInflightPackets);
        Assert.True(rtoDeltaB <= 2,
            $"無後續影格等待 RTO 恢復時，後方 40+ 累積封包不應全部連坐引爆 RTO 洪峰，實際 rtoDelta={rtoDeltaB} (FastDelta={streamA.TotalFastRetransmits - fastBefore})");

        proxyCts.Cancel();
        try { await proxyTask; } catch { }
    }
}
