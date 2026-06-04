using System.Collections.Concurrent;
using ASAP.Dds;
using ASAP.Models.Dds;
using Rti.Dds.Subscription;
using Rti.Types.Dynamic;

namespace ASAP.Services;

/// <summary>
/// DDS 세션 운영 상태 — 활성 구독, 최근 샘플, 발행 이력을 관리.
/// UI는 StateChanged 이벤트를 구독하고 Snapshot으로 표시한다.
/// </summary>
public sealed class DdsStateService
{
    private readonly IDdsSessionService _sessions;
    private readonly ILogger<DdsStateService> _logger;
    private readonly object _subscriptionGate = new();

    private readonly ConcurrentDictionary<string, DdsSubscriptionInfo> _subscriptions = new();
    private readonly Dictionary<string, string> _subscriptionByReaderKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, EntryBuffer<DdsSampleEntry>> _samples = new();
    private readonly ConcurrentDictionary<string, EntryBuffer<DdsOutboundEntry>> _outbound = new();
    private readonly ConcurrentDictionary<string, long> _publishSeq = new();
    private long _subscriptionVersion;

    public event Action? StateChanged;
    public event Action<DdsSubscriptionInfo, DdsSampleEntry>? SampleReceived;

    public DdsStateService(IDdsSessionService sessions, ILogger<DdsStateService> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    // ── Subscriptions ─────────────────────────────────────────────

    public DdsSubscriptionInfo StartSubscription(
        string sessionId, string topicName, string typeName, string qosProfileName)
    {
        var host = _sessions.GetHost(sessionId)
            ?? throw new InvalidOperationException($"DDS 세션 없음: {sessionId}");

        var fullQos = QualifyProfile(qosProfileName);
        _logger.LogInformation(
            "DDS subscription requested session={SessionId} topic={Topic} type={Type} qos={Qos}",
            sessionId,
            topicName,
            typeName,
            fullQos);
        var readerKey = BuildReaderKey(sessionId, topicName);
        lock (_subscriptionGate)
        {
            if (_subscriptionByReaderKey.TryGetValue(readerKey, out var existingId)
                && _subscriptions.TryGetValue(existingId, out var existing)
                && existing.IsActive)
            {
                return existing;
            }
        }

        var reader = host.GetOrCreateReader(topicName, typeName, fullQos);
        _logger.LogInformation(
            "DDS subscription reader ready session={SessionId} topic={Topic} matchStatus={MatchStatus}",
            sessionId,
            topicName,
            host.DescribeReaderMatchStatus(topicName));

        var info = new DdsSubscriptionInfo
        {
            SubscriptionId = System.Guid.NewGuid().ToString(),
            SessionId = sessionId,
            TopicName = topicName,
            TypeName = typeName,
            StartedAt = DateTime.UtcNow,
        };
        lock (_subscriptionGate)
        {
            if (_subscriptionByReaderKey.TryGetValue(readerKey, out var existingId)
                && _subscriptions.TryGetValue(existingId, out var existing)
                && existing.IsActive)
            {
                return existing;
            }

            _samples[info.SubscriptionId] = new EntryBuffer<DdsSampleEntry>();
            reader.DataAvailable += anyReader => HandleDataAvailable(anyReader, info);
            _subscriptions[info.SubscriptionId] = info;
            _subscriptionByReaderKey[readerKey] = info.SubscriptionId;
        }
        Interlocked.Increment(ref _subscriptionVersion);

        _logger.LogInformation("DDS 구독 시작: {Topic} ({Sub})", topicName, info.SubscriptionId);
        StateChanged?.Invoke();
        return info;
    }

    public void StopSubscription(string subscriptionId)
    {
        if (_subscriptions.TryRemove(subscriptionId, out var info))
        {
            info.IsActive = false;
            lock (_subscriptionGate)
            {
                _subscriptionByReaderKey.Remove(BuildReaderKey(info.SessionId, info.TopicName));
            }
            _samples.TryRemove(subscriptionId, out _);
            Interlocked.Increment(ref _subscriptionVersion);

            // 같은 topic을 다른 sub가 더 보고 있지 않으면 reader 제거
            var stillUsed = _subscriptions.Values.Any(s =>
                s.SessionId == info.SessionId && s.TopicName == info.TopicName);
            if (!stillUsed)
            {
                _sessions.GetHost(info.SessionId)?.RemoveReader(info.TopicName);
            }
            _logger.LogInformation("DDS 구독 중지: {Topic} ({Sub})", info.TopicName, subscriptionId);
            StateChanged?.Invoke();
        }
    }

    public IReadOnlyList<DdsSubscriptionInfo> SnapshotSubscriptions(string? sessionId = null)
        => _subscriptions.Values
            .Where(s => sessionId == null || s.SessionId == sessionId)
            .OrderBy(s => s.StartedAt)
            .ToList();

    public IReadOnlyList<DdsSampleEntry> SnapshotSamples(string subscriptionId, int max = int.MaxValue)
    {
        return _samples.TryGetValue(subscriptionId, out var buffer)
            ? buffer.SnapshotNewestFirst(max)
            : [];
    }

    public long SubscriptionVersion => Interlocked.Read(ref _subscriptionVersion);

    public long SampleVersion(string subscriptionId)
        => _samples.TryGetValue(subscriptionId, out var buffer) ? buffer.Version : -1;

    public void ClearSamples(string sessionId)
    {
        foreach (var subscription in _subscriptions.Values.Where(item => item.SessionId == sessionId))
        {
            if (_samples.TryGetValue(subscription.SubscriptionId, out var buffer))
                buffer.Clear();

            Interlocked.Exchange(ref subscription.ReceivedCount, 0);
            lock (subscription)
            {
                subscription.LatencySampleCount = 0;
                subscription.TotalLatencyMs = 0;
                subscription.LastSample = null;
            }
        }

        StateChanged?.Invoke();
    }

    // ── Publishing ────────────────────────────────────────────────

    public DdsPublishResult Publish(
        string sessionId, string topicName, string typeName,
        string qosProfileName, string jsonPayload)
    {
        var host = _sessions.GetHost(sessionId)
            ?? throw new InvalidOperationException($"DDS 세션 없음: {sessionId}");

        var fullQos = QualifyProfile(qosProfileName);
        _logger.LogInformation(
            "DDS publish requested session={SessionId} topic={Topic} type={Type} qos={Qos}",
            sessionId,
            topicName,
            typeName,
            fullQos);
        var writer = host.GetOrCreateWriter(topicName, typeName, fullQos);

        using var sample = host.CreateSample(typeName);
        try
        {
            DdsJsonConverter.ApplyJson(sample, jsonPayload);
            writer.Write(sample);
            _logger.LogInformation(
                "DDS publish write succeeded session={SessionId} topic={Topic} writerMatchStatus={MatchStatus}",
                sessionId,
                topicName,
                host.DescribeWriterMatchStatus(topicName));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DDS publish 실패: {Topic}", topicName);
            RecordOutbound(sessionId, new DdsOutboundEntry
            {
                Timestamp = DateTime.UtcNow,
                TopicName = topicName,
                TypeName = typeName,
                JsonPayload = jsonPayload,
                Success = false,
                Error = ex.Message,
            });
            StateChanged?.Invoke();
            return new DdsPublishResult(false, ex.Message);
        }

        RecordOutbound(sessionId, new DdsOutboundEntry
        {
            Timestamp = DateTime.UtcNow,
            TopicName = topicName,
            TypeName = typeName,
            JsonPayload = jsonPayload,
            Success = true,
        });
        StateChanged?.Invoke();
        return new DdsPublishResult(true, null);
    }

    public IReadOnlyList<DdsOutboundEntry> SnapshotOutbound(string sessionId, int max = int.MaxValue)
    {
        return _outbound.TryGetValue(sessionId, out var buffer)
            ? buffer.SnapshotNewestFirst(max)
            : [];
    }

    public long OutboundVersion(string sessionId)
        => _outbound.TryGetValue(sessionId, out var buffer) ? buffer.Version : -1;

    public void ClearOutbound(string sessionId)
    {
        if (_outbound.TryGetValue(sessionId, out var buffer))
            buffer.Clear();

        StateChanged?.Invoke();
    }

    public void RecordExternalPublish(
        string sessionId,
        string topicName,
        string typeName,
        string jsonPayload,
        bool success,
        string? error = null)
    {
        RecordOutbound(sessionId, new DdsOutboundEntry
        {
            Timestamp = DateTime.UtcNow,
            TopicName = topicName,
            TypeName = typeName,
            JsonPayload = jsonPayload,
            Success = success,
            Error = error,
        });
        StateChanged?.Invoke();
    }

    // ── DataAvailable handler ─────────────────────────────────────

    private void HandleDataAvailable(AnyDataReader anyReader, DdsSubscriptionInfo info)
    {
        if (!info.IsActive) return;
        try
        {
            var typed = (DataReader<DynamicData>)anyReader;
            using var samples = typed.Take();
            var validCount = 0;
            foreach (var s in samples)
            {
                if (!s.Info.ValidData) continue;
                validCount++;
                var json = DdsJsonConverter.ToJson(s.Data!);
                var entry = new DdsSampleEntry
                {
                    SequenceNumber = Interlocked.Increment(ref info.ReceivedCount),
                    TopicName = info.TopicName,
                    TypeName = info.TypeName,
                    ReceivedAt = DateTime.UtcNow,
                    JsonData = json,
                    SourceTimestampNs = s.Info.SourceTimestamp.Seconds * 1_000_000_000L
                                        + s.Info.SourceTimestamp.Nanoseconds,
                };
                info.LastSample = entry;
                RecordLatency(info, entry);
                if (_samples.TryGetValue(info.SubscriptionId, out var buffer))
                    buffer.Append(entry);
                SampleReceived?.Invoke(info, entry);
            }
            if (validCount > 0)
            {
                _logger.LogInformation(
                    "DDS samples received session={SessionId} topic={Topic} count={Count}",
                    info.SessionId,
                    info.TopicName,
                    validCount);
            }
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DDS 샘플 수신 처리 실패: {Topic}", info.TopicName);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private void RecordOutbound(string sessionId, DdsOutboundEntry entry)
    {
        var buffer = _outbound.GetOrAdd(sessionId, _ => new EntryBuffer<DdsOutboundEntry>());
        buffer.Append(entry);
    }

    private static void RecordLatency(DdsSubscriptionInfo info, DdsSampleEntry entry)
    {
        if (entry.SourceTimestampNs <= 0)
            return;

        var source = DateTimeOffset.FromUnixTimeMilliseconds(entry.SourceTimestampNs / 1_000_000).UtcDateTime;
        var latencyMs = (entry.ReceivedAt - source).TotalMilliseconds;
        if (latencyMs < 0 || double.IsNaN(latencyMs) || double.IsInfinity(latencyMs))
            return;

        lock (info)
        {
            info.TotalLatencyMs += latencyMs;
            info.LatencySampleCount++;
        }
    }

    private static string QualifyProfile(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName)) return string.Empty;
        return profileName.Contains("::") ? profileName : $"AmbassadorProfiles::{profileName}";
    }

    private static string BuildReaderKey(string sessionId, string topicName)
        => $"{sessionId}|{topicName}";
}

public sealed record DdsPublishResult(bool Success, string? Error);

public sealed class DdsOutboundEntry
{
    public required DateTime Timestamp { get; init; }
    public required string TopicName { get; init; }
    public required string TypeName { get; init; }
    public required string JsonPayload { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
}

internal sealed class EntryBuffer<T> where T : class
{
    private readonly object _gate = new();
    private const int ChunkSize = 4096;
    private readonly List<T[]> _chunks = [];
    private int _count;
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    public void Append(T item)
    {
        lock (_gate)
        {
            var chunkIndex = _count / ChunkSize;
            var offset = _count % ChunkSize;
            if (offset == 0)
                _chunks.Add(new T[ChunkSize]);

            _chunks[chunkIndex][offset] = item;
            _count++;
            _version++;
        }
    }

    public List<T> SnapshotNewestFirst(int max)
    {
        lock (_gate)
        {
            var count = max == int.MaxValue ? _count : Math.Min(Math.Max(0, max), _count);
            if (count == 0)
                return [];

            var result = new List<T>(count);
            for (var index = _count - 1; index >= _count - count; index--)
            {
                var item = _chunks[index / ChunkSize][index % ChunkSize];
                if (item != null)
                    result.Add(item);
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _chunks.Clear();
            _count = 0;
            _version++;
        }
    }
}
