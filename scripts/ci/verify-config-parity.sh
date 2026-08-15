#!/usr/bin/env bash
# Guards against the bug class this repo has hit five times:
# Redis__ConnectionString, four separate Kafka BootstrapServers sections,
# and Authentication__Authority twice (see
# docs/saga/milestone-75-saga-mode-both-by-default.md and
# docs/architecture/audit-2026-08-15-frontend-catalog-infra-review.md's
# finding #1) - a config value set in compose/compose.yaml's environment
# block for an app service, silently missing from the paired
# kubernetes/base/ manifest, that only surfaces as a crash loop (or, worse,
# a silent no-op) once someone actually deploys to Kubernetes. This diffs
# every app service/Job's env var *names* (not values - the values are
# legitimately different in each environment, e.g. `kafka:9092` vs
# `kafka.orders-lab.svc.cluster.local:9094`) between the two, and fails on
# anything present in Compose and missing from Kubernetes that isn't on
# the explicit allowlist below, with a documented reason for each entry.
set -euo pipefail

cd "$(dirname "$0")/../.."

if ! python3 -c "import yaml" >/dev/null 2>&1; then
  pip install --quiet pyyaml
fi

python3 - "$@" <<'PYEOF'
import sys
import yaml

with open("compose/compose.yaml") as f:
    compose = yaml.safe_load(f)

services = compose["services"]

# Every app service/Job in compose.yaml that has a real Kubernetes
# counterpart. Infra containers (postgres, kafka, redis, mongodb, keycloak,
# ...) aren't in this list - kubernetes/base/infrastructure-services.yaml
# only declares Services for them, not Deployments/env blocks, so there is
# nothing to diff against.
PAIRS = {
    "orders-api-1": "kubernetes/base/orders-api.yaml",
    "orders-worker": "kubernetes/base/orders-worker.yaml",
    "payments-service": "kubernetes/base/payments-service.yaml",
    "catalog-service": "kubernetes/base/catalog-service.yaml",
    "inventory-service": "kubernetes/base/inventory-service.yaml",
    "cart-service": "kubernetes/base/cart-service.yaml",
    "storefront-service": "kubernetes/base/storefront-service.yaml",
    "migrations": "kubernetes/base/migration-job.yaml",
    "migrations-inventory": "kubernetes/base/inventory-migration-job.yaml",
    "migrations-payments": "kubernetes/base/payments-migration-job.yaml",
    "seed-catalog": "kubernetes/base/catalog-seed-job.yaml",
    "seed-inventory": "kubernetes/base/inventory-seed-job.yaml",
}

# key: (compose service, env var name) -> reason it's legitimately
# Compose-only. Every entry here was checked against the service's own
# Program.cs to confirm its absence in Kubernetes doesn't crash-loop or
# silently change behavior - an unexplained gap is a bug (see
# Redis__ConnectionString on seed-catalog, fixed alongside this script,
# not allowlisted); an explained one is a deliberate environment
# difference. Add to this list only with the same level of justification.
ALLOWLIST = {
    ("orders-api-1", "Authentication__AlternateAuthority"): (
        "Compose-only LAN/phone-access convenience (README's 'LAN / phone "
        "access' section, PUBLIC_KEYCLOAK_URL) for a shopper's browser "
        "reaching Keycloak by a different address than the service does. "
        "The K3s deployment doesn't have this split-horizon DNS problem."
    ),
    ("payments-service", "Authentication__AlternateAuthority"): (
        "See orders-api-1/Authentication__AlternateAuthority above."
    ),
    ("catalog-service", "Authentication__AlternateAuthority"): (
        "See orders-api-1/Authentication__AlternateAuthority above."
    ),
    ("inventory-service", "Authentication__AlternateAuthority"): (
        "See orders-api-1/Authentication__AlternateAuthority above."
    ),
    ("cart-service", "Authentication__AlternateAuthority"): (
        "See orders-api-1/Authentication__AlternateAuthority above."
    ),
    ("orders-worker", "LeaderElection__Mode"): (
        "Deliberately one-sided: LeaderElectionMode's own default is "
        "Kubernetes (the real deployment target), so the Kubernetes "
        "manifest sets nothing and takes that default. Compose overrides "
        "it to SingleNode because there is no cluster there to hold a "
        "Lease against, and orders-worker always runs at replicas: 1 in "
        "Compose anyway."
    ),
    ("payments-service", "PaymentSettlement__ExpirySweepIntervalSeconds"): (
        "Compose sets this to 30, identical to PaymentSettlementOptions' "
        "own code default - present there only for the same "
        "explicit-even-when-redundant documentation style Outbox__* "
        "options use elsewhere, not because Kubernetes taking the "
        "default would behave differently."
    ),
    ("migrations-inventory", "Kafka__BootstrapServers"): (
        "InventoryKafkaOptions.ValidateOnStart only checks the value is "
        "non-empty, and its own default (localhost:9092) satisfies that - "
        "so this doesn't crash-loop the Job. Unlike RedisOptions' "
        "ConnectionMultiplexer.Connect(...) (a blocking call, eagerly "
        "invoked by this service's ValidateOnBuild:true), the Kafka "
        "producer/admin client factories Inventory.Service/Program.cs "
        "registers don't dial the broker synchronously at construction, "
        "and the Job's --migrate branch returns before app.RunAsync() "
        "ever starts the IHostedService Kafka consumers that would."
    ),
    ("seed-inventory", "Kafka__BootstrapServers"): (
        "See migrations-inventory/Kafka__BootstrapServers above - same "
        "Program.cs, same --seed-returns-before-RunAsync shape."
    ),
}


def k8s_env_names(path):
    names = set()
    with open(path) as f:
        for doc in yaml.safe_load_all(f):
            if not doc:
                continue
            containers = (
                doc.get("spec", {})
                .get("template", {})
                .get("spec", {})
                .get("containers", [])
            )
            for container in containers:
                for entry in container.get("env", []) or []:
                    names.add(entry["name"])
    return names


failures = []
for compose_service, k8s_path in PAIRS.items():
    compose_env = services[compose_service].get("environment", {}) or {}
    k8s_keys = k8s_env_names(k8s_path)

    for key in sorted(compose_env.keys()):
        if key in k8s_keys:
            continue
        reason = ALLOWLIST.get((compose_service, key))
        if reason is not None:
            continue
        failures.append((compose_service, k8s_path, key))

if failures:
    print("verify-config-parity: compose.yaml sets env vars kubernetes/base/ does not:")
    print()
    for compose_service, k8s_path, key in failures:
        print(f"  {compose_service} sets {key!r}, missing from {k8s_path}")
    print()
    print(
        "Either add the missing env var to the Kubernetes manifest, or add "
        "an entry to ALLOWLIST in scripts/ci/verify-config-parity.sh "
        "explaining why the Kubernetes side is correct to omit it - the "
        "same way every other entry in that list is justified against the "
        "service's own Program.cs, not just asserted."
    )
    sys.exit(1)

print("verify-config-parity: OK - every compose.yaml app-service env var is either present in kubernetes/base/ or explicitly allowlisted")
PYEOF
