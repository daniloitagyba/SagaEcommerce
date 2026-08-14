#!/usr/bin/env bash
set -euo pipefail

run_directory=${1:?Usage: k6-report.sh <run-directory>}
summary_file="$run_directory/summary.json"
resources_file="$run_directory/resources.csv"
state_file="$run_directory/workload-state.json"
before_file="$run_directory/prometheus-before.json"
after_file="$run_directory/prometheus-after.json"

for file in "$summary_file" "$resources_file" "$state_file" "$before_file" "$after_file"; do
  if [[ ! -f "$file" ]]; then
    printf 'Required result file not found: %s\n' "$file" >&2
    exit 1
  fi
done

metric_value() {
  local metric_name=$1
  local value_name=$2
  jq --raw-output \
    --arg metric "$metric_name" \
    --arg value "$value_name" \
    '(
      .metrics[$metric][$value]
      // .metrics[$metric].values[$value]
      // (if $value == "rate" then .metrics[$metric].value else null end)
      // 0
    )' \
    "$summary_file"
}

printf 'K6_RESULT requests=%s iterations=%s failed_rate=%s checks_rate=%s flow_rate=%s\n' \
  "$(metric_value http_reqs count)" \
  "$(metric_value iterations count)" \
  "$(metric_value http_req_failed rate)" \
  "$(metric_value checks rate)" \
  "$(metric_value order_flow_success rate)"

printf 'K6_LATENCY create_p95_ms=%s create_p99_ms=%s get_p95_ms=%s get_p99_ms=%s\n' \
  "$(metric_value 'http_req_duration{endpoint:create-order}' 'p(95)')" \
  "$(metric_value 'http_req_duration{endpoint:create-order}' 'p(99)')" \
  "$(metric_value 'http_req_duration{endpoint:get-order}' 'p(95)')" \
  "$(metric_value 'http_req_duration{endpoint:get-order}' 'p(99)')"

jq --raw-output \
  '"PIPELINE orders_created=\(.orders_created) inbox_processed=\(.inbox_processed) outbox_pending=\(.outbox_pending)"' \
  "$state_file"

awk -F, '
  NR > 1 {
    cpu = $3
    memory = $4
    gsub(/m$/, "", cpu)
    gsub(/Mi$/, "", memory)
    if (cpu + 0 > max_cpu[$2] + 0) {
      max_cpu[$2] = cpu
    }
    if (memory + 0 > max_memory[$2] + 0) {
      max_memory[$2] = memory
    }
  }
  END {
    for (pod in max_cpu) {
      printf "RESOURCE_PEAK pod=%s cpu_m=%s memory_mi=%s\n", pod, max_cpu[pod], max_memory[pod]
    }
  }
' "$resources_file" | sort

jq --null-input \
  --slurpfile before "$before_file" \
  --slurpfile after "$after_file" '
    def series($document):
      reduce $document[0].data.result[] as $item
        ({};
          .[$item.metric.service_instance_id] = ($item.value[1] | tonumber)
        );
    (series($before)) as $beforeSeries
    | (series($after)) as $afterSeries
    | $afterSeries
    | to_entries[]
    | {
        instance: .key,
        created: (.value - ($beforeSeries[.key] // 0))
      }
    | select(.created > 0)
    | "API_DISTRIBUTION instance=\(.instance) orders_created=\(.created | floor)"
  ' --raw-output | sort
