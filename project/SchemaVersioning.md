# Replay Artifact Schema Versioning

## Current baseline
- Manifest schema version is locked to `0.2`.
- Event log schema baseline is `0.2`.
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
- Workflow selection is still CLI-driven and is not yet encoded into the manifest schema.

## Change policy
- Any manifest or event-log schema change must bump the relevant schema version explicitly.
- New schema versions should land with validator updates, replay compatibility decisions, and tests in the same change.
