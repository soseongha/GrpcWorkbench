chcp 65001 | Out-Null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$schemaRoot = Join-Path $root "schema"
$ddsDefinitionsDir = Join-Path $schemaRoot "dds\definitions"
$xmlPath = Join-Path $ddsDefinitionsDir "DDSSim.xml"
$protoDir = Join-Path $schemaRoot "proto"
$grpcProtoPath = Join-Path $protoDir "grpc\DDSSim.proto"
$natsProtoPath = Join-Path $protoDir "nats\DDSSim.proto"

New-Item -ItemType Directory -Path $ddsDefinitionsDir -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $protoDir "grpc") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $protoDir "nats") -Force | Out-Null

if (-not (Test-Path $xmlPath)) {
    throw "DDS XML not found: $xmlPath`nASAP DDS features read this XML directly."
}

if (-not (Test-Path $grpcProtoPath)) {
    throw "gRPC proto not found: $grpcProtoPath`nASAP does not generate gRPC proto from XML. Place the gRPC test proto manually."
}

if (-not (Test-Path $natsProtoPath)) {
    throw "NATS proto not found: $natsProtoPath`nASAP does not generate NATS proto from XML. Place the NATS test proto manually."
}

Write-Host "ASAP schema layout verified." -ForegroundColor Green
Write-Host "  DDS  : schema/dds/definitions/DDSSim.xml" -ForegroundColor DarkGray
Write-Host "  gRPC : schema/proto/grpc/DDSSim.proto" -ForegroundColor DarkGray
Write-Host "  NATS : schema/proto/nats/DDSSim.proto" -ForegroundColor DarkGray
Write-Host "ASAP treats DDS, gRPC, and NATS inputs separately." -ForegroundColor Green