# November 2026 evidence

First-half-November repeated/concurrent runs of the pinned twelve-case dataset (Planner v2, hardened `container-v1`, .NET 11).
`repeat-c1-report.json` (serial) and `repeat-c4-report.json` (concurrency 4) each cover 5 iterations x 12 cases = 60 runs:
per-case outcome-signature stability, stage failures, latency split into planner overhead / tool duration / sandbox overhead,
and CUSUM drift series. The 120 run artifact sets were replayed during the runs (120/120 PASS) but are not retained;
the report is the evidence.

```bash
dotnet run --project source/Aos.ReplayCli -c Release --no-build -- repeat-diagnostics \
  --snapshot-dir source/Aos.RepositoryFixture/Repository --snapshot-dir-v2 source/Aos.RepositoryFixture/RepositoryV2 \
  --cases-root source/Aos.WebApi.Tests/DiagnosticsCases --output-dir /tmp/aos-november-repeat \
  --hmac-key golden-hmac-key --executor container-v1 --runs 5 --concurrency 4
```

Latency is host-dependent (macOS ARM64 / OrbStack) and is not gated.
