# Milestone 62: DLQ Redrive, and Why It's Harder Than It Looks

## Scope

Five dead-letter topics exist in this system - `orders.created.dlq.v1`, `orders.projection.dlq.v1`, `payments.result.dlq.v1`, `payments.decisions.dlq.v1`, `inventory.reservation.dlq.v1` - each fed by a real publisher (`KafkaDeadLetterPublisher`, `OrderProjectionDeadLetterPublisher`, `PaymentResultDeadLetterPublisher`, `PaymentDeadLetterPublisher`, `InventoryDeadLetterPublisher`). All five have been write-only since they were introduced: a poison message goes in, and nothing has ever come out. This milestone builds the missing other half - inspect and redrive - and, in the process of actually proving it works rather than just writing it, found and fixed three real bugs, and surfaced one genuine architectural limitation worth documenting honestly rather than papering over.

## Design

**`DlqRedriveTool`** (`apps/src/DlqRedriveTool`) - a standalone console tool with no project reference to any one service, because every publisher writes the same `DeadLetterEnvelope` JSON shape and header set (`original-topic`, `original-partition`, `original-offset`, `failure-type`, `attempt-count`, plus a new `redrive-count`). Two modes:

- **`inspect`** - a fresh, never-committing consumer group every run, so it's always safe to run repeatedly against a live topic. Prints each envelope and a failure-type summary.
- **`redrive`** - republishes whatever is currently sitting in the DLQ back to `OriginalTopic`, preserving `OriginalKey` exactly (the entire point - Sku/OrderId-keyed messages must land back on the same partition to keep the Milestone 36/41 ownership guarantees intact), and caps how many times a given logical message can be redriven via the `redrive-count` header (default max 3). A `--key-filter <substring>` option lets an operator scope a redrive to one specific message among many, without touching the rest of the topic.

Deliberately **operator-triggered only** - no cron, no automatic redrive-on-dead-letter. `scripts/dlq-inspect.sh` / `scripts/dlq-redrive.sh` wrap it. Both run the already-built tool in a throwaway container attached to the compose stack's internal `backend` network rather than on the bare host: Kafka's advertised listener for that network is the hostname `kafka`, which only resolves via Docker's embedded DNS for containers actually on that network (`backend` is `internal: true`, so the build/restore has to happen on the host first, and only the compiled DLL runs in the container).

## What didn't work

**A dropped pair of quotes turned every message's key into an empty string.** The very first hand-produced test message showed up in the DLQ with `originalKey=""` instead of the SKU it was keyed by. `kafka-console-producer.sh --property key.separator=$(printf "\t")` - unquoted - lets bash's default word-splitting (IFS includes tab) eat the lone tab character the command substitution produces, so the actual argument received was `key.separator=` with nothing after the `=`. The existing `kafka-partition-resize-sku-test.sh` in this repo does this correctly (`--property key.separator="$(printf '\t')"`, quoted) - the bug was in a hand-typed ad hoc command that dropped those quotes, not in the repo's own scripts.

**A batch redrive crashed with `FormatException: The input is not a valid Base-64 string`.** Investigating showed the actual root cause was an incorrect assumption: every `*DeadLetterPublisher` in this repo does *not* agree on the wire format. The three Orders.Worker publishers and Payments.Service's both base64-encode `consumeResult.Message.Value` because their consumers are `ConsumeResult<string, byte[]>` (some of those topics carry Avro). `InventoryDeadLetterPublisher` consumes plain `ConsumeResult<string, string>` and stores the raw JSON text directly - no base64 involved at all. Fixed by trying base64 first and falling back to raw UTF-8 bytes rather than assuming one convention holds everywhere, and by wrapping each message's redrive in its own try/catch so one malformed envelope can't sink an entire batch.

**The redrive-count cap didn't cap anything - a genuine, live, unbounded loop.** The very first full redrive run against a live DLQ never finished; after being killed, every entry in the topic showed `redriveCount=0`, even ones that had visibly been redriven multiple times. Root cause: the `redrive-count` header is added by the tool when it republishes to the *original* topic, but if that message fails again and gets dead-lettered a second time, only `correlation-id`/`traceparent`/`tracestate` were being copied forward into the *new* dead-letter envelope's headers - `redrive-count` was silently dropped every time, so the cap check always saw a fresh `0`. Fixed by adding one more `CopyHeader(..., MessagingHeaders.RedriveCount)` line to all five dead-letter publishers. **Deploying the fix took real work, not just a commit**: the code fix built and passed locally, but the *running* K3s pods were still serving the old image - this repo's local overlay retags every image to a fixed `:local` tag with `imagePullPolicy: Never` (`kubernetes/overlays/local/kustomization.yaml`, renamed from `:rename-v1` post-Milestone-65), so the fix had no effect until `inventory-service`/`orders-worker`/`payments-service` were rebuilt from source, re-imported into K3s's containerd store, and rolled out. Argo CD stayed `Synced`/`Healthy` throughout, since the restart didn't touch the tracked manifest, only which content `:local` currently points to.

**Verbatim redrive can never fix the one thing that actually reaches this system's DLQs.** `InventoryReservationMessageProcessor` treats "insufficient stock" and "unknown SKU" as normal, non-exceptional outcomes - a declined `InventoryReservationReplied` event, not a dead letter. Combined with infrastructure faults being retried forever rather than ever dead-lettering (see `ReservationRequestedConsumer.ProcessWithRetriesAsync`'s special-cased `NpgsqlException` branch), the *only* thing that actually dead-letters is a payload validation failure (`InvalidReservationMessageException` - empty GUIDs, blank SKU, non-positive quantity). That failure mode is 100% deterministic on the payload alone: redriving the exact same bytes fails the exact same way, every time, forever. This is a real, honest limitation, not a workaround-in-waiting - it's exactly *why* the tool republishes verbatim rather than trying to be clever, and exactly why redrive here is only useful after a human or a separate tool has actually repaired the payload first.

## Results

Live proof against a real SKU (`SKU-M62-DEMO`, seeded with 5 units), after the fix and redeploy:

1. Reservation A (3 units) → succeeds → `available=2`.
2. Reservation B (2 units, deliberately invalid `ReservationId=00000000-...`) → exhausts 3 attempts, dead-letters. `available` unchanged at 2 - the poison message never touched inventory state.
3. Reservation C (2 units) → succeeds → `available=0`.
4. `dlq-redrive.sh inventory.reservation.dlq.v1 --key-filter SKU-M62-DEMO`, one run:

   ```
   REDRIVEN (attempt 1): originalTopic=inventory.reservation-requested.v1 key=SKU-M62-DEMO
   REDRIVEN (attempt 2): originalTopic=inventory.reservation-requested.v1 key=SKU-M62-DEMO
   REDRIVEN (attempt 3): originalTopic=inventory.reservation-requested.v1 key=SKU-M62-DEMO
   SKIP (redriveCount=3 >= max=3): originalTopic=inventory.reservation-requested.v1 key=SKU-M62-DEMO
   == inventory.reservation.dlq.v1: read=428 redriven=3 skippedOverCap=1 errored=0 filteredOut=424 dryRun=False ==
   ```

   Three real redrive-fail-dead-letter round trips in one run, then correctly capped and skipped on the fourth - `redriveCount` now genuinely survives across dead-letter cycles, and the loop genuinely stops. (`filteredOut=424` is the accumulated backlog from the runaway-loop incident above, left untouched by `--key-filter`.) `available` stayed at 0 throughout - B never succeeded, exactly as the validation-failure analysis predicted.

5. **Idempotency**: resubmitting reservation A's exact original bytes (simulating a redrive of an *already-processed* message) produced `EventId 9005: "Skipped duplicate reservation ... for consumer inventory-service"` in the logs, with `available`/`reserved` unchanged - the Inbox (Milestone 23) absorbed it with zero side effect.

Full solution (9 test projects) still builds and passes after the header-copy fix across all five publishers.

## Running it

```bash
# Peek at any of the five DLQ topics, safely, repeatably
bash scripts/dlq-inspect.sh inventory.reservation.dlq.v1

# Redrive everything currently sitting there (respects the redrive-count cap)
bash scripts/dlq-redrive.sh inventory.reservation.dlq.v1

# Or scope to one message among many
bash scripts/dlq-redrive.sh inventory.reservation.dlq.v1 --key-filter SKU-123

# Preview without producing or committing anything
bash scripts/dlq-redrive.sh inventory.reservation.dlq.v1 --dry-run
```
