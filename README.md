# Processor Tycoon Agent

Let an AI agent (or a script) play Processor Tycoon through the **visible** game while you watch. A BepInEx plugin exposes the game's own UI as a local API, and the `pt-agent` command-line tool gives agents a compact, documented way to read the company, design CPUs, run research and production, price products and handle contracts, using native player actions only.

An unofficial fan mod for game version **0.2.16a5**. Not affiliated with or endorsed by the game's developers; it contains none of the game's files. It includes no AI model and connects to no AI provider: you bring your own agent (anything that can run local commands).

## Features

- **Game API:** semantic commands for CPU design and review, research, production, markets and finance, contracts and business deals, hardware, saves and settings, with verified results and clear errors.
- **Guarded time:** day-by-day advances that stop on events you care about (demand shifts, rival price cuts, popups, default).
- **Transparent play:** an on-screen feed and a cursor show what the agent does; the tray lets you pause the agent or slow it down at any time.
- **Generic UI API** for anything not mapped yet.
- **Multiplayer aware:** works with the [Processor Tycoon Multiplayer](https://github.com/krittik/processor-tycoon-multiplayer) mod (`pt-agent mp …`).

## Install

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (Windows x64, Mono) into the game folder (the one with `Processor Tycoon Beta.exe`) and start the game once.
2. Download the latest zip from [Releases](https://github.com/krittik/processor-tycoon-agent/releases/latest) and extract it into the game folder, with the game closed.
3. In a terminal in the game folder run `.\pt-agent.cmd prompt` and give the printed prompt to your agent, with your goal. Details: [START-HERE](Agent/PLAYER_START.md).

`.\pt-agent.cmd --help` and `.\pt-agent.cmd guide` explain everything offline; `status` diagnoses the install even with the game closed.

## Building from source

Prerequisites: Windows x64, .NET SDK 9, Processor Tycoon 0.2.16a5 with BepInEx 5 installed.

Clone into a short folder, for example the game folder as `Dev`: Windows limits paths to 260 characters, and a deeply nested clone or build fails with "Filename too long".

The projects compile against the game's own assemblies, which are never part of this repository. The game folder is resolved in this order: `-GameDir <path>` (scripts) or `-p:GameDir=<path>` (`dotnet build`); the environment variable `PT_GAME_DIR`; the repository's parent folder (clone it as `<game>/Dev`).

```powershell
cd Agent
.\build.ps1              # build the plugin and CLI and install them into the game folder
.\build.ps1 -NoDeploy    # build only (CLI in artifacts\agent\tools)
dotnet run --project tests\ProcessorTycoon.Tests   # offline tests, no game needed
.\package.ps1            # release zip in artifacts\dist\
```

More in [Agent/README.md](Agent/README.md); the API contract is [Agent/API_CONTRACT.md](Agent/API_CONTRACT.md) and the full guide is [Agent/AGENT_GUIDE.md](Agent/AGENT_GUIDE.md).

## Community, contributing and license

Questions, ideas and play sessions: [Discord](https://discord.gg/YKdTjge7J2). Issues and pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md). Released under the [MIT license](LICENSE).

By [Critique (Sevastyanoff)](https://discord.gg/YKdTjge7J2), in collaboration with [Claude Code](https://claude.com/claude-code) (Anthropic). Uses BepInEx (LGPL-2.1) and Newtonsoft.Json (MIT).
