#!/usr/bin/env bash
# Milestone 49 live proof: Catalog.Service's MongoDB (compose/compose.yaml's
# `mongodb` service) runs standalone - no replication, no write concern
# choice, no read preference, no failover. This runs the real cost/benefit
# against an isolated 3-node replica set (mongo-rs1/2/3, NOT the live
# standalone `mongodb` Catalog.Service depends on):
#
#   - w:1 vs w:majority write latency, measured, not assumed
#   - a deliberately-lagging secondary (mongo-rs3, secondaryDelaySecs=5)
#     proves the classic secondaryPreferred stale-read hazard
#     deterministically, rather than racing real replication timing
#
# Usage: mongo-replica-set-test.sh [doc_count]
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"
doc_count=${1:-200}
run_id="run-$(date +%s)"

cd "$compose_directory"

echo "== Bringing up the isolated mongo-replicaset-demo cluster (mongo-rs1/2/3) =="
docker compose --profile mongo-replicaset-demo up -d --wait mongo-rs1 mongo-rs2 mongo-rs3 mongo-rs-init

echo "== Waiting for a primary to be elected =="
for i in $(seq 1 30); do
  primary=$(docker compose exec -T mongo-rs1 mongosh --quiet --eval \
    'db.hello().isWritablePrimary ? "mongo-rs1" : (function(){ var s = rs.status(); var p = s.members.find(m => m.state === 1); return p ? p.name : ""; })()' 2>/dev/null || true)
  if [[ -n "$primary" ]]; then
    echo "Primary: ${primary}"
    break
  fi
  sleep 1
done

echo "== Measuring write latency: w:1 vs w:majority (${doc_count} inserts each) =="
w1_ms=$(docker compose exec -T mongo-rs1 mongosh --quiet --host "rs-demo/mongo-rs1,mongo-rs2,mongo-rs3" --eval "
  var start = Date.now();
  for (var i = 0; i < ${doc_count}; i++) {
    db.getSiblingDB('catalog_rs_demo').latency_test.insertOne({run: '${run_id}', mode: 'w1', i: i}, {writeConcern: {w: 1}});
  }
  print(Date.now() - start);
" 2>/dev/null | tail -1)

wmajority_ms=$(docker compose exec -T mongo-rs1 mongosh --quiet --host "rs-demo/mongo-rs1,mongo-rs2,mongo-rs3" --eval "
  var start = Date.now();
  for (var i = 0; i < ${doc_count}; i++) {
    db.getSiblingDB('catalog_rs_demo').latency_test.insertOne({run: '${run_id}', mode: 'wmajority', i: i}, {writeConcern: {w: 'majority'}});
  }
  print(Date.now() - start);
" 2>/dev/null | tail -1)

w1_avg=$(echo "scale=2; $w1_ms / $doc_count" | bc)
wmajority_avg=$(echo "scale=2; $wmajority_ms / $doc_count" | bc)

echo "w:1 total=${w1_ms}ms avg=${w1_avg}ms/doc"
echo "w:majority total=${wmajority_ms}ms avg=${wmajority_avg}ms/doc"

echo ""
echo "== Stale-read proof: write a marker doc (w:1), read it from the deliberately-lagging secondary (mongo-rs3, secondaryDelaySecs=5) =="
docker compose exec -T mongo-rs1 mongosh --quiet --eval "
  db.getSiblingDB('catalog_rs_demo').marker.insertOne({run: '${run_id}', marker: true, writtenAt: new Date()}, {writeConcern: {w: 1}});
  print('marker written to primary');
" 2>/dev/null

immediate_result=$(docker compose exec -T mongo-rs3 mongosh --quiet --eval "
  db.getMongo().setReadPref('secondary');
  var doc = db.getSiblingDB('catalog_rs_demo').marker.findOne({run: '${run_id}'});
  print(doc ? 'FOUND' : 'NOT_FOUND');
" 2>/dev/null | tail -1)

echo "Immediate read from lagging secondary (mongo-rs3): ${immediate_result}"

echo "Waiting 6s for replication to catch up past the 5s delay..."
sleep 6

caught_up_result=$(docker compose exec -T mongo-rs3 mongosh --quiet --eval "
  db.getMongo().setReadPref('secondary');
  var doc = db.getSiblingDB('catalog_rs_demo').marker.findOne({run: '${run_id}'});
  print(doc ? 'FOUND' : 'NOT_FOUND');
" 2>/dev/null | tail -1)

echo "Read from the same secondary after 6s: ${caught_up_result}"

echo ""
echo "=== RESULT ==="
echo "w:1 avg latency:         ${w1_avg}ms/doc"
echo "w:majority avg latency:  ${wmajority_avg}ms/doc"
echo "immediate secondary read: ${immediate_result}"
echo "post-delay secondary read: ${caught_up_result}"
if [[ "$immediate_result" == "NOT_FOUND" && "$caught_up_result" == "FOUND" ]]; then
  echo "==> STALE READ CONFIRMED: a secondaryPreferred/secondary read missed a write acknowledged moments earlier by the primary, then became consistent once replication caught up."
else
  echo "==> unexpected result this run - see docs/architecture/milestone-49-mongodb-replica-set.md."
fi
