#!/usr/bin/env bash
set -euo pipefail

# Milestone 39: measures real p99 latency for GET /orders/{id} with and
# without client-side hedged requests (Dean & Barroso's "Tail at Scale"
# pattern) - fire a request to one orders-api replica, and if it hasn't
# answered within HEDGE_DELAY_MS, also fire to a different replica and take
# whichever responds first. Targets pod IPs directly rather than the K3s
# Service ClusterIP - hedging needs to race two distinct backends, and a
# single client connection through a Service VIP is load-balanced by
# kube-proxy at the TCP-connection level, not per request, so it can't be
# steered to a specific replica.

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
workload_file="$project_directory/load-tests/k6/orders.js"
namespace=${KUBERNETES_NAMESPACE:-orders-lab}
hedge_delay_ms=${1:-20}
results_root=${K6_RESULTS_DIRECTORY:-"$project_directory/artifacts/k6"}

for command_name in jq k6 kubectl; do
  command -v "$command_name" >/dev/null
done

pod_ips=$(kubectl get pods --namespace "$namespace" \
  --selector app.kubernetes.io/name=orders-api \
  --field-selector status.phase=Running \
  --output jsonpath='{.items[*].status.podIP}' | tr ' ' ',')
pod_count=$(echo "$pod_ips" | tr ',' '\n' | grep -c . || true)

if [[ "$pod_count" -lt 2 ]]; then
  printf 'Need at least 2 running orders-api pods to hedge across, found %s.\n' "$pod_count" >&2
  exit 1
fi
printf 'Hedging across %s orders-api replicas: %s\n' "$pod_count" "$pod_ips"

run_id=$(date -u +%Y%m%dT%H%M%SZ)
run_directory="$results_root/$run_id-hedged-read-benchmark"
mkdir -p "$run_directory"

access_token=$("$script_directory/keycloak-get-token.sh")

k6_writable_directory=${K6_WRITABLE_DIRECTORY:-"/home/$(id -un)/snap/k6/common"}
mkdir -p "$k6_writable_directory"
temporary_summary_file=$(mktemp "$k6_writable_directory/saga-ecommerce-hedged-summary.XXXXXX.json")
cleanup() {
  rm -f -- "$temporary_summary_file"
}
trap cleanup EXIT

printf 'Running hedged-read benchmark (hedge delay: %sms)...\n' "$hedge_delay_ms"
set +e
K6_NO_COLOR=true k6 run \
  --env "PROFILE=hedged" \
  --env "RUN_ID=$run_id" \
  --env "ACCESS_TOKEN=$access_token" \
  --env "ORDERS_API_POD_IPS=$pod_ips" \
  --env "HEDGE_DELAY_MS=$hedge_delay_ms" \
  --summary-export "$temporary_summary_file" \
  - <"$workload_file" 2>&1 |
  tee "$run_directory/k6.log"
k6_exit_code=${PIPESTATUS[0]}
set -e

if [[ ! -s "$temporary_summary_file" ]]; then
  echo "k6 did not produce a summary file." >&2
  exit 1
fi
cp "$temporary_summary_file" "$run_directory/summary.json"

jq --raw-output '
  def p(metric; stat): .metrics[metric][stat] // "n/a";
  "\n=== Results ===",
  "Unhedged GET /orders/:id  - p50=\(p("hedged_unhedged_read_duration_ms"; "med"))ms  p95=\(p("hedged_unhedged_read_duration_ms"; "p(95)"))ms  p99=\(p("hedged_unhedged_read_duration_ms"; "p(99)"))ms  max=\(p("hedged_unhedged_read_duration_ms"; "max"))ms",
  "Hedged GET /orders/:id    - p50=\(p("hedged_hedged_read_duration_ms"; "med"))ms  p95=\(p("hedged_hedged_read_duration_ms"; "p(95)"))ms  p99=\(p("hedged_hedged_read_duration_ms"; "p(99)"))ms  max=\(p("hedged_hedged_read_duration_ms"; "max"))ms",
  "Hedge fired rate: \(p("hedged_hedge_fired_rate"; "value"))  (fraction of reads where the primary was slower than \($hedgeDelay)ms)",
  "Hedge won rate (of fired hedges): \(p("hedged_hedge_won_rate"; "value"))"
' --arg hedgeDelay "$hedge_delay_ms" "$run_directory/summary.json" | tee "$run_directory/report.txt"

rm -f -- "$temporary_summary_file"
temporary_summary_file=""

exit "$k6_exit_code"
