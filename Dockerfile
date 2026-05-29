FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY nuget.config ./nuget.config
COPY packages/ ./packages/
COPY rti/ ./rti/
COPY ASAP.csproj ./
RUN dotnet restore ASAP.csproj --configfile nuget.config

COPY . .
RUN dotnet publish ASAP.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

RUN set -eux; \
    TOOLS_DIR="$(find /root/.nuget/packages/grpc.tools -type d -name linux_x64 | head -n1)"; \
    INCLUDE_DIR="$(find /root/.nuget/packages/grpc.tools -type d -path '*build/native/include' | head -n1)"; \
    test -n "$TOOLS_DIR" && test -n "$INCLUDE_DIR"; \
    mkdir -p /app/publish/tools/include; \
    cp "$TOOLS_DIR/protoc" /app/publish/tools/protoc; \
    cp "$TOOLS_DIR/grpc_csharp_plugin" /app/publish/tools/grpc_csharp_plugin; \
    cp -r "$INCLUDE_DIR/." /app/publish/tools/include/; \
    chmod +x /app/publish/tools/protoc /app/publish/tools/grpc_csharp_plugin; \
    test -f /app/publish/tools/include/google/protobuf/empty.proto

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:5226
EXPOSE 5226

ENTRYPOINT ["dotnet", "ASAP.dll"]
