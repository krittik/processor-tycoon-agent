# Processor Tycoon Mod API

Building blocks for Processor Tycoon mods that look and behave like the game itself: native-looking windows and controls in the current theme (light or dark), tooltips, a bottom-bar entry, a fading on-screen feed with an optional input line, an About window with credits, a main-menu entry, custom emails with answer buttons and the game's notifications.

An unofficial modding library for game version **0.2.16a5**, used by [Processor Tycoon Multiplayer](https://github.com/krittik/processor-tycoon-multiplayer) and [Processor Tycoon Agent](https://github.com/krittik/processor-tycoon-agent). Not affiliated with or endorsed by the game's developers; it contains none of the game's files.

## How mods use it

The API ships no assembly: a mod **compiles its source in**, so players install nothing extra and every mod keeps the version it was built with. All types are `internal`, so several mods with their own copies never clash.

1. Vendor the source into your repository, for example with git subtree:

   ```powershell
   git subtree add --prefix ModApi https://github.com/krittik/processor-tycoon-mod-api main --squash
   git subtree pull --prefix ModApi https://github.com/krittik/processor-tycoon-mod-api main --squash   # later updates
   ```

2. Compile it into your plugin project:

   ```xml
   <!-- UI layer only: needs UnityEngine, UnityEngine.UI and Unity.TextMeshPro, no game assembly -->
   <Compile Include="..\..\ModApi\src\Ui\**\*.cs" Link="ModApi\%(RecursiveDir)%(Filename)%(Extension)" />
   <!-- Game layer too (emails, notifications): also needs Assembly-CSharp with Publicize="true"
        (BepInEx.AssemblyPublicizer.MSBuild) and HarmonyX (BepInEx.Core) -->
   <Compile Include="..\..\ModApi\src\**\*.cs" Link="ModApi\%(RecursiveDir)%(Filename)%(Extension)" />
   ```

3. `using ProcessorTycoonModApi;` (and `ProcessorTycoonModApi.Game` for the game layer).

## What is in it

**UI layer** (`src/Ui`, namespace `ProcessorTycoonModApi`). The game is read by type name (theme, colours, cursor), so this layer compiles without the game's assemblies.

| Piece | What it does |
|---|---|
| `Overlay` | A mod's canvas above the game, scaled like the game's UI at every resolution. Call `Tick()` every frame. |
| `Theme`, `Paint`, `Painted` | The current theme's colours, the game's fonts and sprites; graphics keep their theme colour when the player switches themes. |
| `Ui` | Builders: `Label`, `Header`, `Icon`, `Row`, `Column`, `Button` (plain or call-to-action, `Style` switches), `Input`, `Scroll`, `Section` (icon, title, info tooltip), `Info`, `Status` (dot and text), `Segments` (the game's tab style), `Check` (the game's checkbox), `IconButton`, `Link`/`Links`, `VersionLink`, `Mark` (an icon before a linked name). |
| `Tip` | Tooltips drawn like the game's, on the same canvas as the control. |
| `Window` | A window like the game's: title bar with close, body, footer strip (`Footer(version, openAbout)`), dragging, click to front, docking above a bottom-bar entry (`ShowAbove`). |
| `BarItem` | An entry in the bottom bar (icon and text). Entries of every mod line up by their `order` without overlapping. |
| `Feed` | Transparent lines in a bottom corner that fade out; hovering focuses them (history on a backdrop, mouse wheel scrolls back); an optional input line opens with Enter or `OpenInput` (chat). |
| `Credits`, `AboutWindow` | A mod's About window: name, version, license, GitHub and Discord links, description, credits with links and marks, what it is built with, disclaimer, Report an issue. |
| `MenuEntry` | An entry in the main menu (a copy of the game's Settings item); `MenuEntry.Press("New Game")` presses a native one. |
| `Marks` | Icons drawn in code: the Claude mark for credits, an envelope. |

**Game layer** (`src/Game`, namespace `ProcessorTycoonModApi.Game`).

| Piece | What it does |
|---|---|
| `Mail` | Your own emails in the game's Email app: title, sender line, rich-text body, up to two answers on the Email window's own Accept and Decline buttons; posting again updates a letter and brings it to the top. Letters are kept out of saves (a save keeps only an email's ID and date and would load them back empty) and `Clear()` removes them. Set `Mail.Owner` once. |
| `Notify` | The game's notification popup (message, second line, duration). |

## Conventions

Mods built on this API follow the same layout rules, so they feel like one game: see [docs/CONVENTIONS.md](docs/CONVENTIONS.md). It also lists the few names the copies in different mods agree on at runtime.

## Building and checking

Prerequisites: .NET SDK 9 and Processor Tycoon 0.2.16a5 with BepInEx 5. `dotnet build check` compiles the whole API as a mod would (warnings are errors). The game folder is found as in the mods: `-p:GameDir=...`, the environment variable `PT_GAME_DIR`, or the folder containing this repository or the one above it (`<game>\Dev\processor-tycoon-mod-api`).

## Community, contributing and license

Questions and ideas: [Discord](https://discord.gg/YKdTjge7J2). Issues and pull requests are welcome. Released under the [MIT license](LICENSE).

By [Critique (Sevastyanoff)](https://discord.gg/YKdTjge7J2), in collaboration with [Claude Code](https://claude.com/claude-code) (Anthropic).
