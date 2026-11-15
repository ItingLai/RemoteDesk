using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDesk.Common;

public enum TrustStatus
{
    Active = 0,
    Revoked = 1,
    Blocked = 2
}

/// <summary>
/// 可信裝置項目，記錄於被控端本機。
/// 任何連線授權均以本機信任清單為唯一基準，伺服器不得單方面新增或修改。
/// </summary>
public sealed class TrustedDevice
{
    public string TrustedDeviceId { get; set; } = "";
    public string TrustedDeviceName { get; set; } = "";
    public string TrustedPublicKey { get; set; } = ""; // SPKI (Base64)
    public string TrustedPublicKeyFingerprint { get; set; } = "";
    public DateTime PairedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastConnectedAt { get; set; }
    public bool AutoAcceptEnabled { get; set; } = true;
    public TrustStatus TrustStatus { get; set; } = TrustStatus.Active;
}

/// <summary>
/// 本機可信裝置管理庫 (Trust Management)
/// 負責儲存與管理經過人工配對授權的可信主控端裝置。
/// </summary>
internal sealed class TrustStore
{
    private readonly string _filePath;
    private readonly object _lock = new();
    private List<TrustedDevice> _devices = new();

    public TrustStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppConfig.SharedDirectory, "trusted_devices.json");
        Load();
    }

    public IReadOnlyList<TrustedDevice> GetAll()
    {
        lock (_lock)
        {
            return _devices.Select(d => new TrustedDevice
            {
                TrustedDeviceId = d.TrustedDeviceId,
                TrustedDeviceName = d.TrustedDeviceName,
                TrustedPublicKey = d.TrustedPublicKey,
                TrustedPublicKeyFingerprint = d.TrustedPublicKeyFingerprint,
                PairedAt = d.PairedAt,
                LastConnectedAt = d.LastConnectedAt,
                AutoAcceptEnabled = d.AutoAcceptEnabled,
                TrustStatus = d.TrustStatus
            }).ToList();
        }
    }

    public IReadOnlyList<TrustedDevice> GetActive()
    {
        lock (_lock)
        {
            return _devices.Where(d => d.TrustStatus == TrustStatus.Active).ToList();
        }
    }

    /// <summary>
    /// 檢查連入裝置是否已受信任，並檢查其公鑰是否與配對時完全一致。
    /// 若 Device ID 存在但公鑰不同，則判定為 keyMismatch = true，嚴禁自動接受！
    /// </summary>
    public bool IsTrusted(string deviceId, byte[] publicKeySpki, out TrustedDevice? matchedDevice, out bool keyMismatch)
    {
        matchedDevice = null;
        keyMismatch = false;

        string targetKeyBase64 = Convert.ToBase64String(publicKeySpki);

        lock (_lock)
        {
            var dev = _devices.FirstOrDefault(d => string.Equals(d.TrustedDeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (dev == null)
            {
                return false;
            }

            // 裝置存在，比對公鑰
            if (!string.Equals(dev.TrustedPublicKey, targetKeyBase64, StringComparison.Ordinal))
            {
                // 公鑰異常變更！
                Log.Warn($"[安全警示] 裝置 {deviceId} ({dev.TrustedDeviceName}) 公鑰與配對記錄不符！可能為偽造或重灌裝置！");
                matchedDevice = dev;
                keyMismatch = true;
                return false;
            }

            if (dev.TrustStatus != TrustStatus.Active)
            {
                matchedDevice = dev;
                return false;
            }

            matchedDevice = dev;
            return true;
        }
    }

    /// <summary>
    /// 新增或更新已信任裝置（人工配對成功時呼叫）。
    /// </summary>
    public void AddOrUpdate(string deviceId, string deviceName, byte[] publicKeySpki, bool autoAccept = true)
    {
        string keyBase64 = Convert.ToBase64String(publicKeySpki);
        string fingerprint = DeviceIdentity.ComputeFingerprint(publicKeySpki);

        lock (_lock)
        {
            var existing = _devices.FirstOrDefault(d => string.Equals(d.TrustedDeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.TrustedDeviceName = deviceName;
                existing.TrustedPublicKey = keyBase64;
                existing.TrustedPublicKeyFingerprint = fingerprint;
                existing.AutoAcceptEnabled = autoAccept;
                existing.TrustStatus = TrustStatus.Active;
                existing.PairedAt = DateTime.UtcNow;
            }
            else
            {
                _devices.Add(new TrustedDevice
                {
                    TrustedDeviceId = deviceId,
                    TrustedDeviceName = deviceName,
                    TrustedPublicKey = keyBase64,
                    TrustedPublicKeyFingerprint = fingerprint,
                    PairedAt = DateTime.UtcNow,
                    AutoAcceptEnabled = autoAccept,
                    TrustStatus = TrustStatus.Active
                });
            }
            Save();
        }
    }

    /// <summary>
    /// 記錄裝置連線時間。
    /// </summary>
    public void RecordConnected(string deviceId)
    {
        lock (_lock)
        {
            var dev = _devices.FirstOrDefault(d => string.Equals(d.TrustedDeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (dev != null)
            {
                dev.LastConnectedAt = DateTime.UtcNow;
                Save();
            }
        }
    }

    /// <summary>
    /// 撤銷指定裝置之信任關係。
    /// </summary>
    public bool RevokeDevice(string deviceId)
    {
        lock (_lock)
        {
            var dev = _devices.FirstOrDefault(d => string.Equals(d.TrustedDeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (dev != null)
            {
                dev.TrustStatus = TrustStatus.Revoked;
                Save();
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 從可信清單徹底移除指定裝置。
    /// </summary>
    public bool RemoveDevice(string deviceId)
    {
        lock (_lock)
        {
            int removed = _devices.RemoveAll(d => string.Equals(d.TrustedDeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                Save();
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 撤銷所有已授權裝置。
    /// </summary>
    public void RevokeAll()
    {
        lock (_lock)
        {
            foreach (var d in _devices)
            {
                d.TrustStatus = TrustStatus.Revoked;
            }
            Save();
        }
    }

    /// <summary>
    /// 清除所有已授權裝置清單。
    /// </summary>
    public void ClearAll()
    {
        lock (_lock)
        {
            _devices.Clear();
            Save();
        }
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    _devices = JsonSerializer.Deserialize<List<TrustedDevice>>(json, AppConfig.JsonOptions) ?? new();
                }
                else
                {
                    _devices = new();
                }
            }
            catch (Exception ex)
            {
                Log.Error("讀取 trusted_devices.json 失敗", ex);
                _devices = new();
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_devices, AppConfig.JsonOptions));
                File.Move(tmp, _filePath, true);
            }
            catch (Exception ex)
            {
                Log.Error("儲存 trusted_devices.json 失敗", ex);
            }
        }
    }
}
