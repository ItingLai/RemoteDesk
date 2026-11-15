using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using RemoteDesk.Common;
using RemoteDesk.Native;
using RemoteDesk.Net;

namespace RemoteDesk.Host;

/// <summary>連線授權確認結果（是否接受、是否加入長期可信裝置）。</summary>
internal sealed record AccessConfirmationResult(bool IsAccepted, bool TrustDevice);

internal readonly record struct FrameAckHandleResult(
    bool Accepted,
    uint? FrameSeq,
    FrameAckRejectReason RejectReason,
    ulong Generation,
    int InflightCountAfter);

internal enum VideoFrameSendOutcome : byte
{
    Sent = 0,
    SkippedStaleGeneration = 1,
    TransportFailed = 2,
}

internal sealed record FrameAckDiagnosticsSnapshot(
    FrameAckMode Mode,
    ulong Generation,
    int InflightCount,
    uint[] InflightSeqs,
    float LastFrameRttMs,
    long ValidAckCount,
    long StaleAckCount,
    long DuplicateAckCount,
    long UnknownAckCount,
    long InvalidLengthAckCount,
    long LegacyInSequenceAckCount,
    long InflightResetCount,
    string LastResetReason,
    FrameAckRejectReason LastRejectReason,
    bool HasAckedInGeneration,
    uint HighestAckedSeqInGeneration,
    long AckEventSignalCount = 0,
    long SupersededSendSkippedCount = 0);

/// <summary>主機端與 UI 的互動介面（介面層或無頭模式各自實作）。</summary>
internal interface IHostUi
{
    Task<bool> ConfirmAccessAsync(string viewerName, string viewerId, string address, string code, CancellationToken ct);

    Task<AccessConfirmationResult> ConfirmAccessExtendedAsync(
        string viewerName,
        string viewerId,
        string address,
        string code,
        string fingerprint,
        bool keyMismatch,
        CancellationToken ct)
    {
        return ConfirmAccessAsync(viewerName, viewerId, address, code, ct)
            .ContinueWith(t => new AccessConfirmationResult(t.Result, false));
    }

    void SessionStarted(HostSession session);
    void SessionEnded(HostSession session);
    void SetClipboardText(string text);
}

/// <summary>一個已通過驗證的控制連線：負責擷取畫面、送出影像/游標/輸入法狀態，並注入遠端送來的輸入。</summary>
internal sealed class HostSession : IDisposable
{
    public const string AllMonitors = Protocol.AllMonitors;
    public const int TelemetryWindowMs = 500;
    private const int MaxInflight = 2; // 基礎雙影格管線化 (Base Dual-Buffer Pipelining)，維持低延遲並可在高 RTT 健康的 BDP 條件下受控擴展
    private const int MaxDynamicInflight = 6; // 高 RTT (>80ms) 且無壅塞/無掉包/BDP 充裕時之動態視窗上限

    private readonly SecureChannel _ch;
    private readonly IHostUi _ui;
    private readonly CancellationTokenSource _cts = new();
    private readonly InputInjector _injector = new();
    private readonly ImeController _ime = new();
    private readonly CursorTracker _cursor = new();
    private readonly InputThread _input = new();
    private readonly AutoResetEvent _ackEvent = new(false);
    private readonly ConcurrentQueue<Action> _captureActions = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly record struct InflightFrameEntry(long SentAtMs, ulong Generation);

    private sealed class BoundedSeqWindow
    {
        private readonly int _capacity;
        private readonly Dictionary<uint, LinkedListNode<uint>> _map = new();
        private readonly LinkedList<uint> _order = new();

        public BoundedSeqWindow(int capacity = 128) => _capacity = capacity;
        public bool Contains(uint seq) => _map.ContainsKey(seq);

        public void Remove(uint seq)
        {
            if (_map.Remove(seq, out var node))
                _order.Remove(node);
        }

        public void Add(uint seq)
        {
            if (_map.TryGetValue(seq, out var existing))
            {
                _order.Remove(existing);
                _order.AddLast(existing);
                return;
            }

            var node = _order.AddLast(seq);
            _map[seq] = node;
            while (_order.Count > _capacity && _order.First is { } first)
            {
                _order.RemoveFirst();
                _map.Remove(first.Value);
            }
        }
    }

    private readonly object _inflightLock = new();
    private readonly object _videoSendOrderLock = new();
    private int _inflight;
    private readonly Dictionary<uint, InflightFrameEntry> _inflightFrames = new();
    private readonly LinkedList<long> _legacyInflightSentAt = new();
    private readonly BoundedSeqWindow _ackedSeqs = new(128);
    private readonly BoundedSeqWindow _staleSeqs = new(128);
    private ulong _inflightGeneration = 1;
    private bool _hasAckedInGen;
    private uint _highestAckedSeqInGen;
    private long _validFrameAckCount;
    private long _staleFrameAckCount;
    private long _duplicateFrameAckCount;
    private long _unknownFrameAckCount;
    private long _invalidFrameAckLengthCount;
    private long _legacyAckInSequenceModeCount;
    private long _inflightResetCount;
    private long _ackEventSignalCount;
    private long _supersededFrameSendSkippedCount;
    private string _lastResetReason = "";
    private FrameAckRejectReason _lastAckRejectReason = FrameAckRejectReason.None;
    private long _lastAckRejectLogAtMs = -2000;
    private volatile string? _requestedSelection;
    private volatile bool _keyframeRequested;
    private long _lastKeyframeSentAt;
    private long _lastFrameSentAt;
    private long _lastFrameAckReceivedAt;
    private float _lastFrameRttMs;
    private volatile int _quality = 70;
    private volatile int _maxFps = 30;
    private volatile bool _lossless = true;
    private volatile ResolutionMode _resolutionMode = ResolutionMode.Auto;
    private ResolutionMode? _requestedMode;
    private readonly object _configAckLock = new();
    private uint _ackedConfigVersion;
    private uint _latestConfigVersion;
    private ViewerTelemetrySnapshot? _lastViewerTelemetry;
    private long _lastViewerTelemetryAtMs;
    private int _effectiveMaxInflight = MaxInflight;
    private long _frameDropsTotal;
    private volatile ImeSyncMode _imeMode = ImeSyncMode.Bound;
    private volatile bool _pathRecovering;
    private int _pathRecoveryCompletedFlag;
    private int _pathRecoveryCount;
    private uint _currentPathEpoch;
    private volatile bool _sessionClosed;

    public bool IsPathRecovering => _pathRecovering;
    public int PathRecoveryCount => Volatile.Read(ref _pathRecoveryCount);
    public uint CurrentPathEpoch => Volatile.Read(ref _currentPathEpoch);
    public bool IsClosedOrPeerDisconnected => _sessionClosed || _cts.IsCancellationRequested;

    public HostStreamTelemetry? LatestTelemetry { get; private set; }

    // 輸入法綁定狀態（只在擷取執行緒存取）
    private ImeState? _boundIme;
    private ImeState? _lastReportedIme;
    private IntPtr _lastImeForeground;
    private long _imeGraceUntil;
    private long _lastImePoll;
    private string _lastCaret = "";

    public string ViewerName { get; }
    public string ViewerId { get; }
    public string RemoteAddress { get; }
    public FrameAckMode AckMode { get; }
    public bool SupportsFrameSeqAck => AckMode == FrameAckMode.Sequence;
    public DateTime StartedAt { get; } = DateTime.Now;
    public string CaptureMethod { get; private set; } = "";
    public bool AllowDxgi { get; set; } = true;
    internal Func<MonitorInfo?, bool, IScreenCapturer>? CapturerOverride { get; set; }
    internal Func<List<MonitorInfo>>? MonitorsOverride { get; set; }
    internal long ClockMsForTest => _clock.ElapsedMilliseconds;
    internal Action<uint, ulong>? BeforeVideoFrameSendHookForTest { get; set; }
    internal Func<byte[], bool>? VideoFrameSendTransportForTest { get; set; }

    public HostSession(SecureChannel ch, ClientHello hello, string remoteAddress, IHostUi ui)
    {
        _ch = ch;
        _ui = ui;
        ViewerName = hello.ViewerName;
        ViewerId = hello.ViewerId;
        RemoteAddress = remoteAddress;
        AckMode = (hello.Capabilities & ProtocolCapabilities.FrameSeqAck) != 0
            ? FrameAckMode.Sequence
            : FrameAckMode.Legacy;
    }

    public void Disconnect()
    {
        _sessionClosed = true;
        try
        {
            var pkt = new PacketWriter((byte)HostMsg.Disconnect, 1).ToArray();
            _ch.SendSync(pkt, timeoutMs: 300);
        }
        catch { }
        finally
        {
            _cts.Cancel();
            _ackEvent.Set();
            ClearInflightOnShutdown();
            _ch.Dispose();
        }
    }

    public Task SendClipboardAsync(string text) =>
        _ch.TrySendAsync(new PacketWriter((byte)HostMsg.Clipboard).Str(text).ToArray());

    public async Task RunAsync()
    {
        var captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "RemoteDesk-Capture", Priority = ThreadPriority.AboveNormal };
        captureThread.Start();
        try
        {
            await ReceiveLoopAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or EndOfStreamException)
        {
            Log.Info($"控制端 {ViewerName} 連線結束: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error("主機工作階段錯誤", ex);
        }
        finally
        {
            _cts.Cancel();
            _ackEvent.Set();
            captureThread.Join(150);
            ClearInflightOnShutdown();
            _input.Post(_injector.ReleaseAll);
            _input.Dispose();
            _ch.Dispose();
        }
    }

    private int GetInflightCount()
    {
        lock (_inflightLock) return _inflight;
    }

    private float GetLastFrameRttMs()
    {
        lock (_inflightLock) return _lastFrameRttMs;
    }

    private ulong RegisterSentFrameInflight(uint frameSeq, long sentAtMs)
    {
        lock (_inflightLock)
        {
            if (AckMode == FrameAckMode.Sequence)
            {
                _staleSeqs.Remove(frameSeq);
                _ackedSeqs.Remove(frameSeq);
                _inflightFrames[frameSeq] = new InflightFrameEntry(sentAtMs, _inflightGeneration);
                _inflight = _inflightFrames.Count;
            }
            else
            {
                _legacyInflightSentAt.AddLast(sentAtMs);
                _inflight = _legacyInflightSentAt.Count;
            }

            return _inflightGeneration;
        }
    }

    private void RollbackUnqueuedFrameInflight(uint frameSeq, ulong frameGen)
    {
        lock (_inflightLock)
        {
            if (AckMode == FrameAckMode.Sequence)
            {
                if (_inflightFrames.TryGetValue(frameSeq, out var entry) && entry.Generation == frameGen)
                {
                    _inflightFrames.Remove(frameSeq);
                    _inflight = _inflightFrames.Count;
                }
            }
            else if (_inflightGeneration == frameGen && _legacyInflightSentAt.Count > 0)
            {
                _legacyInflightSentAt.RemoveLast();
                _inflight = _legacyInflightSentAt.Count;
            }
        }
    }

    private int ResetInflightTracking(string reason)
    {
        int cleared;
        lock (_inflightLock)
        {
            if (AckMode == FrameAckMode.Sequence)
            {
                cleared = _inflightFrames.Count;
                foreach (uint pendingSeq in _inflightFrames.Keys)
                {
                    _ackedSeqs.Remove(pendingSeq);
                    _staleSeqs.Add(pendingSeq);
                }
                _inflightFrames.Clear();
            }
            else
            {
                cleared = _legacyInflightSentAt.Count;
                _legacyInflightSentAt.Clear();
            }

            _inflight = 0;
            _inflightGeneration++;
            _hasAckedInGen = false;
            _highestAckedSeqInGen = 0;
            _inflightResetCount++;
            _lastResetReason = reason;
        }

        return cleared;
    }

    private void ClearInflightOnShutdown()
    {
        lock (_inflightLock)
        {
            if (AckMode == FrameAckMode.Sequence)
            {
                if (_inflightFrames.Count == 0 && _inflight == 0) return;
                foreach (uint pendingSeq in _inflightFrames.Keys)
                {
                    _ackedSeqs.Remove(pendingSeq);
                    _staleSeqs.Add(pendingSeq);
                }
                _inflightFrames.Clear();
            }
            else
            {
                if (_legacyInflightSentAt.Count == 0 && _inflight == 0) return;
                _legacyInflightSentAt.Clear();
            }

            _inflight = 0;
            _inflightGeneration++;
            _hasAckedInGen = false;
            _highestAckedSeqInGen = 0;
            _inflightResetCount++;
            _lastResetReason = "SessionEnded";
        }
    }

    private FrameAckHandleResult HandleFrameAck(ReadOnlySpan<byte> payload)
    {
        long ackNow = _clock.ElapsedMilliseconds;
        bool wake = false;
        bool shouldLogReject = false;
        FrameAckHandleResult result;

        lock (_inflightLock)
        {
            if (AckMode == FrameAckMode.Legacy)
            {
                if (payload.Length != 0)
                {
                    _invalidFrameAckLengthCount++;
                    _lastAckRejectReason = FrameAckRejectReason.InvalidFrameAckLength;
                    if (ackNow - _lastAckRejectLogAtMs >= 1000)
                    {
                        _lastAckRejectLogAtMs = ackNow;
                        shouldLogReject = true;
                    }
                    result = new FrameAckHandleResult(false, null, FrameAckRejectReason.InvalidFrameAckLength, _inflightGeneration, _inflight);
                }
                else
                {
                    _lastFrameAckReceivedAt = ackNow;
                    bool hasSentAt;
                    long sentAt;
                    if (_legacyInflightSentAt.First is { } first)
                    {
                        sentAt = first.Value;
                        _legacyInflightSentAt.RemoveFirst();
                        hasSentAt = true;
                    }
                    else
                    {
                        sentAt = Volatile.Read(ref _lastFrameSentAt);
                        hasSentAt = sentAt > 0;
                    }

                    if (hasSentAt && ackNow >= sentAt)
                    {
                        float sample = ackNow - sentAt;
                        _lastFrameRttMs = _lastFrameRttMs <= 0 ? sample : (_lastFrameRttMs * 0.7f + sample * 0.3f);
                    }

                    _inflight = _legacyInflightSentAt.Count;
                    _validFrameAckCount++;
                    wake = true;
                    result = new FrameAckHandleResult(true, null, FrameAckRejectReason.None, _inflightGeneration, _inflight);
                }
            }
            else if (payload.Length == 0)
            {
                _legacyAckInSequenceModeCount++;
                _lastAckRejectReason = FrameAckRejectReason.LegacyAckInSequenceMode;
                if (ackNow - _lastAckRejectLogAtMs >= 1000)
                {
                    _lastAckRejectLogAtMs = ackNow;
                    shouldLogReject = true;
                }
                result = new FrameAckHandleResult(false, null, FrameAckRejectReason.LegacyAckInSequenceMode, _inflightGeneration, _inflight);
            }
            else if (payload.Length != 4)
            {
                _invalidFrameAckLengthCount++;
                _lastAckRejectReason = FrameAckRejectReason.InvalidFrameAckLength;
                if (ackNow - _lastAckRejectLogAtMs >= 1000)
                {
                    _lastAckRejectLogAtMs = ackNow;
                    shouldLogReject = true;
                }
                result = new FrameAckHandleResult(false, null, FrameAckRejectReason.InvalidFrameAckLength, _inflightGeneration, _inflight);
            }
            else
            {
                uint ackSeq = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                if (_inflightFrames.TryGetValue(ackSeq, out var entry) && entry.Generation == _inflightGeneration)
                {
                    _inflightFrames.Remove(ackSeq);
                    _inflight = _inflightFrames.Count;
                    _staleSeqs.Remove(ackSeq);
                    _ackedSeqs.Add(ackSeq);
                    _lastFrameAckReceivedAt = ackNow;

                    bool isNewerInGen = !_hasAckedInGen || unchecked((int)(ackSeq - _highestAckedSeqInGen)) > 0;
                    if (isNewerInGen)
                    {
                        _hasAckedInGen = true;
                        _highestAckedSeqInGen = ackSeq;
                        if (ackNow >= entry.SentAtMs)
                        {
                            float sample = ackNow - entry.SentAtMs;
                            _lastFrameRttMs = _lastFrameRttMs <= 0 ? sample : (_lastFrameRttMs * 0.7f + sample * 0.3f);
                        }
                    }

                    _validFrameAckCount++;
                    wake = true;
                    result = new FrameAckHandleResult(true, ackSeq, FrameAckRejectReason.None, _inflightGeneration, _inflight);
                }
                else
                {
                    FrameAckRejectReason reason;
                    if (_staleSeqs.Contains(ackSeq))
                    {
                        reason = FrameAckRejectReason.StaleFrameAck;
                        _staleFrameAckCount++;
                    }
                    else if (_ackedSeqs.Contains(ackSeq))
                    {
                        reason = FrameAckRejectReason.DuplicateFrameAck;
                        _duplicateFrameAckCount++;
                    }
                    else
                    {
                        reason = FrameAckRejectReason.UnknownFrameAck;
                        _unknownFrameAckCount++;
                    }

                    _lastAckRejectReason = reason;
                    if (ackNow - _lastAckRejectLogAtMs >= 1000)
                    {
                        _lastAckRejectLogAtMs = ackNow;
                        shouldLogReject = true;
                    }
                    result = new FrameAckHandleResult(false, ackSeq, reason, _inflightGeneration, _inflight);
                }
            }
        }

        if (wake)
        {
            Interlocked.Increment(ref _ackEventSignalCount);
            _ackEvent.Set();
        }
        else if (shouldLogReject)
        {
            Log.Warn($"[HostSession] 忽略非法或過期 FrameAck (Mode={AckMode}, Reason={result.RejectReason}, Seq={(result.FrameSeq.HasValue ? result.FrameSeq.Value.ToString() : "N/A")}, PayloadLen={payload.Length}, Gen={result.Generation}, Inflight={result.InflightCountAfter})");
        }

        return result;
    }

    internal FrameAckDiagnosticsSnapshot GetFrameAckDiagnosticsSnapshot()
    {
        lock (_inflightLock)
        {
            return new FrameAckDiagnosticsSnapshot(
                AckMode,
                _inflightGeneration,
                _inflight,
                _inflightFrames.Keys.ToArray(),
                _lastFrameRttMs,
                _validFrameAckCount,
                _staleFrameAckCount,
                _duplicateFrameAckCount,
                _unknownFrameAckCount,
                _invalidFrameAckLengthCount,
                _legacyAckInSequenceModeCount,
                _inflightResetCount,
                _lastResetReason,
                _lastAckRejectReason,
                _hasAckedInGen,
                _highestAckedSeqInGen,
                Interlocked.Read(ref _ackEventSignalCount),
                Interlocked.Read(ref _supersededFrameSendSkippedCount));
        }
    }

    internal bool IsSeqInStaleLruForTest(uint frameSeq)
    {
        lock (_inflightLock) return _staleSeqs.Contains(frameSeq);
    }

    internal bool IsSeqInAckedLruForTest(uint frameSeq)
    {
        lock (_inflightLock) return _ackedSeqs.Contains(frameSeq);
    }

    internal VideoFrameSendOutcome SendRegisteredFrameForTest(uint frameSeq, ulong frameGen, byte[] serialized) =>
        SendRegisteredVideoFrame(frameSeq, frameGen, serialized);

    private VideoFrameSendOutcome SendRegisteredVideoFrame(uint frameSeq, ulong frameGen, byte[] serialized)
    {
        BeforeVideoFrameSendHookForTest?.Invoke(frameSeq, frameGen);
        lock (_videoSendOrderLock)
        {
            if (_cts.IsCancellationRequested)
            {
                RollbackUnqueuedFrameInflight(frameSeq, frameGen);
                return VideoFrameSendOutcome.TransportFailed;
            }

            lock (_inflightLock)
            {
                bool stillActive = !_pathRecovering && _inflightGeneration == frameGen && (AckMode == FrameAckMode.Sequence
                    ? (_inflightFrames.TryGetValue(frameSeq, out var entry) && entry.Generation == frameGen)
                    : _legacyInflightSentAt.Count > 0);
                if (!stillActive)
                {
                    if (AckMode == FrameAckMode.Sequence && _inflightFrames.TryGetValue(frameSeq, out var e) && e.Generation == frameGen)
                    {
                        _inflightFrames.Remove(frameSeq);
                        _staleSeqs.Add(frameSeq);
                        _inflight = _inflightFrames.Count;
                    }
                    Interlocked.Increment(ref _supersededFrameSendSkippedCount);
                    return VideoFrameSendOutcome.SkippedStaleGeneration;
                }
            }

            // _inflightLock 已於進入網路 I/O 前釋放，避免慢速傳輸阻塞 ReceiveLoop 的 ACK / NACK 處理；
            // _videoSendOrderLock 僅序列化影像幀寫入順序，確保舊世代已進入 I/O 的影格必先完成，不致尾隨於新世代關鍵影格之後。
            bool sent = VideoFrameSendTransportForTest != null
                ? VideoFrameSendTransportForTest(serialized)
                : Send(serialized);
            if (!sent)
            {
                RollbackUnqueuedFrameInflight(frameSeq, frameGen);
                return VideoFrameSendOutcome.TransportFailed;
            }

            return VideoFrameSendOutcome.Sent;
        }
    }

    internal ulong RegisterFrameSentForTest(uint frameSeq, long? sentAtMsOverride = null)
    {
        long ts = sentAtMsOverride ?? _clock.ElapsedMilliseconds;
        Volatile.Write(ref _lastFrameSentAt, ts);
        return RegisterSentFrameInflight(frameSeq, ts);
    }

    internal FrameAckHandleResult InjectFrameAckForTest(byte[] msg)
    {
        if (msg.Length == 0 || msg[0] != (byte)ViewerMsg.FrameAck)
            throw new ArgumentException("訊息不是 ViewerMsg.FrameAck", nameof(msg));
        return HandleFrameAck(msg.AsSpan(1));
    }

    internal int ResetInflightForTest(string reason) => ResetInflightTracking(reason);

    /// <summary>
    /// Phase 5-4：P2P 路徑失效重連開始時呼叫。
    /// 先以 _videoSendOrderLock 確保進行中的影格寫入已完成並阻擋新影格排入，同時遞增 ACK 世代使所有舊路徑延遲 FrameAck 自動作廢；
    /// 嚴禁重置單調遞增之 FrameSeq。
    /// </summary>
    public int BeginPathRecovery()
    {
        lock (_videoSendOrderLock)
        {
            _pathRecovering = true;
            int cleared = ResetInflightTracking("PathRecovery");
            _ackEvent.Set();
            return cleared;
        }
    }

    /// <summary>
    /// Phase 5-4：P2P 路徑切換成功後呼叫。
    /// 恢復擷取管線、觸發遞增 StreamConfigVersion 與強制 Keyframe（不重置 FrameSeq），並重置 UDP 遙測視窗基準避免假性壅塞降級。
    /// </summary>
    public void CompletePathRecovery(uint newPathEpoch)
    {
        lock (_videoSendOrderLock)
        {
            Volatile.Write(ref _currentPathEpoch, newPathEpoch);
            Interlocked.Increment(ref _pathRecoveryCount);
            ResetInflightTracking("PathRecoveryComplete");
            _pathRecovering = false;
            Interlocked.Exchange(ref _pathRecoveryCompletedFlag, 1);
            _keyframeRequested = true;
            _ackEvent.Set();
        }
    }

    // ---------------------------------------------------------------- 接收
    private async Task ReceiveLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await _ch.ReceiveAsync(ct).ConfigureAwait(false);
                if (msg.Length == 0) continue;
                Dispatch(msg);
            }
        }
        finally
        {
            _cts.Cancel();
            _ackEvent.Set();
        }
    }

    private void Dispatch(byte[] msg)
    {
        var r = new PacketReader(msg.AsSpan(1));
        switch ((ViewerMsg)msg[0])
        {
            case ViewerMsg.Disconnect:
            {
                Log.Info($"控制端 {ViewerName} 要求中斷連線");
                _sessionClosed = true;
                _cts.Cancel();
                _ackEvent.Set();
                _ch.Dispose();
                break;
            }
            case ViewerMsg.MouseMove:
            {
                int x = r.I32(), y = r.I32();
                _input.Post(() => _injector.MouseMove(x, y));
                break;
            }
            case ViewerMsg.MouseButton:
            {
                var b = (MouseButtonId)r.U8(); bool down = r.Bool(); int x = r.I32(), y = r.I32();
                _input.Post(() => _injector.MouseButton(b, down, x, y));
                break;
            }
            case ViewerMsg.MouseWheel:
            {
                int delta = r.I32(); bool h = r.Bool(); int x = r.I32(), y = r.I32();
                _input.Post(() => _injector.Wheel(delta, h, x, y));
                break;
            }
            case ViewerMsg.Key:
            {
                ushort vk = r.U16(), scan = r.U16(); var flags = (KeyFlags)r.U8();
                _input.Post(() => _injector.Key(vk, scan, flags));
                break;
            }
            case ViewerMsg.Text:
            {
                var text = r.Str();
                _input.Post(() => _injector.Text(text));
                break;
            }
            case ViewerMsg.LockKeys:
            {
                var s = r.U8();
                _input.Post(() => _injector.SyncLockKeys(s));
                break;
            }
            case ViewerMsg.Special:
            {
                var a = (SpecialAction)r.U8();
                _input.Post(() => _injector.Special(a));
                break;
            }
            case ViewerMsg.ReleaseAll:
                _input.Post(_injector.ReleaseAll);
                break;
            case ViewerMsg.FrameAck:
            {
                HandleFrameAck(msg.AsSpan(1));
                break;
            }
            case ViewerMsg.SelectMonitor:
                _requestedSelection = r.Str();
                _ackEvent.Set();
                break;
            case ViewerMsg.VideoSettings:
                _quality = Math.Clamp((int)r.U8(), 10, 100);
                _maxFps = Math.Clamp((int)r.U8(), 1, 120);
                _lossless = r.Bool();
                if (r.Remaining > 0)
                {
                    var m = (ResolutionMode)r.U8();
                    if (Enum.IsDefined(m))
                    {
                        _resolutionMode = m;
                        _captureActions.Enqueue(() => _requestedMode = m);
                        _ackEvent.Set();
                    }
                }
                break;
            case ViewerMsg.StreamConfigAck:
            {
                var (cfgVer, _, _) = MessageCodec.ReadStreamConfigAck(ref r);
                lock (_configAckLock)
                {
                    uint latestCfg = Volatile.Read(ref _latestConfigVersion);
                    if (cfgVer > _ackedConfigVersion && cfgVer <= latestCfg)
                        _ackedConfigVersion = cfgVer;
                }
                _ackEvent.Set();
                break;
            }
            case ViewerMsg.ViewerTelemetry:
            {
                var vt = MessageCodec.ReadViewerTelemetry(ref r);
                Volatile.Write(ref _lastViewerTelemetry, vt);
                Volatile.Write(ref _lastViewerTelemetryAtMs, _clock.ElapsedMilliseconds);
                break;
            }
            case ViewerMsg.RequestKeyframe:
                _keyframeRequested = true;
                // P1-03 + FrameSeq：顯式 NACK 重同步請求，原子清空現有世代 Inflight 並遞增世代以拒絕遲到舊 ACK。
                ResetInflightTracking("RequestKeyframe");
                _ackEvent.Set(); // 立即喚醒擷取迴圈產生 FullFrame 基準
                break;
            case ViewerMsg.Ping:
            {
                var ts = r.I64();
                _ = _ch.TrySendAsync(new PacketWriter((byte)HostMsg.Pong, 16).I64(ts).ToArray(), highPriority: true);
                break;
            }
            case ViewerMsg.Clipboard:
                _ui.SetClipboardText(r.Str());
                break;
            case ViewerMsg.ImeMode:
            {
                var mode = (ImeSyncMode)r.U8();
                _imeMode = mode;
                _captureActions.Enqueue(() => { _boundIme = null; _lastReportedIme = null; });
                break;
            }
            case ViewerMsg.ImeSet:
            {
                var s = MessageCodec.ReadIme(ref r);
                _captureActions.Enqueue(() => ApplyBoundIme(s));
                break;
            }
            default:
                Log.Warn($"未知訊息 0x{msg[0]:X2}");
                break;
        }
    }

    // ---------------------------------------------------------------- 輸入法綁定
    private void ApplyBoundIme(ImeState s)
    {
        if (_imeMode != ImeSyncMode.Bound) return;
        _boundIme = s;
        _ime.Apply(s);
        _imeGraceUntil = _clock.ElapsedMilliseconds + 400;
        _lastReportedIme = s;
    }

    private void PollIme()
    {
        long now = _clock.ElapsedMilliseconds;
        if (now - _lastImePoll < 200) return;
        _lastImePoll = now;

        if (_imeMode == ImeSyncMode.Local)
        {
            _ime.ForceAlphanumeric();
            var caret = ImeController.GetCaretScreenRect();
            var key = caret == null ? "" : $"{caret.Value.Left},{caret.Value.Top},{caret.Value.Height}";
            if (key != _lastCaret)
            {
                _lastCaret = key;
                var origin = _injector.Origin;
                var w = new PacketWriter((byte)HostMsg.Caret, 16).Bool(caret != null);
                if (caret != null) w.I32(caret.Value.Left - origin.X).I32(caret.Value.Top - origin.Y).I32(caret.Value.Height);
                else w.I32(0).I32(0).I32(0);
                Send(w.ToArray(), highPriority: true);
            }
            return;
        }

        var cur = _ime.Read();
        if (cur == null) return;
        var fg = _ime.LastForeground;
        bool foregroundChanged = fg != _lastImeForeground;
        _lastImeForeground = fg;

        if (now < _imeGraceUntil) return;

        if (foregroundChanged && _boundIme is { } bound && !cur.Value.SameMode(bound))
        {
            // 遠端切換到另一個視窗：維持綁定狀態，不讓輸入法狀態「跳掉」
            _ime.Apply(bound);
            _imeGraceUntil = now + 400;
            return;
        }

        if (_lastReportedIme is not { } last || !cur.Value.SameMode(last) || cur.Value.KeyboardLayout != last.KeyboardLayout)
        {
            // 使用者在遠端切換了中/英或鍵盤配置 → 成為新的綁定狀態並通知控制端同步本機
            _boundIme = cur;
            _lastReportedIme = cur;
            Send(MessageCodec.Ime((byte)HostMsg.ImeState, cur.Value), highPriority: true);
        }
    }

    // ---------------------------------------------------------------- 擷取
    internal static int ComputeEffectiveMaxInflight(
        float rttMs,
        int targetFps,
        float effectiveWaitMs,
        int sendQueuePending,
        float udpLossRatePercent,
        float retransRatePercent,
        int rtoDelta,
        float arqFinalSuccessRatePercent,
        float inflightRatio,
        bool isCongested,
        int avgFrameSizeBytes,
        float estimatedBandwidthMbps)
    {
        if (rttMs < 25f
            || effectiveWaitMs >= 15f
            || sendQueuePending > 0
            || udpLossRatePercent > 1.0f
            || retransRatePercent > 1.5f
            || rtoDelta > 0
            || arqFinalSuccessRatePercent < 99.0f
            || inflightRatio >= 0.35f
            || isCongested)
        {
            return MaxInflight;
        }

        if (avgFrameSizeBytes <= 0 && estimatedBandwidthMbps <= 0f)
        {
            return MaxInflight;
        }

        // 加上少量編解碼與封包步調處理時間餘裕 (~12ms)，讓中等 RTT (45~79ms) 與高更新率 (60 FPS) 管線不再被硬鎖在 2 幀而卡頓
        double pipelineCycleSec = (rttMs + 12.0) / 1000.0;
        int desired = Math.Clamp((int)Math.Ceiling(Math.Max(1, targetFps) * pipelineCycleSec), MaxInflight, MaxDynamicInflight);
        double budgetBytes = 192.0 * 1024.0; // 保守控制在 ReliableUDP MaxWindowSize (307KB) 與 TCP 256KB SO_SNDBUF 之下
        if (estimatedBandwidthMbps > 0f)
        {
            double bdpBytes = estimatedBandwidthMbps * 125_000.0 * (rttMs / 1000.0);
            budgetBytes = Math.Min(budgetBytes, bdpBytes * 0.75);
        }

        int effectiveAvgFrameBytes = avgFrameSizeBytes > 0 ? avgFrameSizeBytes : 32 * 1024;
        int maxByBytes = Math.Max(MaxInflight, (int)(budgetBytes / effectiveAvgFrameBytes));

        return Math.Clamp(Math.Min(desired, maxByBytes), MaxInflight, MaxDynamicInflight);
    }

    private bool Send(byte[] data, bool highPriority = false)
    {
        try
        {
            _ch.SendAsync(data, highPriority, _cts.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            _cts.Cancel();
            return false;
        }
    }

    private void CaptureLoop()
    {
        Win32.timeBeginPeriod(1);
        Win32.SetThreadExecutionState(Win32.ES_CONTINUOUS | Win32.ES_DISPLAY_REQUIRED | Win32.ES_SYSTEM_REQUIRED);
        IScreenCapturer? cap = null;
        FrameEncoder? enc = null;
        AdaptiveResolutionController? adaptiveCtrl = null;
        List<MonitorInfo> monitors = MonitorsOverride?.Invoke() ?? MonitorEnumerator.GetMonitors();
        string selection = (monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault())?.DeviceName ?? AllMonitors;
        bool needRecreate = true;
        long lastMonitorCheck = 0, lastFrameAt = -1000, lastCaptureAt = -1000;
        long lastSampleAt = 0, lastTelemetryPushAt = 0, lastTelemetryLogAt = 0;
        uint seq = 0;
        uint frameVersion = 0;
        uint streamConfigVersion = 0;
        uint configKeyframeSentForVersion = 0;
        long configSentAt = 0;
        bool configRetrySent = false;
        bool hasCapturedOnce = false;
        bool pendingSwitchKeyframe = false;
        int failures = 0;
        long dxgiRetryAt = 0;

        // 遙測窗口計數器
        int framesSentInWindow = 0;
        int capturesInWindow = 0;
        int captureNoUpdateInWindow = 0;
        int sourceUpdatesInWindow = 0;
        long frameBytesInWindow = 0;
        double encodeMsInWindow = 0;
        double transportStallMsInWindow = 0;
        long dropsInWindow = 0;
        int lastAvgFrameSizeBytes = 0;
        float lastActualSendFps = 0f;
        float lastCaptureFps = 0f;
        float lastSourceFps = 0f;
        float lastAvgEncodeMs = 0f;
        float lastEstimatedBwMbps = 0f;
        bool lastCongestionWarning = false;
        int congestionQualityReduction = 0;
        float lastPacingWaitMs = 0f;
        float lastSocketWriteMs = 0f;
        double prevTotalPacingMs = 0;
        double prevTotalSocketWriteMs = 0;

        long prevPktSent = 0, prevUniqueLost = 0, prevRetrans = 0, prevAcked = 0;
        long prevRtoTimeouts = 0, prevFastRetrans = 0, prevBytesAcked = 0, prevWireBytesSent = 0, prevUnrecoverableLost = 0;
        long lastSampleBytesAcked = 0;
        void SyncUdpTelemetryBaseline(ReliableUdpStream? udpSource)
        {
            if (udpSource == null) return;
            prevPktSent = udpSource.TotalPacketsSent;
            prevUniqueLost = udpSource.UniquePacketsLost;
            prevRetrans = udpSource.TotalRetransmissions;
            prevAcked = udpSource.UniquePacketsAcked;
            prevRtoTimeouts = udpSource.TotalRtoTimeouts;
            prevFastRetrans = udpSource.TotalFastRetransmits;
            prevBytesAcked = udpSource.TotalPayloadBytesAcked;
            prevWireBytesSent = udpSource.TotalWireBytesSent;
            prevUnrecoverableLost = udpSource.TotalUnrecoverableLost;
            lastSampleBytesAcked = prevBytesAcked;
            prevTotalPacingMs = udpSource.TotalPacingWaitMs;
            prevTotalSocketWriteMs = udpSource.TotalSocketWriteDurationMs;
        }
        SyncUdpTelemetryBaseline(_ch.UnderlyingStream as ReliableUdpStream);

        void TriggerConfigSwitch(AdaptiveDecision d, long ts)
        {
            if (enc == null || cap == null || adaptiveCtrl == null) return;
            streamConfigVersion++;
            Volatile.Write(ref _latestConfigVersion, streamConfigVersion);
            enc.ConfigureStreamResolution(d.Profile.StreamWidth, d.Profile.StreamHeight);
            configSentAt = ts;
            configRetrySent = false;
            configKeyframeSentForVersion = 0;
            pendingSwitchKeyframe = true;
            ResetInflightTracking("ConfigSwitch");
            Send(MessageCodec.StreamConfig(
                streamConfigVersion,
                cap.Width,
                cap.Height,
                d.Profile.StreamWidth,
                d.Profile.StreamHeight,
                d.NewLevel,
                adaptiveCtrl.Mode,
                d.Reason), highPriority: true);
        }

        try
        {
            DesktopHelper.SyncThreadDesktop();
            Send(MessageCodec.MonitorList(monitors, selection));

            try
            {
                var wpBytes = DesktopHelper.GetDesktopWallpaperBytes();
                if (wpBytes != null && wpBytes.Length > 0)
                {
                    Send(MessageCodec.Wallpaper(wpBytes));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("讀取並傳送被控端桌布失敗: " + ex.Message);
            }

            while (!_cts.IsCancellationRequested)
            {
                long now = _clock.ElapsedMilliseconds;
                while (_captureActions.TryDequeue(out var action))
                {
                    try { action(); } catch (Exception ex) { Log.Warn("擷取執行緒動作失敗: " + ex.Message); }
                }

                // 螢幕配置變更偵測（插拔螢幕、改解析度）
                if (now - lastMonitorCheck > 1000)
                {
                    lastMonitorCheck = now;
                    var m = MonitorsOverride?.Invoke() ?? MonitorEnumerator.GetMonitors();
                    if (!MonitorEnumerator.SameLayout(m, monitors))
                    {
                        Log.Info($"偵測到螢幕配置變更：{m.Count} 個螢幕");
                        monitors = m;
                        if (selection != AllMonitors && monitors.All(x => x.DeviceName != selection))
                            selection = (monitors.FirstOrDefault(x => x.Primary) ?? monitors.FirstOrDefault())?.DeviceName ?? AllMonitors;
                        Send(MessageCodec.MonitorList(monitors, selection));
                        needRecreate = true;
                    }
                }

                var req = _requestedSelection;
                if (req != null)
                {
                    _requestedSelection = null;
                    if (req == AllMonitors || monitors.Any(x => x.DeviceName == req))
                    {
                        if (req != selection)
                        {
                            selection = req;
                            needRecreate = true;
                            Send(MessageCodec.MonitorList(monitors, selection), highPriority: true);
                        }
                    }
                }

                if (needRecreate || cap == null)
                {
                    cap?.Dispose();
                    cap = null;
                    var mon = selection == AllMonitors ? null : monitors.FirstOrDefault(x => x.DeviceName == selection);
                    try
                    {
                        cap = CapturerOverride?.Invoke(mon, AllowDxgi && now >= dxgiRetryAt)
                            ?? CapturerFactory.Create(mon, AllowDxgi && now >= dxgiRetryAt);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("建立擷取器失敗: " + ex.Message);
                        Thread.Sleep(500);
                        continue;
                    }
                    CaptureMethod = cap.Name;
                    var bounds = mon?.Bounds ?? MonitorEnumerator.VirtualScreen;
                    _injector.Origin = bounds.Location;
                    enc = new FrameEncoder(cap.Width, cap.Height);
                    adaptiveCtrl = new AdaptiveResolutionController(cap.Width, cap.Height, _resolutionMode);
                    streamConfigVersion++;
                    Volatile.Write(ref _latestConfigVersion, streamConfigVersion);
                    lock (_configAckLock) { _ackedConfigVersion = streamConfigVersion; }
                    configKeyframeSentForVersion = 0;
                    hasCapturedOnce = false;
                    pendingSwitchKeyframe = false;
                    ResetInflightTracking("CapturerRecreate");
                    _cursor.Reset();
                    _lastCaret = "~";
                    Send(new PacketWriter((byte)HostMsg.StreamInfo, 64)
                        .Str(selection).I32(cap.Width).I32(cap.Height).I32(bounds.X).I32(bounds.Y).Str(cap.Name).ToArray(), highPriority: true);
                    Send(MessageCodec.StreamConfig(
                        streamConfigVersion,
                        cap.Width,
                        cap.Height,
                        adaptiveCtrl.CurrentProfile.StreamWidth,
                        adaptiveCtrl.CurrentProfile.StreamHeight,
                        adaptiveCtrl.CurrentLevel,
                        adaptiveCtrl.Mode,
                        adaptiveCtrl.LastReason), highPriority: true);
                    Log.Info($"開始擷取 {selection} {cap.Width}x{cap.Height}（{cap.Name}）");
                    needRecreate = false;
                }

                // 使用者切換解析度控制模式 (Auto / Original / Performance)
                if (_requestedMode is { } reqMode && adaptiveCtrl != null)
                {
                    _requestedMode = null;
                    var modeDecision = adaptiveCtrl.SetMode(reqMode, now);
                    if (modeDecision.Changed)
                    {
                        TriggerConfigSwitch(modeDecision, now);
                    }
                }

                // 檢查 StreamConfigAck 是否逾時，若未回覆則重送一次後防卡死放行
                uint currentAckedConfig;
                lock (_configAckLock) { currentAckedConfig = _ackedConfigVersion; }
                if (currentAckedConfig < streamConfigVersion && (now - configSentAt) >= 2000 && adaptiveCtrl != null && enc != null && cap != null)
                {
                    if (!configRetrySent)
                    {
                        configRetrySent = true;
                        configSentAt = now;
                        configKeyframeSentForVersion = 0;
                        pendingSwitchKeyframe = true;
                        ResetInflightTracking("ConfigSwitchRetry");
                        Log.Warn($"[HostSession] StreamConfig v{streamConfigVersion} ACK 逾時 (2000ms)，重送組態並重新發送 Keyframe。");
                        Send(MessageCodec.StreamConfig(
                            streamConfigVersion,
                            cap.Width,
                            cap.Height,
                            adaptiveCtrl.CurrentProfile.StreamWidth,
                            adaptiveCtrl.CurrentProfile.StreamHeight,
                            adaptiveCtrl.CurrentLevel,
                            adaptiveCtrl.Mode,
                            adaptiveCtrl.LastReason), highPriority: true);
                    }
                    else
                    {
                        lock (_configAckLock) { _ackedConfigVersion = streamConfigVersion; }
                        pendingSwitchKeyframe = true;
                        enc.RequestKeyframe();
                    }
                }

                // 自適應解析度與遙測定期評估 (每 500ms 取樣一次，每 1000ms 或狀態切換時推送給 Viewer)
                if (adaptiveCtrl != null && (now - lastSampleAt >= TelemetryWindowMs || lastSampleAt == 0))
                {
                    double dtSec = lastSampleAt > 0 ? Math.Max(0.1, (now - lastSampleAt) / 1000.0) : 0.5;
                    lastSampleAt = now;

                    float actualSendFps = (float)(framesSentInWindow / dtSec);
                    float captureFps = (float)(capturesInWindow / dtSec);
                    float sourceFps = (float)(sourceUpdatesInWindow / dtSec);
                    lastActualSendFps = actualSendFps;
                    lastCaptureFps = captureFps;
                    lastSourceFps = sourceFps;
                    if (framesSentInWindow > 0)
                    {
                        lastAvgFrameSizeBytes = (int)(frameBytesInWindow / framesSentInWindow);
                        lastAvgEncodeMs = (float)(encodeMsInWindow / framesSentInWindow);
                    }
                    else
                    {
                        // P1-04: 靜態桌面無影格送出時歸零，禁止永久鎖存上一張活躍影格之大小與編碼耗時
                        lastAvgFrameSizeBytes = 0;
                        lastAvgEncodeMs = 0f;
                    }

                    bool isStreamActive = framesSentInWindow >= 2;
                    int winFramesSent = framesSentInWindow;
                    long winFrameBytes = frameBytesInWindow;
                    int winNoUpdate = captureNoUpdateInWindow;
                    double winStallMs = transportStallMsInWindow;

                    framesSentInWindow = 0;
                    capturesInWindow = 0;
                    captureNoUpdateInWindow = 0;
                    sourceUpdatesInWindow = 0;
                    frameBytesInWindow = 0;
                    encodeMsInWindow = 0;
                    transportStallMsInWindow = 0;
                    long droppedDelta = dropsInWindow;
                    dropsInWindow = 0;

                    var rawVt = Volatile.Read(ref _lastViewerTelemetry);
                    long vtAgeMs = now - Volatile.Read(ref _lastViewerTelemetryAtMs);
                    var vt = (rawVt != null && vtAgeMs <= 1500) ? rawVt : null;
                    var udp = _ch.UnderlyingStream as ReliableUdpStream;
                    bool isUdp = udp != null;

                    var chWait = _ch.ConsumeWaitWindowSnapshot();
                    var udpWait = udp != null ? udp.ConsumeWaitWindowSnapshot() : default;
                    float waitLastMs = Math.Max(chWait.LastMs, udpWait.LastMs);
                    float waitAvgMs = Math.Max(chWait.EffectiveAverageWaitMs, udpWait.EffectiveAverageWaitMs);
                    float effectiveWaitMs = Math.Max(chWait.EffectiveWaitMs, udpWait.EffectiveWaitMs);

                    float udpLossRate = 0f, retransRate = 0f, arqSuccessRate = 100f;
                    float inflightRatio;
                    int inflightPkts = GetInflightCount();
                    int curEffectiveMaxInflight = Volatile.Read(ref _effectiveMaxInflight);
                    int maxWin = curEffectiveMaxInflight;
                    int rtoDelta = 0, fastDelta = 0;
                    float rttMs = GetLastFrameRttMs();
                    float currentRtoMs = 0f;
                    float goodputMbps = 0f;
                    float wireThroughputMbps = 0f;
                    long dAckedWindow = 0;
                    float dSockWrAvgMs = 0f;

                    if (udp != null)
                    {
                        long curSent = udp.TotalPacketsSent;
                        long curUniqueLost = udp.UniquePacketsLost;
                        long curRetrans = udp.TotalRetransmissions;
                        long curAcked = udp.UniquePacketsAcked;
                        long curRto = udp.TotalRtoTimeouts;
                        long curFast = udp.TotalFastRetransmits;
                        long curBytesAcked = udp.TotalPayloadBytesAcked;
                        long curWireSent = udp.TotalWireBytesSent;
                        long curUnrecoverable = udp.TotalUnrecoverableLost;
                        double curTotalPacingMs = udp.TotalPacingWaitMs;
                        double curTotalSockWrMs = udp.TotalSocketWriteDurationMs;

                        long dSent = Math.Max(0, curSent - prevPktSent);
                        long dUniqueLost = Math.Max(0, curUniqueLost - prevUniqueLost);
                        long dRetrans = Math.Max(0, curRetrans - prevRetrans);
                        long dAcked = Math.Max(0, curAcked - prevAcked);
                        dAckedWindow = dAcked;
                        rtoDelta = (int)Math.Max(0, curRto - prevRtoTimeouts);
                        fastDelta = (int)Math.Max(0, curFast - prevFastRetrans);
                        long dBytesAcked = Math.Max(0, curBytesAcked - lastSampleBytesAcked);
                        long dWireSent = Math.Max(0, curWireSent - prevWireBytesSent);
                        long dUnrecoverable = Math.Max(0, curUnrecoverable - prevUnrecoverableLost);
                        double dPacingMs = Math.Max(0.0, curTotalPacingMs - prevTotalPacingMs);
                        double dSockWrMs = Math.Max(0.0, curTotalSockWrMs - prevTotalSocketWriteMs);

                        prevPktSent = curSent;
                        prevUniqueLost = curUniqueLost;
                        prevRetrans = curRetrans;
                        prevAcked = curAcked;
                        prevRtoTimeouts = curRto;
                        prevFastRetrans = curFast;
                        lastSampleBytesAcked = curBytesAcked;
                        prevWireBytesSent = curWireSent;
                        prevUnrecoverableLost = curUnrecoverable;
                        prevTotalPacingMs = curTotalPacingMs;
                        prevTotalSocketWriteMs = curTotalSockWrMs;

                        lastPacingWaitMs = winFramesSent > 0 ? (float)(dPacingMs / winFramesSent) : 0f;
                        dSockWrAvgMs = winFramesSent > 0 ? (float)(dSockWrMs / winFramesSent) : 0f;
                        lastSocketWriteMs = dSockWrAvgMs;

                        udpLossRate = dSent > 0 ? Math.Min(100f, (float)(dUniqueLost * 100.0 / dSent)) : 0f;
                        retransRate = dSent > 0 ? Math.Min(100f, (float)(dRetrans * 100.0 / dSent)) : 0f;

                        // P1-04: 真實計算視窗內 ARQ 最終交付成功率（分離初始 UdpRawLossRate 與實際未恢復/反覆超時封包）
                        int agedUnacked = udp.GetAgedUnackedCount(minRetries: 2);
                        long unrecovered = dUnrecoverable + (dAcked == 0 && rtoDelta > 0 ? Math.Max(agedUnacked, rtoDelta) : agedUnacked);
                        long totalOutcomes = dAcked + unrecovered;
                        arqSuccessRate = totalOutcomes > 0
                            ? Math.Clamp((float)(dAcked * 100.0 / totalOutcomes), 0f, 100f)
                            : 100f;

                        inflightPkts = udp.CurrentInflightPackets;
                        maxWin = udp.MaxInflightPackets;
                        inflightRatio = maxWin > 0 ? (float)inflightPkts / maxWin : 0f;
                        rttMs = (float)(udp.CurrentSrttMs > 0 ? udp.CurrentSrttMs : rttMs);
                        currentRtoMs = udp.CurrentRtoMs;

                        goodputMbps = (float)(dBytesAcked * 8.0 / dtSec / 1_000_000.0);
                        wireThroughputMbps = (float)(dWireSent * 8.0 / dtSec / 1_000_000.0);

                        float deliverySample = udp.ConsumeDeliveryBandwidthSampleMbps();
                        if (winFramesSent == 0 && dAcked == 0)
                        {
                            lastEstimatedBwMbps = 0f;
                        }
                        else if (deliverySample > 0f)
                        {
                            lastEstimatedBwMbps = lastEstimatedBwMbps <= 0 ? deliverySample : (lastEstimatedBwMbps * 0.6f + deliverySample * 0.4f);
                        }
                        else if (dAcked == 0)
                        {
                            lastEstimatedBwMbps = 0f;
                        }
                    }
                    else
                    {
                        lastPacingWaitMs = 0f;
                        lastSocketWriteMs = winFramesSent > 0 ? _ch.LastWriteDurationMs : 0f;
                        // TCP 無 UDP 封包級滑動視窗佔用率；健康高 RTT 下僅因等待 FrameAck 使 _inflight/2=1.0 並非傳輸層壅塞，
                        // 僅在通道實際出現寫入阻塞或排隊背壓時才將管線佔用納入 InflightRatio，避免誤觸發 hardStall 與鎖死動態管線擴展。
                        bool tcpHasBackpressure = _ch.PendingLowPriorityCount > 0 || effectiveWaitMs >= 10f || _ch.LastWriteDurationMs >= 25f;
                        inflightRatio = tcpHasBackpressure
                            ? (float)GetInflightCount() / Math.Max(1, curEffectiveMaxInflight)
                            : 0f;
                        goodputMbps = (float)(winFrameBytes * 8.0 / dtSec / 1_000_000.0);
                    }

                    bool isStreamIdle = winFramesSent == 0 && dAckedWindow == 0;
                    var (avgMbps, peakMbps) = _ch.GetWindowedThroughputMbps(TelemetryWindowMs);
                    if (!isUdp) wireThroughputMbps = avgMbps;

                    bool hasTransportQueueSignal =
                        effectiveWaitMs >= 10f
                        || _ch.PendingLowPriorityCount >= 1
                        || udpLossRate > 1.0f
                        || retransRate > 1.5f
                        || rtoDelta > 0
                        || inflightRatio >= 0.25f
                        || (isUdp
                            ? (udp!.LastSemaphoreWaitMs >= 10 || dSockWrAvgMs >= 25f || (winFramesSent > 0 && udp.LastSocketWriteDurationMs >= 30f))
                            : _ch.LastWriteDurationMs >= 30f);

                    bool isTransportConstrained =
                        droppedDelta > 0
                        || (winStallMs >= dtSec * 250.0 && hasTransportQueueSignal)
                        || (winNoUpdate == 0 && winStallMs >= dtSec * 80.0 && winFramesSent > 0 && actualSendFps < _maxFps * 0.85f && hasTransportQueueSignal)
                        || ((effectiveWaitMs >= 20f || lastSocketWriteMs >= 30f || udpLossRate > 2.0f || retransRate > 2.5f || rtoDelta > 0 || _ch.PendingLowPriorityCount >= 2)
                            && actualSendFps < _maxFps * 0.85f);

                    float transportWaitMs = isUdp ? Math.Max(effectiveWaitMs, lastSocketWriteMs) : effectiveWaitMs;
                    float avgDecMs = isStreamIdle && vt == null ? 0f : (vt?.AvgDecodeDurationMs ?? 0f);
                    float avgRenMs = isStreamIdle && vt == null ? 0f : (vt?.AvgRenderDurationMs ?? 0f);
                    float e2eDelay = transportWaitMs + lastAvgEncodeMs + (rttMs * 0.5f) + avgDecMs + avgRenMs;
                    float viewerRenderFpsRaw = vt?.RenderFps ?? 0f;
                    float telemetryRenderFps = viewerRenderFpsRaw;

                    var sample = new AdaptiveSample(
                        NowMs: now,
                        SendQueueWaitMs: transportWaitMs,
                        SendQueuePending: _ch.PendingLowPriorityCount,
                        UdpRawLossRatePercent: udpLossRate,
                        RetransmissionRatePercent: retransRate,
                        ArqFinalSuccessRatePercent: arqSuccessRate,
                        InflightRatio: inflightRatio,
                        RtoTimeoutsDelta: rtoDelta,
                        FastRetransmitsDelta: fastDelta,
                        TargetFps: _maxFps,
                        ActualFps: actualSendFps,
                        FrameDropsDelta: droppedDelta,
                        EndToEndDelayMs: e2eDelay,
                        IsStreamActive: isStreamActive,
                        SendQueueWaitAvgMs: waitAvgMs,
                        SendQueueWaitLastMs: waitLastMs,
                        SourceFps: sourceFps,
                        CaptureFps: captureFps,
                        ViewerRenderFps: viewerRenderFpsRaw,
                        IsTransportConstrained: isTransportConstrained);

                    var decision = adaptiveCtrl.Evaluate(in sample);
                    if (decision.Changed)
                    {
                        TriggerConfigSwitch(decision, now);
                    }

                    int nextReduction =
                        (transportWaitMs >= 250f || retransRate >= 8.0f || rtoDelta >= 4 || inflightRatio >= 0.80f || (isTransportConstrained && adaptiveCtrl.CongestionWarning && transportWaitMs >= 120f))
                            ? 10
                            : (adaptiveCtrl.CongestionWarning || isTransportConstrained || transportWaitMs >= 100f || retransRate >= 5.0f || rtoDelta >= 2 || inflightRatio >= 0.60f)
                                ? 5
                                : 0;
                    // 僅於非健康壅塞期間維持或升高衰減，網路恢復穩定後平滑解除；絕不覆寫使用者 _quality 或解析度模式
                    congestionQualityReduction = nextReduction > 0
                        ? nextReduction
                        : (isStreamIdle ? 0 : Math.Max(0, congestionQualityReduction - 5));

                    int nextMaxInflight = ComputeEffectiveMaxInflight(
                        rttMs,
                        _maxFps,
                        transportWaitMs,
                        _ch.PendingLowPriorityCount,
                        udpLossRate,
                        retransRate,
                        rtoDelta,
                        arqSuccessRate,
                        inflightRatio,
                        isCongested: adaptiveCtrl.CongestionWarning || isTransportConstrained,
                        lastAvgFrameSizeBytes,
                        lastEstimatedBwMbps);
                    Volatile.Write(ref _effectiveMaxInflight, nextMaxInflight);

                    var prof = adaptiveCtrl.CurrentProfile;
                    var telemetry = new HostStreamTelemetry
                    {
                        ConfigVersion = streamConfigVersion,
                        Mode = adaptiveCtrl.Mode,
                        Level = adaptiveCtrl.CurrentLevel,
                        OriginalWidth = prof.OriginalWidth,
                        OriginalHeight = prof.OriginalHeight,
                        StreamWidth = prof.StreamWidth,
                        StreamHeight = prof.StreamHeight,
                        Quality = _quality,
                        EffectiveEncodingQuality = _lossless ? 100 : (_quality >= 95 ? _quality : Math.Max(20, _quality - congestionQualityReduction)),
                        TargetFps = _maxFps,
                        ActualSendFps = lastActualSendFps,
                        ViewerRenderFps = telemetryRenderFps,
                        FrameDropCount = Interlocked.Read(ref _frameDropsTotal),
                        SendQueuePending = _ch.PendingLowPriorityCount,
                        SendQueueWaitMs = effectiveWaitMs,
                        SendQueueWaitLastMs = waitLastMs,
                        SendQueueWaitMaxMs = effectiveWaitMs,
                        SendQueueWaitAvgMs = waitAvgMs,
                        PacingWaitMs = lastPacingWaitMs,
                        SocketWriteDurationMs = lastSocketWriteMs,
                        SourceFrameRate = lastSourceFps,
                        CaptureFrameRate = lastCaptureFps,
                        SendFrameRate = lastActualSendFps,
                        RenderFrameRate = telemetryRenderFps,
                        TargetFrameRate = _maxFps,
                        TelemetryWindowMs = TelemetryWindowMs,
                        IsStreamIdle = isStreamIdle,
                        GoodputMbps = goodputMbps,
                        WireThroughputMbps = wireThroughputMbps,
                        EffectiveMaxInflightFrames = nextMaxInflight,
                        EndToEndDelayStages = "QueueWait+Encode+RTT/2+Decode+Render (不含Capture/Display)",
                        UdpRawLossRate = udpLossRate,
                        RetransmissionRate = retransRate,
                        ArqFinalSuccessRate = arqSuccessRate,
                        InflightPackets = inflightPkts,
                        MaxWindowSize = maxWin,
                        RtoTimeoutCount = udp != null ? (int)Math.Min(int.MaxValue, udp.TotalRtoTimeouts) : 0,
                        FastRetransmitCount = udp != null ? (int)Math.Min(int.MaxValue, udp.TotalFastRetransmits) : 0,
                        RttMs = rttMs,
                        CurrentRtoMs = currentRtoMs,
                        EndToEndDelayMs = e2eDelay,
                        AvgThroughputMbps = avgMbps,
                        PeakThroughputMbps = peakMbps,
                        EstimatedBandwidthMbps = isUdp ? lastEstimatedBwMbps : 0f,
                        AverageFrameSizeBytes = lastAvgFrameSizeBytes,
                        AvgEncodeDurationMs = lastAvgEncodeMs,
                        AvgDecodeDurationMs = avgDecMs,
                        AvgRenderDurationMs = avgRenMs,
                        DecodeFailureCount = vt?.DecodeFailureCount ?? 0,
                        CongestionWarning = adaptiveCtrl.CongestionWarning,
                        IsUdpTransport = isUdp,
                        LastSwitchReason = adaptiveCtrl.LastReason,
                        TransportSummary = isUdp
                            ? $"UDP P2P | Loss={udpLossRate:F1}% | Retrans={retransRate:F1}% | ARQ={arqSuccessRate:F1}% | Inflight={inflightPkts}/{maxWin} | Pipe={nextMaxInflight} | RTO={currentRtoMs:F0}ms"
                            : $"TCP LAN | QueueWait={effectiveWaitMs:F1}ms(Avg={waitAvgMs:F1}) | Inflight={inflightPkts}/{maxWin}"
                    };
                    LatestTelemetry = telemetry;

                    if (decision.Changed || adaptiveCtrl.CongestionWarning != lastCongestionWarning || now - lastTelemetryLogAt >= 5000)
                    {
                        if (!isStreamIdle || decision.Changed || adaptiveCtrl.CongestionWarning != lastCongestionWarning || lastTelemetryLogAt == 0)
                        {
                            lastTelemetryLogAt = now;
                            Log.Info($"[HostTelemetry] {telemetry.FormatLogSummary()}");
                        }
                    }

                    if (decision.Changed || adaptiveCtrl.CongestionWarning != lastCongestionWarning || now - lastTelemetryPushAt >= 1000)
                    {
                        lastCongestionWarning = adaptiveCtrl.CongestionWarning;
                        lastTelemetryPushAt = now;
                        Send(MessageCodec.StreamTelemetry(telemetry), highPriority: true);
                    }
                }

                // 游標形狀
                var cursorMsg = _cursor.Poll();
                if (cursorMsg != null) Send(cursorMsg, highPriority: true);
                PollIme();

                // P0-03: 切換 StreamConfig 並送出首張 Keyframe 後、收到 Viewer StreamConfigAck 前，
                // 嚴禁提前呼叫 cap.Capture() 或 enc.Encode()，避免消費 DXGI Dirty/Move rects 或污染 GDI 狀態
                if (_pathRecovering)
                {
                    _ackEvent.WaitOne(15);
                    continue;
                }

                if (Interlocked.Exchange(ref _pathRecoveryCompletedFlag, 0) == 1)
                {
                    // Phase 5-4：路徑切換完成後維持 seq 單調遞增（不歸零），遞增 streamConfigVersion 隔離舊路徑影格，
                    // 重置 UDP 遙測基準避免切換停頓造成假性壅塞降級，並送出 StreamConfig + Keyframe 重新建立視覺基準
                    SyncUdpTelemetryBaseline(_ch.UnderlyingStream as ReliableUdpStream);
                    if (cap != null && enc != null && adaptiveCtrl != null)
                    {
                        streamConfigVersion++;
                        Volatile.Write(ref _latestConfigVersion, streamConfigVersion);
                        pendingSwitchKeyframe = true;
                        configSentAt = now;
                        configRetrySent = false;
                        configKeyframeSentForVersion = 0;
                        _keyframeRequested = true;
                        Send(MessageCodec.StreamConfig(
                            streamConfigVersion,
                            cap.Width,
                            cap.Height,
                            enc.StreamWidth,
                            enc.StreamHeight,
                            adaptiveCtrl.CurrentLevel,
                            adaptiveCtrl.Mode,
                            "PathRecovered"), highPriority: true);
                    }
                    lastFrameAt = now;
                }

                uint ackedCfgBeforeCapture;
                lock (_configAckLock) { ackedCfgBeforeCapture = _ackedConfigVersion; }
                bool waitingForConfigAck = ackedCfgBeforeCapture < streamConfigVersion;
                if (waitingForConfigAck && configKeyframeSentForVersion == streamConfigVersion)
                {
                    long waitBudgetMs = Math.Clamp(2000 - (now - configSentAt), 5, 25);
                    _ackEvent.WaitOne((int)waitBudgetMs);
                    continue;
                }

                // 流量控制：依 RTT 與 BDP 動態調整在途影格數 (基礎 MaxInflight=2，健康高 RTT 時安全擴展至上限 6 幀，避免高 RTT 鎖死 FPS)
                int activeMaxInflight = Volatile.Read(ref _effectiveMaxInflight);
                int curInflight = GetInflightCount();
                if (curInflight >= activeMaxInflight)
                {
                    long waitLimit = 1200; // 適應性等待上限，兼顧網路抖動與暫態重傳
                    if (now - lastFrameAt > waitLimit)
                    {
                        dropsInWindow++;
                        Interlocked.Increment(ref _frameDropsTotal);
                        // 檢查關鍵影格冷卻時間 (Cooldown: 2500ms)，杜絕連續超時觸發連環關鍵影格風暴 (Cascading Keyframe Storm)
                        if (now - _lastKeyframeSentAt >= 2500)
                        {
                            Log.Warn($"[HostSession] 影格確認逾時 ({waitLimit}ms無回覆)，重置串流管線並請求關鍵影格 (Inflight={curInflight})");
                            _keyframeRequested = true;
                            _lastKeyframeSentAt = now;
                        }
                        else
                        {
                            Log.Warn($"[HostSession] 影格確認逾時但處於關鍵影格冷卻中 ({now - _lastKeyframeSentAt}ms < 2500ms)，僅重置 Inflight 佇列");
                        }
                        ResetInflightTracking("Timeout1200ms");
                    }
                    else
                    {
                        var waitSw = Stopwatch.StartNew();
                        _ackEvent.WaitOne(15);
                        waitSw.Stop();
                        transportStallMsInWindow += waitSw.Elapsed.TotalMilliseconds;
                        continue;
                    }
                }

                bool needConfigKeyframe = waitingForConfigAck && configKeyframeSentForVersion != streamConfigVersion;
                bool immediateSwitchFrame = (pendingSwitchKeyframe || needConfigKeyframe) && hasCapturedOnce;
                int interval = 1000 / Math.Max(1, _maxFps);
                long sinceLast = now - lastCaptureAt;
                if (!immediateSwitchFrame && sinceLast < interval)
                {
                    Thread.Sleep((int)Math.Min(interval - sinceLast, 15));
                    continue;
                }

                long captureStartAt = _clock.ElapsedMilliseconds;
                bool got;
                try
                {
                    got = cap!.Capture(immediateSwitchFrame ? 0 : Math.Min(interval, 16));
                    capturesInWindow++;
                    if (got)
                    {
                        hasCapturedOnce = true;
                        lastCaptureAt = captureStartAt;
                    }
                    failures = 0;
                }
                catch (Exception ex)
                {
                    failures++;
                    Log.Warn($"擷取中斷（{cap?.Name}）：{ex.Message}");
                    if (cap is DxgiCapturer && failures >= 3) dxgiRetryAt = _clock.ElapsedMilliseconds + 10_000;
                    cap?.Dispose();
                    cap = null;
                    DesktopHelper.SyncThreadDesktop();
                    Thread.Sleep(Math.Min(1000, 50 * failures));
                    continue;
                }

                if (!hasCapturedOnce && !got)
                {
                    captureNoUpdateInWindow++;
                    if (_clock.ElapsedMilliseconds - captureStartAt < 4)
                    {
                        Thread.Sleep(Math.Clamp(interval / 3, 2, 8));
                    }
                    continue;
                }

                if (immediateSwitchFrame || needConfigKeyframe)
                {
                    enc!.RequestKeyframe();
                    pendingSwitchKeyframe = false;
                }

                enc!.Quality = _quality;
                enc.CongestionQualityReduction = congestionQualityReduction;
                enc.Lossless = _lossless;
                if (_keyframeRequested) { _keyframeRequested = false; enc.RequestKeyframe(); }

                var encSw = Stopwatch.StartNew();
                var tiles = enc.Encode(cap.Frame, got, _clock.ElapsedMilliseconds, out var frameType, out var moveRects);
                encSw.Stop();

                bool hasUpdates = tiles.Count > 0 || (moveRects != null && moveRects.Count > 0);
                if (hasUpdates)
                {
                    sourceUpdatesInWindow++;

                    uint ackedCfg;
                    lock (_configAckLock) { ackedCfg = _ackedConfigVersion; }

                    // 防禦性檢查：切換解析度尚未收到 StreamConfigAck 前，只允許送出新組態的首張 Keyframe
                    bool isPendingConfigKeyframe = ackedCfg < streamConfigVersion;
                    if (isPendingConfigKeyframe)
                    {
                        if (frameType != FrameType.FullFrame || configKeyframeSentForVersion == streamConfigVersion)
                        {
                            continue;
                        }
                    }

                    uint currentBase = (frameType == FrameType.FullFrame) ? 0 : frameVersion;
                    frameVersion = ++seq;

                    if (frameType == FrameType.FullFrame)
                    {
                        _lastKeyframeSentAt = now;
                    }

                    byte[] serialized = FrameEncoder.Serialize(seq, frameVersion, currentBase, frameType, enc.StreamWidth, enc.StreamHeight, moveRects, tiles, streamConfigVersion);
                    encodeMsInWindow += encSw.Elapsed.TotalMilliseconds;

                    long frameSentNow = _clock.ElapsedMilliseconds;
                    Volatile.Write(ref _lastFrameSentAt, frameSentNow);
                    ulong frameGen = RegisterSentFrameInflight(seq, frameSentNow);

                    var sendSw = Stopwatch.StartNew();
                    var sendOutcome = SendRegisteredVideoFrame(seq, frameGen, serialized);
                    sendSw.Stop();
                    if (sendOutcome == VideoFrameSendOutcome.TransportFailed)
                    {
                        break;
                    }
                    if (sendOutcome == VideoFrameSendOutcome.SkippedStaleGeneration)
                    {
                        continue;
                    }

                    if (isPendingConfigKeyframe)
                    {
                        configKeyframeSentForVersion = streamConfigVersion;
                    }
                    framesSentInWindow++;
                    frameBytesInWindow += serialized.Length;
                    // 僅於非 UDP 通道 (TCP 阻塞) 或 UDP 實際發生佇列等待/Socket背壓時才計入 stall，並扣除主動 Pacing 等待時間，
                    // 避免健康通道的微步調平滑傳送誤觸發降級，同時確保真實 UDP Socket 背壓仍能被捕捉
                    if (_ch.UnderlyingStream is not ReliableUdpStream udpStream)
                    {
                        transportStallMsInWindow += sendSw.Elapsed.TotalMilliseconds;
                    }
                    else
                    {
                        double realWaitAndWriteMs = Math.Max(0.0, sendSw.Elapsed.TotalMilliseconds - udpStream.LastPacingWaitMs);
                        if (_ch.LastQueueWaitMs >= 10f || udpStream.LastSemaphoreWaitMs >= 10 || udpStream.LastSocketWriteDurationMs >= 25f)
                        {
                            transportStallMsInWindow += realWaitAndWriteMs;
                        }
                    }
                    lastFrameAt = _clock.ElapsedMilliseconds;
                }
                else
                {
                    captureNoUpdateInWindow++;
                    if (!got && _clock.ElapsedMilliseconds - captureStartAt < 4)
                    {
                        // DXGI Capture(timeoutMs) 已主動等候 GPU 更新，僅在極快返回 (例如 GDI 或游標) 時短暫休眠避免空轉
                        Thread.Sleep(Math.Clamp(interval / 3, 2, 8));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("擷取迴圈錯誤", ex);
            _cts.Cancel();
        }
        finally
        {
            cap?.Dispose();
            Win32.SetThreadExecutionState(Win32.ES_CONTINUOUS);
            Win32.timeEndPeriod(1);
        }
    }

    public void Dispose()
    {
        _sessionClosed = true;
        _cts.Cancel();
        _ackEvent.Set();
        ClearInflightOnShutdown();
        _ch.Dispose();
    }
}
