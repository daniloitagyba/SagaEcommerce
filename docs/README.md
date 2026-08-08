# Docs index

One dated report per milestone, plus a handful of standalone reference docs, organized by
topic. Every report follows the same shape: what was built, what actually broke while
validating it against a live deployment, and what the measurement showed — not just the
intended design. See the root [`README.md`](../README.md) for the system overview.

## architecture

- [Milestone 18: Clean/Hexagonal Architecture Refactor](architecture/milestone-18-clean-architecture.md)
- [Milestone 36: Kubernetes Lease Leader Election](architecture/milestone-36-leader-election.md)
- [Milestone 38: Cluster-Wide Distributed Rate Limiting](architecture/milestone-38-distributed-rate-limiting.md)
- [Milestone 39: Hedged Requests for GET /orders/{id}](architecture/milestone-39-hedged-requests.md)
- [Milestone 40: Catalog Service Backed by MongoDB](architecture/milestone-40-catalog-mongodb.md)
- [Milestone 41: Inventory Service with Kafka-Partitioned Stock Reservation](architecture/milestone-41-inventory-kafka-partitioning.md)
- [Milestone 42: Cart Service with Redis as the System of Record](architecture/milestone-42-cart-redis-primary-store.md)
- [Milestone 44: Bestsellers Projection via Redis Sorted Sets](architecture/milestone-44-bestsellers-redis-sorted-sets.md)
- [Milestone 45: Storefront UI as a Backend-for-Frontend](architecture/milestone-45-storefront-bff.md)
- [Milestone 49: MongoDB Replica Set - Stale Reads and the w:majority Cost, Measured](architecture/milestone-49-mongodb-replica-set.md)
- [Milestone 51: Rebalance vs the Per-SKU Serialization Guarantee](architecture/milestone-51-rebalance-vs-sku-serialization.md)
- [Milestone 57: Jepsen/Elle-Style Linearizability Check of Inventory](architecture/milestone-57-inventory-linearizability.md)
- [Milestone 58: Deterministic Simulation Testing of the Saga](architecture/milestone-58-deterministic-simulation.md)
- [Milestone 60: DDD Architecture Fitness Functions](architecture/milestone-60-ddd-architecture-fitness-functions.md)
- [Milestone 61: Domain-Boundary Guardrails Across Every Service](architecture/milestone-61-service-domain-boundaries.md)
- [Continuous Profiling with Grafana Pyroscope](architecture/continuous-profiling.md) — reference doc, not tied to one milestone
- [Feature Flags with Microsoft.FeatureManagement](architecture/feature-flags.md) — reference doc
- [Idempotency-Key for POST /orders](architecture/idempotency-key.md) — reference doc

## saga

- [Milestone 12: Payments Service and Choreographed Saga](saga/milestone-12-payments-saga.md)
- [Milestone 22: Orchestrated Saga vs Choreographed Saga](saga/milestone-22-orchestration-vs-choreography.md)
- [Milestone 43: Extending the Orchestrated Saga to 4 Steps with Compensation](saga/milestone-43-saga-compensation.md)
- [Milestone 56: TLA+ Formal Verification of the 4-Step Saga](saga/milestone-56-tla-plus-saga-verification.md)
- [Milestone 75: Saga:Mode=Both Is the Default Now, Not Choreography](saga/milestone-75-saga-mode-both-by-default.md)
- [Milestone 77: Inventory Timeout Compensation Was the One Cancelled Order That Never Released Its Stock](saga/milestone-77-inventory-timeout-compensation.md)

## domain

- [Milestone 66: Real Line Items, a Pricing Rules Engine, and Scored Payment Risk](domain/milestone-66-line-items-pricing-and-risk.md)
- [Milestone 67: Coupons That Can Actually Run Out](domain/milestone-67-coupon-lifecycle.md)
- [Milestone 68: Authorize, Then Capture](domain/milestone-68-authorize-capture.md)
- [Milestone 69: The Order's Life Does Not End at Confirmed](domain/milestone-69-order-lifecycle.md)
- [Milestone 70: Returns, Partial Refunds, and a Money Bug in Shipped Code](domain/milestone-70-returns-and-refunds.md)
- [Milestone 71: The Customer Stops Being a String](domain/milestone-71-customers-tiers-and-geography.md)
- [Milestone 72: Stock Lives in Buildings](domain/milestone-72-multi-warehouse-allocation.md)
- [Milestone 73: Closing the Gaps the Plan Left Open](domain/milestone-73-closing-the-plan-gaps.md)
- [Milestone 74: Waiting Is a State, Not a Cancellation](domain/milestone-74-backorders.md)
- [Milestone 76: A Capture That Fails Is Now Visible, Not Silent](domain/milestone-76-settlement-reconciliation.md)

## cqrs

- [Milestone 13: CQRS Read Projections](cqrs/milestone-13-read-projections.md)
- [Milestone 55: Full Read-Model Replay/Reconstruction Drill](cqrs/milestone-55-replay-reconstruction.md)

## data

- [Milestone 20: Zero-Downtime Expand/Contract Schema Migration](data/milestone-20-expand-contract-migration.md)
- [Milestone 23: Event Sourcing for the Order Aggregate](data/milestone-23-event-sourcing.md)
- [Milestone 46: Redis Durability for Cart.Service, Measured Not Assumed](data/milestone-46-redis-durability.md)
- [Milestone 53: Read-Your-Writes Across Postgres Read Replicas](data/milestone-53-read-your-writes.md)
- [Milestone 63: Outbox/Inbox Retention](data/milestone-63-outbox-inbox-retention.md)

## data-platform

- [Milestone 27: Data Layer HA, Backup, and Restore Drill](data-platform/milestone-27-postgres-ha.md)

## messaging

- [Milestone 19: Schema Registry + Contract Evolution](messaging/milestone-19-schema-registry.md)
- [Milestone 21: CDC with Debezium](messaging/milestone-21-debezium-cdc.md)
- [Milestone 47: Acks.All Is a Lie Without Quorum - Proven Against a Real 3-Broker Cluster](messaging/milestone-47-kafka-quorum.md)
- [Milestone 52: Kafka Transactions (EOS) Alongside Outbox/Inbox](messaging/milestone-52-kafka-transactions.md)
- [Milestone 62: DLQ Redrive, and Why It's Harder Than It Looks](messaging/milestone-62-dlq-redrive.md)

## resilience

- [Milestone 8: Autoscaling and Resilience](resilience/milestone-8-autoscaling-resilience.md)
- [Milestone 10: Resilience and Chaos Engineering](resilience/milestone-10-chaos-resilience.md)
- [Milestone 31: Chaos Mesh Game Day](resilience/milestone-31-chaos-mesh-gameday.md)
- [Milestone 37: Network Partition Game Day (Chaos Mesh NetworkChaos)](resilience/milestone-37-network-partition-gameday.md)
- [Milestone 48: Fencing Tokens for Redis Distributed Locks](resilience/milestone-48-fencing-tokens.md)
- [Milestone 50: Clock Skew via Chaos Mesh TimeChaos](resilience/milestone-50-clock-skew.md)
- [Milestone 65: Topology Spread Constraints](resilience/milestone-65-topology-spread.md)

## load-shedding

- [Milestone 11: Rate Limiting and Load Shedding](load-shedding/milestone-11-load-shedding.md)

## performance

- [Milestone 7: Performance Baseline](performance/milestone-7-baseline.md)
- [Milestone 54: Tail-Latency Amplification in a Real BFF Fan-Out](performance/milestone-54-backpressure-tail-latency.md)
- [Milestone 64: BFF Partial-Failure Degradation](performance/milestone-64-bff-partial-failure-degradation.md)

## caching

- [Milestone 9: Redis Cache](caching/milestone-9-cache.md)

## scaling

- [Milestone 14: Kafka Partitioning + KEDA Autoscaling](scaling/milestone-14-partitioning-keda.md)

## gitops

- [Milestone 15: GitOps + Progressive Delivery](gitops/milestone-15-gitops-progressive-delivery.md)
- [Milestone 17: Sealed Secrets for GitOps](gitops/milestone-17-sealed-secrets.md)
- [Milestone 24: Service Mesh (Linkerd) on K3s](gitops/milestone-24-service-mesh.md)

## iac

- [Milestone 28: Infrastructure as Code](iac/milestone-28-infrastructure-as-code.md)

## security

- [Milestone 26: AuthN/AuthZ and Zero-Trust](security/milestone-26-authn-authz.md)

## slo

- [Milestone 16: SLOs + Multi-Window, Multi-Burn-Rate Alerting](slo/milestone-16-slo-burn-rate-alerting.md)

## cicd

- [Milestone 25: CI Pipeline and Supply Chain Security](cicd/milestone-25-ci-pipeline.md)
- [Milestone 29: Contract Testing with Pact](cicd/milestone-29-contract-testing.md)
- [Milestone 30: gRPC and Mesh Traffic Shadowing](cicd/milestone-30-grpc-and-mesh-load-balancing.md)
- [Milestone 59: Quality and Security Guardrails](cicd/milestone-59-quality-security-guardrails.md)
