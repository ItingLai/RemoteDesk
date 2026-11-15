using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RemoteDesk.Net;

/// <summary>
/// P2P 連線協調角色：由發起方 (Controlling) 負責最終配對選定，避免雙端各自選出不同路徑。
/// 若因特殊競態出現雙端皆為 Controlling，則依 TieBreaker 決定讓步方。
/// </summary>
public enum IceRole
{
    Controlling = 1,
    Controlled = 2
}

/// <summary>
/// 單一 Candidate Pair 的雙向連通性檢查狀態（Phase 3 規格 3-4）。
/// </summary>
public enum CandidatePairCheckState
{
    /// <summary>尚未檢查。</summary>
    Unchecked = 0,
    /// <summary>檢查中。</summary>
    InProgress = 1,
    /// <summary>僅單向收到對端封包，尚未完成我方 Request -> Response 雙向驗證。</summary>
    OneWayReceived = 2,
    /// <summary>雙向連通性確認成功。</summary>
    Succeeded = 3,
    /// <summary>多次重試後仍逾時未獲對端回應。</summary>
    TimedOut = 4,
    /// <summary>收到無效回應（Session、裝置身分、來源端點或格式驗證不符）。</summary>
    InvalidResponse = 5,
    /// <summary>配對已失效（例如對應本機 Socket 中斷或候選已過期）。</summary>
    Invalidated = 6
}

/// <summary>
/// Candidate Pair 檢查失敗或未獲選之原因分類。
/// </summary>
public enum CandidatePairFailureReason
{
    None = 0,
    Timeout = 1,
    SocketError = 2,
    InvalidSession = 3,
    InvalidEndpoint = 4,
    InvalidFormat = 5,
    OneWayOnly = 6,
    PeerRejected = 7,
    Cancelled = 8,
    SupersededByHigherPriorityPair = 9,
    LocalSocketDisposed = 10
}

internal enum IceCheckMessageType : byte
{
    CheckRequest = 0x01,
    CheckResponse = 0x02,
    NominateRequest = 0x03,
    NominateAck = 0x04
}

/// <summary>
/// 由本地端候選與遠端候選組成的 P2P 配對檢查單位（Phase 3 規格 3-2）。
/// </summary>
internal sealed class CandidatePair
{
    public required string PairId { get; init; }
    public required string LocalCandidateId { get; init; }
    public required string RemoteCandidateId { get; init; }
    public required IceCandidate LocalCandidate { get; init; }
    public required IceCandidate RemoteCandidate { get; init; }
    public required string SocketId { get; init; }
    public required string InterfaceId { get; init; }
    public required IPEndPoint LocalEndPoint { get; init; }
    public required IPEndPoint RemoteEndPoint { get; init; }
    public required ulong PairPriority { get; init; }

    public bool IsRecentSuccessHint { get; init; }
    public bool IsSameSubnetHost { get; init; }
    public bool IsLoopbackPair { get; init; }
    public bool IsPortPredictionFallback { get; init; }

    public CandidatePairCheckState CheckState { get; set; } = CandidatePairCheckState.Unchecked;
    public DateTime? LastCheckTimestamp { get; set; }
    public int CheckAttempts { get; set; }
    public double? RttMs { get; set; }
    public double? RTT => RttMs;
    public CandidatePairFailureReason FailureReason { get; set; } = CandidatePairFailureReason.None;
    public string FailureDetail { get; set; } = "";

    public bool InboundPacketObserved { get; set; }
    public bool OutboundCheckAcked { get; set; }
    public int DuplicateResponseCount { get; set; }
    public bool IsNominated { get; set; }
    public bool IsSelected { get; set; }

    public long LastTriggeredCheckTick { get; set; }

    public override string ToString() =>
        $"Pair[{PairId}] {LocalCandidate.Type}({LocalEndPoint}, sock={SocketId}) <-> {RemoteCandidate.Type}({RemoteEndPoint}), Prio={PairPriority}, State={CheckState}, RTT={RttMs?.ToString("F1") ?? "-"}ms";
}

internal readonly record struct IceCheckMessage(
    IceCheckMessageType MessageType,
    bool IsControlling,
    bool UseCandidate,
    bool RoleConflictRejected,
    byte AttemptNumber,
    byte[] TransactionId,
    string TransactionIdHex,
    ulong TieBreaker,
    ulong Priority,
    IPEndPoint? ObservedEndPoint,
    string SessionId,
    string SenderDeviceId,
    string ReceiverDeviceId,
    string LocalCandidateId,
    string RemoteCandidateId,
    string PairId);

/// <summary>
/// Phase 3 雙向連通性檢查與選定封包編解碼器（固定 Magic = "RDIC" 0x52444943，避免與 RDSK 資料串流混淆）。
/// </summary>
internal static class IceCheckCodec
{
    public const uint Magic = 0x52444943; // ASCII "RDIC"
    public const byte ProtocolVersion = 0x01;
    private const int FixedHeaderSize = 42;

    public static byte[] CreateTransactionId() => RandomNumberGenerator.GetBytes(12);

    public static byte[] Serialize(
        IceCheckMessageType messageType,
        bool isControlling,
        bool useCandidate,
        bool roleConflictRejected,
        byte attemptNumber,
        ReadOnlySpan<byte> transactionId,
        ulong tieBreaker,
        ulong priority,
        IPEndPoint? observedEndPoint,
        string sessionId,
        string senderDeviceId,
        string receiverDeviceId,
        string localCandidateId,
        string remoteCandidateId,
        string pairId)
    {
        if (transactionId.Length != 12)
            throw new ArgumentException("TransactionId must be exactly 12 bytes.", nameof(transactionId));

        byte[] sBytes = Encoding.UTF8.GetBytes(sessionId ?? "");
        byte[] sndBytes = Encoding.UTF8.GetBytes(senderDeviceId ?? "");
        byte[] rcvBytes = Encoding.UTF8.GetBytes(receiverDeviceId ?? "");
        byte[] locIdBytes = Encoding.UTF8.GetBytes(localCandidateId ?? "");
        byte[] remIdBytes = Encoding.UTF8.GetBytes(remoteCandidateId ?? "");
        byte[] pairIdBytes = Encoding.UTF8.GetBytes(pairId ?? "");

        int totalLen = FixedHeaderSize
            + 2 + sBytes.Length
            + 2 + sndBytes.Length
            + 2 + rcvBytes.Length
            + 2 + locIdBytes.Length
            + 2 + remIdBytes.Length
            + 2 + pairIdBytes.Length;

        var buf = new byte[totalLen];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), Magic);
        buf[4] = ProtocolVersion;
        buf[5] = (byte)messageType;

        byte flags = 0;
        if (isControlling) flags |= 0x01;
        if (useCandidate) flags |= 0x02;
        if (roleConflictRejected) flags |= 0x04;
        buf[6] = flags;
        buf[7] = attemptNumber;

        transactionId.CopyTo(buf.AsSpan(8, 12));
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(20, 8), tieBreaker);
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(28, 8), priority);

        if (observedEndPoint != null && observedEndPoint.AddressFamily == AddressFamily.InterNetwork)
        {
            observedEndPoint.Address.TryWriteBytes(buf.AsSpan(36, 4), out _);
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(40, 2), (ushort)Math.Clamp(observedEndPoint.Port, 0, 65535));
        }

        int offset = FixedHeaderSize;
        WriteStringField(buf, ref offset, sBytes);
        WriteStringField(buf, ref offset, sndBytes);
        WriteStringField(buf, ref offset, rcvBytes);
        WriteStringField(buf, ref offset, locIdBytes);
        WriteStringField(buf, ref offset, remIdBytes);
        WriteStringField(buf, ref offset, pairIdBytes);

        return buf;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out IceCheckMessage message)
    {
        message = default;
        if (data.Length < FixedHeaderSize + 12) return false;

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(data[0..4]);
        if (magic != Magic) return false;

        byte version = data[4];
        if (version != ProtocolVersion) return false;

        byte typeRaw = data[5];
        if (typeRaw < (byte)IceCheckMessageType.CheckRequest || typeRaw > (byte)IceCheckMessageType.NominateAck)
            return false;

        byte flags = data[6];
        bool isControlling = (flags & 0x01) != 0;
        bool useCandidate = (flags & 0x02) != 0;
        bool roleConflictRejected = (flags & 0x04) != 0;
        byte attemptNumber = data[7];

        byte[] txnId = data.Slice(8, 12).ToArray();
        string txnHex = Convert.ToHexString(txnId);
        ulong tieBreaker = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(20, 8));
        ulong priority = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(28, 8));

        var obsIp = new IPAddress(data.Slice(36, 4));
        ushort obsPort = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(40, 2));
        IPEndPoint? observedEp = obsPort > 0 && !IPAddress.Any.Equals(obsIp) ? new IPEndPoint(obsIp, obsPort) : null;

        int offset = FixedHeaderSize;
        if (!TryReadStringField(data, ref offset, out string sessionId)) return false;
        if (!TryReadStringField(data, ref offset, out string senderId)) return false;
        if (!TryReadStringField(data, ref offset, out string receiverId)) return false;
        if (!TryReadStringField(data, ref offset, out string localCandId)) return false;
        if (!TryReadStringField(data, ref offset, out string remoteCandId)) return false;
        if (!TryReadStringField(data, ref offset, out string pairId)) return false;

        message = new IceCheckMessage(
            (IceCheckMessageType)typeRaw,
            isControlling,
            useCandidate,
            roleConflictRejected,
            attemptNumber,
            txnId,
            txnHex,
            tieBreaker,
            priority,
            observedEp,
            sessionId,
            senderId,
            receiverId,
            localCandId,
            remoteCandId,
            pairId);
        return true;
    }

    private static void WriteStringField(byte[] dst, ref int offset, byte[] src)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst.AsSpan(offset, 2), (ushort)src.Length);
        offset += 2;
        if (src.Length > 0)
        {
            src.CopyTo(dst.AsSpan(offset, src.Length));
            offset += src.Length;
        }
    }

    private static bool TryReadStringField(ReadOnlySpan<byte> src, ref int offset, out string value)
    {
        value = "";
        if (offset + 2 > src.Length) return false;
        ushort len = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(offset, 2));
        offset += 2;
        if (offset + len > src.Length || len > 512) return false;
        value = len > 0 ? Encoding.UTF8.GetString(src.Slice(offset, len)) : "";
        offset += len;
        return true;
    }
}

/// <summary>
/// 近期已驗證成功之 P2P 路徑記憶項目，供新 Session 在 Candidate Pair 排序時優先排列。
/// </summary>
internal sealed record RecentSuccessfulPairHint(
    string RemoteDeviceId,
    string LocalInterfaceId,
    string LocalIPv4,
    string RemotePublicIp,
    IceCandidateType LocalType,
    IceCandidateType RemoteType,
    DateTime LastSuccessUtc);

/// <summary>
/// 記錄近期連通配對快取的輕量級執行緒安全存放區。
/// </summary>
internal static class RecentSuccessfulPathStore
{
    private static readonly ConcurrentDictionary<string, RecentSuccessfulPairHint> _hints = new(StringComparer.OrdinalIgnoreCase);

    public static void RecordSuccess(string remoteDeviceId, CandidatePair pair)
    {
        if (string.IsNullOrWhiteSpace(remoteDeviceId)) return;
        string key = BuildKey(remoteDeviceId, pair.InterfaceId, pair.LocalCandidate.LocalAddress, pair.RemoteCandidate.Ip, pair.LocalCandidate.Type, pair.RemoteCandidate.Type);
        _hints[key] = new RecentSuccessfulPairHint(
            remoteDeviceId,
            pair.InterfaceId,
            pair.LocalCandidate.LocalAddress,
            pair.RemoteCandidate.Ip,
            pair.LocalCandidate.Type,
            pair.RemoteCandidate.Type,
            DateTime.UtcNow);
    }

    public static IReadOnlyCollection<RecentSuccessfulPairHint> GetHintsForPeer(string remoteDeviceId, TimeSpan? maxAge = null)
    {
        if (string.IsNullOrWhiteSpace(remoteDeviceId)) return Array.Empty<RecentSuccessfulPairHint>();
        var ttl = maxAge ?? TimeSpan.FromMinutes(30);
        var now = DateTime.UtcNow;
        var list = new List<RecentSuccessfulPairHint>();

        foreach (var kv in _hints)
        {
            if (now - kv.Value.LastSuccessUtc > ttl)
            {
                _hints.TryRemove(kv.Key, out _);
                continue;
            }
            if (string.Equals(kv.Value.RemoteDeviceId, remoteDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(kv.Value);
            }
        }
        return list;
    }

    private static string BuildKey(string remoteDev, string ifaceId, string localIp, string remoteIp, IceCandidateType lType, IceCandidateType rType) =>
        $"{remoteDev}|{ifaceId}|{localIp}|{remoteIp}|{(int)lType}|{(int)rType}";
}

/// <summary>
/// Phase 3 Candidate Pair 檢查與控制策略參數。
/// </summary>
internal sealed class IceConnectivityOptions
{
    public int CheckTimeoutMs { get; set; } = 4500;
    public int PacingIntervalMs { get; set; } = 20;
    public int MaxAttemptsPerPair { get; set; } = 6;
    public IReadOnlyList<int> RetryIntervalsMs { get; set; } = new[] { 120, 220, 350, 500, 650 };
    public int SelectionGraceWindowMs { get; set; } = 70;
    public int NominationTimeoutMs { get; set; } = 1500;
    public int MaxCandidateAgeSeconds { get; set; } = 300;
    public IceRole? ForceRole { get; set; }
    public ulong? OverrideTieBreaker { get; set; }
    public Func<DateTime>? UtcNowProvider { get; set; }

    /// <summary>
    /// Phase 3 規格 3-6：預設禁用固定 Port 猜測，核心連線嚴格依靠 Candidate Pair 雙向檢查。
    /// 僅於明確啟用且所有直接 Candidate Pair 皆失敗時，作為有限次數最後備援並紀錄貢獻度。
    /// </summary>
    public bool AllowRestrictedPortPredictionFallback { get; set; } = false;
    public int RestrictedPortPredictionMaxDelta { get; set; } = 4;

    public IReadOnlyCollection<RecentSuccessfulPairHint>? RecentSuccessHints { get; set; }

    /// <summary>
    /// Phase 4 PAC (延遲失敗判定) 與增量候選接收回呼：
    /// 若遠端尚未宣告本輪候選交換完成，檢查迴圈可持續輪詢新抵達的遠端候選端點並動態配對檢查。
    /// </summary>
    public Func<bool>? IsRemoteExchangeCompleteProvider { get; set; }
    public Func<CancellationToken, Task<IReadOnlyList<IceCandidate>>>? IncrementalRemoteCandidatePollProvider { get; set; }
    public int IncrementalPollIntervalMs { get; set; } = 120;
    public int PacMinimumCheckWaitMs { get; set; } = 0;
}

/// <summary>
/// 供 ReliableUdpStream 在剛切換接管勝出 Socket 後，回覆對端殘留的晚期 Connectivity Check / Nominate 封包。
/// </summary>
internal sealed class IceStreamPostPunchContext
{
    public required string SessionId { get; init; }
    public required string SelfDeviceId { get; init; }
    public required string RemoteDeviceId { get; init; }
    public required string SelectedLocalCandidateId { get; init; }
    public required string SelectedRemoteCandidateId { get; init; }
    public required string SelectedPairId { get; init; }
    public required bool IsControlling { get; init; }
    public required ulong TieBreaker { get; init; }
}

/// <summary>
/// Phase 3 雙向連通性檢查與配對選定完整結果報告。
/// </summary>
internal sealed class ConnectivityCheckResult
{
    public bool Succeeded { get; init; }
    public CandidatePair? SelectedPair { get; init; }
    public IPEndPoint? SelectedRemoteEndPoint => SelectedPair?.RemoteEndPoint;
    public IReadOnlyList<CandidatePair> AllPairs { get; init; } = Array.Empty<CandidatePair>();
    public TimeSpan Elapsed { get; init; }
    public int TotalCheckAttempts { get; init; }
    public int InvalidSessionPacketsRejected { get; init; }
    public int DuplicateResponsesIgnored { get; init; }
    public int InvalidPacketsRejected { get; init; }
    public int RoleConflictCount { get; init; }
    public int LegacyPortPredictionAttempts { get; init; }
    public int LegacyPortPredictionSuccesses { get; init; }
    public bool SelectedViaPortPrediction { get; init; }
    public IceRole FinalRole { get; init; }
    public ulong TieBreaker { get; init; }
    public IReadOnlyList<IPEndPoint> ObservedSelfPublicEndPoints { get; init; } = Array.Empty<IPEndPoint>();
    public string FinalFailureReason { get; init; } = "";
}
