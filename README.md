# AgenticOrchestrationSoftware

## Replay CLI

The replay CLI validates signed manifest integrity, validates event-log integrity, checks artifact compatibility, and replays the `hello` and `planner` workflow artifacts. Planner replay consumes the recorded validated plan and tool outcomes rather than rerunning retrieval, model generation, or tools.

Run from repo root:

```bash
dotnet run --project source/Aos.ReplayCli -- \
  --workflow hello \
  --manifest source/Aos.WebApi.Tests/Golden/hello-workflow-v1/manifest.json \
  --eventlog source/Aos.WebApi.Tests/Golden/hello-workflow-v1/eventlog.jsonl \
  --hmac-key golden-hmac-key
```

Expected result: exit code `0` and a replay verification success message when artifacts match.

Replay a planner scenario with `--workflow planner` and one of the `planner-*` golden artifact directories.

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

## Repository Diagnostics — September Planner v2

The read-only demonstrator diagnoses a frozen miniature .NET repository, cites recorded
source/test evidence, and recovers once from a broad-test timeout or empty retrieval.
The API requires `container-v1`. Only `repo.search`, `repo.read`, and named-profile
`repo.test` actions are permitted. Snapshot bytes are immutable after import; the container
receives one read-only fixture mount and retains the existing network/resource restrictions.

Build the current worker image using the Sandbox Benchmark instructions below, then compare
the v1 baseline (recovery disabled) and v2 on identical snapshots, playbooks, and tools:

```bash
dotnet build source/Aos.sln -c Release
dotnet run --project source/Aos.ReplayCli -c Release --no-build -- \
  evaluate-diagnostics \
  --snapshot-dir source/Aos.RepositoryFixture/Repository \
  --cases-root source/Aos.WebApi.Tests/DiagnosticsCases \
  --output-dir /tmp/aos-september-evaluation \
  --hmac-key golden-hmac-key \
  --executor container-v1
```

Expected result: v1 passes 4/6 cases, v2 passes 6/6, both recoverable cases improve,
no other case regresses, and all twelve generated runs replay. Correct policy denial
counts as task success for the two prohibited-action cases. `report.json` includes
per-case grounding, plan/step validity, rejection reasons, recovery, replay, and timing.
Planner overhead, tool duration, and sandbox startup/cleanup are reported separately.

`--executor process-v1 --worker-assembly source/Aos.SandboxWorker/bin/Release/net10.0/Aos.SandboxWorker.dll`
is available for trusted-fixture protocol regression runs; container mode supplies the
isolation evidence. Test profiles execute the packaged pricing assertion without build,
dependency restore, or snapshot writes. The broad profile deliberately waits past its
deadline. Profile identity is pinned to the packaged fixture's content hash.

Start the Web API normally, retrieve its snapshot reference with
`GET /workflow/diagnostics/snapshot`, then send the `request` object from a checked-in
case file to `POST /workflow/diagnostics`. The response contains the diagnosis or terminal
denial/failure and evidence references. `RepositoryDiagnostics:MaxRetries` accepts `0` or `1`;
`RepositoryDiagnostics:SnapshotDirectory` defaults to `../Aos.RepositoryFixture/Repository`
relative to the API content root. In this slice, named test profiles support the frozen
fixture only; relocating it preserves its content hash.

Replay a retained run with no repository/tool access:

```bash
dotnet run --project source/Aos.ReplayCli -c Release --no-build -- \
  --workflow diagnostics \
  --artifact-dir project/evidence/2026-09/container/planner-v2/RDI-06 \
  --hmac-key golden-hmac-key
```

The checked-in golden set now contains nineteen scenarios, including all twelve diagnostics
cases. September closure evidence is in `project/deliverables/2026-09.md`; October's is in
`project/deliverables/2026-10.md`.
The optional local-model critic adapter can veto a grounded diagnosis; the September
acceptance run uses rules only and makes no local-model performance claim.

### October additions

`evaluate-diagnostics` also accepts `--snapshot-dir-v2 <dir>` (fixture v2, used by RDI-03/04/05),
`--baseline <report.json>` (fails the gate if a previously passing v2 case or stage regresses),
`--min-cases <n>` / `--min-successes <n>` (October gate: 12 / 10), and `--calibration <calibration.json>`
(verifies stored confidence thresholds against the current run). Every run writes `report.json` and
`calibration.json`. Each case records exact and semantic scores, planning/tool/evidence/recovery/replay
stage results, per-tool-call rows, and a runtime-only confidence for diagnosis claims. The formula is in
`DiagnosticsConfidence`; confidence is an evaluation signal and is not part of signed workflow artifacts.
Retained October evidence is under `project/evidence/2026-10/`.

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

## Planner API

The planner endpoint performs exact task-class retrieval, deterministic term-overlap ranking, constrained plan validation, and sequential capability-protected tool execution. Playbooks and allowed actions are configured under `PlannerWorkflow`.

```bash
curl -X POST http://localhost:5057/workflow/planner \
  -H 'Content-Type: application/json' \
  -d '{
    "taskId": "task-demo-1",
    "taskClass": "workflow.plan",
    "terms": ["echo", "single"],
    "arguments": {"message": "hello planner"}
  }'
```

Successful runs emit a signed `planner.plan` event, one `tool.execution` event per completed step, and a final `workflow.planner` event.

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
