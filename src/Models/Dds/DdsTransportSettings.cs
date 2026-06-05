namespace ASAP.Models.Dds;

public enum DdsDiscoveryMode
{
    Default,
    Multicast,
    PeerToPeer
}

public sealed class DdsTransportSettings
{
    public int DomainId { get; set; }
    public DdsDiscoveryMode DiscoveryMode { get; set; } = DdsDiscoveryMode.Default;
    public List<string> InitialPeers { get; set; } = [];
    public string? MulticastAddress { get; set; }
    public bool DataMulticastEnabled { get; set; }
    public string? DataMulticastAddress { get; set; }
    public int? DataMulticastPort { get; set; }
    public List<string> AllowInterfaces { get; set; } = [];
    public List<string> DenyInterfaces { get; set; } = [];
    public int? SendBufferSize { get; set; }
    public int? ReceiveBufferSize { get; set; }
}
