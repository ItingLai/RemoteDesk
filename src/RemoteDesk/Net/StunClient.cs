using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RemoteDesk.Common;

namespace RemoteDesk.Net;

public enum StunQueryFailureReason
{
    None = 0,
    DnsFailed = 1,
    Timeout = 2,
    InvalidResponse = 3,
    ServerError = 4,
    SocketError = 5,
    Cancelled = 6
}

public enum NatMappingBehavior
{
    Unknown = 0,
    InsufficientData = 1,
    EndpointIndependent = 2,
    AddressOrPortDependent = 3,
    MultiWanOrDifferentPublicIp = 4
}

/// <summary>
/// 單一 Socket 對單一 STUN Server 的探測紀錄與診斷資訊。
/// </summary>
internal sealed class StunProbeRecord
{
    public string ConnectionAttemptId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string InterfaceId { get; init; } = "";
    public string LocalIPv4 { get; init; } = "";
    public int LocalPort { get; init; }
    public string SocketId { get; init; } = "";
    public string StunServer { get; init; } = "";
    public IPEndPoint? ResolvedServerEndPoint { get; set; }
    public IPEndPoint? MappedEndPoint { get; set; }
    public string MappedPublicIp => MappedEndPoint?.Address.ToString() ?? "";
    public int MappedPublicPort => MappedEndPoint?.Port ?? 0;
    public double RttMs { get; set; }
    public bool Succeeded { get; set; }
    public StunQueryFailureReason FailureReason { get; set; }
    public int StunErrorCode { get; set; }
    public string Detail { get; set; } = "";
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 同一 Socket 對多個 STUN Server 探測後的 NAT 映射行為觀察（不單憑 Port 差異即誤判為雙 WAN）。
/// </summary>
internal sealed class NatMappingObservation
{
    public string SocketId { get; init; } = "";
    public string InterfaceId { get; init; } = "";
    public string LocalIPv4 { get; init; } = "";
    public int SuccessfulProbeCount { get; init; }
    public bool PublicIpChanged { get; init; }
    public bool PublicPortChanged { get; init; }
    public NatMappingBehavior Behavior { get; init; }
    public IReadOnlyList<string> DistinctPublicIps { get; init; } = Array.Empty<string>();
    public IReadOnlyList<int> DistinctPublicPorts { get; init; } = Array.Empty<int>();
    public IReadOnlyList<IPEndPoint> DistinctMappedEndPoints { get; init; } = Array.Empty<IPEndPoint>();
    public IReadOnlyDictionary<string, IPEndPoint> PerDestinationMappings { get; init; } = new Dictionary<string, IPEndPoint>();
}

internal static class NatMappingAnalyzer
{
    public static NatMappingObservation AnalyzeSocketMappings(
        string socketId,
        string interfaceId,
        string localIPv4,
        IEnumerable<StunProbeRecord> probes)
    {
        var successful = probes.Where(p => p.Succeeded && p.MappedEndPoint != null).ToList();
        var perDest = new Dictionary<string, IPEndPoint>(StringComparer.OrdinalIgnoreCase);
        var distinctIps = new List<string>();
        var distinctPorts = new List<int>();
        var distinctEps = new List<IPEndPoint>();

        foreach (var p in successful)
        {
            var ep = p.MappedEndPoint!;
            if (!string.IsNullOrEmpty(p.StunServer) && !perDest.ContainsKey(p.StunServer))
                perDest[p.StunServer] = ep;

            string ipStr = ep.Address.ToString();
            if (!distinctIps.Contains(ipStr, StringComparer.OrdinalIgnoreCase))
                distinctIps.Add(ipStr);
            if (!distinctPorts.Contains(ep.Port))
                distinctPorts.Add(ep.Port);
            if (!distinctEps.Contains(ep))
                distinctEps.Add(ep);
        }

        bool ipChanged = distinctIps.Count > 1;
        bool portChanged = distinctPorts.Count > 1;

        NatMappingBehavior behavior;
        if (successful.Count < 2)
        {
            behavior = NatMappingBehavior.InsufficientData;
        }
        else if (ipChanged)
        {
            behavior = NatMappingBehavior.MultiWanOrDifferentPublicIp;
        }
        else if (portChanged)
        {
            // 同一個 Socket 對不同 STUN 目標取得不同 Port，反映 NAT 對目的端映射敏感，但不能單以此斷言雙 WAN
            behavior = NatMappingBehavior.AddressOrPortDependent;
        }
        else
        {
            behavior = NatMappingBehavior.EndpointIndependent;
        }

        return new NatMappingObservation
        {
            SocketId = socketId,
            InterfaceId = interfaceId,
            LocalIPv4 = localIPv4,
            SuccessfulProbeCount = successful.Count,
            PublicIpChanged = ipChanged,
            PublicPortChanged = portChanged,
            Behavior = behavior,
            DistinctPublicIps = distinctIps,
            DistinctPublicPorts = distinctPorts,
            DistinctMappedEndPoints = distinctEps,
            PerDestinationMappings = perDest
        };
    }
}

internal readonly record struct StunResponseParseResult(
    bool MatchedTransactionId,
    IPEndPoint? MappedEndPoint,
    StunQueryFailureReason FailureReason,
    int ErrorCode,
    string? ErrorReasonPhrase);

/// <summary>
/// 輕量級 RFC 5389 / 8489 STUN 客戶端，支援單 Socket 多 STUN 並行探測、錯誤分類、RTT 量測與 NAT 映射觀察。
/// </summary>
internal static class StunClient
{
    public const uint MagicCookie = 0x2112A442;
    public const ushort BindingRequest = 0x0001;
    public const ushort BindingResponse = 0x0101;
    public const ushort BindingErrorResponse = 0x0111;

    public const ushort AttrMappedAddress = 0x0001;
    public const ushort AttrErrorCode = 0x0009;
    public const ushort AttrXorMappedAddress = 0x0020;

    public static readonly string[] DefaultStunServers = new[]
    {
        "stun.l.google.com:19302",
        "stun1.l.google.com:19302",
        "stun2.l.google.com:19302",
        "stun.cloudflare.com:3478"
    };

    /// <summary>
    /// 建立 20 位元組的 STUN Binding Request 封包。
    /// </summary>
    public static byte[] CreateBindingRequest(byte[]? transactionId = null)
    {
        var buffer = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), BindingRequest);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), MagicCookie);

        if (transactionId != null && transactionId.Length == 12)
        {
            Buffer.BlockCopy(transactionId, 0, buffer, 8, 12);
        }
        else
        {
            RandomNumberGenerator.Fill(buffer.AsSpan(8, 12));
        }

        return buffer;
    }

    /// <summary>
    /// 解析 STUN Binding Response 封包，提取 IPv4 公網 IP 與連接埠。
    /// </summary>
    public static IPEndPoint? ParseBindingResponse(ReadOnlySpan<byte> data, ReadOnlySpan<byte> expectedTransactionId)
    {
        var result = ParseBindingResult(data, expectedTransactionId);
        return result.FailureReason == StunQueryFailureReason.None ? result.MappedEndPoint : null;
    }

    /// <summary>
    /// 解析 STUN 成功或錯誤回應並進行失敗分類。
    /// </summary>
    public static StunResponseParseResult ParseBindingResult(ReadOnlySpan<byte> data, ReadOnlySpan<byte> expectedTransactionId)
    {
        if (data.Length < 20)
            return new(false, null, StunQueryFailureReason.InvalidResponse, 0, "Packet shorter than 20 bytes");

        ushort msgType = BinaryPrimitives.ReadUInt16BigEndian(data[0..2]);
        ushort msgLen = BinaryPrimitives.ReadUInt16BigEndian(data[2..4]);
        uint cookie = BinaryPrimitives.ReadUInt32BigEndian(data[4..8]);
        if (cookie != MagicCookie)
            return new(false, null, StunQueryFailureReason.InvalidResponse, 0, "Invalid STUN magic cookie");

        var txnId = data[8..20];
        if (expectedTransactionId.Length == 12 && !txnId.SequenceEqual(expectedTransactionId))
            return new(false, null, StunQueryFailureReason.InvalidResponse, 0, "Transaction ID mismatch");

        if (data.Length < 20 + msgLen)
            return new(true, null, StunQueryFailureReason.InvalidResponse, 0, "Truncated STUN payload");

        ReadOnlySpan<byte> attrs = data.Slice(20, msgLen);

        if (msgType == BindingErrorResponse)
        {
            var (code, phrase) = ParseErrorCodeAttribute(attrs);
            return new(true, null, StunQueryFailureReason.ServerError, code, phrase ?? $"STUN error {code}");
        }

        if (msgType != BindingResponse)
            return new(true, null, StunQueryFailureReason.InvalidResponse, 0, $"Unexpected STUN message type 0x{msgType:X4}");

        IPEndPoint? mappedAddress = null;
        IPEndPoint? xorMappedAddress = null;

        int offset = 0;
        while (offset + 4 <= attrs.Length)
        {
            ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(attrs.Slice(offset, 2));
            ushort attrLen = BinaryPrimitives.ReadUInt16BigEndian(attrs.Slice(offset + 2, 2));
            offset += 4;

            if (offset + attrLen > attrs.Length) break;
            var val = attrs.Slice(offset, attrLen);

            if (attrType == AttrXorMappedAddress)
            {
                var ep = ParseXorMappedAddress(val, cookie, txnId);
                if (ep != null && ep.AddressFamily == AddressFamily.InterNetwork)
                    xorMappedAddress = ep;
            }
            else if (attrType == AttrMappedAddress)
            {
                var ep = ParseMappedAddress(val);
                if (ep != null && ep.AddressFamily == AddressFamily.InterNetwork)
                    mappedAddress = ep;
            }

            int padding = (4 - (attrLen % 4)) % 4;
            offset += attrLen + padding;
        }

        var chosen = xorMappedAddress ?? mappedAddress;
        if (chosen == null || chosen.Port <= 0)
            return new(true, null, StunQueryFailureReason.InvalidResponse, 0, "No valid IPv4 mapped address attribute");

        return new(true, chosen, StunQueryFailureReason.None, 0, null);
    }

    private static (int code, string? phrase) ParseErrorCodeAttribute(ReadOnlySpan<byte> attrs)
    {
        int offset = 0;
        while (offset + 4 <= attrs.Length)
        {
            ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(attrs.Slice(offset, 2));
            ushort attrLen = BinaryPrimitives.ReadUInt16BigEndian(attrs.Slice(offset + 2, 2));
            offset += 4;
            if (offset + attrLen > attrs.Length) break;
            var val = attrs.Slice(offset, attrLen);

            if (attrType == AttrErrorCode && val.Length >= 4)
            {
                int errClass = val[2] & 0x07;
                int errNum = val[3];
                int code = errClass * 100 + errNum;
                string? phrase = val.Length > 4 ? Encoding.UTF8.GetString(val.Slice(4)) : null;
                return (code, phrase);
            }

            int padding = (4 - (attrLen % 4)) % 4;
            offset += attrLen + padding;
        }
        return (0, null);
    }

    private static IPEndPoint? ParseXorMappedAddress(ReadOnlySpan<byte> val, uint cookie, ReadOnlySpan<byte> txnId)
    {
        if (val.Length < 8) return null;
        byte family = val[1];

        if (family == 0x01) // IPv4
        {
            ushort xPort = BinaryPrimitives.ReadUInt16BigEndian(val.Slice(2, 2));
            int port = xPort ^ (ushort)(cookie >> 16);

            uint xIp = BinaryPrimitives.ReadUInt32BigEndian(val.Slice(4, 4));
            uint ipUint = xIp ^ cookie;

            Span<byte> ipBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(ipBytes, ipUint);
            return new IPEndPoint(new IPAddress(ipBytes), port);
        }
        else if (family == 0x02 && val.Length >= 20) // IPv6
        {
            ushort xPort = BinaryPrimitives.ReadUInt16BigEndian(val.Slice(2, 2));
            int port = xPort ^ (ushort)(cookie >> 16);

            Span<byte> xorMask = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(xorMask[0..4], cookie);
            txnId.CopyTo(xorMask[4..16]);

            Span<byte> ipBytes = stackalloc byte[16];
            for (int i = 0; i < 16; i++)
            {
                ipBytes[i] = (byte)(val[4 + i] ^ xorMask[i]);
            }
            return new IPEndPoint(new IPAddress(ipBytes), port);
        }

        return null;
    }

    private static IPEndPoint? ParseMappedAddress(ReadOnlySpan<byte> val)
    {
        if (val.Length < 8) return null;
        byte family = val[1];

        if (family == 0x01) // IPv4
        {
            int port = BinaryPrimitives.ReadUInt16BigEndian(val.Slice(2, 2));
            var ip = new IPAddress(val.Slice(4, 4));
            return new IPEndPoint(ip, port);
        }
        else if (family == 0x02 && val.Length >= 20) // IPv6
        {
            int port = BinaryPrimitives.ReadUInt16BigEndian(val.Slice(2, 2));
            var ip = new IPAddress(val.Slice(4, 16));
            return new IPEndPoint(ip, port);
        }

        return null;
    }

    /// <summary>
    /// 在指定的已繫結 Socket 上向單一 STUN 伺服器查詢公網映射端點 (保留相容介面)。
    /// </summary>
    public static async Task<IPEndPoint?> QueryAsync(
        Socket socket,
        string stunServer = "stun.l.google.com:19302",
        int timeoutMs = 2500,
        CancellationToken ct = default)
    {
        var probes = await ProbeAllServersOnSocketAsync(
            socket,
            new[] { stunServer },
            overallTimeoutMs: timeoutMs,
            ct: ct).ConfigureAwait(false);

        return probes.FirstOrDefault(p => p.Succeeded)?.MappedEndPoint;
    }

    /// <summary>
    /// 在指定的已繫結 UDP Socket 上對多組 STUN 伺服器並行發送 Binding Request，
    /// 不會在收到第一個回應後提前終止，並對每組伺服器記錄成功映射、RTT 與失敗分類。
    /// </summary>
    public static async Task<List<StunProbeRecord>> ProbeAllServersOnSocketAsync(
        Socket boundSocket,
        IEnumerable<string>? stunServers = null,
        string interfaceId = "",
        string socketId = "",
        string connectionAttemptId = "",
        string sessionId = "",
        int overallTimeoutMs = 2000,
        CancellationToken ct = default)
    {
        var serverList = (stunServers ?? DefaultStunServers)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        IPEndPoint? localEp = null;
        try { localEp = boundSocket.LocalEndPoint as IPEndPoint; } catch { }
        string localIpStr = localEp?.Address.ToString() ?? "";
        int localPort = localEp?.Port ?? 0;

        var records = new Dictionary<string, StunProbeRecord>(StringComparer.OrdinalIgnoreCase);
        if (serverList.Count == 0) return new();

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(Math.Max(200, overallTimeoutMs));
        var token = overallCts.Token;

        // 1. 平行解析所有 STUN Server IPv4 位址 (受整體逾時控制，不因單一 DNS 慢而拖累全部)
        var resolvedEndpoints = new List<(string server, IPEndPoint ep, byte[] txnId, string txnHex)>();
        var dnsTasks = serverList.Select(async server =>
        {
            var rec = new StunProbeRecord
            {
                ConnectionAttemptId = connectionAttemptId,
                SessionId = sessionId,
                InterfaceId = interfaceId,
                LocalIPv4 = localIpStr,
                LocalPort = localPort,
                SocketId = socketId,
                StunServer = server
            };

            if (!TryParseHostPort(server, 19302, out string host, out int port))
            {
                rec.FailureReason = StunQueryFailureReason.DnsFailed;
                rec.Detail = "Invalid STUN server format";
                lock (records) { records[server] = rec; }
                return;
            }

            try
            {
                var ip = await ResolveIPv4Async(host, token).ConfigureAwait(false);
                if (ip == null)
                {
                    rec.FailureReason = StunQueryFailureReason.DnsFailed;
                    rec.Detail = "No IPv4 address resolved";
                    lock (records) { records[server] = rec; }
                    return;
                }

                rec.ResolvedServerEndPoint = new IPEndPoint(ip, port);
                lock (records) { records[server] = rec; }
            }
            catch (OperationCanceledException)
            {
                rec.FailureReason = ct.IsCancellationRequested ? StunQueryFailureReason.Cancelled : StunQueryFailureReason.Timeout;
                rec.Detail = "DNS lookup timed out or cancelled";
                lock (records) { records[server] = rec; }
            }
            catch (Exception ex)
            {
                rec.FailureReason = StunQueryFailureReason.DnsFailed;
                rec.Detail = ex.Message;
                lock (records) { records[server] = rec; }
            }
        }).ToList();

        try { await Task.WhenAll(dnsTasks).ConfigureAwait(false); } catch { }

        // 建立待回應查詢表 (Key = 12-byte TransactionId Hex，同 Socket 多 STUN 不互搶封包)
        var pendingByTxn = new Dictionary<string, (StunProbeRecord record, byte[] request, long sentTs)>(StringComparer.OrdinalIgnoreCase);
        var stateLock = new object();

        foreach (var server in serverList)
        {
            var rec = records[server];
            if (rec.ResolvedServerEndPoint == null) continue;

            byte[] req = CreateBindingRequest();
            byte[] txn = req.AsSpan(8, 12).ToArray();
            string txnHex = Convert.ToHexString(txn);
            pendingByTxn[txnHex] = (rec, req, 0);
        }

        if (pendingByTxn.Count > 0 && !token.IsCancellationRequested)
        {
            int completedCount = 0;
            int expectedCount = pendingByTxn.Count;
            using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(token);

            // 2. 先啟動單一 Socket 接收迴圈，以 Transaction ID 多工分派，避免本機/高速回應漏接或跨請求搶封包
            var receiveTask = Task.Run(async () =>
            {
                byte[] recvBuf = new byte[1024];
                EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

                while (!recvCts.IsCancellationRequested)
                {
                    try
                    {
                        var recvResult = await boundSocket.ReceiveFromAsync(recvBuf, SocketFlags.None, remoteEp, recvCts.Token).ConfigureAwait(false);
                        var span = recvBuf.AsSpan(0, recvResult.ReceivedBytes);
                        if (span.Length < 20) continue;

                        string txnHex = Convert.ToHexString(span.Slice(8, 12));
                        (StunProbeRecord record, byte[] request, long sentTs) entry;
                        lock (stateLock)
                        {
                            if (!pendingByTxn.TryGetValue(txnHex, out entry))
                                continue;
                        }

                        var parsed = ParseBindingResult(span, span.Slice(8, 12));
                        if (!parsed.MatchedTransactionId) continue;

                        double rttMs = entry.sentTs > 0 ? Stopwatch.GetElapsedTime(entry.sentTs).TotalMilliseconds : 0;

                        bool finishProbe = false;
                        lock (stateLock)
                        {
                            if (!pendingByTxn.ContainsKey(txnHex)) continue;

                            if (parsed.FailureReason == StunQueryFailureReason.None && parsed.MappedEndPoint != null)
                            {
                                entry.record.MappedEndPoint = parsed.MappedEndPoint;
                                entry.record.RttMs = Math.Round(rttMs, 2);
                                entry.record.Succeeded = true;
                                entry.record.FailureReason = StunQueryFailureReason.None;
                                entry.record.Detail = "OK";
                                finishProbe = true;
                            }
                            else if (parsed.FailureReason == StunQueryFailureReason.ServerError)
                            {
                                entry.record.RttMs = Math.Round(rttMs, 2);
                                entry.record.Succeeded = false;
                                entry.record.FailureReason = StunQueryFailureReason.ServerError;
                                entry.record.StunErrorCode = parsed.ErrorCode;
                                entry.record.Detail = parsed.ErrorReasonPhrase ?? $"STUN Binding Error {parsed.ErrorCode}";
                                finishProbe = true;
                            }
                            else if (parsed.FailureReason == StunQueryFailureReason.InvalidResponse)
                            {
                                // 若收到損毀回應仍先標記，等待可能隨後到的有效重傳或最終結算
                                if (entry.record.FailureReason == StunQueryFailureReason.None)
                                {
                                    entry.record.FailureReason = StunQueryFailureReason.InvalidResponse;
                                    entry.record.Detail = parsed.ErrorReasonPhrase ?? "Invalid STUN response";
                                }
                            }

                            if (finishProbe)
                            {
                                pendingByTxn.Remove(txnHex);
                                completedCount++;
                                if (completedCount >= expectedCount)
                                {
                                    recvCts.Cancel();
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        MarkRemainingAs(pendingByTxn, stateLock, StunQueryFailureReason.SocketError, "Socket disposed during STUN probe");
                        break;
                    }
                    catch (SocketException sex)
                    {
                        // 單一 STUN 主機回傳 ICMP Port Unreachable (ConnectionReset/HostUnreachable) 時不中斷同 Socket 其他探測；
                        // 僅在介面/Socket 本身失效時結束迴圈
                        if (IsFatalInterfaceSocketError(sex.SocketErrorCode))
                        {
                            MarkRemainingAs(pendingByTxn, stateLock, StunQueryFailureReason.SocketError, $"Interface/Socket error: {sex.SocketErrorCode}");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        MarkRemainingAs(pendingByTxn, stateLock, StunQueryFailureReason.SocketError, ex.Message);
                        break;
                    }
                }
            }, CancellationToken.None);

            // 3. 發送第一次 Binding Request 至所有已解析 STUN 伺服器
            var sendTargets = pendingByTxn.ToList();
            foreach (var kv in sendTargets)
            {
                if (recvCts.IsCancellationRequested) break;
                try
                {
                    lock (stateLock)
                    {
                        if (!pendingByTxn.TryGetValue(kv.Key, out var cur)) continue;
                        pendingByTxn[kv.Key] = (cur.record, cur.request, Stopwatch.GetTimestamp());
                    }

                    await boundSocket.SendToAsync(kv.Value.request, SocketFlags.None, kv.Value.record.ResolvedServerEndPoint!, recvCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException)
                {
                    MarkSingleAs(pendingByTxn, stateLock, kv.Key, StunQueryFailureReason.SocketError, "Socket disposed", ref completedCount, expectedCount, recvCts);
                }
                catch (SocketException sex)
                {
                    MarkSingleAs(pendingByTxn, stateLock, kv.Key, StunQueryFailureReason.SocketError, $"Send failed: {sex.SocketErrorCode}", ref completedCount, expectedCount, recvCts);
                }
                catch (Exception ex)
                {
                    MarkSingleAs(pendingByTxn, stateLock, kv.Key, StunQueryFailureReason.SocketError, ex.Message, ref completedCount, expectedCount, recvCts);
                }
            }

            // 4. 短延遲後針對尚未回應的伺服器補送 1 次重試（防單次 UDP 遺失）
            int retryDelayMs = Math.Clamp(overallTimeoutMs / 3, 150, 500);
            try
            {
                await Task.Delay(retryDelayMs, recvCts.Token).ConfigureAwait(false);
                List<KeyValuePair<string, (StunProbeRecord record, byte[] request, long sentTs)>> unacked;
                lock (stateLock)
                {
                    unacked = pendingByTxn.ToList();
                }

                foreach (var kv in unacked)
                {
                    if (recvCts.IsCancellationRequested) break;
                    try
                    {
                        await boundSocket.SendToAsync(kv.Value.request, SocketFlags.None, kv.Value.record.ResolvedServerEndPoint!, recvCts.Token).ConfigureAwait(false);
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { }

            try { await receiveTask.ConfigureAwait(false); } catch { }

            // 5. 將仍未收到有效回應之探測歸類為 Timeout / Cancelled
            var finalReason = ct.IsCancellationRequested ? StunQueryFailureReason.Cancelled : StunQueryFailureReason.Timeout;
            lock (stateLock)
            {
                foreach (var kv in pendingByTxn)
                {
                    if (kv.Value.record.FailureReason == StunQueryFailureReason.None)
                    {
                        kv.Value.record.FailureReason = finalReason;
                        kv.Value.record.Detail = finalReason == StunQueryFailureReason.Cancelled ? "Cancelled" : "STUN Binding Request timeout";
                    }
                }
                pendingByTxn.Clear();
            }
        }

        var ordered = serverList.Select(s => records[s]).ToList();
        foreach (var r in ordered)
        {
            string resultTag = r.Succeeded ? "OK" : r.FailureReason.ToString();
            Log.Info($"[STUN] Attempt={connectionAttemptId}, Session={sessionId}, Iface={r.InterfaceId}, Local={r.LocalIPv4}:{r.LocalPort}, Socket={r.SocketId}, Server={r.StunServer}, Mapped={r.MappedPublicIp}:{r.MappedPublicPort}, RTT={r.RttMs:F1}ms, Result={resultTag} ({r.Detail})");
        }
        return ordered;
    }

    private static void MarkSingleAs(
        Dictionary<string, (StunProbeRecord record, byte[] request, long sentTs)> pendingByTxn,
        object stateLock,
        string txnHex,
        StunQueryFailureReason reason,
        string detail,
        ref int completedCount,
        int expectedCount,
        CancellationTokenSource recvCts)
    {
        lock (stateLock)
        {
            if (pendingByTxn.TryGetValue(txnHex, out var entry))
            {
                entry.record.Succeeded = false;
                entry.record.FailureReason = reason;
                entry.record.Detail = detail;
                pendingByTxn.Remove(txnHex);
                completedCount++;
                if (completedCount >= expectedCount)
                {
                    recvCts.Cancel();
                }
            }
        }
    }

    private static void MarkRemainingAs(
        Dictionary<string, (StunProbeRecord record, byte[] request, long sentTs)> pendingByTxn,
        object stateLock,
        StunQueryFailureReason reason,
        string detail)
    {
        lock (stateLock)
        {
            foreach (var kv in pendingByTxn)
            {
                kv.Value.record.Succeeded = false;
                kv.Value.record.FailureReason = reason;
                kv.Value.record.Detail = detail;
            }
            pendingByTxn.Clear();
        }
    }

    private static bool IsFatalInterfaceSocketError(SocketError err) =>
        err is SocketError.NetworkDown
            or SocketError.NotConnected
            or SocketError.Shutdown
            or SocketError.InvalidArgument
            or SocketError.OperationAborted
            or SocketError.Interrupted;

    internal static bool TryParseHostPort(string server, int defaultPort, out string host, out int port)
    {
        host = server.Trim();
        port = defaultPort;
        int colonIdx = host.LastIndexOf(':');
        if (colonIdx > 0)
        {
            string portPart = host[(colonIdx + 1)..];
            host = host[..colonIdx];
            if (!int.TryParse(portPart, out port) || port <= 0 || port > 65535)
                return false;
        }
        return !string.IsNullOrWhiteSpace(host);
    }

    internal static async Task<IPAddress?> ResolveIPv4Async(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var parsed))
        {
            return parsed.AddressFamily == AddressFamily.InterNetwork ? parsed : null;
        }

        var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
    }

    /// <summary>
    /// 使用多組預設 STUN 伺服器輪流查詢，直到成功取得公網映射端點 (保留相容介面)。
    /// </summary>
    public static async Task<IPEndPoint?> QueryWithFallbackAsync(
        Socket socket,
        IEnumerable<string>? stunServers = null,
        int timeoutMs = 2500,
        CancellationToken ct = default)
    {
        var servers = stunServers ?? DefaultStunServers;
        foreach (var s in servers)
        {
            if (ct.IsCancellationRequested) break;
            var ep = await QueryAsync(socket, s, timeoutMs, ct).ConfigureAwait(false);
            if (ep != null) return ep;
        }
        return null;
    }
}
