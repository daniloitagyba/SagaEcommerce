# ADR 002: Retire the Unconsumed OrderQuery gRPC Surface

## Status

Accepted, 2026-08-17.

## Context

`Orders.Api` exposed `OrderQuery.GetOrder` on a dedicated HTTP/2 port. No
application, test client, or operational workflow consumes that endpoint. The
REST read endpoint already provides the same authorization and use case.

Keeping the unused transport expanded the public attack surface, image
dependencies, service ports, and Kubernetes configuration without a consumer
or ownership commitment.

## Decision

Remove the gRPC endpoint, protocol contract, package dependency, listener, and
Kubernetes service port. Keep REST as the sole public order-query transport.

## Consequences

- Orders.Api no longer exposes port 8081.
- A future gRPC proposal must identify an owner, a consumer, and a transport
  requirement that REST does not satisfy.
- The Milestone 30 document remains a historical record of the mesh experiment.
