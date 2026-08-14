#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"
batch_size=${BATCH_SIZE:-5000}

for command_name in docker; do
  command -v "$command_name" >/dev/null
done

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

initial_pending=$(query_pending_count)
printf 'Backfilling amount_cents: %s rows pending, batch size %s\n' "$initial_pending" "$batch_size"

while true; do
  pending=$(query_pending_count)
  if [[ "$pending" == "0" ]]; then
    break
  fi
  run_batch >/dev/null
  printf 'Backfilled a batch; %s rows still pending\n' "$pending"
done

printf 'Backfill complete: 0 rows pending (started at %s)\n' "$initial_pending"
