#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
compose_directory="$project_directory/compose"
prometheus_url=${PROMETHEUS_URL:-http://127.0.0.1:9090}
results_root=${ALERT_RESULTS_DIRECTORY:-"$project_directory/artifacts/alerts"}

scenario=${1:?"Usage: $0 <anti-entropy|settlement-reconciliation|rate-limit-fail-open>"}
case "$scenario" in
  anti-entropy | settlement-reconciliation | rate-limit-fail-open) ;;
  *)
    printf 'Unsupported scenario "%s".\n' "$scenario" >&2
    exit 1
    ;;
esac

for command_name in curl docker jq python3; do
  command -v "$command_name" >/dev/null
done

compose() {
  docker compose \
    --project-directory "$compose_directory" \
    --file "$compose_directory/compose.yaml" \
    "$@"
}

run_id=$(date -u +%Y%m%dT%H%M%SZ)
test_directory="$results_root/$run_id-$scenario"
mkdir -p "$test_directory"

query_alert_count() {
  local alert_name=$1
  curl --fail --silent --show-error --get \
    --data-urlencode "query=ALERTS{alertname=\"$alert_name\",alertstate=\"firing\"}" \
    "$prometheus_url/api/v1/query" |
    jq --raw-output '.data.result | length'
}

wait_for_alert() {
  local alert_name=$1
  local attempts=$2
  for _ in $(seq 1 "$attempts"); do
    if [[ "$(query_alert_count "$alert_name")" -gt 0 ]]; then
      return 0
    fi
    sleep 5
  done
  printf 'Alert %s did not reach firing state.\n' "$alert_name" >&2
  return 1
}

require_alert_rule() {
  local alert_name=$1
  curl --fail --silent --show-error --request POST "$prometheus_url/-/reload" >/dev/null
  sleep 2
  local rule_count
  rule_count=$(curl --fail --silent --show-error "$prometheus_url/api/v1/rules" |
    jq --arg alert "$alert_name" '[.data.groups[].rules[] | select(.name == $alert)] | length')
  if [[ "$rule_count" -eq 0 ]]; then
    printf 'Alert rule %s is not loaded; recreate Prometheus if its bind mount is stale.\n' "$alert_name" >&2
    return 1
  fi
}

record_result() {
  local alert_name=$1
  jq --null-input \
    --arg scenario "$scenario" \
    --arg alert "$alert_name" \
    --arg completedAt "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    '{scenario:$scenario,alert:$alert,completed_at:$completedAt,result:"success"}' |
    tee "$test_directory/result.json"
}

curl --fail --silent --show-error "$prometheus_url/-/ready" >/dev/null

case "$scenario" in
  settlement-reconciliation)
    alert_name=SettlementReconciliationUnresolved
    require_alert_rule "$alert_name"
    # A counter's first exported sample establishes its baseline and does not
    # produce a positive increase(). Emit twice across a scrape boundary so a
    # clean lab with no pre-existing series proves the alert too.
    for sample in 1 2; do
      order_id=$(python3 -c 'import uuid; print(uuid.uuid4())')
      payment_id=$(python3 -c 'import uuid; print(uuid.uuid4())')
      correlation_id="alert-drill-$run_id-$sample"
      payload=$(jq --compact-output --null-input \
        --arg orderId "$order_id" \
        --arg paymentId "$payment_id" \
        --arg correlationId "$correlation_id" \
        --arg settledAt "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
        '{orderId:$orderId,paymentId:$paymentId,state:"Expired",amount:1.00,currency:"BRL",correlationId:$correlationId,settledAt:$settledAt,requiresReconciliation:true}')

      printf '%s|%s\n' "$order_id" "$payload" |
        compose exec -T kafka \
          /opt/kafka/bin/kafka-console-producer.sh \
          --bootstrap-server kafka:9092 \
          --topic payments.settlement-replied.v1 \
          --property parse.key=true \
          --property 'key.separator=|'
      if [[ "$sample" -eq 1 ]]; then
        sleep 20
      fi
    done

    wait_for_alert "$alert_name" 24
    record_result "$alert_name"
    ;;

  rate-limit-fail-open)
    alert_name=RateLimitingFailedOpen
    require_alert_rule "$alert_name"
    # shellcheck disable=SC1091
    source "$compose_directory/.env"
    orders_url=${ORDERS_URL:-"http://127.0.0.1:${ORDERS_PORT:-8088}"}
    access_token=$({ "$script_directory/../infra/keycloak-get-token.sh"; } 2>"$test_directory/token.log")
    redis_stopped=false
    restore_redis() {
      if [[ "$redis_stopped" == true ]]; then
        compose start redis >/dev/null
      fi
    }
    trap restore_redis EXIT

    compose stop --timeout 20 redis >/dev/null
    redis_stopped=true
    for _ in $(seq 1 4); do
      order_id=$(python3 -c 'import uuid; print(uuid.uuid4())')
      curl --silent --show-error --max-time 15 --output /dev/null \
        --header "Authorization: Bearer $access_token" \
        "$orders_url/orders/$order_id" || true
    done
    compose start redis >/dev/null
    redis_stopped=false

    wait_for_alert "$alert_name" 24
    curl --fail --silent --show-error "$orders_url/health/ready" >/dev/null
    record_result "$alert_name"
    ;;

  anti-entropy)
    alert_name=AntiEntropyDivergenceDetected
    require_alert_rule "$alert_name"
    reservation_id=$(python3 -c 'import uuid; print(uuid.uuid4())')
    order_id=$(python3 -c 'import uuid; print(uuid.uuid4())')
    marker_sku="ALERT-DRILL-$run_id"
    marker_inserted=false
    remove_marker() {
      if [[ "$marker_inserted" == true ]]; then
        compose exec -T postgres \
          psql --username orders --dbname inventory --quiet \
          --command "DELETE FROM backorders WHERE reservation_id = '$reservation_id';" >/dev/null || true
      fi
    }
    trap remove_marker EXIT

    compose exec -T postgres \
      psql --username orders --dbname inventory --quiet \
      --command "INSERT INTO backorders (reservation_id, order_id, sku, quantity, correlation_id, requested_at) VALUES ('$reservation_id', '$order_id', '$marker_sku', 1, 'alert-drill-$run_id', now());" >/dev/null
    marker_inserted=true

    # The normal lab sweep interval is five minutes. Waiting for the real
    # scheduled sweep proves the deployed loop and its cross-service reads,
    # not just the pure invariant function covered by unit tests.
    wait_for_alert "$alert_name" 72
    remove_marker
    marker_inserted=false
    record_result "$alert_name"
    ;;
esac
