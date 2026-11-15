using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using RemoteDesk.Common;

namespace RemoteDesk.Net;

/// <summary>
/// 基於 UDP 的可靠串流傳輸層 (RFC-inspired ARQ Reliable UDP Stream)。
/// 具備分框、MTU 安全切片 (< 1200 bytes)、滑動視窗累積確認、封包遺失自動重傳 (ARQ)、
/// 亂序重組、以及 NAT 穿透保活 (Keepalive Ping/Pong)。
/// 完全實作 System.IO.Stream，可無縫銜接 SecureChannel 進行端對端 ECDH + AES-256-GCM 加密。
/// </summary>
internal sealed class ReliableUdpStream : Stream
{
    public const uint Magic = 0x5244534B; // ASCII "RDSK"
    public const int MaxPayloadSize = 1200; // < MTU 1400/1500，防止 IP 分片遺失
    public const int HeaderSize = 16;
    public const int MaxWindowSize = 256; // 滑動視窗上限 (256 * 1200 bytes ≈ 307 KB，徹底杜絕 1.2MB 緩衝膨脹與佇列延遲累積)
    private const int PacingBurstBytes = 24_576;                      // ~20 個封包突發額度，避免小影格產生多餘等待
    private const double HealthyPaceBytesPerSec = 8_000_000;          // 健康基準平滑步調 (~64 Mbps)，兼顧路由器佇列保護
    private const double MaxAdaptiveHealthyPaceBytesPerSec = 125_000_000; // 自適應解鎖上限 (~1 Gbps)，避免高頻寬網路 (100/500 Mbps) 受固定上限扼殺
    private const double MaxHealthyBurstBytes = 262_144.0;            // 單次最大突發額度 (~213 packets < MaxWindowSize)，確保 Windows 15.6ms 計時粒度下仍可達到全速
    private const double LossyPaceBytesPerSec = 4_500_000;            // 偵測近期封包遺失時暫時降載步調 (~36 Mbps)

    public const byte TypeData = 0x01;
    public const byte TypeAck = 0x02;
    public const byte TypePing = 0x03;
    public const byte TypePong = 0x04;
    public const byte TypeClose = 0x05;

    private Socket _socket;
    private bool _ownsSocket;
    private EndPoint _remoteEndPoint;
    private IceStreamPostPunchContext? _postPunchContext;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _pathControlLock = new(1, 1);
    private readonly object _recvCoordLock = new();
    private CancellationTokenSource _recvEpochCts;
    private bool _recvPauseRequested;
    private TaskCompletionSource _recvEnteredPauseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _recvResumeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _pathEpoch;
    private long _rejectedStalePathPackets;
    private int _consecutiveSendSocketErrors;
    private int _consecutiveRecvSocketErrors;
    private int _lastSocketErrorCode;
    private bool _fatalSocketErrorOccurred;
    private bool _remoteCloseReceived;
    private long _lastSuccessfulSendTicks = DateTime.UtcNow.Ticks;
    private long _lastSuccessfulRecvTicks = DateTime.UtcNow.Ticks;
    private long _lastHeartbeatSentTicks;
    private long _lastHeartbeatAckTicks;
    private double _lastHeartbeatRttMs;
    private uint _nextHeartbeatProbeSeq;
    private uint _pendingHeartbeatProbeSeq;
    private long _pendingHeartbeatSentTs;
    private TaskCompletionSource<double?>? _pendingHeartbeatTcs;
    private readonly object _heartbeatLock = new();

    // 傳送端狀態
    private uint _nextSendSeq = 1;
    private readonly ConcurrentDictionary<uint, SentPacketInfo> _inFlight = new();
    private readonly SemaphoreSlim _sendWindowSemaphore = new(MaxWindowSize, MaxWindowSize);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _lastSendTimeTicks = DateTime.UtcNow.Ticks;
    private uint _highestAckReceived = 1;
    private int _duplicateAckCount = 0;
    private int _rtoContiguousRun = 0;
    private long _cleanPacketsAckedSinceLoss;
    private int _pendingDataAckCount;
    private long _lastPeerContactTicks = DateTime.UtcNow.Ticks;

    // RTT 與 RTO 動態估算 (RFC 6298 Jacobson / Karels 算法)
    private double _srttMs = 40.0;
    private double _rttVarMs = 15.0;
    private int _rtoMs = 120; // 基礎 RTO 動態適應，杜絕固定 250ms 造成的尾端掉包 1/4 秒凍結卡頓

    // 診斷監控與遙測統計 (Telemetry & Diagnostics)
    private long _totalPacketsSent;
    private long _totalAcksReceived;
    private long _totalRetransmissions;
    private long _totalPacketLossCount;
    private long _uniquePacketsLost;
    private long _uniquePacketsAcked;
    private long _totalRtoTimeouts;
    private long _totalFastRetransmits;
    private long _totalPayloadBytesSent;
    private long _totalPayloadBytesAcked;
    private long _totalWireBytesSent;
    private long _totalWireBytesAcked;
    private long _totalUnrecoverableLost;
    private readonly object _bwLock = new();
    private long _burstFirstAckTs;
    private long _burstLastAckTs;
    private long _burstWireBytes;
    private int _burstPacketCount;
    private double _lastDeliverySampleMbps;
    private long _lastDeliverySampleTs;
    private readonly QueueWaitWindowTracker _semWaitTracker = new();
    private long _maxSemaphoreWaitMs;
    private long _lastFrameWriteDurationMs;
    private long _lastPacingWaitUs;
    private long _totalPacingWaitUs;
    private long _lastSocketWriteUs;
    private long _totalSocketWriteUs;
    private long _lastLossObservedTs;
    private double _dynamicHealthyPaceBps = HealthyPaceBytesPerSec;
    private double _pacingTokensBytes = PacingBurstBytes;
    private long _lastTokenRefillTs = System.Diagnostics.Stopwatch.GetTimestamp();

    // 接收端狀態
    private uint _nextExpectedRecvSeq = 1;
    private readonly ConcurrentDictionary<uint, byte[]> _outOfOrderBuffer = new();
    private readonly Channel<byte[]> _recvChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private byte[]? _currentReadChunk;
    private int _currentReadOffset;

    // 背景任務
    private readonly Task _recvLoopTask;
    private readonly Task _retransmitLoopTask;
    private readonly Task _keepAliveTask;
    private bool _disposed;

    private sealed class SentPacketInfo
    {
        private long _sentAtTicks;
        private int _retries;

        public byte[] Data { get; }
        public DateTime FirstSentAt { get; }
        public DateTime SentAt
        {
            get => new DateTime(Interlocked.Read(ref _sentAtTicks), DateTimeKind.Utc);
            set => Interlocked.Exchange(ref _sentAtTicks, value.Ticks);
        }
        public int Retries => Volatile.Read(ref _retries);
        public int CountedLost;

        public SentPacketInfo(byte[] data)
        {
            Data = data;
            FirstSentAt = DateTime.UtcNow;
            _sentAtTicks = FirstSentAt.Ticks;
            _retries = 0;
        }

        public int IncrementRetries() => Interlocked.Increment(ref _retries);
    }

    private static bool IsSeqBefore(uint a, uint b) => unchecked((int)(a - b)) < 0;

    public ReliableUdpStream(Socket socket, EndPoint remoteEndPoint, bool ownsSocket = false, IceStreamPostPunchContext? postPunchContext = null)
    {
        _socket = socket;
        _remoteEndPoint = remoteEndPoint;
        _ownsSocket = ownsSocket;
        _postPunchContext = postPunchContext;
        _recvEpochCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _recvResumeTcs.TrySetResult();

        ConfigureUdpSocket(_socket);

        _recvLoopTask = Task.Run(ReceiveLoopAsync);
        _retransmitLoopTask = Task.Run(RetransmitLoopAsync);
        _keepAliveTask = Task.Run(KeepAliveLoopAsync);
    }

    private static void ConfigureUdpSocket(Socket sock)
    {
        try { sock.SendBufferSize = 4 * 1024 * 1024; } catch { }
        try { sock.ReceiveBufferSize = 4 * 1024 * 1024; } catch { }

        if (OperatingSystem.IsWindows())
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            try { sock.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null); } catch { }
        }
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public EndPoint RemoteEndPoint => Volatile.Read(ref _remoteEndPoint);
    public Socket ActiveSocket { get { lock (_recvCoordLock) return _socket; } }
    public EndPoint? LocalEndPoint { get { try { return ActiveSocket.LocalEndPoint; } catch { return null; } } }
    public uint PathEpoch => Volatile.Read(ref _pathEpoch);
    public long RejectedStalePathPackets => Interlocked.Read(ref _rejectedStalePathPackets);
    public bool RemoteCloseReceived => Volatile.Read(ref _remoteCloseReceived);
    public bool IsDisposed => Volatile.Read(ref _disposed);
    public bool SuppressDeadDisconnectDuringRecovery { get; set; }
    public bool SuppressInternalKeepAlive { get; set; }
    public bool AllowAlternatePairCheckResponses { get; set; }
    internal Func<IPEndPoint, ReadOnlyMemory<byte>, bool>? ExternalDatagramInterceptor { get; set; }
    internal event Action<IPEndPoint, IceCheckMessage>? PeerNominatedSwitchObserved;

    public int ConsecutiveSendSocketErrors => Volatile.Read(ref _consecutiveSendSocketErrors);
    public int ConsecutiveRecvSocketErrors => Volatile.Read(ref _consecutiveRecvSocketErrors);
    public int ConsecutiveSocketErrors => Math.Max(ConsecutiveSendSocketErrors, ConsecutiveRecvSocketErrors);
    public bool FatalSocketErrorOccurred => Volatile.Read(ref _fatalSocketErrorOccurred);
    public SocketError? LastSocketError
    {
        get
        {
            int code = Volatile.Read(ref _lastSocketErrorCode);
            return code != 0 ? (SocketError)code : null;
        }
    }
    public DateTime LastSuccessfulSendTimestampUtc => new(Interlocked.Read(ref _lastSuccessfulSendTicks), DateTimeKind.Utc);
    public DateTime LastSuccessfulReceiveTimestampUtc => new(Interlocked.Read(ref _lastSuccessfulRecvTicks), DateTimeKind.Utc);
    public DateTime LastHeartbeatSentTimestampUtc => new(Interlocked.Read(ref _lastHeartbeatSentTicks), DateTimeKind.Utc);
    public DateTime LastHeartbeatAckTimestampUtc => new(Interlocked.Read(ref _lastHeartbeatAckTicks), DateTimeKind.Utc);
    public double LastMeasuredHeartbeatRttMs => Volatile.Read(ref _lastHeartbeatRttMs);
    public bool IsUnderlyingSocketAlive
    {
        get
        {
            try
            {
                var sock = ActiveSocket;
                if (sock == null || sock.SafeHandle == null || sock.SafeHandle.IsInvalid || sock.SafeHandle.IsClosed)
                    return false;
                _ = sock.LocalEndPoint;
                return true;
            }
            catch { return false; }
        }
    }

    private static byte EncodePathTag(uint epoch) =>
        epoch == 0 ? (byte)0 : (byte)(1 + ((epoch - 1) % 255));

    private byte CurrentPathTag() => EncodePathTag(Volatile.Read(ref _pathEpoch));

    private static bool EndPointMatches(EndPoint incoming, EndPoint expected)
    {
        if (incoming.Equals(expected)) return true;
        if (incoming is IPEndPoint inIp && expected is IPEndPoint exIp)
        {
            var a = inIp.Address.IsIPv4MappedToIPv6 ? inIp.Address.MapToIPv4() : inIp.Address;
            var b = exIp.Address.IsIPv4MappedToIPv6 ? exIp.Address.MapToIPv4() : exIp.Address;
            return inIp.Port == exIp.Port && a.Equals(b);
        }
        return false;
    }

    private static bool IsFatalUdpSocketError(SocketError err) =>
        err is SocketError.NetworkDown
            or SocketError.NotConnected
            or SocketError.Shutdown
            or SocketError.InvalidArgument
            or SocketError.OperationAborted
            or SocketError.Interrupted
            or SocketError.HostUnreachable
            or SocketError.NetworkUnreachable
            or SocketError.AddressNotAvailable;

    private (Socket Sock, EndPoint RemoteEp, byte PathTag) GetActiveTransportWithTag()
    {
        lock (_recvCoordLock)
        {
            return (_socket, _remoteEndPoint, EncodePathTag(_pathEpoch));
        }
    }

    private void RecordSocketSendSuccess()
    {
        Interlocked.Exchange(ref _consecutiveSendSocketErrors, 0);
        long nowTicks = DateTime.UtcNow.Ticks;
        Interlocked.Exchange(ref _lastSendTimeTicks, nowTicks);
        Interlocked.Exchange(ref _lastSuccessfulSendTicks, nowTicks);
    }

    private void RecordSocketSendError(Exception ex)
    {
        if (ex is SocketException sex)
        {
            Interlocked.Exchange(ref _lastSocketErrorCode, (int)sex.SocketErrorCode);
            Interlocked.Increment(ref _consecutiveSendSocketErrors);
            if (IsFatalUdpSocketError(sex.SocketErrorCode))
                Volatile.Write(ref _fatalSocketErrorOccurred, true);
        }
        else if (ex is ObjectDisposedException)
        {
            Interlocked.Increment(ref _consecutiveSendSocketErrors);
        }
    }

    private void RecordSocketRecvError(Exception ex)
    {
        if (ex is SocketException sex)
        {
            Interlocked.Exchange(ref _lastSocketErrorCode, (int)sex.SocketErrorCode);
            Interlocked.Increment(ref _consecutiveRecvSocketErrors);
            if (IsFatalUdpSocketError(sex.SocketErrorCode))
                Volatile.Write(ref _fatalSocketErrorOccurred, true);
        }
    }

    // 診斷監控指標 (Diagnostics & Telemetry)
    public int CurrentInflightPackets => _inFlight.Count;
    public int MaxInflightPackets => MaxWindowSize;
    public long TotalPacketsSent => Interlocked.Read(ref _totalPacketsSent);
    public long TotalAcksReceived => Interlocked.Read(ref _totalAcksReceived);
    public long TotalRetransmissions => Interlocked.Read(ref _totalRetransmissions);
    public long TotalPacketLossCount => Interlocked.Read(ref _totalPacketLossCount);
    public long UniquePacketsLost => Interlocked.Read(ref _uniquePacketsLost);
    public long UniquePacketsAcked => Interlocked.Read(ref _uniquePacketsAcked);
    public long TotalRtoTimeouts => Interlocked.Read(ref _totalRtoTimeouts);
    public long TotalFastRetransmits => Interlocked.Read(ref _totalFastRetransmits);
    public long TotalPayloadBytesSent => Interlocked.Read(ref _totalPayloadBytesSent);
    public long TotalPayloadBytesAcked => Interlocked.Read(ref _totalPayloadBytesAcked);
    public long TotalWireBytesSent => Interlocked.Read(ref _totalWireBytesSent);
    public long TotalWireBytesAcked => Interlocked.Read(ref _totalWireBytesAcked);
    public long TotalUnrecoverableLost => Interlocked.Read(ref _totalUnrecoverableLost);
    public const int UdpIpOverheadBytes = 28; // IPv4 (20B) + UDP (8B)

    public int GetAgedUnackedCount(int minRetries = 2)
    {
        int count = 0;
        foreach (var (_, info) in _inFlight)
        {
            if (info.Retries >= minRetries) count++;
        }
        return count;
    }

    /// <summary>
    /// 取得近期突發封包確認鏈的被動交付速率估測 (Mbps)，僅在實際發送突發且未超時逾時過期時回傳有效值；
    /// 與應用層 ACK Payload Goodput 完全分離，閒置或單封包時回傳 0。
    /// </summary>
    public float ConsumeDeliveryBandwidthSampleMbps(double staleMs = 1500.0)
    {
        long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_bwLock)
        {
            if (_lastDeliverySampleTs == 0) return 0f;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_lastDeliverySampleTs, nowTs).TotalMilliseconds > staleMs)
            {
                _lastDeliverySampleMbps = 0;
                _lastDeliverySampleTs = 0;
                return 0f;
            }
            return (float)_lastDeliverySampleMbps;
        }
    }
    public long LastSemaphoreWaitMs => (long)_semWaitTracker.GetFreshLastMs();
    public long MaxSemaphoreWaitMs => Interlocked.Read(ref _maxSemaphoreWaitMs);
    public long LastFrameWriteDurationMs => Interlocked.Read(ref _lastFrameWriteDurationMs);
    public float LastPacingWaitMs => (float)(Interlocked.Read(ref _lastPacingWaitUs) / 1000.0);
    public double TotalPacingWaitMs => Interlocked.Read(ref _totalPacingWaitUs) / 1000.0;
    public float LastSocketWriteDurationMs => (float)(Interlocked.Read(ref _lastSocketWriteUs) / 1000.0);
    public double TotalSocketWriteDurationMs => Interlocked.Read(ref _totalSocketWriteUs) / 1000.0;
    private bool IsRecentLossActive(out long observedTs)
    {
        observedTs = Interlocked.Read(ref _lastLossObservedTs);
        if (observedTs == 0) return false;
        double sinceLossMs = System.Diagnostics.Stopwatch.GetElapsedTime(observedTs).TotalMilliseconds;
        bool recoveredByCleanTraffic = sinceLossMs >= 800.0 && Interlocked.Read(ref _cleanPacketsAckedSinceLoss) >= 120;
        return sinceLossMs < 2000.0 && !recoveredByCleanTraffic;
    }

    private void RecordLossObserved()
    {
        Interlocked.Exchange(ref _cleanPacketsAckedSinceLoss, 0);
        Interlocked.Exchange(ref _lastLossObservedTs, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    public double CurrentEffectivePaceBytesPerSec =>
        IsRecentLossActive(out _) ? LossyPaceBytesPerSec : Volatile.Read(ref _dynamicHealthyPaceBps);
    public double CurrentTokenBucketBytes => Volatile.Read(ref _pacingTokensBytes);
    internal string LastRtoDiagnostic { get; private set; } = string.Empty;
    public QueueWaitWindowMetrics ConsumeWaitWindowSnapshot() => _semWaitTracker.ConsumeWindow();
    public int CurrentRtoMs => Volatile.Read(ref _rtoMs);
    public double CurrentSrttMs => Volatile.Read(ref _srttMs);
    public int PendingRetransmissionCount
    {
        get
        {
            var now = DateTime.UtcNow;
            int rto = Volatile.Read(ref _rtoMs);
            int pending = 0;
            foreach (var (_, info) in _inFlight)
            {
                int currentRto = Math.Min(2000, rto + (info.Retries * 100));
                if ((now - info.SentAt).TotalMilliseconds >= currentRto)
                {
                    pending++;
                }
            }
            return pending;
        }
    }

    // ---------------- 寫入 (Write / WriteAsync) ----------------

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty) return;

        using var linkedCts = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token)
            : null;
        CancellationToken opCt = linkedCts?.Token ?? _cts.Token;

        var frameSw = System.Diagnostics.Stopwatch.StartNew();
        await _writeLock.WaitAsync(opCt).ConfigureAwait(false);
        long writePacingUs = 0;
        long writeSocketUs = 0;
        try
        {
            int total = buffer.Length;
            int offset = 0;

            while (offset < total)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                opCt.ThrowIfCancellationRequested();

                long waitId = _semWaitTracker.BeginWait(out long semStartTs);
                bool semAcquired = false;
                float exactSemWaitMs;
                try
                {
                    await _sendWindowSemaphore.WaitAsync(opCt).ConfigureAwait(false);
                    exactSemWaitMs = _semWaitTracker.EndWait(waitId, semStartTs);
                    semAcquired = true;
                }
                finally
                {
                    if (!semAcquired) _semWaitTracker.CancelWait(waitId);
                }
                long semWaitMs = (long)exactSemWaitMs;

                if (semWaitMs > Interlocked.Read(ref _maxSemaphoreWaitMs))
                {
                    Interlocked.Exchange(ref _maxSemaphoreWaitMs, semWaitMs);
                }

                if (semWaitMs > 100)
                {
                    uint oldestSeq = 0;
                    int oldestRetries = 0;
                    foreach (var (k, v) in _inFlight)
                    {
                        if (oldestSeq == 0 || k < oldestSeq)
                        {
                            oldestSeq = k;
                            oldestRetries = v.Retries;
                        }
                    }
                    string severity = semWaitMs >= 1000 ? "嚴重阻塞 (>1000ms)" : (semWaitMs >= 500 ? "顯著延遲 (>500ms)" : "輕微等待 (>100ms)");
                    Log.Warn($"[ReliableUDP][視窗等待警告-{severity}] 等待耗時={semWaitMs}ms, Inflight={_inFlight.Count}/{MaxWindowSize}, " +
                             $"OldestInflightSeq={oldestSeq}(重試={oldestRetries}), HighestAck={Volatile.Read(ref _highestAckReceived)}, " +
                             $"RTO={_rtoMs}ms, SRTT={_srttMs:F1}ms, 待重傳數={PendingRetransmissionCount}, 累積掉包={TotalPacketLossCount}");
                }

                int chunkSize = Math.Min(total - offset, MaxPayloadSize);
                var chunk = buffer.Slice(offset, chunkSize);

                byte[] packet = new byte[HeaderSize + chunkSize];
                uint seq = _nextSendSeq++;
                uint ack = _nextExpectedRecvSeq;

                WriteHeader(packet.AsSpan(0, HeaderSize), TypeData, CurrentPathTag(), seq, ack, (ushort)chunkSize);
                chunk.CopyTo(packet.AsMemory(HeaderSize, chunkSize));

                // 跨呼叫連續權杖桶微步調 (Stateful Token-Bucket Micro-Pacing)：
                // 1. 嚴格維持 _pacingTokensBytes >= 0 且 <= maxBurst，無負額度與突發過量問題；
                // 2. 健康狀態若實測交付頻寬超過 52 Mbps，自動解鎖至最大 ~1 Gbps，不扼殺 100/500 Mbps 網路；
                // 3. 偵測近期遺失降載至 ~36 Mbps，連續 2 秒無新增遺失則自動清除標記恢復健康速率；
                // 4. PacingWaitMs 與 SocketWriteDurationMs、SemaphoreWaitMs 完全分開統計。
                bool recentLoss = IsRecentLossActive(out long lastLossTs);
                if (!recentLoss && lastLossTs != 0)
                {
                    Interlocked.CompareExchange(ref _lastLossObservedTs, 0, lastLossTs);
                }

                double healthyPaceBps = Volatile.Read(ref _dynamicHealthyPaceBps);
                if (recentLoss)
                {
                    healthyPaceBps = HealthyPaceBytesPerSec;
                    Volatile.Write(ref _dynamicHealthyPaceBps, healthyPaceBps);
                }
                else
                {
                    float measuredMbps = ConsumeDeliveryBandwidthSampleMbps();
                    if (measuredMbps >= 35.0f)
                    {
                        healthyPaceBps = Math.Clamp(
                            Math.Max(healthyPaceBps, measuredMbps * 125_000.0 * 2.0),
                            HealthyPaceBytesPerSec,
                            MaxAdaptiveHealthyPaceBytesPerSec);
                        Volatile.Write(ref _dynamicHealthyPaceBps, healthyPaceBps);
                    }
                }

                double paceBytesPerSec = recentLoss ? LossyPaceBytesPerSec : healthyPaceBps;
                double maxBurst = Math.Clamp(paceBytesPerSec * 0.018, PacingBurstBytes, MaxHealthyBurstBytes);
                long tokenNowTs = System.Diagnostics.Stopwatch.GetTimestamp();
                double elapsedSec = Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(_lastTokenRefillTs, tokenNowTs).TotalSeconds, 0.0, 1.0);
                _lastTokenRefillTs = tokenNowTs;
                double tokens = Math.Clamp(Volatile.Read(ref _pacingTokensBytes) + (elapsedSec * paceBytesPerSec), 0.0, maxBurst);

                if (tokens < packet.Length)
                {
                    if (!recentLoss && _inFlight.Count < 48 && Volatile.Read(ref _rtoContiguousRun) == 0)
                    {
                        healthyPaceBps = Math.Min(MaxAdaptiveHealthyPaceBytesPerSec, healthyPaceBps * 1.35);
                        Volatile.Write(ref _dynamicHealthyPaceBps, healthyPaceBps);
                        paceBytesPerSec = healthyPaceBps;
                        maxBurst = Math.Clamp(paceBytesPerSec * 0.018, PacingBurstBytes, MaxHealthyBurstBytes);
                    }

                    double targetTokens = Math.Min(maxBurst, Math.Max(packet.Length, paceBytesPerSec * 0.004));
                    double neededBytes = Math.Max(0.0, targetTokens - tokens);
                    double neededMs = (neededBytes * 1000.0) / paceBytesPerSec;
                    if (neededMs >= 1.0)
                    {
                        int sleepMs = Math.Clamp((int)Math.Ceiling(neededMs), 1, 8);
                        long paceStartTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        await Task.Delay(sleepMs, opCt).ConfigureAwait(false);
                        long sleptUs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(paceStartTs).TotalMicroseconds;
                        writePacingUs += sleptUs;
                        Interlocked.Add(ref _totalPacingWaitUs, sleptUs);

                        long afterWaitTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        double waitSec = Math.Clamp(System.Diagnostics.Stopwatch.GetElapsedTime(_lastTokenRefillTs, afterWaitTs).TotalSeconds, 0.0, 1.0);
                        _lastTokenRefillTs = afterWaitTs;
                        tokens = Math.Clamp(tokens + (waitSec * paceBytesPerSec), 0.0, maxBurst);
                    }
                }
                Volatile.Write(ref _pacingTokensBytes, Math.Max(0.0, tokens - packet.Length));

                var info = new SentPacketInfo(packet);
                _inFlight[seq] = info;

                long sockStartTs = System.Diagnostics.Stopwatch.GetTimestamp();
                var (activeSock, activeRemoteEp, pathTag) = GetActiveTransportWithTag();
                packet[5] = pathTag;
                try
                {
                    await activeSock.SendToAsync(packet, SocketFlags.None, activeRemoteEp, opCt).ConfigureAwait(false);
                    long sockUs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(sockStartTs).TotalMicroseconds;
                    writeSocketUs += sockUs;
                    Interlocked.Add(ref _totalSocketWriteUs, sockUs);
                    Interlocked.Increment(ref _totalPacketsSent);
                    Interlocked.Add(ref _totalPayloadBytesSent, chunkSize);
                    Interlocked.Add(ref _totalWireBytesSent, packet.Length + UdpIpOverheadBytes);
                    RecordSocketSendSuccess();
                }
                catch (OperationCanceledException)
                {
                    if (_inFlight.TryRemove(seq, out _))
                    {
                        try { _sendWindowSemaphore.Release(); } catch { }
                    }
                    throw;
                }
                catch (Exception ex) when ((ex is ObjectDisposedException || ex is SocketException) && !ReferenceEquals(activeSock, ActiveSocket))
                {
                    var (retrySock, retryRemoteEp, retryTag) = GetActiveTransportWithTag();
                    packet[5] = retryTag;
                    try
                    {
                        await retrySock.SendToAsync(packet, SocketFlags.None, retryRemoteEp, opCt).ConfigureAwait(false);
                        long sockUs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(sockStartTs).TotalMicroseconds;
                        writeSocketUs += sockUs;
                        Interlocked.Add(ref _totalSocketWriteUs, sockUs);
                        Interlocked.Increment(ref _totalPacketsSent);
                        Interlocked.Add(ref _totalPayloadBytesSent, chunkSize);
                        Interlocked.Add(ref _totalWireBytesSent, packet.Length + UdpIpOverheadBytes);
                        RecordSocketSendSuccess();
                    }
                    catch (OperationCanceledException)
                    {
                        if (_inFlight.TryRemove(seq, out _))
                        {
                            try { _sendWindowSemaphore.Release(); } catch { }
                        }
                        throw;
                    }
                    catch (Exception retryEx)
                    {
                        long sockUs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(sockStartTs).TotalMicroseconds;
                        writeSocketUs += sockUs;
                        Interlocked.Add(ref _totalSocketWriteUs, sockUs);
                        RecordSocketSendError(retryEx);
                        Log.Warn($"[ReliableUDP] 發送封包 Seq={seq} 失敗: {retryEx.Message}");
                    }
                }
                catch (Exception ex)
                {
                    long sockUs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(sockStartTs).TotalMicroseconds;
                    writeSocketUs += sockUs;
                    Interlocked.Add(ref _totalSocketWriteUs, sockUs);
                    RecordSocketSendError(ex);
                    Log.Warn($"[ReliableUDP] 發送封包 Seq={seq} 失敗: {ex.Message}");
                }

                offset += chunkSize;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _lastPacingWaitUs, writePacingUs);
            Interlocked.Exchange(ref _lastSocketWriteUs, writeSocketUs);
            Interlocked.Exchange(ref _lastFrameWriteDurationMs, frameSw.ElapsedMilliseconds);
            _writeLock.Release();
        }
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await WriteAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    // ---------------- 讀取 (Read / ReadAsync) ----------------

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty) return 0;

        while (true)
        {
            if (_currentReadChunk != null && _currentReadOffset < _currentReadChunk.Length)
            {
                int available = _currentReadChunk.Length - _currentReadOffset;
                int toCopy = Math.Min(buffer.Length, available);
                _currentReadChunk.AsSpan(_currentReadOffset, toCopy).CopyTo(buffer.Span);
                _currentReadOffset += toCopy;
                if (_currentReadOffset >= _currentReadChunk.Length)
                {
                    _currentReadChunk = null;
                    _currentReadOffset = 0;
                }
                return toCopy;
            }

            try
            {
                if (await _recvChannel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    if (_recvChannel.Reader.TryRead(out var chunk))
                    {
                        if (chunk.Length == 0) return 0; // 串流結束
                        _currentReadChunk = chunk;
                        _currentReadOffset = 0;
                        continue;
                    }
                }
                return 0; // 通道已關閉
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return 0;
            }
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var mem = buffer.AsMemory(offset, count);
        return await ReadAsync(mem, ct).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    public override async Task FlushAsync(CancellationToken ct)
    {
        // 若本端確有接收資料且存在尚未確認的封包，補送累積 ACK 加速對端確認
        if (_nextExpectedRecvSeq > 1)
        {
            _pendingDataAckCount = 0;
            await SendControlPacketAsync(TypeAck, 0, _nextExpectedRecvSeq).ConfigureAwait(false);
        }

        // 等待在途封包全部確認
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!_inFlight.IsEmpty && sw.ElapsedMilliseconds < 2000 && !ct.IsCancellationRequested)
        {
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    // ---------------- 接收背景迴圈 ----------------

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[2048];
        EndPoint senderEp = new IPEndPoint(IPAddress.Any, 0);

        while (!_cts.IsCancellationRequested && !_disposed)
        {
            Task? resumeTask = null;
            CancellationToken epochToken;
            Socket activeSock;
            EndPoint activeRemoteEp;
            byte expectedTag;
            lock (_recvCoordLock)
            {
                if (_recvPauseRequested)
                {
                    _recvEnteredPauseTcs.TrySetResult();
                    resumeTask = _recvResumeTcs.Task;
                }
                activeSock = _socket;
                activeRemoteEp = _remoteEndPoint;
                expectedTag = EncodePathTag(_pathEpoch);
                epochToken = _recvEpochCts.Token;
            }

            if (resumeTask != null)
            {
                try { await resumeTask.WaitAsync(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await activeSock.ReceiveFromAsync(buffer, SocketFlags.None, senderEp, epochToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested && !_disposed)
                {
                    // Path pause or epoch rotation requested; loop back to coordinate pause gate cleanly.
                    continue;
                }

                Interlocked.Exchange(ref _consecutiveRecvSocketErrors, 0);

                if (ExternalDatagramInterceptor != null && result.RemoteEndPoint is IPEndPoint rawSenderIp)
                {
                    IPEndPoint normIp = rawSenderIp.Address.IsIPv4MappedToIPv6
                        ? new IPEndPoint(rawSenderIp.Address.MapToIPv4(), rawSenderIp.Port)
                        : rawSenderIp;
                    try
                    {
                        if (ExternalDatagramInterceptor.Invoke(normIp, buffer.AsMemory(0, result.ReceivedBytes)))
                            continue;
                    }
                    catch { }
                }

                if (result.ReceivedBytes < HeaderSize) continue;

                var span = buffer.AsSpan(0, result.ReceivedBytes);

                // 若在通道剛建立時收到對方殘留的打洞探針 (Type 0x10)，立即回送 ProbeAck (0x11)，防止對方遺失確認
                if (span.Length >= 5 && span[0] == 0x52 && span[1] == 0x44 && span[2] == 0x53 && span[3] == 0x4B && span[4] == 0x10)
                {
                    if (EndPointMatches(result.RemoteEndPoint, activeRemoteEp) || AllowAlternatePairCheckResponses)
                    {
                        try
                        {
                            byte[] ackPkt = span.ToArray();
                            ackPkt[4] = 0x11;
                            await activeSock.SendToAsync(ackPkt, SocketFlags.None, result.RemoteEndPoint, _cts.Token).ConfigureAwait(false);
                        }
                        catch { }
                    }
                    continue;
                }

                // 若在通道剛切換接管或待機候選檢查期間收到同 Session 的 RDIC CheckRequest / NominateRequest，主動補回 ACK
                var postCtx = Volatile.Read(ref _postPunchContext);
                if (IceCheckCodec.TryParse(span, out var iceMsg))
                {
                    bool endpointAllowed = EndPointMatches(result.RemoteEndPoint, activeRemoteEp) || AllowAlternatePairCheckResponses;
                    if (postCtx != null &&
                        endpointAllowed &&
                        string.Equals(iceMsg.SessionId, postCtx.SessionId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(iceMsg.SenderDeviceId, postCtx.RemoteDeviceId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(iceMsg.ReceiverDeviceId, postCtx.SelfDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (iceMsg.MessageType == IceCheckMessageType.CheckRequest ||
                            iceMsg.MessageType == IceCheckMessageType.NominateRequest)
                        {
                            bool peerSwitchDetected = false;
                            IPEndPoint? normalizedSwitchEp = null;

                            if (iceMsg.MessageType == IceCheckMessageType.NominateRequest &&
                                result.RemoteEndPoint is IPEndPoint nomRemoteIp)
                            {
                                normalizedSwitchEp = nomRemoteIp.Address.IsIPv4MappedToIPv6
                                    ? new IPEndPoint(nomRemoteIp.Address.MapToIPv4(), nomRemoteIp.Port)
                                    : nomRemoteIp;

                                bool hasSwitchTag = HasRecoverySwitchTag(iceMsg);
                                bool hasTargetEpoch = TryExtractRecoveryTargetEpoch(iceMsg, out uint targetEpoch);
                                bool endpointChangedNow = !EndPointMatches(normalizedSwitchEp, activeRemoteEp);

                                if (endpointChangedNow || (hasSwitchTag && (!hasTargetEpoch || targetEpoch > Volatile.Read(ref _pathEpoch))))
                                {
                                    // 若 Monitor 已掛載 PeerNominatedSwitchObserved 且接收迴圈未處於暫停協商狀態，先在回送 NominateAck 前原子完成同 Socket 路徑與 PathEpoch 同步，
                                    // 防止對端收到 ACK 後立即高速發送新 Epoch 封包卻被本端舊 Epoch 視窗誤拒。
                                    if (PeerNominatedSwitchObserved != null)
                                    {
                                        lock (_recvCoordLock)
                                        {
                                            if (!_recvPauseRequested)
                                            {
                                                if (TryAdoptSameSocketPeerSwitchLocked(normalizedSwitchEp, iceMsg, hasTargetEpoch ? targetEpoch : null, out uint adoptedEpoch, out uint prevEpoch))
                                                {
                                                    expectedTag = EncodePathTag(adoptedEpoch);
                                                    activeRemoteEp = normalizedSwitchEp;
                                                    peerSwitchDetected = true;
                                                    Log.Info($"[ReliableUDP] Eager peer switch synced before NominateAck: OldEpoch={prevEpoch} -> NewEpoch={adoptedEpoch}, Remote={normalizedSwitchEp}");
                                                }
                                            }
                                            else
                                            {
                                                peerSwitchDetected = true;
                                            }
                                        }
                                    }
                                    else
                                    {
                                        peerSwitchDetected = true;
                                    }
                                }
                            }

                            var replyType = iceMsg.MessageType == IceCheckMessageType.NominateRequest
                                ? IceCheckMessageType.NominateAck
                                : IceCheckMessageType.CheckResponse;
                            byte[] iceAck = IceCheckCodec.Serialize(
                                replyType,
                                isControlling: postCtx.IsControlling,
                                useCandidate: replyType == IceCheckMessageType.NominateAck,
                                roleConflictRejected: false,
                                attemptNumber: iceMsg.AttemptNumber,
                                transactionId: iceMsg.TransactionId,
                                tieBreaker: postCtx.TieBreaker,
                                priority: iceMsg.Priority,
                                observedEndPoint: result.RemoteEndPoint as IPEndPoint,
                                sessionId: postCtx.SessionId,
                                senderDeviceId: postCtx.SelfDeviceId,
                                receiverDeviceId: postCtx.RemoteDeviceId,
                                localCandidateId: postCtx.SelectedLocalCandidateId,
                                remoteCandidateId: postCtx.SelectedRemoteCandidateId,
                                pairId: postCtx.SelectedPairId);
                            try { await activeSock.SendToAsync(iceAck, SocketFlags.None, result.RemoteEndPoint, _cts.Token).ConfigureAwait(false); } catch { }

                            if (peerSwitchDetected && normalizedSwitchEp != null)
                            {
                                PeerNominatedSwitchObserved?.Invoke(normalizedSwitchEp, iceMsg);
                            }
                        }
                    }
                    continue;
                }

                if (!ParseHeader(span, out byte type, out byte flags, out uint seq, out uint ack, out ushort payloadLen))
                {
                    continue; // 忽略非 RDSK 封包
                }

                // 嚴格隔離舊路徑 / 未知來源 / 過期 PathEpoch 的延遲封包，絕不允許污染新路徑 ACK、視窗或重組緩衝
                if (!EndPointMatches(result.RemoteEndPoint, activeRemoteEp) || flags != expectedTag)
                {
                    Interlocked.Increment(ref _rejectedStalePathPackets);
                    continue;
                }

                if (result.ReceivedBytes < HeaderSize + payloadLen) continue;

                // 更新最近一次收到對端有效封包時間 (證明實體鏈路與對端存活)
                long nowTicks = DateTime.UtcNow.Ticks;
                Interlocked.Exchange(ref _lastPeerContactTicks, nowTicks);
                Interlocked.Exchange(ref _lastSuccessfulRecvTicks, nowTicks);

                var payload = span.Slice(HeaderSize, payloadLen).ToArray();

                // 更新對方發給我們的 ACK (釋放滑動視窗)
                ProcessPeerAck(ack);

                switch (type)
                {
                    case TypeData:
                        await ProcessIncomingDataAsync(seq, payload).ConfigureAwait(false);
                        break;

                    case TypeAck:
                        // 僅為 ACK 封包，ProcessPeerAck 已處理
                        break;

                    case TypePing:
                        // 回覆 Pong，原封帶回 Probe seq 以供精確測量 Heartbeat RTT
                        await SendControlPacketAsync(TypePong, seq, Volatile.Read(ref _nextExpectedRecvSeq)).ConfigureAwait(false);
                        break;

                    case TypePong:
                        // 收到 Pong，紀錄時間並完成待確認的 Heartbeat Probe
                        Interlocked.Exchange(ref _lastHeartbeatAckTicks, nowTicks);
                        CompleteHeartbeatProbeIfMatches(seq);
                        break;

                    case TypeClose:
                        Volatile.Write(ref _remoteCloseReceived, true);
                        _recvChannel.Writer.TryComplete();
                        return;
                }

                // 若剛處理完控制封包且 socket 已清空，立刻補送尚待確認之累積 ACK，杜絕尾包卡到對端 RTO
                if (_pendingDataAckCount > 0 && IsSocketReceiveEmpty())
                {
                    _pendingDataAckCount = 0;
                    await SendControlPacketAsync(TypeAck, 0, Volatile.Read(ref _nextExpectedRecvSeq)).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException)
            {
                if (_cts.IsCancellationRequested || _disposed) break;
                // Underlying socket was likely swapped during recovery; pause briefly and pick up new socket
                try { await Task.Delay(15, _cts.Token).ConfigureAwait(false); } catch { break; }
            }
            catch (SocketException sex)
            {
                RecordSocketRecvError(sex);
                if (!_disposed && !_cts.IsCancellationRequested)
                {
                    int consec = Volatile.Read(ref _consecutiveRecvSocketErrors);
                    if (consec <= 3 || consec % 20 == 0)
                        Log.Warn($"[ReliableUDP] 接收 Socket 異常 ({sex.SocketErrorCode}, consec={consec}): {sex.Message}");
                    if (IsFatalUdpSocketError(sex.SocketErrorCode))
                    {
                        try { await Task.Delay(50, _cts.Token).ConfigureAwait(false); } catch { break; }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_disposed)
                {
                    Log.Warn($"[ReliableUDP] 接收異常: {ex.Message}");
                }
            }
        }
    }

    private bool IsSocketReceiveEmpty()
    {
        try { return ActiveSocket.Available == 0; }
        catch { return true; }
    }

    private void ProcessPeerAck(uint ack)
    {
        if (ack <= 1) return;
        Interlocked.Increment(ref _totalAcksReceived);

        uint highestAck = Volatile.Read(ref _highestAckReceived);
        if (IsSeqBefore(highestAck, ack))
        {
            Volatile.Write(ref _highestAckReceived, ack);
            Interlocked.Exchange(ref _duplicateAckCount, 0);

            // 移除所有 Seq < ack 的已確認封包並動態估算 RTT (RFC 6298 + Karn's Algorithm 單批抗污染)
            DateTime now = DateTime.UtcNow;
            long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
            int removedCount = 0;
            bool anyRetriedAckedInBatch = false;
            bool hasLatestCleanSample = false;
            uint latestCleanSeq = 0;
            DateTime latestCleanFirstSentAt = default;
            uint minRemainingSeq = 0;
            SentPacketInfo? minRemainingInfo = null;

            foreach (var (key, entry) in _inFlight)
            {
                if (IsSeqBefore(key, ack))
                {
                    if (_inFlight.TryRemove(key, out var info))
                    {
                        removedCount++;
                        try { _sendWindowSemaphore.Release(); } catch { }
                        Interlocked.Increment(ref _uniquePacketsAcked);
                        int payloadLen = Math.Max(0, info.Data.Length - HeaderSize);
                        int wireLen = info.Data.Length + UdpIpOverheadBytes;
                        Interlocked.Add(ref _totalPayloadBytesAcked, payloadLen);
                        Interlocked.Add(ref _totalWireBytesAcked, wireLen);

                        if (info.Retries > 0)
                        {
                            anyRetriedAckedInBatch = true;
                        }
                        else
                        {
                            lock (_bwLock)
                            {
                                if (_burstPacketCount == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(_burstLastAckTs, nowTs).TotalMilliseconds > 40.0)
                                {
                                    _burstFirstAckTs = nowTs;
                                    _burstLastAckTs = nowTs;
                                    _burstWireBytes = wireLen;
                                    _burstPacketCount = 1;
                                }
                                else
                                {
                                    _burstLastAckTs = nowTs;
                                    _burstWireBytes += wireLen;
                                    _burstPacketCount++;
                                    double spanMs = System.Diagnostics.Stopwatch.GetElapsedTime(_burstFirstAckTs, _burstLastAckTs).TotalMilliseconds;
                                    if ((_burstPacketCount >= 6 && spanMs >= 2.0) || (_burstPacketCount >= 16 && spanMs >= 0.15))
                                    {
                                        _lastDeliverySampleMbps = Math.Clamp((_burstWireBytes * 8.0) / (spanMs * 1000.0), 0.1, 10_000.0);
                                        _lastDeliverySampleTs = nowTs;
                                    }
                                }
                            }

                            if (!hasLatestCleanSample || IsSeqBefore(latestCleanSeq, key))
                            {
                                hasLatestCleanSample = true;
                                latestCleanSeq = key;
                                latestCleanFirstSentAt = info.FirstSentAt;
                            }
                        }
                    }
                }
                else if (minRemainingInfo == null || IsSeqBefore(key, minRemainingSeq))
                {
                    minRemainingSeq = key;
                    minRemainingInfo = entry;
                }
            }

            // Karn's Rule：若本批累積 ACK 順帶解鎖了曾重傳之缺口 (HoL Blocking)，其餘封包延遲反映的是救回耗時而非真實路徑 RTT，禁止污染 SRTT/RTO；
            // 僅於乾淨無重傳批次內取最新一張封包執行單次 RFC 6298 更新。
            if (!anyRetriedAckedInBatch && removedCount > 0)
            {
                Interlocked.Add(ref _cleanPacketsAckedSinceLoss, removedCount);
                if (hasLatestCleanSample)
                {
                    double sampleRtt = (now - latestCleanFirstSentAt).TotalMilliseconds;
                    if (sampleRtt > 0 && sampleRtt < 3000)
                    {
                        _rttVarMs = (0.75 * _rttVarMs) + (0.25 * Math.Abs(_srttMs - sampleRtt));
                        _srttMs = (0.875 * _srttMs) + (0.125 * sampleRtt);
                        int minRto = Math.Min(2000, Math.Max(100, (int)(_srttMs * 2.0)));
                        _rtoMs = Math.Clamp((int)(_srttMs + Math.Max(30, 4 * _rttVarMs)), minRto, 2000);
                    }
                }
            }

            if (minRemainingInfo == null)
            {
                Volatile.Write(ref _rtoContiguousRun, 0);
            }
            else
            {
                int headRto = Math.Min(2000, Volatile.Read(ref _rtoMs) + (minRemainingInfo.Retries * 100));
                if (removedCount <= 2 && (now - minRemainingInfo.SentAt).TotalMilliseconds >= headRto)
                    Interlocked.Increment(ref _rtoContiguousRun);
                else
                    Volatile.Write(ref _rtoContiguousRun, 0);
            }
        }
        else if (ack == highestAck && _inFlight.TryGetValue(ack, out var lostPkt))
        {
            // 收到重複 ACK (TCP Fast Retransmit 機制)：
            // dup == 3 立即觸發快速重傳；若重傳封包本身在途中再度遺失而後續封包持續抵達，
            // 則於至少間隔 1 個安全 RTT 後每 6 次 dup ACK 允許再度快速重傳，避免整條視窗卡死至 RTO 爆量逾時
            int dup = Interlocked.Increment(ref _duplicateAckCount);
            bool shouldFastRetransmit = dup == 3;
            if (!shouldFastRetransmit && dup >= 6 && dup % 6 == 0)
            {
                double retryCooldownMs = Math.Max(30.0, Volatile.Read(ref _srttMs) * 1.25);
                if ((DateTime.UtcNow - lostPkt.SentAt).TotalMilliseconds >= retryCooldownMs)
                    shouldFastRetransmit = true;
            }

            if (shouldFastRetransmit)
            {
                lostPkt.SentAt = DateTime.UtcNow;
                lostPkt.IncrementRetries();
                if (Interlocked.Exchange(ref lostPkt.CountedLost, 1) == 0)
                    Interlocked.Increment(ref _uniquePacketsLost);
                Interlocked.Increment(ref _totalPacketLossCount);
                Interlocked.Increment(ref _totalRetransmissions);
                Interlocked.Increment(ref _totalFastRetransmits);
                RecordLossObserved();
                Interlocked.Add(ref _totalWireBytesSent, lostPkt.Data.Length + UdpIpOverheadBytes);
                var (fastSock, fastRemoteEp, fastTag) = GetActiveTransportWithTag();
                lostPkt.Data[5] = fastTag;
                _ = SendFastRetransmitPacketAsync(fastSock, lostPkt.Data, fastRemoteEp);
            }
        }
    }

    private async Task SendFastRetransmitPacketAsync(Socket sock, byte[] data, EndPoint remoteEp)
    {
        try
        {
            await sock.SendToAsync(data, SocketFlags.None, remoteEp, _cts.Token).ConfigureAwait(false);
            RecordSocketSendSuccess();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordSocketSendError(ex);
        }
    }

    private async Task ProcessIncomingDataAsync(uint seq, byte[] payload)
    {
        uint nextExpected = _nextExpectedRecvSeq;

        if (seq == nextExpected)
        {
            // 正好是預期的封包
            _recvChannel.Writer.TryWrite(payload);
            _nextExpectedRecvSeq++;

            bool drainedOutOfOrder = false;
            // 檢查亂序暫存中是否有連續封包可解鎖
            while (_outOfOrderBuffer.TryRemove(_nextExpectedRecvSeq, out var cachedPayload))
            {
                _recvChannel.Writer.TryWrite(cachedPayload);
                _nextExpectedRecvSeq++;
                drainedOutOfOrder = true;
            }

            _pendingDataAckCount++;

            // 發送累積 ACK：
            // 若成功解鎖亂序暫存、或當前 socket 緩衝區已無待讀封包、或每累積 2 個新資料封包，立即發送累積 ACK；
            // 若因控制封包暫存使 socket 暫非空，ReceiveLoop 處理完控制封包後亦會檢查 _pendingDataAckCount 立即補送，絕不卡到對端 RTO。
            if (drainedOutOfOrder || _pendingDataAckCount >= 2 || IsSocketReceiveEmpty())
            {
                _pendingDataAckCount = 0;
                await SendControlPacketAsync(TypeAck, 0, _nextExpectedRecvSeq).ConfigureAwait(false);
            }
        }
        else if (seq > nextExpected)
        {
            // 亂序封包，暫存並立即發送當前預期 ACK (讓對方得知遺失並觸發 Fast Retransmit)
            if (_outOfOrderBuffer.Count < 4096)
            {
                _outOfOrderBuffer[seq] = payload;
            }
            _pendingDataAckCount = 0;
            await SendControlPacketAsync(TypeAck, 0, nextExpected).ConfigureAwait(false);
        }
        else
        {
            // 重複收到的舊封包，重新 ACK 以免對方卡住
            _pendingDataAckCount = 0;
            await SendControlPacketAsync(TypeAck, 0, nextExpected).ConfigureAwait(false);
        }
    }

    private async Task SendControlPacketAsync(byte type, uint seq, uint ack, CancellationToken ct = default)
    {
        var (activeSock, activeRemoteEp, pathTag) = GetActiveTransportWithTag();
        byte[] packet = new byte[HeaderSize];
        WriteHeader(packet.AsSpan(0, HeaderSize), type, pathTag, seq, ack, 0);
        try
        {
            using var linkedCts = ct.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct)
                : null;
            var sendCt = linkedCts?.Token ?? _cts.Token;
            await activeSock.SendToAsync(packet, SocketFlags.None, activeRemoteEp, sendCt).ConfigureAwait(false);
            RecordSocketSendSuccess();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordSocketSendError(ex);
        }
        catch { }
    }

    private void CompleteHeartbeatProbeIfMatches(uint ackedProbeSeq)
    {
        TaskCompletionSource<double?>? tcsToComplete = null;
        double measuredRttMs = 0;
        lock (_heartbeatLock)
        {
            if (_pendingHeartbeatTcs != null &&
                ((ackedProbeSeq != 0 && ackedProbeSeq == _pendingHeartbeatProbeSeq) ||
                 (SuppressInternalKeepAlive && ackedProbeSeq == 0)))
            {
                measuredRttMs = Math.Max(0.05, System.Diagnostics.Stopwatch.GetElapsedTime(_pendingHeartbeatSentTs).TotalMilliseconds);
                Volatile.Write(ref _lastHeartbeatRttMs, measuredRttMs);
                tcsToComplete = _pendingHeartbeatTcs;
                _pendingHeartbeatTcs = null;
            }
        }
        tcsToComplete?.TrySetResult(measuredRttMs);
    }

    /// <summary>
    /// 發送主動心跳探針 (TypePing 攜帶單調 seq)，等待對應 TypePong 並量測往返 RTT (ms)。
    /// 超時回傳 null；不會影響資料串流序號或滑動視窗。
    /// </summary>
    public async Task<double?> SendHeartbeatProbeAsync(int timeoutMs, CancellationToken ct = default)
    {
        if (_disposed || _cts.IsCancellationRequested) return null;

        uint probeSeq = Interlocked.Increment(ref _nextHeartbeatProbeSeq);
        if (probeSeq == 0) probeSeq = Interlocked.Increment(ref _nextHeartbeatProbeSeq);

        var tcs = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
        long sentTs = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_heartbeatLock)
        {
            _pendingHeartbeatProbeSeq = probeSeq;
            _pendingHeartbeatSentTs = sentTs;
            _pendingHeartbeatTcs?.TrySetResult(null);
            _pendingHeartbeatTcs = tcs;
        }

        Interlocked.Exchange(ref _lastHeartbeatSentTicks, DateTime.UtcNow.Ticks);
        await SendControlPacketAsync(TypePing, probeSeq, Volatile.Read(ref _nextExpectedRecvSeq), ct).ConfigureAwait(false);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
        linkedCts.CancelAfter(Math.Max(50, timeoutMs));
        using var reg = linkedCts.Token.Register(() => tcs.TrySetResult(null));

        double? rtt = await tcs.Task.ConfigureAwait(false);
        if (rtt == null)
        {
            lock (_heartbeatLock)
            {
                if (ReferenceEquals(_pendingHeartbeatTcs, tcs))
                    _pendingHeartbeatTcs = null;
            }
        }
        return rtt;
    }

    /// <summary>
    /// 暫停接收迴圈以便路徑監控/打洞檢查安全共用目前 ActiveSocket。
    /// 呼叫端必須於完成後呼叫 ResumeReceiveAfterCheck 或 SwitchPathAsync。
    /// </summary>
    internal async Task<bool> PauseReceiveLoopForCheckAsync(int timeoutMs = 2000, CancellationToken ct = default)
    {
        if (_disposed || _cts.IsCancellationRequested) return false;
        await _pathControlLock.WaitAsync(ct).ConfigureAwait(false);
        bool holdLock = true;
        try
        {
            var enteredTask = PauseReceiveLoopCore();
            if (enteredTask != null)
            {
                using var ctsWait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
                ctsWait.CancelAfter(Math.Max(100, timeoutMs));
                try
                {
                    await enteredTask.WaitAsync(ctsWait.Token).ConfigureAwait(false);
                }
                catch
                {
                    ResumeReceiveLoopCore();
                    return false;
                }
            }
            holdLock = false;
            return true;
        }
        finally
        {
            if (holdLock) _pathControlLock.Release();
        }
    }

    /// <summary>
    /// 解除 PauseReceiveLoopForCheckAsync 之暫停並恢復既有的 ReceiveLoopAsync。
    /// </summary>
    internal void ResumeReceiveAfterCheck()
    {
        try
        {
            ResumeReceiveLoopCore();
        }
        finally
        {
            try { _pathControlLock.Release(); } catch { }
        }
    }

    private Task? PauseReceiveLoopCore()
    {
        CancellationTokenSource? toCancel = null;
        TaskCompletionSource enteredTcs;
        lock (_recvCoordLock)
        {
            if (_disposed || _cts.IsCancellationRequested) return null;
            if (!_recvPauseRequested)
            {
                _recvPauseRequested = true;
                _recvEnteredPauseTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _recvResumeTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                toCancel = _recvEpochCts;
            }
            enteredTcs = _recvEnteredPauseTcs;
        }
        try { toCancel?.Cancel(); } catch { }
        return enteredTcs.Task;
    }

    private void ResumeReceiveLoopCore()
    {
        CancellationTokenSource? oldCts = null;
        TaskCompletionSource? resumeTcs = null;
        lock (_recvCoordLock)
        {
            if (_recvPauseRequested)
            {
                oldCts = _recvEpochCts;
                _recvEpochCts = _cts.IsCancellationRequested
                    ? new CancellationTokenSource()
                    : CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                _recvPauseRequested = false;
                resumeTcs = _recvResumeTcs;
            }
        }
        try { oldCts?.Dispose(); } catch { }
        resumeTcs?.TrySetResult();
    }

    /// <summary>
    /// 原子切換 ReliableUdpStream 底層傳輸路徑（可替換 Socket 與 RemoteEndPoint），並遞增 PathEpoch。
    /// 不重建串流本身與 AES-256-GCM 狀態，並以新 PathTag 重新送出尚未確認的在途封包，同時隔離舊路徑遲到封包。
    /// </summary>
    internal void EnsurePostPunchContext(IceStreamPostPunchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Interlocked.CompareExchange(ref _postPunchContext, context, null);
    }

    internal static bool HasRecoverySwitchTag(IceCheckMessage msg)
    {
        static bool ContainsTag(string? s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return s.Contains("#recheck-", StringComparison.Ordinal) ||
                   s.Contains("#s2-", StringComparison.Ordinal) ||
                   s.Contains("#s2a-", StringComparison.Ordinal) ||
                   s.Contains("#s3-", StringComparison.Ordinal) ||
                   s.Contains("#ep-", StringComparison.Ordinal);
        }
        return ContainsTag(msg.PairId) || ContainsTag(msg.LocalCandidateId);
    }

    internal static bool TryExtractRecoveryTargetEpoch(IceCheckMessage msg, out uint targetEpoch)
    {
        static bool TryParseFromString(string? s, out uint epoch)
        {
            epoch = 0;
            if (string.IsNullOrEmpty(s)) return false;
            string[] prefixes = { "#recheck-", "#s2a-", "#s2-", "#s3-", "#ep-" };
            uint best = 0;
            foreach (var p in prefixes)
            {
                int idx = s.IndexOf(p, StringComparison.Ordinal);
                while (idx >= 0)
                {
                    int start = idx + p.Length;
                    int end = start;
                    while (end < s.Length && char.IsDigit(s[end])) end++;
                    if (end > start && uint.TryParse(s.AsSpan(start, end - start), out uint parsed) && parsed > best)
                    {
                        best = parsed;
                    }
                    idx = s.IndexOf(p, start, StringComparison.Ordinal);
                }
            }
            if (best > 0)
            {
                epoch = best;
                return true;
            }
            return false;
        }

        if (TryParseFromString(msg.LocalCandidateId, out targetEpoch))
            return true;
        return TryParseFromString(msg.PairId, out targetEpoch);
    }

    private bool TryAdoptSameSocketPeerSwitchLocked(
        IPEndPoint normalizedNomIp,
        IceCheckMessage nomMsg,
        uint? targetEpoch,
        out uint adoptedEpoch,
        out uint previousEpoch)
    {
        previousEpoch = _pathEpoch;
        bool endpointChanged = !EndPointMatches(_remoteEndPoint, normalizedNomIp);
        if (!endpointChanged && (!targetEpoch.HasValue || targetEpoch.Value <= _pathEpoch))
        {
            adoptedEpoch = _pathEpoch;
            return false;
        }

        if (targetEpoch.HasValue)
        {
            if (targetEpoch.Value > _pathEpoch)
                adoptedEpoch = targetEpoch.Value;
            else if (targetEpoch.Value == _pathEpoch && endpointChanged)
                adoptedEpoch = _pathEpoch;
            else
                adoptedEpoch = _pathEpoch + 1;
        }
        else
        {
            adoptedEpoch = _pathEpoch + 1;
        }

        _remoteEndPoint = normalizedNomIp;
        _pathEpoch = adoptedEpoch;
        byte newTag = EncodePathTag(adoptedEpoch);

        var curCtx = Volatile.Read(ref _postPunchContext);
        if (curCtx != null)
        {
            Volatile.Write(ref _postPunchContext, new IceStreamPostPunchContext
            {
                SessionId = curCtx.SessionId,
                SelfDeviceId = curCtx.SelfDeviceId,
                RemoteDeviceId = curCtx.RemoteDeviceId,
                SelectedLocalCandidateId = string.IsNullOrWhiteSpace(nomMsg.RemoteCandidateId) ? curCtx.SelectedLocalCandidateId : nomMsg.RemoteCandidateId,
                SelectedRemoteCandidateId = string.IsNullOrWhiteSpace(nomMsg.LocalCandidateId) ? curCtx.SelectedRemoteCandidateId : nomMsg.LocalCandidateId,
                SelectedPairId = string.IsNullOrWhiteSpace(nomMsg.PairId) ? curCtx.SelectedPairId : nomMsg.PairId,
                IsControlling = curCtx.IsControlling,
                TieBreaker = curCtx.TieBreaker
            });
        }

        Interlocked.Exchange(ref _consecutiveSendSocketErrors, 0);
        Interlocked.Exchange(ref _consecutiveRecvSocketErrors, 0);
        Volatile.Write(ref _lastSocketErrorCode, 0);
        Volatile.Write(ref _fatalSocketErrorOccurred, false);

        long nowTicks = DateTime.UtcNow.Ticks;
        Interlocked.Exchange(ref _lastPeerContactTicks, nowTicks);
        Interlocked.Exchange(ref _lastSuccessfulSendTicks, nowTicks);
        Interlocked.Exchange(ref _lastSuccessfulRecvTicks, nowTicks);
        Volatile.Write(ref _rtoContiguousRun, 0);

        DateTime utcNow = DateTime.UtcNow;
        foreach (var (_, info) in _inFlight)
        {
            if (info.Data.Length >= HeaderSize)
                info.Data[5] = newTag;
            info.SentAt = utcNow;
        }

        return true;
    }

    internal async Task<uint> SwitchPathAsync(
        Socket newSocket,
        EndPoint newRemoteEndPoint,
        bool newOwnsSocket,
        IceStreamPostPunchContext? updatedPostPunchContext = null,
        bool callerAlreadyPausedReceive = false,
        CancellationToken ct = default,
        uint? targetEpoch = null)
    {
        ArgumentNullException.ThrowIfNull(newSocket);
        ArgumentNullException.ThrowIfNull(newRemoteEndPoint);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!callerAlreadyPausedReceive)
        {
            await _pathControlLock.WaitAsync(ct).ConfigureAwait(false);
        }

        Socket? socketToDispose = null;
        uint newEpoch;
        try
        {
            if (!callerAlreadyPausedReceive)
            {
                var enteredTask = PauseReceiveLoopCore();
                if (enteredTask != null)
                {
                    using var pauseCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);
                    pauseCts.CancelAfter(2000);
                    try { await enteredTask.WaitAsync(pauseCts.Token).ConfigureAwait(false); } catch { }
                }
            }

            CancellationTokenSource? oldEpochCts;
            TaskCompletionSource resumeTcs;
            lock (_recvCoordLock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ReliableUdpStream));

                Socket oldSocket = _socket;
                bool oldOwns = _ownsSocket;
                bool sameSocket = ReferenceEquals(newSocket, oldSocket);
                bool alreadySyncedSameSocket = sameSocket &&
                    EndPointMatches(_remoteEndPoint, newRemoteEndPoint) &&
                    targetEpoch.HasValue &&
                    _pathEpoch >= targetEpoch.Value;

                if (!sameSocket)
                {
                    ConfigureUdpSocket(newSocket);
                    _socket = newSocket;
                    _ownsSocket = newOwnsSocket;
                    if (oldOwns)
                        socketToDispose = oldSocket;
                }
                else
                {
                    _ownsSocket = newOwnsSocket;
                }

                bool epChanged = !EndPointMatches(_remoteEndPoint, newRemoteEndPoint);
                _remoteEndPoint = newRemoteEndPoint;
                if (updatedPostPunchContext != null)
                {
                    Volatile.Write(ref _postPunchContext, updatedPostPunchContext);
                }

                if (alreadySyncedSameSocket)
                {
                    newEpoch = _pathEpoch;
                }
                else
                {
                    if (targetEpoch.HasValue)
                    {
                        if (targetEpoch.Value > _pathEpoch)
                            newEpoch = targetEpoch.Value;
                        else if (targetEpoch.Value == _pathEpoch && (!sameSocket || epChanged))
                            newEpoch = _pathEpoch;
                        else
                            newEpoch = _pathEpoch + 1;
                    }
                    else
                    {
                        newEpoch = _pathEpoch + 1;
                    }

                    _pathEpoch = newEpoch;
                    byte newTag = EncodePathTag(newEpoch);

                    Interlocked.Exchange(ref _consecutiveSendSocketErrors, 0);
                    Interlocked.Exchange(ref _consecutiveRecvSocketErrors, 0);
                    Volatile.Write(ref _lastSocketErrorCode, 0);
                    Volatile.Write(ref _fatalSocketErrorOccurred, false);

                    long nowTicks = DateTime.UtcNow.Ticks;
                    Interlocked.Exchange(ref _lastPeerContactTicks, nowTicks);
                    Interlocked.Exchange(ref _lastSuccessfulSendTicks, nowTicks);
                    Interlocked.Exchange(ref _lastSuccessfulRecvTicks, nowTicks);
                    Volatile.Write(ref _rtoContiguousRun, 0);

                    DateTime utcNow = DateTime.UtcNow;
                    foreach (var (_, info) in _inFlight)
                    {
                        if (info.Data.Length >= HeaderSize)
                            info.Data[5] = newTag;
                        info.SentAt = utcNow;
                    }
                }

                oldEpochCts = _recvEpochCts;
                _recvEpochCts = _cts.IsCancellationRequested
                    ? new CancellationTokenSource()
                    : CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                _recvPauseRequested = false;
                resumeTcs = _recvResumeTcs;
            }

            try { oldEpochCts?.Dispose(); } catch { }
            resumeTcs.TrySetResult();

            if (socketToDispose != null)
            {
                try { socketToDispose.Dispose(); } catch { }
            }

            // 立刻於新路徑補發累積 ACK + 保活 Probe 同步雙向狀態，並立即重送既有的未確認在途封包
            await SendControlPacketAsync(TypeAck, 0, Volatile.Read(ref _nextExpectedRecvSeq), ct).ConfigureAwait(false);
            if (!_inFlight.IsEmpty)
            {
                var pending = _inFlight.ToArray();
                Array.Sort(pending, (a, b) => unchecked((int)(a.Key - b.Key)));
                var (sockNow, remoteNow, tagNow) = GetActiveTransportWithTag();
                foreach (var (_, info) in pending)
                {
                    if (_cts.IsCancellationRequested || _disposed) break;
                    info.Data[5] = tagNow;
                    info.SentAt = DateTime.UtcNow;
                    try
                    {
                        await sockNow.SendToAsync(info.Data, SocketFlags.None, remoteNow, _cts.Token).ConfigureAwait(false);
                        RecordSocketSendSuccess();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        RecordSocketSendError(ex);
                    }
                }
            }

            Log.Info($"[ReliableUDP] Path switched atomically: PathEpoch={newEpoch}, Local={LocalEndPoint}, Remote={newRemoteEndPoint}, RequeuedInflight={_inFlight.Count}");
            return newEpoch;
        }
        finally
        {
            try { _pathControlLock.Release(); } catch { }
        }
    }

    // ---------------- 逾時重傳背景迴圈 (ARQ) ----------------

    private async Task RetransmitLoopAsync()
    {
        while (!_cts.IsCancellationRequested && !_disposed)
        {
            try
            {
                await Task.Delay(15, _cts.Token).ConfigureAwait(false);

                if (_inFlight.IsEmpty) continue;
                bool recvPaused;
                lock (_recvCoordLock) { recvPaused = _recvPauseRequested; }
                if (recvPaused) continue;

                uint oldestSeq = 0;
                SentPacketInfo? oldestInfo = null;
                foreach (var (seq, info) in _inFlight)
                {
                    if (oldestInfo == null || IsSeqBefore(seq, oldestSeq))
                    {
                        oldestSeq = seq;
                        oldestInfo = info;
                    }
                }
                if (oldestInfo == null) continue;

                var now = DateTime.UtcNow;
                int baseRto = Volatile.Read(ref _rtoMs);
                int oldestRto = Math.Min(2000, baseRto + (oldestInfo.Retries * 100));
                if ((now - oldestInfo.SentAt).TotalMilliseconds < oldestRto)
                {
                    // 累積 ACK 協定在最舊缺口未補齊前無法單獨確認後續封包；
                    // 當最舊封包仍在等待 ACK（或剛完成 Fast Retransmit）時，禁止對後方仍在途封包同時觸發 RTO 洪峰。
                    continue;
                }

                int dupAcks = Volatile.Read(ref _duplicateAckCount);
                int contiguousRun = Volatile.Read(ref _rtoContiguousRun);
                int maxRtoPerTick = dupAcks > 0
                    ? 1
                    : (oldestInfo.Retries >= 1 || contiguousRun >= 1 ? 4 : 1);

                List<(uint Seq, SentPacketInfo Info)>? candidates = null;
                if (maxRtoPerTick > 1)
                {
                    candidates = new List<(uint Seq, SentPacketInfo Info)>(8);
                    foreach (var (seq, info) in _inFlight)
                    {
                        int currentRto = Math.Min(2000, baseRto + (info.Retries * 100));
                        if ((now - info.SentAt).TotalMilliseconds >= currentRto)
                        {
                            candidates.Add((seq, info));
                        }
                    }
                    if (candidates.Count > 1)
                    {
                        candidates.Sort((a, b) => unchecked((int)(a.Seq - b.Seq)));
                    }
                }

                int totalToSend = candidates != null ? Math.Min(maxRtoPerTick, candidates.Count) : 1;
                for (int i = 0; i < totalToSend; i++)
                {
                    var (seq, info) = candidates != null ? candidates[i] : (oldestSeq, oldestInfo);
                    if (!_inFlight.ContainsKey(seq)) continue;

                    int retriesAfter = info.IncrementRetries();
                    if (Interlocked.Exchange(ref info.CountedLost, 1) == 0)
                        Interlocked.Increment(ref _uniquePacketsLost);
                    Interlocked.Increment(ref _totalPacketLossCount);
                    Interlocked.Increment(ref _totalRetransmissions);
                    Interlocked.Increment(ref _totalRtoTimeouts);
                    RecordLossObserved();
                    double rtoAgeMs = (now - info.SentAt).TotalMilliseconds;
                    LastRtoDiagnostic = $"Seq={seq},AgeMs={rtoAgeMs:F1},RtoMs={oldestRto},BaseRto={baseRto},Retries={retriesAfter},Inflight={_inFlight.Count},HighAck={Volatile.Read(ref _highestAckReceived)},NextSend={_nextSendSeq},DupAck={dupAcks},SentPkts={TotalPacketsSent}";

                    // 連線中斷判定：
                    // 僅在超過 20 秒毫無收到對端任何封包 (連線已實質斷絕) 且重傳次數 > 40 時才判定斷線
                    // 若對端仍有發送 ACK / Pong / Data，表示連線仍在，絕不貿然斷線
                    long lastContactTicks = Interlocked.Read(ref _lastPeerContactTicks);
                    var timeSinceContact = now - new DateTime(lastContactTicks, DateTimeKind.Utc);

                    if (retriesAfter > 40 && timeSinceContact.TotalSeconds >= 20 && !SuppressDeadDisconnectDuringRecovery)
                    {
                        Interlocked.Add(ref _totalUnrecoverableLost, Math.Max(1, _inFlight.Count));
                        Log.Warn($"[ReliableUDP] 封包 Seq={seq} 連續重傳逾時且超過 20 秒無回應，判定連線中斷");
                        _recvChannel.Writer.TryComplete();
                        return;
                    }

                    info.SentAt = now;
                    Interlocked.Add(ref _totalWireBytesSent, info.Data.Length + UdpIpOverheadBytes);
                    var (rtxSock, rtxRemoteEp, rtxTag) = GetActiveTransportWithTag();
                    info.Data[5] = rtxTag;
                    try
                    {
                        await rtxSock.SendToAsync(info.Data, SocketFlags.None, rtxRemoteEp, _cts.Token).ConfigureAwait(false);
                        RecordSocketSendSuccess();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        RecordSocketSendError(ex);
                    }
                    catch { }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    // ---------------- NAT 穿透保活 (Keepalive Ping) ----------------

    private async Task KeepAliveLoopAsync()
    {
        while (!_cts.IsCancellationRequested && !_disposed)
        {
            try
            {
                await Task.Delay(2000, _cts.Token).ConfigureAwait(false);

                if (SuppressInternalKeepAlive) continue;
                bool recvPaused;
                lock (_recvCoordLock) { recvPaused = _recvPauseRequested; }
                if (recvPaused) continue;

                long lastSend = Interlocked.Read(ref _lastSendTimeTicks);
                if (DateTime.UtcNow.Ticks - lastSend > TimeSpan.FromSeconds(2).Ticks)
                {
                    // 超過 2 秒無發送封包，主動發送 PING 維護 NAT 映射洞口
                    await SendControlPacketAsync(TypePing, 0, Volatile.Read(ref _nextExpectedRecvSeq)).ConfigureAwait(false);
                    Interlocked.Exchange(ref _lastSendTimeTicks, DateTime.UtcNow.Ticks);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    // ---------------- 標頭編碼與解析 ----------------

    private static void WriteHeader(Span<byte> header, byte type, byte flags, uint seq, uint ack, ushort payloadLen)
    {
        BinaryPrimitives.WriteUInt32BigEndian(header[0..4], Magic);
        header[4] = type;
        header[5] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(header[6..10], seq);
        BinaryPrimitives.WriteUInt32BigEndian(header[10..14], ack);
        BinaryPrimitives.WriteUInt16BigEndian(header[14..16], payloadLen);
    }

    private static bool ParseHeader(ReadOnlySpan<byte> header, out byte type, out byte flags, out uint seq, out uint ack, out ushort payloadLen)
    {
        type = 0; flags = 0; seq = 0; ack = 0; payloadLen = 0;
        if (header.Length < HeaderSize) return false;

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(header[0..4]);
        if (magic != Magic) return false;

        type = header[4];
        flags = header[5];
        seq = BinaryPrimitives.ReadUInt32BigEndian(header[6..10]);
        ack = BinaryPrimitives.ReadUInt32BigEndian(header[10..14]);
        payloadLen = BinaryPrimitives.ReadUInt16BigEndian(header[14..16]);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            _cts.Cancel();
            try
            {
                // 送出關閉控制封包
                var (closeSock, closeRemoteEp, closeTag) = GetActiveTransportWithTag();
                var packet = new byte[HeaderSize];
                WriteHeader(packet.AsSpan(0, HeaderSize), TypeClose, closeTag, 0, Volatile.Read(ref _nextExpectedRecvSeq), 0);
                closeSock.SendTo(packet, closeRemoteEp);
            }
            catch { }

            lock (_heartbeatLock)
            {
                _pendingHeartbeatTcs?.TrySetResult(null);
                _pendingHeartbeatTcs = null;
            }

            _recvChannel.Writer.TryComplete();
            _sendWindowSemaphore.Dispose();
            _writeLock.Dispose();
            _pathControlLock.Dispose();
            try { _recvEpochCts.Dispose(); } catch { }
            _cts.Dispose();

            lock (_recvCoordLock)
            {
                if (_ownsSocket)
                {
                    try { _socket.Dispose(); } catch { }
                }
            }
        }

        base.Dispose(disposing);
    }
}
