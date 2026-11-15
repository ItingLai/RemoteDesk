using System.Security.Cryptography;
using System.Text;
using RemoteDesk.Common;
using Xunit;

namespace RemoteDesk.Tests;

public class DeviceIdentityTests : IDisposable
{
    private readonly string _tempDir;

    public DeviceIdentityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RemoteDeskTests_Identity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Generate_ProducesValid9DigitId_AndP256Key()
    {
        using var identity = DeviceIdentity.LoadOrCreate(_tempDir, "123456789", "TestDevice");

        Assert.Equal("123456789", identity.DeviceId);
        Assert.Equal("TestDevice", identity.DeviceName);
        Assert.NotNull(identity.PublicKeySpki);
        Assert.NotEmpty(identity.PublicKeySpki);
        Assert.False(string.IsNullOrEmpty(identity.PublicKeyBase64));
        Assert.False(string.IsNullOrEmpty(identity.PublicKeyFingerprint));

        // 指紋應為冒號分隔的 SHA-256 十六進位字串 (32 bytes -> 31 冒號 + 64 字元 = 95 字元)
        Assert.Equal(95, identity.PublicKeyFingerprint.Length);
        Assert.Contains(":", identity.PublicKeyFingerprint);
    }

    [Fact]
    public void SignAndVerify_ValidSignature_ReturnsTrue()
    {
        using var identity = DeviceIdentity.LoadOrCreate(_tempDir);
        byte[] payload = Encoding.UTF8.GetBytes("RemoteDesk-Challenge-Data-12345");

        byte[] sig = identity.SignData(payload);
        Assert.NotNull(sig);
        Assert.NotEmpty(sig);

        bool isValid = DeviceIdentity.VerifySignature(payload, sig, identity.PublicKeySpki);
        Assert.True(isValid);
    }

    [Fact]
    public void SignAndVerify_TamperedData_ReturnsFalse()
    {
        using var identity = DeviceIdentity.LoadOrCreate(_tempDir);
        byte[] payload = Encoding.UTF8.GetBytes("Original Payload");
        byte[] tampered = Encoding.UTF8.GetBytes("Tampered Payload");

        byte[] sig = identity.SignData(payload);
        bool isValid = DeviceIdentity.VerifySignature(tampered, sig, identity.PublicKeySpki);
        Assert.False(isValid);
    }

    [Fact]
    public void SignAndVerify_WrongPublicKey_ReturnsFalse()
    {
        using var id1 = DeviceIdentity.LoadOrCreate(Path.Combine(_tempDir, "d1"));
        using var id2 = DeviceIdentity.LoadOrCreate(Path.Combine(_tempDir, "d2"));

        byte[] payload = Encoding.UTF8.GetBytes("Signature payload");
        byte[] sig1 = id1.SignData(payload);

        bool verifiedBy2 = DeviceIdentity.VerifySignature(payload, sig1, id2.PublicKeySpki);
        Assert.False(verifiedBy2);
    }

    [Fact]
    public void SaveAndLoad_PreservesIdAndKey_WithDpapi()
    {
        string id, name, fp;
        byte[] originalSpki;

        using (var id1 = DeviceIdentity.LoadOrCreate(_tempDir, "999888777", "PersistentPC"))
        {
            id = id1.DeviceId;
            name = id1.DeviceName;
            fp = id1.PublicKeyFingerprint;
            originalSpki = id1.PublicKeySpki;
        }

        // 重新從磁碟載入
        using (var id2 = DeviceIdentity.LoadOrCreate(_tempDir))
        {
            Assert.Equal(id, id2.DeviceId);
            Assert.Equal(name, id2.DeviceName);
            Assert.Equal(fp, id2.PublicKeyFingerprint);
            Assert.Equal(originalSpki, id2.PublicKeySpki);

            // 測試以載入的私鑰簽章與驗證
            byte[] data = "VerificationData"u8.ToArray();
            byte[] sig = id2.SignData(data);
            Assert.True(DeviceIdentity.VerifySignature(data, sig, id2.PublicKeySpki));
        }
    }

    [Fact]
    public void Regenerate_ChangesKeyAndFingerprint()
    {
        using var identity = DeviceIdentity.LoadOrCreate(_tempDir, "111222333", "DeviceA");
        var oldFp = identity.PublicKeyFingerprint;
        var oldSpki = identity.PublicKeySpki;

        identity.Regenerate(_tempDir, "444555666");

        Assert.Equal("444555666", identity.DeviceId);
        Assert.NotEqual(oldFp, identity.PublicKeyFingerprint);
        Assert.False(oldSpki.SequenceEqual(identity.PublicKeySpki));
    }
}
