using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using RemoteDesk.Common;
using RemoteDesk.Net;

namespace RemoteDesk.Host;

/// <summary>
/// 被控端服務核心：監聽 TCP 連線、執行金鑰協商與身份驗證（密碼或互動彈窗同意），
/// 並將連線交付給 HostSession 進行高速螢幕擷取與輸入注入。
/// </summary>
internal sealed class HostServer : IDisposable
{
    private readonly HostConfig _config;
    private readonly LanDiscovery _discovery;
    private readonly IHostUi _ui;
    private readonly TrustStore _trustStore;
    private readonly DeviceIdentity _identity;
    private readonly bool _ownsIdentity;
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private HostSession? _activeSession;
    private readonly object _sessionLock = new();
    private bool _disposed;
    private SignalingClient? _signalingClient;
    private CancellationTokenSource? _wanCts;
    private Task? _wanListenerTask;
    private P2PPathMonitor? _activePathMonitor;
    private P2PPathMonitorOptions? _pathMonitorOptionsForTest;

    public HostServer(HostConfig config, LanDiscovery discovery, IHostUi ui, TrustStore? trustStore = null, DeviceIdentity? identity = null)
    {
        _config = config;
        _discovery = discovery;
        _ui = ui;
        _trustStore = trustStore ?? new TrustStore();
        if (identity != null)
        {
            _identity = identity;
            _ownsIdentity = false;
        }
        else
        {
            _identity = DeviceIdentity.LoadOrCreate(AppConfig.SharedDirectory, _config.DeviceId, _config.DeviceName);
            _ownsIdentity = true;
        }
    }

    public TrustStore TrustStore => _trustStore;
    public DeviceIdentity Identity => _identity;

    public bool IsRunning => _listener != null;
    public HostSession? ActiveSession { get { lock (_sessionLock) return _activeSession; } }
    public P2PPathMetrics? CurrentPathMetrics => Volatile.Read(ref _activePathMonitor)?.GetMetrics();
    internal P2PPathMonitor? ActivePathMonitorForTest => Volatile.Read(ref _activePathMonitor);
    internal void ConfigurePathMonitorForTest(P2PPathMonitorOptions options) => _pathMonitorOptionsForTest = options;

    public void Start()
    {
        if (_listener != null) return;
        try
        {
            _listener = new TcpListener(IPAddress.Any, _config.TcpPort);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(10);
            Log.Info($"主機伺服器啟動於 TCP 連接埠 {_config.TcpPort}");

            UpdateAdvert();

            _acceptTask = Task.Run(AcceptLoopAsync);
        }
        catch (Exception ex)
        {
            Log.Error($"無法啟動主機伺服器於連接埠 {_config.TcpPort}", ex);
            throw;
        }
    }

    public void UpdateAdvert()
    {
        if (!_config.HostEnabled)
        {
            _discovery.SetAdvert(null);
            return;
        }

        bool isBusy;
        lock (_sessionLock) { isBusy = _activeSession != null; }

        _discovery.SetAdvert(new LanDiscovery.HostAdvert
        {
            Id = _config.DeviceId,
            Name = _config.DeviceName,
            Port = _config.TcpPort,
            Busy = isBusy
        });
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener != null)
        {
            try
            {
                var socket = await _listener.AcceptSocketAsync(_cts.Token).ConfigureAwait(false);
                SecureChannel.TuneSocket(socket);
                _ = Task.Run(() => HandleClientAsync(socket, _cts.Token));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.Warn($"Accept 失敗: {ex.Message}");
            }
        }
    }

    public void SetSignalingClient(SignalingClient? client)
    {
        if (ReferenceEquals(_signalingClient, client)) return;
        _wanCts?.Cancel();
        _signalingClient = client;
        if (client != null && !_disposed)
        {
            _wanCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _wanListenerTask = Task.Run(() => WanSessionListenerLoopAsync(client, _wanCts.Token));
        }
    }

    private async Task WanSessionListenerLoopAsync(SignalingClient client, CancellationToken ct)
    {
        var handledSessions = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        Log.Info("[HostServer] 啟動 WAN P2P 連線監聽服務...");

        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                var now = DateTime.UtcNow;
                foreach (var kv in handledSessions)
                {
                    if ((now - kv.Value).TotalMinutes >= 20)
                    {
                        handledSessions.TryRemove(kv.Key, out _);
                    }
                }

                var incoming = await client.GetIncomingSessionsAsync(ct).ConfigureAwait(false);
                foreach (var sess in incoming)
                {
                    if (handledSessions.TryAdd(sess.SessionId, now))
                    {
                        Log.Info($"[HostServer] 收到 WAN 連線請求，來自裝置: {sess.SourceDeviceId} (Session: {sess.SessionId})");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var retryOpts = UdpHolePuncher.BuildRetryOptionsFromTimeout(
                                    timeoutMs: 15000,
                                    gatheringOptions: new IceGatheringOptions
                                    {
                                        StunTimeoutMs = 1500,
                                        DiscoveryOptions = new NetworkDiscoveryOptions { VpnPolicy = _config.VpnCandidatePolicy }
                                    },
                                    retainStandbyBindingsForMonitoring: true);
                                var punchResult = await UdpHolePuncher.PunchHoleWithRetryAsync(
                                    client,
                                    sess.SessionId,
                                    client.Identity.DeviceId,
                                    sess.SourceDeviceId,
                                    isInitiator: false,
                                    retryOptions: retryOpts,
                                    ct: ct).ConfigureAwait(false);
                                var udpStream = punchResult.Stream;

                                if (udpStream != null)
                                {
                                    var remoteEpStr = udpStream.RemoteEndPoint?.ToString() ?? "P2P-UDP";
                                    Log.Info($"[HostServer] WAN P2P 通道建立成功 (Attempt={punchResult.ConnectionAttemptId}, Session={sess.SessionId}, Rounds={punchResult.TotalRoundsAttempted}, SelectedProtocol=UDP, ConnectionEstablishmentTime={punchResult.Elapsed.TotalMilliseconds:F0}ms)，開始安全握手: {remoteEpStr}");
                                    await HandleClientStreamAsync(udpStream, remoteEpStr, ct, punchResult.PathContext, client).ConfigureAwait(false);
                                }
                                else
                                {
                                    Log.Warn($"[HostServer] WAN P2P 打洞失敗 (Attempt={punchResult.ConnectionAttemptId}, Session={sess.SessionId}, SelectedProtocol=UDP, ConnectionEstablishmentTime={punchResult.Elapsed.TotalMilliseconds:F0}ms, Reason={punchResult.FinalFailureReason}, Rounds={punchResult.TotalRoundsAttempted})");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warn($"[HostServer] 處理 WAN 連線請求失敗: {ex.Message}");
                            }
                        }, ct);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warn($"[HostServer] 查詢 WAN 請求異常: {ex.Message}");
            }

            try { await Task.Delay(100, ct).ConfigureAwait(false); } catch { break; }
        }
    }

    private async Task HandleClientAsync(Socket socket, CancellationToken serverCt)
    {
        var remoteEndpoint = socket.RemoteEndPoint?.ToString() ?? "Unknown";
        Log.Info($"收到來自 {remoteEndpoint} 的區域網路 TCP 連線請求");
        Stream stream = new NetworkStream(socket, ownsSocket: true);
        await HandleClientStreamAsync(stream, remoteEndpoint, serverCt).ConfigureAwait(false);
    }

    private async Task HandleClientStreamAsync(
        Stream stream,
        string remoteEndpoint,
        CancellationToken serverCt,
        P2PPathSessionContext? pathContext = null,
        SignalingClient? wanSignalingClient = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        cts.CancelAfter(TimeSpan.FromSeconds(30)); // 握手驗證限時 30 秒

        SecureChannel? channel = null;
        try
        {
            // 1. 安全握手 (ECDH + HKDF + AES-GCM)
            byte[] salt = !string.IsNullOrEmpty(_config.PasswordSalt)
                ? Convert.FromBase64String(_config.PasswordSalt)
                : RandomNumberGenerator.GetBytes(16);

            bool busy;
            lock (_sessionLock) { busy = _activeSession != null; }

            var (secChannel, clientHello) = await SecureChannel.ServerHandshakeAsync(stream, ch =>
                new ServerHello(
                    Protocol.Version,
                    _config.DeviceId,
                    _config.DeviceName,
                    salt,
                    _config.PasswordIterations,
                    _config.HasPassword,
                    _config.AllowInteractive,
                    busy,
                    ch.Capabilities & ProtocolCapabilities.FrameSeqAck
                ), cts.Token).ConfigureAwait(false);

            channel = secChannel;
            cts.CancelAfter(TimeSpan.FromSeconds(60)); // 重設逾時給使用者確認

            if (string.Equals(clientHello.ViewerId, _config.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn($"拒絕來自本機自己的連線請求 ({clientHello.ViewerId})");
                await channel.SendAsync(new PacketWriter((byte)0xFF).Str("禁止連線到本機自己的 IP 或 ID！").ToArray(), cts.Token).ConfigureAwait(false);
                channel.Dispose();
                return;
            }

            if (busy)
            {
                Log.Warn($"主機忙碌中，拒絕來自 {clientHello.ViewerName} ({remoteEndpoint}) 的連線");
                await channel.SendAsync(new PacketWriter((byte)0xFF).Str("主機正在使用中，同一時間僅允許一個連線").ToArray(), cts.Token).ConfigureAwait(false);
                channel.Dispose();
                return;
            }

            // 2. 身份驗證
            var authMsg = await channel.ReceiveAsync(cts.Token).ConfigureAwait(false);
            if (authMsg.Length < 1) throw new InvalidDataException("驗證訊息格式錯誤");

            var authMethod = (AuthMethod)authMsg[0];
            var r = new PacketReader(authMsg.AsSpan(1));
            bool authenticated = false;
            string rejectReason = "驗證失敗";
            byte[] receivedNonce = Array.Empty<byte>();
            long receivedTimestamp = 0;
            string authViewerId = clientHello.ViewerId;

            if (authMethod == AuthMethod.DeviceIdentity)
            {
                var viewerId = r.Str();
                var viewerName = r.Str();
                var viewerPub = r.Bytes().ToArray();
                var nonce = r.Bytes().ToArray();
                long timestamp = r.I64();
                var sig = r.Bytes().ToArray();

                authViewerId = viewerId;
                receivedNonce = nonce;
                receivedTimestamp = timestamp;

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (Math.Abs(now - timestamp) > 300_000)
                {
                    rejectReason = "驗證請求時間戳記已過期或時間未同步";
                }
                else
                {
                    byte[] signedData = Crypto.Concat(channel.Transcript, nonce, BitConverter.GetBytes(timestamp), Encoding.UTF8.GetBytes(_config.DeviceId));
                    if (!DeviceIdentity.VerifySignature(signedData, sig, viewerPub))
                    {
                        rejectReason = "裝置公鑰簽章驗證失敗 (偽造身分)";
                    }
                    else if (string.Equals(viewerId, _config.DeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        rejectReason = "禁止連線到本機自己的 IP 或 ID！";
                    }
                    else
                    {
                        bool isTrusted = _trustStore.IsTrusted(viewerId, viewerPub, out var trustedDev, out bool keyMismatch);

                        if (keyMismatch)
                        {
                            // 公鑰異常變更，不得自動接受，強制警示並要求本機確認
                            if (!_config.AllowInteractive)
                            {
                                rejectReason = "裝置公鑰與歷史配對記錄不符，已拒絕連線";
                            }
                            else
                            {
                                string code = channel.VerificationCode;
                                string fp = DeviceIdentity.ComputeFingerprint(viewerPub);
                                Log.Warn($"[安全警示] 裝置 {viewerName} ({viewerId}) 公鑰變更，提示使用者重新確認");
                                var res = await _ui.ConfirmAccessExtendedAsync(viewerName, viewerId, remoteEndpoint, code, fp, keyMismatch: true, cts.Token).ConfigureAwait(false);
                                if (res.IsAccepted)
                                {
                                    authenticated = true;
                                    if (res.TrustDevice)
                                    {
                                        _trustStore.AddOrUpdate(viewerId, viewerName, viewerPub, autoAccept: true);
                                    }
                                    _trustStore.RecordConnected(viewerId);
                                }
                                else
                                {
                                    rejectReason = "主機使用者拒絕了連線請求";
                                }
                            }
                        }
                        else
                        {
                            bool isAutoAccepted = isTrusted && trustedDev != null && trustedDev.TrustStatus == TrustStatus.Active
                            && _config.UnattendedAccessEnabled && _config.AutoAcceptTrustedDevices && trustedDev.AutoAcceptEnabled;

                        if (isAutoAccepted)
                        {
                            // 無人值守模式啟用且允許自動接受：自動放行！
                            Log.Info($"[無人值守] 自動放行已信任裝置：{viewerName} ({viewerId})");
                            _trustStore.RecordConnected(viewerId);
                            authenticated = true;
                        }
                        else
                        {
                            // 需要驗證：若主機開啟互動授權，自動彈出連線確認視窗（若同時設定了密碼，則啟動雙軌並行）
                            if (_config.AllowInteractive && _config.HasPassword)
                            {
                                // 雙軌並行模式：被控端自動彈出確認視窗，同時控制端亦可直接輸入密碼
                                string code = channel.VerificationCode;
                                string fp = DeviceIdentity.ComputeFingerprint(viewerPub);
                                Log.Info($"[連線請求] 啟動雙軌並行授權（自動彈出確認視窗 + 支援密碼連線）：控制端={viewerName} ID={viewerId} 驗證碼={code} 指紋={fp}");

                                using var dualCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                                var confirmTask = _ui.ConfirmAccessExtendedAsync(viewerName, viewerId, remoteEndpoint, code, fp, keyMismatch: false, dualCts.Token);

                                // 通知控制端：主機已自動彈出視窗，控制端可等待對方接受，亦可輸入密碼
                                var waitPkt = new PacketWriter(0x02).Str("WAITING_APPROVAL_OR_PASSWORD").ToArray();
                                await channel.SendAsync(waitPkt, cts.Token).ConfigureAwait(false);

                                var receiveTask = channel.ReceiveAsync(dualCts.Token);

                                while (!authenticated && !cts.IsCancellationRequested)
                                {
                                    var completedTask = await Task.WhenAny(confirmTask, receiveTask).ConfigureAwait(false);
                                    if (completedTask == confirmTask)
                                    {
                                        var res = await confirmTask.ConfigureAwait(false);
                                        if (res.IsAccepted)
                                        {
                                            authenticated = true;
                                            if (res.TrustDevice)
                                            {
                                                _trustStore.AddOrUpdate(viewerId, viewerName, viewerPub, autoAccept: true);
                                                Log.Info($"已將控制端 {viewerName} ({viewerId}) 加入可信裝置清單");
                                            }
                                            _trustStore.RecordConnected(viewerId);
                                            try { dualCts.Cancel(); } catch { }
                                            break;
                                        }
                                        else
                                        {
                                            rejectReason = "主機使用者拒絕了連線請求";
                                            try { dualCts.Cancel(); } catch { }
                                            break;
                                        }
                                    }
                                    else if (completedTask == receiveTask)
                                    {
                                        byte[] msg;
                                        try { msg = await receiveTask.ConfigureAwait(false); }
                                        catch { break; }

                                        if (msg.Length > 0 && msg[0] == (byte)AuthMethod.Password)
                                        {
                                            var pr = new PacketReader(msg.AsSpan(1));
                                            var proof = pr.Bytes();
                                            var storedVerifier = Convert.FromBase64String(_config.PasswordVerifier!);
                                            var expectedProof = Crypto.PasswordProof(storedVerifier, channel.Transcript);

                                            if (CryptographicOperations.FixedTimeEquals(proof, expectedProof))
                                            {
                                                authenticated = true;
                                                try { dualCts.Cancel(); } catch { } // 密碼驗證通過，自動關閉主機端彈出的確認視窗
                                                Log.Info($"控制端 {viewerName} ({viewerId}) 透過連線密碼驗證成功！");
                                                break;
                                            }
                                            else
                                            {
                                                Log.Warn($"控制端 {viewerName} ({viewerId}) 輸入之連線密碼錯誤");
                                                var errPkt = new PacketWriter(0x03).Str("連線密碼錯誤，請重新輸入，或等待被控端接受連線").ToArray();
                                                await channel.SendAsync(errPkt, cts.Token).ConfigureAwait(false);
                                                receiveTask = channel.ReceiveAsync(dualCts.Token);
                                            }
                                        }
                                        else
                                        {
                                            break;
                                        }
                                    }
                                }
                            }
                            else if (_config.AllowInteractive && !_config.HasPassword)
                            {
                                // 僅互動授權模式：自動彈出連線確認視窗
                                string code = channel.VerificationCode;
                                string fp = DeviceIdentity.ComputeFingerprint(viewerPub);
                                Log.Info($"[連線請求] 彈出螢幕核准對話盒：控制端={viewerName} ID={viewerId} 驗證碼={code}");
                                var res = await _ui.ConfirmAccessExtendedAsync(viewerName, viewerId, remoteEndpoint, code, fp, keyMismatch: false, cts.Token).ConfigureAwait(false);
                                if (res.IsAccepted)
                                {
                                    authenticated = true;
                                    if (res.TrustDevice)
                                    {
                                        _trustStore.AddOrUpdate(viewerId, viewerName, viewerPub, autoAccept: true);
                                        Log.Info($"已將控制端 {viewerName} ({viewerId}) 加入可信裝置清單");
                                    }
                                    _trustStore.RecordConnected(viewerId);
                                }
                                else
                                {
                                    rejectReason = "主機使用者拒絕了連線請求";
                                }
                            }
                            else if (!_config.AllowInteractive && _config.HasPassword)
                            {
                                rejectReason = "NEED_PASSWORD:此主機已設定連線密碼，請輸入連線密碼。";
                            }
                            else
                            {
                                rejectReason = "主機未開啟無人值守存取，且不允許未經核准之連線";
                            }
                        }
                    }
                }
            }
        }
        else if (authMethod == AuthMethod.Password)
            {
                if (!_config.HasPassword)
                {
                    rejectReason = "主機未啟用固定密碼，請使用互動授權連線";
                }
                else
                {
                    var proof = r.Bytes();
                    var storedVerifier = Convert.FromBase64String(_config.PasswordVerifier!);
                    var expectedProof = Crypto.PasswordProof(storedVerifier, channel.Transcript);

                    if (CryptographicOperations.FixedTimeEquals(proof, expectedProof))
                    {
                        authenticated = true;
                    }
                    else
                    {
                        rejectReason = "連線密碼錯誤";
                    }
                }
            }
            else if (authMethod == AuthMethod.RequestAccess)
            {
                if (!_config.AllowInteractive)
                {
                    rejectReason = "主機不允許互動請求連線";
                }
                else
                {
                    // 向本機 UI 發起存取確認對話盒
                    string code = channel.VerificationCode;
                    Log.Info($"等待本機使用者確認連線：控制端={clientHello.ViewerName} ID={clientHello.ViewerId} 驗證碼={code}");
                    bool confirmed = await _ui.ConfirmAccessAsync(
                        clientHello.ViewerName,
                        clientHello.ViewerId,
                        remoteEndpoint,
                        code,
                        cts.Token).ConfigureAwait(false);

                    if (confirmed)
                    {
                        authenticated = true;
                    }
                    else
                    {
                        rejectReason = "主機使用者拒絕了連線請求";
                    }
                }
            }
            else
            {
                rejectReason = "未知的驗證方式";
            }

            if (!authenticated)
            {
                Log.Warn($"連線驗證失敗 ({remoteEndpoint}): {rejectReason}");
                await channel.SendAsync(new PacketWriter((byte)0xFE).Str(rejectReason).ToArray(), cts.Token).ConfigureAwait(false);
                channel.Dispose();
                return;
            }

            // 3. 建立 HostSession 並於回覆 OK 前先行註冊 ActiveSession，避免控制端收到 OK 後立即查詢 ActiveSession 出現競態
            var session = new HostSession(channel, clientHello, remoteEndpoint, _ui);
            P2PPathMonitor? pathMonitor = null;
            if (stream is ReliableUdpStream udpStream && pathContext != null)
            {
                pathMonitor = new P2PPathMonitor(
                    udpStream,
                    pathContext,
                    wanSignalingClient ?? _signalingClient,
                    _pathMonitorOptionsForTest,
                    () => session.IsClosedOrPeerDisconnected);
                pathMonitor.PathRecoveryStarted += _ => session.BeginPathRecovery();
                pathMonitor.PathRecoveryCompleted += report =>
                {
                    if (report.Succeeded)
                        session.CompletePathRecovery(report.NewPathEpoch);
                };
                pathMonitor.PathClosedNormally += (_, _) => session.Disconnect();
                pathMonitor.PathPermanentlyFailed += (_, _) => session.Disconnect();
                Volatile.Write(ref _activePathMonitor, pathMonitor);
            }

            lock (_sessionLock)
            {
                _activeSession = session;
            }
            UpdateAdvert();

            try
            {
                byte[] okPacket;
                if (authMethod == AuthMethod.DeviceIdentity)
                {
                    byte[] hostProofData = Crypto.Concat(channel.Transcript, receivedNonce, BitConverter.GetBytes(receivedTimestamp), Encoding.UTF8.GetBytes(authViewerId));
                    byte[] hostSig = _identity.SignData(hostProofData);
                    okPacket = new PacketWriter((byte)0x00).Str("OK").Bytes(_identity.PublicKeySpki).Bytes(hostSig).ToArray();
                }
                else
                {
                    okPacket = new PacketWriter((byte)0x00).Str("OK").ToArray();
                }
                await channel.SendAsync(okPacket, cts.Token).ConfigureAwait(false);
                Log.Info($"控制端 {clientHello.ViewerName} ({clientHello.ViewerId}) 連線驗證成功，開始遠端作業");

                _ui.SessionStarted(session);
                await session.RunAsync().ConfigureAwait(false);
            }
            finally
            {
                if (pathMonitor != null)
                {
                    if (ReferenceEquals(_activePathMonitor, pathMonitor))
                        Volatile.Write(ref _activePathMonitor, null);
                    pathMonitor.NotifyPeerClosedNormally("HostSession ended");
                    pathMonitor.Dispose();
                }
                pathContext?.Dispose();
                lock (_sessionLock)
                {
                    if (ReferenceEquals(_activeSession, session))
                        _activeSession = null;
                }
                UpdateAdvert();
                _ui.SessionEnded(session);
                session.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info($"連線處理逾時或已取消 ({remoteEndpoint})");
            channel?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"處理客戶端連線發生異常 ({remoteEndpoint})", ex);
            channel?.Dispose();
        }
    }

    public void DisconnectActiveSession()
    {
        lock (_sessionLock)
        {
            _activeSession?.Disconnect();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _wanCts?.Cancel();
        _wanCts?.Dispose();
        DisconnectActiveSession();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        if (_ownsIdentity) _identity.Dispose();
    }
}
