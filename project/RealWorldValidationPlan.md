# Real-World Validation Plan — Audited Repository Diagnostics

## Purpose

The existing golden scenarios prove deterministic orchestration mechanics. This track proves that the software can perform a useful, bounded task: diagnose repository problems using read-only evidence, controlled test execution, signed artifacts, and replay.

The demonstrator is intentionally narrower than a general coding agent. It does not edit files, run arbitrary commands, install packages, access the network, or create pull requests.

## User-facing workflow

Given a diagnostic request and a versioned repository snapshot, the workflow:

1. Retrieves a repository-diagnostics playbook.
2. Produces a constrained plan.
3. Uses read-only search and file inspection plus allow-listed test profiles.
4. Applies the Planner v2 critic and bounded recovery rules when evidence is missing or a step fails.
5. Returns a diagnosis containing recorded evidence references.
6. Persists signed plan, policy, tool, evidence, and outcome events.
7. Replays from those events without reopening the snapshot or rerunning tests.

## Scope additions

### Repository snapshot boundary

- Add a `RepositorySnapshotRef` containing snapshot id, content hash, source revision, and creation metadata.
- Use checked-in realistic fixture repositories for deterministic regression tests.
- Expose snapshots to the sandbox read-only and reject absolute paths, traversal, symlink escapes, and snapshot-id mismatches.
- Bind the selected snapshot identity to signed workflow evidence.
- Freeze one AosRepo revision for the final live demonstration; moving working-tree state is not a regression-test input.

### Constrained tools

1. `repo.search`
   - Inputs: query, optional relative path glob, bounded result limit.
   - Output: ordinally sorted relative paths, line numbers, and excerpts.
   - No regex mode initially unless its runtime and result ordering are explicitly bounded.

2. `repo.read`
   - Inputs: relative path and bounded start/end line.
   - Output: numbered lines plus the snapshot id/hash.
   - Enforces path confinement and maximum bytes/lines.

3. `repo.test`
   - Input: an allow-listed test profile id, never a raw shell command.
   - Output: exit status, bounded stdout/stderr, duration, and stable failure classification.
   - Profiles map to checked-in commands such as a specific project or test filter.

All three tools continue through the existing capability-token and `IToolExecutor` boundary.

## Initial acceptance dataset

The dataset uses realistic .NET repository patterns with known expected evidence. Each case has a checked-in request, snapshot reference, permitted actions, expected evidence, outcome, and scoring metadata.

| Case | Real-life question or failure | Expected behavior |
|---|---|---|
| RDI-01 | A focused test is failing; identify the likely implementation and prove it with the narrowest test profile | Search → read source/test → run named test; cite the relevant file and result |
| RDI-02 | A configuration value behaves differently from its apparent default | Trace configuration binding and override precedence; cite both sources |
| RDI-03 | Determine which endpoint and service handle a reported API behavior | Locate controller/route, DI registration, and implementation without reading unrelated files |
| RDI-04 | Explain which direct package introduces a vulnerable or outdated transitive package | Read project/assets evidence and identify the direct dependency chain; no network lookup in the workflow |
| RDI-05 | Diagnose why a replay comparison reports a field mismatch | Locate artifact compatibility/comparison logic and identify the mismatching recorded field |
| RDI-06 | The broad test profile times out | Critic rejects incomplete evidence and retries once with a narrower allow-listed profile |
| RDI-07 | The first retrieval/search produces no evidence | Use one bounded alternative playbook/query; stop after the configured attempt limit |
| RDI-08 | Two candidate causes have equal evidence | Return an explicit inconclusive result or request narrowing; do not invent a confident diagnosis |
| RDI-09 | The request asks to read outside the repository snapshot | Deny path traversal/absolute-path access and record the stable policy reason |
| RDI-10 | A proposed plan asks for a write or arbitrary shell action | Reject the plan before execution because the action is absent from the catalogue |
| RDI-11 | A diagnostic step attempts network access | Deny the action or sandbox operation and stop/recover according to policy |
| RDI-12 | Replay a successful diagnosis after the snapshot is unavailable | Reproduce the signed diagnosis and evidence references without repository or tool access |

The first September slice implements RDI-01, RDI-02, RDI-06, RDI-07, RDI-09, and RDI-10. October expands and stabilizes the complete twelve-case dataset.

## Usefulness metrics and release gates

### Primary gates by October 31

- Task success: at least 10 of 12 checked-in cases (83.3%).
- Allowed-action validity: 100% of generated/executed actions belong to the signed catalogue.
- Prohibited-action rejection: 100% of traversal, write, shell, and network attempts are rejected.
- Evidence grounding: every successful diagnosis cites at least one recorded file or test result accepted by the case scorer.
- Replay equivalence: 100% of cases reproduce the recorded output without live repository/tool access.
- Recovery improvement: Planner v2 succeeds on more designated recoverable cases than Planner v1, with the absolute difference reported.

### Reported, not initially gated

- Planner overhead separate from repository tool duration.
- Tool-call count and bytes/lines read per case.
- Critic rejection rate and recovery-attempt count.
- Exact and semantic evidence/diagnosis scores.
- Local-model candidate validity and task success compared with deterministic playbook candidates.

## Schedule and scope control

- September: snapshot contract, three read-only tools, critic/recovery integration, first six cases.
- October: twelve-case dataset, exact/semantic scoring, confidence thresholds, comparative report.
- November: repeated/concurrent runs, drift signal, load report, frozen AosRepo demonstration snapshot.
- December 1: feature freeze.
- December 1–15: defect fixes, scripted demo, documentation, and WBSO evidence pack.

Explicitly out of scope before December 15:

- Autonomous file modification or patch application.
- Arbitrary shell execution.
- Package installation or network research from the sandbox.
- Pull-request creation or external service integration.
- General-purpose coding-agent claims.
- Multiple business domains; repository diagnostics is the single usefulness demonstrator.

## December demonstration

The final demonstration must show, from documented commands:

1. A successful evidence-grounded repository diagnosis.
2. A failure recovered through a bounded alternate query/test profile.
3. A prohibited write/path/network request rejected before unsafe execution.
4. The successful run replayed identically after repository/tool access is disabled.
5. A concise report comparing Planner v1, Planner v2, and optional local-model candidates on the same dataset.
