# Play with an agent

The Agentic mod lets a local AI agent play Processor Tycoon through a CLI while you watch the real game. It does not include an AI model or connect to a provider on your behalf. Your agent needs permission to run local commands (for example, a local Codex task).

## Install

For Windows x64 / Processor Tycoon 0.2.16a5, with BepInEx 5 Mono x64 already installed. This mod package does not bundle the BepInEx loader. Extract the package into the directory containing `Processor Tycoon Beta.exe`, with the game closed. The distribution includes a self-contained CLI: no .NET installation, SDK, Unity editor, source code, development project or AGENTS.md is required.

## Give the agent one starting point

Open a terminal in the game directory and run:

```powershell
.\pt-agent.cmd prompt
```

Or, in the game, open the Agent window (the Agent item in the bottom bar, or F8) and click **Copy prompt**: it copies the same prompt, headed by where the CLI is on this PC.

Copy the printed prompt to your agent and add your goal, for example: “Start a new game on Normal and try to dominate the market.” The prompt describes the game and its economic mechanics, but does not include a machine-specific CLI path. Give the agent the CLI path separately if it does not already have access to it.

On the first successful CLI connection to each game process, the same prompt is printed to standard error before the command response. Later commands in that game process do not repeat it. An explicit `prompt` command always prints it to standard output, even when the game is closed. JSON command responses remain on standard output.

Alternatively, tell the agent: “Play Processor Tycoon through `FULL_PATH_TO_GAME\tools\pt-agent.exe`; begin with --help and status.”

The agent learns the API from the tool itself. It checks whether the game is closed, the mod is reachable, or the main menu is open without a campaign. `launch` can open the visible game; new/load remain separate explicit actions. Do not run multiple agents controlling the same game at once.

You can stop agent actions through the Agent Settings tray. Agent pause is separate from game-time pause. The agent must not resume itself or change its action delay without your explicit request.

## Manual diagnostics

```powershell
.\pt-agent.cmd status
.\pt-agent.cmd launch
.\pt-agent.cmd guide
```

`status` succeeding does not mean a campaign is loaded: read `game.state` and `game.next`. If the process exists but the bridge is unavailable, wait for startup or inspect `BepInEx/LogOutput.log`; do not launch a duplicate game. Never share saves, logs or source code just to bootstrap an agent.

Without a CLI path, a project instruction, or a registered tool, an arbitrary agent cannot reliably discover an installed mod. The copyable prompt is the portable onboarding path; MCP/automatic tool registration is not part of this distribution.
