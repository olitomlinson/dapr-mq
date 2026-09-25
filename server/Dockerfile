# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Overridable at build time (--build-arg DaprActorsSdkVersion=...) to build against a
# different local Dapr.Actors SDK build from nuget-local/ - e.g. for comparing the
# reentrancyfix.1 and refreshfix.1 builds. Defaults to whatever the csproj files pin.
ARG DaprActorsSdkVersion=""

# Copy all source code
COPY src/ ./src/
COPY NuGet.config .
COPY nuget-local/ ./nuget-local/

# Restore and build
WORKDIR /src/src/DaprMQ.ApiServer
# Only pass the property when set - an empty global property would override the csproj default.
RUN dotnet restore ${DaprActorsSdkVersion:+-p:DaprActorsSdkVersion=$DaprActorsSdkVersion}
RUN dotnet publish -c Release -o /app/publish ${DaprActorsSdkVersion:+-p:DaprActorsSdkVersion=$DaprActorsSdkVersion}

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

# Expose port
EXPOSE 8080

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "DaprMQ.ApiServer.dll"]
