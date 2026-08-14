# Infrastructure as Code (Milestone 28)

Reproduces this lab's **host + cluster add-on layer** from code: Docker, K3s, and every Helm-based cluster add-on (Sealed Secrets, Argo Rollouts, Argo CD, KEDA, Linkerd + CNI, Kyverno, CloudNativePG), plus the Kyverno/Linkerd cluster policies under `kubernetes/cluster-policies/`.

Deliberately stops there. Application manifests (`kubernetes/base`, `kubernetes/overlays/local`) are Argo CD's job (Milestone 15), not this playbook's - running this against a fresh host still needs `scripts/infra/k3s-deploy.sh` and the Argo CD bootstrap `Application` afterward, per the main README's "Deploy the applications to K3s" section.

## Run

```bash
cd iac/ansible
pip install --user ansible-core kubernetes
ansible-galaxy collection install kubernetes.core community.docker

# Dry run against the live host - shows what would change without changing anything
ansible-playbook -i inventory.ini site.yml --check --diff

# Apply for real
ansible-playbook -i inventory.ini site.yml
```

## What's intentionally not automated here

- **Docker and K3s installation themselves need root.** Every other role in this repo runs as the regular user, matching every other script in this project's no-sudo convention (see Milestone 17's `kubeseal` install to `~/.local/bin` for the precedent). Those two roles check first and are no-ops on a host that already has them - which is every host this playbook has actually been run against so far. They're written for a genuinely fresh host, not exercised end-to-end here.
- **Compose infrastructure** (`compose/compose.yaml`) - already declarative, `docker compose up` is already the reproducible command.
- **Secrets** - `compose/.env`, the sealed-secrets private key, and every credential this lab generates locally are deliberately outside version control and outside this playbook. Provisioning them is `scripts/infra/k3s-deploy.sh`'s job (streams the Orders DB connection string into a K8s Secret without printing or storing it) and each milestone's own setup script (`scripts/infra/keycloak-configure-realm.sh`, `scripts/infra/postgres-ha-provision.sh`).
