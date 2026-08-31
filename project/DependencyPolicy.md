# Dependency Policy

## Current target
- The solution currently targets `net10.0`.
- .NET 10 is the compatibility boundary for ASP.NET Core and Microsoft.Extensions packages in this repo.

## Package update rule
- Prefer stable package releases.
- Avoid preview/dev packages unless the repo intentionally moves to the matching preview SDK/runtime.
- Keep Microsoft.AspNetCore and Microsoft.Extensions package major versions aligned with the target framework major version.

## OpenAPI compatibility note
- `Microsoft.AspNetCore.OpenApi` is intentionally pinned to the compatible `10.x` line while the app targets `net10.0`.
- Keep `Swashbuckle.AspNetCore` on a stable release whose `Microsoft.OpenApi` dependency floor is patched for known advisories; `10.2.3` raises that floor to `2.7.5`.
- `11.x` preview packages expose the .NET 11 API/asset surface and caused the `net10.0` build to fail with missing OpenAPI transformer types.

## Future .NET 11 migration
- Revisit `Microsoft.AspNetCore.OpenApi` and related ASP.NET Core packages only when the project target framework moves to `net11.0`.
- Treat that as a framework migration, not a routine package patch.
