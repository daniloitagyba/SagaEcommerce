#!/usr/bin/env bash
# Milestone 50 live proof: SagaTimeoutSweeper compares `DateTimeOffset.UtcNow`
# (orders-worker's own local clock) against a saga's `requested_at`
# (written in real time, possibly by a different moment or even a
# different pod) to decide if it's been running longer than
# SagaOrchestrationOptions.TimeoutSeconds (default 5s). Skewing
# orders-worker's clock forward makes it think far more time has passed
# than really has - a saga that's genuinely only been open for a couple of
# real seconds gets swept as timed out.
#
# Rather than racing a real saga's natural speed (it usually completes in
# well under a second, leaving almost no window to inject the fault before
# the row completes and deletes itself), this inserts one synthetic
# saga_orchestration_states row directly - real, unskewed `requested_at` -
# then applies the clock skew and watches the REAL SagaTimeoutSweeper /
# ClaimTimedOutAsync code path react to it. The mechanism under test is
# exactly the production one; only the row's origin is synthetic.
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"
chaos_manifest="$project_directory/kubernetes/chaos-experiments/timechaos-orders-worker-clock-skew.yaml"
namespace=${KUBERNETES_NAMESPACE:-orders-lab}

order_id=$(python3 -c "import uuid; print(uuid.uuid4())")
reservation_id=$(python3 -c "import uuid; print(uuid.uuid4())")
correlation_id="clock-skew-test-$(date +%s)"

echo "== Applying TimeChaos ahead of time: skewing orders-worker's clock +180s for 60s =="
kubectl apply -f "$chaos_manifest"
kubectl wait --for=condition=AllInjected timechaos/skew-orders-worker-clock -n "$namespace" --timeout=30s || true

insert_start=$(date +%s.%N)
echo "== Inserting a synthetic in-flight saga row (order_id=${order_id}, requested_at=NOW(), step=DecidePayment) - real clock, timestamped now that chaos is already injected =="
(cd "$compose_directory" && docker compose exec -T postgres psql -U orders -d orders -c "
  INSERT INTO saga_orchestration_states (order_id, correlation_id, requested_at, step, reservation_id, sku, quantity, amount, currency)
  VALUES ('${order_id}', '${correlation_id}', NOW(), 'DecidePayment', '${reservation_id}', 'SKU-ELEC-001', 1, 100.00, 'BRL');
")

still_present_before=$(cd "$compose_directory" && docker compose exec -T postgres psql -U orders -d orders -t -c \
  "SELECT count(*) FROM saga_orchestration_states WHERE order_id = '${order_id}';" | tr -d ' ')
echo "Row count immediately after insert: ${still_present_before}"

echo "Waiting 2 real seconds (well under the configured 5s timeout) for the sweeper's 1s tick to run under the skewed clock..."
sleep 2

still_present_after=$(cd "$compose_directory" && docker compose exec -T postgres psql -U orders -d orders -t -c \
  "SELECT count(*) FROM saga_orchestration_states WHERE order_id = '${order_id}';" | tr -d ' ')
check_end=$(date +%s.%N)
real_elapsed=$(echo "$check_end - $insert_start" | bc)
echo "Row count after 2 real seconds (elapsed: ${real_elapsed}s, chaos was already active before insert): ${still_present_after}"

echo "== orders-worker log evidence =="
worker_pod=$(kubectl get pods -n "$namespace" -l app.kubernetes.io/name=orders-worker -o jsonpath='{.items[0].metadata.name}')
kubectl logs "$worker_pod" -n "$namespace" -c orders-worker --tail=200 | grep -F "$order_id" || echo "(no matching log line found - see note below)"

echo "== Cleaning up: removing the TimeChaos experiment =="
kubectl delete -f "$chaos_manifest" --ignore-not-found

echo ""
echo "=== RESULT ==="
echo "real elapsed time (insert to final check): ${real_elapsed}s (configured timeout: 5s)"
echo "row present immediately after insert: ${still_present_before} (expected 1)"
echo "row present after ${real_elapsed}s real time:      ${still_present_after} (expected 0 - swept as timed out despite real elapsed time being under the 5s timeout)"
if [[ "$still_present_before" == "1" && "$still_present_after" == "0" ]]; then
  echo "==> CLOCK SKEW BUG CONFIRMED: real elapsed time never came close to the configured 5s timeout, but the sweeper's own clock was skewed +180s, so it saw the saga as having run for minutes and swept it."
else
  echo "==> unexpected result this run - see docs/resilience/milestone-50-clock-skew.md."
fi
