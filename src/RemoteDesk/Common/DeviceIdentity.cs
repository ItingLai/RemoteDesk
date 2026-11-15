using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDesk.Common;

/// <summary>
/// 裝置公私鑰身分識別（基於 ECDSA P-256 + Windows DPAPI 安全加密儲存）。
/// 徹底擺脫傳統使用者帳號密碼，每台裝置具備唯一的密鑰與 9 位數識別碼。
/// </summary>
internal sealed class DeviceIdentity : IDisposable
{
    private ECDsa _ecdsa;
    private bool _disposed;

    public string DeviceId { get; private set; }
    public string DeviceName { get; set; }
    public byte[] PublicKeySpki { get; private set; }
    public string PublicKeyBase64 => Convert.ToBase64String(PublicKeySpki);
    public string PublicKeyFingerprint => ComputeFingerprint(PublicKeySpki);

    private DeviceIdentity(string deviceId, string deviceName, ECDsa ecdsa)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        _ecdsa = ecdsa;
        PublicKeySpki = ecdsa.ExportSubjectPublicKeyInfo();
    }

    /// <summary>計算公鑰之 SHA-256 十六進位指紋（格式：XX:XX:XX:...）</summary>
    public static string ComputeFingerprint(byte[] spki)
    {
        var hash = SHA256.HashData(spki);
        var sb = new StringBuilder(hash.Length * 3);
        for (int i = 0; i < hash.Length; i++)
        {
            if (i > 0) sb.Append(':');
            sb.Append(hash[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 以本機私鑰對資料進行 ECDSA P-256 SHA-256 數位簽章。
    /// </summary>
    public byte[] SignData(byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    /// <summary>
    /// 驗證對方的 ECDSA P-256 數位簽章是否有效。
    /// </summary>
    public static bool VerifySignature(byte[] data, byte[] signature, byte[] publicKeySpki)
    {
        try
        {
            using var peerEcdsa = ECDsa.Create();
            peerEcdsa.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            return peerEcdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex)
        {
            Log.Warn($"ECDSA 簽章驗證失敗: {ex.Message}");
            return false;
        }
    }

    private sealed class IdentityMeta
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    private static string GetMetaPath(string dir) => Path.Combine(dir, "device_identity.json");
    private static string GetKeyPath(string dir) => Path.Combine(dir, "device_identity.dat");

    /// <summary>
    /// 從指定目錄載入既有身分，若不存在或損壞則自動建立新身分並以 DPAPI 加密儲存。
    /// </summary>
    public static DeviceIdentity LoadOrCreate(string dir, string? fallbackDeviceId = null, string? deviceName = null)
    {
        Directory.CreateDirectory(dir);
        var metaPath = GetMetaPath(dir);
        var keyPath = GetKeyPath(dir);

        if (File.Exists(metaPath) && File.Exists(keyPath))
        {
            try
            {
                var metaJson = File.ReadAllText(metaPath);
                var meta = JsonSerializer.Deserialize<IdentityMeta>(metaJson, AppConfig.JsonOptions);
                var encKey = File.ReadAllBytes(keyPath);

                byte[] rawKey;
                try
                {
                    rawKey = ProtectedData.Unprotect(encKey, null, DataProtectionScope.CurrentUser);
                }
                catch
                {
                    // 若以 SYSTEM 服務或另一使用者執行，嘗試 LocalMachine
                    rawKey = ProtectedData.Unprotect(encKey, null, DataProtectionScope.LocalMachine);
                }

                var ecdsa = ECDsa.Create();
                ecdsa.ImportPkcs8PrivateKey(rawKey, out _);
                CryptographicOperations.ZeroMemory(rawKey);

                if (meta != null && !string.IsNullOrWhiteSpace(meta.DeviceId))
                {
                    string idToUse = !string.IsNullOrWhiteSpace(fallbackDeviceId) ? fallbackDeviceId : meta.DeviceId;
                    string nameToUse = !string.IsNullOrWhiteSpace(deviceName) ? deviceName : meta.DeviceName;
                    return new DeviceIdentity(idToUse, nameToUse, ecdsa);
                }
            }
            catch (Exception ex)
            {
                Log.Error("載入本機裝置金鑰身分失敗，將重新產生新身分", ex);
            }
        }

        // 首次產生新身分
        var newEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string id = !string.IsNullOrWhiteSpace(fallbackDeviceId) ? fallbackDeviceId : AppConfig.GenerateDeviceId();
        string name = !string.IsNullOrWhiteSpace(deviceName) ? deviceName : Environment.MachineName;

        var identity = new DeviceIdentity(id, name, newEcdsa);
        identity.Save(dir);
        return identity;
    }

    /// <summary>
    /// 將公私鑰身分安全加密寫入磁碟（私鑰以 DPAPI 防護）。
    /// </summary>
    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        var metaPath = GetMetaPath(dir);
        var keyPath = GetKeyPath(dir);

        var meta = new IdentityMeta
        {
            DeviceId = DeviceId,
            DeviceName = DeviceName,
            CreatedAt = DateTime.UtcNow
        };
        File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, AppConfig.JsonOptions));

        var pkcs8 = _ecdsa.ExportPkcs8PrivateKey();
        try
        {
            byte[] encKey;
            try
            {
                encKey = ProtectedData.Protect(pkcs8, null, DataProtectionScope.CurrentUser);
            }
            catch
            {
                encKey = ProtectedData.Protect(pkcs8, null, DataProtectionScope.LocalMachine);
            }
            File.WriteAllBytes(keyPath, encKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }
    }

    /// <summary>
    /// 重新產生裝置身分金鑰（金鑰遺失或重新註冊流程）。
    /// </summary>
    public void Regenerate(string dir, string? newDeviceId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ecdsa.Dispose();
        _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        PublicKeySpki = _ecdsa.ExportSubjectPublicKeyInfo();
        if (!string.IsNullOrWhiteSpace(newDeviceId))
        {
            DeviceId = newDeviceId;
        }
        Save(dir);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ecdsa.Dispose();
    }
}
