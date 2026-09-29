# Repository instructions

This repository is the **Processor Tycoon Mod API**: source that mods compile in (no assembly of its own). Read [README.md](README.md) and [docs/CONVENTIONS.md](docs/CONVENTIONS.md) first.

- `src/Ui` must compile without the game's assemblies: read game types by name (reflection) and only for looks. `src/Game` may use the publicized Assembly-CSharp and HarmonyX.
- Everything is `internal` and namespaced `ProcessorTycoonModApi` / `ProcessorTycoonModApi.Game`, so copies in several mods never clash. Cross-mod behaviour goes only through the runtime names in CONVENTIONS.md; changing one breaks mods built on older copies, so add new names instead.
- Harmony patches: prefix/postfix only, installed only while they are needed, removed afterwards.
- Add to the API what another mod could use as is; keep mod-specific wording and behaviour in the mods. Record user-visible changes in [CHANGELOG.md](CHANGELOG.md).
- `dotnet build check` must pass (warnings are errors). Mods pick up changes with `git subtree pull --prefix ModApi … main --squash`.
- Never commit decompiled game code or game assemblies.
