FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props global.json ./
COPY apps/Directory.Packages.props apps/
COPY apps/src/BuildingBlocks.Caching/BuildingBlocks.Caching.csproj apps/src/BuildingBlocks.Caching/
COPY apps/src/BuildingBlocks.Contracts/BuildingBlocks.Contracts.csproj apps/src/BuildingBlocks.Contracts/
COPY apps/src/BuildingBlocks.Messaging/BuildingBlocks.Messaging.csproj apps/src/BuildingBlocks.Messaging/
COPY apps/src/BuildingBlocks.Observability/BuildingBlocks.Observability.csproj apps/src/BuildingBlocks.Observability/
COPY apps/src/BuildingBlocks.Persistence/BuildingBlocks.Persistence.csproj apps/src/BuildingBlocks.Persistence/
COPY apps/src/BuildingBlocks.Resilience/BuildingBlocks.Resilience.csproj apps/src/BuildingBlocks.Resilience/
COPY apps/src/Catalog.Service/Catalog.Service.csproj apps/src/Catalog.Service/
RUN dotnet restore apps/src/Catalog.Service/Catalog.Service.csproj

COPY apps/src/BuildingBlocks.Caching/ apps/src/BuildingBlocks.Caching/
COPY apps/src/BuildingBlocks.Contracts/ apps/src/BuildingBlocks.Contracts/
COPY apps/src/BuildingBlocks.Messaging/ apps/src/BuildingBlocks.Messaging/
COPY apps/src/BuildingBlocks.Observability/ apps/src/BuildingBlocks.Observability/
COPY apps/src/BuildingBlocks.Persistence/ apps/src/BuildingBlocks.Persistence/
COPY apps/src/BuildingBlocks.Resilience/ apps/src/BuildingBlocks.Resilience/
COPY apps/src/Catalog.Service/ apps/src/Catalog.Service/
RUN dotnet publish apps/src/Catalog.Service/Catalog.Service.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Pyroscope's .NET profiler is a native CLR profiler, not a pure-managed NuGet
# package - the .so files aren't delivered by `dotnet restore`/`dotnet publish`
# and must be fetched separately. Baked into the image but inert unless
# PYROSCOPE_PROFILING_ENABLED=1 is set at deploy time (see kubernetes/base).
RUN curl -sL https://github.com/grafana/pyroscope-dotnet/releases/download/pyroscope-1.4.0/pyroscope.1.4.0-glibc-x86_64.tar.gz \
    | tar xz -C /opt

WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .

USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080 \
    OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317 \
    OTEL_EXPORTER_OTLP_PROTOCOL=grpc \
    OTEL_METRIC_EXPORT_INTERVAL=10000 \
    CORECLR_ENABLE_PROFILING=1 \
    CORECLR_PROFILER={BD1A650D-AC5D-4896-B64F-D6FA25D6B26A} \
    CORECLR_PROFILER_PATH=/opt/Pyroscope.Profiler.Native.so \
    LD_PRELOAD=/opt/Pyroscope.Linux.ApiWrapper.x64.so \
    LD_LIBRARY_PATH=/opt
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=10s --retries=5 \
    CMD curl --fail --silent http://127.0.0.1:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "Catalog.Service.dll"]
