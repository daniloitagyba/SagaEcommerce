#!/usr/bin/env bash
# Milestone 80: Catalog.Service's MongoDB has had a persistent volume
# since Milestone 40 (mongodb-data:/data/db) but no backup and no proven
# restore path - "the disk survives" was the entire strategy, the same gap
# Milestone 27 closed for Postgres. Unlike M27, this drill runs against
# the LIVE `mongodb` container's real `catalog` database rather than a
# synthetic isolated cluster: mongodump against a running mongod is a
# safe, non-blocking, point-in-time operation, so there's no need to
# stand up a separate instance just to get a realistic data volume to
# restore. What IS isolated is the restore target - a throwaway container,
# never the live one - so this drill can never corrupt real catalog data.
#
# Usage: mongodb-backup-restore-drill.sh
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"
work_directory=$(mktemp -d)
trap 'rm -rf "$work_directory"; docker rm -f mongodb-restore-drill >/dev/null 2>&1 || true' EXIT

cd "$compose_directory"
marker_id="backup-drill-$(date +%s)"

echo "== Baseline: real document counts in the live catalog database =="
products_before=$(docker compose exec -T mongodb mongosh --quiet catalog --eval "db.products.countDocuments({})" | tail -1)
categories_before=$(docker compose exec -T mongodb mongosh --quiet catalog --eval "db.categories.countDocuments({})" | tail -1)
echo "products=${products_before} categories=${categories_before}"

echo ""
echo "== Writing a marker document (proves point-in-time, not just 'a backup exists') =="
docker compose exec -T mongodb mongosh --quiet catalog --eval "
  db.backup_drill_marker.insertOne({markerId: '${marker_id}', writtenAt: new Date()});
  print('marker written');
"

echo ""
echo "== mongodump: full 'catalog' database, gzip archive, from the live container =="
backup_started=$(date +%s)
docker compose exec -T mongodb mongodump --db catalog --archive --gzip > "$work_directory/catalog-backup.archive.gz"
backup_seconds=$(( $(date +%s) - backup_started ))
backup_size=$(du -h "$work_directory/catalog-backup.archive.gz" | cut -f1)
echo "Backup complete in ${backup_seconds}s, ${backup_size}: ${work_directory}/catalog-backup.archive.gz"

echo ""
echo "== Removing the marker from the LIVE database (cleanup, not part of the drill) - the backup above already has it archived =="
docker compose exec -T mongodb mongosh --quiet catalog --eval "db.backup_drill_marker.deleteOne({markerId: '${marker_id}'}); print('marker removed from live db');"

echo ""
echo "== Restoring into a throwaway, isolated container - never the live one =="
restore_started=$(date +%s)
mongodb_network=$(docker inspect "$(docker compose ps -q mongodb)" -f '{{range $k, $v := .NetworkSettings.Networks}}{{println $k}}{{end}}' | head -1)
docker run -d --name mongodb-restore-drill --network "$mongodb_network" mongo:8.0 >/dev/null

echo "Waiting for the restore target to accept connections..."
for i in $(seq 1 30); do
  if docker exec mongodb-restore-drill mongosh --quiet --eval "db.adminCommand('ping')" >/dev/null 2>&1; then
    break
  fi
  sleep 1
done

docker cp "$work_directory/catalog-backup.archive.gz" mongodb-restore-drill:/tmp/catalog-backup.archive.gz
docker exec mongodb-restore-drill mongorestore --archive=/tmp/catalog-backup.archive.gz --gzip >/dev/null
restore_seconds=$(( $(date +%s) - restore_started ))

echo ""
echo "== Verifying the restored, isolated copy =="
products_after=$(docker exec mongodb-restore-drill mongosh --quiet catalog --eval "db.products.countDocuments({})" | tail -1)
categories_after=$(docker exec mongodb-restore-drill mongosh --quiet catalog --eval "db.categories.countDocuments({})" | tail -1)
marker_found=$(docker exec mongodb-restore-drill mongosh --quiet catalog --eval "db.backup_drill_marker.countDocuments({markerId: '${marker_id}'})" | tail -1)

echo ""
echo "=== RESULT ==="
echo "products:    live(before)=${products_before}  restored=${products_after}"
echo "categories:  live(before)=${categories_before}  restored=${categories_after}"
echo "marker document recovered: $([ "$marker_found" = "1" ] && echo YES || echo NO)"
echo "Backup RTO (mongodump):    ${backup_seconds}s"
echo "Restore RTO (mongorestore, container start to verified): ${restore_seconds}s"

if [[ "$products_after" == "$products_before" && "$categories_after" == "$categories_before" && "$marker_found" == "1" ]]; then
  echo "==> RESTORE VERIFIED: every real document plus the point-in-time marker recovered into an isolated instance; the live catalog database was never modified beyond the marker write/cleanup."
else
  echo "==> MISMATCH - see docs/data-platform/milestone-80-nosql-backup-restore.md."
  exit 1
fi
