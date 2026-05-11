using GrpcWorkbench.Models.Grpc;

namespace GrpcWorkbench.Models.Session;

public class GrpcSession
{
    public string SessionId { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; } = 50051;
    public bool UseTls { get; set; } = false;
    public bool UseUnixDomainSocket { get; set; } = false;
    public string? UnixSocketPath { get; set; }
    public byte[]? ProtoContent { get; set; }
    public string? ProtoFileName { get; set; }
    public List<ServiceMetadata> Services { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastAccessedAt { get; set; } = DateTime.UtcNow;
}
