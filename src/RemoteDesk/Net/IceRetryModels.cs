using System.Net;

namespace RemoteDesk.Net;

/// <summary>
/// Phase 4 P2P 失敗原因明確分類（規格 4-1：必須包含所有指定分類並對應明確處理策略）。
/// </summary>
public enum P2PFailureReason
{
    None = 0,
    StunTimeout = 1,
    StunServerUnavailable = 2,
    NoUsableInterface = 3,
    CandidateExchangeTimeout = 4,
    CandidatePairCheckTimeout = 5,
    OneWayConnectivity = 6,
    SocketFailure = 7,
    NatMappingChanged = 8,
    SignalingMessageTimeout = 9,
    SessionExpired = 10,
    AllCandidatePairsFailed = 11,
    Cancelled = 12
}

internal enum P2PRetryAction
{
    None = 0,
    RecheckExistingPairs = 1,
    RefreshInterfacesAndSockets = 2,
    RotateAndSupplementStun = 3,
    RetrySignalingExchange = 4,
    FullP2PRenegotiation = 5,
    AbortSessionExpired = 6,
    AbortCancelled = 7
}

internal enum P2PRetryState
{
    NotStarted = 0,
    InitialGathering = 1,
    ExchangingInitialCandidates = 2,
    Round1CheckingExistingPairs = 3,
    BackingOffBeforeRound2 = 4,
    Round2RefreshingCandidates = 5,
    BackingOffBeforeRound3 = 6,
    Round3Renegotiating = 7,
    Succeeded = 8,
    Cancelled = 9,
    Expired = 10,
    Failed = 11
}

internal sealed record P2PFailureHandlingStrategy(
    P2PFailureReason Reason,
    bool IsRetryableInSameSession,
    bool RequiresInterfaceAndSocketRefresh,
    bool RequiresStunRotation,
    bool RequiresSignalingReExchange,
    bool SkipWaitAndRecoverImmediately,
    P2PRetryAction NextAction,
    double BackoffMultiplier,
    string Description);

internal sealed record StunProbeSummary(
    int TotalExternalProbes,
    int SucceededProbes,
    int TimedOutProbes,
    int UnavailableProbes,
    int SocketErrorProbes,
    IReadOnlyList<string> FailedServers)
{
    public bool HasAnySuccess => SucceededProbes > 0;
    public bool IsPartialFailure => TotalExternalProbes > 0 && SucceededProbes > 0 && (TimedOutProbes + UnavailableProbes + SocketErrorProbes) > 0;
    public bool IsTotalTimeout => TotalExternalProbes > 0 && SucceededProbes == 0 && TimedOutProbes >= UnavailableProbes;
    public bool IsTotalServerUnavailable => TotalExternalProbes > 0 && SucceededProbes == 0 && UnavailableProbes > TimedOutProbes;
}

internal sealed record SignalingExchangeOutcome(
    bool Succeeded,
    SignalingCallStatus WorstStatus,
    int TransportErrorCount,
    int RemoteCandidateCount,
    bool RemoteDone,
    string Detail = "");

internal sealed record GatheringRoundSnapshot(
    IReadOnlyDictionary<string, IReadOnlyList<IPEndPoint>> ExternalStunMappingsByInterface,
    IReadOnlyList<string> DistinctPublicIps,
    IReadOnlyList<NatMappingObservation> NatObservations,
    StunProbeSummary StunSummary,
    int DiscoveredExternalInterfaceCount,
    int ValidLocalCandidateCount);

/// <summary>
/// P2P 信令傳輸送收抽象，預設包裝 SignalingClient，並讓受控重試測試可模擬伺服器延遲、ACK 延遲與 Session 過期。
/// </summary>
internal interface IP2PSignalingTransport
{
    string SelfDeviceId { get; }
    TimeSpan EstimatedServerClockOffset { get; }

    Task<SignalingPostResult> PostSignalAsync(
        string sessionId,
        string toDeviceId,
        string data,
        string messageType,
        CancellationToken ct,
        string? messageId = null);

    Task<SignalingPollResult> GetSignalsAsync(string sessionId, CancellationToken ct);

    Task<SignalingAckResult> AckSignalsAsync(string sessionId, IEnumerable<string> messageIds, CancellationToken ct);
}

internal sealed class SignalingClientTransport : IP2PSignalingTransport
{
    private readonly SignalingClient _client;

    public SignalingClientTransport(SignalingClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string SelfDeviceId => _client.Identity.DeviceId;
    public TimeSpan EstimatedServerClockOffset => _client.EstimatedServerClockOffset;

    public Task<SignalingPostResult> PostSignalAsync(
        string sessionId,
        string toDeviceId,
        string data,
        string messageType,
        CancellationToken ct,
        string? messageId = null) =>
        _client.PostSignalDetailedAsync(sessionId, toDeviceId, data, messageType, ct, messageId);

    public Task<SignalingPollResult> GetSignalsAsync(string sessionId, CancellationToken ct) =>
        _client.GetSignalsDetailedAsync(sessionId, ct);

    public Task<SignalingAckResult> AckSignalsAsync(string sessionId, IEnumerable<string> messageIds, CancellationToken ct) =>
        _client.AckSignalsDetailedAsync(sessionId, messageIds, ct);
}

internal sealed record P2PRetryRoundReport(
    int RoundNumber,
    P2PRetryState StateEntered,
    P2PRetryAction ActionTaken,
    DateTime RetryStartTimestampUtc,
    DateTime RetryEndTimestampUtc,
    TimeSpan Elapsed,
    int LocalCandidateCount,
    int RemoteCandidateCount,
    int PairCount,
    bool Succeeded,
    P2PFailureReason FailureReason,
    string FailureDetail,
    StunProbeSummary StunSummary,
    IReadOnlyList<CandidatePair> PairsSnapshot,
    int BackoffBeforeRoundMs);

internal sealed class P2PConnectionResult
{
    public bool Succeeded { get; init; }
    public ReliableUdpStream? Stream { get; init; }
    public CandidatePair? SelectedPair { get; init; }
    public P2PFailureReason FinalFailureReason { get; init; }
    public string FinalFailureDetail { get; init; } = "";
    public P2PRetryState FinalState { get; init; }
    public int TotalRoundsAttempted { get; init; }
    public int TotalCheckAttempts { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string ConnectionAttemptId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public IReadOnlyList<P2PRetryRoundReport> RoundReports { get; init; } = Array.Empty<P2PRetryRoundReport>();
    public P2PPathSessionContext? PathContext { get; init; }
}

internal sealed class P2PRetryOptions
{
    public int MaxRetryRounds { get; set; } = 3;
    public int TotalTimeoutMs { get; set; } = 15000;

    public int Round1ExchangeTimeoutMs { get; set; } = 4000;
    public int Round1CheckTimeoutMs { get; set; } = 2400;
    public int Round2ExchangeTimeoutMs { get; set; } = 3000;
    public int Round2CheckTimeoutMs { get; set; } = 3400;
    public int Round3ExchangeTimeoutMs { get; set; } = 2800;
    public int Round3CheckTimeoutMs { get; set; } = 4200;

    public int StunTimeoutMs { get; set; } = 1500;
    public int Round2StunTimeoutMs { get; set; } = 2200;

    public int BackoffBeforeRound2MinMs { get; set; } = 110;
    public int BackoffBeforeRound2MaxMs { get; set; } = 280;
    public int BackoffBeforeRound3MinMs { get; set; } = 220;
    public int BackoffBeforeRound3MaxMs { get; set; } = 560;

    public int ExchangePollIntervalMs { get; set; } = 50;
    public int PacMinimumCheckWaitMs { get; set; } = 120;

    public IReadOnlyList<string> PrimaryStunServers { get; set; } = StunClient.DefaultStunServers;
    public IReadOnlyList<string> SupplementalStunServers { get; set; } = new[]
    {
        "stun3.l.google.com:19302",
        "stun4.l.google.com:19302",
        "stun.stunprotocol.org:3478"
    };

    public IceGatheringOptions? GatheringOptions { get; set; }
    public IceConnectivityOptions? ConnectivityOptions { get; set; }

    /// <summary>
    /// Phase 5：P2P 連線建立成功後保留非勝出且仍存活的備援介面 Socket 與已知配對，
    /// 供 P2PPathMonitor 執行持續監控與失敗後快速切換路徑。
    /// </summary>
    public bool RetainStandbyBindingsForMonitoring { get; set; }

    public Func<int, int, int>? RandomNextProvider { get; set; }
    public Func<DateTime>? UtcNowProvider { get; set; }

    /// <summary>
    /// 供測試或自訂介面環境覆寫本機候選收集委派（預設呼叫 IceCandidateGatherer.GatherAsync）。
    /// </summary>
    public Func<int, string, string, IceGatheringOptions, CancellationToken, Task<CandidateGatheringResult>>? CustomGatherAsync { get; set; }

    /// <summary>
    /// 供測試覆寫雙向連通性檢查委派（預設呼叫 IceConnectivityChecker.RunAsync）。
    /// </summary>
    public Func<int, CandidateGatheringResult, IReadOnlyList<IceCandidate>, string, string, string, string, bool, IceConnectivityOptions, CancellationToken, Task<ConnectivityCheckResult>>? CustomCheckAsync { get; set; }
}

/// <summary>
/// Phase 4 失敗原因分類器與策略查詢表（規格 4-1）。
/// </summary>
internal static class P2PFailureClassifier
{
    private static readonly IReadOnlyDictionary<P2PFailureReason, P2PFailureHandlingStrategy> Strategies =
        new Dictionary<P2PFailureReason, P2PFailureHandlingStrategy>
        {
            [P2PFailureReason.None] = new(
                P2PFailureReason.None, false, false, false, false, false,
                P2PRetryAction.None, 1.0, "連線成功，無須重試"),

            [P2PFailureReason.StunTimeout] = new(
                P2PFailureReason.StunTimeout, true, true, true, true, false,
                P2PRetryAction.RotateAndSupplementStun, 1.2,
                "STUN 查詢逾時：輪替並補充 STUN Server、延長 STUN 逾時後重建 Socket 重新收集"),

            [P2PFailureReason.StunServerUnavailable] = new(
                P2PFailureReason.StunServerUnavailable, true, true, true, true, false,
                P2PRetryAction.RotateAndSupplementStun, 1.1,
                "STUN 伺服器不可用或 DNS/回應失敗：排除失效 STUN Server 並切換備援伺服器重新收集"),

            [P2PFailureReason.NoUsableInterface] = new(
                P2PFailureReason.NoUsableInterface, true, true, false, true, true,
                P2PRetryAction.RefreshInterfacesAndSockets, 1.4,
                "無可用 IPv4 介面或 Socket 無法繫結：等待短暫退避後重新列舉介面與建立 Socket"),

            [P2PFailureReason.CandidateExchangeTimeout] = new(
                P2PFailureReason.CandidateExchangeTimeout, true, false, false, true, false,
                P2PRetryAction.RetrySignalingExchange, 1.3,
                "候選端點交換逾時：保留同一個 Session 與既有存活 Socket，重送/補齊信令交換"),

            [P2PFailureReason.CandidatePairCheckTimeout] = new(
                P2PFailureReason.CandidatePairCheckTimeout, true, true, true, true, false,
                P2PRetryAction.RefreshInterfacesAndSockets, 1.0,
                "配對雙向檢查逾時：第 2 輪重建 Socket 與更新 STUN，第 3 輪擴大時窗與 pacing 重新協商"),

            [P2PFailureReason.OneWayConnectivity] = new(
                P2PFailureReason.OneWayConnectivity, true, true, true, true, false,
                P2PRetryAction.RefreshInterfacesAndSockets, 0.9,
                "僅單向連通（收到對端但出向確認失敗）：重建本機 Socket 與新 NAT 映射埠重新雙向打洞"),

            [P2PFailureReason.SocketFailure] = new(
                P2PFailureReason.SocketFailure, true, true, false, true, true,
                P2PRetryAction.RefreshInterfacesAndSockets, 0.6,
                "本機 UDP Socket 異常或網卡切換中斷：立即捨棄失效 Socket，重新偵測網卡並建立新 Socket"),

            [P2PFailureReason.NatMappingChanged] = new(
                P2PFailureReason.NatMappingChanged, true, true, true, true, true,
                P2PRetryAction.RotateAndSupplementStun, 1.0,
                "偵測 NAT 映射改變或埠號依目的端漂移：強制刷新 Socket、更換 STUN 並更新最新候選配對"),

            [P2PFailureReason.SignalingMessageTimeout] = new(
                P2PFailureReason.SignalingMessageTimeout, true, false, false, true, false,
                P2PRetryAction.RetrySignalingExchange, 1.6,
                "Signaling 訊號送收或 ACK 逾時：延長退避並以冪等 MessageId 重試同 Session 信令交換"),

            [P2PFailureReason.SessionExpired] = new(
                P2PFailureReason.SessionExpired, false, false, false, false, false,
                P2PRetryAction.AbortSessionExpired, 0.0,
                "Signaling Session 已過期或關閉：立即終止當前重試，不進行無意義重試風暴"),

            [P2PFailureReason.AllCandidatePairsFailed] = new(
                P2PFailureReason.AllCandidatePairsFailed, true, true, true, true, false,
                P2PRetryAction.FullP2PRenegotiation, 1.2,
                "所有候選配對皆驗證失敗：進入下一輪候選刷新與最新配對全量重新協商"),

            [P2PFailureReason.Cancelled] = new(
                P2PFailureReason.Cancelled, false, false, false, false, false,
                P2PRetryAction.AbortCancelled, 0.0,
                "使用者主動取消連線：立即清理資源並終止所有重試")
        };

    public static P2PFailureHandlingStrategy GetStrategy(P2PFailureReason reason) =>
        Strategies.TryGetValue(reason, out var s) ? s : Strategies[P2PFailureReason.AllCandidatePairsFailed];

    public static StunProbeSummary SummarizeStunProbes(CandidateGatheringResult gathering)
    {
        var externalBindingIds = new HashSet<string>(
            gathering.Bindings.Where(b => !b.IsLoopbackFallback && b.Interface.HasValidRoute).Select(b => b.SocketId),
            StringComparer.OrdinalIgnoreCase);

        var probes = externalBindingIds.Count > 0
            ? gathering.StunProbes.Where(p => externalBindingIds.Contains(p.SocketId)).ToList()
            : gathering.StunProbes.ToList();

        int ok = 0, timeout = 0, unavail = 0, sockErr = 0;
        var failedServers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in probes)
        {
            if (p.Succeeded)
            {
                ok++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(p.StunServer))
                failedServers.Add(p.StunServer);

            switch (p.FailureReason)
            {
                case StunQueryFailureReason.Timeout:
                    timeout++;
                    break;
                case StunQueryFailureReason.DnsFailed:
                case StunQueryFailureReason.ServerError:
                case StunQueryFailureReason.InvalidResponse:
                    unavail++;
                    break;
                case StunQueryFailureReason.SocketError:
                    sockErr++;
                    break;
            }
        }

        return new StunProbeSummary(probes.Count, ok, timeout, unavail, sockErr, failedServers.ToList());
    }

    public static GatheringRoundSnapshot CaptureSnapshot(CandidateGatheringResult gathering, int validLocalCount)
    {
        var extMappings = new Dictionary<string, IReadOnlyList<IPEndPoint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in gathering.Bindings)
        {
            if (b.IsLoopbackFallback) continue;
            var eps = b.Candidates
                .Where(c => c.Type == IceCandidateType.Srflx && c.ValidityStatus == CandidateValidityStatus.Valid && c.EndPoint != null)
                .Select(c => c.EndPoint!)
                .ToList();
            if (eps.Count > 0)
            {
                extMappings[$"{b.Interface.InterfaceId}|{b.Interface.LocalIPv4}"] = eps;
            }
        }

        return new GatheringRoundSnapshot(
            extMappings,
            gathering.DistinctPublicIps.ToList(),
            gathering.NatObservations.ToList(),
            SummarizeStunProbes(gathering),
            gathering.Interfaces.Count,
            validLocalCount);
    }

    public static bool HasNatMappingChanged(
        GatheringRoundSnapshot? previousSnapshot,
        CandidateGatheringResult currentGathering,
        ConnectivityCheckResult? checkResult)
    {
        // 1. 同一介面於同一輪公網 IP 實際改變（例如雙 WAN 出口跳變）；僅對不同 STUN 伺服器得到不同 Port (AddressOrPortDependent) 屬靜態 NAT 映射特性，不得誤判為 NAT 運行時改變 (Spec 7.3)
        if (currentGathering.NatObservations.Any(o =>
                o.SuccessfulProbeCount >= 2 &&
                o.PublicIpChanged))
        {
            return true;
        }

        // 2. 跨輪次比較同介面與本機 IPv4 組合之公網映射（IP 改變代表公網 NAT 切換或雙 WAN 路由漂移）
        if (previousSnapshot != null)
        {
            foreach (var b in currentGathering.Bindings)
            {
                if (b.IsLoopbackFallback) continue;
                var curIps = b.StunProbes
                    .Where(p => p.Succeeded && p.MappedEndPoint != null)
                    .Select(p => p.MappedEndPoint!.Address.ToString())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (curIps.Count == 0) continue;

                string ifaceIpKey = $"{b.Interface.InterfaceId}|{b.Interface.LocalIPv4}";
                if ((previousSnapshot.ExternalStunMappingsByInterface.TryGetValue(ifaceIpKey, out var prevEps) ||
                     previousSnapshot.ExternalStunMappingsByInterface.TryGetValue(b.Interface.InterfaceId, out prevEps)) &&
                    prevEps.Count > 0)
                {
                    var prevIps = prevEps.Select(e => e.Address.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (!curIps.Any(ip => prevIps.Contains(ip, StringComparer.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }
        }

        // 3. 對端回應之 ObservedEndPoint 與我方宣告的所有外部 Srflx 映射皆不一致（代表出向封包經 NAT 改寫至非預期公網 IP/Port）
        if (checkResult != null && !checkResult.Succeeded && checkResult.ObservedSelfPublicEndPoints.Count > 0)
        {
            var advertisedSrflx = currentGathering.Candidates
                .Where(c => c.Type == IceCandidateType.Srflx && c.ValidityStatus == CandidateValidityStatus.Valid && c.EndPoint != null && !IPAddress.IsLoopback(c.EndPoint.Address))
                .Select(c => c.EndPoint!)
                .ToHashSet();

            if (advertisedSrflx.Count > 0 &&
                checkResult.ObservedSelfPublicEndPoints.Any(obs => !IPAddress.IsLoopback(obs.Address) && !advertisedSrflx.Contains(obs)))
            {
                return true;
            }
        }

        return false;
    }

    public static P2PFailureReason ClassifyRoundFailure(
        CandidateGatheringResult? gathering,
        GatheringRoundSnapshot? previousSnapshot,
        int validLocalCount,
        SignalingExchangeOutcome? signalingOutcome,
        ConnectivityCheckResult? checkResult,
        int remoteCandidateCount,
        CancellationToken outerCt)
    {
        if (outerCt.IsCancellationRequested)
            return P2PFailureReason.Cancelled;

        if (signalingOutcome is { WorstStatus: SignalingCallStatus.SessionExpired })
            return P2PFailureReason.SessionExpired;

        // 本機收集失敗判定
        if (gathering == null || validLocalCount == 0 || gathering.Bindings.Count == 0)
        {
            if (gathering != null && gathering.Interfaces.Count > 0)
                return P2PFailureReason.SocketFailure;
            return P2PFailureReason.NoUsableInterface;
        }

        if (gathering.Bindings.All(b => b.IsDisposed || !CandidatePairBuilder.IsSocketAlive(b.Socket)))
            return P2PFailureReason.SocketFailure;

        var stunSummary = SummarizeStunProbes(gathering);

        // 信令失敗與交換逾時
        if (signalingOutcome != null && !signalingOutcome.Succeeded)
        {
            if (signalingOutcome.TransportErrorCount > 0 && remoteCandidateCount == 0)
                return P2PFailureReason.SignalingMessageTimeout;

            if (remoteCandidateCount == 0)
                return P2PFailureReason.CandidateExchangeTimeout;
        }

        if (remoteCandidateCount == 0)
            return P2PFailureReason.CandidateExchangeTimeout;

        // 連通性檢查與 NAT 映射判定
        if (checkResult != null && !checkResult.Succeeded)
        {
            if (string.Equals(checkResult.FinalFailureReason, "Cancelled", StringComparison.OrdinalIgnoreCase))
                return P2PFailureReason.Cancelled;

            if (string.Equals(checkResult.FinalFailureReason, "OneWayConnectivity", StringComparison.OrdinalIgnoreCase))
                return P2PFailureReason.OneWayConnectivity;

            if (string.Equals(checkResult.FinalFailureReason, "SocketFailure", StringComparison.OrdinalIgnoreCase))
                return P2PFailureReason.SocketFailure;

            if (HasNatMappingChanged(previousSnapshot, gathering, checkResult))
                return P2PFailureReason.NatMappingChanged;

            if (string.Equals(checkResult.FinalFailureReason, "NoValidCandidatePairs", StringComparison.OrdinalIgnoreCase))
            {
                if (stunSummary.TotalExternalProbes > 0 && !stunSummary.HasAnySuccess)
                    return stunSummary.IsTotalTimeout ? P2PFailureReason.StunTimeout : P2PFailureReason.StunServerUnavailable;

                return P2PFailureReason.AllCandidatePairsFailed;
            }

            if (stunSummary.TotalExternalProbes > 0 && !stunSummary.HasAnySuccess &&
                gathering.Candidates.All(c => c.Type != IceCandidateType.Srflx))
            {
                return stunSummary.IsTotalTimeout ? P2PFailureReason.StunTimeout : P2PFailureReason.StunServerUnavailable;
            }

            if (string.Equals(checkResult.FinalFailureReason, "AllCandidatePairsFailed", StringComparison.OrdinalIgnoreCase))
                return P2PFailureReason.AllCandidatePairsFailed;

            if (checkResult.AllPairs.Count > 1 &&
                checkResult.AllPairs.Select(p => p.FailureReason).Distinct().Count() > 1)
            {
                return P2PFailureReason.AllCandidatePairsFailed;
            }

            return P2PFailureReason.CandidatePairCheckTimeout;
        }

        if (stunSummary.IsTotalTimeout)
            return P2PFailureReason.StunTimeout;
        if (stunSummary.IsTotalServerUnavailable)
            return P2PFailureReason.StunServerUnavailable;

        return P2PFailureReason.AllCandidatePairsFailed;
    }
}
