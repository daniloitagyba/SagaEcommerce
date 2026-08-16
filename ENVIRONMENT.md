# Local vs. remote environment

This repository is worked on from two different machines. Knowing which is which
avoids wasted time trying to run something that was never meant to run locally.

## Local (this sandbox)

- Has the .NET SDK - use it to `dotnet restore`/`dotnet build`/`dotnet test` for
  fast feedback: compilation, unit tests, and the architecture-fitness tests all
  run fine here without any external dependency.
- Does **not** run Docker for this project. Even if a local Docker daemon can be
  started, do not start it, and do not run `docker compose`, Testcontainers-backed
  integration tests, or anything else that expects Postgres/Kafka/Redis/Mongo
  containers here. That workload belongs on the remote/lab machine (see below) -
  starting it locally wastes time bringing up a daemon and pulling images for a
  run that has to happen elsewhere anyway.
- Practically: run the `*.UnitTests` and `*.ArchitectureTests` projects locally;
  leave `*.IntegrationTests` (anything that spins up a `Testcontainers.*`
  container) and `compose/` for the remote machine.

## Remote (the lab server)

- This is where Docker, Docker Compose, and every Testcontainers-backed
  integration test actually run - see `docs/saga/milestone-75-saga-mode-both-by-default.md`
  for an example of a milestone validated there ("Full solution, real
  Testcontainers, on the lab server").
- Live-deployment validation (the k6 load tests, Toxiproxy chaos runs, the
  Kubernetes/K3s milestones, anything under `docs/` claiming to have run against
  "a live deployment") happens there, not in this sandbox.

## What this means for a change made here

A change built and unit-tested locally is not yet proven against a real
Postgres/Kafka/Redis - say so plainly when reporting on work done in this
sandbox, rather than implying integration-level or live validation happened.
