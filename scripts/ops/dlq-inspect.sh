#!/usr/bin/env bash
# Milestone 62: peek at a dead-letter topic without consuming it for real -
# a fresh, never-committing consumer group every run, so this is always
# safe to run repeatedly against a live system. All five DLQ topics in this
# repo (orders.created.dlq.v1, orders.projection.dlq.v1,
# payments.result.dlq.v1, payments.decisions.dlq.v1,
# inventory.reservation.dlq.v1) share the same DeadLetterEnvelope shape, so
# one tool inspects any of them.
#
# Runs the already-built tool in a throwaway container attached to the
# compose stack's internal "backend" network, not directly on the host:
# Kafka's advertised listener for this network is the hostname "kafka",
# which only resolves via Docker's embedded DNS for containers on that
# network - not from the bare host, and "backend" is deliberately
# internal:true (no internet), so the build/restore has to happen on the
# host first and only the already-compiled DLL runs in the container.
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
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
