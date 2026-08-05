# Continuous Profiling with Grafana Pyroscope

## Scope

Tier 3 hardening item: this lab's OpenTelemetry stack (traces, metrics, logs) answers "what happened and how long did it take", but not "where exactly did the CPU time go inside this function". Continuous profiling closes that gap without duplicating the existing pipeline - it's a fourth, independent signal alongside traces/metrics/logs, not a replacement for any of them.

## Design

- **Grafana Pyroscope**, not `dotnet-monitor`: Pyroscope integrates directly with the Grafana instance this lab already runs (Tempo, Loki, Prometheus) via a native `grafana-pyroscope-datasource`, and Tempo's `tracesToProfiles` link lets a span's detail view jump straight to a flame graph - no separate profiling UI. `dotnet-monitor` is built for on-demand dumps/traces via its own HTTP API, has no continuous-profiling mode, and produces `.nettrace`/`.gcdump` artifacts that Grafana can't render natively.
- **Pyroscope server** runs as one more container in the docker-compose stack (`compose/compose.yaml`), alongside Tempo/Loki/Prometheus, bridged into K3s via the exact same fixed-IP `EndpointSlice` pattern already used for otel-collector/redis/postgres/etc. (`kubernetes/overlays/local/infrastructure-endpoints.yaml`, `172.30.0.19`). Gated behind the `profiling` Compose profile - only the K3s manifests actually instrument against it, so it stays down for the plain Compose quickstart: `docker compose --profile profiling up --detach --wait pyroscope`.
- **The .NET profiler is native, not managed**: `grafana/pyroscope-dotnet` is a native CLR profiler (a fork of Datadog's own profiler codebase, repurposed - confirmed directly from the repo's file layout: `Datadog.Trace.sln`, `Datadog.Profiler.Native.sln`, `pyroscope.fork.md`). The `.so` files aren't delivered by `dotnet restore`/`publish` - each service's Dockerfile fetches them directly from the `pyroscope-dotnet` GitHub release (`pyroscope.1.4.0-glibc-x86_64.tar.gz`) and sets the CLR profiler-attach environment variables (`CORECLR_ENABLE_PROFILING`, `CORECLR_PROFILER`, `CORECLR_PROFILER_PATH`, `LD_PRELOAD`). No code changes to `Program.cs` - activation is entirely environment-variable driven, gated behind `PYROSCOPE_PROFILING_ENABLED` at deploy time so it isn't unconditionally forced on.

## What didn't work (three real findings, in the order they were hit)

**1. The renamed-for-Pyroscope debug env vars produce zero log output, because the actual profiler engine still uses its original Datadog-lineage names internally.** `PYROSCOPE_LOG_LEVEL=debug` and `PYROSCOPE_LOG_STDERR=true` (documented, Pyroscope-branded env vars used by the project's own integration-test `Makefile`) produced nothing at all - no log lines, no log file, complete silence, even though the app booted normally. Reading `EnvironmentVariables.h` in the `pyroscope-dotnet` source directly showed why: the actual C++ profiler *engine* component (`Datadog.Profiler.Native` - not yet fully rebranded) still reads `DD_TRACE_DEBUG` for debug logging and `DD_TRACE_LOG_DIRECTORY` for where to write its log file. Setting those two instead immediately produced a real log file at `/tmp/Pyroscope-DotNet-Profiler-Native-dotnet-1.log`.

**2. `DOTNET_EnableDiagnostics=0` - baked into every service's Dockerfile since this lab's very first commit, for no documented reason - silently disables CLR profiler attach entirely.** This is confirmed upstream, not a guess: [dotnet/runtime#96227](https://github.com/dotnet/runtime/issues/96227), "`DOTNET_EnableDiagnostics=0` disables profiling in .NET 8" - a behavior change from .NET 7, and still true in .NET 10 (which is in `pyroscope-dotnet`'s own CI test matrix, so .NET 10 itself isn't the problem). With this flag removed, `/proc/1/maps` inside a running pod showed the 160MB `Pyroscope.Profiler.Native.so` actually mapped into the process for the first time - before the fix, only the tiny `Pyroscope.Linux.ApiWrapper.x64.so` (an `LD_PRELOAD` shim) ever loaded, and the real profiler engine never got a chance to attach at all.

**3. Pyroscope's native profiler refuses to run below a 1-core CPU limit - a real, ongoing resource cost, not a one-time fix.** Even after finding #2, `orders-api` and `payments-service` still sent zero profile data (confirmed via Pyroscope's `LabelValues` API returning only `orders-worker` and `pyroscope` itself) despite the profiler engine successfully loading into their processes. The debug log (from finding #1's fix) had the answer directly: `CPU limit is 0.5 with 1 threshold` / `The CPU limit is too low for the profiler to work properly` / `It is not safe to start the profiler` - the profiler protects against starving an already CPU-constrained container and simply disposes itself rather than degrading service. `orders-worker` was already at a 1-core limit (`kubernetes/base/orders-worker.yaml`, set for unrelated reasons) and worked immediately, first try; `orders-api` and `payments-service` were both at `500m` and needed raising to `1000m` (`kubernetes/base/orders-api.yaml`, `kubernetes/base/payments-service.yaml`). This is a genuine tradeoff of running continuous profiling in this lab, not a config mistake to fix once - a lower-resource deployment would need to accept losing profiling on those two services, or lowering profiling fidelity in some other way this milestone didn't explore.

## Results

Live confirmation, post-fix, against the real Pyroscope server:

```
$ curl -X POST http://172.30.0.19:4040/querier.v1.QuerierService/LabelValues \
    -H "Content-Type: application/json" -d '{"name":"service_name"}'
{"names":["orders-api", "orders-worker", "payments-service", "pyroscope"]}
```

All three application services (plus Pyroscope's own self-profiling) are sending real profile data - CPU, allocations, contention, exceptions (`PYROSCOPE_PROFILING_*_ENABLED` are all on by default in `pyroscope-dotnet`).

### Regression check

`scripts/k6-run.sh smoke`, run twice post-deploy: `failed_rate=0`, `checks_rate=1`, `flow_rate=1` both times - the first run showed the same small-sample-size cold-start latency artifact on `get-order` p95 documented in the Idempotency-Key milestone (a fresh k6 process's first-request connection cost dominating a 9-10-sample percentile, not a real regression), the second run was clean.

## Running it

Query Pyroscope directly, or open Grafana → Explore → Pyroscope datasource → select a service, or click "Profiles for this span" from any Tempo trace detail view (once a trace and a profile happen to overlap in time for the same service):

```bash
curl -X POST http://172.30.0.19:4040/querier.v1.QuerierService/LabelValues \
  -H "Content-Type: application/json" -d '{"name":"service_name"}'
```

To disable profiling for a service without rebuilding the image, set `PYROSCOPE_PROFILING_ENABLED=0` in its deployment - the native profiler binaries stay baked into the image but never attach.
