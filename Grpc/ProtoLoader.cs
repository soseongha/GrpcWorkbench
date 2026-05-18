using Google.Protobuf;
using Google.Protobuf.Reflection;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GrpcWorkbench.Models.Grpc;

namespace GrpcWorkbench.Grpc;

public interface IProtoLoader
{
    Task<List<ServiceMetadata>> LoadProtoServicesAsync(byte[] protoContent);
    DescriptorProto? GetMessageDescriptor(FileDescriptorProto fileDescriptor, string messageName);
}

public class ProtoLoader : IProtoLoader
{
    private const string TemporaryProtoFileName = "service.proto";
    private readonly ILogger<ProtoLoader> _logger;

    public ProtoLoader(ILogger<ProtoLoader> logger)
    {
        _logger = logger;
    }

    public async Task<List<ServiceMetadata>> LoadProtoServicesAsync(byte[] protoContent)
    {
        try
        {
            var descriptorSet = await BuildDescriptorSetAsync(protoContent);
            var targetFile = descriptorSet.File
                .FirstOrDefault(f => string.Equals(f.Name, TemporaryProtoFileName, StringComparison.OrdinalIgnoreCase))
                ?? descriptorSet.File.FirstOrDefault(f => f.Service.Count > 0)
                ?? descriptorSet.File.FirstOrDefault();

            if (targetFile == null)
                return [];

            var typeIndex = BuildTypeIndex(descriptorSet);
            var services = new List<ServiceMetadata>();

            foreach (var service in targetFile.Service)
            {
                var serviceMetadata = new ServiceMetadata
                {
                    ServiceName = service.Name,
                    Description = null
                };

                foreach (var method in service.Method)
                {
                    var inputType = NormalizeTypeName(method.InputType);
                    var outputType = NormalizeTypeName(method.OutputType);

                    var methodMetadata = new MethodMetadata
                    {
                        MethodName = method.Name,
                        InputType = inputType,
                        OutputType = outputType,
                        RpcType = DetermineRpcType(method).ToString(),
                        InputSchema = GenerateJsonSchema(typeIndex.Messages, typeIndex.Enums, inputType),
                        OutputSchema = GenerateJsonSchema(typeIndex.Messages, typeIndex.Enums, outputType)
                    };

                    serviceMetadata.Methods.Add(methodMetadata);
                }

                services.Add(serviceMetadata);
            }

            _logger.LogInformation("Loaded {ServiceCount} services from proto", services.Count);
            return await Task.FromResult(services);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load proto services");
            throw;
        }
    }

    private FileDescriptorProto ParseProtoText(string protoText)
    {
        var fileDescriptor = new FileDescriptorProto();

        // Remove comments and normalize whitespace
        var lines = protoText.Split('\n');
        var cleanedText = new StringBuilder();

        foreach (var line in lines)
        {
            var commentIndex = line.IndexOf("//");
            var cleanedLine = commentIndex >= 0 ? line.Substring(0, commentIndex) : line;
            cleanedText.Append(cleanedLine).Append("\n");
        }

        var text = cleanedText.ToString();

        var packageMatch = System.Text.RegularExpressions.Regex.Match(text, @"package\s+([\w\.]+)\s*;");
        if (packageMatch.Success)
        {
            fileDescriptor.Package = packageMatch.Groups[1].Value;
        }

        var importPattern = @"import\s+(?:\w+\s+)?""([^""]+)""\s*;";
        var importMatches = System.Text.RegularExpressions.Regex.Matches(text, importPattern);
        foreach (System.Text.RegularExpressions.Match importMatch in importMatches)
        {
            fileDescriptor.Dependency.Add(importMatch.Groups[1].Value);
        }

        // Parse services
        var servicePattern = @"service\s+(\w+)\s*\{([^}]*)\}";
        var serviceMatches = System.Text.RegularExpressions.Regex.Matches(text, servicePattern, System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (System.Text.RegularExpressions.Match serviceMatch in serviceMatches)
        {
            var serviceName = serviceMatch.Groups[1].Value;
            var serviceBody = serviceMatch.Groups[2].Value;

            var service = new ServiceDescriptorProto { Name = serviceName };

            // Parse RPC methods within service
            var rpcPattern = @"rpc\s+(\w+)\s*\(\s*(stream\s+)?([\.\w]+)\s*\)\s*returns\s*\(\s*(stream\s+)?([\.\w]+)\s*\)\s*;?";
            var rpcMatches = System.Text.RegularExpressions.Regex.Matches(serviceBody, rpcPattern);

            foreach (System.Text.RegularExpressions.Match rpcMatch in rpcMatches)
            {
                var methodName = rpcMatch.Groups[1].Value;
                var inputStreamKeyword = rpcMatch.Groups[2].Value;
                var inputType = rpcMatch.Groups[3].Value;
                var outputStreamKeyword = rpcMatch.Groups[4].Value;
                var outputType = rpcMatch.Groups[5].Value;

                var method = new MethodDescriptorProto
                {
                    Name = methodName,
                    InputType = EnsureLeadingDot(inputType),
                    OutputType = EnsureLeadingDot(outputType),
                    ClientStreaming = !string.IsNullOrEmpty(inputStreamKeyword),
                    ServerStreaming = !string.IsNullOrEmpty(outputStreamKeyword)
                };

                service.Method.Add(method);
            }

            if (service.Method.Count > 0)
                fileDescriptor.Service.Add(service);
        }

        // Parse enums
        var enumPattern = @"enum\s+(\w+)\s*\{([^}]*)\}";
        var enumMatches = System.Text.RegularExpressions.Regex.Matches(text, enumPattern, System.Text.RegularExpressions.RegexOptions.Singleline);
        var enumNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (System.Text.RegularExpressions.Match enumMatch in enumMatches)
        {
            var enumName = enumMatch.Groups[1].Value;
            var enumBody = enumMatch.Groups[2].Value;

            var enumDescriptor = new EnumDescriptorProto { Name = enumName };
            var enumValuePattern = @"(\w+)\s*=\s*(-?\d+)\s*;?";
            var enumValueMatches = System.Text.RegularExpressions.Regex.Matches(enumBody, enumValuePattern);

            foreach (System.Text.RegularExpressions.Match enumValueMatch in enumValueMatches)
            {
                enumDescriptor.Value.Add(new EnumValueDescriptorProto
                {
                    Name = enumValueMatch.Groups[1].Value,
                    Number = int.Parse(enumValueMatch.Groups[2].Value)
                });
            }

            if (enumDescriptor.Value.Count > 0)
            {
                fileDescriptor.EnumType.Add(enumDescriptor);
                enumNames.Add(enumName);
            }
        }

        // Parse messages
        var messagePattern = @"message\s+(\w+)\s*\{([^}]*)\}";
        var messageMatches = System.Text.RegularExpressions.Regex.Matches(text, messagePattern, System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (System.Text.RegularExpressions.Match messageMatch in messageMatches)
        {
            var messageName = messageMatch.Groups[1].Value;
            var messageBody = messageMatch.Groups[2].Value;

            var message = new DescriptorProto { Name = messageName };

            // Parse fields
            var fieldPattern = @"(?:(repeated|optional|required)\s+)?([\.\w]+)\s+(\w+)\s*=\s*(\d+)";
            var fieldMatches = System.Text.RegularExpressions.Regex.Matches(messageBody, fieldPattern);

            foreach (System.Text.RegularExpressions.Match fieldMatch in fieldMatches)
            {
                var label = fieldMatch.Groups[1].Value;
                var fieldType = fieldMatch.Groups[2].Value;
                var fieldName = fieldMatch.Groups[3].Value;
                var fieldNumber = int.Parse(fieldMatch.Groups[4].Value);

                var field = new FieldDescriptorProto
                {
                    Name = fieldName,
                    Number = fieldNumber
                };

                if (!string.IsNullOrEmpty(label))
                {
                    field.Label = label switch
                    {
                        "repeated" => FieldDescriptorProto.Types.Label.Repeated,
                        "required" => FieldDescriptorProto.Types.Label.Required,
                        _ => FieldDescriptorProto.Types.Label.Optional
                    };
                }

                if (TryConvertScalarType(fieldType, out var scalarType))
                {
                    field.Type = scalarType;
                }
                else
                {
                    field.Type = enumNames.Contains(fieldType) ? FieldDescriptorProto.Types.Type.Enum : FieldDescriptorProto.Types.Type.Message;
                    field.TypeName = EnsureLeadingDot(fieldType);
                }

                message.Field.Add(field);
            }

            if (message.Field.Count > 0)
                fileDescriptor.MessageType.Add(message);
        }

        return fileDescriptor;
    }

    private bool TryConvertScalarType(string protoType, out FieldDescriptorProto.Types.Type type)
    {
        type = protoType switch
        {
            "string" => FieldDescriptorProto.Types.Type.String,
            "int32" => FieldDescriptorProto.Types.Type.Int32,
            "int64" => FieldDescriptorProto.Types.Type.Int64,
            "uint32" => FieldDescriptorProto.Types.Type.Uint32,
            "uint64" => FieldDescriptorProto.Types.Type.Uint64,
            "float" => FieldDescriptorProto.Types.Type.Float,
            "double" => FieldDescriptorProto.Types.Type.Double,
            "bool" => FieldDescriptorProto.Types.Type.Bool,
            "bytes" => FieldDescriptorProto.Types.Type.Bytes,
            _ => default
        };

        return type != default;
    }

    public DescriptorProto? GetMessageDescriptor(FileDescriptorProto fileDescriptor, string messageName)
    {
        return fileDescriptor.MessageType.FirstOrDefault(m =>
            string.Equals(m.Name, messageName, StringComparison.Ordinal) ||
            string.Equals(m.Name, messageName.Split('.').LastOrDefault(), StringComparison.Ordinal));
    }

    private RpcTypeEnum DetermineRpcType(MethodDescriptorProto method)
    {
        var clientStreaming = method.ClientStreaming;
        var serverStreaming = method.ServerStreaming;

        return (clientStreaming, serverStreaming) switch
        {
            (false, false) => RpcTypeEnum.Unary,
            (true, false) => RpcTypeEnum.ClientStreaming,
            (false, true) => RpcTypeEnum.ServerStreaming,
            (true, true) => RpcTypeEnum.BidirectionalStreaming,
        };
    }

    private string? GenerateJsonSchema(
        Dictionary<string, DescriptorProto> messages,
        Dictionary<string, EnumDescriptorProto> enums,
        string messageName)
    {
        if (string.Equals(messageName, "google.protobuf.Empty", StringComparison.Ordinal))
        {
            return JsonSerializer.Serialize(new
            {
                type = "object",
                properties = new Dictionary<string, object>()
            }, new JsonSerializerOptions { WriteIndented = true });
        }

        if (!TryResolveMessage(messages, messageName, out var message))
            return null;

        var properties = new Dictionary<string, object>();

        foreach (var field in message.Field)
        {
            var fieldSchema = BuildFieldSchema(messages, enums, field);

            if (field.Label == FieldDescriptorProto.Types.Label.Repeated)
            {
                fieldSchema = new
                {
                    type = "array",
                    items = fieldSchema
                };
            }

            properties[field.Name] = fieldSchema;
        }

        var schema = new
        {
            type = "object",
            properties
        };

        return JsonSerializer.Serialize(schema, new JsonSerializerOptions { WriteIndented = true });
    }

    private object BuildFieldSchema(
        Dictionary<string, DescriptorProto> messages,
        Dictionary<string, EnumDescriptorProto> enums,
        FieldDescriptorProto field)
    {
        if (field.Type == FieldDescriptorProto.Types.Type.Enum)
        {
            if (TryResolveEnum(enums, field.TypeName, out var enumDescriptor))
            {
                return new
                {
                    type = "string",
                    @enum = enumDescriptor.Value.Select(v => v.Name).ToArray()
                };
            }

            return new { type = "string" };
        }

        if (field.Type == FieldDescriptorProto.Types.Type.Message)
        {
            var resolvedTypeName = NormalizeTypeName(field.TypeName);
            if (string.Equals(resolvedTypeName, "google.protobuf.Empty", StringComparison.Ordinal))
            {
                return new
                {
                    type = "object",
                    properties = new Dictionary<string, object>()
                };
            }

            if (TryResolveMessage(messages, resolvedTypeName, out _))
            {
                return new
                {
                    type = "object",
                    referenceType = resolvedTypeName
                };
            }

            return new { type = "object" };
        }

        return new { type = GetJsonType(field.Type), description = "" };
    }

    private static bool TryResolveMessage(Dictionary<string, DescriptorProto> messages, string typeName, out DescriptorProto message)
    {
        var normalized = NormalizeTypeName(typeName);

        if (messages.TryGetValue(normalized, out message))
            return true;

        var shortName = normalized.Split('.').LastOrDefault();
        if (!string.IsNullOrEmpty(shortName) && messages.TryGetValue(shortName, out message))
            return true;

        message = null!;
        return false;
    }

    private static bool TryResolveEnum(Dictionary<string, EnumDescriptorProto> enums, string typeName, out EnumDescriptorProto enumDescriptor)
    {
        var normalized = NormalizeTypeName(typeName);

        if (enums.TryGetValue(normalized, out enumDescriptor))
            return true;

        var shortName = normalized.Split('.').LastOrDefault();
        if (!string.IsNullOrEmpty(shortName) && enums.TryGetValue(shortName, out enumDescriptor))
            return true;

        enumDescriptor = null!;
        return false;
    }

    private async Task<FileDescriptorSet> BuildDescriptorSetAsync(byte[] protoContent)
    {
        try
        {
            return await BuildDescriptorSetWithProtocAsync(protoContent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse proto via protoc. Falling back to simplified parser.");

            try
            {
                var protoText = Encoding.UTF8.GetString(protoContent);
                var parsedDescriptor = ParseProtoText(protoText);
                var fallbackSet = new FileDescriptorSet();
                fallbackSet.File.Add(parsedDescriptor);
                return fallbackSet;
            }
            catch
            {
                var binaryDescriptor = FileDescriptorProto.Parser.ParseFrom(protoContent);
                var binarySet = new FileDescriptorSet();
                binarySet.File.Add(binaryDescriptor);
                return binarySet;
            }
        }
    }

    private async Task<FileDescriptorSet> BuildDescriptorSetWithProtocAsync(byte[] protoContent)
    {
        var protocPath = FindProtoc();
        if (string.IsNullOrWhiteSpace(protocPath))
            throw new InvalidOperationException("protoc not found.");

        var includePath = FindWellKnownTypesIncludePath();
        var tempDir = Path.Combine(Path.GetTempPath(), $"grpc_loader_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var protoPath = Path.Combine(tempDir, TemporaryProtoFileName);
        var descriptorSetPath = Path.Combine(tempDir, "descriptor.pb");

        try
        {
            await File.WriteAllBytesAsync(protoPath, protoContent);

            var arguments = new StringBuilder();
            arguments.Append($"-I=\"{tempDir}\" ");

            if (!string.IsNullOrWhiteSpace(includePath))
            {
                arguments.Append($"-I=\"{includePath}\" ");
            }

            arguments.Append($"--descriptor_set_out=\"{descriptorSetPath}\" ");
            arguments.Append("--include_imports ");
            arguments.Append($"\"{protoPath}\"");

            var startInfo = new ProcessStartInfo
            {
                FileName = protocPath,
                Arguments = arguments.ToString(),
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tempDir
            };

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start protoc process.");

            var stdOut = await process.StandardOutput.ReadToEndAsync();
            var stdErr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"protoc failed (exit: {process.ExitCode}). {stdOut} {stdErr}".Trim());

            if (!File.Exists(descriptorSetPath))
                throw new InvalidOperationException("Descriptor set was not generated.");

            var descriptorBytes = await File.ReadAllBytesAsync(descriptorSetPath);
            return FileDescriptorSet.Parser.ParseFrom(descriptorBytes);
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
            }
        }
    }

    private static TypeIndex BuildTypeIndex(FileDescriptorSet descriptorSet)
    {
        var messages = new Dictionary<string, DescriptorProto>(StringComparer.Ordinal);
        var enums = new Dictionary<string, EnumDescriptorProto>(StringComparer.Ordinal);

        foreach (var file in descriptorSet.File)
        {
            var prefix = file.Package ?? string.Empty;

            foreach (var enumType in file.EnumType)
            {
                RegisterType(enums, BuildQualifiedName(prefix, enumType.Name), enumType);
            }

            foreach (var message in file.MessageType)
            {
                RegisterMessage(messages, enums, prefix, message);
            }
        }

        return new TypeIndex(messages, enums);
    }

    private static void RegisterMessage(
        Dictionary<string, DescriptorProto> messages,
        Dictionary<string, EnumDescriptorProto> enums,
        string prefix,
        DescriptorProto message)
    {
        var messageName = BuildQualifiedName(prefix, message.Name);
        RegisterType(messages, messageName, message);

        foreach (var enumType in message.EnumType)
        {
            RegisterType(enums, BuildQualifiedName(messageName, enumType.Name), enumType);
        }

        foreach (var nested in message.NestedType)
        {
            RegisterMessage(messages, enums, messageName, nested);
        }
    }

    private static void RegisterType<T>(Dictionary<string, T> dictionary, string qualifiedName, T value)
    {
        var normalized = NormalizeTypeName(qualifiedName);
        if (!string.IsNullOrEmpty(normalized))
        {
            dictionary[normalized] = value;
            var shortName = normalized.Split('.').LastOrDefault();
            if (!string.IsNullOrEmpty(shortName) && !dictionary.ContainsKey(shortName))
            {
                dictionary[shortName] = value;
            }
        }
    }

    private static string BuildQualifiedName(string prefix, string name)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return name;
        return $"{prefix}.{name}";
    }

    private static string EnsureLeadingDot(string typeName)
    {
        var normalized = NormalizeTypeName(typeName);
        return string.IsNullOrWhiteSpace(normalized) ? typeName : $".{normalized}";
    }

    private static string NormalizeTypeName(string? typeName)
    {
        return (typeName ?? string.Empty).Trim().TrimStart('.');
    }

    private string? FindProtoc()
    {
        var isWindows = OperatingSystem.IsWindows();
        var exeName = isWindows ? "protoc.exe" : "protoc";

        var appToolsPath = Path.Combine(AppContext.BaseDirectory, "tools", exeName);
        if (File.Exists(appToolsPath))
            return appToolsPath;

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (var path in pathVar.Split(Path.PathSeparator))
            {
                var protocPath = Path.Combine(path, exeName);
                if (File.Exists(protocPath))
                    return protocPath;
            }
        }

        var nugetTools = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "grpc.tools");

        if (Directory.Exists(nugetTools))
        {
            var protocPaths = Directory.GetFiles(nugetTools, exeName, SearchOption.AllDirectories);
            if (protocPaths.Length > 0)
                return protocPaths.Last();
        }

        return null;
    }

    private string? FindWellKnownTypesIncludePath()
    {
        var appIncludePath = Path.Combine(AppContext.BaseDirectory, "tools", "include");
        if (File.Exists(Path.Combine(appIncludePath, "google", "protobuf", "empty.proto")))
            return appIncludePath;

        var nugetTools = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "grpc.tools");

        if (Directory.Exists(nugetTools))
        {
            var emptyProtoPaths = Directory.GetFiles(nugetTools, "empty.proto", SearchOption.AllDirectories)
                .Where(path => path.Replace('\\', '/').EndsWith("google/protobuf/empty.proto", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (emptyProtoPaths.Length > 0)
            {
                var includeDir = Directory.GetParent(emptyProtoPaths[0])?.Parent?.Parent?.FullName;
                if (!string.IsNullOrWhiteSpace(includeDir))
                    return includeDir;
            }
        }

        return null;
    }

    private sealed record TypeIndex(
        Dictionary<string, DescriptorProto> Messages,
        Dictionary<string, EnumDescriptorProto> Enums);

    private string GetJsonType(FieldDescriptorProto.Types.Type type) => type switch
    {
        FieldDescriptorProto.Types.Type.String => "string",
        FieldDescriptorProto.Types.Type.Int32 or
        FieldDescriptorProto.Types.Type.Int64 or
        FieldDescriptorProto.Types.Type.Uint32 or
        FieldDescriptorProto.Types.Type.Uint64 => "integer",
        FieldDescriptorProto.Types.Type.Double or
        FieldDescriptorProto.Types.Type.Float => "number",
        FieldDescriptorProto.Types.Type.Bool => "boolean",
        FieldDescriptorProto.Types.Type.Bytes => "string",
        _ => "object"
    };
}
