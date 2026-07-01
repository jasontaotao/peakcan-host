# Release Notes — PeakCan Host v1.2.0

**Date:** 2026-06-25

## Summary

v1.2.0 closes the three follow-up items the v1.1.0 spec carve-out
(`docs/superpowers/specs/2026-06-25-v1-1-0-uds-ui-and-key-provider-design.md` §9,
items D1 / D2 / D3): the 279-line monolithic `UdsViewModel` is replaced
with a thin orchestrator holding four panel ViewModels (Session / DID /
Routine / DTC), and the UDS tab UI is rewritten so the DID / Routine
tabs are backed by `DataGrid`s bound to the v1.1.0-shipped `DidDatabase`
and `RoutineDatabase` instead of free-text `TextBox` inputs.

## New Features

- **4-panel UDS orchestrator** — `UdsViewModel` shrinks from 279 lines to
  ≤80 lines and owns no `UdsClient` interaction. Each panel VM owns its
  own `RelayCommand`s and shares a structured `OutputLog` via the new
  `IUdsPanel.AttachLog` hook.
- **`UdsLogLine` record** — replaces v1.1.0's `ObservableCollection<string>`
  log with `(Timestamp, Level, Message)` so the new RichTextBox log can
  color-code by severity without re-parsing.
- **RichTextBox output log with color-coded severity** — Info = default
  text, Warn = `#DCDCAA` (VS Code Warning Yellow), Error = `#F48771`
  (VS Code Error Red); auto-trims oldest inline when over 500 lines.
- **DIDs tab DataGrid** — 5 built-in DIDs from v1.1.0 (`DidDatabase`)
  appear automatically; custom DIDs from `%APPDATA%\PeakCan.Host\uds-dids.json`
  appear after restart. Right pane shows selected DID's length +
  write-hex textbox + Read/Write buttons + LastResult.
- **Routines tab DataGrid** — Routines from
  `%APPDATA%\PeakCan.Host\uds-routines.json` appear in the grid;
  Start / Stop / Query buttons drive `RoutineControlAsync` sub-functions
  0x01 / 0x02 / 0x03.
- **DTCs tab DataGrid** — preserved from v1.1.0 (DTC code / status /
  description columns); Read / Clear buttons.
- **Top Session header strip** — Default / Extended / Programming session
  buttons + TesterPresent checkbox (with 2s background loop) +
  SecurityAccess (Level 1) button + status text.

## Bug Fixes

- **`UdsViewModel.TesterPresentCommand`** — v1.1.0 sent exactly one
  TesterPresent frame on each click. v1.2.0 `SessionPanelViewModel.ToggleTesterPresentCommand`
  runs a cancellable background loop at 2s interval; checkbox reflects state.

## Test Results

- Baseline v1.1.0: 477 pass + 6 SKIP + 0 fail (Core 207 + App 196 + Infrastructure 74).
- v1.2.0: **514 pass + 6 SKIP + 0 fail** (Core 207 + App 233 + Infrastructure 74).
  Delta **+37 pass** (App +37 from Tasks 1–7 new VM + DI tests; Core and
  Infrastructure unchanged). 6 SKIP unchanged (4 hardware-dependent App +
  2 hardware-dependent Infrastructure).
- Coverage: all new VM code ≥80% line coverage (project default floor).

## Commits Since v1.1.0

```
e6cfb98 feat(uds): rewrite UdsView.xaml to spec §4.9 (Session strip + 3 tabs + RichTextBox log)
c805dd1 refactor(uds): wire v1.2.0 4-panel DI + delete old monolith
194f4f4 docs(spec): v1.2.0 §9.1 — 2nd pre-existing flake (DbcDecodeBackgroundServiceTests)
f3eea70 refactor(uds): add 4-panel orchestrator alongside monolith
320fbcc docs(plan): Task 6 defers monolith deletion to Task 7
31537ea docs(spec): v1.2.0 §3.1 enumerates 6 'virtual' UdsClient methods
a14f80b feat(uds): add DtcPanelViewModel (Read/Clear DTCs with 4-byte parser)
da06611 docs(spec): v1.2.0 §3.1 + §9.1 paperwork from Task 4 review
1342df4 feat(uds): add RoutinePanelViewModel (Start/Stop/Query from RoutineDatabase)
cd41562 docs(spec): v1.2.0 §3.1 + §4.6 amendments + §9.1 DidDatabase NRE backlog
3eb4181 feat(uds): add DidPanelViewModel (Read/Write DIDs from DidDatabase)
b660687 docs(plan): v1.2.0 Tasks 3-5 briefs reference canonical RecordingUdsClient
9c2f2b6 docs(spec): v1.2.0 §3.1 testability hook + §6 InvalidOp clarification
e42204c feat(uds): add SessionPanelViewModel (5 RelayCommands + 4-catch SecurityAccess)
3589a3e feat(uds): add v1.2.0 foundation types (UdsLogLine, IUdsPanel, Row types)
```

## Known Limitations / v2.0 Backlog

- J1939 / CANopen (v2.0).
- Linux + SocketCAN cross-platform (v2.0).
- OEM-specific key algorithms remain out of scope; OEMs wire their
  `IKeyDerivationAlgorithm` at deploy time via DI.