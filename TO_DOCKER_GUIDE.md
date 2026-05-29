# Docker / K8s 배포 가이드

> 이 저장소는 **오프라인(폐쇄망) 빌드**입니다. NuGet 복원은 커밋된 `./packages`
> 캐시만 사용합니다(`nuget.config`). Dockerfile은 `protoc` / `grpc_csharp_plugin` /
> well-known-type include를 `tools/` 에 번들해 런타임 동적 proto 컴파일이 동작하도록 합니다.

## 1. 이미지 빌드

```powershell
docker build -t asap:test -f .\Dockerfile .
```

`Dockerfile.deploytemp` 없이 체크인된 `Dockerfile`이 공식 빌드 경로입니다.

Sidecar 이미지는 공통 부모 폴더를 build context 로 사용해야 합니다.

```powershell
Set-Location ..
docker build -t dds-nats-sidecar:test -f .\NATS\DDSNATSSidecar\Dockerfile .
```

로컬 registry 테스트 배포에서는 다음 태그를 사용합니다.

```powershell
docker tag asap:test localhost:5000/asap:latest
docker tag dds-nats-sidecar:test localhost:5000/dds-nats-sidecar:latest
docker tag nats:2.10-alpine localhost:5000/nats:2.10-alpine

docker push localhost:5000/asap:latest
docker push localhost:5000/dds-nats-sidecar:latest
docker push localhost:5000/nats:2.10-alpine
```

## 2. K8s 2-인스턴스 테스트 (권장)

워크벤치 A·B 두 인스턴스가 공유 UDS 볼륨으로 서로 호출하는 토폴로지입니다.

```powershell
# 빌드 + 배포 (이미지 빌드 포함)
.\build-and-deploy-test.ps1

# 이미 빌드된 이미지로 재배포만
.\build-and-deploy-test.ps1 -SkipBuild

# 정리
.\build-and-deploy-test.ps1 -Teardown
```

배포 후:

- Workbench A: <http://localhost:30226>  (자기 gRPC 서버 소켓: `/var/run/dds-ambassador/grpc.sock`)
- Workbench B: <http://localhost:30227>

테스트 흐름: A UI에서 연결할 소켓 경로를 `/var/run/dds-ambassador-peer/grpc.sock`
으로 세션 생성 → 요청 전송 → B UI 수신 패널에 표시 (반대 방향도 동일).

### Docker Desktop Kubernetes EOF 복구

Docker Desktop 은 떠 있는데 `kubectl get nodes` 가 `EOF` 로 실패할 때는 아래 스크립트로
live 복구를 시도할 수 있습니다.

```powershell
.\repair-docker-desktop-k8s.ps1
```

기본 동작:

- `docker desktop status` 와 `kubectl` 상태를 먼저 확인
- `docker-desktop` WSL 내부 cgroup 에서 `cpu` / `cpuset` / `io` controller 복구
- Docker Desktop BackendAPI(named pipe) 로 Kubernetes 를 `disable -> enable`
- 마지막에 `kubectl get nodes -o wide` 로 정상화 확인

이미 정상인 상태에서는 아무 것도 건드리지 않고 종료합니다. 강제로 같은 절차를 다시 태우려면
`-Force` 를 사용합니다.

## 3. 단일 컨테이너 수동 실행 (선택)

```powershell
docker run --rm -it --name asap `
  -p 5226:5226 `
  -e ASPNETCORE_URLS=http://+:5226 `
  -e UDS_SOCKET_PATH=/var/run/dds-ambassador/grpc.sock `
  -v grpc-sock:/var/run/dds-ambassador `
  asap:test
```

UI: <http://localhost:5226>

## 환경 변수

| 변수 | 기본값 | 설명 |
|------|--------|------|
| `ASPNETCORE_URLS` | `http://+:5226` | 웹 UI 바인딩 (HTTP/1+2) |
| `UDS_SOCKET_PATH` | `/var/run/dds-ambassador/grpc.sock` | 이 인스턴스의 gRPC 서버가 listen 하는 UDS 경로 |

## 동작 메모

- 번들 proto는 `ddssim.DdsBridge` (16개 양방향 스트리밍 RPC). UI에서 다른 `.proto`
  업로드도 가능합니다.
- 내장 gRPC 수신부는 들어오는 모든 호출을 protobuf→JSON 디코딩해 UI에 표시하고
  `grpc-status: 0`(OK)을 반환하는 **시각화용 리시버**입니다(실제 에코 서버 아님).
- `/health` (HTTP)는 k8s readinessProbe 용도입니다.

## Sidecar 연결 범위

이번 정리 기준으로 `dds-nats-sidecar`는 공식 Dockerfile로 빌드되고, 두 가지 경로가 검증됐습니다.

1. pod A 내부 `ASAP (NATS)` 와 pod-local NATS broker 연결
2. pod A sidecar 와 pod B `ASAP (DDS)` 사이의 DDS 브리지 연결

다만 이것이 곧바로 "임의의 다른 컨테이너나 DDS 통신 대상에도 항상 문제 없음"을 뜻하지는 않습니다.
아래 조건이 맞아야 같은 방식으로 붙습니다.

1. DDS 쪽은 같은 domain, topic/type 정의, 그리고 호환 가능한 QoS를 사용해야 합니다.
2. NATS 쪽은 `dds.sidecar.to-dds.<TopicName>` 또는 `dds.sidecar.to-nats.<TopicName>` 규약을 따라야 합니다.
3. `to-dds` 방향 payload는 JSON이 아니라 해당 protobuf message의 raw binary 여야 합니다.
4. sidecar 컨테이너 안에 `/app/schema/dds/definitions` 와 RTI license 가 있어야 합니다.
5. 실제 컨테이너 네트워크에서 DDS discovery 와 data traffic 이 통과해야 합니다.

즉, sidecar 는 다른 대상에도 붙을 수 있지만, 현재 보장되는 건 "같은 정의와 네트워크 조건을 맞춘 대상"까지입니다.
새 대상에 붙일 때는 README 의 acceptance check 순서대로 한 번 검증하는 걸 권장합니다.
