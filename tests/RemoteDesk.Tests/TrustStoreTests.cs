using RemoteDesk.Common;
using Xunit;

namespace RemoteDesk.Tests;

public class TrustStoreTests : IDisposable
{
    private readonly string _tempFile;

    public TrustStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), "RemoteDeskTests_Trust_" + Guid.NewGuid().ToString("N") + ".json");
    }

    public void Dispose()
    {
        try { if (File.Exists(_tempFile)) File.Delete(_tempFile); } catch { }
    }

    [Fact]
    public void AddOrUpdate_StoresDeviceWithFingerprint()
    {
        var store = new TrustStore(_tempFile);
        byte[] spki = "DummySpkiKeyBytesForTesting12345678"u8.ToArray();

        store.AddOrUpdate("111222333", "Controller-01", spki, autoAccept: true);

        var list = store.GetAll();
        Assert.Single(list);
        var dev = list[0];
        Assert.Equal("111222333", dev.TrustedDeviceId);
        Assert.Equal("Controller-01", dev.TrustedDeviceName);
        Assert.Equal(Convert.ToBase64String(spki), dev.TrustedPublicKey);
        Assert.True(dev.AutoAcceptEnabled);
        Assert.Equal(TrustStatus.Active, dev.TrustStatus);
        Assert.False(string.IsNullOrEmpty(dev.TrustedPublicKeyFingerprint));
    }

    [Fact]
    public void IsTrusted_MatchingKey_ReturnsTrue()
    {
        var store = new TrustStore(_tempFile);
        byte[] spki = "RealDevicePublicKey1234567890"u8.ToArray();
        store.AddOrUpdate("999888777", "WorkPC", spki);

        bool isTrusted = store.IsTrusted("999888777", spki, out var matched, out bool mismatch);

        Assert.True(isTrusted);
        Assert.NotNull(matched);
        Assert.False(mismatch);
        Assert.Equal("WorkPC", matched.TrustedDeviceName);
    }

    [Fact]
    public void IsTrusted_KeyMismatch_ReturnsFalseAndSetsMismatchFlag()
    {
        var store = new TrustStore(_tempFile);
        byte[] originalSpki = "OriginalKeyBytes12345678"u8.ToArray();
        byte[] tamperedSpki = "TamperedKeyBytes87654321"u8.ToArray();

        store.AddOrUpdate("999888777", "WorkPC", originalSpki);

        // 相同的 Device ID 但公鑰不同 (公鑰變更或偽冒)
        bool isTrusted = store.IsTrusted("999888777", tamperedSpki, out var matched, out bool mismatch);

        Assert.False(isTrusted);
        Assert.NotNull(matched);
        Assert.True(mismatch, "公鑰不一致時必須標記 keyMismatch 為 true");
    }

    [Fact]
    public void IsTrusted_UnknownDevice_ReturnsFalse()
    {
        var store = new TrustStore(_tempFile);
        byte[] spki = "RandomKeyBytes"u8.ToArray();

        bool isTrusted = store.IsTrusted("000000000", spki, out var matched, out bool mismatch);

        Assert.False(isTrusted);
        Assert.Null(matched);
        Assert.False(mismatch);
    }

    [Fact]
    public void RevokeDevice_ChangesStatusToRevoked_ReturnsFalseForIsTrusted()
    {
        var store = new TrustStore(_tempFile);
        byte[] spki = "DeviceKeyToRevoke"u8.ToArray();
        store.AddOrUpdate("123123123", "RevokeTarget", spki);

        Assert.True(store.RevokeDevice("123123123"));

        bool isTrusted = store.IsTrusted("123123123", spki, out var matched, out bool mismatch);
        Assert.False(isTrusted);
        Assert.NotNull(matched);
        Assert.Equal(TrustStatus.Revoked, matched.TrustStatus);
        Assert.False(mismatch);
    }

    [Fact]
    public void RevokeAll_RevokesAllDevices()
    {
        var store = new TrustStore(_tempFile);
        byte[] k1 = "Key1"u8.ToArray();
        byte[] k2 = "Key2"u8.ToArray();

        store.AddOrUpdate("111", "Dev1", k1);
        store.AddOrUpdate("222", "Dev2", k2);

        store.RevokeAll();

        Assert.Empty(store.GetActive());
        Assert.Equal(2, store.GetAll().Count);
        Assert.All(store.GetAll(), d => Assert.Equal(TrustStatus.Revoked, d.TrustStatus));
    }

    [Fact]
    public void RemoveDevice_RemovesFromList()
    {
        var store = new TrustStore(_tempFile);
        store.AddOrUpdate("333", "Dev3", "Key3"u8.ToArray());

        Assert.True(store.RemoveDevice("333"));
        Assert.Empty(store.GetAll());
    }

    [Fact]
    public void Persistence_LoadsSavedDevicesAccurately()
    {
        byte[] spki = "PersistentPublicKeyBytes123"u8.ToArray();
        var store1 = new TrustStore(_tempFile);
        store1.AddOrUpdate("777888999", "SavedDev", spki, autoAccept: false);

        var store2 = new TrustStore(_tempFile);
        var list = store2.GetAll();

        Assert.Single(list);
        Assert.Equal("777888999", list[0].TrustedDeviceId);
        Assert.Equal("SavedDev", list[0].TrustedDeviceName);
        Assert.False(list[0].AutoAcceptEnabled);
    }
}
