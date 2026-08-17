#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
compose_directory="$project_directory/compose"

mode=${1:?"usage: $0 <none|rdb|aof-everysec|aof-always> [cart_count]"}
cart_count=${2:-300}
sku="SKU-ELEC-001"
ttl_seconds=${CART_TTL_SECONDS:-1800}
run_id="dur-${mode}-$(date +%s)"

case "$mode" in
  none)
    export REDIS_SAVE="" REDIS_APPENDONLY="no" REDIS_APPENDFSYNC="everysec"
    ;;
  rdb)
    export REDIS_SAVE="60 1" REDIS_APPENDONLY="no" REDIS_APPENDFSYNC="everysec"
    ;;
  aof-everysec)
    export REDIS_SAVE="" REDIS_APPENDONLY="yes" REDIS_APPENDFSYNC="everysec"
    ;;
  aof-always)
    export REDIS_SAVE="" REDIS_APPENDONLY="yes" REDIS_APPENDFSYNC="always"
    ;;
  *)
    echo "unknown mode: $mode (expected none|rdb|aof-everysec|aof-always)" >&2
    exit 1
    ;;
esac

echo "== Mode: ${mode} - recreating redis fresh with REDIS_SAVE='${REDIS_SAVE}' REDIS_APPENDONLY=${REDIS_APPENDONLY} REDIS_APPENDFSYNC=${REDIS_APPENDFSYNC} =="
(cd "$compose_directory" && docker compose up -d --force-recreate --wait redis)
redis_container=$(cd "$compose_directory" && docker compose ps -q redis)
docker exec "$redis_container" redis-cli CONFIG GET appendonly
docker exec "$redis_container" redis-cli CONFIG GET save
docker exec "$redis_container" redis-cli CONFIG GET appendfsync

echo "== Creating ${cart_count} carts directly in Redis (run ${run_id}) =="
create_one() {
  local i=$1
  local cart_id="cart:${run_id}-${i}"
  local payload
  payload="{\"sku\":\"${sku}\",\"quantity\":1,\"unitPrice\":199.90,\"currency\":\"BRL\",\"productName\":\"durability test item\",\"addedAt\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"}"
  docker exec "$redis_container" redis-cli HSET "$cart_id" "$sku" "$payload" __version 1 >/dev/null
  docker exec "$redis_container" redis-cli EXPIRE "$cart_id" "$ttl_seconds" >/dev/null
  echo "${cart_id} created"
}
export -f create_one
export redis_container sku run_id ttl_seconds

results_file=$(mktemp)
trap 'rm -f "$results_file"' EXIT
seq 1 "$cart_count" | xargs -P 20 -I{} bash -c 'create_one {}' > "$results_file"

created=$(grep -c ' created$' "$results_file" || true)
echo "Created ${created}/${cart_count} carts successfully."

echo "== Killing redis (SIGKILL, no graceful save) =="
kill_start=$(date +%s.%N)
docker kill -s SIGKILL "$redis_container" >/dev/null

echo "Waiting for redis to come back healthy..."
started_manually=false
for i in $(seq 1 60); do
  if docker exec "$redis_container" redis-cli ping >/dev/null 2>&1; then
    break
  fi
  if [[ $i -eq 10 && "$started_manually" == "false" ]]; then
    docker start "$redis_container" >/dev/null 2>&1 || true
    started_manually=true
  fi
  sleep 0.5
done
kill_end=$(date +%s.%N)
recovery_seconds=$(echo "$kill_end - $kill_start" | bc)
echo "Redis back up after ${recovery_seconds}s."

echo "== Re-checking all ${cart_count} carts =="
check_one() {
  local i=$1
  local cart_id="cart:${run_id}-${i}"
  local value
  value=$(docker exec "$redis_container" redis-cli HGET "$cart_id" "$sku" 2>/dev/null || true)
  if [[ -n "$value" ]]; then
    echo "survived"
  else
    echo "lost"
  fi
}
export -f check_one

survived=$(seq 1 "$cart_count" | xargs -P 20 -I{} bash -c 'check_one {}' | grep -c '^survived$' || true)
lost=$((cart_count - survived))
loss_pct=$(echo "scale=1; 100 * $lost / $cart_count" | bc)

echo ""
echo "=== RESULT (mode=${mode}) ==="
echo "created:        ${created}/${cart_count}"
echo "survived kill:  ${survived}/${cart_count}"
echo "lost:           ${lost}/${cart_count} (${loss_pct}%)"
echo "recovery time:  ${recovery_seconds}s"
