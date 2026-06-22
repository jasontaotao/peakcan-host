# Release Notes — PeakCan Host v0.10.1

**Date:** 2026-06-22

## Summary

PeakCan Host v0.10.1 is a feature release that adds real-time signal charting,
Trace/DBC polish, and various UI improvements. It builds on the MVP v0.1.0
foundation with significant usability enhancements.

## New Features (v0.8.0 – v0.10.1)

### Signal Chart (v0.8.0)
- **Real-time OxyPlot chart** — check "Plot" checkbox on any signal row to
  add it to a time-series chart. Multiple signals render with distinct
  Tableau 10 palette colors.
- **Rolling 30-second window** with auto-scrolling X axis.
- **30 Hz render timer** — buffer coalescing decouples ~8 kfps decode rate
  from chart refresh rate.
- **Plot All / Plot None** — one-click buttons to select/deselect all signals.
- **Signal statistics panel** — shows min/max/avg/n for each charted signal.
- **Export CSV** — exports charted signal data to CSV file.
- **Clear Chart** — removes all charted signals.

### Trace Polish (v0.9.0 – v0.10.0)
- **Frame type column** — shows "FD", "ERR", or "" (standard).
- **Error frame highlight** — error rows get red background (#FFCDD2).
- **FD frame highlight** — FD rows get blue background (#E3F2FD).
- **Highlight by CAN ID** — hex prefix highlight with yellow background (#FFFDE7).
- **Errors-only filter** — checkbox to show only error frames.
- **Pause** — checkbox to freeze display while counters still update.
- **Clear button** — clears all trace entries and resets counters.
- **Export CSV** — exports current trace entries to CSV file.
- **Auto-scroll** — automatically scrolls to newest rows when at bottom.
- **Message ID frequency stats** — top-N message IDs by count with percentages.
- **Total frame counter** — shows total frames received on filter bar.

### DBC Polish (v0.9.0 – v0.10.1)
- **Message search** — search bar filters messages by name or sender.
- **Signal details** — expand a message row to see its signal list with
  name, unit, mux status, and bit layout.
- **Export CSV** — exports DBC messages to CSV file.
- **Message/signal counts** — shows total messages and signals in toolbar.

### Signal Search (v0.9.1)
- **Signal search filter** — search bar filters signals by message or signal name.

## Bug Fixes

- **v0.8.0 hotfix** — Upsert preserves IsSelected checkbox state across frame
  updates (previously reset to false on every frame).

## Test Results

- **407 pass + 6 SKIP** (Core 155 + Infrastructure 74 + App 178)
- 6 SKIP: 2 hardware-dependent, 1 flaky background service, 3 hardware-dependent App tests
- 0 fail

## Commits Since v0.7.0

24 commits covering signal chart, trace polish, DBC polish, and UI improvements.

## Known Limitations

- Scripting automation (CodeMirror 6 + sandboxed script engine) deferred to v1.0.
- UDS diagnostic stack deferred to v1.1.
- J1939 / CANopen deferred to v2.0.
