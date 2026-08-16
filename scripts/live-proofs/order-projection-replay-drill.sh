#!/usr/bin/env bash
# Milestone 55 live proof: the CQRS read model (order_summaries) has never
# actually been rebuilt from the event log since Milestone 13 built it -
# only measured for lag, never actually replayed from zero. Full scope
# note: orders.created.v1/payments.result.v1 both retain messages for only
# 24h (compose.yaml's kafka-init, retention.ms=86400000) - by the time
# this drill ran, BOTH topics were completely empty (earliest == latest
# == 0 on every partition), meaning the accumulated 150k+ historical rows
# in order_summaries could never be rebuilt from Kafka at all anymore.
# That's a real, important finding on its own (see the milestone doc) and
# it's why this drill creates fresh orders to replay, rather than
# truncating the live, irreplaceable projection table.
#
# Real replay of the FULL history would need Milestone 23's event store
# (an append-only Postgres table, not subject to Kafka's retention), not
# this topic - out of scope for this drill, noted as a follow-up.
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
compose_directory="$project_directory/compose"
namespace=${KUBERNETES_NAMESPACE:-orders-lab}
order_count=${1:-20}

cd "$compose_directory"

access_token=$("$script_directory/../infra/keycloak-get-token.sh")
orders_ip=$(kubectl get svc orders-api -n "$namespace" -o jsonpath='{.spec.clusterIP}')

echo "== Creating ${order_count} fresh test orders =="
order_ids_file=$(mktemp)
trap 'rm -f "$order_ids_file"' EXIT
for i in $(seq 1 "$order_count"); do
  response=$(curl -s -X POST "http://${orders_ip}/orders" \
    -H "Authorization: Bearer ${access_token}" \
    -H "Content-Type: application/json" \
    -d "{\"customerId\":\"replay-drill-${i}\",\"amount\":$((10 + i)).50,\"currency\":\"BRL\"}")
  order_id=$(echo "$response" | python3 -c "import sys,json; print(json.load(sys.stdin)['id'])" 2>/dev/null || true)
  if [[ -n "$order_id" ]]; then
    echo "$order_id" >> "$order_ids_file"
  fi
done
created=$(wc -l < "$order_ids_file" | tr -d ' ')
echo "Created ${created}/${order_count} orders."

echo "Waiting for the projector to catch up on the fresh orders..."
sleep 5

id_list=$(awk '{printf "%s%s", (NR>1?",":""), "\x27" $0 "\x27"}' "$order_ids_file")

pre_count=$(docker compose exec -T postgres psql -U orders -d orders -t -c "SELECT count(*) FROM order_summaries;" | tr -d ' ')
pre_snapshot=$(docker compose exec -T postgres psql -U orders -d orders -t -c \
  "SELECT order_id, customer_id, amount, currency, status FROM order_summaries WHERE order_id IN (${id_list}) ORDER BY order_id;")
echo "== Pre-drill: ${pre_count} total rows; ${created} test rows present =="
echo "$pre_snapshot"

echo ""
echo "== Deleting ONLY the ${created} test rows just created (never touching the other $((pre_count - created)) real rows) =="
docker compose exec -T postgres psql -U orders -d orders -c \
  "DELETE FROM order_summaries WHERE order_id IN (${id_list});"

deleted_count=$(docker compose exec -T postgres psql -U orders -d orders -t -c "SELECT count(*) FROM order_summaries;" | tr -d ' ')
echo "Row count after deletion: ${deleted_count} (expected $((pre_count - created)))"

echo ""
echo "== Scaling orders-worker to 0 to safely reset the orders-projector consumer group's offsets =="
kubectl scale deployment/orders-worker -n "$namespace" --replicas=0
kubectl wait --for=delete pod -l app.kubernetes.io/name=orders-worker -n "$namespace" --timeout=60s || true

echo "Waiting for Kafka to consider the consumer group actually Empty (pod deletion alone isn't enough - the group coordinator keeps it Stable until the session/rebalance timeout elapses)..."
for i in $(seq 1 60); do
  group_state=$(docker compose exec -T kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server kafka:9092 \
    --describe --group orders-projector --state 2>/dev/null | grep -oE 'Stable|Empty|Dead|PreparingRebalance|CompletingRebalance' | tail -1)
  echo "  group state: ${group_state:-<none>}"
  if [[ "$group_state" == "Empty" || -z "$group_state" ]]; then
    break
  fi
  sleep 2
done

echo "== Resetting orders-projector's offsets to earliest on both source topics =="
docker compose exec -T kafka /opt/kafka/bin/kafka-consumer-groups.sh --bootstrap-server kafka:9092 \
  --group orders-projector --reset-offsets --to-earliest --execute \
  --topic orders.created.v1 --topic payments.result.v1

echo ""
echo "== Scaling orders-worker back to 1 - the projector will now replay from offset 0 =="
replay_start=$(date +%s.%N)
kubectl scale deployment/orders-worker -n "$namespace" --replicas=1
kubectl wait --for=condition=Available deployment/orders-worker -n "$namespace" --timeout=60s

echo "Waiting for the projection to fully catch back up..."
post_count=0
for i in $(seq 1 60); do
  post_count=$(docker compose exec -T postgres psql -U orders -d orders -t -c "SELECT count(*) FROM order_summaries;" 2>/dev/null | tr -d ' ')
  if [[ "$post_count" == "$pre_count" ]]; then
    break
  fi
  sleep 1
done
replay_end=$(date +%s.%N)
replay_seconds=$(echo "$replay_end - $replay_start" | bc)

post_snapshot=$(docker compose exec -T postgres psql -U orders -d orders -t -c \
  "SELECT order_id, customer_id, amount, currency, status FROM order_summaries WHERE order_id IN (${id_list}) ORDER BY order_id;")

echo ""
echo "=== RESULT ==="
echo "Reconstruction time (orders-worker back up to row count restored): ${replay_seconds}s"
echo "Row count: pre-drill=${pre_count}, after deleting test rows=${deleted_count}, post-replay=${post_count}"
if [[ "$pre_snapshot" == "$post_snapshot" ]]; then
  echo "==> CONVERGENCE CONFIRMED: the ${created} deliberately-deleted rows are back, byte-for-byte identical to before deletion."
else
  echo "==> Snapshots differ - see docs/cqrs/milestone-55-replay-reconstruction.md."
  echo "--- pre ---"; echo "$pre_snapshot"
  echo "--- post ---"; echo "$post_snapshot"
fi
if [[ "$post_count" == "$pre_count" ]]; then
  echo "==> NO DUPLICATION: replaying the whole retained topic re-upserted every row idempotently - row count unchanged."
fi
