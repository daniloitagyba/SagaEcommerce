#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"

# shellcheck disable=SC1091
source "$compose_directory/.env"

# The published host port (127.0.0.1:18081, matching KEYCLOAK_PORT) records
# a correct binding in `docker inspect` but doesn't actually get programmed
# on this host - the same class of quirk already hit with Alertmanager
# (Milestone 16) and Debezium's connector API (Milestone 21). The k3s-bridge
# network's fixed IP is host-routable and reliably reachable instead.
keycloak_url=${KEYCLOAK_URL:-http://172.30.0.17:8080}
realm_name=orders-lab
client_id=orders-api-clients

curl_json() {
  curl --fail --silent --show-error --header "Content-Type: application/json" "$@"
}

# Milestone 84: one client now needs an audience claim recognized by up to
# three services (orders-api, catalog-service, inventory-service), each
# validating its own Audience so a token minted for one can't be replayed
# against another - so this is a function, not three more copy-pasted
# blocks like Milestone 83 already had once for orders-api alone.
create_audience_mapper() {
  local internal_id=$1
  local audience=$2
  local mapper_name="${audience}-audience"

  local existing
  existing=$(
    curl --fail --silent --header "$auth_header" \
      "$keycloak_url/admin/realms/$realm_name/clients/$internal_id/protocol-mappers/models" |
      jq --raw-output --arg name "$mapper_name" '.[] | select(.name == $name) | .id // empty'
  )
  if [[ -z "$existing" ]]; then
    curl_json --header "$auth_header" \
      --data "{\"name\":\"$mapper_name\",\"protocol\":\"openid-connect\",\"protocolMapper\":\"oidc-audience-mapper\",\"config\":{\"included.custom.audience\":\"$audience\",\"access.token.claim\":\"true\",\"id.token.claim\":\"false\"}}" \
      "$keycloak_url/admin/realms/$realm_name/clients/$internal_id/protocol-mappers/models"
    printf 'Created %s audience mapper (client %s).\n' "$audience" "$internal_id"
  else
    printf 'Audience mapper %s already exists (client %s).\n' "$audience" "$internal_id"
  fi
}

# Milestone 84: adds every role in $2... to user $1 that it doesn't already hold.
assign_missing_roles() {
  local user_id=$1
  shift
  local existing
  existing=$(
    curl --fail --silent --header "$auth_header" \
      "$keycloak_url/admin/realms/$realm_name/users/$user_id/role-mappings/realm" |
      jq --raw-output '.[].name'
  )

  local to_assign="[]"
  for role_name in "$@"; do
    if grep --quiet --fixed-strings --line-regexp "$role_name" <<<"$existing"; then
      continue
    fi
    local role_json
    role_json=$(curl --fail --silent --header "$auth_header" "$keycloak_url/admin/realms/$realm_name/roles/$role_name")
    to_assign=$(jq --argjson role "$role_json" '. + [$role]' <<<"$to_assign")
  done

  if [[ "$(jq 'length' <<<"$to_assign")" -gt 0 ]]; then
    curl_json --header "$auth_header" --data "$to_assign" \
      "$keycloak_url/admin/realms/$realm_name/users/$user_id/role-mappings/realm"
    printf 'Assigned roles to user %s.\n' "$user_id"
  else
    printf 'User %s already has every requested role.\n' "$user_id"
  fi
}

admin_token=$(
  curl --fail --silent --show-error \
    --data-urlencode "client_id=admin-cli" \
    --data-urlencode "username=admin" \
    --data-urlencode "password=$KEYCLOAK_ADMIN_PASSWORD" \
    --data-urlencode "grant_type=password" \
    "$keycloak_url/realms/master/protocol/openid-connect/token" |
    jq --raw-output '.access_token'
)
auth_header="Authorization: Bearer $admin_token"

if curl --fail --silent --header "$auth_header" "$keycloak_url/admin/realms/$realm_name" >/dev/null 2>&1; then
  printf 'Realm %s already exists.\n' "$realm_name"
else
  curl_json --header "$auth_header" \
    --data "{\"realm\":\"$realm_name\",\"enabled\":true,\"accessTokenLifespan\":900}" \
    "$keycloak_url/admin/realms"
  printf 'Created realm %s.\n' "$realm_name"
fi

# Milestone 83: orders:admin - cross-customer access (a support agent, the
# warehouse's fulfilment tooling), distinct from a plain shopper's
# orders:read/orders:write. Orders.Api's policies accept it in place of
# either, so a single role covers everything a shopper's token can do plus
# what an admin-only route (POST /orders/{id}/fulfillment) requires.
#
# Milestone 84: catalog:admin gates catalog writes (POST /products,
# POST /categories) - unauthenticated before this milestone, and a product
# price a client could inject is a price OrderPricingService then trusts.
# inventory:read gates GET /inventory (the full-catalog listing with exact
# quantities); the per-SKU lookup stays open to any caller, coarsened to an
# availability band rather than an exact count for one that isn't authenticated.
#
# Milestone 88 follow-up: payments:read gates GET /payments/by-order/{id} -
# the anti-entropy sweep's own read, unauthenticated (a named gap) until
# this role gave Orders.Worker's own service account something to present.
for role_name in "orders:read" "orders:write" "orders:admin" "catalog:admin" "inventory:read" "payments:read"; do
  if curl --fail --silent --header "$auth_header" "$keycloak_url/admin/realms/$realm_name/roles/$role_name" >/dev/null 2>&1; then
    printf 'Role %s already exists.\n' "$role_name"
  else
    curl_json --header "$auth_header" \
      --data "{\"name\":\"$role_name\"}" \
      "$keycloak_url/admin/realms/$realm_name/roles"
    printf 'Created role %s.\n' "$role_name"
  fi
done

client_internal_id=$(
  curl --fail --silent --header "$auth_header" \
    "$keycloak_url/admin/realms/$realm_name/clients?clientId=$client_id" |
    jq --raw-output '.[0].id // empty'
)

if [[ -z "$client_internal_id" ]]; then
  # secret is fixed to KEYCLOAK_CLIENT_SECRET (from .env, same file
  # POSTGRES_PASSWORD and KEYCLOAK_ADMIN_PASSWORD already live in) rather
  # than left for Keycloak to generate - every script that needs a token
  # (smoke tests, k6) reads it from the same known source instead of
  # scraping this script's own output.
  curl_json --header "$auth_header" \
    --data "{\"clientId\":\"$client_id\",\"publicClient\":false,\"secret\":\"$KEYCLOAK_CLIENT_SECRET\",\"serviceAccountsEnabled\":true,\"standardFlowEnabled\":false,\"directAccessGrantsEnabled\":false}" \
    "$keycloak_url/admin/realms/$realm_name/clients"
  client_internal_id=$(
    curl --fail --silent --header "$auth_header" \
      "$keycloak_url/admin/realms/$realm_name/clients?clientId=$client_id" |
      jq --raw-output '.[0].id'
  )
  printf 'Created client %s.\n' "$client_id"
else
  printf 'Client %s already exists.\n' "$client_id"
fi

# client_credentials tokens default to "aud": "account" - a hardcoded
# audience mapper is what makes each service's own Audience check
# meaningful, rather than accepting any token this realm ever issues.
# Milestone 84: catalog-service, inventory-service and cart-service join
# orders-api - this client (trusted backend tooling) needs to reach all four.
# Milestone 88 follow-up: payments-service joins too - this same client is
# also what Orders.Worker's anti-entropy sweeper authenticates as now.
create_audience_mapper "$client_internal_id" "orders-api"
create_audience_mapper "$client_internal_id" "catalog-service"
create_audience_mapper "$client_internal_id" "inventory-service"
create_audience_mapper "$client_internal_id" "cart-service"
create_audience_mapper "$client_internal_id" "payments-service"

service_account_user_id=$(
  curl --fail --silent --header "$auth_header" \
    "$keycloak_url/admin/realms/$realm_name/clients/$client_internal_id/service-account-user" |
    jq --raw-output '.id'
)

# Milestone 83/84: this service account is this lab's stand-in for trusted
# backend tooling (an operator, a warehouse integration) - every role a
# backend caller might legitimately need across all three protected services.
assign_missing_roles "$service_account_user_id" \
  "orders:read" "orders:write" "orders:admin" "catalog:admin" "inventory:read" "payments:read"

# Milestone 83: orders-storefront - a public client (no secret; PKCE
# instead, since a browser can't keep one) so a shopper authenticates as
# themselves rather than Storefront.Service asserting an identity on their
# behalf. Direct access grants (Resource Owner Password Credentials) are
# also enabled here - not something a real production realm would turn on,
# but this lab's non-interactive smoke/k6 scripts have no browser to drive
# an authorization-code redirect through, and ROPC is the only way they can
# obtain a genuine per-shopper token without one.
storefront_client_id=orders-storefront
storefront_internal_id=$(
  curl --fail --silent --header "$auth_header" \
    "$keycloak_url/admin/realms/$realm_name/clients?clientId=$storefront_client_id" |
    jq --raw-output '.[0].id // empty'
)

if [[ -z "$storefront_internal_id" ]]; then
  curl_json --header "$auth_header" \
    --data "{\"clientId\":\"$storefront_client_id\",\"publicClient\":true,\"serviceAccountsEnabled\":false,\"standardFlowEnabled\":true,\"directAccessGrantsEnabled\":true,\"attributes\":{\"pkce.code.challenge.method\":\"S256\"}}" \
    "$keycloak_url/admin/realms/$realm_name/clients"
  storefront_internal_id=$(
    curl --fail --silent --header "$auth_header" \
      "$keycloak_url/admin/realms/$realm_name/clients?clientId=$storefront_client_id" |
      jq --raw-output '.[0].id'
  )
  printf 'Created client %s.\n' "$storefront_client_id"
else
  printf 'Client %s already exists.\n' "$storefront_client_id"
fi

# storefront-web's own follow-up: this client's create call above never set
# redirectUris/webOrigins - Keycloak leaves both empty when omitted, which
# fails the authorization-code+PKCE flow outright with "invalid
# redirect_uri" the moment a real browser tries it, whether the client was
# just created above or already existed from an earlier run. Idempotent
# either way: fetch-merge-PUT, not create-only, so re-running this script
# after the frontend's origins change (or against an already-configured
# realm) still converges instead of silently doing nothing.
curl --fail --silent --header "$auth_header" \
  "$keycloak_url/admin/realms/$realm_name/clients/$storefront_internal_id" |
  jq '.redirectUris = ["http://localhost:5173/*", "http://localhost:8089/*"]
    | .webOrigins = ["http://localhost:5173", "http://localhost:8089"]' \
  > /tmp/orders-storefront-client.json
curl --fail --silent --request PUT --header "$auth_header" --header "Content-Type: application/json" \
  --data @/tmp/orders-storefront-client.json \
  "$keycloak_url/admin/realms/$realm_name/clients/$storefront_internal_id"
rm -f /tmp/orders-storefront-client.json
printf 'Set redirectUris/webOrigins on %s for the Vite dev server (5173) and the compose-served build (8089).\n' "$storefront_client_id"

# orders-api (checkout) and cart-service (Milestone 84: the shopper's own
# cart, keyed by their own identity) - not catalog-service or
# inventory-service, which stay anonymous through Storefront's proxy.
create_audience_mapper "$storefront_internal_id" "orders-api"
create_audience_mapper "$storefront_internal_id" "cart-service"

# Milestone 83: a demo shopper, username matching the customerId this lab's
# quickstart and coupon seeds have used since Milestone 7
# ("customer-42") - Orders.Api reads preferred_username as the order's
# customerId (CallerIdentityExtensions), so this is the bridge that keeps
# every existing consumer of that string (coupons, loyalty tiers, order
# history) working unchanged under real identity, no parallel migration.
demo_username=customer-42
demo_user_id=$(
  curl --fail --silent --header "$auth_header" \
    "$keycloak_url/admin/realms/$realm_name/users?username=$demo_username&exact=true" |
    jq --raw-output '.[0].id // empty'
)

if [[ -z "$demo_user_id" ]]; then
  # email/firstName/lastName, not just username: this realm's User Profile
  # config requires them, and a user created without them fails every
  # login - including the direct-grant one this same script's ROPC callers
  # use - with "resolve_required_actions"/"Account is not fully set up",
  # a Keycloak error that names neither the missing field nor the fix.
  curl_json --header "$auth_header" \
    --data "{\"username\":\"$demo_username\",\"enabled\":true,\"email\":\"$demo_username@example.invalid\",\"emailVerified\":true,\"firstName\":\"Demo\",\"lastName\":\"Shopper\",\"credentials\":[{\"type\":\"password\",\"value\":\"$KEYCLOAK_DEMO_CUSTOMER_PASSWORD\",\"temporary\":false}]}" \
    "$keycloak_url/admin/realms/$realm_name/users"
  demo_user_id=$(
    curl --fail --silent --header "$auth_header" \
      "$keycloak_url/admin/realms/$realm_name/users?username=$demo_username&exact=true" |
      jq --raw-output '.[0].id'
  )
  printf 'Created demo user %s.\n' "$demo_username"
else
  printf 'Demo user %s already exists.\n' "$demo_username"
fi

assign_missing_roles "$demo_user_id" "orders:read" "orders:write"

printf '\nRealm ready.\n'
printf 'Confidential client: %s (secret is KEYCLOAK_CLIENT_SECRET in .env) - orders:read, orders:write, orders:admin, catalog:admin, inventory:read, payments:read.\n' "$client_id"
printf 'Public client: %s (PKCE, no secret) - the shopper-facing client. orders-api + cart-service audiences.\n' "$storefront_client_id"
printf 'Demo shopper: %s / KEYCLOAK_DEMO_CUSTOMER_PASSWORD in .env.\n' "$demo_username"
