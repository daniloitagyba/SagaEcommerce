#!/usr/bin/env bash
# Milestone 53 live proof: orders-api runs 2 replicas and Milestone 27's
# postgres-ha CNPG cluster has 2 read replicas that have sat unused since -
# every read in this codebase, live or in that cluster, goes to the
# primary. This proves what routing GETs to a replica would actually cost:
# a classic read-your-writes violation, made deterministic by pausing WAL
# replay on one replica (pg_wal_replay_pause/resume - a native Postgres
# mechanism, not a chaos tool) rather than racing real (sub-millisecond,
# same-host) replication lag. Then proves the fix: gate the read on the
# replica's replay LSN actually reaching the primary's LSN at write time,
# rather than reading immediately.
#
# Runs against the isolated postgres-ha cluster from Milestone 27 (NOT the
# live orders/payments Postgres) - same reasoning as every other isolated
# demo cluster this lab has built for a similarly disruptive experiment.
set -euo pipefail

namespace="postgres-ha"
cluster="orders-ha"
db="orders_ha_demo"
appuser="orders_ha_demo"
apppassword=$(kubectl get secret "${cluster}-app" -n "$namespace" -o jsonpath='{.data.password}' | base64 -d)

primary=$(kubectl get cluster "$cluster" -n "$namespace" -o jsonpath='{.status.currentPrimary}')
replica=""
for pod in $(kubectl get pods -n "$namespace" -l "cnpg.io/cluster=${cluster}" -o jsonpath='{.items[*].metadata.name}'); do
  if [[ "$pod" != "$primary" ]]; then
    replica="$pod"
    break
  fi
done
echo "Primary: ${primary}; target replica: ${replica}"

psql_rw() {
  kubectl exec -n "$namespace" "$primary" -c postgres -- env PGPASSWORD="${apppassword}" \
    psql -h "${cluster}-rw" -U "$appuser" -d "$db" -t -c "$1" 2>/dev/null
}

psql_replica_super() {
  kubectl exec -n "$namespace" "$replica" -c postgres -- psql -U postgres -d "$db" -t -c "$1" 2>/dev/null
}

echo "== Ensuring the demo table exists (via -rw, the primary) =="
psql_rw "CREATE TABLE IF NOT EXISTS read_your_writes_demo (id serial PRIMARY KEY, value text);" >/dev/null

marker="marker-$(date +%s)"
echo "== Writing '${marker}' via -rw (the primary) =="
psql_rw "INSERT INTO read_your_writes_demo (value) VALUES ('${marker}');" >/dev/null
primary_lsn=$(psql_rw "SELECT pg_current_wal_lsn();" | tr -d ' ')
echo "Primary WAL LSN at write time: ${primary_lsn}"

echo ""
echo "== Pausing WAL replay on the replica (pg_wal_replay_pause - deterministic lag, not a race) =="
kubectl exec -n "$namespace" "$replica" -c postgres -- psql -U postgres -c "SELECT pg_wal_replay_pause();" >/dev/null

second_marker="marker2-$(date +%s)"
psql_rw "INSERT INTO read_your_writes_demo (value) VALUES ('${second_marker}');" >/dev/null
second_lsn=$(psql_rw "SELECT pg_current_wal_lsn();" | tr -d ' ')

echo "== Reading directly from the PAUSED replica for '${second_marker}' (secondaryPreferred-style read, immediately after the write) =="
naive_read=$(psql_replica_super "SELECT count(*) FROM read_your_writes_demo WHERE value = '${second_marker}';" | tr -d ' ')
echo "Row found on paused replica: ${naive_read} (expected 0 - stale)"

echo ""
echo "== The fix: gate the read on the replica's replay LSN reaching the write's LSN, not on reading immediately =="
echo "Resuming WAL replay..."
kubectl exec -n "$namespace" "$replica" -c postgres -- psql -U postgres -c "SELECT pg_wal_replay_resume();" >/dev/null

echo "Polling pg_last_wal_replay_lsn() until it reaches ${second_lsn}..."
caught_up="false"
for i in $(seq 1 30); do
  replica_lsn=$(kubectl exec -n "$namespace" "$replica" -c postgres -- psql -U postgres -t -c "SELECT pg_last_wal_replay_lsn();" 2>/dev/null | tr -d ' ')
  is_caught_up=$(kubectl exec -n "$namespace" "$replica" -c postgres -- psql -U postgres -t -c \
    "SELECT pg_wal_lsn_diff('${replica_lsn}', '${second_lsn}') >= 0;" 2>/dev/null | tr -d ' ')
  if [[ "$is_caught_up" == "t" ]]; then
    caught_up="true"
    echo "Replica caught up (replay_lsn=${replica_lsn} >= write_lsn=${second_lsn}) after $((i))s"
    break
  fi
  sleep 1
done

gated_read=$(psql_replica_super "SELECT count(*) FROM read_your_writes_demo WHERE value = '${second_marker}';" | tr -d ' ')
echo "Row found on replica after LSN-gated wait: ${gated_read} (expected 1 - now consistent)"

echo ""
echo "=== RESULT ==="
echo "immediate read on paused replica: ${naive_read} (expected 0 - stale, classic read-your-writes violation)"
echo "LSN-gated read after replay caught up: ${gated_read} (expected 1 - correct)"
if [[ "$naive_read" == "0" && "$gated_read" == "1" ]]; then
  echo "==> READ-YOUR-WRITES VIOLATION CONFIRMED, AND FIXED: an immediate secondary read missed a write the primary already committed; gating the read on the replica's WAL replay position (an LSN token, not a fixed sleep) made it correct."
fi

echo ""
echo "== Cleaning up demo table =="
psql_rw "DROP TABLE IF EXISTS read_your_writes_demo;" >/dev/null
