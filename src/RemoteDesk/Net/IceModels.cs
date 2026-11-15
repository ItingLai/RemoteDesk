using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteDesk.Net;

public enum IceCandidateType
{
    Host = 1,
    Srflx = 2,
    Relay = 3
}

public enum CandidateValidityStatus
{
    Unknown = 0,
    Valid = 1,
    StunTimeout = 2,
    StunFailed = 3,
    NoExternalRoute = 4,
    SocketFailed = 5,
    Expired = 6
}

/// <summary>
/// ICE 連線候選端點 (Host、Server Reflexive，並保留 Relay 結構擴充空間；本階段僅實作 IPv4)。
/// </summary>
public sealed class IceCandidate
{
    private string _ip = "";
    private string _publicAddress = "";
    private int _port;
    private int _publicPort;

    [JsonPropertyName("candidateId")]
    public string CandidateId { get; set; } = Guid.NewGuid().ToString("N")[..12];

    [JsonPropertyName("type")]
    public IceCandidateType Type { get; set; } = IceCandidateType.Host;

    [JsonIgnore]
    public IceCandidateType CandidateType
    {
        get => Type;
        set => Type = value;
    }

    [JsonPropertyName("addressFamily")]
    public AddressFamily AddressFamily { get; set; } = AddressFamily.InterNetwork;

    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "UDP";

    [JsonPropertyName("ip")]
    public string Ip
    {
        get => !string.IsNullOrEmpty(_ip) ? _ip : _publicAddress;
        set
        {
            _ip = value ?? "";
            if (string.IsNullOrEmpty(_publicAddress) && !string.IsNullOrEmpty(_ip))
                _publicAddress = _ip;
        }
    }

    [JsonPropertyName("port")]
    public int Port
    {
        get => _port > 0 ? _port : _publicPort;
        set
        {
            _port = value;
            if (_publicPort <= 0 && value > 0)
                _publicPort = value;
        }
    }

    [JsonPropertyName("localAddress")]
    public string LocalAddress { get; set; } = "";

    [JsonPropertyName("localPort")]
    public int LocalPort { get; set; }

    [JsonPropertyName("publicAddress")]
    public string PublicAddress
    {
        get => !string.IsNullOrEmpty(_publicAddress) ? _publicAddress : _ip;
        set
        {
            _publicAddress = value ?? "";
            if (string.IsNullOrEmpty(_ip) && !string.IsNullOrEmpty(_publicAddress))
                _ip = _publicAddress;
        }
    }

    [JsonPropertyName("publicPort")]
    public int PublicPort
    {
        get => _publicPort > 0 ? _publicPort : _port;
        set
        {
            _publicPort = value;
            if (_port <= 0 && value > 0)
                _port = value;
        }
    }

    [JsonPropertyName("interfaceId")]
    public string InterfaceId { get; set; } = "";

    [JsonPropertyName("socketId")]
    public string SocketId { get; set; } = "";

    [JsonPropertyName("stunServer")]
    public string StunServer { get; set; } = "";

    [JsonPropertyName("priority")]
    public uint Priority { get; set; }

    [JsonPropertyName("discoveryTimestamp")]
    public DateTime DiscoveryTimestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("validityStatus")]
    public CandidateValidityStatus ValidityStatus { get; set; } = CandidateValidityStatus.Valid;

    [JsonPropertyName("ufrag")]
    public string Ufrag { get; set; } = "";

    [JsonIgnore]
    public IPEndPoint? EndPoint => IPAddress.TryParse(Ip, out var addr) ? new IPEndPoint(addr, Port) : null;

    public static uint ComputePriority(IceCandidateType type, ushort localPref = 65535, byte component = 1)
    {
        uint typePref = type switch
        {
            IceCandidateType.Host => 126,
            IceCandidateType.Srflx => 100,
            IceCandidateType.Relay => 0,
            _ => 50
        };
        return (typePref << 24) | ((uint)localPref << 8) | (uint)(256 - component);
    }

    public override string ToString() =>
        $"candidate:{CandidateId} {Type} {Ip}:{Port} (local={LocalAddress}:{LocalPort}, iface={InterfaceId}, stun={StunServer}, prio={Priority})";
}

/// <summary>
/// 於 Signaling Server 上交換之端對端 ICE 控制訊息。
/// </summary>
public sealed class IceSignal
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = ""; // "candidate", "candidates-done", "ready"

    [JsonPropertyName("candidate")]
    public IceCandidate? Candidate { get; set; }

    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("senderDeviceId")]
    public string SenderDeviceId { get; set; } = "";

    [JsonPropertyName("receiverDeviceId")]
    public string ReceiverDeviceId { get; set; } = "";

    [JsonPropertyName("retryRound")]
    public int RetryRound { get; set; } = 1;

    [JsonPropertyName("retryReason")]
    public string RetryReason { get; set; } = "";

    [JsonPropertyName("recoveryEpoch")]
    public int RecoveryEpoch { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static IceSignal? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<IceSignal>(json); }
        catch { return null; }
    }
}
