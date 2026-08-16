# Milestone 63: Outbox/Inbox Retention

## Scope

`OutboxPublisher` marks a row published (`processed_at = now()`) but never deletes it. `InboxStore`/the inline inbox INSERT in `InventoryReservationMessageProcessor`/Payments' equivalent only ever insert. Nothing in this repo has ever deleted a row from `outbox_messages` or `inbox_messages`, in any of the three services that have them (Orders.Worker, Inventory.Service, Payments.Service). Verified against the live database before writing a line of code: Orders' `outbox_messages` alone had grown to 202,892 rows / 119 MB, and `inbox_messages` to 665,740 rows / 215 MB - entirely from this project's own load tests and milestone drills.

The obvious hypothesis going in was "the outbox poller's query degrades as the table grows." Measuring it first rather than assuming it turned out to matter: that hypothesis was wrong, and the real story is different and more interesting.

## Design

**Measured before building anything.** `EXPLAIN (ANALYZE, BUFFERS)` on the poller's actual query (`SELECT * FROM outbox_messages WHERE processed_at IS NULL AND next_attempt_at <= now() ORDER BY occurred_at LIMIT 100 FOR UPDATE SKIP LOCKED`) against the live 202,892-row table: **0.752 ms**, using `ix_outbox_messages_pending` - a partial index (`WHERE processed_at IS NULL`, added in the original `AddTransactionalOutbox` migration) that never contains a processed row at all. The poller genuinely does not care how much processed history has piled up.

The Inbox tells a different story. `PK_inbox_messages` on `(consumer_name, event_id)` has no partial filter - it must index every row forever, because the whole point is deduplicating against messages processed at any point in the past. A representative `INSERT ... ON CONFLICT (consumer_name, event_id) DO NOTHING` still only took 1.2 ms at 665,740 rows (B-tree lookups scale sub-linearly, so raw latency isn't the visible problem either, at least not yet) - but the disk cost is real, unbounded, and already measured at 215 MB for one table in one service.

**New shared `RetentionSweeper`** (`BuildingBlocks`) - a `BackgroundService` taking a connection string and a list of `(table, timestamp column)` targets, batch-deleting rows older than a configurable retention window (default 7 days) via `ctid`-batched deletes (1,000 rows per statement, looping until a batch returns fewer than the batch size) rather than one unbounded `DELETE ... WHERE ...` that would hold a long lock over hundreds of thousands of rows. Wired into all three services - Orders.Worker (`outbox_messages` + `inbox_messages`), Inventory.Service (same two), Payments.Service (same two) - since the table shape and cleanup logic are identical everywhere, just the table owner differs.

## What didn't work

**The hypothesis that motivated this milestone was wrong.** Expected the poller query to show real degradation from historical bloat; measured it first and found the existing partial index already prevents that entirely. Worth stating plainly rather than quietly dropping the disproven angle - Postgres's indexing is more resilient to pure historical growth than the initial assumption gave it credit for. The real, provable cost turned out to be disk space and the operational weight of a table that only ever grows, not query latency.

**Plain `VACUUM` reclaimed one table's disk space completely and left the other's almost untouched - because one is a heap-scan and one is index-bloat.** After batch-deleting all 202,892/665,740 rows and running `VACUUM (VERBOSE)` on both tables: `outbox_messages` dropped from 119 MB to 9.4 MB (`table "outbox_messages": truncated 14002 to 0 pages` - the heap file itself shrank, since VACUUM can truncate trailing all-empty pages). `inbox_messages` stayed at 127 MB despite `pg_relation_size` (the heap alone) reporting `0 bytes` - `pg_indexes_size` showed the real number: all 127 MB was sitting in its three indexes (`PK_inbox_messages`, `ix_inbox_messages_processed_at`, `ix_inbox_messages_source_position`), which plain `VACUUM` marks as reusable space but does not shrink on disk. `REINDEX TABLE inbox_messages` (29.5 ms) dropped it to 24 kB. A retention job that only runs `DELETE` + relies on autovacuum is not enough by itself to reclaim index-heavy tables - it needs an occasional `REINDEX` (or `VACUUM FULL`, at the cost of an exclusive lock) alongside it.

## Results

Orders' database, measured directly (`pg_total_relation_size`), before and after a full batch-delete + `VACUUM` + `REINDEX` pass:

| Table | Rows before | Size before | Size after `VACUUM` | Size after `REINDEX` |
| --- | --- | --- | --- | --- |
| `outbox_messages` | 202,892 | 119 MB | 9.4 MB | 40 kB |
| `inbox_messages` | 665,740 | 215 MB | 127 MB (heap: 0 bytes, all in indexes) | 48 kB |

Batch-deleting both tables (1,000-row batches via `ctid`) took 0.92 s and 1.96 s respectively - fast enough that the same logic running as a background sweep, on whatever schedule, is never going to compete meaningfully with live traffic.

`RetentionSweeper` deployed to Orders.Worker, Inventory.Service, and Payments.Service; all three started cleanly post-deploy with no wiring errors. Full solution (132 tests, 9 projects) still passes.

## Running it

```bash
# Check current size/row count for either table, any of the three databases
psql -c "SELECT count(*) FROM outbox_messages;" \
     -c "SELECT pg_size_pretty(pg_total_relation_size('outbox_messages'));"

# The sweeper runs automatically (default: every 60 minutes, 7-day retention) -
# to force an immediate pass while testing, lower Retention:RetentionDays and
# restart the service, or run the equivalent SQL directly:
DELETE FROM outbox_messages WHERE ctid IN (
  SELECT ctid FROM outbox_messages WHERE processed_at IS NOT NULL AND processed_at < now() - interval '7 days' LIMIT 1000
);
```

## Kafka replay and terminal-failure retention

The Compose topic bootstrap now gives business and CDC topics a **7-day**
retention (`604800000` ms), aligned with the default processed outbox/inbox
retention. This makes the replay promise coherent: an operator has seven days
to restore a consumer or rebuild a projection without the broker having
discarded data that the database still claims is within its evidence window.

DLQ topics retain records for **30 days** (`2592000000` ms). They are terminal
failure evidence, not a normal retry queue, and must be inspected/redriven with
the audited procedure in `docs/operations/runbooks.md`. The compacted `_schemas`
topic intentionally has no time-based retention.

Changing a retention value in Compose only configures topics when they are
created. Existing lab topics must be altered explicitly during a controlled
maintenance window and the effective configuration verified with
`kafka-configs.sh`; topic recreation is not an acceptable migration strategy.
