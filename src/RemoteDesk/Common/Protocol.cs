using System.Buffers.Binary;
using System.Text;

namespace RemoteDesk.Common;

internal static class Protocol
{
    public const int DefaultTcpPort = 21119;
    public const int DiscoveryPort = 21118;
    public const ushort Version = 1;
    public static readonly byte[] Magic = "RDSK"u8.ToArray();
    public const int MaxMessageSize = 64 * 1024 * 1024;
    public const string AllMonitors = "*";
    /// <summary>主機注入輸入事件時寫入 dwExtraInfo 的標記，避免連到自己時造成回授迴圈。</summary>
    public static readonly IntPtr InjectedMarker = new(0x5244534B);
}

/// <summary>主機 → 檢視端訊息。</summary>
internal enum HostMsg : byte
{
    MonitorList = 0x01,
    VideoFrame = 0x02,
    CursorShape = 0x03,
    ImeState = 0x05,
    Clipboard = 0x06,
    Pong = 0x07,
    Caret = 0x08,
    Notice = 0x09,
    StreamInfo = 0x0A,
}

/// <summary>檢視端 → 主機訊息。</summary>
internal enum ViewerMsg : byte
{
    SelectMonitor = 0x41,
    MouseMove = 0x42,
    MouseButton = 0x43,
    MouseWheel = 0x44,
    Key = 0x45,
    Text = 0x46,
    ImeSet = 0x47,
    FrameAck = 0x48,
    VideoSettings = 0x49,
    Clipboard = 0x4A,
    Ping = 0x4B,
    LockKeys = 0x4C,
    Special = 0x4D,
    ImeMode = 0x4E,
    ReleaseAll = 0x4F,
    RequestKeyframe = 0x50,
}

internal enum MouseButtonId : byte { Left = 0, Right = 1, Middle = 2, X1 = 3, X2 = 4 }

internal enum SpecialAction : byte { CtrlAltDel = 1, LockScreen = 2, TaskManager = 3 }

/// <summary>輸入法同步模式。</summary>
internal enum ImeSyncMode : byte
{
    /// <summary>綁定模式（預設）：按鍵原封不動送到遠端，由遠端輸入法組字；兩端中/英狀態與鍵盤配置雙向同步。</summary>
    Bound = 0,
    /// <summary>本機輸入法模式：在本機組字，完成的文字以 Unicode 送出；遠端輸入法會被強制切到英數。</summary>
    Local = 1,
}

[Flags]
internal enum KeyFlags : byte { None = 0, Extended = 1, Up = 2 }

internal enum TileEncoding : byte { Jpeg = 0, Deflate = 1 }

internal readonly record struct ImeState(long KeyboardLayout, bool Open, int Conversion)
{
    public ushort LangId => (ushort)(KeyboardLayout & 0xFFFF);
    /// <summary>是否為「中文（原生）」輸入狀態。</summary>
    public bool IsNative => Open && (Conversion & 0x0001) != 0;
    public bool SameMode(ImeState o) => LangId == o.LangId && IsNative == o.IsNative && ((Conversion & 0x8) == (o.Conversion & 0x8));
}

internal sealed record MonitorInfo(string DeviceName, string FriendlyName, int Number, int X, int Y, int Width, int Height, bool Primary, int Dpi)
{
    public System.Drawing.Rectangle Bounds => new(X, Y, Width, Height);
    public string Label => $"螢幕 {Number}{(Primary ? " (主)" : "")}";
}

/// <summary>簡易二進位封包寫入器。</summary>
internal sealed class PacketWriter
{
    private byte[] _buf;
    private int _pos;

    public PacketWriter(byte type, int capacity = 256)
    {
        _buf = new byte[capacity];
        _buf[_pos++] = type;
    }

    public static PacketWriter Raw(int capacity = 256) => new(capacity);

    private PacketWriter(int capacity)
    {
        _buf = new byte[capacity];
    }

    public int Length => _pos;

    private void Ensure(int n)
    {
        if (_pos + n <= _buf.Length) return;
        var nb = new byte[Math.Max(_buf.Length * 2, _pos + n)];
        Buffer.BlockCopy(_buf, 0, nb, 0, _pos);
        _buf = nb;
    }

    public PacketWriter U8(byte v) { Ensure(1); _buf[_pos++] = v; return this; }
    public PacketWriter Bool(bool v) => U8(v ? (byte)1 : (byte)0);
    public PacketWriter U16(ushort v) { Ensure(2); BinaryPrimitives.WriteUInt16LittleEndian(_buf.AsSpan(_pos), v); _pos += 2; return this; }
    public PacketWriter I16(short v) { Ensure(2); BinaryPrimitives.WriteInt16LittleEndian(_buf.AsSpan(_pos), v); _pos += 2; return this; }
    public PacketWriter I32(int v) { Ensure(4); BinaryPrimitives.WriteInt32LittleEndian(_buf.AsSpan(_pos), v); _pos += 4; return this; }
    public PacketWriter U32(uint v) { Ensure(4); BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(_pos), v); _pos += 4; return this; }
    public PacketWriter I64(long v) { Ensure(8); BinaryPrimitives.WriteInt64LittleEndian(_buf.AsSpan(_pos), v); _pos += 8; return this; }

    public PacketWriter Bytes(ReadOnlySpan<byte> data)
    {
        I32(data.Length);
        Raw(data);
        return this;
    }

    public PacketWriter Raw(ReadOnlySpan<byte> data)
    {
        Ensure(data.Length);
        data.CopyTo(_buf.AsSpan(_pos));
        _pos += data.Length;
        return this;
    }

    public PacketWriter Str(string? s)
    {
        var bytes = Encoding.UTF8.GetBytes(s ?? "");
        return Bytes(bytes);
    }

    public byte[] ToArray() => _buf.AsSpan(0, _pos).ToArray();
    public ReadOnlyMemory<byte> Memory => _buf.AsMemory(0, _pos);
}

/// <summary>簡易二進位封包讀取器。</summary>
internal ref struct PacketReader
{
    private readonly ReadOnlySpan<byte> _buf;
    private int _pos;

    public PacketReader(ReadOnlySpan<byte> buf)
    {
        _buf = buf;
        _pos = 0;
    }

    public int Remaining => _buf.Length - _pos;

    private void Need(int n)
    {
        if (_pos + n > _buf.Length) throw new InvalidDataException("封包長度不足");
    }

    public byte U8() { Need(1); return _buf[_pos++]; }
    public bool Bool() => U8() != 0;
    public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(_buf[_pos..]); _pos += 2; return v; }
    public short I16() { Need(2); var v = BinaryPrimitives.ReadInt16LittleEndian(_buf[_pos..]); _pos += 2; return v; }
    public int I32() { Need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(_buf[_pos..]); _pos += 4; return v; }
    public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(_buf[_pos..]); _pos += 4; return v; }
    public long I64() { Need(8); var v = BinaryPrimitives.ReadInt64LittleEndian(_buf[_pos..]); _pos += 8; return v; }

    public ReadOnlySpan<byte> Bytes()
    {
        int n = I32();
        if (n < 0) throw new InvalidDataException("長度錯誤");
        Need(n);
        var s = _buf.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public ReadOnlySpan<byte> Raw(int n)
    {
        Need(n);
        var s = _buf.Slice(_pos, n);
        _pos += n;
        return s;
    }

    public string Str() => Encoding.UTF8.GetString(Bytes());
}

internal static class MessageCodec
{
    public static byte[] MonitorList(IReadOnlyList<MonitorInfo> monitors, string selected)
    {
        var w = new PacketWriter((byte)HostMsg.MonitorList);
        w.Str(selected).U16((ushort)monitors.Count);
        foreach (var m in monitors)
            w.Str(m.DeviceName).Str(m.FriendlyName).I32(m.Number).I32(m.X).I32(m.Y).I32(m.Width).I32(m.Height).Bool(m.Primary).I32(m.Dpi);
        return w.ToArray();
    }

    public static (List<MonitorInfo> list, string selected) ReadMonitorList(ref PacketReader r)
    {
        var selected = r.Str();
        int n = r.U16();
        var list = new List<MonitorInfo>(n);
        for (int i = 0; i < n; i++)
            list.Add(new MonitorInfo(r.Str(), r.Str(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.Bool(), r.I32()));
        return (list, selected);
    }

    public static byte[] Ime(byte type, ImeState s) =>
        new PacketWriter(type, 16).I64(s.KeyboardLayout).Bool(s.Open).I32(s.Conversion).ToArray();

    public static ImeState ReadIme(ref PacketReader r) => new(r.I64(), r.Bool(), r.I32());
}
