#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
prometheus_url=${PROMETHEUS_URL:-http://127.0.0.1:9090}
alertmanager_url=${ALERTMANAGER_URL:-http://127.0.0.1:9093}
results_root=${K6_RESULTS_DIRECTORY:-"$project_directory/artifacts/k6"}

# This script only generates load and observes the alerting pipeline - it
# does not change what image orders-api runs. Deploying the deliberately
# broken build (and reverting it afterward) goes through git and Argo CD,
# same as Milestone 15's canary rollback demo, since orders-api is
# GitOps-managed: a direct `kubectl set image` would just be reverted by
# Argo CD's selfHeal moments later.
for command_name in curl jq k6; do
  command -v "$command_name" >/dev/null
done

run_id=$(date -u +%Y%m%dT%H%M%SZ)
test_directory="$results_root/$run_id-slo-burn-rate"
mkdir -p "$test_directory"

alert_state() {
  curl --silent --get --data-urlencode "query=ALERTS{alertname=\"OrdersApiErrorBudgetBurnDemo\"}" \
    "$prometheus_url/api/v1/query" | jq --raw-output '.data.result[0].metric.alertstate // "inactive"'
}

mode=${1:?"Usage: $0 <generate-load|confirm-resolved>"}

case "$mode" in
  generate-load)
    printf 'Baseline: OrdersApiErrorBudgetBurnDemo state = %s\n' "$(alert_state)"

    printf 'Generating sustained load against the currently-deployed build (~2.5 minutes)\n'
    set +o errexit
    "$script_directory/../load-test/k6-run.sh" baseline >"$test_directory/load-1.log" 2>&1
    "$script_directory/../load-test/k6-run.sh" baseline >"$test_directory/load-2.log" 2>&1
    set -o errexit

    printf 'Polling for OrdersApiErrorBudgetBurnDemo to fire\n'
    fired=false
    for _ in $(seq 1 30); do
      state=$(alert_state)
      printf '  alert state: %s\n' "$state"
      if [[ "$state" == "firing" ]]; then
        fired=true
        break
      fi
      sleep 5
    done

    if [[ "$fired" != true ]]; then
      echo "OrdersApiErrorBudgetBurnDemo did not reach firing state." >&2
      exit 1
    fi

    printf 'Confirming Alertmanager received it\n'
    alertmanager_has_it=$(curl --silent "$alertmanager_url/api/v2/alerts" | jq --raw-output '[.[] | select(.labels.alertname == "OrdersApiErrorBudgetBurnDemo")] | length > 0')
    if [[ "$alertmanager_has_it" != true ]]; then
      echo "Alertmanager did not receive OrdersApiErrorBudgetBurnDemo." >&2
      exit 1
    fi

    printf 'SLO_BURN_RATE_RESULT phase=fired results=%s\n' "$test_directory" |
      tee "$test_directory/result-fired.txt"
    ;;

  confirm-resolved)
    resolved=false
    for _ in $(seq 1 36); do
      state=$(alert_state)
      printf '  alert state: %s\n' "$state"
      if [[ "$state" == "inactive" ]]; then
        resolved=true
        break
      fi
      sleep 10
    done

    if [[ "$resolved" != true ]]; then
      echo "OrdersApiErrorBudgetBurnDemo did not clear after recovery." >&2
      exit 1
    fi

    printf 'SLO_BURN_RATE_RESULT phase=resolved\n' |
      tee "$test_directory/result-resolved.txt"
    ;;

  *)
    echo "Usage: $0 <generate-load|confirm-resolved>" >&2
    exit 1
    ;;
esac
