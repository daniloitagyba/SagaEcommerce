#!/usr/bin/env bash
# Guards against a specific class of bug found live during the
# DistributedEcommerce rename: compose.yaml's top-level project `name:`
# (and any network's explicit `name:` override) is the label prefix Docker
# actually uses for containers/networks/volumes. A script that hardcodes a
# `--network <project>_<net>` reference silently drifts out of sync if the
# project name in compose.yaml changes without updating it too - nothing
# fails until that script is run against the live stack. This check
# compares every hardcoded `--network` reference under scripts/ against the
# network names compose.yaml would actually produce.
set -euo pipefail

cd "$(dirname "$0")/../.."

COMPOSE_FILE="compose/compose.yaml"
PROJECT_NAME=$(grep -m1 '^name:' "$COMPOSE_FILE" | sed -E 's/^name:[[:space:]]*//')

if [[ -z "$PROJECT_NAME" ]]; then
  echo "verify-compose-network-names: could not find top-level 'name:' in $COMPOSE_FILE" >&2
  exit 1
fi

# Explicit network name overrides declared under `networks:` in compose.yaml.
EXPLICIT_NAMES=()
while IFS= read -r name; do
  [[ -n "$name" ]] && EXPLICIT_NAMES+=("$name")
done < <(awk '/^networks:/{f=1;next} f && /^[a-zA-Z]/{f=0} f && /^    name:/{sub(/^    name:[[:space:]]*/,"");print}' "$COMPOSE_FILE")

# Implicit names Compose derives as "${project}_${network}" for any network
# key that doesn't have an explicit name override.
NETWORK_KEYS=()
while IFS= read -r key; do
  [[ -n "$key" ]] && NETWORK_KEYS+=("$key")
done < <(awk '/^networks:/{f=1;next} f && /^[a-zA-Z]/{f=0} f && /^  [A-Za-z0-9_-]+:/{gsub(":","");print $1}' "$COMPOSE_FILE")

VALID_NAMES=("${EXPLICIT_NAMES[@]}" "none" "host" "bridge")
for key in "${NETWORK_KEYS[@]}"; do
  VALID_NAMES+=("${PROJECT_NAME}_${key}")
done

# Scan every script except this one (its own comments/messages mention
# "--network" as prose, not as an actual invocation).
SCAN_FILES=()
while IFS= read -r f; do
  [[ "$(basename "$f")" == "verify-compose-network-names.sh" ]] && continue
  SCAN_FILES+=("$f")
done < <(find scripts -name '*.sh' | sort)

fail=0
while IFS=: read -r file line_no rest; do
  network_name="${rest#--network }"
  match=0
  for valid in "${VALID_NAMES[@]}"; do
    [[ "$network_name" == "$valid" ]] && match=1 && break
  done
  if [[ "$match" -eq 0 ]]; then
    echo "verify-compose-network-names: $file:$line_no references docker network '$network_name', which compose.yaml (project: '$PROJECT_NAME') would not create. Known-good names: ${VALID_NAMES[*]}" >&2
    fail=1
  fi
done < <(grep -noE -- '--network [A-Za-z0-9._-]+' "${SCAN_FILES[@]}" | grep -vE ':[0-9]+:[[:space:]]*#')

if [[ "$fail" -eq 0 ]]; then
  echo "verify-compose-network-names: OK - all hardcoded --network references match compose.yaml"
fi
exit "$fail"
