# Milestone 25 CI Pipeline and Supply Chain Security

## Scope

Every prior milestone in this lab has CD (Argo CD reconciling the cluster from `main`) but no CI at all - nothing stopped a broken commit from reaching `main` except the canary's `AnalysisTemplate` catching it *in production*. This milestone adds the missing front half: GitHub Actions gates every push on `dotnet test`, then builds, scans, SBOMs, and signs each service's image before it's usable anywhere - and a Kyverno policy on the cluster that refuses to admit a pod referencing an unsigned image from this project's own registry namespace.

## Design

- **`test` job**: restores, builds, and runs the full suite (24 unit + 7 integration) on GitHub-hosted `ubuntu-latest` runners, which have Docker available natively - the same Testcontainers-based integration tests that run on the lab server run unmodified here.
- **`build-and-push` job** (matrix over the three services, `needs: test`, only on push to `main`): builds each image, pushes it to `ghcr.io/daniloitagyba/saga-ecommerce/<service>`, generates an SPDX SBOM (`anchore/sbom-action`), scans the built image for CRITICAL/HIGH vulnerabilities with Trivy (fails the job on a match), and signs the image **keylessly** with cosign - no signing key to generate, rotate, or leak; the workflow's own GitHub Actions OIDC identity *is* the signing identity, verified against Sigstore's public Fulcio CA and logged to the public Rekor transparency log.
- **Kyverno `ClusterPolicy`** (`kubernetes/cluster-policies/verify-image-signatures.yaml`, applied imperatively - cluster infrastructure, not an Argo CD-managed application manifest, the same category as Linkerd and Sealed Secrets): requires any pod referencing `ghcr.io/daniloitagyba/saga-ecommerce/*` to carry a cosign signature from the **exact** identity `https://github.com/daniloitagyba/SagaEcommerce/.github/workflows/ci.yml@refs/heads/main` - not "signed by anyone," a signature from a fork, a different workflow, or a feature branch is rejected just as surely as no signature at all.

## What didn't work

**`aquasecurity/trivy-action@0.28.0` doesn't exist - the tag needed a leading `v`, and a newer version existed anyway.** First CI run failed instantly at "Set up job" for all three matrix jobs: `Unable to resolve action 'aquasecurity/trivy-action@0.28.0', unable to find version '0.28.0'`. Checked the repo's actual tags directly (`gh api repos/aquasecurity/trivy-action/tags`) rather than guessing again - fixed to `v0.36.0`, and cross-checked every other pinned action (`docker/build-push-action@v6`, `anchore/sbom-action@v0`, `sigstore/cosign-installer@v3`, etc.) against real tags in the same pass instead of finding them one failure at a time.

**Milestone 19's documented gap - a test hardcoded to this lab's own home-server IP - broke the instant it ran anywhere else.** `PaymentMessageProcessorTests` pointed its `SchemaRegistryClient` directly at `172.30.0.16:8081`, the lab's always-on Karapace instance, noted at the time as "a deliberate departure from the Testcontainers-hermetic pattern." That departure was fine on a single personal server; it meant three tests failed after a consistent 30-second HTTP timeout on GitHub's runners, which obviously have no route to a home LAN address. Fixed properly rather than special-cased for CI: **Redpanda** ships a Confluent-compatible schema registry in the same single container as its Kafka-API broker, and `Testcontainers.Redpanda` (matching the version already used for `Testcontainers.PostgreSql`/`Testcontainers.Redis` in this project) exposes both addresses directly - the test now gets a real, ephemeral, hermetic registry per run, closing a gap that had been open and documented since Milestone 19 rather than working around it a second time.

**GHCR packages default to private, and the current session's `gh` OAuth token has no `read:packages` scope to inspect or change that.** Verifying the pushed images required either a new credential or a visibility change - both are real, externally-visible actions (a long-lived pull credential; making previously-private images publicly downloadable), and the session's permission classifier correctly declined to let either happen without explicit confirmation. Given the choice, the private option was chosen - more realistic, and consistent with this project's existing Sealed Secrets pattern for exactly this kind of credential. **Generating and handing over that PAT was deferred by request**, so wiring the live K3s deployments to pull this pipeline's own signed images is not yet done - the cluster's `orders-api`/`orders-worker`/`payments-service` still run the pre-existing, locally-imported images, completely unchanged by this milestone.

**Proving Kyverno's enforcement live, without that credential, needed a different signed target - and the first negative test was a false pass, not a real one.** With the real GHCR images unreachable for a live admission test, `ghcr.io/sigstore/cosign/cosign` was used instead - a foundational, definitely-real project that signs its own releases keylessly (confirmed directly: `cosign verify ghcr.io/sigstore/cosign/cosign:v2.4.1` against Google's OIDC issuer). The first attempt at a negative test created a pod with `alpine:latest` expecting it to be rejected - it was admitted instead, which looked like a broken policy but was actually a test-construction mistake: the demo policy's `imageReferences` glob (`ghcr.io/sigstore/cosign/cosign:*`) never matched `alpine:latest` at all, so `verifyImages` correctly never ran for it - nothing was actually being tested. Fixed by adding `alpine:*` to the glob so the negative case is genuinely in scope, then re-ran both cases for real (see Results).

## Results

Live CI run ([30412895960](https://github.com/daniloitagyba/local-distributed-lab/actions/runs/30412895960)), all green:

| Job | Duration |
| --- | --- |
| build and test (24 unit + 7 integration) | 1m26s |
| build, scan, sign - orders-api | 2m35s |
| build, scan, sign - orders-worker | 2m58s |
| build, scan, sign - payments-service | 2m36s |

Each image: pushed to GHCR, SBOM generated and uploaded as a workflow artifact, scanned clean of CRITICAL/HIGH vulnerabilities, and signed - verifiable independent of this repo's own tooling:

```
$ cosign verify ghcr.io/sigstore/cosign/cosign:v2.4.1 \
    --certificate-oidc-issuer https://accounts.google.com \
    --certificate-identity keyless@projectsigstore.iam.gserviceaccount.com
Verification for ghcr.io/sigstore/cosign/cosign:v2.4.1 --
The following checks were performed on each of these signatures:
  - The cosign claims were validated
  - Existence of the claims in the transparency log was verified offline
  - The code-signing certificate was verified using trusted certificate authority certificates
```

Kyverno admission enforcement, proven live with both outcomes on the same policy (see "what didn't work" for the corrected negative test):

```
$ kubectl run signed-ok --image=ghcr.io/sigstore/cosign/cosign:v2.4.1 ...
pod/signed-ok created

$ kubectl run unsigned-blocked --image=docker.io/library/alpine:latest ...
Error from server: admission webhook "mutate.kyverno.svc-fail" denied the request:
resource Pod/kyverno-demo/unsigned-blocked was blocked due to the following policies
verify-cosign-demo:
  verify-signed-cosign-release: 'failed to verify image docker.io/library/alpine:latest:
    .attestors[0].entries[0].keyless: no signatures found'
```

The admitted pod's image reference was also rewritten to the exact verified digest (`mutateDigest: true`) - `ghcr.io/sigstore/cosign/cosign:v2.4.1` became `...@sha256:b03690aa...`, so what actually runs is pinned to precisely what was verified, immune to the tag being retargeted afterward.

### Resource overhead

Kyverno's four controllers (admission, background, cleanup, reports): ~150Mi memory total, negligible CPU - measured after installation, before and after had no visible effect on cluster headroom.

### Regression check

`dotnet test`: 24 unit + 7 integration, all passing (including the now-hermetic `PaymentMessageProcessorTests`). `k3s-smoke-test.sh`: passes cleanly. A `payments-service` rollout restart was exercised specifically to confirm the new Kyverno policy has zero effect on the existing application pods - their image references don't match `ghcr.io/daniloitagyba/saga-ecommerce/*` at all, so `verifyImages` never applies to them.

## Running the experiment

```bash
# Trigger the pipeline
git push origin main
gh run watch --exit-status

# Inspect what it produced
cosign verify ghcr.io/daniloitagyba/saga-ecommerce/orders-api:latest \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-identity "https://github.com/daniloitagyba/SagaEcommerce/.github/workflows/ci.yml@refs/heads/main"
# (requires registry read access - see "what didn't work" above)

# Prove the cluster-side gate, without needing a private-registry credential
kubectl run should-work --image=ghcr.io/sigstore/cosign/cosign:v2.4.1 -- sleep 3600
kubectl run should-fail --image=docker.io/library/alpine:latest -- sleep 3600
kubectl get clusterpolicy verify-saga-ecommerce-image-signatures
```
