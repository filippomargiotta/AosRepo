# October 2026 evidence

`container/report.json` compares Planner v1 (recovery disabled) and v2 on the twelve-case dataset
(fixture v1 for RDI-01/02/06–12, fixture v2 for RDI-03/04/05), with exact + semantic scoring, per-stage
results, named gate checks, and a regression comparison against the retained September report.
`container/calibration.json` holds the stored confidence observations, reliability bins, Brier/ECE, and
thresholds. `container/planner-v1` and `planner-v2` contain the signed manifests, HMAC-chained logs, and
replay descriptors (24 runs). The signing key is the public fixture key `golden-hmac-key`; these are
development evidence artifacts, not production credentials.

`verification.json` records host/runtime context, worker image identity, suite results, and the offline
replay check.

Repeat the run (Docker/OrbStack required, worker image built as in the repository README):

```bash
dotnet run --project source/Aos.ReplayCli -c Release --no-build -- evaluate-diagnostics \
  --snapshot-dir source/Aos.RepositoryFixture/Repository \
  --snapshot-dir-v2 source/Aos.RepositoryFixture/RepositoryV2 \
  --cases-root source/Aos.WebApi.Tests/DiagnosticsCases \
  --output-dir /tmp/aos-october-evaluation --hmac-key golden-hmac-key --executor container-v1 \
  --baseline project/evidence/2026-09/container/report.json \
  --calibration project/evidence/2026-10/container/calibration.json
```

Offline replay of the 24 retained runs uses the same staging/`docker run --network none` procedure as
`project/evidence/2026-09/README.md`, with `container` mounted from this directory. Expected: 24 PASS.

Interpretation limit: all 14 diagnosis claims were correct, so the stored accept threshold (0.85) is the
lowest observed correct confidence and is not validated against false accepts.
