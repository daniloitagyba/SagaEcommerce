#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"
workload_file="$project_directory/load-tests/k6/orders.js"
namespace=${KUBERNETES_NAMESPACE:-orders-lab}
profile=${1:-baseline}
results_root=${K6_RESULTS_DIRECTORY:-"$project_directory/artifacts/k6"}
prometheus_url=${PROMETHEUS_URL:-http://127.0.0.1:9090}

case "$profile" in
  smoke | baseline | autoscale | resilience | stress | soak | cache | chaos | overload | saga) ;;
  *)
    printf \
      'Unsupported profile "%s". Use smoke, baseline, autoscale, resilience, stress, soak, cache, chaos, overload, or saga.\n' \
      "$profile" >&2
    exit 1
    ;;
esac

if [[ -n "${PIPELINE_DRAIN_TIMEOUT_SECONDS:-}" ]]; then
  pipeline_drain_timeout_seconds=$PIPELINE_DRAIN_TIMEOUT_SECONDS
elif [[ "$profile" == "autoscale" || "$profile" == "overload" ]]; then
  pipeline_drain_timeout_seconds=180
else
  pipeline_drain_timeout_seconds=60
fi
pipeline_drain_attempts=$((pipeline_drain_timeout_seconds / 2))

for command_name in awk curl docker jq k6 kubectl; do
  command -v "$command_name" >/dev/null
done

kubectl wait \
  --namespace "$namespace" \
  --for=condition=available \
  deployment/orders-worker \
  --timeout=120s >/dev/null

# orders-api is an Argo Rollout, not a Deployment (Milestone 15) - it has no
# "Available" condition, and its own status.phase is "Progressing" (not
# "Healthy") for the whole duration of an active canary, which this script
# must be able to run load against. Poll for at least one available replica
# instead of requiring the rollout to be fully promoted.
for _ in $(seq 1 60); do
  available=$(kubectl get rollout orders-api --namespace "$namespace" --output jsonpath='{.status.availableReplicas}' 2>/dev/null)
  if [[ -n "$available" && "$available" -ge 1 ]]; then
    break
  fi
  sleep 2
done

service_ip=$(kubectl get service orders-api --namespace "$namespace" --output jsonpath='{.spec.clusterIP}')
if [[ -z "$service_ip" || "$service_ip" == "None" ]]; then
  echo "The Orders API ClusterIP could not be resolved." >&2
  exit 1
fi

base_url=${BASE_URL:-"http://$service_ip"}
curl --fail --silent --show-error --max-time 5 "$base_url/health/ready" >/dev/null
curl --fail --silent --show-error --max-time 5 "$prometheus_url/-/ready" >/dev/null

run_id=$(date -u +%Y%m%dT%H%M%SZ)
run_directory="$results_root/$run_id-$profile"
mkdir -p "$run_directory"

query_database_scalar() {
  local query=$1
  (
    cd "$compose_directory"
    docker compose exec -T postgres \
      psql \
      --username orders \
      --dbname orders \
      --tuples-only \
      --no-align \
      --command "$query"
  )
}

query_consumer_lag() {
  (
    cd "$compose_directory"
    docker compose exec -T kafka \
      /opt/kafka/bin/kafka-consumer-groups.sh \
      --bootstrap-server kafka:9092 \
      --group orders-worker \
      --describe |
      awk '$1 == "orders-worker" { lag += $6 } END { print lag + 0 }'
  )
}

capture_prometheus_snapshot() {
  local output_file=$1
  curl --fail --silent --show-error \
    --get \
    --data-urlencode 'query=orders_created_total{k8s_namespace_name="orders-lab",service_name="orders-api"}' \
    "$prometheus_url/api/v1/query" >"$output_file"
}

prometheus_created_delta() {
  local before_file=$1
  local after_file=$2
  jq --null-input \
    --slurpfile before "$before_file" \
    --slurpfile after "$after_file" '
      def total($document):
        [$document[0].data.result[].value[1] | tonumber] | add // 0;
      (total($after) - total($before)) | floor
    '
}

consumer_lag=0
for attempt in $(seq 1 60); do
  consumer_lag=$(query_consumer_lag)
  if [[ "$consumer_lag" == "0" ]]; then
    break
  fi
  printf 'Waiting for the existing Kafka backlog to drain: lag=%s\n' "$consumer_lag"
  sleep 2
done
if [[ "$consumer_lag" != "0" ]]; then
  printf 'Kafka consumer lag did not reach zero before the workload: lag=%s\n' "$consumer_lag" >&2
  exit 1
fi

orders_before=$(query_database_scalar 'SELECT count(*) FROM orders;')
inbox_before=$(query_database_scalar "SELECT count(*) FROM inbox_messages WHERE consumer_name = 'orders-worker';")
capture_prometheus_snapshot "$run_directory/prometheus-before.json"

jq --null-input \
  --arg runId "$run_id" \
  --arg profile "$profile" \
  --arg baseUrl "$base_url" \
  --arg gitCommit "$(git -C "$project_directory" rev-parse HEAD)" \
  --arg k6Version "$(k6 version | head -1)" \
  --arg node "$(kubectl get nodes --output jsonpath='{.items[0].metadata.name}')" \
  '{
    run_id: $runId,
    profile: $profile,
    base_url: $baseUrl,
    git_commit: $gitCommit,
    k6_version: $k6Version,
    kubernetes_node: $node
  }' >"$run_directory/metadata.json"

k6_writable_directory=${K6_WRITABLE_DIRECTORY:-"/home/$(id -un)/snap/k6/common"}
mkdir -p "$k6_writable_directory"
temporary_summary_file=$(mktemp "$k6_writable_directory/saga-ecommerce-summary.XXXXXX.json")

printf 'timestamp,pod,cpu,memory\n' >"$run_directory/resources.csv"
(
  while true; do
    timestamp=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    kubectl top pods --namespace "$namespace" --no-headers 2>/dev/null |
      awk -v timestamp="$timestamp" '{ print timestamp "," $1 "," $2 "," $3 }' \
        >>"$run_directory/resources.csv" || true
    sleep 2
  done
) &
sampler_pid=$!

cleanup() {
  if [[ -n "${sampler_pid:-}" ]] && kill -0 "$sampler_pid" 2>/dev/null; then
    kill "$sampler_pid" 2>/dev/null || true
    wait "$sampler_pid" 2>/dev/null || true
  fi
  if [[ -n "${temporary_summary_file:-}" && -f "$temporary_summary_file" ]]; then
    rm -f -- "$temporary_summary_file"
  fi
}
trap cleanup EXIT

access_token=$("$script_directory/keycloak-get-token.sh")

printf 'Running k6 profile %s against %s\n' "$profile" "$base_url"
set +e
K6_NO_COLOR=true k6 run \
  --env "BASE_URL=$base_url" \
  --env "PROFILE=$profile" \
  --env "RUN_ID=$run_id" \
  --env "ACCESS_TOKEN=$access_token" \
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
rm -f -- "$temporary_summary_file"
temporary_summary_file=""

orders_after=$(query_database_scalar 'SELECT count(*) FROM orders;')
outbox_pending=$(query_database_scalar 'SELECT count(*) FROM outbox_messages WHERE processed_at IS NULL;')
inbox_after=$(query_database_scalar "SELECT count(*) FROM inbox_messages WHERE consumer_name = 'orders-worker';")
orders_created=$((orders_after - orders_before))

for attempt in $(seq 1 "$pipeline_drain_attempts"); do
  inbox_after=$(query_database_scalar "SELECT count(*) FROM inbox_messages WHERE consumer_name = 'orders-worker';")
  outbox_pending=$(query_database_scalar 'SELECT count(*) FROM outbox_messages WHERE processed_at IS NULL;')
  inbox_processed=$((inbox_after - inbox_before))
  if (( inbox_processed >= orders_created )) && [[ "$outbox_pending" == "0" ]]; then
    break
  fi
  sleep 2
done

for attempt in $(seq 1 15); do
  capture_prometheus_snapshot "$run_directory/prometheus-after.json"
  prometheus_delta=$(
    prometheus_created_delta \
      "$run_directory/prometheus-before.json" \
      "$run_directory/prometheus-after.json"
  )
  if (( prometheus_delta >= orders_created )); then
    break
  fi
  sleep 2
done
kubectl top pods --namespace "$namespace" >"$run_directory/resources-after.txt"

inbox_processed=$((inbox_after - inbox_before))
jq --null-input \
  --argjson ordersCreated "$orders_created" \
  --argjson inboxProcessed "$inbox_processed" \
  --argjson outboxPending "$outbox_pending" \
  '{
    orders_created: $ordersCreated,
    inbox_processed: $inboxProcessed,
    outbox_pending: $outboxPending
  }' >"$run_directory/workload-state.json"

"$script_directory/k6-report.sh" "$run_directory" |
  tee "$run_directory/report.txt"

if (( inbox_processed < orders_created )) || [[ "$outbox_pending" != "0" ]]; then
  printf \
    'The asynchronous pipeline did not drain within %s seconds.\n' \
    "$pipeline_drain_timeout_seconds" >&2
  exit 1
fi

exit "$k6_exit_code"
