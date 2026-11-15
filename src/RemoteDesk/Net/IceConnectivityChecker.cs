using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RemoteDesk.Common;

namespace RemoteDesk.Net;

/// <summary>
/// Phase 3 Candidate Pair 建立、排序與多介面均衡排程器。
/// </summary>
internal static class CandidatePairBuilder
{
    public sealed record BuiltPairPlan(
        IReadOnlyList<CandidatePair> PriorityOrderedPairs,
        IReadOnlyList<CandidatePair> CheckSchedulePairs,
        int ExpiredLocalCandidatesSkipped,
        int ExpiredRemoteCandidatesSkipped,
        int InvalidOrUnboundCandidatesSkipped);

    public static BuiltPairPlan BuildPairs(
        CandidateGatheringResult gathering,
        IReadOnlyList<IceCandidate> remoteCandidates,
        string remoteDeviceId,
        IceRole localRole,
        IceConnectivityOptions? options = null,
        bool includePortPredictionFallback = false)
    {
        options ??= new IceConnectivityOptions();
        DateTime nowUtc = (options.UtcNowProvider ?? (() => DateTime.UtcNow))();
        TimeSpan maxAge = TimeSpan.FromSeconds(Math.Max(5, options.MaxCandidateAgeSeconds));

        var bindingMap = gathering.Bindings.ToDictionary(b => b.SocketId, b => b, StringComparer.OrdinalIgnoreCase);
        var recentHints = options.RecentSuccessHints ?? RecentSuccessfulPathStore.GetHintsForPeer(remoteDeviceId);

        int expiredLocal = 0, expiredRemote = 0, invalidSkipped = 0;

        var validLocals = new List<(IceCandidate Cand, GatheredSocketBinding Binding, IPEndPoint Ep)>();
        foreach (var lc in gathering.Candidates)
        {
            if (lc.ValidityStatus != CandidateValidityStatus.Valid || lc.AddressFamily != AddressFamily.InterNetwork)
            {
                invalidSkipped++;
                continue;
            }
            if (lc.DiscoveryTimestamp != default && (nowUtc - lc.DiscoveryTimestamp) > maxAge)
            {
                lc.ValidityStatus = CandidateValidityStatus.Expired;
                expiredLocal++;
                continue;
            }

            if (!bindingMap.TryGetValue(lc.SocketId, out var binding))
            {
                var sock = gathering.GetSocketForCandidate(lc);
                binding = sock != null ? gathering.Bindings.FirstOrDefault(b => ReferenceEquals(b.Socket, sock)) : null;
            }

            if (binding == null || binding.IsDisposed || !IsSocketAlive(binding.Socket))
            {
                lc.ValidityStatus = CandidateValidityStatus.SocketFailed;
                invalidSkipped++;
                continue;
            }

            var lep = lc.EndPoint ?? binding.LocalEndPoint;
            if (lep == null || lep.Port <= 0 || lep.AddressFamily != AddressFamily.InterNetwork)
            {
                invalidSkipped++;
                continue;
            }

            validLocals.Add((lc, binding, binding.LocalEndPoint));
        }

        var validRemotes = new List<(IceCandidate Cand, IPEndPoint Ep)>();
        var seenRemoteKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rc in remoteCandidates)
        {
            if (rc.ValidityStatus != CandidateValidityStatus.Valid || rc.AddressFamily != AddressFamily.InterNetwork)
            {
                invalidSkipped++;
                continue;
            }
            if (rc.DiscoveryTimestamp != default && (nowUtc - rc.DiscoveryTimestamp) > maxAge)
            {
                expiredRemote++;
                continue;
            }

            var rep = rc.EndPoint;
            if (rep == null || rep.Port <= 0 || rep.Port > 65535 || IPAddress.Any.Equals(rep.Address))
            {
                invalidSkipped++;
                continue;
            }

            string dedupKey = !string.IsNullOrWhiteSpace(rc.CandidateId)
                ? $"id:{rc.CandidateId}"
                : $"ep:{(int)rc.Type}:{rep}";
            if (!seenRemoteKeys.Add(dedupKey)) continue;

            validRemotes.Add((rc, rep));
        }

        var pairs = new List<CandidatePair>();
        var pairDedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (lc, binding, localBoundEp) in validLocals)
        {
            bool localIsFallbackLoop = binding.IsLoopbackFallback;
            bool localIsStrictRealNic = !binding.IsLoopbackFallback && !IPAddress.IsLoopback(localBoundEp.Address);

            foreach (var (rc, remEp) in validRemotes)
            {
                bool remIsLoop = IPAddress.IsLoopback(remEp.Address);

                // 僅讓 LoopbackFallback 配對 Loopback；真實實體 NIC 不必對 127.0.0.1 送探測
                if (localIsFallbackLoop && !remIsLoop) continue;
                if (localIsStrictRealNic && remIsLoop) continue;

                if (!includePortPredictionFallback)
                {
                    AddPairIfUnique(pairs, pairDedup, lc, rc, binding, localBoundEp, remEp, localRole, recentHints, isPredictionFallback: false);
                }
                else if (rc.Type == IceCandidateType.Srflx && !IPAddress.Loopback.Equals(remEp.Address) && !localIsFallbackLoop)
                {
                    int maxDelta = Math.Clamp(options.RestrictedPortPredictionMaxDelta, 1, 8);
                    for (int delta = 1; delta <= maxDelta; delta++)
                    {
                        foreach (int signedDelta in new[] { delta, -delta })
                        {
                            int port = remEp.Port + signedDelta;
                            if (port <= 1024 || port >= 65535) continue;

                            var predictedRc = new IceCandidate
                            {
                                CandidateId = $"{rc.CandidateId}-p{(signedDelta > 0 ? $"+{signedDelta}" : $"{signedDelta}")}",
                                Type = rc.Type,
                                AddressFamily = rc.AddressFamily,
                                Protocol = rc.Protocol,
                                Ip = remEp.Address.ToString(),
                                Port = port,
                                PublicAddress = remEp.Address.ToString(),
                                PublicPort = port,
                                InterfaceId = rc.InterfaceId,
                                Priority = Math.Max(1U, rc.Priority > 1000 ? rc.Priority - 1000 : rc.Priority),
                                DiscoveryTimestamp = rc.DiscoveryTimestamp,
                                ValidityStatus = rc.ValidityStatus
                            };
                            AddPairIfUnique(pairs, pairDedup, lc, predictedRc, binding, localBoundEp, new IPEndPoint(remEp.Address, port), localRole, recentHints, isPredictionFallback: true);
                        }
                    }
                }
            }
        }

        var priorityOrdered = pairs
            .OrderByDescending(p => p.PairPriority)
            .ThenBy(p => p.IsPortPredictionFallback)
            .ThenBy(p => p.PairId, StringComparer.Ordinal)
            .ToList();

        var checkScheduled = BuildDiverseCheckSchedule(priorityOrdered);

        return new BuiltPairPlan(priorityOrdered, checkScheduled, expiredLocal, expiredRemote, invalidSkipped);
    }

    private static void AddPairIfUnique(
        List<CandidatePair> pairs,
        HashSet<string> pairDedup,
        IceCandidate lc,
        IceCandidate rc,
        GatheredSocketBinding binding,
        IPEndPoint localBoundEp,
        IPEndPoint remEp,
        IceRole localRole,
        IReadOnlyCollection<RecentSuccessfulPairHint> recentHints,
        bool isPredictionFallback)
    {
        string sockEpKey = $"{binding.SocketId}|{remEp}";
        if (!pairDedup.Add(sockEpKey)) return;

        bool isLoopPair = binding.IsLoopbackFallback || IPAddress.Loopback.Equals(remEp.Address);
        bool isSameSubnet = !isLoopPair &&
                            lc.Type == IceCandidateType.Host &&
                            rc.Type == IceCandidateType.Host &&
                            IsSameIPv4Subnet(binding.Interface.LocalIPv4, binding.Interface.SubnetMask, remEp.Address);

        bool isRecentHint = !isPredictionFallback && recentHints.Any(h =>
            string.Equals(h.LocalInterfaceId, binding.Interface.InterfaceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(h.LocalIPv4, binding.Interface.LocalIPv4.ToString(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(h.RemotePublicIp, remEp.Address.ToString(), StringComparison.OrdinalIgnoreCase) &&
            h.LocalType == lc.Type &&
            h.RemoteType == rc.Type);

        ulong g = localRole == IceRole.Controlling ? lc.Priority : rc.Priority;
        ulong d = localRole == IceRole.Controlling ? rc.Priority : lc.Priority;
        ulong rfcBase = ((ulong)Math.Min(g, d) << 32) + ((ulong)Math.Max(g, d) << 1) + (g > d ? 1UL : 0UL);
        ulong scaledRfc = rfcBase >> 4;

        // Tier 排序：近期成功 > 同子網 Host <-> Host > 一般非 Loopback > Loopback Fallback
        ulong tier = isLoopPair
            ? 0UL
            : isRecentHint
                ? 3UL
                : isSameSubnet
                    ? 2UL
                    : 1UL;

        ulong pairPriority = (tier << 60) | scaledRfc;
        string pairId = $"pair-{ShortTag(lc.CandidateId)}-{ShortTag(rc.CandidateId)}-{pairs.Count + 1}";

        pairs.Add(new CandidatePair
        {
            PairId = pairId,
            LocalCandidateId = lc.CandidateId,
            RemoteCandidateId = rc.CandidateId,
            LocalCandidate = lc,
            RemoteCandidate = rc,
            SocketId = binding.SocketId,
            InterfaceId = binding.Interface.InterfaceId,
            LocalEndPoint = localBoundEp,
            RemoteEndPoint = remEp,
            PairPriority = pairPriority,
            IsRecentSuccessHint = isRecentHint,
            IsSameSubnetHost = isSameSubnet,
            IsLoopbackPair = isLoopPair,
            IsPortPredictionFallback = isPredictionFallback
        });
    }

    /// <summary>
    /// 在同階層內依 (本地介面, 遠端候選介面/IP) 輪詢交錯排程，避免單一故障 WAN 或網卡佔滿初期檢查視窗。
    /// 最終獲勝仍依據 PairPriority，而非檢查順序。
    /// </summary>
    private static List<CandidatePair> BuildDiverseCheckSchedule(List<CandidatePair> priorityOrdered)
    {
        if (priorityOrdered.Count <= 2) return new List<CandidatePair>(priorityOrdered);

        var scheduled = new List<CandidatePair>(priorityOrdered.Count);
        var tierGroups = priorityOrdered.GroupBy(p => p.PairPriority >> 60).OrderByDescending(g => g.Key);

        foreach (var group in tierGroups)
        {
            var buckets = group
                .GroupBy(p => $"{p.InterfaceId}|{p.RemoteEndPoint.Address}", StringComparer.OrdinalIgnoreCase)
                .Select(b => new Queue<CandidatePair>(b))
                .ToList();

            bool progressed;
            do
            {
                progressed = false;
                foreach (var q in buckets)
                {
                    if (q.Count > 0)
                    {
                        scheduled.Add(q.Dequeue());
                        progressed = true;
                    }
                }
            } while (progressed);
        }

        return scheduled;
    }

    internal static bool IsSocketAlive(Socket? s)
    {
        if (s == null) return false;
        try { return s.IsBound && s.LocalEndPoint != null; }
        catch { return false; }
    }

    private static bool IsSameIPv4Subnet(IPAddress localIp, IPAddress subnetMask, IPAddress remoteIp)
    {
        if (localIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            remoteIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            subnetMask.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        Span<byte> a = stackalloc byte[4];
        Span<byte> b = stackalloc byte[4];
        Span<byte> m = stackalloc byte[4];
        if (!localIp.TryWriteBytes(a, out _) || !remoteIp.TryWriteBytes(b, out _) || !subnetMask.TryWriteBytes(m, out _))
            return false;

        // 僅當遮罩有效 (非 0.0.0.0) 時比對網路前綴
        if (m[0] == 0 && m[1] == 0 && m[2] == 0 && m[3] == 0) return false;
        for (int i = 0; i < 4; i++)
        {
            if ((a[i] & m[i]) != (b[i] & m[i])) return false;
        }
        return true;
    }

    private static string ShortTag(string id)
    {
        var clean = new string(id.Where(char.IsLetterOrDigit).ToArray());
        if (clean.Length == 0) return "c";
        return clean.Length <= 6 ? clean : clean[..6];
    }
}

/// <summary>
/// Phase 3 Candidate Pair 雙向連通性檢查與選定協調引擎。
/// - 每個 Pair 使用原本對應的已繫結 UDP Socket 送收。
/// - 每筆 Check 附帶唯一 Transaction ID、Session ID 與裝置身分驗證。
/// - 嚴格區分單向收到封包 (OneWayReceived) 與雙向確認成功 (Succeeded)。
/// - 由 Controlling 方統一發起 NominateRequest，Controlled 方僅於局部配對亦通過雙向檢查後回覆 NominateAck，避免雙端選出不同 Pair。
/// - 支援雙 Controlling 競態下的 TieBreaker 仲裁收斂。
/// </summary>
internal sealed class IceConnectivityChecker
{
    private sealed record PendingTransaction(
        CandidatePair Pair,
        string SocketId,
        int AttemptNumber,
        long SentTimestamp,
        bool IsNomination);

    private readonly CandidateGatheringResult _gathering;
    private readonly string _connectionAttemptId;
    private readonly string _sessionId;
    private readonly string _selfDeviceId;
    private readonly string _remoteDeviceId;
    private readonly IceConnectivityOptions _options;
    private readonly Func<DateTime> _utcNow;
    private readonly ulong _tieBreaker;

    private readonly object _stateLock = new();
    private readonly Dictionary<string, PendingTransaction> _pendingTxns = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completedResponseTxns = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CandidatePair> _allPairs = new();
    private readonly HashSet<string> _registeredPairKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IPEndPoint> _observedSelfPublicEndPoints = new();
    private readonly Dictionary<string, List<CandidatePair>> _pairsBySocket = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<CandidatePair> _triggeredQueue = new();
    private readonly Queue<CandidatePair> _dynamicSchedulePairs = new();
    private readonly SemaphoreSlim _wakeSignal = new(0);

    private IceRole _currentRole;
    private CandidatePair? _activeNominationPair;
    private (CandidatePair Pair, byte[] TxnId, IPEndPoint FromEp)? _pendingRemoteNomination;
    private volatile CandidatePair? _selectedPair;
    private int _winnerClaimed;
    private int _dynamicPrflxCount;
    private long _controlledFirstSelectTick;
    private long _controlledLastSwitchTick;
    private volatile bool _peerStreamStarted;

    private int _totalCheckAttempts;
    private int _invalidSessionPacketsRejected;
    private int _duplicateResponsesIgnored;
    private int _invalidPacketsRejected;
    private int _roleConflictCount;

    private IceConnectivityChecker(
        CandidateGatheringResult gathering,
        string connectionAttemptId,
        string sessionId,
        string selfDeviceId,
        string remoteDeviceId,
        IceRole initialRole,
        ulong tieBreaker,
        IceConnectivityOptions options)
    {
        _gathering = gathering;
        _connectionAttemptId = connectionAttemptId;
        _sessionId = sessionId;
        _selfDeviceId = selfDeviceId;
        _remoteDeviceId = remoteDeviceId;
        _currentRole = initialRole;
        _tieBreaker = tieBreaker;
        _options = options;
        _utcNow = options.UtcNowProvider ?? (() => DateTime.UtcNow);
    }

    public static async Task<ConnectivityCheckResult> RunAsync(
        CandidateGatheringResult gathering,
        IReadOnlyList<IceCandidate> remoteCandidates,
        string connectionAttemptId,
        string sessionId,
        string selfDeviceId,
        string remoteDeviceId,
        bool isInitiator,
        IceConnectivityOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new IceConnectivityOptions();
        var role = options.ForceRole ?? (isInitiator ? IceRole.Controlling : IceRole.Controlled);
        ulong tieBreaker = options.OverrideTieBreaker ?? BuildDefaultTieBreaker();

        var checker = new IceConnectivityChecker(
            gathering,
            connectionAttemptId,
            sessionId,
            selfDeviceId,
            remoteDeviceId,
            role,
            tieBreaker,
            options);

        return await checker.ExecuteAsync(remoteCandidates, ct).ConfigureAwait(false);
    }

    private async Task<ConnectivityCheckResult> ExecuteAsync(IReadOnlyList<IceCandidate> remoteCandidates, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();

        var plan = CandidatePairBuilder.BuildPairs(
            _gathering,
            remoteCandidates,
            _remoteDeviceId,
            _currentRole,
            _options,
            includePortPredictionFallback: false);

        if (plan.ExpiredLocalCandidatesSkipped > 0 || plan.ExpiredRemoteCandidatesSkipped > 0)
        {
            Log.Info($"[ICE/Pair] Attempt={_connectionAttemptId}, Session={_sessionId} 略過過期候選: LocalExpired={plan.ExpiredLocalCandidatesSkipped}, RemoteExpired={plan.ExpiredRemoteCandidatesSkipped}");
        }

        bool canExpectDynamicRemotes = _options.IncrementalRemoteCandidatePollProvider != null &&
                                       _gathering.Bindings.Any(b => !b.IsDisposed && CandidatePairBuilder.IsSocketAlive(b.Socket));

        if (plan.PriorityOrderedPairs.Count == 0 && !canExpectDynamicRemotes)
        {
            return new ConnectivityCheckResult
            {
                Succeeded = false,
                AllPairs = Array.Empty<CandidatePair>(),
                Elapsed = totalSw.Elapsed,
                FinalFailureReason = "NoValidCandidatePairs"
            };
        }

        RegisterPairs(plan.PriorityOrderedPairs);

        Log.Info($"[ICE/Pair] Attempt={_connectionAttemptId}, Session={_sessionId}, Role={_currentRole}, PairsBuilt={plan.PriorityOrderedPairs.Count}");

        using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiveTasks = StartPerSocketReceiveLoops(recvCts.Token);

        using var primaryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        primaryCts.CancelAfter(Math.Max(200, _options.CheckTimeoutMs));
        await RunChecksAndSelectionLoopAsync(plan.CheckSchedulePairs, isFallbackPass: false, remoteCandidates, primaryCts).ConfigureAwait(false);

        int legacyAttempts = 0;
        int legacySuccesses = 0;
        bool selectedViaPrediction = false;

        // 3-6：僅當所有直接 Candidate Pair 皆失敗，且顯式啟用受控備援時，執行有限 Port 偏移 Candidate Pair 檢查
        if (_selectedPair == null && !ct.IsCancellationRequested && _options.AllowRestrictedPortPredictionFallback)
        {
            var fallbackPlan = CandidatePairBuilder.BuildPairs(
                _gathering,
                remoteCandidates,
                _remoteDeviceId,
                _currentRole,
                _options,
                includePortPredictionFallback: true);

            if (fallbackPlan.PriorityOrderedPairs.Count > 0)
            {
                Log.Info($"[ICE/Fallback] Attempt={_connectionAttemptId}, Session={_sessionId} 所有直接 Candidate Pair 失敗，啟動受限埠預測備援 (Pairs={fallbackPlan.PriorityOrderedPairs.Count}, MaxDelta=±{_options.RestrictedPortPredictionMaxDelta})");
                RegisterPairs(fallbackPlan.PriorityOrderedPairs);

                using var fbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                fbCts.CancelAfter(Math.Clamp(_options.CheckTimeoutMs, 800, 2000));
                int attemptsBefore = _totalCheckAttempts;
                await RunChecksAndSelectionLoopAsync(fallbackPlan.CheckSchedulePairs, isFallbackPass: true, remoteCandidates, fbCts).ConfigureAwait(false);
                legacyAttempts = Math.Max(0, _totalCheckAttempts - attemptsBefore);

                if (_selectedPair != null && _selectedPair.IsPortPredictionFallback)
                {
                    legacySuccesses = 1;
                    selectedViaPrediction = true;
                }
            }
        }

        recvCts.Cancel();
        try { await Task.WhenAll(receiveTasks).ConfigureAwait(false); } catch { }
        _wakeSignal.Dispose();

        FinalizeRemainingPairStates(ct.IsCancellationRequested);

        var allSnapshot = GetAllPairsSnapshot();
        var winner = _selectedPair;

        if (winner != null)
        {
            RecentSuccessfulPathStore.RecordSuccess(_remoteDeviceId, winner);
            Log.Info($"[ICE/Pair] Attempt={_connectionAttemptId}, Session={_sessionId} ★ Pair 選定完成: PairId={winner.PairId}, LocalCand={winner.LocalCandidateId} ({winner.LocalEndPoint}), RemoteCand={winner.RemoteCandidateId} ({winner.RemoteEndPoint}), RTT={winner.RttMs?.ToString("F1") ?? "-"}ms, Elapsed={totalSw.ElapsedMilliseconds}ms, ViaPrediction={selectedViaPrediction}");
        }
        else
        {
            string summaryReason = ClassifyOverallFailure(allSnapshot, ct.IsCancellationRequested);
            Log.Warn($"[ICE/Pair] Attempt={_connectionAttemptId}, Session={_sessionId} 所有 Candidate Pair 檢查未通過: Pairs={allSnapshot.Count}, Reason={summaryReason}, Elapsed={totalSw.ElapsedMilliseconds}ms");
        }

        List<IPEndPoint> observedSnapshot;
        lock (_stateLock)
        {
            observedSnapshot = _observedSelfPublicEndPoints.ToList();
        }

        return new ConnectivityCheckResult
        {
            Succeeded = winner != null,
            SelectedPair = winner,
            AllPairs = allSnapshot,
            Elapsed = totalSw.Elapsed,
            TotalCheckAttempts = _totalCheckAttempts,
            InvalidSessionPacketsRejected = _invalidSessionPacketsRejected,
            DuplicateResponsesIgnored = _duplicateResponsesIgnored,
            InvalidPacketsRejected = _invalidPacketsRejected,
            RoleConflictCount = _roleConflictCount,
            LegacyPortPredictionAttempts = legacyAttempts,
            LegacyPortPredictionSuccesses = legacySuccesses,
            SelectedViaPortPrediction = selectedViaPrediction,
            FinalRole = GetCurrentRole(),
            TieBreaker = _tieBreaker,
            ObservedSelfPublicEndPoints = observedSnapshot,
            FinalFailureReason = winner != null ? "OK" : ClassifyOverallFailure(allSnapshot, ct.IsCancellationRequested)
        };
    }

    private List<CandidatePair> RegisterPairs(IReadOnlyList<CandidatePair> pairs)
    {
        var added = new List<CandidatePair>();
        lock (_stateLock)
        {
            foreach (var p in pairs)
            {
                string key = $"{p.SocketId}|{p.RemoteEndPoint}";
                if (!_registeredPairKeys.Add(key))
                    continue;

                _allPairs.Add(p);
                added.Add(p);
                if (!_pairsBySocket.TryGetValue(p.SocketId, out var list))
                {
                    list = new List<CandidatePair>();
                    _pairsBySocket[p.SocketId] = list;
                }
                list.Add(p);
            }
        }
        return added;
    }

    private List<Task> StartPerSocketReceiveLoops(CancellationToken ct)
    {
        var tasks = new List<Task>(_gathering.Bindings.Count);
        foreach (var binding in _gathering.Bindings)
        {
            if (binding.IsDisposed || !CandidatePairBuilder.IsSocketAlive(binding.Socket))
            {
                InvalidatePairsForSocket(binding.SocketId, CandidatePairFailureReason.LocalSocketDisposed, "Socket already disposed before check");
                continue;
            }

            tasks.Add(Task.Run(() => SocketReceiveLoopAsync(binding, ct), CancellationToken.None));
        }
        return tasks;
    }

    private async Task SocketReceiveLoopAsync(GatheredSocketBinding binding, CancellationToken ct)
    {
        byte[] recvBuf = new byte[4096];
        EndPoint remoteAny = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested && !_peerStreamStarted)
        {
            try
            {
                SocketReceiveFromResult res;
                if (_selectedPair != null)
                {
                    var peekRes = await binding.Socket.ReceiveFromAsync(recvBuf, SocketFlags.Peek, remoteAny, ct).ConfigureAwait(false);
                    if (peekRes.ReceivedBytes >= 4 &&
                        recvBuf[0] == (byte)'R' &&
                        recvBuf[1] == (byte)'D' &&
                        recvBuf[2] == (byte)'S' &&
                        recvBuf[3] == (byte)'K')
                    {
                        _peerStreamStarted = true;
                        WakeScheduler();
                        break;
                    }

                    res = await binding.Socket.ReceiveFromAsync(recvBuf, SocketFlags.None, remoteAny, ct).ConfigureAwait(false);
                }
                else
                {
                    res = await binding.Socket.ReceiveFromAsync(recvBuf, SocketFlags.None, remoteAny, ct).ConfigureAwait(false);
                }

                var span = recvBuf.AsSpan(0, res.ReceivedBytes);
                var fromEp = (IPEndPoint)res.RemoteEndPoint;

                if (!IceCheckCodec.TryParse(span, out var msg))
                {
                    Interlocked.Increment(ref _invalidPacketsRejected);
                    continue;
                }

                // Session 與雙方裝置身分隔離檢查（拒絕舊 Session、錯誤裝置封包）
                if (!string.Equals(msg.SessionId, _sessionId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(msg.ReceiverDeviceId, _selfDeviceId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(msg.SenderDeviceId, _remoteDeviceId, StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref _invalidSessionPacketsRejected);
                    Log.Warn($"[ICE/Pair] Attempt={_connectionAttemptId}, Session={_sessionId} 拒絕跨階段或身分不符封包: MsgType={msg.MessageType}, MsgSession={msg.SessionId}, From={fromEp}");
                    continue;
                }

                switch (msg.MessageType)
                {
                    case IceCheckMessageType.CheckRequest:
                        await HandleIncomingCheckRequestAsync(binding, fromEp, msg, ct).ConfigureAwait(false);
                        break;

                    case IceCheckMessageType.CheckResponse:
                        await HandleIncomingCheckResponseAsync(binding, fromEp, msg).ConfigureAwait(false);
                        break;

                    case IceCheckMessageType.NominateRequest:
                        await HandleIncomingNominateRequestAsync(binding, fromEp, msg, ct).ConfigureAwait(false);
                        break;

                    case IceCheckMessageType.NominateAck:
                        HandleIncomingNominateAck(binding, fromEp, msg);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                InvalidatePairsForSocket(binding.SocketId, CandidatePairFailureReason.LocalSocketDisposed, "Socket disposed mid-check");
                WakeScheduler();
                break;
            }
            catch (SocketException sex)
            {
                if (IsFatalSocketError(sex.SocketErrorCode))
                {
                    bool disposedNow = binding.IsDisposed || !CandidatePairBuilder.IsSocketAlive(binding.Socket);
                    InvalidatePairsForSocket(
                        binding.SocketId,
                        disposedNow ? CandidatePairFailureReason.LocalSocketDisposed : CandidatePairFailureReason.SocketError,
                        disposedNow ? $"Socket disposed ({sex.SocketErrorCode})" : $"SocketError {sex.SocketErrorCode}");
                    WakeScheduler();
                    break;
                }
                // 暫時性 ICMP Port Unreachable (ConnectionReset 等) 不應終止其他目標檢查
            }
            catch (Exception ex)
            {
                Log.Warn($"[ICE/Pair] Attempt={_connectionAttemptId}, Socket={binding.SocketId} 接收迴圈異常: {ex.Message}");
                break;
            }
        }
    }

    private async Task HandleIncomingCheckRequestAsync(
        GatheredSocketBinding binding,
        IPEndPoint fromEp,
        IceCheckMessage msg,
        CancellationToken ct)
    {
        CandidatePair? matchedPair;
        bool shouldTrigger = false;

        lock (_stateLock)
        {
            if (_selectedPair != null && _currentRole != IceRole.Controlled) return;

            matchedPair = FindOrRegisterInboundPairLocked(
                binding,
                fromEp,
                msg.RemoteCandidateId,
                msg.LocalCandidateId,
                allowPeerReflexive: _currentRole == IceRole.Controlled,
                out bool newlyRegisteredPrflx);
            if (newlyRegisteredPrflx)
            {
                shouldTrigger = true;
            }

            if (matchedPair != null && matchedPair.CheckState != CandidatePairCheckState.Invalidated)
            {
                matchedPair.InboundPacketObserved = true;
                if (matchedPair.CheckState is CandidatePairCheckState.Unchecked
                    or CandidatePairCheckState.InProgress
                    or CandidatePairCheckState.TimedOut)
                {
                    matchedPair.CheckState = CandidatePairCheckState.OneWayReceived;
                }

                if (!matchedPair.OutboundCheckAcked)
                {
                    long nowTicks = Stopwatch.GetTimestamp();
                    if (matchedPair.LastTriggeredCheckTick == 0 ||
                        Stopwatch.GetElapsedTime(matchedPair.LastTriggeredCheckTick, nowTicks).TotalMilliseconds >= 50)
                    {
                        matchedPair.LastTriggeredCheckTick = nowTicks;
                        _triggeredQueue.Enqueue(matchedPair);
                        shouldTrigger = true;
                    }
                }
            }
        }

        if (shouldTrigger) WakeScheduler();

        // 不論我方是否已檢出對向，只要同 Session 的合法 CheckRequest 到達即回送對應 TxnId 的 CheckResponse
        byte[] resp = IceCheckCodec.Serialize(
            IceCheckMessageType.CheckResponse,
            isControlling: GetCurrentRole() == IceRole.Controlling,
            useCandidate: false,
            roleConflictRejected: false,
            attemptNumber: msg.AttemptNumber,
            transactionId: msg.TransactionId,
            tieBreaker: _tieBreaker,
            priority: matchedPair?.PairPriority ?? msg.Priority,
            observedEndPoint: fromEp,
            sessionId: _sessionId,
            senderDeviceId: _selfDeviceId,
            receiverDeviceId: _remoteDeviceId,
            localCandidateId: matchedPair?.LocalCandidateId ?? msg.RemoteCandidateId,
            remoteCandidateId: matchedPair?.RemoteCandidateId ?? msg.LocalCandidateId,
            pairId: matchedPair?.PairId ?? msg.PairId);

        try
        {
            await binding.Socket.SendToAsync(resp, SocketFlags.None, fromEp, ct).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task HandleIncomingCheckResponseAsync(
        GatheredSocketBinding binding,
        IPEndPoint fromEp,
        IceCheckMessage msg)
    {
        CandidatePair? succeededPair = null;
        PendingTransaction? pending;
        double rttMs = 0;
        bool needSendPendingNomAck = false;
        (CandidatePair Pair, byte[] TxnId, IPEndPoint FromEp)? nomToConfirm = null;

        lock (_stateLock)
        {
            if (_completedResponseTxns.Contains(msg.TransactionIdHex))
            {
                Interlocked.Increment(ref _duplicateResponsesIgnored);
                if (_pairsBySocket.TryGetValue(binding.SocketId, out var bPairs))
                {
                    var dupPair = bPairs.FirstOrDefault(p => p.RemoteEndPoint.Equals(fromEp));
                    if (dupPair != null) dupPair.DuplicateResponseCount++;
                }
                return;
            }

            if (!_pendingTxns.TryGetValue(msg.TransactionIdHex, out pending))
            {
                Interlocked.Increment(ref _invalidPacketsRejected);
                return;
            }

            // 必須確認回應來自預期的對端與同一 Socket
            if (!string.Equals(pending.SocketId, binding.SocketId, StringComparison.OrdinalIgnoreCase) ||
                !fromEp.Equals(pending.Pair.RemoteEndPoint))
            {
                Interlocked.Increment(ref _invalidPacketsRejected);
                if (pending.Pair.CheckState != CandidatePairCheckState.Succeeded &&
                    pending.Pair.CheckState != CandidatePairCheckState.Invalidated)
                {
                    pending.Pair.CheckState = CandidatePairCheckState.InvalidResponse;
                    pending.Pair.FailureReason = CandidatePairFailureReason.InvalidEndpoint;
                    pending.Pair.FailureDetail = $"Response source mismatch: expected {pending.Pair.RemoteEndPoint}, got {fromEp}";
                }
                Log.Warn($"[ICE/Pair] Attempt={_connectionAttemptId}, PairId={pending.Pair.PairId} 回應來源不符 ({fromEp} != {pending.Pair.RemoteEndPoint})，已拒絕");
                return;
            }

            _completedResponseTxns.Add(msg.TransactionIdHex);

            if (msg.ObservedEndPoint != null && !_observedSelfPublicEndPoints.Contains(msg.ObservedEndPoint))
            {
                _observedSelfPublicEndPoints.Add(msg.ObservedEndPoint);
            }

            var pair = pending.Pair;
            if (pair.CheckState == CandidatePairCheckState.Invalidated)
                return;

            rttMs = Math.Round(Stopwatch.GetElapsedTime(pending.SentTimestamp).TotalMilliseconds, 2);
            pair.OutboundCheckAcked = true;

            if (pair.CheckState != CandidatePairCheckState.Succeeded)
            {
                pair.RttMs = rttMs;
                pair.CheckState = CandidatePairCheckState.Succeeded;
                pair.FailureReason = CandidatePairFailureReason.None;
                pair.FailureDetail = "OK";
                succeededPair = pair;
            }

            // 若對端已發來此 Pair 的 NominateRequest 正在等待我方確認雙向成功，立即完成選定與 ACK
            if (_pendingRemoteNomination is { } pendNom &&
                ReferenceEquals(pendNom.Pair, pair))
            {
                if (_selectedPair == null && Interlocked.CompareExchange(ref _winnerClaimed, 1, 0) == 0)
                {
                    pair.IsNominated = true;
                    pair.IsSelected = true;
                    _selectedPair = pair;
                    _pendingRemoteNomination = null;
                    _controlledFirstSelectTick = Stopwatch.GetTimestamp();
                    _controlledLastSwitchTick = _controlledFirstSelectTick;
                    needSendPendingNomAck = true;
                    nomToConfirm = pendNom;
                }
                else if (_currentRole == IceRole.Controlled && _selectedPair != null && !ReferenceEquals(_selectedPair, pair))
                {
                    _selectedPair.IsSelected = false;
                    _selectedPair.IsNominated = false;
                    pair.IsNominated = true;
                    pair.IsSelected = true;
                    _selectedPair = pair;
                    _pendingRemoteNomination = null;
                    _controlledLastSwitchTick = Stopwatch.GetTimestamp();
                    needSendPendingNomAck = true;
                    nomToConfirm = pendNom;
                    Log.Info($"[ICE/Pair] Attempt={_connectionAttemptId}, Session={_sessionId} Controlled 完成候補雙向檢查並切換至 PairId={pair.PairId}");
                }
            }
        }

        if (succeededPair != null)
        {
            Log.Info($"[ICE/Pair] Attempt={_connectionAttemptId}, Session={_sessionId}, PairId={succeededPair.PairId}, LocalCand={succeededPair.LocalCandidateId}, RemoteCand={succeededPair.RemoteCandidateId}, Attempt={pending!.AttemptNumber}, Ts={_utcNow():O}, RTT={rttMs:F1}ms, Result=Succeeded");
            WakeScheduler();
        }

        if (needSendPendingNomAck && nomToConfirm is { } ackTarget)
        {
            await SendNominateAckBurstAsync(binding, ackTarget.FromEp, ackTarget.TxnId, ackTarget.Pair).ConfigureAwait(false);
            WakeScheduler();
        }
    }

    private async Task HandleIncomingNominateRequestAsync(
        GatheredSocketBinding binding,
        IPEndPoint fromEp,
        IceCheckMessage msg,
        CancellationToken ct)
    {
        bool sendConflictReject = false;
        bool sendAckNow = false;
        bool needTriggerBidirectionalCheck = false;
        CandidatePair? matchedPair;

        lock (_stateLock)
        {
            matchedPair = FindOrRegisterInboundPairLocked(binding, fromEp, msg.RemoteCandidateId, msg.LocalCandidateId, allowPeerReflexive: false, out _);

            // 已選定同一 Pair，重送 NominateAck 確保對方不因單包遺失卡住；Controlled 在確認視窗內允許切換至對端改選的備援 Pair
            if (_selectedPair != null)
            {
                if (matchedPair != null && ReferenceEquals(_selectedPair, matchedPair) && fromEp.Equals(_selectedPair.RemoteEndPoint))
                {
                    sendAckNow = true;
                }
                else if (_currentRole == IceRole.Controlled &&
                         matchedPair != null &&
                         fromEp.Equals(matchedPair.RemoteEndPoint) &&
                         matchedPair.CheckState != CandidatePairCheckState.Invalidated)
                {
                    matchedPair.InboundPacketObserved = true;
                    if (matchedPair.CheckState == CandidatePairCheckState.Succeeded)
                    {
                        _selectedPair.IsSelected = false;
                        _selectedPair.IsNominated = false;
                        matchedPair.IsNominated = true;
                        matchedPair.IsSelected = true;
                        _selectedPair = matchedPair;
                        _pendingRemoteNomination = null;
                        _controlledLastSwitchTick = Stopwatch.GetTimestamp();
                        sendAckNow = true;
                        Log.Info($"[ICE/Pair] Attempt={_connectionAttemptId}, Session={_sessionId} Controlled 依對端重新 Nominate 切換至 PairId={matchedPair.PairId}");
                    }
                    else
                    {
                        _pendingRemoteNomination = (matchedPair, msg.TransactionId, fromEp);
                        _triggeredQueue.Enqueue(matchedPair);
                        needTriggerBidirectionalCheck = true;
                    }
                }
                else
                {
                    return;
                }
            }
            else if (matchedPair == null || !fromEp.Equals(matchedPair.RemoteEndPoint))
            {
                Interlocked.Increment(ref _invalidPacketsRejected);
                return;
            }
            else if (matchedPair.CheckState == CandidatePairCheckState.Invalidated)
            {
                return;
            }
            else if (_currentRole == IceRole.Controlling)
            {
                // 雙方同時皆為 Controlling 時依 TieBreaker 仲裁：數值大（相等時 DeviceId 字典序較大）者勝出並維持 Controlling，另一方讓步為 Controlled
                int cmp = CompareAuthority(msg.TieBreaker, msg.SenderDeviceId, _tieBreaker, _selfDeviceId);
                if (cmp > 0)
                {
                    _roleConflictCount++;
                    _currentRole = IceRole.Controlled;
                    _activeNominationPair = null;
                    Log.Info($"[ICE/Pair] Attempt={_connectionAttemptId}, Session={_sessionId} 偵測雙 Controlling 競爭 (PeerTie={msg.TieBreaker} > LocalTie={_tieBreaker})，本端讓步切換為 Controlled 接受對端選定 PairId={matchedPair.PairId}");
                }
                else
                {
                    _roleConflictCount++;
                    sendConflictReject = true;
                }
            }

            if (!sendConflictReject && !sendAckNow && !needTriggerBidirectionalCheck && matchedPair != null)
            {
                matchedPair.InboundPacketObserved = true;

                // 3-5：只有經過有效雙向連通性檢查（本端亦確認 CheckState == Succeeded）的 Pair 才能完成選定
                if (matchedPair.CheckState == CandidatePairCheckState.Succeeded)
                {
                    if (Interlocked.CompareExchange(ref _winnerClaimed, 1, 0) == 0)
                    {
                        matchedPair.IsNominated = true;
                        matchedPair.IsSelected = true;
                        _selectedPair = matchedPair;
                        _pendingRemoteNomination = null;
                        _controlledFirstSelectTick = Stopwatch.GetTimestamp();
                        _controlledLastSwitchTick = _controlledFirstSelectTick;
                        sendAckNow = true;
                    }
                }
                else
                {
                    // 尚未完成我方 CheckRequest -> CheckResponse 雙向驗證：先暫存待選定，並立即補發一次高優先雙向 Check
                    _pendingRemoteNomination = (matchedPair, msg.TransactionId, fromEp);
                    _triggeredQueue.Enqueue(matchedPair);
                    needTriggerBidirectionalCheck = true;
                }
            }
        }

        if (sendConflictReject)
        {
            byte[] rejectPkt = IceCheckCodec.Serialize(
                IceCheckMessageType.NominateAck,
                isControlling: true,
                useCandidate: false,
                roleConflictRejected: true,
                attemptNumber: msg.AttemptNumber,
                transactionId: msg.TransactionId,
                tieBreaker: _tieBreaker,
                priority: matchedPair?.PairPriority ?? 0,
                observedEndPoint: fromEp,
                sessionId: _sessionId,
                senderDeviceId: _selfDeviceId,
                receiverDeviceId: _remoteDeviceId,
                localCandidateId: matchedPair?.LocalCandidateId ?? msg.RemoteCandidateId,
                remoteCandidateId: matchedPair?.RemoteCandidateId ?? msg.LocalCandidateId,
                pairId: matchedPair?.PairId ?? msg.PairId);
            try { await binding.Socket.SendToAsync(rejectPkt, SocketFlags.None, fromEp, ct).ConfigureAwait(false); } catch { }
            return;
        }

        if (needTriggerBidirectionalCheck)
        {
            WakeScheduler();
            return;
        }

        if (sendAckNow && matchedPair != null)
        {
            await SendNominateAckBurstAsync(binding, fromEp, msg.TransactionId, matchedPair).ConfigureAwait(false);
            WakeScheduler();
        }
    }

    private void HandleIncomingNominateAck(GatheredSocketBinding binding, IPEndPoint fromEp, IceCheckMessage msg)
    {
        bool wake = false;
        lock (_stateLock)
        {
            if (_completedResponseTxns.Contains(msg.TransactionIdHex))
            {
                Interlocked.Increment(ref _duplicateResponsesIgnored);
                return;
            }

            if (!_pendingTxns.TryGetValue(msg.TransactionIdHex, out var pending) || !pending.IsNomination)
            {
                Interlocked.Increment(ref _invalidPacketsRejected);
                return;
            }

            if (!string.Equals(pending.SocketId, binding.SocketId, StringComparison.OrdinalIgnoreCase) ||
                !fromEp.Equals(pending.Pair.RemoteEndPoint))
            {
                Interlocked.Increment(ref _invalidPacketsRejected);
                return;
            }

            _completedResponseTxns.Add(msg.TransactionIdHex);

            if (msg.ObservedEndPoint != null && !_observedSelfPublicEndPoints.Contains(msg.ObservedEndPoint))
            {
                _observedSelfPublicEndPoints.Add(msg.ObservedEndPoint);
            }

            if (msg.RoleConflictRejected)
            {
                int cmp = CompareAuthority(msg.TieBreaker, msg.SenderDeviceId, _tieBreaker, _selfDeviceId);
                if (cmp > 0)
                {
                    _roleConflictCount++;
                    _currentRole = IceRole.Controlled;
                    _activeNominationPair = null;
                    Log.Info($"[ICE/Pair] Attempt={_connectionAttemptId}, Session={_sessionId} 收到對端 RoleConflict (PeerTie={msg.TieBreaker})，切換為 Controlled 等待對端 NominateRequest");
                    wake = true;
                }
                return;
            }

            var pair = pending.Pair;
            if (pair.CheckState == CandidatePairCheckState.Succeeded &&
                Interlocked.CompareExchange(ref _winnerClaimed, 1, 0) == 0)
            {
                pair.IsNominated = true;
                pair.IsSelected = true;
                _selectedPair = pair;
                wake = true;
            }
        }

        if (wake) WakeScheduler();
    }

    private async Task RunChecksAndSelectionLoopAsync(
        IReadOnlyList<CandidatePair> scheduleOrder,
        bool isFallbackPass,
        IReadOnlyList<IceCandidate> baseRemoteCandidates,
        CancellationTokenSource runCts)
    {
        var ct = runCts.Token;
        int maxAttempts = isFallbackPass ? Math.Min(3, _options.MaxAttemptsPerPair) : Math.Max(1, _options.MaxAttemptsPerPair);
        var retryIntervals = _options.RetryIntervalsMs.Count > 0 ? _options.RetryIntervalsMs : new[] { 120, 220, 350, 500, 650 };

        // 為每個 Pair 規劃下一次檢查時刻（起始以 PacingIntervalMs 交錯發送，避免一次 UDP 洪峰）
        var activeSchedule = new List<CandidatePair>(scheduleOrder);
        var knownRemoteIds = new HashSet<string>(
            baseRemoteCandidates.Select(r => $"{r.CandidateId}|{r.Ip}:{r.Port}"),
            StringComparer.OrdinalIgnoreCase);

        var nextSendDueMs = new Dictionary<CandidatePair, long>();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < activeSchedule.Count; i++)
        {
            nextSendDueMs[activeSchedule[i]] = (long)i * Math.Max(0, _options.PacingIntervalMs);
        }

        long firstLowerPrioritySuccessSwMs = -1;
        long lastNominationSendMs = -1000;
        long nominationStartMs = -1;
        long lastIncrementalPollMs = 0;

        while (!ct.IsCancellationRequested && !ShouldExitSelectionLoop(out _))
        {
            long nowMs = sw.ElapsedMilliseconds;

            // 0b. 納入執行期動態偵測之 Peer-Reflexive (prflx) Pair，確保同樣具備重送與逾時結算
            while (true)
            {
                CandidatePair? dynPair = null;
                lock (_stateLock)
                {
                    if (_dynamicSchedulePairs.Count > 0) dynPair = _dynamicSchedulePairs.Dequeue();
                }
                if (dynPair == null) break;
                if (!nextSendDueMs.ContainsKey(dynPair))
                {
                    activeSchedule.Add(dynPair);
                    nextSendDueMs[dynPair] = nowMs;
                }
            }

            // 0. 增量遠端候選端點輪詢 (Phase 4 PAC 與對端非同步重試同步)：動態加入新收到的遠端 Candidate
            if (!isFallbackPass &&
                _options.IncrementalRemoteCandidatePollProvider != null &&
                (nowMs - lastIncrementalPollMs) >= Math.Max(40, _options.IncrementalPollIntervalMs))
            {
                lastIncrementalPollMs = nowMs;
                try
                {
                    var polledRemotes = await _options.IncrementalRemoteCandidatePollProvider(ct).ConfigureAwait(false);
                    var brandNew = new List<IceCandidate>();
                    foreach (var rc in polledRemotes)
                    {
                        string rk = $"{rc.CandidateId}|{rc.Ip}:{rc.Port}";
                        if (knownRemoteIds.Add(rk))
                        {
                            brandNew.Add(rc);
                        }
                    }

                    if (brandNew.Count > 0)
                    {
                        var extraPlan = CandidatePairBuilder.BuildPairs(
                            _gathering,
                            brandNew,
                            _remoteDeviceId,
                            _currentRole,
                            _options,
                            includePortPredictionFallback: false);
                        var addedPairs = RegisterPairs(extraPlan.CheckSchedulePairs);
                        if (addedPairs.Count > 0)
                        {
                            long baseNowMs = sw.ElapsedMilliseconds;
                            for (int idx = 0; idx < addedPairs.Count; idx++)
                            {
                                var np = addedPairs[idx];
                                activeSchedule.Add(np);
                                nextSendDueMs[np] = baseNowMs + (long)idx * Math.Max(0, _options.PacingIntervalMs);
                            }
                            Log.Info($"[ICE/Pair] Attempt={_connectionAttemptId}, Session={_sessionId} 動態加入 {addedPairs.Count} 組新遠端 Candidate Pair 進入檢查");
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            }

            // 1. 優先處理 Triggered Check 佇列（收到對端 CheckRequest/Nominate 時即時回覆檢查）
            while (true)
            {
                CandidatePair? trig = null;
                lock (_stateLock)
                {
                    if (_triggeredQueue.Count > 0) trig = _triggeredQueue.Dequeue();
                }
                if (trig == null) break;

                if (trig.CheckState != CandidatePairCheckState.Succeeded &&
                    trig.CheckState != CandidatePairCheckState.Invalidated &&
                    trig.CheckState != CandidatePairCheckState.InvalidResponse)
                {
                    await SendCheckRequestAsync(trig, isTriggered: true, ct).ConfigureAwait(false);
                }
            }

            // 2. 控制端選定決策 (3-5：依 PairPriority 選定，而非只選最快回來的)
            if (GetCurrentRole() == IceRole.Controlling)
            {
                CandidatePair? nomTarget = null;
                bool waitMoreHigherPairs = false;

                lock (_stateLock)
                {
                    if (_activeNominationPair != null)
                    {
                        if (_activeNominationPair.CheckState == CandidatePairCheckState.Invalidated)
                        {
                            _activeNominationPair = null;
                        }
                        else if (nominationStartMs >= 0 && (nowMs - nominationStartMs) >= _options.NominationTimeoutMs)
                        {
                            _activeNominationPair.CheckState = CandidatePairCheckState.TimedOut;
                            _activeNominationPair.FailureReason = CandidatePairFailureReason.Timeout;
                            _activeNominationPair.FailureDetail = "Nomination ACK timeout";
                            _activeNominationPair = null;
                        }
                        else
                        {
                            nomTarget = _activeNominationPair;
                        }
                    }

                    if (nomTarget == null)
                    {
                        var bestSucceeded = _allPairs
                            .Where(p => p.CheckState == CandidatePairCheckState.Succeeded)
                            .OrderByDescending(p => p.PairPriority)
                            .ThenBy(p => p.RttMs ?? double.MaxValue)
                            .FirstOrDefault();

                        if (bestSucceeded != null)
                        {
                            bool hasHigherActivePair = _allPairs.Any(p =>
                                p.PairPriority > bestSucceeded.PairPriority &&
                                p.CheckState is CandidatePairCheckState.Unchecked
                                    or CandidatePairCheckState.InProgress
                                    or CandidatePairCheckState.OneWayReceived);

                            if (!hasHigherActivePair)
                            {
                                _activeNominationPair = bestSucceeded;
                                nomTarget = bestSucceeded;
                                nominationStartMs = nowMs;
                            }
                            else
                            {
                                if (firstLowerPrioritySuccessSwMs < 0) firstLowerPrioritySuccessSwMs = nowMs;
                                if ((nowMs - firstLowerPrioritySuccessSwMs) >= Math.Max(0, _options.SelectionGraceWindowMs))
                                {
                                    _activeNominationPair = bestSucceeded;
                                    nomTarget = bestSucceeded;
                                    nominationStartMs = nowMs;
                                }
                                else
                                {
                                    waitMoreHigherPairs = true;
                                }
                            }
                        }
                    }
                }

                if (nomTarget != null && (nowMs - lastNominationSendMs) >= 45)
                {
                    lastNominationSendMs = nowMs;
                    await SendNominateRequestAsync(nomTarget, ct).ConfigureAwait(false);
                }

                if (!waitMoreHigherPairs && nomTarget != null && _selectedPair != null && GetCurrentRole() == IceRole.Controlling) break;
            }

            // 3. 排程送出尚未完成的 CheckRequest 重試
            long nextDueMs = long.MaxValue;
            bool allFinishedOrMaxed = true;

            for (int si = 0; si < activeSchedule.Count; si++)
            {
                var pair = activeSchedule[si];
                if (ct.IsCancellationRequested || ShouldExitSelectionLoop(out _)) break;

                CandidatePairCheckState st;
                int attempts;
                lock (_stateLock)
                {
                    st = pair.CheckState;
                    attempts = pair.CheckAttempts;
                }

                if (st is CandidatePairCheckState.Succeeded or CandidatePairCheckState.Invalidated or CandidatePairCheckState.InvalidResponse or CandidatePairCheckState.TimedOut)
                    continue;

                if (attempts >= maxAttempts)
                {
                    // 等待最後一次嘗試的 RTT 逾時後正式結算該 Pair
                    long lastTryWait = retryIntervals[^1];
                    long finalExpireMs = nextSendDueMs[pair] + lastTryWait;
                    if (nowMs >= finalExpireMs)
                    {
                        MarkPairExhausted(pair);
                    }
                    else
                    {
                        allFinishedOrMaxed = false;
                        nextDueMs = Math.Min(nextDueMs, finalExpireMs);
                    }
                    continue;
                }

                allFinishedOrMaxed = false;
                long due = nextSendDueMs[pair];
                if (nowMs >= due)
                {
                    await SendCheckRequestAsync(pair, isTriggered: false, ct).ConfigureAwait(false);
                    int intervalIdx = Math.Min(attempts, retryIntervals.Count - 1);
                    nextSendDueMs[pair] = sw.ElapsedMilliseconds + retryIntervals[intervalIdx];
                    nextDueMs = Math.Min(nextDueMs, nextSendDueMs[pair]);
                }
                else
                {
                    nextDueMs = Math.Min(nextDueMs, due);
                }
            }

            if (ShouldExitSelectionLoop(out long controlledHoldRemainingMs)) break;

            // Phase 4 PAC (延遲失敗判定)：
            // 僅當「所有既有 Pair 均完成且無 Nominate 待定」+「對端本輪候選交換已完成」+「已達最小 PAC 觀察視窗」才可提早結束。
            bool hasAnySucceededPair;
            bool hasRemoteNomPending;
            lock (_stateLock)
            {
                hasAnySucceededPair = _allPairs.Any(p => p.CheckState == CandidatePairCheckState.Succeeded);
                hasRemoteNomPending = _pendingRemoteNomination != null;
            }
            bool hasNominationInFlight = GetCurrentRole() == IceRole.Controlling && _activeNominationPair != null;
            bool isRemoteExchangeDone = isFallbackPass || (_options.IsRemoteExchangeCompleteProvider?.Invoke() ?? true);
            bool pacWaitSatisfied = isFallbackPass || sw.ElapsedMilliseconds >= Math.Max(0, _options.PacMinimumCheckWaitMs);

            if (allFinishedOrMaxed && _selectedPair == null && !hasAnySucceededPair && !hasNominationInFlight && !hasRemoteNomPending && isRemoteExchangeDone && pacWaitSatisfied)
            {
                break;
            }

            long sleepMs = 30;
            if (nextDueMs != long.MaxValue)
            {
                sleepMs = Math.Clamp(nextDueMs - sw.ElapsedMilliseconds, 5, 40);
            }
            if (hasNominationInFlight || hasRemoteNomPending || firstLowerPrioritySuccessSwMs >= 0 || hasAnySucceededPair)
            {
                sleepMs = Math.Min(sleepMs, 15);
            }
            if (_selectedPair != null && controlledHoldRemainingMs > 0)
            {
                sleepMs = Math.Min(sleepMs, Math.Clamp(controlledHoldRemainingMs, 5, 25));
            }
            if (!isRemoteExchangeDone && _options.IncrementalRemoteCandidatePollProvider != null)
            {
                sleepMs = Math.Min(sleepMs, Math.Max(20, _options.IncrementalPollIntervalMs));
            }

            try
            {
                await _wakeSignal.WaitAsync((int)sleepMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendCheckRequestAsync(CandidatePair pair, bool isTriggered, CancellationToken ct)
    {
        var binding = _gathering.Bindings.FirstOrDefault(b => string.Equals(b.SocketId, pair.SocketId, StringComparison.OrdinalIgnoreCase));
        if (binding == null || binding.IsDisposed || !CandidatePairBuilder.IsSocketAlive(binding.Socket))
        {
            InvalidatePairsForSocket(pair.SocketId, CandidatePairFailureReason.LocalSocketDisposed, "Socket unavailable on send");
            return;
        }

        byte[] txnId = IceCheckCodec.CreateTransactionId();
        string txnHex = Convert.ToHexString(txnId);
        int attemptNum;
        IceRole currentRole;

        lock (_stateLock)
        {
            if ((_selectedPair != null && !(_currentRole == IceRole.Controlled && !ReferenceEquals(_selectedPair, pair))) ||
                pair.CheckState == CandidatePairCheckState.Invalidated)
            {
                return;
            }

            if (!isTriggered)
                pair.CheckAttempts++;
            else if (pair.CheckAttempts == 0)
                pair.CheckAttempts = 1;

            attemptNum = pair.CheckAttempts;
            pair.LastCheckTimestamp = _utcNow();
            if (pair.CheckState == CandidatePairCheckState.Unchecked)
            {
                pair.CheckState = CandidatePairCheckState.InProgress;
            }

            _pendingTxns[txnHex] = new PendingTransaction(pair, binding.SocketId, attemptNum, Stopwatch.GetTimestamp(), IsNomination: false);
            currentRole = _currentRole;
        }

        Interlocked.Increment(ref _totalCheckAttempts);

        byte[] pkt = IceCheckCodec.Serialize(
            IceCheckMessageType.CheckRequest,
            isControlling: currentRole == IceRole.Controlling,
            useCandidate: false,
            roleConflictRejected: false,
            attemptNumber: (byte)Math.Clamp(attemptNum, 1, 255),
            transactionId: txnId,
            tieBreaker: _tieBreaker,
            priority: pair.PairPriority,
            observedEndPoint: null,
            sessionId: _sessionId,
            senderDeviceId: _selfDeviceId,
            receiverDeviceId: _remoteDeviceId,
            localCandidateId: pair.LocalCandidateId,
            remoteCandidateId: pair.RemoteCandidateId,
            pairId: pair.PairId);

        try
        {
            await binding.Socket.SendToAsync(pkt, SocketFlags.None, pair.RemoteEndPoint, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException)
        {
            InvalidatePairsForSocket(pair.SocketId, CandidatePairFailureReason.LocalSocketDisposed, "Socket disposed during SendToAsync");
        }
        catch (SocketException sex)
        {
            if (IsFatalSocketError(sex.SocketErrorCode))
            {
                bool disposedNow = binding.IsDisposed || !CandidatePairBuilder.IsSocketAlive(binding.Socket);
                InvalidatePairsForSocket(
                    pair.SocketId,
                    disposedNow ? CandidatePairFailureReason.LocalSocketDisposed : CandidatePairFailureReason.SocketError,
                    disposedNow ? $"Socket disposed on send ({sex.SocketErrorCode})" : $"Send failed: {sex.SocketErrorCode}");
            }
        }
        catch { }
    }

    private async Task SendNominateRequestAsync(CandidatePair pair, CancellationToken ct)
    {
        var binding = _gathering.Bindings.FirstOrDefault(b => string.Equals(b.SocketId, pair.SocketId, StringComparison.OrdinalIgnoreCase));
        if (binding == null || binding.IsDisposed || !CandidatePairBuilder.IsSocketAlive(binding.Socket))
        {
            InvalidatePairsForSocket(pair.SocketId, CandidatePairFailureReason.LocalSocketDisposed, "Socket disposed before nomination");
            return;
        }

        byte[] txnId = IceCheckCodec.CreateTransactionId();
        string txnHex = Convert.ToHexString(txnId);

        lock (_stateLock)
        {
            if (_selectedPair != null || _currentRole != IceRole.Controlling) return;
            pair.IsNominated = true;
            _pendingTxns[txnHex] = new PendingTransaction(pair, binding.SocketId, pair.CheckAttempts, Stopwatch.GetTimestamp(), IsNomination: true);
        }

        byte[] pkt = IceCheckCodec.Serialize(
            IceCheckMessageType.NominateRequest,
            isControlling: true,
            useCandidate: true,
            roleConflictRejected: false,
            attemptNumber: (byte)Math.Clamp(pair.CheckAttempts, 1, 255),
            transactionId: txnId,
            tieBreaker: _tieBreaker,
            priority: pair.PairPriority,
            observedEndPoint: null,
            sessionId: _sessionId,
            senderDeviceId: _selfDeviceId,
            receiverDeviceId: _remoteDeviceId,
            localCandidateId: pair.LocalCandidateId,
            remoteCandidateId: pair.RemoteCandidateId,
            pairId: pair.PairId);

        try
        {
            await binding.Socket.SendToAsync(pkt, SocketFlags.None, pair.RemoteEndPoint, ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            InvalidatePairsForSocket(pair.SocketId, CandidatePairFailureReason.LocalSocketDisposed, "Socket disposed during nominate send");
        }
        catch { }
    }

    private async Task SendNominateAckBurstAsync(GatheredSocketBinding binding, IPEndPoint targetEp, byte[] reqTxnId, CandidatePair pair)
    {
        byte[] ack = IceCheckCodec.Serialize(
            IceCheckMessageType.NominateAck,
            isControlling: false,
            useCandidate: true,
            roleConflictRejected: false,
            attemptNumber: (byte)Math.Clamp(pair.CheckAttempts, 1, 255),
            transactionId: reqTxnId,
            tieBreaker: _tieBreaker,
            priority: pair.PairPriority,
            observedEndPoint: targetEp,
            sessionId: _sessionId,
            senderDeviceId: _selfDeviceId,
            receiverDeviceId: _remoteDeviceId,
            localCandidateId: pair.LocalCandidateId,
            remoteCandidateId: pair.RemoteCandidateId,
            pairId: pair.PairId);

        for (int i = 0; i < 3; i++)
        {
            try
            {
                await binding.Socket.SendToAsync(ack, SocketFlags.None, targetEp, CancellationToken.None).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private bool ShouldExitSelectionLoop(out long remainingControlledHoldMs)
    {
        remainingControlledHoldMs = 0;
        if (_peerStreamStarted) return true;

        bool hasPendingNom;
        int viablePairCount;
        long firstTick;
        long switchTick;
        lock (_stateLock)
        {
            if (_selectedPair == null) return false;
            if (_currentRole != IceRole.Controlled) return true;

            hasPendingNom = _pendingRemoteNomination != null;
            viablePairCount = _allPairs.Count(p => p.CheckState != CandidatePairCheckState.Invalidated);
            firstTick = _controlledFirstSelectTick;
            switchTick = _controlledLastSwitchTick;
        }

        if (viablePairCount <= 1 && !hasPendingNom) return true;

        long nomWaitMs = Math.Clamp(_options.NominationTimeoutMs + 220, 260, 800);
        bool hasSwitched = switchTick > firstTick;
        long anchorTick = hasSwitched ? switchTick : firstTick;
        long elapsedMs = anchorTick > 0
            ? (long)Stopwatch.GetElapsedTime(anchorTick).TotalMilliseconds
            : 0;
        long targetHoldMs = hasSwitched && !hasPendingNom ? Math.Min(180, nomWaitMs) : nomWaitMs;
        if (hasPendingNom)
        {
            targetHoldMs = Math.Max(targetHoldMs, nomWaitMs);
        }

        remainingControlledHoldMs = Math.Max(0, targetHoldMs - elapsedMs);
        return elapsedMs >= targetHoldMs;
    }

    private CandidatePair? FindOrRegisterInboundPairLocked(
        GatheredSocketBinding binding,
        IPEndPoint fromEp,
        string expectedLocalCandidateId,
        string expectedRemoteCandidateId,
        bool allowPeerReflexive,
        out bool newlyRegisteredPrflx)
    {
        newlyRegisteredPrflx = false;
        if (!_pairsBySocket.TryGetValue(binding.SocketId, out var list) || list.Count == 0)
            return null;

        // 1. 精確匹配 CandidateId 與來源 EndPoint
        var exact = list.FirstOrDefault(p =>
            p.RemoteEndPoint.Equals(fromEp) &&
            (string.Equals(p.LocalCandidateId, expectedLocalCandidateId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.RemoteCandidateId, expectedRemoteCandidateId, StringComparison.OrdinalIgnoreCase)));
        if (exact != null) return exact;

        // 2. 退而匹配同一 Socket + 來源 EndPoint
        var byEp = list.FirstOrDefault(p => p.RemoteEndPoint.Equals(fromEp));
        if (byEp != null) return byEp;

        // 3. Peer-Reflexive (prflx)：已通過 SessionId 與雙方 DeviceId 驗證，但對端經 NAT 轉換使用新映射端點 fromEp
        if (!allowPeerReflexive || _dynamicPrflxCount >= 12)
            return null;

        string pairKey = $"{binding.SocketId}|{fromEp}";
        if (_registeredPairKeys.Contains(pairKey))
            return null;

        var templatePair =
            list.FirstOrDefault(p =>
                p.CheckState != CandidatePairCheckState.Invalidated &&
                p.RemoteEndPoint.Address.Equals(fromEp.Address) &&
                (string.Equals(p.LocalCandidateId, expectedLocalCandidateId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(p.RemoteCandidateId, expectedRemoteCandidateId, StringComparison.OrdinalIgnoreCase)))
            ?? list.FirstOrDefault(p =>
                p.CheckState != CandidatePairCheckState.Invalidated &&
                p.RemoteEndPoint.Address.Equals(fromEp.Address));

        if (templatePair == null)
            return null;

        string prflxRemoteId = !string.IsNullOrWhiteSpace(expectedRemoteCandidateId)
            ? $"{expectedRemoteCandidateId}#prflx-{fromEp.Port}"
            : $"{templatePair.RemoteCandidateId}#prflx-{fromEp.Port}";

        var prflxRemoteCand = new IceCandidate
        {
            CandidateId = prflxRemoteId,
            Type = templatePair.RemoteCandidate.Type == IceCandidateType.Host ? IceCandidateType.Srflx : templatePair.RemoteCandidate.Type,
            AddressFamily = templatePair.RemoteCandidate.AddressFamily,
            Protocol = templatePair.RemoteCandidate.Protocol,
            Ip = fromEp.Address.ToString(),
            Port = fromEp.Port,
            LocalAddress = templatePair.RemoteCandidate.LocalAddress,
            LocalPort = templatePair.RemoteCandidate.LocalPort,
            PublicAddress = fromEp.Address.ToString(),
            PublicPort = fromEp.Port,
            Priority = templatePair.RemoteCandidate.Priority,
            InterfaceId = templatePair.RemoteCandidate.InterfaceId,
            SocketId = templatePair.RemoteCandidate.SocketId,
            StunServer = templatePair.RemoteCandidate.StunServer,
            DiscoveryTimestamp = DateTime.UtcNow,
            ValidityStatus = CandidateValidityStatus.Valid,
            Ufrag = templatePair.RemoteCandidate.Ufrag
        };

        var prflxPair = new CandidatePair
        {
            PairId = $"{templatePair.PairId}#prflx-{fromEp.Port}",
            InterfaceId = templatePair.InterfaceId,
            SocketId = templatePair.SocketId,
            LocalCandidateId = templatePair.LocalCandidateId,
            RemoteCandidateId = prflxRemoteCand.CandidateId,
            LocalCandidate = templatePair.LocalCandidate,
            RemoteCandidate = prflxRemoteCand,
            LocalEndPoint = templatePair.LocalEndPoint,
            RemoteEndPoint = fromEp,
            PairPriority = templatePair.PairPriority,
            IsPortPredictionFallback = false
        };

        _registeredPairKeys.Add(pairKey);
        _allPairs.Add(prflxPair);
        list.Add(prflxPair);
        _dynamicPrflxCount++;
        _dynamicSchedulePairs.Enqueue(prflxPair);
        _triggeredQueue.Enqueue(prflxPair);
        newlyRegisteredPrflx = true;

        Log.Info($"[ICE/Pair] Attempt={_connectionAttemptId}, Session={_sessionId} 動態建立 Peer-Reflexive Pair: PairId={prflxPair.PairId}, Local={prflxPair.LocalEndPoint}, PrflxRemote={fromEp}");
        return prflxPair;
    }

    private void MarkPairExhausted(CandidatePair pair)
    {
        bool changed = false;
        lock (_stateLock)
        {
            if (pair.CheckState is CandidatePairCheckState.Succeeded
                or CandidatePairCheckState.Invalidated
                or CandidatePairCheckState.InvalidResponse
                or CandidatePairCheckState.TimedOut)
            {
                return;
            }

            if (pair.InboundPacketObserved && !pair.OutboundCheckAcked)
            {
                pair.CheckState = CandidatePairCheckState.OneWayReceived;
                pair.FailureReason = CandidatePairFailureReason.OneWayOnly;
                pair.FailureDetail = "Inbound CheckRequest observed, but outbound CheckResponse never arrived";
            }
            else
            {
                pair.CheckState = CandidatePairCheckState.TimedOut;
                pair.FailureReason = CandidatePairFailureReason.Timeout;
                pair.FailureDetail = $"No CheckResponse after {pair.CheckAttempts} attempts";
            }
            changed = true;
        }

        if (changed)
        {
            Log.Info($"[ICE/Pair] Attempt={_connectionAttemptId}, Session={_sessionId}, PairId={pair.PairId}, LocalCand={pair.LocalCandidateId}, RemoteCand={pair.RemoteCandidateId}, Attempt={pair.CheckAttempts}, Ts={_utcNow():O}, RTT=-ms, Result={pair.CheckState}, FailureReason={pair.FailureReason}");
        }
    }

    private void InvalidatePairsForSocket(string socketId, CandidatePairFailureReason reason, string detail)
    {
        List<CandidatePair>? impacted = null;
        lock (_stateLock)
        {
            if (!_pairsBySocket.TryGetValue(socketId, out var list)) return;
            foreach (var p in list)
            {
                if (ReferenceEquals(p, _selectedPair)) continue;
                if (p.CheckState != CandidatePairCheckState.Invalidated)
                {
                    p.CheckState = CandidatePairCheckState.Invalidated;
                    p.FailureReason = reason;
                    p.FailureDetail = detail;
                    impacted ??= new List<CandidatePair>();
                    impacted.Add(p);
                }
            }
        }

        if (impacted != null)
        {
            foreach (var p in impacted)
            {
                Log.Warn($"[ICE/Pair] Attempt={_connectionAttemptId}, Session={_sessionId}, PairId={p.PairId}, LocalCand={p.LocalCandidateId}, RemoteCand={p.RemoteCandidateId}, Result=Invalidated, FailureReason={reason} ({detail})");
            }
        }
    }

    private void FinalizeRemainingPairStates(bool outerCancelled)
    {
        lock (_stateLock)
        {
            foreach (var p in _allPairs)
            {
                if (ReferenceEquals(p, _selectedPair)) continue;

                if (p.CheckState == CandidatePairCheckState.Succeeded)
                {
                    if (_selectedPair != null && p.FailureReason == CandidatePairFailureReason.None)
                    {
                        p.FailureReason = CandidatePairFailureReason.SupersededByHigherPriorityPair;
                        p.FailureDetail = $"Selected {_selectedPair.PairId}";
                    }
                    continue;
                }

                if (p.CheckState is CandidatePairCheckState.InProgress or CandidatePairCheckState.Unchecked or CandidatePairCheckState.OneWayReceived)
                {
                    if (p.InboundPacketObserved && !p.OutboundCheckAcked)
                    {
                        p.CheckState = CandidatePairCheckState.OneWayReceived;
                        p.FailureReason = CandidatePairFailureReason.OneWayOnly;
                        p.FailureDetail = "Inbound received only; outbound confirmation failed";
                    }
                    else if (_selectedPair != null)
                    {
                        p.FailureReason = CandidatePairFailureReason.SupersededByHigherPriorityPair;
                        p.FailureDetail = $"Stopped after selecting {_selectedPair.PairId}";
                    }
                    else if (outerCancelled)
                    {
                        p.FailureReason = CandidatePairFailureReason.Cancelled;
                        p.FailureDetail = "Cancelled";
                    }
                    else
                    {
                        p.CheckState = CandidatePairCheckState.TimedOut;
                        p.FailureReason = CandidatePairFailureReason.Timeout;
                        p.FailureDetail = $"Timed out after {p.CheckAttempts} attempts";
                    }
                }
            }
        }
    }

    private List<CandidatePair> GetAllPairsSnapshot()
    {
        lock (_stateLock) { return _allPairs.ToList(); }
    }

    private IceRole GetCurrentRole()
    {
        lock (_stateLock) { return _currentRole; }
    }

    private void WakeScheduler()
    {
        try { _wakeSignal.Release(); } catch { }
    }

    private static string ClassifyOverallFailure(IReadOnlyList<CandidatePair> pairs, bool cancelled)
    {
        if (cancelled) return "Cancelled";
        if (pairs.Count == 0) return "NoValidCandidatePairs";
        if (pairs.Any(p => p.CheckState == CandidatePairCheckState.OneWayReceived)) return "OneWayConnectivity";
        if (pairs.All(p => p.CheckState == CandidatePairCheckState.Invalidated)) return "SocketFailure";
        if (pairs.Any(p => p.CheckState == CandidatePairCheckState.InvalidResponse)) return "AllCandidatePairsFailed";
        if (pairs.Count > 1 && pairs.Select(p => p.FailureReason).Distinct().Count() > 1) return "AllCandidatePairsFailed";
        return "CandidatePairCheckTimeout";
    }

    private static int CompareAuthority(ulong peerTie, string peerDeviceId, ulong localTie, string localDeviceId)
    {
        int c = peerTie.CompareTo(localTie);
        return c != 0 ? c : string.CompareOrdinal(peerDeviceId, localDeviceId);
    }

    private static ulong BuildDefaultTieBreaker()
    {
        byte[] b = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(b);
    }

    private static bool IsFatalSocketError(SocketError err) =>
        err is SocketError.NetworkDown
            or SocketError.NotConnected
            or SocketError.Shutdown
            or SocketError.InvalidArgument
            or SocketError.OperationAborted
            or SocketError.Interrupted;
}
