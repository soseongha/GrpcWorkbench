using Google.Protobuf;
using Grpc.Core;
using GrpcWorkbench.Grpc;
using GrpcWorkbench.Models.Api;
using GrpcWorkbench.Models.Session;
using System.Diagnostics;
using System.Reflection;

namespace GrpcWorkbench.Services;

public interface IUnaryGrpcService
{
    /// <summary>Unary RPC를 실행하고 결과를 반환합니다.</summary>
    Task<GrpcResponseData> ExecuteUnaryCallAsync(GrpcRequestPayload payload, GrpcSession session);
}

public class UnaryGrpcService : IUnaryGrpcService
{
    private readonly IGrpcChannelProvider _channelProvider;
    private readonly IJsonMessageConverter _jsonConverter;
    private readonly IDynamicProtoCompiler _protoCompiler;
    private readonly IGrpcServiceClientFinder _clientFinder;
    private readonly ILogger<UnaryGrpcService> _logger;

    public UnaryGrpcService(
        IGrpcChannelProvider channelProvider,
        IJsonMessageConverter jsonConverter,
        IDynamicProtoCompiler protoCompiler,
        IGrpcServiceClientFinder clientFinder,
        ILogger<UnaryGrpcService> logger)
    {
        _channelProvider = channelProvider;
        _jsonConverter = jsonConverter;
        _protoCompiler = protoCompiler;
        _clientFinder = clientFinder;
        _logger = logger;
    }

    /// <summary>
    /// Proto를 동적으로 컴파일하여 Unary RPC를 실행합니다.
    /// </summary>
    public async Task<GrpcResponseData> ExecuteUnaryCallAsync(GrpcRequestPayload payload, GrpcSession session)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = new GrpcResponseData();

        try
        {
            var assembly = await _protoCompiler.CompileProtoToAssemblyAsync(session.ProtoContent ?? []);

            var clientType = _clientFinder.FindServiceClientType(assembly, payload.ServiceName);
            if (clientType == null)
                throw new InvalidOperationException($"Service client for '{payload.ServiceName}' not found");

            var methodInfo = clientType.GetMethods()
                .Where(m => m.Name == payload.MethodName)
                .FirstOrDefault(m =>
                {
                    var p = m.GetParameters();
                    return p.Length == 2 && p[1].ParameterType == typeof(CallOptions);
                })
                ?? clientType.GetMethods().FirstOrDefault(m => m.Name == payload.MethodName);

            if (methodInfo == null)
                throw new InvalidOperationException($"Method '{payload.MethodName}' not found on {clientType.FullName}");

            var requestMessageType = methodInfo.GetParameters().FirstOrDefault()?.ParameterType;
            if (requestMessageType == null || !typeof(IMessage).IsAssignableFrom(requestMessageType))
                throw new InvalidOperationException($"Could not determine request message type for '{payload.MethodName}'");

            var requestMessage = await _jsonConverter.JsonToMessageAsync(payload.RequestJson, requestMessageType);
            var channel = await _channelProvider.GetChannelAsync(session);

            var metadata = new Metadata();
            if (payload.Metadata != null)
            {
                foreach (var kvp in payload.Metadata)
                    metadata.Add(kvp.Key, kvp.Value);
            }

            var callOptions = new CallOptions(metadata, DateTime.UtcNow.AddSeconds(payload.TimeoutSeconds));
            var clientInstance = Activator.CreateInstance(clientType, channel);

            dynamic? result;
            if (methodInfo.ReturnType.IsGenericType &&
                methodInfo.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var task = (Task)methodInfo.Invoke(clientInstance, [requestMessage, callOptions])!;
                await task;
                result = task.GetType().GetProperty("Result")?.GetValue(task);
            }
            else
            {
                result = methodInfo.Invoke(clientInstance, [requestMessage, callOptions]);
            }

            if (result is IMessage message)
                response.ResponseJson = _jsonConverter.MessageToJson(message);

            response.IsSuccess = true;
            _logger.LogInformation("Unary call succeeded: {ServiceName}.{MethodName}",
                payload.ServiceName, payload.MethodName);
        }
        catch (Exception ex)
        {
            var actualException = UnwrapException(ex);
            response.IsSuccess = false;
            response.ErrorMessage = actualException.Message;
            _logger.LogError(actualException, "Unary call failed: {ServiceName}.{MethodName}",
                payload.ServiceName, payload.MethodName);
        }
        finally
        {
            stopwatch.Stop();
            response.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
        }

        return response;
    }

    private static Exception UnwrapException(Exception ex)
    {
        while (ex is TargetInvocationException tie && tie.InnerException != null)
        {
            ex = tie.InnerException;
        }

        if (ex is AggregateException aggregateException && aggregateException.InnerException != null)
        {
            return UnwrapException(aggregateException.InnerException);
        }

        return ex;
    }
}
