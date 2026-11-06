using System.Collections.Concurrent;
using System.Diagnostics;
using RemoteDesk.Common;
using RemoteDesk.Native;

namespace RemoteDesk.Host;

/// <summary>主機端與 UI 的互動介面（介面層或無頭模式各自實作）。</summary>
internal interface IHostUi
{
    Task<bool> ConfirmAccessAsync(string viewerName, string viewerId, string address, string code, CancellationToken ct);
    void SessionStarted(HostSession session);
    void SessionEnded(HostSession session);
    void SetClipboardText(string text);
}

/// <summary>一個已通過驗證的控制連線：負責擷取畫面、送出影像/游標/輸入法狀態，並注入遠端送來的輸入。</summary>
internal sealed class HostSession : IDisposable
{
    public const string AllMonitors = Protocol.AllMonitors;
    private const int MaxInflight = 2;

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

    private int _inflight;
    private volatile string? _requestedSelection;
    private volatile bool _keyframeRequested;
    private volatile int _quality = 70;
    private volatile int _maxFps = 30;
    private volatile bool _lossless = true;
    private volatile ImeSyncMode _imeMode = ImeSyncMode.Bound;

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
    public DateTime StartedAt { get; } = DateTime.Now;
    public string CaptureMethod { get; private set; } = "";
    public bool AllowDxgi { get; set; } = true;

    public HostSession(SecureChannel ch, ClientHello hello, string remoteAddress, IHostUi ui)
    {
        _ch = ch;
        _ui = ui;
        ViewerName = hello.ViewerName;
        ViewerId = hello.ViewerId;
        RemoteAddress = remoteAddress;
    }

    public void Disconnect()
    {
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
            _input.Post(_injector.ReleaseAll);
            _input.Dispose();
            _ch.Dispose();
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
                if (Interlocked.Decrement(ref _inflight) < 0) Interlocked.Exchange(ref _inflight, 0);
                _ackEvent.Set();
                break;
            case ViewerMsg.SelectMonitor:
                _requestedSelection = r.Str();
                _ackEvent.Set();
                break;
            case ViewerMsg.VideoSettings:
                _quality = Math.Clamp((int)r.U8(), 10, 100);
                _maxFps = Math.Clamp((int)r.U8(), 1, 120);
                _lossless = r.Bool();
                break;
            case ViewerMsg.RequestKeyframe:
                _keyframeRequested = true;
                break;
            case ViewerMsg.Ping:
            {
                var ts = r.I64();
                _ = _ch.TrySendAsync(new PacketWriter((byte)HostMsg.Pong, 16).I64(ts).ToArray());
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
                Send(w.ToArray());
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
            Send(MessageCodec.Ime((byte)HostMsg.ImeState, cur.Value));
        }
    }

    // ---------------------------------------------------------------- 擷取
    private void Send(byte[] data)
    {
        try { _ch.SendAsync(data, _cts.Token).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            _cts.Cancel();
        }
    }

    private void CaptureLoop()
    {
        Win32.timeBeginPeriod(1);
        Win32.SetThreadExecutionState(Win32.ES_CONTINUOUS | Win32.ES_DISPLAY_REQUIRED | Win32.ES_SYSTEM_REQUIRED);
        IScreenCapturer? cap = null;
        FrameEncoder? enc = null;
        List<MonitorInfo> monitors = MonitorEnumerator.GetMonitors();
        string selection = (monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault())?.DeviceName ?? AllMonitors;
        bool needRecreate = true;
        long lastMonitorCheck = 0, lastFrameAt = -1000;
        uint seq = 0;
        int failures = 0;
        long dxgiRetryAt = 0;

        try
        {
            DesktopHelper.SyncThreadDesktop();
            Send(MessageCodec.MonitorList(monitors, selection));

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
                    var m = MonitorEnumerator.GetMonitors();
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
                            Send(MessageCodec.MonitorList(monitors, selection));
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
                        cap = CapturerFactory.Create(mon, AllowDxgi && now >= dxgiRetryAt);
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
                    Interlocked.Exchange(ref _inflight, 0);
                    _cursor.Reset();
                    _lastCaret = "~";
                    Send(new PacketWriter((byte)HostMsg.StreamInfo, 64)
                        .Str(selection).I32(cap.Width).I32(cap.Height).I32(bounds.X).I32(bounds.Y).Str(cap.Name).ToArray());
                    Log.Info($"開始擷取 {selection} {cap.Width}x{cap.Height}（{cap.Name}）");
                    needRecreate = false;
                }

                // 游標形狀
                var cursorMsg = _cursor.Poll();
                if (cursorMsg != null) Send(cursorMsg);
                PollIme();

                // 流量控制：最多 MaxInflight 張未確認的畫面，避免網路塞車造成延遲累積
                if (Volatile.Read(ref _inflight) >= MaxInflight)
                {
                    _ackEvent.WaitOne(20);
                    continue;
                }

                int interval = 1000 / Math.Max(1, _maxFps);
                long sinceLast = now - lastFrameAt;
                if (sinceLast < interval)
                {
                    Thread.Sleep((int)Math.Min(interval - sinceLast, 15));
                    continue;
                }

                bool got;
                try
                {
                    got = cap.Capture(Math.Min(interval, 16));
                    failures = 0;
                }
                catch (Exception ex)
                {
                    failures++;
                    Log.Warn($"擷取中斷（{cap.Name}）：{ex.Message}");
                    if (cap is DxgiCapturer && failures >= 3) dxgiRetryAt = _clock.ElapsedMilliseconds + 10_000;
                    cap.Dispose();
                    cap = null;
                    DesktopHelper.SyncThreadDesktop();
                    Thread.Sleep(Math.Min(1000, 50 * failures));
                    continue;
                }

                enc!.Quality = _quality;
                enc.Lossless = _lossless;
                if (_keyframeRequested) { _keyframeRequested = false; enc.RequestKeyframe(); }

                var tiles = enc.Encode(cap.Frame, got, _clock.ElapsedMilliseconds);
                if (tiles.Count > 0)
                {
                    Interlocked.Increment(ref _inflight);
                    Send(FrameEncoder.Serialize(++seq, enc.Width, enc.Height, tiles));
                    lastFrameAt = _clock.ElapsedMilliseconds;
                }
                else if (!got)
                {
                    // 沒有變化：DXGI 已在 Capture 中等待，GDI 則稍作休息
                    lastFrameAt = _clock.ElapsedMilliseconds - interval / 2;
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
        _cts.Cancel();
        _ch.Dispose();
    }
}
