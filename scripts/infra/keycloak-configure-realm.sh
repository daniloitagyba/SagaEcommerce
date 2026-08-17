#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
compose_directory="$project_directory/compose"

source "$compose_directory/.env"

keycloak_url=${KEYCLOAK_URL:-http://172.30.0.17:8080}
realm_name=orders-lab
client_id=orders-api-clients

curl_json() {
  curl --fail --silent --show-error --header "Content-Type: application/json" "$@"
}

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
    --data "{\"realm\":\"$realm_name\",\"enabled\":true,\"accessTokenLifespan\":900,\"registrationAllowed\":true}" \
    "$keycloak_url/admin/realms"
  printf 'Created realm %s.\n' "$realm_name"
fi

curl --fail --silent --header "$auth_header" \
  "$keycloak_url/admin/realms/$realm_name" |
  jq '.registrationAllowed = true' \
  > /tmp/orders-lab-realm.json
curl --fail --silent --request PUT --header "$auth_header" --header "Content-Type: application/json" \
  --data @/tmp/orders-lab-realm.json \
  "$keycloak_url/admin/realms/$realm_name"
rm -f /tmp/orders-lab-realm.json
printf 'Ensured registrationAllowed=true on realm %s.\n' "$realm_name"

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

assign_missing_roles "$service_account_user_id" \
  "orders:read" "orders:write" "orders:admin" "catalog:admin" "inventory:read" "payments:read"

worker_client_id=orders-worker
worker_internal_id=$(
  curl --fail --silent --header "$auth_header" \
    "$keycloak_url/admin/realms/$realm_name/clients?clientId=$worker_client_id" |
    jq --raw-output '.[0].id // empty'
)

if [[ -z "$worker_internal_id" ]]; then
  curl_json --header "$auth_header" \
    --data "{\"clientId\":\"$worker_client_id\",\"publicClient\":false,\"secret\":\"$KEYCLOAK_CLIENT_SECRET\",\"serviceAccountsEnabled\":true,\"standardFlowEnabled\":false,\"directAccessGrantsEnabled\":false}" \
    "$keycloak_url/admin/realms/$realm_name/clients"
  worker_internal_id=$(
    curl --fail --silent --header "$auth_header" \
      "$keycloak_url/admin/realms/$realm_name/clients?clientId=$worker_client_id" |
      jq --raw-output '.[0].id'
  )
  printf 'Created least-privilege client %s.\n' "$worker_client_id"
else
  printf 'Client %s already exists.\n' "$worker_client_id"
fi

create_audience_mapper "$worker_internal_id" "inventory-service"
create_audience_mapper "$worker_internal_id" "payments-service"
worker_service_account_user_id=$(
  curl --fail --silent --header "$auth_header" \
    "$keycloak_url/admin/realms/$realm_name/clients/$worker_internal_id/service-account-user" |
    jq --raw-output '.id'
)
assign_missing_roles "$worker_service_account_user_id" "inventory:read" "payments:read"

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

storefront_origins=("http://localhost:5173" "http://localhost:8089")
if [[ -n "${PUBLIC_STOREFRONT_URL:-}" ]]; then
  storefront_origins+=("$PUBLIC_STOREFRONT_URL")
fi
storefront_redirect_uris=$(printf '%s/*\n' "${storefront_origins[@]}" | jq --raw-input --slurp 'split("\n") | map(select(length > 0))')
storefront_web_origins=$(printf '%s\n' "${storefront_origins[@]}" | jq --raw-input --slurp 'split("\n") | map(select(length > 0))')
curl --fail --silent --header "$auth_header" \
  "$keycloak_url/admin/realms/$realm_name/clients/$storefront_internal_id" |
  jq --argjson redirectUris "$storefront_redirect_uris" --argjson webOrigins "$storefront_web_origins" \
    '.redirectUris = $redirectUris | .webOrigins = $webOrigins' \
  > /tmp/orders-storefront-client.json
curl --fail --silent --request PUT --header "$auth_header" --header "Content-Type: application/json" \
  --data @/tmp/orders-storefront-client.json \
  "$keycloak_url/admin/realms/$realm_name/clients/$storefront_internal_id"
rm -f /tmp/orders-storefront-client.json
printf 'Set redirectUris/webOrigins on %s for: %s\n' "$storefront_client_id" "${storefront_origins[*]}"

create_audience_mapper "$storefront_internal_id" "orders-api"
create_audience_mapper "$storefront_internal_id" "cart-service"

demo_username=customer-42
demo_user_id=$(
  curl --fail --silent --header "$auth_header" \
    "$keycloak_url/admin/realms/$realm_name/users?username=$demo_username&exact=true" |
    jq --raw-output '.[0].id // empty'
)

if [[ -z "$demo_user_id" ]]; then
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
printf 'Worker client: %s (same lab secret, separate identity) - inventory:read, payments:read only.\n' "$worker_client_id"
printf 'Public client: %s (PKCE, no secret) - the shopper-facing client. orders-api + cart-service audiences.\n' "$storefront_client_id"
printf 'Demo shopper: %s / KEYCLOAK_DEMO_CUSTOMER_PASSWORD in .env.\n' "$demo_username"
