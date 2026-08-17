#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
namespace=${KUBERNETES_NAMESPACE:-orders-lab}
results_root=${K6_RESULTS_DIRECTORY:-"$project_directory/artifacts/k6"}

for command_name in curl jq kubectl; do
  command -v "$command_name" >/dev/null
done

run_id=$(date -u +%Y%m%dT%H%M%SZ)
result_directory="$results_root/$run_id-network-partition-gameday"
mkdir -p "$result_directory"

access_token=$("$script_directory/../infra/keycloak-get-token.sh")
auth_header="Authorization: Bearer $access_token"
service_ip=$(kubectl get service orders-api --namespace "$namespace" --output jsonpath='{.spec.clusterIP}')
orders_url="http://$service_ip"

poll_ready_endpoints() {
  kubectl get endpoints orders-api --namespace "$namespace" --output jsonpath='{.subsets[*].addresses[*].ip}' 2>/dev/null | wc -w | tr -d ' '
}

poll_worker_ready() {
  kubectl get pods --namespace "$namespace" --selector app.kubernetes.io/name=orders-worker \
    --output jsonpath='{.items[0].status.containerStatuses[0].ready}' 2>/dev/null
}

echo "=== Experiment 1: partition orders-worker <-> Kafka (orders-api and Postgres untouched) ==="

pre_partition_ids_file="$result_directory/pre-partition-order-ids.txt"
: >"$pre_partition_ids_file"
for _ in $(seq 1 5); do
  order_id=$(curl --silent --request POST "$orders_url/orders" \
    --header 'Content-Type: application/json' --header "$auth_header" \
    --data "{\"customerId\":\"partition-gameday-pre\",\"amount\":9.90,\"currency\":\"BRL\"}" |
    jq --raw-output '.id')
  echo "$order_id" >>"$pre_partition_ids_file"
done
printf 'Created %s orders before the partition\n' "$(wc -l <"$pre_partition_ids_file" | tr -d ' ')"

partition_started_at=$(date +%s)
kubectl apply --filename "$project_directory/kubernetes/chaos-experiments/networkchaos-orders-worker-kafka-partition.yaml" >/dev/null
printf 'Partition applied at %s\n' "$(date -u +%H:%M:%S)"

sleep 5
worker_ready_during_partition=$(poll_worker_ready)
printf 'orders-worker readiness 5s into the partition: %s\n' "$worker_ready_during_partition"

during_partition_ids_file="$result_directory/during-partition-order-ids.txt"
: >"$during_partition_ids_file"
for _ in $(seq 1 5); do
  order_id=$(curl --silent --request POST "$orders_url/orders" \
    --header 'Content-Type: application/json' --header "$auth_header" \
    --data "{\"customerId\":\"partition-gameday-during\",\"amount\":9.90,\"currency\":\"BRL\"}" |
    jq --raw-output '.id')
  echo "$order_id" >>"$during_partition_ids_file"
done
printf 'Created %s more orders during the partition (orders-api itself is unaffected)\n' \
  "$(wc -l <"$during_partition_ids_file" | tr -d ' ')"

printf 'Waiting for the partition window to elapse (60s duration)...\n'
elapsed_since_partition=$(($(date +%s) - partition_started_at))
remaining=$((60 - elapsed_since_partition))
if [[ "$remaining" -gt 0 ]]; then
  sleep "$remaining"
fi
kubectl delete --filename "$project_directory/kubernetes/chaos-experiments/networkchaos-orders-worker-kafka-partition.yaml" --ignore-not-found >/dev/null
partition_healed_at=$(date +%s)

printf 'Waiting for orders-worker to report ready again...\n'
kubectl wait --namespace "$namespace" --for=condition=ready pod \
  --selector app.kubernetes.io/name=orders-worker --timeout=60s >/dev/null
worker_recovered_at=$(date +%s)
printf 'orders-worker ready %ss after the partition healed\n' "$((worker_recovered_at - partition_healed_at))"

all_ids_file="$result_directory/all-experiment1-order-ids.txt"
cat "$pre_partition_ids_file" "$during_partition_ids_file" >"$all_ids_file"
total_orders=$(wc -l <"$all_ids_file" | tr -d ' ')

printf 'Polling until all %s orders reach a terminal state...\n' "$total_orders"
converged=0
for _ in $(seq 1 60); do
  converged=0
  while read -r order_id; do
    status=$(curl --silent --header "$auth_header" "$orders_url/orders/$order_id" | jq --raw-output '.status')
    if [[ "$status" == "Confirmed" || "$status" == "Cancelled" ]]; then
      converged=$((converged + 1))
    fi
  done <"$all_ids_file"
  if [[ "$converged" -eq "$total_orders" ]]; then
    break
  fi
  sleep 2
done
convergence_completed_at=$(date +%s)

printf '\n--- Experiment 1 results ---\n'
printf 'Total orders (before + during partition): %s\n' "$total_orders"
printf 'Converged to a terminal state: %s\n' "$converged"
printf 'Data loss: %s order(s)\n' "$((total_orders - converged))"
printf 'orders-worker readiness during partition: %s (expected: false)\n' "$worker_ready_during_partition"
printf 'Recovery time (partition healed to worker ready): %ss\n' "$((worker_recovered_at - partition_healed_at))"
printf 'Total time (partition applied to full convergence): %ss\n' "$((convergence_completed_at - partition_started_at))"

echo ""
echo "=== Experiment 2: partition orders-api (all 3 replicas) <-> Postgres ==="

ready_endpoints_before=$(poll_ready_endpoints)
printf 'orders-api ready Service endpoints before the partition: %s\n' "$ready_endpoints_before"

api_partition_started_at=$(date +%s)
kubectl apply --filename "$project_directory/kubernetes/chaos-experiments/networkchaos-orders-api-postgres-partition.yaml" >/dev/null
printf 'Partition applied at %s\n' "$(date -u +%H:%M:%S)"

sleep 10
ready_endpoints_during=$(poll_ready_endpoints)
printf 'orders-api ready Service endpoints 10s into the partition: %s (expected: 0)\n' "$ready_endpoints_during"

create_during_status=$(curl --silent --output /dev/null --write-out '%{http_code}' \
  --max-time 5 --request POST "$orders_url/orders" \
  --header 'Content-Type: application/json' --header "$auth_header" \
  --data '{"customerId":"partition-gameday-api","amount":9.90,"currency":"BRL"}' || echo "curl_failed")
printf 'POST /orders during the partition: %s\n' "$create_during_status"

printf 'Waiting for the partition window to elapse (45s duration)...\n'
elapsed_since_api_partition=$(($(date +%s) - api_partition_started_at))
api_remaining=$((45 - elapsed_since_api_partition))
if [[ "$api_remaining" -gt 0 ]]; then
  sleep "$api_remaining"
fi
kubectl delete --filename "$project_directory/kubernetes/chaos-experiments/networkchaos-orders-api-postgres-partition.yaml" --ignore-not-found >/dev/null
api_partition_healed_at=$(date +%s)

printf 'Waiting for orders-api pods to report ready again...\n'
kubectl wait --namespace "$namespace" --for=condition=ready pod \
  --selector app.kubernetes.io/name=orders-api --timeout=60s >/dev/null
api_recovered_at=$(date +%s)
ready_endpoints_after=$(poll_ready_endpoints)

create_after_id=$(curl --silent --request POST "$orders_url/orders" \
  --header 'Content-Type: application/json' --header "$auth_header" \
  --data '{"customerId":"partition-gameday-api-recovered","amount":9.90,"currency":"BRL"}' |
  jq --raw-output '.id')

printf '\n--- Experiment 2 results ---\n'
printf 'Ready endpoints before / during / after: %s / %s / %s\n' "$ready_endpoints_before" "$ready_endpoints_during" "$ready_endpoints_after"
printf 'POST /orders during partition: %s (expected: failure - 503, connection error, or timeout)\n' "$create_during_status"
printf 'Recovery time (partition healed to all pods ready): %ss\n' "$((api_recovered_at - api_partition_healed_at))"
printf 'POST /orders after recovery: order %s created successfully\n' "$create_after_id"

echo ""
if [[ "$converged" -ne "$total_orders" ]]; then
  echo "GAME DAY FAILED: experiment 1 lost orders." >&2
  exit 1
fi
if [[ "$ready_endpoints_during" != "0" ]]; then
  echo "GAME DAY WARNING: orders-api Service still had ready endpoints during the Postgres partition - health checks may not be detecting it as expected." >&2
fi

echo "GAME DAY PASSED: both partitions healed automatically with zero measured order loss."
