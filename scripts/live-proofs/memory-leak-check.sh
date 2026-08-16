#!/usr/bin/env bash
# Guardrail: memory leak / unbounded heap growth detection ("heap without
# ref" - objects that should have become unreachable but are still
# retained by a lingering reference: an unsubscribed event handler, a
# cache with no eviction, a static collection that only ever grows).
#
# Rather than reaching for dotnet-counters/dotnet-gcdump (which need to
# attach to the live process - awkward against a containerized pod
# without the SDK installed in the runtime image), this reuses telemetry
# the stack already collects: OpenTelemetry's System.Runtime instrumentation
# exports dotnet_gc_last_collection_heap_size_bytes per generation to
# Prometheus already. A real leak's signature is long-lived generations
# (gen2 + LOH - short-lived gen0/gen1 churn constantly and aren't
# meaningful here) trending upward across a sustained load run instead of
# stabilizing once the garbage collector has had several chances to run.
#
# Drives scripts/k6-run.sh's `soak` profile (5 minutes, constant load
# against orders-api) as the sustained-load generator, then compares the
# first third of the run's gen2+LOH heap samples against the last third
# per replica pod.
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
service=${1:-orders-api}
prometheus_url=${PROMETHEUS_URL:-http://127.0.0.1:9090}
growth_threshold_pct=${2:-25}

start_epoch=$(date -u +%s)
echo "== Running a sustained load (k6 soak profile, ~5m20s) against ${service} to exercise steady-state GC behavior =="
set +e
bash "$script_directory/../load-test/k6-run.sh" soak
k6_exit=$?
set -e
if [[ "$k6_exit" -ne 0 ]]; then
  echo "NOTE: k6 exited ${k6_exit} (one of its own latency/error thresholds was crossed) - unrelated to this" >&2
  echo "check's own purpose. Load was still generated, so the heap analysis below still proceeds; the" >&2
  echo "threshold miss itself is a separate finding worth its own look." >&2
fi
end_epoch=$(date -u +%s)

echo ""
echo "== Querying Prometheus for gen2+LOH heap size across the run (service=${service}) =="
range_json_file=$(mktemp)
trap 'rm -f "$range_json_file"' EXIT

curl -s -G "${prometheus_url}/api/v1/query_range" \
  --data-urlencode "query=sum by (exported_instance) (dotnet_gc_last_collection_heap_size_bytes{service_name=\"${service}\", gc_heap_generation=~\"gen2|loh\"})" \
  --data-urlencode "start=${start_epoch}" \
  --data-urlencode "end=${end_epoch}" \
  --data-urlencode "step=15" > "$range_json_file"

# range_json_file is passed as argv, not piped on stdin: `python3 -` reads the
# script itself from stdin via this heredoc, so stdin isn't free for the JSON.
python3 - "$growth_threshold_pct" "$range_json_file" <<'PYEOF'
import json
import sys

threshold_pct = float(sys.argv[1])
with open(sys.argv[2]) as f:
    data = json.load(f)

if data.get("status") != "success":
    print(f"Prometheus query failed: {data}")
    sys.exit(2)

results = data["data"]["result"]
if not results:
    print("No heap samples returned - is the service name correct and Prometheus reachable?")
    sys.exit(2)

any_leak_suspected = False
for series in results:
    pod = series["metric"].get("exported_instance", "?")
    values = [float(v) for _, v in series["values"]]
    n = len(values)
    if n < 6:
        print(f"{pod}: only {n} samples - too few to assess a trend, skipping")
        continue

    third = max(1, n // 3)
    first_avg = sum(values[:third]) / third
    last_avg = sum(values[-third:]) / third
    growth_pct = ((last_avg - first_avg) / first_avg * 100) if first_avg > 0 else 0.0

    verdict = "OK"
    if growth_pct > threshold_pct:
        verdict = "SUSPECTED LEAK"
        any_leak_suspected = True

    print(f"{pod}: gen2+LOH heap first-third avg={first_avg/1024/1024:.2f}MiB "
          f"last-third avg={last_avg/1024/1024:.2f}MiB growth={growth_pct:+.1f}% -> {verdict}")

print("")
if any_leak_suspected:
    print(f"==> At least one replica's gen2+LOH heap grew more than {threshold_pct}% from the start of the "
          "run to the end, across a sustained 5-minute load with multiple GC cycles - long-lived object "
          "retention that isn't stabilizing. Investigate with a real dotnet-gcdump before/after diff.")
    sys.exit(1)
else:
    print(f"==> No replica's gen2+LOH heap grew more than {threshold_pct}% across the run - no leak signature detected.")
PYEOF
