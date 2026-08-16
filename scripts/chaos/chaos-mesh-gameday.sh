#!/usr/bin/env bash
set -euo pipefail

# Milestone 31 game day: creates a batch of orders, kills orders-worker's
# pod with SIGKILL (Chaos Mesh PodChaos, action: pod-kill) partway through -
# an abrupt crash, not the graceful rolling restart Milestone 8's
# resilience-test.sh already covers - then measures whether every order
# still reaches a terminal, processed state and how long recovery takes.
# Hypothesis: zero data loss (Kafka at-least-once + the PostgreSQL Inbox's
# dedup by (consumer_name, event_id), same guarantee documented in the
# README's "Delivery guarantees" section), but this script measures that,
# it does not assume it.

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
namespace=${KUBERNETES_NAMESPACE:-orders-lab}
order_count=${1:-20}
kill_after=${2:-10}
results_root=${K6_RESULTS_DIRECTORY:-"$project_directory/artifacts/k6"}

for command_name in curl jq kubectl; do
  command -v "$command_name" >/dev/null
done

run_id=$(date -u +%Y%m%dT%H%M%SZ)
result_directory="$results_root/$run_id-chaos-mesh-gameday"
mkdir -p "$result_directory"

access_token=$("$script_directory/../infra/keycloak-get-token.sh")
auth_header="Authorization: Bearer $access_token"

service_ip=$(kubectl get service orders-api --namespace "$namespace" --output jsonpath='{.spec.clusterIP}')
orders_url="http://$service_ip"

echo "=== Chaos Mesh game day: orders-worker pod-kill mid-flight ==="
printf 'Creating %s orders, killing orders-worker after order #%s\n' "$order_count" "$kill_after"

order_ids_file="$result_directory/order-ids.txt"
: >"$order_ids_file"

for attempt in $(seq 1 "$order_count"); do
  order_id=$(curl --silent --request POST "$orders_url/orders" \
    --header 'Content-Type: application/json' \
    --header "$auth_header" \
    --header "X-Correlation-ID: chaos-mesh-gameday-$run_id-$attempt" \
    --data "{\"customerId\":\"chaos-mesh-gameday\",\"amount\":9.90,\"currency\":\"BRL\"}" |
    jq --raw-output '.id')
  echo "$order_id" >>"$order_ids_file"
  printf 'created order %s/%s: %s\n' "$attempt" "$order_count" "$order_id"

  if [[ "$attempt" -eq "$kill_after" ]]; then
    printf '\n--- killing orders-worker pod (SIGKILL via Chaos Mesh PodChaos) ---\n'
    kill_started_at=$(date +%s)
    kubectl apply --filename "$project_directory/kubernetes/chaos-experiments/podchaos-orders-worker-kill.yaml" >/dev/null
  fi
done

printf '\nWaiting for orders-worker to become ready again...\n'
kubectl wait --namespace "$namespace" --for=condition=ready pod \
  --selector app.kubernetes.io/name=orders-worker --timeout=120s >/dev/null
recovered_at=$(date +%s)
printf 'orders-worker ready again after %ss\n' "$((recovered_at - kill_started_at))"

printf '\nPolling until every order reaches a terminal state (Confirmed/Cancelled)...\n'
pending=$(wc -l <"$order_ids_file" | tr -d ' ')
converged=0
for _ in $(seq 1 60); do
  converged=0
  while read -r order_id; do
    status=$(curl --silent --header "$auth_header" "$orders_url/orders/$order_id" | jq --raw-output '.status')
    if [[ "$status" == "Confirmed" || "$status" == "Cancelled" ]]; then
      converged=$((converged + 1))
    fi
  done <"$order_ids_file"
  if [[ "$converged" -eq "$pending" ]]; then
    break
  fi
  sleep 2
done
converged_at=$(date +%s)

printf '\n=== Results ===\n'
printf 'Orders created: %s\n' "$pending"
printf 'Orders converged to a terminal state: %s\n' "$converged"
printf 'Data loss: %s order(s)\n' "$((pending - converged))"
printf 'Recovery time (kill to orders-worker ready): %ss\n' "$((recovered_at - kill_started_at))"
printf 'Total time (kill to full convergence): %ss\n' "$((converged_at - kill_started_at))"

kubectl delete --filename "$project_directory/kubernetes/chaos-experiments/podchaos-orders-worker-kill.yaml" --ignore-not-found >/dev/null

if [[ "$converged" -ne "$pending" ]]; then
  echo "GAME DAY FAILED: not every order converged." >&2
  exit 1
fi

echo "GAME DAY PASSED: every order converged with zero measured data loss."
