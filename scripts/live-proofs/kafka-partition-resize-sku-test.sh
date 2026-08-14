#!/usr/bin/env bash
# Milestone 51 live proof: Milestone 41's per-SKU mutual exclusion in
# Inventory.Service relies entirely on Kafka partition ownership - the
# same SKU always hashes to the same partition, so only one consumer
# instance ever owns it at a time. That guarantee has a hole nobody had
# demonstrated: increasing a topic's partition count changes the
# key-to-partition mapping (Kafka's default partitioner is
# murmur2(key) % num_partitions) for every key, RETROACTIVELY, the moment
# the resize happens - a SKU already "owned" by one consumer can
# reassign to a different partition, and therefore a different consumer
# instance, mid-flight.
#
# Runs against an isolated demo topic on the live `kafka` broker (a new
# topic, not `inventory.reservation-requested.v1` itself) so the real
# Inventory.Service consumer group's live traffic is never touched.
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"
topic="chaos-demo.sku-partitioning.v1"
skus=(SKU-A SKU-B SKU-C SKU-D SKU-E SKU-F SKU-G SKU-H SKU-I SKU-J)

cd "$compose_directory"

echo "== Creating isolated demo topic '${topic}' with 3 partitions (mirrors inventory.reservation-requested.v1's live partition count) =="
docker compose exec -T kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server kafka:9092 \
  --create --if-not-exists --topic "$topic" --partitions 3 --replication-factor 1

produce_and_show_partitions() {
  local label=$1
  local payload_file
  payload_file=$(mktemp)
  for sku in "${skus[@]}"; do
    printf '%s\t%s-%s\n' "$sku" "$label" "$sku" >> "$payload_file"
  done
  docker compose exec -T kafka /opt/kafka/bin/kafka-console-producer.sh \
    --bootstrap-server kafka:9092 --topic "$topic" \
    --property parse.key=true --property key.separator="$(printf '\t')" \
    < "$payload_file" >/dev/null
  rm -f "$payload_file"

  docker compose exec -T kafka /opt/kafka/bin/kafka-console-consumer.sh \
    --bootstrap-server kafka:9092 --topic "$topic" --from-beginning --timeout-ms 8000 \
    --property print.partition=true --property print.key=true \
    2>/dev/null | grep "^Partition:"
}

# Given "Partition:<n>\t<key>\t<value>" lines, print the partition number
# for the row whose value matches "<label>-<sku>".
partition_for() {
  local output=$1 label=$2 sku=$3
  echo "$output" | awk -F'\t' -v val="${label}-${sku}" '$3==val {print $1}' | grep -oE '[0-9]+' || true
}

echo "== Producing one keyed message per SKU under 3 partitions, recording where each SKU landed =="
before_output=$(produce_and_show_partitions "before")
echo "$before_output"

echo ""
echo "== Resizing the topic: 3 -> 6 partitions (kafka-topics.sh --alter) =="
docker compose exec -T kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server kafka:9092 \
  --alter --topic "$topic" --partitions 6

sleep 2

echo "== Producing the SAME SKUs again under 6 partitions =="
after_output=$(produce_and_show_partitions "after")
echo "$after_output"

echo ""
echo "=== RESULT: per-SKU partition assignment, 3 partitions vs 6 ==="
printf "%-10s %-12s %-12s %s\n" "SKU" "partition@3" "partition@6" "changed?"
changed_count=0
for sku in "${skus[@]}"; do
  before_partition=$(partition_for "$before_output" "before" "$sku")
  after_partition=$(partition_for "$after_output" "after" "$sku")
  changed="no"
  if [[ -n "$before_partition" && -n "$after_partition" && "$before_partition" != "$after_partition" ]]; then
    changed="YES"
    changed_count=$((changed_count + 1))
  fi
  printf "%-10s %-12s %-12s %s\n" "$sku" "$before_partition" "$after_partition" "$changed"
done

echo ""
echo "${changed_count}/${#skus[@]} SKUs mapped to a DIFFERENT partition after the resize."
if [[ "$changed_count" -gt 0 ]]; then
  echo "==> RETROACTIVE BREAK CONFIRMED: had this been inventory.reservation-requested.v1, a reservation request for one of these SKUs produced before the resize (owned by one Inventory.Service consumer instance) and another produced after (now owned by a different instance, per the new partition count) could be processed concurrently by two different pods - the per-SKU mutual exclusion Milestone 41 relies on would no longer hold for that SKU, silently, the moment the topic was resized."
fi

echo ""
echo "== Cleaning up: deleting the isolated demo topic =="
docker compose exec -T kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server kafka:9092 --delete --topic "$topic" || true
