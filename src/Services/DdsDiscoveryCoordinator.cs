using System.Net;
using System.Net.Sockets;

namespace ASAP.Services;

public sealed class DdsDiscoveryCoordinator : IHostedService, IDisposable
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DdsDiscoveryCoordinator> _logger;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private List<string> _peers = [];

    public DdsDiscoveryCoordinator(
        IConfiguration configuration,
        ILogger<DdsDiscoveryCoordinator> logger)
    {
        _configuration = configuration;
        _logger = logger;
        var refreshSeconds = Math.Clamp(
            _configuration.GetValue("ASAP:DdsDiscoveryRefreshSeconds", 10),
            2,
            300);
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(refreshSeconds));
    }

    public IReadOnlyList<string> SnapshotPeers()
    {
        lock (_gate)
            return _peers.ToArray();
    }

    public async Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        var peers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var peer in ParseList(_configuration["ASAP:DdsInitialPeers"]))
            peers.Add(NormalizeLocator(peer));

        foreach (var host in ParseList(_configuration["ASAP:DdsDiscoveryPeerHosts"]))
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
                foreach (var address in addresses.Where(IsUsableAddress))
                    peers.Add(NormalizeLocator(address.ToString()));
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                    _logger.LogDebug(ex, "DDS discovery peer host resolve failed: {Host}", host);
            }
        }

        lock (_gate)
            _peers = peers.Where(x => !string.IsNullOrWhiteSpace(x)).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(RunAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _timer.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            await RefreshNowAsync(_cts.Token);
            while (await _timer.WaitForNextTickAsync(_cts.Token))
                await RefreshNowAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static IEnumerable<string> ParseList(string? value)
        => (value ?? string.Empty)
            .Split([',', ';', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x));

    private static string NormalizeLocator(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        return trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed
            : $"udpv4://{trimmed}";
    }

    private static bool IsUsableAddress(IPAddress address)
        => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
           && !IPAddress.IsLoopback(address);
}
