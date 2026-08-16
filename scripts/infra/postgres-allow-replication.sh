#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"
rule='host    replication     orders          all                     scram-sha-256'

# The official postgres image's default pg_hba.conf only allows replication
# connections from loopback (127.0.0.1/::1) - its one wildcard rule
# (`host all all all scram-sha-256`) does NOT cover replication connections,
# since pg_hba.conf treats `database all` and the special `replication`
# pseudo-database as distinct matches. Debezium (Milestone 21) connects from
# a different container on the Docker network and needs a real replication
# connection, not just a regular one.
cd "$compose_directory"

if docker compose exec -T postgres grep -q -F "$rule" /var/lib/postgresql/data/pgdata/pg_hba.conf; then
  echo "Replication rule already present."
  exit 0
fi

docker compose exec -T postgres sh -c "echo '$rule' >> /var/lib/postgresql/data/pgdata/pg_hba.conf"
docker compose exec -T postgres psql --username orders --dbname orders --command 'SELECT pg_reload_conf();' >/dev/null
echo "Added replication rule and reloaded PostgreSQL configuration."
