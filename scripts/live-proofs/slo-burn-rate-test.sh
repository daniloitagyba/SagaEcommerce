#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
compose_directory="$project_directory/compose"
prometheus_url=${PROMETHEUS_URL:-http://127.0.0.1:9090}
alertmanager_url=${ALERTMANAGER_URL:-http://127.0.0.1:9093}
results_root=${K6_RESULTS_DIRECTORY:-"$project_directory/artifacts/k6"}

# This script observes the alerting pipeline without changing the image that
# orders-api runs. The postgres-outage mode provokes real 5xx responses by
# stopping only the lab Compose database and restores it through an exit trap.
# A deliberately broken application build would still have to go through Git
# and Argo CD because a direct image change is reverted by self-healing.
for command_name in curl jq; do
  command -v "$command_name" >/dev/null
done

compose() {
  docker compose \
    --project-directory "$compose_directory" \
    --file "$compose_directory/compose.yaml" \
    "$@"
}

run_id=$(date -u +%Y%m%dT%H%M%SZ)
test_directory="$results_root/$run_id-slo-burn-rate"
mkdir -p "$test_directory"

alert_state() {
  curl --silent --get --data-urlencode "query=ALERTS{alertname=\"OrdersApiErrorBudgetBurnDemo\"}" \
    "$prometheus_url/api/v1/query" | jq --raw-output '.data.result[0].metric.alertstate // "inactive"'
}

mode=${1:?"Usage: $0 <generate-load|postgres-outage|confirm-resolved>"}

case "$mode" in
  generate-load)
    command -v k6 >/dev/null
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

  postgres-outage)
    for command_name in docker python3; do
      command -v "$command_name" >/dev/null
    done
    # shellcheck disable=SC1091
    source "$compose_directory/.env"
    orders_url=${ORDERS_URL:-"http://127.0.0.1:${ORDERS_PORT:-8088}"}
    access_token=$({ "$script_directory/../infra/keycloak-get-token.sh"; } 2>"$test_directory/token.log")

    curl --fail --silent --show-error --request POST "$prometheus_url/-/reload" >/dev/null
    sleep 2
    rule_count=$(curl --fail --silent --show-error "$prometheus_url/api/v1/rules" |
      jq '[.data.groups[].rules[] | select(.name == "OrdersApiErrorBudgetBurnDemo")] | length')
    [[ "$rule_count" -gt 0 ]]

    postgres_stopped=false
    restore_postgres() {
      if [[ "$postgres_stopped" == true ]]; then
        compose start postgres >/dev/null
      fi
    }
    trap restore_postgres EXIT

    compose stop --timeout 20 postgres >/dev/null
    postgres_stopped=true
    fired=false
    request_count=0
    server_error_count=0
    deadline=$((SECONDS + 120))
    while (( SECONDS < deadline )); do
      order_id=$(python3 -c 'import uuid; print(uuid.uuid4())')
      status_code=$(curl --silent --show-error --max-time 5 --output /dev/null \
        --write-out '%{http_code}' \
        --header "Authorization: Bearer $access_token" \
        "$orders_url/orders/$order_id" || true)
      request_count=$((request_count + 1))
      if [[ "$status_code" =~ ^5 ]]; then
        server_error_count=$((server_error_count + 1))
      fi
      if [[ "$(alert_state)" == "firing" ]]; then
        fired=true
        break
      fi
    done

    if [[ "$fired" != true || "$server_error_count" -eq 0 ]]; then
      echo "OrdersApiErrorBudgetBurnDemo did not fire during the controlled PostgreSQL outage." >&2
      exit 1
    fi

    compose start postgres >/dev/null
    postgres_stopped=false
    for _ in $(seq 1 60); do
      if curl --fail --silent --show-error --max-time 5 "$orders_url/health/ready" >/dev/null; then
        break
      fi
      sleep 5
    done
    curl --fail --silent --show-error --max-time 5 "$orders_url/health/ready" >/dev/null

    alertmanager_has_it=$(curl --silent "$alertmanager_url/api/v2/alerts" |
      jq --raw-output '[.[] | select(.labels.alertname == "OrdersApiErrorBudgetBurnDemo")] | length > 0')
    [[ "$alertmanager_has_it" == true ]]

    jq --null-input \
      --arg completedAt "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
      --argjson requests "$request_count" \
      --argjson serverErrors "$server_error_count" \
      '{scenario:"postgres-outage",alert:"OrdersApiErrorBudgetBurnDemo",completed_at:$completedAt,requests:$requests,server_errors:$serverErrors,recovered:true,result:"success"}' |
      tee "$test_directory/result-fired.json"

    resolved=false
    for _ in $(seq 1 36); do
      if [[ "$(alert_state)" == "inactive" ]]; then
        resolved=true
        break
      fi
      sleep 10
    done
    [[ "$resolved" == true ]]
    printf 'SLO_BURN_RATE_RESULT phase=fired-and-resolved results=%s\n' "$test_directory" |
      tee "$test_directory/result-resolved.txt"
    trap - EXIT
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
    echo "Usage: $0 <generate-load|postgres-outage|confirm-resolved>" >&2
    exit 1
    ;;
esac
