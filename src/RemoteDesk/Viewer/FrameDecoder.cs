using System.Buffers;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using RemoteDesk.Common;

namespace RemoteDesk.Viewer;

/// <summary>
/// P1-03: DecodeFrame 的明確執行狀態，用於區分正常套用、靜態空影格、過期設定、版本失步與封包/解碼錯誤，
/// 讓 ViewerSession 能決定精準的 FrameAck / Keyframe (NACK) / 靜默丟棄策略。
/// </summary>
internal enum FrameDecodeStatus
{
    Applied = 0,
    EmptyNoChange = 1,
    StaleConfigVersion = 2,
    Desynced = 3,
    Failed = 4,
    StalePath = 5
}

/// <summary>
/// 影像區塊解碼器：接收遠端傳來的 JPEG / Deflate 壓縮區塊，
/// 並直接渲染到本機點陣圖 (32bpp BGRA) 上，支援區域性髒污更新以達成極致效能。
/// </summary>
internal sealed unsafe class FrameDecoder : IDisposable
{
    internal const int MaxFrameDimension = 16384;
    internal const long MaxFrameBufferBytes = 512L * 1024 * 1024;
    internal const int MaxMoveRectCount = 4096;
    internal const int MaxTileCount = 16384;

    private Bitmap? _bitmap;
    private byte[]? _buffer;
    private byte* _bufPtr;
    private readonly object _renderLock = new();
    private int _width;
    private int _height;
    private bool _disposed;
    private uint _canvasVersion;
    private uint _streamConfigVersion;
    private bool _hasValidBaseline;
    private int _decodeFailureCount;
    private float _lastDecodeDurationMs;
    private bool _isPathReconnecting;
    private uint _highestAcceptedFrameSeq;
    private uint _minAcceptedFrameSeqOnReconnect;
    private bool _enforceMinFrameSeq;
    private long _rejectedStalePathFrames;

    public event Action? DesyncDetected;

    public int Width => _width;
    public int Height => _height;
    public uint CanvasVersion => _canvasVersion;
    public uint StreamConfigVersion => _streamConfigVersion;
    public bool HasValidBaseline => _hasValidBaseline;
    public int DecodeFailureCount => Volatile.Read(ref _decodeFailureCount);
    public float LastDecodeDurationMs => Volatile.Read(ref _lastDecodeDurationMs);
    public bool IsPathReconnecting { get { lock (_renderLock) return _isPathReconnecting; } }
    public long RejectedStalePathFrames => Interlocked.Read(ref _rejectedStalePathFrames);

    /// <summary>
    /// P2P 路徑進入重新檢查/切換期間調用：在切換完成前拒絕接受任何在途或舊路徑遲到影格，防止誤套用或觸發虛假 Desync。
    /// </summary>
    public void BeginPathReconnection()
    {
        lock (_renderLock)
        {
            _isPathReconnecting = true;
            if (_highestAcceptedFrameSeq > 0)
            {
                _minAcceptedFrameSeqOnReconnect = Math.Max(_minAcceptedFrameSeqOnReconnect, _highestAcceptedFrameSeq + 1);
            }
        }
    }

    /// <summary>
    /// P2P 路徑完成重連切換後調用：解除阻擋並以先前最高已接受 FrameSeq 建立最低水位，僅接受新路徑之後的新影格與新基準。
    /// </summary>
    public void CompletePathReconnection(bool requireNewFrameSeqAbovePrevious = true)
    {
        lock (_renderLock)
        {
            _isPathReconnecting = false;
            if (requireNewFrameSeqAbovePrevious && _highestAcceptedFrameSeq > 0)
            {
                _minAcceptedFrameSeqOnReconnect = Math.Max(_minAcceptedFrameSeqOnReconnect, _highestAcceptedFrameSeq + 1);
                _enforceMinFrameSeq = true;
            }
            _hasValidBaseline = false;
        }
    }

    internal static bool TryValidateFrameDimensions(int width, int height, out int bufferBytes)
    {
        bufferBytes = 0;
        if (width <= 0 || height <= 0 || width > MaxFrameDimension || height > MaxFrameDimension)
            return false;

        long rowBytes = (long)width * 4L;
        long totalBytes = rowBytes * height;
        if (rowBytes <= 0 || rowBytes > int.MaxValue || totalBytes <= 0 || totalBytes > MaxFrameBufferBytes)
            return false;

        bufferBytes = (int)totalBytes;
        return true;
    }

    internal static bool TryValidateMoveRectangle(in MoveRectangle m, int frameWidth, int frameHeight, out int rowBytes, out int byteCount)
    {
        rowBytes = 0;
        byteCount = 0;
        if (frameWidth <= 0 || frameHeight <= 0) return false;

        int srcX = m.SourcePoint.X;
        int srcY = m.SourcePoint.Y;
        int dstX = m.DestinationRect.X;
        int dstY = m.DestinationRect.Y;
        int w = m.DestinationRect.Width;
        int h = m.DestinationRect.Height;

        if (w <= 0 || h <= 0) return false;
        if (srcX < 0 || srcY < 0 || dstX < 0 || dstY < 0) return false;
        if ((long)srcX + w > frameWidth || (long)srcY + h > frameHeight) return false;
        if ((long)dstX + w > frameWidth || (long)dstY + h > frameHeight) return false;

        long rowBytesLong = (long)w * 4L;
        long totalBytesLong = rowBytesLong * h;
        if (rowBytesLong <= 0 || rowBytesLong > int.MaxValue || totalBytesLong <= 0 || totalBytesLong > MaxFrameBufferBytes)
            return false;

        rowBytes = (int)rowBytesLong;
        byteCount = (int)totalBytesLong;
        return true;
    }

    internal static bool TryValidateTileBounds(int x, int y, int w, int h, int frameWidth, int frameHeight, out int byteCount)
    {
        byteCount = 0;
        if (frameWidth <= 0 || frameHeight <= 0) return false;
        if (w <= 0 || h <= 0 || x < 0 || y < 0) return false;
        if ((long)x + w > frameWidth || (long)y + h > frameHeight) return false;

        long rowBytesLong = (long)w * 4L;
        long totalBytesLong = rowBytesLong * h;
        if (rowBytesLong <= 0 || rowBytesLong > int.MaxValue || totalBytesLong <= 0 || totalBytesLong > MaxFrameBufferBytes)
            return false;

        byteCount = (int)totalBytesLong;
        return true;
    }

    /// <summary>
    /// 預先宣告即將切換的串流解析度設定版本，重置畫布基準版本同時保留上一解析度縮放影像避免黑畫面閃爍。
    /// </summary>
    public void ConfigureStream(uint configVersion, int width, int height)
    {
        if (!TryValidateFrameDimensions(width, height, out _))
        {
            Log.Warn($"[FrameDecoder] ConfigureStream 收到非法解析度 {width}x{height} (Cfg v{configVersion})，已拒絕套用。");
            return;
        }

        lock (_renderLock)
        {
            if (configVersion < _streamConfigVersion) return;
            _streamConfigVersion = configVersion;
            Resize(width, height);
        }
    }

    public void Resize(int width, int height)
    {
        if (!TryValidateFrameDimensions(width, height, out int bufferBytes)) return;
        lock (_renderLock)
        {
            if (_disposed) return;
            if (_width == width && _height == height && _bitmap != null) return;

            var newBuffer = GC.AllocateUninitializedArray<byte>(bufferBytes, pinned: true);
            byte* newPtr = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(
                ref MemoryMarshal.GetArrayDataReference(newBuffer));
            var newBitmap = new Bitmap(width, height, checked(width * 4), PixelFormat.Format32bppRgb, (IntPtr)newPtr);

            if (_bitmap != null && _width > 0 && _height > 0)
            {
                // 解析度切換瞬間將舊畫布影像平滑延伸至新 BackBuffer，避免等待 Keyframe 期間閃現黑屏
                using var g = Graphics.FromImage(newBitmap);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(_bitmap, new Rectangle(0, 0, width, height), 0, 0, _width, _height, GraphicsUnit.Pixel);
            }
            else
            {
                Array.Clear(newBuffer);
            }

            Cleanup();
            _width = width;
            _height = height;
            _canvasVersion = 0;
            _hasValidBaseline = false;
            _buffer = newBuffer;
            _bufPtr = newPtr;
            _bitmap = newBitmap;
        }
    }

    /// <summary>
    /// 解碼收到的 VideoFrame 封包，並回傳有變動的區域矩形與明確的解碼狀態。
    /// 完整支援 StreamConfigVersion 隔離、Move Rectangles、Dirty Rectangles、FrameVersion、FrameType 與嚴格邊界防護。
    /// </summary>
    public Rectangle DecodeFrame(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out uint frameVersion,
        out uint baseVersion,
        out FrameType frameType,
        out uint packetConfigVersion,
        out FrameDecodeStatus status)
    {
        var decodeSw = System.Diagnostics.Stopwatch.StartNew();
        sequence = 0;
        frameVersion = 0;
        baseVersion = 0;
        frameType = default;
        packetConfigVersion = 0;
        status = FrameDecodeStatus.Failed;

        DecodedTile[]? decoded = null;
        int tileCount = 0;
        try
        {
            var r = new PacketReader(packet);
            sequence = r.U32();
            int frameW = r.I32();
            int frameH = r.I32();
            frameVersion = r.U32();
            baseVersion = r.U32();
            byte rawType = r.U8();
            bool hasConfigVer = (rawType & 0x80) != 0;
            frameType = (FrameType)(rawType & 0x7F);
            packetConfigVersion = hasConfigVer ? r.U32() : 0;

            if (!Enum.IsDefined(frameType))
            {
                Interlocked.Increment(ref _decodeFailureCount);
                Log.Warn($"[FrameDecoder] 收到未知 FrameType={rawType & 0x7F} (序號={sequence})，拒絕套用並請求 Keyframe。");
                status = FrameDecodeStatus.Failed;
                DesyncDetected?.Invoke();
                return Rectangle.Empty;
            }

            if (!TryValidateFrameDimensions(frameW, frameH, out _))
            {
                Interlocked.Increment(ref _decodeFailureCount);
                Log.Warn($"[FrameDecoder] 影格尺寸非法或溢位 {frameW}x{frameH} (序號={sequence})，拒絕配置並請求 Keyframe。");
                status = FrameDecodeStatus.Failed;
                DesyncDetected?.Invoke();
                return Rectangle.Empty;
            }

            uint pendingConfigVersion;
            lock (_renderLock)
            {
                if (_disposed)
                {
                    status = FrameDecodeStatus.Failed;
                    return Rectangle.Empty;
                }

                if (_isPathReconnecting || (_enforceMinFrameSeq && sequence < _minAcceptedFrameSeqOnReconnect))
                {
                    Interlocked.Increment(ref _rejectedStalePathFrames);
                    status = FrameDecodeStatus.StalePath;
                    return Rectangle.Empty;
                }

                if (hasConfigVer && packetConfigVersion < _streamConfigVersion)
                {
                    // 丟棄屬於舊解析度設定版本的過期影格（防止汙染新解析度 BackBuffer），不視為解碼失敗亦不重複觸發 Keyframe
                    status = FrameDecodeStatus.StaleConfigVersion;
                    return Rectangle.Empty;
                }

                if (frameType == FrameType.FullFrame)
                {
                    if (hasConfigVer && packetConfigVersion > _streamConfigVersion)
                        _streamConfigVersion = packetConfigVersion;
                    if (_width != frameW || _height != frameH || _bitmap == null)
                        Resize(frameW, frameH);
                    if (_bufPtr == null || _width != frameW || _height != frameH)
                    {
                        Interlocked.Increment(ref _decodeFailureCount);
                        status = FrameDecodeStatus.Failed;
                        DesyncDetected?.Invoke();
                        return Rectangle.Empty;
                    }
                    pendingConfigVersion = _streamConfigVersion;
                }
                else
                {
                    if (hasConfigVer && packetConfigVersion != _streamConfigVersion)
                    {
                        Log.Warn($"[FrameDecoder] 增量影格組態版本不符 (畫布={_streamConfigVersion}, 封包={packetConfigVersion}, 序號={sequence})，放棄並請求重同步。");
                        status = FrameDecodeStatus.Desynced;
                        DesyncDetected?.Invoke();
                        return Rectangle.Empty;
                    }

                    if (!_hasValidBaseline || _canvasVersion != baseVersion || _width != frameW || _height != frameH)
                    {
                        Log.Warn($"[FrameDecoder] 畫面版本或解析度不符！本機版本={_canvasVersion}({_width}x{_height}), 影格基準={baseVersion}({frameW}x{frameH}), 影格序號={sequence}。放棄增量更新並請求 FullFrame 重新同步！");
                        status = FrameDecodeStatus.Desynced;
                        DesyncDetected?.Invoke();
                        return Rectangle.Empty;
                    }
                    pendingConfigVersion = _streamConfigVersion;
                }
            }

            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

            // 1. 在完全無鎖狀態下讀取所有 MoveRects 與 Tiles 元資料並嚴格驗證邊界與整數溢位
            int moveCount = r.U16();
            if (moveCount > MaxMoveRectCount)
            {
                Interlocked.Increment(ref _decodeFailureCount);
                Log.Warn($"[FrameDecoder] MoveRect 數量超過上限 ({moveCount} > {MaxMoveRectCount}, 序號={sequence})，拒絕套用並請求 Keyframe。");
                status = FrameDecodeStatus.Failed;
                DesyncDetected?.Invoke();
                return Rectangle.Empty;
            }

            List<MoveRectangle>? moveRects = null;
            if (moveCount > 0)
            {
                moveRects = new List<MoveRectangle>(moveCount);
                for (int i = 0; i < moveCount; i++)
                {
                    int sx = r.I32(), sy = r.I32();
                    int dx = r.I32(), dy = r.I32(), dw = r.I32(), dh = r.I32();
                    var m = new MoveRectangle(new Point(sx, sy), new Rectangle(dx, dy, dw, dh));
                    if (!TryValidateMoveRectangle(m, frameW, frameH, out _, out _))
                    {
                        Interlocked.Increment(ref _decodeFailureCount);
                        Log.Warn($"[FrameDecoder] 非法 MoveRectangle ({sx},{sy} -> {dx},{dy},{dw}x{dh} 於 {frameW}x{frameH}, 序號={sequence})，拒絕套用並請求 Keyframe。");
                        status = FrameDecodeStatus.Failed;
                        DesyncDetected?.Invoke();
                        return Rectangle.Empty;
                    }

                    moveRects.Add(m);

                    if (dx < minX) minX = dx;
                    if (dy < minY) minY = dy;
                    if (dx + dw > maxX) maxX = dx + dw;
                    if (dy + dh > maxY) maxY = dy + dh;
                }
            }

            tileCount = r.U16();
            if (tileCount > MaxTileCount)
            {
                Interlocked.Increment(ref _decodeFailureCount);
                Log.Warn($"[FrameDecoder] Tile 數量超過上限 ({tileCount} > {MaxTileCount}, 序號={sequence})，拒絕套用並請求 Keyframe。");
                status = FrameDecodeStatus.Failed;
                DesyncDetected?.Invoke();
                return Rectangle.Empty;
            }

            decoded = tileCount > 0 ? new DecodedTile[tileCount] : Array.Empty<DecodedTile>();

            // 2. 在完全無鎖 (Zero Lock) 狀態下執行軟體 JPEG / Deflate 解碼
            for (int i = 0; i < tileCount; i++)
            {
                int x = r.U16();
                int y = r.U16();
                int w = r.U16();
                int h = r.U16();
                var enc = (TileEncoding)r.U8();
                var data = r.Bytes();

                if (!TryValidateTileBounds(x, y, w, h, frameW, frameH, out int byteCount))
                {
                    Interlocked.Increment(ref _decodeFailureCount);
                    Log.Warn($"[FrameDecoder] 非法 Tile 邊界或尺寸溢位 ({x},{y},{w}x{h} 於 {frameW}x{frameH}, 序號={sequence})，拒絕套用並請求 Keyframe。");
                    status = FrameDecodeStatus.Failed;
                    DesyncDetected?.Invoke();
                    return Rectangle.Empty;
                }

                byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
                decoded[i] = new DecodedTile(x, y, w, h, rented);

                if (enc == TileEncoding.Jpeg)
                {
                    DecodeJpegTileToBuffer(w, h, data, rented);
                }
                else if (enc == TileEncoding.JpegDownscaled2x)
                {
                    DecodeJpegDownscaled2xTileToBuffer(w, h, data, rented);
                }
                else if (enc == TileEncoding.Deflate)
                {
                    DecodeDeflateTileToBuffer(w, h, data, rented);
                }
                else
                {
                    throw new InvalidDataException($"未知區塊編碼類型 {(byte)enc}");
                }

                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x + w > maxX) maxX = x + w;
                if (y + h > maxY) maxY = y + h;
            }

            // 3. 所有 JPEG 軟體解碼完成後，僅在極速 MemoryCopy 期間持有 _renderLock，並於成功寫入後才提交版本狀態
            lock (_renderLock)
            {
                if (_disposed || _bufPtr == null)
                {
                    status = FrameDecodeStatus.Failed;
                    return Rectangle.Empty;
                }

                if (_isPathReconnecting || (_enforceMinFrameSeq && sequence < _minAcceptedFrameSeqOnReconnect))
                {
                    Interlocked.Increment(ref _rejectedStalePathFrames);
                    status = FrameDecodeStatus.StalePath;
                    return Rectangle.Empty;
                }

                if (_streamConfigVersion != pendingConfigVersion)
                {
                    // 解碼期間已切換至較新的解析度組態，靜默放棄避免寫入新畫布
                    status = FrameDecodeStatus.StaleConfigVersion;
                    return Rectangle.Empty;
                }

                if (_width != frameW || _height != frameH)
                {
                    status = FrameDecodeStatus.Desynced;
                    DesyncDetected?.Invoke();
                    return Rectangle.Empty;
                }

                if (frameType != FrameType.FullFrame && (!_hasValidBaseline || _canvasVersion != baseVersion))
                {
                    status = FrameDecodeStatus.Desynced;
                    DesyncDetected?.Invoke();
                    return Rectangle.Empty;
                }

                if (moveRects != null && moveRects.Count > 0)
                {
                    if (!ApplyMoveRectangles(moveRects))
                    {
                        Interlocked.Increment(ref _decodeFailureCount);
                        status = FrameDecodeStatus.Failed;
                        DesyncDetected?.Invoke();
                        return Rectangle.Empty;
                    }
                }

                int dstStride = checked(_width * 4);
                for (int i = 0; i < tileCount; i++)
                {
                    var dt = decoded[i];
                    if (!TryValidateTileBounds(dt.X, dt.Y, dt.W, dt.H, _width, _height, out _)) continue;
                    int rowBytes = checked(dt.W * 4);
                    fixed (byte* pSrc = dt.Buffer)
                    {
                        for (int row = 0; row < dt.H; row++)
                        {
                            byte* s = pSrc + (long)row * rowBytes;
                            byte* d = _bufPtr + (long)(dt.Y + row) * dstStride + (long)dt.X * 4;
                            Buffer.MemoryCopy(s, d, rowBytes, rowBytes);
                        }
                    }
                }

                _canvasVersion = frameVersion;
                if (sequence > _highestAcceptedFrameSeq)
                {
                    _highestAcceptedFrameSeq = sequence;
                }
                if (frameType == FrameType.FullFrame)
                {
                    _hasValidBaseline = true;
                }
            }

            if (frameType == FrameType.FullFrame)
            {
                status = FrameDecodeStatus.Applied;
                return new Rectangle(0, 0, frameW, frameH);
            }

            if (minX < maxX && minY < maxY)
            {
                status = FrameDecodeStatus.Applied;
                return new Rectangle(minX, minY, maxX - minX, maxY - minY);
            }

            status = FrameDecodeStatus.EmptyNoChange;
            return Rectangle.Empty;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _decodeFailureCount);
            Log.Warn($"[FrameDecoder] 解碼失敗 (序號={sequence}): {ex.Message}");
            status = FrameDecodeStatus.Failed;
            DesyncDetected?.Invoke();
            return Rectangle.Empty;
        }
        finally
        {
            if (decoded != null)
            {
                for (int i = 0; i < tileCount; i++)
                {
                    if (decoded[i].Buffer != null)
                    {
                        ArrayPool<byte>.Shared.Return(decoded[i].Buffer);
                    }
                }
            }
            Volatile.Write(ref _lastDecodeDurationMs, (float)decodeSw.Elapsed.TotalMilliseconds);
        }
    }

    public Rectangle DecodeFrame(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out uint frameVersion,
        out uint baseVersion,
        out FrameType frameType,
        out uint packetConfigVersion) =>
        DecodeFrame(packet, out sequence, out frameVersion, out baseVersion, out frameType, out packetConfigVersion, out _);

    public Rectangle DecodeFrame(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out uint frameVersion,
        out uint baseVersion,
        out FrameType frameType) =>
        DecodeFrame(packet, out sequence, out frameVersion, out baseVersion, out frameType, out _);

    private readonly record struct DecodedTile(int X, int Y, int W, int H, byte[] Buffer);

    public Rectangle DecodeFrame(
        ReadOnlySpan<byte> packet,
        out uint sequence,
        out uint frameVersion,
        out FrameType frameType) =>
        DecodeFrame(packet, out sequence, out frameVersion, out _, out frameType);

    public Rectangle DecodeFrame(ReadOnlySpan<byte> packet, out uint sequence) =>
        DecodeFrame(packet, out sequence, out _, out _, out _);

    /// <summary>
    /// 安全執行 Move Rectangles 平移操作，嚴格遵循 DXGI 規範，並對全部分支（單一、順序、衝突暫存）驗證邊界與整數溢位。
    /// 若任一 MoveRectangle 非法（負座標、零/負寬高、越界或乘法溢位）則拒絕對非受控記憶體執行配置或複製並回傳 false。
    /// </summary>
    internal bool ApplyMoveRectangles(IReadOnlyList<MoveRectangle> moves)
    {
        if (moves == null || moves.Count == 0) return true;

        lock (_renderLock)
        {
            if (_disposed || _bufPtr == null || _width <= 0 || _height <= 0) return false;
            int stride = checked(_width * 4);

            for (int i = 0; i < moves.Count; i++)
            {
                if (!TryValidateMoveRectangle(moves[i], _width, _height, out _, out _))
                    return false;
            }

            if (moves.Count == 1)
            {
                return ApplySingleMove(moves[0], stride);
            }

            // 檢查多個 MoveRectangle 之間是否存在目的區域覆蓋其他來源區域的衝突
            bool hasConflict = false;
            for (int i = 0; i < moves.Count && !hasConflict; i++)
            {
                var dstI = moves[i].DestinationRect;
                for (int j = 0; j < moves.Count; j++)
                {
                    if (i == j) continue;
                    var srcJ = new Rectangle(
                        moves[j].SourcePoint.X,
                        moves[j].SourcePoint.Y,
                        moves[j].DestinationRect.Width,
                        moves[j].DestinationRect.Height);
                    if (dstI.IntersectsWith(srcJ))
                    {
                        hasConflict = true;
                        break;
                    }
                }
            }

            if (!hasConflict)
            {
                for (int i = 0; i < moves.Count; i++)
                {
                    if (!ApplySingleMove(moves[i], stride))
                        return false;
                }
                return true;
            }

            // 有衝突：先針對已驗證邊界與溢位之來源區域快照至池化暫存區，再安全寫回目的區域
            var scratch = new List<(byte[] buf, int rowBytes, Rectangle dst)>(moves.Count);
            try
            {
                for (int i = 0; i < moves.Count; i++)
                {
                    var m = moves[i];
                    if (!TryValidateMoveRectangle(m, _width, _height, out int rowBytes, out int byteCount))
                        return false;

                    byte[] tmp = ArrayPool<byte>.Shared.Rent(byteCount);
                    scratch.Add((tmp, rowBytes, m.DestinationRect));

                    int h = m.DestinationRect.Height;
                    fixed (byte* pTmp = tmp)
                    {
                        for (int row = 0; row < h; row++)
                        {
                            byte* s = _bufPtr + (long)(m.SourcePoint.Y + row) * stride + (long)m.SourcePoint.X * 4;
                            byte* d = pTmp + (long)row * rowBytes;
                            Buffer.MemoryCopy(s, d, rowBytes, rowBytes);
                        }
                    }
                }

                for (int i = 0; i < scratch.Count; i++)
                {
                    var (data, rowBytes, dst) = scratch[i];
                    fixed (byte* pData = data)
                    {
                        for (int row = 0; row < dst.Height; row++)
                        {
                            byte* s = pData + (long)row * rowBytes;
                            byte* d = _bufPtr + (long)(dst.Y + row) * stride + (long)dst.X * 4;
                            Buffer.MemoryCopy(s, d, rowBytes, rowBytes);
                        }
                    }
                }

                return true;
            }
            finally
            {
                for (int i = 0; i < scratch.Count; i++)
                {
                    ArrayPool<byte>.Shared.Return(scratch[i].buf);
                }
            }
        }
    }

    private bool ApplySingleMove(MoveRectangle m, int stride)
    {
        if (!TryValidateMoveRectangle(m, _width, _height, out int rowBytes, out _))
            return false;

        int srcX = m.SourcePoint.X;
        int srcY = m.SourcePoint.Y;
        int dstX = m.DestinationRect.X;
        int dstY = m.DestinationRect.Y;
        int h = m.DestinationRect.Height;

        if (dstY > srcY)
        {
            // 向下平移：倒序拷貝，防止上一列覆寫下一列來源
            for (int row = h - 1; row >= 0; row--)
            {
                byte* s = _bufPtr + (long)(srcY + row) * stride + (long)srcX * 4;
                byte* d = _bufPtr + (long)(dstY + row) * stride + (long)dstX * 4;
                Buffer.MemoryCopy(s, d, rowBytes, rowBytes);
            }
        }
        else
        {
            // 向上平移或同一水平列：正序拷貝
            for (int row = 0; row < h; row++)
            {
                byte* s = _bufPtr + (long)(srcY + row) * stride + (long)srcX * 4;
                byte* d = _bufPtr + (long)(dstY + row) * stride + (long)dstX * 4;
                Buffer.MemoryCopy(s, d, rowBytes, rowBytes);
            }
        }

        return true;
    }

    private static void DecodeJpegTileToBuffer(int w, int h, ReadOnlySpan<byte> data, byte[] dest)
    {
        fixed (byte* pData = data)
        {
            using var ms = new UnmanagedMemoryStream(pData, data.Length);
            using var tileBmp = (Bitmap)Image.FromStream(ms);
            if (tileBmp.Width != w || tileBmp.Height != h)
                throw new InvalidDataException($"JPEG 區塊尺寸不符: 預期 {w}x{h}，實際 {tileBmp.Width}x{tileBmp.Height}");

            var bd = tileBmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                byte* src = (byte*)bd.Scan0;
                int srcStride = bd.Stride;
                int rowBytes = checked(w * 4);

                fixed (byte* dst = dest)
                {
                    if (srcStride == rowBytes)
                    {
                        Buffer.MemoryCopy(src, dst, (long)rowBytes * h, (long)rowBytes * h);
                    }
                    else
                    {
                        for (int row = 0; row < h; row++)
                        {
                            byte* s = src + (long)row * srcStride;
                            byte* d = dst + (long)row * rowBytes;
                            Buffer.MemoryCopy(s, d, rowBytes, rowBytes);
                        }
                    }
                }
            }
            finally
            {
                tileBmp.UnlockBits(bd);
            }
        }
    }

    private static void DecodeJpegDownscaled2xTileToBuffer(int w, int h, ReadOnlySpan<byte> data, byte[] dest)
    {
        fixed (byte* pData = data)
        {
            using var ms = new UnmanagedMemoryStream(pData, data.Length);
            using var tileBmp = (Bitmap)Image.FromStream(ms);

            int dw = tileBmp.Width;
            int dh = tileBmp.Height;
            if (dw <= 0 || dh <= 0)
                throw new InvalidDataException($"降採樣 JPEG 區塊尺寸無效: {dw}x{dh}");

            var bd = tileBmp.LockBits(new Rectangle(0, 0, dw, dh), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                byte* src = (byte*)bd.Scan0;
                int srcStride = bd.Stride;
                int rowBytes = checked(w * 4);

                fixed (byte* dst = dest)
                {
                    for (int row = 0; row < h; row++)
                    {
                        int srcRow = Math.Min(dh - 1, row >> 1);
                        byte* s = src + (long)srcRow * srcStride;
                        byte* d = dst + (long)row * rowBytes;

                        for (int col = 0; col < w; col++)
                        {
                            int srcCol = Math.Min(dw - 1, col >> 1);
                            *(uint*)(d + (long)col * 4) = *(uint*)(s + (long)srcCol * 4);
                        }
                    }
                }
            }
            finally
            {
                tileBmp.UnlockBits(bd);
            }
        }
    }

    private static void DecodeDeflateTileToBuffer(int w, int h, ReadOnlySpan<byte> data, byte[] dest)
    {
        fixed (byte* pData = data)
        {
            using var ms = new UnmanagedMemoryStream(pData, data.Length);
            using var z = new ZLibStream(ms, CompressionMode.Decompress);

            int rowBytes = checked(w * 3);
            var rowBuf = ArrayPool<byte>.Shared.Rent(rowBytes);
            try
            {
                int dstStride = checked(w * 4);
                fixed (byte* dst = dest)
                {
                    for (int row = 0; row < h; row++)
                    {
                        int read = 0;
                        while (read < rowBytes)
                        {
                            int n = z.Read(rowBuf, read, rowBytes - read);
                            if (n == 0) break;
                            read += n;
                        }
                        if (read < rowBytes)
                            throw new EndOfStreamException($"Deflate 區塊資料不足 (第 {row} 列僅讀取 {read}/{rowBytes} bytes)");

                        byte* d = dst + (long)row * dstStride;
                        for (int col = 0, so = 0; col < w; col++, so += 3, d += 4)
                        {
                            d[0] = rowBuf[so];     // B
                            d[1] = rowBuf[so + 1]; // G
                            d[2] = rowBuf[so + 2]; // R
                            d[3] = 255;            // A
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rowBuf);
            }
        }
    }

    /// <summary>
    /// 在指定的 Graphics 上繪製目前畫面（可縮放與保持比例）。
    /// 1:1 像素映射時自動啟用極速 NearestNeighbor Blit，消除雙線性內插過濾開銷。
    /// </summary>
    public void DrawTo(Graphics g, Rectangle destRect)
    {
        lock (_renderLock)
        {
            if (_bitmap != null)
            {
                if (destRect.Width == _width && destRect.Height == _height)
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                }
                else
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                }
                g.DrawImage(_bitmap, destRect, 0, 0, _width, _height, GraphicsUnit.Pixel);
            }
            else
            {
                g.Clear(Color.FromArgb(30, 30, 30));
            }
        }
    }

    /// <summary>
    /// 截取目前遠端桌面畫面作為縮圖（用於辨識裝置與最近連線記錄）。
    /// </summary>
    public Bitmap? CreateThumbnail(int thumbWidth = 240, int thumbHeight = 135)
    {
        lock (_renderLock)
        {
            if (_bitmap == null || _width <= 0 || _height <= 0) return null;
            var thumb = new Bitmap(thumbWidth, thumbHeight, PixelFormat.Format32bppRgb);
            using var g = Graphics.FromImage(thumb);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(_bitmap, new Rectangle(0, 0, thumbWidth, thumbHeight), 0, 0, _width, _height, GraphicsUnit.Pixel);
            return thumb;
        }
    }

    private void Cleanup()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _buffer = null;
        _bufPtr = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_renderLock)
        {
            Cleanup();
        }
    }
}
