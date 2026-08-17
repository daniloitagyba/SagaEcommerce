#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
namespace=${KUBERNETES_NAMESPACE:-orders-lab}
toxiproxy_admin_url=${TOXIPROXY_ADMIN_URL:-http://127.0.0.1:8474}
results_root=${K6_RESULTS_DIRECTORY:-"$project_directory/artifacts/k6"}

target=${1:?"Usage: $0 <postgres|kafka> <latency|outage>"}
scenario=${2:?"Usage: $0 <postgres|kafka> <latency|outage>"}

case "$target" in
  postgres) proxy_name=postgres; proxy_port=15432 ;;
  kafka) proxy_name=kafka; proxy_port=19092 ;;
  *)
    printf 'Unsupported target "%s". Use postgres or kafka.\n' "$target" >&2
    exit 1
    ;;
esac

case "$scenario" in
  latency | outage) ;;
  *)
    printf 'Unsupported scenario "%s". Use latency or outage.\n' "$scenario" >&2
    exit 1
    ;;
esac

for command_name in curl docker jq kubectl; do
  command -v "$command_name" >/dev/null
done

run_id=$(date -u +%Y%m%dT%H%M%SZ)
test_directory="$results_root/$run_id-chaos-$target-$scenario"
mkdir -p "$test_directory"

access_token=$("$script_directory/../infra/keycloak-get-token.sh")
auth_header="Authorization: Bearer $access_token"

endpointslice_name="${target}-compose"
backup_file="$test_directory/endpointslice-backup.json"
kubectl get endpointslice "$endpointslice_name" --namespace "$namespace" --output json >"$backup_file"
real_address=$(jq --raw-output '.endpoints[0].addresses[0]' "$backup_file")
real_port=$(jq --raw-output '.ports[0].port' "$backup_file")

toxic_added=false
proxy_disabled=false
endpointslice_patched=false

restart_workloads() {
  kubectl patch rollout/orders-api --namespace "$namespace" --type merge \
    --patch "{\"spec\":{\"restartAt\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"}}"
  kubectl rollout restart deployment/orders-worker --namespace "$namespace"
  for _ in $(seq 1 60); do
    desired=$(kubectl get rollout/orders-api --namespace "$namespace" --output jsonpath='{.spec.replicas}')
    available=$(kubectl get rollout/orders-api --namespace "$namespace" --output jsonpath='{.status.availableReplicas}')
    updated=$(kubectl get rollout/orders-api --namespace "$namespace" --output jsonpath='{.status.updatedReplicas}')
    if [[ -n "$available" && "$available" -ge "$desired" && -n "$updated" && "$updated" -ge "$desired" ]]; then
      break
    fi
    sleep 2
  done
  kubectl rollout status deployment/orders-worker --namespace "$namespace" --timeout=120s
}

route_endpointslice() {
  local address=$1
  local port=$2
  kubectl patch endpointslice "$endpointslice_name" --namespace "$namespace" --type=json \
    --patch "[{\"op\":\"replace\",\"path\":\"/endpoints/0/addresses/0\",\"value\":\"$address\"},{\"op\":\"replace\",\"path\":\"/ports/0/port\",\"value\":$port}]" >/dev/null
}

functional_smoke_check() {
  local log_file=$1
  set +o errexit
  "$script_directory/../load-test/k6-run.sh" smoke >"${log_file%.log}-warmup.log" 2>&1
  "$script_directory/../load-test/k6-run.sh" smoke >"$log_file" 2>&1
  set -o errexit

  grep --quiet 'K6_RESULT requests=.*failed_rate=0 checks_rate=1 flow_rate=1' "$log_file"
}

cleanup() {
  if [[ "$toxic_added" == true ]]; then
    curl --silent --request DELETE "$toxiproxy_admin_url/proxies/$proxy_name/toxics/chaos-latency" >/dev/null 2>&1 || true
  fi
  if [[ "$proxy_disabled" == true ]]; then
    curl --silent --request POST --header 'Content-Type: application/json' \
      --data '{"enabled":true}' "$toxiproxy_admin_url/proxies/$proxy_name" >/dev/null 2>&1 || true
  fi
  if [[ "$endpointslice_patched" == true ]]; then
    route_endpointslice "$real_address" "$real_port" || true
    restart_workloads || true
  fi
}
trap cleanup EXIT

curl --fail --silent --show-error "$toxiproxy_admin_url/proxies" >/dev/null

printf 'Routing %s traffic through Toxiproxy (%s:%s)\n' "$target" 172.30.0.14 "$proxy_port"
route_endpointslice 172.30.0.14 "$proxy_port"
endpointslice_patched=true

restart_workloads

printf 'Verifying connectivity through Toxiproxy before injecting a fault\n'
if ! functional_smoke_check "$test_directory/pre-fault-smoke.log"; then
  echo "Traffic did not flow correctly through Toxiproxy before fault injection." >&2
  exit 1
fi

if [[ "$scenario" == "latency" ]]; then
  printf 'Injecting latency toxic (100ms +/- 20ms jitter) on %s\n' "$target"
  curl --fail --silent --request POST --header 'Content-Type: application/json' \
    --data '{"name":"chaos-latency","type":"latency","attributes":{"latency":100,"jitter":20}}' \
    "$toxiproxy_admin_url/proxies/$proxy_name/toxics" >/dev/null
  toxic_added=true

  printf 'Running the chaos k6 profile with the latency toxic active\n'
  set +o errexit
  "$script_directory/../load-test/k6-run.sh" chaos 2>&1 | tee "$test_directory/chaos-load.log"
  chaos_exit_code=${PIPESTATUS[0]}
  set -o errexit

  curl --silent --request DELETE "$toxiproxy_admin_url/proxies/$proxy_name/toxics/chaos-latency" >/dev/null
  toxic_added=false

  if (( chaos_exit_code != 0 )); then
    echo "The chaos workload failed under latency injection." >&2
    exit "$chaos_exit_code"
  fi
else
  printf 'Disabling the %s proxy to simulate a hard outage\n' "$target"
  curl --fail --silent --request POST --header 'Content-Type: application/json' \
    --data '{"enabled":false}' "$toxiproxy_admin_url/proxies/$proxy_name" >/dev/null
  proxy_disabled=true

  service_ip=$(kubectl get service orders-api --namespace "$namespace" --output jsonpath='{.spec.clusterIP}')
  outage_log="$test_directory/outage-requests.log"
  : >"$outage_log"

  printf 'Sending requests during the outage window\n'
  for attempt in $(seq 1 10); do
    started_ms=$(date +%s%3N)
    status=$(curl --silent --output /dev/null --write-out '%{http_code}' --max-time 5 \
      --request POST "http://$service_ip/orders" \
      --header 'Content-Type: application/json' \
      --header "$auth_header" \
      --header "X-Correlation-ID: chaos-outage-$run_id-$attempt" \
      --data '{"customerId":"chaos-outage-customer","amount":9.99,"currency":"BRL"}' || echo "000")
    ended_ms=$(date +%s%3N)
    elapsed_ms=$((ended_ms - started_ms))
    printf 'attempt=%s status=%s elapsed_ms=%s\n' "$attempt" "$status" "$elapsed_ms" | tee -a "$outage_log"
    sleep 0.5
  done

  curl --fail --silent --request POST --header 'Content-Type: application/json' \
    --data '{"enabled":true}' "$toxiproxy_admin_url/proxies/$proxy_name" >/dev/null
  proxy_disabled=false
fi

printf 'Reverting %s to the real backend (%s:%s)\n' "$target" "$real_address" "$real_port"
route_endpointslice "$real_address" "$real_port"
endpointslice_patched=false
restart_workloads

printf 'Verifying recovery\n'
current_address=$(kubectl get endpointslice "$endpointslice_name" --namespace "$namespace" --output jsonpath='{.endpoints[0].addresses[0]}')
current_port=$(kubectl get endpointslice "$endpointslice_name" --namespace "$namespace" --output jsonpath='{.ports[0].port}')
if [[ "$current_address" != "$real_address" || "$current_port" != "$real_port" ]]; then
  printf 'EndpointSlice %s did not revert: expected %s:%s, found %s:%s\n' \
    "$endpointslice_name" "$real_address" "$real_port" "$current_address" "$current_port" >&2
  exit 1
fi

sleep 3
if ! functional_smoke_check "$test_directory/post-fault-smoke.log"; then
  echo "Recovery smoke check reported failures after reverting the fault." >&2
  exit 1
fi

printf 'CHAOS_RESULT target=%s scenario=%s recovered=true results=%s\n' \
  "$target" "$scenario" "$test_directory" |
  tee "$test_directory/result.txt"
