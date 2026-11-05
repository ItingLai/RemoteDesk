using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteDesk.Common;

namespace RemoteDesk.Net;

internal sealed class DiscoveredDevice
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public string User { get; set; } = "";
    public IPAddress Address { get; set; } = IPAddress.None;
    public int Port { get; set; }
    public DateTime LastSeen { get; set; }
    public bool Busy { get; set; }
    public IPEndPoint EndPoint => new(Address, Port);
}

/// <summary>
/// 區域網路裝置探索：使用 UDP 廣播。
/// 主機定期廣播自己的 ID / 名稱 / 連線埠，並回應查詢；因此對方 IP 變動（DHCP）也能用固定的 9 位數 ID 找到。
/// </summary>
internal sealed class LanDiscovery : IDisposable
{
    private sealed class Packet
    {
        [JsonPropertyName("m")] public string Magic { get; set; } = "RDSK";
        [JsonPropertyName("t")] public string Type { get; set; } = "";     // q=查詢, a=宣告/回應, bye=離線
        [JsonPropertyName("id")] public string? Id { get; set; }            // a: 自己的 ID；q: 要找的 ID（空 = 全部）
        [JsonPropertyName("from")] public string? From { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("user")] public string? User { get; set; }
        [JsonPropertyName("port")] public int Port { get; set; }
        [JsonPropertyName("busy")] public bool Busy { get; set; }
        [JsonPropertyName("v")] public int Version { get; set; } = Protocol.Version;
    }

    public sealed class HostAdvert
    {
        public string Id = "";
        public string Name = "";
        public int Port;
        public bool Busy;
    }

    private readonly int _port;
    private UdpClient? _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, DiscoveredDevice> _devices = new();
    private readonly string _selfId;
    private volatile HostAdvert? _advert;
    private Task? _recvTask, _announceTask;

    public event Action<DiscoveredDevice>? DeviceUpdated;

    public LanDiscovery(string selfId, int port = Protocol.DiscoveryPort)
    {
        _selfId = selfId;
        _port = port;
    }

    public IReadOnlyCollection<DiscoveredDevice> Devices => _devices.Values.ToList();

    public bool IsRunning => _udp != null;

    /// <summary>設定（或清除）本機主機宣告資訊；null 代表不提供被控服務。</summary>
    public void SetAdvert(HostAdvert? advert)
    {
        var old = _advert;
        _advert = advert;
        if (advert != null) _ = AnnounceOnceAsync();
        else if (old != null) _ = SendToAllAsync(new Packet { Type = "bye", Id = old.Id });
    }

    public void Start()
    {
        try
        {
            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
            try
            {
                // 避免 ICMP Port Unreachable 讓 Receive 拋出 10054
                const int SIO_UDP_CONNRESET = -1744830452;
                udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
            }
            catch { }
            _udp = udp;
        }
        catch (Exception ex)
        {
            Log.Error($"探索服務無法綁定 UDP {_port}", ex);
            return;
        }
        _recvTask = Task.Run(ReceiveLoop);
        _announceTask = Task.Run(AnnounceLoop);
        _ = QueryAsync(null);
    }

    private async Task AnnounceLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await AnnounceOnceAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Warn("探索宣告失敗: " + ex.Message); }
        }
    }

    private Task AnnounceOnceAsync()
    {
        var a = _advert;
        if (a == null) return Task.CompletedTask;
        return SendToAllAsync(MakeAnswer(a));
    }

    private static Packet MakeAnswer(HostAdvert a) => new()
    {
        Type = "a", Id = a.Id, Name = a.Name, Port = a.Port, Busy = a.Busy,
        User = Environment.UserName,
    };

    /// <summary>送出查詢；targetId 為 null 代表尋找全部主機。</summary>
    public Task QueryAsync(string? targetId) => SendToAllAsync(new Packet { Type = "q", Id = targetId, From = _selfId });

    /// <summary>以 ID 尋找主機的目前 IP。</summary>
    public async Task<DiscoveredDevice?> ResolveAsync(string id, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_devices.TryGetValue(id, out var known) && DateTime.Now - known.LastSeen < TimeSpan.FromSeconds(20))
            return known;

        var tcs = new TaskCompletionSource<DiscoveredDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(DiscoveredDevice d) { if (d.Id == id) tcs.TrySetResult(d); }
        DeviceUpdated += Handler;
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await QueryAsync(id).ConfigureAwait(false);
                var done = await Task.WhenAny(tcs.Task, Task.Delay(400, ct)).ConfigureAwait(false);
                if (done == tcs.Task) return tcs.Task.Result;
            }
        }
        catch (OperationCanceledException) { }
        finally { DeviceUpdated -= Handler; }
        // 逾時仍回傳舊資料（可能還有效）
        return known;
    }

    private async Task ReceiveLoop()
    {
        var udp = _udp!;
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try { res = await udp.ReceiveAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            Packet? p;
            try { p = JsonSerializer.Deserialize<Packet>(res.Buffer); }
            catch { continue; }
            if (p == null || p.Magic != "RDSK") continue;

            try { await HandleAsync(p, res.RemoteEndPoint).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("處理探索封包失敗: " + ex.Message); }
        }
    }

    private async Task HandleAsync(Packet p, IPEndPoint from)
    {
        switch (p.Type)
        {
            case "q":
            {
                var a = _advert;
                if (a == null) return;
                if (!string.IsNullOrEmpty(p.Id) && p.Id != a.Id) return;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(MakeAnswer(a));
                // 直接回覆給查詢者，再補一個廣播（對方防火牆擋單播回應時）
                await _udp!.SendAsync(bytes, from).ConfigureAwait(false);
                break;
            }
            case "a":
            {
                if (string.IsNullOrEmpty(p.Id) || p.Port <= 0) return;
                var dev = _devices.AddOrUpdate(p.Id,
                    _ => new DiscoveredDevice { Id = p.Id },
                    (_, d) => d);
                dev.Name = p.Name ?? "";
                dev.User = p.User ?? "";
                dev.Port = p.Port;
                dev.Busy = p.Busy;
                // 同一台電腦有多張網卡時，優先保留與本機同網段的位址
                if (dev.Address.Equals(IPAddress.None) || DateTime.Now - dev.LastSeen > TimeSpan.FromSeconds(15) || PreferAddress(from.Address, dev.Address))
                    dev.Address = from.Address;
                dev.LastSeen = DateTime.Now;
                DeviceUpdated?.Invoke(dev);
                break;
            }
            case "bye":
                if (!string.IsNullOrEmpty(p.Id) && _devices.TryRemove(p.Id, out var removed))
                {
                    removed.LastSeen = DateTime.MinValue;
                    DeviceUpdated?.Invoke(removed);
                }
                break;
        }
    }

    private static bool PreferAddress(IPAddress candidate, IPAddress current)
    {
        if (candidate.Equals(current)) return false;
        bool candLocal = IsOnLocalSubnet(candidate), curLocal = IsOnLocalSubnet(current);
        return candLocal && !curLocal;
    }

    private static bool IsOnLocalSubnet(IPAddress addr)
    {
        if (IPAddress.IsLoopback(addr)) return false;
        foreach (var (ip, mask) in LocalIPv4())
        {
            var a = ip.GetAddressBytes(); var b = addr.GetAddressBytes(); var m = mask.GetAddressBytes();
            bool same = true;
            for (int i = 0; i < 4; i++) if ((a[i] & m[i]) != (b[i] & m[i])) { same = false; break; }
            if (same) return true;
        }
        return false;
    }

    public static IEnumerable<(IPAddress ip, IPAddress mask)> LocalIPv4()
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); } catch { yield break; }
        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); } catch { continue; }
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.IPv4Mask == null || ua.Address.ToString().StartsWith("169.254.")) continue;
                yield return (ua.Address, ua.IPv4Mask);
            }
        }
    }

    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        var set = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var (ip, mask) in LocalIPv4())
        {
            var a = ip.GetAddressBytes(); var m = mask.GetAddressBytes();
            var b = new byte[4];
            for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
            set.Add(new IPAddress(b));
        }
        set.Add(IPAddress.Loopback); // 方便同一台電腦測試
        return set;
    }

    private async Task SendToAllAsync(Packet p)
    {
        var udp = _udp;
        if (udp == null) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(p);
        foreach (var target in BroadcastTargets())
        {
            try { await udp.SendAsync(bytes, new IPEndPoint(target, _port)).ConfigureAwait(false); }
            catch { }
        }
    }

    public void Dispose()
    {
        var a = _advert;
        if (a != null && _udp != null)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new Packet { Type = "bye", Id = a.Id });
                foreach (var t in BroadcastTargets()) _udp.Send(bytes, new IPEndPoint(t, _port));
            }
            catch { }
        }
        _cts.Cancel();
        _udp?.Dispose();
        _udp = null;
    }
}
