#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"
connector_name=orders-outbox-connector

for command_name in curl docker jq; do
  command -v "$command_name" >/dev/null
done

cd "$compose_directory"
docker compose --profile compose-apps --profile cdc up --detach --wait cdc-topics-init debezium

postgres_password=$(
  cd "$compose_directory"
  docker compose --profile compose-apps config --format json |
    jq --exit-status --raw-output '.services.migrations.environment.ConnectionStrings__Orders' |
    grep --only-matching --perl-regexp 'Password=\K[^;]+'
)

# The Debezium REST API's published host port doesn't actually bind on this
# server despite `docker inspect` showing correct HostConfig.PortBindings -
# the same Docker networking quirk already documented for Alertmanager in
# Milestone 16. Worked around the same way: call the API from inside the
# container itself rather than via the host port.
cd "$compose_directory"
docker compose exec -T debezium curl \
  --fail --silent --show-error \
  --request PUT "http://localhost:8083/connectors/$connector_name/config" \
  --header 'Content-Type: application/json' \
  --data @- <<EOF
{
  "connector.class": "io.debezium.connector.postgresql.PostgresConnector",
  "database.hostname": "postgres",
  "database.port": "5432",
  "database.user": "orders",
  "database.password": "$postgres_password",
  "database.dbname": "orders",
  "topic.prefix": "orders-cdc",
  "table.include.list": "public.outbox_messages",
  "plugin.name": "pgoutput",
  "slot.name": "debezium_orders_outbox",
  "publication.autocreate.mode": "filtered",
  "snapshot.mode": "no_data",
  "tombstones.on.delete": "false"
}
EOF

echo
printf 'Registered connector %s\n' "$connector_name"

for _ in $(seq 1 15); do
  state=$(docker compose exec -T debezium curl --fail --silent "http://localhost:8083/connectors/$connector_name/status" | jq --raw-output '.connector.state')
  printf 'Connector state: %s\n' "$state"
  if [[ "$state" == "RUNNING" ]]; then
    exit 0
  fi
  sleep 2
done

echo "Connector did not reach RUNNING state." >&2
exit 1
