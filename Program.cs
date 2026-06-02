using ASAP.Dds;
using ASAP.Grpc;
using ASAP.Nats;
using ASAP.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.SignalR;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// ── UDS socket path: this instance's gRPC server listens here ───────────────
var udsPath = Environment.GetEnvironmentVariable("UDS_SOCKET_PATH")
              ?? builder.Configuration["ASAP:UdsSocketPath"]
              ?? "/var/run/grpc-test/grpc.sock";

// ── Parse web port from configured URLs (default 5226) ──────────────────────
int webPort = 5226;
var configuredUrls = builder.Configuration["urls"]
                     ?? builder.Configuration["ASPNETCORE_URLS"]
                     ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (!string.IsNullOrEmpty(configuredUrls))
{
    var m = System.Text.RegularExpressions.Regex.Match(configuredUrls, @":(\d+)");
    if (m.Success && int.TryParse(m.Groups[1].Value, out var p)) webPort = p;
}

// ── Kestrel: web UI on TCP (HTTP/1+HTTP/2) + gRPC server on UDS (HTTP/2) ────
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(webPort, lo => lo.Protocols = HttpProtocols.Http1AndHttp2);

    var socketDir = Path.GetDirectoryName(udsPath);
    if (!string.IsNullOrEmpty(socketDir)) Directory.CreateDirectory(socketDir);
    if (File.Exists(udsPath)) File.Delete(udsPath);
    options.ListenUnixSocket(udsPath, lo => lo.Protocols = HttpProtocols.Http2);
});

// ── Services (always all) ────────────────────────────────────────────────────
builder.Services.AddGrpc();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(30);
        options.DisconnectedCircuitMaxRetained = 200;
        options.JSInteropDefaultCallTimeout = TimeSpan.FromSeconds(30);
    })
    .AddHubOptions(options =>
    {
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(120);
        options.HandshakeTimeout = TimeSpan.FromSeconds(30);
        options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
    });
builder.Services.AddMudServices();

builder.Services.AddSingleton<ISessionService, SessionService>();
builder.Services.AddSingleton<IGrpcChannelProvider, GrpcChannelProvider>();
builder.Services.AddSingleton<IProtoLoader, ProtoLoader>();
builder.Services.AddSingleton<IJsonMessageConverter, JsonMessageConverter>();
builder.Services.AddSingleton<IGrpcServiceClientFinder, GrpcServiceClientFinder>();
builder.Services.AddSingleton<IDynamicProtoCompiler, DynamicProtoCompiler>();
builder.Services.AddSingleton<IUnaryGrpcService, UnaryGrpcService>();
builder.Services.AddSingleton<IGrpcStreamingService, GrpcStreamingService>();
builder.Services.AddSingleton<INatsSessionService, NatsSessionService>();
builder.Services.AddSingleton<WorkbenchNotificationService>();
builder.Services.AddSingleton<WorkbenchStateService>();
builder.Services.AddSingleton<DdsDiscoveryCoordinator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DdsDiscoveryCoordinator>());
// TriggerExecutor: 싱글톤으로도 노출(UI Inject) + IHostedService로 자동 Start/Stop
builder.Services.AddSingleton<TriggerExecutor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TriggerExecutor>());
builder.Services.AddScoped<UiStateService>();

// DDS (RTI Connext) — DDS 탭에서 사용
builder.Services.AddSingleton<DdsParticipantHostFactory>();
builder.Services.AddSingleton<IDdsSessionService, DdsSessionService>();
builder.Services.AddSingleton<DdsStateService>();
builder.Services.AddSingleton<DdsTriggerService>();
builder.Services.AddSingleton<NatsTriggerService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", p => p
        .AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

// ── App ──────────────────────────────────────────────────────────────────────
var app = builder.Build();

// Register .proto MIME type so UseStaticFiles serves it (ASP.NET blocks unknown extensions by default)
var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
provider.Mappings[".proto"] = "text/plain; charset=utf-8";
provider.Mappings[".ttf"] = "font/ttf";
provider.Mappings[".otf"] = "font/otf";
provider.Mappings[".woff"] = "font/woff";
provider.Mappings[".woff2"] = "font/woff2";
app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
{
    ContentTypeProvider = provider,
    // proto 파일은 항상 최신 버전을 내려줘야 하므로 캐시 비활성화
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.EndsWith(".proto", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            ctx.Context.Response.Headers["Pragma"] = "no-cache";
        }
    }
});
app.UseCors("AllowAll");

// Generic gRPC receiver — must run BEFORE UseRouting so it intercepts requests
// for services not registered via MapGrpcService (e.g., proto-defined services).
app.UseMiddleware<GenericGrpcReceiverMiddleware>();

app.UseRouting();
app.UseAntiforgery();

app.MapGet("/health", () => Results.Ok(new { status = "connected" }));
app.MapRazorComponents<ASAP.Components.App>()
    .AddInteractiveServerRenderMode();

// 회로 없이도 미들웨어 알림을 누적해야 하므로 시작 시 한 번 깨워서 구독 시작 보장.
_ = app.Services.GetRequiredService<WorkbenchStateService>();

app.Logger.LogInformation(
    "ASAP: web UI :{Port}, gRPC server on {UdsPath}", webPort, udsPath);

app.Run();
