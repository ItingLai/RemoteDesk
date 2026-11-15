using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace RemoteDesk.Net;

/// <summary>
/// 決定 VPN 虛擬介面是否納入 P2P 候選端點收集。
/// </summary>
public enum VpnCandidatePolicy
{
    /// <summary>依實際 IPv4 路由自動決定：具有效路由的 VPN 納入，無路由 VPN 排除。</summary>
    Auto = 0,
    /// <summary>只要處於 Up 且具合法 IPv4 位址即納入。</summary>
    Include = 1,
    /// <summary>依使用者設定一律排除 VPN 介面。</summary>
    Exclude = 2
}

/// <summary>
/// 網路介面的靜態快照描述，供實機列舉與多網卡/雙 WAN 單元測試共用同一套篩選與排序邏輯。
/// </summary>
internal sealed class NetworkInterfaceDescriptor
{
    public string InterfaceId { get; init; } = "";
    public string InterfaceName { get; init; } = "";
    public string Description { get; init; } = "";
    public NetworkInterfaceType InterfaceType { get; init; } = NetworkInterfaceType.Ethernet;
    public OperationalStatus InterfaceStatus { get; init; } = OperationalStatus.Up;
    public IPAddress LocalIPv4 { get; init; } = IPAddress.None;
    public IPAddress SubnetMask { get; init; } = IPAddress.None;
    public IPAddress? GatewayIPv4 { get; init; }
    public int InterfaceIndex { get; init; }
    public int InterfaceMetric { get; init; } = 25;
    public bool IsVirtual { get; init; }
    public bool IsVpn { get; init; }
    public bool HasValidRoute { get; init; }
}

internal sealed class NetworkDiscoveryOptions
{
    public VpnCandidatePolicy VpnPolicy { get; set; } = VpnCandidatePolicy.Auto;
    /// <summary>非 VPN 之虛擬網卡 (例如 Hyper-V vSwitch / VMware / Docker) 若無有效路由，預設排除。</summary>
    public bool RequireRouteForVirtualInterfaces { get; set; } = true;
    /// <summary>實體網卡在無對外預設閘道時是否仍保留（可作為 LAN Host Candidate，其 STUN 會獨立失敗而不影響其他介面）。</summary>
    public bool AllowUnroutedPhysicalAsHostCandidate { get; set; } = true;
    public HashSet<string> ExcludedInterfaceIds { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 已驗證可用於 P2P 的 IPv4 網路介面資訊（對應 Phase 2 規格 2-1）。
/// </summary>
internal sealed class DiscoveredNetworkInterface
{
    public required string InterfaceId { get; init; }
    public required string InterfaceName { get; init; }
    public string Description { get; init; } = "";
    public NetworkInterfaceType InterfaceType { get; init; }
    public required IPAddress LocalIPv4 { get; init; }
    public required IPAddress SubnetMask { get; init; }
    public IPAddress? GatewayIPv4 { get; init; }
    public int InterfaceIndex { get; init; }
    public int InterfaceMetric { get; init; }
    public OperationalStatus InterfaceStatus { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsVpn { get; init; }
    public bool HasValidRoute { get; init; }
    public string AssociatedSocketId { get; set; } = "";

    public override string ToString() =>
        $"{InterfaceName} [{InterfaceId}] {LocalIPv4} (Metric={InterfaceMetric}, Virtual={IsVirtual}, VPN={IsVpn}, Route={HasValidRoute}, Socket={AssociatedSocketId})";
}

/// <summary>
/// IPv4 網路介面列舉與過濾器（Ethernet、Wi-Fi、其他實體介面與具合法路由之虛擬/VPN 介面）。
/// 本階段不實作 IPv6。
/// </summary>
internal static class NetworkInterfaceDiscovery
{
    private static readonly string[] VpnKeywords =
    {
        "vpn", "wireguard", "wintun", "openvpn", "tap-windows", "tap0", "tun",
        "tailscale", "zerotier", "anyconnect", "cisco", "fortinet", "fortissl",
        "globalprotect", "pulse secure", "sstp", "l2tp", "ikev2", "nordlynx",
        "protonvpn", "cloudflare warp", "mullvad", "surfshark", "expressvpn",
        "checkpoint", "sophos"
    };

    private static readonly string[] VirtualKeywords =
    {
        "virtual", "vethernet", "hyper-v", "vmware", "vmnet", "virtualbox", "vbox",
        "wsl", "docker", "loopback", "teredo", "6to4", "isatap", "km-test",
        "npcap", "microsoft wi-fi direct"
    };

    /// <summary>
    /// 探索本機所有適用於 P2P 收集的 IPv4 網路介面。
    /// </summary>
    public static List<DiscoveredNetworkInterface> DiscoverValidIPv4Interfaces(NetworkDiscoveryOptions? options = null)
    {
        var snapshots = CaptureSystemDescriptors();
        return FilterAndSortInterfaces(snapshots, options);
    }

    internal static List<NetworkInterfaceDescriptor> CaptureSystemDescriptors()
    {
        var list = new List<NetworkInterfaceDescriptor>();
        NetworkInterface[] nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return list;
        }

        var routeTable = ReadIPv4InterfaceRoutesAndMetrics();

        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.Supports(NetworkInterfaceComponent.IPv4)) continue;

            IPInterfaceProperties props;
            IPv4InterfaceProperties? v4Props = null;
            try
            {
                props = nic.GetIPProperties();
                v4Props = props.GetIPv4Properties();
            }
            catch
            {
                continue;
            }

            string combinedText = $"{nic.Name} {nic.Description}";
            bool isVpn = IsVpnInterface(nic.NetworkInterfaceType, combinedText);
            bool isVirtual = isVpn || IsVirtualInterface(nic.NetworkInterfaceType, combinedText);

            var gateways = props.GatewayAddresses
                .Select(g => g.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(a))
                .ToList();

            IPAddress? primaryGw = gateways.FirstOrDefault();
            int ifaceIndex = v4Props?.Index ?? -1;

            bool hasDefaultRouteFromTable = false;
            int? osMetric = null;
            if (ifaceIndex >= 0 && routeTable.TryGetValue(ifaceIndex, out var routeInfo))
            {
                hasDefaultRouteFromTable = routeInfo.hasDefaultRoute;
                osMetric = routeInfo.minMetric;
            }

            bool hasValidRoute = primaryGw != null || hasDefaultRouteFromTable;
            int metric = osMetric ?? (isVpn ? 50 : isVirtual ? 100 : (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 35 : 25));

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = ua.Address;
                var mask = ua.IPv4Mask ?? IPAddress.Parse("255.255.255.0");

                if (!IsUsableUnicastIPv4(ip)) continue;

                list.Add(new NetworkInterfaceDescriptor
                {
                    InterfaceId = !string.IsNullOrWhiteSpace(nic.Id) ? nic.Id : $"{nic.Name}:{ip}",
                    InterfaceName = nic.Name,
                    Description = nic.Description,
                    InterfaceType = nic.NetworkInterfaceType,
                    InterfaceStatus = nic.OperationalStatus,
                    LocalIPv4 = ip,
                    SubnetMask = mask,
                    GatewayIPv4 = primaryGw,
                    InterfaceIndex = ifaceIndex > 0 ? ifaceIndex : 0,
                    InterfaceMetric = metric,
                    IsVirtual = isVirtual,
                    IsVpn = isVpn,
                    HasValidRoute = hasValidRoute
                });
            }
        }

        return list;
    }

    internal static List<DiscoveredNetworkInterface> FilterAndSortInterfaces(
        IEnumerable<NetworkInterfaceDescriptor> descriptors,
        NetworkDiscoveryOptions? options = null)
    {
        options ??= new NetworkDiscoveryOptions();
        var seenIps = new HashSet<IPAddress>();
        var result = new List<DiscoveredNetworkInterface>();

        foreach (var d in descriptors)
        {
            if (d.InterfaceStatus != OperationalStatus.Up) continue;
            if (d.InterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!IsUsableUnicastIPv4(d.LocalIPv4)) continue;
            if (options.ExcludedInterfaceIds.Contains(d.InterfaceId)) continue;

            // VPN 介面依使用者設定與實際路由決定
            if (d.IsVpn)
            {
                switch (options.VpnPolicy)
                {
                    case VpnCandidatePolicy.Exclude:
                        continue;
                    case VpnCandidatePolicy.Auto:
                        if (!d.HasValidRoute) continue;
                        break;
                    case VpnCandidatePolicy.Include:
                        break;
                }
            }
            else if (d.IsVirtual)
            {
                // 虛擬轉接器 (Hyper-V vSwitch / VMware Host-Only / WSL / Docker 等) 無有效路由時不適合 P2P
                if (options.RequireRouteForVirtualInterfaces && !d.HasValidRoute)
                    continue;
            }
            else
            {
                // 實體網卡 (Ethernet / Wi-Fi / USB NIC 等)
                if (!options.AllowUnroutedPhysicalAsHostCandidate && !d.HasValidRoute)
                    continue;
            }

            if (!seenIps.Add(d.LocalIPv4)) continue;

            result.Add(new DiscoveredNetworkInterface
            {
                InterfaceId = d.InterfaceId,
                InterfaceName = d.InterfaceName,
                Description = d.Description,
                InterfaceType = d.InterfaceType,
                LocalIPv4 = d.LocalIPv4,
                SubnetMask = d.SubnetMask,
                GatewayIPv4 = d.GatewayIPv4,
                InterfaceIndex = d.InterfaceIndex,
                InterfaceMetric = d.InterfaceMetric,
                InterfaceStatus = d.InterfaceStatus,
                IsVirtual = d.IsVirtual,
                IsVpn = d.IsVpn,
                HasValidRoute = d.HasValidRoute
            });
        }

        return result
            .OrderByDescending(i => i.HasValidRoute)
            .ThenBy(i => i.IsVirtual)
            .ThenBy(i => i.IsVpn)
            .ThenBy(i => i.InterfaceMetric)
            .ThenBy(i => i.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsUsableUnicastIPv4(IPAddress? ip)
    {
        if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(ip) || IPAddress.Any.Equals(ip) || IPAddress.Broadcast.Equals(ip)) return false;

        Span<byte> bytes = stackalloc byte[4];
        if (!ip.TryWriteBytes(bytes, out _)) return false;

        // 排除 APIPA (169.254.0.0/16)、0.x.x.x 與 Multicast (224.0.0.0/4+)
        if (bytes[0] == 0) return false;
        if (bytes[0] == 127) return false;
        if (bytes[0] == 169 && bytes[1] == 254) return false;
        if (bytes[0] >= 224) return false;

        return true;
    }

    internal static bool IsVpnInterface(NetworkInterfaceType type, string nameAndDescription)
    {
        if (type == NetworkInterfaceType.Ppp) return true;
        string lower = nameAndDescription.ToLowerInvariant();
        foreach (var kw in VpnKeywords)
        {
            if (lower.Contains(kw, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    internal static bool IsVirtualInterface(NetworkInterfaceType type, string nameAndDescription)
    {
        if (type == NetworkInterfaceType.Tunnel) return true;
        string lower = nameAndDescription.ToLowerInvariant();
        foreach (var kw in VirtualKeywords)
        {
            if (lower.Contains(kw, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // ---------------- Windows IPv4 Route Table (預設路由與 Metric 讀取) ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPFORWARDROW
    {
        public uint dwForwardDest;
        public uint dwForwardMask;
        public uint dwForwardPolicy;
        public uint dwForwardNextHop;
        public uint dwForwardIfIndex;
        public uint dwForwardType;
        public uint dwForwardProto;
        public uint dwForwardAge;
        public uint dwForwardNextHopAS;
        public uint dwForwardMetric1;
        public uint dwForwardMetric2;
        public uint dwForwardMetric3;
        public uint dwForwardMetric4;
        public uint dwForwardMetric5;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpForwardTable(IntPtr pIpForwardTable, ref int pdwSize, bool bOrder);

    private static Dictionary<int, (bool hasDefaultRoute, int minMetric)> ReadIPv4InterfaceRoutesAndMetrics()
    {
        var map = new Dictionary<int, (bool hasDefaultRoute, int minMetric)>();
        if (!OperatingSystem.IsWindows()) return map;

        try
        {
            var raw = new Dictionary<int, (bool hasDefault, int defaultMetric, int anyMetric)>();
            int size = 0;
            int rc = GetIpForwardTable(IntPtr.Zero, ref size, false);
            if (size <= 0) return map;

            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                rc = GetIpForwardTable(buffer, ref size, false);
                if (rc != 0) return map;

                int count = Marshal.ReadInt32(buffer);
                int rowSize = Marshal.SizeOf<MIB_IPFORWARDROW>();
                IntPtr rowPtr = IntPtr.Add(buffer, 4);

                for (int i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_IPFORWARDROW>(rowPtr);
                    rowPtr = IntPtr.Add(rowPtr, rowSize);

                    int ifIndex = unchecked((int)row.dwForwardIfIndex);
                    // 預設閘道路由 (0.0.0.0/0) 或大範圍 VPN split-default (0.0.0.0/1 或 128.0.0.0/1，常見於 WireGuard / OpenVPN)
                    bool isDefaultLike = row.dwForwardMask == 0 ||
                                         row.dwForwardMask == 0x00000080u; // big-endian 128.0.0.0 -> little-endian 0x00000080
                    int metric = row.dwForwardMetric1 > int.MaxValue ? 9999 : (int)row.dwForwardMetric1;

                    if (raw.TryGetValue(ifIndex, out var prev))
                    {
                        bool hasDef = prev.hasDefault || isDefaultLike;
                        int defM = isDefaultLike ? Math.Min(prev.defaultMetric, metric) : prev.defaultMetric;
                        int anyM = Math.Min(prev.anyMetric, metric);
                        raw[ifIndex] = (hasDef, defM, anyM);
                    }
                    else
                    {
                        raw[ifIndex] = (isDefaultLike, isDefaultLike ? metric : int.MaxValue, metric);
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            foreach (var (ifIndex, entry) in raw)
            {
                map[ifIndex] = (entry.hasDefault, entry.hasDefault ? entry.defaultMetric : entry.anyMetric);
            }
        }
        catch { }

        return map;
    }
}
