# Changelog — Processor Tycoon Agent

## 0.5.0 "Autopilot" — 2026-09-29

Plugin and `pt-agent` CLI now share one version.

- **Game over is reported:** when the company is bankrupt, every reply carries `companyBankrupt` (the native Game Over window, or a Multiplayer session keeping the player as a spectator); `status`, `desktop-read` and `game situation` say so too. Agents no longer keep playing a finished company behind a stale "Bankruptcy in 0 days".
- **Covered popups:** a popup hidden behind windows opened after it (for example Research Completed under the Research window or the CPU designer) is listed by `dialog-read` with its text and choices (`covered: true`) and by `window-list` under `coveredWindows`; `dialog-choose` brings it to the front first, as a click would. Research commands no longer open the full-screen Research window over an open popup.
- **Multiplayer:** `cpu-review` / `cpu-develop` ignore the process node's daily maturity % while a session clock runs (reviews failed with `context_changed` at 1 day/s); a `context_changed` names the field that moved. `research-funding-compare` works in a session (its shared clock cannot be paused).
- **About and credits:** the Agent Settings panel (F8 or the tray) shows the version and release name; click it for credits, license and the project page (`agent panel --value about`). `pt-agent --help` and `status` show the version too.
- The Agent window (bottom-bar item or F8) is drawn like the game's windows and the Multiplayer mod's: title bar, what the agent is doing, pause, feed and the delay between actions, and a footer whose version link opens About. It opens above the bottom-bar item; buttons press like the game's, with the hand cursor.
- The mod's own overlay (Agent window, About window, feed) never blocks agent commands.
- Released under the MIT license; the zip carries the license and the notices of what it redistributes (Newtonsoft.Json, the .NET runtime inside `pt-agent.exe`).

## Earlier versions

0.1–0.4.6 (2026-09-22 to 2026-09-27, before the public repository): the generic UI API, the Game API modules (CPU design and review, research, production, markets and finance, contracts and business, hardware, session and settings), the CLI compositions (situation, watch-advance, products-pulse, monthly-digest, price tools) and the Multiplayer interop.
