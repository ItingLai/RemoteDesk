using System.Diagnostics;

namespace RemoteDesk.Common;

/// <summary>影格更新類型。</summary>
internal enum FrameType : byte
{
    FullFrame = 0x01,     // 完整畫面 (基準影格)
    RegionUpdate = 0x02,  // 局部區域更新 (Dirty Rectangles)
    MoveAndRegion = 0x03  // 位移 (Move Rectangles) + 邊緣局部區域更新
}

/// <summary>
/// 影格效能遙測指標 (Telemetry Frame Metrics)。
/// 嚴格遵循無敏感資訊原則：不記錄畫面內容、金鑰、或未去識別化網路位址。
/// </summary>
internal struct FrameMetrics
{
    // 影格識別
    public uint FrameId;
    public FrameType Type;
    public int FrameWidth;
    public int FrameHeight;

    // 主機端擷取
    public float CaptureDurationMs;     // DXGI / GDI 擷取耗時
    public float GpuCopyDurationMs;      // GPU 內部 CopyResource 耗時
    public float CpuCopyDurationMs;      // PCIe -> CPU RAM Map & MemoryCopy 耗時
    public int DirtyRectCount;          // 變更區域數量
    public int MoveRectCount;           // 位移區域數量
    public float DirtyAreaPercentage;   // 變更面積百分比 (0.0 ~ 100.0%)

    // 主機端比對與編碼
    public float CpuDiffDurationMs;     // CPU 軟體逐圖塊比對耗時
    public float JpegEncodeDurationMs;  // GDI+ JPEG 壓縮耗時
    public int JpegEncodedSize;         // 壓縮後總大小 (bytes)
    public int PacketCount;             // 傳輸切片封包數
    public int InflightCount;           // 傳輸在途影格數
    public float SendDurationMs;        // 傳輸層發送耗時

    // 接收端解碼與渲染
    public float ReceiveDurationMs;     // 封包接收重組耗時
    public float DecodeDurationMs;      // JPEG 解碼耗時
    public float FramebufferUpdateDurationMs; // 寫入畫布緩衝區耗時
    public float RenderDurationMs;      // UI DrawImage 渲染耗時
    public float EndToEndLatencyMs;     // 往返 / 單向端對端延遲 (ms)
}

/// <summary>
/// 遙測統計聚合器，計算平均值與 P95。
/// </summary>
internal sealed class TelemetryStats
{
    private readonly List<float> _captureTimes = new();
    private readonly List<float> _cpuDiffTimes = new();
    private readonly List<float> _encodeTimes = new();
    private readonly List<int> _encodedSizes = new();
    private readonly List<float> _decodeTimes = new();
    private readonly List<float> _renderTimes = new();

    public void Record(in FrameMetrics m)
    {
        lock (_captureTimes)
        {
            if (m.CaptureDurationMs > 0) _captureTimes.Add(m.CaptureDurationMs);
            if (m.CpuDiffDurationMs > 0) _cpuDiffTimes.Add(m.CpuDiffDurationMs);
            if (m.JpegEncodeDurationMs > 0) _encodeTimes.Add(m.JpegEncodeDurationMs);
            if (m.JpegEncodedSize > 0) _encodedSizes.Add(m.JpegEncodedSize);
            if (m.DecodeDurationMs > 0) _decodeTimes.Add(m.DecodeDurationMs);
            if (m.RenderDurationMs > 0) _renderTimes.Add(m.RenderDurationMs);
        }
    }

    public static float GetAverage(List<float> list) => list.Count == 0 ? 0 : list.Average();
    public static float GetP95(List<float> list)
    {
        if (list.Count == 0) return 0;
        var sorted = list.OrderBy(x => x).ToList();
        int idx = (int)Math.Ceiling(0.95 * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }

    public static float GetAverage(List<int> list) => list.Count == 0 ? 0 : (float)list.Average();
    public static int GetP95(List<int> list)
    {
        if (list.Count == 0) return 0;
        var sorted = list.OrderBy(x => x).ToList();
        int idx = (int)Math.Ceiling(0.95 * sorted.Count) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Count - 1)];
    }
}

/// <summary>主機端定期傳送給檢視端的串流與自適應解析度遙測快照。</summary>
public sealed class HostStreamTelemetry
{
    public uint ConfigVersion { get; set; }
    public ResolutionMode Mode { get; set; }
    public ResolutionLevel Level { get; set; }
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }
    public int StreamWidth { get; set; }
    public int StreamHeight { get; set; }
    public int Quality { get; set; }
    public int TargetFps { get; set; }
    public float ActualSendFps { get; set; }
    public float ViewerRenderFps { get; set; }
    public long FrameDropCount { get; set; }
    public int SendQueuePending { get; set; }
    public float SendQueueWaitMs { get; set; }
    public float UdpRawLossRate { get; set; }
    public float RetransmissionRate { get; set; }
    public float ArqFinalSuccessRate { get; set; } = 100f;
    public int InflightPackets { get; set; }
    public int MaxWindowSize { get; set; }
    public int RtoTimeoutCount { get; set; }
    public int FastRetransmitCount { get; set; }
    public float RttMs { get; set; }
    public float CurrentRtoMs { get; set; }
    public float EndToEndDelayMs { get; set; }
    public float AvgThroughputMbps { get; set; }
    public float PeakThroughputMbps { get; set; }
    public float EstimatedBandwidthMbps { get; set; }
    public int AverageFrameSizeBytes { get; set; }
    public float AvgEncodeDurationMs { get; set; }
    public float AvgDecodeDurationMs { get; set; }
    public float AvgRenderDurationMs { get; set; }
    public int DecodeFailureCount { get; set; }
    public bool CongestionWarning { get; set; }
    public bool IsUdpTransport { get; set; }
    public string LastSwitchReason { get; set; } = string.Empty;
    public string TransportSummary { get; set; } = string.Empty;

    // P0-01: 明確區分採樣視窗內的 Last / WindowMax / WindowAverage 佇列等待時間
    public float SendQueueWaitLastMs { get; set; }
    public float SendQueueWaitMaxMs { get; set; }
    public float SendQueueWaitAvgMs { get; set; }

    // P0-02: 明確分離五類 FPS 指標 (來源 / 擷取 / 送出 / 渲染 / 目標上限)
    public float SourceFrameRate { get; set; }
    public float CaptureFrameRate { get; set; }
    public float SendFrameRate { get; set; }
    public float RenderFrameRate { get; set; }
    public int TargetFrameRate { get; set; }

    // P1-04 / Stage 2C: 明確標記採樣視窗、閒置狀態、Goodput/Wire 分離、E2E 階段組成與動態管線視窗
    public int TelemetryWindowMs { get; set; } = 500;
    public bool IsStreamIdle { get; set; }
    public float GoodputMbps { get; set; }
    public float WireThroughputMbps { get; set; }
    public int EffectiveMaxInflightFrames { get; set; } = 2;
    public string EndToEndDelayStages { get; set; } = "QueueWait+Encode+RTT/2+Decode+Render";
    public float PacingWaitMs { get; set; }
    public float SocketWriteDurationMs { get; set; }
    public int EffectiveEncodingQuality { get; set; }

    /// <summary>產生供系統 Log 記錄的完整連線品質與遙測診斷摘要。</summary>
    public string FormatLogSummary() =>
        $"Mode={Mode} L{(int)Level}(Cfg v{ConfigVersion}) Res={StreamWidth}x{StreamHeight}/{OriginalWidth}x{OriginalHeight} " +
        $"Q={Quality}%(Eff={EffectiveEncodingQuality}%) FPS(Src/Cap/Send/Ren/Tgt)={SourceFrameRate:F1}/{CaptureFrameRate:F1}/{ActualSendFps:F1}/{ViewerRenderFps:F1}/{TargetFps} Drops={FrameDropCount} " +
        $"QWait={SendQueueWaitMs:F1}ms(Avg={SendQueueWaitAvgMs:F1}/Last={SendQueueWaitLastMs:F1}) PaceWait={PacingWaitMs:F1}ms SockWr={SocketWriteDurationMs:F1}ms Pending={SendQueuePending} " +
        $"RTT={RttMs:F0}ms RTO={CurrentRtoMs:F0}ms E2E={EndToEndDelayMs:F0}ms " +
        $"BW(Goodput/Wire/Est/Peak)={GoodputMbps:F2}/{WireThroughputMbps:F2}/{EstimatedBandwidthMbps:F2}/{PeakThroughputMbps:F2}Mbps " +
        $"AvgFrame={AverageFrameSizeBytes / 1024.0:F1}KB Enc/Dec/Ren={AvgEncodeDurationMs:F1}/{AvgDecodeDurationMs:F1}/{AvgRenderDurationMs:F1}ms " +
        $"Idle={IsStreamIdle} Warn={CongestionWarning} [{TransportSummary}] Reason=\"{LastSwitchReason}\"";
}

/// <summary>單一採樣視窗內的佇列等待時間統計快照。</summary>
internal readonly record struct QueueWaitWindowMetrics(
    float LastMs,
    float WindowMaxMs,
    float WindowAverageMs,
    float OngoingWaitMs,
    int SampleCount)
{
    public float EffectiveWaitMs => Math.Max(WindowMaxMs, OngoingWaitMs);
    public float EffectiveAverageWaitMs => SampleCount > 0 ? Math.Max(WindowAverageMs, OngoingWaitMs) : OngoingWaitMs;
}

/// <summary>
/// 執行緒安全的採樣視窗佇列等待追蹤器：
/// 區分單筆 Last、視窗 WindowMax、視窗 WindowAverage 與進行中 OngoingWait，
/// 並於視窗結束時正確重置且在無新傳輸時自然清除過期狀態。
/// </summary>
internal sealed class QueueWaitWindowTracker
{
    private readonly object _lock = new();
    private readonly List<(long Id, long StartTs)> _activeWaiters = new(2);
    private long _nextId;
    private float _winLastMs;
    private float _winMaxMs;
    private double _winSumMs;
    private int _winCount;
    private float _lifetimeLastMs;
    private long _lastActivityTs;

    public long BeginWait(out long startTs)
    {
        startTs = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            long id = ++_nextId;
            _activeWaiters.Add((id, startTs));
            _lastActivityTs = startTs;
            return id;
        }
    }

    public float EndWait(long waitId, long startTs, bool recordSample = true)
    {
        long nowTs = Stopwatch.GetTimestamp();
        float elapsedMs = (float)Stopwatch.GetElapsedTime(startTs, nowTs).TotalMilliseconds;
        lock (_lock)
        {
            RemoveWaiterLocked(waitId);
            if (recordSample)
            {
                _lifetimeLastMs = elapsedMs;
                _lastActivityTs = nowTs;
                _winLastMs = elapsedMs;
                if (elapsedMs > _winMaxMs) _winMaxMs = elapsedMs;
                _winSumMs += elapsedMs;
                _winCount++;
            }
        }
        return elapsedMs;
    }

    public void CancelWait(long waitId)
    {
        lock (_lock)
        {
            RemoveWaiterLocked(waitId);
        }
    }

    /// <summary>
    /// 取得近期最後一次等待時間；若目前無活躍等待且距上次傳輸活動已超過 staleMs，回傳 0 避免永久鎖死。
    /// </summary>
    public float GetFreshLastMs(double staleMs = 500.0)
    {
        long nowTs = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            float ongoing = GetOngoingMaxLocked(nowTs);
            if (ongoing > 0f) return Math.Max(_lifetimeLastMs, ongoing);
            if (_lastActivityTs == 0) return 0f;
            double idleMs = Stopwatch.GetElapsedTime(_lastActivityTs, nowTs).TotalMilliseconds;
            return idleMs >= staleMs ? 0f : _lifetimeLastMs;
        }
    }

    /// <summary>消費並重置目前採樣視窗的 Last / WindowMax / WindowAverage，同時保留仍在進行中的等待狀態。</summary>
    public QueueWaitWindowMetrics ConsumeWindow()
    {
        long nowTs = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            float ongoing = GetOngoingMaxLocked(nowTs);
            int count = _winCount;
            float last = count > 0 ? _winLastMs : ongoing;
            float max = count > 0 ? Math.Max(_winMaxMs, ongoing) : ongoing;
            float avg = count > 0
                ? (float)Math.Max(_winSumMs / count, ongoing)
                : ongoing;

            _winLastMs = 0f;
            _winMaxMs = 0f;
            _winSumMs = 0.0;
            _winCount = 0;

            return new QueueWaitWindowMetrics(last, max, avg, ongoing, count);
        }
    }

    private float GetOngoingMaxLocked(long nowTs)
    {
        if (_activeWaiters.Count == 0) return 0f;
        long earliest = long.MaxValue;
        for (int i = 0; i < _activeWaiters.Count; i++)
        {
            if (_activeWaiters[i].StartTs < earliest) earliest = _activeWaiters[i].StartTs;
        }
        return earliest == long.MaxValue ? 0f : (float)Stopwatch.GetElapsedTime(earliest, nowTs).TotalMilliseconds;
    }

    private void RemoveWaiterLocked(long waitId)
    {
        for (int i = 0; i < _activeWaiters.Count; i++)
        {
            if (_activeWaiters[i].Id == waitId)
            {
                _activeWaiters.RemoveAt(i);
                return;
            }
        }
    }
}

/// <summary>檢視端回報給主機端的真實解碼、渲染與延遲遙測快照。</summary>
public sealed class ViewerTelemetrySnapshot
{
    public uint ConfigVersion { get; set; }
    public float RenderFps { get; set; }
    public float AvgDecodeDurationMs { get; set; }
    public float AvgRenderDurationMs { get; set; }
    public float EndToEndDelayMs { get; set; }
    public int DecodeFailureCount { get; set; }
    public int TelemetryWindowMs { get; set; } = 500;
    public bool IsIdle { get; set; } = true;
}
