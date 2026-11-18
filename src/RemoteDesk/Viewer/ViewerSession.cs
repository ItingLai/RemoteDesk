using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RemoteDesk.Common;
using RemoteDesk.Native;
using RemoteDesk.Net;

namespace RemoteDesk.Viewer;

/// <summary>
/// 當目標遠端設備已啟用密碼保護，但控制端尚未提供密碼時拋出之例外。
/// 繼承自 UnauthorizedAccessException 以保持相容。
/// </summary>
public class NeedPasswordException : UnauthorizedAccessException
{
    public bool AllowInteractive { get; }

    public NeedPasswordException(bool allowInteractive, string message = "此主機已設定連線密碼保護，請輸入連線密碼。")
        : base(message)
    {
        AllowInteractive = allowInteractive;
    }
}

internal sealed class ViewerSession : IDisposable
{
    private readonly ViewerConfig _config;
    private readonly LanDiscovery _discovery;
    private readonly FrameDecoder _decoder = new();
    private readonly CancellationTokenSource _cts = new();

    private TcpClient? _tcp;
    private Stream? _underlyingStream;
    private SecureChannel? _channel;
    private Task? _recvTask;
    private Task? _pingTask;
    private P2PPathSessionContext? _pathContext;
    private P2PPathMonitor? _pathMonitor;
    private P2PPathMonitorOptions? _pathMonitorOptionsForTest;
    private string? _activeSignalingSessionId;
    private volatile bool _peerClosedCleanly;
    private bool _disposed;
    private int _disconnectedFired;

    private void FireDisconnected(string reason)
    {
        if (Interlocked.Exchange(ref _disconnectedFired, 1) == 0)
        {
            Disconnected?.Invoke(reason);
        }
    }

    public FrameDecoder Decoder => _decoder;
    public bool IsConnected => _channel != null && !_cts.IsCancellationRequested;

    public string TargetIdOrHost { get; }
    public string HostDeviceId { get; private set; } = "";
    public string HostDeviceName { get; private set; } = "";
    public List<MonitorInfo> Monitors { get; private set; } = new();
    public string SelectedMonitor { get; private set; } = "";
    public int OriginalWidth { get; private set; }
    public int OriginalHeight { get; private set; }
    public int StreamWidth => _decoder.Width;
    public int StreamHeight => _decoder.Height;
    public int RemoteWidth => OriginalWidth > 0 ? OriginalWidth : _decoder.Width;
    public int RemoteHeight => OriginalHeight > 0 ? OriginalHeight : _decoder.Height;
    public uint StreamConfigVersion { get; private set; }
    public ResolutionLevel CurrentResolutionLevel { get; private set; } = ResolutionLevel.Level0;
    public ResolutionMode CurrentResolutionMode { get; private set; } = ResolutionMode.Auto;
    public int CurrentQuality { get; private set; }
    public int CurrentMaxFps { get; private set; }
    public bool CurrentLossless { get; private set; }
    public bool IsUdpTransport => TransportModeOverrideForTest ?? (_underlyingStream is ReliableUdpStream || (CurrentTelemetry?.IsUdpTransport ?? false));
    public string TransportTypeLabel => IsUdpTransport ? "WAN UDP P2P" : "LAN TCP 直連";
    public string LastSwitchReason { get; private set; } = "";
    public HostStreamTelemetry? CurrentTelemetry { get; private set; }
    public int CurrentRttMs { get; private set; }

    public bool SupportsFrameSeqAck { get; private set; } = true;
    public P2PPathMetrics? CurrentPathMetrics => _pathMonitor?.GetMetrics();
    internal P2PPathMonitor? PathMonitorForTest => _pathMonitor;
    internal void ConfigurePathMonitorForTest(P2PPathMonitorOptions options) => _pathMonitorOptionsForTest = options;
    internal bool? TransportModeOverrideForTest { get; set; }
    internal int FrameAckCountForTest => Volatile.Read(ref _frameAckCount);
    internal uint LastAckedFrameSeqForTest { get; private set; }
    internal int KeyframeRequestCountForTest => Volatile.Read(ref _keyframeRequestCount);
    internal (int Quality, int MaxFps, bool Lossless, ResolutionMode Mode)? LastSentVideoSettingsForTest { get; private set; }
    internal FrameDecodeStatus LastDecodeStatusForTest { get; private set; }
    internal void SetFrameAckCapabilityForTest(bool supported) => SupportsFrameSeqAck = supported;
    public long LastKeyframeRequestedTicks { get; private set; }

    private int _frameAckCount;
    private int _keyframeRequestCount;
    private ResolutionMode? _pendingMode;
    private long _pendingModeSetAtMs;

    public event Action? Connected;
    public event Action<string>? Disconnected;
    public event Action<Rectangle, uint>? FrameReceived;
    public event Action<List<MonitorInfo>, string>? MonitorsUpdated;
    public event Action<Cursor?>? CursorShapeChanged;
    public event Action<ImeState>? ImeStateReceived;
    public event Action<int, int, int>? CaretReceived;
    public event Action<string>? ClipboardReceived;
    public event Action<int>? RttUpdated;
    public event Action<byte[]>? WallpaperReceived;
    public event Action<int, int>? ResolutionChanged;
    public event Action<int, int, int, int, ResolutionLevel, ResolutionMode, string>? StreamConfigChanged;
    public event Action<HostStreamTelemetry>? TelemetryUpdated;

    private readonly string? _selfDeviceId;
    private readonly DeviceIdentity _identity;
    private readonly bool _ownsIdentity;
    private readonly SignalingClient? _signalingClient;

    public string? HostPublicKeyFingerprint { get; private set; }
    internal bool ClearedStaleSavedPassword { get; private set; }
    public Func<PasswordPromptContext, Task>? PasswordPromptHandler { get; set; }

    // 影格時序診斷 (Frame Timing Telemetry)
    public long LastFrameReceivedTicks { get; private set; }
    public long LastFrameAckSentTicks { get; private set; }
    public long LastFrameDecodeCompletedTicks { get; private set; }
    public double LastFrameDecodeDurationMs { get; private set; }
    public double LastRenderDurationMs { get; private set; }
    public long CurrentDecodedFrameVersion => Interlocked.Read(ref _decodedFrameVersion);

    private readonly object _telLock = new();
    private long _decodedFrameVersion;
    private long _lastCountedRenderVersion;
    private long _decodedInWindow;
    private double _decodeMsSum;
    private long _renderedInWindow;
    private double _renderMsSum;
    private long _telWindowStartMs = Environment.TickCount64;
    private long _lastTelemetryLogAtMs = -10_000;
    private uint _lastLoggedTelemetryCfgVer;
    private ResolutionLevel _lastLoggedTelemetryLevel;
    private bool _lastLoggedCongestionWarning;

    public ViewerSession(
        string targetIdOrHost,
        ViewerConfig config,
        LanDiscovery discovery,
        string? selfDeviceId = null,
        DeviceIdentity? identity = null,
        SignalingClient? signalingClient = null)
    {
        TargetIdOrHost = targetIdOrHost;
        _config = config;
        CurrentQuality = Math.Clamp(config.Quality, 10, 100);
        CurrentMaxFps = Math.Clamp(config.MaxFps, 1, 120);
        CurrentLossless = config.Lossless;
        CurrentResolutionMode = config.ResolutionMode;
        _discovery = discovery;
        _selfDeviceId = selfDeviceId;
        _signalingClient = signalingClient;

        // 當解碼器偵測到底層 FrameVersion 不一致 (例如封包遺失或版本跳躍) 時，立即以高優先權向主機發起 Keyframe 重新同步
        _decoder.DesyncDetected += () =>
        {
            Log.Warn("[ViewerSession] 偵測到畫面基準版本不一致 (Desync Detected)，立即發送高優先權 Keyframe 請求！");
            RequestKeyframe();
        };

        if (identity != null)
        {
            _identity = identity;
            _ownsIdentity = false;
        }
        else
        {
            _identity = DeviceIdentity.LoadOrCreate(AppConfig.DataDirectory, config.ViewerId, Environment.MachineName);
            _ownsIdentity = true;
        }
    }

    /// <summary>
    /// 連線至被控端電腦（自動解析 9 位數 Device ID 或 IP 地址）。
    /// </summary>
    public async Task ConnectAsync(string? password, CancellationToken ct, bool forceInteractive = false)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linkedCts.Token;

        string? activeSignalingSessionId = null;
        try
        {
            IPEndPoint? endpoint = null;
            Stream? stream = null;
            string cleanTarget = TargetIdOrHost.Replace(" ", "").Trim();

            if (!string.IsNullOrEmpty(_selfDeviceId) && NetworkHelper.IsSelfTarget(cleanTarget, _selfDeviceId, _config.ViewerId))
            {
                throw new InvalidOperationException("禁止連線到本機自己的 IP 或 ID！");
            }
            if (cleanTarget == _config.ViewerId || (!string.IsNullOrEmpty(_selfDeviceId) && cleanTarget == _selfDeviceId))
            {
                throw new InvalidOperationException("禁止連線到本機自己的 IP 或 ID！");
            }

            // 判斷是否為 9 位數 ID
            if (cleanTarget.Length == 9 && cleanTarget.All(char.IsDigit))
            {
                Log.Info($"正在尋找裝置 ID: {cleanTarget}...");
                DiscoveredDevice? resolved = null;
                try
                {
                    // 1. 優先在區網廣播尋找 (1.5 秒超時)
                    resolved = await _discovery.ResolveAsync(cleanTarget, TimeSpan.FromMilliseconds(1500), token).ConfigureAwait(false);
                }
                catch { }

                if (resolved != null && !resolved.Address.Equals(IPAddress.None))
                {
                    endpoint = resolved.EndPoint;
                    Log.Info($"[區網探索成功] 找到裝置 {cleanTarget} -> {endpoint} (採用內網高速直連)");
                }
                else if (_signalingClient != null)
                {
                    // 2. 區網未找到，向中央協調伺服器查詢目標裝置在線狀態並發起 P2P 打洞
                    Log.Info($"區網廣播未搜尋到，正在向中央伺服器查詢裝置 {cleanTarget}...");
                    DeviceEndpointInfo? dev = null;
                    try
                    {
                        dev = await _signalingClient.ResolveDeviceAsync(cleanTarget, token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"伺服器查詢發生錯誤: {ex.Message}");
                    }

                    if (dev != null && dev.IsOnline)
                    {
                        // 2a. 若對方回報有內網 IP 且與本機處於同一子網段，先嘗試高速內網 TCP 直連
                        if (dev.LocalIps != null && dev.LocalIps.Count > 0)
                        {
                            foreach (var lip in dev.LocalIps)
                            {
                                if (IPAddress.TryParse(lip, out var parsedLip) && NetworkHelper.IsInSameSubnet(parsedLip))
                                {
                                    var fastTcp = new TcpClient(AddressFamily.InterNetwork);
                                    using var quickCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                                    quickCts.CancelAfter(400);
                                    using var reg = quickCts.Token.Register(() => { try { fastTcp.Dispose(); } catch { } });
                                    try
                                    {
                                        int targetPort = dev.TcpPort > 0 ? dev.TcpPort : Protocol.DefaultTcpPort;
                                        await fastTcp.ConnectAsync(new IPEndPoint(parsedLip, targetPort), quickCts.Token).ConfigureAwait(false);
                                        _tcp = fastTcp;
                                        SecureChannel.TuneSocket(fastTcp.Client);
                                        stream = fastTcp.GetStream();
                                        _underlyingStream = stream;
                                        Log.Info($"[內網直連成功] 透過目標回報之同網段內網 IP {parsedLip}:{targetPort} 建立 TCP 連線 (SelectedProtocol=LAN_TCP, Remote={parsedLip}:{targetPort})");
                                        break;
                                    }
                                    catch
                                    {
                                        fastTcp.Dispose();
                                    }
                                }
                            }
                        }

                        // 2b. 若非同網段（內網直連失敗），則發起外網 P2P STUN 打洞連線
                        if (stream == null)
                        {
                            Log.Info($"[中央伺服器] 目標裝置 {cleanTarget} 在線，正在發起外網 P2P 連線工作階段...");
                            string? sessionId = await _signalingClient.CreateSessionAsync(cleanTarget, token).ConfigureAwait(false);
                            if (string.IsNullOrEmpty(sessionId))
                            {
                                throw new InvalidOperationException($"向中央伺服器建立工作階段失敗，請確認與 Signaling Server 通訊正常。");
                            }
                            activeSignalingSessionId = sessionId;

                            Log.Info($"[P2P/ICE] 正在執行 STUN 候選端點收集、UDP Hole Punching 與失敗分類自動重試 (Session: {sessionId})...");
                            var retryOpts = UdpHolePuncher.BuildRetryOptionsFromTimeout(
                                timeoutMs: 15000,
                                retainStandbyBindingsForMonitoring: true);
                            var punchResult = await UdpHolePuncher.PunchHoleWithRetryAsync(
                                _signalingClient, sessionId, _signalingClient.Identity.DeviceId, cleanTarget,
                                isInitiator: true, retryOptions: retryOpts, ct: token).ConfigureAwait(false);
                            var udpStream = punchResult.Stream;

                            if (udpStream == null)
                            {
                                try { await _signalingClient.EndSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false); } catch { }
                                activeSignalingSessionId = null;
                                Log.Warn($"[P2P/ICE] 外網 P2P 直連失敗 (Attempt={punchResult.ConnectionAttemptId}, Session={sessionId}, SelectedProtocol=UDP, ConnectionEstablishmentTime={punchResult.Elapsed.TotalMilliseconds:F0}ms, TotalRetryCount={Math.Max(0, punchResult.TotalRoundsAttempted - 1)}, FinalFailureReason={punchResult.FinalFailureReason})");
                                if (punchResult.FinalFailureReason == P2PFailureReason.Cancelled && token.IsCancellationRequested)
                                {
                                    throw new OperationCanceledException("已取消 P2P 連線作業。", token);
                                }
                                throw new InvalidOperationException($"無法建立外網 P2P 直連穿透（原因：{punchResult.FinalFailureReason}，重試輪數：{punchResult.TotalRoundsAttempted}，詳情：{punchResult.FinalFailureDetail}）。");
                            }

                            stream = udpStream;
                            _underlyingStream = udpStream;
                            _pathContext = punchResult.PathContext;
                            Log.Info($"[P2P/ICE] ★ 外網 P2P 直連通道建立成功 (Attempt={punchResult.ConnectionAttemptId}, Session={sessionId}, SelectedProtocol=UDP, ConnectionEstablishmentTime={punchResult.Elapsed.TotalMilliseconds:F0}ms, Rounds={punchResult.TotalRoundsAttempted})！端點: {udpStream.RemoteEndPoint}");
                        }
                    }
                    else if (dev != null && !dev.IsOnline)
                    {
                        throw new InvalidOperationException($"目標裝置 {cleanTarget} 目前處於離線狀態。\n請確認對方電腦已開啟 RemoteDesk 並連上網際網路。");
                    }
                    else
                    {
                        throw new InvalidOperationException($"在中央伺服器與區域網路中皆找不到裝置 ID {cleanTarget}。\n請確認 ID 是否輸入正確，且被控端電腦已開啟 RemoteDesk。");
                    }
                }
                else
                {
                    throw new InvalidOperationException($"在區網內找不到裝置 ID {cleanTarget}。\n請確認被控端已啟動並連上同一網路；若為跨網段/外網連線，請於設定中開啟中央協調伺服器。");
                }
            }
            else
            {
                // 視為主機名稱或 IP 地址 (支援 host:port 格式)
                string host = cleanTarget;
                int port = Protocol.DefaultTcpPort;
                if (cleanTarget.Contains(':'))
                {
                    var parts = cleanTarget.Split(':');
                    host = parts[0];
                    int.TryParse(parts[1], out port);
                }

                var addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
                var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                         ?? addresses.FirstOrDefault();
                if (ip == null) throw new InvalidOperationException($"無法解析主機名稱: {host}");
                endpoint = new IPEndPoint(ip, port);
            }

            // 若尚未透過 UDP P2P 建立連線，則透過 TCP 連線
            if (stream == null)
            {
                if (endpoint == null) throw new InvalidOperationException("無有效之連線端點位址");
                if (!string.IsNullOrEmpty(_selfDeviceId) && NetworkHelper.IsLocalAddress(endpoint.Address))
                {
                    throw new InvalidOperationException("禁止連線到本機自己的 IP 或 ID！");
                }

                var tcp = new TcpClient(AddressFamily.InterNetwork);
                _tcp = tcp;
                SecureChannel.TuneSocket(tcp.Client);

                var tcpSw = System.Diagnostics.Stopwatch.StartNew();
                Log.Info($"正在連線至 {endpoint} (TCP)...");
                await tcp.ConnectAsync(endpoint, token).ConfigureAwait(false);
                tcpSw.Stop();
                stream = tcp.GetStream();
                _underlyingStream = stream;
                Log.Info($"[LAN/TCP] 內網直連建立完成 (SelectedProtocol=LAN_TCP, Remote={endpoint}, ConnectionEstablishmentTime={tcpSw.ElapsedMilliseconds}ms)");
            }

            // 1. 金鑰協商（保留 activeSignalingSessionId 供 Phase 5 連線期間路徑異常時重新交換候選與判斷 SessionExpired）
            const ProtocolCapabilities offeredCapabilities = ProtocolCapabilities.FrameSeqAck;
            var (channel, hello) = await SecureChannel.ClientHandshakeAsync(
                stream, _identity.DeviceId, _identity.DeviceName, token, offeredCapabilities).ConfigureAwait(false);
            _activeSignalingSessionId = activeSignalingSessionId;
            SupportsFrameSeqAck = (offeredCapabilities & hello.Capabilities & ProtocolCapabilities.FrameSeqAck) != 0;
            _channel = channel;
            HostDeviceId = hello.HostId;
            HostDeviceName = hello.HostName;

            if (hello.HostId == _identity.DeviceId || hello.HostId == _config.ViewerId || (!string.IsNullOrEmpty(_selfDeviceId) && hello.HostId == _selfDeviceId))
            {
                channel.Dispose();
                _underlyingStream?.Dispose();
                _underlyingStream = null;
                _tcp?.Dispose();
                _tcp = null;
                throw new InvalidOperationException("禁止連線到本機自己的 IP 或 ID！");
            }

        if (hello.Busy)
        {
            throw new InvalidOperationException($"主機 {hello.HostName} 目前忙碌中（已有其他連線進行中）。");
        }

        // 2. 身份驗證
        if (!string.IsNullOrEmpty(password) && !hello.HasPassword)
        {
            Log.Info($"[Auth] 目標主機 {hello.HostName} ({hello.HostId}) 未啟用密碼保護，略過舊快取密碼並改採一般授權驗證流程。");
            ClearStaleSavedPassword(cleanTarget, hello.HostId);
            password = null;
        }

        byte[] sentNonce = Array.Empty<byte>();
        long sentTimestamp = 0;

        if (!string.IsNullOrEmpty(password) && hello.HasPassword)
        {
            var key = Crypto.DerivePasswordKey(password, hello.Salt, hello.Iterations);
            var proof = Crypto.PasswordProof(key, channel.Transcript);
            var authMsg = new PacketWriter((byte)AuthMethod.Password).Bytes(proof).ToArray();
            await channel.SendAsync(authMsg, token).ConfigureAwait(false);
        }
        else if (forceInteractive)
        {
            if (!hello.AllowInteractive)
            {
                throw new InvalidOperationException("此主機未啟用互動授權連線。");
            }
            Log.Info($"使用者選擇發起互動授權，安全驗證碼: {channel.VerificationCode}，等待對方按「允許」...");
            var authMsg = new PacketWriter((byte)AuthMethod.RequestAccess).ToArray();
            await channel.SendAsync(authMsg, token).ConfigureAwait(false);
        }
        else
        {
            // 預設採用 Device Identity 公私鑰數位簽章驗證（支援無人值守與首次配對）
            sentNonce = RandomNumberGenerator.GetBytes(16);
            sentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            byte[] dataToSign = Crypto.Concat(channel.Transcript, sentNonce, BitConverter.GetBytes(sentTimestamp), Encoding.UTF8.GetBytes(hello.HostId));
            byte[] sig = _identity.SignData(dataToSign);

            var authMsg = new PacketWriter((byte)AuthMethod.DeviceIdentity)
                .Str(_identity.DeviceId)
                .Str(_identity.DeviceName)
                .Bytes(_identity.PublicKeySpki)
                .Bytes(sentNonce)
                .I64(sentTimestamp)
                .Bytes(sig)
                .ToArray();
            await channel.SendAsync(authMsg, token).ConfigureAwait(false);
        }

        var resp = await channel.ReceiveAsync(token).ConfigureAwait(false);

        // 處理主機端雙軌並行 (0x02: 主機已自動在被控端螢幕彈出連線確認對話盒，同時控制端亦可直接輸入密碼)
        if (resp.Length > 0 && resp[0] == 0x02 && PasswordPromptHandler != null)
        {
            var ctx = new PasswordPromptContext
            {
                HostDisplayName = HostDeviceName,
                HostDeviceId = HostDeviceId,
                SubmitPasswordAsync = async (enteredPwd, remember) =>
                {
                    var key = Crypto.DerivePasswordKey(enteredPwd, hello.Salt, hello.Iterations);
                    var proof = Crypto.PasswordProof(key, channel.Transcript);
                    var authMsg = new PacketWriter((byte)AuthMethod.Password).Bytes(proof).ToArray();
                    await channel.SendAsync(authMsg, token).ConfigureAwait(false);
                    if (remember)
                    {
                        _config.RememberPassword(cleanTarget, enteredPwd);
                        _config.Save();
                    }
                    return true;
                }
            };

            var promptTask = PasswordPromptHandler(ctx);

            while (!token.IsCancellationRequested)
            {
                resp = await channel.ReceiveAsync(token).ConfigureAwait(false);
                if (resp.Length > 0 && resp[0] == 0x00)
                {
                    // 驗證成功（被控端按下接受 或 控制端輸入之密碼正確）
                    ctx.CloseDialogOnRemoteAccepted?.Invoke();
                    break;
                }
                else if (resp.Length > 0 && resp[0] == 0x03)
                {
                    // 密碼錯誤通知
                    var r = new PacketReader(resp.AsSpan(1));
                    string errMsg = r.Remaining > 0 ? r.Str() : "連線密碼錯誤";
                    ctx.ShowErrorMessage?.Invoke(errMsg);
                    // 繼續在迴圈等待下一次輸入密碼或遠端使用者接受
                }
                else
                {
                    // 拒絕或其他錯誤
                    var r = new PacketReader(resp.AsSpan(1));
                    string rejectReason = r.Remaining > 0 ? r.Str() : "連線已被主機拒絕";
                    ctx.CloseDialogOnRemoteAccepted?.Invoke();
                    throw new UnauthorizedAccessException(rejectReason);
                }
            }
        }
        else if (resp.Length < 1 || resp[0] != 0x00)
        {
            var r = new PacketReader(resp.AsSpan(1));
            string reason = r.Remaining > 0 ? r.Str() : "驗證被主機拒絕";
            if (resp[0] == 0x02 ||
                (hello.HasPassword && (reason.StartsWith("NEED_PASSWORD:") || reason.Contains("連線密碼") || reason.Contains("密碼") || reason.Contains("PASSWORD"))))
            {
                string cleanReason = reason.StartsWith("NEED_PASSWORD:") ? reason["NEED_PASSWORD:".Length..] : reason;
                throw new NeedPasswordException(hello.AllowInteractive, cleanReason);
            }
            throw new UnauthorizedAccessException(reason);
        }

        // 若主機回覆公鑰與數位簽章，驗證主機身分
        var respReader = new PacketReader(resp.AsSpan(1));
        if (respReader.Remaining > 0)
        {
            var okMsg = respReader.Str();
            if (respReader.Remaining > 0)
            {
                var hostPub = respReader.Bytes().ToArray();
                var hostSig = respReader.Bytes().ToArray();
                byte[] hostProofData = Crypto.Concat(channel.Transcript, sentNonce, BitConverter.GetBytes(sentTimestamp), Encoding.UTF8.GetBytes(_identity.DeviceId));
                if (!DeviceIdentity.VerifySignature(hostProofData, hostSig, hostPub))
                {
                    throw new InvalidOperationException("主機端數位簽章無效，可能遭遇偽冒主機！");
                }
                HostPublicKeyFingerprint = DeviceIdentity.ComputeFingerprint(hostPub);
                Log.Info($"遠端主機身分已驗證，公鑰指紋: {HostPublicKeyFingerprint}");
            }
        }

        Log.Info($"成功連線至 {HostDeviceName} ({HostDeviceId})！");

        // 3. 送出初始視訊品質設定與解析度模式
        SendVideoSettings(_config.Quality, _config.MaxFps, _config.Lossless, _config.ResolutionMode);
        SendImeMode(_config.ImeMode);

        // 4. 若為 WAN UDP P2P 連線，啟動 Phase 5 P2P 路徑健康監控與自動重連協作
        if (_underlyingStream is ReliableUdpStream udpPathStream && _pathContext != null)
        {
            _pathMonitor = new P2PPathMonitor(
                udpPathStream,
                _pathContext,
                _signalingClient,
                _pathMonitorOptionsForTest,
                () => _peerClosedCleanly || _disposed);
            _pathMonitor.PathRecoveryStarted += _ => _decoder.BeginPathReconnection();
            _pathMonitor.PathRecoveryCompleted += report =>
            {
                if (report.Succeeded)
                {
                    _decoder.CompletePathReconnection();
                    if (IsConnected) RequestKeyframe();
                }
            };
            _pathMonitor.PathClosedNormally += (reason, detail) =>
            {
                _peerClosedCleanly = true;
                FireDisconnected(reason == P2PPathFailureType.PeerSessionTerminated
                    ? "遠端工作階段已正常結束"
                    : "遠端主機已主動結束連線");
                _cts.Cancel();
            };
            _pathMonitor.PathPermanentlyFailed += (reason, detail) =>
            {
                FireDisconnected($"P2P 連線中斷且無法自動恢復 ({reason}): {detail}");
                _cts.Cancel();
            };
        }

        Connected?.Invoke();
        _recvTask = Task.Run(ReceiveLoopAsync);
        _pingTask = Task.Run(PingLoopAsync);
    }
    catch
    {
        EndActiveSignalingSessionBestEffort();
        _pathMonitor?.Dispose();
        _pathMonitor = null;
        _pathContext?.Dispose();
        _pathContext = null;
        _channel?.Dispose();
        _channel = null;
        _underlyingStream?.Dispose();
        _underlyingStream = null;
        _tcp?.Dispose();
        _tcp = null;
        throw;
    }
}

    private async Task ReceiveLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested && _channel != null)
            {
                var msg = await _channel.ReceiveAsync(ct).ConfigureAwait(false);
                if (msg.Length == 0) continue;
                Dispatch(msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Info($"與遠端主機的連線中斷: {ex.Message}");
            FireDisconnected(ex.Message);
        }
        finally
        {
            Dispose();
        }
    }

    internal void DispatchForTest(byte[] msg) => Dispatch(msg);

    private void Dispatch(byte[] msg)
    {
        var r = new PacketReader(msg.AsSpan(1));
        switch ((HostMsg)msg[0])
        {
            case HostMsg.Disconnect:
            {
                Log.Info("遠端主機已結束連線");
                _peerClosedCleanly = true;
                _pathMonitor?.NotifyPeerClosedNormally("HostMsg.Disconnect received");
                FireDisconnected("遠端主機已主動結束連線");
                _cts.Cancel();
                break;
            }
            case HostMsg.MonitorList:
            {
                var (list, selected) = MessageCodec.ReadMonitorList(ref r);
                Monitors = list;
                SelectedMonitor = selected;
                MonitorsUpdated?.Invoke(list, selected);
                break;
            }
            case HostMsg.StreamInfo:
            {
                SelectedMonitor = r.Str();
                int w = r.I32();
                int h = r.I32();
                OriginalWidth = w;
                OriginalHeight = h;
                _decoder.Resize(w, h);
                ResolutionChanged?.Invoke(w, h);
                RequestKeyframe();
                break;
            }
            case HostMsg.StreamConfig:
            {
                var (cfgVer, origW, origH, streamW, streamH, level, mode, reason) = MessageCodec.ReadStreamConfig(ref r);
                OriginalWidth = origW;
                OriginalHeight = origH;
                StreamConfigVersion = cfgVer;
                CurrentResolutionLevel = level;
                if (_pendingMode is { } pendingMode && mode != pendingMode && (Environment.TickCount64 - _pendingModeSetAtMs) < 1500)
                {
                    CurrentResolutionMode = pendingMode;
                }
                else
                {
                    _pendingMode = null;
                    CurrentResolutionMode = mode;
                }
                LastSwitchReason = reason;

                _decoder.ConfigureStream(cfgVer, streamW, streamH);
                SendStreamConfigAck(cfgVer, streamW, streamH);
                StreamConfigChanged?.Invoke(origW, origH, streamW, streamH, level, CurrentResolutionMode, reason);
                ResolutionChanged?.Invoke(origW, origH);
                break;
            }
            case HostMsg.StreamTelemetry:
            {
                var telemetry = MessageCodec.ReadStreamTelemetry(ref r);
                CurrentTelemetry = telemetry;
                CurrentResolutionLevel = telemetry.Level;
                if (_pendingMode is { } pendingMode && telemetry.Mode != pendingMode && (Environment.TickCount64 - _pendingModeSetAtMs) < 1500)
                {
                    // P1-01: 剛發起模式切換後、收到切換前在途的舊 Telemetry 時，不得將 UI 與 Session 暫態倒退回舊模式
                    CurrentResolutionMode = pendingMode;
                }
                else
                {
                    _pendingMode = null;
                    CurrentResolutionMode = telemetry.Mode;
                }
                if (!string.IsNullOrEmpty(telemetry.LastSwitchReason))
                    LastSwitchReason = telemetry.LastSwitchReason;
                LogTelemetryIfDue(telemetry);
                TelemetryUpdated?.Invoke(telemetry);
                break;
            }
            case HostMsg.VideoFrame:
            {
                LastFrameReceivedTicks = DateTime.UtcNow.Ticks;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var dirty = _decoder.DecodeFrame(
                    msg.AsSpan(1),
                    out var seq,
                    out _,
                    out _,
                    out _,
                    out _,
                    out var decodeStatus);
                sw.Stop();

                LastDecodeStatusForTest = decodeStatus;
                LastFrameDecodeCompletedTicks = DateTime.UtcNow.Ticks;
                LastFrameDecodeDurationMs = sw.Elapsed.TotalMilliseconds;

                // P1-03 ACK/NACK 明確語意（與 Reliable UDP TypeAck 0x02 完全正交）：
                // 1. TypeAck 0x02 由 ReliableUdpStream 在 Socket 接收/組裝成功時立即釋放傳輸層重傳視窗 (_sendWindowSemaphore)；
                // 2. ViewerMsg.FrameAck 0x48 僅用於 HostSession 應用層擷取管線視窗 (_inflight, MaxInflight=2)；
                // 3. 必須在 DecodeFrame 完成後才回傳 FrameAck：
                //    - Applied / EmptyNoChange：正常套用後 ACK，讓 Host 取得真實解碼完成時序與背壓；
                //    - StaleConfigVersion：安全丟棄（不改畫布、不計失敗、不重發 Keyframe），仍送出 FrameAck 釋放舊組態管線額度；
                //    - Desynced / Failed：DecodeFrame 已透過 DesyncDetected 送出高優先權 RequestKeyframe (顯式 NACK)，
                //      此處絕不誤送 FrameAck；Host 端收到 RequestKeyframe 會立即歸零 _inflight 並重新產生 FullFrame。
                if (decodeStatus is FrameDecodeStatus.Applied or FrameDecodeStatus.EmptyNoChange or FrameDecodeStatus.StaleConfigVersion)
                {
                    SendFrameAck(seq);
                }

                if (decodeStatus == FrameDecodeStatus.Applied && !dirty.IsEmpty)
                {
                    Interlocked.Increment(ref _decodedFrameVersion);
                    lock (_telLock)
                    {
                        _decodedInWindow++;
                        _decodeMsSum += LastFrameDecodeDurationMs;
                    }
                    FrameReceived?.Invoke(dirty, seq);
                }
                break;
            }
            case HostMsg.CursorShape:
            {
                bool visible = r.Bool();
                if (!visible)
                {
                    CursorShapeChanged?.Invoke(null);
                    break;
                }
                int stdId = r.I32();
                if (stdId != 0)
                {
                    // 標準游標
                    var hCur = Win32.LoadCursor(IntPtr.Zero, stdId);
                    var cur = hCur != IntPtr.Zero ? new Cursor(hCur) : Cursors.Default;
                    CursorShapeChanged?.Invoke(cur);
                }
                else
                {
                    int hx = r.I32(), hy = r.I32(), cw = r.I32(), ch = r.I32();
                    var bgra = r.Bytes();
                    var cur = CreateCustomCursor(cw, ch, hx, hy, bgra);
                    CursorShapeChanged?.Invoke(cur);
                }
                break;
            }
            case HostMsg.Wallpaper:
            {
                var wp = MessageCodec.ReadWallpaper(ref r);
                WallpaperReceived?.Invoke(wp);
                break;
            }
            case HostMsg.ImeState:
            {
                var ime = MessageCodec.ReadIme(ref r);
                ImeStateReceived?.Invoke(ime);
                break;
            }
            case HostMsg.Caret:
            {
                bool hasCaret = r.Bool();
                int cx = r.I32(), cy = r.I32(), ch = r.I32();
                if (hasCaret) CaretReceived?.Invoke(cx, cy, ch);
                break;
            }
            case HostMsg.Clipboard:
            {
                string text = r.Str();
                ClipboardReceived?.Invoke(text);
                break;
            }
            case HostMsg.Pong:
            {
                long sentTime = r.I64();
                int rtt = (int)(Stopwatch.GetTimestamp() - sentTime);
                int rttMs = (int)(rtt * 1000L / Stopwatch.Frequency);
                CurrentRttMs = rttMs;
                RttUpdated?.Invoke(rttMs);
                break;
            }
        }
    }

    private static Cursor? CreateCustomCursor(int w, int h, int hx, int hy, ReadOnlySpan<byte> bgra)
    {
        if (w <= 0 || h <= 0 || bgra.Length < w * h * 4) return Cursors.Default;
        try
        {
            using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                Marshal.Copy(bgra.ToArray(), 0, bd.Scan0, w * h * 4);
            }
            finally { bmp.UnlockBits(bd); }

            var iconHandle = bmp.GetHicon();
            if (Win32.GetIconInfo(iconHandle, out var iconInfo))
            {
                iconInfo.xHotspot = hx;
                iconInfo.yHotspot = hy;
                iconInfo.fIcon = false;
                var cursorHandle = Win32.CreateIconIndirect(ref iconInfo);
                Win32.DestroyIcon(iconHandle);
                if (iconInfo.hbmColor != IntPtr.Zero) Win32.DeleteObject(iconInfo.hbmColor);
                if (iconInfo.hbmMask != IntPtr.Zero) Win32.DeleteObject(iconInfo.hbmMask);
                if (cursorHandle != IntPtr.Zero) return new Cursor(cursorHandle);
            }
        }
        catch { }
        return Cursors.Default;
    }

    public void ReportFrameRendered(double renderMs) =>
        ReportFrameRendered(renderMs, decodedFrameVersion: 0);

    /// <summary>
    /// 回報畫面已實際繪製到視窗。當提供 decodedFrameVersion (> 0) 時，會對相同影格版本去重，
    /// 避免滑鼠經過、視窗重疊或尺寸調整觸發的多餘 WM_PAINT 重繪虛增 ViewerRenderFps。
    /// </summary>
    public bool ReportFrameRendered(double renderMs, long decodedFrameVersion)
    {
        LastRenderDurationMs = renderMs;
        lock (_telLock)
        {
            if (decodedFrameVersion > 0)
            {
                if (decodedFrameVersion <= _lastCountedRenderVersion)
                    return false;
                _lastCountedRenderVersion = decodedFrameVersion;
            }
            _renderedInWindow++;
            _renderMsSum += renderMs;
            return true;
        }
    }

    internal ViewerTelemetrySnapshot CollectViewerTelemetrySnapshotForTest() =>
        CollectViewerTelemetrySnapshot();

    private ViewerTelemetrySnapshot CollectViewerTelemetrySnapshot()
    {
        long nowMs = Environment.TickCount64;
        long rendered, decoded;
        double renderSum, decodeSum;
        double elapsedSec;
        lock (_telLock)
        {
            elapsedSec = Math.Max(0.1, (nowMs - _telWindowStartMs) / 1000.0);
            rendered = _renderedInWindow;
            decoded = _decodedInWindow;
            renderSum = _renderMsSum;
            decodeSum = _decodeMsSum;

            _renderedInWindow = 0;
            _decodedInWindow = 0;
            _renderMsSum = 0;
            _decodeMsSum = 0;
            _telWindowStartMs = nowMs;
        }

        // P1-04: RenderFps 嚴格僅計算視窗內實際繪製的新影格，不以 decoded 或重複 WM_PAINT 充數；靜態桌面閒置時明確歸零，不永久鎖存上一幀耗時。
        float fps = (float)(rendered / elapsedSec);
        float avgDec = decoded > 0 ? (float)(decodeSum / decoded) : 0f;
        float avgRen = rendered > 0 ? (float)(renderSum / rendered) : 0f;
        bool isIdle = decoded == 0 && rendered == 0;
        float e2e = isIdle ? 0f : (CurrentRttMs * 0.5f) + avgDec + avgRen;

        return new ViewerTelemetrySnapshot
        {
            ConfigVersion = _decoder.StreamConfigVersion,
            RenderFps = fps,
            AvgDecodeDurationMs = avgDec,
            AvgRenderDurationMs = avgRen,
            EndToEndDelayMs = e2e,
            DecodeFailureCount = _decoder.DecodeFailureCount,
            TelemetryWindowMs = 500,
            IsIdle = isIdle
        };
    }

    private async Task PingLoopAsync()
    {
        var ct = _cts.Token;
        int tick = 0;
        while (!ct.IsCancellationRequested && _channel != null)
        {
            try
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                tick++;

                var vt = CollectViewerTelemetrySnapshot();
                await _channel.SendAsync(MessageCodec.ViewerTelemetry(vt), highPriority: true, ct).ConfigureAwait(false);

                if (tick % 4 == 0)
                {
                    var w = new PacketWriter((byte)ViewerMsg.Ping, 16).I64(Stopwatch.GetTimestamp());
                    await _channel.SendAsync(w.ToArray(), highPriority: true, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    public void SelectMonitor(string deviceName)
    {
        SelectedMonitor = deviceName;
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.SelectMonitor).Str(deviceName).ToArray(), highPriority: true);
    }

    public void SendMouseMove(int x, int y) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.MouseMove, 16).I32(x).I32(y).ToArray(), highPriority: true);

    public void SendMouseButton(MouseButtonId btn, bool down, int x, int y) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.MouseButton, 16)
            .U8((byte)btn).Bool(down).I32(x).I32(y).ToArray(), highPriority: true);

    public void SendMouseWheel(int delta, bool horizontal, int x, int y) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.MouseWheel, 16)
            .I32(delta).Bool(horizontal).I32(x).I32(y).ToArray(), highPriority: true);

    public void SendKey(ushort vk, ushort scan, KeyFlags flags) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.Key, 16)
            .U16(vk).U16(scan).U8((byte)flags).ToArray(), highPriority: true);

    public void SendText(string text) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.Text).Str(text).ToArray(), highPriority: true);

    public void SendImeSet(ImeState state) =>
        _channel?.TrySendAsync(MessageCodec.Ime((byte)ViewerMsg.ImeSet, state), highPriority: true);

    public void SendImeMode(ImeSyncMode mode) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.ImeMode, 2).U8((byte)mode).ToArray(), highPriority: true);

    public void SendLockKeys(byte state) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.LockKeys, 2).U8(state).ToArray(), highPriority: true);

    public void SendSpecial(SpecialAction action) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.Special, 2).U8((byte)action).ToArray(), highPriority: true);

    public void SetResolutionMode(ResolutionMode mode) =>
        SendVideoSettings(CurrentQuality, CurrentMaxFps, CurrentLossless, mode);

    public void SendVideoSettings(int quality, int maxFps, bool lossless) =>
        SendVideoSettings(quality, maxFps, lossless, CurrentResolutionMode);

    public void SendVideoSettings(int quality, int maxFps, bool lossless, ResolutionMode mode)
    {
        quality = Math.Clamp(quality, 10, 100);
        maxFps = Math.Clamp(maxFps, 1, 120);
        CurrentQuality = quality;
        CurrentMaxFps = maxFps;
        CurrentLossless = lossless;
        CurrentResolutionMode = mode;
        _config.Quality = quality;
        _config.MaxFps = maxFps;
        _config.Lossless = lossless;
        _config.ResolutionMode = mode;
        _pendingMode = mode;
        _pendingModeSetAtMs = Environment.TickCount64;
        LastSentVideoSettingsForTest = (quality, maxFps, lossless, mode);
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 5)
            .U8((byte)quality).U8((byte)maxFps).Bool(lossless).U8((byte)mode).ToArray(), highPriority: true);
    }

    public void SendStreamConfigAck(uint configVersion, int streamWidth, int streamHeight) =>
        _channel?.TrySendAsync(MessageCodec.StreamConfigAck(configVersion, streamWidth, streamHeight), highPriority: true);

    public void SendClipboard(string text) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.Clipboard).Str(text).ToArray(), highPriority: true);

    public void SendFrameAck(uint frameSeq)
    {
        Interlocked.Increment(ref _frameAckCount);
        LastAckedFrameSeqForTest = frameSeq;
        LastFrameAckSentTicks = DateTime.UtcNow.Ticks;
        byte[] pkt = SupportsFrameSeqAck
            ? MessageCodec.FrameAck(frameSeq)
            : MessageCodec.LegacyFrameAck();
        _channel?.TrySendAsync(pkt, highPriority: true);
    }

    public void RequestKeyframe()
    {
        Interlocked.Increment(ref _keyframeRequestCount);
        LastKeyframeRequestedTicks = DateTime.UtcNow.Ticks;
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.RequestKeyframe, 1).ToArray(), highPriority: true);
    }

    public void SendDisconnect()
    {
        _peerClosedCleanly = true;
        _pathMonitor?.NotifyPeerClosedNormally("Viewer initiated disconnect");
        try
        {
            if (_channel != null && !_disposed)
            {
                var pkt = new PacketWriter((byte)ViewerMsg.Disconnect, 1).ToArray();
                _channel.SendSync(pkt, timeoutMs: 300);
            }
        }
        catch { }
        finally
        {
            EndActiveSignalingSessionBestEffort();
            try { _tcp?.Client?.Shutdown(SocketShutdown.Both); } catch { }
            _cts.Cancel();
        }
    }

    private void EndActiveSignalingSessionBestEffort()
    {
        string? sid = Interlocked.Exchange(ref _activeSignalingSessionId, null);
        if (sid != null && _signalingClient != null)
        {
            try
            {
                using var endCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
                _signalingClient.EndSessionAsync(sid, endCts.Token).GetAwaiter().GetResult();
            }
            catch { }
        }
    }

    private void ClearStaleSavedPassword(string cleanTarget, string hostId)
    {
        bool changed = false;
        if (!string.IsNullOrEmpty(cleanTarget) && _config.SavedPasswords.Remove(cleanTarget))
            changed = true;
        if (!string.IsNullOrEmpty(TargetIdOrHost) && _config.SavedPasswords.Remove(TargetIdOrHost))
            changed = true;
        if (!string.IsNullOrEmpty(hostId) && _config.SavedPasswords.Remove(hostId))
            changed = true;

        if (changed)
            ClearedStaleSavedPassword = true;
    }

    private void LogTelemetryIfDue(HostStreamTelemetry telemetry)
    {
        long now = Environment.TickCount64;
        bool stateChanged = telemetry.ConfigVersion != _lastLoggedTelemetryCfgVer
                         || telemetry.Level != _lastLoggedTelemetryLevel
                         || telemetry.CongestionWarning != _lastLoggedCongestionWarning;
        if (!stateChanged && (telemetry.IsStreamIdle || now - _lastTelemetryLogAtMs < 5000))
        {
            return;
        }

        _lastTelemetryLogAtMs = now;
        _lastLoggedTelemetryCfgVer = telemetry.ConfigVersion;
        _lastLoggedTelemetryLevel = telemetry.Level;
        _lastLoggedCongestionWarning = telemetry.CongestionWarning;
        Log.Info($"[ViewerTelemetry] {telemetry.FormatLogSummary()}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _peerClosedCleanly = true;
        _pathMonitor?.NotifyPeerClosedNormally("ViewerSession disposed");
        EndActiveSignalingSessionBestEffort();
        _cts.Cancel();
        _pathMonitor?.Dispose();
        _pathContext?.Dispose();
        try { _tcp?.Client?.Shutdown(SocketShutdown.Both); } catch { }
        _channel?.Dispose();
        _underlyingStream?.Dispose();
        _tcp?.Dispose();
        _decoder.Dispose();
        if (_ownsIdentity) _identity.Dispose();
    }
}

/// <summary>
/// 雙軌並行連線時，控制端密碼對話盒的上下文回呼介面。
/// </summary>
public sealed class PasswordPromptContext
{
    public string HostDisplayName { get; init; } = "";
    public string HostDeviceId { get; init; } = "";
    public string? InitialError { get; init; }
    public Func<string, bool, Task<bool>> SubmitPasswordAsync { get; init; } = (_, _) => Task.FromResult(false);
    public Action? CloseDialogOnRemoteAccepted { get; set; }
    public Action<string>? ShowErrorMessage { get; set; }
}
