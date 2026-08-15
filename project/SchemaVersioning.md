# Replay Artifact Schema Versioning

## Current baseline
- Manifest schema version is locked to `0.3`.
- Event log schema baseline is `0.2`.
- Planner plan schema baseline is `0.1`.
- Planner playbook schema baseline is `0.1`.
- Replay currently supports exact-version artifacts only; it does not attempt cross-version migration.

## Compatibility rules
- The signed manifest artifact wraps:
  - `manifest`
  - `integrity.algorithm`
  - `integrity.keyId`
  - `integrity.manifestMac`
- The manifest `manifest.manifestVersion` must be supported exactly by the runtime validator.
- Replay validates the manifest HMAC before event-log validation or workflow replay.
- The manifest payload now includes `eventLog.schemaVersion`, `eventLog.recordCount`, and `eventLog.lastChainMac`.
- The manifest payload now includes `routingDecisions[]`, with the effective router policy, selected model, ranked candidates, scores, and rejection reasons used for the run.
- Every event-log record must use a supported `schemaVersion`.
- Every event-log record wraps the domain event as:
  - `entry`
  - `integrity.algorithm`
  - `integrity.keyId`
  - `integrity.previousChainMac`
  - `integrity.chainMac`
- Replay validates the HMAC chain before artifact compatibility checks or workflow replay.
- Every wrapped event-log entry must use the same `runId` as the manifest.
- If an event-log payload includes `manifestVersion`, it must match the manifest `manifestVersion`.
- The event log line count must match `manifest.eventLog.recordCount`.
- The event log tail `chainMac` must match `manifest.eventLog.lastChainMac`.
- Replay uses the recorded routing decision instead of re-evaluating live router configuration, so historical artifacts stay reproducible after policy/candidate changes.
- Event-log schema `0.2` can carry domain event payloads such as `tool.execution`; adding June tool I/O and its capability-policy decision did not change the signed event-log envelope.
- Replay validates the requested tool identity and input against the recorded `tool.execution` event, then consumes the recorded status, output, and deterministic error. External tool execution is not repeated during replay.
- Sandbox runtime metadata such as container id, warm/cold state, and latency remains outside signed artifacts; the deterministic sandbox outcome is captured by the signed tool status/output/error fields.
- Capability JWTs are never persisted in replay artifacts. The `tool.execution` payload records only the deterministic policy id, allow/deny decision, stable reason code, and non-secret token id.
- Workflow selection is still CLI-driven and is not yet encoded into the manifest schema.

## Planner artifact boundary

- Planner plans and playbooks use separate `0.1` schema-versioned contracts and are not part of manifest `0.3`.
- Plan action ids are matched using ordinal, case-sensitive comparison against an allowed-action catalogue bound to concrete tool ids and versions.
- Plan validation is fail-closed: unsupported schemas, invalid step order, duplicate step ids, unknown actions/arguments, and missing required arguments are rejected with stable error codes.
- Playbook retrieval first filters by exact task class, then ranks normalized retrieval-term overlap descending, followed by playbook id/version in ordinal lexical order.
- Planner artifacts are not persisted or signed in August 1/2. Signing must land together with the planner envelope, validation-before-persistence rules, replay linkage, and compatibility tests.
- Any later decision to embed or reference planner artifacts in the manifest must explicitly decide whether manifest `0.3` can represent the reference or requires a version bump. The change must not be implicit.

## Change policy
- Any manifest or event-log schema change must bump the relevant schema version explicitly.
- New schema versions should land with validator updates, replay compatibility decisions, and tests in the same change.
