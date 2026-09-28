# Reference

Read-only material about the game, used to understand internals. Nothing here is compiled into the mods.

- `decomp/<game version>/` — decompiled `Assembly-CSharp.dll` (ILSpy project layout: one folder per namespace). **Ignored by git and never shipped**: it is the game developer's code. Regenerate with `scripts/decompile.ps1`; after a game update, run it again and diff the two version folders to see what changed.

Findings worth keeping go into the mods' docs (e.g. `Multiplayer/docs/GAME_INTERNALS.md`) with file references into `decomp/<version>/`.
