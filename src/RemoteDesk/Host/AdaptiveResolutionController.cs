using RemoteDesk.Common;

namespace RemoteDesk.Host;

/// <summary>單次遙測取樣輸入（用於評估當前網路與發送管線壅塞狀態）。</summary>
public readonly record struct AdaptiveSample(
    long NowMs,
    float SendQueueWaitMs,
    int SendQueuePending,
    float UdpRawLossRatePercent,
    float RetransmissionRatePercent,
    float ArqFinalSuccessRatePercent,
    float InflightRatio,
    int RtoTimeoutsDelta,
    int FastRetransmitsDelta,
    int TargetFps,
    float ActualFps,
    long FrameDropsDelta,
    float EndToEndDelayMs,
    bool IsStreamActive,
    float SendQueueWaitAvgMs = 0f,
    float SendQueueWaitLastMs = 0f,
    float SourceFps = 0f,
    float CaptureFps = 0f,
    float ViewerRenderFps = 0f,
    bool IsTransportConstrained = false);

/// <summary>自適應解析度狀態機切換結果。</summary>
public readonly record struct AdaptiveDecision(
    bool Changed,
    ResolutionLevel PreviousLevel,
    ResolutionLevel NewLevel,
    ResolutionProfile Profile,
    string Reason,
    bool CongestionWarning);

/// <summary>
/// 自適應解析度控制器（Adaptive Resolution Controller）。
/// 原則：
/// 1. 預設維持 Level 0 原生解析度，與 JPEG Quality 完全解耦；
/// 2. 多指標共識評估壅塞，允許輕微可恢復 UDP 掉包 (<=2% 且 ARQ 成功)；
/// 3. 上升與下降採用遲滯門檻與不對稱觀察視窗，避免來回震盪 (Ping-Pong)；
/// 4. 支援步驟式升降 (0 <-> 1 <-> 2) 與每次切換至少 5 秒冷卻保護。
/// </summary>
public sealed class AdaptiveResolutionController
{
    private readonly ResolutionProfile[] _profiles;
    private ResolutionMode _mode;
    private ResolutionLevel _currentLevel = ResolutionLevel.Level0;

    // 時序與遲滯控制
    public const int DefaultCooldownMs = 5000;
    public const int EmergencyCooldownMinMs = 2500;
    public const int BaseRecoveryWindowMs = 6000;
    public const int MaxRecoveryWindowMs = 18000;

    private long _lastSwitchAtMs = -DefaultCooldownMs;
    private long _lastUpgradeAtMs = long.MinValue;
    private long _congestionStartMs = -1;
    private long _stableStartMs = -1;
    private long _lastEvalAtMs = -1;
    private int _unhealthyMildStreak = 0;
    private int _recoveryRequiredMs = BaseRecoveryWindowMs;

    private string _lastReason = "初始化原生解析度 (Level 0)";
    private bool _congestionWarning;
    private long _lastWarningLogMs = -60_000;

    public AdaptiveResolutionController(int originalWidth, int originalHeight, ResolutionMode initialMode = ResolutionMode.Auto)
    {
        _profiles = ResolutionProfileManager.BuildProfiles(originalWidth, originalHeight);
        _mode = initialMode;
        _currentLevel = ResolutionLevel.Level0;
    }

    public ResolutionMode Mode => _mode;
    public ResolutionLevel CurrentLevel => _currentLevel;
    public ResolutionProfile CurrentProfile => _profiles[(int)_currentLevel];
    public IReadOnlyList<ResolutionProfile> Profiles => _profiles;
    public string LastReason => _lastReason;
    public bool CongestionWarning => _congestionWarning;
    public int CurrentRecoveryWindowMs => _recoveryRequiredMs;

    public int DowngradeWindowMs => _mode == ResolutionMode.Performance ? 1500 : 3000;

    /// <summary>
    /// 切換解析度控制模式（Auto / Original / Performance）。
    /// 若切換至 Original 且目前處於降採樣層級，將立即觸發升回 Level 0。
    /// </summary>
    public AdaptiveDecision SetMode(ResolutionMode mode, long nowMs)
    {
        var prevLevel = _currentLevel;
        _mode = mode;
        _congestionStartMs = -1;
        _stableStartMs = -1;
        _lastEvalAtMs = nowMs;
        _unhealthyMildStreak = 0;

        if (mode == ResolutionMode.Original && _currentLevel != ResolutionLevel.Level0)
        {
            _currentLevel = ResolutionLevel.Level0;
            _lastSwitchAtMs = nowMs;
            _congestionWarning = false;
            var prevProf = _profiles[(int)prevLevel];
            var nextProf = _profiles[0];
            _lastReason = "User switched mode to Original";
            LogTransition(prevProf, nextProf, _lastReason);
            return new AdaptiveDecision(true, prevLevel, _currentLevel, nextProf, _lastReason, false);
        }

        if (mode != ResolutionMode.Original)
        {
            _congestionWarning = false;
        }

        return new AdaptiveDecision(false, prevLevel, _currentLevel, CurrentProfile, _lastReason, _congestionWarning);
    }

    /// <summary>
    /// 評估最新遙測取樣並決定是否進行漸進式解析度調整。
    /// </summary>
    public AdaptiveDecision Evaluate(in AdaptiveSample s)
    {
        long evalDelta = _lastEvalAtMs >= 0 ? Math.Clamp(s.NowMs - _lastEvalAtMs, 0, 5000) : 500;
        _lastEvalAtMs = s.NowMs;

        // P1-02: 必須始終執行完整壅塞信號評估；isBenignUdpLoss 僅能抑制 sigWindow 中的 UDP 封包遺失/重傳分支，
        // 絕不跳過 Queue / Inflight / FPS / Latency / hardStall 或 Performance 模式門檻，亦不讓 TCP 模式繞過評估。
        EvaluateCongestionSignals(in s, out bool isCongested, out bool isSevere, out string reason);

        bool isHealthy = IsHealthyForRecovery(in s);

        // Original 模式：永不自動降級，僅在偵測持續壅塞時觸發警告訊息
        if (_mode == ResolutionMode.Original)
        {
            if (_currentLevel != ResolutionLevel.Level0)
            {
                var prev = _currentLevel;
                var prevProf = _profiles[(int)prev];
                _currentLevel = ResolutionLevel.Level0;
                _lastSwitchAtMs = s.NowMs;
                _lastReason = "Locked to Original mode";
                LogTransition(prevProf, _profiles[0], _lastReason);
                return new AdaptiveDecision(true, prev, _currentLevel, _profiles[0], _lastReason, false);
            }

            if (isCongested)
            {
                if (_congestionStartMs < 0) _congestionStartMs = s.NowMs;
                if (s.NowMs - _congestionStartMs >= DowngradeWindowMs)
                {
                    _congestionWarning = true;
                    _lastReason = $"Original鎖定模式偵測頻寬不足 ({reason})";
                    if (s.NowMs - _lastWarningLogMs >= 10_000)
                    {
                        _lastWarningLogMs = s.NowMs;
                        Log.Warn($"[AdaptiveResolution] {CurrentProfile.StreamWidth}x{CurrentProfile.StreamHeight} 鎖定原生模式頻寬不足: {reason}");
                    }
                }
            }
            else if (isHealthy)
            {
                _congestionStartMs = -1;
                if (_congestionWarning)
                {
                    _congestionWarning = false;
                    _lastReason = "原生模式傳輸恢復穩定";
                }
            }

            return new AdaptiveDecision(false, _currentLevel, _currentLevel, CurrentProfile, _lastReason, _congestionWarning);
        }

        // Auto / Performance 模式：處理降級與恢復
        if (isCongested)
        {
            _stableStartMs = -1;
            _unhealthyMildStreak = 0;
            if (_congestionStartMs < 0) _congestionStartMs = s.NowMs;

            int requiredDowngradeMs = isSevere ? Math.Min(DowngradeWindowMs, 1500) : DowngradeWindowMs;
            bool windowMet = (s.NowMs - _congestionStartMs) >= requiredDowngradeMs;

            if (windowMet && (int)_currentLevel < ResolutionProfileManager.MaxLevelIndex)
            {
                long sinceSwitch = s.NowMs - _lastSwitchAtMs;
                int minCooldown = isSevere ? EmergencyCooldownMinMs : DefaultCooldownMs;
                if (sinceSwitch >= minCooldown)
                {
                    var prevLevel = _currentLevel;
                    var prevProf = _profiles[(int)prevLevel];
                    _currentLevel = (ResolutionLevel)((int)_currentLevel + 1);
                    var nextProf = _profiles[(int)_currentLevel];

                    // 防震盪退避：若升級後 15 秒內又再次壅塞降級，加倍恢復門檻（最高 35 秒）
                    if (_lastUpgradeAtMs > 0 && (s.NowMs - _lastUpgradeAtMs) <= 15_000)
                    {
                        _recoveryRequiredMs = Math.Min(MaxRecoveryWindowMs, _recoveryRequiredMs * 2);
                    }

                    _lastSwitchAtMs = s.NowMs;
                    _congestionStartMs = -1;
                    _congestionWarning = false;
                    _lastReason = reason;
                    LogTransition(prevProf, nextProf, reason);
                    return new AdaptiveDecision(true, prevLevel, _currentLevel, nextProf, reason, false);
                }
            }

            return new AdaptiveDecision(false, _currentLevel, _currentLevel, CurrentProfile, _lastReason, _congestionWarning);
        }

        // 非壅塞時重置降級觀察視窗
        _congestionStartMs = -1;

        if ((int)_currentLevel > 0 && isHealthy)
        {
            _unhealthyMildStreak = 0;
            if (_stableStartMs < 0) _stableStartMs = s.NowMs;

            if ((s.NowMs - _stableStartMs) >= _recoveryRequiredMs &&
                (s.NowMs - _lastSwitchAtMs) >= DefaultCooldownMs)
            {
                var prevLevel = _currentLevel;
                var prevProf = _profiles[(int)prevLevel];
                _currentLevel = (ResolutionLevel)((int)_currentLevel - 1);
                var nextProf = _profiles[(int)_currentLevel];

                _lastSwitchAtMs = s.NowMs;
                _lastUpgradeAtMs = s.NowMs;
                _stableStartMs = -1;
                _congestionWarning = false;
                _lastReason = $"Network stabilized for {_recoveryRequiredMs / 1000.0:F0}s";
                LogTransition(prevProf, nextProf, _lastReason);
                return new AdaptiveDecision(true, prevLevel, _currentLevel, nextProf, _lastReason, false);
            }
        }
        else if (!isHealthy)
        {
            // 中間模糊地帶（遲滯保護）：容忍最多 2 個孤立輕微抖動視窗 (暫停累計而非直接歸零)，
            // 若連續 >= 3 個取樣 (~1.5s) 仍未達健康門檻則重設恢復計時，避免邊緣抖動過早升級或無謂重置進度。
            _unhealthyMildStreak++;
            if (_stableStartMs >= 0 && _unhealthyMildStreak <= 2)
            {
                _stableStartMs = Math.Min(s.NowMs, _stableStartMs + evalDelta);
            }
            else
            {
                _stableStartMs = -1;
            }
        }
        else if (_currentLevel == ResolutionLevel.Level0 && _recoveryRequiredMs > BaseRecoveryWindowMs)
        {
            // 已穩定回到 Level 0 且無壅塞，逐步重置退避等待時間
            if (_stableStartMs < 0) _stableStartMs = s.NowMs;
            if ((s.NowMs - _stableStartMs) >= 12_000)
            {
                _recoveryRequiredMs = BaseRecoveryWindowMs;
                _stableStartMs = -1;
            }
        }

        return new AdaptiveDecision(false, _currentLevel, _currentLevel, CurrentProfile, _lastReason, _congestionWarning);
    }

    private void EvaluateCongestionSignals(in AdaptiveSample s, out bool isCongested, out bool isSevere, out string reason)
    {
        bool perf = _mode == ResolutionMode.Performance;
        float qThresh = perf ? 60f : 100f;
        float inflightThresh = perf ? 0.30f : 0.45f;
        float retransThresh = perf ? 3.5f : 5.0f;
        float minLossForRetrans = perf ? 1.5f : 2.0f;
        float fpsFactor = perf ? 0.75f : 0.65f;
        float e2eThresh = perf ? 220f : 350f;
        int rtoThresh = perf ? 1 : 2;
        long dropThresh = perf ? 1 : 2;

        // P1-02: isBenignUdpLoss 嚴格限定於 UDP 輕微封包遺失已由 ARQ 乾淨補回、且佇列/視窗未達門檻時，
        // 僅抑制 UDP 遺失/重傳誤觸發；Queue、InflightRatio、FPS、Latency 與硬停頓一律正常參與壅塞評估。
        bool isBenignUdpLoss = s.UdpRawLossRatePercent > 0f
                            && s.UdpRawLossRatePercent <= 2.0f
                            && s.ArqFinalSuccessRatePercent >= 99.0f
                            && s.SendQueueWaitMs < qThresh
                            && s.InflightRatio < inflightThresh
                            && s.RtoTimeoutsDelta == 0;

        bool sigQueue = s.SendQueueWaitMs >= qThresh
                     || s.SendQueuePending >= 2
                     || (s.IsTransportConstrained && (s.SendQueueWaitMs >= 25f || s.InflightRatio >= 0.30f));
        bool udpRetransSignaled = !isBenignUdpLoss
                               && s.RetransmissionRatePercent >= retransThresh
                               && s.UdpRawLossRatePercent > minLossForRetrans;
        bool sigWindow = s.InflightRatio >= inflightThresh
                      || udpRetransSignaled
                      || s.RtoTimeoutsDelta >= rtoThresh;
        float expectedFps = GetEffectiveExpectedSendFps(in s);
        bool sendFpsDrop = s.IsStreamActive && expectedFps >= 10f && s.ActualFps < expectedFps * fpsFactor;
        bool renderFpsDrop = s.IsStreamActive && expectedFps >= 10f && s.ActualFps >= 6f
                          && s.ViewerRenderFps > 0f
                          && s.ViewerRenderFps < Math.Min(expectedFps, s.ActualFps) * fpsFactor;
        bool sigFpsDrop = sendFpsDrop || renderFpsDrop || s.FrameDropsDelta >= dropThresh;
        bool sigLatency = s.EndToEndDelayMs >= e2eThresh;

        int signalCount = (sigQueue ? 1 : 0) + (sigWindow ? 1 : 0) + (sigFpsDrop ? 1 : 0) + (sigLatency ? 1 : 0);

        bool hardStall = s.SendQueueWaitMs >= (perf ? 250f : 400f) || s.InflightRatio >= (perf ? 0.60f : 0.75f);
        isCongested = signalCount >= 2 || hardStall;
        isSevere = s.SendQueueWaitMs >= 800f || s.InflightRatio >= 0.85f || s.RtoTimeoutsDelta >= 5;

        if (!isCongested)
        {
            reason = string.Empty;
            return;
        }

        var parts = new List<string>(4);
        if (sigQueue) parts.Add($"QueueWait={s.SendQueueWaitMs:F0}ms");
        if (s.InflightRatio >= inflightThresh) parts.Add($"Inflight={s.InflightRatio * 100f:F0}%");
        if (udpRetransSignaled)
            parts.Add($"Retrans={s.RetransmissionRatePercent:F1}%");
        if (s.RtoTimeoutsDelta >= rtoThresh) parts.Add($"RTO={s.RtoTimeoutsDelta}");
        if (sigFpsDrop) parts.Add($"FPS={s.ActualFps:F1}/{expectedFps:F0}(Cap:{s.TargetFps})");
        if (sigLatency) parts.Add($"E2E={s.EndToEndDelayMs:F0}ms");
        reason = string.Join(", ", parts);
    }

    private static float GetEffectiveExpectedSendFps(in AdaptiveSample s)
    {
        if (s.TargetFps <= 0) return 0f;
        if (s.IsTransportConstrained) return s.TargetFps;
        if (s.SourceFps > 0f) return Math.Min(s.TargetFps, s.SourceFps);

        bool transportClean = s.SendQueueWaitMs < 20f
                           && s.SendQueuePending <= 1
                           && s.InflightRatio < 0.15f
                           && s.UdpRawLossRatePercent <= 2.0f
                           && s.RetransmissionRatePercent <= 2.5f
                           && s.RtoTimeoutsDelta == 0
                           && s.FrameDropsDelta == 0
                           && s.EndToEndDelayMs < 180f;
        if (transportClean && s.ActualFps > 0f)
            return Math.Min(s.TargetFps, s.ActualFps);

        return s.TargetFps;
    }

    private static bool IsHealthyForRecovery(in AdaptiveSample s)
    {
        if (s.SendQueueWaitMs >= 20f || s.SendQueuePending > 1) return false;
        if (s.InflightRatio >= 0.15f) return false;
        if (s.UdpRawLossRatePercent > 2.0f || s.RetransmissionRatePercent > 2.5f) return false;
        if (s.RtoTimeoutsDelta > 0 || s.FrameDropsDelta > 0) return false;
        if (s.EndToEndDelayMs >= 180f) return false;
        if (s.IsTransportConstrained) return false;

        // 分離來源更新速率 (SourceFps) 與使用者目標上限 (TargetFps)：
        // 當 24/30fps 影片在 60fps 上限下順暢播放時，只要通道無排隊與掉幀且 SendFps 跟上 SourceFps，即視為穩定可恢復。
        float expectedFps = GetEffectiveExpectedSendFps(in s);
        if (s.IsStreamActive && expectedFps >= 10f && s.ActualFps < expectedFps * 0.85f)
            return false;

        if (s.IsStreamActive && expectedFps >= 10f && s.ActualFps >= 6f
            && s.ViewerRenderFps > 0f
            && s.ViewerRenderFps < Math.Min(expectedFps, s.ActualFps) * 0.75f)
            return false;

        return true;
    }

    private static void LogTransition(ResolutionProfile prev, ResolutionProfile next, string reason)
    {
        Log.Info($"AdaptiveResolution: {prev.StreamWidth}x{prev.StreamHeight} -> {next.StreamWidth}x{next.StreamHeight} (Level {(int)prev.Level} -> Level {(int)next.Level}) Reason: {reason}");
    }
}
