#!/usr/bin/env bash
# Milestone 54 live proof: Storefront.Service's new GET
# /api/storefront/products/{sku} is this lab's first genuine BFF fan-out -
# it calls Catalog and Inventory in parallel and waits for both, so its
# own tail latency is at least as bad as whichever leg is having a slow
# moment (Dean & Barroso's "Tail At Scale": P(any of N calls is slow)
# grows with N, even when each call is individually fine most of the
# time). A Toxiproxy latency toxic (15% toxicity, 200ms+/-50ms) models a
# dependency with a real but infrequent slow tail on the Inventory leg
# specifically - measures Catalog alone, Inventory alone, and the
# aggregate, with and without hedging the Inventory call.
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"
n=${1:-200}
sku="SKU-ELEC-001"

cd "$compose_directory"

measure() {
  local label=$1 url=$2
  local times_file
  times_file=$(mktemp)
  # Warm-up: JIT/connection-pool/DNS-resolution cost after a container
  # (re)start otherwise contaminates the first several timed samples -
  # discard 10 throwaway requests before the timed run.
  for i in $(seq 1 10); do
    docker compose exec -T storefront-service curl -s -o /dev/null "$url" >/dev/null 2>&1 || true
  done
  for i in $(seq 1 "$n"); do
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
