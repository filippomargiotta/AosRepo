# AgenticOrchestrationSoftware

## Replay CLI

The replay CLI validates event-log integrity, checks compatibility, and replays the `hello` workflow artifacts.

Run from repo root:

```bash
dotnet run --project source/Aos.ReplayCli -- \
  --workflow hello \
  --manifest source/Aos.WebApi.Tests/Golden/hello-workflow-v1/manifest.json \
  --eventlog source/Aos.WebApi.Tests/Golden/hello-workflow-v1/eventlog.jsonl \
  --hmac-key golden-hmac-key
```

Expected result: exit code `0` and a replay verification success message when artifacts match.

Current schema/versioning rules for replay artifacts are documented in `project/SchemaVersioning.md`.
