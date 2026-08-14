#!/usr/bin/env bash
# Live proof for Milestone 41: same-SKU oversell prevention and
# different-SKU parallelism, both driven purely by Kafka partition
# ownership - Inventory.Service never takes a database row lock.
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"
namespace=${KUBERNETES_NAMESPACE:-orders-lab}

low_stock_sku="SKU-ELEC-002"
low_stock_available=8
low_stock_requests=30

echo "== Phase 1: hammering ${low_stock_sku} (available=${low_stock_available}) with ${low_stock_requests} concurrent-looking reservation requests =="

payload_file=$(mktemp)
trap 'rm -f "$payload_file"' EXIT

for i in $(seq 1 "$low_stock_requests"); do
  reservation_id=$(python3 -c "import uuid; print(uuid.uuid4())")
  order_id=$(python3 -c "import uuid; print(uuid.uuid4())")
  now=$(python3 -c "import datetime; print(datetime.datetime.now(datetime.timezone.utc).isoformat())")
  json=$(python3 -c "
import json
print(json.dumps({
  'reservationId': '$reservation_id',
  'orderId': '$order_id',
  'sku': '$low_stock_sku',
  'quantity': 1,
  'correlationId': 'concurrency-test-$i',
  'requestedAt': '$now'
}))
")
  printf '%s\t%s\n' "$low_stock_sku" "$json" >> "$payload_file"
done

cd "$compose_directory"
docker compose exec -T kafka /opt/kafka/bin/kafka-console-producer.sh \
  --bootstrap-server kafka:9092 \
  --topic inventory.reservation-requested.v1 \
  --property parse.key=true \
  --property key.separator="$(printf '\t')" \
  < "$payload_file"

echo "Produced ${low_stock_requests} requests. Waiting for Inventory.Service to process them..."
sleep 8

echo "== Final ${low_stock_sku} state =="
inventory_ip=$(kubectl get svc inventory-service -n "$namespace" -o jsonpath="{.spec.clusterIP}")
curl -s "http://${inventory_ip}/inventory/${low_stock_sku}" | python3 -m json.tool

echo "== Phase 2: a different SKU (SKU-BOOK-001, plenty of stock) should be free to land on a different pod =="

other_sku="SKU-BOOK-001"
other_requests=10
other_payload_file=$(mktemp)

for i in $(seq 1 "$other_requests"); do
  reservation_id=$(python3 -c "import uuid; print(uuid.uuid4())")
  order_id=$(python3 -c "import uuid; print(uuid.uuid4())")
  now=$(python3 -c "import datetime; print(datetime.datetime.now(datetime.timezone.utc).isoformat())")
  json=$(python3 -c "
import json
print(json.dumps({
  'reservationId': '$reservation_id',
  'orderId': '$order_id',
  'sku': '$other_sku',
  'quantity': 1,
  'correlationId': 'parallelism-test-$i',
  'requestedAt': '$now'
}))
")
  printf '%s\t%s\n' "$other_sku" "$json" >> "$other_payload_file"
done

docker compose exec -T kafka /opt/kafka/bin/kafka-console-producer.sh \
  --bootstrap-server kafka:9092 \
  --topic inventory.reservation-requested.v1 \
  --property parse.key=true \
  --property key.separator="$(printf '\t')" \
  < "$other_payload_file"

sleep 8

echo "== Which pod(s) decided which reservations (proves partition ownership AND parallelism) =="
for pod in $(kubectl get pods -n "$namespace" -l app.kubernetes.io/name=inventory-service -o jsonpath="{.items[*].metadata.name}"); do
  low_stock_count=$(kubectl logs "$pod" -n "$namespace" -c inventory-service --tail=1000 | grep -c "\"Sku\":\"${low_stock_sku}\"" || true)
  other_count=$(kubectl logs "$pod" -n "$namespace" -c inventory-service --tail=1000 | grep -c "\"Sku\":\"${other_sku}\"" || true)
  echo "$pod: ${low_stock_sku} mentions=$low_stock_count, ${other_sku} mentions=$other_count"
done
