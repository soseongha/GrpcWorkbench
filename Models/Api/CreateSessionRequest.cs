namespace GrpcWorkbench.Models.Api;

public class CreateSessionRequest
{
    public string? Address { get; set; }
    public int? Port { get; set; }
    public bool UseTls { get; set; }
    public bool UseUnixDomainSocket { get; set; }
    public string? UnixSocketPath { get; set; }
}
