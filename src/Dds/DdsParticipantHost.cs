using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using ASAP.Models.Dds;
using Rti.Dds.Core;
using Rti.Dds.Core.Policy;
using Rti.Dds.Domain;
using Rti.Dds.Publication;
using Rti.Dds.Subscription;
using Rti.Dds.Topics;
using Rti.Types.Dynamic;

namespace ASAP.Dds;

/// <summary>
/// 한 DDS 세션에 대응하는 RTI 런타임. DomainParticipant 1개와 그 위에서 만들어진
/// DynamicData 기반 Topic/Reader/Writer를 관리한다.
///
/// Type 등록은 QosProvider에 사용자가 올린 DDSSim.xml을 넘겨서 처리한다.
/// (Wire-compat: 동일 type_name + 동일 topic_name이면 ambassador와 매칭됨.)
/// </summary>
public sealed class DdsParticipantHost : IAsyncDisposable
{
    private readonly DomainParticipant _participant;
    private readonly QosProvider _qosProvider;
    private readonly Publisher _publisher;
    private readonly Subscriber _subscriber;
    private readonly DdsTransportSettings _transport;
    private readonly ILogger<DdsParticipantHost> _logger;

    private readonly ConcurrentDictionary<string, Topic<DynamicData>> _topics = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DataReader<DynamicData>> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DataWriter<DynamicData>> _writers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DynamicType> _typeCache = new(StringComparer.OrdinalIgnoreCase);

    private long _disposed;

    public DdsParticipantHost(
        DomainParticipant participant,
        QosProvider qosProvider,
        DdsTransportSettings transport,
        ILogger<DdsParticipantHost> logger)
    {
        _participant = participant;
        _qosProvider = qosProvider;
        _transport = transport;
        _publisher = participant.ImplicitPublisher;
        _subscriber = participant.ImplicitSubscriber;
        _logger = logger;
    }

    public DomainParticipant Participant => _participant;
    public QosProvider QosProvider => _qosProvider;

    /// <summary>
    /// 토픽에 대한 typed Topic<DynamicData>를 가져오거나 새로 만든다.
    /// </summary>
    public Topic<DynamicData> GetOrCreateTopic(string topicName, string typeName)
    {
        return _topics.GetOrAdd(topicName, _ =>
        {
            var dynType = _typeCache.GetOrAdd(typeName, n =>
            {
                var t = _qosProvider.GetType(n)
                    ?? throw new InvalidOperationException($"DDS 타입을 찾을 수 없음: {n}");
                return t;
            });
            return _participant.CreateTopic(topicName, dynType);
        });
    }

    /// <summary>
    /// DataReader 생성. 동일 토픽에 이미 있으면 기존 것 반환.
    /// </summary>
    public DataReader<DynamicData> GetOrCreateReader(string topicName, string typeName, string qosProfileFullName)
    {
        return _readers.GetOrAdd(topicName, _ =>
        {
            var topic = GetOrCreateTopic(topicName, typeName);
            var readerQos = ApplyDataMulticast(TryGetDataReaderQos(qosProfileFullName));
            var reader = readerQos is not null
                ? _subscriber.CreateDataReader(topic, readerQos)
                : _subscriber.CreateDataReader(topic);
            _logger.LogInformation(
                "DDS reader created topic={Topic} type={Type} qos={Qos} matchStatus={MatchStatus}",
                topicName,
                typeName,
                qosProfileFullName,
                TryDescribeMatchedStatus(reader));
            return reader;
        });
    }

    /// <summary>
    /// DataWriter 생성. 동일 토픽에 이미 있으면 기존 것 반환.
    /// </summary>
    public DataWriter<DynamicData> GetOrCreateWriter(string topicName, string typeName, string qosProfileFullName)
    {
        return _writers.GetOrAdd(topicName, _ =>
        {
            var topic = GetOrCreateTopic(topicName, typeName);
            var writerQos = TryGetDataWriterQos(qosProfileFullName);
            var writer = writerQos is not null
                ? _publisher.CreateDataWriter(topic, writerQos)
                : _publisher.CreateDataWriter(topic);
            _logger.LogInformation(
                "DDS writer created topic={Topic} type={Type} qos={Qos} matchStatus={MatchStatus}",
                topicName,
                typeName,
                qosProfileFullName,
                TryDescribeMatchedStatus(writer));
            return writer;
        });
    }

    public string DescribeWriterMatchStatus(string topicName)
        => _writers.TryGetValue(topicName, out var writer)
            ? TryDescribeMatchedStatus(writer)
            : "writer-not-created";

    public string DescribeReaderMatchStatus(string topicName)
        => _readers.TryGetValue(topicName, out var reader)
            ? TryDescribeMatchedStatus(reader)
            : "reader-not-created";

    public DynamicData CreateSample(string typeName)
    {
        var dynType = _typeCache.GetOrAdd(typeName, n =>
            _qosProvider.GetType(n)
                ?? throw new InvalidOperationException($"DDS 타입을 찾을 수 없음: {n}"));
        return new DynamicData(dynType);
    }

    public void RemoveReader(string topicName)
    {
        if (_readers.TryRemove(topicName, out var r))
        {
            try { r.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Reader Dispose 실패: {Topic}", topicName); }
        }
    }

    public void RemoveWriter(string topicName)
    {
        if (_writers.TryRemove(topicName, out var w))
        {
            try { w.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Writer Dispose 실패: {Topic}", topicName); }
        }
    }

    private DataReaderQos? TryGetDataReaderQos(string fullProfileName)
    {
        try { return _qosProvider.GetDataReaderQos(fullProfileName); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DataReaderQos 로드 실패, 기본 QoS 사용: {Profile}", fullProfileName);
            return null;
        }
    }

    private DataWriterQos? TryGetDataWriterQos(string fullProfileName)
    {
        try { return _qosProvider.GetDataWriterQos(fullProfileName); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DataWriterQos 로드 실패, 기본 QoS 사용: {Profile}", fullProfileName);
            return null;
        }
    }

    private DataReaderQos? ApplyDataMulticast(DataReaderQos? baseQos)
    {
        if (!_transport.DataMulticastEnabled)
            return baseQos;

        if (string.IsNullOrWhiteSpace(_transport.DataMulticastAddress))
        {
            _logger.LogWarning("DDS data multicast was enabled without an address; reader QoS multicast is unchanged.");
            return baseQos;
        }

        var qos = baseQos ?? _qosProvider.GetDataReaderQos();
        var address = _transport.DataMulticastAddress.Trim();
        var port = _transport.DataMulticastPort.GetValueOrDefault();

        var multicast = TransportMulticast.Default.With(policy =>
        {
            policy.Kind = TransportMulticastKind.Automatic;
            policy.Value.Clear();
            policy.Value.Add(TransportMulticastSettings.Default.With(settings =>
            {
                settings.ReceiveAddress = address;
                if (port > 0)
                    settings.ReceivePort = port;
                settings.Transports.Clear();
                settings.Transports.Add("builtin.udpv4");
            }));
        });

        _logger.LogInformation(
            "DDS data multicast enabled receiveAddress={Address} receivePort={Port}",
            address,
            port > 0 ? port.ToString() : "default");

        return qos.WithMulticast(multicast);
    }

    private static string TryDescribeMatchedStatus(object entity)
    {
        try
        {
            var type = entity.GetType();
            var candidates = new[]
            {
                "PublicationMatchedStatus",
                "SubscriptionMatchedStatus",
                "MatchedPublicationStatus",
                "MatchedSubscriptionStatus"
            };

            foreach (var name in candidates)
            {
                var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                if (property is not null)
                    return $"{name}={property.GetValue(entity)}";

                var method = type.GetMethod($"Get{name}", BindingFlags.Instance | BindingFlags.Public, Type.EmptyTypes);
                if (method is not null)
                    return $"{name}={method.Invoke(entity, null)}";
            }

            return "match-status-api-not-found";
        }
        catch (Exception ex)
        {
            return $"match-status-unavailable:{ex.GetType().Name}:{ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        foreach (var r in _readers.Values) try { r.Dispose(); } catch { /* swallow */ }
        foreach (var w in _writers.Values) try { w.Dispose(); } catch { /* swallow */ }
        foreach (var t in _topics.Values) try { t.Dispose(); } catch { /* swallow */ }
        _readers.Clear();
        _writers.Clear();
        _topics.Clear();
        _typeCache.Clear();

        try { _qosProvider.Dispose(); } catch { /* swallow */ }
        try { _participant.Dispose(); } catch { /* swallow */ }

        await Task.CompletedTask;
    }
}

/// <summary>
/// DdsParticipantHost 생성 팩토리. 세션 단위로 호출된다.
/// QosProvider 입력 XML을 임시 파일에 쓰고, DomainParticipant를 생성한 뒤 반환.
/// </summary>
public sealed class DdsParticipantHostFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public DdsParticipantHostFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public DdsParticipantHost Create(
        DdsTransportSettings transport,
        string typesXmlContent,
        string? qosProfilesXml)
    {
        // RTI QosProvider는 파일 경로로 동작 → 임시 디렉토리에 두 XML을 쓴다.
        var sessionTempDir = Path.Combine(Path.GetTempPath(), "asap-dds", System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sessionTempDir);
        var typesPath = Path.Combine(sessionTempDir, "types.xml");
        File.WriteAllText(typesPath, typesXmlContent);

        var urls = new List<string> { typesPath };
        if (!string.IsNullOrWhiteSpace(qosProfilesXml))
        {
            var qosPath = Path.Combine(sessionTempDir, "qos_profiles.xml");
            File.WriteAllText(qosPath, qosProfilesXml);
            urls.Add(qosPath);
        }

        var profile = Profile.Default.With(b =>
        {
            b.UrlProfile.Clear();
            foreach (var u in urls) b.UrlProfile.Add(u);
        });
        var qosProvider = new QosProvider(profile);

        var participantQos = ApplyTransport(qosProvider.GetDomainParticipantQos(), transport);
        var factoryLogger = _loggerFactory.CreateLogger<DdsParticipantHostFactory>();
        factoryLogger.LogInformation(
            "DDS participant creating domain={Domain} discovery={DiscoveryMode} initialPeers={InitialPeers} multicast={Multicast}",
            transport.DomainId,
            transport.DiscoveryMode,
            string.Join(",", transport.InitialPeers.Select(NormalizeUdpv4Locator)),
            transport.MulticastAddress ?? "");
        var participant = DomainParticipantFactory.Instance.CreateParticipant(
            transport.DomainId, participantQos);

        var hostLogger = _loggerFactory.CreateLogger<DdsParticipantHost>();
        return new DdsParticipantHost(participant, qosProvider, transport, hostLogger);
    }

    private static DomainParticipantQos ApplyTransport(
        DomainParticipantQos baseQos,
        DdsTransportSettings transport)
    {
        var props = new Dictionary<string, string>();

        if (transport.AllowInterfaces.Count > 0)
            props["dds.transport.UDPv4.builtin.parent.allow_interfaces"] =
                string.Join(",", transport.AllowInterfaces);
        if (transport.DenyInterfaces.Count > 0)
            props["dds.transport.UDPv4.builtin.parent.deny_interfaces"] =
                string.Join(",", transport.DenyInterfaces);
        if (transport.SendBufferSize is int sb)
            props["dds.transport.UDPv4.builtin.send_socket_buffer_size"] = sb.ToString();
        if (transport.ReceiveBufferSize is int rb)
            props["dds.transport.UDPv4.builtin.recv_socket_buffer_size"] = rb.ToString();

        var qos = baseQos;
        if (props.Count > 0)
            qos = qos.WithProperty(Property.FromDictionary(props));

        qos = qos.WithDiscovery(d =>
        {
            switch (transport.DiscoveryMode)
            {
                case DdsDiscoveryMode.PeerToPeer:
                    d.InitialPeers.Clear();
                    d.MulticastReceiveAddresses.Clear();
                    foreach (var peer in transport.InitialPeers.Select(NormalizeUdpv4Locator).Where(x => !string.IsNullOrWhiteSpace(x)))
                        d.InitialPeers.Add(peer);
                    break;

                case DdsDiscoveryMode.Multicast:
                    if (!string.IsNullOrWhiteSpace(transport.MulticastAddress))
                    {
                        var locator = NormalizeUdpv4Locator(transport.MulticastAddress!);
                        d.InitialPeers.Clear();
                        d.InitialPeers.Add(locator);
                        d.MulticastReceiveAddresses.Clear();
                        d.MulticastReceiveAddresses.Add(locator);
                    }
                    break;
            }
        });

        return qos;
    }

    private static string NormalizeUdpv4Locator(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        return trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed
            : $"udpv4://{trimmed}";
    }
}
