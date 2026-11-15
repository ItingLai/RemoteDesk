using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using RemoteDesk.Native;

namespace RemoteDesk.Common;

/// <summary>
/// 單一主程式實例守護 (Windows Mutex + Activation Event)。
/// 依目前使用者環境、Profile 身分與裝置 ID 建立獨立的具名 Mutex：
/// - 同一使用者 + 同一 Profile / Device ID 僅允許一個主要實例。
/// - 不同 `--profile` 或不同 Device ID 互不阻塞，可同時執行。
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly int _ownerThreadId;
    private bool _ownsMutex;
    private bool _disposed;

    public string ScopeKey { get; }
    public string MutexName { get; }
    public string ActivationEventName { get; }
    public string? ProfileName { get; }
    public string DeviceId { get; }
    public EventWaitHandle ActivationEvent => _activationEvent;

    private SingleInstanceGuard(
        Mutex mutex,
        EventWaitHandle activationEvent,
        int ownerThreadId,
        string scopeKey,
        string mutexName,
        string activationEventName,
        string? profileName,
        string deviceId)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        _ownerThreadId = ownerThreadId;
        _ownsMutex = true;
        ScopeKey = scopeKey;
        MutexName = mutexName;
        ActivationEventName = activationEventName;
        ProfileName = profileName;
        DeviceId = deviceId;
    }

    /// <summary>
    /// 計算標準化的實例隔離識別碼 (供 Mutex 與啟用事件共用)。
    /// </summary>
    public static string BuildScopeKey(string? profileName, string? deviceId, string? userScope = null)
    {
        string resolvedUser = !string.IsNullOrWhiteSpace(userScope)
            ? userScope.Trim()
            : ResolveCurrentUserScope();

        string normalizedProfile = string.IsNullOrWhiteSpace(profileName)
            ? "__default__"
            : "profile:" + profileName.Trim().ToLowerInvariant();

        string normalizedDeviceId = string.IsNullOrWhiteSpace(deviceId)
            ? ""
            : deviceId.Replace(" ", "", StringComparison.Ordinal).Trim().ToUpperInvariant();

        return $"{resolvedUser}|{normalizedProfile}|{normalizedDeviceId}";
    }

    public static string BuildMutexName(string? profileName, string? deviceId, string? userScope = null)
    {
        string key = BuildScopeKey(profileName, deviceId, userScope);
        return "Local\\RemoteDesk_Instance_" + HashKey(key);
    }

    public static string BuildActivationEventName(string? profileName, string? deviceId, string? userScope = null)
    {
        string key = BuildScopeKey(profileName, deviceId, userScope);
        return "Local\\RemoteDesk_Activate_" + HashKey(key);
    }

    private static string ResolveCurrentUserScope()
    {
        try
        {
            return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        }
        catch
        {
            return Environment.UserName;
        }
    }

    private static string HashKey(string raw)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// 嘗試取得目前 Profile / Device ID 的單一實例 Mutex。
    /// 若相同身分的主程式實例已在執行，回傳 null。
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(string? profileName, string? deviceId, string? userScope = null)
    {
        string scopeKey = BuildScopeKey(profileName, deviceId, userScope);
        string hash = HashKey(scopeKey);
        string mutexName = "Local\\RemoteDesk_Instance_" + hash;
        string eventName = "Local\\RemoteDesk_Activate_" + hash;

        var mutex = new Mutex(initiallyOwned: false, name: mutexName);
        bool acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(0, exitContext: false);
            }
            catch (AbandonedMutexException)
            {
                // 前一個持有此 Mutex 的程序異常終止，本實例可合法接手所有權
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return null;
            }

            var activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            return new SingleInstanceGuard(
                mutex,
                activationEvent,
                Environment.CurrentManagedThreadId,
                scopeKey,
                mutexName,
                eventName,
                profileName,
                deviceId ?? "");
        }
        catch
        {
            if (acquired)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
            mutex.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 喚醒並將焦點切換至既有的相同實例視窗。
    /// </summary>
    public static bool NotifyExistingInstance(string? profileName, string? deviceId, string? userScope = null)
    {
        bool notified = false;
        string eventName = BuildActivationEventName(profileName, deviceId, userScope);

        try
        {
            if (EventWaitHandle.TryOpenExisting(eventName, out var evt))
            {
                using (evt)
                {
                    notified = evt.Set();
                }
            }
        }
        catch { }

        if (!notified)
        {
            try
            {
                using var current = Process.GetCurrentProcess();
                var others = Process.GetProcessesByName(current.ProcessName);
                foreach (var proc in others)
                {
                    using (proc)
                    {
                        if (proc.Id == current.Id) continue;
                        IntPtr hWnd = proc.MainWindowHandle;
                        if (hWnd != IntPtr.Zero)
                        {
                            Win32.ShowWindow(hWnd, Win32.SW_RESTORE);
                            Win32.SetForegroundWindow(hWnd);
                            notified = true;
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        return notified;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_ownsMutex && Environment.CurrentManagedThreadId == _ownerThreadId)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 避免不同執行緒釋放拋出例外
            }
            _ownsMutex = false;
        }

        try { _activationEvent.Dispose(); } catch { }
        try { _mutex.Dispose(); } catch { }
    }
}
