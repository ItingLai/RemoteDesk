using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using RemoteDesk.Net;
using Xunit;

namespace RemoteDesk.Tests;

public class StunClientTests
{
    [Fact]
    public void CreateBindingRequest_HasValidFormat()
    {
        var txnId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var req = StunClient.CreateBindingRequest(txnId);

        Assert.Equal(20, req.Length);
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(0, 2));
        Assert.Equal(StunClient.BindingRequest, type);

        ushort len = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(2, 2));
        Assert.Equal(0, len);

        uint cookie = BinaryPrimitives.ReadUInt32BigEndian(req.AsSpan(4, 4));
        Assert.Equal(StunClient.MagicCookie, cookie);

        Assert.True(req.AsSpan(8, 12).SequenceEqual(txnId));
    }

    [Fact]
    public void ParseBindingResponse_WithXorMappedAddress_ParsesCorrectly()
    {
        var txnId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var targetIp = IPAddress.Parse("203.0.113.45");
        int targetPort = 54321;

        // 構造 STUN Response
        var resp = new byte[20 + 12]; // 20 header + 12 attr
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(0, 2), StunClient.BindingResponse);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2, 2), 12); // length
        BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(4, 4), StunClient.MagicCookie);
        txnId.CopyTo(resp.AsSpan(8, 12));

        // Attr: XOR-MAPPED-ADDRESS (0x0020), length 8
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(20, 2), StunClient.AttrXorMappedAddress);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(22, 2), 8);
        resp[24] = 0x00; // reserved
        resp[25] = 0x01; // IPv4

        // X-Port = targetPort ^ (cookie >> 16)
        ushort xPort = (ushort)(targetPort ^ (ushort)(StunClient.MagicCookie >> 16));
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(26, 2), xPort);

        // X-Address = targetIp ^ cookie
        uint ipUint = BinaryPrimitives.ReadUInt32BigEndian(targetIp.GetAddressBytes());
        uint xIp = ipUint ^ StunClient.MagicCookie;
        BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(28, 4), xIp);

        var parsed = StunClient.ParseBindingResponse(resp, txnId);
        Assert.NotNull(parsed);
        Assert.Equal(targetIp, parsed.Address);
        Assert.Equal(targetPort, parsed.Port);
    }

    [Fact]
    public void ParseBindingResponse_WithWrongTxnId_ReturnsNull()
    {
        var txnId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        var wrongTxnId = new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 };

        var resp = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(0, 2), StunClient.BindingResponse);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(4, 4), StunClient.MagicCookie);
        txnId.CopyTo(resp.AsSpan(8, 12));

        var parsed = StunClient.ParseBindingResponse(resp, wrongTxnId);
        Assert.Null(parsed);
    }

    [Fact]
    public async Task LiveOrMockStunQuery_LocalSocketQuery_Completes()
    {
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sock.Bind(new IPEndPoint(IPAddress.Any, 0));

        // 測試 STUN 查詢 (若本機聯外正常將傳回公網 IP，若斷網或受限亦不應拋出未處理例外)
        var result = await StunClient.QueryAsync(sock, "stun.l.google.com:19302", timeoutMs: 1500);
        // 如果連線成功，則傳回有效公網 IP；如果無網路環境，傳回 null 亦為合法行為
        if (result != null)
        {
            Assert.True(result.Port > 0);
            Assert.False(result.Address.Equals(IPAddress.Any));
        }
    }
}
