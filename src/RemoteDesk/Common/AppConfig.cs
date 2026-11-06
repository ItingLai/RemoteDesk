using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace RemoteDesk.Common;

/// <summary>被控端（主機）設定：裝置 ID、密碼驗證值、連線埠等。存放於 %ProgramData%\RemoteDesk\host.json，讓服務模式與使用者介面共用。</summary>
internal sealed class HostConfig
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = Environment.MachineName;
    public int TcpPort { get; set; } = Protocol.DefaultTcpPort;
    /// <summary>無人值守密碼：PBKDF2 鹽值 (Base64)。</summary>
    public string? PasswordSalt { get; set; }
    /// <summary>無人值守密碼：PBKDF2 驗證值 (Base64)。</summary>
    public string? PasswordVerifier { get; set; }
    public int PasswordIterations { get; set; } = 120_000;
    /// <summary>允許未持有密碼的連線請求，由本機使用者按「接受」。</summary>
    public bool AllowInteractive { get; set; } = true;
    public bool HostEnabled { get; set; } = true;
    /// <summary>連線時是否同步剪貼簿。</summary>
    public bool AllowClipboard { get; set; } = true;

    [JsonIgnore] public bool HasPassword => !string.IsNullOrEmpty(PasswordVerifier) && !string.IsNullOrEmpty(PasswordSalt);

    public void SetPassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            PasswordSalt = null;
            PasswordVerifier = null;
            return;
        }
        var salt = RandomNumberGenerator.GetBytes(16);
        PasswordSalt = Convert.ToBase64String(salt);
        PasswordVerifier = Convert.ToBase64String(Crypto.DerivePasswordKey(password, salt, PasswordIterations));
    }

    public static string FilePath => Path.Combine(AppConfig.SharedDirectory, "host.json");

    public static HostConfig Load()
    {
        HostConfig cfg;
        try
        {
            cfg = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(FilePath), AppConfig.JsonOptions) ?? new HostConfig()
                : new HostConfig();
        }
        catch (Exception ex)
        {
            Log.Error("讀取 host.json 失敗，使用預設值", ex);
            cfg = new HostConfig();
        }
        bool dirty = false;
        if (string.IsNullOrWhiteSpace(cfg.DeviceId) || cfg.DeviceId.Length != 9 || !cfg.DeviceId.All(char.IsDigit))
        {
            cfg.DeviceId = AppConfig.GenerateDeviceId();
            dirty = true;
        }
        if (string.IsNullOrWhiteSpace(cfg.DeviceName)) { cfg.DeviceName = Environment.MachineName; dirty = true; }
        if (dirty || !File.Exists(FilePath)) cfg.TrySave();
        return cfg;
    }

    public bool TrySave()
    {
        try
        {
            AppConfig.EnsureSharedDirectory();
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, AppConfig.JsonOptions));
            File.Move(tmp, FilePath, true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("儲存 host.json 失敗", ex);
            return false;
        }
    }
}

internal sealed class RecentEntry
{
    public string Key { get; set; } = "";      // ID 或 IP/主機名稱
    public string? DeviceId { get; set; }
    public string? Name { get; set; }
    public string? LastAddress { get; set; }   // 最後一次成功連線的 IP
    public int Port { get; set; } = Protocol.DefaultTcpPort;
    public DateTime LastUsed { get; set; }
    public string? ThumbnailPath { get; set; } // 被連線裝置的桌布/畫面縮圖
    public bool IsFavorite { get; set; }       // 是否加入我的最愛 (規格 4.6)
    public int DurationSeconds { get; set; }   // 連線持續秒數 (規格 8)
    public string? DisconnectReason { get; set; } // 中斷原因
}

/// <summary>控制端（檢視端）設定，存放在使用者的 %APPDATA%\RemoteDesk\viewer.json。</summary>
internal sealed class ViewerConfig
{
    public List<RecentEntry> Recent { get; set; } = new();
    /// <summary>DeviceId → DPAPI 加密後的密碼 (Base64)。</summary>
    public Dictionary<string, string> SavedPasswords { get; set; } = new();
    public ImeSyncMode ImeMode { get; set; } = ImeSyncMode.Bound;
    public int Quality { get; set; } = 70;
    public int MaxFps { get; set; } = 30;
    public bool Lossless { get; set; } = true;
    public bool FitToWindow { get; set; } = true;
    public bool SyncClipboard { get; set; } = true;
    public string ViewerId { get; set; } = "";
    public string ThemeMode { get; set; } = "Dark";      // Dark / Light / System (規格 2.2)
    public string Language { get; set; } = "zh-TW";      // 規格 6.2
    public bool StartWithWindows { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;

    public static string FilePath => Path.Combine(AppConfig.DataDirectory, "viewer.json");

    public static ViewerConfig Load()
    {
        ViewerConfig cfg;
        try
        {
            cfg = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<ViewerConfig>(File.ReadAllText(FilePath), AppConfig.JsonOptions) ?? new ViewerConfig()
                : new ViewerConfig();
        }
        catch (Exception ex)
        {
            Log.Error("讀取 viewer.json 失敗", ex);
            cfg = new ViewerConfig();
        }
        if (string.IsNullOrEmpty(cfg.ViewerId)) cfg.ViewerId = AppConfig.GenerateDeviceId();
        return cfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DataDirectory);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, AppConfig.JsonOptions));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { Log.Error("儲存 viewer.json 失敗", ex); }
    }

    public void RememberPassword(string deviceId, string? password)
    {
        if (string.IsNullOrEmpty(password)) { SavedPasswords.Remove(deviceId); return; }
        var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
        SavedPasswords[deviceId] = Convert.ToBase64String(enc);
    }

    public string? GetSavedPassword(string deviceId)
    {
        if (!SavedPasswords.TryGetValue(deviceId, out var b64)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    public void TouchRecent(string key, string? deviceId, string? name, string? address, int port)
    {
        var existing = Recent.FirstOrDefault(r =>
            (deviceId != null && r.DeviceId == deviceId) || string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            existing = new RecentEntry();
            Recent.Add(existing);
        }
        existing.Key = deviceId ?? key;
        existing.DeviceId = deviceId ?? existing.DeviceId;
        existing.Name = name ?? existing.Name;
        existing.LastAddress = address ?? existing.LastAddress;
        existing.Port = port;
        existing.LastUsed = DateTime.Now;
        Recent = Recent.OrderByDescending(r => r.LastUsed).Take(50).ToList();
    }

    public void UpdateThumbnail(string key, string? deviceId, string thumbPath)
    {
        var existing = Recent.FirstOrDefault(r =>
            (deviceId != null && r.DeviceId == deviceId) || string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.ThumbnailPath = thumbPath;
            Save();
        }
    }

    public void ToggleFavorite(string key)
    {
        var existing = Recent.FirstOrDefault(r =>
            string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.DeviceId, key, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.IsFavorite = !existing.IsFavorite;
            Save();
        }
    }

    public void RemoveRecent(string key)
    {
        Recent.RemoveAll(r =>
            string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.DeviceId, key, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public List<RecentEntry> GetRecent() => Recent.OrderByDescending(r => r.LastUsed).ToList();
    public List<RecentEntry> GetFavorites() => Recent.Where(r => r.IsFavorite).OrderByDescending(r => r.LastUsed).ToList();
}

internal static class AppConfig
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string? _dataOverride;

    /// <summary>每位使用者的資料夾（檢視端設定、日誌）。</summary>
    public static string DataDirectory =>
        _dataOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteDesk");

    /// <summary>全機共用資料夾（主機 ID、密碼）。</summary>
    public static string SharedDirectory =>
        _dataOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteDesk");

    /// <summary>測試或可攜模式時指定資料夾。</summary>
    public static void OverrideDataDirectory(string dir)
    {
        _dataOverride = dir;
        Directory.CreateDirectory(dir);
    }

    public static void EnsureSharedDirectory()
    {
        if (Directory.Exists(SharedDirectory)) return;
        Directory.CreateDirectory(SharedDirectory);
        if (_dataOverride != null) return;
        try
        {
            // 讓一般使用者與 SYSTEM 服務都能讀寫主機設定。
            var di = new DirectoryInfo(SharedDirectory);
            var sec = di.GetAccessControl();
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                System.Security.AccessControl.FileSystemRights.Modify,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
            di.SetAccessControl(sec);
        }
        catch (Exception ex) { Log.Warn("設定共用資料夾權限失敗: " + ex.Message); }
    }

    /// <summary>依 MachineGuid 產生穩定的 9 位數 ID（類似 AnyDesk ID），重灌設定檔也不會變。</summary>
    public static string GenerateDeviceId()
    {
        string seed;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            seed = key?.GetValue("MachineGuid") as string ?? Guid.NewGuid().ToString();
        }
        catch { seed = Guid.NewGuid().ToString(); }
        if (_dataOverride != null) seed += _dataOverride; // 測試時讓不同資料夾有不同 ID
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("RemoteDesk-ID:" + seed));
        var n = BitConverter.ToUInt64(hash, 0) % 900_000_000UL + 100_000_000UL;
        return n.ToString();
    }

    public static string FormatId(string id) =>
        id.Length == 9 ? $"{id[..3]} {id[3..6]} {id[6..]}" : id;

    public static void SetStartupWithWindows(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enable)
            {
                var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue("RemoteDesk", $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue("RemoteDesk", false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("設定開機自啟動失敗: " + ex.Message);
        }
    }

    public static bool IsStartupWithWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("RemoteDesk") != null;
        }
        catch { return false; }
    }
}
