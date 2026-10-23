# WBSO Technical Challenges and Experimental Evidence — Project SW2

**Project:** AI-Driven Agentic Orchestration Software (AOC-WebAPI)\
**Period:** 1 January–31 December 2026\
**Evidence status date:** 30 September 2026\
**Purpose:** working technical-project record for the WBSO administration

This document connects the technical bottlenecks in the original WBSO application to the experiments, failures, implementation decisions, tests, and measurements recorded in this repository. It is not a substitute for the approved application, the hours registration, or advice from the WBSO adviser. Only work actually performed should be entered in the administration.

Source application: [StellaDev WBSO 2026](<StellaDev WBSO 2026.pdf>).

## Evidence rules

RVO expects the project administration to make the nature, content, and progress of the S&O work clear. For software projects, version-control history, issue records, test results, and other dated technical artifacts can support that record. The administration for a development project should also show the technical problems or bottlenecks and the considered solution directions.

- Keep this record chronological and factual. Do not turn planned work into completed work.
- Link each material experiment to a commit, test, benchmark, deliverable, or signed replay artifact.
- Record negative results and abandoned approaches. A failed experiment can still be useful technical evidence.
- Keep S&O hours in the separate hours registration and make them reconcilable with the dates and activities here.
- Under the current RVO administration guidance, record actual S&O hours at least every 10 working days, complete the project administration within two months after each quarter, and retain the administration for seven years.
- Treat ordinary documentation, routine dependency upgrades, cosmetic refactoring, general project management, and administration separately; they are not automatically qualifying technical R&D.
- Retain the administration for the required statutory period and confirm the final classification with the adviser.

Official references:

- [RVO — S&O administration](https://www.rvo.nl/subsidies-financiering/wbso/na-uw-aanvraag/so-administratie-bijhouden)
- [RVO — WBSO publications and 2026 manual](https://www.rvo.nl/subsidies-financiering/wbso/publicaties)

## Current evidence health

| Area | Status | Evidence-based assessment |
|---|---|---|
| Traceability to approved TC1–TC5 | Green | Each approved bottleneck maps to repository work and dated artifacts. |
| Experimental and negative results | Green | Local-model failures, sandbox latency misses, isolation limitations, and implementation defects are recorded. |
| Objective verification | Green | Tests, golden replay scenarios, tamper tests, security tests, and latency benchmarks exist. |
| Hours-to-activity reconciliation | Owner check | The Excel registration is intentionally not changed here; its entries must match the dated work. |
| Change control | Attention required | The delivered sandbox uses hardened OCI/runc, not gVisor/Firecracker. The reason and remaining boundary are documented below and should be checked with the adviser as a project-development update. |
| Full-year completion | In progress | TC1 and TC2 are implemented; TC3 has measured isolation and snapshot evidence with an explicit runtime boundary; TC4 bounded critic/recovery is demonstrated; TC5 is only partly implemented. |

The strongest evidence is for deterministic replay, routing, sandbox behavior, and constrained planning. September adds measured bounded recovery on repository fixtures. This does not resolve all five uncertainties: broader model planning, semantic evaluation, confidence calibration, and drift detection remain future work.

## Traceability to the approved technical bottlenecks

| Approved item | Original uncertainty | Work performed through September | Present conclusion |
|---|---|---|---|
| TC1 / TS1 | Whether an agentic workflow can be reproduced and cryptographically verified despite time, randomness, state, and external outcomes | Seed locking, record/replay time, versioned manifests, HMAC event-log chain, signed manifest/log binding, field-level replay differences, golden replay | A deterministic signed replay path has been demonstrated for the bounded workflows. External actions are replayed from signed recorded outcomes rather than repeated. |
| TC2 / TS2 | Whether strict latency/cost/quality/compliance constraints and historical metrics can be evaluated quickly without unstable choices | Hard filtering, deterministic scoring and tie-breaking, task policies, exact metrics lookup, runtime metrics blending, decision capture in the signed manifest | Stable routing was demonstrated under reordered and concurrent inputs. Measured p95 was far below the 10 ms hypothesis on the bounded catalog. Scaling behavior still requires later load evidence. |
| TC3 / TS3 | Whether untrusted tool execution can combine isolation, tenant separation, deterministic failures, and less than 50 ms warm overhead | Stable tool protocol, JWT capability binding, pre-warmed pool, hardened container, security tests, immutable snapshot tools, read-only fixture mount, deterministic failure replay | Warm execution met the measured p95 hypothesis; cold did not. Snapshot/tool restrictions are demonstrated on bounded fixtures. Current isolation remains OCI/runc. |
| TC4 / TS4 | Whether local models below 7B can create valid multi-step plans and recover without external LLM dependence | April local-model spike, constrained plans/playbooks, deterministic retrieval, capability-protected execution, evidence critic, one-retry recovery, signed replay | Raw model planning was unreliable. Deterministic playbooks plus bounded recovery improve fixture task success from 4/6 to 6/6; both designated recovery cases improve. No live model-critic performance claim is made. |
| TC5 / TS5 | Whether changing multi-step behavior can be scored and drift detected without false alarms | Exact golden replay harness, thirteen signed scenarios, and six-case grounding/outcome metrics | Exact equivalence and bounded case scoring are demonstrated. Semantic scoring, calibration, CUSUM-style detection, and false-positive control remain future experiments. |

## TC1 — Deterministic and auditable execution

### Technical uncertainty

The unknown was not simply how to serialize a workflow. The problem was whether time, random seeds, configuration order, routing decisions, tool results, and schema evolution could be captured so that a historical execution remains verifiable after the live environment changes. It was unclear at the outset which data belonged in the signed deterministic surface and which data had to stay runtime-only.

### Experiments and encountered challenges

1. **January — smallest viable determinism contract.** A minimal versioned manifest and event-log model was created before the final workflow shape was known. The design risk was either omitting replay-critical inputs or prematurely freezing a large schema.
2. **February — hidden time and seed inputs.** System time and generated seeds made normal execution unsuitable for exact replay. Separate recording and replay time sources and a locked seed provider were introduced. Configuration-to-manifest tests also exposed ordering as part of the deterministic contract.
3. **February–March — a replay pass/fail result was not diagnosable.** Coarse mismatch output did not identify why replay diverged. Structural comparison was changed to report a manifest field or event-log line/path, with byte comparison retained as a fallback.
4. **March — unsigned artifacts could be edited without proof.** An HMAC chain was developed to detect modified, removed, and reordered records and use of the wrong key. A separately signed manifest was then bound to the event-log count and tail MAC.
5. **March onward — live dependencies would invalidate historical replay.** Routing configuration, sandbox state, latency, and tool behavior can change. The implementation records replay-relevant decisions and outcomes, then validates and consumes those signed values rather than rerouting or rerunning external tools.

### Implemented solution direction

- Versioned manifest and event-log contracts with exact-version validation.
- Seed-locked execution plus record/replay time abstractions.
- HMAC-SHA256 event-log chain and signed manifest-to-log binding.
- Stable, field-level incompatibility and mismatch reporting.
- Golden scenario evaluator and dedicated replay handlers.
- Recorded route, plan, capability decision, and tool outcome as the historical replay boundary.

### Result and remaining boundary

The bounded workflows replay successfully from signed artifacts, and deliberate tampering is rejected. This demonstrates the working principle for the current contracts; it does not prove that arbitrary future tools or sources of nondeterminism are automatically covered. New event types must explicitly decide what is signed, recorded, or runtime-only.

### Evidence

- Monthly records: [January](deliverables/2026-01.md), [February](deliverables/2026-02.md), [March](deliverables/2026-03.md)
- Design record: [Schema versioning](SchemaVersioning.md)
- Implementations: [`HmacEventLogIntegrityChain`](../source/Aos.Core/Services/HmacEventLogIntegrityChain.cs), [`HmacManifestSigner`](../source/Aos.Core/Services/HmacManifestSigner.cs), [`ReplayTimeSource`](../source/Aos.Core/Services/ReplayTimeSource.cs), [`ReplayCliRunner`](../source/Aos.ReplayCli/ReplayCliRunner.cs)
- Tests: [`EventLogIntegrityChainTests`](../source/Aos.WebApi.Tests/EventLogIntegrityChainTests.cs), [`ManifestSignerTests`](../source/Aos.WebApi.Tests/ManifestSignerTests.cs), [`ReplayCliTests`](../source/Aos.WebApi.Tests/ReplayCliTests.cs), [`GoldenEvaluationRunnerTests`](../source/Aos.WebApi.Tests/GoldenEvaluationRunnerTests.cs)
- Principal commits: `8f15221`, `fb39fb9`, `4f391f3`, `ca31357`, `7dab9ed`

## TC2 — Deterministic capability routing under competing constraints

### Technical uncertainty

The uncertainty was whether hard compliance and resource constraints could be combined with latency, cost, quality, and changing historical performance without introducing order-dependent or concurrency-dependent choices. It was also unknown whether a richer decision could stay under the application's 10 ms routing hypothesis.

### Experiments and encountered challenges

1. **Hard constraints versus weighted preferences.** Treating every input as a score could allow a high-scoring but prohibited candidate to win. Hard constraints were therefore evaluated before scoring.
2. **Equal scores and collection order.** Candidate order could otherwise become an accidental decision input. A complete lexical and numeric tie-break chain was defined and tested with reordered candidates.
3. **Live configuration drift.** Recalculating a historical route using current policies or metrics produced the wrong replay model. The signed effective policy, candidate ranking, rejections, and selection were added to manifest schema `0.3`; replay consumes that decision.
4. **Historical metrics could destabilize routing.** Metrics are looked up by exact task-class/provider/model/version identity, validated, and blended only after hard filtering. Missing or unusable samples fall back to static scoring.
5. **Dependency experimentation produced an incompatible path.** Preview .NET 11 package candidates broke the .NET 10 build and were reverted to stable compatible packages. This is supporting engineering evidence, not by itself the claimed routing R&D.

### Implemented solution direction

- Deterministic hard-constraint filtering before scoring.
- Fully specified stable ranking and tie-breaking.
- Task-class policies with deterministic stricter constraint merging.
- Exact-identity metrics store and deterministic static/observed score blending.
- Signed route-decision capture so replay is independent of live metrics.
- Repeatable benchmark and parallel stability tests.

### Result and remaining boundary

The May comparison measured a metrics-enabled routing p95 of `0.0048 ms`, versus a static baseline p95 of `0.0043 ms`, for the bounded local catalog. Decisions remained stable under input reordering, concurrent calls, and replay. This supports the working principle but is not evidence for an arbitrarily large catalog or every future constraint type.

### Evidence

- Monthly records: [April](deliverables/2026-04.md), [May](deliverables/2026-05.md)
- Implementations: [`RouterSelectionEngine`](../source/Aos.Core/Services/RouterSelectionEngine.cs), [`DeterministicRouterService`](../source/Aos.Core/Services/DeterministicRouterService.cs), [`InMemoryRouterMetricsStore`](../source/Aos.Core/Services/InMemoryRouterMetricsStore.cs), [`RouterBenchmarkRunner`](../source/Aos.Core/Services/RouterBenchmarkRunner.cs)
- Tests: [`RouterServiceTests`](../source/Aos.WebApi.Tests/RouterServiceTests.cs), [`RouterMetricsStoreTests`](../source/Aos.WebApi.Tests/RouterMetricsStoreTests.cs), [`RouterBenchmarkTests`](../source/Aos.WebApi.Tests/RouterBenchmarkTests.cs)
- Principal commits: `7121365`, `32c4611`, `d58a994`, `1db5abb`, `9c44599`

## TC3 — Low-latency isolated tool execution

### Technical uncertainty

The approved problem combines goals that normally conflict: execute an untrusted tool with restricted network, filesystem, resources, privileges, and tenant state while preserving deterministic protocol behavior and keeping warm overhead below 50 ms. It was not known whether process/container startup and pool lifecycle behavior would meet that threshold safely.

### Experiments and encountered challenges

1. **Protocol before isolation.** A stable request/result/event contract was needed before switching execution backends. Tool identity, version, input, output, error, and invocation binding were made explicit.
2. **Capability scope and secret leakage.** JWT claims had to bind the exact run, invocation, tool, version, action, and expiry. Denied calls must not reach the executor, while raw tokens must not enter signed artifacts.
3. **In-process pool gave a misleading latency result.** June's warm and cold p95 were both approximately `0.0001 ms` because slot creation did not represent OS isolation. The result showed that the prototype could validate pool semantics but could not validate the latency hypothesis.
4. **Real subprocesses introduced lifecycle failures.** Crash, timeout, and malformed output required stable error codes. Synchronous refill hid startup cost in caller latency, so refill became asynchronous. Release/dispose races required synchronization, and redirected stderr had to be drained to avoid a full-pipe deadlock.
5. **Isolation required an independently testable backend.** The container implementation disabled networking, used a read-only root, bounded `/tmp`, ran non-root, dropped capabilities, enabled `no-new-privileges`, applied CPU/memory/PID limits, and mounted no host paths. Tests verified filesystem/network denial, secret exclusion, tenant-state separation, limits, timeout cleanup, and protocol identity.
6. **Cold startup missed the hypothesis.** Hardened cold total p95 measured `225.1958 ms`; hardened warm total p95 measured `26.6897 ms`. This negative result established that pre-warming is not merely an optimization but a requirement for the target.
7. **Runtime deviation from the proposed solution.** The measured implementation uses OrbStack's OCI `runc` path, namespaces, cgroups, and hardening flags. It is not gVisor or Firecracker and does not claim their stronger isolation boundary. `ISandboxSlot` preserves a backend seam for a later `runsc` or micro-VM experiment.

### Implemented solution direction

- Versioned deterministic tool I/O and failure protocol.
- Exact-scope HS256 JWT capability enforcement without recording raw tokens.
- One-shot pre-warmed pool with backend-independent slot interface.
- Process-backed worker to expose startup, crash, timeout, protocol, and lifecycle behavior.
- Hardened OCI container backend and opt-in live security suite.
- Signed recording and replay of tool outcomes without re-executing the tool.

### Result and remaining boundary

The warm implementation meets the `<50 ms` p95 hypothesis on the measured host and the security tests prove the configured OCI restrictions. Cold startup does not meet it. The current result does not establish a gVisor/Firecracker-grade boundary; that difference should remain visible in WBSO progress reporting and any product security claim.

### Evidence

- Monthly records: [June](deliverables/2026-06.md), [July](deliverables/2026-07.md)
- Implementations: [`CapabilityEnforcingToolExecutor`](../source/Aos.Core/Services/CapabilityEnforcingToolExecutor.cs), [`PreWarmedSandboxPool`](../source/Aos.Core/Services/PreWarmedSandboxPool.cs), [`ProcessSandboxSlot`](../source/Aos.Core/Services/ProcessSandboxSlot.cs), [`ContainerSandboxSlot`](../source/Aos.Core/Services/ContainerSandboxSlot.cs), [`Aos.SandboxWorker`](../source/Aos.SandboxWorker/Program.cs)
- Tests: [`CapabilityTokenTests`](../source/Aos.WebApi.Tests/CapabilityTokenTests.cs), [`SandboxPoolTests`](../source/Aos.WebApi.Tests/SandboxPoolTests.cs)
- Container definition: [`Dockerfile`](../source/Aos.SandboxWorker/Dockerfile)
- Principal commits: `aaf1369`, `7cc40b6`, `c64652f`, `ee92b67`, `58d1eda`

## TC4 — Reliable multi-step planning with a small local model

### Technical uncertainty

The uncertainty was whether a local model below 7B could produce executable, repository-grounded, multi-step action sequences and recover from mistakes without relying on an external LLM. The technical risk included invalid structure, fabricated actions, weak context use, and unreliable recovery.

### Experiments and encountered challenges

1. **April raw-model spike did not meet the reliability requirement.** Five tasks were run with local Ollama `qwen2.5-coder:1.5b-base` at temperature zero. The model often ignored the requested `PLAN`/`RISKS` form, fabricated repository paths and commands, drifted from .NET toward Python/YAML, and returned generic risk analysis. Cold latency was about `10.1 s`; warm latency was about `1.5–2.1 s`.
2. **Signing raw output would have turned unreliable text into trusted evidence.** Planner output was deliberately excluded from manifest `0.3` after the spike.
3. **Plans needed a smaller executable state space.** Separate plan/playbook schema `0.1`, concrete allowed actions, exact tool/version binding, argument allow-lists, and contiguous step ordering were developed.
4. **Retrieval itself could become nondeterministic.** Task class, normalized exact-term overlap, score ordering, and lexical id/version tie-breaks were specified and tested independently of input order.
5. **Model candidates remained untrusted.** Optional local-model JSON is parsed only as a candidate. Malformed JSON, wrong task class, unknown action, unknown argument, or invalid ordering fails closed before execution or persistence.
6. **Multi-step authority and failure handling required per-step boundaries.** Execution issues one capability per invocation, serializes arguments canonically, runs strictly sequentially, and stops on denial, cancellation, timeout, protocol error, or other failure.

### Implemented solution direction

- Retrieval-augmented playbooks with deterministic ranking and exact template binding.
- Separate versioned planner contracts and stable validation errors.
- Immutable allowed-action catalog mapped to concrete tool identities and arguments.
- Optional local model behind the same fail-closed validator as deterministic candidates.
- Sequential capability-protected execution and signed plan/tool evidence.
- Replay of the recorded validated plan and outcomes rather than repeating retrieval or generation.

### Result and remaining boundary

Five representative planner scenarios execute end to end and were part of the seven-scenario August golden set. The August release suite passed `126` tests with six opt-in container tests skipped; the container-enabled suite passed `134`. At that closure, critic and recovery effectiveness remained unproven. September's bounded recovery experiment and remaining model/backtracking boundary are recorded below.

### Evidence

- Monthly record: [August](deliverables/2026-08.md); April failure record: [April](deliverables/2026-04.md)
- Implementations: [`DeterministicPlannerService`](../source/Aos.Core/Services/DeterministicPlannerService.cs), [`DeterministicPlaybookRetriever`](../source/Aos.Core/Services/DeterministicPlaybookRetriever.cs), [`PlannerPlanValidator`](../source/Aos.Core/Services/PlannerPlanValidator.cs), [`PlannerStepExecutor`](../source/Aos.Core/Services/PlannerStepExecutor.cs), [`PlannerReplayWorkflow`](../source/Aos.ReplayCli/PlannerReplayWorkflow.cs)
- Tests: [`PlannerPlanValidatorTests`](../source/Aos.WebApi.Tests/PlannerPlanValidatorTests.cs), [`PlaybookRetrievalTests`](../source/Aos.WebApi.Tests/PlaybookRetrievalTests.cs), [`DeterministicPlannerServiceTests`](../source/Aos.WebApi.Tests/DeterministicPlannerServiceTests.cs), [`PlannerStepExecutorTests`](../source/Aos.WebApi.Tests/PlannerStepExecutorTests.cs), [`PlannerGoldenTests`](../source/Aos.WebApi.Tests/PlannerGoldenTests.cs)
- Principal commits: `53e4a0d`, `9d2b8c5`, `7dab9ed`

## TC5 — Deterministic evaluation and drift detection

### Technical uncertainty

The unresolved problem is how to distinguish a real behavioral regression from harmless structural or textual variation in multi-step agent output. Exact comparison is too strict for some future outputs, while semantic comparison can be unstable or hide evidence errors. Confidence thresholds and CUSUM-style signals may produce noisy false alarms.

### Work performed and current limitation

- March established exact golden replay evaluation with signed inputs and pass/fail summaries.
- July added a deterministic timeout outcome.
- August added five successful planner scenarios, bringing the golden set to seven.
- The current evaluator proves exact replay equivalence and catches structural/tamper failures.
- It does **not** yet implement semantic scoring, confidence calibration, statistical drift detection, or measured false-positive control. Those must not be reported as completed work.

### Evidence

- Monthly records: [March](deliverables/2026-03.md), [July](deliverables/2026-07.md), [August](deliverables/2026-08.md)
- Implementations: [`GoldenEvaluationRunner`](../source/Aos.ReplayCli/GoldenEvaluationRunner.cs), [`PlannerGoldenTests`](../source/Aos.WebApi.Tests/PlannerGoldenTests.cs)
- Principal commits: `ca31357`, `58d1eda`, `7dab9ed`

## Supporting implementation incidents

These incidents show normal technical investigation and corrective work. They should only be attributed to S&O where they directly supported an approved technical bottleneck.

| Period | Incident | Technical consequence | Resolution/evidence |
|---|---|---|---|
| February | xUnit tests were not discovered correctly by the local CI path | A green build could have hidden missing verification | Local CI/test discovery was corrected in commit `8c4231b`. |
| June | Dependency injection could choose between multiple resolvable `IRouterMetricsStore` constructors | The live router could resolve the wrong metrics wiring | Replaced ambiguous registration with an explicit factory; smoke tests now resolve the real service graph. |
| July | Pool replacement startup happened during release | Measured warm-path caller latency included hidden refill work | Refill moved to tracked background work with release/dispose synchronization. |
| July | Redirected worker stderr was not drained | A noisy worker could deadlock when the pipe filled | Added asynchronous draining and a regression test. |
| August | A high-severity transitive `Microsoft.OpenApi 2.4.1` advisory was present | Release dependency health was not acceptable | Stable package updates moved the graph to `Microsoft.OpenApi 2.7.5`; the final scan reported no vulnerable packages. This is maintenance evidence, not a standalone R&D claim. |

## Quantitative evidence summary

| Hypothesis or gate | Observation | Interpretation |
|---|---|---|
| Router p95 near/below 10 ms | `0.0048 ms` with metrics on the bounded local catalog | Gate met for the measured configuration; not a large-catalog scaling claim. |
| Hardened warm sandbox p95 below 50 ms | `26.6897 ms` | Gate met on the measured OrbStack/runc host. |
| Hardened cold sandbox p95 below 50 ms | `225.1958 ms` | Gate missed; pre-warming is required. |
| Small local model produces trustworthy raw plans | Structural violations, hallucinated repo content, architecture drift | Hypothesis rejected for the April setup; constrain and validate candidates. |
| Signed replay detects tampering | Modified/removed/reordered lines, wrong keys, manifest/log mismatch rejected | Working principle demonstrated by automated tests. |
| Planner v1 bounded end-to-end operation | Five planner scenarios; seven total August golden scenarios | August working principle demonstrated; September recovery comparison is recorded below. |
| August release health | 126 standard tests; 134 with live container coverage; zero failures | Stable closure baseline for the implemented scope. |

## Planned technical uncertainties — not yet completed evidence

The following items belong to the future plan. September's bounded experiments are recorded separately below.

- **October / TC5:** whether exact evidence scoring plus bounded semantic scoring can accept harmless wording changes without accepting unsupported diagnoses.
- **October / TC5:** whether confidence values can be calibrated reproducibly on a small but realistic dataset.
- **November / TC5:** whether CUSUM-style signals detect injected regressions without producing unactionable alert noise.
- **November–December:** whether the repository-diagnostics demonstrator remains reproducible under repeated and concurrent use and meets the December 15 usefulness gates in the [Real-World Validation Plan](RealWorldValidationPlan.md).

## September 30 — TC3/TC4 bounded diagnostic recovery experiment

- **Uncertainty:** can evidence validation and one deterministic retry improve useful diagnosis
  without introducing loops, unsafe actions, or live dependencies during replay?
- **Prototype:** a frozen miniature .NET repository, immutable content-addressed snapshots,
  three capability-protected tools, named packaged test profiles, rule-based grounding,
  and a signed attempt/critic/recovery protocol.
- **Baseline:** identical deterministic planner, playbooks, tools, evidence assessment, and
  fixture inputs with recovery disabled. This avoids comparing real diagnostics to August's
  echo-only tasks.
- **Alternatives considered:** unrestricted shell/build commands were excluded from the action
  space; packaged assertions replace build/restore during execution. Recovery is restricted to
  a configured narrower profile or alternate retrieval rather than unbounded backtracking.
- **Encountered defects:** nested snapshot timestamps initially broke log verification after
  deserialization. Freezing new domain payloads as JSON before signing fixed the disk round
  trip without changing historical signing envelopes. The first timing report attributed cold
  worker lifecycle to planner time; the report now separates planner, tool, and sandbox time.
  An output-directory check also found that the Replay CLI's benchmark dependency copied the
  worker and fixture into its build output. A seven-file replay-only staging package removes
  those files and dependency-manifest entries; the offline proof was repeated on that package.
- **Observed result:** hardened-container task success improved **4/6 → 6/6**; recoverable
  success improved **0/2 → 2/2**, with one retry each and no non-recovery regression.
  Both prohibited-action cases fail closed; a live mount probe confirms snapshot writes fail.
- **Replay proof:** all twelve baseline/v2 artifacts replayed in a read-only, network-disabled
  runtime container without the repository snapshot, worker/test runner, or Docker socket.
- **Verification:** 155 standard Release tests passed (8 container tests skipped); 165 tests
  passed with container coverage; thirteen golden scenarios passed. Tests include symlink and
  traversal rejection, bounded I/O, model veto/invalid output, retry limits, and signed-but-invalid
  evidence-reference rejection.
- **Conclusion:** the working principle is demonstrated for bounded deterministic diagnostics.
  A live local-model critic, broader datasets, semantic scoring, and load behavior are not
  established by this experiment. Cold-slot report timings are not a warm-sandbox benchmark.
- **Evidence:** [September deliverable](deliverables/2026-09.md),
  [comparison report](evidence/2026-09/container/report.json),
  [runtime/source metadata](evidence/2026-09/verification.json),
  `source/Aos.Core/Services/DiagnosticsWorkflowService.cs`,
  `source/Aos.WebApi.Tests/RepositoryDiagnosticsTests.cs`,
  `source/Aos.WebApi.Tests/RepositoryToolsTests.cs`. Source is an uncommitted working-tree
  implementation based on `7dab9ed`; the evidence metadata retains its source-tree digest.
- **Hours registration:** maintained separately by the owner; no hours were entered by this task.

## Update template for each new experiment

Copy this block into the relevant monthly deliverable or add a dated subsection here:

```text
Date / period:
Approved bottleneck: TC… / TS…
Technical uncertainty tested:
Why the result was not known beforehand:
Prototype or experiment:
Alternatives considered:
Observed result, including failures:
Measurement / acceptance threshold:
Technical conclusion and next decision:
Remaining limitation:
Evidence: commit, source, test, benchmark, signed artifact:
Hours-registration reference: maintained separately by owner:
```

## Evidence-pack checklist

- [ ] Hours registration agrees with the dates and technical activities; update actual hours within the applicable RVO timing rules.
- [x] Original WBSO project description retained in `project/StellaDev WBSO 2026.pdf`.
- [x] Monthly deliverables retained under `project/deliverables/`.
- [x] Source and test history retained in Git with dated commits.
- [x] Negative results and scope boundaries are explicit.
- [x] Benchmarks include commands, host/runtime context, and bounded conclusions.
- [x] Golden replay artifacts and tamper/failure tests are retained.
- [ ] Discuss the runc versus gVisor/Firecracker implementation development with the WBSO adviser and record any required project update.
- [ ] At year end, add the September–December results and a final TC1–TC5 conclusion without rewriting the historical observations.
