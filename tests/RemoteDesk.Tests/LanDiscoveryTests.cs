using System.Net;
using RemoteDesk.Common;
using RemoteDesk.Net;
using Xunit;

namespace RemoteDesk.Tests;

public class LanDiscoveryTests
{
    [Fact]
    public void TestDeviceIdGeneration()
    {
        string id1 = AppConfig.GenerateDeviceId();
        string id2 = AppConfig.GenerateDeviceId();

        Assert.Equal(id1, id2); // 同一台機器應該產出相同且穩定的 ID
        Assert.Equal(9, id1.Length);
        Assert.True(id1.All(char.IsDigit));

        string formatted = AppConfig.FormatId(id1);
        Assert.Equal(11, formatted.Length); // "123 456 789"
        Assert.Equal(' ', formatted[3]);
        Assert.Equal(' ', formatted[7]);
    }

    [Fact]
    public void TestDiscoveredDeviceModel()
    {
        var dev = new DiscoveredDevice
        {
            Id = "123456789",
            Name = "TEST-PC",
            User = "Alice",
            Address = IPAddress.Parse("192.168.1.100"),
            Port = 21119,
            Busy = false,
            LastSeen = DateTime.Now
        };

        Assert.Equal("123456789", dev.Id);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.1.100"), 21119), dev.EndPoint);
        Assert.False(dev.Busy);
    }
}
