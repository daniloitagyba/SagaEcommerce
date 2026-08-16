#!/usr/bin/env bash
# Milestone 62: republish whatever is currently sitting in a dead-letter
# topic back to its original topic, preserving the original message key (the
# whole point - Sku/OrderId-keyed messages must land back on the same
# partition to keep the ownership guarantees from Milestones 36/41 intact)
# and capping how many times a given logical message can be redriven via
# the redrive-count header, so a message that fails identically every time
# can't loop forever.
#
# Never automatic, never scheduled - deliberately an operator-triggered
# action (see docs/messaging/milestone-62-dlq-redrive.md for why: a poison
# message redriven blindly is just a poison message that fails again, and
# only a human deciding "this one's fixed now" makes redrive meaningful).
#
# Same internal-network-container reasoning as dlq-inspect.sh.
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
topic=${1:?Usage: dlq-redrive.sh <dlq-topic> [--dry-run] [--key-filter <substring>] [--consumer-group <group>]}
shift
tool_arguments=("$@")

network=${COMPOSE_BACKEND_NETWORK:-local-distributed-lab_backend}
runtime_image=${DOTNET_RUNTIME_IMAGE:-mcr.microsoft.com/dotnet/aspnet:10.0}

cd "$project_directory/apps"
dotnet build src/DlqRedriveTool/DlqRedriveTool.csproj --configuration Release --nologo --verbosity quiet

docker run --rm --network "$network" \
  -v "$project_directory/apps/src/DlqRedriveTool/bin/Release/net10.0:/app" \
  -w /app \
  "$runtime_image" \
  dotnet DlqRedriveTool.dll redrive --bootstrap-servers kafka:9092 --topic "$topic" --max-redrives 3 "${tool_arguments[@]}"
