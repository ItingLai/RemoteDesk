using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RemoteDesk.Common;

internal static class Crypto
{
    public static byte[] DerivePasswordKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    public static byte[] PasswordProof(byte[] passwordKey, byte[] transcript) =>
        HMACSHA256.HashData(passwordKey, Concat("RDSK-AUTH-v1"u8.ToArray(), transcript));

    public static string VerificationCode(byte[] transcript)
    {
        var h = SHA256.HashData(Concat(transcript, "RDSK-CODE"u8.ToArray()));
        return (BinaryPrimitives.ReadUInt32LittleEndian(h) % 1_000_000).ToString("D6");
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
        return r;
    }
}

/// <summary>以 AES-256-GCM 加密的訊息通道（長度前綴分框），金鑰由 ECDH P-256 交換並以 HKDF 衍生。</summary>
internal sealed class SecureChannel : IDisposable
{
    private const int TagSize = 16;
    private readonly Stream _stream;
    private readonly AesGcm _sendAes;
    private readonly AesGcm _recvAes;
    private ulong _sendCounter;
    private ulong _recvCounter;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _lowPriorityLock = new(1, 1);
    private readonly bool _trackWriteWait;
    private int _priorityWaiters;
    private int _pendingLowPriority;
    private readonly QueueWaitWindowTracker _queueWaitTracker = new();
    private long _lastWriteDurationUs;
    private readonly object _throughputLock = new();
    private readonly long[] _bucketBytes = new long[8];
    private readonly long[] _bucketSecondTicks = new long[8]; // 250ms tick index
    private readonly byte[] _lenBuf = new byte[4];
    private bool _disposed;

    public Stream UnderlyingStream => _stream;
    public byte[] Transcript { get; }
    public string VerificationCode => Crypto.VerificationCode(Transcript);
    public long BytesSent;
    public long BytesReceived;
    public int PendingLowPriorityCount => Volatile.Read(ref _pendingLowPriority);
    public int HighPriorityWaiters => Volatile.Read(ref _priorityWaiters);
    public float LastQueueWaitMs => _queueWaitTracker.GetFreshLastMs();
    public float LastWriteDurationMs => (float)(Interlocked.Read(ref _lastWriteDurationUs) / 1000.0);
    public QueueWaitWindowMetrics ConsumeWaitWindowSnapshot() => _queueWaitTracker.ConsumeWindow();

    private void RecordSentBytes(int bytes)
    {
        Interlocked.Add(ref BytesSent, bytes);
        long bucketTick = Environment.TickCount64 / 250;
        int slot = (int)(bucketTick & 7);
        lock (_throughputLock)
        {
            if (_bucketSecondTicks[slot] != bucketTick)
            {
                _bucketSecondTicks[slot] = bucketTick;
                _bucketBytes[slot] = bytes;
            }
            else
            {
                _bucketBytes[slot] += bytes;
            }
        }
    }

    public (float avgMbps, float peakMbps) GetWindowedThroughputMbps(int windowMs = 500)
    {
        long nowTick = Environment.TickCount64 / 250;
        long totalBytes = 0;
        long maxBucketBytes = 0;
        int activeWindowBuckets = Math.Clamp(windowMs / 250, 1, 8);
        lock (_throughputLock)
        {
            for (int i = 0; i < 8; i++)
            {
                if (nowTick - _bucketSecondTicks[i] < activeWindowBuckets && _bucketBytes[i] > 0)
                {
                    totalBytes += _bucketBytes[i];
                    if (_bucketBytes[i] > maxBucketBytes)
                        maxBucketBytes = _bucketBytes[i];
                }
            }
        }

        double windowSec = activeWindowBuckets * 0.25;
        float avgMbps = (float)(totalBytes * 8.0 / windowSec / 1_000_000.0);
        float peakMbps = (float)(maxBucketBytes * 8.0 / 0.25 / 1_000_000.0);
        return (avgMbps, peakMbps);
    }

    private SecureChannel(Stream stream, byte[] sendKey, byte[] recvKey, byte[] transcript)
    {
        _stream = stream;
        _trackWriteWait = stream is not RemoteDesk.Net.ReliableUdpStream;
        _sendAes = new AesGcm(sendKey, TagSize);
        _recvAes = new AesGcm(recvKey, TagSize);
        Transcript = transcript;
    }

    // ---------------- 明文分框（僅握手使用） ----------------
    private static async Task WritePlainAsync(Stream s, byte[] data, CancellationToken ct)
    {
        var buf = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buf, data.Length);
        data.CopyTo(buf, 4);
        await s.WriteAsync(buf, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadPlainAsync(Stream s, int maxLen, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await s.ReadExactlyAsync(lenBuf, ct).ConfigureAwait(false);
        int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (len <= 0 || len > maxLen) throw new InvalidDataException("握手封包長度錯誤");
        var data = new byte[len];
        await s.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        return data;
    }

    // ---------------- 加密訊息 ----------------
    public async Task SendAsync(ReadOnlyMemory<byte> plain, bool highPriority = false, CancellationToken ct = default)
    {
        var frame = new byte[4 + plain.Length + TagSize];
        BinaryPrimitives.WriteInt32LittleEndian(frame, plain.Length + TagSize);

        if (highPriority)
        {
            Interlocked.Increment(ref _priorityWaiters);
            try
            {
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _priorityWaiters);
            }

            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                Span<byte> nonce = stackalloc byte[12];
                BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _sendCounter++);
                _sendAes.Encrypt(nonce, plain.Span, frame.AsSpan(4, plain.Length), frame.AsSpan(4 + plain.Length, TagSize));
                long writeStart = System.Diagnostics.Stopwatch.GetTimestamp();
                await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
                long writeUs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(writeStart).TotalMicroseconds;
                Interlocked.Exchange(ref _lastWriteDurationUs, writeUs);
                RecordSentBytes(frame.Length);
            }
            finally { _writeLock.Release(); }
        }
        else
        {
            // 低優先級進入低優先級排隊門閥，確保高優先級封包永遠能插隊在所有等待中的低優先級封包之前
            long waitId = _queueWaitTracker.BeginWait(out long queueStart);
            bool waitEnded = false;
            Interlocked.Increment(ref _pendingLowPriority);
            try
            {
                await _lowPriorityLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // 若有高優先級控制封包 (滑鼠/鍵盤/ACK/Ping) 正在等待，低優先級 (影像資料/剪貼簿) 稍作讓步
                    while (Volatile.Read(ref _priorityWaiters) > 0)
                    {
                        await Task.Yield();
                    }

                    await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (!_trackWriteWait)
                        {
                            // ReliableUdpStream 本身透過 _semWaitTracker 獨立量測滑動視窗阻塞，排除 Micro-Pacing 睡眠干擾
                            _queueWaitTracker.EndWait(waitId, queueStart);
                            waitEnded = true;
                        }

                        ObjectDisposedException.ThrowIf(_disposed, this);
                        Span<byte> nonce = stackalloc byte[12];
                        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _sendCounter++);
                        _sendAes.Encrypt(nonce, plain.Span, frame.AsSpan(4, plain.Length), frame.AsSpan(4 + plain.Length, TagSize));
                        long writeStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
                        long writeUs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(writeStart).TotalMicroseconds;
                        Interlocked.Exchange(ref _lastWriteDurationUs, writeUs);
                        RecordSentBytes(frame.Length);

                        if (_trackWriteWait)
                        {
                            // TCP/Relay 等非滑動視窗串流：將底層 Socket/寫入阻塞納入完整佇列等待視窗，涵蓋單執行緒阻塞與 OngoingWait
                            _queueWaitTracker.EndWait(waitId, queueStart);
                            waitEnded = true;
                        }
                    }
                    finally { _writeLock.Release(); }
                }
                finally { _lowPriorityLock.Release(); }
            }
            finally
            {
                if (!waitEnded) _queueWaitTracker.CancelWait(waitId);
                Interlocked.Decrement(ref _pendingLowPriority);
            }
        }
    }

    public Task SendAsync(ReadOnlyMemory<byte> plain, CancellationToken ct) =>
        SendAsync(plain, highPriority: false, ct);

    /// <summary>傳送訊息，忽略連線已中斷的例外（用於非關鍵通知）。</summary>
    public async Task<bool> TrySendAsync(ReadOnlyMemory<byte> plain, bool highPriority = false, CancellationToken ct = default)
    {
        try { await SendAsync(plain, highPriority, ct).ConfigureAwait(false); return true; }
        catch { return false; }
    }

    public Task<bool> TrySendAsync(ReadOnlyMemory<byte> plain, CancellationToken ct) =>
        TrySendAsync(plain, highPriority: false, ct);

    /// <summary>同步發送訊息並立即 Flush（用於連線關閉前的關鍵 Disconnect 封包，防止非同步未完成即被釋放）。</summary>
    public bool SendSync(ReadOnlyMemory<byte> plain, int timeoutMs = 500)
    {
        if (_disposed) return false;
        var frame = new byte[4 + plain.Length + TagSize];
        BinaryPrimitives.WriteInt32LittleEndian(frame, plain.Length + TagSize);
        if (!_writeLock.Wait(timeoutMs)) return false;
        try
        {
            if (_disposed) return false;
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _sendCounter++);
            _sendAes.Encrypt(nonce, plain.Span, frame.AsSpan(4, plain.Length), frame.AsSpan(4 + plain.Length, TagSize));
            _stream.Write(frame);
            _stream.Flush();
            RecordSentBytes(frame.Length);
            return true;
        }
        catch { return false; }
        finally { _writeLock.Release(); }
    }

    /// <summary>即時檢查通道是否有效。</summary>
    public bool IsConnected
    {
        get
        {
            if (_disposed) return false;
            try
            {
                if (_stream is NetworkStream ns)
                {
                    var s = ns.Socket;
                    if (s == null || !s.Connected) return false;
                }
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>接收一則訊息；只能由單一執行緒呼叫。</summary>
    public async Task<byte[]> ReceiveAsync(CancellationToken ct = default)
    {
        await _stream.ReadExactlyAsync(_lenBuf, ct).ConfigureAwait(false);
        int len = BinaryPrimitives.ReadInt32LittleEndian(_lenBuf);
        if (len < TagSize || len > Protocol.MaxMessageSize) throw new InvalidDataException($"訊息長度錯誤: {len}");
        var cipher = new byte[len];
        await _stream.ReadExactlyAsync(cipher, ct).ConfigureAwait(false);
        var plain = new byte[len - TagSize];
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _recvCounter++);
        _recvAes.Decrypt(nonce, cipher.AsSpan(0, plain.Length), cipher.AsSpan(plain.Length), plain);
        Interlocked.Add(ref BytesReceived, len + 4);
        return plain;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream.Dispose(); } catch { }
        _sendAes.Dispose();
        _recvAes.Dispose();
        _writeLock.Dispose();
        _lowPriorityLock.Dispose();
    }

    // ---------------- 握手 ----------------

    private static (byte[] c2s, byte[] s2c) DeriveKeys(byte[] secret, byte[] transcript)
    {
        var c2s = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, transcript, "RDSK c2s"u8.ToArray());
        var s2c = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, transcript, "RDSK s2c"u8.ToArray());
        return (c2s, s2c);
    }

    private static byte[] Agree(ECDiffieHellman me, byte[] peerSpki)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerSpki, out _);
        return me.DeriveRawSecretAgreement(peer.PublicKey);
    }

    /// <summary>檢視端：送出 ClientHello，取得 ServerHello 並建立加密通道（尚未驗證身分）。</summary>
    public static async Task<(SecureChannel channel, ServerHello hello)> ClientHandshakeAsync(
        Stream stream,
        string viewerId,
        string viewerName,
        CancellationToken ct,
        ProtocolCapabilities capabilities = ProtocolCapabilities.FrameSeqAck)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var helloWriter = PacketWriter.Raw(256).Raw(Protocol.Magic).U16(Protocol.Version)
            .Str(viewerId).Str(viewerName).Bytes(ecdh.ExportSubjectPublicKeyInfo());
        if (capabilities != ProtocolCapabilities.None)
            helloWriter.U32((uint)capabilities);
        var clientHello = helloWriter.ToArray();
        await WritePlainAsync(stream, clientHello, ct).ConfigureAwait(false);

        var serverHelloBytes = await ReadPlainAsync(stream, 8192, ct).ConfigureAwait(false);
        var hello = ServerHello.Parse(serverHelloBytes, out var serverPub);
        var transcript = SHA256.HashData(Crypto.Concat(clientHello, serverHelloBytes));
        var (c2s, s2c) = DeriveKeys(Agree(ecdh, serverPub), transcript);
        return (new SecureChannel(stream, c2s, s2c, transcript), hello);
    }

    /// <summary>主機：讀取 ClientHello，回覆 ServerHello 並建立加密通道。</summary>
    public static async Task<(SecureChannel channel, ClientHello hello)> ServerHandshakeAsync(
        Stream stream, Func<ClientHello, ServerHello> makeHello, CancellationToken ct)
    {
        var clientHelloBytes = await ReadPlainAsync(stream, 8192, ct).ConfigureAwait(false);
        var r = new PacketReader(clientHelloBytes);
        if (!r.Raw(4).SequenceEqual(Protocol.Magic)) throw new InvalidDataException("非 RemoteDesk 連線");
        var ver = r.U16();
        var viewerId = r.Str();
        var viewerName = r.Str();
        var clientPub = r.Bytes().ToArray();
        var clientCaps = r.Remaining >= 4 ? (ProtocolCapabilities)r.U32() : ProtocolCapabilities.None;
        var ch = new ClientHello(ver, viewerId, viewerName, clientCaps);

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var sh = makeHello(ch);
        var serverHelloBytes = sh.Serialize(ecdh.ExportSubjectPublicKeyInfo());
        await WritePlainAsync(stream, serverHelloBytes, ct).ConfigureAwait(false);
        var transcript = SHA256.HashData(Crypto.Concat(clientHelloBytes, serverHelloBytes));
        var (c2s, s2c) = DeriveKeys(Agree(ecdh, clientPub), transcript);
        return (new SecureChannel(stream, s2c, c2s, transcript), ch);
    }

    public static void TuneSocket(Socket s)
    {
        s.NoDelay = true;
        s.SendBufferSize = 256 * 1024; // 調整為 256 KB (杜絕 TCP 4MB 緩衝膨脹延遲 3 秒)
        s.ReceiveBufferSize = 1024 * 1024;
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        try
        {
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 5);
        }
        catch { }
    }
}

internal sealed record ClientHello(
    ushort Version,
    string ViewerId,
    string ViewerName,
    ProtocolCapabilities Capabilities = ProtocolCapabilities.None);

internal enum AuthMethod : byte { Password = 1, RequestAccess = 2, DeviceIdentity = 3 }

internal sealed record ServerHello(
    ushort Version, string HostId, string HostName, byte[] Salt, int Iterations,
    bool HasPassword, bool AllowInteractive, bool Busy,
    ProtocolCapabilities Capabilities = ProtocolCapabilities.None)
{
    public byte[] Serialize(byte[] pub)
    {
        var w = PacketWriter.Raw(256).Raw(Protocol.Magic).U16(Version).Str(HostId).Str(HostName).Bytes(pub)
            .Bytes(Salt).I32(Iterations).Bool(HasPassword).Bool(AllowInteractive).Bool(Busy);
        if (Capabilities != ProtocolCapabilities.None)
            w.U32((uint)Capabilities);
        return w.ToArray();
    }

    public static ServerHello Parse(byte[] data, out byte[] pub)
    {
        var r = new PacketReader(data);
        if (!r.Raw(4).SequenceEqual(Protocol.Magic)) throw new InvalidDataException("對方不是 RemoteDesk 主機");
        var ver = r.U16();
        var id = r.Str();
        var name = r.Str();
        pub = r.Bytes().ToArray();
        var salt = r.Bytes().ToArray();
        int iterations = r.I32();
        bool hasPassword = r.Bool();
        bool allowInteractive = r.Bool();
        bool busy = r.Bool();
        var capabilities = r.Remaining >= 4 ? (ProtocolCapabilities)r.U32() : ProtocolCapabilities.None;
        return new ServerHello(ver, id, name, salt, iterations, hasPassword, allowInteractive, busy, capabilities);
    }
}
