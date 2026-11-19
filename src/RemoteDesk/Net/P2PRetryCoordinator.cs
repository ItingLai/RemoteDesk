using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemoteDesk.Common;

namespace RemoteDesk.Net;

/// <summary>
/// Phase 4 P2P 失敗分類與多輪策略性自動重試協調引擎（規格 4-1 ~ 4-4）：
/// - Round 1：使用既有收集之 Candidate Pair 執行有限次雙向 Connectivity Check（搭配 PAC 延遲失敗判定）。
/// - Round 2：依失敗原因輪替／補充 STUN Server、重查有效網路介面、重建 UDP Socket 並重交換候選端點（維持同 Session）。
/// - Round 3：匯聚最新候選端點建立新 Candidate Pair、放寬檢查排程與逾時重新協商。
/// - 受控退避、單 Session 隔離、防重試風暴與使用者取消即時資源回收。
/// </summary>
internal static class P2PRetryCoordinator
{
    public static Task<P2PConnectionResult> ExecuteAsync(
        SignalingClient signalingClient,
        string sessionId,
        string selfDeviceId,
        string remoteDeviceId,
        bool isInitiator,
        P2PRetryOptions? options = null,
        CancellationToken ct = default)
    {
        return ExecuteAsync(
            new SignalingClientTransport(signalingClient),
            sessionId,
            selfDeviceId,
            remoteDeviceId,
            isInitiator,
            options,
            ct);
    }

    public static async Task<P2PConnectionResult> ExecuteAsync(
        IP2PSignalingTransport signaling,
        string sessionId,
        string selfDeviceId,
        string remoteDeviceId,
        bool isInitiator,
        P2PRetryOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new P2PRetryOptions();
        var utcNow = options.UtcNowProvider ?? (() => DateTime.UtcNow);
        var totalSw = Stopwatch.StartNew();
        string attemptId = Guid.NewGuid().ToString("N")[..12];

        if (!string.Equals(selfDeviceId, signaling.SelfDeviceId, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(signaling.SelfDeviceId))
        {
            Log.Info($"[ICE/HolePunch] Attempt={attemptId}, 本機 ViewerId ({selfDeviceId}) 與 Signaling 裝置 ID ({signaling.SelfDeviceId}) 不同，訊號通道採用 Signaling 裝置 ID。");
            selfDeviceId = signaling.SelfDeviceId;
        }

        int totalBudgetMs = Math.Max(800, options.TotalTimeoutMs);
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        totalCts.CancelAfter(totalBudgetMs);
        var token = totalCts.Token;

        int maxRounds = Math.Clamp(options.MaxRetryRounds, 1, 3);
        var inbox = new SignalingSessionInbox(
            sessionId,
            selfDeviceId,
            remoteDeviceId,
            () => utcNow() + signaling.EstimatedServerClockOffset);

        var roundReports = new List<P2PRetryRoundReport>();
        CandidateGatheringResult? currentGathering = null;
        List<IceCandidate> currentValidLocals = new();
        GatheringRoundSnapshot? previousSnapshot = null;
        StunProbeSummary? lastStunSummary = null;

        P2PFailureReason lastFailureReason = P2PFailureReason.None;
        string lastFailureDetail = "";
        int totalCheckAttempts = 0;
        int consecutiveNoInterface = 0;
        bool sessionExpiredDuringIncrementalPoll = false;

        try
        {
            for (int round = 1; round <= maxRounds; round++)
            {
                if (ct.IsCancellationRequested)
                {
                    lastFailureReason = P2PFailureReason.Cancelled;
                    lastFailureDetail = "Cancelled by caller";
                    break;
                }

                int remainingMs = totalBudgetMs - (int)totalSw.ElapsedMilliseconds;
                if (token.IsCancellationRequested || remainingMs < 180)
                {
                    if (lastFailureReason == P2PFailureReason.None)
                    {
                        lastFailureReason = ct.IsCancellationRequested ? P2PFailureReason.Cancelled : P2PFailureReason.CandidatePairCheckTimeout;
                        lastFailureDetail = "Total P2P timeout budget exhausted";
                    }
                    break;
                }

                // Round 2 / Round 3 前依上一輪失敗原因評估策略、防止重試風暴與執行隨機退避 (4-3)
                int appliedBackoffMs = 0;
                P2PFailureHandlingStrategy prevStrategy = P2PFailureClassifier.GetStrategy(lastFailureReason);
                if (round > 1)
                {
                    if (!prevStrategy.IsRetryableInSameSession)
                    {
                        Log.Warn($"[ICE/Retry] Attempt={attemptId}, Session={sessionId} 失敗原因 {lastFailureReason} 不可重試，終止後續輪次 ({prevStrategy.Description})");
                        break;
                    }

                    if (lastFailureReason == P2PFailureReason.NoUsableInterface && consecutiveNoInterface >= 2)
                    {
                        Log.Warn($"[ICE/Retry] Attempt={attemptId}, Session={sessionId} 連續兩輪皆無可用網路介面，抑制重複無效嘗試並結束");
                        break;
                    }

                    appliedBackoffMs = ComputeRandomizedBackoffMs(round, prevStrategy, options, remainingMs);
                    if (appliedBackoffMs > 0)
                    {
                        P2PRetryState backoffState = round == 2
                            ? P2PRetryState.BackingOffBeforeRound2
                            : P2PRetryState.BackingOffBeforeRound3;
                        Log.Info($"[ICE/Retry] Attempt={attemptId}, Session={sessionId}, NextRound={round}, State={backoffState}, PrevReason={lastFailureReason}, Action={prevStrategy.NextAction}, Backoff={appliedBackoffMs}ms");

                        try
                        {
                            await Task.Delay(appliedBackoffMs, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            lastFailureReason = ct.IsCancellationRequested ? P2PFailureReason.Cancelled : lastFailureReason;
                            break;
                        }
                    }

                    remainingMs = totalBudgetMs - (int)totalSw.ElapsedMilliseconds;
                    if (remainingMs < 150)
                    {
                        break;
                    }
                }

                DateTime roundStartUtc = utcNow();
                var roundSw = Stopwatch.StartNew();
                P2PRetryState roundState = round switch
                {
                    1 => P2PRetryState.Round1CheckingExistingPairs,
                    2 => P2PRetryState.Round2RefreshingCandidates,
                    _ => P2PRetryState.Round3Renegotiating
                };
                P2PRetryAction roundAction = round == 1
                    ? P2PRetryAction.RecheckExistingPairs
                    : (round == 3 ? P2PRetryAction.FullP2PRenegotiation : prevStrategy.NextAction);

                // ========================================================
                // Step A：候選端點收集 / 依策略重建 Socket、輪替 STUN、重查介面
                // ========================================================
                bool needGather = ShouldGatherInRound(round, prevStrategy, currentGathering, currentValidLocals);
                if (needGather)
                {
                    if (currentGathering != null)
                    {
                        previousSnapshot = P2PFailureClassifier.CaptureSnapshot(currentGathering, currentValidLocals.Count);
                        currentGathering.Dispose();
                        currentGathering = null;
                        currentValidLocals.Clear();
                    }

                    var gatherOpts = BuildGatheringOptionsForRound(round, prevStrategy, lastStunSummary, options, remainingMs);
                    try
                    {
                        currentGathering = options.CustomGatherAsync != null
                            ? await options.CustomGatherAsync(round, sessionId, attemptId, gatherOpts, token).ConfigureAwait(false)
                            : await IceCandidateGatherer.GatherAsync(sessionId, attemptId, gatherOpts, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        lastFailureReason = ct.IsCancellationRequested ? P2PFailureReason.Cancelled : P2PFailureReason.StunTimeout;
                        lastFailureDetail = ct.IsCancellationRequested ? "Cancelled during gathering" : "Gathering timeout";
                        RecordRoundAndLog(
                            roundReports, attemptId, sessionId, round, roundState, roundAction,
                            roundStartUtc, utcNow(), roundSw.Elapsed, 0, inbox.RemoteCandidates.Count, 0,
                            false, lastFailureReason, lastFailureDetail,
                            lastStunSummary ?? EmptyStunSummary(), Array.Empty<CandidatePair>(), appliedBackoffMs);
                        if (ct.IsCancellationRequested) break;
                        continue;
                    }

                    currentValidLocals = FilterAliveValidLocalCandidates(currentGathering);
                    lastStunSummary = P2PFailureClassifier.SummarizeStunProbes(currentGathering);
                }
                else
                {
                    // Round 3 重用健康存活 Socket（確保無已失效的 Candidate 混入）
                    currentValidLocals = FilterAliveValidLocalCandidates(currentGathering);
                    if (currentGathering == null || currentValidLocals.Count == 0)
                    {
                        currentGathering?.Dispose();
                        currentGathering = null;
                        var gatherOpts = BuildGatheringOptionsForRound(round, prevStrategy, lastStunSummary, options, remainingMs);
                        currentGathering = options.CustomGatherAsync != null
                            ? await options.CustomGatherAsync(round, sessionId, attemptId, gatherOpts, token).ConfigureAwait(false)
                            : await IceCandidateGatherer.GatherAsync(sessionId, attemptId, gatherOpts, token).ConfigureAwait(false);
                        currentValidLocals = FilterAliveValidLocalCandidates(currentGathering);
                        lastStunSummary = P2PFailureClassifier.SummarizeStunProbes(currentGathering);
                    }
                }

                if (currentGathering == null || currentValidLocals.Count == 0)
                {
                    bool hadInterfaces = currentGathering != null && currentGathering.Interfaces.Count > 0;
                    lastFailureReason = hadInterfaces ? P2PFailureReason.SocketFailure : P2PFailureReason.NoUsableInterface;
                    lastFailureDetail = hadInterfaces ? "Discovered interfaces failed socket binding" : "No usable IPv4 interface discovered";
                    if (lastFailureReason == P2PFailureReason.NoUsableInterface)
                        consecutiveNoInterface++;
                    else
                        consecutiveNoInterface = 0;

                    RecordRoundAndLog(
                        roundReports, attemptId, sessionId, round, roundState, roundAction,
                        roundStartUtc, utcNow(), roundSw.Elapsed, 0, inbox.RemoteCandidates.Count, 0,
                        false, lastFailureReason, lastFailureDetail,
                        lastStunSummary ?? EmptyStunSummary(), Array.Empty<CandidatePair>(), appliedBackoffMs);
                    continue;
                }

                consecutiveNoInterface = 0;

                // 若在 Round 2/3 重新收集後立即偵測公網 IP 漂移（NAT Mapping 改變），先記錄診斷
                bool natShiftedBetweenGatherRounds = P2PFailureClassifier.HasNatMappingChanged(previousSnapshot, currentGathering, null);
                if (natShiftedBetweenGatherRounds && round > 1)
                {
                    Log.Info($"[ICE/Retry] Attempt={attemptId}, Session={sessionId}, Round={round} 偵測到 NAT 映射相較上一輪已發生變化，使用新收集之公網端點進行交換與檢查");
                }

                // ========================================================
                // Step B：發佈本輪候選端點並與對端交換（維持同一 SessionId + 冪等 MessageId）
                // ========================================================
                bool shouldPublishLocals = needGather ||
                                          lastFailureReason is P2PFailureReason.CandidateExchangeTimeout
                                              or P2PFailureReason.SignalingMessageTimeout ||
                                          round == 3;

                SignalingExchangeOutcome exchangeOutcome;
                if (shouldPublishLocals)
                {
                    var pubOutcome = await PublishRoundCandidatesAsync(
                        signaling, sessionId, selfDeviceId, remoteDeviceId, attemptId,
                        round, lastFailureReason, currentValidLocals, token).ConfigureAwait(false);

                    if (pubOutcome.WorstStatus == SignalingCallStatus.SessionExpired)
                    {
                        lastFailureReason = P2PFailureReason.SessionExpired;
                        lastFailureDetail = pubOutcome.Detail;
                        RecordRoundAndLog(
                            roundReports, attemptId, sessionId, round, roundState, roundAction,
                            roundStartUtc, utcNow(), roundSw.Elapsed, currentValidLocals.Count, inbox.RemoteCandidates.Count, 0,
                            false, lastFailureReason, lastFailureDetail,
                            lastStunSummary ?? EmptyStunSummary(), Array.Empty<CandidatePair>(), appliedBackoffMs);
                        break;
                    }

                    int exchangeBudgetMs = GetRoundExchangeBudgetMs(round, maxRounds, options, totalBudgetMs - (int)totalSw.ElapsedMilliseconds);
                    exchangeOutcome = await WaitForRoundRemoteCandidatesAsync(
                        signaling, inbox, round, exchangeBudgetMs, options.ExchangePollIntervalMs, pubOutcome, token).ConfigureAwait(false);
                }
                else
                {
                    int exchangeBudgetMs = Math.Min(600, GetRoundExchangeBudgetMs(round, maxRounds, options, totalBudgetMs - (int)totalSw.ElapsedMilliseconds));
                    exchangeOutcome = await WaitForRoundRemoteCandidatesAsync(
                        signaling, inbox, round, exchangeBudgetMs, options.ExchangePollIntervalMs,
                        new SignalingExchangeOutcome(true, SignalingCallStatus.Ok, 0, inbox.RemoteCandidates.Count, inbox.RemoteDone),
                        token).ConfigureAwait(false);
                }

                if (exchangeOutcome.WorstStatus == SignalingCallStatus.SessionExpired)
                {
                    lastFailureReason = P2PFailureReason.SessionExpired;
                    lastFailureDetail = exchangeOutcome.Detail;
                    RecordRoundAndLog(
                        roundReports, attemptId, sessionId, round, roundState, roundAction,
                        roundStartUtc, utcNow(), roundSw.Elapsed, currentValidLocals.Count, inbox.RemoteCandidates.Count, 0,
                        false, lastFailureReason, lastFailureDetail,
                        lastStunSummary ?? EmptyStunSummary(), Array.Empty<CandidatePair>(), appliedBackoffMs);
                    break;
                }

                var effectiveRemotes = inbox.GetEffectiveRemoteCandidates(round, allowFallbackToOlderRounds: true).ToList();
                if (effectiveRemotes.Count == 0)
                {
                    lastFailureReason = exchangeOutcome.TransportErrorCount > 0
                        ? P2PFailureReason.SignalingMessageTimeout
                        : P2PFailureReason.CandidateExchangeTimeout;
                    lastFailureDetail = string.IsNullOrWhiteSpace(exchangeOutcome.Detail)
                        ? $"Round {round} remote candidate exchange timed out"
                        : exchangeOutcome.Detail;

                    RecordRoundAndLog(
                        roundReports, attemptId, sessionId, round, roundState, roundAction,
                        roundStartUtc, utcNow(), roundSw.Elapsed, currentValidLocals.Count, 0, 0,
                        false, lastFailureReason, lastFailureDetail,
                        lastStunSummary ?? EmptyStunSummary(), Array.Empty<CandidatePair>(), appliedBackoffMs);
                    continue;
                }

                // ========================================================
                // Step C：建立 Candidate Pair 並執行雙向 Connectivity Check（含 PAC 與增量更新）
                // ========================================================
                int remainingForCheckMs = totalBudgetMs - (int)totalSw.ElapsedMilliseconds;
                if (remainingForCheckMs < 150)
                {
                    lastFailureReason = P2PFailureReason.CandidatePairCheckTimeout;
                    lastFailureDetail = "Remaining timeout budget insufficient for connectivity check";
                    RecordRoundAndLog(
                        roundReports, attemptId, sessionId, round, roundState, roundAction,
                        roundStartUtc, utcNow(), roundSw.Elapsed, currentValidLocals.Count, effectiveRemotes.Count, 0,
                        false, lastFailureReason, lastFailureDetail,
                        lastStunSummary ?? EmptyStunSummary(), Array.Empty<CandidatePair>(), appliedBackoffMs);
                    break;
                }

                using var roundCheckCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var roundConnOpts = BuildConnectivityOptionsForRound(
                    round,
                    maxRounds,
                    options,
                    remainingForCheckMs,
                    inbox,
                    signaling,
                    onSessionExpired: () =>
                    {
                        sessionExpiredDuringIncrementalPoll = true;
                        try { roundCheckCts.Cancel(); } catch { }
                    });

                ConnectivityCheckResult checkResult;
                try
                {
                    checkResult = options.CustomCheckAsync != null
                        ? await options.CustomCheckAsync(round, currentGathering, effectiveRemotes, attemptId, sessionId, selfDeviceId, remoteDeviceId, isInitiator, roundConnOpts, roundCheckCts.Token).ConfigureAwait(false)
                        : await IceConnectivityChecker.RunAsync(currentGathering, effectiveRemotes, attemptId, sessionId, selfDeviceId, remoteDeviceId, isInitiator, roundConnOpts, roundCheckCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    lastFailureReason = P2PFailureReason.Cancelled;
                    lastFailureDetail = "Cancelled during connectivity check";
                    RecordRoundAndLog(
                        roundReports, attemptId, sessionId, round, roundState, roundAction,
                        roundStartUtc, utcNow(), roundSw.Elapsed, currentValidLocals.Count, effectiveRemotes.Count, 0,
                        false, lastFailureReason, lastFailureDetail,
                        lastStunSummary ?? EmptyStunSummary(), Array.Empty<CandidatePair>(), appliedBackoffMs);
                    break;
                }

                totalCheckAttempts += checkResult.TotalCheckAttempts;

                if (sessionExpiredDuringIncrementalPoll)
                {
                    lastFailureReason = P2PFailureReason.SessionExpired;
                    lastFailureDetail = "Session expired during incremental signaling poll";
                    RecordRoundAndLog(
                        roundReports, attemptId, sessionId, round, roundState, roundAction,
                        roundStartUtc, utcNow(), roundSw.Elapsed, currentValidLocals.Count, effectiveRemotes.Count, checkResult.AllPairs.Count,
                        false, lastFailureReason, lastFailureDetail,
                        lastStunSummary ?? EmptyStunSummary(), checkResult.AllPairs, appliedBackoffMs);
                    break;
                }

                if (checkResult.Succeeded && checkResult.SelectedPair != null)
                {
                    var selected = checkResult.SelectedPair;
                    Socket? wonSocket = currentGathering.DetachWinningSocket(selected.SocketId);
                    if (wonSocket != null)
                    {
                        bool ctrlRole = checkResult.FinalRole == IceRole.Controlling;
                        ulong finalTieBreaker = checkResult.TieBreaker != 0
                            ? checkResult.TieBreaker
                            : (roundConnOpts.OverrideTieBreaker ?? 0);

                        var postPunchContext = new IceStreamPostPunchContext
                        {
                            SessionId = sessionId,
                            SelfDeviceId = selfDeviceId,
                            RemoteDeviceId = remoteDeviceId,
                            SelectedLocalCandidateId = selected.LocalCandidateId,
                            SelectedRemoteCandidateId = selected.RemoteCandidateId,
                            SelectedPairId = selected.PairId,
                            IsControlling = ctrlRole,
                            TieBreaker = finalTieBreaker
                        };

                        var stream = new ReliableUdpStream(wonSocket, selected.RemoteEndPoint, ownsSocket: true, postPunchContext);

                        RecordRoundAndLog(
                            roundReports, attemptId, sessionId, round, roundState, roundAction,
                            roundStartUtc, utcNow(), roundSw.Elapsed, currentValidLocals.Count, effectiveRemotes.Count, checkResult.AllPairs.Count,
                            true, P2PFailureReason.None, "OK",
                            lastStunSummary ?? EmptyStunSummary(), checkResult.AllPairs, appliedBackoffMs);

                        Log.Info($"[ICE/Result] ★ Attempt={attemptId}, Session={sessionId}, SelectedLocalCandidate={selected.LocalCandidate}, SelectedRemoteCandidate={selected.RemoteCandidate}, SelectedInterface={selected.InterfaceId}, SelectedProtocol=UDP, ConnectionEstablishmentTime={totalSw.ElapsedMilliseconds}ms, TotalRetryCount={Math.Max(0, round - 1)}, TotalCheckAttempts={totalCheckAttempts}, FinalFailureReason=None");

                        try
                        {
                            if (inbox.PendingAckCount > 0)
                                await inbox.FlushPendingAcksDetailedAsync(signaling, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch { }

                        P2PPathSessionContext? pathContext = BuildPathSessionContext(
                            sessionId,
                            selfDeviceId,
                            remoteDeviceId,
                            isInitiator,
                            attemptId,
                            selected,
                            currentGathering,
                            currentValidLocals,
                            effectiveRemotes,
                            checkResult,
                            inbox,
                            options);

                        return new P2PConnectionResult
                        {
                            Succeeded = true,
                            Stream = stream,
                            SelectedPair = selected,
                            FinalFailureReason = P2PFailureReason.None,
                            FinalFailureDetail = "OK",
                            FinalState = P2PRetryState.Succeeded,
                            TotalRoundsAttempted = round,
                            TotalCheckAttempts = totalCheckAttempts,
                            Elapsed = totalSw.Elapsed,
                            ConnectionAttemptId = attemptId,
                            SessionId = sessionId,
                            RoundReports = roundReports,
                            PathContext = pathContext
                        };
                    }

                    lastFailureReason = P2PFailureReason.SocketFailure;
                    lastFailureDetail = "Winning socket detach failed";
                }
                else
                {
                    lastFailureReason = P2PFailureClassifier.ClassifyRoundFailure(
                        currentGathering,
                        previousSnapshot,
                        currentValidLocals.Count,
                        exchangeOutcome,
                        checkResult,
                        effectiveRemotes.Count,
                        ct);
                    lastFailureDetail = checkResult.FinalFailureReason;
                }

                RecordRoundAndLog(
                    roundReports, attemptId, sessionId, round, roundState, roundAction,
                    roundStartUtc, utcNow(), roundSw.Elapsed, currentValidLocals.Count, effectiveRemotes.Count, checkResult.AllPairs.Count,
                    false, lastFailureReason, lastFailureDetail,
                    lastStunSummary ?? EmptyStunSummary(), checkResult.AllPairs, appliedBackoffMs);
            }

            if (ct.IsCancellationRequested)
            {
                lastFailureReason = P2PFailureReason.Cancelled;
            }
            else if (lastFailureReason == P2PFailureReason.None)
            {
                lastFailureReason = P2PFailureReason.AllCandidatePairsFailed;
            }

            P2PRetryState finalState = lastFailureReason switch
            {
                P2PFailureReason.Cancelled => P2PRetryState.Cancelled,
                P2PFailureReason.SessionExpired => P2PRetryState.Expired,
                _ => P2PRetryState.Failed
            };

            int retriesExecuted = Math.Max(0, roundReports.Count - 1);
            Log.Warn($"[ICE/Result] Attempt={attemptId}, Session={sessionId}, SelectedLocalCandidate=-, SelectedRemoteCandidate=-, SelectedInterface=-, SelectedProtocol=UDP, ConnectionEstablishmentTime={totalSw.ElapsedMilliseconds}ms, TotalRetryCount={retriesExecuted}, TotalCheckAttempts={totalCheckAttempts}, FinalFailureReason={lastFailureReason} ({lastFailureDetail})");

            return new P2PConnectionResult
            {
                Succeeded = false,
                Stream = null,
                SelectedPair = null,
                FinalFailureReason = lastFailureReason,
                FinalFailureDetail = lastFailureDetail,
                FinalState = finalState,
                TotalRoundsAttempted = roundReports.Count,
                TotalCheckAttempts = totalCheckAttempts,
                Elapsed = totalSw.Elapsed,
                ConnectionAttemptId = attemptId,
                SessionId = sessionId,
                RoundReports = roundReports
            };
        }
        catch (OperationCanceledException)
        {
            var reason = ct.IsCancellationRequested ? P2PFailureReason.Cancelled : (lastFailureReason == P2PFailureReason.None ? P2PFailureReason.CandidatePairCheckTimeout : lastFailureReason);
            Log.Warn($"[ICE/Result] Attempt={attemptId}, Session={sessionId}, SelectedLocalCandidate=-, SelectedRemoteCandidate=-, SelectedInterface=-, SelectedProtocol=UDP, ConnectionEstablishmentTime={totalSw.ElapsedMilliseconds}ms, TotalRetryCount={Math.Max(0, roundReports.Count - 1)}, TotalCheckAttempts={totalCheckAttempts}, FinalFailureReason={reason}");

            return new P2PConnectionResult
            {
                Succeeded = false,
                FinalFailureReason = reason,
                FinalFailureDetail = ct.IsCancellationRequested ? "Cancelled" : "Timed out",
                FinalState = ct.IsCancellationRequested ? P2PRetryState.Cancelled : P2PRetryState.Failed,
                TotalRoundsAttempted = roundReports.Count,
                TotalCheckAttempts = totalCheckAttempts,
                Elapsed = totalSw.Elapsed,
                ConnectionAttemptId = attemptId,
                SessionId = sessionId,
                RoundReports = roundReports
            };
        }
        finally
        {
            currentGathering?.Dispose();
        }
    }

    private static bool ShouldGatherInRound(
        int round,
        P2PFailureHandlingStrategy prevStrategy,
        CandidateGatheringResult? currentGathering,
        List<IceCandidate> currentValidLocals)
    {
        if (round == 1) return true;
        if (currentGathering == null || currentValidLocals.Count == 0) return true;

        // 任何既有 Socket 已死亡或標記 SocketFailed，必須重整介面並重建 Socket
        if (currentGathering.Bindings.Any(b => b.IsDisposed || !CandidatePairBuilder.IsSocketAlive(b.Socket)))
            return true;

        if (round == 2)
        {
            // 第 2 輪依規格與策略決定：若單純 Signaling/Exchange 問題且既有 Socket 與候選均完整，保留既有好 Socket 不重複摧毀；
            // 其餘 STUN/Socket/NAT/PairCheckTimeout 皆重建 Socket + 更換 STUN 重新收集
            return prevStrategy.RequiresInterfaceAndSocketRefresh || prevStrategy.RequiresStunRotation;
        }

        // 第 3 輪預設優先使用最新健康端點重新協商；僅在前一輪面臨 STUN/NAT/Socket/NoInterface/單向阻塞 異常時重採
        return prevStrategy.RequiresInterfaceAndSocketRefresh &&
               prevStrategy.Reason is P2PFailureReason.SocketFailure
                   or P2PFailureReason.NatMappingChanged
                   or P2PFailureReason.OneWayConnectivity
                   or P2PFailureReason.NoUsableInterface
                   or P2PFailureReason.StunTimeout
                   or P2PFailureReason.StunServerUnavailable;
    }

    private static IceGatheringOptions BuildGatheringOptionsForRound(
        int round,
        P2PFailureHandlingStrategy prevStrategy,
        StunProbeSummary? lastStunSummary,
        P2PRetryOptions retryOptions,
        int remainingMs)
    {
        var baseOpts = retryOptions.GatheringOptions;
        var baseServers = baseOpts?.StunServers ?? retryOptions.PrimaryStunServers;

        List<string> orderedServers;
        bool stunEnabledInConfig = baseServers.Count > 0 ||
                                   prevStrategy.Reason is P2PFailureReason.StunTimeout or P2PFailureReason.StunServerUnavailable;

        if (round == 1 || !stunEnabledInConfig)
        {
            orderedServers = baseServers.ToList();
        }
        else
        {
            var failedSet = lastStunSummary?.FailedServers != null
                ? new HashSet<string>(lastStunSummary.FailedServers, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var healthy = baseServers.Where(s => !failedSet.Contains(s)).ToList();
            var supplemental = retryOptions.SupplementalStunServers
                .Where(s => !baseServers.Contains(s, StringComparer.OrdinalIgnoreCase) && !failedSet.Contains(s))
                .ToList();
            var failedTail = prevStrategy.Reason == P2PFailureReason.StunServerUnavailable && (healthy.Count + supplemental.Count) > 0
                ? new List<string>() // DNS/ServerError 明確失敗之 STUN 若有備援則本輪先跳過
                : baseServers.Where(s => failedSet.Contains(s)).ToList();

            // 第 2／3 輪輪替順序，優先嘗試未失敗與備援 STUN Server
            orderedServers = new List<string>();
            if (round >= 3 && healthy.Count > 1)
            {
                healthy.Reverse();
            }
            orderedServers.AddRange(healthy);
            orderedServers.AddRange(supplemental);
            orderedServers.AddRange(failedTail);

            if (orderedServers.Count == 0)
            {
                orderedServers.AddRange(baseServers);
                orderedServers.AddRange(retryOptions.SupplementalStunServers);
            }
        }

        int targetStunMs = round == 1 ? retryOptions.StunTimeoutMs : retryOptions.Round2StunTimeoutMs;
        if (baseOpts != null && round == 1)
            targetStunMs = baseOpts.StunTimeoutMs;
        int minStun = (baseOpts != null && round == 1) ? 50 : 200;
        int cappedStunMs = Math.Clamp(targetStunMs, minStun, Math.Max(minStun, remainingMs / 2));

        return new IceGatheringOptions
        {
            StunServers = orderedServers.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            StunTimeoutMs = cappedStunMs,
            MaxConcurrentInterfaces = baseOpts?.MaxConcurrentInterfaces ?? 6,
            IncludeLoopbackFallback = baseOpts?.IncludeLoopbackFallback ?? true,
            SkipStunWhenNoRoute = baseOpts?.SkipStunWhenNoRoute ?? false,
            DiscoveryOptions = baseOpts?.DiscoveryOptions,
            OverrideInterfaces = baseOpts?.OverrideInterfaces
        };
    }

    private static List<IceCandidate> FilterAliveValidLocalCandidates(CandidateGatheringResult? gathering)
    {
        if (gathering == null) return new List<IceCandidate>();
        return gathering.Candidates.Where(c =>
        {
            if (c.ValidityStatus != CandidateValidityStatus.Valid || string.IsNullOrWhiteSpace(c.CandidateId))
                return false;
            var sock = gathering.GetSocketForCandidate(c);
            return CandidatePairBuilder.IsSocketAlive(sock);
        }).ToList();
    }

    internal static async Task<SignalingExchangeOutcome> PublishRoundCandidatesAsync(
        IP2PSignalingTransport signaling,
        string sessionId,
        string selfDeviceId,
        string remoteDeviceId,
        string attemptId,
        int round,
        P2PFailureReason retryReason,
        IReadOnlyList<IceCandidate> localCandidates,
        CancellationToken ct)
    {
        int transportErrors = 0;
        SignalingCallStatus worstStatus = SignalingCallStatus.Ok;
        string detail = "";
        string reasonTag = retryReason == P2PFailureReason.None ? "" : retryReason.ToString();

        object outcomeLock = new();
        SignalingExchangeOutcome? expiredOutcome = null;

        if (localCandidates.Count > 0)
        {
            using var concurrencyGate = new SemaphoreSlim(6, 6);
            using var publishCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var tasks = localCandidates.Select(async cand =>
            {
                await concurrencyGate.WaitAsync(publishCts.Token).ConfigureAwait(false);
                try
                {
                    if (publishCts.IsCancellationRequested)
                        return;

                    var sig = new IceSignal
                    {
                        Type = "candidate",
                        Candidate = cand,
                        SessionId = sessionId,
                        SenderDeviceId = selfDeviceId,
                        ReceiverDeviceId = remoteDeviceId,
                        RetryRound = round,
                        RetryReason = reasonTag
                    };
                    string json = sig.ToJson();
                    string deterministicMsgId = $"{attemptId}-r{round}-c-{cand.CandidateId}";

                    var postRes = await signaling.PostSignalAsync(sessionId, remoteDeviceId, json, "candidate", publishCts.Token, deterministicMsgId).ConfigureAwait(false);
                    if (!postRes.Succeeded && postRes.Status == SignalingCallStatus.TimedOutOrTransportError && !publishCts.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref transportErrors);
                        await Task.Delay(80, publishCts.Token).ConfigureAwait(false);
                        postRes = await signaling.PostSignalAsync(sessionId, remoteDeviceId, json, "candidate", publishCts.Token, deterministicMsgId).ConfigureAwait(false);
                    }

                    if (postRes.Status == SignalingCallStatus.SessionExpired)
                    {
                        lock (outcomeLock)
                        {
                            expiredOutcome ??= new SignalingExchangeOutcome(false, SignalingCallStatus.SessionExpired, Volatile.Read(ref transportErrors), 0, false, postRes.ErrorDetail);
                        }
                        publishCts.Cancel();
                        return;
                    }

                    if (!postRes.Succeeded)
                    {
                        Interlocked.Increment(ref transportErrors);
                        lock (outcomeLock)
                        {
                            if (worstStatus == SignalingCallStatus.Ok) worstStatus = postRes.Status;
                            detail = postRes.ErrorDetail;
                        }
                    }
                }
                catch (OperationCanceledException) when (publishCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // Another candidate detected SessionExpired and cancelled sibling uploads.
                }
                finally
                {
                    concurrencyGate.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);
            if (expiredOutcome != null)
                return expiredOutcome;
        }

        var doneSig = new IceSignal
        {
            Type = "candidates-done",
            SessionId = sessionId,
            SenderDeviceId = selfDeviceId,
            ReceiverDeviceId = remoteDeviceId,
            RetryRound = round,
            RetryReason = reasonTag
        };
        string doneMsgId = $"{attemptId}-r{round}-done";
        var doneRes = await signaling.PostSignalAsync(sessionId, remoteDeviceId, doneSig.ToJson(), "candidates-done", ct, doneMsgId).ConfigureAwait(false);
        if (!doneRes.Succeeded && doneRes.Status == SignalingCallStatus.TimedOutOrTransportError && !ct.IsCancellationRequested)
        {
            transportErrors++;
            await Task.Delay(80, ct).ConfigureAwait(false);
            doneRes = await signaling.PostSignalAsync(sessionId, remoteDeviceId, doneSig.ToJson(), "candidates-done", ct, doneMsgId).ConfigureAwait(false);
        }

        if (doneRes.Status == SignalingCallStatus.SessionExpired)
        {
            return new SignalingExchangeOutcome(false, SignalingCallStatus.SessionExpired, transportErrors, 0, false, doneRes.ErrorDetail);
        }

        if (!doneRes.Succeeded)
        {
            transportErrors++;
            if (worstStatus == SignalingCallStatus.Ok) worstStatus = doneRes.Status;
            if (string.IsNullOrEmpty(detail)) detail = doneRes.ErrorDetail;
        }

        return new SignalingExchangeOutcome(transportErrors == 0, worstStatus, transportErrors, 0, false, detail);
    }

    internal static async Task<SignalingExchangeOutcome> WaitForRoundRemoteCandidatesAsync(
        IP2PSignalingTransport signaling,
        SignalingSessionInbox inbox,
        int round,
        int exchangeTimeoutMs,
        int pollIntervalMs,
        SignalingExchangeOutcome publishOutcome,
        CancellationToken ct,
        bool allowEarlyPriorFallback = true)
    {
        if (publishOutcome.WorstStatus == SignalingCallStatus.SessionExpired)
            return publishOutcome;

        var sw = Stopwatch.StartNew();
        int transportErrors = publishOutcome.TransportErrorCount;
        SignalingCallStatus worstStatus = publishOutcome.WorstStatus;
        string lastDetail = publishOutcome.Detail;
        int pollStep = Math.Clamp(pollIntervalMs, 20, 200);

        // 若進入第 2/3 輪且先前已取得對端較舊輪次候選，保留合理同步視窗等待對端本輪新候選，避免太早以舊埠空耗本輪時間
        bool hasPriorRemotes = allowEarlyPriorFallback && round > 1 && inbox.RemoteCandidates.Count > 0;
        int syncGraceWhenPriorExistsMs = Math.Min(exchangeTimeoutMs, Math.Max(420, (int)(exchangeTimeoutMs * 0.55)));

        while (sw.ElapsedMilliseconds < exchangeTimeoutMs && !ct.IsCancellationRequested)
        {
            if (!inbox.IsRoundDone(round))
            {
                var pollRes = await inbox.PollOnceAndAckDetailedAsync(signaling, ct).ConfigureAwait(false);
                if (pollRes.Status == SignalingCallStatus.SessionExpired)
                {
                    return new SignalingExchangeOutcome(false, SignalingCallStatus.SessionExpired, transportErrors, inbox.RemoteCandidates.Count, inbox.IsRoundDone(round), pollRes.ErrorDetail);
                }

                if (pollRes.Status == SignalingCallStatus.TimedOutOrTransportError)
                {
                    transportErrors++;
                    if (worstStatus == SignalingCallStatus.Ok) worstStatus = pollRes.Status;
                    if (!string.IsNullOrEmpty(pollRes.ErrorDetail)) lastDetail = pollRes.ErrorDetail;
                }
            }
            else if (inbox.PendingAckCount > 0)
            {
                var ackRes = await inbox.FlushPendingAcksDetailedAsync(signaling, ct).ConfigureAwait(false);
                if (ackRes.Status == SignalingCallStatus.SessionExpired)
                {
                    return new SignalingExchangeOutcome(false, SignalingCallStatus.SessionExpired, transportErrors, inbox.RemoteCandidates.Count, inbox.IsRoundDone(round), ackRes.ErrorDetail);
                }
                if (!ackRes.Succeeded)
                {
                    transportErrors++;
                    if (worstStatus == SignalingCallStatus.Ok) worstStatus = ackRes.Status;
                    if (!string.IsNullOrEmpty(ackRes.ErrorDetail)) lastDetail = ackRes.ErrorDetail;
                }
            }

            if (inbox.IsRoundDone(round) && inbox.PendingAckCount == 0)
                break;

            if (hasPriorRemotes && sw.ElapsedMilliseconds >= syncGraceWhenPriorExistsMs && inbox.PendingAckCount == 0)
                break;

            try
            {
                await Task.Delay(pollStep, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        int effectiveCount = inbox.GetEffectiveRemoteCandidates(round, allowFallbackToOlderRounds: true).Count;
        bool ok = effectiveCount > 0;
        return new SignalingExchangeOutcome(ok, worstStatus, transportErrors, effectiveCount, inbox.IsRoundDone(round), lastDetail);
    }

    private static IceConnectivityOptions BuildConnectivityOptionsForRound(
        int round,
        int maxRounds,
        P2PRetryOptions retryOptions,
        int remainingBudgetMs,
        SignalingSessionInbox inbox,
        IP2PSignalingTransport signaling,
        Action onSessionExpired)
    {
        var baseConn = retryOptions.ConnectivityOptions;

        int defaultTimeout = round switch
        {
            1 => maxRounds == 1 ? remainingBudgetMs : Math.Min(retryOptions.Round1CheckTimeoutMs, remainingBudgetMs),
            2 => maxRounds == 2 ? remainingBudgetMs : Math.Min(retryOptions.Round2CheckTimeoutMs, remainingMsForRound2(retryOptions, remainingBudgetMs)),
            _ => remainingBudgetMs
        };

        int checkTimeoutMs = baseConn != null
            ? Math.Min(baseConn.CheckTimeoutMs, remainingBudgetMs)
            : Math.Max(200, defaultTimeout);

        int maxAttempts = baseConn?.MaxAttemptsPerPair ?? (round switch
        {
            1 => maxRounds == 1 ? 6 : 4,
            2 => 5,
            _ => 6
        });

        IReadOnlyList<int> retryIntervals = baseConn?.RetryIntervalsMs ?? (round switch
        {
            1 => new[] { 90, 160, 260 },
            2 => new[] { 120, 220, 340, 480 },
            _ => new[] { 140, 250, 400, 580, 760 }
        });

        int pacingMs = baseConn?.PacingIntervalMs ?? (round == 3 ? 25 : 20);

        return new IceConnectivityOptions
        {
            CheckTimeoutMs = checkTimeoutMs,
            PacingIntervalMs = pacingMs,
            MaxAttemptsPerPair = maxAttempts,
            RetryIntervalsMs = retryIntervals,
            SelectionGraceWindowMs = baseConn?.SelectionGraceWindowMs ?? 70,
            NominationTimeoutMs = baseConn?.NominationTimeoutMs ?? 1500,
            MaxCandidateAgeSeconds = baseConn?.MaxCandidateAgeSeconds ?? 300,
            ForceRole = baseConn?.ForceRole,
            OverrideTieBreaker = baseConn?.OverrideTieBreaker,
            UtcNowProvider = baseConn?.UtcNowProvider ?? retryOptions.UtcNowProvider,
            AllowRestrictedPortPredictionFallback = baseConn?.AllowRestrictedPortPredictionFallback ?? false,
            RestrictedPortPredictionMaxDelta = baseConn?.RestrictedPortPredictionMaxDelta ?? 4,
            RecentSuccessHints = baseConn?.RecentSuccessHints,
            PacMinimumCheckWaitMs = baseConn != null
                ? Math.Max(baseConn.PacMinimumCheckWaitMs, 0)
                : Math.Min(retryOptions.PacMinimumCheckWaitMs, Math.Max(0, checkTimeoutMs / 4)),
            IncrementalPollIntervalMs = Math.Max(40, retryOptions.ExchangePollIntervalMs * 2),
            IsRemoteExchangeCompleteProvider = () => inbox.IsRoundDone(round),
            IncrementalRemoteCandidatePollProvider = async pollCt =>
            {
                var pollRes = await inbox.PollOnceAndAckDetailedAsync(signaling, pollCt).ConfigureAwait(false);
                if (pollRes.Status == SignalingCallStatus.SessionExpired)
                {
                    onSessionExpired();
                    return Array.Empty<IceCandidate>();
                }
                return inbox.GetEffectiveRemoteCandidates(round, allowFallbackToOlderRounds: true);
            }
        };

        static int remainingMsForRound2(P2PRetryOptions opts, int rem) =>
            Math.Min(opts.Round2CheckTimeoutMs, Math.Max(250, (int)(rem * 0.60)));
    }

    private static int GetRoundExchangeBudgetMs(int round, int maxRounds, P2PRetryOptions options, int remainingMs)
    {
        int raw = round switch
        {
            1 => options.Round1ExchangeTimeoutMs,
            2 => options.Round2ExchangeTimeoutMs,
            _ => options.Round3ExchangeTimeoutMs
        };

        int minExchange = Math.Min(raw, 80);
        int maxShare = round == maxRounds
            ? Math.Max(minExchange, (int)(remainingMs * 0.45))
            : Math.Max(minExchange, (int)(remainingMs * 0.40));
        return Math.Clamp(raw, minExchange, maxShare);
    }

    private static int ComputeRandomizedBackoffMs(
        int nextRound,
        P2PFailureHandlingStrategy strategy,
        P2PRetryOptions options,
        int remainingMs)
    {
        if (strategy.BackoffMultiplier <= 0.0)
            return 0;

        int rawMin = nextRound == 2 ? options.BackoffBeforeRound2MinMs : options.BackoffBeforeRound3MinMs;
        int rawMax = nextRound == 2 ? options.BackoffBeforeRound2MaxMs : options.BackoffBeforeRound3MaxMs;
        if (rawMax < rawMin) rawMax = rawMin;

        int scaledMin = (int)Math.Round(rawMin * strategy.BackoffMultiplier);
        int scaledMax = (int)Math.Round(rawMax * strategy.BackoffMultiplier);
        if (scaledMax < scaledMin) scaledMax = scaledMin;

        // 限制單次退避不超過剩餘預算的 25%，確保留有充分時間給實際收集與打洞檢查
        int maxAllowed = Math.Max(10, remainingMs / 4);
        scaledMax = Math.Min(scaledMax, maxAllowed);
        scaledMin = Math.Min(scaledMin, scaledMax);

        if (scaledMax <= 0) return 0;
        if (scaledMin == scaledMax) return scaledMin;

        var rng = options.RandomNextProvider ?? ((min, maxExclusive) => Random.Shared.Next(min, maxExclusive));
        return Math.Clamp(rng(scaledMin, scaledMax + 1), scaledMin, scaledMax);
    }

    private static void RecordRoundAndLog(
        List<P2PRetryRoundReport> reports,
        string attemptId,
        string sessionId,
        int round,
        P2PRetryState state,
        P2PRetryAction action,
        DateTime startUtc,
        DateTime endUtc,
        TimeSpan elapsed,
        int localCandidateCount,
        int remoteCandidateCount,
        int pairCount,
        bool succeeded,
        P2PFailureReason reason,
        string detail,
        StunProbeSummary stunSummary,
        IReadOnlyList<CandidatePair> pairsSnapshot,
        int backoffMs)
    {
        reports.Add(new P2PRetryRoundReport(
            round, state, action, startUtc, endUtc, elapsed,
            localCandidateCount, remoteCandidateCount, pairCount,
            succeeded, reason, detail, stunSummary, pairsSnapshot, backoffMs));

        string finalStr = succeeded ? "Succeeded" : $"{reason} ({detail})";
        Log.Info($"[ICE/Retry] Attempt={attemptId}, Session={sessionId}, RetryRound={round}, RetryReason={reason}, RetryStartTimestamp={startUtc:O}, RetryEndTimestamp={endUtc:O}, CandidateCount={localCandidateCount}/{remoteCandidateCount}, PairCount={pairCount}, BackoffMs={backoffMs}, FinalResult={finalStr}");
    }

    private static StunProbeSummary EmptyStunSummary() =>
        new(0, 0, 0, 0, 0, Array.Empty<string>());

    private static P2PPathSessionContext? BuildPathSessionContext(
        string sessionId,
        string selfDeviceId,
        string remoteDeviceId,
        bool isInitiator,
        string attemptId,
        CandidatePair selected,
        CandidateGatheringResult gathering,
        IReadOnlyList<IceCandidate> localCandidates,
        IReadOnlyList<IceCandidate> remoteCandidates,
        ConnectivityCheckResult checkResult,
        SignalingSessionInbox inbox,
        P2PRetryOptions options)
    {
        if (!options.RetainStandbyBindingsForMonitoring)
            return null;

        var activeBinding = gathering.Bindings.FirstOrDefault(b =>
            string.Equals(b.SocketId, selected.SocketId, StringComparison.OrdinalIgnoreCase));
        var activeIface = activeBinding?.Interface ?? new DiscoveredNetworkInterface
        {
            InterfaceId = selected.InterfaceId,
            InterfaceName = selected.InterfaceId,
            LocalIPv4 = IPAddress.TryParse(selected.LocalCandidate.LocalAddress, out var parsedLocalIp)
                ? parsedLocalIp
                : (IPAddress.TryParse(selected.LocalCandidate.Ip, out var parsedCandIp) ? parsedCandIp : IPAddress.Any),
            SubnetMask = IPAddress.Parse("255.255.255.0"),
            GatewayIPv4 = null,
            HasValidRoute = true
        };

        var ctx = new P2PPathSessionContext
        {
            SessionId = sessionId,
            SelfDeviceId = selfDeviceId,
            RemoteDeviceId = remoteDeviceId,
            IsInitiator = isInitiator,
            ConnectionAttemptId = attemptId,
            ActivePair = selected,
            ActiveInterface = activeIface,
            ActiveLocalCandidate = selected.LocalCandidate,
            ActiveRemoteCandidate = selected.RemoteCandidate,
            SharedInbox = inbox,
            StunServers = options.GatheringOptions?.StunServers ?? options.PrimaryStunServers,
            SupplementalStunServers = options.SupplementalStunServers,
            BaseGatheringOptions = options.GatheringOptions,
            BaseConnectivityOptions = options.ConnectivityOptions
        };

        ctx.KnownLocalCandidates.AddRange(localCandidates);
        ctx.KnownRemoteCandidates.AddRange(remoteCandidates);
        ctx.KnownPairs.AddRange(checkResult.AllPairs);

        // 將尚未釋放之非勝出備援介面 Socket 的所有權安全轉移至 P2PPathSessionContext（供 Phase 5 步驟 2 快速測試）
        foreach (var b in gathering.Bindings)
        {
            if (string.Equals(b.SocketId, selected.SocketId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (b.IsDisposed || b.IsDetached || !CandidatePairBuilder.IsSocketAlive(b.Socket))
                continue;

            try
            {
                var standbySock = b.DetachSocket();
                var transferred = new GatheredSocketBinding
                {
                    SocketId = b.SocketId,
                    Interface = b.Interface,
                    Socket = standbySock,
                    LocalEndPoint = b.LocalEndPoint,
                    NatObservation = b.NatObservation,
                    IsLoopbackFallback = b.IsLoopbackFallback,
                    OwnsSocket = true
                };
                transferred.Candidates.AddRange(b.Candidates);
                transferred.StunProbes.AddRange(b.StunProbes);
                ctx.StandbyBindings.Add(transferred);
            }
            catch { }
        }

        return ctx;
    }
}
