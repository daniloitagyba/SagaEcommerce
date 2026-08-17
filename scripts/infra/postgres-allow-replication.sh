#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
compose_directory="$project_directory/compose"
rule='host    replication     orders          all                     scram-sha-256'

cd "$compose_directory"

if docker compose exec -T postgres grep -q -F "$rule" /var/lib/postgresql/data/pgdata/pg_hba.conf; then
  echo "Replication rule already present."
  exit 0
fi

docker compose exec -T postgres sh -c "echo '$rule' >> /var/lib/postgresql/data/pgdata/pg_hba.conf"
docker compose exec -T postgres psql --username orders --dbname orders --command 'SELECT pg_reload_conf();' >/dev/null
echo "Added replication rule and reloaded PostgreSQL configuration."
