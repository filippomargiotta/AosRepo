# September 2026 evidence

`container/report.json` compares identical frozen inputs with recovery disabled/enabled.
The twelve scenario directories contain signed manifests, HMAC-chained logs, and replay
descriptors. The signing key is the public fixture key `golden-hmac-key`; these are development
evidence artifacts, not production credentials.

`verification.json` records host/runtime context, worker image identity, suite results, and
the replay-without-snapshot check. Reproduction commands and interpretation are in
`project/deliverables/2026-09.md` and the repository README.

To repeat the offline proof, build the Release solution and run from the repository root:

```bash
python3 scripts/stage-replay-only.py \
  --binaries source/Aos.ReplayCli/bin/Release/net10.0 \
  --output /tmp/aos-september-replay-only
docker run --rm --network none --read-only --cap-drop ALL \
  --security-opt no-new-privileges:true --user 1654 \
  --env DOTNET_EnableDiagnostics=0 \
  --mount "type=bind,source=/tmp/aos-september-replay-only,target=/replay,readonly" \
  --mount "type=bind,source=$PWD/project/evidence/2026-09/container,target=/evidence,readonly" \
  --entrypoint dotnet \
  mcr.microsoft.com/dotnet/runtime:10.0@sha256:68d35011fe04a39cca38208d392ed48f2df15653633dca16dbc4582d07342b9f \
  /replay/Aos.ReplayCli.dll evaluate --scenarios-root /evidence --hmac-key golden-hmac-key
```

Expected: twelve PASS results. The only mounts are replay binaries and recorded evidence;
the snapshot, sandbox worker/test assembly, Docker socket, and network are unavailable.
The staging script removes benchmark-only worker dependencies from the staged host manifest;
the original Release output is unchanged.
