# AgenticOrchestrationSoftware

## Replay CLI

The replay CLI validates signed manifest integrity, validates event-log integrity, checks artifact compatibility, and replays the `hello` workflow artifacts. The current `hello` golden scenario includes a deterministic tool execution event, so replay also compares captured tool input/output.

Run from repo root:

```bash
dotnet run --project source/Aos.ReplayCli -- \
  --workflow hello \
  --manifest source/Aos.WebApi.Tests/Golden/hello-workflow-v1/manifest.json \
  --eventlog source/Aos.WebApi.Tests/Golden/hello-workflow-v1/eventlog.jsonl \
  --hmac-key golden-hmac-key
```

Expected result: exit code `0` and a replay verification success message when artifacts match.

Tool execution is protected by a short-lived invocation-specific JWT capability. The token is validated immediately before `IToolExecutor` delegates work, while signed artifacts record only the allow/deny decision and non-secret token id; raw JWTs are not persisted.

You can also point replay at a per-run artifact directory instead of passing both files explicitly:

```bash
dotnet run --project source/Aos.ReplayCli -- \
  --workflow hello \
  --artifact-dir source/Aos.WebApi.Tests/Golden/hello-workflow-v1 \
  --hmac-key golden-hmac-key
```

## Evaluation Harness

The same CLI also includes a golden-set evaluation mode that scans `scenario.json` files and replays each scenario exactly.

Run from repo root:

```bash
dotnet run --project source/Aos.ReplayCli -- \
  evaluate \
  --scenarios-root source/Aos.WebApi.Tests/Golden \
  --hmac-key golden-hmac-key
```

Expected result: exit code `0` and a per-scenario PASS summary.

## Router API

The API also exposes a deterministic router decision endpoint backed by configured model candidates and weighted routing scores.
The `hello` workflow now uses the same router path and records the effective routing decision in the signed manifest so replay can reproduce the original model selection without depending on live router configuration.

Sample request:

```bash
curl -X POST http://localhost:5057/router/decide \
  -H 'Content-Type: application/json' \
  -d '{
    "taskClass": "chat.response",
    "maxLatencyMs": 200,
    "maxCostPer1KTokens": 0.5,
    "minQualityScore": 60,
    "requiredComplianceTags": ["eu", "standard"]
  }'
```

Expected result: a ranked deterministic routing decision with the selected model and any rejected candidates.

Current schema/versioning rules for replay artifacts are documented in `project/SchemaVersioning.md`.

## Router Benchmark

The replay CLI can also report the current deterministic router latency baseline from the checked-in router configuration:

```bash
dotnet run --project source/Aos.ReplayCli -- \
  benchmark-router \
  --iterations 10000 \
  --warmup 1000 \
  --task-class workflow.hello
```

Expected result: a latency summary with min, median, p95, and max decision time plus the selected model and candidate counts.

## Sandbox Benchmark

Tool execution runs through a pre-warmed `PooledSandboxToolExecutor` backed by `PreWarmedSandboxPool`.
The Web API defaults to the hardened `container-v1` backend, which starts a single-use
`Aos.SandboxWorker` container per slot. The container runs as a non-root user with no network,
a read-only root filesystem, a bounded tmpfs, dropped Linux capabilities, `no-new-privileges`,
and CPU, memory, and PID limits. `process-v1` remains available for fast local protocol tests.

Build the pinned worker image with OrbStack or another Docker-compatible Linux runtime:

```bash
docker build \
  -f source/Aos.SandboxWorker/Dockerfile \
  -t aos-sandbox-worker:local \
  source
```

Run the container security suite:

```bash
AOS_CONTAINER_TESTS=1 dotnet test source/Aos.sln --no-restore -v minimal
```

The benchmark CLI reports warm/cold start counts and acquire + total latency percentiles for both paths:

```bash
dotnet run --project source/Aos.ReplayCli -- \
  benchmark-sandbox \
  --iterations 20 \
  --warmup 0 \
  --pool-size 4 \
  --executor container-v1
```

To measure cold-path overhead (no pre-warmed slots):

```bash
dotnet run --project source/Aos.ReplayCli -- \
  benchmark-sandbox \
  --iterations 10 \
  --warmup 0 \
  --pool-size 0 \
  --executor container-v1
```

Expected result: warm/cold start counts and latency percentiles. Warm acquire measures queue dequeue
for an already-ready isolated worker; cold acquire includes container and .NET worker startup.
Use bounded iteration counts because each one-shot slot is replaced with a fresh container.
