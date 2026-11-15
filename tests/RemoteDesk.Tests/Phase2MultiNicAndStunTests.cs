using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RemoteDesk.Net;
using Xunit;

namespace RemoteDesk.Tests;

public sealed class Phase2MultiNicAndStunTests : IDisposable
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
    /// 輕量級本機 UDP STUN 測試伺服器：
    /// 支援依來源 LocalIP / Port 自訂回傳 XOR-MAPPED-ADDRESS、延遲回應、或回傳 0x0111 BindingErrorResponse。
    /// </summary>
    private sealed class MockUdpStunServer : IDisposable
    {
        private readonly Socket _socket;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly Func<IPEndPoint, (IPEndPoint? mappedEp, int errorCode, int delayMs)> _responder;

        public IPEndPoint EndPoint { get; }
        public string AddressString => EndPoint.ToString();
        public int ReceivedRequestCount { get; private set; }

        public MockUdpStunServer(
            IPAddress bindIp,
            Func<IPEndPoint, (IPEndPoint? mappedEp, int errorCode, int delayMs)> responder)
        {
            _responder = responder;
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _socket.Bind(new IPEndPoint(bindIp, 0));
            EndPoint = (IPEndPoint)_socket.LocalEndPoint!;
            _loop = Task.Run(ServeLoopAsync);
        }

        private async Task ServeLoopAsync()
        {
            byte[] buf = new byte[512];
            EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var res = await _socket.ReceiveFromAsync(buf, SocketFlags.None, remoteEp, _cts.Token).ConfigureAwait(false);
                    if (res.ReceivedBytes < 20) continue;

                    ushort msgType = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(0, 2));
                    uint cookie = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4, 4));
                    if (msgType != StunClient.BindingRequest || cookie != StunClient.MagicCookie) continue;

                    byte[] txnId = buf.AsSpan(8, 12).ToArray();
                    var src = (IPEndPoint)res.RemoteEndPoint;
                    ReceivedRequestCount++;

                    var (mappedEp, errorCode, delayMs) = _responder(src);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, _cts.Token).ConfigureAwait(false);
                    }

                    byte[]? reply = null;
                    if (errorCode > 0)
                    {
                        reply = BuildErrorResponse(txnId, errorCode);
                    }
                    else if (mappedEp != null)
                    {
                        reply = BuildXorMappedSuccessResponse(txnId, mappedEp);
                    }

                    if (reply != null)
                    {
                        await _socket.SendToAsync(reply, SocketFlags.None, src, _cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { }
            }
        }

        private static byte[] BuildXorMappedSuccessResponse(byte[] txnId, IPEndPoint mapped)
        {
            var resp = new byte[20 + 12];
            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(0, 2), StunClient.BindingResponse);
            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2, 2), 12);
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(4, 4), StunClient.MagicCookie);
            txnId.CopyTo(resp.AsSpan(8, 12));

            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(20, 2), StunClient.AttrXorMappedAddress);
            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(22, 2), 8);
            resp[24] = 0x00;
            resp[25] = 0x01; // IPv4

            ushort xPort = (ushort)(mapped.Port ^ (ushort)(StunClient.MagicCookie >> 16));
            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(26, 2), xPort);

            uint ipUint = BinaryPrimitives.ReadUInt32BigEndian(mapped.Address.GetAddressBytes());
            uint xIp = ipUint ^ StunClient.MagicCookie;
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(28, 4), xIp);
            return resp;
        }

        private static byte[] BuildErrorResponse(byte[] txnId, int errorCode)
        {
            // ERROR-CODE attr: header (4) + reserved(2) + class(1) + number(1) + padded reason(4) = 12 bytes
            var resp = new byte[20 + 12];
            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(0, 2), StunClient.BindingErrorResponse);
            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2, 2), 12);
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(4, 4), StunClient.MagicCookie);
            txnId.CopyTo(resp.AsSpan(8, 12));

            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(20, 2), StunClient.AttrErrorCode);
            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(22, 2), 8);
            resp[26] = (byte)(errorCode / 100);
            resp[27] = (byte)(errorCode % 100);
            return resp;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _socket.Dispose(); } catch { }
            try { _loop.Wait(500); } catch { }
            _cts.Dispose();
        }
    }

    private MockUdpStunServer CreateStunServer(Func<IPEndPoint, (IPEndPoint? mappedEp, int errorCode, int delayMs)> responder)
    {
        var srv = new MockUdpStunServer(IPAddress.Loopback, responder);
        _disposables.Add(srv);
        return srv;
    }

    private static DiscoveredNetworkInterface MakeInterface(
        string id,
        string name,
        string ip,
        NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        int metric = 25,
        bool isVirtual = false,
        bool isVpn = false,
        bool hasValidRoute = true) =>
        new()
        {
            InterfaceId = id,
            InterfaceName = name,
            Description = name,
            InterfaceType = type,
            LocalIPv4 = IPAddress.Parse(ip),
            SubnetMask = IPAddress.Parse("255.255.255.0"),
            GatewayIPv4 = hasValidRoute ? IPAddress.Parse("192.0.2.1") : null,
            InterfaceMetric = metric,
            InterfaceStatus = OperationalStatus.Up,
            IsVirtual = isVirtual,
            IsVpn = isVpn,
            HasValidRoute = hasValidRoute
        };

    // ------------------------------------------------------------
    // 測試 1：單網卡、單 WAN + 多 STUN 不因第一個回應就提早停止 + Socket 一致性
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario1_SingleNic_SingleWan_GathersAllMultiStunCandidatesOnSameBoundSocket()
    {
        var stun1 = CreateStunServer(src => (new IPEndPoint(IPAddress.Parse("203.0.113.10"), 41000 + (src.Port % 1000)), 0, 5));
        var stun2 = CreateStunServer(src => (new IPEndPoint(IPAddress.Parse("203.0.113.10"), 41000 + (src.Port % 1000)), 0, 40));
        var stun3 = CreateStunServer(src => (new IPEndPoint(IPAddress.Parse("203.0.113.10"), 42000 + (src.Port % 1000)), 0, 60));

        var nic1 = MakeInterface("eth-1", "Ethernet", "127.0.0.2", metric: 20);
        using var result = await IceCandidateGatherer.GatherAsync(
            sessionId: "sess-single-wan",
            connectionAttemptId: "attempt-1",
            options: new IceGatheringOptions
            {
                OverrideInterfaces = new[] { nic1 },
                StunServers = new[] { stun1.AddressString, stun2.AddressString, stun3.AddressString },
                StunTimeoutMs = 1500,
                IncludeLoopbackFallback = false
            });

        // 不得只因為第一個 STUN 回應就停止，3 組 STUN 都要實際收到請求並有紀錄
        Assert.Equal(1, stun1.ReceivedRequestCount);
        Assert.Equal(1, stun2.ReceivedRequestCount);
        Assert.Equal(1, stun3.ReceivedRequestCount);
        Assert.Equal(3, result.StunProbes.Count);
        Assert.All(result.StunProbes, p => Assert.True(p.Succeeded));

        // 1 Host + 2 去重後的不同映射埠 Srflx (41xxx 與 42xxx)
        Assert.Single(result.Bindings);
        var binding = result.Bindings[0];
        Assert.Equal(binding.SocketId, nic1.AssociatedSocketId);
        Assert.Equal(IPAddress.Parse("127.0.0.2"), binding.LocalEndPoint.Address);

        Assert.Equal(3, result.Candidates.Count);
        Assert.Single(result.Candidates, c => c.Type == IceCandidateType.Host);
        Assert.Equal(2, result.Candidates.Count(c => c.Type == IceCandidateType.Srflx));

        // 每個 Candidate 都精確對應原本的 Socket
        foreach (var cand in result.Candidates)
        {
            Assert.Equal(binding.SocketId, cand.SocketId);
            Assert.Equal(nic1.InterfaceId, cand.InterfaceId);
            Assert.Same(binding.Socket, result.GetSocketForCandidate(cand));
            Assert.Equal("UDP", cand.Protocol);
            Assert.Equal(AddressFamily.InterNetwork, cand.AddressFamily);
            Assert.False(string.IsNullOrWhiteSpace(cand.CandidateId));
        }

        Assert.Equal(NatMappingBehavior.AddressOrPortDependent, binding.NatObservation!.Behavior);
        Assert.False(result.HasMultiWanPublicIps);
    }

    // ------------------------------------------------------------
    // 測試 2：雙網卡、單 WAN
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario2_DualNic_SingleWan_BindsIndependentSocketPerInterfaceAndSharesPublicIp()
    {
        var sharedWanStun = CreateStunServer(src =>
            (new IPEndPoint(IPAddress.Parse("198.51.100.25"), src.Address.Equals(IPAddress.Parse("127.0.0.2")) ? 51001 : 51002), 0, 10));

        var eth = MakeInterface("eth-main", "Ethernet", "127.0.0.2", metric: 20);
        var wifi = MakeInterface("wlan-sec", "Wi-Fi", "127.0.0.3", NetworkInterfaceType.Wireless80211, metric: 35);

        using var result = await IceCandidateGatherer.GatherAsync(
            sessionId: "sess-dual-nic",
            options: new IceGatheringOptions
            {
                OverrideInterfaces = new[] { eth, wifi },
                StunServers = new[] { sharedWanStun.AddressString },
                StunTimeoutMs = 1500,
                IncludeLoopbackFallback = false
            });

        Assert.Equal(2, result.Bindings.Count);
        Assert.NotEqual(result.Bindings[0].SocketId, result.Bindings[1].SocketId);
        Assert.NotSame(result.Bindings[0].Socket, result.Bindings[1].Socket);
        Assert.Equal(eth.InterfaceId, result.Bindings.First(b => b.LocalEndPoint.Address.Equals(IPAddress.Parse("127.0.0.2"))).Interface.InterfaceId);
        Assert.Equal(wifi.InterfaceId, result.Bindings.First(b => b.LocalEndPoint.Address.Equals(IPAddress.Parse("127.0.0.3"))).Interface.InterfaceId);

        // 同一公網 IP（單 WAN）、不同映射埠分別對應各自 Socket
        Assert.False(result.HasMultiWanPublicIps);
        Assert.Single(result.DistinctPublicIps);
        Assert.Equal("198.51.100.25", result.DistinctPublicIps[0]);

        var srflxEth = result.Candidates.Single(c => c.Type == IceCandidateType.Srflx && c.InterfaceId == eth.InterfaceId);
        var srflxWifi = result.Candidates.Single(c => c.Type == IceCandidateType.Srflx && c.InterfaceId == wifi.InterfaceId);
        Assert.Equal(51001, srflxEth.PublicPort);
        Assert.Equal(51002, srflxWifi.PublicPort);
        Assert.NotEqual(srflxEth.SocketId, srflxWifi.SocketId);
    }

    // ------------------------------------------------------------
    // 測試 3：雙 WAN
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario3_DualWan_GathersDistinctPublicIpsMappedToSeparateSockets()
    {
        var stun1 = CreateStunServer(src =>
            src.Address.Equals(IPAddress.Parse("127.0.0.2"))
                ? (new IPEndPoint(IPAddress.Parse("203.0.113.10"), 40001), 0, 5)
                : (new IPEndPoint(IPAddress.Parse("198.51.100.88"), 52002), 0, 5));

        var stun2 = CreateStunServer(src =>
            src.Address.Equals(IPAddress.Parse("127.0.0.2"))
                ? (new IPEndPoint(IPAddress.Parse("203.0.113.10"), 40001), 0, 10)
                : (new IPEndPoint(IPAddress.Parse("198.51.100.88"), 52002), 0, 10));

        var wan1 = MakeInterface("wan-isp1", "WAN1-Chunghwa", "127.0.0.2", metric: 20);
        var wan2 = MakeInterface("wan-isp2", "WAN2-Backup", "127.0.0.3", metric: 25);

        using var result = await IceCandidateGatherer.GatherAsync(
            sessionId: "sess-dual-wan",
            options: new IceGatheringOptions
            {
                OverrideInterfaces = new[] { wan1, wan2 },
                StunServers = new[] { stun1.AddressString, stun2.AddressString },
                StunTimeoutMs = 1500,
                IncludeLoopbackFallback = false
            });

        Assert.True(result.HasMultiWanPublicIps);
        Assert.Contains("203.0.113.10", result.DistinctPublicIps);
        Assert.Contains("198.51.100.88", result.DistinctPublicIps);

        var wan1Bind = result.Bindings.Single(b => b.Interface.InterfaceId == "wan-isp1");
        var wan2Bind = result.Bindings.Single(b => b.Interface.InterfaceId == "wan-isp2");
        Assert.Equal(NatMappingBehavior.EndpointIndependent, wan1Bind.NatObservation!.Behavior);
        Assert.Equal(NatMappingBehavior.EndpointIndependent, wan2Bind.NatObservation!.Behavior);
    }

    // ------------------------------------------------------------
    // 測試 4 & 5：Wi-Fi + Ethernet 並存、VPN 路由與使用者政策、無效介面排除
    // ------------------------------------------------------------
    [Fact]
    public void Scenario4And5_InterfaceDiscovery_HandlesWifiEthernetVpnAndExclusionsPerPolicy()
    {
        var raw = new List<NetworkInterfaceDescriptor>
        {
            new() { InterfaceId = "loop", InterfaceName = "Loopback", InterfaceType = NetworkInterfaceType.Loopback, InterfaceStatus = OperationalStatus.Up, LocalIPv4 = IPAddress.Loopback, SubnetMask = IPAddress.Parse("255.0.0.0"), HasValidRoute = true },
            new() { InterfaceId = "down-eth", InterfaceName = "Ethernet Disabled", InterfaceType = NetworkInterfaceType.Ethernet, InterfaceStatus = OperationalStatus.Down, LocalIPv4 = IPAddress.Parse("192.168.1.99"), SubnetMask = IPAddress.Parse("255.255.255.0"), HasValidRoute = true },
            new() { InterfaceId = "apipa", InterfaceName = "Ethernet APIPA", InterfaceType = NetworkInterfaceType.Ethernet, InterfaceStatus = OperationalStatus.Up, LocalIPv4 = IPAddress.Parse("169.254.12.34"), SubnetMask = IPAddress.Parse("255.255.0.0"), HasValidRoute = false },
            new() { InterfaceId = "wifi", InterfaceName = "Wi-Fi", InterfaceType = NetworkInterfaceType.Wireless80211, InterfaceStatus = OperationalStatus.Up, LocalIPv4 = IPAddress.Parse("192.168.1.50"), SubnetMask = IPAddress.Parse("255.255.255.0"), GatewayIPv4 = IPAddress.Parse("192.168.1.1"), InterfaceMetric = 35, HasValidRoute = true },
            new() { InterfaceId = "eth", InterfaceName = "Ethernet", InterfaceType = NetworkInterfaceType.Ethernet, InterfaceStatus = OperationalStatus.Up, LocalIPv4 = IPAddress.Parse("192.168.1.40"), SubnetMask = IPAddress.Parse("255.255.255.0"), GatewayIPv4 = IPAddress.Parse("192.168.1.1"), InterfaceMetric = 25, HasValidRoute = true },
            new() { InterfaceId = "vpn-routed", InterfaceName = "WireGuard Tunnel", InterfaceType = NetworkInterfaceType.Unknown, InterfaceStatus = OperationalStatus.Up, LocalIPv4 = IPAddress.Parse("10.8.0.2"), SubnetMask = IPAddress.Parse("255.255.255.0"), GatewayIPv4 = IPAddress.Parse("10.8.0.1"), InterfaceMetric = 50, IsVirtual = true, IsVpn = true, HasValidRoute = true },
            new() { InterfaceId = "vpn-dead", InterfaceName = "OpenVPN TAP", InterfaceType = NetworkInterfaceType.Ethernet, InterfaceStatus = OperationalStatus.Up, LocalIPv4 = IPAddress.Parse("10.9.0.2"), SubnetMask = IPAddress.Parse("255.255.255.0"), InterfaceMetric = 60, IsVirtual = true, IsVpn = true, HasValidRoute = false },
            new() { InterfaceId = "vswitch-unrouted", InterfaceName = "vEthernet (Default Switch)", InterfaceType = NetworkInterfaceType.Ethernet, InterfaceStatus = OperationalStatus.Up, LocalIPv4 = IPAddress.Parse("172.25.0.1"), SubnetMask = IPAddress.Parse("255.255.240.0"), InterfaceMetric = 100, IsVirtual = true, IsVpn = false, HasValidRoute = false },
            new() { InterfaceId = "vm-routed", InterfaceName = "VMware Routed Bridge", InterfaceType = NetworkInterfaceType.Ethernet, InterfaceStatus = OperationalStatus.Up, LocalIPv4 = IPAddress.Parse("192.168.50.2"), SubnetMask = IPAddress.Parse("255.255.255.0"), GatewayIPv4 = IPAddress.Parse("192.168.50.1"), InterfaceMetric = 90, IsVirtual = true, IsVpn = false, HasValidRoute = true }
        };

        // 1. Auto 策略：Ethernet + Wi-Fi + 具路由 VPN + 具路由虛擬網卡應全數納入，無效/未路由虛擬/Loopback/APIPA/Down 全排除
        var autoResult = NetworkInterfaceDiscovery.FilterAndSortInterfaces(raw, new NetworkDiscoveryOptions { VpnPolicy = VpnCandidatePolicy.Auto });
        Assert.Equal(new[] { "eth", "wifi", "vm-routed", "vpn-routed" }, autoResult.Select(x => x.InterfaceId).ToArray());

        // 2. Exclude 策略：使用者停用 VPN 候選時排除全部 VPN，保留實體與具路由橋接虛擬網卡
        var excludeVpnResult = NetworkInterfaceDiscovery.FilterAndSortInterfaces(raw, new NetworkDiscoveryOptions { VpnPolicy = VpnCandidatePolicy.Exclude });
        Assert.Equal(new[] { "eth", "wifi", "vm-routed" }, excludeVpnResult.Select(x => x.InterfaceId).ToArray());

        // 3. Include 策略：顯式包含所有 Up VPN
        var includeVpnResult = NetworkInterfaceDiscovery.FilterAndSortInterfaces(raw, new NetworkDiscoveryOptions { VpnPolicy = VpnCandidatePolicy.Include });
        Assert.Contains(includeVpnResult, x => x.InterfaceId == "vpn-routed");
        Assert.Contains(includeVpnResult, x => x.InterfaceId == "vpn-dead");
    }

    // ------------------------------------------------------------
    // 測試 6：部分 STUN Server 失敗（Timeout、Error 0x0111、DNS 失敗），其他 Server 仍成功
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario6_PartialStunFailures_ClassifiesErrorsAndPreservesSuccessfulServers()
    {
        var timeoutServer = CreateStunServer(_ => (null, 0, 0)); // 不回包 -> Timeout
        var errorServer = CreateStunServer(_ => (null, 500, 5));  // STUN 0x0111 Error 500
        var goodServer1 = CreateStunServer(_ => (new IPEndPoint(IPAddress.Parse("203.0.113.44"), 60001), 0, 5));
        var goodServer2 = CreateStunServer(_ => (new IPEndPoint(IPAddress.Parse("203.0.113.44"), 60001), 0, 10));

        string badDnsServer = "nonexistent.invalid.stun.remotedesk.test:19302";

        var iface = MakeInterface("eth-main", "Ethernet", "127.0.0.2");
        using var result = await IceCandidateGatherer.GatherAsync(
            sessionId: "sess-partial-stun",
            options: new IceGatheringOptions
            {
                OverrideInterfaces = new[] { iface },
                StunServers = new[]
                {
                    timeoutServer.AddressString,
                    errorServer.AddressString,
                    badDnsServer,
                    goodServer1.AddressString,
                    goodServer2.AddressString
                },
                StunTimeoutMs = 900,
                IncludeLoopbackFallback = false
            });

        Assert.Equal(5, result.StunProbes.Count);

        var pTimeout = result.StunProbes.Single(p => p.StunServer == timeoutServer.AddressString);
        var pError = result.StunProbes.Single(p => p.StunServer == errorServer.AddressString);
        var pDns = result.StunProbes.Single(p => p.StunServer == badDnsServer);
        var pGood1 = result.StunProbes.Single(p => p.StunServer == goodServer1.AddressString);
        var pGood2 = result.StunProbes.Single(p => p.StunServer == goodServer2.AddressString);

        Assert.Equal(StunQueryFailureReason.Timeout, pTimeout.FailureReason);
        Assert.Equal(StunQueryFailureReason.ServerError, pError.FailureReason);
        Assert.Equal(500, pError.StunErrorCode);
        Assert.Equal(StunQueryFailureReason.DnsFailed, pDns.FailureReason);
        Assert.True(pGood1.Succeeded);
        Assert.True(pGood2.Succeeded);

        // 1 Host + 1 Srflx (因 goodServer1 與 goodServer2 回傳同公網端點 203.0.113.44:60001)
        Assert.Single(result.Candidates, c => c.Type == IceCandidateType.Srflx);
        Assert.Equal("203.0.113.44", result.Candidates.Single(c => c.Type == IceCandidateType.Srflx).PublicAddress);
    }

    // ------------------------------------------------------------
    // 測試 7：同一 Socket 對不同 STUN Server 回傳不同 Port（NAT Mapping 觀察，不得直接誤判為雙 WAN）
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario7_DifferentStunMappingsOnSameSocket_ObservedWithoutFalselyClaimingDualWan()
    {
        var stunA = CreateStunServer(_ => (new IPEndPoint(IPAddress.Parse("203.0.113.88"), 41001), 0, 5));
        var stunB = CreateStunServer(_ => (new IPEndPoint(IPAddress.Parse("203.0.113.88"), 41055), 0, 5));

        var iface = MakeInterface("eth-nat", "Ethernet", "127.0.0.2");
        using var result = await IceCandidateGatherer.GatherAsync(
            sessionId: "sess-nat-obs",
            options: new IceGatheringOptions
            {
                OverrideInterfaces = new[] { iface },
                StunServers = new[] { stunA.AddressString, stunB.AddressString },
                StunTimeoutMs = 1200,
                IncludeLoopbackFallback = false
            });

        var obs = Assert.Single(result.NatObservations);
        Assert.False(obs.PublicIpChanged);
        Assert.True(obs.PublicPortChanged);
        Assert.Equal(NatMappingBehavior.AddressOrPortDependent, obs.Behavior);
        Assert.False(result.HasMultiWanPublicIps);
        Assert.Equal(2, obs.PerDestinationMappings.Count);
        Assert.Equal(41001, obs.PerDestinationMappings[stunA.AddressString].Port);
        Assert.Equal(41055, obs.PerDestinationMappings[stunB.AddressString].Port);

        // 兩組不同映射埠皆須保留在候選清單中供 Phase 3 Connectivity Check
        Assert.Equal(2, result.Candidates.Count(c => c.Type == IceCandidateType.Srflx));
    }

    // ------------------------------------------------------------
    // 測試 8：部分介面沒有外網路由（不影響其他有效介面完成收集）
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario8_UnroutedSecondaryInterface_DoesNotPreventHealthyInterfaceGathering()
    {
        var healthyStun = CreateStunServer(src =>
            src.Address.Equals(IPAddress.Parse("127.0.0.2"))
                ? (new IPEndPoint(IPAddress.Parse("203.0.113.66"), 33445), 0, 5)
                : (null, 0, 0)); // 模擬第二介面無外網回包 (Timeout)

        var nicRouted = MakeInterface("eth-wan", "Ethernet-WAN", "127.0.0.2", metric: 20, hasValidRoute: true);
        var nicIsolated = MakeInterface("eth-lan-only", "Isolated-LAN", "127.0.0.3", metric: 40, hasValidRoute: false);

        using var result = await IceCandidateGatherer.GatherAsync(
            sessionId: "sess-partial-route",
            options: new IceGatheringOptions
            {
                OverrideInterfaces = new[] { nicRouted, nicIsolated },
                StunServers = new[] { healthyStun.AddressString },
                StunTimeoutMs = 600,
                IncludeLoopbackFallback = false
            });

        Assert.Equal(2, result.Bindings.Count);
        Assert.Contains(result.Candidates, c => c.Type == IceCandidateType.Host && c.InterfaceId == "eth-lan-only");
        Assert.Contains(result.Candidates, c => c.Type == IceCandidateType.Srflx && c.InterfaceId == "eth-wan");

        // 也測試 SkipStunWhenNoRoute = true：無路由介面直接跳過 STUN 並保留 Host Candidate
        using var skipResult = await IceCandidateGatherer.GatherAsync(
            sessionId: "sess-skip-unrouted",
            options: new IceGatheringOptions
            {
                OverrideInterfaces = new[] { nicRouted, nicIsolated },
                StunServers = new[] { healthyStun.AddressString },
                StunTimeoutMs = 600,
                SkipStunWhenNoRoute = true,
                IncludeLoopbackFallback = false
            });

        var wanBind = skipResult.Bindings.Single(b => b.Interface.InterfaceId == "eth-wan");
        var lanBind = skipResult.Bindings.Single(b => b.Interface.InterfaceId == "eth-lan-only");
        Assert.Contains(wanBind.Candidates, c => c.Type == IceCandidateType.Srflx);
        Assert.Empty(lanBind.StunProbes);
        Assert.Single(lanBind.Candidates, c => c.Type == IceCandidateType.Host);
    }

    // ------------------------------------------------------------
    // 測試 9：介面/Socket 在 STUN 查詢期間中斷
    // ------------------------------------------------------------
    [Fact]
    public async Task Scenario9_SocketDropsMidStunProbe_ClassifiesSocketErrorWithoutCrashingOtherProbes()
    {
        var slowStun = CreateStunServer(_ => (new IPEndPoint(IPAddress.Parse("203.0.113.70"), 40000), 0, 500));
        var fastStun = CreateStunServer(_ => (new IPEndPoint(IPAddress.Parse("203.0.113.71"), 40001), 0, 5));

        using var healthySock = IceCandidateGatherer.CreateConfiguredUdpSocket();
        healthySock.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));

        var droppingSock = IceCandidateGatherer.CreateConfiguredUdpSocket();
        droppingSock.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.3"), 0));

        // 啟動兩個介面的平行 STUN 查詢，於 80ms 後模擬第二介面斷線 (Dispose Socket)
        var healthyTask = StunClient.ProbeAllServersOnSocketAsync(
            healthySock,
            new[] { fastStun.AddressString },
            interfaceId: "eth-healthy",
            socketId: "sock-healthy",
            overallTimeoutMs: 1200);

        var droppingTask = StunClient.ProbeAllServersOnSocketAsync(
            droppingSock,
            new[] { slowStun.AddressString },
            interfaceId: "eth-drop",
            socketId: "sock-drop",
            overallTimeoutMs: 1200);

        _ = Task.Run(async () =>
        {
            await Task.Delay(80);
            try { droppingSock.Dispose(); } catch { }
        });

        var results = await Task.WhenAll(healthyTask, droppingTask);

        Assert.Single(results[0]);
        Assert.True(results[0][0].Succeeded);
        Assert.Single(results[1]);
        Assert.False(results[1][0].Succeeded);
        Assert.True(results[1][0].FailureReason == StunQueryFailureReason.SocketError ||
                    results[1][0].FailureReason == StunQueryFailureReason.Timeout);
    }

    // ------------------------------------------------------------
    // 測試 10：Candidate 完整欄位序列化與非勝出 Socket 生命週期釋放
    // ------------------------------------------------------------
    [Fact]
    public async Task CandidateMetadata_SerializesFullSchemaAndDisposesUnusedSockets()
    {
        var stun = CreateStunServer(src => (new IPEndPoint(IPAddress.Parse("203.0.113.99"), 54321), 0, 5));
        var nic1 = MakeInterface("nic-a", "NIC-A", "127.0.0.2", metric: 20);
        var nic2 = MakeInterface("nic-b", "NIC-B", "127.0.0.3", metric: 30);

        var result = await IceCandidateGatherer.GatherAsync(
            sessionId: "sess-lifecycle",
            options: new IceGatheringOptions
            {
                OverrideInterfaces = new[] { nic1, nic2 },
                StunServers = new[] { stun.AddressString },
                StunTimeoutMs = 1000,
                IncludeLoopbackFallback = false
            });

        var srflx = result.Candidates.First(c => c.Type == IceCandidateType.Srflx && c.InterfaceId == "nic-a");
        string json = new IceSignal
        {
            Type = "candidate",
            Candidate = srflx,
            SessionId = "sess-lifecycle",
            SenderDeviceId = "111222333",
            ReceiverDeviceId = "444555666"
        }.ToJson();

        var parsed = IceSignal.FromJson(json)!;
        Assert.NotNull(parsed.Candidate);
        Assert.Equal(srflx.CandidateId, parsed.Candidate.CandidateId);
        Assert.Equal(srflx.SocketId, parsed.Candidate.SocketId);
        Assert.Equal("nic-a", parsed.Candidate.InterfaceId);
        Assert.Equal(stun.AddressString, parsed.Candidate.StunServer);
        Assert.Equal("203.0.113.99", parsed.Candidate.PublicAddress);
        Assert.Equal(54321, parsed.Candidate.PublicPort);
        Assert.Equal(CandidateValidityStatus.Valid, parsed.Candidate.ValidityStatus);

        // 模擬 nic-a 勝出：轉移 Socket 所有權後 Dispose result，nic-a 應仍存活、nic-b 必須已釋放
        var wonSock = result.DetachWinningSocket(result.Bindings.Single(b => b.Interface.InterfaceId == "nic-a").SocketId);
        var lostSock = result.Bindings.Single(b => b.Interface.InterfaceId == "nic-b").Socket;
        Assert.NotNull(wonSock);

        result.Dispose();

        // 勝出的 Socket 仍可讀取 LocalEndPoint，未轉移的 Socket 已釋放會拋出 ObjectDisposedException
        Assert.NotNull(wonSock.LocalEndPoint);
        Assert.Throws<ObjectDisposedException>(() => _ = lostSock.LocalEndPoint);
        wonSock.Dispose();
    }

    // ------------------------------------------------------------
    // 測試 11：本機實體環境介面列舉安全檢查（不得包含 Loopback、APIPA 或 Down 介面）
    // ------------------------------------------------------------
    [Fact]
    public void LiveOs_DiscoverValidIPv4Interfaces_ExcludesLoopbackApipaAndDown()
    {
        var interfaces = NetworkInterfaceDiscovery.DiscoverValidIPv4Interfaces();
        foreach (var iface in interfaces)
        {
            Assert.Equal(OperationalStatus.Up, iface.InterfaceStatus);
            Assert.NotEqual(NetworkInterfaceType.Loopback, iface.InterfaceType);
            Assert.Equal(AddressFamily.InterNetwork, iface.LocalIPv4.AddressFamily);
            Assert.False(IPAddress.IsLoopback(iface.LocalIPv4));
            Assert.False(IPAddress.Any.Equals(iface.LocalIPv4));

            byte[] bytes = iface.LocalIPv4.GetAddressBytes();
            Assert.False(bytes[0] == 169 && bytes[1] == 254, $"APIPA leaked: {iface.LocalIPv4}");
            Assert.False(string.IsNullOrWhiteSpace(iface.InterfaceId));
            Assert.False(string.IsNullOrWhiteSpace(iface.InterfaceName));
        }
    }
}
