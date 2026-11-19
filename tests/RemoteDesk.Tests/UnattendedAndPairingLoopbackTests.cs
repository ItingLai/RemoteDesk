using System.Net;
using System.Net.Sockets;
using RemoteDesk.Common;
using RemoteDesk.Host;
using RemoteDesk.Net;
using RemoteDesk.Viewer;
using Xunit;

namespace RemoteDesk.Tests;

public class UnattendedAndPairingLoopbackTests : IDisposable
{
    private readonly string _tempDir;

    public UnattendedAndPairingLoopbackTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RemoteDeskTests_Unattended_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static int FindFreeTcpPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class TrackingHostUi : IHostUi
    {
        public bool AcceptResponse = true;
        public bool TrustResponse = false;
        public int PromptCount = 0;
        public bool LastKeyMismatch = false;

        public Task<bool> ConfirmAccessAsync(string viewerName, string viewerId, string address, string code, CancellationToken ct)
        {
            PromptCount++;
            return Task.FromResult(AcceptResponse);
        }

        public Task<AccessConfirmationResult> ConfirmAccessExtendedAsync(
            string viewerName, string viewerId, string address, string code, string fingerprint, bool keyMismatch, CancellationToken ct)
        {
            PromptCount++;
            LastKeyMismatch = keyMismatch;
            return Task.FromResult(new AccessConfirmationResult(AcceptResponse, TrustResponse));
        }

        public void SessionStarted(HostSession session) { }
        public void SessionEnded(HostSession session) { }
        public void SetClipboardText(string text) { }
    }

    [Fact]
    public async Task FirstConnection_ManualAcceptWithoutTrust_DoesNotAddToTrustStore()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "host");
        var viewerDir = Path.Combine(_tempDir, "viewer");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "999000111", "HostPC");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "111000999", "ViewerPC");

        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            UnattendedAccessEnabled = false
        };

        var trustStore = new TrustStore(Path.Combine(hostDir, "trusted.json"));
        var ui = new TrackingHostUi { AcceptResponse = true, TrustResponse = false };

        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21121);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, trustStore, hostId);
        hostServer.Start();

        var viewerConfig = new ViewerConfig { ViewerId = viewerId.DeviceId };
        using var viewerDiscovery = new LanDiscovery(viewerId.DeviceId, port: 21122);
        using var viewerSession = new ViewerSession($"127.0.0.1:{port}", viewerConfig, viewerDiscovery, null, viewerId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await viewerSession.ConnectAsync(password: null, cts.Token);

        Assert.True(viewerSession.IsConnected);
        Assert.Equal(1, ui.PromptCount);
        Assert.Empty(trustStore.GetAll()); // 未勾選信任，不可加入 TrustStore
    }

    [Fact]
    public async Task FirstConnection_ManualAcceptWithTrust_AddsToTrustStore()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "host2");
        var viewerDir = Path.Combine(_tempDir, "viewer2");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "888000111", "HostPC2");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "222000888", "ViewerPC2");

        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            UnattendedAccessEnabled = false
        };

        var trustStore = new TrustStore(Path.Combine(hostDir, "trusted.json"));
        var ui = new TrackingHostUi { AcceptResponse = true, TrustResponse = true }; // 選擇信任

        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21123);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, trustStore, hostId);
        hostServer.Start();

        var viewerConfig = new ViewerConfig { ViewerId = viewerId.DeviceId };
        using var viewerDiscovery = new LanDiscovery(viewerId.DeviceId, port: 21124);
        using var viewerSession = new ViewerSession($"127.0.0.1:{port}", viewerConfig, viewerDiscovery, null, viewerId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await viewerSession.ConnectAsync(password: null, cts.Token);

        Assert.True(viewerSession.IsConnected);
        Assert.Equal(1, ui.PromptCount);

        // 已信任此裝置，驗證 TrustStore 包含該裝置公鑰與 ID
        var list = trustStore.GetAll();
        Assert.Single(list);
        Assert.Equal(viewerId.DeviceId, list[0].TrustedDeviceId);
        Assert.Equal(viewerId.PublicKeyBase64, list[0].TrustedPublicKey);
        Assert.Equal(TrustStatus.Active, list[0].TrustStatus);
    }

    [Fact]
    public async Task SecondConnection_WithUnattendedAccessOn_AutoAcceptsWithoutPrompt()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "host3");
        var viewerDir = Path.Combine(_tempDir, "viewer3");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "777000111", "HostPC3");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "333000777", "ViewerPC3");

        var trustStore = new TrustStore(Path.Combine(hostDir, "trusted.json"));
        // 預先完成配對加入 TrustStore
        trustStore.AddOrUpdate(viewerId.DeviceId, viewerId.DeviceName, viewerId.PublicKeySpki, autoAccept: true);

        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            UnattendedAccessEnabled = true, // 開啟無人值守
            AutoAcceptTrustedDevices = true
        };

        var ui = new TrackingHostUi(); // 若有彈窗會遞增 PromptCount

        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21125);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, trustStore, hostId);
        hostServer.Start();

        var viewerConfig = new ViewerConfig { ViewerId = viewerId.DeviceId };
        using var viewerDiscovery = new LanDiscovery(viewerId.DeviceId, port: 21126);
        using var viewerSession = new ViewerSession($"127.0.0.1:{port}", viewerConfig, viewerDiscovery, null, viewerId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await viewerSession.ConnectAsync(password: null, cts.Token);

        Assert.True(viewerSession.IsConnected);
        // 重要驗收：無人值守自動放行，PromptCount 必須為 0！
        Assert.Equal(0, ui.PromptCount);
    }

    [Fact]
    public async Task SecondConnection_WithUnattendedAccessOff_RequiresManualPrompt()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "host4");
        var viewerDir = Path.Combine(_tempDir, "viewer4");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "666000111", "HostPC4");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "444000666", "ViewerPC4");

        var trustStore = new TrustStore(Path.Combine(hostDir, "trusted.json"));
        trustStore.AddOrUpdate(viewerId.DeviceId, viewerId.DeviceName, viewerId.PublicKeySpki, autoAccept: true);

        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            UnattendedAccessEnabled = false // 關閉無人值守
        };

        var ui = new TrackingHostUi { AcceptResponse = true };

        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21127);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, trustStore, hostId);
        hostServer.Start();

        var viewerConfig = new ViewerConfig { ViewerId = viewerId.DeviceId };
        using var viewerDiscovery = new LanDiscovery(viewerId.DeviceId, port: 21128);
        using var viewerSession = new ViewerSession($"127.0.0.1:{port}", viewerConfig, viewerDiscovery, null, viewerId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await viewerSession.ConnectAsync(password: null, cts.Token);

        Assert.True(viewerSession.IsConnected);
        // 無人值守關閉時，即使已信任，仍需手動確認
        Assert.Equal(1, ui.PromptCount);
    }

    [Fact]
    public async Task Connection_WithKeyMismatch_FailsAutoAcceptAndFlagsMismatch()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "host5");
        var viewerDir = Path.Combine(_tempDir, "viewer5");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "555000111", "HostPC5");
        using var originalViewerId = DeviceIdentity.LoadOrCreate(viewerDir, "555000555", "ViewerPC5");

        var trustStore = new TrustStore(Path.Combine(hostDir, "trusted.json"));
        trustStore.AddOrUpdate(originalViewerId.DeviceId, originalViewerId.DeviceName, originalViewerId.PublicKeySpki, autoAccept: true);

        // 模擬控制端公鑰變更（重灌或偽冒）
        var rogueDir = Path.Combine(_tempDir, "rogue");
        using var rogueViewerId = DeviceIdentity.LoadOrCreate(rogueDir, originalViewerId.DeviceId, "RogueViewer");
        rogueViewerId.Regenerate(rogueDir, originalViewerId.DeviceId); // 重新產生金鑰

        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            UnattendedAccessEnabled = true // 即使無人值守開啟
        };

        var ui = new TrackingHostUi { AcceptResponse = true };

        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21129);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, trustStore, hostId);
        hostServer.Start();

        var viewerConfig = new ViewerConfig { ViewerId = rogueViewerId.DeviceId };
        using var viewerDiscovery = new LanDiscovery(rogueViewerId.DeviceId, port: 21130);
        using var viewerSession = new ViewerSession($"127.0.0.1:{port}", viewerConfig, viewerDiscovery, null, rogueViewerId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await viewerSession.ConnectAsync(password: null, cts.Token);

        // 核心安全規格：公鑰不同時嚴禁自動放行，必須觸發彈窗且標記 keyMismatch
        Assert.Equal(1, ui.PromptCount);
        Assert.True(ui.LastKeyMismatch, "公鑰變更時必須向 UI 傳送 keyMismatch = true");
    }

    [Fact]
    public async Task UnattendedAccess_UntrustedWithoutPassword_ThrowsNeedPassword()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "host6");
        var viewerDir = Path.Combine(_tempDir, "viewer6");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "444000111", "HostPC6");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "666000444", "ViewerPC6");

        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            UnattendedAccessEnabled = true
        };
        hostConfig.SetPassword("SecretUnattendedPass123!");

        var trustStore = new TrustStore(Path.Combine(hostDir, "trusted.json"));
        var ui = new TrackingHostUi();

        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21131);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, trustStore, hostId);
        hostServer.Start();

        var viewerConfig = new ViewerConfig { ViewerId = viewerId.DeviceId };
        using var viewerDiscovery = new LanDiscovery(viewerId.DeviceId, port: 21132);
        using var viewerSession = new ViewerSession($"127.0.0.1:{port}", viewerConfig, viewerDiscovery, null, viewerId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // 未提供密碼且未配對：雙軌並行模式下主機彈出確認視窗，同時回覆密碼等待並拋出 NeedPasswordException
        var ex = await Assert.ThrowsAsync<NeedPasswordException>(() => viewerSession.ConnectAsync(password: null, cts.Token));
        Assert.NotNull(ex);
        Assert.Equal(1, ui.PromptCount); // 雙軌並行模式下，被控端自動彈出確認視窗，同時控制端提示輸入密碼
    }

    [Fact]
    public async Task UnattendedAccess_WithPassword_ConnectsWithoutInteractivePrompt()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "host7");
        var viewerDir = Path.Combine(_tempDir, "viewer7");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "333000111", "HostPC7");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "777000333", "ViewerPC7");

        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            UnattendedAccessEnabled = true
        };
        hostConfig.SetPassword("SecretUnattendedPass123!");

        var trustStore = new TrustStore(Path.Combine(hostDir, "trusted.json"));
        var ui = new TrackingHostUi();

        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21133);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, trustStore, hostId);
        hostServer.Start();

        var viewerConfig = new ViewerConfig { ViewerId = viewerId.DeviceId };
        using var viewerDiscovery = new LanDiscovery(viewerId.DeviceId, port: 21134);
        using var viewerSession = new ViewerSession($"127.0.0.1:{port}", viewerConfig, viewerDiscovery, null, viewerId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await viewerSession.ConnectAsync("SecretUnattendedPass123!", cts.Token);

        Assert.True(viewerSession.IsConnected);
        Assert.Equal(0, ui.PromptCount); // 無人值守密碼驗證通過，被控端無須彈窗
    }

    [Fact]
    public async Task UnattendedAccess_WithWrongPassword_ThrowsException()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "host8");
        var viewerDir = Path.Combine(_tempDir, "viewer8");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "222000111", "HostPC8");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "888000222", "ViewerPC8");

        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            UnattendedAccessEnabled = true
        };
        hostConfig.SetPassword("CorrectPass123!");

        var trustStore = new TrustStore(Path.Combine(hostDir, "trusted.json"));
        var ui = new TrackingHostUi();

        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21135);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, trustStore, hostId);
        hostServer.Start();

        var viewerConfig = new ViewerConfig { ViewerId = viewerId.DeviceId };
        using var viewerDiscovery = new LanDiscovery(viewerId.DeviceId, port: 21136);
        using var viewerSession = new ViewerSession($"127.0.0.1:{port}", viewerConfig, viewerDiscovery, null, viewerId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<NeedPasswordException>(() => viewerSession.ConnectAsync("WrongPass999!", cts.Token));
        Assert.Contains("連線密碼錯誤", ex.Message);
        Assert.False(viewerSession.IsConnected);
    }

    [Fact]
    public async Task UnattendedAccess_ViewerRememberPassword_UsesSavedPasswordSeamlessly()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "host9");
        var viewerDir = Path.Combine(_tempDir, "viewer9");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "111000111", "HostPC9");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "999000111", "ViewerPC9");

        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            UnattendedAccessEnabled = true
        };
        string unattendedPassword = "SuperSecurePassword888!";
        hostConfig.SetPassword(unattendedPassword);

        var trustStore = new TrustStore(Path.Combine(hostDir, "trusted.json"));
        var ui = new TrackingHostUi();

        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21137);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, trustStore, hostId);
        hostServer.Start();

        var viewerConfig = new ViewerConfig { ViewerId = viewerId.DeviceId };

        // 測試控制端「記住密碼」（使用 Windows DPAPI 本機加密）
        viewerConfig.RememberPassword(hostId.DeviceId, unattendedPassword);
        string? retrievedPassword = viewerConfig.GetSavedPassword(hostId.DeviceId);
        Assert.Equal(unattendedPassword, retrievedPassword);

        using var viewerDiscovery = new LanDiscovery(viewerId.DeviceId, port: 21138);
        using var viewerSession = new ViewerSession($"127.0.0.1:{port}", viewerConfig, viewerDiscovery, null, viewerId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // 使用控制端已儲存的 DPAPI 密碼直接連線
        await viewerSession.ConnectAsync(retrievedPassword, cts.Token);

        Assert.True(viewerSession.IsConnected);
        Assert.Equal(0, ui.PromptCount);
    }

    private sealed class FuncHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public FuncHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }

    [Fact]
    public async Task ConnectAsync_WhenLanDiscoveryFails_ResolvesViaSignalingServer()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "host10");
        var viewerDir = Path.Combine(_tempDir, "viewer10");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "123000789", "HostPC10");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "789000123", "ViewerPC10");

        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            UnattendedAccessEnabled = true
        };
        string password = "TestSignalingPass!";
        hostConfig.SetPassword(password);

        var trustStore = new TrustStore(Path.Combine(hostDir, "trusted.json"));
        var ui = new TrackingHostUi();

        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21139);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, trustStore, hostId);
        hostServer.Start();

        // 模擬 Viewer 的 LanDiscovery 在不同 UDP port，區網廣播找不到 Host
        var viewerConfig = new ViewerConfig { ViewerId = viewerId.DeviceId };
        using var viewerDiscovery = new LanDiscovery(viewerId.DeviceId, port: 21140);

        // 模擬 SignalingClient 回傳 Host 的 IP 與 Port
        var mockHttpHandler = new FuncHttpMessageHandler(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains($"/api/devices/{hostId.DeviceId}"))
            {
                var json = $$"""
                {
                    "success": true,
                    "deviceId": "{{hostId.DeviceId}}",
                    "deviceName": "{{hostId.DeviceName}}",
                    "publicKey": "{{hostId.PublicKeyBase64}}",
                    "publicIp": "127.0.0.1",
                    "tcpPort": {{port}},
                    "localIps": ["127.0.0.1"],
                    "onlineStatus": true
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var mockHttp = new HttpClient(mockHttpHandler);
        using var signalingClient = new SignalingClient("https://mock-signaling.local", viewerId, mockHttp);

        // 傳入 9 位數 ID (不傳 IP)，並帶入 signalingClient
        using var viewerSession = new ViewerSession(hostId.DeviceId, viewerConfig, viewerDiscovery, null, viewerId, signalingClient);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await viewerSession.ConnectAsync(password, cts.Token);

        Assert.True(viewerSession.IsConnected);
        Assert.Equal(hostId.DeviceId, viewerSession.HostDeviceId);
    }

    [Fact]
    public async Task ConnectByDeviceId_WhenRemoteLocalIpCollidesWithSelfOrWrongHost_FallsBackToWanInsteadOfConnectingToSelf()
    {
        int wrongPort = FindFreeTcpPort();
        var wrongDir = Path.Combine(_tempDir, "wrongHostSameIp");
        var viewerDir = Path.Combine(_tempDir, "viewerSameIp");

        // 本機自己（或同網段他機）於 wrongPort 監聽，模擬遠端 ID 與本機剛好擁有相同內網 IP 時誤連本機之衝突情境
        using var wrongSelfId = DeviceIdentity.LoadOrCreate(wrongDir, "111222333", "SelfOrWrongHost");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "111222333", "SelfViewer");
        const string intendedRemoteId = "555666777";

        var selfHostConfig = new HostConfig
        {
            DeviceId = wrongSelfId.DeviceId,
            DeviceName = wrongSelfId.DeviceName,
            TcpPort = wrongPort,
            HostEnabled = true,
            AllowInteractive = false,
            UnattendedAccessEnabled = true
        };
        var trustStore = new TrustStore(Path.Combine(wrongDir, "trusted.json"));
        using var hostDiscovery = new LanDiscovery(wrongSelfId.DeviceId, port: FindFreeTcpPort());
        using var selfHostServer = new HostServer(selfHostConfig, hostDiscovery, new TrackingHostUi(), trustStore, wrongSelfId);
        selfHostServer.Start();

        var viewerConfig = new ViewerConfig { ViewerId = viewerId.DeviceId };
        using var viewerDiscovery = new LanDiscovery(viewerId.DeviceId, port: FindFreeTcpPort());

        string? ownLocalIp = LanDiscovery.LocalIPv4().Select(x => x.ip.ToString()).FirstOrDefault();
        bool sessionCreateAttempted = false;
        var localIpsJson = ownLocalIp != null ? $"[\"{ownLocalIp}\", \"127.0.0.1\"]" : "[\"127.0.0.1\"]";

        var mockHttpHandler = new FuncHttpMessageHandler(req =>
        {
            if (req.RequestUri!.PathAndQuery.Contains($"/api/devices/{intendedRemoteId}"))
            {
                var json = $$"""
                {
                    "success": true,
                    "deviceId": "{{intendedRemoteId}}",
                    "deviceName": "RemoteTargetPC",
                    "publicKey": "{{wrongSelfId.PublicKeyBase64}}",
                    "publicIp": "203.0.113.10",
                    "tcpPort": {{wrongPort}},
                    "localIps": {{localIpsJson}},
                    "onlineStatus": true
                }
                """;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            }
            if (req.RequestUri!.PathAndQuery.Contains("/api/sessions"))
            {
                sessionCreateAttempted = true;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var mockHttp = new HttpClient(mockHttpHandler);
        using var signalingClient = new SignalingClient("https://mock-signaling.local", viewerId, mockHttp);
        using var viewerSession = new ViewerSession(intendedRemoteId, viewerConfig, viewerDiscovery, wrongSelfId.DeviceId, viewerId, signalingClient);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => viewerSession.ConnectAsync(null, cts.Token));

        Assert.DoesNotContain("禁止連線到本機自己的 IP 或 ID", ex.Message);
        Assert.True(sessionCreateAttempted, "應略過撞名之內網 IP 並改走外網 Signaling / P2P 流程");
    }

    [Fact]
    public async Task DualParallelAuth_HostAcceptsFirst_ConnectsSuccessfully()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "hostDual1");
        var viewerDir = Path.Combine(_tempDir, "viewerDual1");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "999888111", "HostDual1");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "111888999", "ViewerDual1");

        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            UnattendedAccessEnabled = true
        };
        hostConfig.SetPassword("DualPassWord123!");

        var trustStore = new TrustStore(Path.Combine(hostDir, "trusted.json"));
        var ui = new TrackingHostUi { AcceptResponse = true, TrustResponse = false };

        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21133);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, trustStore, hostId);
        hostServer.Start();

        var viewerConfig = new ViewerConfig { ViewerId = viewerId.DeviceId };
        using var viewerDiscovery = new LanDiscovery(viewerId.DeviceId, port: 21134);
        using var viewerSession = new ViewerSession($"127.0.0.1:{port}", viewerConfig, viewerDiscovery, null, viewerId);

        bool dialogClosed = false;
        viewerSession.PasswordPromptHandler = ctx =>
        {
            ctx.CloseDialogOnRemoteAccepted = () => { dialogClosed = true; };
            return Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await viewerSession.ConnectAsync(password: null, cts.Token);

        Assert.True(viewerSession.IsConnected);
        Assert.Equal(1, ui.PromptCount); // 被控端自動彈出確認
        Assert.True(dialogClosed); // 控制端密碼對話盒因遠端接受而自動關閉
    }

    private sealed class DelayedHostUi : IHostUi
    {
        public TaskCompletionSource<AccessConfirmationResult> Tcs = new();
        public int PromptCount = 0;

        public Task<bool> ConfirmAccessAsync(string viewerName, string viewerId, string address, string code, CancellationToken ct)
        {
            PromptCount++;
            return Task.FromResult(true);
        }

        public Task<AccessConfirmationResult> ConfirmAccessExtendedAsync(
            string viewerName, string viewerId, string address, string code, string fingerprint, bool keyMismatch, CancellationToken ct)
        {
            PromptCount++;
            ct.Register(() => Tcs.TrySetCanceled());
            return Tcs.Task;
        }

        public void SessionStarted(HostSession session) { }
        public void SessionEnded(HostSession session) { }
        public void SetClipboardText(string text) { }
    }

    [Fact]
    public async Task DualParallelAuth_ViewerSubmitsPassword_ConnectsSuccessfully()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "hostDual2");
        var viewerDir = Path.Combine(_tempDir, "viewerDual2");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "999888222", "HostDual2");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "222888999", "ViewerDual2");

        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            UnattendedAccessEnabled = true
        };
        hostConfig.SetPassword("DualPassWord456!");

        var trustStore = new TrustStore(Path.Combine(hostDir, "trusted.json"));
        // 被控端彈出確認視窗，但使用者尚未點選 (模擬無人值守或尚未操作)
        var ui = new DelayedHostUi();

        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21135);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, trustStore, hostId);
        hostServer.Start();

        var viewerConfig = new ViewerConfig { ViewerId = viewerId.DeviceId };
        using var viewerDiscovery = new LanDiscovery(viewerId.DeviceId, port: 21136);
        using var viewerSession = new ViewerSession($"127.0.0.1:{port}", viewerConfig, viewerDiscovery, null, viewerId);

        bool dialogClosed = false;
        viewerSession.PasswordPromptHandler = async ctx =>
        {
            ctx.CloseDialogOnRemoteAccepted = () => { dialogClosed = true; };
            // 控制端在對話盒中輸入密碼並點擊連線
            await ctx.SubmitPasswordAsync("DualPassWord456!", false);
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await viewerSession.ConnectAsync(password: null, cts.Token);

        Assert.True(viewerSession.IsConnected);
        Assert.Equal(1, ui.PromptCount); // 被控端有自動彈出確認
        Assert.True(dialogClosed); // 密碼驗證通過後自動關閉對話盒
    }

    [Fact]
    public async Task ClearedPassword_WithStaleViewerSavedPassword_SkipsPasswordAndPromptsHost()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "hostCleared1");
        var viewerDir = Path.Combine(_tempDir, "viewerCleared1");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "999777111", "HostCleared1");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "111777999", "ViewerCleared1");

        // 原先有設定密碼並開啟無人值守，之後清除密碼並關閉無人值守
        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            UnattendedAccessEnabled = true
        };
        hostConfig.SetPassword("OldPass123!");
        hostConfig.SetPassword(null);
        hostConfig.UnattendedAccessEnabled = false;

        var trustStore = new TrustStore(Path.Combine(hostDir, "trusted.json"));
        var ui = new TrackingHostUi { AcceptResponse = true, TrustResponse = false };

        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21139);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, trustStore, hostId);
        hostServer.Start();

        // 控制端仍保留舊密碼快取
        var viewerConfig = new ViewerConfig { ViewerId = viewerId.DeviceId };
        viewerConfig.RememberPassword(hostId.DeviceId, "OldPass123!");
        viewerConfig.RememberPassword($"127.0.0.1:{port}", "OldPass123!");

        bool passwordPrompted = false;
        using var viewerDiscovery = new LanDiscovery(viewerId.DeviceId, port: 21140);
        using var viewerSession = new ViewerSession($"127.0.0.1:{port}", viewerConfig, viewerDiscovery, null, viewerId);
        viewerSession.PasswordPromptHandler = _ =>
        {
            passwordPrompted = true;
            return Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await viewerSession.ConnectAsync("OldPass123!", cts.Token);

        Assert.True(viewerSession.IsConnected);
        Assert.Equal(1, ui.PromptCount); // 被控端必須跳出是否接受視窗
        Assert.False(passwordPrompted); // 控制端不得跳出密碼視窗
        Assert.True(viewerSession.ClearedStaleSavedPassword);
        Assert.Null(viewerConfig.GetSavedPassword(hostId.DeviceId));
        Assert.Null(viewerConfig.GetSavedPassword($"127.0.0.1:{port}"));
    }

    [Fact]
    public async Task ClearedPassword_WithStaleViewerSavedPassword_HostRejects_DoesNotThrowNeedPassword()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "hostCleared2");
        var viewerDir = Path.Combine(_tempDir, "viewerCleared2");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "999777222", "HostCleared2");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "222777999", "ViewerCleared2");

        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            UnattendedAccessEnabled = false
        };
        hostConfig.SetPassword("OldPass456!");
        hostConfig.SetPassword(null);

        var trustStore = new TrustStore(Path.Combine(hostDir, "trusted.json"));
        var ui = new TrackingHostUi { AcceptResponse = false }; // 被控端選擇拒絕

        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21141);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, trustStore, hostId);
        hostServer.Start();

        var viewerConfig = new ViewerConfig { ViewerId = viewerId.DeviceId };
        viewerConfig.RememberPassword($"127.0.0.1:{port}", "OldPass456!");

        using var viewerDiscovery = new LanDiscovery(viewerId.DeviceId, port: 21142);
        using var viewerSession = new ViewerSession($"127.0.0.1:{port}", viewerConfig, viewerDiscovery, null, viewerId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viewerSession.ConnectAsync("OldPass456!", cts.Token));
        Assert.IsNotType<NeedPasswordException>(ex); // 不得誤判為 NeedPasswordException 造成無限輸入密碼迴圈
        Assert.Equal(1, ui.PromptCount);
        Assert.Contains("拒絕", ex.Message);
        Assert.Null(viewerConfig.GetSavedPassword($"127.0.0.1:{port}"));
    }

    [Fact]
    public async Task ClearedPassword_LegacyPasswordAuthPacket_FallsBackToInteractivePrompt()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "hostCleared3");
        var viewerDir = Path.Combine(_tempDir, "viewerCleared3");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "999777333", "HostCleared3");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "333777999", "ViewerCleared3");

        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            UnattendedAccessEnabled = false
        };
        hostConfig.SetPassword(null);

        var ui = new TrackingHostUi { AcceptResponse = true };
        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21143);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, new TrustStore(Path.Combine(hostDir, "trusted.json")), hostId);
        hostServer.Start();

        // 模擬未更新的舊版控制端無視 Hello.HasPassword=false 仍送出 AuthMethod.Password
        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var (channel, hello) = await SecureChannel.ClientHandshakeAsync(tcp.GetStream(), viewerId.DeviceId, "LegacyViewer", cts.Token);
        Assert.False(hello.HasPassword);

        var staleKey = Crypto.DerivePasswordKey("LegacyCachedPass", hello.Salt, hello.Iterations);
        var proof = Crypto.PasswordProof(staleKey, channel.Transcript);
        await channel.SendAsync(new PacketWriter((byte)AuthMethod.Password).Bytes(proof).ToArray(), cts.Token);

        var resp = await channel.ReceiveAsync(cts.Token);
        Assert.NotEmpty(resp);
        Assert.Equal(0x00, resp[0]); // 被控端自動降級為跳窗核准，按下接受後成功連線
        Assert.Equal(1, ui.PromptCount);
        channel.Dispose();
    }

    [Fact]
    public async Task UnattendedOff_WithStoredPasswordAndTrustedDevice_StillPromptsHost()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "hostUnattendedOff1");
        var viewerDir = Path.Combine(_tempDir, "viewerUnattendedOff1");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "999777444", "HostUnattendedOff1");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "444777999", "ViewerUnattendedOff1");

        var trustStore = new TrustStore(Path.Combine(hostDir, "trusted.json"));
        trustStore.AddOrUpdate(viewerId.DeviceId, viewerId.DeviceName, viewerId.PublicKeySpki, autoAccept: true);

        // 先設定了密碼，之後僅將無人值守總開關關閉（尚未按清除密碼）
        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            AutoAcceptTrustedDevices = true,
            UnattendedAccessEnabled = false
        };
        hostConfig.SetPassword("StoredUnattendedPass999!");
        Assert.True(hostConfig.HasPassword);

        var ui = new TrackingHostUi { AcceptResponse = false }; // 被控端拒絕
        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21144);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, trustStore, hostId);
        hostServer.Start();

        var viewerConfig = new ViewerConfig { ViewerId = viewerId.DeviceId };
        viewerConfig.RememberPassword($"127.0.0.1:{port}", "StoredUnattendedPass999!");

        bool passwordPrompted = false;
        using var viewerDiscovery = new LanDiscovery(viewerId.DeviceId, port: 21145);
        using var viewerSession = new ViewerSession($"127.0.0.1:{port}", viewerConfig, viewerDiscovery, null, viewerId);
        viewerSession.PasswordPromptHandler = _ =>
        {
            passwordPrompted = true;
            return Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // 即使曾加入可信清單且控制端送入原本正確的密碼，關閉無人值守後不得直接放行
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            viewerSession.ConnectAsync("StoredUnattendedPass999!", cts.Token));
        Assert.IsNotType<NeedPasswordException>(ex);
        Assert.Equal(1, ui.PromptCount);
        Assert.False(passwordPrompted);
        Assert.False(viewerSession.IsConnected);
    }

    [Fact]
    public async Task UnattendedOff_WithStoredPassword_LegacyPasswordPacket_StillPromptsHost()
    {
        int port = FindFreeTcpPort();
        var hostDir = Path.Combine(_tempDir, "hostUnattendedOff2");
        var viewerDir = Path.Combine(_tempDir, "viewerUnattendedOff2");

        using var hostId = DeviceIdentity.LoadOrCreate(hostDir, "999777555", "HostUnattendedOff2");
        using var viewerId = DeviceIdentity.LoadOrCreate(viewerDir, "555777999", "ViewerUnattendedOff2");

        var hostConfig = new HostConfig
        {
            DeviceId = hostId.DeviceId,
            DeviceName = hostId.DeviceName,
            TcpPort = port,
            HostEnabled = true,
            AllowInteractive = true,
            UnattendedAccessEnabled = false
        };
        hostConfig.SetPassword("StoredUnattendedPass888!");

        var ui = new TrackingHostUi { AcceptResponse = false }; // 被控端拒絕
        using var hostDiscovery = new LanDiscovery(hostId.DeviceId, port: 21146);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, ui, new TrustStore(Path.Combine(hostDir, "trusted.json")), hostId);
        hostServer.Start();

        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await tcp.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var (channel, hello) = await SecureChannel.ClientHandshakeAsync(tcp.GetStream(), viewerId.DeviceId, "LegacyViewer", cts.Token);
        Assert.False(hello.HasPassword); // 無人值守關閉時不得宣告密碼登入可用

        // 即使舊版控制端以本機密碼鹽嘗試 AuthMethod.Password，也必須交由主機彈窗核准
        var salt = Convert.FromBase64String(hostConfig.PasswordSalt!);
        var key = Crypto.DerivePasswordKey("StoredUnattendedPass888!", salt, hostConfig.PasswordIterations);
        var proof = Crypto.PasswordProof(key, channel.Transcript);
        await channel.SendAsync(new PacketWriter((byte)AuthMethod.Password).Bytes(proof).ToArray(), cts.Token);

        var resp = await channel.ReceiveAsync(cts.Token);
        Assert.NotEmpty(resp);
        Assert.Equal(0xFE, resp[0]); // 因被控端選擇拒絕而遭到拒絕，絕不可憑密碼直接放行
        Assert.Equal(1, ui.PromptCount);
        channel.Dispose();
    }
}
