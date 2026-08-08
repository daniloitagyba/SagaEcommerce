# Milestone 80: The Backup/Restore Drill Postgres Had and Mongo/Redis Didn't

## Scope

Milestone 27 gave Postgres real backup/restore with a measured RTO/RPO. Catalog.Service's MongoDB and Cart.Service's Redis never got the same treatment - both have durable volumes (`mongodb-data`, `redis-data`) and, for Redis, a measured crash-survival story (Milestone 46's AOF `everysec`), but "the data survives a crash in place" and "the data can be backed up and recovered somewhere else" are different claims. Nothing in this lab had ever proven the second one for either store.

## Design: live source, isolated target

Milestone 27 deliberately built a whole separate, isolated Postgres cluster for its drill rather than touching the real `orders`/`payments` database - backup/restore is inherently a destructive-adjacent operation, and that database is 26+ milestones of real validated output nothing else can regenerate.

This milestone takes a different balance, appropriate to what `mongodump`/`BGSAVE` actually are: both are safe, non-blocking, point-in-time operations against a *running* instance - there's no need to fabricate a synthetic database just to have something to back up when the real one is already right there and the backup operation itself can't hurt it. So both drills (`scripts/mongodb-backup-restore-drill.sh`, `scripts/redis-backup-restore-drill.sh`) run their **backup** step against the actual live `mongodb`/`redis` containers Catalog.Service and Cart.Service depend on, but their **restore** step always lands in a brand-new, throwaway container - never the live one. The live store is never at risk of data loss from running either script; the only write either one makes against the live instance is a marker key/document, immediately deleted again once it's safely captured in the backup.

**The marker matters.** A backup that merely contains what was already there when the script started doesn't prove point-in-time capture - it could be a scheduled backup from an hour ago and the drill wouldn't know the difference. Writing a uniquely-timestamped marker immediately before backing up, then confirming it survived into the restored copy, is the same "insert a row, then check it's there after restore" proof Milestone 27's Postgres drill used.

## What each drill does

**MongoDB** (`mongodb-backup-restore-drill.sh`): records real `products`/`categories` counts, writes a marker document to a scratch collection, runs `mongodump --db catalog --archive --gzip` straight off the live container, removes the marker from the live database, then restores the archive into a fresh `mongo:8.0` container on the same Docker network and verifies every real document plus the marker is present.

**Redis** (`redis-backup-restore-drill.sh`): records the real key count, writes a marker key, triggers `BGSAVE` and polls `LASTSAVE` until the snapshot completes, copies `dump.rdb` out via `docker compose cp`, removes the marker from the live instance, then loads the snapshot into a fresh `redis:7.4-alpine` container (stop the empty auto-started instance, drop the real `dump.rdb` into its data directory, restart) and verifies the key count and marker value match.

Both scripts clean up their throwaway container on exit (`trap ... EXIT`), succeed/fail loudly (`exit 1` on any mismatch), and never touch the live store's real data beyond the marker round-trip.

## Live validation (real Compose stack, real data)

**MongoDB**, against the actual live `catalog` database (not a demo dataset):

```
Baseline:           products=9  categories=4
Backup (mongodump): 0s, 4.0K archive
Restore (mongorestore, into a fresh container): 3s
products:    live(before)=9  restored=9
categories:  live(before)=4  restored=4
marker document recovered: YES
==> RESTORE VERIFIED
```

**Redis**, against the actual live cart store:

```
Baseline:        keys=27
Backup (BGSAVE): 0s, 4.0K dump.rdb
Restore (fresh container, load from dump.rdb): 1s
keys:   live(before)=27  restored=28   (27 + the marker written just before the snapshot)
marker key recovered: YES
==> RESTORE VERIFIED
```

Both ran clean on the first attempt - no failed attempts to document this time, unlike Milestone 27's CNPG drill. Confirmed after each run: no leftover throwaway containers (`docker ps -a | grep drill` empty), no leftover marker data in either live store (`backup_drill_marker` collection empty in Mongo, no `cart:backup-drill-marker*` keys in Redis).

## RTO, compared with Milestone 27's Postgres numbers

|  | Backup | Restore |
|---|---|---|
| Postgres (M27, CNPG + Barman Cloud, near-empty demo DB) | 4s (base backup) | 43s (point-in-time recovery, full cluster bring-up) |
| MongoDB (this milestone, real live catalog DB) | <1s (mongodump) | 3s (mongorestore into a fresh container) |
| Redis (this milestone, real live cart store) | <1s (BGSAVE) | 1s (fresh container, load from RDB) |

The gap isn't really "Postgres is slower" - it's that M27's restore RTO includes standing up an entire new CNPG-managed 3-instance cluster with an operator reconciling it, where these two drills restore into a single bare container with nothing else to coordinate. Point-in-time recovery (an arbitrary `targetTime`, not just "the last backup") also isn't attempted here - `mongodump`/`BGSAVE` are full-database point-in-time snapshots, not continuous-archiving systems with a replay log, so the meaningful recovery point is simply "whenever the last backup ran," not any second in between.

## What this milestone deliberately doesn't do

No continuous backup, no scheduled/automated drill, no S3-compatible remote storage for either store (M27's MinIO target was worth the setup for Postgres specifically because CNPG's Barman Cloud integration expects one; a one-off `mongodump`/`BGSAVE` has no such expectation and adding one here would be infrastructure for its own sake). No point-in-time recovery to an arbitrary moment - only "restore the last snapshot," which is what these tools actually offer without layering something like Mongo's oplog-based continuous backup or Redis's AOF replay on top, neither of which this lab's scale justifies. Both are real gaps relative to Milestone 27's Postgres story, left open deliberately rather than built out to match it milestone-for-milestone.

## Running the drills

```bash
scripts/mongodb-backup-restore-drill.sh
scripts/redis-backup-restore-drill.sh
```

Both are safe to run repeatedly against a live environment - they read real data, write and immediately clean up a marker, and only ever mutate a throwaway container that's removed on exit.
