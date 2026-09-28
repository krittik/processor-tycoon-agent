# Project instructions

This folder is the **Agent mod**: `src/ProcessorTycoon.Mod` (BepInEx plugin), `src/ProcessorTycoon.Cli` (`pt-agent`), `src/Shared` (compiled into both) and `tests/ProcessorTycoon.Tests`. Repository-wide conventions: [../AGENTS.md](../AGENTS.md). The separate [Multiplayer mod](https://github.com/krittik/processor-tycoon-multiplayer) is reached only through its public `MpApi`, by reflection (`MultiplayerInterop`).

For PLAYING rather than developing: use `./pt-agent.cmd --help`, then `status`. `launch` starts the visible client if needed; `guide` is the full offline API reference. Prefer the semantic Game API.

## Rules

- The mod plays like a human: use native player actions and player-visible data. No arbitrary reflection or hidden game-state access; report unsupported or mismatched UI honestly instead of guessing.
- Keep version-sensitive UI mappings isolated in the game modules. Preserve game files and the player's saves.
- CLI users cannot be assumed to have this file: explain method selection, differences and use cases in the CLI help and `AGENT_GUIDE.md` (embedded in the CLI). Do not advertise planned commands as shipped ([API_CONTRACT.md](API_CONTRACT.md), [API_COVERAGE_PLAN.md](API_COVERAGE_PLAN.md)).
- Tests must never act on a local game process; the offline tests use a mock bridge. Live checks run serially in a disposable game copy, never with competing clients.
- Record user-visible changes in [CHANGELOG.md](CHANGELOG.md). Keep simple statements and method arguments on one line when readable; avoid excessive defensive code and redundant tests.
