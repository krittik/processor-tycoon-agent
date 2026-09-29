# Processor Tycoon Agent mod

BepInEx plugin (`ProcessorTycoon.Mod`) plus the `pt-agent` CLI that lets an external agent play the visible game. Building and the game path: see the [repository README](../README.md).

```
Agent/
  src/ProcessorTycoon.Mod/   plugin          src/ProcessorTycoon.Cli/  pt-agent CLI
  src/Shared/                code compiled into both
  tests/ProcessorTycoon.Tests/  offline tests
  package/                   files shipped as-is in the player zip (pt-agent.cmd, play-only AGENTS.md)
  assets/                    icon sources (npm renderer, node_modules ignored)
  *.md                       guide (embedded in the CLI), API contract and coverage plan, changelog
  Agent.slnx, build.ps1, package.ps1
```

Player onboarding (no development project required): [PLAYER_START.md](PLAYER_START.md). `pt-agent --help` is compact; `guide` is the full offline reference, `prompt` emits a copyable game brief, `status` diagnoses even a closed game, and `launch` starts its visible client without starting a campaign. The first live CLI connection per game process prints the brief to stderr before its JSON response. `package.ps1` builds a Release plugin and a self-contained win-x64 CLI into `artifacts/dist/` as a zip that extracts into the game folder, excluding saves, configuration, runtime discovery files, source and project memory. BepInEx 5 Mono x64 is a separate runtime prerequisite, not bundled.

The development `AGENTS.md` in this directory holds the project rules; the game-root `AGENTS.md` shipped in the zip is play-only (its source is `package/AGENTS.md`). Changes: [CHANGELOG.md](CHANGELOG.md).

The implementation has **Game API + detailed Generic UI**. Eight game modules plus core routes cover CPU design, research/projects, production, markets/finance/Inspector, contracts/business, hardware, session/settings and desktop utilities. `product-pulse` and the CLI compositions `watch-advance` (one or several products), `products-pulse`, `window-tidy` and `price-probe` add compact, guarded observation; they have targeted runtime checks, not an independent full-run acceptance yet. Calendar/Wiki are dormant in this game build. Prefer simple game commands for ordinary play; Generic remains detailed discovery/fallback. See [API_CONTRACT.md](API_CONTRACT.md) and [API_COVERAGE_PLAN.md](API_COVERAGE_PLAN.md). Do not infer a profitable strategy from API correctness.

After building, launch the game and run these commands from the game directory:

```powershell
.\tools\pt-agent.exe guide
.\tools\pt-agent.exe status
.\tools\pt-agent.exe capabilities
.\tools\pt-agent.exe game cpu-preview --name "Trial One" --frequency-mhz 0.5
.\tools\pt-agent.exe game cpu-options architecture
.\tools\pt-agent.exe game cpu-select architecture --value CISC
.\tools\pt-agent.exe game projects-wait "Trial One"
.\tools\pt-agent.exe observe
.\tools\pt-agent.exe observe --scope Settings --changes
.\tools\pt-agent.exe events --since 0
.\tools\pt-agent.exe ui inspect HANDLE
.\tools\pt-agent.exe ui click HANDLE
.\tools\pt-agent.exe screenshot
.\tools\pt-agent.exe screenshot --output runtime\review.png
.\tools\pt-agent.exe quit
```

`quit` exits the game client without saving (`--save NAME` saves first); it refuses while a native dialog or time advance is active and never kills the process (fallback: it asks the window to close).

`guide`/`help` are embedded in the CLI and work offline. They explain Game API as the default for supported workflows, Generic fallback, examples, native input, hidden support, stale handles, operation results and pause etiquette. An external agent does not need AGENTS.md.

The plugin exposes HTTP on `127.0.0.1:17616`; discovery is in the game directory's `tools/endpoint.json`. Override via the plugin config and CLI `--endpoint` if necessary. Networking queues Unity work onto the main thread. The CLI is self-contained for Windows x64. There is no remote model or automatic LLM runner.

The bottom-bar item shows whether an agent is connected or paused. Click it, press F8, or use `agent panel --value true` for the Agent window: pause, Copy prompt, the delay between actions (presets; any value through `agent delay`) and feed visibility. The action feed (bottom right) fades out and lets clicks pass through; hover it to read the recent history. The UI comes from the Processor Tycoon Mod API: the game's colours, font and window style, with monochrome action icons. A decorative Agent cursor follows actual visible action targets without moving the OS cursor or delaying commands.

Game commands clean up prior safe agent-owned workspaces when switching domains. Player/pinned windows, unfinished drafts and native decisions are preserved; explicit `window-open`, `window-close` and dialog commands remain available. `--hidden` never silently flashes a window: non-invasive Generic reads support it, while unsupported navigation/mutations reject it explicitly. Agent pause is separate from game-time pause; the agent must not resume itself or change its action delay without an explicit user request.

The tray fades in a subtle hover background and briefly compresses its contents on press. Its settings overlay the stationary feed. Normal observations only add a compact notice for new local player input; detailed UI differences require `--changes`, and input/action history requires `events`. Simulation ticks do not spam the feed. Typed characters are never logged.

`screenshot` captures only the rendered game, including the overlay, to a temporary PNG replaced by the next capture. `--output PATH` preserves a copy. Use `observe` for gameplay and screenshots for visual QA; computer-use is not required for the normal workflow. The game must render a visible window: a Windows-hidden launch produced a black capture during testing.

## Build

Run from PowerShell (builds/deploys the plugin and publishes the CLI to the game folder's `tools/`; the game folder is resolved as described in the [repository README](../README.md)):

```powershell
.\build.ps1              # or: .\build.ps1 -GameDir 'D:\Games\Processor Tycoon'
.\build.ps1 -NoDeploy    # build only; CLI goes to artifacts/agent/tools, installed game untouched
.\package.ps1            # player zip in artifacts/dist/
```

The build automatically copies `ProcessorTycoon.Mod.dll` and its PDB to `<game>\BepInEx\plugins\ProcessorTycoon.Mod\`.

The plugin DLL is loaded by the running game, so a rebuilt plugin takes effect only after the game restarts (the copy fails while the game holds the file; use `-NoDeploy` and copy it later). The CLI in `tools/` can be replaced between commands.

## Tests

Offline tests (no game needed): `dotnet run --project tests/ProcessorTycoon.Tests`. They cover the shared display-number parser and line accounting (`Shared/`, compiled into both the plugin and the CLI), market-signal diffing, research/price checks, and run the CLI compositions and the built `pt-agent.dll` end to end against an in-process mock bridge (`tests/ProcessorTycoon.Tests/MockBridge.cs`). State files are redirected with `PT_AGENT_STATE_DIR`, so the real `tools/` files are never touched. Exit code 0 means all passed. They do not replace a live check of the native UI mappings.

## First game run

Start `Processor Tycoon Beta.exe`. BepInEx will create its configuration and log files. Verify this line in `<game>\BepInEx\LogOutput.log`:

```text
Processor Tycoon Agent 0.5.0; generic UI API: http://127.0.0.1:17616/
```

## Important paths

- Game code: `<game>\Processor Tycoon Beta_Data\Managed\Assembly-CSharp.dll`
- Plugin source: `src\ProcessorTycoon.Mod\Plugin.cs`
- Deployed plugin: `<game>\BepInEx\plugins\ProcessorTycoon.Mod\`
- BepInEx log: `<game>\BepInEx\LogOutput.log`

Open `Agent.slnx` or a `.csproj` in Visual Studio, Rider, or VS Code with C# tooling.

## Inspect game code

ILSpy CLI is a local .NET tool of the repository (`dotnet tool restore` in the repository root after cloning). Full decompilation into the ignored `reference/decomp/<game version>/`: `..\scripts\decompile.ps1`. Single type:

```powershell
dotnet tool run ilspycmd -t 'Full.Type.Name' '<game>\Processor Tycoon Beta_Data\Managed\Assembly-CSharp.dll'
```
