#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
namespace=${KUBERNETES_NAMESPACE:-orders-lab}
results_root=${K6_RESULTS_DIRECTORY:-"$project_directory/artifacts/k6"}
order_count=${1:-10}

for command_name in curl jq kubectl; do
  command -v "$command_name" >/dev/null
done

run_id=$(date -u +%Y%m%dT%H%M%SZ)
test_directory="$results_root/$run_id-saga-chaos"
mkdir -p "$test_directory"

access_token=$("$script_directory/../infra/keycloak-get-token.sh")
auth_header="Authorization: Bearer $access_token"

scaled_down=false
cleanup() {
  if [[ "$scaled_down" == true ]]; then
    kubectl scale deployment/payments-service --namespace "$namespace" --replicas=1 >/dev/null 2>&1 || true
    kubectl rollout status deployment/payments-service --namespace "$namespace" --timeout=60s >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

service_ip=$(kubectl get service orders-api --namespace "$namespace" --output jsonpath='{.spec.clusterIP}')

printf 'Scaling payments-service to zero replicas\n'
kubectl scale deployment/payments-service --namespace "$namespace" --replicas=0
kubectl wait --for=delete pod --namespace "$namespace" --selector app.kubernetes.io/name=payments-service --timeout=30s || true
scaled_down=true

printf 'Creating %s orders while payments-service is down\n' "$order_count"
order_ids_file="$test_directory/order-ids.txt"
: >"$order_ids_file"
for attempt in $(seq 1 "$order_count"); do
  if (( attempt % 2 == 0 )); then
    item='{"sku":"SKU-ELEC-001","quantity":1}'
  else
    item='{"sku":"SKU-BOOK-002","quantity":1}'
  fi
  order_id=$(curl --silent --request POST "http://$service_ip/orders" \
    --header 'Content-Type: application/json' \
    --header "$auth_header" \
    --header "X-Correlation-ID: saga-chaos-$run_id-$attempt" \
    --data "{\"customerId\":\"saga-chaos-customer\",\"items\":[${item}]}" |
    jq --raw-output '.id')
  echo "$order_id" >>"$order_ids_file"
done

printf 'Verifying all %s orders remain Created while payments-service is down\n' "$order_count"
sleep 3
stuck_as_created=0
while read -r order_id; do
  status=$(curl --silent --header "$auth_header" "http://$service_ip/orders/$order_id" | jq --raw-output '.status')
  if [[ "$status" == "Created" ]]; then
    stuck_as_created=$((stuck_as_created + 1))
  fi
done <"$order_ids_file"

if (( stuck_as_created != order_count )); then
  printf 'Expected all %s orders to remain Created with payments-service down, found %s.\n' \
    "$order_count" "$stuck_as_created" >&2
  exit 1
fi
printf 'Confirmed: all %s orders are Created (Payments.Service outage does not fail order creation)\n' "$order_count"

printf 'Scaling payments-service back to one replica\n'
kubectl scale deployment/payments-service --namespace "$namespace" --replicas=1
kubectl rollout status deployment/payments-service --namespace "$namespace" --timeout=60s
scaled_down=false

printf 'Waiting for all orders to converge to Confirmed or Cancelled\n'
converged=0
for attempt in $(seq 1 30); do
  converged=0
  while read -r order_id; do
    status=$(curl --silent --header "$auth_header" "http://$service_ip/orders/$order_id" | jq --raw-output '.status')
    if [[ "$status" == "Confirmed" || "$status" == "Cancelled" ]]; then
      converged=$((converged + 1))
    fi
  done <"$order_ids_file"
  if (( converged == order_count )); then
    break
  fi
  sleep 1
done

if (( converged != order_count )); then
  printf 'Only %s of %s orders converged after payments-service recovered.\n' "$converged" "$order_count" >&2
  exit 1
fi

printf 'SAGA_CHAOS_RESULT order_count=%s stuck_during_outage=%s converged_after_recovery=%s\n' \
  "$order_count" "$stuck_as_created" "$converged" |
  tee "$test_directory/result.txt"
