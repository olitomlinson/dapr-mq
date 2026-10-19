# Build with the repo root as context:
#   docker build -f tools/k8s-throughput/Dockerfile -t daprmq-throughput-test:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The SDK and the test compile their gRPC contracts straight from the server's proto files, at these
# relative paths (see DaprMQ.Client.csproj and ThroughputTest.csproj).
COPY server/src/DaprMQ.ApiServer/Protos/ ./server/src/DaprMQ.ApiServer/Protos/
COPY server/src/DaprMQ.Operator/Protos/ ./server/src/DaprMQ.Operator/Protos/
COPY sdks/dotnet/src/DaprMQ.Client/ ./sdks/dotnet/src/DaprMQ.Client/
COPY tools/k8s-throughput/ThroughputTest/ ./tools/k8s-throughput/ThroughputTest/

WORKDIR /src/tools/k8s-throughput/ThroughputTest
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "ThroughputTest.dll"]
