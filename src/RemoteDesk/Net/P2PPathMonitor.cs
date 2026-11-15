using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using RemoteDesk.Common;

namespace RemoteDesk.Net;

/// <summary>
/// Phase 5 P2P 連線持續監控、路徑異常／失效分類與五步驟自動重新連線協調器。
/// 負責規格 5-1、5-2、5-3 之完整執行，並透過生命週期事件與 ReliableUdpStream / HostSession / FrameDecoder 安全協作。
/// </summary>
internal sealed class P2PPathMonitor : IAsyncDisposable, IDisposable
{
    private readonly ReliableUdpStream _stream;
    private readonly P2PPathSessionContext _context;
    private readonly IP2PSignalingTransport? _signaling;
    private readonly P2PPathMonitorOptions _options;
    private readonly Func<bool>? _peerClosedPredicate;
    private readonly Func<DateTime> _utcNow;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _recoveryLock = new(1, 1);
    private readonly object _stateLock = new();

    private Task? _monitorTask;
    private CancellationTokenSource? _standbyResponderCts;
    private readonly List<Task> _standbyResponderTasks = new();
    private readonly List<Task> _followerTasks = new();
    private PendingPeerSwitchRequest? _pendingPeerSwitch;
    private int _disposeStarted;

    private P2PPathHealthState _healthState = P2PPathHealthState.Healthy;
    private P2PPathFailureType _detectedCondition = P2PPathFailureType.None;
    private DateTime _lastHeartbeatTimestampUtc;
    private DateTime _lastHeartbeatProbeAttemptUtc;
    private DateTime _lastInterfaceCheckUtc;
    private DateTime _lastNatCheckUtc;
    private DateTime _lastSessionCheckUtc;
    private DateTime _lastPathSwitchUtc;
    private DateTime _healthySinceUtc;

    private int _heartbeatTimeoutCount;
    private int _consecutiveHeartbeatTimeouts;
    private double _baselineRttMs;
    private int _recoveryAttemptCount;
    private int _lastHandledRemoteRecoveryEpoch;
    private volatile bool _closedNormally;
    private volatile bool _isRecovering;
    private volatile bool _permanentFailureRaised;
    private bool _disposed;

    // 滑動視窗封包遺失率／重傳率基準快照
    private long _lastPacketsSentSnap;
    private long _lastUniqueLostSnap;
    private long _lastRetransmitsSnap;
    private double _windowPacketLossRate;
    private double _windowRetransmissionRate;

    public P2PPathMonitor(
        ReliableUdpStream stream,
        P2PPathSessionContext context,
        SignalingClient? signalingClient = null,
        P2PPathMonitorOptions? options = null,
        Func<bool>? peerClosedPredicate = null,
        bool autoStart = true)
        : this(
            stream,
            context,
            signalingClient != null ? new SignalingClientTransport(signalingClient) : null,
            options,
            peerClosedPredicate,
            autoStart)
    {
    }

    public P2PPathMonitor(
        ReliableUdpStream stream,
        P2PPathSessionContext context,
        IP2PSignalingTransport? signalingTransport,
        P2PPathMonitorOptions? options = null,
        Func<bool>? peerClosedPredicate = null,
        bool autoStart = true)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _signaling = signalingTransport;
        _options = options ?? new P2PPathMonitorOptions();
        _peerClosedPredicate = peerClosedPredicate;
        _utcNow = _options.UtcNowProvider ?? (() => DateTime.UtcNow);

        DateTime now = _utcNow();
        _lastHeartbeatTimestampUtc = _stream.LastSuccessfulReceiveTimestampUtc;
        _lastHeartbeatProbeAttemptUtc = now;
        _lastInterfaceCheckUtc = now;
        _lastNatCheckUtc = now;
        _lastSessionCheckUtc = now;
        _lastPathSwitchUtc = DateTime.MinValue;
        _healthySinceUtc = now;
        _baselineRttMs = _stream.CurrentSrttMs;
        _lastPacketsSentSnap = _stream.TotalPacketsSent;
        _lastUniqueLostSnap = _stream.UniquePacketsLost;
        _lastRetransmitsSnap = _stream.TotalRetransmissions;
        _lastHandledRemoteRecoveryEpoch = _context.SharedInbox?.HighestRecoveryEpochRequested ?? 0;

        _stream.SuppressInternalKeepAlive = true;
        _stream.EnsurePostPunchContext(BuildPostPunchContext(_context.ActivePair));
        _stream.AllowAlternatePairCheckResponses = true;
        _stream.PeerNominatedSwitchObserved += OnPeerNominatedSwitchObserved;

        if (autoStart)
        {
            Start();
        }
    }

    public event Action<P2PPathMetrics>? MetricsUpdated;
    public event Action<P2PPathFailureType>? PathRecoveryStarted;
    public event Action<P2PPathRecoveryReport>? PathRecoveryCompleted;
    public event Action<P2PPathFailureType, string>? PathClosedNormally;
    public event Action<P2PPathFailureType, string>? PathPermanentlyFailed;

    public P2PPathHealthState CurrentHealthState
    {
        get { lock (_stateLock) return _healthState; }
    }

    public P2PPathFailureType CurrentDetectedCondition
    {
        get { lock (_stateLock) return _detectedCondition; }
    }

    public bool IsClosedNormally => _closedNormally;
    public bool IsRecovering => _isRecovering;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateLock)
        {
            if (_monitorTask != null) return;
            StartStandbyRespondersLocked();
            _monitorTask = Task.Run(MonitorLoopAsync);
        }
    }

    /// <summary>
    /// 主動通知對端正常結束程序／連線（例如 HostMsg.Disconnect），避免誤判為網路斷線並停止重連。
    /// </summary>
    public void NotifyPeerClosedNormally(string detail = "Remote peer closed cleanly")
    {
        MarkClosedNormally(P2PPathFailureType.PeerProcessClosed, detail);
    }

    /// <summary>
    /// 取得規格 5-1 完整監控指標快照。
    /// </summary>
    public P2PPathMetrics GetMetrics()
    {
        DateTime now = _utcNow();
        lock (_stateLock)
        {
            return BuildMetricsSnapshotLocked(now);
        }
    }

    private async Task MonitorLoopAsync()
    {
        int intervalMs = Math.Clamp(_options.MonitorIntervalMs, 40, 5000);
        while (!_cts.IsCancellationRequested && !_disposed && !_stream.IsDisposed && !_closedNormally)
        {
            try
            {
                await EvaluateOnceAsync(_cts.Token).ConfigureAwait(false);
                if (_cts.IsCancellationRequested || _closedNormally || _permanentFailureRaised || _stream.IsDisposed)
                    break;

                await Task.Delay(intervalMs, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warn($"[ICE/PathMonitor] Loop error (Attempt={_context.ConnectionAttemptId}, Session={_context.SessionId}): {ex.Message}");
                try { await Task.Delay(Math.Min(intervalMs, 250), _cts.Token).ConfigureAwait(false); }
                catch { break; }
            }
        }
    }

    /// <summary>
    /// 執行單輪完整健康評估、條件分類與必要之 P2P 路徑重連；亦可供單元測試直接驅動。
    /// </summary>
    internal async Task<P2PPathMetrics> EvaluateOnceAsync(CancellationToken ct = default)
    {
        if (_disposed || _stream.IsDisposed || _closedNormally)
            return GetMetrics();

        DateTime now = _utcNow();

        // 1. 先分辨是否為正常關閉 / Session 已結束（規格 5-2 禁止誤入重連）
        if (_stream.RemoteCloseReceived || _peerClosedPredicate?.Invoke() == true)
        {
            MarkClosedNormally(P2PPathFailureType.PeerProcessClosed, "ReliableUdp TypeClose or remote Disconnect received");
            return GetMetrics();
        }

        // 2. 定期或於可疑時檢查 Signaling Session 狀態與對端是否發起 path-recheck
        bool shouldPollSignaling = _signaling != null && _context.SharedInbox != null &&
            ((now - _lastSessionCheckUtc).TotalMilliseconds >= _options.SessionStatusCheckIntervalMs ||
             Volatile.Read(ref _consecutiveHeartbeatTimeouts) >= 1);
        if (shouldPollSignaling && _signaling != null && _context.SharedInbox != null)
        {
            _lastSessionCheckUtc = now;
            var poll = await _context.SharedInbox.PollOnceAndAckDetailedAsync(_signaling, ct).ConfigureAwait(false);
            if (poll.Status == SignalingCallStatus.SessionExpired)
            {
                MarkClosedNormally(P2PPathFailureType.PeerSessionTerminated, $"Signaling session expired: {poll.ErrorDetail}");
                return GetMetrics();
            }

            int requestedEpoch = _context.SharedInbox.HighestRecoveryEpochRequested;
            if (requestedEpoch > _lastHandledRemoteRecoveryEpoch)
            {
                _lastHandledRemoteRecoveryEpoch = requestedEpoch;
                int requestedRound = _context.SharedInbox.HighestRecoveryRetryRound;
                string reasonText = _context.SharedInbox.LatestRecoveryReason;
                Log.Info($"[ICE/PathMonitor] Peer requested path recovery (Attempt={_context.ConnectionAttemptId}, Session={_context.SessionId}, RecoveryEpoch={requestedEpoch}, Round={requestedRound}, Reason={reasonText})");
                if (_options.AutoReconnectOnPathFailure && !_closedNormally)
                {
                    await TryRecoverFromLoopAsync(ParseFailureReasonOrDefault(reasonText, P2PPathFailureType.HeartbeatTimeout), requestedEpoch, requestedRound, ct).ConfigureAwait(false);
                    return GetMetrics();
                }
            }
        }

        // 3. 檢查本機介面狀態（InterfaceDown）
        bool? ifaceAlive = null;
        if ((now - _lastInterfaceCheckUtc).TotalMilliseconds >= _options.InterfaceCheckIntervalMs ||
            _stream.ConsecutiveSocketErrors > 0 ||
            _consecutiveHeartbeatTimeouts >= 1)
        {
            _lastInterfaceCheckUtc = now;
            ifaceAlive = CheckLocalInterfaceAlive(_context.ActiveInterface);
            if (ifaceAlive == false)
            {
                Log.Warn($"[ICE/PathMonitor] Detected active local interface down (Attempt={_context.ConnectionAttemptId}, Session={_context.SessionId}): {_context.ActiveInterface.InterfaceName} ({_context.ActiveInterface.InterfaceId})");
                SetConditionLocked(P2PPathHealthState.Failed, P2PPathFailureType.InterfaceDown);
                if (_options.AutoReconnectOnPathFailure)
                {
                    await TryRecoverFromLoopAsync(P2PPathFailureType.InterfaceDown, ct).ConfigureAwait(false);
                    return GetMetrics();
                }
            }
        }

        // 4. 檢查底層 Socket 嚴重或連續錯誤（SocketError）
        if (_stream.FatalSocketErrorOccurred ||
            !_stream.IsUnderlyingSocketAlive ||
            _stream.ConsecutiveSocketErrors >= Math.Max(1, _options.SocketErrorFailureThreshold))
        {
            Log.Warn($"[ICE/PathMonitor] Detected UDP socket failure (Attempt={_context.ConnectionAttemptId}, Session={_context.SessionId}, Fatal={_stream.FatalSocketErrorOccurred}, Alive={_stream.IsUnderlyingSocketAlive}, ConsecErr={_stream.ConsecutiveSocketErrors}, LastErr={_stream.LastSocketError})");
            SetConditionLocked(P2PPathHealthState.Failed, P2PPathFailureType.SocketError);
            if (_options.AutoReconnectOnPathFailure)
            {
                await TryRecoverFromLoopAsync(P2PPathFailureType.SocketError, ct).ConfigureAwait(false);
                return GetMetrics();
            }
        }

        // 5. 主動輕量心跳探針 (HeartbeatIntervalMs)；禁止單一遺失即判失敗
        if ((now - _lastHeartbeatProbeAttemptUtc).TotalMilliseconds >= Math.Max(50, _options.HeartbeatIntervalMs))
        {
            _lastHeartbeatProbeAttemptUtc = now;
            DateTime probeSentAtUtc = _utcNow();
            double? rtt = await _stream.SendHeartbeatProbeAsync(_options.HeartbeatTimeoutMs, ct).ConfigureAwait(false);
            now = _utcNow();

            if (_stream.RemoteCloseReceived || _peerClosedPredicate?.Invoke() == true)
            {
                MarkClosedNormally(P2PPathFailureType.PeerProcessClosed, "Remote peer closed during heartbeat probe");
                return GetMetrics();
            }

            if (rtt.HasValue)
            {
                lock (_stateLock)
                {
                    _lastHeartbeatTimestampUtc = now;
                    _consecutiveHeartbeatTimeouts = 0;
                    _baselineRttMs = _baselineRttMs <= 0.1
                        ? rtt.Value
                        : (_baselineRttMs * 0.8) + (rtt.Value * 0.2);
                }
            }
            else if (_stream.LastSuccessfulReceiveTimestampUtc > probeSentAtUtc)
            {
                // 雖 Pong 遲到但同一週期內仍有實際資料/ACK 抵達，路徑仍通暢
                lock (_stateLock)
                {
                    _lastHeartbeatTimestampUtc = _stream.LastSuccessfulReceiveTimestampUtc;
                    _consecutiveHeartbeatTimeouts = 0;
                }
            }
            else
            {
                int consec = Interlocked.Increment(ref _consecutiveHeartbeatTimeouts);
                Interlocked.Increment(ref _heartbeatTimeoutCount);
                Log.Warn($"[ICE/PathMonitor] Heartbeat timeout (Attempt={_context.ConnectionAttemptId}, Session={_context.SessionId}, {consec}/{_options.MaxConsecutiveHeartbeatTimeouts}) on Pair={_context.ActivePair.PairId}, Epoch={_stream.PathEpoch}");

                if (consec >= Math.Max(1, _options.MaxConsecutiveHeartbeatTimeouts))
                {
                    SetConditionLocked(P2PPathHealthState.Failed, P2PPathFailureType.HeartbeatTimeout);
                    if (_options.AutoReconnectOnPathFailure)
                    {
                        await TryRecoverFromLoopAsync(P2PPathFailureType.HeartbeatTimeout, ct).ConfigureAwait(false);
                        return GetMetrics();
                    }
                }
            }
        }

        // 6. NAT 映射變化偵測（定期或於心跳連續異常時主動複檢）
        bool shouldProbeNat = (_options.CustomActiveNatProbeAsync != null || _context.ActiveLocalCandidate.Type == IceCandidateType.Srflx) &&
            ((now - _lastNatCheckUtc).TotalMilliseconds >= _options.NatMappingCheckIntervalMs ||
             Volatile.Read(ref _consecutiveHeartbeatTimeouts) >= 1);
        if (shouldProbeNat && !_closedNormally)
        {
            _lastNatCheckUtc = now;
            IPEndPoint? mappedNow = await ProbeActiveNatMappingAsync(ct).ConfigureAwait(false);
            IPEndPoint? expectedMapped = _context.ActiveLocalCandidate.EndPoint;
            if (mappedNow != null && expectedMapped != null &&
                (_options.CustomActiveNatProbeAsync != null || _context.ActiveLocalCandidate.Type == IceCandidateType.Srflx) &&
                (!mappedNow.Address.Equals(expectedMapped.Address) || mappedNow.Port != expectedMapped.Port))
            {
                Log.Warn($"[ICE/PathMonitor] NAT mapping changed detected (Attempt={_context.ConnectionAttemptId}, Session={_context.SessionId}): Expected={expectedMapped}, Observed={mappedNow}");
                SetConditionLocked(P2PPathHealthState.Failed, P2PPathFailureType.NatMappingChanged);
                if (_options.AutoReconnectOnPathFailure)
                {
                    await TryRecoverFromLoopAsync(P2PPathFailureType.NatMappingChanged, ct).ConfigureAwait(false);
                    return GetMetrics();
                }
            }
        }

        // 7. 計算滑動視窗品質指標並分類 TransientPacketLoss / NetworkJitter / Suspect / Healthy
        P2PPathMetrics snapshot;
        lock (_stateLock)
        {
            UpdateWindowRatesLocked();
            double currentRtt = ResolveCurrentRttMsLocked();

            P2PPathHealthState nextHealth;
            P2PPathFailureType nextCondition;
            int consecTimeouts = Volatile.Read(ref _consecutiveHeartbeatTimeouts);

            if (consecTimeouts > 0)
            {
                nextHealth = P2PPathHealthState.Suspect;
                nextCondition = P2PPathFailureType.HeartbeatTimeout;
                _healthySinceUtc = now;
            }
            else if (_stream.ConsecutiveSocketErrors > 0)
            {
                nextHealth = P2PPathHealthState.Suspect;
                nextCondition = P2PPathFailureType.SocketError;
                _healthySinceUtc = now;
            }
            else if (currentRtt >= _options.DegradedRttMsThreshold ||
                     (_baselineRttMs >= 1.0 && currentRtt >= _baselineRttMs * _options.JitterRttSpikeMultiplier && currentRtt >= 60.0))
            {
                nextHealth = P2PPathHealthState.Degraded;
                nextCondition = P2PPathFailureType.NetworkJitter;
                _healthySinceUtc = now;
            }
            else if (_windowPacketLossRate >= _options.DegradedLossRateThreshold ||
                     _windowRetransmissionRate >= _options.DegradedRetransmissionRateThreshold)
            {
                nextHealth = P2PPathHealthState.Degraded;
                nextCondition = P2PPathFailureType.TransientPacketLoss;
                _healthySinceUtc = now;
            }
            else
            {
                nextHealth = P2PPathHealthState.Healthy;
                nextCondition = P2PPathFailureType.None;
                if (_healthState != P2PPathHealthState.Healthy)
                    _healthySinceUtc = now;
                else if ((now - _healthySinceUtc).TotalSeconds >= 25.0)
                    _recoveryAttemptCount = 0; // 穩定持續健康後重置恢復重試額度
            }

            _healthState = nextHealth;
            _detectedCondition = nextCondition;
            snapshot = BuildMetricsSnapshotLocked(now);
        }

        MetricsUpdated?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>
    /// 執行規格 5-3 五步驟 P2P 路徑重新檢查與恢復：
    /// 步驟 1 重檢現行候選 -> 步驟 2 嘗試其他尚有效 Candidate Pair ->
    /// 步驟 3 重新收集 STUN -> 步驟 4 Signaling 交換更新候選與雙向 Connectivity Check -> 步驟 5 切換至新有效 Pair。
    /// </summary>
    public Task<P2PPathRecoveryReport> RecoverPathAsync(P2PPathFailureType triggerReason, CancellationToken ct = default) =>
        RecoverPathCoreAsync(triggerReason, peerRequestedRecoveryEpoch: 0, peerRequestedRetryRound: 0, ct);

    private async Task<P2PPathRecoveryReport> RecoverPathCoreAsync(
        P2PPathFailureType triggerReason,
        int peerRequestedRecoveryEpoch,
        int peerRequestedRetryRound,
        CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        var token = linkedCts.Token;

        await _recoveryLock.WaitAsync(token).ConfigureAwait(false);
        uint oldEpoch = _stream.PathEpoch;
        string oldPairId = _context.ActivePair.PairId;
        string oldLocalEp = _stream.LocalEndPoint?.ToString() ?? "";
        string oldRemoteEp = _stream.RemoteEndPoint.ToString() ?? "";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (_closedNormally || _disposed || _stream.IsDisposed)
            {
                return CreateReport(false, oldEpoch, oldEpoch, triggerReason, P2PReconnectionStep.None,
                    oldPairId, oldPairId, oldLocalEp, oldRemoteEp, sw.Elapsed, "Monitor disposed or closed normally");
            }

            int attempt = Interlocked.Increment(ref _recoveryAttemptCount);
            if (attempt > Math.Max(1, _options.MaxRecoveryAttempts))
            {
                string maxMsg = $"Exceeded MaxRecoveryAttempts ({_options.MaxRecoveryAttempts})";
                HandlePermanentRecoveryFailure(triggerReason, maxMsg);
                return CreateReport(false, oldEpoch, oldEpoch, triggerReason, P2PReconnectionStep.FullP2PRenegotiationFailed,
                    oldPairId, oldPairId, oldLocalEp, oldRemoteEp, sw.Elapsed, maxMsg);
            }

            _isRecovering = true;
            _stream.SuppressDeadDisconnectDuringRecovery = true;
            SetConditionLocked(P2PPathHealthState.Recovering, triggerReason);
            PathRecoveryStarted?.Invoke(triggerReason);
            await StopStandbyRespondersAsync().ConfigureAwait(false);

            Log.Info($"[ICE/Reconnect] Starting recovery ConnectionAttemptId={_context.ConnectionAttemptId}, RecoveryAttempt={attempt}/{_options.MaxRecoveryAttempts}, Session={_context.SessionId}, Reason={triggerReason}, OldPair={oldPairId}, OldEpoch={oldEpoch}");

            int targetRound;
            if (peerRequestedRecoveryEpoch > 0)
            {
                _context.NextRecoveryEpoch = Math.Max(_context.NextRecoveryEpoch, peerRequestedRecoveryEpoch + 1);
                targetRound = Math.Max(2, peerRequestedRetryRound > 0
                    ? peerRequestedRetryRound
                    : Math.Max(_context.SharedInbox?.HighestRemoteRound ?? 1, 2));
            }
            else
            {
                int baseRound = Math.Max(_context.SharedInbox?.HighestRemoteRound ?? 1, _context.SharedInbox?.HighestRecoveryRetryRound ?? 1);
                targetRound = Math.Max(2, baseRound + 1);
            }

            // 若具 Signaling 通道且由本端主動發起恢復，通知對端進入路徑複檢並先拉取最新待處理候選/狀態（不重覆回送以免形成 echo 風暴）
            if (_signaling != null)
            {
                if (peerRequestedRecoveryEpoch <= 0)
                {
                    int reqEpoch = _context.NextRecoveryEpoch++;
                    _lastHandledRemoteRecoveryEpoch = Math.Max(_lastHandledRemoteRecoveryEpoch, reqEpoch);
                    var recheckSig = new IceSignal
                    {
                        Type = "path-recheck",
                        SessionId = _context.SessionId,
                        SenderDeviceId = _context.SelfDeviceId,
                        ReceiverDeviceId = _context.RemoteDeviceId,
                        RetryRound = targetRound,
                        RetryReason = triggerReason.ToString(),
                        RecoveryEpoch = reqEpoch
                    };
                    var postRes = await _signaling.PostSignalAsync(
                        _context.SessionId,
                        _context.RemoteDeviceId,
                        recheckSig.ToJson(),
                        "path-recheck",
                        token,
                        $"{_context.ConnectionAttemptId}-{_context.SelfDeviceId}-recheck-{reqEpoch}").ConfigureAwait(false);

                    if (postRes.Status == SignalingCallStatus.SessionExpired)
                    {
                        MarkClosedNormally(P2PPathFailureType.PeerSessionTerminated, $"Signaling session expired during path recovery: {postRes.ErrorDetail}");
                        return CreateReport(false, oldEpoch, oldEpoch, P2PPathFailureType.PeerSessionTerminated, P2PReconnectionStep.None,
                            oldPairId, oldPairId, oldLocalEp, oldRemoteEp, sw.Elapsed, "SessionExpired");
                    }
                }

                if (_context.SharedInbox != null)
                {
                    var prePoll = await _context.SharedInbox.PollOnceAndAckDetailedAsync(_signaling, token).ConfigureAwait(false);
                    if (prePoll.Status == SignalingCallStatus.SessionExpired)
                    {
                        MarkClosedNormally(P2PPathFailureType.PeerSessionTerminated, $"Signaling session expired during poll: {prePoll.ErrorDetail}");
                        return CreateReport(false, oldEpoch, oldEpoch, P2PPathFailureType.PeerSessionTerminated, P2PReconnectionStep.None,
                            oldPairId, oldPairId, oldLocalEp, oldRemoteEp, sw.Elapsed, "SessionExpired");
                    }
                    _lastHandledRemoteRecoveryEpoch = Math.Max(_lastHandledRemoteRecoveryEpoch, _context.SharedInbox.HighestRecoveryEpochRequested);
                    if (_context.SharedInbox.HighestRecoveryRetryRound > targetRound)
                        targetRound = _context.SharedInbox.HighestRecoveryRetryRound;
                }
            }

            // 步驟 1：首先重新檢查現行候選端點 (若明確為本地介面失效/Socket損毀/NAT映射變更則快速跳過至步驟 2)
            bool activeLocalUsable = triggerReason != P2PPathFailureType.InterfaceDown &&
                                     triggerReason != P2PPathFailureType.NatMappingChanged &&
                                     !_stream.FatalSocketErrorOccurred &&
                                     _stream.IsUnderlyingSocketAlive &&
                                     CheckLocalInterfaceAlive(_context.ActiveInterface);

            if (activeLocalUsable)
            {
                var step1 = await TryStep1RecheckCurrentAsync(oldEpoch, oldPairId, oldLocalEp, oldRemoteEp, triggerReason, sw, token).ConfigureAwait(false);
                if (step1 != null)
                {
                    FinalizeRecoveredStateLocked();
                    StartStandbyRespondersLocked();
                    PathRecoveryCompleted?.Invoke(step1);
                    return step1;
                }
            }

            // 步驟 2：嘗試其他仍有效之 Candidate Pair（使用既有的備援介面 Socket 與已知遠端候選）
            var step2 = await TryStep2AlternatePairsAsync(oldEpoch, oldPairId, oldLocalEp, oldRemoteEp, triggerReason, activeLocalUsable, sw, token).ConfigureAwait(false);
            if (step2 != null)
            {
                FinalizeRecoveredStateLocked();
                StartStandbyRespondersLocked();
                PathRecoveryCompleted?.Invoke(step2);
                return step2;
            }

            // 步驟 3 ~ 5：重新收集 STUN 與多網卡 -> Signaling 交換更新候選並執行雙向 Connectivity Check -> 切換新有效 Pair
            var step34 = await TryStep3And4RegatherAndNegotiateAsync(oldEpoch, targetRound, oldPairId, oldLocalEp, oldRemoteEp, triggerReason, sw, token).ConfigureAwait(false);
            if (step34 != null)
            {
                FinalizeRecoveredStateLocked();
                StartStandbyRespondersLocked();
                PathRecoveryCompleted?.Invoke(step34);
                return step34;
            }

            string failDetail = $"All recovery steps (1-5) exhausted for reason {triggerReason}";
            Log.Warn($"[ICE/Reconnect] Failed: Attempt={_context.ConnectionAttemptId}, Session={_context.SessionId}, OldPair={oldPairId}, {failDetail}");
            SetConditionLocked(P2PPathHealthState.Failed, triggerReason);

            if (attempt >= _options.MaxRecoveryAttempts)
            {
                HandlePermanentRecoveryFailure(triggerReason, failDetail);
            }

            var failReport = CreateReport(false, oldEpoch, oldEpoch, triggerReason, P2PReconnectionStep.FullP2PRenegotiationFailed,
                oldPairId, oldPairId, oldLocalEp, oldRemoteEp, sw.Elapsed, failDetail);
            StartStandbyRespondersLocked();
            PathRecoveryCompleted?.Invoke(failReport);
            return failReport;
        }
        finally
        {
            _isRecovering = false;
            _stream.SuppressDeadDisconnectDuringRecovery = false;
            try
            {
                if (!_disposed && !_closedNormally && !_stream.IsDisposed)
                {
                    await DrainPendingPeerSwitchWhileLockedAsync(_cts.Token).ConfigureAwait(false);
                }
            }
            catch { }
            try { _recoveryLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private Task TryRecoverFromLoopAsync(P2PPathFailureType reason, CancellationToken ct) =>
        TryRecoverFromLoopAsync(reason, peerRequestedRecoveryEpoch: 0, peerRequestedRetryRound: 0, ct);

    private async Task TryRecoverFromLoopAsync(
        P2PPathFailureType reason,
        int peerRequestedRecoveryEpoch,
        int peerRequestedRetryRound,
        CancellationToken ct)
    {
        var report = await RecoverPathCoreAsync(reason, peerRequestedRecoveryEpoch, peerRequestedRetryRound, ct).ConfigureAwait(false);
        if (!report.Succeeded && !_closedNormally && !_permanentFailureRaised && _options.RecoveryCooldownMs > 0)
        {
            await Task.Delay(_options.RecoveryCooldownMs, ct).ConfigureAwait(false);
        }
    }

    private async Task<P2PPathRecoveryReport?> TryStep1RecheckCurrentAsync(
        uint oldEpoch,
        string oldPairId,
        string oldLocalEp,
        string oldRemoteEp,
        P2PPathFailureType triggerReason,
        System.Diagnostics.Stopwatch sw,
        CancellationToken ct)
    {
        if (!await _stream.PauseReceiveLoopForCheckAsync(1500, ct).ConfigureAwait(false))
            return null;

        bool switched = false;
        try
        {
            Socket activeSock = _stream.ActiveSocket;
            if (!CandidatePairBuilder.IsSocketAlive(activeSock))
                return null;

            IPEndPoint? localEp = activeSock.LocalEndPoint as IPEndPoint;
            if (localEp == null) return null;

            uint targetEpoch = oldEpoch + 1;
            string recheckSockId = $"active-recheck-{targetEpoch}";
            var clonedLocal = CloneCandidateForCheck(_context.ActiveLocalCandidate, recheckSockId, $"recheck-{targetEpoch}");

            using var gathering = BuildNonOwningGathering(new[]
            {
                new NonOwningBindingSpec(recheckSockId, _context.ActiveInterface, activeSock, localEp, clonedLocal)
            });

            var connOpts = BuildCheckOptions(_options.Step1RecheckTimeoutMs, maxAttempts: 3, forceControllingRole: true);
            var checkRes = await RunCheckCoreAsync(
                round: (int)targetEpoch,
                gathering,
                new[] { _context.ActiveRemoteCandidate },
                $"{_context.ConnectionAttemptId}-s1-{targetEpoch}",
                connOpts,
                ct).ConfigureAwait(false);

            if (!checkRes.Succeeded || checkRes.SelectedPair == null || checkRes.SelectedRemoteEndPoint == null)
                return null;

            var winner = checkRes.SelectedPair;
            var newPostCtx = BuildPostPunchContext(winner, checkRes.FinalRole == IceRole.Controlling, checkRes.TieBreaker);
            uint newEpoch = await _stream.SwitchPathAsync(
                activeSock,
                checkRes.SelectedRemoteEndPoint,
                newOwnsSocket: true,
                updatedPostPunchContext: newPostCtx,
                callerAlreadyPausedReceive: true,
                ct: ct,
                targetEpoch: targetEpoch).ConfigureAwait(false);
            switched = true;

            UpdateActiveContextFromPair(winner, _context.ActiveInterface);
            Log.Info($"[ICE/Reconnect] Step 1 succeeded (rechecked active path): Attempt={_context.ConnectionAttemptId}, Session={_context.SessionId}, OldEpoch={oldEpoch} -> NewEpoch={newEpoch}, Pair={winner.PairId}, Elapsed={sw.ElapsedMilliseconds}ms");
            return CreateReport(true, oldEpoch, newEpoch, triggerReason, P2PReconnectionStep.RecheckedCurrentCandidates,
                oldPairId, winner.PairId, oldLocalEp, oldRemoteEp, sw.Elapsed, "Active pair confirmed reachable again");
        }
        finally
        {
            if (!switched)
            {
                _stream.ResumeReceiveAfterCheck();
            }
        }
    }

    private async Task<P2PPathRecoveryReport?> TryStep2AlternatePairsAsync(
        uint oldEpoch,
        string oldPairId,
        string oldLocalEp,
        string oldRemoteEp,
        P2PPathFailureType triggerReason,
        bool includeActiveSocket,
        System.Diagnostics.Stopwatch sw,
        CancellationToken ct)
    {
        var specs = new List<NonOwningBindingSpec>();
        var ifaceBySocketId = new Dictionary<string, DiscoveredNetworkInterface>(StringComparer.OrdinalIgnoreCase);

        bool pausedActive = false;
        if (includeActiveSocket)
        {
            pausedActive = await _stream.PauseReceiveLoopForCheckAsync(1500, ct).ConfigureAwait(false);
        }

        bool switched = false;
        try
        {
            // 先確認是否有不同於目前失效 ActiveRemote 之其他遠端候選；若完全沒有任何備援本地 Socket 也沒有其他遠端候選，Step 1 已測過此單一配對，可直接進入 Step 3
            var altRemotes = _context.KnownRemoteCandidates
                .Where(rc => !string.Equals(rc.CandidateId, _context.ActiveRemoteCandidate.CandidateId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (altRemotes.Count == 0 && _context.SharedInbox != null)
            {
                foreach (var rc in _context.SharedInbox.RemoteCandidates)
                {
                    if (!string.Equals(rc.CandidateId, _context.ActiveRemoteCandidate.CandidateId, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals($"{rc.Ip}:{rc.Port}", $"{_context.ActiveRemoteCandidate.Ip}:{_context.ActiveRemoteCandidate.Port}", StringComparison.OrdinalIgnoreCase))
                    {
                        altRemotes.Add(rc);
                    }
                }
            }

            uint targetEpoch = oldEpoch + 1;

            foreach (var standby in _context.StandbyBindings)
            {
                if (standby.IsDisposed || standby.IsDetached || !CandidatePairBuilder.IsSocketAlive(standby.Socket))
                    continue;
                if (!CheckLocalInterfaceAlive(standby.Interface))
                    continue;

                var cands = standby.Candidates.Count > 0
                    ? standby.Candidates
                    : _context.KnownLocalCandidates.Where(c => string.Equals(c.SocketId, standby.SocketId, StringComparison.OrdinalIgnoreCase)).ToList();
                var cloned = cands.Select(c => CloneCandidateForCheck(c, standby.SocketId, $"s2-{targetEpoch}")).ToList();
                if (cloned.Count == 0) continue;

                specs.Add(new NonOwningBindingSpec(standby.SocketId, standby.Interface, standby.Socket, standby.LocalEndPoint, cloned));
                ifaceBySocketId[standby.SocketId] = standby.Interface;
            }

            if (pausedActive && altRemotes.Count > 0 && CandidatePairBuilder.IsSocketAlive(_stream.ActiveSocket) &&
                _stream.ActiveSocket.LocalEndPoint is IPEndPoint activeLocalEp)
            {
                string activeSockId = "active-step2";
                var activeCand = CloneCandidateForCheck(_context.ActiveLocalCandidate, activeSockId, $"s2a-{targetEpoch}");
                specs.Add(new NonOwningBindingSpec(activeSockId, _context.ActiveInterface, _stream.ActiveSocket, activeLocalEp, activeCand));
                ifaceBySocketId[activeSockId] = _context.ActiveInterface;
            }

            if (specs.Count == 0)
                return null;

            var candidateRemotes = new List<IceCandidate>();
            candidateRemotes.AddRange(altRemotes);
            if (altRemotes.Count == 0 || specs.Any(s => !string.Equals(s.SocketId, "active-step2", StringComparison.OrdinalIgnoreCase)))
            {
                if (!candidateRemotes.Any(r => string.Equals(r.CandidateId, _context.ActiveRemoteCandidate.CandidateId, StringComparison.OrdinalIgnoreCase)))
                    candidateRemotes.Add(_context.ActiveRemoteCandidate);
            }

            if (_context.SharedInbox != null)
            {
                foreach (var rc in _context.SharedInbox.RemoteCandidates)
                {
                    if (!candidateRemotes.Any(x => string.Equals(x.Ip, rc.Ip, StringComparison.OrdinalIgnoreCase) && x.Port == rc.Port))
                        candidateRemotes.Add(rc);
                }
            }

            if (candidateRemotes.Count == 0)
                return null;

            using var gathering = BuildNonOwningGathering(specs);
            var connOpts = BuildCheckOptions(_options.Step2AlternatePairTimeoutMs, maxAttempts: 4, forceControllingRole: true);
            var checkRes = await RunCheckCoreAsync(
                round: (int)(targetEpoch + 1),
                gathering,
                candidateRemotes,
                $"{_context.ConnectionAttemptId}-s2-{targetEpoch}",
                connOpts,
                ct).ConfigureAwait(false);

            if (!checkRes.Succeeded || checkRes.SelectedPair == null || checkRes.SelectedRemoteEndPoint == null)
                return null;

            var winner = checkRes.SelectedPair;
            bool winnerIsActiveSock = string.Equals(winner.SocketId, "active-step2", StringComparison.OrdinalIgnoreCase);
            Socket winnerSock;
            DiscoveredNetworkInterface winnerIface = ifaceBySocketId.TryGetValue(winner.SocketId, out var foundIface)
                ? foundIface
                : _context.ActiveInterface;

            if (winnerIsActiveSock)
            {
                winnerSock = _stream.ActiveSocket;
            }
            else
            {
                var matchedStandby = _context.DetachStandbyBindingBySocketId(winner.SocketId);
                if (matchedStandby == null) return null;
                winnerSock = matchedStandby.DetachSocket();
                matchedStandby.Dispose();
            }

            var newPostCtx = BuildPostPunchContext(winner, checkRes.FinalRole == IceRole.Controlling, checkRes.TieBreaker);
            uint newEpoch = await _stream.SwitchPathAsync(
                winnerSock,
                checkRes.SelectedRemoteEndPoint,
                newOwnsSocket: true,
                updatedPostPunchContext: newPostCtx,
                callerAlreadyPausedReceive: pausedActive,
                ct: ct,
                targetEpoch: targetEpoch).ConfigureAwait(false);
            switched = true;

            UpdateActiveContextFromPair(winner, winnerIface);
            Log.Info($"[ICE/Reconnect] Step 2 succeeded (switched to alternate candidate pair): Attempt={_context.ConnectionAttemptId}, Session={_context.SessionId}, OldPair={oldPairId} -> NewPair={winner.PairId}, Interface={winnerIface.InterfaceName}, NewEpoch={newEpoch}, Elapsed={sw.ElapsedMilliseconds}ms");
            return CreateReport(true, oldEpoch, newEpoch, triggerReason, P2PReconnectionStep.SwitchedToAlternateExistingPair,
                oldPairId, winner.PairId, oldLocalEp, oldRemoteEp, sw.Elapsed, $"Switched cleanly via interface {winnerIface.InterfaceName}");
        }
        finally
        {
            if (pausedActive && !switched)
            {
                _stream.ResumeReceiveAfterCheck();
            }
        }
    }

    private async Task<P2PPathRecoveryReport?> TryStep3And4RegatherAndNegotiateAsync(
        uint oldEpoch,
        int targetRound,
        string oldPairId,
        string oldLocalEp,
        string oldRemoteEp,
        P2PPathFailureType triggerReason,
        System.Diagnostics.Stopwatch sw,
        CancellationToken ct)
    {
        int round = Math.Max(2, targetRound);
        var servers = (_options.StunServers.Count > 0 ? _options.StunServers : _context.StunServers)
            .Concat(_options.SupplementalStunServers)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var gatherOpts = new IceGatheringOptions
        {
            StunServers = servers.Count > 0 ? servers : StunClient.DefaultStunServers,
            StunTimeoutMs = _options.RecoveryStunTimeoutMs,
            MaxConcurrentInterfaces = _context.BaseGatheringOptions?.MaxConcurrentInterfaces ?? 6,
            IncludeLoopbackFallback = _context.BaseGatheringOptions?.IncludeLoopbackFallback ?? true,
            SkipStunWhenNoRoute = _context.BaseGatheringOptions?.SkipStunWhenNoRoute ?? false,
            DiscoveryOptions = _context.BaseGatheringOptions?.DiscoveryOptions,
            OverrideInterfaces = _context.BaseGatheringOptions?.OverrideInterfaces
        };

        string attemptId = $"{_context.ConnectionAttemptId}-{_context.SelfDeviceId}-s3-{oldEpoch + 1}";
        CandidateGatheringResult? gathering = null;
        try
        {
            gathering = await RunGatherCoreAsync(round, _context.SessionId, attemptId, gatherOpts, ct).ConfigureAwait(false);
            var validLocals = gathering.Candidates.Where(c =>
                c.ValidityStatus == CandidateValidityStatus.Valid &&
                CandidatePairBuilder.IsSocketAlive(gathering.GetSocketForCandidate(c))).ToList();

            if (validLocals.Count == 0)
            {
                Log.Warn($"[ICE/Reconnect] Step 3 STUN/interface regather found zero valid local candidates (Attempt={_context.ConnectionAttemptId}, Session={_context.SessionId})");
                return null;
            }

            IReadOnlyList<IceCandidate> remoteCandidates = _context.KnownRemoteCandidates;
            if (_signaling != null && _context.SharedInbox != null)
            {
                var pubOutcome = await P2PRetryCoordinator.PublishRoundCandidatesAsync(
                    _signaling,
                    _context.SessionId,
                    _context.SelfDeviceId,
                    _context.RemoteDeviceId,
                    attemptId,
                    round,
                    P2PFailureReason.NatMappingChanged,
                    validLocals,
                    ct).ConfigureAwait(false);

                if (pubOutcome.WorstStatus == SignalingCallStatus.SessionExpired)
                {
                    MarkClosedNormally(P2PPathFailureType.PeerSessionTerminated, pubOutcome.Detail);
                    return null;
                }

                var waitOutcome = await P2PRetryCoordinator.WaitForRoundRemoteCandidatesAsync(
                    _signaling,
                    _context.SharedInbox,
                    round,
                    _options.Step3ExchangeTimeoutMs,
                    100,
                    pubOutcome,
                    ct,
                    allowEarlyPriorFallback: true).ConfigureAwait(false);

                if (waitOutcome.WorstStatus == SignalingCallStatus.SessionExpired)
                {
                    MarkClosedNormally(P2PPathFailureType.PeerSessionTerminated, waitOutcome.Detail);
                    return null;
                }

                remoteCandidates = _context.SharedInbox.GetEffectiveRemoteCandidates(round, allowFallbackToOlderRounds: true);
            }

            if (remoteCandidates.Count == 0)
                return null;

            var connOpts = BuildCheckOptions(_options.Step4CheckTimeoutMs, maxAttempts: 5);
            if (_signaling != null && _context.SharedInbox != null)
            {
                connOpts.IsRemoteExchangeCompleteProvider = () => _context.SharedInbox.IsRoundDone(round);
                connOpts.IncrementalRemoteCandidatePollProvider = async pollCt =>
                {
                    if (_context.SharedInbox.IsRoundDone(round))
                        return Array.Empty<IceCandidate>();
                    var poll = await _context.SharedInbox.PollOnceAndAckDetailedAsync(_signaling, pollCt).ConfigureAwait(false);
                    if (poll.Status == SignalingCallStatus.SessionExpired)
                        return Array.Empty<IceCandidate>();
                    return _context.SharedInbox.GetEffectiveRemoteCandidates(round, allowFallbackToOlderRounds: true);
                };
            }

            var checkRes = await RunCheckCoreAsync(round, gathering, remoteCandidates, attemptId, connOpts, ct).ConfigureAwait(false);
            if (!checkRes.Succeeded || checkRes.SelectedPair == null || checkRes.SelectedRemoteEndPoint == null)
                return null;

            var winner = checkRes.SelectedPair;
            Socket? winnerSock = gathering.DetachWinningSocket(winner.SocketId);
            if (winnerSock == null) return null;

            var winnerIface = gathering.Bindings.FirstOrDefault(b => string.Equals(b.SocketId, winner.SocketId, StringComparison.OrdinalIgnoreCase))?.Interface
                              ?? _context.ActiveInterface;

            // 將此次收集其餘存活的備援 Socket 轉移給下一次監控使用
            var nextStandbys = new List<GatheredSocketBinding>();
            foreach (var b in gathering.Bindings)
            {
                if (string.Equals(b.SocketId, winner.SocketId, StringComparison.OrdinalIgnoreCase)) continue;
                if (b.IsDisposed || b.IsDetached || !CandidatePairBuilder.IsSocketAlive(b.Socket)) continue;
                try
                {
                    var sock = b.DetachSocket();
                    var transferred = new GatheredSocketBinding
                    {
                        SocketId = b.SocketId,
                        Interface = b.Interface,
                        Socket = sock,
                        LocalEndPoint = b.LocalEndPoint,
                        NatObservation = b.NatObservation,
                        IsLoopbackFallback = b.IsLoopbackFallback,
                        OwnsSocket = true
                    };
                    transferred.Candidates.AddRange(b.Candidates);
                    transferred.StunProbes.AddRange(b.StunProbes);
                    nextStandbys.Add(transferred);
                }
                catch { }
            }
            _context.ReplaceStandbyBindings(nextStandbys);

            var newPostCtx = BuildPostPunchContext(winner, checkRes.FinalRole == IceRole.Controlling, checkRes.TieBreaker);
            uint newEpoch = await _stream.SwitchPathAsync(
                winnerSock,
                checkRes.SelectedRemoteEndPoint,
                newOwnsSocket: true,
                updatedPostPunchContext: newPostCtx,
                callerAlreadyPausedReceive: false,
                ct: ct,
                targetEpoch: oldEpoch + 1).ConfigureAwait(false);

            UpdateActiveContextFromPair(winner, winnerIface);
            _context.KnownLocalCandidates.Clear();
            _context.KnownLocalCandidates.AddRange(validLocals);
            _context.KnownRemoteCandidates.Clear();
            _context.KnownRemoteCandidates.AddRange(remoteCandidates);
            _context.KnownPairs.Clear();
            _context.KnownPairs.AddRange(checkRes.AllPairs);

            Log.Info($"[ICE/Reconnect] Step 3-5 succeeded (STUN regather & renegotiation): Attempt={_context.ConnectionAttemptId}, Session={_context.SessionId}, OldPair={oldPairId} -> NewPair={winner.PairId}, Remote={checkRes.SelectedRemoteEndPoint}, NewEpoch={newEpoch}, Elapsed={sw.ElapsedMilliseconds}ms");
            return CreateReport(true, oldEpoch, newEpoch, triggerReason, P2PReconnectionStep.RegatheredStunAndExchangedCandidates,
                oldPairId, winner.PairId, oldLocalEp, oldRemoteEp, sw.Elapsed, $"Renegotiated via STUN/Signaling onto {winner.LocalEndPoint} -> {winner.RemoteEndPoint}");
        }
        finally
        {
            gathering?.Dispose();
        }
    }

    private async Task<IPEndPoint?> ProbeActiveNatMappingAsync(CancellationToken ct)
    {
        if (_options.CustomActiveNatProbeAsync != null)
        {
            try { return await _options.CustomActiveNatProbeAsync(ct).ConfigureAwait(false); }
            catch { return null; }
        }

        var servers = (_options.StunServers.Count > 0 ? _options.StunServers : _context.StunServers)
            .Concat(_options.SupplementalStunServers)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (servers.Count == 0) return null;

        // 盡可能優先探測先前用來建立 ActiveLocalCandidate 的同一 STUN Server，以避免因 Symmetric NAT 對不同目標分配不同 Port 而誤判為 NAT 變更
        string? preferredServer = servers.FirstOrDefault(s =>
            string.Equals(s, _context.ActiveLocalCandidate.StunServer, StringComparison.OrdinalIgnoreCase)) ?? servers[0];

        if (!StunClient.TryParseHostPort(preferredServer, 19302, out string host, out int port))
            return null;

        IPAddress? ip;
        try
        {
            ip = await StunClient.ResolveIPv4Async(host, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        if (ip == null) return null;

        var serverEp = new IPEndPoint(ip, port);
        byte[] txnId = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        byte[] req = StunClient.CreateBindingRequest(txnId);
        var tcs = new TaskCompletionSource<IPEndPoint?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Func<IPEndPoint, ReadOnlyMemory<byte>, bool>? prevInterceptor = _stream.ExternalDatagramInterceptor;
        _stream.ExternalDatagramInterceptor = (senderEp, data) =>
        {
            var parsed = StunClient.ParseBindingResult(data.Span, txnId);
            if (parsed.FailureReason == StunQueryFailureReason.None && parsed.MappedEndPoint != null)
            {
                tcs.TrySetResult(parsed.MappedEndPoint);
                return true;
            }
            return prevInterceptor?.Invoke(senderEp, data) ?? false;
        };

        try
        {
            await _stream.ActiveSocket.SendToAsync(req, SocketFlags.None, serverEp, ct).ConfigureAwait(false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Math.Clamp(_options.RecoveryStunTimeoutMs, 250, 1500));
            using var reg = timeoutCts.Token.Register(() => tcs.TrySetResult(null));
            return await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        finally
        {
            _stream.ExternalDatagramInterceptor = prevInterceptor;
        }
    }

    private void OnPeerNominatedSwitchObserved(IPEndPoint newRemoteEp, IceCheckMessage nomMsg)
    {
        if (_disposed || _closedNormally) return;

        uint? parsedEpoch = ReliableUdpStream.TryExtractRecoveryTargetEpoch(nomMsg, out uint parsed) ? parsed : null;
        uint streamEpochNow = _stream.PathEpoch;
        bool alreadyAdopted = (_stream.RemoteEndPoint is IPEndPoint curEp && curEp.Address.Equals(newRemoteEp.Address) && curEp.Port == newRemoteEp.Port) &&
                              (!parsedEpoch.HasValue || streamEpochNow >= parsedEpoch.Value);
        uint? effectiveTargetEpoch = parsedEpoch ?? (alreadyAdopted ? streamEpochNow : null);
        uint reportedOldEpoch = alreadyAdopted ? Math.Max(1u, streamEpochNow - 1) : streamEpochNow;

        EnqueueFollowerSwitch(new PendingPeerSwitchRequest(
            newRemoteEp,
            nomMsg,
            StandbyBinding: null,
            TargetEpoch: effectiveTargetEpoch,
            ReportedOldEpoch: reportedOldEpoch));
    }

    private void EnqueueFollowerSwitch(PendingPeerSwitchRequest req)
    {
        Task? spawned = null;
        lock (_stateLock)
        {
            if (_disposed || _closedNormally || _cts.IsCancellationRequested)
                return;

            if (_pendingPeerSwitch == null)
            {
                _pendingPeerSwitch = req;
            }
            else
            {
                bool takeNew = req.StandbyBinding != null && _pendingPeerSwitch.StandbyBinding == null
                    || (req.TargetEpoch ?? 0) >= (_pendingPeerSwitch.TargetEpoch ?? 0)
                    || !req.RemoteEndPoint.Equals(_pendingPeerSwitch.RemoteEndPoint);
                if (takeNew)
                {
                    _pendingPeerSwitch = req;
                }
            }

            _followerTasks.RemoveAll(t => t.IsCompleted);
            spawned = Task.Run(DrainFollowerSwitchTaskAsync);
            _followerTasks.Add(spawned);
        }
    }

    private async Task DrainFollowerSwitchTaskAsync()
    {
        if (_disposed || _closedNormally || _cts.IsCancellationRequested) return;
        try
        {
            await _recoveryLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (ObjectDisposedException) { return; }

        try
        {
            await DrainPendingPeerSwitchWhileLockedAsync(_cts.Token).ConfigureAwait(false);
        }
        finally
        {
            try { _recoveryLock.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task DrainPendingPeerSwitchWhileLockedAsync(CancellationToken ct)
    {
        while (!_disposed && !_closedNormally && !_stream.IsDisposed && !ct.IsCancellationRequested)
        {
            PendingPeerSwitchRequest? req;
            lock (_stateLock)
            {
                req = _pendingPeerSwitch;
                _pendingPeerSwitch = null;
            }
            if (req == null) break;

            bool prevRecovering = _isRecovering;
            bool prevSuppress = _stream.SuppressDeadDisconnectDuringRecovery;
            try
            {
                string oldPairId = _context.ActivePair.PairId;
                string oldLocalEp = _stream.LocalEndPoint?.ToString() ?? "";
                string oldRemoteEp = _stream.RemoteEndPoint.ToString() ?? "";
                uint oldEpoch = req.ReportedOldEpoch > 0 ? req.ReportedOldEpoch : _stream.PathEpoch;
                var sw = System.Diagnostics.Stopwatch.StartNew();

                if (req.StandbyBinding != null)
                {
                    var standby = req.StandbyBinding;
                    if (standby.IsDisposed || standby.IsDetached) continue;

                    _isRecovering = true;
                    _stream.SuppressDeadDisconnectDuringRecovery = true;
                    PathRecoveryStarted?.Invoke(P2PPathFailureType.InterfaceDown);

                    await StopStandbyRespondersAsync().ConfigureAwait(false);
                    var removed = _context.DetachStandbyBindingBySocketId(standby.SocketId);
                    if (removed == null || removed.IsDisposed) continue;
                    Socket sock = removed.DetachSocket();
                    var standbyIface = removed.Interface;
                    var standbySockId = removed.SocketId;
                    removed.Dispose();

                    uint newEpoch = await _stream.SwitchPathAsync(
                        sock,
                        req.RemoteEndPoint,
                        newOwnsSocket: true,
                        updatedPostPunchContext: BuildFollowerContext(req.NomMsg),
                        callerAlreadyPausedReceive: false,
                        ct: ct,
                        targetEpoch: req.TargetEpoch).ConfigureAwait(false);

                    UpdateActiveContextFromFollower(req.RemoteEndPoint, req.NomMsg, standbyIface, standbySockId, _stream.LocalEndPoint as IPEndPoint);
                    FinalizeRecoveredStateLocked();
                    StartStandbyRespondersLocked();
                    Log.Info($"[ICE/Reconnect] Followed peer nomination onto standby interface {standbyIface.InterfaceName} (Attempt={_context.ConnectionAttemptId}, Session={_context.SessionId}): OldEpoch={oldEpoch} -> NewEpoch={newEpoch}");
                    PathRecoveryCompleted?.Invoke(CreateReport(
                        true, oldEpoch, newEpoch, P2PPathFailureType.None, P2PReconnectionStep.SwitchedToAlternateExistingPair,
                        oldPairId, req.NomMsg.PairId, oldLocalEp, oldRemoteEp, sw.Elapsed, $"Followed peer onto standby interface {standbyIface.InterfaceName}"));
                }
                else
                {
                    _isRecovering = true;
                    _stream.SuppressDeadDisconnectDuringRecovery = true;
                    PathRecoveryStarted?.Invoke(P2PPathFailureType.NatMappingChanged);

                    uint newEpoch = await _stream.SwitchPathAsync(
                        _stream.ActiveSocket,
                        req.RemoteEndPoint,
                        newOwnsSocket: true,
                        updatedPostPunchContext: BuildFollowerContext(req.NomMsg),
                        callerAlreadyPausedReceive: false,
                        ct: ct,
                        targetEpoch: req.TargetEpoch).ConfigureAwait(false);

                    UpdateActiveContextFromFollower(req.RemoteEndPoint, req.NomMsg, _context.ActiveInterface, _context.ActivePair.SocketId, _stream.LocalEndPoint as IPEndPoint);
                    FinalizeRecoveredStateLocked();
                    Log.Info($"[ICE/Reconnect] Followed peer-initiated path switch (Attempt={_context.ConnectionAttemptId}, Session={_context.SessionId}): OldEpoch={oldEpoch} -> NewEpoch={newEpoch}, NewRemote={req.RemoteEndPoint}");
                    PathRecoveryCompleted?.Invoke(CreateReport(
                        true, oldEpoch, newEpoch, P2PPathFailureType.None, P2PReconnectionStep.SwitchedToAlternateExistingPair,
                        oldPairId, req.NomMsg.PairId, oldLocalEp, oldRemoteEp, sw.Elapsed, $"Aligned to peer nominated endpoint {req.RemoteEndPoint}"));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"[ICE/Reconnect] Follow peer switch failed (Attempt={_context.ConnectionAttemptId}, Session={_context.SessionId}): {ex.Message}");
            }
            finally
            {
                _isRecovering = prevRecovering;
                _stream.SuppressDeadDisconnectDuringRecovery = prevSuppress;
            }
        }
    }

    private void StartStandbyRespondersLocked()
    {
        if (_standbyResponderCts != null || _context.StandbyBindings.Count == 0)
            return;

        _standbyResponderCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var token = _standbyResponderCts.Token;

        foreach (var binding in _context.StandbyBindings)
        {
            if (binding.IsDisposed || binding.IsDetached || !CandidatePairBuilder.IsSocketAlive(binding.Socket))
                continue;
            _standbyResponderTasks.Add(Task.Run(() => RunStandbyResponderAsync(binding, token), CancellationToken.None));
        }
    }

    private async Task StopStandbyRespondersAsync()
    {
        CancellationTokenSource? cts;
        Task[] tasks;
        lock (_stateLock)
        {
            cts = _standbyResponderCts;
            _standbyResponderCts = null;
            tasks = _standbyResponderTasks.ToArray();
            _standbyResponderTasks.Clear();
        }

        if (cts != null)
        {
            try { cts.Cancel(); } catch { }
            if (tasks.Length > 0)
            {
                using var waitCts = new CancellationTokenSource(400);
                try { await Task.WhenAll(tasks).WaitAsync(waitCts.Token).ConfigureAwait(false); } catch { }
            }
            try { cts.Dispose(); } catch { }
        }
    }

    private async Task RunStandbyResponderAsync(GatheredSocketBinding binding, CancellationToken ct)
    {
        byte[] buf = new byte[1024];
        EndPoint anyEp = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested && !binding.IsDisposed && !binding.IsDetached && !_closedNormally)
        {
            try
            {
                var recv = await binding.Socket.ReceiveFromAsync(buf, SocketFlags.None, anyEp, ct).ConfigureAwait(false);
                var span = buf.AsSpan(0, recv.ReceivedBytes);
                if (!IceCheckCodec.TryParse(span, out var msg)) continue;
                if (!string.Equals(msg.SessionId, _context.SessionId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(msg.SenderDeviceId, _context.RemoteDeviceId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(msg.ReceiverDeviceId, _context.SelfDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (msg.MessageType == IceCheckMessageType.CheckRequest || msg.MessageType == IceCheckMessageType.NominateRequest)
                {
                    var replyType = msg.MessageType == IceCheckMessageType.NominateRequest
                        ? IceCheckMessageType.NominateAck
                        : IceCheckMessageType.CheckResponse;
                    var reply = IceCheckCodec.Serialize(
                        replyType,
                        isControlling: _context.IsInitiator,
                        useCandidate: replyType == IceCheckMessageType.NominateAck,
                        roleConflictRejected: false,
                        attemptNumber: msg.AttemptNumber,
                        transactionId: msg.TransactionId,
                        tieBreaker: (ulong)(_context.IsInitiator ? 2 : 1),
                        priority: msg.Priority,
                        observedEndPoint: recv.RemoteEndPoint as IPEndPoint,
                        sessionId: _context.SessionId,
                        senderDeviceId: _context.SelfDeviceId,
                        receiverDeviceId: _context.RemoteDeviceId,
                        localCandidateId: msg.RemoteCandidateId,
                        remoteCandidateId: msg.LocalCandidateId,
                        pairId: msg.PairId);
                    await binding.Socket.SendToAsync(reply, SocketFlags.None, recv.RemoteEndPoint, ct).ConfigureAwait(false);

                    if (msg.MessageType == IceCheckMessageType.NominateRequest &&
                        recv.RemoteEndPoint is IPEndPoint remoteIp &&
                        !_isRecovering)
                    {
                        var normIp = remoteIp.Address.IsIPv4MappedToIPv6
                            ? new IPEndPoint(remoteIp.Address.MapToIPv4(), remoteIp.Port)
                            : remoteIp;
                        TriggerFollowOnStandbyBinding(binding, normIp, msg);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { try { await Task.Delay(30, ct).ConfigureAwait(false); } catch { break; } }
        }
    }

    private void TriggerFollowOnStandbyBinding(GatheredSocketBinding standby, IPEndPoint newRemoteEp, IceCheckMessage nomMsg)
    {
        uint? parsedEpoch = ReliableUdpStream.TryExtractRecoveryTargetEpoch(nomMsg, out uint parsed) ? parsed : null;
        EnqueueFollowerSwitch(new PendingPeerSwitchRequest(
            newRemoteEp,
            nomMsg,
            StandbyBinding: standby,
            TargetEpoch: parsedEpoch,
            ReportedOldEpoch: _stream.PathEpoch));
    }

    private Task<CandidateGatheringResult> RunGatherCoreAsync(
        int round, string sessionId, string attemptId, IceGatheringOptions opts, CancellationToken ct)
    {
        if (_options.CustomGatherAsync != null)
            return _options.CustomGatherAsync(round, sessionId, attemptId, opts, ct);
        return IceCandidateGatherer.GatherAsync(sessionId, attemptId, opts, ct);
    }

    private Task<ConnectivityCheckResult> RunCheckCoreAsync(
        int round,
        CandidateGatheringResult gathering,
        IReadOnlyList<IceCandidate> remoteCandidates,
        string attemptId,
        IceConnectivityOptions opts,
        CancellationToken ct)
    {
        if (_options.CustomCheckAsync != null)
        {
            return _options.CustomCheckAsync(
                round, gathering, remoteCandidates, attemptId,
                _context.SessionId, _context.SelfDeviceId, _context.RemoteDeviceId,
                _context.IsInitiator, opts, ct);
        }
        return IceConnectivityChecker.RunAsync(
            gathering, remoteCandidates, attemptId,
            _context.SessionId, _context.SelfDeviceId, _context.RemoteDeviceId,
            _context.IsInitiator, opts, ct);
    }

    private IceConnectivityOptions BuildCheckOptions(int timeoutMs, int maxAttempts, bool forceControllingRole = false) =>
        new()
        {
            CheckTimeoutMs = timeoutMs,
            PacingIntervalMs = _context.BaseConnectivityOptions?.PacingIntervalMs ?? 15,
            MaxAttemptsPerPair = maxAttempts,
            RetryIntervalsMs = new[] { 80, 160, 260, 400 },
            SelectionGraceWindowMs = 45,
            NominationTimeoutMs = Math.Min(1000, Math.Max(350, timeoutMs / 2)),
            MaxCandidateAgeSeconds = 600,
            AllowRestrictedPortPredictionFallback = false,
            ForceRole = forceControllingRole ? IceRole.Controlling : null,
            UtcNowProvider = _options.UtcNowProvider
        };

    private static CandidateGatheringResult BuildNonOwningGathering(IReadOnlyList<NonOwningBindingSpec> specs)
    {
        var bindings = new List<GatheredSocketBinding>(specs.Count);
        var cands = new List<IceCandidate>();
        var ifaces = new List<DiscoveredNetworkInterface>();

        foreach (var s in specs)
        {
            var b = new GatheredSocketBinding
            {
                SocketId = s.SocketId,
                Interface = s.Interface,
                Socket = s.Socket,
                LocalEndPoint = s.LocalEndPoint,
                OwnsSocket = false
            };
            b.Candidates.AddRange(s.Candidates);
            bindings.Add(b);
            cands.AddRange(s.Candidates);
            if (!ifaces.Any(x => string.Equals(x.InterfaceId, s.Interface.InterfaceId, StringComparison.OrdinalIgnoreCase)))
                ifaces.Add(s.Interface);
        }

        return new CandidateGatheringResult
        {
            ConnectionAttemptId = $"recheck-{Guid.NewGuid():N}"[..16],
            SessionId = "",
            Interfaces = ifaces,
            Bindings = bindings,
            Candidates = cands
        };
    }

    private static IceCandidate CloneCandidateForCheck(IceCandidate src, string newSocketId, string tag) =>
        new()
        {
            CandidateId = $"{src.CandidateId}#{tag}",
            Type = src.Type,
            AddressFamily = src.AddressFamily,
            Protocol = src.Protocol,
            Ip = src.Ip,
            Port = src.Port,
            LocalAddress = src.LocalAddress,
            LocalPort = src.LocalPort,
            PublicAddress = src.PublicAddress,
            PublicPort = src.PublicPort,
            InterfaceId = src.InterfaceId,
            SocketId = newSocketId,
            StunServer = src.StunServer,
            Priority = src.Priority,
            DiscoveryTimestamp = DateTime.UtcNow,
            ValidityStatus = CandidateValidityStatus.Valid,
            Ufrag = src.Ufrag
        };

    private IceStreamPostPunchContext BuildPostPunchContext(CandidatePair pair, bool? isControllingOverride = null, ulong? tieBreakerOverride = null) =>
        new()
        {
            SessionId = _context.SessionId,
            SelfDeviceId = _context.SelfDeviceId,
            RemoteDeviceId = _context.RemoteDeviceId,
            SelectedLocalCandidateId = pair.LocalCandidateId,
            SelectedRemoteCandidateId = pair.RemoteCandidateId,
            SelectedPairId = pair.PairId,
            IsControlling = isControllingOverride ?? _context.IsInitiator,
            TieBreaker = tieBreakerOverride is > 0 ? tieBreakerOverride.Value : (ulong)(_context.IsInitiator ? 2 : 1)
        };

    private IceStreamPostPunchContext BuildFollowerContext(IceCheckMessage nomMsg) =>
        new()
        {
            SessionId = _context.SessionId,
            SelfDeviceId = _context.SelfDeviceId,
            RemoteDeviceId = _context.RemoteDeviceId,
            SelectedLocalCandidateId = string.IsNullOrWhiteSpace(nomMsg.RemoteCandidateId) ? _context.ActiveLocalCandidate.CandidateId : nomMsg.RemoteCandidateId,
            SelectedRemoteCandidateId = string.IsNullOrWhiteSpace(nomMsg.LocalCandidateId) ? _context.ActiveRemoteCandidate.CandidateId : nomMsg.LocalCandidateId,
            SelectedPairId = string.IsNullOrWhiteSpace(nomMsg.PairId) ? _context.ActivePair.PairId : nomMsg.PairId,
            IsControlling = !nomMsg.IsControlling,
            TieBreaker = (ulong)(_context.IsInitiator ? 2 : 1)
        };

    private void UpdateActiveContextFromPair(CandidatePair pair, DiscoveredNetworkInterface iface)
    {
        _context.ActivePair = pair;
        _context.ActiveInterface = iface;
        _context.ActiveLocalCandidate = pair.LocalCandidate;
        _context.ActiveRemoteCandidate = pair.RemoteCandidate;
    }

    private void UpdateActiveContextFromFollower(
        IPEndPoint newRemoteEp,
        IceCheckMessage nomMsg,
        DiscoveredNetworkInterface newIface,
        string newSocketId,
        IPEndPoint? actualLocalEp)
    {
        var baseRemote = _context.KnownRemoteCandidates.FirstOrDefault(r =>
            r.EndPoint != null && r.EndPoint.Equals(newRemoteEp))
            ?? _context.ActiveRemoteCandidate;

        bool remoteEpChanged = baseRemote.EndPoint == null || !baseRemote.EndPoint.Equals(newRemoteEp);
        var updatedRemote = new IceCandidate
        {
            CandidateId = !string.IsNullOrWhiteSpace(nomMsg.LocalCandidateId) ? nomMsg.LocalCandidateId : baseRemote.CandidateId,
            Type = remoteEpChanged && baseRemote.Type == IceCandidateType.Host ? IceCandidateType.Srflx : baseRemote.Type,
            AddressFamily = baseRemote.AddressFamily,
            Protocol = baseRemote.Protocol,
            Ip = newRemoteEp.Address.ToString(),
            Port = newRemoteEp.Port,
            LocalAddress = remoteEpChanged ? "" : baseRemote.LocalAddress,
            LocalPort = remoteEpChanged ? 0 : baseRemote.LocalPort,
            PublicAddress = baseRemote.Type == IceCandidateType.Host && !remoteEpChanged ? "" : newRemoteEp.Address.ToString(),
            PublicPort = baseRemote.Type == IceCandidateType.Host && !remoteEpChanged ? 0 : newRemoteEp.Port,
            InterfaceId = baseRemote.InterfaceId,
            SocketId = baseRemote.SocketId,
            StunServer = baseRemote.StunServer,
            Priority = baseRemote.Priority,
            DiscoveryTimestamp = DateTime.UtcNow,
            ValidityStatus = CandidateValidityStatus.Valid,
            Ufrag = baseRemote.Ufrag
        };

        var baseLocal = _context.ActiveLocalCandidate;
        var updatedLocal = new IceCandidate
        {
            CandidateId = !string.IsNullOrWhiteSpace(nomMsg.RemoteCandidateId) ? nomMsg.RemoteCandidateId : baseLocal.CandidateId,
            Type = baseLocal.Type,
            AddressFamily = baseLocal.AddressFamily,
            Protocol = baseLocal.Protocol,
            Ip = actualLocalEp?.Address.ToString() ?? baseLocal.Ip,
            Port = actualLocalEp?.Port ?? baseLocal.Port,
            LocalAddress = newIface.LocalIPv4.ToString(),
            LocalPort = actualLocalEp?.Port ?? baseLocal.LocalPort,
            PublicAddress = baseLocal.PublicAddress,
            PublicPort = baseLocal.PublicPort,
            InterfaceId = newIface.InterfaceId,
            SocketId = newSocketId,
            StunServer = baseLocal.StunServer,
            Priority = baseLocal.Priority,
            DiscoveryTimestamp = DateTime.UtcNow,
            ValidityStatus = CandidateValidityStatus.Valid,
            Ufrag = baseLocal.Ufrag
        };

        var localEp = actualLocalEp ?? _context.ActivePair.LocalEndPoint;
        var updatedPair = new CandidatePair
        {
            PairId = !string.IsNullOrWhiteSpace(nomMsg.PairId) ? nomMsg.PairId : _context.ActivePair.PairId,
            InterfaceId = newIface.InterfaceId,
            SocketId = newSocketId,
            LocalCandidateId = updatedLocal.CandidateId,
            RemoteCandidateId = updatedRemote.CandidateId,
            LocalCandidate = updatedLocal,
            RemoteCandidate = updatedRemote,
            LocalEndPoint = localEp,
            RemoteEndPoint = newRemoteEp,
            PairPriority = _context.ActivePair.PairPriority,
            IsPortPredictionFallback = false,
            CheckState = CandidatePairCheckState.Succeeded,
            IsNominated = true,
            IsSelected = true
        };

        _context.ActiveInterface = newIface;
        _context.ActiveLocalCandidate = updatedLocal;
        _context.ActiveRemoteCandidate = updatedRemote;
        _context.ActivePair = updatedPair;
    }

    private void FinalizeRecoveredStateLocked()
    {
        DateTime now = _utcNow();
        lock (_stateLock)
        {
            _healthState = P2PPathHealthState.Healthy;
            _detectedCondition = P2PPathFailureType.None;
            _consecutiveHeartbeatTimeouts = 0;
            _lastHeartbeatTimestampUtc = now;
            _lastHeartbeatProbeAttemptUtc = now;
            _lastPathSwitchUtc = now;
            _healthySinceUtc = now;
            _lastPacketsSentSnap = _stream.TotalPacketsSent;
            _lastUniqueLostSnap = _stream.UniquePacketsLost;
            _lastRetransmitsSnap = _stream.TotalRetransmissions;
            _windowPacketLossRate = 0;
            _windowRetransmissionRate = 0;
        }
    }

    private void MarkClosedNormally(P2PPathFailureType reason, string detail)
    {
        bool fire = false;
        P2PPathMetrics metrics;
        lock (_stateLock)
        {
            if (!_closedNormally)
            {
                _closedNormally = true;
                _healthState = P2PPathHealthState.ClosedNormally;
                _detectedCondition = reason;
                fire = true;
            }
            metrics = BuildMetricsSnapshotLocked(_utcNow());
        }

        if (fire)
        {
            Log.Info($"[ICE/PathMonitor] Path closed cleanly (Attempt={_context.ConnectionAttemptId}, Session={_context.SessionId}, {reason}): {detail}");
            MetricsUpdated?.Invoke(metrics);
            PathClosedNormally?.Invoke(reason, detail);
            try { _cts.Cancel(); } catch { }
        }
    }

    private void HandlePermanentRecoveryFailure(P2PPathFailureType reason, string detail)
    {
        if (_permanentFailureRaised) return;
        _permanentFailureRaised = true;
        SetConditionLocked(P2PPathHealthState.Failed, reason);
        Log.Error($"[ICE/PathMonitor] Path permanently failed (Attempt={_context.ConnectionAttemptId}, Session={_context.SessionId}, {reason}): {detail}");
        PathPermanentlyFailed?.Invoke(reason, detail);
    }

    private void SetConditionLocked(P2PPathHealthState health, P2PPathFailureType condition)
    {
        lock (_stateLock)
        {
            _healthState = health;
            _detectedCondition = condition;
        }
    }

    private void UpdateWindowRatesLocked()
    {
        long nowSent = _stream.TotalPacketsSent;
        long nowLost = _stream.UniquePacketsLost;
        long nowRtx = _stream.TotalRetransmissions;

        long dSent = Math.Max(0, nowSent - _lastPacketsSentSnap);
        long dLost = Math.Max(0, nowLost - _lastUniqueLostSnap);
        long dRtx = Math.Max(0, nowRtx - _lastRetransmitsSnap);

        if (dSent >= 10)
        {
            _windowPacketLossRate = Math.Clamp(dLost / (double)dSent, 0.0, 1.0);
            _windowRetransmissionRate = Math.Clamp(dRtx / (double)dSent, 0.0, 1.0);
            _lastPacketsSentSnap = nowSent;
            _lastUniqueLostSnap = nowLost;
            _lastRetransmitsSnap = nowRtx;
        }
        else if (dSent > 0 && dLost > 0)
        {
            _windowPacketLossRate = Math.Clamp(dLost / (double)Math.Max(dSent, 1), 0.0, 1.0);
            _windowRetransmissionRate = Math.Clamp(dRtx / (double)Math.Max(dSent, 1), 0.0, 1.0);
        }
        else
        {
            _windowPacketLossRate *= 0.6;
            _windowRetransmissionRate *= 0.6;
        }
    }

    private double ResolveCurrentRttMsLocked()
    {
        double hbRtt = _stream.LastMeasuredHeartbeatRttMs;
        DateTime hbAck = _stream.LastHeartbeatAckTimestampUtc;
        if (hbRtt > 0 && (_utcNow() - hbAck).TotalMilliseconds <= _options.HeartbeatIntervalMs * 2.5)
            return hbRtt;
        return _stream.CurrentSrttMs;
    }

    private P2PPathRecoveryReport CreateReport(
        bool succeeded,
        uint oldEpoch,
        uint newEpoch,
        P2PPathFailureType triggerReason,
        P2PReconnectionStep stepTaken,
        string oldPairId,
        string newPairId,
        string oldLocalEndpoint,
        string oldRemoteEndpoint,
        TimeSpan elapsed,
        string detail)
    {
        string newLocalEndpoint = _stream.LocalEndPoint?.ToString() ?? oldLocalEndpoint;
        string newRemoteEndpoint = _stream.RemoteEndPoint.ToString() ?? oldRemoteEndpoint;
        return new P2PPathRecoveryReport(
            succeeded,
            oldEpoch,
            newEpoch,
            triggerReason,
            stepTaken,
            oldPairId,
            newPairId,
            oldLocalEndpoint,
            newLocalEndpoint,
            oldRemoteEndpoint,
            newRemoteEndpoint,
            elapsed,
            detail);
    }

    private P2PPathMetrics BuildMetricsSnapshotLocked(DateTime nowUtc) =>
        new(
            LastHeartbeatTimestamp: _lastHeartbeatTimestampUtc,
            HeartbeatTimeoutCount: _heartbeatTimeoutCount,
            ConsecutiveHeartbeatTimeouts: Volatile.Read(ref _consecutiveHeartbeatTimeouts),
            CurrentRttMs: ResolveCurrentRttMsLocked(),
            PacketLossRate: _windowPacketLossRate,
            RetransmissionRate: _windowRetransmissionRate,
            CurrentLocalInterface: _context.ActiveInterface.InterfaceName,
            CurrentLocalEndpoint: _stream.LocalEndPoint?.ToString() ?? _context.ActiveLocalCandidate.LocalAddress,
            CurrentRemoteEndpoint: _stream.RemoteEndPoint.ToString() ?? "",
            LastSuccessfulReceiveTimestamp: _stream.LastSuccessfulReceiveTimestampUtc,
            LastSuccessfulSendTimestamp: _stream.LastSuccessfulSendTimestampUtc,
            PathEpoch: _stream.PathEpoch,
            SelectedPairId: _context.ActivePair.PairId,
            HealthState: _healthState,
            DetectedCondition: _detectedCondition,
            ConsecutiveSocketErrors: _stream.ConsecutiveSocketErrors,
            RecoveryAttemptCount: Volatile.Read(ref _recoveryAttemptCount),
            RejectedStalePathPackets: _stream.RejectedStalePathPackets,
            CapturedAtUtc: nowUtc);

    private bool CheckLocalInterfaceAlive(DiscoveredNetworkInterface iface)
    {
        if (_options.CustomInterfaceAliveCheck != null)
            return _options.CustomInterfaceAliveCheck(iface.InterfaceId);

        if (IPAddress.IsLoopback(iface.LocalIPv4))
            return true;

        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces();
            if (nics.Length == 0) return true;

            var matchedById = nics.FirstOrDefault(n => string.Equals(n.Id, iface.InterfaceId, StringComparison.OrdinalIgnoreCase));
            if (matchedById != null)
            {
                if (matchedById.OperationalStatus != OperationalStatus.Up)
                    return false;
                var props = matchedById.GetIPProperties();
                return props.UnicastAddresses.Any(u =>
                    u.Address.AddressFamily == AddressFamily.InterNetwork &&
                    u.Address.Equals(iface.LocalIPv4));
            }

            if (IPAddress.Any.Equals(iface.LocalIPv4) || IPAddress.None.Equals(iface.LocalIPv4))
                return true;

            return nics.Any(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.GetIPProperties().UnicastAddresses.Any(u =>
                    u.Address.AddressFamily == AddressFamily.InterNetwork &&
                    u.Address.Equals(iface.LocalIPv4)));
        }
        catch
        {
            return true;
        }
    }

    private static P2PPathFailureType ParseFailureReasonOrDefault(string text, P2PPathFailureType fallback) =>
        Enum.TryParse<P2PPathFailureType>(text, true, out var parsed) ? parsed : fallback;

    private sealed record NonOwningBindingSpec(
        string SocketId,
        DiscoveredNetworkInterface Interface,
        Socket Socket,
        IPEndPoint LocalEndPoint,
        params IceCandidate[] Candidates)
    {
        public NonOwningBindingSpec(
            string socketId,
            DiscoveredNetworkInterface iface,
            Socket socket,
            IPEndPoint localEndPoint,
            IReadOnlyList<IceCandidate> candidates)
            : this(socketId, iface, socket, localEndPoint, candidates.ToArray())
        {
        }
    }

    private sealed record PendingPeerSwitchRequest(
        IPEndPoint RemoteEndPoint,
        IceCheckMessage NomMsg,
        GatheredSocketBinding? StandbyBinding,
        uint? TargetEpoch,
        uint ReportedOldEpoch);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _disposed = true;
        _stream.PeerNominatedSwitchObserved -= OnPeerNominatedSwitchObserved;
        try { _cts.Cancel(); } catch { }
        try { _standbyResponderCts?.Cancel(); } catch { }

        var tasksToWait = SnapshotBackgroundTasksForDispose();
        if (tasksToWait.Count > 0)
        {
            try { Task.WhenAll(tasksToWait).Wait(TimeSpan.FromMilliseconds(400)); } catch { }
        }

        DisposeResourcesCore();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _disposed = true;
        _stream.PeerNominatedSwitchObserved -= OnPeerNominatedSwitchObserved;
        try { _cts.Cancel(); } catch { }
        try { _standbyResponderCts?.Cancel(); } catch { }

        var tasksToWait = SnapshotBackgroundTasksForDispose();
        if (tasksToWait.Count > 0)
        {
            try { await Task.WhenAll(tasksToWait).WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false); } catch { }
        }

        DisposeResourcesCore();
    }

    private List<Task> SnapshotBackgroundTasksForDispose()
    {
        var list = new List<Task>();
        lock (_stateLock)
        {
            _pendingPeerSwitch = null;
            if (_monitorTask != null) list.Add(_monitorTask);
            list.AddRange(_standbyResponderTasks);
            list.AddRange(_followerTasks);
            _standbyResponderTasks.Clear();
            _followerTasks.Clear();
        }
        return list;
    }

    private void DisposeResourcesCore()
    {
        try { _standbyResponderCts?.Dispose(); } catch { }
        try { _recoveryLock.Dispose(); } catch { }
        try { _context.Dispose(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
