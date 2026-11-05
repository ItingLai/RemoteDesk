using RemoteDesk.Host;
using Xunit;

namespace RemoteDesk.Tests;

public class MonitorTests
{
    [Fact]
    public void TestMonitorEnumeration()
    {
        var monitors = MonitorEnumerator.GetMonitors();
        Assert.NotNull(monitors);
        // 本機若有螢幕，至少會有 1 個；若是 CI/無頭環境也必須不拋例外
        if (monitors.Count > 0)
        {
            var primary = monitors.FirstOrDefault(m => m.Primary);
            Assert.NotNull(primary);
            Assert.True(primary.Width > 0);
            Assert.True(primary.Height > 0);
            Assert.False(string.IsNullOrEmpty(primary.DeviceName));
        }

        var vs = MonitorEnumerator.VirtualScreen;
        Assert.True(vs.Width > 0);
        Assert.True(vs.Height > 0);
    }
}
