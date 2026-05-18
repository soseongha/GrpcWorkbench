using System.Diagnostics;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace GrpcWorkbench.Grpc;

/// <summary>
/// Proto 파일을 동적으로 C# 코드로 컴파일하고 어셈블리로 로드
/// </summary>
public interface IDynamicProtoCompiler
{
    Task<Assembly> CompileProtoToAssemblyAsync(byte[] protoContent);
}

public class DynamicProtoCompiler : IDynamicProtoCompiler
{
    private readonly ILogger<DynamicProtoCompiler> _logger;

    public DynamicProtoCompiler(ILogger<DynamicProtoCompiler> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Proto 파일 바이트를 .NET 어셈블리로 동적 컴파일
    /// </summary>
    public async Task<Assembly> CompileProtoToAssemblyAsync(byte[] protoContent)
    {
        try
        {
            _logger.LogInformation("Starting dynamic Proto compilation...");

            // 1단계: Proto 파일을 임시 파일로 저장
            var tempDir = Path.Combine(Path.GetTempPath(), $"grpc_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);

            var protoFilePath = Path.Combine(tempDir, "service.proto");
            await File.WriteAllBytesAsync(protoFilePath, protoContent);
            _logger.LogInformation($"Proto file saved to: {protoFilePath}");

            // 2단계: protoc 실행하여 C# 코드 생성
            var csharpFiles = await RunProtocAsync(tempDir, protoFilePath);
            _logger.LogInformation($"Generated {csharpFiles.Count} C# files");

            // 3단계: C# 코드를 어셈블리로 컴파일 (각 파일을 독립 SyntaxTree로)
            var assembly = CompileCsharpToAssembly(csharpFiles);
            _logger.LogInformation("Successfully compiled Proto to Assembly");

            // 정리
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to cleanup temp directory: {ex.Message}");
            }

            return assembly;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Proto compilation failed");
            throw;
        }
    }

    /// <summary>
    /// protoc 도구를 실행하여 C# 코드 생성
    /// </summary>
    private async Task<List<string>> RunProtocAsync(string tempDir, string protoFilePath)
    {
        var protocPath = FindProtoc();
        if (string.IsNullOrEmpty(protocPath))
            throw new InvalidOperationException("protoc not found. Please install Grpc.Tools");

        var includePath = FindWellKnownTypesIncludePath(protocPath);
        if (string.IsNullOrWhiteSpace(includePath))
        {
            includePath = CreateFallbackWellKnownTypesInclude(tempDir);
            _logger.LogWarning("Well-known types include path not found. Using fallback proto definitions at: {IncludePath}", includePath);
        }

        var pluginPath = FindGrpcPlugin();
        if (string.IsNullOrEmpty(pluginPath))
            throw new InvalidOperationException("grpc_csharp_plugin not found");

        _logger.LogInformation($"Using protoc: {protocPath}");
        _logger.LogInformation($"Using plugin: {pluginPath}");

        var arguments = new StringBuilder();
        arguments.Append($"-I=\"{tempDir}\" ");
        if (!string.IsNullOrWhiteSpace(includePath))
        {
            arguments.Append($"-I=\"{includePath}\" ");
        }
        arguments.Append($"--csharp_out=\"{tempDir}\" ");
        arguments.Append($"--grpc_out=\"{tempDir}\" ");
        arguments.Append($"--plugin=protoc-gen-grpc=\"{pluginPath}\" ");
        arguments.Append($"\"{protoFilePath}\"");

        _logger.LogInformation($"Protoc arguments: {arguments}");

        var processInfo = new ProcessStartInfo
        {
            FileName = protocPath,
            Arguments = arguments.ToString(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = tempDir
        };

        using (var process = Process.Start(processInfo))
        {
            if (process == null)
                throw new InvalidOperationException("Failed to start protoc process");

            var standardOutput = await process.StandardOutput.ReadToEndAsync();
            var standardError = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            _logger.LogInformation($"Protoc exit code: {process.ExitCode}");

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"protoc failed with exit code {process.ExitCode}: {standardError}");
            }
        }

        // 생성된 C# 파일 찾기
        _logger.LogInformation($"Looking for generated C# files in: {tempDir}");
        var csFiles = Directory.GetFiles(tempDir, "*.cs");
        _logger.LogInformation($"Found {csFiles.Length} C# files");

        if (csFiles.Length == 0)
        {
            var allFiles = Directory.GetFiles(tempDir);
            var debugInfo = $"No .cs files found. All files in temp dir: {string.Join(", ", allFiles.Select(f => Path.GetFileName(f)))}";
            _logger.LogWarning(debugInfo);
            throw new InvalidOperationException("No C# files generated by protoc");
        }

        var csharpFiles = new List<string>();
        var debugOutputPath = Path.Combine(Path.GetTempPath(), $"grpc_debug_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var debugOutput = new StringBuilder();

        debugOutput.AppendLine("=== Generated C# Code Debug Output ===");
        debugOutput.AppendLine($"Generated at: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        debugOutput.AppendLine($"Temp directory: {tempDir}");
        debugOutput.AppendLine($"Number of files: {csFiles.Length}");
        debugOutput.AppendLine();

        foreach (var csFile in csFiles)
        {
            var fileContent = await File.ReadAllTextAsync(csFile);
            var fileName = Path.GetFileName(csFile);

            debugOutput.AppendLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            debugOutput.AppendLine($"File: {fileName} ({fileContent.Length} bytes)");
            debugOutput.AppendLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            debugOutput.AppendLine(fileContent);
            debugOutput.AppendLine();

            csharpFiles.Add(fileContent);
        }

        // 디버그 파일 저장
        await File.WriteAllTextAsync(debugOutputPath, debugOutput.ToString());
        _logger.LogInformation($"Debug output saved to: {debugOutputPath}");

        return csharpFiles;
    }

    /// <summary>
    /// C# 코드를 .NET 어셈블리로 컴파일 (각 파일을 독립 SyntaxTree로 처리)
    /// </summary>
    private Assembly CompileCsharpToAssembly(List<string> csharpFiles)
    {
        _logger.LogInformation("Compiling C# code to assembly...");

        var references = new List<MetadataReference>();
        var addedPaths = new HashSet<string>();

        // 현재 로드된 모든 어셈블리를 참조로 추가 (Location이 있는 것만)
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (!asm.IsDynamic && !string.IsNullOrEmpty(asm.Location) && addedPaths.Add(asm.Location))
                {
                    references.Add(MetadataReference.CreateFromFile(asm.Location));
                }
            }
            catch { }
        }

        _logger.LogInformation($"Added {references.Count} assembly references");

        // 각 파일을 독립된 SyntaxTree로 파싱
        var syntaxTrees = csharpFiles
            .Select((code, i) => CSharpSyntaxTree.ParseText(code, path: $"file{i}.cs"))
            .ToList();

        _logger.LogInformation($"Parsed {syntaxTrees.Count} syntax trees");

        var compilation = CSharpCompilation.Create("DynamicGrpcAssembly")
            .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .AddReferences(references)
            .AddSyntaxTrees(syntaxTrees);

        using (var stream = new MemoryStream())
        {
            var result = compilation.Emit(stream);

            if (!result.Success)
            {
                var errors = result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .ToList();

                var errorMessage = string.Join("\n", errors.Select(d => $"{d.Location}: {d.GetMessage()}"));
                _logger.LogError($"Compilation errors ({errors.Count}):\n{errorMessage}");
                throw new InvalidOperationException($"C# compilation failed with {errors.Count} errors:\n{errorMessage}");
            }

            stream.Seek(0, SeekOrigin.Begin);
            var assemblyBytes = stream.ToArray();
            _logger.LogInformation($"Assembly compiled successfully ({assemblyBytes.Length} bytes)");

            return Assembly.Load(assemblyBytes);
        }
    }

    /// <summary>
    /// protoc 실행 파일 경로 찾기 (Windows/Linux 모두 지원)
    /// </summary>
    private string? FindProtoc()
    {
        var isWindows = OperatingSystem.IsWindows();
        var exeName = isWindows ? "protoc.exe" : "protoc";

        // 1. 앱 디렉토리의 tools 폴더에서 찾기 (Docker 컨테이너용)
        var appToolsPath = Path.Combine(AppContext.BaseDirectory, "tools", exeName);
        if (File.Exists(appToolsPath))
            return appToolsPath;

        // 2. PATH에서 찾기
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

        // 3. NuGet tools 폴더에서 찾기
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

    /// <summary>
    /// grpc_csharp_plugin 경로 찾기 (Windows/Linux 모두 지원)
    /// </summary>
    private string? FindGrpcPlugin()
    {
        var isWindows = OperatingSystem.IsWindows();
        var exeName = isWindows ? "grpc_csharp_plugin.exe" : "grpc_csharp_plugin";

        // 1. 앱 디렉토리의 tools 폴더에서 찾기 (Docker 컨테이너용)
        var appToolsPath = Path.Combine(AppContext.BaseDirectory, "tools", exeName);
        if (File.Exists(appToolsPath))
            return appToolsPath;

        // 2. PATH에서 찾기
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (var path in pathVar.Split(Path.PathSeparator))
            {
                var pluginPath = Path.Combine(path, exeName);
                if (File.Exists(pluginPath))
                    return pluginPath;
            }
        }

        // 3. NuGet tools 폴더에서 찾기
        var nugetTools = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "grpc.tools");

        if (Directory.Exists(nugetTools))
        {
            var pluginPaths = Directory.GetFiles(nugetTools, exeName, SearchOption.AllDirectories);
            if (pluginPaths.Length > 0)
                return pluginPaths.Last();
        }

        return null;
    }

    /// <summary>
    /// google/protobuf/*.proto(well-known types) include 경로 찾기
    /// </summary>
    private string? FindWellKnownTypesIncludePath(string protocPath)
    {
        // 1. 앱 디렉토리의 tools/include (컨테이너/배포 환경)
        var appIncludePath = Path.Combine(AppContext.BaseDirectory, "tools", "include");
        if (File.Exists(Path.Combine(appIncludePath, "google", "protobuf", "empty.proto")))
            return appIncludePath;

        // 2. protoc 실행 파일 주변 경로 탐색
        var protocDir = Path.GetDirectoryName(protocPath);
        if (!string.IsNullOrWhiteSpace(protocDir))
        {
            var cursor = new DirectoryInfo(protocDir);
            for (var i = 0; i < 8 && cursor != null; i++)
            {
                var includeCandidate = Path.Combine(cursor.FullName, "include");
                if (File.Exists(Path.Combine(includeCandidate, "google", "protobuf", "empty.proto")))
                    return includeCandidate;

                var grpcToolsIncludeCandidate = Path.Combine(cursor.FullName, "build", "native", "include");
                if (File.Exists(Path.Combine(grpcToolsIncludeCandidate, "google", "protobuf", "empty.proto")))
                    return grpcToolsIncludeCandidate;

                cursor = cursor.Parent;
            }
        }

        // 3. NuGet grpc.tools 경로
        var nugetTools = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "grpc.tools");

        if (Directory.Exists(nugetTools))
        {
            var emptyProtoPaths = Directory.GetFiles(nugetTools, "empty.proto", SearchOption.AllDirectories)
                .Where(path => path.Replace('\\', '/')
                    .EndsWith("google/protobuf/empty.proto", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (emptyProtoPaths.Length > 0)
            {
                // .../include/google/protobuf/empty.proto -> include
                var includeDir = Directory.GetParent(emptyProtoPaths[0])?.Parent?.Parent?.FullName;
                if (!string.IsNullOrWhiteSpace(includeDir))
                    return includeDir;
            }
        }

        return null;
    }

    private static string CreateFallbackWellKnownTypesInclude(string tempDir)
    {
        var includeRoot = Path.Combine(tempDir, "wkt_include");
        var googleProtobufDir = Path.Combine(includeRoot, "google", "protobuf");
        Directory.CreateDirectory(googleProtobufDir);

        File.WriteAllText(Path.Combine(googleProtobufDir, "empty.proto"),
            "syntax = \"proto3\";\npackage google.protobuf;\nmessage Empty {}\n");

        File.WriteAllText(Path.Combine(googleProtobufDir, "timestamp.proto"),
            "syntax = \"proto3\";\npackage google.protobuf;\nmessage Timestamp { int64 seconds = 1; int32 nanos = 2; }\n");

        File.WriteAllText(Path.Combine(googleProtobufDir, "wrappers.proto"),
            "syntax = \"proto3\";\npackage google.protobuf;\n"
            + "message DoubleValue { double value = 1; }\n"
            + "message FloatValue { float value = 1; }\n"
            + "message Int64Value { int64 value = 1; }\n"
            + "message UInt64Value { uint64 value = 1; }\n"
            + "message Int32Value { int32 value = 1; }\n"
            + "message UInt32Value { uint32 value = 1; }\n"
            + "message BoolValue { bool value = 1; }\n"
            + "message StringValue { string value = 1; }\n"
            + "message BytesValue { bytes value = 1; }\n");

        File.WriteAllText(Path.Combine(googleProtobufDir, "struct.proto"),
            "syntax = \"proto3\";\npackage google.protobuf;\n"
            + "message Struct { map<string, Value> fields = 1; }\n"
            + "message Value { oneof kind { NullValue null_value = 1; double number_value = 2; string string_value = 3; bool bool_value = 4; Struct struct_value = 5; ListValue list_value = 6; } }\n"
            + "enum NullValue { NULL_VALUE = 0; }\n"
            + "message ListValue { repeated Value values = 1; }\n");

        return includeRoot;
    }
}
