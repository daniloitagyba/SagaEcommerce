# Production Operations Runbooks

These procedures define the first safe response for page-worthy SagaEcommerce
alerts. The repository owner is the default incident commander for the lab.
Any action that deletes data, volumes or Kubernetes resources, changes network
or host configuration, or performs a production redrive requires explicit
human approval. Evidence belongs in the incident/task record before mutation.

## Common first response

1. Record alert name, UTC start time, environment, deployment revision and the
   correlation/message/order identifiers shown by logs or traces.
2. Check whether a deployment or migration started in the preceding 30 minutes.
3. Preserve logs and relevant metrics before restarting anything.
4. Prefer stopping promotion or producers over deleting queued work.
5. Stop if the target environment, message set or rollback compatibility cannot
   be established unambiguously.

## Dead letter or outbox dead letter

**Alerts:** `DeadLetterMessagesDetected`, `OutboxRowDeadLettered`.

**Authority:** an operator may inspect. Redrive requires approval from the owner
of the affected order/payment/inventory flow.

1. Inspect with `scripts/ops/dlq-inspect.sh`; do not print payload credentials.
2. Classify the failure as transient, poison payload, incompatible contract or
   product bug. Deploy the product/contract fix before replaying a poison item.
3. Run `scripts/ops/dlq-redrive.sh` in dry-run/inspection mode first and record
   exact topic, partition, offset/message ID and expected idempotency mechanism.
4. Redrive a bounded batch and verify inbox deduplication, aggregate state and
   DLQ/outbox backlog before continuing.

**Stop conditions:** missing durable message ID, unknown consumer contract,
non-idempotent side effect or increasing DLQ depth.

## Stuck or failed saga compensation

**Alerts:** `OrphanedSagaRepliesHigh`, `AntiEntropyDivergenceDetected`, saga
timeout/compensation panels in `saga-overview.json`.

1. Locate the saga row, order status and correlated Inventory/Payments messages.
2. Determine whether the saga is active, timed out, parked for backorder or
   terminal. Do not replay a reply against a terminal state without confirming
   the compare-and-swap guard that will reject or deduplicate it.
3. Compare Orders, Payments and Inventory facts using the anti-entropy endpoints.
4. If compensation failed, repair the dependency first, then replay only the
   failed command through the DLQ procedure above.

**Stop conditions:** facts disagree and ownership is unclear, a payment may be
captured twice, or a stock release could create negative/available drift.

## Kafka or broker outage

**Alerts:** `KafkaConsumerGroupLagHigh`, `OutboxBacklogGrowing`.

1. Verify broker quorum/ISR and client reachability; do not increase retries
   while the broker is unavailable.
2. Confirm APIs continue committing local outbox rows and that backlog retention
   has sufficient headroom.
3. Restore broker health, then watch lag, outbox age and DLQ rate while consumers
   drain. Apply backpressure to producers if drain time grows.
4. Run the Kafka quorum proof only in the lab, never against a production topic.

**Stop conditions:** ISR below `min.insync.replicas`, disk saturation, retention
window at risk or replay causing repeated side effects.

## Failed database migration

1. Stop environment promotion. Capture migration name, application digest and
   database schema version.
2. If the application has not switched to the new shape, roll back the
   application digest and leave additive schema in place.
3. For expand/contract changes, complete or reverse only the documented phase;
   never drop the old column/table while the old application may still run.
4. Restore a backup only after the restore runbook below establishes target,
   recovery point and data-loss window.

**Stop conditions:** destructive `Down` migration, unknown partially applied DDL
or a rollback that would run code incompatible with the current schema.

## Backup and restore

**Authority:** restore into an isolated target may be automated. Replacing a live
database requires explicit approval and an announced write freeze.

1. Identify data owner, backup identifier, checksum, creation time and required
   recovery point.
2. Restore into an isolated database/container using the scripts under
   `scripts/backup-drills/`.
3. Verify counts, marker/checksum and application-level reads. Record measured
   RPO and RTO in the workflow evidence artifact.
4. For a live cutover, freeze writes, capture a final backup, switch connection
   configuration declaratively and retain the previous database for rollback.

**Stop conditions:** backup checksum/marker mismatch, wrong environment, restore
would overwrite a live target, or measured data loss exceeds the approved RPO.

## Deployment rollback

1. Stop promotion and record the failing image digest plus Git revision.
2. Confirm database and contract compatibility with the previous signed digest.
3. Revert the environment manifest through a reviewed Git change. Do not rebuild
   or retag the previous release.
4. Let Argo CD reconcile, run the staging/production smoke checks and verify SLO,
   saga backlog and business invariants before resolving the incident.

**Stop conditions:** previous digest is unsigned/unscanned, schema is not
backward compatible, or rollback increases payment/stock divergence.
