using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RemoteDesk.Common;

namespace RemoteDesk.Net;

internal enum SignalProcessDisposition
{
    Applied,
    DuplicateSkipped,
    RejectedSessionMismatch,
    RejectedReceiverMismatch,
    RejectedSenderMismatch,
    RejectedExpired,
    RejectedInvalidPayload
}

/// <summary>
/// 單一 Signaling Session 的收件箱：負責驗證 Session 隔離、Receiver/Sender 身分、有效期檢查、
/// 依 MessageId 去重，以及在處理成功後收集待回報之 MessageId。
/// </summary>
internal sealed class SignalingSessionInbox
{
    private readonly string _sessionId;
    private readonly string _localDeviceId;
    private readonly string _remoteDeviceId;
    private readonly Func<DateTime> _utcNow;
    private readonly HashSet<string> _processedMessageIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingAckIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(IceCandidate Candidate, int Round)> _roundCandidates = new();
    private readonly HashSet<int> _doneRounds = new();

    public string SessionId => _sessionId;
    public int AppliedCount { get; private set; }
    public int DuplicateCount { get; private set; }
    public int RejectedCount { get; private set; }
    public int PendingAckCount => _pendingAckIds.Count;
    public bool RemoteDone { get; private set; }
    public int HighestRemoteRound { get; private set; }
    public int HighestRecoveryEpochRequested { get; private set; }
    public int HighestRecoveryRetryRound { get; private set; }
    public string LatestRecoveryReason { get; private set; } = "";
    public List<IceCandidate> RemoteCandidates { get; } = new();

    public SignalingSessionInbox(
        string sessionId,
        string localDeviceId,
        string remoteDeviceId,
        Func<DateTime>? utcNow = null)
    {
        _sessionId = sessionId;
        _localDeviceId = localDeviceId;
        _remoteDeviceId = remoteDeviceId;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool IsRoundDone(int minRound) => _doneRounds.Any(r => r >= minRound);

    public IReadOnlyList<IceCandidate> GetEffectiveRemoteCandidates(int targetRound, bool allowFallbackToOlderRounds = true)
    {
        var newerOrCurrent = _roundCandidates.Where(x => x.Round >= targetRound).ToList();
        bool hasCompleteNewRound = IsRoundDone(targetRound) && newerOrCurrent.Count > 0;

        IEnumerable<(IceCandidate Candidate, int Round)> source = hasCompleteNewRound
            ? newerOrCurrent
            : (allowFallbackToOlderRounds
                ? _roundCandidates.OrderByDescending(x => x.Round)
                : newerOrCurrent);

        var result = new List<IceCandidate>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (cand, _) in source)
        {
            string epKey = $"{(int)cand.Type}:{cand.Ip}:{cand.Port}";
            bool idKnown = !string.IsNullOrWhiteSpace(cand.CandidateId) && !seenIds.Add(cand.CandidateId);
            if (idKnown || !seenEndpoints.Add(epKey))
                continue;
            result.Add(cand);
        }

        return result;
    }

    public SignalProcessDisposition TryProcessSignal(SignalingMessageInfo? msg, out IceSignal? parsed)
    {
        parsed = null;
        if (msg == null)
        {
            RejectedCount++;
            return SignalProcessDisposition.RejectedInvalidPayload;
        }

        if (!string.Equals(msg.SessionId, _sessionId, StringComparison.OrdinalIgnoreCase))
        {
            RejectedCount++;
            return SignalProcessDisposition.RejectedSessionMismatch;
        }

        string receiverId = msg.EffectiveReceiverId;
        if (string.IsNullOrWhiteSpace(receiverId) ||
            !string.Equals(receiverId, _localDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            RejectedCount++;
            return SignalProcessDisposition.RejectedReceiverMismatch;
        }

        string senderId = msg.EffectiveSenderId;
        if (string.IsNullOrWhiteSpace(senderId) ||
            !string.Equals(senderId, _remoteDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            RejectedCount++;
            return SignalProcessDisposition.RejectedSenderMismatch;
        }

        if (msg.IsExpired(_utcNow()))
        {
            RejectedCount++;
            return SignalProcessDisposition.RejectedExpired;
        }

        var sig = IceSignal.FromJson(msg.Data);
        if (sig == null)
        {
            RejectedCount++;
            return SignalProcessDisposition.RejectedInvalidPayload;
        }

        if (!string.IsNullOrEmpty(sig.SessionId) &&
            !string.Equals(sig.SessionId, _sessionId, StringComparison.OrdinalIgnoreCase))
        {
            RejectedCount++;
            return SignalProcessDisposition.RejectedSessionMismatch;
        }

        if (!string.IsNullOrEmpty(sig.ReceiverDeviceId) &&
            !string.Equals(sig.ReceiverDeviceId, _localDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            RejectedCount++;
            return SignalProcessDisposition.RejectedReceiverMismatch;
        }

        if (!string.IsNullOrEmpty(sig.SenderDeviceId) &&
            !string.Equals(sig.SenderDeviceId, _remoteDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            RejectedCount++;
            return SignalProcessDisposition.RejectedSenderMismatch;
        }

        parsed = sig;

        string dedupKey = BuildDedupKey(msg, sig);
        if (_processedMessageIds.Contains(dedupKey))
        {
            DuplicateCount++;
            return SignalProcessDisposition.DuplicateSkipped;
        }

        if (sig.Type == "candidate" && sig.Candidate != null)
        {
            int sigRound = sig.RetryRound > 0 ? sig.RetryRound : 1;
            HighestRemoteRound = Math.Max(HighestRemoteRound, sigRound);
            var cand = sig.Candidate;
            if (cand.ValidityStatus == CandidateValidityStatus.Valid &&
                cand.AddressFamily == AddressFamily.InterNetwork &&
                cand.EndPoint != null)
            {
                _roundCandidates.Add((cand, sigRound));
                bool exists = RemoteCandidates.Any(c =>
                    (!string.IsNullOrWhiteSpace(c.CandidateId) && !string.IsNullOrWhiteSpace(cand.CandidateId) &&
                     string.Equals(c.CandidateId, cand.CandidateId, StringComparison.OrdinalIgnoreCase)) ||
                    (c.Type == cand.Type && c.Ip == cand.Ip && c.Port == cand.Port));
                if (!exists)
                {
                    RemoteCandidates.Add(cand);
                }
            }
        }
        else if (sig.Type == "candidates-done")
        {
            int doneRound = sig.RetryRound > 0 ? sig.RetryRound : 1;
            HighestRemoteRound = Math.Max(HighestRemoteRound, doneRound);
            _doneRounds.Add(doneRound);
            RemoteDone = true;
        }
        else if (sig.Type == "path-recheck")
        {
            int reqEpoch = sig.RecoveryEpoch > 0 ? sig.RecoveryEpoch : 1;
            if (sig.RetryRound > HighestRecoveryRetryRound)
            {
                HighestRecoveryRetryRound = sig.RetryRound;
            }
            if (reqEpoch >= HighestRecoveryEpochRequested)
            {
                HighestRecoveryEpochRequested = reqEpoch;
                LatestRecoveryReason = sig.RetryReason ?? "";
            }
        }

        _processedMessageIds.Add(dedupKey);

        AppliedCount++;
        return SignalProcessDisposition.Applied;
    }

    private static string BuildDedupKey(SignalingMessageInfo msg, IceSignal sig) =>
        !string.IsNullOrWhiteSpace(msg.MessageId)
            ? $"id:{msg.MessageId}"
            : $"fb:{msg.SessionId}|{msg.EffectiveSenderId}|{msg.SequenceNumber}|{sig.Type}|{sig.RetryRound}|{sig.RecoveryEpoch}|{sig.Candidate?.CandidateId ?? ""}";

    /// <summary>
    /// 讀取未 ACK 訊號 -> 驗證隔離與效期 -> 去重並應用內容 -> 成功後傳送 Message ACK。
    /// </summary>
    public async Task<int> PollOnceAndAckAsync(SignalingClient signalingClient, CancellationToken ct = default)
    {
        var detailed = await PollOnceAndAckDetailedAsync(new SignalingClientTransport(signalingClient), ct).ConfigureAwait(false);
        return detailed.MessageCount;
    }

    internal async Task<(int MessageCount, SignalingCallStatus Status, string ErrorDetail)> PollOnceAndAckDetailedAsync(
        IP2PSignalingTransport transport,
        CancellationToken ct = default)
    {
        var pollRes = await transport.GetSignalsAsync(_sessionId, ct).ConfigureAwait(false);
        if (pollRes.Status != SignalingCallStatus.Ok)
        {
            return (0, pollRes.Status, pollRes.ErrorDetail);
        }

        var signals = pollRes.Messages;
        foreach (var s in signals)
        {
            var disp = TryProcessSignal(s, out var sig);
            if (disp == SignalProcessDisposition.Applied)
            {
                if (!string.IsNullOrWhiteSpace(s.MessageId))
                {
                    _pendingAckIds.Add(s.MessageId);
                }

                if (sig?.Type == "candidate" && sig.Candidate != null)
                {
                    Log.Info($"[ICE] 收到對方候選端點: {sig.Candidate} (Session={_sessionId}, Round={sig.RetryRound}, MsgId={s.MessageId}, Seq={s.SequenceNumber})");
                }
            }
            else if (disp == SignalProcessDisposition.DuplicateSkipped)
            {
                // 已處理過但伺服器可能重送（先前 ACK 遺失或延遲），仍補送 ACK 讓伺服器標記完成但不重複執行動作
                if (!string.IsNullOrWhiteSpace(s.MessageId))
                {
                    _pendingAckIds.Add(s.MessageId);
                }
            }
            else
            {
                Log.Warn($"[ICE] 略過非法或跨階段訊號 ({disp}): ExpectedSession={_sessionId}, MsgSession={s.SessionId}, Receiver={s.EffectiveReceiverId}, Sender={s.EffectiveSenderId}, MsgId={s.MessageId}");
            }
        }

        ct.ThrowIfCancellationRequested();

        if (_pendingAckIds.Count > 0)
        {
            var ackRes = await FlushPendingAcksDetailedAsync(transport, ct).ConfigureAwait(false);
            if (ackRes.Status == SignalingCallStatus.SessionExpired)
            {
                return (signals.Count, SignalingCallStatus.SessionExpired, ackRes.ErrorDetail);
            }
            if (!ackRes.Succeeded)
            {
                return (signals.Count, ackRes.Status, ackRes.ErrorDetail);
            }
        }

        return (signals.Count, SignalingCallStatus.Ok, "");
    }

    /// <summary>
    /// 補送尚未成功獲得伺服器確認的已處理訊息 ACK。
    /// </summary>
    public async Task<bool> FlushPendingAcksAsync(SignalingClient signalingClient, CancellationToken ct = default)
    {
        var res = await FlushPendingAcksDetailedAsync(new SignalingClientTransport(signalingClient), ct).ConfigureAwait(false);
        return res.Succeeded;
    }

    internal async Task<SignalingAckResult> FlushPendingAcksDetailedAsync(IP2PSignalingTransport transport, CancellationToken ct = default)
    {
        if (_pendingAckIds.Count == 0) return new SignalingAckResult(true, SignalingCallStatus.Ok);

        var batch = _pendingAckIds.ToList();
        var ackRes = await transport.AckSignalsAsync(_sessionId, batch, ct).ConfigureAwait(false);
        if (ackRes.Succeeded)
        {
            foreach (var id in batch)
            {
                _pendingAckIds.Remove(id);
            }
            return ackRes;
        }

        Log.Warn($"[ICE] 訊號 ACK 回報失敗，等待重送/重試 (Session={_sessionId}, Count={batch.Count}, Status={ackRes.Status})");
        return ackRes;
    }
}

/// <summary>
/// 負責收集本機 ICE Candidates (Host + STUN Srflx)、透過 Signaling Server 進行候選端點交換，
/// 建立排序後的 Candidate Pair、執行雙向連通性檢查與角色仲裁選定，並於失敗時透過 Phase 4 協調策略性自動重試。
/// </summary>
internal static class UdpHolePuncher
{
    /// <summary>
    /// 執行完整多網卡/多 STUN ICE 收集、交換、Candidate Pair 建立、雙向 Connectivity Check 與 Phase 4 策略重試。
    /// 成功則傳回已就緒之 ReliableUdpStream，失敗傳回 null。
    /// </summary>
    public static async Task<ReliableUdpStream?> PunchHoleAsync(
        SignalingClient signalingClient,
        string sessionId,
        string selfDeviceId,
        string remoteDeviceId,
        bool isInitiator,
        int timeoutMs = 10000,
        CancellationToken ct = default,
        IceGatheringOptions? gatheringOptions = null,
        IceConnectivityOptions? connectivityOptions = null)
    {
        var result = await PunchHoleWithRetryAsync(
            signalingClient,
            sessionId,
            selfDeviceId,
            remoteDeviceId,
            isInitiator,
            BuildRetryOptionsFromTimeout(timeoutMs, gatheringOptions, connectivityOptions),
            ct).ConfigureAwait(false);

        return result.Stream;
    }

    public static Task<P2PConnectionResult> PunchHoleWithRetryAsync(
        SignalingClient signalingClient,
        string sessionId,
        string selfDeviceId,
        string remoteDeviceId,
        bool isInitiator,
        P2PRetryOptions? retryOptions = null,
        CancellationToken ct = default)
    {
        return P2PRetryCoordinator.ExecuteAsync(
            signalingClient,
            sessionId,
            selfDeviceId,
            remoteDeviceId,
            isInitiator,
            retryOptions,
            ct);
    }

    internal static P2PRetryOptions BuildRetryOptionsFromTimeout(
        int timeoutMs,
        IceGatheringOptions? gatheringOptions = null,
        IceConnectivityOptions? connectivityOptions = null,
        bool retainStandbyBindingsForMonitoring = false)
    {
        int effectiveTotal = Math.Max(800, timeoutMs);
        int stunTimeout = gatheringOptions?.StunTimeoutMs ?? Math.Min(1500, Math.Max(450, effectiveTotal / 5));

        return new P2PRetryOptions
        {
            TotalTimeoutMs = effectiveTotal,
            MaxRetryRounds = 3,
            StunTimeoutMs = stunTimeout,
            Round2StunTimeoutMs = Math.Max(stunTimeout, Math.Min(2400, effectiveTotal / 4)),
            Round1ExchangeTimeoutMs = Math.Clamp((int)(effectiveTotal * 0.35), 900, 5000),
            Round1CheckTimeoutMs = Math.Clamp((int)(effectiveTotal * 0.22), 700, 3400),
            Round2ExchangeTimeoutMs = Math.Clamp((int)(effectiveTotal * 0.25), 800, 3600),
            Round2CheckTimeoutMs = Math.Clamp((int)(effectiveTotal * 0.30), 900, 4500),
            Round3ExchangeTimeoutMs = Math.Clamp((int)(effectiveTotal * 0.22), 700, 3400),
            Round3CheckTimeoutMs = Math.Clamp((int)(effectiveTotal * 0.35), 1000, 5500),
            GatheringOptions = gatheringOptions ?? new IceGatheringOptions
            {
                StunTimeoutMs = stunTimeout,
                IncludeLoopbackFallback = true
            },
            ConnectivityOptions = connectivityOptions,
            RetainStandbyBindingsForMonitoring = retainStandbyBindingsForMonitoring
        };
    }
}
