# Replay Artifact Schema Versioning

## Current baseline
- Manifest schema version is locked to `0.1`.
- Event log schema baseline is `0.1`.
- Replay currently supports exact-version artifacts only; it does not attempt cross-version migration.

## Compatibility rules
- The manifest `manifestVersion` must be supported exactly by the runtime validator.
- Every event log entry must use the same `runId` as the manifest.
- If an event log payload includes `manifestVersion`, it must match the manifest `manifestVersion`.
- Workflow selection is still CLI-driven and is not yet encoded into the manifest schema.

## Change policy
- Any manifest or event-log schema change must bump the relevant schema version explicitly.
- New schema versions should land with validator updates, replay compatibility decisions, and tests in the same change.
