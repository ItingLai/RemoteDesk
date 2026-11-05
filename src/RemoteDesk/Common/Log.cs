namespace RemoteDesk.Common;

/// <summary>簡易執行緒安全日誌，寫入 %APPDATA%\RemoteDesk\logs。</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;
    public static bool ConsoleEcho { get; set; }

    public static string LogDirectory => Path.Combine(AppConfig.DataDirectory, "logs");

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
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, line + Environment.NewLine); } catch { }
        }
    }
}
