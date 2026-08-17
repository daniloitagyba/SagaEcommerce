#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
compose_directory="$project_directory/compose"

source "$compose_directory/.env"

keycloak_url=${KEYCLOAK_URL:-http://172.30.0.17:8080}

curl --fail --silent --show-error \
  --data-urlencode "grant_type=password" \
  --data-urlencode "client_id=orders-storefront" \
  --data-urlencode "username=customer-42" \
  --data-urlencode "password=$KEYCLOAK_DEMO_CUSTOMER_PASSWORD" \
  "$keycloak_url/realms/orders-lab/protocol/openid-connect/token" |
  jq --raw-output '.access_token'
