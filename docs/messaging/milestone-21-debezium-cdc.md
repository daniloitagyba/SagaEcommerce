# Milestone 21 CDC with Debezium

## Scope

The transactional outbox publisher built in earlier milestones is poll-based: every `orders-api` replica independently queries `outbox_messages` on a timer, whether or not there's anything new to publish. Change data capture is the alternative architecture - read the database's own write-ahead log and stream changes as they commit, with no polling query against the table at all. This milestone deploys Debezium alongside the existing poller (not replacing it - a separate topic, so nothing in the live consumer path changes) and measures, rather than assumes, how the two compare on publish latency, database load, and behavior when Kafka is unavailable.

## Design

- **Debezium's own Kafka Connect distribution** (`debezium/connect:2.7.3.Final`), not vanilla Kafka Connect with a separately-installed plugin - it bundles the Postgres connector, keeping the Compose service to one image instead of two.
- **`wal_level=logical`** is required for Postgres's logical replication protocol (`pgoutput`, the plugin Debezium uses); the default `apache/kafka`-adjacent `postgres:17-alpine` image ships with `wal_level=replica`. Changing it requires a full Postgres restart, not just a config reload - done deliberately, with the smoke test run immediately after to confirm the live system recovered cleanly.
- **The connector watches `outbox_messages`, not the whole database**, and publishes raw row-change events (`table.include.list: public.outbox_messages`) to `orders-cdc.public.outbox_messages` - a topic nothing in the existing consumer graph subscribes to. Debezium's purpose-built `EventRouter` SMT (which reshapes outbox rows into per-aggregate-type topics matching what a real replacement would need) is a natural next step, deliberately out of scope here: the milestone's question is "how does CDC latency and load compare to polling," which raw change capture already answers without adding SMT-configuration risk to a lab that already has working consumers on `orders.created.v1`.
- **`snapshot.mode: no_data`**: skip an initial full-table snapshot and start streaming from the current WAL position - the comparison only needs newly-created rows, not the 191k+ historical ones from Milestone 20's backfill.
- **A dedicated Postgres `pg_hba.conf` rule for replication connections**, added via `scripts/postgres-allow-replication.sh` rather than editing the file once by hand: the default image's `pg_hba.conf` allows replication connections only from loopback, and the one wildcard rule that *does* cover the Docker network (`host all all all scram-sha-256`) doesn't apply to replication connections at all - Postgres's `pg_hba.conf` treats the `replication` pseudo-database as distinct from `all`, deliberately, since granting REPLICATION is a meaningfully bigger privilege than granting SELECT.

## What didn't work

**The `.env` password had drifted from what PostgreSQL actually has.** Debezium's connector registration failed with `password authentication failed`, and PostgreSQL's own default `pg_hba.conf` gap (above) looked like the obvious cause - it wasn't, or at least wasn't the only one. `compose/.env`'s `POSTGRES_PASSWORD` no longer matched the live database's actual role password (confirmed by decoding the Milestone 17 sealed secret, which still held the real value from when it was sealed). `.env` is gitignored by design, so this drift wasn't visible in any diff; it's exactly the kind of local, undocumented state a fresh clone of this repo would never reproduce, caught only because Debezium needed to authenticate directly rather than going through the app's already-correct sealed-secret-sourced connection string. Fixed by correcting `.env` to match the database's actual credential.

**Toxiproxy didn't actually block Debezium's Kafka traffic on the first attempt - not a bug, a real property of how Kafka clients work.** Debezium was configured to bootstrap through `toxiproxy:19092` specifically so this milestone's own Kafka-outage test wouldn't also affect the K8s-facing traffic Milestone 10's `resilience-chaos.sh` uses the same proxy for. Disabling the proxy and creating an order produced a CDC event anyway - the connector kept working. The reason: Kafka clients use the bootstrap address only for the *initial* metadata fetch; every subsequent produce/fetch goes to whatever address the broker *advertises itself as* (`KAFKA_ADVERTISED_LISTENERS: PLAINTEXT://kafka:9092`), which Debezium had already cached from before the proxy was disabled. Confirmed by restarting the Debezium container while the proxy stayed disabled, forcing a genuinely fresh bootstrap attempt: it stuck in `starting` for 100+ seconds, unable to connect at all - proof the block is real, just not retroactive against an already-established connection. Re-enabling the proxy brought it to `healthy` within ~9 seconds, and every order created during the whole experiment (including the one created while the connection was silently bypassing the fault) was eventually captured with zero loss - Postgres's replication slot preserves WAL position regardless of how long a consumer is disconnected, which is the actual safety property CDC provides here, independent of this particular fault-injection wrinkle.

**CDC was not faster than the poller - the opposite of the naive expectation, with a real explanation once measured.** Two paired measurements, same order, both paths timestamped from the same Postgres commit: the poller published in 104ms and 130ms; Debezium's own `ts_ms - source.ts_ms` for the matching change event was 410ms and 215ms. The poller isn't actually interval-throttled the way its 500ms `PollIntervalMilliseconds` setting suggests - `OutboxPublisher.ExecuteAsync` only sleeps when a batch finds *zero* rows; under any real traffic it loops essentially continuously, and with three `orders-api` replicas each running their own poller, the effective latency for any given row is bounded by whichever replica's poller happens to catch it soonest, not the nominal interval. Debezium's per-message overhead - Kafka Connect's internal offset-commit cycle, full before/after row JSON serialization - is a real, measurable cost that a naive "CDC eliminates polling delay" story doesn't account for. CDC's actual advantage here is elsewhere.

## Results

### Publish latency, poller vs CDC (same order, both paths measured from the same Postgres commit)

| Sample | Poller latency (`processed_at - occurred_at`) | CDC latency (`payload.ts_ms - payload.source.ts_ms`) |
| --- | --- | --- |
| 1 | 104 ms | 410 ms |
| 2 | 130 ms | 215 ms |

### Database load, idle period (zero new orders, 10 seconds)

| Metric | Result |
| --- | --- |
| `outbox_messages` index scans | 60 over 10s = **6/sec**, purely from polling overhead - matches 3 `orders-api` replicas x their independent ~2/sec idle poll rate exactly |
| CDC's contribution to that same count | 0 - Debezium reads the WAL stream, never queries the table |

### Kafka outage behavior

| Step | Result |
| --- | --- |
| Disable Toxiproxy, create an order | CDC event still produced - already-open connection bypassed the fault (see above) |
| Restart Debezium with the proxy still disabled | Stuck in `starting` for 100+ seconds - confirms the block is real for a fresh connection |
| Re-enable Toxiproxy | `healthy` within ~9 seconds |
| Every order created during the entire experiment | All eventually captured on the CDC topic - zero data loss |

### Regression check

`k3s-smoke-test.sh` passed cleanly after the `wal_level` change (Postgres restart) and every subsequent experiment.

## Running the experiment

```bash
docker compose up -d postgres            # picks up wal_level=logical (restarts Postgres)
scripts/postgres-allow-replication.sh
docker compose up -d cdc-topics-init debezium  # cdc-topics-init creates the CDC topics; kafka-init comes up as its dependency
scripts/debezium-register-connector.sh

# Compare latency for a fresh order:
docker compose exec postgres psql -U orders -d orders -c \
  "SELECT occurred_at, processed_at FROM outbox_messages WHERE id = '<event-id>';"
docker compose exec kafka /opt/kafka/bin/kafka-console-consumer.sh \
  --bootstrap-server kafka:9092 --topic orders-cdc.public.outbox_messages --from-beginning
```
