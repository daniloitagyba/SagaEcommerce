#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
compose_directory="$project_directory/compose"

orders_url=${ORDERS_URL:-http://127.0.0.1:8088}
coupon_code=${COUPON_CODE:-RACE$RANDOM}
redemption_limit=${REDEMPTION_LIMIT:-5}
concurrent_checkouts=${CONCURRENT_CHECKOUTS:-40}
sku=${SKU:-SKU-BOOK-002}

psql() {
  docker compose -f "$compose_directory/compose.yaml" exec -T postgres \
    psql -U orders -d orders -tAc "$1"
}

echo "== Seeding coupon ${coupon_code}: ${redemption_limit} total redemptions, no per-customer limit =="
psql "
INSERT INTO coupons (code, description, percentage, valid_from, valid_until,
                     minimum_order_amount, max_total_redemptions, max_per_customer, redemption_count)
VALUES ('${coupon_code}', 'concurrency test', 10.00, NOW() - INTERVAL '1 hour', NOW() + INTERVAL '1 hour',
        0.00, ${redemption_limit}, NULL, 0)
ON CONFLICT (code) DO UPDATE SET redemption_count = 0, max_total_redemptions = ${redemption_limit};
" > /dev/null

echo "== Firing ${concurrent_checkouts} concurrent checkouts at a coupon with ${redemption_limit} slots =="
token=$("$script_directory/../infra/keycloak-get-token.sh")
status_file=$(mktemp)
trap 'rm -f "$status_file"' EXIT

for i in $(seq 1 "$concurrent_checkouts"); do
  (
    status=$(curl -s -o /dev/null -w '%{http_code}' \
      --request POST "${orders_url}/orders" \
      --header "Authorization: Bearer ${token}" \
      --header 'Content-Type: application/json' \
      --data "{\"customerId\":\"race-customer-${i}\",\"items\":[{\"sku\":\"${sku}\",\"quantity\":1}],\"couponCode\":\"${coupon_code}\"}")
    printf '%s\n' "$status" >> "$status_file"
  ) &
done
wait

created=$(grep -c '^201$' "$status_file" || true)
conflict=$(grep -c '^409$' "$status_file" || true)
other=$(grep -vcE '^(201|409)$' "$status_file" || true)

echo
echo "== Results =="
echo "  201 Created (slot claimed): ${created}"
echo "  409 Conflict (lost the race): ${conflict}"
echo "  anything else:               ${other}"
if [ "$other" -ne 0 ]; then
  echo "  breakdown of everything else:"
  grep -vE '^(201|409)$' "$status_file" | sort | uniq -c | sed 's/^/    /'
fi

reserved=$(psql "SELECT COUNT(*) FROM coupon_redemptions WHERE code = '${coupon_code}' AND state <> 'Released';")
counter=$(psql "SELECT redemption_count FROM coupons WHERE code = '${coupon_code}';")

echo "  coupon_redemptions rows:     ${reserved}"
echo "  coupons.redemption_count:    ${counter}"
echo

failed=0

if [ "$created" -gt "$redemption_limit" ]; then
  echo "FAIL: ${created} checkouts succeeded against a limit of ${redemption_limit} - the coupon was over-redeemed."
  failed=1
fi

if [ "$reserved" -gt "$redemption_limit" ]; then
  echo "FAIL: ${reserved} redemption rows exist against a limit of ${redemption_limit}."
  failed=1
fi

if [ "$counter" != "$reserved" ]; then
  echo "FAIL: coupons.redemption_count (${counter}) disagrees with the redemption rows (${reserved})."
  failed=1
fi

if [ "$other" -ne 0 ]; then
  echo "FAIL: ${other} checkouts returned neither 201 nor 409 - see the statuses above."
  failed=1
fi

if [ "$created" -lt "$redemption_limit" ]; then
  echo "FAIL: only ${created} of ${redemption_limit} slots were claimed - the limit was never reached, so nothing was proven."
  failed=1
fi

if [ "$failed" -eq 0 ]; then
  echo "PASS: exactly ${created} of ${concurrent_checkouts} checkouts claimed the ${redemption_limit} available slots;"
  echo "      the remaining ${conflict} were rejected with 409 and the counter matches the rows."
fi

exit "$failed"
