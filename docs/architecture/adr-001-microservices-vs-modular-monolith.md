# ADR 001: Keep the Microservice Boundary

## Status

Accepted, 2026-08-16.

## Context

`docs/architecture/implementation-roadmap.md`'s Phase 2 asks for an explicit
decision: does the current deployment-independence and scaling need justify
seven separate services (Cart, Catalog, Inventory, Orders.Api, Orders.Worker,
Payments, Storefront), or would a modular monolith be the more economical
near-term choice, keeping the module boundaries extraction-friendly for later?

The honest answer requires separating two different justifications for a
microservice architecture, because only one of them applies here:

1. **Independent deployment/scaling under real, differentiated load** - the
   usual production justification. This repository does not have that: it is
   a personal lab with no production traffic profile to scale against.
2. **A deliberate vehicle for learning and demonstrating distributed-systems
   engineering** - sagas, the transactional outbox/inbox, anti-entropy
   reconciliation, per-SKU advisory locking, Kafka partitioning strategy,
   leader election, chaos engineering, GitOps promotion. Every one of the 99+
   milestones this repository has accumulated is either building one of these
   mechanisms or hardening it under a failure mode a monolith would not
   expose the same way (a monolith with one database has no outbox/inbox gap
   to close, no cross-service anti-entropy divergence to detect, no saga
   compensation path to test).

Justification 1 does not hold. Justification 2 is the repository's actual,
stated purpose - the README, every milestone doc, and the audit series all
frame this as a distributed-systems engineering exercise, not a production
e-commerce platform being built for real scale.

## Decision

**Keep the seven-service boundary.** Collapsing it into a modular monolith
would remove the exact mechanisms - sagas, outbox/inbox, anti-entropy,
partition-aware Kafka consumption, cross-service resilience pipelines - that
the majority of this repository's engineering investment exists to build and
exercise. The cost this decision accepts (more moving parts, more
operational surface, more YAML) is the cost of keeping the thing the
repository is actually for.

This does not mean every service boundary drawn to date is correct forever -
finding 11 in `audit-2026-08-15-architecture-and-cross-cutting-review.md`
(orders-worker running eight independent workloads - saga orchestrator, two
sweepers, projection processor, anti-entropy - inside one process/lifecycle)
remains open and is a legitimate candidate for splitting further, not for
merging back. The boundary between "how many deployable units" and "how many
logical roles run inside one of them" are separate questions; this ADR
answers only the first.

## Consequences

- No migration to a modular monolith is undertaken. Existing service
  boundaries, `Services.ArchitectureTests`, and each service's independent
  Dockerfile/deployment pipeline stand as-is.
- Roadmap Phase 2's remaining items (order-transition centralization, gRPC
  money representation, loyalty/backorder/risk-query closure, architecture
  fitness tests) are evaluated on their own merits against the current
  seven-service topology - see `audit-rebaseline-2026-08-16.md`, which found
  all four already satisfied by prior work.
- Future proposals to add an eighth service, or to split `orders-worker`
  (finding 11), should be justified the same way this ADR was: what
  distributed-systems property does the new boundary let the repository
  build or exercise that the current one cannot.
