# Repository instructions

This repository holds the **Processor Tycoon Agent mod** (plugin + `pt-agent` CLI) in [Agent/](Agent/). Read [Agent/AGENTS.md](Agent/AGENTS.md) before working on it.

For PLAYING the game through the mod, no source is needed: use `./pt-agent.cmd --help` from the game folder.

- Never hard-code the game path. Scripts dot-source `scripts/common.ps1` (`Resolve-GameDir`); projects use `$(GameDir)` / `$(GameManagedDir)` from `Directory.Build.props`.
- Generated output goes to `bin/`, `obj/` or `artifacts/` only. Decompiled game code (`reference/decomp/<version>/`) and game assemblies are never committed or shipped.
- Release zips mirror the game folder and are produced only by `Agent/package.ps1`.
- Experiments run in disposable game copies (`artifacts/testbeds/`, `scripts/new-testbed.ps1`), not in someone's main game folder.
