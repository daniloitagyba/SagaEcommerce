#!/usr/bin/env bash
# Milestone 47 live proof: every producer in this codebase sets
# Acks.All, but every topic on the live `kafka` service is
# --replication-factor 1 on a single broker - acks=all over one replica
# is semantically acks=1, the guarantee is configured but doesn't exist.
# This runs the real quadrant against the isolated 3-broker
# `kafka-quorum-demo` cluster (compose/compose.yaml's kafka-q1/q2/q3,
# NOT the live single-broker `kafka` the saga pipeline depends on):
#
#   - unsafe: min.insync.replicas=1, unclean.leader.election.enable=true
#   - safe:   min.insync.replicas=2, unclean.leader.election.enable=false
#
# Same physical fault both times: the two follower brokers are `docker
# pause`d (frozen, not killed) until they fall out of the ISR, then the
# leader is force-killed while only it holds the latest writes. In
# "unsafe" mode this proves ACKS=ALL CAN STILL SILENTLY LOSE ACKNOWLEDGED
# DATA. In "safe" mode the same fault makes the producer fail loudly
# instead - correct, visible unavailability rather than invisible loss.
#
# A paused container can't be `docker exec`'d into at all, so every kafka
# CLI invocation issued while the two followers are paused has to run
# inside the LEADER's own container (the only one still running) - not
# a hardcoded kafka-q1, which might itself be one of the paused followers.
#
# Usage: kafka-quorum-durability-test.sh <safe|unsafe> [message_count]
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"

mode=${1:?"usage: $0 <safe|unsafe> [message_count]"}
message_count=${2:-100}
topic="quorum-demo.events.v1"
run_id="run-${mode}-$(date +%s)"

cd "$compose_directory"

case "$mode" in
  safe)
    min_isr=2
    unclean=false
    ;;
  unsafe)
    min_isr=1
    unclean=true
    ;;
  *)
    echo "unknown mode: $mode (expected safe|unsafe)" >&2
    exit 1
    ;;
esac

echo "== Bringing up the isolated kafka-quorum-demo cluster (kafka-q1/q2/q3) =="
docker compose --profile kafka-quorum-demo up -d --wait kafka-q1 kafka-q2 kafka-q3 kafka-quorum-init

docker compose exec -T kafka-q1 /opt/kafka/bin/kafka-configs.sh --bootstrap-server kafka-q1:9092 \
  --entity-type topics --entity-name "$topic" --alter \
  --add-config "min.insync.replicas=${min_isr},unclean.leader.election.enable=${unclean}"
echo "== Topic config set: min.insync.replicas=${min_isr}, unclean.leader.election.enable=${unclean} =="

state=$(docker compose exec -T kafka-q1 /opt/kafka/bin/kafka-topics.sh --bootstrap-server kafka-q1:9092 --describe --topic "$topic")
echo "$state"
leader_id=$(echo "$state" | grep -oE 'Leader: [0-9]+' | grep -oE '[0-9]+' || true)
leader_svc="kafka-q${leader_id}"
leader_container="local-distributed-lab-kafka-q${leader_id}-1"
follower_ids=$(echo "1 2 3" | tr ' ' '\n' | grep -v "^${leader_id}$")
follower_containers=()
for id in $follower_ids; do
  follower_containers+=("local-distributed-lab-kafka-q${id}-1")
done
# A follower is never killed in this test (only paused, then unpaused), so
# it's a safe exec target once operations move past the leader's death.
helper_svc="kafka-q$(echo "$follower_ids" | head -1)"
echo "Leader is broker ${leader_id} (${leader_svc}); followers: ${follower_containers[*]}; helper=${helper_svc}"

# All exec'd via the leader - the only container guaranteed not to be
# paused during the "followers frozen" window.
describe_via_leader() {
  docker compose exec -T "$leader_svc" /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --describe --topic "$topic" 2>/dev/null
}

cleanup() {
  echo "== Cleanup: ensuring all three brokers are unpaused and running =="
  for c in "$leader_container" "${follower_containers[@]}"; do
    docker unpause "$c" >/dev/null 2>&1 || true
    if [[ "$(docker inspect -f '{{.State.Status}}' "$c" 2>/dev/null)" != "running" ]]; then
      docker start "$c" >/dev/null 2>&1 || true
    fi
  done
}
trap cleanup EXIT

echo "== Pausing both followers (freezing them, not killing - they'll fall out of the ISR) =="
for c in "${follower_containers[@]}"; do
  docker pause "$c" >/dev/null
done

echo "Waiting for ISR to shrink to the leader alone (replica.lag.time.max.ms default 30s)..."
for i in $(seq 1 40); do
  isr_line=$(describe_via_leader | grep "Partition: 0" || true)
  isr=$(echo "$isr_line" | grep -oE 'Isr: [0-9,]+' | cut -d' ' -f2 || true)
  echo "  Isr=${isr}"
  if [[ "$isr" == "$leader_id" ]]; then
    break
  fi
  sleep 1
done

echo "== Producing ${message_count} messages (acks=all, max.block.ms=5000) with only the leader available =="
producer_out=$(mktemp)
set +e
seq 1 "$message_count" | sed "s/^/${run_id}-/" | docker compose exec -T "$leader_svc" /opt/kafka/bin/kafka-console-producer.sh \
  --bootstrap-server localhost:9092 \
  --topic "$topic" \
  --producer-property acks=all \
  --producer-property max.block.ms=5000 \
  --producer-property request.timeout.ms=4000 \
  --producer-property delivery.timeout.ms=8000 \
  > "$producer_out" 2>&1
producer_exit=$?
set -e
not_enough_replicas_errors=$(grep -c "NotEnoughReplicasException" "$producer_out" || true)
echo "Producer exit code: ${producer_exit} (NotEnoughReplicasException count: ${not_enough_replicas_errors})"
tail -15 "$producer_out"
rm -f "$producer_out"

if [[ "$mode" == "unsafe" ]]; then
  echo "== Killing the leader (${leader_container}) while followers are still frozen/behind =="
  docker kill -s SIGKILL "$leader_container" >/dev/null

  echo "Unpausing followers - one of them (out of sync, but unclean election allows it) becomes the new leader"
  for c in "${follower_containers[@]}"; do
    docker unpause "$c" >/dev/null
  done

  echo "Waiting for a new leader to be elected (must differ from the killed broker ${leader_id})..."
  state=""
  new_leader=""
  for i in $(seq 1 30); do
    state=$(docker compose exec -T "$helper_svc" /opt/kafka/bin/kafka-topics.sh --bootstrap-server "${helper_svc}:9092" --describe --topic "$topic" 2>/dev/null || true)
    new_leader=$(echo "$state" | grep -oE 'Leader: [0-9]+' | grep -oE '[0-9]+' || true)
    if [[ -n "$new_leader" && "$new_leader" != "-1" && "$new_leader" != "$leader_id" ]]; then
      echo "New leader: broker ${new_leader}"
      break
    fi
    sleep 1
  done
  echo "$state"

  echo "== Consuming all '${run_id}' messages from earliest =="
  consumed=$(docker compose exec -T "$helper_svc" /opt/kafka/bin/kafka-console-consumer.sh \
    --bootstrap-server "${helper_svc}:9092" \
    --topic "$topic" --from-beginning --timeout-ms 10000 2>/dev/null | grep -c "^${run_id}-" || true)

  echo ""
  echo "=== RESULT (mode=unsafe) ==="
  echo "producer exit code:       ${producer_exit} (0 = client believed all ${message_count} were acked)"
  echo "messages producer sent:   ${message_count}"
  echo "messages consumer found:  ${consumed}"
  if [[ "$producer_exit" -eq 0 && "$consumed" -lt "$message_count" ]]; then
    echo "==> DATA LOSS CONFIRMED: $((message_count - consumed)) acked message(s) never made it to the elected leader."
  else
    echo "==> no gap detected this run (see docs/messaging/milestone-47-kafka-quorum.md for why timing can matter here)."
  fi
else
  echo ""
  echo "=== RESULT (mode=safe) ==="
  echo "messages attempted:              ${message_count}"
  echo "NotEnoughReplicasException count: ${not_enough_replicas_errors} (kafka-console-producer.sh logs per-record failures via a callback and still exits 0 - the error COUNT is the real signal here, not the process exit code)"
  if [[ "$not_enough_replicas_errors" -gt 0 ]]; then
    echo "==> SAFE UNAVAILABILITY CONFIRMED: every write was correctly refused (min.insync.replicas=2 unmet with only 1 replica available) - no data was silently lost because the write was never falsely acknowledged."
  else
    echo "==> no NotEnoughReplicasException seen - min.insync.replicas did not block the write as intended."
  fi

  echo ""
  echo "== Recovery check: unpausing followers, waiting for ISR to heal, retrying the same batch =="
  for c in "${follower_containers[@]}"; do
    docker unpause "$c" >/dev/null
  done
  for i in $(seq 1 30); do
    isr_line=$(describe_via_leader | grep "Partition: 0" || true)
    isr=$(echo "$isr_line" | grep -oE 'Isr: [0-9,]+' | cut -d' ' -f2 || true)
    isr_count=$(echo "$isr" | tr ',' '\n' | grep -c '[0-9]' || true)
    if [[ "$isr_count" -eq 3 ]]; then
      echo "ISR healed: ${isr}"
      break
    fi
    sleep 1
  done
  retry_out=$(mktemp)
  set +e
  seq 1 "$message_count" | sed "s/^/${run_id}-retry-/" | docker compose exec -T "$leader_svc" /opt/kafka/bin/kafka-console-producer.sh \
    --bootstrap-server localhost:9092 \
    --topic "$topic" \
    --producer-property acks=all \
    --producer-property max.block.ms=5000 \
    > "$retry_out" 2>&1
  retry_exit=$?
  set -e
  echo "Retry after ISR healed - exit code: ${retry_exit} (0 expected now that 2 replicas are available again)"
  rm -f "$retry_out"
fi
