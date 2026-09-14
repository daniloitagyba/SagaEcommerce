FROM node:26-alpine AS frontend-build
WORKDIR /src/apps/storefront-web

COPY apps/storefront-web/package.json apps/storefront-web/package-lock.json ./
RUN npm ci

COPY apps/storefront-web/ ./
# Vite inlines these at build time - the app is served from the same origin
# it calls, so no VITE_BFF_URL is needed, only the Keycloak endpoint the
# browser itself redirects to for login.
ARG VITE_KEYCLOAK_URL=http://localhost:18081
ARG VITE_KEYCLOAK_REALM=orders-lab
ARG VITE_KEYCLOAK_CLIENT_ID=orders-storefront
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src

COPY Directory.Build.props global.json ./
COPY apps/Directory.Packages.props apps/
COPY apps/src/BuildingBlocks.Caching/BuildingBlocks.Caching.csproj apps/src/BuildingBlocks.Caching/
COPY apps/src/BuildingBlocks.Contracts/BuildingBlocks.Contracts.csproj apps/src/BuildingBlocks.Contracts/
COPY apps/src/BuildingBlocks.Messaging/BuildingBlocks.Messaging.csproj apps/src/BuildingBlocks.Messaging/
COPY apps/src/BuildingBlocks.Observability/BuildingBlocks.Observability.csproj apps/src/BuildingBlocks.Observability/
COPY apps/src/BuildingBlocks.Persistence/BuildingBlocks.Persistence.csproj apps/src/BuildingBlocks.Persistence/
COPY apps/src/BuildingBlocks.Resilience/BuildingBlocks.Resilience.csproj apps/src/BuildingBlocks.Resilience/
COPY apps/src/Storefront.Service/Storefront.Service.csproj apps/src/Storefront.Service/
RUN dotnet restore apps/src/Storefront.Service/Storefront.Service.csproj

COPY apps/src/BuildingBlocks.Caching/ apps/src/BuildingBlocks.Caching/
COPY apps/src/BuildingBlocks.Contracts/ apps/src/BuildingBlocks.Contracts/
COPY apps/src/BuildingBlocks.Messaging/ apps/src/BuildingBlocks.Messaging/
COPY apps/src/BuildingBlocks.Observability/ apps/src/BuildingBlocks.Observability/
COPY apps/src/BuildingBlocks.Persistence/ apps/src/BuildingBlocks.Persistence/
COPY apps/src/BuildingBlocks.Resilience/ apps/src/BuildingBlocks.Resilience/
COPY apps/src/Storefront.Service/ apps/src/Storefront.Service/
RUN dotnet publish apps/src/Storefront.Service/Storefront.Service.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

# Pinned to a specific patch, not the floating 10.0 tag - matches the SDK
# pin above and the runtime this repo's CI actually tests against.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.11 AS runtime
RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build --chown=$APP_UID:$APP_UID /app/publish .
COPY --from=frontend-build --chown=$APP_UID:$APP_UID /src/apps/storefront-web/dist ./wwwroot

USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080 \
    OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317 \
    OTEL_EXPORTER_OTLP_PROTOCOL=grpc \
    OTEL_METRIC_EXPORT_INTERVAL=10000
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=10s --retries=5 \
    CMD curl --fail --silent http://127.0.0.1:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "Storefront.Service.dll"]
