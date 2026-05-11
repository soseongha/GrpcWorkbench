using GrpcWorkbench.Grpc;
using GrpcWorkbench.Models.Api;
using GrpcWorkbench.Models.Session;
using GrpcWorkbench.Services;
using Microsoft.AspNetCore.Mvc;
using Grpc.Health.V1;
using GrpcChannel = global::Grpc.Net.Client.GrpcChannel;
using GrpcChannelOptions = global::Grpc.Net.Client.GrpcChannelOptions;

namespace GrpcWorkbench.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProtoController : ControllerBase
{
    private readonly ISessionService _sessionService;
    private readonly IProtoLoader _protoLoader;
    private readonly ILogger<ProtoController> _logger;

    public ProtoController(
        ISessionService sessionService,
        IProtoLoader protoLoader,
        ILogger<ProtoController> logger)
    {
        _sessionService = sessionService;
        _protoLoader = protoLoader;
        _logger = logger;
    }

    [HttpPost("upload")]
    public async Task<IActionResult> UploadProto([FromForm] ProtoUploadRequest request)
    {
        try
        {
            if (request.ProtoFile == null || request.ProtoFile.Length == 0)
                return BadRequest("Proto file is required");

            // 파일 읽기
            using var memoryStream = new MemoryStream();
            await request.ProtoFile.CopyToAsync(memoryStream);
            var protoContent = memoryStream.ToArray();

            // Proto 파싱 (먼저 검증)
            var services = await _protoLoader.LoadProtoServicesAsync(protoContent);

            if (services.Count == 0)
                return BadRequest("No services found in proto file");

            // 기존 세션 확인 (없으면 새로 생성)
            GrpcSession session;
            if (!string.IsNullOrEmpty(request.SessionId))
            {
                session = _sessionService.GetSession(request.SessionId);
                if (session == null)
                {
                    // 세션이 없으면 새로 생성 (새로고침 후 재연결)
                    _logger.LogWarning("Session {SessionId} not found, creating new session", request.SessionId);
                    session = _sessionService.CreateSession(
                        request.Address ?? "localhost",
                        request.Port);
                }
            }
            else
            {
                // 세션이 없으면 생성
                session = _sessionService.CreateSession(
                    request.Address ?? "localhost",
                    request.Port);
            }

            // Proto 업데이트
            await _sessionService.UpdateSessionProtoAsync(
                session.SessionId,
                protoContent,
                request.ProtoFile.FileName);

            session.Services = services;

            _logger.LogInformation("Uploaded proto file: {FileName}", request.ProtoFile.FileName);

            return Ok(new
            {
                sessionId = session.SessionId,
                services = services,
                address = session.Address,
                port = session.Port
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Proto upload failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("create-session")]
    public IActionResult CreateSession([FromBody] CreateSessionRequest request)
    {
        try
        {
            GrpcSession session;

            if (request.UseUnixDomainSocket && !string.IsNullOrEmpty(request.UnixSocketPath))
            {
                session = _sessionService.CreateSession(
                    request.Address ?? "localhost",
                    request.Port ?? 50051,
                    request.UseTls,
                    true,
                    request.UnixSocketPath);
            }
            else
            {
                session = _sessionService.CreateSession(
                    request.Address ?? "localhost",
                    request.Port ?? 50051,
                    request.UseTls);
            }

            return Ok(new
            {
                sessionId = session.SessionId,
                address = session.Address,
                port = session.Port,
                useTls = session.UseTls,
                useUnixDomainSocket = session.UseUnixDomainSocket,
                unixSocketPath = session.UnixSocketPath
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session creation failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("session/{sessionId}")]
    public IActionResult GetSession(string sessionId)
    {
        try
        {
            var session = _sessionService.GetSession(sessionId);
            if (session == null)
                return NotFound("Session not found");

            return Ok(new
            {
                sessionId = session.SessionId,
                address = session.Address,
                port = session.Port,
                protoFileName = session.ProtoFileName,
                services = session.Services
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get session");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpDelete("session/{sessionId}")]
    public IActionResult DeleteSession(string sessionId)
    {
        try
        {
            _sessionService.DeleteSession(sessionId);
            return Ok("Session deleted");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session deletion failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("upload-text")]
    public async Task<IActionResult> UploadProtoText([FromBody] ProtoTextUploadRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.ProtoText))
                return BadRequest("Proto text is required");

            var protoContent = System.Text.Encoding.UTF8.GetBytes(request.ProtoText);
            var services = await _protoLoader.LoadProtoServicesAsync(protoContent);

            if (services.Count == 0)
                return BadRequest("No services found in proto definition");

            GrpcSession session;
            if (!string.IsNullOrEmpty(request.SessionId))
            {
                session = _sessionService.GetSession(request.SessionId)
                    ?? _sessionService.CreateSession(request.Address ?? "localhost", request.Port);
            }
            else
            {
                session = _sessionService.CreateSession(request.Address ?? "localhost", request.Port);
            }

            await _sessionService.UpdateSessionProtoAsync(session.SessionId, protoContent, "editor.proto");
            session.Services = services;

            return Ok(new
            {
                sessionId = session.SessionId,
                services,
                address = session.Address,
                port = session.Port
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Proto text upload failed");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("health-check/{sessionId}")]
    public async Task<IActionResult> HealthCheck(string sessionId)
    {
        try
        {
            var session = _sessionService.GetSession(sessionId);
            if (session == null)
                return NotFound(new { status = "disconnected", message = "Session not found" });

            var scheme = session.UseTls ? "https" : "http";
            var address = $"{scheme}://{session.Address}:{session.Port}";

            using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    ConnectTimeout = TimeSpan.FromSeconds(3)
                }
            });

            var client = new Health.HealthClient(channel);

            try
            {
                var response = await client.CheckAsync(
                    new HealthCheckRequest(),
                    deadline: DateTime.UtcNow.AddSeconds(3));

                return Ok(new
                {
                    status = "connected",
                    message = $"Server is {response.Status}"
                });
            }
            catch
            {
                // Health 서비스가 없어도 TCP 연결이 되면 reachable
                try
                {
                    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                    await httpClient.GetAsync(address);
                    return Ok(new { status = "connected", message = "Server is reachable" });
                }
                catch (HttpRequestException)
                {
                    // HTTP/2 연결 시도 - 거부되지 않으면 서버가 있음
                    try
                    {
                        using var tcpClient = new System.Net.Sockets.TcpClient();
                        var connectTask = tcpClient.ConnectAsync(session.Address, session.Port);
                        if (await Task.WhenAny(connectTask, Task.Delay(3000)) == connectTask)
                        {
                            return Ok(new { status = "connected", message = "Server is reachable (TCP)" });
                        }
                    }
                    catch { }

                    return Ok(new { status = "disconnected", message = "Server is unreachable" });
                }
            }
        }
        catch (Exception ex)
        {
            //_logger.LogError(ex, "Health check failed");
            return Ok(new { status = "disconnected", message = ex.Message });
        }
    }


}

public class ProtoTextUploadRequest
{
    public string? SessionId { get; set; }
    public string ProtoText { get; set; } = string.Empty;
    public string? Address { get; set; }
    public int Port { get; set; } = 50051;
}
