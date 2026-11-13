# Dependency Policy

## Current target
- The solution targets `net11.0` (.NET 11 GA, SDK `11.0.100`, pinned in `source/global.json`).
- .NET 11 is the compatibility boundary for ASP.NET Core and Microsoft.Extensions packages in this repo.
- The sandbox worker image is built from `mcr.microsoft.com/dotnet/sdk:11.0` / `runtime:11.0`, both pinned by digest in
  `source/Aos.SandboxWorker/Dockerfile`. Re-pin the digests deliberately; do not float the tag.

## Package update rule
- Prefer stable package releases.
- Avoid preview/dev packages unless the repo intentionally moves to the matching preview SDK/runtime.
- Keep Microsoft.AspNetCore and Microsoft.Extensions package major versions aligned with the target framework major version.
- Do not reference packages the shared framework already provides (NU1510); `Microsoft.Extensions.Options` was removed from `Aos.Core` for this reason.
- Check `dotnet list source/Aos.sln package --outdated`, `--vulnerable --include-transitive` and `--deprecated` after each bump.

## Migration record (2026-11-13, net10.0 -> net11.0)
- Updated: `Microsoft.AspNetCore.OpenApi` 11.0.0, `Swashbuckle.AspNetCore` 11.0.1, OpenTelemetry packages 1.21.0,
  `Scalar.AspNetCore` 2.21.0, `Microsoft.NET.Test.Sdk` 18.12.0, `xunit.runner.visualstudio` 4.0.1.
- Result: build clean (0 warnings), Release 177 passed / 8 skipped, container-enabled 187 passed, golden 19/19, container gate PASS
  (v1 10/12, v2 12/12, replay 24/24). No vulnerable packages.
- Retained evidence under `project/evidence/2026-09` and `2026-10` was produced on .NET 10 and stays valid: replay verifies signed
  artifacts, not the producing runtime.

## Known deferrals
- `xunit` stays on `2.9.3`, the latest stable of the v2 package. NuGet marks it legacy in favour of `xunit.v3`, which requires the
  Microsoft.Testing.Platform runner (`dotnet test` CLI and `global.json` changes) and fixing xUnit1051 analyzer warnings. Treat that as
  a separate test-infrastructure migration.
