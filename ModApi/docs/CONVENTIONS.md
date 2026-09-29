# Conventions

## Layout

Mods built on this API follow these rules, so their windows read like the game's and like each other's.

- **Window anatomy.** A title bar with the close button, a body and a footer strip. A main window's footer starts with its version link (it opens About) and ends with actions for the whole window. The title bar's × is the only Close; a dialog that needs an answer has its answers in the footer.
- **One primary action.** At most one call-to-action (blue) button per section: the most likely next step. It may move with the state (`Ui.Style`), for example to Resume while paused.
- **State first.** A main window starts with a status line (`Ui.Status`): a coloured dot and a short text. Body text shows state and actions only.
- **Explanations in tooltips.** Anything longer than a line goes into a tooltip on an info icon (`Ui.Info`, `Ui.Section`) or on the control it explains. A tooltip starts with a short header.
- **Choices are strips.** A choice between modes or preset values is a strip in the game's tab style (`Ui.Segments`), never a row of blue buttons. An on/off setting is the game's checkbox (`Ui.Check`).
- **Sizes.** Window titles 18; section headers 18 with a 20 px icon; body 15; secondary text 14 in the low-hierarchy colour; fine print 13; buttons and fields 28 px high with 15 px text.
- **Credits.** Name people without roles; each name links to its closest page.
- **Glyphs.** The game's font lacks many symbols (arrows, for example). Prefer words; `·`, `…` and `×` are safe.

## Runtime names shared between mods

Every mod compiles its own copy of the API, so copies cannot share types. Where they must cooperate, they agree on plain names:

| Name | Used for |
|---|---|
| `ModApiOverlay` (component type name) | Marks the root of every API overlay canvas. `Overlay` ignores such canvases when it looks for the game's main canvas; mods can use it to skip each other's UI. |
| GameObject `Processor Tycoon Mod API bottom bar` | Holds one marker per bottom-bar entry, named `<order>:<id>` with the entry's width. Entries sit left of every marker with a lower name. Orders in use: Agent 0, Multiplayer 10. |
| Email IDs | Each mod's letters use their own block: 1,000,000 + 10,000 × (hash of `Mail.Owner`). The game's own emails use 0. |
| Harmony IDs | `processortycoon.modapi.mail.<Owner>`; the patches exist only while the mod has letters. |
| A child named `ModApi` under the Email window's Accept/Decline buttons | The first mod to use these unused native buttons clears their prefab listeners and leaves this marker; later mods only add their own. |
| Canvas sorting orders | The game's windows are below 32000; Multiplayer 32600, Agent 32760. Pick your own value. |
