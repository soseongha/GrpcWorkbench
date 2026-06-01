using ASAP.Models.Grpc;
using ASAP.Models.Session;
using ASAP.Models.Triggers;
using ASAP.Models.Ui;
using LogLevel = ASAP.Models.Ui.LogLevel;

namespace ASAP.Services;

/// <summary>
/// 회로(circuit)와 독립적으로 살아있는 워크벤치 누적 상태 보관자.
/// 미들웨어 알림을 직접 구독해 IncomingCalls / Logs / StreamRecv 등을 누적하고,
/// 1-탭 정책: ClaimActive로 새 클라이언트가 진입하면 이전 클라이언트는 Evicted 통지.
/// 모든 상태 변경은 _lock 안에서 수행, 렌더 측은 Snapshot* 으로 안전 사본을 얻는다.
/// </summary>
public class WorkbenchStateService : IDisposable
{
    private readonly WorkbenchNotificationService _notify;
    private readonly object _lock = new();
    private readonly Timer _incomingChangedTimer;

    // ── 누적 상태 ──────────────────────────────────────────────────────────
    // RPC(Service.Method)별 집계. 대규모 부하 대응으로 flat 콜 리스트 대신 사용.
    private readonly Dictionary<string, RpcAggregate> _aggregates = new(StringComparer.Ordinal);
    // 미들웨어가 CallId로 프레임을 보내므로 빠른 조회용 인덱스.
    private readonly Dictionary<string, IncomingCallVm> _callIndex = new(StringComparer.Ordinal);
    private readonly List<LogEntry> _logs = [];
    private readonly List<OutboundMessageEntry> _outbound = [];
    private readonly List<string> _streamRecv = [];

    // ── 발신/선택 상태 ─────────────────────────────────────────────────────
    public GrpcSession? Session { get; private set; }
    public string? StreamId { get; private set; }
    public bool IsStreamOpen => StreamId != null;
    public string? ActiveStreamSessionId { get; private set; }
    public string? ActiveStreamServiceName { get; private set; }
    public string? ActiveStreamMethodName { get; private set; }
    public string? ActiveStreamRpcType { get; private set; }
    public int SentCount { get; private set; }

    public List<ServiceMetadata> Services { get; private set; } = [];
    public string? SelectedServiceName { get; private set; }
    public string? SelectedMethodName { get; private set; }

    public bool IncomingPaused { get; private set; }

    // ── Triggers ───────────────────────────────────────────────────────────
    private readonly List<Trigger> _triggers = [];
    public event Action? TriggersChanged;
    public int TriggerCount { get { lock (_lock) return _triggers.Count; } }

    // 카운트 (lock 안에서)
    public int RpcCount { get { lock (_lock) return _aggregates.Count; } }
    public int ActiveCallsTotal
    {
        get { lock (_lock) return _aggregates.Values.Sum(a => a.ActiveCalls); }
    }
    public double TotalRatePerSec
    {
        get { lock (_lock) return _aggregates.Values.Sum(a => a.RatePerSec); }
    }
    public int LogCount { get { lock (_lock) return _logs.Count; } }
    public int OutboundCount { get { lock (_lock) return _outbound.Count; } }
    public int StreamRecvCount { get { lock (_lock) return _streamRecv.Count; } }

    private const int MaxRecentCallsPerRpc = 10;
    private const int DefaultFramesPerCall = 24;
    private const int MaxFramesBuffered = 200;
    private const int MaxLogs = 500;
    private const int MaxStreamRecv = 500;
    private const int MaxOutboundEntries = 5000;
    private const int MaxRpcAggregates = 256;
    private static readonly TimeSpan IncomingUiRefreshInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ActiveDisplayHold = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan ActiveStaleTimeout = TimeSpan.FromSeconds(8);
    private static readonly long IncomingUiRefreshIntervalMs = (long)IncomingUiRefreshInterval.TotalMilliseconds;
    private int _incomingChangedScheduled;
    private long _lastIncomingChangedAtMs;

    public event Action? Changed;

    // ── 1-탭 정책 ──────────────────────────────────────────────────────────
    private Guid? _activeClient;
    public event Action? Evicted;

    public WorkbenchStateService(WorkbenchNotificationService notify)
    {
        _notify = notify;
        _lastIncomingChangedAtMs = Environment.TickCount64;
        _incomingChangedTimer = new Timer(_ => FlushIncomingChanged(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _notify.CallStarted += OnCallStarted;
        _notify.StreamMessageReceived += OnStreamMessage;
        _notify.CallEnded += OnCallEnded;
    }

    public void Dispose()
    {
        _incomingChangedTimer.Dispose();
        _notify.CallStarted -= OnCallStarted;
        _notify.StreamMessageReceived -= OnStreamMessage;
        _notify.CallEnded -= OnCallEnded;
    }

    // ── 스냅샷 (렌더 측에서 호출) ──────────────────────────────────────────
    // 락 안에서 얕은 사본을 만들어 enumerator 동시 변경 예외를 막는다.
    public IReadOnlyList<RpcAggregate> SnapshotAggregates()
    {
        lock (_lock) return [.. _aggregates.Values];
    }

    public IReadOnlyList<IncomingCallVm> SnapshotRecentCalls(RpcAggregate agg)
    {
        lock (_lock) return [.. agg.RecentCalls];
    }

    public IReadOnlyList<FrameVm> SnapshotFrames(IncomingCallVm call)
    {
        lock (_lock) return [.. call.Frames];
    }

    public IReadOnlyList<LogEntry> SnapshotLogs()
    {
        lock (_lock) return [.. _logs];
    }

    public IReadOnlyList<string> SnapshotStreamRecv()
    {
        lock (_lock) return [.. _streamRecv];
    }

    public IReadOnlyList<OutboundMessageEntry> SnapshotOutbound()
    {
        lock (_lock) return [.. _outbound];
    }

    public int GetDisplayedActiveCalls(RpcAggregate agg)
    {
        lock (_lock) return ComputeDisplayedActiveCalls(agg, DateTime.UtcNow);
    }

    // ── 미들웨어 알림 핸들러 ───────────────────────────────────────────────
    private void OnCallStarted(IncomingCallStartedEvent e)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var key = $"{e.Service}.{e.Method}";
            if (!_aggregates.TryGetValue(key, out var agg))
            {
                agg = new RpcAggregate(e.Service, e.Method, e.Type);
                _aggregates[key] = agg;
                PruneOldInactiveAggregatesUnderLock(MaxRpcAggregates);
            }
            var call = new IncomingCallVm(e.CallId, e.Service, e.Method, e.Type, e.SessionId);
            agg.RecentCalls.Add(call);
            if (agg.RecentCalls.Count > MaxRecentCallsPerRpc)
            {
                var evicted = agg.RecentCalls[0];
                agg.RecentCalls.RemoveAt(0);
                if (evicted.Result == null)
                    agg.ActiveCalls = Math.Max(0, agg.ActiveCalls - 1);
                _callIndex.Remove(evicted.CallId);
            }
            _callIndex[e.CallId] = call;
            agg.ActiveCalls++;
            agg.TotalCalls++;
            agg.LastSeenAt = now;
        }
        SignalIncomingChanged();
    }

    private void OnStreamMessage(IncomingStreamMessageEvent e)
    {
        lock (_lock)
        {
            if (!_callIndex.TryGetValue(e.CallId, out var call)) return;
            var key = $"{call.Service}.{call.Method}";
            if (!_aggregates.TryGetValue(key, out var agg)) return;
            var now = DateTime.UtcNow;

            // 기본 모드에서도 최근 일부 프레임은 남겨 상세 확인이 가능해야 한다.
            var cap = agg.BufferMode ? MaxFramesBuffered : DefaultFramesPerCall;
            if (call.Frames.Count >= cap) call.Frames.RemoveAt(0);
            call.Frames.Add(new FrameVm(e.FrameIndex, e.Data, now));
            call.LastActivityAt = now;

            agg.TotalFrames++;
            agg.LastSeenAt = now;
            agg.RecordFrame(now);
        }
        SignalIncomingChanged();
    }

    private void OnCallEnded(IncomingCallEndedEvent e)
    {
        lock (_lock)
        {
            if (!_callIndex.TryGetValue(e.CallId, out var call)) return;
            call.Result = e.Res;
            call.EndedAt = DateTime.UtcNow;
            call.LastActivityAt = call.EndedAt.Value;
            var key = $"{call.Service}.{call.Method}";
            if (_aggregates.TryGetValue(key, out var agg))
            {
                agg.ActiveCalls = Math.Max(0, agg.ActiveCalls - 1);
                agg.LastSeenAt = DateTime.UtcNow;
            }
        }
        SignalIncomingChanged();
    }

    // ── UI 호출 ────────────────────────────────────────────────────────────
    public void ClearIncoming()
    {
        lock (_lock)
        {
            _aggregates.Clear();
            _callIndex.Clear();
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<Trigger> SnapshotTriggers()
    {
        lock (_lock) return [.. _triggers];
    }

    public IncomingCallVm? FindCallById(string callId)
    {
        lock (_lock) return _callIndex.TryGetValue(callId, out var c) ? c : null;
    }

    public void AddTrigger(Trigger t)
    {
        lock (_lock) _triggers.Add(t);
        TriggersChanged?.Invoke();
        Changed?.Invoke();
    }

    public void RemoveTrigger(string id)
    {
        lock (_lock) _triggers.RemoveAll(x => x.Id == id);
        TriggersChanged?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>
    /// Trigger 필드를 직접 수정한 뒤 호출 — Executor에 동기화 신호 + UI 갱신.
    /// </summary>
    public void NotifyTriggersChanged()
    {
        TriggersChanged?.Invoke();
        Changed?.Invoke();
    }

    public void SetBufferMode(string aggKey, bool on)
    {
        lock (_lock)
        {
            if (!_aggregates.TryGetValue(aggKey, out var agg)) return;
            if (agg.BufferMode == on) return;
            agg.BufferMode = on;
            // OFF 전환 시에도 최근 몇 개 프레임은 남겨 상세 확인이 가능하게 유지.
            if (!on)
            {
                foreach (var call in agg.RecentCalls)
                {
                    if (call.Frames.Count > DefaultFramesPerCall)
                        call.Frames.RemoveRange(0, call.Frames.Count - DefaultFramesPerCall);
                }
            }
        }
        Changed?.Invoke();
    }

    public void ClearLogs()
    {
        lock (_lock) _logs.Clear();
        Changed?.Invoke();
    }

    public void ClearOutbound()
    {
        lock (_lock) _outbound.Clear();
        Changed?.Invoke();
    }

    public int TrimOutbound(int keepLatest)
    {
        if (keepLatest < 0) keepLatest = 0;

        int removed;
        lock (_lock)
        {
            removed = Math.Max(0, _outbound.Count - keepLatest);
            if (removed > 0)
                _outbound.RemoveRange(0, removed);
        }

        if (removed > 0) Changed?.Invoke();
        return removed;
    }

    public int TrimIncomingAggregates(int keepLatestInactive)
    {
        if (keepLatestInactive < 0) keepLatestInactive = 0;

        int removed;
        lock (_lock)
        {
            removed = PruneOldInactiveAggregatesUnderLock(keepLatestInactive);
        }

        if (removed > 0) Changed?.Invoke();
        return removed;
    }

    public void SetPaused(bool paused)
    {
        if (IncomingPaused == paused) return;
        IncomingPaused = paused;
        Changed?.Invoke();
    }

    public void AddLog(string text, LogLevel level = LogLevel.Info)
    {
        lock (_lock)
        {
            if (_logs.Count >= MaxLogs) _logs.RemoveAt(0);
            _logs.Add(new LogEntry(DateTime.Now, text, level));
        }
        Changed?.Invoke();
    }

    public void AddOutbound(string service, string method, string json, string source, string? sessionId = null)
    {
        lock (_lock)
        {
            if (_outbound.Count >= MaxOutboundEntries)
                _outbound.RemoveRange(0, _outbound.Count - MaxOutboundEntries + 1);

            _outbound.Add(new OutboundMessageEntry
            {
                SessionId = sessionId,
                Time = DateTime.Now,
                Service = service,
                Method = method,
                Json = json,
                Source = source
            });
        }
        Changed?.Invoke();
    }

    private static int ComputeDisplayedActiveCalls(RpcAggregate agg, DateTime now)
    {
        var active = agg.RecentCalls.Count(call =>
            call.Result == null &&
            now - call.LastActivityAt <= ActiveStaleTimeout);

        if (active > 0) return active;
        return now - agg.LastSeenAt <= ActiveDisplayHold ? 1 : 0;
    }

    private int PruneOldInactiveAggregatesUnderLock(int keepLatestInactive)
    {
        var inactive = _aggregates.Values
            .Where(a => a.ActiveCalls <= 0)
            .OrderByDescending(a => a.LastSeenAt)
            .ToList();

        var removable = inactive.Skip(keepLatestInactive).ToList();
        foreach (var agg in removable)
            RemoveAggregateUnderLock(agg);

        return removable.Count;
    }

    private void RemoveAggregateUnderLock(RpcAggregate aggregate)
    {
        _aggregates.Remove(aggregate.Key);
        foreach (var call in aggregate.RecentCalls)
            _callIndex.Remove(call.CallId);
    }

    private void SignalIncomingChanged()
    {
        if (IncomingPaused) return;

        var now = Environment.TickCount64;
        var elapsed = now - Interlocked.Read(ref _lastIncomingChangedAtMs);
        if (elapsed >= IncomingUiRefreshIntervalMs)
        {
            Interlocked.Exchange(ref _incomingChangedScheduled, 0);
            _incomingChangedTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            Interlocked.Exchange(ref _lastIncomingChangedAtMs, now);
            Changed?.Invoke();
            return;
        }

        if (Interlocked.Exchange(ref _incomingChangedScheduled, 1) == 1) return;

        var dueMs = Math.Max(1, (int)(IncomingUiRefreshIntervalMs - elapsed));
        _incomingChangedTimer.Change(TimeSpan.FromMilliseconds(dueMs), Timeout.InfiniteTimeSpan);
    }

    private void FlushIncomingChanged()
    {
        if (Interlocked.Exchange(ref _incomingChangedScheduled, 0) == 0) return;
        if (IncomingPaused) return;
        Interlocked.Exchange(ref _lastIncomingChangedAtMs, Environment.TickCount64);
        Changed?.Invoke();
    }

    public void SetSession(GrpcSession? session)
    {
        Session = session;
        if (session == null)
        {
            StreamId = null;
            ActiveStreamSessionId = null;
            ActiveStreamServiceName = null;
            ActiveStreamMethodName = null;
            ActiveStreamRpcType = null;
            SentCount = 0;
            lock (_lock) _streamRecv.Clear();
        }
        Changed?.Invoke();
    }

    public void SetStream(string? streamId, string? sessionId = null, string? serviceName = null, string? methodName = null, string? rpcType = null)
    {
        StreamId = streamId;
        ActiveStreamSessionId = streamId == null ? null : sessionId;
        ActiveStreamServiceName = streamId == null ? null : serviceName;
        ActiveStreamMethodName = streamId == null ? null : methodName;
        ActiveStreamRpcType = streamId == null ? null : rpcType;
        Changed?.Invoke();
    }

    public void ResetStream()
    {
        StreamId = null;
        ActiveStreamSessionId = null;
        ActiveStreamServiceName = null;
        ActiveStreamMethodName = null;
        ActiveStreamRpcType = null;
        SentCount = 0;
        lock (_lock) _streamRecv.Clear();
        Changed?.Invoke();
    }

    public void IncrementSent()
    {
        SentCount++;
        Changed?.Invoke();
    }

    public void AddStreamRecv(string callId, string service, string method, string type, string json, string? sessionId = null)
    {
        lock (_lock)
        {
            if (_streamRecv.Count >= MaxStreamRecv) _streamRecv.RemoveAt(0);
            _streamRecv.Add(json);

            var now = DateTime.UtcNow;
            var key = $"{service}.{method}";
            if (!_aggregates.TryGetValue(key, out var agg))
            {
                agg = new RpcAggregate(service, method, type);
                _aggregates[key] = agg;
                PruneOldInactiveAggregatesUnderLock(MaxRpcAggregates);
            }

            if (!_callIndex.TryGetValue(callId, out var call))
            {
                call = new IncomingCallVm(callId, service, method, type, sessionId);
                agg.RecentCalls.Add(call);
                if (agg.RecentCalls.Count > MaxRecentCallsPerRpc)
                {
                    var evicted = agg.RecentCalls[0];
                    agg.RecentCalls.RemoveAt(0);
                    if (evicted.Result == null)
                        agg.ActiveCalls = Math.Max(0, agg.ActiveCalls - 1);
                    _callIndex.Remove(evicted.CallId);
                }

                _callIndex[callId] = call;
                agg.ActiveCalls++;
                agg.TotalCalls++;
            }

            var cap = agg.BufferMode ? MaxFramesBuffered : DefaultFramesPerCall;
            if (call.Frames.Count >= cap) call.Frames.RemoveAt(0);
            call.Frames.Add(new FrameVm(call.Frames.Count + 1, json, now));
            call.LastActivityAt = now;

            agg.TotalFrames++;
            agg.LastSeenAt = now;
            agg.RecordFrame(now);
        }
        SignalIncomingChanged();
    }

    public void CompleteSyntheticStreamCall(string callId, string? result)
    {
        lock (_lock)
        {
            if (!_callIndex.TryGetValue(callId, out var call)) return;
            if (call.Result != null) return;

            call.Result = string.IsNullOrWhiteSpace(result) ? "종료됨" : result;
            call.EndedAt = DateTime.UtcNow;
            call.LastActivityAt = call.EndedAt.Value;

            var key = $"{call.Service}.{call.Method}";
            if (_aggregates.TryGetValue(key, out var agg))
            {
                agg.ActiveCalls = Math.Max(0, agg.ActiveCalls - 1);
                agg.LastSeenAt = call.EndedAt.Value;
            }
        }
        SignalIncomingChanged();
    }

    public void SetServices(List<ServiceMetadata> services)
    {
        Services = services;
        Changed?.Invoke();
    }

    public void SetSelected(string? serviceName, string? methodName)
    {
        SelectedServiceName = serviceName;
        SelectedMethodName = methodName;
        Changed?.Invoke();
    }

    // ── 1-탭 정책 ──────────────────────────────────────────────────────────
    // 새 탭이 진입하면 이전 활성 클라이언트는 Evicted 통지를 받고 오버레이 표시.
    public Guid ClaimActive()
    {
        Guid newId;
        bool hadPrevious;
        lock (_lock)
        {
            hadPrevious = _activeClient != null;
            newId = Guid.NewGuid();
            _activeClient = newId;
        }
        if (hadPrevious) Evicted?.Invoke();
        return newId;
    }

    public void Release(Guid clientId)
    {
        lock (_lock)
        {
            if (_activeClient == clientId) _activeClient = null;
        }
    }

    public bool IsActive(Guid clientId)
    {
        lock (_lock) return _activeClient == clientId;
    }
}
