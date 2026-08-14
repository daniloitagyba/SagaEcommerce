#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"

# shellcheck disable=SC1091
source "$compose_directory/.env"

# Same host-published-port quirk as scripts/keycloak-configure-realm.sh -
# the k3s-bridge fixed IP is what's actually reachable from the host.
keycloak_url=${KEYCLOAK_URL:-http://172.30.0.17:8080}

# Milestone 83: a real shopper token, not a service-account one -
# scripts/keycloak-get-token.sh's client_credentials grant authenticates as
# the orders-api-clients service itself, which is exactly the identity
# StorefrontEndpoints.CheckoutAsync no longer trusts for "whose order is
# this." Resource Owner Password Credentials against the public
# orders-storefront client is this lab's non-interactive stand-in for the
# browser's authorization-code+PKCE flow, which nothing here can drive
# without a browser to redirect through.
curl --fail --silent --show-error \
  --data-urlencode "grant_type=password" \
  --data-urlencode "client_id=orders-storefront" \
  --data-urlencode "username=customer-42" \
  --data-urlencode "password=$KEYCLOAK_DEMO_CUSTOMER_PASSWORD" \
  "$keycloak_url/realms/orders-lab/protocol/openid-connect/token" |
  jq --raw-output '.access_token'
