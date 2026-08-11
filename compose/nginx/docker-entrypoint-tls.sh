#!/bin/sh
set -eu

# storefront-tls.conf/keycloak-tls.conf reference certs/lab.{crt,key}
# unconditionally. Those aren't committed (self-signed, gitignored - see
# scripts/generate-lab-tls-cert.sh) and, without this, a fresh clone or CI
# runner has none: nginx fails its config test on boot ("cannot load
# certificate"), the container crash-loops, and `docker compose up --wait`
# times out on the healthcheck - exactly the reproducibility problem
# compose.lab.yaml solved for the otel-collector network, just for TLS
# instead. So: synthesize a throwaway self-signed cert here if one isn't
# already mounted, purely so nginx has something to boot with. It's only
# valid for 127.0.0.1/localhost, which is enough for the plain quickstart;
# run scripts/generate-lab-tls-cert.sh <your-lan-ip> and restart nginx to
# get a cert your LAN devices' browsers will actually trust for that IP.
certs_directory=/etc/nginx/conf.d/certs
cert_file="$certs_directory/lab.crt"
key_file="$certs_directory/lab.key"

if [ ! -f "$cert_file" ] || [ ! -f "$key_file" ]; then
  mkdir -p "$certs_directory"
  openssl req -x509 -nodes -newkey rsa:2048 -days 3650 \
    -keyout "$key_file" -out "$cert_file" \
    -subj "/CN=local-distributed-lab" \
    -addext "subjectAltName=IP:127.0.0.1,DNS:localhost" \
    >/dev/null 2>&1
fi

exec /docker-entrypoint.sh "$@"
