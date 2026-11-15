using System.Net;

namespace RemoteDesk.Net;

/// <summary>
/// Phase 5 P2P 連線健康狀態。
/// </summary>
public enum P2PPathHealthState
{
    Healthy = 0,
    Degraded = 1,
    Suspect = 2,
    Failed = 3,
    Recovering = 4,
    ClosedNormally = 5
}

/// <summary>
/// Phase 5 P2P 路徑異常／失效判定分類（規格 5-2）。
/// </summary>
public enum P2PPathFailureType
{
    None = 0,
    TransientPacketLoss = 1,
    NetworkJitter = 2,
    SocketError = 3,
    InterfaceDown = 4,
    NatMappingChanged = 5,
    PeerProcessClosed = 6,
    PeerSessionTerminated = 7,
    HeartbeatTimeout = 8
}

/// <summary>
/// Phase 5 P2P 路徑失效後執行重連之階段記錄（規格 5-3 步驟 1~5）。
/// </summary>
public enum P2PReconnectionStep
{
    None = 0,
    RecheckedCurrentCandidates = 1,
    SwitchedToAlternateExistingPair = 2,
    RegatheredStunAndExchangedCandidates = 3,
    FullP2PRenegotiationFailed = 4
}

/// <summary>
/// Phase 5-1 持續監控指標快照。
/// </summary>
internal sealed record P2PPathMetrics(
    DateTime LastHeartbeatTimestamp,
    int HeartbeatTimeoutCount,
    int ConsecutiveHeartbeatTimeouts,
    double CurrentRttMs,
    double PacketLossRate,
    double RetransmissionRate,
    string CurrentLocalInterface,
    string CurrentLocalEndpoint,
    string CurrentRemoteEndpoint,
    DateTime LastSuccessfulReceiveTimestamp,
    DateTime LastSuccessfulSendTimestamp,
    uint PathEpoch,
    string SelectedPairId,
    P2PPathHealthState HealthState,
    P2PPathFailureType DetectedCondition,
    int ConsecutiveSocketErrors,
    int RecoveryAttemptCount,
    long RejectedStalePathPackets,
    DateTime CapturedAtUtc)
{
    public double PacketLossRatePercent => PacketLossRate * 100.0;
    public double RetransmissionRatePercent => RetransmissionRate * 100.0;
}

/// <summary>
/// Phase 5 路徑監控與重新連線參數設定（監控頻率與門檻皆可調，避免造成過度封包負載）。
/// </summary>
internal sealed class P2PPathMonitorOptions
{
    public int MonitorIntervalMs { get; set; } = 300;
    public int HeartbeatIntervalMs { get; set; } = 2000;
    public int HeartbeatTimeoutMs { get; set; } = 2200;
    public int MaxConsecutiveHeartbeatTimeouts { get; set; } = 3;

    public int SocketErrorFailureThreshold { get; set; } = 2;
    public double DegradedLossRateThreshold { get; set; } = 0.05;
    public double DegradedRetransmissionRateThreshold { get; set; } = 0.08;
    public double DegradedRttMsThreshold { get; set; } = 280.0;
    public double JitterRttSpikeMultiplier { get; set; } = 2.5;

    public int InterfaceCheckIntervalMs { get; set; } = 1000;
    public int NatMappingCheckIntervalMs { get; set; } = 15000;
    public int SessionStatusCheckIntervalMs { get; set; } = 5000;

    public bool AutoReconnectOnPathFailure { get; set; } = true;
    public int MaxRecoveryAttempts { get; set; } = 3;
    public int RecoveryCooldownMs { get; set; } = 1000;
    public int Step1RecheckTimeoutMs { get; set; } = 650;
    public int Step2AlternatePairTimeoutMs { get; set; } = 1200;
    public int Step3ExchangeTimeoutMs { get; set; } = 2500;
    public int Step4CheckTimeoutMs { get; set; } = 3500;
    public int RecoveryStunTimeoutMs { get; set; } = 1500;

    public IReadOnlyList<string> StunServers { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> SupplementalStunServers { get; set; } = new[]
    {
        "stun3.l.google.com:19302",
        "stun4.l.google.com:19302",
        "stun.stunprotocol.org:3478"
    };

    /// <summary>受控測試覆寫：檢查指定 InterfaceId（及目前本機位址）是否仍處於 UP 可用狀態。</summary>
    public Func<string, bool>? CustomInterfaceAliveCheck { get; set; }

    /// <summary>受控測試覆寫：查詢 active socket 當下公網 NAT 映射端點（null 代表未測得或 STUN 不可達）。</summary>
    public Func<CancellationToken, Task<IPEndPoint?>>? CustomActiveNatProbeAsync { get; set; }

    /// <summary>受控測試覆寫：於 Step 3 重新收集候選端點（預設呼叫 IceCandidateGatherer.GatherAsync）。</summary>
    public Func<int, string, string, IceGatheringOptions, CancellationToken, Task<CandidateGatheringResult>>? CustomGatherAsync { get; set; }

    /// <summary>受控測試覆寫：檢查或測試既有／新建 Candidate Pair（預設呼叫 IceConnectivityChecker.RunAsync）。</summary>
    public Func<int, CandidateGatheringResult, IReadOnlyList<IceCandidate>, string, string, string, string, bool, IceConnectivityOptions, CancellationToken, Task<ConnectivityCheckResult>>? CustomCheckAsync { get; set; }

    public Func<DateTime>? UtcNowProvider { get; set; }
}

/// <summary>
/// 由 Phase 4 成功連線保留下來、供 Phase 5 持續監控與路徑重新檢查使用之候選與備援 Socket 上下文。
/// </summary>
internal sealed class P2PPathSessionContext : IDisposable
{
    private bool _disposed;

    public required string SessionId { get; init; }
    public required string SelfDeviceId { get; init; }
    public required string RemoteDeviceId { get; init; }
    public required bool IsInitiator { get; init; }
    public required string ConnectionAttemptId { get; init; }

    public required CandidatePair ActivePair { get; set; }
    public required DiscoveredNetworkInterface ActiveInterface { get; set; }
    public required IceCandidate ActiveLocalCandidate { get; set; }
    public required IceCandidate ActiveRemoteCandidate { get; set; }

    public List<CandidatePair> KnownPairs { get; } = new();
    public List<IceCandidate> KnownLocalCandidates { get; } = new();
    public List<IceCandidate> KnownRemoteCandidates { get; } = new();
    public List<GatheredSocketBinding> StandbyBindings { get; } = new();

    public SignalingSessionInbox? SharedInbox { get; init; }
    public IReadOnlyList<string> StunServers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SupplementalStunServers { get; init; } = Array.Empty<string>();
    public IceGatheringOptions? BaseGatheringOptions { get; init; }
    public IceConnectivityOptions? BaseConnectivityOptions { get; init; }

    public int NextRecoveryEpoch { get; set; } = 1;

    public void ReplaceStandbyBindings(IEnumerable<GatheredSocketBinding> newStandbys)
    {
        foreach (var old in StandbyBindings)
        {
            if (newStandbys.Any(n => ReferenceEquals(n, old))) continue;
            try { old.Dispose(); } catch { }
        }
        StandbyBindings.Clear();
        StandbyBindings.AddRange(newStandbys);
    }

    public GatheredSocketBinding? DetachStandbyBindingBySocketId(string? socketId)
    {
        if (string.IsNullOrWhiteSpace(socketId)) return null;
        var found = StandbyBindings.FirstOrDefault(b => string.Equals(b.SocketId, socketId, StringComparison.OrdinalIgnoreCase));
        if (found != null)
        {
            StandbyBindings.Remove(found);
        }
        return found;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var b in StandbyBindings)
        {
            try { b.Dispose(); } catch { }
        }
        StandbyBindings.Clear();
    }
}

/// <summary>
/// Phase 5 單次路徑修復結果紀錄。
/// </summary>
internal sealed record P2PPathRecoveryReport(
    bool Succeeded,
    uint OldPathEpoch,
    uint NewPathEpoch,
    P2PPathFailureType TriggerReason,
    P2PReconnectionStep StepTaken,
    string OldPairId,
    string NewPairId,
    string OldLocalEndpoint,
    string NewLocalEndpoint,
    string OldRemoteEndpoint,
    string NewRemoteEndpoint,
    TimeSpan Elapsed,
    string Detail = "");
