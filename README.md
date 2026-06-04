# Signal Forge ASAP

ASAP is the Signal Forge web workbench. It can run independently from
`DDS_NATS` when you only need the UI, direct DDS testing, NATS message testing,
or gRPC/UDS workbench features.

## Standalone Docker Build

Build from this repository root:

```powershell
docker build -t asap:test -f .\Dockerfile .
```

For the local Kubernetes registry used by the test manifests:

```powershell
docker tag asap:test localhost:5000/asap:latest
docker push localhost:5000/asap:latest
```

## Standalone Kubernetes

The standalone manifest starts ASAP plus a pod-local NATS broker for the NATS
tab. DDS features still require the RTI runtime/license and a reachable DDS
network. gRPC features require a reachable target endpoint or UDS socket.

```powershell
kubectl apply -f .\k8s\asap-standalone.yaml
kubectl port-forward svc/asap-standalone-ui 5226:5226
```

Open:

```text
http://localhost:5226/
```

## Runtime Defaults

- HTTP UI: `5226`
- NATS URL: `nats://localhost:4222`
- gRPC UDS socket: `/var/run/asap/grpc.sock`
- Default gRPC target UDS socket: `/var/run/dds-ambassador/grpc.sock`
- DDS discovery: `Default`

Environment variables can override `appsettings.json` through the `ASAP`
section, for example:

```text
ASAP__NatsUrl=nats://localhost:4222
ASAP__UdsSocketPath=/var/run/asap/grpc.sock
ASAP__DdsDiscoveryMode=Default
ASAP__DdsInitialPeers=
```

For compatibility with the existing gRPC/UDS host code, the Kubernetes example
also sets:

```text
UDS_SOCKET_PATH=/var/run/asap/grpc.sock
DEFAULT_TARGET_UDS_SOCKET_PATH=/var/run/dds-ambassador/grpc.sock
```

## DDS Discovery In Kubernetes

DDS discovery is not a Kubernetes Service discovery protocol. The default RTI
discovery path relies on the DDS/RTPS network stack and may use multicast. It
can work in local clusters, but it is not guaranteed across every CNI, cloud
network, firewall, or multi-node topology.

ASAP DDS sessions expose three discovery modes:

- `Default`: leave participant discovery to RTI defaults.
- `Multicast`: use a selected multicast address when the cluster network allows
  multicast.
- `PeerToPeer`: disable multicast receive addresses for that session and use the
  listed `initial_peers` as unicast discovery locators.

For k8s tests where message loss is unacceptable and discovery must be
repeatable, prefer `PeerToPeer` for one-to-one or small fixed topologies. For
many dynamic participants, use an RTI discovery service/cloud discovery design
instead of trying to maintain every peer address in every pod.

ASAP does not run a discovery coordinator in-process. For multicast-disabled
k8s environments, configure each DDS session with a reachable RTI Connext
initial peer locator. If an external discovery relay is deployed in the same
cluster, the value can be a service DNS name, for example:

```text
[32]@builtin.udpv4://dds-discovery.default.svc.cluster.local
```

For another namespace, replace `default` with the namespace where that external
discovery endpoint is deployed.
