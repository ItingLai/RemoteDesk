using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private HostSession? _activeSession;
    private readonly object _sessionLock = new();
    private bool _disposed;

    public HostServer(HostConfig config, LanDiscovery discovery, IHostUi ui)
    {
        _config = config;
        _discovery = discovery;
        _ui = ui;
    }

    public bool IsRunning => _listener != null;
    public HostSession? ActiveSession { get { lock (_sessionLock) return _activeSession; } }

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

    private async Task HandleClientAsync(Socket socket, CancellationToken serverCt)
    {
        var remoteEndpoint = socket.RemoteEndPoint?.ToString() ?? "Unknown";
        Log.Info($"收到來自 {remoteEndpoint} 的連線請求");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
        cts.CancelAfter(TimeSpan.FromSeconds(30)); // 握手驗證限時 30 秒

        Stream stream = new NetworkStream(socket, ownsSocket: true);
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
                    busy
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

            if (authMethod == AuthMethod.Password)
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

            // 3. 驗證通過，回覆成功標記
            await channel.SendAsync(new PacketWriter((byte)0x00).Str("OK").ToArray(), cts.Token).ConfigureAwait(false);
            Log.Info($"控制端 {clientHello.ViewerName} ({clientHello.ViewerId}) 連線驗證成功，開始遠端作業");

            // 4. 建立並啟動 HostSession
            var session = new HostSession(channel, clientHello, remoteEndpoint, _ui);
            lock (_sessionLock)
            {
                _activeSession = session;
            }
            UpdateAdvert();
            _ui.SessionStarted(session);

            try
            {
                await session.RunAsync().ConfigureAwait(false);
            }
            finally
            {
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
        DisconnectActiveSession();
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }
}
