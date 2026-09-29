# Changelog — Processor Tycoon Mod API

## 0.3.1 — 2026-09-29

- `Feed`: the history opens from the input line or a `FocusOn` element (the lines only in a feed with neither), no longer when the pointer passes over the lines: they may lie on a game window or the Pause Menu, which the backdrop then covered.
- `GameScreen.PauseMenuOpen` (game layer): hide an overlay while the Pause Menu covers the left side of the screen.

## 0.3.0 — 2026-09-29

- `Feed`: `InputAlwaysVisible` keeps the input line on screen below the lines (dim, with `IdleHint`; a click or Enter starts typing); `FocusOn(rect)` lets another element (a bottom-bar entry) open the history on hover, so it stays reachable after every line has faded; `ScrollBy`, and Page Up / Page Down while typing; a thin bar shows the scroll position; a reader scrolled back keeps their place when new lines arrive; the history backdrop is darker, readable over desktop icons. `InputOpen` is now `Typing`.

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
