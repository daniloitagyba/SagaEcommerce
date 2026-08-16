#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
compose_directory="$project_directory/compose"

# shellcheck disable=SC1091
source "$compose_directory/.env"

# Same host-published-port quirk as scripts/keycloak-configure-realm.sh -
# the k3s-bridge fixed IP is what's actually reachable from the host.
keycloak_url=${KEYCLOAK_URL:-http://172.30.0.17:8080}
keycloak_host_port=${keycloak_url#http://}
keycloak_ip=${keycloak_host_port%%:*}

# Keycloak runs with KC_HOSTNAME_STRICT=false and no KC_HOSTNAME (see
# compose.yaml's comment on the keycloak service): it mints each token's
# "iss" claim from whatever Host header the request actually arrived with.
# orders-api/inventory-service/payments-service only accept "keycloak:8080"
# (Authentication:Authority) or PUBLIC_KEYCLOAK_URL (AlternateAuthority,
# unset here) as issuers - a raw request to the k3s-bridge IP mints
# iss=http://172.30.0.17:8080/..., which matches neither, so every
# resulting token gets rejected as an issuer mismatch (401, not the 403 an
# actual missing-role problem would give). --resolve makes curl connect to
# that IP while sending "keycloak:8080" as the Host header, so the minted
# token's issuer matches Authority exactly, the same identity every
# in-network service already validates against.
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
