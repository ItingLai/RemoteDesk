namespace RemoteDesk.Common;

/// <summary>簡易執行緒安全日誌，寫入 %APPDATA%\RemoteDesk\logs。</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly List<Action<string>> Listeners = new();
    private static string? _path;
    public static bool ConsoleEcho { get; set; }

    public static string LogDirectory => Path.Combine(AppConfig.DataDirectory, "logs");

    /// <summary>註冊執行期日誌訂閱器（僅供診斷稽核與測試收集，不影響檔案寫入）。</summary>
    internal static IDisposable Subscribe(Action<string> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (Gate)
        {
            Listeners.Add(listener);
        }
        return new LogSubscription(listener);
    }

    private sealed class LogSubscription(Action<string> listener) : IDisposable
    {
        private Action<string>? _listener = listener;
        public void Dispose()
        {
            var target = Interlocked.Exchange(ref _listener, null);
            if (target == null) return;
            lock (Gate)
            {
                Listeners.Remove(target);
            }
        }
    }

    private static string FilePath
    {
        get
        {
            if (_path == null)
            {
                Directory.CreateDirectory(LogDirectory);
                _path = Path.Combine(LogDirectory, $"remotedesk-{DateTime.Now:yyyyMMdd}-{Environment.ProcessId}.log");
                TryCleanup();
            }
            return _path;
        }
    }

    private static void TryCleanup()
    {
        try
        {
            foreach (var f in Directory.GetFiles(LogDirectory, "*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)) File.Delete(f);
        }
        catch { }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] [{Environment.CurrentManagedThreadId}] {msg}";
        if (ConsoleEcho) Console.WriteLine(line);
        Action<string>[]? snapshot = null;
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch { }
            if (Listeners.Count > 0) snapshot = Listeners.ToArray();
        }
        if (snapshot != null)
        {
            foreach (var listener in snapshot)
            {
                try { listener(line); } catch { }
            }
        }
    }
}
