using System.Collections.Concurrent;
using System.Diagnostics;
using RemoteDesk.Native;

namespace RemoteDesk.Host;

/// <summary>讓工作執行緒跟隨目前的輸入桌面（Default / Winlogon），以便在 UAC、鎖定畫面時（服務模式）仍可擷取與操作。</summary>
internal static class DesktopHelper
{
    [ThreadStatic] private static string? _currentName;

    /// <summary>若輸入桌面已改變，將目前執行緒切換過去。回傳是否發生切換。</summary>
    public static bool SyncThreadDesktop()
    {
        var hDesk = Win32.OpenInputDesktop(0, false, Win32.GENERIC_ALL);
        if (hDesk == IntPtr.Zero) return false; // 一般權限無法開啟安全桌面
        var name = Win32.GetDesktopName(hDesk);
        _currentName ??= Win32.GetDesktopName(Win32.GetThreadDesktop(Win32.GetCurrentThreadId()));
        if (string.Equals(name, _currentName, StringComparison.OrdinalIgnoreCase))
        {
            Win32.CloseDesktop(hDesk);
            return false;
        }
        if (Win32.SetThreadDesktop(hDesk))
        {
            Common.Log.Info($"執行緒切換到桌面 {name}");
            _currentName = name;
            return true; // 刻意不關閉 handle，執行緒仍在使用
        }
        Win32.CloseDesktop(hDesk);
        return false;
    }
}

/// <summary>專用的輸入注入執行緒：保持事件順序，且可獨立切換到輸入桌面。</summary>
internal sealed class InputThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastDesktopCheck;

    public InputThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "RemoteDesk-Input", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Post(Action a)
    {
        try { _queue.Add(a); } catch (InvalidOperationException) { }
    }

    private void Run()
    {
        DesktopHelper.SyncThreadDesktop();
        foreach (var a in _queue.GetConsumingEnumerable())
        {
            var now = _sw.ElapsedMilliseconds;
            if (now - _lastDesktopCheck > 500)
            {
                _lastDesktopCheck = now;
                DesktopHelper.SyncThreadDesktop();
            }
            try { a(); }
            catch (Exception ex) { Common.Log.Warn("注入輸入失敗: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(1000);
    }
}
