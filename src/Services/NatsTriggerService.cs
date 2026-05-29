using System.Collections.Concurrent;
using System.Reflection;
using ASAP.Models.Nats;
using ASAP.Grpc;
using ASAP.Nats;
using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;

namespace ASAP.Services;

public sealed class NatsTriggerService : IAsyncDisposable
{
    private readonly INatsSessionService _natsSessions;
    private readonly ILogger<NatsTriggerService> _logger;
    private readonly IDynamicProtoCompiler _protoCompiler;
    private readonly IJsonMessageConverter _jsonMessageConverter;
    private readonly IWebHostEnvironment _environment;

    private readonly ConcurrentDictionary<string, NatsTrigger> _triggers = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _periodicCts = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastOnIncomingFireAt = new();
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _onIncomingFireWindow = new();
    private Task<Assembly>? _bridgeSchemaAssemblyTask;

    public event Action? Changed;

    public NatsTriggerService(
        INatsSessionService natsSessions,
        ILogger<NatsTriggerService> logger,
        IDynamicProtoCompiler protoCompiler,
        IJsonMessageConverter jsonMessageConverter,
        IWebHostEnvironment environment)
    {
        _natsSessions = natsSessions;
        _logger = logger;
        _protoCompiler = protoCompiler;
        _jsonMessageConverter = jsonMessageConverter;
        _environment = environment;
        _natsSessions.MessageReceived += OnMessageReceived;
    }

    public IReadOnlyList<NatsTrigger> Snapshot(string? sessionId = null) =>
        _triggers.Values
            .Where(trigger => sessionId == null || trigger.SessionId == sessionId)
            .OrderBy(trigger => trigger.Name)
            .ThenBy(trigger => trigger.Subject)
            .ToList();

    public NatsTrigger Add(NatsTrigger trigger)
    {
        _triggers[trigger.Id] = trigger;
        if (trigger.Enabled && trigger.Type == NatsTriggerType.Periodic)
            StartPeriodic(trigger);
        Changed?.Invoke();
        return trigger;
    }

    public void SetEnabled(string triggerId, bool enabled)
    {
        if (!_triggers.TryGetValue(triggerId, out var trigger))
            return;

        trigger.Enabled = enabled;
        StopPeriodic(triggerId);
        if (enabled && trigger.Type == NatsTriggerType.Periodic)
            StartPeriodic(trigger);
        Changed?.Invoke();
    }

    public void Remove(string triggerId)
    {
        StopPeriodic(triggerId);
        _triggers.TryRemove(triggerId, out _);
        _lastOnIncomingFireAt.TryRemove(triggerId, out _);
        _onIncomingFireWindow.TryRemove(triggerId, out _);
        Changed?.Invoke();
    }

    public async Task<(bool Success, string? Error)> FireOnceAsync(string triggerId)
    {
        if (!_triggers.TryGetValue(triggerId, out var trigger))
            return (false, "�ڵ� ���� �׸��� �����ϴ�");

        return await PublishAsync(trigger);
    }

    public async Task FireBulkAsync(string triggerId)
    {
        if (!_triggers.TryGetValue(triggerId, out var trigger))
            return;

        if (trigger.BulkParallel)
        {
            await Task.WhenAll(Enumerable.Range(0, trigger.BulkCount).Select(_ => PublishAsync(trigger)));
        }
        else
        {
            for (var index = 0; index < trigger.BulkCount; index++)
                await PublishAsync(trigger);
        }

        Changed?.Invoke();
    }

    private void StartPeriodic(NatsTrigger trigger)
    {
        var cts = new CancellationTokenSource();
        _periodicCts[trigger.Id] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (trigger.MaxFires.HasValue && trigger.TotalFires >= trigger.MaxFires.Value)
                        break;

                    await PublishAsync(trigger);
                    Changed?.Invoke();
                    try
                    {
                        await Task.Delay(Math.Max(1, trigger.IntervalMs), cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NATS periodic trigger failed: {Id}", trigger.Id);
            }
        }, cts.Token);
    }

    private void StopPeriodic(string triggerId)
    {
        if (_periodicCts.TryRemove(triggerId, out var cts))
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
            }
        }
    }

    private async void OnMessageReceived(string sessionId, NatsMessageEntry message)
    {
        var now = DateTime.UtcNow;
        foreach (var trigger in _triggers.Values)
        {
            if (!trigger.Enabled || trigger.Type != NatsTriggerType.OnIncoming)
                continue;
            if (!string.Equals(trigger.SessionId, sessionId, StringComparison.Ordinal))
                continue;
            if (!string.IsNullOrWhiteSpace(trigger.MatchSubject) &&
                !MatchesSubjectPattern(trigger.MatchSubject, message.Subject))
                continue;
            if (trigger.BlockSelfSubjectLoop && string.Equals(trigger.Subject, message.Subject, StringComparison.Ordinal))
                continue;
            if (trigger.MinFireIntervalMs > 0 &&
                _lastOnIncomingFireAt.TryGetValue(trigger.Id, out var lastAt) &&
                (now - lastAt).TotalMilliseconds < trigger.MinFireIntervalMs)
                continue;

            if (trigger.MaxFiresPerMinute > 0)
            {
                var window = _onIncomingFireWindow.GetOrAdd(trigger.Id, _ => new Queue<DateTime>());
                lock (window)
                {
                    while (window.Count > 0 && (now - window.Peek()).TotalSeconds > 60)
                        window.Dequeue();
                    if (window.Count >= trigger.MaxFiresPerMinute)
                        continue;
                }
            }

            await PublishAsync(trigger);
            _lastOnIncomingFireAt[trigger.Id] = now;
            if (trigger.MaxFiresPerMinute > 0)
            {
                var window = _onIncomingFireWindow.GetOrAdd(trigger.Id, _ => new Queue<DateTime>());
                lock (window)
                {
                    window.Enqueue(now);
                    while (window.Count > 0 && (now - window.Peek()).TotalSeconds > 60)
                        window.Dequeue();
                }
            }
            Changed?.Invoke();
        }
    }

    private async Task<(bool Success, string? Error)> PublishAsync(NatsTrigger trigger)
    {
        try
        {
            if (trigger.UseBridgeMessage)
            {
                var bridgeMessageType = ResolveBridgeMessageType(trigger.BridgeMessageType);
                if (bridgeMessageType == null)
                    throw new InvalidOperationException($"NATS bridge 메시지 타입을 찾을 수 없습니다: {trigger.BridgeMessageType}");

                var message = await _jsonMessageConverter.JsonToMessageAsync(trigger.PayloadText, bridgeMessageType);
                await _natsSessions.PublishBinaryAsync(trigger.SessionId, trigger.Subject, message.ToByteArray(), trigger.PayloadText);
            }
            else
            {
                await _natsSessions.PublishTextAsync(trigger.SessionId, trigger.Subject, trigger.PayloadText);
            }

            Interlocked.Increment(ref trigger.TotalFires);
            trigger.LastFiredAt = DateTime.UtcNow;
            trigger.LastError = null;
            return (true, null);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref trigger.Errors);
            trigger.LastError = ex.Message;
            return (false, ex.Message);
        }
    }

    private Type? ResolveBridgeMessageType(string? messageTypeName)
    {
        if (string.IsNullOrWhiteSpace(messageTypeName))
            return null;

        var assembly = GetBridgeSchemaAssemblyAsync().GetAwaiter().GetResult();
        return assembly.GetTypes().FirstOrDefault(type =>
            typeof(Google.Protobuf.IMessage).IsAssignableFrom(type)
            && string.Equals(type.Name, messageTypeName, StringComparison.Ordinal));
    }

    private Task<Assembly> GetBridgeSchemaAssemblyAsync()
    {
        var current = _bridgeSchemaAssemblyTask;
        if (current != null)
            return current;

        var created = LoadBridgeSchemaAssemblyAsync();
        var existing = Interlocked.CompareExchange(ref _bridgeSchemaAssemblyTask, created, null);
        return existing ?? created;
    }

    private async Task<Assembly> LoadBridgeSchemaAssemblyAsync()
    {
        var protoPath = Path.Combine(_environment.ContentRootPath, "nats", "DDSSim.proto");
        if (!File.Exists(protoPath))
            throw new FileNotFoundException("nats/DDSSim.proto 파일을 찾을 수 없습니다.", protoPath);

        var protoBytes = await File.ReadAllBytesAsync(protoPath);
        return await _protoCompiler.CompileProtoToAssemblyAsync(protoBytes);
    }

    private static bool MatchesSubjectPattern(string pattern, string subject)
    {
        if (string.Equals(pattern, subject, StringComparison.Ordinal))
            return true;

        var patternTokens = pattern.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var subjectTokens = subject.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var patternIndex = 0;
        var subjectIndex = 0;

        while (patternIndex < patternTokens.Length && subjectIndex < subjectTokens.Length)
        {
            var token = patternTokens[patternIndex];
            if (token == ">")
                return patternIndex == patternTokens.Length - 1;
            if (token != "*" && !string.Equals(token, subjectTokens[subjectIndex], StringComparison.Ordinal))
                return false;

            patternIndex++;
            subjectIndex++;
        }

        if (patternIndex == patternTokens.Length && subjectIndex == subjectTokens.Length)
            return true;

        return patternIndex == patternTokens.Length - 1 && patternTokens[patternIndex] == ">";
    }

    public async ValueTask DisposeAsync()
    {
        _natsSessions.MessageReceived -= OnMessageReceived;
        foreach (var triggerId in _periodicCts.Keys.ToList())
            StopPeriodic(triggerId);

        _lastOnIncomingFireAt.Clear();
        _onIncomingFireWindow.Clear();
        await Task.CompletedTask;
    }
}