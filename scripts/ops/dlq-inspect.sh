#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
topic=${1:?Usage: dlq-inspect.sh <dlq-topic>}
network=${COMPOSE_BACKEND_NETWORK:-local-distributed-lab_backend}
runtime_image=${DOTNET_RUNTIME_IMAGE:-mcr.microsoft.com/dotnet/aspnet:10.0}

cd "$project_directory/apps"
dotnet build src/DlqRedriveTool/DlqRedriveTool.csproj --configuration Release --nologo --verbosity quiet

docker run --rm --network "$network" \
  -v "$project_directory/apps/src/DlqRedriveTool/bin/Release/net10.0:/app" \
  -w /app \
  "$runtime_image" \
  dotnet DlqRedriveTool.dll inspect --bootstrap-servers kafka:9092 --topic "$topic"
