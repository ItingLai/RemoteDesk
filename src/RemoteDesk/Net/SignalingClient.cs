using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteDesk.Common;

namespace RemoteDesk.Net;

internal sealed class DeviceEndpointInfo
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string PublicIp { get; set; } = "";
    public int TcpPort { get; set; } = Protocol.DefaultTcpPort;
    public List<string> LocalIps { get; set; } = new();
    public bool IsOnline { get; set; }
    public DateTime? LastSeen { get; set; }
}

internal sealed record DeviceStatusQueryResult(bool Reachable, DeviceEndpointInfo? Endpoint);

internal sealed class IncomingSessionInfo
{
    public string SessionId { get; set; } = "";
    public string SourceDeviceId { get; set; } = "";
    public string TargetDeviceId { get; set; } = "";
    public string SessionToken { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

internal sealed class SignalingMessageInfo
{
    public string SessionId { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string SenderDeviceId { get; set; } = "";
    public string ReceiverDeviceId { get; set; } = "";
    public string FromDeviceId { get; set; } = "";
    public string ToDeviceId { get; set; } = "";
    public string MessageType { get; set; } = "signal";
    public long SequenceNumber { get; set; }
    public string Data { get; set; } = "";
    public long Timestamp { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpireAt { get; set; }

    public string EffectiveSenderId => !string.IsNullOrEmpty(SenderDeviceId) ? SenderDeviceId : FromDeviceId;
    public string EffectiveReceiverId => !string.IsNullOrEmpty(ReceiverDeviceId) ? ReceiverDeviceId : ToDeviceId;
    public bool IsExpired(DateTime utcNow) => ExpireAt != default && ExpireAt != DateTime.MinValue && ExpireAt <= utcNow;
}

internal enum SignalingCallStatus
{
    Ok = 0,
    TimedOutOrTransportError = 1,
    SessionExpired = 2,
    Rejected = 3
}

internal readonly record struct SignalingPostResult(bool Succeeded, SignalingCallStatus Status, string ErrorDetail = "");
internal readonly record struct SignalingPollResult(List<SignalingMessageInfo> Messages, SignalingCallStatus Status, string ErrorDetail = "");
internal readonly record struct SignalingAckResult(bool Succeeded, SignalingCallStatus Status, string ErrorDetail = "");

internal sealed class SignalingClient : IDisposable
{
    private readonly string _serverBaseUrl;
    private readonly DeviceIdentity _identity;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, long> _sessionSeqs = new(StringComparer.OrdinalIgnoreCase);
    private Task? _heartbeatTask;
    private string? _authToken;
    private TimeSpan _estimatedServerClockOffset = TimeSpan.Zero;
    private bool _disposed;

    public TimeSpan EstimatedServerClockOffset => _estimatedServerClockOffset;

    public int TcpPort { get; set; } = Protocol.DefaultTcpPort;

    private static readonly HashSet<string> BannedSinkholeIps = new(StringComparer.OrdinalIgnoreCase)
    {
        "208.91.112.55", // Fortinet FortiGuard DNS Sinkhole Block Page
        "208.91.112.52",
        "208.91.112.53",
        "208.91.112.54",
        "0.0.0.0"
    };

    public static HttpClient CreateResilientHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var port = context.DnsEndPoint.Port;

                var ip = await ResolveHostAddressAsync(host, ct).ConfigureAwait(false);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.NoDelay = true;
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(ip, port), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(8)
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public static async Task<IPAddress> ResolveHostAddressAsync(string host, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var parsedIp))
        {
            return parsedIp;
        }

        // 1. 先嘗試系統預設 DNS 解析 (若未受路由器攔截污染)
        try
        {
            var systemAddrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            var ipv4 = systemAddrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 != null && !BannedSinkholeIps.Contains(ipv4.ToString()))
            {
                if (!IPAddress.IsLoopback(ipv4) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    return ipv4;
                }
            }
        }
        catch { }

        // 2. 若系統 DNS 被企業防火牆 (例如 Fortinet 208.91.112.55) 污染或攔截，改以 DoH (DNS-over-HTTPS) 查詢
        try
        {
            var dohIp = await QueryDoHAsync(host, ct).ConfigureAwait(false);
            if (dohIp != null && !BannedSinkholeIps.Contains(dohIp.ToString()))
            {
                return dohIp;
            }
        }
        catch { }

        throw new SocketException((int)SocketError.HostNotFound);
    }

    private static async Task<IPAddress?> QueryDoHAsync(string host, CancellationToken ct)
    {
        // 透過 Cloudflare 1.1.1.1 / 1.0.0.1 DoH 加密查詢，完全避開本地 UDP 53 DNS 攔截
        var dohUrls = new[]
        {
            $"https://1.1.1.1/dns-query?name={Uri.EscapeDataString(host)}",
            $"https://1.0.0.1/dns-query?name={Uri.EscapeDataString(host)}"
        };

        using var dohClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        dohClient.DefaultRequestHeaders.Add("Accept", "application/dns-json");

        foreach (var url in dohUrls)
        {
            try
            {
                using var resp = await dohClient.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
                if (doc.RootElement.TryGetProperty("Answer", out var answer) && answer.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in answer.EnumerateArray())
                    {
                        if (elem.TryGetProperty("type", out var typeProp) && typeProp.GetInt32() == 1 &&
                            elem.TryGetProperty("data", out var dataProp))
                        {
                            string ipStr = dataProp.GetString() ?? "";
                            if (IPAddress.TryParse(ipStr, out var ip) && !BannedSinkholeIps.Contains(ipStr))
                            {
                                Log.Info($"[DoH] 透過加密 DNS 成功解析 {host} -> {ipStr}");
                                return ip;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        return null;
    }

    public SignalingClient(string serverBaseUrl, DeviceIdentity identity, HttpClient? http = null)
    {
        _serverBaseUrl = serverBaseUrl.TrimEnd('/');
        _identity = identity;
        _http = http ?? CreateResilientHttpClient();
    }

    public bool IsRegistered { get; private set; }
    public bool IsOnline { get; private set; }

    /// <summary>
    /// 向 Signaling Server 註冊新裝置（帶 ECDSA 數位簽章）。
    /// </summary>
    public async Task<bool> RegisterAsync(CancellationToken ct = default)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        string version = typeof(SignalingClient).Assembly.GetName().Version?.ToString(3) ?? "2.0.1";
        var localIps = LanDiscovery.LocalIPv4().Select(x => x.ip.ToString()).ToList();

        // 訊息格式: REGISTER:{deviceId}:{deviceName}:{publicKey}:{version}:{timestamp}:{nonce}
        string msg = $"REGISTER:{_identity.DeviceId}:{_identity.DeviceName}:{_identity.PublicKeyBase64}:{version}:{timestamp}:{nonce}";
        byte[] sig = _identity.SignData(Encoding.UTF8.GetBytes(msg));

        var req = new
        {
            deviceId = _identity.DeviceId,
            deviceName = _identity.DeviceName,
            publicKey = _identity.PublicKeyBase64,
            version,
            tcpPort = TcpPort,
            localIps,
            timestamp,
            nonce,
            signature = Convert.ToBase64String(sig)
        };

        try
        {
            var resp = await _http.PostAsJsonAsync($"{_serverBaseUrl}/api/devices/register", req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                IsRegistered = true;
                IsOnline = true;
                Log.Info($"裝置 {_identity.DeviceId} 成功於 Signaling Server 註冊！");
                return true;
            }
            var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            Log.Warn($"裝置註冊失敗 ({resp.StatusCode}): {err}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"連線至 Signaling Server 註冊失敗: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 裝置驗證身分，取得短效授權 Token。
    /// </summary>
    public async Task<string?> AuthenticateAsync(CancellationToken ct = default)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        string msg = $"AUTH:{_identity.DeviceId}:{timestamp}:{nonce}";
        byte[] sig = _identity.SignData(Encoding.UTF8.GetBytes(msg));

        var req = new
        {
            deviceId = _identity.DeviceId,
            timestamp,
            nonce,
            signature = Convert.ToBase64String(sig)
        };

        try
        {
            var resp = await _http.PostAsJsonAsync($"{_serverBaseUrl}/api/devices/authenticate", req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
                _authToken = doc.GetProperty("token").GetString();
                IsOnline = true;
                return _authToken;
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"向 Signaling Server 身分驗證失敗: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 查詢目標裝置公開資訊。
    /// </summary>
    public async Task<JsonElement?> GetDeviceAsync(string targetDeviceId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{_serverBaseUrl}/api/devices/{targetDeviceId}", ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return await resp.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"查詢裝置 {targetDeviceId} 失敗: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 發起端對端連線 Session 請求。
    /// </summary>
    public async Task<string?> CreateSessionAsync(string targetDeviceId, CancellationToken ct = default)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        string msg = $"SESSION:{_identity.DeviceId}:{targetDeviceId}:{timestamp}:{nonce}";
        byte[] sig = _identity.SignData(Encoding.UTF8.GetBytes(msg));

        var req = new
        {
            sourceDeviceId = _identity.DeviceId,
            targetDeviceId,
            timestamp,
            nonce,
            signature = Convert.ToBase64String(sig)
        };

        try
        {
            var resp = await _http.PostAsJsonAsync($"{_serverBaseUrl}/api/sessions", req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
                return doc.GetProperty("sessionId").GetString();
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"建立工作階段失敗: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 結束連線工作階段。
    /// </summary>
    public async Task<bool> EndSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_serverBaseUrl}/api/sessions/{sessionId}");
            req.Headers.Add("X-Device-Id", _identity.DeviceId);
            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 撤銷本機裝置身分。
    /// </summary>
    public async Task<bool> RevokeAsync(CancellationToken ct = default)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        string msg = $"REVOKE:{_identity.DeviceId}:{timestamp}:{nonce}";
        byte[] sig = _identity.SignData(Encoding.UTF8.GetBytes(msg));

        var req = new
        {
            deviceId = _identity.DeviceId,
            timestamp,
            nonce,
            signature = Convert.ToBase64String(sig)
        };

        try
        {
            var resp = await _http.PostAsJsonAsync($"{_serverBaseUrl}/api/devices/{_identity.DeviceId}/revoke", req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                IsRegistered = false;
                IsOnline = false;
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 發送單次心跳。
    /// </summary>
    public async Task<bool> SendHeartbeatAsync(CancellationToken ct = default)
    {
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var localIps = LanDiscovery.LocalIPv4().Select(x => x.ip.ToString()).ToList();

        string msg = $"HEARTBEAT:{_identity.DeviceId}:{timestamp}:{nonce}";
        byte[] sig = _identity.SignData(Encoding.UTF8.GetBytes(msg));

        var req = new
        {
            deviceId = _identity.DeviceId,
            tcpPort = TcpPort,
            localIps,
            timestamp,
            nonce,
            signature = Convert.ToBase64String(sig)
        };

        try
        {
            var resp = await _http.PostAsJsonAsync($"{_serverBaseUrl}/api/devices/heartbeat", req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                IsOnline = true;
                return true;
            }

            // 若伺服器回傳 400 或 404（例如伺服器重啟導致裝置記憶體資料遺失），自動補送註冊
            if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest || resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Log.Info("[SignalingClient] 伺服器回報無此裝置（可能伺服器重啟），正在自動重新註冊...");
                bool reReg = await RegisterAsync(ct).ConfigureAwait(false);
                if (reReg)
                {
                    IsOnline = true;
                    return true;
                }
            }

            return false;
        }
        catch
        {
            IsOnline = false;
            return false;
        }
    }

    /// <summary>
    /// 向 Signaling Server 查詢目標裝置連線端點 (公網 IP, 連接埠, 內網 IP, 在線狀態)。
    /// </summary>
    public async Task<DeviceEndpointInfo?> ResolveDeviceAsync(string targetDeviceId, CancellationToken ct = default)
    {
        var result = await ResolveDeviceStatusAsync(targetDeviceId, ct).ConfigureAwait(false);
        return result.Endpoint;
    }

    /// <summary>
    /// 向 Signaling Server 查詢目標裝置公開端點與連線狀態，同時區分「伺服器不可達」與「裝置未註冊/離線」。
    /// </summary>
    public async Task<DeviceStatusQueryResult> ResolveDeviceStatusAsync(string targetDeviceId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{_serverBaseUrl}/api/devices/{targetDeviceId}", ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return new DeviceStatusQueryResult(true, null);
            }

            if (resp.IsSuccessStatusCode)
            {
                var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
                DateTime? lastSeenUtc = null;
                if (doc.TryGetProperty("lastSeen", out var dLastSeen) && dLastSeen.TryGetDateTime(out var parsedLastSeen))
                {
                    lastSeenUtc = parsedLastSeen.Kind == DateTimeKind.Unspecified
                        ? DateTime.SpecifyKind(parsedLastSeen, DateTimeKind.Utc)
                        : parsedLastSeen.ToUniversalTime();
                }

                var info = new DeviceEndpointInfo
                {
                    DeviceId = doc.TryGetProperty("deviceId", out var dId) ? dId.GetString() ?? "" : "",
                    DeviceName = doc.TryGetProperty("deviceName", out var dName) ? dName.GetString() ?? "" : "",
                    PublicKey = doc.TryGetProperty("publicKey", out var dPub) ? dPub.GetString() ?? "" : "",
                    PublicIp = doc.TryGetProperty("publicIp", out var dIp) ? dIp.GetString() ?? "" : "",
                    TcpPort = doc.TryGetProperty("tcpPort", out var dPort) ? dPort.GetInt32() : Protocol.DefaultTcpPort,
                    IsOnline = doc.TryGetProperty("onlineStatus", out var dOn) && dOn.GetBoolean(),
                    LastSeen = lastSeenUtc
                };
                if (doc.TryGetProperty("localIps", out var dLips) && dLips.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in dLips.EnumerateArray())
                    {
                        var s = elem.GetString();
                        if (!string.IsNullOrEmpty(s)) info.LocalIps.Add(s);
                    }
                }
                return new DeviceStatusQueryResult(true, info);
            }
            return new DeviceStatusQueryResult(false, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new DeviceStatusQueryResult(false, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"向伺服器查詢裝置 {targetDeviceId} 失敗: {ex.Message}");
            return new DeviceStatusQueryResult(false, null);
        }
    }

    public DeviceIdentity Identity => _identity;

    /// <summary>
    /// 查詢是否有主控端發起給本機的連線請求。
    /// </summary>
    public async Task<List<IncomingSessionInfo>> GetIncomingSessionsAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{_serverBaseUrl}/api/devices/{_identity.DeviceId}/incoming-sessions", ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var list = await resp.Content.ReadFromJsonAsync<List<IncomingSessionInfo>>(ct).ConfigureAwait(false);
                return list ?? new();
            }
            return new();
        }
        catch (Exception ex)
        {
            Log.Warn($"查詢待處理連線失敗: {ex.Message}");
            return new();
        }
    }

    /// <summary>
    /// 發送端對端 ICE 訊號或加密中繼訊息至 Signaling Server。
    /// </summary>
    public Task<bool> PostSignalAsync(string sessionId, string toDeviceId, string data, CancellationToken ct = default) =>
        PostSignalAsync(sessionId, toDeviceId, data, "signal", ct);

    /// <summary>
    /// 發送具備完整 Session 隔離與訊息識別欄位之訊號至 Signaling Server。
    /// </summary>
    public async Task<bool> PostSignalAsync(
        string sessionId,
        string toDeviceId,
        string data,
        string messageType,
        CancellationToken ct = default,
        string? messageId = null,
        DateTime? expireAt = null)
    {
        var detailed = await PostSignalDetailedAsync(sessionId, toDeviceId, data, messageType, ct, messageId, expireAt).ConfigureAwait(false);
        return detailed.Succeeded;
    }

    internal async Task<SignalingPostResult> PostSignalDetailedAsync(
        string sessionId,
        string toDeviceId,
        string data,
        string messageType,
        CancellationToken ct = default,
        string? messageId = null,
        DateTime? expireAt = null)
    {
        var now = DateTime.UtcNow;
        string resolvedMessageId = string.IsNullOrWhiteSpace(messageId) ? Guid.NewGuid().ToString("N") : messageId;
        long seq = _sessionSeqs.AddOrUpdate(sessionId, 1L, (_, prev) => prev + 1);
        DateTime resolvedExpireAt = expireAt ?? now.AddMinutes(2);

        var msg = new
        {
            sessionId,
            messageId = resolvedMessageId,
            senderDeviceId = _identity.DeviceId,
            receiverDeviceId = toDeviceId,
            fromDeviceId = _identity.DeviceId,
            toDeviceId,
            messageType = string.IsNullOrWhiteSpace(messageType) ? "signal" : messageType,
            sequenceNumber = seq,
            data,
            timestamp = new DateTimeOffset(now).ToUnixTimeSeconds(),
            createdAt = now,
            expireAt = resolvedExpireAt
        };

        try
        {
            var resp = await _http.PostAsJsonAsync($"{_serverBaseUrl}/api/sessions/{sessionId}/signal", msg, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return new SignalingPostResult(true, SignalingCallStatus.Ok);
            }

            string errBody = "";
            try { errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
            var status = ClassifyHttpFailureStatus(resp.StatusCode, errBody);
            Log.Warn($"發送訊號遭拒或失敗 (Session={sessionId}, Status={(int)resp.StatusCode}, Classified={status}): {errBody}");
            return new SignalingPostResult(false, status, errBody);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"發送訊號失敗 (Session={sessionId}, MessageId={resolvedMessageId}, Seq={seq}): {ex.Message}");
            return new SignalingPostResult(false, SignalingCallStatus.TimedOutOrTransportError, ex.Message);
        }
    }

    /// <summary>
    /// 獲取指定工作階段中尚未 ACK 且未過期之訊號訊息。
    /// </summary>
    public async Task<List<SignalingMessageInfo>> GetSignalsAsync(string sessionId, CancellationToken ct = default)
    {
        var detailed = await GetSignalsDetailedAsync(sessionId, ct).ConfigureAwait(false);
        return detailed.Messages;
    }

    internal async Task<SignalingPollResult> GetSignalsDetailedAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{_serverBaseUrl}/api/sessions/{sessionId}/signal?deviceId={_identity.DeviceId}", ct).ConfigureAwait(false);
            if (resp.Headers.Date.HasValue)
            {
                _estimatedServerClockOffset = resp.Headers.Date.Value.UtcDateTime - DateTime.UtcNow;
            }
            if (resp.Headers.TryGetValues("X-Session-Active", out var activeVals))
            {
                string? activeVal = activeVals.FirstOrDefault();
                if (string.Equals(activeVal, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return new SignalingPollResult(new(), SignalingCallStatus.SessionExpired, "Server marked X-Session-Active=false");
                }
            }
            if (resp.IsSuccessStatusCode)
            {
                var list = await resp.Content.ReadFromJsonAsync<List<SignalingMessageInfo>>(ct).ConfigureAwait(false);
                return new SignalingPollResult(list ?? new(), SignalingCallStatus.Ok);
            }

            string errBody = "";
            try { errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
            return new SignalingPollResult(new(), ClassifyHttpFailureStatus(resp.StatusCode, errBody), errBody);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"接收訊號失敗 (Session={sessionId}): {ex.Message}");
            return new SignalingPollResult(new(), SignalingCallStatus.TimedOutOrTransportError, ex.Message);
        }
    }

    /// <summary>
    /// 向 Signaling Server 回報已完成驗證並處理之訊息 ACK，避免讀取後尚未處理即遺失。
    /// </summary>
    public async Task<bool> AckSignalsAsync(string sessionId, IEnumerable<string> messageIds, CancellationToken ct = default)
    {
        var detailed = await AckSignalsDetailedAsync(sessionId, messageIds, ct).ConfigureAwait(false);
        return detailed.Succeeded;
    }

    internal async Task<SignalingAckResult> AckSignalsDetailedAsync(string sessionId, IEnumerable<string> messageIds, CancellationToken ct = default)
    {
        var ids = messageIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ids.Count == 0)
            return new SignalingAckResult(true, SignalingCallStatus.Ok);

        var payload = new
        {
            deviceId = _identity.DeviceId,
            messageIds = ids
        };

        try
        {
            var resp = await _http.PostAsJsonAsync($"{_serverBaseUrl}/api/sessions/{sessionId}/signal/ack", payload, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                return new SignalingAckResult(true, SignalingCallStatus.Ok);
            }

            string errBody = "";
            try { errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
            return new SignalingAckResult(false, ClassifyHttpFailureStatus(resp.StatusCode, errBody), errBody);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"回報訊號 ACK 失敗 (Session={sessionId}, Count={ids.Count}): {ex.Message}");
            return new SignalingAckResult(false, SignalingCallStatus.TimedOutOrTransportError, ex.Message);
        }
    }

    private static SignalingCallStatus ClassifyHttpFailureStatus(HttpStatusCode code, string? body)
    {
        if (code == HttpStatusCode.NotFound || code == HttpStatusCode.Gone)
            return SignalingCallStatus.SessionExpired;

        if (!string.IsNullOrWhiteSpace(body) &&
            (body.Contains("Session 不存在", StringComparison.OrdinalIgnoreCase) ||
             body.Contains("已結束", StringComparison.OrdinalIgnoreCase) ||
             body.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
             body.Contains("closed", StringComparison.OrdinalIgnoreCase) ||
             body.Contains("工作階段不存在", StringComparison.OrdinalIgnoreCase)))
        {
            return SignalingCallStatus.SessionExpired;
        }

        if (code == HttpStatusCode.RequestTimeout ||
            code == HttpStatusCode.BadGateway ||
            code == HttpStatusCode.ServiceUnavailable ||
            code == HttpStatusCode.GatewayTimeout ||
            (int)code >= 500)
        {
            return SignalingCallStatus.TimedOutOrTransportError;
        }

        return SignalingCallStatus.Rejected;
    }

    /// <summary>
    /// 啟動背景定期心跳迴路（每 30 秒發送一次）。
    /// </summary>
    public void StartHeartbeatLoop(TimeSpan? interval = null)
    {
        if (_heartbeatTask != null) return;
        var step = interval ?? TimeSpan.FromSeconds(30);

        _heartbeatTask = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await SendHeartbeatAsync(_cts.Token).ConfigureAwait(false);
                    await Task.Delay(step, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Warn($"心跳發送失敗: {ex.Message}");
                    try { await Task.Delay(step, _cts.Token).ConfigureAwait(false); } catch { break; }
                }
            }
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _http.Dispose();
    }
}
