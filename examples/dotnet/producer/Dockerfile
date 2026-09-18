# Build with the repo root as context:
#   docker build -f examples/dotnet/producer/Dockerfile -t daprmq-examples-dotnet-producer .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# DaprMQ.Client.csproj pulls its gRPC contract straight from the server project
# (../../../../server/src/DaprMQ.ApiServer/Protos/daprmq.proto, relative to the
# client's own .csproj) since the SDK isn't published - that proto file has to be
# copied in at the same relative path for `dotnet restore`/`publish` to resolve it.
COPY server/src/DaprMQ.ApiServer/Protos/ ./server/src/DaprMQ.ApiServer/Protos/
COPY sdks/dotnet/src/DaprMQ.Client/ ./sdks/dotnet/src/DaprMQ.Client/
COPY examples/dotnet/producer/ ./examples/dotnet/producer/

WORKDIR /src/examples/dotnet/producer
RUN dotnet restore
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Producer.dll"]
