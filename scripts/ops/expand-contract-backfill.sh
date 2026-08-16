#!/usr/bin/env bash
# Mutates every row in `orders` with amount_cents IS NULL, in place, against
# whatever `compose/` checkout this happens to be run from - there was
# previously no way to preview the blast radius or confirm the target before
# the UPDATE loop started, unlike dlq-redrive.sh's --dry-run. Usage:
#   expand-contract-backfill.sh --dry-run   # count pending rows, mutate nothing
#   expand-contract-backfill.sh --yes       # skip the interactive confirmation
#   expand-contract-backfill.sh             # prompts before the first batch
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
compose_directory="$project_directory/compose"
batch_size=${BATCH_SIZE:-5000}
dry_run=false
skip_confirmation=false

for arg in "$@"; do
  case "$arg" in
    --dry-run) dry_run=true ;;
    --yes) skip_confirmation=true ;;
    *)
      echo "Unknown argument: $arg (expected --dry-run and/or --yes)" >&2
      exit 1
      ;;
  esac
done

command -v docker >/dev/null

query_pending_count() {
  (
    cd "$compose_directory"
    docker compose exec -T postgres \
      psql --username orders --dbname orders --tuples-only --no-align \
      --command 'SELECT count(*) FROM orders WHERE amount_cents IS NULL;'
  )
}

run_batch() {
  (
    cd "$compose_directory"
    docker compose exec -T postgres \
      psql --username orders --dbname orders --tuples-only --no-align \
      --command "
        WITH batch AS (
          SELECT id FROM orders
          WHERE amount_cents IS NULL
          LIMIT $batch_size
          FOR UPDATE SKIP LOCKED
        )
        UPDATE orders
        SET amount_cents = ROUND(orders.amount * 100)
        FROM batch
        WHERE orders.id = batch.id;
      "
  )
}

compose_project=$(cd "$compose_directory" && docker compose config --format json 2>/dev/null | python3 -c 'import json,sys; print(json.load(sys.stdin).get("name","unknown"))' 2>/dev/null || echo "unknown")

initial_pending=$(query_pending_count)
printf 'Target: docker compose project "%s" in %s\n' "$compose_project" "$compose_directory"
printf 'Backfilling amount_cents: %s rows pending, batch size %s\n' "$initial_pending" "$batch_size"

if [[ "$dry_run" == "true" ]]; then
  printf 'Dry run - no rows will be modified.\n'
  exit 0
fi

if [[ "$initial_pending" == "0" ]]; then
  printf 'Nothing pending - exiting without touching anything.\n'
  exit 0
fi

if [[ "$skip_confirmation" != "true" ]]; then
  printf 'About to UPDATE %s row(s) in the "orders" table above. Type "yes" to proceed: ' "$initial_pending"
  read -r confirmation
  if [[ "$confirmation" != "yes" ]]; then
    printf 'Aborted - no rows modified.\n'
    exit 1
  fi
fi

while true; do
  pending=$(query_pending_count)
  if [[ "$pending" == "0" ]]; then
    break
  fi
  run_batch >/dev/null
  printf 'Backfilled a batch; %s rows still pending\n' "$pending"
done

printf 'Backfill complete: 0 rows pending (started at %s)\n' "$initial_pending"
