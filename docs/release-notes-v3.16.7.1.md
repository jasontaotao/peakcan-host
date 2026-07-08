# Release Notes v3.16.7.1 — Fix diagnostic log silence (PATCH)

**Released:** 2026-07-08
**Parent:** v3.16.7 PATCH (`0e4bde9`)
**Tag:** v3.16.7.1
**Branch:** `feature/v3-12-0-minor`

## Highlights

User reported "没 log 啊" after installing v3.16.7. Multi-agent review of the diagnostic logging instrumentation found that **`TraceViewerService` was not forwarding its `ILogger<TraceViewerService>` to the `ReplayTimeline` constructor** — so the timeline's `_logger` field defaulted to `NullLogger.Instance` and all 8 newly-added log lines (Play/OnTick/FrameEmitted) were silently swallowed before reaching Serilog.

This is the same "silent early return" class of bug that the diagnostics were meant to detect — applied to the diagnostic machinery itself.

| Bug | Symptom | Real cause | Fix |
|-----|---------|------------|-----|
| v3.16.7 logs never reach Serilog | User installed v3.16.7 PATCH, ran app, hit ▶ Play, no ReplayTimeline/OnTick/OnAnyFrameEmitted log entries appeared in `%LOCALAPPDATA%/PeakCan.Host/logs/peak-{date}.log` | `TraceViewerService` constructor (line 50-60 pre-PATCH) called `new ReplayTimeline(emit, onPlaybackEnded, onSinkThrew)` with only 3 args. ReplayTimeline's 4th arg `ILogger? logger = null` defaulted to `NullLogger.Instance`. v3.16.7 added 6 log lines via the `_logger` field — all swallowed. The sibling `ReplayService` class already forwarded its logger (v3.14.0 MINOR A7) — only TraceViewerService was missed. | Pass `_logger` as the 4th arg to `new ReplayTimeline(...)` in `TraceViewerService` ctor. |

### A note on the log file location

`appsettings.json` Serilog config writes to `%LOCALAPPDATA%/PeakCan.Host/logs/peak-.log` (rolling daily), not `bin/Debug/net10.0-windows/logs/`. The release notes for v3.16.7 had this wrong.

**Correct path on Windows**: `C:\Users\{username}\AppData\Local\PeakCan.Host\logs\peak-{date}.log` (where `{date}` is the rolling date stamp).

## What's in the box

| Commit | Change |
|--------|--------|
| (local) | `TraceViewerService` ctor: add `logger: _logger` as 4th arg to `new ReplayTimeline(...)`. |

## Lessons (1-of-1)

1. **`diagnostic-instrumentation-must-itself-be-observable`** — process lesson. Adding `[LoggerMessage]` log calls does not guarantee they reach the user. Every log call is a wire from the call site to the user's eyes — that wire has a chain of dependencies (logger field initialized, logger forwarded through constructors, Serilog configured, sink configured, file path writable, level filter not excluding the level). The pre-PATCH code passed the wire from the call site to `_logger` but the wire from `_logger` to Serilog was missing (NullLogger was the default). **When shipping a diagnostic PATCH, also ship a smoke test or a one-time log message at the very first line of `Main` that proves Serilog itself is wired — then the user can immediately tell "log machinery works" from "log machinery broken".** This PATCH adds no such smoke test; it's a reactive fix.

2. **`shared-utility-class-with-optional-dependency-defaults-to-null`** — design lesson. `ReplayTimeline`'s 4th ctor arg is `ILogger? logger = null` (nullable, defaults to NullLogger). The sibling `ReplayService` correctly forwards its logger; `TraceViewerService` does not. **Any class that constructs a dependency with an optional logger parameter must explicitly forward its own logger** — there is no inheritance or convention to make this automatic. Either (a) make the logger parameter required (no default), forcing the call site to be explicit, or (b) use a static `[LoggerMessage]` source-gen pattern that doesn't need a logger field at all (the source generator emits a static method that takes the message; the user wires the logger separately). The current "nullable with NullLogger fallback" pattern silently swallows diagnostics in callers that forget to forward.

## NEXT (unchanged)

- **▶ Play real fix** (deferred to v3.16.8): after this PATCH, user can install v3.16.7.1, click ▶ Play, and find real log lines in `C:\Users\{username}\AppData\Local\PeakCan.Host\logs\peak-{date}.log`. Send the first 20 lines for actual root-cause diagnosis.
- **Reset() should not unload sources** (deferred to v3.16.8 or v3.17.0): user reported "重开 trace viewer 菜单栏内容丢了" — playback controls hidden because `Reset()` unloads registry sources.

## Files in this PATCH

```
src/Directory.Build.props                       (bump 3.16.7 -> 3.16.7.1)
src/PeakCan.Host.Core/Replay/TraceViewerService.cs   (forward logger to ReplayTimeline)
docs/release-notes-v3.16.7.1.md                (this file)
```
