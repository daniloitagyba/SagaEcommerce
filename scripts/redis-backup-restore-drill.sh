#!/usr/bin/env bash
# Milestone 80: Cart.Service's Redis has had AOF persistence since
# Milestone 46 (everysec, surviving a hard kill with zero cart loss - see
# cart-redis-durability-test.sh) but "the data survives a crash" and "the
# data can be restored somewhere else" are different claims. This drill
# proves the second one: a live BGSAVE snapshot, copied out, loaded into a
# throwaway isolated container - never the live one, so a real cart in
# flight is never at risk from running this.
#
# Usage: redis-backup-restore-drill.sh
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"
work_directory=$(mktemp -d)
trap 'rm -rf "$work_directory"; docker rm -f redis-restore-drill >/dev/null 2>&1 || true' EXIT

cd "$compose_directory"
marker_key="cart:backup-drill-marker-$(date +%s)"

echo "== Baseline: real key count in the live Redis =="
keys_before=$(docker compose exec -T redis redis-cli DBSIZE | tr -d '\r')
echo "keys=${keys_before}"

echo ""
echo "== Writing a marker key (proves point-in-time, not just 'a snapshot exists') =="
docker compose exec -T redis redis-cli SET "$marker_key" "written-at-$(date -u +%Y-%m-%dT%H:%M:%SZ)" >/dev/null

echo ""
echo "== BGSAVE: non-blocking point-in-time snapshot of the live instance =="
backup_started=$(date +%s)
last_save_before=$(docker compose exec -T redis redis-cli LASTSAVE | tr -d '\r')
docker compose exec -T redis redis-cli BGSAVE >/dev/null
for i in $(seq 1 30); do
  last_save_after=$(docker compose exec -T redis redis-cli LASTSAVE | tr -d '\r')
  if [[ "$last_save_after" != "$last_save_before" ]]; then
    break
  fi
  sleep 1
done
backup_seconds=$(( $(date +%s) - backup_started ))

docker compose cp redis:/data/dump.rdb "$work_directory/dump.rdb"
backup_size=$(du -h "$work_directory/dump.rdb" | cut -f1)
echo "Snapshot complete in ${backup_seconds}s, ${backup_size}: ${work_directory}/dump.rdb"

echo ""
echo "== Removing the marker from the LIVE instance (cleanup, not part of the drill) - the snapshot above already has it saved =="
docker compose exec -T redis redis-cli DEL "$marker_key" >/dev/null

echo ""
echo "== Restoring into a throwaway, isolated container - never the live one =="
restore_started=$(date +%s)
redis_network=$(docker inspect "$(docker compose ps -q redis)" -f '{{range $k, $v := .NetworkSettings.Networks}}{{println $k}}{{end}}' | head -1)
docker run -d --name redis-restore-drill --network "$redis_network" redis:7.4-alpine >/dev/null
for i in $(seq 1 30); do
  if docker exec redis-restore-drill redis-cli ping >/dev/null 2>&1; then
    break
  fi
  sleep 1
done
# Load from the snapshot: stop the freshly-started instance's own empty
# dataset, drop the real snapshot in its data directory, then restart the
# server process so it loads dump.rdb from disk the normal way.
docker exec redis-restore-drill redis-cli SHUTDOWN NOSAVE >/dev/null 2>&1 || true
sleep 1
docker cp "$work_directory/dump.rdb" redis-restore-drill:/data/dump.rdb
docker start redis-restore-drill >/dev/null
for i in $(seq 1 30); do
  if docker exec redis-restore-drill redis-cli ping >/dev/null 2>&1; then
    break
  fi
  sleep 1
done
restore_seconds=$(( $(date +%s) - restore_started ))

echo ""
echo "== Verifying the restored, isolated copy =="
keys_after=$(docker exec redis-restore-drill redis-cli DBSIZE | tr -d '\r')
marker_value=$(docker exec redis-restore-drill redis-cli GET "$marker_key" | tr -d '\r')

echo ""
echo "=== RESULT ==="
echo "keys:   live(before)=${keys_before}  restored=${keys_after}"
echo "marker key recovered: $([ -n "$marker_value" ] && echo "YES ($marker_value)" || echo NO)"
echo "Backup RTO (BGSAVE):    ${backup_seconds}s"
echo "Restore RTO (container start to verified): ${restore_seconds}s"

if [[ "$keys_after" == "$((keys_before + 1))" && -n "$marker_value" ]]; then
  echo "==> RESTORE VERIFIED: every real key plus the point-in-time marker recovered into an isolated instance; the live Redis was never modified beyond the marker write/cleanup."
else
  echo "==> MISMATCH - see docs/data-platform/milestone-80-nosql-backup-restore.md."
  exit 1
fi
