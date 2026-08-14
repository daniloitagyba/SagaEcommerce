#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
workload_file="$project_directory/load-tests/k6/orders.js"

profile=${1:-baseline}
base_url=${BASE_URL:-http://127.0.0.1:8088}

case "$profile" in
  smoke | baseline | autoscale | resilience | stress | soak | cache | chaos | overload | saga) ;;
  *)
    printf \
      'Unsupported profile "%s". Use smoke, baseline, autoscale, resilience, stress, soak, cache, chaos, overload, or saga.\n' \
      "$profile" >&2
    exit 1
    ;;
esac

command -v k6 >/dev/null || {
  echo "k6 is not installed locally (brew install k6)." >&2
  exit 1
}

if ! curl --fail --silent --show-error --max-time 5 "$base_url/health/ready" >/dev/null; then
  printf \
    'Orders API is not reachable at %s - is the SSH tunnel / kubectl port-forward running?\n' \
    "$base_url" >&2
  exit 1
fi

printf 'Running k6 profile "%s" against %s\n' "$profile" "$base_url"
k6 run \
  --env "BASE_URL=$base_url" \
  --env "PROFILE=$profile" \
  --env "RUN_ID=$(date -u +%Y%m%dT%H%M%SZ)" \
  "$workload_file"
