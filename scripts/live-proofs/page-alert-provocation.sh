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
  local check_name=${2:-}
  local selector="alertname=\"$alert_name\",alertstate=\"firing\""
  if [[ -n "$check_name" ]]; then
    selector+=",check=\"$check_name\""
  fi
  curl --fail --silent --show-error --get \
    --data-urlencode "query=ALERTS{$selector}" \
    "$prometheus_url/api/v1/query" |
    jq --raw-output '.data.result | length'
}

wait_for_alert() {
  local alert_name=$1
  local attempts=$2
  local check_name=${3:-}
  for _ in $(seq 1 "$attempts"); do
    if [[ "$(query_alert_count "$alert_name" "$check_name")" -gt 0 ]]; then
      return 0
    fi
    sleep 5
  done
  printf 'Alert %s did not reach firing state.\n' "$alert_name" >&2
  return 1
}

query_counter_count() {
  local check_name=$1
  curl --fail --silent --show-error --get \
    --data-urlencode "query=anti_entropy_divergences_total{check=\"$check_name\"}" \
    "$prometheus_url/api/v1/query" |
    jq --raw-output '.data.result | length'
}

wait_for_counter() {
  local check_name=$1
  local attempts=$2
  for _ in $(seq 1 "$attempts"); do
    if [[ "$(query_counter_count "$check_name")" -gt 0 ]]; then
      return 0
    fi
    sleep 5
  done
  printf 'Counter for anti-entropy check %s was not exported.\n' "$check_name" >&2
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
  local check_name=${2:-}
  jq --null-input \
    --arg scenario "$scenario" \
    --arg alert "$alert_name" \
    --arg check "$check_name" \
    --arg completedAt "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    '{scenario:$scenario,alert:$alert,check:(if $check == "" then null else $check end),completed_at:$completedAt,result:"success"}' |
    tee "$test_directory/result.json"
}

curl --fail --silent --show-error "$prometheus_url/-/ready" >/dev/null

case "$scenario" in
  settlement-reconciliation)
    alert_name=SettlementReconciliationUnresolved
    require_alert_rule "$alert_name"
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

    for batch in 1 2; do
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
      if [[ "$batch" -eq 1 ]]; then
        sleep 20
      fi
    done

    wait_for_alert "$alert_name" 24
    curl --fail --silent --show-error "$orders_url/health/ready" >/dev/null
    record_result "$alert_name"
    ;;

  anti-entropy)
    alert_name=AntiEntropyDivergenceDetected
    check_name=backorder_on_dead_order
    require_alert_rule "$alert_name"
    reservation_id=$(python3 -c 'import uuid; print(uuid.uuid4())')
    order_id=$(python3 -c 'import uuid; print(uuid.uuid4())')
    marker_sku="ALERT-DRILL-$run_id"
    marker_inserted=false
    insert_marker() {
      compose exec -T postgres \
        psql --username orders --dbname inventory --quiet \
        --command "INSERT INTO backorders (reservation_id, order_id, sku, quantity, correlation_id, requested_at) VALUES ('$reservation_id', '$order_id', '$marker_sku', 1, 'alert-drill-$run_id', now());" >/dev/null
      marker_inserted=true
    }
    remove_marker() {
      if [[ "$marker_inserted" == true ]]; then
        compose exec -T postgres \
          psql --username orders --dbname inventory --quiet \
          --command "DELETE FROM backorders WHERE reservation_id = '$reservation_id';" >/dev/null || true
      fi
    }
    trap remove_marker EXIT

    counter_already_exported=$(query_counter_count "$check_name")
    insert_marker

    if [[ "$counter_already_exported" -eq 0 ]]; then
      wait_for_counter "$check_name" 72
      remove_marker
      marker_inserted=false
      reservation_id=$(python3 -c 'import uuid; print(uuid.uuid4())')
      order_id=$(python3 -c 'import uuid; print(uuid.uuid4())')
      marker_sku="ALERT-DRILL-$run_id-SECOND"
      insert_marker
    fi

    wait_for_alert "$alert_name" 72 "$check_name"
    remove_marker
    marker_inserted=false
    record_result "$alert_name" "$check_name"
    ;;
esac
