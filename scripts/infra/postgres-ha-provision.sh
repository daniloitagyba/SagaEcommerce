#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"
data_platform_directory="$project_directory/kubernetes/data-platform"

# shellcheck disable=SC1091
source "$compose_directory/.env"

for command_name in docker kubectl; do
  command -v "$command_name" >/dev/null
done

(cd "$compose_directory" && docker compose --profile postgres-ha up --detach --wait minio)

kubectl create namespace postgres-ha --dry-run=client -o yaml | kubectl apply -f -

kubectl create secret generic minio-backup-credentials -n postgres-ha \
  --from-literal=ACCESS_KEY_ID=minio-admin \
  --from-literal=ACCESS_SECRET_KEY="$MINIO_ROOT_PASSWORD" \
  --dry-run=client -o yaml | kubectl apply -f -

docker run --rm --network local-distributed-lab_backend --entrypoint /bin/sh minio/mc -c "
  mc alias set localminio http://minio:9000 minio-admin '$MINIO_ROOT_PASSWORD' &&
  mc mb --ignore-existing localminio/postgres-backups
"

kubectl apply -f "$data_platform_directory/postgres-ha-cluster.yaml"

printf 'Waiting for orders-ha to become ready...\n'
until [[ "$(kubectl -n postgres-ha get cluster orders-ha -o jsonpath='{.status.readyInstances}' 2>/dev/null)" == "3" ]]; do
  sleep 5
done

printf 'orders-ha is ready (3/3 instances).\n'
