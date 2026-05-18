[서버]
cd C:\Users\76922.LIGDNA\source\repos\GrpcTestServer
dotnet publish .\GrpcTestServer\GrpcTestServer.csproj -c Release -o .\publish /p:UseAppHost=false
docker build -t grpc-echo-server .
docker run --rm -d --name grpc-echo-server -v grpc-sock:/tmp grpc-echo-server

[클라이언트]
docker volume create grpc-sock

docker build -t localhost:5000/grpc-workbench:v1.0.4 .

docker run --rm -it --name grpc-workbench -p 5226:5226 ^
  -e ASPNETCORE_URLS=http://+:5226 ^
  -v grpc-sock:/tmp ^
  localhost:5000/grpc-workbench:v1.0.4



