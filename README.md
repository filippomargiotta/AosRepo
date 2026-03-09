# AgenticOrchestrationSoftware

## Replay CLI (baseline)

The replay CLI currently validates and replays the `hello` workflow artifacts.

Run from repo root:

```bash
dotnet run --project source/Aos.ReplayCli -- \
  --workflow hello \
  --manifest source/Aos.WebApi.Tests/Golden/hello-workflow-v1/manifest.json \
  --eventlog source/Aos.WebApi.Tests/Golden/hello-workflow-v1/eventlog.jsonl
```

Expected result: exit code `0` and a replay verification success message when artifacts match.
