#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
namespace=${KUBERNETES_NAMESPACE:-orders-lab}
results_root=${K6_RESULTS_DIRECTORY:-"$project_directory/artifacts/k6"}
run_id=$(date -u +%Y%m%dT%H%M%SZ)
test_directory="$results_root/$run_id-hpa"
load_log="$test_directory/load.log"
timeline_file="$test_directory/hpa-timeline.csv"
load_pid=""

for command_name in jq kubectl; do
  command -v "$command_name" >/dev/null
done

mkdir -p "$test_directory"
printf 'timestamp,current_replicas,desired_replicas,ready_replicas,current_cpu\n' >"$timeline_file"

kubectl wait \
  --namespace "$namespace" \
  --for=condition=available \
  deployment/orders-worker \
  --timeout=120s >/dev/null

# orders-api is an Argo Rollout (Milestone 15), not a Deployment; its
# HPA-managed minimum replica count is read dynamically rather than assumed,
# since it has changed across milestones (2 originally, 3 since Milestone 13).
min_replicas=$(
  kubectl get horizontalpodautoscaler/orders-api \
    --namespace "$namespace" \
    --output jsonpath='{.spec.minReplicas}'
)

hpa_at_minimum=false
for _ in $(seq 1 36); do
  desired_replicas=$(
    kubectl get horizontalpodautoscaler/orders-api \
      --namespace "$namespace" \
      --output jsonpath='{.status.desiredReplicas}'
  )
  ready_replicas=$(
    kubectl get rollout/orders-api \
      --namespace "$namespace" \
      --output jsonpath='{.status.readyReplicas}'
  )
  if [[ "$desired_replicas" == "$min_replicas" && "$ready_replicas" == "$min_replicas" ]]; then
    hpa_at_minimum=true
    break
  fi
  printf 'Waiting for the HPA precondition: desired=%s ready=%s (want %s)\n' "$desired_replicas" "$ready_replicas" "$min_replicas"
  sleep 5
done
if [[ "$hpa_at_minimum" != true ]]; then
  echo "HPA did not reach its minimum before the test." >&2
  exit 1
fi

cleanup() {
  if [[ -n "$load_pid" ]] && kill -0 "$load_pid" 2>/dev/null; then
    kill "$load_pid" 2>/dev/null || true
    wait "$load_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT

(
  set -o pipefail
  "$script_directory/../load-test/k6-run.sh" autoscale 2>&1 | tee "$load_log"
) &
load_pid=$!

max_replicas=0
while kill -0 "$load_pid" 2>/dev/null; do
  hpa_json=$(kubectl get horizontalpodautoscaler/orders-api --namespace "$namespace" --output json)
  rollout_json=$(kubectl get rollout/orders-api --namespace "$namespace" --output json)
  current_replicas=$(jq --raw-output '.status.currentReplicas // 0' <<<"$hpa_json")
  desired_replicas=$(jq --raw-output '.status.desiredReplicas // 0' <<<"$hpa_json")
  current_cpu=$(jq --raw-output '.status.currentMetrics[0].resource.current.averageUtilization // 0' <<<"$hpa_json")
  ready_replicas=$(jq --raw-output '.status.readyReplicas // 0' <<<"$rollout_json")

  if (( current_replicas > max_replicas )); then
    max_replicas=$current_replicas
  fi
  if (( desired_replicas > max_replicas )); then
    max_replicas=$desired_replicas
  fi

  printf '%s,%s,%s,%s,%s\n' \
    "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
    "$current_replicas" \
    "$desired_replicas" \
    "$ready_replicas" \
    "$current_cpu" |
    tee --append "$timeline_file"
  sleep 5
done

set +e
wait "$load_pid"
load_exit_code=$?
set -e
load_pid=""

if (( load_exit_code != 0 )); then
  echo "The autoscaling workload failed." >&2
  exit "$load_exit_code"
fi

if (( max_replicas <= min_replicas )); then
  printf 'HPA did not scale above its minimum of %s; maximum observed was %s.\n' "$min_replicas" "$max_replicas" >&2
  exit 1
fi

scaled_down=false
for _ in $(seq 1 36); do
  desired_replicas=$(
    kubectl get horizontalpodautoscaler/orders-api \
      --namespace "$namespace" \
      --output jsonpath='{.status.desiredReplicas}'
  )
  ready_replicas=$(
    kubectl get rollout/orders-api \
      --namespace "$namespace" \
      --output jsonpath='{.status.readyReplicas}'
  )
  printf 'Waiting for scale-down: desired=%s ready=%s (want %s)\n' "$desired_replicas" "$ready_replicas" "$min_replicas"
  if [[ "$desired_replicas" == "$min_replicas" && "$ready_replicas" == "$min_replicas" ]]; then
    scaled_down=true
    break
  fi
  sleep 5
done

if [[ "$scaled_down" != true ]]; then
  echo "HPA did not return to two replicas within 180 seconds." >&2
  exit 1
fi

printf 'HPA_RESULT max_replicas=%s final_replicas=%s\n' "$max_replicas" "$min_replicas" |
  tee "$test_directory/result.txt"
