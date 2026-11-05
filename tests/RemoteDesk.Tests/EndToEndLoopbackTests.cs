using System.Drawing;
using System.Net;
using System.Net.Sockets;
using RemoteDesk.Common;
using RemoteDesk.Host;
using RemoteDesk.Net;
using RemoteDesk.Viewer;
using Xunit;

namespace RemoteDesk.Tests;

public class EndToEndLoopbackTests
{
    private sealed class DummyHostUi : IHostUi
    {
        public bool ConfirmResponse = true;
        public HostSession? ActiveSession;

        public Task<bool> ConfirmAccessAsync(string viewerName, string viewerId, string address, string code, CancellationToken ct) =>
            Task.FromResult(ConfirmResponse);

        public void SessionStarted(HostSession session) => ActiveSession = session;
        public void SessionEnded(HostSession session) => ActiveSession = null;
        public void SetClipboardText(string text) { }
    }

    private static int FindFreeTcpPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task TestCompleteHostAndViewerSessionLoopback()
    {
        int testPort = FindFreeTcpPort();

        var hostConfig = new HostConfig
        {
            DeviceId = "987654321",
            DeviceName = "LoopbackTestHost",
            TcpPort = testPort,
            HostEnabled = true,
            AllowInteractive = true
        };
        hostConfig.SetPassword("RemoteDeskPassword999!");

        var dummyUi = new DummyHostUi();
        using var hostDiscovery = new LanDiscovery(hostConfig.DeviceId, port: 21117);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, dummyUi);
        hostServer.Start();
        Assert.True(hostServer.IsRunning);

        var viewerConfig = new ViewerConfig
        {
            ViewerId = "111222333",
            ImeMode = ImeSyncMode.Bound,
            Quality = 60,
            MaxFps = 30
        };

        using var viewerDiscovery = new LanDiscovery(viewerConfig.ViewerId, port: 21116);
        using var viewerSession = new ViewerSession($"127.0.0.1:{testPort}", viewerConfig, viewerDiscovery);

        var frameReceivedTcs = new TaskCompletionSource<Rectangle>(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitorListTcs = new TaskCompletionSource<List<MonitorInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var imeReceivedTcs = new TaskCompletionSource<ImeState>(TaskCreationOptions.RunContinuationsAsynchronously);

        viewerSession.FrameReceived += (rect, seq) => frameReceivedTcs.TrySetResult(rect);
        viewerSession.MonitorsUpdated += (list, selected) => monitorListTcs.TrySetResult(list);
        viewerSession.ImeStateReceived += state => imeReceivedTcs.TrySetResult(state);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // 1. 連線並驗證密碼
        await viewerSession.ConnectAsync("RemoteDeskPassword999!", cts.Token);
        Assert.True(viewerSession.IsConnected);
        Assert.Equal("987654321", viewerSession.HostDeviceId);
        Assert.Equal("LoopbackTestHost", viewerSession.HostDeviceName);

        // 2. 驗證被控端已啟動 Session
        for (int i = 0; i < 50 && hostServer.ActiveSession == null; i++) await Task.Delay(20);
        Assert.NotNull(hostServer.ActiveSession);

        // 3. 驗證收到螢幕清單
        var monitorTask = await Task.WhenAny(monitorListTcs.Task, Task.Delay(5000, cts.Token));
        Assert.True(monitorTask == monitorListTcs.Task, "應收到螢幕清單通知");
        var monitors = await monitorListTcs.Task;
        Assert.NotNull(monitors);
        Assert.NotEmpty(monitors);

        // 4. 驗證收到畫面幀
        var frameTask = await Task.WhenAny(frameReceivedTcs.Task, Task.Delay(8000, cts.Token));
        Assert.True(frameTask == frameReceivedTcs.Task, "應在數秒內成功接收並解碼第一幀畫面");
        var dirtyRect = await frameReceivedTcs.Task;
        Assert.False(dirtyRect.IsEmpty);
        Assert.True(viewerSession.RemoteWidth > 0);
        Assert.True(viewerSession.RemoteHeight > 0);

        // 5. 測試雙向輸入法狀態傳遞
        var testIme = new ImeState(0x04040404, Open: true, Conversion: 0x0001);
        viewerSession.SendImeSet(testIme);

        // 6. 測試滑鼠與鍵盤事件發送 (不應拋出任何異常)
        viewerSession.SendMouseMove(100, 100);
        viewerSession.SendMouseButton(MouseButtonId.Left, true, 100, 100);
        viewerSession.SendMouseButton(MouseButtonId.Left, false, 100, 100);
        viewerSession.SendKey(0x41, 0x1E, KeyFlags.None); // Key 'A'
        viewerSession.SendKey(0x41, 0x1E, KeyFlags.Up);

        // 7. 乾淨中斷連線
        viewerSession.Dispose();
        hostServer.Dispose();

        Assert.False(viewerSession.IsConnected);
    }

    [Fact]
    public async Task TestInteractiveAccessRequestApproval()
    {
        int testPort = FindFreeTcpPort();

        var hostConfig = new HostConfig
        {
            DeviceId = "888777666",
            DeviceName = "InteractiveHost",
            TcpPort = testPort,
            HostEnabled = true,
            AllowInteractive = true
        };
        // 無固定密碼
        hostConfig.SetPassword(null);

        var dummyUi = new DummyHostUi { ConfirmResponse = true };
        using var hostDiscovery = new LanDiscovery(hostConfig.DeviceId, port: 21115);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, dummyUi);
        hostServer.Start();

        var viewerConfig = new ViewerConfig { ViewerId = "555444333" };
        using var viewerDiscovery = new LanDiscovery(viewerConfig.ViewerId, port: 21114);
        using var viewerSession = new ViewerSession($"127.0.0.1:{testPort}", viewerConfig, viewerDiscovery);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await viewerSession.ConnectAsync(password: null, cts.Token);

        Assert.True(viewerSession.IsConnected);
        Assert.Equal("888777666", viewerSession.HostDeviceId);

        viewerSession.Dispose();
        hostServer.Dispose();
    }

    [Fact]
    public async Task TestInteractiveAccessRequestRejection()
    {
        int testPort = FindFreeTcpPort();

        var hostConfig = new HostConfig
        {
            DeviceId = "777666555",
            DeviceName = "RejectHost",
            TcpPort = testPort,
            HostEnabled = true,
            AllowInteractive = true
        };
        hostConfig.SetPassword(null);

        var dummyUi = new DummyHostUi { ConfirmResponse = false }; // 拒絕
        using var hostDiscovery = new LanDiscovery(hostConfig.DeviceId, port: 21113);
        using var hostServer = new HostServer(hostConfig, hostDiscovery, dummyUi);
        hostServer.Start();

        var viewerConfig = new ViewerConfig { ViewerId = "222111000" };
        using var viewerDiscovery = new LanDiscovery(viewerConfig.ViewerId, port: 21112);
        using var viewerSession = new ViewerSession($"127.0.0.1:{testPort}", viewerConfig, viewerDiscovery);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viewerSession.ConnectAsync(password: null, cts.Token));
        Assert.Contains("拒絕", ex.Message);

        viewerSession.Dispose();
        hostServer.Dispose();
    }
}
