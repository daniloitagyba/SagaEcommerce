#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
namespace=${KUBERNETES_NAMESPACE:-orders-lab}
local_port=${K3S_SMOKE_PORT:-18088}
temporary_directory=$(mktemp -d)

# orders-api is an Argo Rollout (Milestone 15), reconciled by Argo CD and
# horizontally scaled by an HPA (Milestone 13) - its replica count moves with
# load, so this only asserts every desired replica is actually ready rather
# than a specific fixed count.
for _ in $(seq 1 60); do
  rollout_json=$(kubectl get rollout/orders-api --namespace "$namespace" --output json 2>/dev/null) && break
  sleep 2
done
desired_replicas=$(jq --raw-output '.spec.replicas' <<<"$rollout_json")
ready_replicas=$(jq --raw-output '.status.readyReplicas // 0' <<<"$rollout_json")
if [[ -z "$desired_replicas" || "$ready_replicas" -lt "$desired_replicas" ]]; then
  printf 'Expected all Orders API replicas ready; desired=%s ready=%s.\n' "$desired_replicas" "$ready_replicas" >&2
  exit 1
fi

port_forward_log="$temporary_directory/port-forward.log"
port_forward_pid=""

cleanup() {
  if [[ -n "$port_forward_pid" ]]; then
    kill "$port_forward_pid" 2>/dev/null || true
    wait "$port_forward_pid" 2>/dev/null || true
  fi
  rm -rf -- "$temporary_directory"
}
trap cleanup EXIT

kubectl port-forward \
  --namespace "$namespace" \
  --address 127.0.0.1 \
  service/orders-api \
  "$local_port:80" >"$port_forward_log" 2>&1 &
port_forward_pid=$!

for attempt in $(seq 1 20); do
  if curl --fail --silent "http://127.0.0.1:$local_port/health/ready" >/dev/null; then
    break
  fi
  if ! kill -0 "$port_forward_pid" 2>/dev/null; then
    cat "$port_forward_log" >&2
    exit 1
  fi
  sleep 1
done

curl --fail --silent "http://127.0.0.1:$local_port/health/ready" >/dev/null

WORKER_RUNTIME=kubernetes \
KUBERNETES_NAMESPACE="$namespace" \
EXPECTED_API_INSTANCES=1 \
ORDERS_URL="http://127.0.0.1:$local_port" \
  "$script_directory/smoke-test.sh"

printf 'K3s smoke test verified two ready Orders API replicas.\n'
