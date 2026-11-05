using RemoteDesk.Common;
using Xunit;

namespace RemoteDesk.Tests;

public class ProtocolTests
{
    [Fact]
    public void TestPacketWriterAndReaderPrimitives()
    {
        var w = PacketWriter.Raw();
        w.U8(0x42);
        w.Bool(true);
        w.U16(12345);
        w.I32(-987654);
        w.U32(3000000000U);
        w.I64(987654321012345L);
        w.Str("遠端桌面中文測試 Hello World 123");

        var bytes = w.ToArray();
        var r = new PacketReader(bytes);

        Assert.Equal(0x42, r.U8());
        Assert.True(r.Bool());
        Assert.Equal(12345, r.U16());
        Assert.Equal(-987654, r.I32());
        Assert.Equal(3000000000U, r.U32());
        Assert.Equal(987654321012345L, r.I64());
        Assert.Equal("遠端桌面中文測試 Hello World 123", r.Str());
        Assert.Equal(0, r.Remaining);
    }

    [Fact]
    public void TestMonitorListCodec()
    {
        var monitors = new List<MonitorInfo>
        {
            new MonitorInfo(@"\\.\DISPLAY1", "ASUS 4K", 1, 0, 0, 3840, 2160, true, 144),
            new MonitorInfo(@"\\.\DISPLAY2", "Dell 2K", 2, 3840, 0, 2560, 1440, false, 96),
        };

        var packet = MessageCodec.MonitorList(monitors, @"\\.\DISPLAY1");
        Assert.Equal((byte)HostMsg.MonitorList, packet[0]);

        var r = new PacketReader(packet.AsSpan(1));
        var (decodedList, selected) = MessageCodec.ReadMonitorList(ref r);

        Assert.Equal(@"\\.\DISPLAY1", selected);
        Assert.Equal(2, decodedList.Count);

        Assert.Equal(monitors[0].DeviceName, decodedList[0].DeviceName);
        Assert.Equal(monitors[0].FriendlyName, decodedList[0].FriendlyName);
        Assert.Equal(monitors[0].Width, decodedList[0].Width);
        Assert.Equal(monitors[0].Height, decodedList[0].Height);
        Assert.True(decodedList[0].Primary);
        Assert.Equal(144, decodedList[0].Dpi);

        Assert.Equal(monitors[1].DeviceName, decodedList[1].DeviceName);
        Assert.Equal(monitors[1].Width, decodedList[1].Width);
        Assert.False(decodedList[1].Primary);
    }

    [Fact]
    public void TestImeCodec()
    {
        var state = new ImeState(0x04040404, Open: true, Conversion: 0x0001);
        var packet = MessageCodec.Ime((byte)HostMsg.ImeState, state);

        Assert.Equal((byte)HostMsg.ImeState, packet[0]);
        var r = new PacketReader(packet.AsSpan(1));
        var decoded = MessageCodec.ReadIme(ref r);

        Assert.Equal(state.KeyboardLayout, decoded.KeyboardLayout);
        Assert.Equal(state.Open, decoded.Open);
        Assert.Equal(state.Conversion, decoded.Conversion);
        Assert.True(decoded.IsNative);
    }
}
