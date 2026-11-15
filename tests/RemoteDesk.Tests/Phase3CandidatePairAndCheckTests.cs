using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using RemoteDesk.Common;
using RemoteDesk.Net;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class Phase3CandidatePairAndCheckTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 雙向可控式本機 NAT / 防火牆模擬橋接器：
    /// Side A 送往 ProxyEndPointForA 的封包會依規則轉交給真正的 Peer B Socket（以 ProxyEndPointForB 作為來源）；
    /// 反之亦然，藉此在真實 OS UDP Socket 上精確重現單向阻斷、延遲、重複封包與封包觀察。
    /// </summary>
    private sealed class NatUdpBridge : IDisposable
    {
        private readonly Socket _proxyFacingA;
        private readonly Socket _proxyFacingB;
        private readonly IPEndPoint _realPeerA;
        private readonly IPEndPoint _realPeerB;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loopAtoB;
        private readonly Task _loopBtoA;

        public IPEndPoint EndPointForA { get; }
        public IPEndPoint EndPointForB { get; }

        public bool AllowAtoB { get; set; } = true;
        public bool AllowBtoA { get; set; } = true;
        public int DelayAtoBMs { get; set; }
        public int DelayBtoAMs { get; set; }
        public int DuplicateBtoACount { get; set; } = 1;
        public Func<byte[], int, bool>? FilterAtoB { get; set; }
        public Func<byte[], int, int?>? CustomDelayBtoAForPacket { get; set; }
        public Action<byte[], IPEndPoint>? OnObservedBtoAPacket { get; set; }
        public int PacketsForwardedAtoB => _packetsAtoB;
        public int PacketsForwardedBtoA => _packetsBtoA;

        private int _packetsAtoB;
        private int _packetsBtoA;

        public NatUdpBridge(IPEndPoint realPeerA, IPEndPoint realPeerB, string? bindIpA = null, string? bindIpB = null)
        {
            _realPeerA = realPeerA;
            _realPeerB = realPeerB;

            _proxyFacingA = IceCandidateGatherer.CreateConfiguredUdpSocket();
            _proxyFacingA.Bind(new IPEndPoint(IPAddress.Parse(bindIpA ?? "127.0.0.10"), 0));
            EndPointForA = (IPEndPoint)_proxyFacingA.LocalEndPoint!;

            _proxyFacingB = IceCandidateGatherer.CreateConfiguredUdpSocket();
            _proxyFacingB.Bind(new IPEndPoint(IPAddress.Parse(bindIpB ?? "127.0.0.11"), 0));
            EndPointForB = (IPEndPoint)_proxyFacingB.LocalEndPoint!;

            _loopAtoB = Task.Run(ForwardAtoBAsync);
            _loopBtoA = Task.Run(ForwardBtoAAsync);
        }

        private async Task ForwardAtoBAsync()
        {
            byte[] buf = new byte[4096];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var res = await _proxyFacingA.ReceiveFromAsync(buf, SocketFlags.None, remote, _cts.Token).ConfigureAwait(false);
                    byte[] copy = buf.AsSpan(0, res.ReceivedBytes).ToArray();
                    int seqIdx = Interlocked.Increment(ref _packetsAtoB);

                    if (!AllowAtoB) continue;
                    if (FilterAtoB != null && !FilterAtoB(copy, seqIdx)) continue;

                    int d = DelayAtoBMs;
                    if (d > 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(d, _cts.Token).ConfigureAwait(false);
                                await _proxyFacingB.SendToAsync(copy, SocketFlags.None, _realPeerB, _cts.Token).ConfigureAwait(false);
                            }
                            catch { }
                        });
                    }
                    else
                    {
                        await _proxyFacingB.SendToAsync(copy, SocketFlags.None, _realPeerB, _cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        private async Task ForwardBtoAAsync()
        {
            byte[] buf = new byte[4096];
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var res = await _proxyFacingB.ReceiveFromAsync(buf, SocketFlags.None, remote, _cts.Token).ConfigureAwait(false);
                    byte[] copy = buf.AsSpan(0, res.ReceivedBytes).ToArray();
                    int seqIdx = Interlocked.Increment(ref _packetsBtoA);

                    OnObservedBtoAPacket?.Invoke(copy, (IPEndPoint)res.RemoteEndPoint);

                    if (!AllowBtoA) continue;

                    int? customDelay = CustomDelayBtoAForPacket?.Invoke(copy, seqIdx);
                    int d = customDelay ?? DelayBtoAMs;
                    int dup = Math.Max(1, DuplicateBtoACount);

                    async Task SendCopiesAsync()
                    {
                        for (int i = 0; i < dup; i++)
                        {
                            try { await _proxyFacingA.SendToAsync(copy, SocketFlags.None, _realPeerA, _cts.Token).ConfigureAwait(false); } catch { }
                        }
                    }

                    if (d > 0)
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(d, _cts.Token).ConfigureAwait(false);
                                await SendCopiesAsync().ConfigureAwait(false);
                            }
                            catch { }
                        });
                    }
                    else
                    {
                        await SendCopiesAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _proxyFacingA.Dispose(); } catch { }
            try { _proxyFacingB.Dispose(); } catch { }
            try { Task.WaitAll(new[] { _loopAtoB, _loopBtoA }, 500); } catch { }
            _cts.Dispose();
        }
    }

    private (CandidateGatheringResult gathering, Socket socket, GatheredSocketBinding binding, IceCandidate localCand) CreateBoundPeer(
        string peerTag,
        string localIp,
        string ifaceId = "eth-1",
        IceCandidateType localType = IceCandidateType.Host,
        int metric = 25,
        string? advertisedPublicIp = null,
        int advertisedPublicPort = 0)
    {
        var sock = IceCandidateGatherer.CreateConfiguredUdpSocket();
        sock.Bind(new IPEndPoint(IPAddress.Parse(localIp), 0));
        var boundEp = (IPEndPoint)sock.LocalEndPoint!;

        var iface = new DiscoveredNetworkInterface
        {
            InterfaceId = ifaceId,
            InterfaceName = ifaceId,
            Description = ifaceId,
            InterfaceType = NetworkInterfaceType.Ethernet,
            LocalIPv4 = IPAddress.Parse(localIp),
            SubnetMask = IPAddress.Parse("255.255.255.0"),
            GatewayIPv4 = IPAddress.Parse("192.0.2.1"),
            InterfaceMetric = metric,
            InterfaceStatus = OperationalStatus.Up,
            HasValidRoute = true,
            AssociatedSocketId = $"sock-{peerTag}-{ifaceId}"
        };

        string pubIp = advertisedPublicIp ?? localIp;
        int pubPort = advertisedPublicPort > 0 ? advertisedPublicPort : boundEp.Port;

        var cand = new IceCandidate
        {
            CandidateId = $"cand-{peerTag}-{ifaceId}-{Guid.NewGuid().ToString("N")[..4]}",
            Type = localType,
            AddressFamily = AddressFamily.InterNetwork,
            Protocol = "UDP",
            Ip = pubIp,
            Port = pubPort,
            LocalAddress = localIp,
            LocalPort = boundEp.Port,
            PublicAddress = pubIp,
            PublicPort = pubPort,
            InterfaceId = ifaceId,
            SocketId = iface.AssociatedSocketId,
            Priority = IceCandidate.ComputePriority(localType, (ushort)Math.Clamp(60000 - metric, 1000, 65535)),
            DiscoveryTimestamp = DateTime.UtcNow,
            ValidityStatus = CandidateValidityStatus.Valid
        };

        var binding = new GatheredSocketBinding
        {
            SocketId = iface.AssociatedSocketId,
            Interface = iface,
            Socket = sock,
            LocalEndPoint = boundEp,
            IsLoopbackFallback = false
        };
        binding.Candidates.Add(cand);

        var gathering = new CandidateGatheringResult
        {
            ConnectionAttemptId = $"att-{peerTag}",
            SessionId = "test-sess",
            Interfaces = new[] { iface },
            Bindings = new[] { binding },
            Candidates = new[] { cand }
        };
        _disposables.Add(gathering);
        return (gathering, sock, binding, cand);
    }

    private NatUdpBridge CreateBridge(IPEndPoint realA, IPEndPoint realB, string ipA = "127.0.0.10", string ipB = "127.0.0.11")
    {
        var bridge = new NatUdpBridge(realA, realB, ipA, ipB);
        _disposables.Add(bridge);
        return bridge;
    }

    // ------------------------------------------------------------
    // 測試 1：單一 Candidate Pair 雙向連通性檢查並建立資料通道
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario1_SinglePair_BidirectionalCheckAndSelectionSucceeds()
    {
        var peerA = CreateBoundPeer("A", "127.0.0.2");
        var peerB = CreateBoundPeer("B", "127.0.0.3");
        var bridge = CreateBridge((IPEndPoint)peerA.socket.LocalEndPoint!, (IPEndPoint)peerB.socket.LocalEndPoint!);

        var remoteForA = new IceCandidate
        {
            CandidateId = peerB.localCand.CandidateId,
            Type = IceCandidateType.Srflx,
            Ip = bridge.EndPointForA.Address.ToString(),
            Port = bridge.EndPointForA.Port,
            Priority = IceCandidate.ComputePriority(IceCandidateType.Srflx)
        };
        var remoteForB = new IceCandidate
        {
            CandidateId = peerA.localCand.CandidateId,
            Type = IceCandidateType.Srflx,
            Ip = bridge.EndPointForB.Address.ToString(),
            Port = bridge.EndPointForB.Port,
            Priority = IceCandidate.ComputePriority(IceCandidateType.Srflx)
        };

        var tA = IceConnectivityChecker.RunAsync(peerA.gathering, new[] { remoteForA }, "att-1", "sess-1", "111000111", "222000222", isInitiator: true);
        var tB = IceConnectivityChecker.RunAsync(peerB.gathering, new[] { remoteForB }, "att-1", "sess-1", "222000222", "111000111", isInitiator: false);
        await Task.WhenAll(tA, tB);

        var resA = await tA;
        var resB = await tB;

        Assert.True(resA.Succeeded);
        Assert.True(resB.Succeeded);
        Assert.NotNull(resA.SelectedPair);
        Assert.NotNull(resB.SelectedPair);
        Assert.Equal(CandidatePairCheckState.Succeeded, resA.SelectedPair.CheckState);
        Assert.Equal(CandidatePairCheckState.Succeeded, resB.SelectedPair.CheckState);
        Assert.True(resA.SelectedPair.InboundPacketObserved || resA.SelectedPair.OutboundCheckAcked);
        Assert.True(resA.SelectedPair.OutboundCheckAcked);
        Assert.True(resB.SelectedPair.OutboundCheckAcked);
        Assert.True(resA.SelectedPair.RttMs >= 0);

        // 轉移勝出 Socket 給 ReliableUdpStream 驗證真實資料互通
        using var streamA = new ReliableUdpStream(peerA.gathering.DetachWinningSocket(resA.SelectedPair.SocketId)!, resA.SelectedRemoteEndPoint!, ownsSocket: true);
        using var streamB = new ReliableUdpStream(peerB.gathering.DetachWinningSocket(resB.SelectedPair.SocketId)!, resB.SelectedRemoteEndPoint!, ownsSocket: true);

        byte[] msg = Encoding.UTF8.GetBytes("Phase3-SinglePair-OK");
        await streamA.WriteAsync(msg);
        byte[] buf = new byte[64];
        using var rCts = new CancellationTokenSource(3000);
        int read = await streamB.ReadAsync(buf, rCts.Token);
        Assert.Equal("Phase3-SinglePair-OK", Encoding.UTF8.GetString(buf, 0, read));
    }

    // ------------------------------------------------------------
    // 測試 2：多個 Pair 同時成功 -> 嚴格依 PairPriority 選定高優先配對（而非單憑誰先回傳）
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario2_MultiplePairsSucceedSimultaneously_SelectsHighestPriorityOverFasterLowerPair()
    {
        var sockA1 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockA1.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        var sockA2 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockA2.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.3"), 0));
        var sockB1 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockB1.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.4"), 0));
        var sockB2 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockB2.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.5"), 0));

        // 刻意將較高優先權的 Pair 1 (Host <-> Host, Metric 10) 加上 30ms 延遲，讓低優先權 Pair 2 (Srflx, Metric 60) 先回應
        var bridge1 = CreateBridge((IPEndPoint)sockA1.LocalEndPoint!, (IPEndPoint)sockB1.LocalEndPoint!, "127.0.0.20", "127.0.0.21");
        bridge1.DelayAtoBMs = 15;
        bridge1.DelayBtoAMs = 15;

        var bridge2 = CreateBridge((IPEndPoint)sockA2.LocalEndPoint!, (IPEndPoint)sockB2.LocalEndPoint!, "127.0.0.22", "127.0.0.23");

        var (gA, cA1, cA2) = BuildDualBindingGather("A", sockA1, sockA2, IceCandidateType.Host, IceCandidateType.Srflx, 10, 60);
        var (gB, cB1, cB2) = BuildDualBindingGather("B", sockB1, sockB2, IceCandidateType.Host, IceCandidateType.Srflx, 10, 60);

        var remoteForA = new[]
        {
            CloneWithEp(cB1, bridge1.EndPointForA),
            CloneWithEp(cB2, bridge2.EndPointForA)
        };
        var remoteForB = new[]
        {
            CloneWithEp(cA1, bridge1.EndPointForB),
            CloneWithEp(cA2, bridge2.EndPointForB)
        };

        var opt = new IceConnectivityOptions { SelectionGraceWindowMs = 150, CheckTimeoutMs = 3000 };
        var tA = IceConnectivityChecker.RunAsync(gA, remoteForA, "att-2", "sess-multi", "111000111", "222000222", isInitiator: true, opt);
        var tB = IceConnectivityChecker.RunAsync(gB, remoteForB, "att-2", "sess-multi", "222000222", "111000111", isInitiator: false, opt);
        await Task.WhenAll(tA, tB);

        var resA = await tA;
        var resB = await tB;

        Assert.True(resA.Succeeded);
        Assert.True(resB.Succeeded);

        // 即使 Pair 2 回應更快，選定結果必須一致選擇高優先權的 Pair 1 (Socket A1 <-> B1)
        Assert.Equal("sock-A-eth1", resA.SelectedPair!.SocketId);
        Assert.Equal("sock-B-eth1", resB.SelectedPair!.SocketId);
        Assert.Equal(bridge1.EndPointForA, resA.SelectedRemoteEndPoint);
        Assert.Equal(bridge1.EndPointForB, resB.SelectedRemoteEndPoint);
    }

    // ------------------------------------------------------------
    // 測試 3：第一組 Pair 失敗，第二組 Pair 成功
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario3_FirstPairFails_SecondPairSucceeds()
    {
        var sockA1 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockA1.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        var sockA2 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockA2.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.3"), 0));
        var sockB1 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockB1.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.4"), 0));
        var sockB2 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockB2.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.5"), 0));

        var deadBridge = CreateBridge((IPEndPoint)sockA1.LocalEndPoint!, (IPEndPoint)sockB1.LocalEndPoint!, "127.0.0.30", "127.0.0.31");
        deadBridge.AllowAtoB = false;
        deadBridge.AllowBtoA = false; // 第一組高優先 Pair 完全黑洞阻斷

        var liveBridge = CreateBridge((IPEndPoint)sockA2.LocalEndPoint!, (IPEndPoint)sockB2.LocalEndPoint!, "127.0.0.32", "127.0.0.33");

        var (gA, cA1, cA2) = BuildDualBindingGather("A", sockA1, sockA2, IceCandidateType.Host, IceCandidateType.Srflx, 10, 50);
        var (gB, cB1, cB2) = BuildDualBindingGather("B", sockB1, sockB2, IceCandidateType.Host, IceCandidateType.Srflx, 10, 50);

        var remoteForA = new[] { CloneWithEp(cB1, deadBridge.EndPointForA), CloneWithEp(cB2, liveBridge.EndPointForA) };
        var remoteForB = new[] { CloneWithEp(cA1, deadBridge.EndPointForB), CloneWithEp(cA2, liveBridge.EndPointForB) };

        var opt = new IceConnectivityOptions { SelectionGraceWindowMs = 60, CheckTimeoutMs = 2500 };
        var tA = IceConnectivityChecker.RunAsync(gA, remoteForA, "att-3", "sess-fail1", "111000111", "222000222", isInitiator: true, opt);
        var tB = IceConnectivityChecker.RunAsync(gB, remoteForB, "att-3", "sess-fail1", "222000222", "111000111", isInitiator: false, opt);
        await Task.WhenAll(tA, tB);

        var resA = await tA;
        var resB = await tB;

        Assert.True(resA.Succeeded);
        Assert.True(resB.Succeeded);
        Assert.Equal("sock-A-eth2", resA.SelectedPair!.SocketId);
        Assert.Equal("sock-B-eth2", resB.SelectedPair!.SocketId);
    }

    // ------------------------------------------------------------
    // 測試 4：單向連通失敗（A 收到 B 封包，但 B 無法收到 A 封包 -> 不得誤判已建立 P2P）
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario4_OneWayConnectivity_AReceivesBButBCannotReceiveA_RejectedCleanly()
    {
        var peerA = CreateBoundPeer("A", "127.0.0.2");
        var peerB = CreateBoundPeer("B", "127.0.0.3");
        var oneWayBridge = CreateBridge((IPEndPoint)peerA.socket.LocalEndPoint!, (IPEndPoint)peerB.socket.LocalEndPoint!);
        oneWayBridge.AllowBtoA = true;  // B -> A 通行（A 會收到 B 的 CheckRequest）
        oneWayBridge.AllowAtoB = false; // A -> B 阻斷（B 永遠收不到 A 的 CheckRequest / CheckResponse）

        var remoteForA = new[]
        {
            new IceCandidate { CandidateId = peerB.localCand.CandidateId, Type = IceCandidateType.Srflx, Ip = oneWayBridge.EndPointForA.Address.ToString(), Port = oneWayBridge.EndPointForA.Port, Priority = 1000 }
        };
        var remoteForB = new[]
        {
            new IceCandidate { CandidateId = peerA.localCand.CandidateId, Type = IceCandidateType.Srflx, Ip = oneWayBridge.EndPointForB.Address.ToString(), Port = oneWayBridge.EndPointForB.Port, Priority = 1000 }
        };

        var opt = new IceConnectivityOptions
        {
            MaxAttemptsPerPair = 3,
            RetryIntervalsMs = new[] { 60, 80, 100 },
            CheckTimeoutMs = 1500
        };

        var tA = IceConnectivityChecker.RunAsync(peerA.gathering, remoteForA, "att-4", "sess-oneway", "111000111", "222000222", isInitiator: true, opt);
        var tB = IceConnectivityChecker.RunAsync(peerB.gathering, remoteForB, "att-4", "sess-oneway", "222000222", "111000111", isInitiator: false, opt);
        await Task.WhenAll(tA, tB);

        var resA = await tA;
        var resB = await tB;

        Assert.False(resA.Succeeded);
        Assert.False(resB.Succeeded);
        Assert.Null(resA.SelectedPair);
        Assert.Null(resB.SelectedPair);

        // 檢查狀態機精確區分「單向收到封包」與「Timeout」
        var pairA = Assert.Single(resA.AllPairs);
        Assert.True(pairA.InboundPacketObserved);
        Assert.False(pairA.OutboundCheckAcked);
        Assert.Equal(CandidatePairCheckState.OneWayReceived, pairA.CheckState);
        Assert.Equal(CandidatePairFailureReason.OneWayOnly, pairA.FailureReason);

        var pairB = Assert.Single(resB.AllPairs);
        Assert.False(pairB.InboundPacketObserved);
        Assert.Equal(CandidatePairCheckState.TimedOut, pairB.CheckState);
        Assert.Equal(CandidatePairFailureReason.Timeout, pairB.FailureReason);
    }

    // ------------------------------------------------------------
    // 測試 5：多組 Pair 同時發送連通性檢查 (2x2 = 4 Pairs 併發交叉配對無錯亂)
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario5_ConcurrentCrossPairs_ChecksAllValidCombinationsWithoutCrosstalk()
    {
        var sockA1 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockA1.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        var sockA2 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockA2.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.3"), 0));
        var sockB1 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockB1.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.4"), 0));
        var sockB2 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockB2.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.5"), 0));

        var (gA, cA1, cA2) = BuildDualBindingGather("A", sockA1, sockA2, IceCandidateType.Host, IceCandidateType.Host, 20, 30);
        var (gB, cB1, cB2) = BuildDualBindingGather("B", sockB1, sockB2, IceCandidateType.Host, IceCandidateType.Host, 20, 30);

        // 設定 PacingIntervalMs = 0 讓 4 組交叉配對於首波即同時併發送出 CheckRequest，最後按 Priority 選定 Pair 1
        var opt = new IceConnectivityOptions { PacingIntervalMs = 0, SelectionGraceWindowMs = 150, CheckTimeoutMs = 3000 };
        var tA = IceConnectivityChecker.RunAsync(gA, new[] { cB1, cB2 }, "att-5", "sess-cross", "111000111", "222000222", isInitiator: true, opt);
        var tB = IceConnectivityChecker.RunAsync(gB, new[] { cA1, cA2 }, "att-5", "sess-cross", "222000222", "111000111", isInitiator: false, opt);
        await Task.WhenAll(tA, tB);

        var resA = await tA;
        var resB = await tB;

        Assert.True(resA.Succeeded);
        Assert.True(resB.Succeeded);
        Assert.Equal(4, resA.AllPairs.Count);
        Assert.Equal(4, resB.AllPairs.Count);
        Assert.All(resA.AllPairs, p => Assert.True(p.CheckAttempts >= 1));
        Assert.Contains(resA.AllPairs, p => p.CheckState == CandidatePairCheckState.Succeeded);
        Assert.Equal("sock-A-eth1", resA.SelectedPair!.SocketId);
        Assert.Equal("sock-B-eth1", resB.SelectedPair!.SocketId);
    }

    // ------------------------------------------------------------
    // 測試 6：重複 CheckResponse 回應去重
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario6_DuplicateCheckResponses_DeduplicatedCleanly()
    {
        var peerA = CreateBoundPeer("A", "127.0.0.2");
        var peerB = CreateBoundPeer("B", "127.0.0.3");
        var bridge = CreateBridge((IPEndPoint)peerA.socket.LocalEndPoint!, (IPEndPoint)peerB.socket.LocalEndPoint!);
        bridge.DuplicateBtoACount = 3; // 每次 B -> A 封包複製 3 份

        var remA = new[] { new IceCandidate { CandidateId = peerB.localCand.CandidateId, Type = IceCandidateType.Srflx, Ip = bridge.EndPointForA.Address.ToString(), Port = bridge.EndPointForA.Port, Priority = 1000 } };
        var remB = new[] { new IceCandidate { CandidateId = peerA.localCand.CandidateId, Type = IceCandidateType.Srflx, Ip = bridge.EndPointForB.Address.ToString(), Port = bridge.EndPointForB.Port, Priority = 1000 } };

        var tA = IceConnectivityChecker.RunAsync(peerA.gathering, remA, "att-6", "sess-dup", "111000111", "222000222", isInitiator: true);
        var tB = IceConnectivityChecker.RunAsync(peerB.gathering, remB, "att-6", "sess-dup", "222000222", "111000111", isInitiator: false);
        await Task.WhenAll(tA, tB);

        var resA = await tA;
        var resB = await tB;

        Assert.True(resA.Succeeded);
        Assert.True(resB.Succeeded);
        Assert.True(resA.DuplicateResponsesIgnored >= 1 || resA.SelectedPair!.DuplicateResponseCount >= 1);
    }

    // ------------------------------------------------------------
    // 測試 7：延遲回應處理（Attempt 1 回應於 Attempt 2 送出後才遲到 -> 透過 TransactionId 精準配對成功）
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario7_DelayedAttempt1ResponseAfterAttempt2Sent_StillMatchesAndSucceeds()
    {
        var peerA = CreateBoundPeer("A", "127.0.0.2");
        var peerB = CreateBoundPeer("B", "127.0.0.3");
        var bridge = CreateBridge((IPEndPoint)peerA.socket.LocalEndPoint!, (IPEndPoint)peerB.socket.LocalEndPoint!);

        // B 的第 1 筆封包刻意延遲 180ms（超過 RetryInterval 70ms），後續重傳封包正常抵達
        bridge.CustomDelayBtoAForPacket = (_, seq) => seq == 1 ? 180 : 0;

        var remA = new[] { new IceCandidate { CandidateId = peerB.localCand.CandidateId, Type = IceCandidateType.Srflx, Ip = bridge.EndPointForA.Address.ToString(), Port = bridge.EndPointForA.Port, Priority = 1000 } };
        var remB = new[] { new IceCandidate { CandidateId = peerA.localCand.CandidateId, Type = IceCandidateType.Srflx, Ip = bridge.EndPointForB.Address.ToString(), Port = bridge.EndPointForB.Port, Priority = 1000 } };

        var opt = new IceConnectivityOptions
        {
            MaxAttemptsPerPair = 4,
            RetryIntervalsMs = new[] { 70, 120, 200 },
            CheckTimeoutMs = 2500
        };

        var tA = IceConnectivityChecker.RunAsync(peerA.gathering, remA, "att-7", "sess-delay", "111000111", "222000222", isInitiator: true, opt);
        var tB = IceConnectivityChecker.RunAsync(peerB.gathering, remB, "att-7", "sess-delay", "222000222", "111000111", isInitiator: false, opt);
        await Task.WhenAll(tA, tB);

        var resA = await tA;
        var resB = await tB;

        Assert.True(resA.Succeeded);
        Assert.True(resB.Succeeded);
    }

    // ------------------------------------------------------------
    // 測試 8：舊 Session 封包與未預期來源端點回應 -> 必須精準拒絕並保持通道安全性
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario8_OldSessionAndSpoofedSourceResponses_RejectedWithoutPollutingCurrentSession()
    {
        var peerA = CreateBoundPeer("A", "127.0.0.2");
        var peerB = CreateBoundPeer("B", "127.0.0.3");

        // 設定高延遲讓注入封包先抵達
        var bridge = CreateBridge((IPEndPoint)peerA.socket.LocalEndPoint!, (IPEndPoint)peerB.socket.LocalEndPoint!);
        bridge.DelayBtoAMs = 120;

        var remA = new[] { new IceCandidate { CandidateId = peerB.localCand.CandidateId, Type = IceCandidateType.Srflx, Ip = bridge.EndPointForA.Address.ToString(), Port = bridge.EndPointForA.Port, Priority = 1000 } };
        var remB = new[] { new IceCandidate { CandidateId = peerA.localCand.CandidateId, Type = IceCandidateType.Srflx, Ip = bridge.EndPointForB.Address.ToString(), Port = bridge.EndPointForB.Port, Priority = 1000 } };

        // 另開一個未知來源 Socket，模擬舊 Session 攻擊/殘留封包直接打入 Peer A
        using var rogueSock = IceCandidateGatherer.CreateConfiguredUdpSocket();
        rogueSock.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.50"), 0));

        _ = Task.Run(async () =>
        {
            await Task.Delay(25);
            byte[] oldSessionReq = IceCheckCodec.Serialize(
                IceCheckMessageType.CheckRequest,
                isControlling: false,
                useCandidate: false,
                roleConflictRejected: false,
                attemptNumber: 1,
                transactionId: IceCheckCodec.CreateTransactionId(),
                tieBreaker: 1,
                priority: 9999,
                observedEndPoint: null,
                sessionId: "OLD-SESSION-999",
                senderDeviceId: "222000222",
                receiverDeviceId: "111000111",
                localCandidateId: peerB.localCand.CandidateId,
                remoteCandidateId: peerA.localCand.CandidateId,
                pairId: "old-pair");
            await rogueSock.SendToAsync(oldSessionReq, SocketFlags.None, peerA.socket.LocalEndPoint!);

            // 同 Session 但來源 IP:Port 與預期對端不符的偽造 NominateRequest
            byte[] spoofedNom = IceCheckCodec.Serialize(
                IceCheckMessageType.NominateRequest,
                isControlling: true,
                useCandidate: true,
                roleConflictRejected: false,
                attemptNumber: 1,
                transactionId: IceCheckCodec.CreateTransactionId(),
                tieBreaker: ulong.MaxValue,
                priority: 9999,
                observedEndPoint: null,
                sessionId: "sess-current",
                senderDeviceId: "222000222",
                receiverDeviceId: "111000111",
                localCandidateId: peerB.localCand.CandidateId,
                remoteCandidateId: peerA.localCand.CandidateId,
                pairId: "spoof-pair");
            await rogueSock.SendToAsync(spoofedNom, SocketFlags.None, peerA.socket.LocalEndPoint!);
        });

        var tA = IceConnectivityChecker.RunAsync(peerA.gathering, remA, "att-8", "sess-current", "111000111", "222000222", isInitiator: true);
        var tB = IceConnectivityChecker.RunAsync(peerB.gathering, remB, "att-8", "sess-current", "222000222", "111000111", isInitiator: false);
        await Task.WhenAll(tA, tB);

        var resA = await tA;
        var resB = await tB;

        Assert.True(resA.Succeeded);
        Assert.True(resB.Succeeded);
        Assert.True(resA.InvalidSessionPacketsRejected >= 1);
        Assert.True(resA.InvalidPacketsRejected >= 1);
        Assert.Equal(bridge.EndPointForA, resA.SelectedRemoteEndPoint);
    }

    // ------------------------------------------------------------
    // 測試 9：Pair 檢查期間其中一個 Socket 中斷 -> 該介面 Pair 標記 Invalidated，其餘介面仍成功選定
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario9_SocketDropsMidCheck_InvalidatesBrokenSocketAndSucceedsOnHealthySocket()
    {
        var sockA1 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockA1.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        var sockA2 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockA2.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.3"), 0));
        var sockB1 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockB1.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.4"), 0));
        var sockB2 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockB2.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.5"), 0));

        var bridge1 = CreateBridge((IPEndPoint)sockA1.LocalEndPoint!, (IPEndPoint)sockB1.LocalEndPoint!, "127.0.0.40", "127.0.0.41");
        bridge1.DelayAtoBMs = 200;
        bridge1.DelayBtoAMs = 200;
        var bridge2 = CreateBridge((IPEndPoint)sockA2.LocalEndPoint!, (IPEndPoint)sockB2.LocalEndPoint!, "127.0.0.42", "127.0.0.43");
        bridge2.DelayAtoBMs = 70;
        bridge2.DelayBtoAMs = 70;

        var (gA, cA1, cA2) = BuildDualBindingGather("A", sockA1, sockA2, IceCandidateType.Host, IceCandidateType.Srflx, 10, 40);
        var (gB, cB1, cB2) = BuildDualBindingGather("B", sockB1, sockB2, IceCandidateType.Host, IceCandidateType.Srflx, 10, 40);

        var remA = new[] { CloneWithEp(cB1, bridge1.EndPointForA), CloneWithEp(cB2, bridge2.EndPointForA) };
        var remB = new[] { CloneWithEp(cA1, bridge1.EndPointForB), CloneWithEp(cA2, bridge2.EndPointForB) };

        // 在檢查開始 40ms 後模擬 sockA1 中斷 Dispose
        _ = Task.Run(async () =>
        {
            await Task.Delay(40);
            try { sockA1.Dispose(); } catch { }
        });

        var opt = new IceConnectivityOptions { SelectionGraceWindowMs = 60, CheckTimeoutMs = 3000 };
        var tA = IceConnectivityChecker.RunAsync(gA, remA, "att-9", "sess-drop", "111000111", "222000222", isInitiator: true, opt);
        var tB = IceConnectivityChecker.RunAsync(gB, remB, "att-9", "sess-drop", "222000222", "111000111", isInitiator: false, opt);
        await Task.WhenAll(tA, tB);

        var resA = await tA;
        var resB = await tB;

        Assert.True(resA.Succeeded);
        Assert.True(resB.Succeeded);
        Assert.Equal("sock-A-eth2", resA.SelectedPair!.SocketId);
        Assert.Equal("sock-B-eth2", resB.SelectedPair!.SocketId);

        var sock1Pairs = resA.AllPairs.Where(p => p.SocketId == "sock-A-eth1").ToList();
        Assert.NotEmpty(sock1Pairs);
        Assert.All(sock1Pairs, p =>
        {
            Assert.Equal(CandidatePairCheckState.Invalidated, p.CheckState);
            Assert.Equal(CandidatePairFailureReason.LocalSocketDisposed, p.FailureReason);
        });
    }

    // ------------------------------------------------------------
    // 測試 10：雙方同時嘗試選定不同 Pair 的競爭情境（TieBreaker 角色仲裁收斂至唯一一致配對）
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario10_DualControllingCompetingSelectionRace_ConvergesToSingleMatchingPair()
    {
        var sockA1 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockA1.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        var sockA2 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockA2.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.3"), 0));
        var sockB1 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockB1.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.4"), 0));
        var sockB2 = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockB2.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.5"), 0));

        var bridge1 = CreateBridge((IPEndPoint)sockA1.LocalEndPoint!, (IPEndPoint)sockB1.LocalEndPoint!, "127.0.0.60", "127.0.0.61");
        var bridge2 = CreateBridge((IPEndPoint)sockA2.LocalEndPoint!, (IPEndPoint)sockB2.LocalEndPoint!, "127.0.0.62", "127.0.0.63");

        var (gA, cA1, cA2) = BuildDualBindingGather("A", sockA1, sockA2, IceCandidateType.Host, IceCandidateType.Host, 20, 30);
        var (gB, cB1, cB2) = BuildDualBindingGather("B", sockB1, sockB2, IceCandidateType.Host, IceCandidateType.Host, 20, 30);

        var remA = new[] { CloneWithEp(cB1, bridge1.EndPointForA), CloneWithEp(cB2, bridge2.EndPointForA) };
        var remB = new[] { CloneWithEp(cA1, bridge1.EndPointForB), CloneWithEp(cA2, bridge2.EndPointForB) };

        // 強制雙端皆以 Controlling 啟動：Peer A 想選 eth1 (Tie=100)，Peer B 透過近期路徑提示想選 eth2 (Tie=999)
        var optA = new IceConnectivityOptions
        {
            ForceRole = IceRole.Controlling,
            OverrideTieBreaker = 100UL,
            SelectionGraceWindowMs = 60,
            CheckTimeoutMs = 3000,
            RecentSuccessHints = new[]
            {
                new RecentSuccessfulPairHint("222000222", "eth1", "127.0.0.2", bridge1.EndPointForA.Address.ToString(), IceCandidateType.Host, IceCandidateType.Host, DateTime.UtcNow)
            }
        };
        var optB = new IceConnectivityOptions
        {
            ForceRole = IceRole.Controlling,
            OverrideTieBreaker = 999UL,
            SelectionGraceWindowMs = 60,
            CheckTimeoutMs = 3000,
            RecentSuccessHints = new[]
            {
                new RecentSuccessfulPairHint("111000111", "eth2", "127.0.0.5", bridge2.EndPointForB.Address.ToString(), IceCandidateType.Host, IceCandidateType.Host, DateTime.UtcNow)
            }
        };

        var tA = IceConnectivityChecker.RunAsync(gA, remA, "att-10", "sess-race", "111000111", "222000222", isInitiator: true, optA);
        var tB = IceConnectivityChecker.RunAsync(gB, remB, "att-10", "sess-race", "222000222", "111000111", isInitiator: true, optB);
        await Task.WhenAll(tA, tB);

        var resA = await tA;
        var resB = await tB;

        Assert.True(resA.Succeeded);
        Assert.True(resB.Succeeded);
        Assert.True(resA.RoleConflictCount + resB.RoleConflictCount >= 1);

        // 仲裁結果必須讓 Peer A 讓步至 Peer B (Tie=999) 所選定的 eth2 (bridge2)，不可雙端選不同 Pair
        Assert.Equal("sock-A-eth2", resA.SelectedPair!.SocketId);
        Assert.Equal("sock-B-eth2", resB.SelectedPair!.SocketId);
        Assert.Equal(bridge2.EndPointForA, resA.SelectedRemoteEndPoint);
        Assert.Equal(bridge2.EndPointForB, resB.SelectedRemoteEndPoint);
    }

    // ------------------------------------------------------------
    // 測試 11：移除固定 Port ±1..±8 猜測作為核心策略之驗證（預設禁止盲掃，顯式備援才記錄貢獻）
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario11_FixedPortGuessingRemovedFromCoreStrategy_OnlyOptionalBoundedFallbackWithTelemetry()
    {
        var peerA = CreateBoundPeer("A", "127.0.0.2");
        var sockB = IceCandidateGatherer.CreateConfiguredUdpSocket();
        // Peer B 故意綁在預設宣告埠 +2 以外的位置：建構橋接器使 B 的真正窗口比宣告 Srflx 埠高 +2
        sockB.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.3"), 0));
        var sockBEp = (IPEndPoint)sockB.LocalEndPoint!;
        var bridge = CreateBridge((IPEndPoint)peerA.socket.LocalEndPoint!, sockBEp, "127.0.0.70", "127.0.0.71");

        // 宣告給 A 的 Port 比實際 Bridge 監聽埠少 2 (模擬舊式 ±2 猜測才能矇到的情境)
        int unadvertisedDelta = 2;
        var advertisedWrongCandB = new IceCandidate
        {
            CandidateId = "cand-B-srflx-shifted",
            Type = IceCandidateType.Srflx,
            Ip = bridge.EndPointForA.Address.ToString(),
            Port = bridge.EndPointForA.Port - unadvertisedDelta,
            LocalAddress = "127.0.0.3",
            LocalPort = sockBEp.Port,
            InterfaceId = "eth-b",
            SocketId = "sock-B-shift",
            Priority = IceCandidate.ComputePriority(IceCandidateType.Srflx),
            DiscoveryTimestamp = DateTime.UtcNow,
            ValidityStatus = CandidateValidityStatus.Valid
        };

        var gB = new CandidateGatheringResult
        {
            ConnectionAttemptId = "att-11b",
            SessionId = "sess-nopredict",
            Bindings = new[]
            {
                new GatheredSocketBinding
                {
                    SocketId = "sock-B-shift",
                    Interface = new DiscoveredNetworkInterface
                    {
                        InterfaceId = "eth-b",
                        InterfaceName = "eth-b",
                        LocalIPv4 = IPAddress.Parse("127.0.0.3"),
                        SubnetMask = IPAddress.Parse("255.255.255.0"),
                        InterfaceMetric = 25,
                        InterfaceStatus = OperationalStatus.Up,
                        HasValidRoute = true,
                        AssociatedSocketId = "sock-B-shift"
                    },
                    Socket = sockB,
                    LocalEndPoint = sockBEp,
                    Candidates = { advertisedWrongCandB }
                }
            },
            Candidates = new[] { advertisedWrongCandB }
        };
        _disposables.Add(gB);

        var remB = new[]
        {
            new IceCandidate
            {
                CandidateId = peerA.localCand.CandidateId,
                Type = IceCandidateType.Srflx,
                Ip = bridge.EndPointForB.Address.ToString(),
                Port = bridge.EndPointForB.Port,
                Priority = IceCandidate.ComputePriority(IceCandidateType.Srflx)
            }
        };

        // 1. 預設策略 (AllowRestrictedPortPredictionFallback = false)：只形成 1 組直接 CandidatePair，不應掃描 +2 成功
        var defaultOpt = new IceConnectivityOptions
        {
            MaxAttemptsPerPair = 2,
            RetryIntervalsMs = new[] { 60 },
            CheckTimeoutMs = 400,
            AllowRestrictedPortPredictionFallback = false
        };
        var resNoFallback = await IceConnectivityChecker.RunAsync(peerA.gathering, new[] { advertisedWrongCandB }, "att-11", "sess-nopredict", "111000111", "222000222", isInitiator: true, defaultOpt);
        Assert.False(resNoFallback.Succeeded);
        Assert.Single(resNoFallback.AllPairs);
        Assert.Equal(0, resNoFallback.LegacyPortPredictionAttempts);
        Assert.Equal(0, resNoFallback.LegacyPortPredictionSuccesses);

        // 2. 明確啟用受控有限埠預測備援時，先檢直接 Pair 失敗後才啟動有限備援並精確紀錄貢獻度
        var optA = new IceConnectivityOptions
        {
            MaxAttemptsPerPair = 2,
            RetryIntervalsMs = new[] { 50 },
            CheckTimeoutMs = 300,
            AllowRestrictedPortPredictionFallback = true,
            RestrictedPortPredictionMaxDelta = 2
        };
        var optB = new IceConnectivityOptions
        {
            MaxAttemptsPerPair = 8,
            RetryIntervalsMs = new[] { 80, 120, 160, 200, 250, 300 },
            CheckTimeoutMs = 2500
        };

        var tA = IceConnectivityChecker.RunAsync(peerA.gathering, new[] { advertisedWrongCandB }, "att-11fb", "sess-fallback", "111000111", "222000222", isInitiator: true, optA);
        var tB = IceConnectivityChecker.RunAsync(gB, remB, "att-11fb", "sess-fallback", "222000222", "111000111", isInitiator: false, optB);
        await Task.WhenAll(tA, tB);

        var resA = await tA;
        var resB = await tB;

        Assert.True(resA.Succeeded);
        Assert.True(resB.Succeeded);
        Assert.True(resA.SelectedViaPortPrediction);
        Assert.True(resA.LegacyPortPredictionAttempts >= 1);
        Assert.Equal(1, resA.LegacyPortPredictionSuccesses);
        Assert.Equal(bridge.EndPointForA, resA.SelectedRemoteEndPoint);
    }

    // ------------------------------------------------------------
    // 測試 13：ReliableUdpStream 接管勝出 Socket 後仍可正確處理同 Session 晚期 Check / Nominate 並過濾非法封包
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario13_PostPunchReliableUdpStream_RepliesLateValidRdicWithoutCorruptingDataStream()
    {
        using var sockA = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockA.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var sockB = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockB.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        var epA = (IPEndPoint)sockA.LocalEndPoint!;
        var epB = (IPEndPoint)sockB.LocalEndPoint!;

        var ctxB = new IceStreamPostPunchContext
        {
            SessionId = "sess-post",
            SelfDeviceId = "222000222",
            RemoteDeviceId = "111000111",
            SelectedLocalCandidateId = "cand-B",
            SelectedRemoteCandidateId = "cand-A",
            SelectedPairId = "pair-post",
            IsControlling = false,
            TieBreaker = 555
        };

        using var streamB = new ReliableUdpStream(sockB, epA, ownsSocket: false, postPunchContext: ctxB);

        // 送出同 Session 晚期 NominateRequest 至已建立的 streamB
        byte[] lateTxn = IceCheckCodec.CreateTransactionId();
        byte[] lateNom = IceCheckCodec.Serialize(
            IceCheckMessageType.NominateRequest,
            isControlling: true,
            useCandidate: true,
            roleConflictRejected: false,
            attemptNumber: 1,
            transactionId: lateTxn,
            tieBreaker: 100,
            priority: 1000,
            observedEndPoint: null,
            sessionId: "sess-post",
            senderDeviceId: "111000111",
            receiverDeviceId: "222000222",
            localCandidateId: "cand-A",
            remoteCandidateId: "cand-B",
            pairId: "pair-post");
        await sockA.SendToAsync(lateNom, SocketFlags.None, epB);

        // 驗證 streamB 主動補回相同 TxnId 的 NominateAck
        byte[] recvBuf = new byte[512];
        using var rCts = new CancellationTokenSource(1500);
        var recv = await sockA.ReceiveFromAsync(recvBuf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), rCts.Token);
        Assert.True(IceCheckCodec.TryParse(recvBuf.AsSpan(0, recv.ReceivedBytes), out var ackMsg));
        Assert.Equal(IceCheckMessageType.NominateAck, ackMsg.MessageType);
        Assert.Equal(Convert.ToHexString(lateTxn), ackMsg.TransactionIdHex);

        // 同時送入過期/舊 Session RDIC 封包後，建立正常 streamA 寫入應用層資料不得受干擾
        byte[] staleRdic = IceCheckCodec.Serialize(
            IceCheckMessageType.CheckRequest,
            isControlling: true,
            useCandidate: false,
            roleConflictRejected: false,
            attemptNumber: 1,
            transactionId: IceCheckCodec.CreateTransactionId(),
            tieBreaker: 1,
            priority: 1,
            observedEndPoint: null,
            sessionId: "stale-old-session",
            senderDeviceId: "111000111",
            receiverDeviceId: "222000222",
            localCandidateId: "x",
            remoteCandidateId: "y",
            pairId: "p");
        await sockA.SendToAsync(staleRdic, SocketFlags.None, epB);

        using var streamA = new ReliableUdpStream(sockA, epB, ownsSocket: false);
        byte[] appBytes = Encoding.UTF8.GetBytes("CleanPayload");
        await streamA.WriteAsync(appBytes);

        byte[] appRecv = new byte[64];
        int appLen = await streamB.ReadAsync(appRecv, rCts.Token);
        Assert.Equal("CleanPayload", Encoding.UTF8.GetString(appRecv, 0, appLen));
    }

    // ------------------------------------------------------------
    // 測試 12：候選過期淘汰、近期成功優先與多介面均衡排程驗證
    // ------------------------------------------------------------
    [Fact]
    public void Scenario12_CandidatePairBuilder_RejectsExpiredAndRanksRecentSuccessThenSameSubnetThenMultiWan()
    {
        var now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var sockEth = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockEth.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
        var sockWan = IceCandidateGatherer.CreateConfiguredUdpSocket(); sockWan.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.3"), 0));

        var ifaceEth = new DiscoveredNetworkInterface
        {
            InterfaceId = "eth-lan", InterfaceName = "Ethernet", LocalIPv4 = IPAddress.Parse("192.168.10.20"),
            SubnetMask = IPAddress.Parse("255.255.255.0"), InterfaceMetric = 20, InterfaceStatus = OperationalStatus.Up, HasValidRoute = true, AssociatedSocketId = "sock-eth"
        };
        var ifaceWan = new DiscoveredNetworkInterface
        {
            InterfaceId = "wan-2", InterfaceName = "WAN-2", LocalIPv4 = IPAddress.Parse("10.20.30.2"),
            SubnetMask = IPAddress.Parse("255.255.255.0"), InterfaceMetric = 35, InterfaceStatus = OperationalStatus.Up, HasValidRoute = true, AssociatedSocketId = "sock-wan"
        };

        var candHostSameLan = new IceCandidate { CandidateId = "loc-lan", Type = IceCandidateType.Host, Ip = "192.168.10.20", Port = 50001, SocketId = "sock-eth", InterfaceId = "eth-lan", Priority = IceCandidate.ComputePriority(IceCandidateType.Host), DiscoveryTimestamp = now };
        var candSrflxWan2 = new IceCandidate { CandidateId = "loc-wan2", Type = IceCandidateType.Srflx, Ip = "198.51.100.2", Port = 50002, SocketId = "sock-wan", InterfaceId = "wan-2", Priority = IceCandidate.ComputePriority(IceCandidateType.Srflx), DiscoveryTimestamp = now };
        var candExpired = new IceCandidate { CandidateId = "loc-old", Type = IceCandidateType.Host, Ip = "192.168.10.20", Port = 50003, SocketId = "sock-eth", InterfaceId = "eth-lan", Priority = 999999999, DiscoveryTimestamp = now.AddMinutes(-10) };

        var gather = new CandidateGatheringResult
        {
            ConnectionAttemptId = "att-12",
            SessionId = "sess-build",
            Interfaces = new[] { ifaceEth, ifaceWan },
            Bindings = new[]
            {
                new GatheredSocketBinding { SocketId = "sock-eth", Interface = ifaceEth, Socket = sockEth, LocalEndPoint = (IPEndPoint)sockEth.LocalEndPoint!, Candidates = { candHostSameLan, candExpired } },
                new GatheredSocketBinding { SocketId = "sock-wan", Interface = ifaceWan, Socket = sockWan, LocalEndPoint = (IPEndPoint)sockWan.LocalEndPoint!, Candidates = { candSrflxWan2 } }
            },
            Candidates = new[] { candHostSameLan, candSrflxWan2, candExpired }
        };
        _disposables.Add(gather);

        var remSameLanHost = new IceCandidate { CandidateId = "rem-lan", Type = IceCandidateType.Host, Ip = "192.168.10.55", Port = 60001, Priority = IceCandidate.ComputePriority(IceCandidateType.Host), DiscoveryTimestamp = now };
        var remRemoteSrflx = new IceCandidate { CandidateId = "rem-wan", Type = IceCandidateType.Srflx, Ip = "203.0.113.9", Port = 60002, Priority = IceCandidate.ComputePriority(IceCandidateType.Srflx), DiscoveryTimestamp = now };
        var remExpired = new IceCandidate { CandidateId = "rem-exp", Type = IceCandidateType.Srflx, Ip = "203.0.113.9", Port = 60003, Priority = 9999, DiscoveryTimestamp = now.AddMinutes(-15) };

        var hints = new[]
        {
            new RecentSuccessfulPairHint("333000333", "wan-2", "10.20.30.2", "203.0.113.9", IceCandidateType.Srflx, IceCandidateType.Srflx, now.AddMinutes(-1))
        };

        var plan = CandidatePairBuilder.BuildPairs(
            gather,
            new[] { remSameLanHost, remRemoteSrflx, remExpired },
            remoteDeviceId: "333000333",
            localRole: IceRole.Controlling,
            options: new IceConnectivityOptions { MaxCandidateAgeSeconds = 120, UtcNowProvider = () => now, RecentSuccessHints = hints });

        Assert.Equal(1, plan.ExpiredLocalCandidatesSkipped);
        Assert.Equal(1, plan.ExpiredRemoteCandidatesSkipped);
        Assert.Equal(4, plan.PriorityOrderedPairs.Count);

        // 第一名必須為近期已知成功提示的 WAN-2 Srflx<->Srflx 配對
        Assert.True(plan.PriorityOrderedPairs[0].IsRecentSuccessHint);
        Assert.Equal("loc-wan2", plan.PriorityOrderedPairs[0].LocalCandidateId);
        Assert.Equal("rem-wan", plan.PriorityOrderedPairs[0].RemoteCandidateId);

        // 第二名必須為同子網 LAN Host<->Host
        Assert.True(plan.PriorityOrderedPairs[1].IsSameSubnetHost);
        Assert.Equal("loc-lan", plan.PriorityOrderedPairs[1].LocalCandidateId);
        Assert.Equal("rem-lan", plan.PriorityOrderedPairs[1].RemoteCandidateId);
    }

    private (CandidateGatheringResult gather, IceCandidate cand1, IceCandidate cand2) BuildDualBindingGather(
        string tag,
        Socket sock1,
        Socket sock2,
        IceCandidateType type1,
        IceCandidateType type2,
        int metric1,
        int metric2)
    {
        var ep1 = (IPEndPoint)sock1.LocalEndPoint!;
        var ep2 = (IPEndPoint)sock2.LocalEndPoint!;

        var iface1 = new DiscoveredNetworkInterface
        {
            InterfaceId = "eth1", InterfaceName = "eth1", LocalIPv4 = ep1.Address, SubnetMask = IPAddress.Parse("255.255.255.0"),
            InterfaceMetric = metric1, InterfaceStatus = OperationalStatus.Up, HasValidRoute = true, AssociatedSocketId = $"sock-{tag}-eth1"
        };
        var iface2 = new DiscoveredNetworkInterface
        {
            InterfaceId = "eth2", InterfaceName = "eth2", LocalIPv4 = ep2.Address, SubnetMask = IPAddress.Parse("255.255.255.0"),
            InterfaceMetric = metric2, InterfaceStatus = OperationalStatus.Up, HasValidRoute = true, AssociatedSocketId = $"sock-{tag}-eth2"
        };

        var c1 = new IceCandidate
        {
            CandidateId = $"cand-{tag}-1", Type = type1, Ip = ep1.Address.ToString(), Port = ep1.Port,
            LocalAddress = ep1.Address.ToString(), LocalPort = ep1.Port, PublicAddress = ep1.Address.ToString(), PublicPort = ep1.Port,
            InterfaceId = "eth1", SocketId = iface1.AssociatedSocketId, Priority = IceCandidate.ComputePriority(type1, (ushort)(60000 - metric1)), DiscoveryTimestamp = DateTime.UtcNow
        };
        var c2 = new IceCandidate
        {
            CandidateId = $"cand-{tag}-2", Type = type2, Ip = ep2.Address.ToString(), Port = ep2.Port,
            LocalAddress = ep2.Address.ToString(), LocalPort = ep2.Port, PublicAddress = ep2.Address.ToString(), PublicPort = ep2.Port,
            InterfaceId = "eth2", SocketId = iface2.AssociatedSocketId, Priority = IceCandidate.ComputePriority(type2, (ushort)(60000 - metric2)), DiscoveryTimestamp = DateTime.UtcNow
        };

        var b1 = new GatheredSocketBinding { SocketId = iface1.AssociatedSocketId, Interface = iface1, Socket = sock1, LocalEndPoint = ep1, Candidates = { c1 } };
        var b2 = new GatheredSocketBinding { SocketId = iface2.AssociatedSocketId, Interface = iface2, Socket = sock2, LocalEndPoint = ep2, Candidates = { c2 } };

        var g = new CandidateGatheringResult
        {
            ConnectionAttemptId = $"att-{tag}",
            SessionId = "test",
            Interfaces = new[] { iface1, iface2 },
            Bindings = new[] { b1, b2 },
            Candidates = new[] { c1, c2 }
        };
        _disposables.Add(g);
        return (g, c1, c2);
    }

    private static IceCandidate CloneWithEp(IceCandidate src, IPEndPoint ep) => new()
    {
        CandidateId = src.CandidateId,
        Type = src.Type,
        Ip = ep.Address.ToString(),
        Port = ep.Port,
        PublicAddress = ep.Address.ToString(),
        PublicPort = ep.Port,
        InterfaceId = src.InterfaceId,
        Priority = src.Priority,
        DiscoveryTimestamp = src.DiscoveryTimestamp,
        ValidityStatus = src.ValidityStatus
    };
}
