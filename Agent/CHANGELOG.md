# Changelog — Processor Tycoon Agent

## 0.5.0 "Autopilot" — 2026-09-29

Plugin and `pt-agent` CLI now share one version.

- **Game over is reported:** when the company is bankrupt, every reply carries `companyBankrupt` (the native Game Over window, or a Multiplayer session keeping the player as a spectator); `status`, `desktop-read` and `game situation` say so too. Agents no longer keep playing a finished company behind a stale "Bankruptcy in 0 days".
- **Covered popups:** a popup hidden behind windows opened after it (for example Research Completed under the Research window or the CPU designer) is listed by `dialog-read` with its text and choices (`covered: true`) and by `window-list` under `coveredWindows`; `dialog-choose` brings it to the front first, as a click would. Research commands no longer open the full-screen Research window over an open popup.
- **Multiplayer:** `cpu-review` / `cpu-develop` ignore the process node's daily maturity % while a session clock runs (reviews failed with `context_changed` at 1 day/s); a `context_changed` names the field that moved. `research-funding-compare` works in a session (its shared clock cannot be paused).
- **About and credits:** the Agent window's footer shows the version and release name; click it for credits, license and links to GitHub and Discord (`agent panel --value about`). `pt-agent --help` and `status` show the version too.
- **Agent window** (bottom-bar item or F8), drawn like the game's windows and laid out like the Multiplayer mod's: a status line (connected, paused or no agent), **Pause agent** / **Copy prompt** (the likely next step is the blue one), the delay between agent actions as presets (Off to 1 s) and a checkbox for the on-screen action feed; explanations are in tooltips. It opens above the bottom-bar item, which now reads just "Agent · Connected" (or Paused, Offline).
- **Copy prompt** in the Agent window copies a short starting message for an agent: the path of this installation's CLI and the instruction to read the full brief with `pt-agent prompt`, ending in "My goal:" for you to fill in. Without a goal, the agent first learns the game and the CLI, then asks you what you want before it starts, loads or changes a campaign (the same rule is in the `pt-agent prompt` brief).
- The mod's own overlay (Agent window, About window, feed) never blocks agent commands.
- Released under the MIT license; the zip carries the license and the notices of what it redistributes (Newtonsoft.Json, the .NET runtime inside `pt-agent.exe`).

## Earlier versions

0.1–0.4.6 (2026-09-22 to 2026-09-27, before the public repository): the generic UI API, the Game API modules (CPU design and review, research, production, markets and finance, contracts and business, hardware, session and settings), the CLI compositions (situation, watch-advance, products-pulse, monthly-digest, price tools) and the Multiplayer interop.
