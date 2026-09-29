# Changelog — Processor Tycoon Mod API

## 0.2.0 — 2026-09-29

- `TextBadge`: an icon after a text the game draws, following it; `NativeTooltip` (game layer): the game's own tooltip on it.
- `ActionPointer`: the decorative pointer that shows where automation acts (from the Agent mod), with any label.
- `BugReport`: the diagnostics zip and its window (from the Multiplayer mod), for any mod's files.
- `Sprites`: sprites from embedded PNGs. `UiDump`: the UI hierarchy dump used to match the game's styles.

## 0.1.0 — 2026-09-29

First version, extracted from the Multiplayer and Agent mods so both share one implementation.

- **UI layer** (no game assembly needed): theme colours and fonts, windows, buttons, inputs, scroll lists, tooltips, status lines, info icons, tab-style choice strips, checkboxes, icon buttons, links, version links, bottom-bar entries that line up across mods, a fading feed with hover focus and an input line, About windows with credits, main-menu entries, the Claude mark and an envelope icon drawn in code.
- **Game layer:** custom emails with answer buttons, kept out of saves; the game's notification popups.
- [docs/CONVENTIONS.md](docs/CONVENTIONS.md): the layout rules and the runtime names mods agree on.
