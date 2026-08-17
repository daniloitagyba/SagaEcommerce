#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
compose_directory="$project_directory/compose"
n=${1:-200}
sku="SKU-ELEC-001"

cd "$compose_directory"

measure() {
  local label=$1 url=$2
  local times_file
  times_file=$(mktemp)
  for _ in $(seq 1 10); do
    docker compose exec -T storefront-service curl -s -o /dev/null "$url" >/dev/null 2>&1 || true
  done
  for _ in $(seq 1 "$n"); do
    docker compose exec -T storefront-service curl -s -o /dev/null -w "%{time_total}\n" "$url" >> "$times_file"
  done
  echo "-- ${label} --"
  sort -n "$times_file" | awk -v label="$label" '
    { a[NR]=$1*1000; sum+=$1*1000 }
    END {
      n=NR
      p50=a[int(n*0.50)+1]; p95=a[int(n*0.95)+1]; p99=a[int(n*0.99)+1]
      printf "%s: n=%d mean=%.1fms p50=%.1fms p95=%.1fms p99=%.1fms max=%.1fms\n", label, n, sum/n, p50, p95, p99, a[n]
    }'
  rm -f "$times_file"
}

aggregate_label=${2:-"Aggregate"}

echo "=== Individual legs (Inventory via jittered toxiproxy path) ==="
measure "Catalog alone" "http://catalog-service:8080/products/by-sku/${sku}"
measure "Inventory alone (jittered)" "http://toxiproxy:19170/inventory/${sku}"

echo ""
echo "=== Aggregate endpoint (${aggregate_label}) ==="
measure "Aggregate (${aggregate_label})" "http://localhost:8080/api/storefront/products/${sku}"
