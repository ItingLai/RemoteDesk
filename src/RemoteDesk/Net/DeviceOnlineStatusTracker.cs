using System.Collections.Concurrent;
using System.Net;
using RemoteDesk.Common;

namespace RemoteDesk.Net;

/// <summary>
/// 統一追蹤已連線／收藏／最近設備的即時在線狀態（整合主動工作階段、區網 UDP 廣播與中央 Signaling Server 查詢）。
/// </summary>
internal sealed class DeviceOnlineStatusTracker
{
    internal static readonly TimeSpan LanOnlineWindow = TimeSpan.FromSeconds(25);
    internal static readonly TimeSpan RemoteCacheTtl = TimeSpan.FromSeconds(120);
    internal static readonly TimeSpan LanByeSuppressionWindow = TimeSpan.FromSeconds(95);
    private static readonly TimeSpan PerDeviceQueryTimeout = TimeSpan.FromSeconds(5);

    private sealed record RemoteDeviceState(bool IsOnline, DateTime? ServerLastSeenUtc, DateTime QueriedAtUtc);
    private sealed record LanByeState(DateTime SuppressUntilUtc, DateTime? BaselineServerLastSeenUtc);

    private readonly ConcurrentDictionary<string, RemoteDeviceState> _remoteStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LanByeState> _lanByeSuppressed = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    internal static bool IsValidDeviceId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length == 9 && id.All(char.IsDigit);

    internal static string? TryGetDeviceId(RecentEntry entry)
    {
        if (IsValidDeviceId(entry.DeviceId)) return entry.DeviceId;
        if (IsValidDeviceId(entry.Key)) return entry.Key;
        return null;
    }

    internal static bool MatchesLanDevice(RecentEntry entry, DiscoveredDevice d)
    {
        if ((!string.IsNullOrEmpty(entry.DeviceId) && d.Id == entry.DeviceId) ||
            d.Id == entry.Key)
        {
            return true;
        }

        if (d.Address.Equals(IPAddress.None))
        {
            return false;
        }

        string ip = d.Address.ToString();
        string ep = d.EndPoint.ToString();
        return ip == entry.Key ||
               ep == entry.Key ||
               (!string.IsNullOrEmpty(entry.LastAddress) && (ip == entry.LastAddress || ep == entry.LastAddress));
    }

    /// <summary>
    /// 當區網 UDP 探索收到裝置宣告 ("a") 或離線 ("bye") 時更新本地抑制狀態，避免剛離線的區網裝置在 Signaling 心跳超時前誤判為在線。
    /// </summary>
    internal void OnLanDeviceUpdated(DiscoveredDevice device, DateTime? utcNow = null)
    {
        if (string.IsNullOrEmpty(device.Id)) return;

        DateTime nowUtc = utcNow ?? DateTime.UtcNow;
        if (device.LastSeen == DateTime.MinValue)
        {
            DateTime? baseline = _remoteStates.TryGetValue(device.Id, out var existing)
                ? existing.ServerLastSeenUtc
                : null;
            _lanByeSuppressed[device.Id] = new LanByeState(nowUtc + LanByeSuppressionWindow, baseline);
            if (existing != null)
            {
                _remoteStates[device.Id] = existing with { IsOnline = false, QueriedAtUtc = nowUtc };
            }
        }
        else
        {
            _lanByeSuppressed.TryRemove(device.Id, out _);
        }
    }

    internal bool IsDeviceOnline(
        RecentEntry entry,
        bool isInActiveSession,
        IReadOnlyCollection<DiscoveredDevice> lanDevices,
        string? selfDeviceId = null,
        DateTime? nowLocal = null,
        DateTime? nowUtc = null)
    {
        if (isInActiveSession) return true;

        DateTime localNow = nowLocal ?? DateTime.Now;
        if (lanDevices.Any(d =>
                d.LastSeen != DateTime.MinValue &&
                (localNow - d.LastSeen < LanOnlineWindow) &&
                MatchesLanDevice(entry, d)))
        {
            return true;
        }

        string? deviceId = TryGetDeviceId(entry);
        if (string.IsNullOrEmpty(deviceId) ||
            (!string.IsNullOrEmpty(selfDeviceId) && string.Equals(deviceId, selfDeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return IsRemoteCacheOnline(deviceId, nowUtc ?? DateTime.UtcNow);
    }

    /// <summary>
    /// 以有界並行向 Signaling Server 查詢所有非區網已紀錄設備之最新在線狀態；若有任一設備狀態變化則回傳 true。
    /// </summary>
    internal async Task<bool> RefreshRemoteStatusesAsync(
        SignalingClient? client,
        IEnumerable<RecentEntry> entries,
        IReadOnlyCollection<DiscoveredDevice> lanDevices,
        string? selfDeviceId = null,
        DateTime? nowLocal = null,
        DateTime? nowUtc = null,
        CancellationToken ct = default)
    {
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (client == null)
            {
                bool hadOnline = _remoteStates.Values.Any(s => s.IsOnline);
                _remoteStates.Clear();
                return hadOnline;
            }

            DateTime localNow = nowLocal ?? DateTime.Now;
            DateTime utcNow = nowUtc ?? DateTime.UtcNow;

            var lanOnlineIds = new HashSet<string>(
                lanDevices
                    .Where(d => d.LastSeen != DateTime.MinValue && (localNow - d.LastSeen < LanOnlineWindow))
                    .Select(d => d.Id),
                StringComparer.OrdinalIgnoreCase);

            var targetIds = entries
                .Select(TryGetDeviceId)
                .Where(id => !string.IsNullOrEmpty(id) &&
                             !string.Equals(id, selfDeviceId, StringComparison.OrdinalIgnoreCase) &&
                             !lanOnlineIds.Contains(id!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Cast<string>()
                .ToList();

            if (targetIds.Count == 0)
            {
                return false;
            }

            using var concurrency = new SemaphoreSlim(4, 4);
            var changedFlag = 0;

            var tasks = targetIds.Select(async id =>
            {
                await concurrency.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    bool wasOnline = IsRemoteCacheOnline(id, utcNow);
                    using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    reqCts.CancelAfter(PerDeviceQueryTimeout);

                    var result = await client.ResolveDeviceStatusAsync(id, reqCts.Token).ConfigureAwait(false);
                    if (!result.Reachable) return;

                    ApplyServerResult(id, result.Endpoint, utcNow);

                    bool isOnline = IsRemoteCacheOnline(id, utcNow);
                    if (wasOnline != isOnline)
                    {
                        Interlocked.Exchange(ref changedFlag, 1);
                    }
                }
                finally
                {
                    concurrency.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return Volatile.Read(ref changedFlag) == 1;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private bool IsRemoteCacheOnline(string deviceId, DateTime utcNow)
    {
        if (_lanByeSuppressed.TryGetValue(deviceId, out var bye))
        {
            if (utcNow < bye.SuppressUntilUtc)
            {
                return false;
            }
            _lanByeSuppressed.TryRemove(deviceId, out _);
        }

        if (_remoteStates.TryGetValue(deviceId, out var state))
        {
            return state.IsOnline && (utcNow - state.QueriedAtUtc <= RemoteCacheTtl);
        }

        return false;
    }

    private void ApplyServerResult(string deviceId, DeviceEndpointInfo? ep, DateTime utcNow)
    {
        if (ep == null)
        {
            _lanByeSuppressed.TryRemove(deviceId, out _);
            _remoteStates[deviceId] = new RemoteDeviceState(false, null, utcNow);
            return;
        }

        if (_lanByeSuppressed.TryGetValue(deviceId, out var bye))
        {
            if (!ep.IsOnline || (bye.BaselineServerLastSeenUtc.HasValue && ep.LastSeen.HasValue && ep.LastSeen.Value > bye.BaselineServerLastSeenUtc.Value))
            {
                _lanByeSuppressed.TryRemove(deviceId, out _);
            }
            else if (!bye.BaselineServerLastSeenUtc.HasValue)
            {
                _lanByeSuppressed[deviceId] = bye with
                {
                    BaselineServerLastSeenUtc = ep.LastSeen ?? DateTime.MinValue
                };
            }
        }

        _remoteStates[deviceId] = new RemoteDeviceState(ep.IsOnline, ep.LastSeen, utcNow);
    }
}
