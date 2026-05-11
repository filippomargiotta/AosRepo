# AocWebApi — 12-Month Project Plan (WBSO 2026)

This file captures the month-by-month plan to implement **AocWebApi** (Agentic Orchestration Web API) in 2026, with an emphasis on **determinism, auditability, replay**, secure tool execution, planning, and evaluation/drift monitoring.

---

## 2026-01 — Foundations + determinism contract

**Goal:** Establish the platform skeleton and the **determinism contract** that every component must obey.

### Deliverables
- Repo structure, CI, test harness skeleton (**xUnit**), observability baseline (**Serilog / OpenTelemetry**)
- Draft **manifest schema** + **event log schema** (include: seeds, time source, model/tool ids, policy decisions)
- First vertical slice: API endpoint triggers a trivial workflow and emits a manifest/event log (even if “toy”)

### Definition of Done (DoD)
- You can run a “hello workflow” and produce a **manifest + log**.

### Implementation slices
- **Session A:** Scaffold solution + manifest model + baseline API route
- **Session B:** Add log writer/reader + golden test harness skeleton

---

## 2026-02 — Deterministic engine MVP + early sandbox spike

**Goal:** Make replay real early, and de-risk sandbox latency early.

### Deliverables
- Seed-locked execution (single deterministic PRNG stream)
- Time mocking interface + record/replay time events
- Sandbox spike: run a “tool” in an isolated environment and measure cold/warm startup; decide pre-warm strategy (targeting **<50ms** overhead)

### Definition of Done (DoD)
- Replay produces identical output for **5+ runs**; you have a sandbox latency baseline with concrete numbers.

---

## 2026-03 — Replay hardening + HMAC log chain + evaluation harness MVP

**Goal:** Make determinism defensible and auditable.

### Deliverables
- **HMAC-chained** event log (“tamper-evident”)
- **Signed manifest** (cryptographic signing)
- Replay tool: `replay(manifest + log) -> identical result`
- Evaluation harness MVP: golden set runner that replays N workflows and compares exact outputs

### Definition of Done (DoD)
- You can prove **tamper detection** (modify log → verification fails) and **replay equivalence**.

---

## 2026-04 — Capability router v1 + early planning spike

**Goal:** Deterministic routing logic + de-risk local planning early.

### Deliverables
- Router scoring model (latency/cost/quality/compliance)
- Deterministic constraint satisfaction approach (simple but predictable)
- Planning spike: local **<7B** model integration (Ollama/ONNX Runtime), generate simple step plans for 3–5 tasks, record failure modes

### Definition of Done (DoD)
- Router deterministically selects the same model for the same inputs; local model can produce a plan sometimes, and you know why it fails when it does.

---

## 2026-05 — Router v2 (10ms target) + metrics store

**Goal:** Make router fast and stable as conditions grow.

### Deliverables
- Historical performance weighting (store metrics by model + task class)
- Router performance benchmark suite + optimization passes targeting **~10ms**
- Determinism tests: routing decisions don’t vary with map iteration order / concurrency

### Definition of Done (DoD)
- Benchmark report shows P95 routing time and stable decisions across replays.

---

## 2026-06 — Secure sandbox v1 (pre-warm + tokens + gateway)

**Goal:** Functional isolation path integrated with the orchestration API.

### Deliverables
- Pre-warmed isolation pool (gVisor/Firecracker style)
- JWT capability tokens + enforcement at API gateway
- Tool execution protocol (inputs/outputs captured for replay)

### Definition of Done (DoD)
- A workflow can call a sandboxed tool; all I/O is captured deterministically.

---

## 2026-07 — Sandbox hardening + security tests + latency tuning

**Goal:** Reduce the chance the sandbox becomes the schedule killer late in the year.

### Deliverables
- Resource limits, filesystem/network restrictions, cross-tenant isolation tests
- Startup overhead tuning: pool sizing, warm strategies, memory mapping injection approach
- Failure handling + deterministic error surfaces (same error on replay)

### Definition of Done (DoD)
- Documented security test suite + measured overhead; replay includes sandbox outcomes.

---

## 2026-08 — Planner v1: playbooks + retrieval + step execution

**Goal:** Move from “model outputs plans” to “system produces reliable plans”.

### Deliverables
- Playbook schema and storage; retrieval-augmented plan suggestion
- Plan format with allowed actions (constrain action space)
- Planner integrated into deterministic engine: plan → steps → tool calls

### Definition of Done (DoD)
- At least 5 representative tasks can be planned and executed end-to-end with logs/manifests.

---

## 2026-09 — Planner v2: critic validation + recovery loops

**Goal:** Handle local model weaknesses (known risk).

### Deliverables
- Critic/validator stage (rule-based + model-based, but deterministic)
- Recovery strategies: backtracking, alternative subplan retrieval, step repair
- Planning evaluation metrics (success rate, step validity rate)

### Definition of Done (DoD)
- Planning success rate improves measurably versus Month 8 baseline, with reproducible evaluation.

---

## 2026-10 — Evaluation v2: semantic scoring + confidence calibration

**Goal:** Solve the “agent output variability” scoring problem.

### Deliverables
- Exact scoring + semantic scoring (deterministic)
- Confidence calibration logic (store distributions, thresholds)
- Regression report output (per workflow, per step, per tool call)

### Definition of Done (DoD)
- Evaluation detects real regressions without flagging harmless formatting changes too often.

---

## 2026-11 — Drift detection (CUSUM) + observability + load testing

**Goal:** Monitoring that works in practice, not only in theory.

### Deliverables
- Drift detector using CUSUM-style signals
- Dashboards/alerts (even basic) + release gates
- Full-system performance tests: replay speed, router P95, sandbox overhead, throughput

### Definition of Done (DoD)
- You can run a weekly drift job and get actionable output (not noise).

---

## 2026-12 — Stabilization + documentation + WBSO evidence pack

**Goal:** Ship a stable baseline and prove the R&D claims with artifacts.

### Deliverables
- Hardening sprint: fix nondeterminism leaks, flaky tests, perf regressions
- Documentation: architecture, determinism contract, manifest/log spec, router policy spec
- “Evidence pack”: benchmarks, replay proofs, drift reports, security test results (WBSO-friendly)

### Definition of Done (DoD)
- Reproducible builds, reproducible replays, and a narrative of tested technical uncertainties.
