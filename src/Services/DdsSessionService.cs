using System.Collections.Concurrent;
using ASAP.Dds;
using ASAP.Models.Dds;
using ASAP.Models.Session;

namespace ASAP.Services;

public interface IDdsSessionService
{
    DdsSession Create(DdsSessionCreateRequest request);
    DdsSession? Get(string sessionId);
    DdsParticipantHost? GetHost(string sessionId);
    Task DeleteAsync(string sessionId);
    IReadOnlyList<DdsSession> GetAll();
}

public sealed class DdsSessionCreateRequest
{
    public required string Name { get; init; }
    public required DdsTransportSettings Transport { get; init; }
    public required string TypesXmlContent { get; init; }
    public string? TypesXmlFileName { get; init; }
    public required string ConfigXmlContent { get; init; }
    public string? ConfigXmlFileName { get; init; }
}

public sealed class DdsSessionService : IDdsSessionService, IAsyncDisposable
{
    private readonly DdsParticipantHostFactory _hostFactory;
    private readonly ILogger<DdsSessionService> _logger;

    private readonly ConcurrentDictionary<string, DdsSession> _sessions = new();
    private readonly ConcurrentDictionary<string, DdsParticipantHost> _hosts = new();

    public DdsSessionService(
        DdsParticipantHostFactory hostFactory,
        ILogger<DdsSessionService> logger)
    {
        _hostFactory = hostFactory;
        _logger = logger;
    }

    public DdsSession Create(DdsSessionCreateRequest request)
    {
        var configParse = DdsConfigParser.Parse(request.ConfigXmlContent);
        var types = DdsTypeParser.Parse(request.TypesXmlContent);
        var transport = NormalizeTransport(request.Transport);

        _logger.LogInformation(
            "DDS session create requested name={Name} domain={Domain} discovery={DiscoveryMode} initialPeers={InitialPeers} multicast={Multicast} dataMulticast={DataMulticast}:{DataMulticastPort}",
            request.Name,
            transport.DomainId,
            transport.DiscoveryMode,
            string.Join(",", transport.InitialPeers),
            transport.MulticastAddress ?? "",
            transport.DataMulticastAddress ?? "",
            transport.DataMulticastPort?.ToString() ?? "");

        var host = _hostFactory.Create(transport, request.TypesXmlContent, configParse.QosProfilesXml);

        var session = new DdsSession
        {
            SessionId = System.Guid.NewGuid().ToString(),
            Name = request.Name,
            Transport = transport,
            TypesXmlContent = System.Text.Encoding.UTF8.GetBytes(request.TypesXmlContent),
            TypesXmlFileName = request.TypesXmlFileName,
            ConfigXmlContent = System.Text.Encoding.UTF8.GetBytes(request.ConfigXmlContent),
            ConfigXmlFileName = request.ConfigXmlFileName,
            Topics = configParse.Topics.ToList(),
            Types = types,
            QosProfiles = configParse.QosProfileNames.ToList(),
        };

        _sessions[session.SessionId] = session;
        _hosts[session.SessionId] = host;

        _logger.LogInformation(
            "DDS 세션 생성: {Name} ({Id}) — domain={Domain}, topics={Topics}, types={Types}",
            session.Name, session.SessionId, transport.DomainId,
            session.Topics.Count, session.Types.Count);
        return session;
    }

    private DdsTransportSettings NormalizeTransport(DdsTransportSettings source)
    {
        var initialPeers = source.InitialPeers
            .Where(peer => !string.IsNullOrWhiteSpace(peer))
            .Select(peer => peer.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var discoveryMode = source.DiscoveryMode;
        if (discoveryMode == DdsDiscoveryMode.Default && initialPeers.Count > 0)
        {
            discoveryMode = DdsDiscoveryMode.PeerToPeer;
            _logger.LogWarning(
                "DDS discovery mode was Default while InitialPeers were supplied; using PeerToPeer. initialPeers={InitialPeers}",
                string.Join(",", initialPeers));
        }

        return new DdsTransportSettings
        {
            DomainId = source.DomainId,
            DiscoveryMode = discoveryMode,
            InitialPeers = initialPeers,
            MulticastAddress = string.IsNullOrWhiteSpace(source.MulticastAddress) ? null : source.MulticastAddress.Trim(),
            DataMulticastEnabled = source.DataMulticastEnabled,
            DataMulticastAddress = string.IsNullOrWhiteSpace(source.DataMulticastAddress) ? null : source.DataMulticastAddress.Trim(),
            DataMulticastPort = source.DataMulticastPort is > 0 ? source.DataMulticastPort : null,
            AllowInterfaces = source.AllowInterfaces
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            DenyInterfaces = source.DenyInterfaces
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            SendBufferSize = source.SendBufferSize,
            ReceiveBufferSize = source.ReceiveBufferSize,
        };
    }

    public DdsSession? Get(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var s))
        {
            s.LastAccessedAt = DateTime.UtcNow;
            return s;
        }
        return null;
    }

    public DdsParticipantHost? GetHost(string sessionId)
        => _hosts.TryGetValue(sessionId, out var h) ? h : null;

    public async Task DeleteAsync(string sessionId)
    {
        if (_hosts.TryRemove(sessionId, out var host))
        {
            try { await host.DisposeAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "DDS host dispose 실패: {Id}", sessionId); }
        }
        _sessions.TryRemove(sessionId, out _);
        _logger.LogInformation("DDS 세션 삭제: {Id}", sessionId);
    }

    public IReadOnlyList<DdsSession> GetAll() => _sessions.Values.ToList();

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList())
            await DeleteAsync(id);
    }
}
