using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteDesk.Net;

internal static class NetworkHelper
{
    /// <summary>
    /// 取得本機所有可用的網路介面 IP 位址、主機名稱 IP 與 Loopback 位址。
    /// </summary>
    public static HashSet<IPAddress> GetLocalAddresses()
    {
        return LoadLocalAddresses();
    }

    private static HashSet<IPAddress> LoadLocalAddresses()
    {
        var set = new HashSet<IPAddress>
        {
            IPAddress.Loopback,
            IPAddress.IPv6Loopback,
            IPAddress.Any,
            IPAddress.IPv6Any
        };

        try
        {
            // 讀取本機所有網卡介面（不限 OperationalStatus，包含離線、虛擬、VPN 介面）
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); } catch { continue; }

                foreach (var ua in props.UnicastAddresses)
                {
                    set.Add(ua.Address);
                    if (ua.Address.IsIPv4MappedToIPv6)
                    {
                        set.Add(ua.Address.MapToIPv4());
                    }
                }
            }
        }
        catch { }

        try
        {
            var hostName = Dns.GetHostName();
            if (!string.IsNullOrWhiteSpace(hostName))
            {
                foreach (var hip in Dns.GetHostAddresses(hostName))
                {
                    set.Add(hip);
                    if (hip.IsIPv4MappedToIPv6)
                    {
                        set.Add(hip.MapToIPv4());
                    }
                }
            }
        }
        catch { }

        try
        {
            string machineName = Environment.MachineName;
            if (!string.IsNullOrWhiteSpace(machineName))
            {
                foreach (var hip in Dns.GetHostAddresses(machineName))
                {
                    set.Add(hip);
                    if (hip.IsIPv4MappedToIPv6)
                    {
                        set.Add(hip.MapToIPv4());
                    }
                }
            }
        }
        catch { }

        return set;
    }

    /// <summary>
    /// 判斷特定 IPAddress 是否代表本機地址（包含 127.0.0.0/8、::1、0.0.0.0、:: 以及所有 NIC IP）。
    /// </summary>
    public static bool IsLocalAddress(IPAddress? address)
    {
        if (address == null) return false;

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return true;

        // IPv4 整個 127.0.0.0/8 均為 loopback
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b.Length > 0 && b[0] == 127) return true;
        }

        // IPv4-mapped IPv6 (例如 ::ffff:127.0.0.1)
        if (address.IsIPv4MappedToIPv6)
        {
            var v4 = address.MapToIPv4();
            if (IsLocalAddress(v4)) return true;
        }

        var localIps = GetLocalAddresses();
        var bytes = address.GetAddressBytes();
        foreach (var lip in localIps)
        {
            if (lip.Equals(address)) return true;
            if (lip.AddressFamily == address.AddressFamily && lip.GetAddressBytes().SequenceEqual(bytes))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 檢查連線目標是否為本機自己（包含本機 Device ID、本機 Viewer ID、Loopback、本機所有 IP 位址或主機名稱）。
    /// </summary>
    public static bool IsSelfTarget(string? target, string? selfDeviceId, string? selfViewerId = null)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;

        string clean = target.Trim();
        if (string.IsNullOrEmpty(clean)) return false;

        // 移除常見引號
        clean = clean.Trim('"', '\'', '`');

        // 移除通訊協定開頭 (tcp://, http://, https://, rdsk://, //)
        int schemeIdx = clean.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            clean = clean.Substring(schemeIdx + 3);
        }
        else if (clean.StartsWith("//"))
        {
            clean = clean.Substring(2);
        }

        // 移除結尾斜線
        clean = clean.TrimEnd('/', '\\');

        string targetWithoutSpaces = clean.Replace(" ", "");

        // 1. 比對 Device ID 與 Viewer ID（忽略空格大小寫）
        string normSelfId = (selfDeviceId ?? "").Replace(" ", "").Trim();
        if (!string.IsNullOrEmpty(normSelfId) && string.Equals(targetWithoutSpaces, normSelfId, StringComparison.OrdinalIgnoreCase))
            return true;

        string normViewerId = (selfViewerId ?? "").Replace(" ", "").Trim();
        if (!string.IsNullOrEmpty(normViewerId) && string.Equals(targetWithoutSpaces, normViewerId, StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. 解析主機部分（移除 :port、處理 [IPv6] 等格式）
        string host = targetWithoutSpaces;
        if (host.StartsWith("[") && host.Contains(']'))
        {
            // IPv6 格式 [::1]:port 或 [::1]
            int end = host.IndexOf(']');
            host = host.Substring(1, end - 1);
        }
        else if (IPEndPoint.TryParse(host, out var parsedEp))
        {
            host = parsedEp.Address.ToString();
        }
        else if (host.Contains(':') && host.IndexOf(':') == host.LastIndexOf(':'))
        {
            // host:port 格式 (IPv4 或一般 hostname:port)
            host = host.Split(':')[0];
        }
        else if (IPAddress.TryParse(host, out var rawIp))
        {
            host = rawIp.ToString();
        }
        else if (host.Contains(':'))
        {
            // 多個冒號：可能為未帶括號的 IPv6 加上 :port，或純 IPv6
            int lastColon = host.LastIndexOf(':');
            string potentialPort = host.Substring(lastColon + 1);
            if (int.TryParse(potentialPort, out _) && IPAddress.TryParse(host.Substring(0, lastColon), out var ipBeforeColon))
            {
                host = ipBeforeColon.ToString();
            }
        }

        if (string.IsNullOrWhiteSpace(host)) return false;

        // 3. 常見 Loopback 與 本機主機名稱
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "::", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, Dns.GetHostName(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 4. 判斷是否為本機 IP 位址
        if (IPAddress.TryParse(host, out var parsedIp))
        {
            return IsLocalAddress(parsedIp);
        }

        // 5. 主機名稱 DNS 解析比對
        try
        {
            var addresses = Dns.GetHostAddresses(host);
            if (addresses.Any(IsLocalAddress))
                return true;
        }
        catch { }

        return false;
    }
}
