# Environment overlays

`local/` is the development-only exception that references `:local` images
already imported into K3s containerd. It is never a production promotion path.

Staging and production overlays are created or updated only by
`.github/workflows/promote.yml`. The workflow accepts a full commit SHA,
resolves the seven immutable registry digests, verifies each keyless signature,
renders the overlay, rejects surviving mutable tags and opens a pull request.
The same digests move between environments; no environment rebuild occurs.

Environment protection rules should require review for `production`. Argo CD
must be configured to reconcile the corresponding overlay only after its
promotion pull request merges. Rollback is a Git revert to the previous set of
known-good digests, subject to database/contract compatibility in
`docs/operations/runbooks.md`.

After the staging PR is merged and reconciled, run `staging-smoke.yml` on the
self-hosted lab runner. It verifies that every ready application pod reports
the expected registry digest and then exercises the correlated order flow. A
production promotion must provide that successful run ID; `promote.yml`
downloads its retained evidence and rejects a different revision or failed
result. Cluster credentials, Argo destinations and GitHub environment approval
rules are intentionally provisioned outside the repository.
