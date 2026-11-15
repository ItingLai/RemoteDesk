using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RemoteDesk.Common;

namespace RemoteDesk.Net;

internal sealed class IceGatheringOptions
{
    public IReadOnlyList<string> StunServers { get; init; } = StunClient.DefaultStunServers;
    public int StunTimeoutMs { get; init; } = 1800;
    public int MaxConcurrentInterfaces { get; init; } = 6;
    public bool IncludeLoopbackFallback { get; init; } = true;
    public bool SkipStunWhenNoRoute { get; init; } = false;
    public NetworkDiscoveryOptions? DiscoveryOptions { get; init; }
    /// <summary>供受控/多網卡測試注入指定介面清單；為 null 時自動呼叫 NetworkInterfaceDiscovery。</summary>
    public IEnumerable<DiscoveredNetworkInterface>? OverrideInterfaces { get; init; }
}

/// <summary>
/// 單一介面與其專屬已繫結 UDP Socket 之對應關係，確保候選收集與後續打洞共用同一 Socket。
/// </summary>
internal sealed class GatheredSocketBinding : IDisposable
{
    private bool _disposed;

    public required string SocketId { get; init; }
    public required DiscoveredNetworkInterface Interface { get; init; }
    public required Socket Socket { get; init; }
    public required IPEndPoint LocalEndPoint { get; init; }
    public List<IceCandidate> Candidates { get; } = new();
    public List<StunProbeRecord> StunProbes { get; } = new();
    public NatMappingObservation? NatObservation { get; set; }
    public bool IsLoopbackFallback { get; init; }
    public bool OwnsSocket { get; init; } = true;
    public bool IsDetached { get; private set; }
    public bool IsDisposed => _disposed;

    public Socket DetachSocket()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IsDetached = true;
        return Socket;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!IsDetached && OwnsSocket)
        {
            try { Socket.Dispose(); } catch { }
        }
    }
}

/// <summary>
/// 單次連線嘗試所收集之所有介面、專屬 Socket、候選端點、STUN 紀錄與 NAT 映射觀察結果。
/// </summary>
internal sealed class CandidateGatheringResult : IDisposable
{
    private bool _disposed;

    public required string ConnectionAttemptId { get; init; }
    public required string SessionId { get; init; }
    public IReadOnlyList<DiscoveredNetworkInterface> Interfaces { get; init; } = Array.Empty<DiscoveredNetworkInterface>();
    public IReadOnlyList<GatheredSocketBinding> Bindings { get; init; } = Array.Empty<GatheredSocketBinding>();
    public IReadOnlyList<IceCandidate> Candidates { get; init; } = Array.Empty<IceCandidate>();
    public IReadOnlyList<StunProbeRecord> StunProbes { get; init; } = Array.Empty<StunProbeRecord>();
    public IReadOnlyList<NatMappingObservation> NatObservations { get; init; } = Array.Empty<NatMappingObservation>();
    public IReadOnlyList<string> DistinctPublicIps { get; init; } = Array.Empty<string>();
    public bool HasMultiWanPublicIps => DistinctPublicIps.Count > 1;

    public Socket? GetSocketById(string? socketId)
    {
        if (string.IsNullOrWhiteSpace(socketId)) return null;
        return Bindings.FirstOrDefault(b => string.Equals(b.SocketId, socketId, StringComparison.OrdinalIgnoreCase))?.Socket;
    }

    public Socket? GetSocketForCandidate(IceCandidate? candidate)
    {
        if (candidate == null) return null;
        if (!string.IsNullOrWhiteSpace(candidate.SocketId))
        {
            var byId = GetSocketById(candidate.SocketId);
            if (byId != null) return byId;
        }

        if (!string.IsNullOrWhiteSpace(candidate.LocalAddress) && candidate.LocalPort > 0)
        {
            var byEp = Bindings.FirstOrDefault(b =>
                string.Equals(b.LocalEndPoint.Address.ToString(), candidate.LocalAddress, StringComparison.OrdinalIgnoreCase) &&
                b.LocalEndPoint.Port == candidate.LocalPort);
            if (byEp != null) return byEp.Socket;
        }

        return null;
    }

    public Socket? DetachWinningSocket(string socketId)
    {
        var binding = Bindings.FirstOrDefault(b => string.Equals(b.SocketId, socketId, StringComparison.OrdinalIgnoreCase));
        return binding?.DetachSocket();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var b in Bindings)
        {
            b.Dispose();
        }
    }
}

/// <summary>
/// Phase 2 多網卡、多 WAN 及多 STUN 候選端點收集器：
/// 1. 列舉所有有效 IPv4 介面（依路由與設定決定虛擬/VPN 介面）
/// 2. 為每個介面建立並綁定獨立 UDP Socket
/// 3. 使用同一 Socket 對多組 STUN Server 並行發送探測（不會因第一組回應即提早結束，也不因單一介面/Server 失敗而中止其他介面）
/// 4. 記錄 NAT 映射觀察並保持 Socket 與 Candidate 精確對應關係
/// </summary>
internal static class IceCandidateGatherer
{
    public static async Task<CandidateGatheringResult> GatherAsync(
        string sessionId,
        string connectionAttemptId = "",
        IceGatheringOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new IceGatheringOptions();
        if (string.IsNullOrWhiteSpace(connectionAttemptId))
            connectionAttemptId = Guid.NewGuid().ToString("N")[..12];

        var interfaces = (options.OverrideInterfaces?.ToList()
                          ?? NetworkInterfaceDiscovery.DiscoverValidIPv4Interfaces(options.DiscoveryOptions));

        Log.Info($"[ICE/Gather] Attempt={connectionAttemptId}, Session={sessionId}, DiscoveredIPv4Interfaces={interfaces.Count}");

        var bindings = new List<GatheredSocketBinding>();
        var bindingsLock = new object();

        using var concurrencyGate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentInterfaces));

        var interfaceTasks = interfaces.Select(async (iface, index) =>
        {
            await concurrencyGate.WaitAsync(ct).ConfigureAwait(false);
            Socket? socket = null;
            try
            {
                if (ct.IsCancellationRequested) return;

                socket = CreateConfiguredUdpSocket();
                socket.Bind(new IPEndPoint(iface.LocalIPv4, 0));
                bool unicastIfApplied = ApplyInterfaceEgressBinding(socket, iface);
                var localEp = (IPEndPoint)socket.LocalEndPoint!;
                string socketId = $"sock-{ShortId(iface.InterfaceId)}-{Guid.NewGuid().ToString("N")[..6]}";
                iface.AssociatedSocketId = socketId;

                var binding = new GatheredSocketBinding
                {
                    SocketId = socketId,
                    Interface = iface,
                    Socket = socket,
                    LocalEndPoint = localEp,
                    IsLoopbackFallback = false
                };
                // 轉移所有權給 binding，避免 finally 提前 Dispose
                socket = null;

                ushort localPref = ComputeInterfaceLocalPreference(iface, index);
                var now = DateTime.UtcNow;

                // 建立該介面的 Host Candidate
                var hostCand = new IceCandidate
                {
                    CandidateId = NewCandidateId(),
                    Type = IceCandidateType.Host,
                    AddressFamily = AddressFamily.InterNetwork,
                    Protocol = "UDP",
                    Ip = localEp.Address.ToString(),
                    Port = localEp.Port,
                    LocalAddress = localEp.Address.ToString(),
                    LocalPort = localEp.Port,
                    PublicAddress = localEp.Address.ToString(),
                    PublicPort = localEp.Port,
                    InterfaceId = iface.InterfaceId,
                    SocketId = socketId,
                    StunServer = "",
                    Priority = IceCandidate.ComputePriority(IceCandidateType.Host, localPref),
                    DiscoveryTimestamp = now,
                    ValidityStatus = CandidateValidityStatus.Valid
                };
                binding.Candidates.Add(hostCand);

                Log.Info($"[ICE/Gather] Attempt={connectionAttemptId}, Session={sessionId}, BindOK Iface={iface.InterfaceName}({iface.InterfaceId}), IfIdx={iface.InterfaceIndex}, UnicastIfBound={unicastIfApplied}, Local={localEp}, Socket={socketId}, Metric={iface.InterfaceMetric}, Virtual={iface.IsVirtual}, VPN={iface.IsVpn}, Route={iface.HasValidRoute}");

                // 使用同一已繫結 Socket 對全部 STUN Server 執行查詢
                if (!options.SkipStunWhenNoRoute || iface.HasValidRoute)
                {
                    var probes = await StunClient.ProbeAllServersOnSocketAsync(
                        binding.Socket,
                        options.StunServers,
                        iface.InterfaceId,
                        socketId,
                        connectionAttemptId,
                        sessionId,
                        options.StunTimeoutMs,
                        ct).ConfigureAwait(false);

                    binding.StunProbes.AddRange(probes);
                    binding.NatObservation = NatMappingAnalyzer.AnalyzeSocketMappings(
                        socketId,
                        iface.InterfaceId,
                        localEp.Address.ToString(),
                        probes);

                    var seenSrflxEndpoints = new HashSet<IPEndPoint>();
                    foreach (var p in probes)
                    {
                        if (!p.Succeeded || p.MappedEndPoint == null) continue;
                        if (!seenSrflxEndpoints.Add(p.MappedEndPoint)) continue;

                        var srflxCand = new IceCandidate
                        {
                            CandidateId = NewCandidateId(),
                            Type = IceCandidateType.Srflx,
                            AddressFamily = AddressFamily.InterNetwork,
                            Protocol = "UDP",
                            Ip = p.MappedEndPoint.Address.ToString(),
                            Port = p.MappedEndPoint.Port,
                            LocalAddress = localEp.Address.ToString(),
                            LocalPort = localEp.Port,
                            PublicAddress = p.MappedEndPoint.Address.ToString(),
                            PublicPort = p.MappedEndPoint.Port,
                            InterfaceId = iface.InterfaceId,
                            SocketId = socketId,
                            StunServer = p.StunServer,
                            Priority = IceCandidate.ComputePriority(IceCandidateType.Srflx, localPref),
                            DiscoveryTimestamp = p.Timestamp,
                            ValidityStatus = CandidateValidityStatus.Valid
                        };
                        binding.Candidates.Add(srflxCand);
                    }

                    if (binding.NatObservation.SuccessfulProbeCount > 0)
                    {
                        Log.Info($"[ICE/NAT] Attempt={connectionAttemptId}, Session={sessionId}, Iface={iface.InterfaceId}, Socket={socketId}, Behavior={binding.NatObservation.Behavior}, PublicIpChanged={binding.NatObservation.PublicIpChanged}, PublicPortChanged={binding.NatObservation.PublicPortChanged}, MappedCount={binding.NatObservation.DistinctMappedEndPoints.Count}");
                    }
                }
                else
                {
                    Log.Info($"[ICE/Gather] Attempt={connectionAttemptId}, Session={sessionId}, Iface={iface.InterfaceId} 無有效對外 IPv4 路由，略過 STUN 並保留 Host Candidate");
                }

                lock (bindingsLock)
                {
                    bindings.Add(binding);
                }
            }
            catch (OperationCanceledException)
            {
                socket?.Dispose();
            }
            catch (Exception ex)
            {
                socket?.Dispose();
                Log.Warn($"[ICE/Gather] Attempt={connectionAttemptId}, Session={sessionId}, Iface={iface.InterfaceName}({iface.InterfaceId}) Socket 繫結或 STUN 收集失敗 (不影響其他介面): {ex.Message}");
            }
            finally
            {
                concurrencyGate.Release();
            }
        }).ToList();

        await Task.WhenAll(interfaceTasks).ConfigureAwait(false);

        // 必要時建立獨立的 127.0.0.1 Socket 供本機回環測試使用（與實體介面 Socket 分離）
        if (options.IncludeLoopbackFallback && !ct.IsCancellationRequested)
        {
            Socket? loSock = null;
            try
            {
                loSock = CreateConfiguredUdpSocket();
                loSock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                var loEp = (IPEndPoint)loSock.LocalEndPoint!;
                string loSocketId = $"sock-lo-{Guid.NewGuid().ToString("N")[..6]}";
                var loIface = new DiscoveredNetworkInterface
                {
                    InterfaceId = "loopback-fallback",
                    InterfaceName = "Loopback Fallback",
                    InterfaceType = NetworkInterfaceType.Loopback,
                    LocalIPv4 = IPAddress.Loopback,
                    SubnetMask = IPAddress.Parse("255.0.0.0"),
                    InterfaceMetric = 65000,
                    InterfaceStatus = OperationalStatus.Up,
                    IsVirtual = false,
                    IsVpn = false,
                    HasValidRoute = false,
                    AssociatedSocketId = loSocketId
                };

                var loBinding = new GatheredSocketBinding
                {
                    SocketId = loSocketId,
                    Interface = loIface,
                    Socket = loSock,
                    LocalEndPoint = loEp,
                    IsLoopbackFallback = true
                };
                loSock = null;

                loBinding.Candidates.Add(new IceCandidate
                {
                    CandidateId = NewCandidateId(),
                    Type = IceCandidateType.Host,
                    AddressFamily = AddressFamily.InterNetwork,
                    Protocol = "UDP",
                    Ip = "127.0.0.1",
                    Port = loEp.Port,
                    LocalAddress = "127.0.0.1",
                    LocalPort = loEp.Port,
                    PublicAddress = "127.0.0.1",
                    PublicPort = loEp.Port,
                    InterfaceId = loIface.InterfaceId,
                    SocketId = loSocketId,
                    Priority = IceCandidate.ComputePriority(IceCandidateType.Host, 500),
                    DiscoveryTimestamp = DateTime.UtcNow,
                    ValidityStatus = CandidateValidityStatus.Valid
                });

                lock (bindingsLock)
                {
                    bindings.Add(loBinding);
                }
            }
            catch
            {
                loSock?.Dispose();
            }
        }

        // 彙整所有候選端點、STUN 紀錄、NAT 觀察與跨介面不同公網 IP 判斷 (真實雙 WAN 觀察)
        var allCandidates = bindings
            .SelectMany(b => b.Candidates)
            .OrderByDescending(c => c.Priority)
            .ToList();

        var allProbes = bindings.SelectMany(b => b.StunProbes).ToList();
        var allObs = bindings.Where(b => b.NatObservation != null).Select(b => b.NatObservation!).ToList();

        var distinctPublicIps = allObs
            .SelectMany(o => o.DistinctPublicIps)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new CandidateGatheringResult
        {
            ConnectionAttemptId = connectionAttemptId,
            SessionId = sessionId,
            Interfaces = interfaces,
            Bindings = bindings,
            Candidates = allCandidates,
            StunProbes = allProbes,
            NatObservations = allObs,
            DistinctPublicIps = distinctPublicIps
        };

        Log.Info($"[ICE/Gather] Attempt={connectionAttemptId}, Session={sessionId} 收集完成: Bindings={bindings.Count}, Candidates={allCandidates.Count} (Host={allCandidates.Count(c => c.Type == IceCandidateType.Host)}, Srflx={allCandidates.Count(c => c.Type == IceCandidateType.Srflx)}), DistinctPublicIPs={distinctPublicIps.Count}");
        return result;
    }

    internal static Socket CreateConfiguredUdpSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.SendBufferSize = 4 * 1024 * 1024;
        socket.ReceiveBufferSize = 4 * 1024 * 1024;
        if (OperatingSystem.IsWindows())
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            try { socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { }
        }
        return socket;
    }

    internal static bool ApplyInterfaceEgressBinding(Socket socket, DiscoveredNetworkInterface iface)
    {
        if (!OperatingSystem.IsWindows() || iface.InterfaceIndex <= 0 || IPAddress.IsLoopback(iface.LocalIPv4))
            return false;

        const SocketOptionName IP_UNICAST_IF = (SocketOptionName)31;
        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, IP_UNICAST_IF, IPAddress.HostToNetworkOrder(iface.InterfaceIndex));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ushort ComputeInterfaceLocalPreference(DiscoveredNetworkInterface iface, int discoveryOrder)
    {
        int score = 60000;
        if (!iface.HasValidRoute) score -= 15000;
        if (iface.IsVirtual) score -= 5000;
        if (iface.IsVpn) score -= 4000;
        score -= Math.Clamp(iface.InterfaceMetric, 0, 5000);
        score -= Math.Clamp(discoveryOrder * 10, 0, 1000);
        return (ushort)Math.Clamp(score, 1000, 65535);
    }

    private static string ShortId(string id)
    {
        var clean = new string(id.Where(char.IsLetterOrDigit).ToArray());
        if (clean.Length == 0) return "nic";
        return clean.Length <= 8 ? clean : clean[..8];
    }

    private static string NewCandidateId() => Guid.NewGuid().ToString("N")[..12];
}
