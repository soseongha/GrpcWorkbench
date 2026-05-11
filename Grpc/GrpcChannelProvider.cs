using Grpc.Net.Client;
using GrpcWorkbench.Models.Session;
using System.Net;
using System.Net.Sockets;

namespace GrpcWorkbench.Grpc;

public interface IGrpcChannelProvider
{
    Task<GrpcChannel> GetChannelAsync(GrpcSession session);
    void ClearChannel(string sessionId);
}

public class GrpcChannelProvider : IGrpcChannelProvider
{
    private readonly Dictionary<string, GrpcChannel> _channels = new();
    private readonly ILogger<GrpcChannelProvider> _logger;

    public GrpcChannelProvider(ILogger<GrpcChannelProvider> logger)
    {
        _logger = logger;
    }

    public Task<GrpcChannel> GetChannelAsync(GrpcSession session)
    {
        // UDS 사용 시
        if (session.UseUnixDomainSocket && !string.IsNullOrEmpty(session.UnixSocketPath))
        {
            return GetUnixDomainSocketChannelAsync(session);
        }

        // TCP 사용 시
        var scheme = session.UseTls ? "https" : "http";
        var key = $"{scheme}://{session.Address}:{session.Port}";

        if (_channels.TryGetValue(key, out var channel) && channel != null)
        {
            return Task.FromResult(channel);
        }

        var httpHandler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };

        // TLS를 사용하지 않을 경우 인증서 검증 무시
        if (!session.UseTls)
        {
            httpHandler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true
            };
        }

        var options = new GrpcChannelOptions
        {
            HttpHandler = httpHandler,
            MaxReceiveMessageSize = 100 * 1024 * 1024, // 100 MB
            MaxSendMessageSize = 100 * 1024 * 1024,    // 100 MB
            MaxRetryAttempts = 3,
            MaxRetryBufferSize = 16 * 1024 * 1024,     // 16 MB
            MaxRetryBufferPerCallSize = 1024 * 1024    // 1 MB
        };

        var newChannel = GrpcChannel.ForAddress(key, options);
        _channels[key] = newChannel;

        _logger.LogInformation("Created gRPC channel to {Key} (TLS: {UseTls})", key, session.UseTls);

        return Task.FromResult(newChannel);
    }

    private Task<GrpcChannel> GetUnixDomainSocketChannelAsync(GrpcSession session)
    {
        var key = $"unix://{session.UnixSocketPath}";

        if (_channels.TryGetValue(key, out var channel) && channel != null)
        {
            return Task.FromResult(channel);
        }

        var udsEndPoint = new UnixDomainSocketEndPoint(session.UnixSocketPath!);
        var connectionFactory = new UnixDomainSocketConnectionFactory(udsEndPoint);

        var socketsHttpHandler = new SocketsHttpHandler
        {
            ConnectCallback = connectionFactory.ConnectAsync,
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5)
        };

        var channelOptions = new GrpcChannelOptions
        {
            HttpHandler = socketsHttpHandler,
            MaxReceiveMessageSize = 100 * 1024 * 1024,
            MaxSendMessageSize = 100 * 1024 * 1024,
            MaxRetryAttempts = 3,
            MaxRetryBufferSize = 16 * 1024 * 1024,
            MaxRetryBufferPerCallSize = 1024 * 1024
        };

        // UDS는 http://로 시작해야 함 (실제 연결은 ConnectCallback에서 처리)
        var newChannel = GrpcChannel.ForAddress("http://localhost", channelOptions);
        _channels[key] = newChannel;

        _logger.LogInformation("Created gRPC channel for UDS: {UnixSocketPath}", session.UnixSocketPath);

        return Task.FromResult(newChannel);
    }

    public void ClearChannel(string sessionId)
    {
        foreach (var channel in _channels.Values)
        {
            channel.Dispose();
        }
        _channels.Clear();
        _logger.LogInformation("Cleared gRPC channels");
    }
}

/// <summary>
/// Unix Domain Socket을 위한 연결 팩토리
/// </summary>
internal class UnixDomainSocketConnectionFactory
{
    private readonly EndPoint _endPoint;

    public UnixDomainSocketConnectionFactory(EndPoint endPoint)
    {
        _endPoint = endPoint;
    }

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            await socket.ConnectAsync(_endPoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
