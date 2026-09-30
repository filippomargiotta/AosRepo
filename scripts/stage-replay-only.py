#!/usr/bin/env python3
"""Stage the existing CLI for offline replay without its benchmark worker/fixture dependency."""
import argparse
import json
import shutil
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--binaries", type=Path, required=True)
parser.add_argument("--output", type=Path, required=True)
args = parser.parse_args()
files = {
    "Aos.ReplayCli.dll", "Aos.ReplayCli.runtimeconfig.json", "Aos.ReplayCli.deps.json",
    "Aos.Core.dll", "Microsoft.Extensions.Options.dll", "Microsoft.Extensions.Primitives.dll",
    "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
}
if args.output.resolve() == args.binaries.resolve():
    parser.error("Output must be a separate replay-only directory.")
if args.output.exists() and any(p.name not in files or not p.is_file() for p in args.output.iterdir()):
    parser.error("Output directory contains files outside the replay-only package.")
if any(not (args.binaries / name).is_file() for name in files):
    parser.error("Build the Release Replay CLI before staging.")
args.output.mkdir(parents=True, exist_ok=True)
for name in sorted(files - {"Aos.ReplayCli.deps.json"}):
    shutil.copyfile(args.binaries / name, args.output / name)

# Benchmark-only project dependencies must also be removed from the staged host manifest.
# The original binaries and their build output remain unchanged.
manifest = json.loads((args.binaries / "Aos.ReplayCli.deps.json").read_text())
excluded = {"Aos.SandboxWorker", "Aos.RepositoryFixture"}
for target in manifest["targets"].values():
    for key in list(target):
        if key.split("/")[0] in excluded:
            del target[key]
    for package in target.values():
        for dependency in list(package.get("dependencies", {})):
            if dependency in excluded:
                del package["dependencies"][dependency]
for key in list(manifest["libraries"]):
    if key.split("/")[0] in excluded:
        del manifest["libraries"][key]
(args.output / "Aos.ReplayCli.deps.json").write_text(json.dumps(manifest, indent=2) + "\n")
print(f"Staged {len(files)} replay files; no worker, test runner, or repository snapshot: {args.output}")
