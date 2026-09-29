# Direct fast adapter (0.2.16a5)

Explicit `Request.HeadlessFast` executes `Fast/FastGameApi` on the existing Unity main-thread request queue. Reads and native mutations finish in that call, with a coherent date/frame and numeric data. Ordinary `IGameModule` UI workflows are unchanged. Unsupported commands never fall back. Discovery/status advertise `headlessFastVersion:1`; the CLI checks it before dispatch so old mods cannot ignore the flag and run visible actions. A local discovery/brief marker avoids repeated HTTP preflights in steady state.

## Native mapping

- Finances: `MoneyBalance.GetBalance()` saved daily DTO, `ICompany.MoneyAmount`, `CentralBank.DebtLimit` and native default/bankruptcy state. Expenses retain native signs; monthly-equivalent rates are daily ×30.
- Products: own `Owner.GetCpus()`, native price/name/sale flags and retirement. Capacity uses factory plus manual/contract/outsource assignments. Existing production sliders are synchronized passively so native UI updates cannot overwrite model changes; no window is opened.
- Research: `ResearchDataProvider`, `ResearchSector.TechnologyAsResearch`, locks, native start/stop/funding and `ResearchMath`. Existing research UI bindings are synchronized without navigation. Listed spending and spending after native cost modifiers are separate values.
- CPU draft: independent adapter storage; owned hardware and currently available architectures. `CpuMath` and `ProductionMath` calculate native specs/cost/yield; shared math modifiers/cache settings are restored after calculations. Cache raw units are4/16/64 KB for L1/L2/L3. Frequencies/IPS are native thousands, hence MHz/MIPS divide by1000. Frequencies follow `SliderUI.Setup(5,max/20,max)` discrete choices.
- Development: same CPU DTO/`Project.Initialize`/`TimerScheduler.Schedule` lifecycle as `CreatorCpu.CreateCpu`, without creating a visual project row. Completion uses native IDs and `ProjectCompletedHandler.AddCpu`. Review is bound to draft/date/cash/cost, reason and acknowledged risk kinds.
- Release: same ownership/date/price/`Company.AddCpu`/completion removal sequence as `ProjectReleaseWindow.ReleaseCpu`; clears an already bound release form so its Update cannot rename the released CPU. No release window is opened by the adapter.
- MP: existing ownership, native entity/project DTO capture and tick hooks replicate changes. Local confirmation does not promise immediate visibility on another peer; deltas arrive on subsequent host ticks.

The implementation uses target game assemblies for compilation only. They are not shipped. Native reference sources above were inspected in the sibling Multiplayer repository's ignored `reference/decomp/0.2.16a5` tree.

## Validation (disposable copies only)

Testbeds `artifacts/testbeds/fast-api` and `fast-peer`, ports17816/17817, multiplayer TCP17818. Fresh1975 Impossible campaigns, no cheats; main installation and four-player tournament untouched.

- Native visible creator and fast draft at CISC/DIP1970/10µm/DIP SRAM,15mm²,0.5MHz, zero cache matched IPC0.0446875,2.61W,40.49°C,80.95% yield and displayed unit cost. Fast CPU development spent native funds, reached testing at day100, completed at day101, entered the native release lifecycle and released successfully.
- Six direct CPU variants calculated in6.27ms without changing draft. Manual production refused while automation was enabled; disabling automation and assigning two lines produced275 units on a subsequent native day.
- Peer fast research/funding25% remained correct after102 shared days. Peer CPU development/release/new price122 reached host catalog after subsequent ticks. Monthly checkpoint reported `day1916 ok`, resyncs0. Both companies had native difficulty5.
- Locked technology, invalid frequency/price, unsupported catalog parameters/commands refused. Agent pause blocked mutation;1000ms action delay refused a second immediate price edit and did not enqueue it. Test settings restored.
- `situation` at30FPS,10 CLI invocations: average218.3ms wall time (192.1–307.9ms),4.81ms adapter (3.46–9.85ms). Ordinary CLI situation in the same testbed:10120.6ms. These are local measurements, not latency guarantees.
- Offline CLI regressions verify a single fast semantic request without UI composition/operation polling and refusal of an older bridge before mutation. Existing offline tests cover ordinary behavior.

## Coverage limits

Only capabilities/guide-listed commands have direct backends. Contracts/deals, licensed foundries, factory expansion, historical charts, statistics, settings/save/session navigation and guarded time advance retain ordinary paths and reject fast mode. Fast drafts are separate from the visible editor; fast projects may lack ordinary UI rows until rebuilt. Market shares use last-day rates/current market size; full native tooltip preferences and trend/runway compositions are not claimed. Fast schemas intentionally differ from UI schemas. See embedded `AGENT_GUIDE.md` for mode choice, visual feedback and parameter details.
