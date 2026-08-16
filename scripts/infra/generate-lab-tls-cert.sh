#!/usr/bin/env bash
set -euo pipefail

# Generates the self-signed certificate compose/nginx/storefront-tls.conf and
# keycloak-tls.conf serve - lets a browser treat those origins as a secure
# context, which the storefront's OIDC/PKCE login flow needs (see those two
# files for why). Always trusted for 127.0.0.1/localhost; pass your LAN IP
# (and/or hostname) as arguments to also trust the certificate there, e.g.:
#
#   scripts/generate-lab-tls-cert.sh 192.168.15.11
#
# Not committed (compose/nginx/certs/ is gitignored) - self-signed and
# regenerable, and a private key has no business in git history. Re-run any
# time the LAN IP changes; restart nginx afterward to pick it up:
#   docker compose --profile compose-apps up -d nginx

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
certs_directory="$project_directory/compose/nginx/certs"
mkdir -p "$certs_directory"

sans=("IP:127.0.0.1" "DNS:localhost")
for host in "$@"; do
  if [[ "$host" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    sans+=("IP:$host")
  else
    sans+=("DNS:$host")
  fi
done
san_list=$(IFS=,; echo "${sans[*]}")

openssl req -x509 -nodes -newkey rsa:2048 -days 3650 \
  -keyout "$certs_directory/lab.key" \
  -out "$certs_directory/lab.crt" \
  -subj "/CN=local-distributed-lab" \
  -addext "subjectAltName=$san_list" \
  >/dev/null 2>&1

chmod 600 "$certs_directory/lab.key"
printf 'Generated %s (SANs: %s)\n' "$certs_directory/lab.crt" "$san_list"
printf 'Restart nginx to pick it up: docker compose --profile compose-apps up -d nginx\n'
printf 'First visit to https://<host>:8443 or :18443 will need a one-time "proceed anyway" past the self-signed warning.\n'
