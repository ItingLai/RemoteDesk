using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using RemoteDesk.Common;
using RemoteDesk.Native;
using RemoteDesk.Net;

namespace RemoteDesk.Viewer;

internal sealed class ViewerSession : IDisposable
{
    private readonly ViewerConfig _config;
    private readonly LanDiscovery _discovery;
    private readonly FrameDecoder _decoder = new();
    private readonly CancellationTokenSource _cts = new();

    private TcpClient? _tcp;
    private SecureChannel? _channel;
    private Task? _recvTask;
    private Task? _pingTask;
    private bool _disposed;

    public FrameDecoder Decoder => _decoder;
    public bool IsConnected => _channel != null && !_cts.IsCancellationRequested;

    public string TargetIdOrHost { get; }
    public string HostDeviceId { get; private set; } = "";
    public string HostDeviceName { get; private set; } = "";
    public List<MonitorInfo> Monitors { get; private set; } = new();
    public string SelectedMonitor { get; private set; } = "";
    public int RemoteWidth => _decoder.Width;
    public int RemoteHeight => _decoder.Height;
    public int CurrentRttMs { get; private set; }

    public event Action? Connected;
    public event Action<string>? Disconnected;
    public event Action<Rectangle, uint>? FrameReceived;
    public event Action<List<MonitorInfo>, string>? MonitorsUpdated;
    public event Action<Cursor?>? CursorShapeChanged;
    public event Action<ImeState>? ImeStateReceived;
    public event Action<int, int, int>? CaretReceived;
    public event Action<string>? ClipboardReceived;
    public event Action<int>? RttUpdated;

    public ViewerSession(string targetIdOrHost, ViewerConfig config, LanDiscovery discovery)
    {
        TargetIdOrHost = targetIdOrHost;
        _config = config;
        _discovery = discovery;
    }

    /// <summary>
    /// 連線至被控端電腦（自動解析 9 位數 Device ID 或 IP 地址）。
    /// </summary>
    public async Task ConnectAsync(string? password, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linkedCts.Token;

        IPEndPoint endpoint;
        string cleanTarget = TargetIdOrHost.Replace(" ", "").Trim();

        // 判斷是否為 9 位數 ID
        if (cleanTarget.Length == 9 && cleanTarget.All(char.IsDigit))
        {
            Log.Info($"正在區域網路中搜尋裝置 ID: {cleanTarget}...");
            var resolved = await _discovery.ResolveAsync(cleanTarget, TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            if (resolved == null || resolved.Address.Equals(IPAddress.None))
            {
                throw new InvalidOperationException($"在區網內找不到裝置 ID {cleanTarget}。請確認被控端 RemoteDesk 已啟動並連上同一網路。");
            }
            endpoint = resolved.EndPoint;
            Log.Info($"已找到裝置 {cleanTarget} -> {endpoint}");
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

        var tcp = new TcpClient(AddressFamily.InterNetwork);
        _tcp = tcp;
        SecureChannel.TuneSocket(tcp.Client);

        Log.Info($"正在連線至 {endpoint}...");
        await tcp.ConnectAsync(endpoint, token).ConfigureAwait(false);

        // 1. 金鑰協商
        var stream = tcp.GetStream();
        var (channel, hello) = await SecureChannel.ClientHandshakeAsync(
            stream, _config.ViewerId, Environment.MachineName, token).ConfigureAwait(false);
        _channel = channel;
        HostDeviceId = hello.HostId;
        HostDeviceName = hello.HostName;

        if (hello.Busy)
        {
            throw new InvalidOperationException($"主機 {hello.HostName} 目前忙碌中（已有其他連線進行中）。");
        }

        // 2. 身份驗證
        if (!string.IsNullOrEmpty(password))
        {
            var key = Crypto.DerivePasswordKey(password, hello.Salt, hello.Iterations);
            var proof = Crypto.PasswordProof(key, channel.Transcript);
            var authMsg = new PacketWriter((byte)AuthMethod.Password).Bytes(proof).ToArray();
            await channel.SendAsync(authMsg, token).ConfigureAwait(false);
        }
        else
        {
            if (!hello.AllowInteractive)
            {
                throw new InvalidOperationException("此主機未啟用無密碼互動授權，請輸入連線密碼。");
            }
            Log.Info($"主機需要互動授權，安全驗證碼: {channel.VerificationCode}，等待對方按「允許」...");
            var authMsg = new PacketWriter((byte)AuthMethod.RequestAccess).ToArray();
            await channel.SendAsync(authMsg, token).ConfigureAwait(false);
        }

        var resp = await channel.ReceiveAsync(token).ConfigureAwait(false);
        if (resp.Length < 1 || resp[0] != 0x00)
        {
            var r = new PacketReader(resp.AsSpan(1));
            string reason = r.Remaining > 0 ? r.Str() : "驗證被主機拒絕";
            throw new UnauthorizedAccessException(reason);
        }

        Log.Info($"成功連線至 {HostDeviceName} ({HostDeviceId})！");

        // 3. 送出初始視訊品質設定
        SendVideoSettings(_config.Quality, _config.MaxFps, _config.Lossless);
        SendImeMode(_config.ImeMode);

        Connected?.Invoke();
        _recvTask = Task.Run(ReceiveLoopAsync);
        _pingTask = Task.Run(PingLoopAsync);
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
            Disconnected?.Invoke(ex.Message);
        }
        finally
        {
            Dispose();
        }
    }

    private void Dispatch(byte[] msg)
    {
        var r = new PacketReader(msg.AsSpan(1));
        switch ((HostMsg)msg[0])
        {
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
                _decoder.Resize(w, h);
                RequestKeyframe();
                break;
            }
            case HostMsg.VideoFrame:
            {
                var dirty = _decoder.DecodeFrame(msg.AsSpan(1), out var seq);
                SendFrameAck();
                FrameReceived?.Invoke(dirty, seq);
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

    private async Task PingLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested && _channel != null)
        {
            try
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
                var w = new PacketWriter((byte)ViewerMsg.Ping, 16).I64(Stopwatch.GetTimestamp());
                await _channel.SendAsync(w.ToArray(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    public void SelectMonitor(string deviceName)
    {
        SelectedMonitor = deviceName;
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.SelectMonitor).Str(deviceName).ToArray());
    }

    public void SendMouseMove(int x, int y) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.MouseMove, 16).I32(x).I32(y).ToArray());

    public void SendMouseButton(MouseButtonId btn, bool down, int x, int y) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.MouseButton, 16)
            .U8((byte)btn).Bool(down).I32(x).I32(y).ToArray());

    public void SendMouseWheel(int delta, bool horizontal, int x, int y) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.MouseWheel, 16)
            .I32(delta).Bool(horizontal).I32(x).I32(y).ToArray());

    public void SendKey(ushort vk, ushort scan, KeyFlags flags) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.Key, 16)
            .U16(vk).U16(scan).U8((byte)flags).ToArray());

    public void SendText(string text) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.Text).Str(text).ToArray());

    public void SendImeSet(ImeState state) =>
        _channel?.TrySendAsync(MessageCodec.Ime((byte)ViewerMsg.ImeSet, state));

    public void SendImeMode(ImeSyncMode mode) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.ImeMode, 2).U8((byte)mode).ToArray());

    public void SendLockKeys(byte state) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.LockKeys, 2).U8(state).ToArray());

    public void SendSpecial(SpecialAction action) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.Special, 2).U8((byte)action).ToArray());

    public void SendVideoSettings(int quality, int maxFps, bool lossless) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.VideoSettings, 4)
            .U8((byte)quality).U8((byte)maxFps).Bool(lossless).ToArray());

    public void SendClipboard(string text) =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.Clipboard).Str(text).ToArray());

    public void SendFrameAck() =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.FrameAck, 1).ToArray());

    public void RequestKeyframe() =>
        _channel?.TrySendAsync(new PacketWriter((byte)ViewerMsg.RequestKeyframe, 1).ToArray());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _channel?.Dispose();
        _tcp?.Dispose();
        _decoder.Dispose();
    }
}
