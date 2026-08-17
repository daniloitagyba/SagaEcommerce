#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
results_root=${K6_RESULTS_DIRECTORY:-"$project_directory/artifacts/k6"}
prometheus_url=${PROMETHEUS_URL:-http://127.0.0.1:9090}
max_p95_lag_ms=${MAX_P95_PROJECTION_LAG_MS:-4000}

for command_name in curl jq; do
  command -v "$command_name" >/dev/null
done

run_id=$(date -u +%Y%m%dT%H%M%SZ)
test_directory="$results_root/$run_id-projection-lag"
mkdir -p "$test_directory"

printf 'Running the autoscale profile (unmodified) as write-side load\n'
"$script_directory/../load-test/k6-run.sh" autoscale 2>&1 | tee "$test_directory/autoscale-load.log"

printf 'Waiting for metrics to settle before sampling\n'
sleep 15

printf 'Sampling projection lag observed during the last 3 minutes\n'
p95_response=$(curl --fail --silent --get \
  --data-urlencode 'query=histogram_quantile(0.95, sum by (le, event_type) (rate(orders_projection_lag_ms_bucket[3m])))' \
  "$prometheus_url/api/v1/query")
echo "$p95_response" >"$test_directory/projection-lag-p95.json"

if [[ "$(echo "$p95_response" | jq --raw-output '.data.result | length')" == "0" ]]; then
  printf 'No projection lag samples were found in the query window.\n' >&2
  exit 1
fi

if echo "$p95_response" | jq --exit-status '.data.result[].value[1] | select(. == "NaN")' >/dev/null; then
  printf 'Projection lag could not be computed (NaN) for at least one event type - insufficient samples.\n' >&2
  exit 1
fi

echo "$p95_response" | jq -c '.data.result[] | {event_type: .metric.event_type, p95_ms: (.value[1] | tonumber)}' |
  tee "$test_directory/projection-lag-p95-summary.txt"

max_observed=$(echo "$p95_response" | jq --raw-output '[.data.result[].value[1] | tonumber] | max')
exceeded=$(jq --null-input --argjson observed "$max_observed" --argjson budget "$max_p95_lag_ms" '$observed > $budget')

if [[ "$exceeded" == "true" ]]; then
  printf 'Projection lag p95 %.0f ms exceeded the %s ms budget.\n' "$max_observed" "$max_p95_lag_ms" >&2
  exit 1
fi

printf 'PROJECTION_LAG_RESULT max_p95_ms=%.0f budget_ms=%s\n' "$max_observed" "$max_p95_lag_ms" |
  tee "$test_directory/result.txt"
