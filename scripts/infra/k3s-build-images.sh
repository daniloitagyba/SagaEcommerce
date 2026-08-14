#!/usr/bin/env bash
set -euo pipefail

script_directory=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
project_directory=$(cd -- "$script_directory/.." && pwd)
compose_directory="$project_directory/compose"

# Must match kubernetes/overlays/local/kustomization.yaml's `newTag` for
# every image - the K3s Deployments/Rollout use imagePullPolicy: Never, so
# unlike Compose (which has its own `build:` block and self-heals on
# `docker compose up`), a tag mismatch here means the pod finds nothing
# locally and sits in ImagePullBackOff.
local_tag=local
services=(orders-api orders-worker payments-service catalog-service inventory-service cart-service storefront-service)

for command_name in docker jq; do
  command -v "$command_name" >/dev/null
done

cd "$compose_directory"
docker compose --profile compose-apps build "${services[@]}"

# Each service's compose-declared image tag drifts independently (whatever
# milestone last touched that Dockerfile - e.g. milestone-41-inventory,
# milestone-42-by-sku), so it's read back from `compose config` rather than
# assumed, and retagged to the one fixed tag the K3s overlay expects.
built_images=$(docker compose --profile compose-apps config --format json |
  jq -r '.services | to_entries[] | select(.value.image != null and (.value.image | startswith("saga-ecommerce/"))) | "\(.key) \(.value.image)"')

images=()
for service in "${services[@]}"; do
  compose_image=$(echo "$built_images" | awk -v s="$service" '$1 == s {print $2}')
  if [[ -z "$compose_image" ]]; then
    echo "No built image found for service '$service' in compose config" >&2
    exit 1
  fi
  docker image tag "$compose_image" "saga-ecommerce/${service}:${local_tag}"
  images+=("saga-ecommerce/${service}:${local_tag}")
done

docker image inspect "${images[@]}" >/dev/null

printf 'Importing images into the K3s containerd image store\n'
docker save "${images[@]}" |
  docker run --rm --interactive \
    --network none \
    --entrypoint /bin/ctr \
    --volume /run/k3s/containerd/containerd.sock:/run/k3s/containerd/containerd.sock \
    rancher/k3s:v1.36.2-k3s1 \
    --address /run/k3s/containerd/containerd.sock \
    --namespace k8s.io images import -

docker run --rm \
  --network none \
  --entrypoint /bin/ctr \
  --volume /run/k3s/containerd/containerd.sock:/run/k3s/containerd/containerd.sock \
  rancher/k3s:v1.36.2-k3s1 \
  --address /run/k3s/containerd/containerd.sock \
  --namespace k8s.io images list --quiet |
  grep --extended-regexp "saga-ecommerce/($(IFS='|'; echo "${services[*]}")):${local_tag}"
