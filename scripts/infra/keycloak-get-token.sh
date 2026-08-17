#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
compose_directory="$project_directory/compose"

source "$compose_directory/.env"

keycloak_url=${KEYCLOAK_URL:-http://172.30.0.17:8080}
keycloak_host_port=${keycloak_url#http://}
keycloak_ip=${keycloak_host_port%%:*}

token_response=$(curl --fail --silent --show-error \
  --resolve "keycloak:8080:${keycloak_ip}" \
  --data-urlencode "grant_type=client_credentials" \
  --data-urlencode "client_id=orders-api-clients" \
  --data-urlencode "client_secret=$KEYCLOAK_CLIENT_SECRET" \
  "http://keycloak:8080/realms/orders-lab/protocol/openid-connect/token")

access_token=$(jq --raw-output '.access_token // empty' <<<"$token_response")
if [[ -z "$access_token" ]]; then
  echo "ERROR: token response had no access_token: $token_response" >&2
  exit 1
fi
printf '%s\n' "$access_token"
