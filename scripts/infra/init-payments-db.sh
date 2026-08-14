#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"

for command_name in docker; do
  command -v "$command_name" >/dev/null
done

cd "$compose_directory"
docker compose exec -T postgres psql \
  --username orders \
  --dbname orders <<'SQL'
SELECT 'CREATE DATABASE payments OWNER orders' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'payments')\gexec
SQL

printf 'The payments database is present.\n'
