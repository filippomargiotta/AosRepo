# AgenticOrchestrationSoftware

## Replay CLI

The replay CLI validates signed manifest integrity, validates event-log integrity, checks artifact compatibility, and replays the `hello` workflow artifacts.

Run from repo root:

```bash
dotnet run --project source/Aos.ReplayCli -- \
  --workflow hello \
  --manifest source/Aos.WebApi.Tests/Golden/hello-workflow-v1/manifest.json \
  --eventlog source/Aos.WebApi.Tests/Golden/hello-workflow-v1/eventlog.jsonl \
  --hmac-key golden-hmac-key
```

Expected result: exit code `0` and a replay verification success message when artifacts match.

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

Current schema/versioning rules for replay artifacts are documented in `project/SchemaVersioning.md`.
