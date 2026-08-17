#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/../.." && pwd)
compose_directory="$project_directory/compose"
overlay_directory="$project_directory/kubernetes/overlays/local"
namespace=orders-lab

for command_name in docker kubectl; do
  command -v "$command_name" >/dev/null
done

(cd "$compose_directory" && docker compose up --detach --wait)

"$script_directory/init-payments-db.sh"

kubectl apply --filename "$project_directory/kubernetes/base/namespace.yaml"

kubectl delete job orders-migrations-m7 payments-migrations-m12 \
  catalog-seed-m40 inventory-migrations-m41 inventory-seed-m41 \
  --namespace "$namespace" --ignore-not-found

kubectl apply --kustomize "$overlay_directory"

for _ in $(seq 1 30); do
  kubectl get secret orders-runtime --namespace "$namespace" >/dev/null 2>&1 && break
  sleep 2
done
kubectl get secret orders-runtime --namespace "$namespace" >/dev/null

kubectl rollout restart \
  deployment/orders-worker deployment/payments-service \
  deployment/catalog-service deployment/inventory-service \
  deployment/cart-service deployment/storefront-service \
  --namespace "$namespace"
kubectl patch rollout/orders-api --namespace "$namespace" --type merge \
  --patch "{\"spec\":{\"restartAt\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\"}}"

kubectl wait \
  --namespace "$namespace" \
  --for=condition=complete \
  job/compose-connectivity-m6 \
  --timeout=120s
kubectl wait \
  --namespace "$namespace" \
  --for=condition=complete \
  job/orders-migrations-m7 \
  --timeout=180s
kubectl wait \
  --namespace "$namespace" \
  --for=condition=complete \
  job/payments-migrations-m12 \
  --timeout=180s
kubectl wait \
  --namespace "$namespace" \
  --for=condition=complete \
  job/inventory-migrations-m41 \
  --timeout=180s
kubectl wait \
  --namespace "$namespace" \
  --for=condition=complete \
  job/inventory-seed-m41 \
  --timeout=180s
kubectl wait \
  --namespace "$namespace" \
  --for=condition=complete \
  job/catalog-seed-m40 \
  --timeout=180s
for _ in $(seq 1 90); do
  desired=$(kubectl get rollout orders-api --namespace "$namespace" --output jsonpath='{.spec.replicas}' 2>/dev/null)
  available=$(kubectl get rollout orders-api --namespace "$namespace" --output jsonpath='{.status.availableReplicas}' 2>/dev/null)
  if [[ -n "$desired" && -n "$available" && "$available" -ge "$desired" ]]; then
    break
  fi
  sleep 2
done
kubectl rollout status \
  --namespace "$namespace" \
  deployment/orders-worker \
  --timeout=180s
kubectl rollout status \
  --namespace "$namespace" \
  deployment/payments-service \
  --timeout=180s
kubectl rollout status \
  --namespace "$namespace" \
  deployment/catalog-service \
  --timeout=180s
kubectl rollout status \
  --namespace "$namespace" \
  deployment/inventory-service \
  --timeout=180s
kubectl rollout status \
  --namespace "$namespace" \
  deployment/cart-service \
  --timeout=180s
kubectl rollout status \
  --namespace "$namespace" \
  deployment/storefront-service \
  --timeout=180s

cd "$compose_directory"
docker compose --profile compose-apps stop nginx orders-api-1 orders-api-2 orders-worker payments-service \
  catalog-service inventory-service cart-service storefront-service

kubectl get pods --namespace "$namespace" --output wide
kubectl get services,endpointslices --namespace "$namespace"
