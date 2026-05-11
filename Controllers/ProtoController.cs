using GrpcWorkbench.Grpc;
using GrpcWorkbench.Models.Api;
using GrpcWorkbench.Models.Session;
using GrpcWorkbench.Services;
using Microsoft.AspNetCore.Mvc;

namespace GrpcWorkbench.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProtoController : ControllerBase
{
    private readonly ISessionService _sessionService;
    private readonly IProtoLoader _protoLoader;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProtoController> _logger;

    public ProtoController(
        ISessionService sessionService,
        IProtoLoader protoLoader,
        IConfiguration configuration,
        ILogger<ProtoController> logger)
    {
        _sessionService = sessionService;
        _protoLoader = protoLoader;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("upload")]
    public async Task<IActionResult> UploadProto([FromForm] ProtoUploadRequest request)
    {
        try
        {
            if (request.ProtoFile == null || request.ProtoFile.Length == 0)
                return BadRequest(new { error = "Proto file is required" });

            // 파일 읽기
            using var memoryStream = new MemoryStream();
            await request.ProtoFile.CopyToAsync(memoryStream);
            var protoContent = memoryStream.ToArray();

            // Proto 파싱 (먼저 검증)
            var services = await _protoLoader.LoadProtoServicesAsync(protoContent);

            if (services.Count == 0)
                return BadRequest(new { error = "No services found in proto file" });

            if (string.IsNullOrEmpty(request.SessionId))
                return BadRequest(new { error = "SessionId is required. Create UDS session first." });

            var session = _sessionService.GetSession(request.SessionId);
            if (session == null)
                return NotFound(new { error = "Session not found. Recreate UDS session." });

            if (!session.UseUnixDomainSocket || string.IsNullOrWhiteSpace(session.UnixSocketPath))
                return BadRequest(new { error = "Only UDS session is supported." });

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
    public IActionResult CreateSession()
    {
        try
        {
            var unixSocketPath = _configuration["UDS_SOCKET_PATH"]
                ?? _configuration["GrpcWorkbench:UdsSocketPath"];

            if (string.IsNullOrWhiteSpace(unixSocketPath))
                return BadRequest(new
                {
                    error = "UDS socket path is not configured. Set UDS_SOCKET_PATH env var or GrpcWorkbench:UdsSocketPath setting."
                });

            var session = _sessionService.CreateSession(
                "localhost",
                50051,
                false,
                true,
                unixSocketPath);

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

            if (string.IsNullOrEmpty(request.SessionId))
                return BadRequest(new { error = "SessionId is required. Create UDS session first." });

            var session = _sessionService.GetSession(request.SessionId);
            if (session == null)
                return NotFound(new { error = "Session not found. Recreate UDS session." });

            if (!session.UseUnixDomainSocket || string.IsNullOrWhiteSpace(session.UnixSocketPath))
                return BadRequest(new { error = "Only UDS session is supported." });

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

            // UDS 세션은 소켓 파일 존재 여부 + 실제 연결 시도로 확인
            if (session.UseUnixDomainSocket && !string.IsNullOrEmpty(session.UnixSocketPath))
            {
                if (!System.IO.File.Exists(session.UnixSocketPath))
                    return Ok(new { status = "disconnected", message = $"소켓 파일 없음: {session.UnixSocketPath}" });

                try
                {
                    var udsEndPoint = new System.Net.Sockets.UnixDomainSocketEndPoint(session.UnixSocketPath);
                    using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                    var connectTask = socket.ConnectAsync(udsEndPoint);
                    if (await Task.WhenAny(connectTask, Task.Delay(3000)) == connectTask)
                        return Ok(new { status = "connected", message = "UDS 서버 연결됨" });
                    return Ok(new { status = "disconnected", message = "UDS 연결 타임아웃" });
                }
                catch (Exception ex)
                {
                    return Ok(new { status = "disconnected", message = $"UDS 연결 실패: {ex.Message}" });
                }
            }

            return Ok(new { status = "disconnected", message = "Only UDS session is supported." });
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
