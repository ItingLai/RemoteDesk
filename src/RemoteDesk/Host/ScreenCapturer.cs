using System.Drawing;
using System.Runtime.InteropServices;
using RemoteDesk.Common;
using RemoteDesk.Native;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteDesk.Host;

internal sealed class CaptureLostException(string msg) : Exception(msg);

/// <summary>擷取到的畫面（BGRA，32bpp），Data 在下一次 Capture 前有效。</summary>
internal readonly record struct CapturedFrame(IntPtr Data, int Width, int Height, int Stride);

internal interface IScreenCapturer : IDisposable
{
    string Name { get; }
    int Width { get; }
    int Height { get; }
    /// <summary>擷取下一張畫面。回傳 true 表示 Frame 內容可能已更新；false 表示逾時沒有變化。</summary>
    bool Capture(int timeoutMs);
    CapturedFrame Frame { get; }
}

/// <summary>GDI BitBlt 擷取，任何環境皆可使用（後備方案；也用於「全部螢幕」模式）。</summary>
internal sealed class GdiCapturer : IScreenCapturer
{
    private readonly Rectangle _bounds;
    private readonly IntPtr _memDc;
    private readonly IntPtr _dib;
    private readonly IntPtr _old;
    private readonly IntPtr _bits;
    private bool _disposed;

    public string Name => "GDI";
    public int Width => _bounds.Width;
    public int Height => _bounds.Height;
    public CapturedFrame Frame => new(_bits, _bounds.Width, _bounds.Height, _bounds.Width * 4);

    public GdiCapturer(Rectangle bounds)
    {
        _bounds = bounds;
        var screenDc = Win32.GetDC(IntPtr.Zero);
        try
        {
            _memDc = Win32.CreateCompatibleDC(screenDc);
            var bih = new Win32.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = bounds.Width,
                biHeight = -bounds.Height, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
            _dib = Win32.CreateDIBSection(screenDc, ref bih, 0, out _bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection 失敗");
            _old = Win32.SelectObject(_memDc, _dib);
        }
        finally { Win32.ReleaseDC(IntPtr.Zero, screenDc); }
    }

    public bool Capture(int timeoutMs)
    {
        var screenDc = Win32.GetDC(IntPtr.Zero);
        try
        {
            if (!Win32.BitBlt(_memDc, 0, 0, _bounds.Width, _bounds.Height, screenDc, _bounds.X, _bounds.Y, Win32.SRCCOPY))
            {
                // 安全桌面（UAC/鎖定）時會失敗，交給上層切換桌面後重試
                throw new CaptureLostException("BitBlt 失敗");
            }
        }
        finally { Win32.ReleaseDC(IntPtr.Zero, screenDc); }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Win32.SelectObject(_memDc, _old);
        Win32.DeleteObject(_dib);
        Win32.DeleteDC(_memDc);
    }
}

/// <summary>DXGI Desktop Duplication 擷取：GPU 直接取得畫面，效率最佳，只有畫面變動時才回傳。</summary>
internal sealed class DxgiCapturer : IScreenCapturer
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _dup;
    private readonly ID3D11Texture2D _staging;
    private readonly IntPtr _buffer;
    private readonly int _width, _height;
    private bool _first = true;
    private bool _disposed;

    public string Name => "DXGI";
    public int Width => _width;
    public int Height => _height;
    public CapturedFrame Frame => new(_buffer, _width, _height, _width * 4);

    public DxgiCapturer(MonitorInfo monitor)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? foundAdapter = null;
        IDXGIOutput? foundOutput = null;
        for (uint a = 0; foundOutput == null && factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                if (string.Equals(output.Description.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    foundAdapter = adapter;
                    foundOutput = output;
                    break;
                }
                output.Dispose();
            }
            if (foundOutput == null) adapter.Dispose();
        }
        if (foundAdapter == null || foundOutput == null)
            throw new InvalidOperationException($"找不到 DXGI 輸出 {monitor.DeviceName}");

        try
        {
            var desc = foundOutput.Description;
            if (desc.Rotation != ModeRotation.Identity && desc.Rotation != ModeRotation.Unspecified)
                throw new NotSupportedException("旋轉螢幕改用 GDI 擷取");

            D3D11.D3D11CreateDevice(foundAdapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 },
                out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
            _device = device!;
            _context = context!;

            using var output1 = foundOutput.QueryInterface<IDXGIOutput1>();
            _dup = output1.DuplicateOutput(_device);
            var dd = _dup.Description;
            _width = (int)dd.ModeDescription.Width;
            _height = (int)dd.ModeDescription.Height;
            if (_width != monitor.Width || _height != monitor.Height)
                Log.Warn($"DXGI 尺寸 {_width}x{_height} 與螢幕資訊 {monitor.Width}x{monitor.Height} 不同");

            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            _buffer = Marshal.AllocHGlobal(_width * _height * 4);
        }
        finally
        {
            foundOutput.Dispose();
            foundAdapter.Dispose();
        }
    }

    public unsafe bool Capture(int timeoutMs)
    {
        var hr = _dup.AcquireNextFrame((uint)Math.Max(0, timeoutMs), out var info, out var resource);
        if (hr == Vortice.DXGI.ResultCode.WaitTimeout) return false;
        if (hr.Failure) throw new CaptureLostException($"AcquireNextFrame 0x{hr.Code:X8}");
        try
        {
            if (info.LastPresentTime == 0 && !_first) return false; // 只有游標移動
            _first = false;
            using var tex = resource!.QueryInterface<ID3D11Texture2D>();
            _context.CopyResource(_staging, tex);
            var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowBytes = _width * 4;
                byte* src = (byte*)map.DataPointer;
                byte* dst = (byte*)_buffer;
                if (map.RowPitch == rowBytes)
                    Buffer.MemoryCopy(src, dst, (long)rowBytes * _height, (long)rowBytes * _height);
                else
                    for (int y = 0; y < _height; y++)
                        Buffer.MemoryCopy(src + (long)y * map.RowPitch, dst + (long)y * rowBytes, rowBytes, rowBytes);
            }
            finally { _context.Unmap(_staging, 0); }
            return true;
        }
        finally
        {
            resource?.Dispose();
            try { _dup.ReleaseFrame(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _staging?.Dispose();
        _dup?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
    }
}

internal static class CapturerFactory
{
    /// <summary>建立擷取器：單一螢幕優先使用 DXGI，失敗則退回 GDI；monitor 為 null 代表整個虛擬桌面。</summary>
    public static IScreenCapturer Create(MonitorInfo? monitor, bool allowDxgi)
    {
        if (monitor != null && allowDxgi)
        {
            try { return new DxgiCapturer(monitor); }
            catch (Exception ex) { Log.Warn($"DXGI 擷取無法使用 ({monitor.DeviceName})，改用 GDI: {ex.Message}"); }
        }
        return new GdiCapturer(monitor?.Bounds ?? MonitorEnumerator.VirtualScreen);
    }
}
