# Release Notes v3.16.7 — Diagnostic logs for ▶ Play (PATCH)

**Released:** 2026-07-08
**Parent:** v3.16.6 PATCH (`d989bde`)
**Tag:** v3.16.7
**Branch:** `feature/v3-12-0-minor`

## Highlights

This PATCH adds **diagnostic logs only** — no behavior changes. After 8 PATCHes (v3.14.2 through v3.16.6) chasing ▶ Play failures with multi-agent root-cause reviews that all turned out wrong, this PATCH stops guessing and instruments the playback chain at every step.

**This is a "find the actual root cause" PATCH, not a "fix it" PATCH.**

| Where | What | Why |
|-------|------|-----|
| `ReplayTimeline.Play()` entry | Logs `_frames.Count`, `_isPlaying`, `_hasStarted`, `_currentTimestamp` | Tells us if the user hit Play with 0 frames loaded (the most likely cause per the v3.16.4 release notes' "silent early return" diagnosis) |
| `ReplayTimeline.Play()` skipped | Logs "already playing" or "0 frames loaded" | Tells us which early-return branch fired |
| `ReplayTimeline.Play()` started | Logs after timer creation | Tells us timer was actually created |
| `ReplayTimeline.OnTick` entry | Logs `_isPlaying`, `_frames.Count`, `_nextFrameIndex`, `_currentTimestamp` **on every tick** | Tells us if timer is actually firing (1 ms = 1000 ticks/sec) |
| `ReplayTimeline.OnTick` not playing | Logs when `!_isPlaying` early-return fires | Tells us if something flipped `_isPlaying` to false after Play() |
| `ReplayTimeline.OnTick` emitting | Logs `toEmit.Count` and new cursor | Tells us how many frames per tick and whether cursor advances |
| `TraceViewerViewModel.PlayCommand` entry | Logs `_allServices.Count`, `MasterSourceId`, `HasSources` | Tells us if the command reached the VM at all |
| `TraceViewerViewModel.PlayCommand` per-svc | Logs each `svc.Play()` call with `TotalDuration` | Tells us if 0-frame svc blocks playback of healthy svcs |
| `TraceViewerViewModel.OnAnyFrameEmitted` | Logs first 5 + every 100th frame | Tells us if the VM's FrameEmitted handler is firing at all |

## What to do after installing

1. Run the app
2. Open Trace Viewer, load ASC, click + Add to watch, click Plot
3. **Click ▶ Play**
4. Find the Serilog file at `bin/Debug/net10.0-windows/logs/peakcan-YYYY-MM-DD.txt` (or wherever Serilog is configured to write)
5. **Send me the lines containing** `ReplayTimeline.Play`, `OnTick`, `OnAnyFrameEmitted`, or `TraceViewerViewModel.Play`

The first 5 lines of the Play() call + the first 3 OnTick entries will tell us exactly where the chain is broken.

## Why a logs PATCH instead of a fix PATCH

The previous 8 PATCHes shipped 4 different "root cause" diagnoses for ▶ Play that all turned out wrong:

| PATCH | "Root cause" claim | Reality |
|-------|-------------------|---------|
| v3.16.3 | ScrubberValue not written back in OnAnyFrameEmitted | Real but partial — still didn't make Play work |
| v3.16.4 | ☑ Plot Click guard + OxyPlot deferred ItemsSource | Real but unrelated to Play |
| v3.16.5 | (4-agent review) LineAnnotation never created + 1ms timer + reverse-seek | Three hypotheses, none proven |
| v3.16.6 | (4-agent review) DataGrid cumulative drift + window cache stale | Real but unrelated to Play |

**Each PATCH claimed to have found THE root cause and shipped a fix. None of them fixed Play.** This PATCH ships no fix — only logs. The logs will tell us which of the candidate root causes is real, and which are noise.

## Lessons (1-of-1)

1. **`guess-roots-after-3-failed-shipped-fixes-is-not-engineering`** — process lesson. The previous 4 PATCHes each shipped a "this is THE root cause" fix based on multi-agent review, but Play still doesn't work. **The next PATCH must be observation, not conjecture.** When 3+ shipped fixes all turn out to be wrong, the failure mode is "model is wrong" not "fix is incomplete" — instrument the system, observe, then fix.
2. **`silent-early-return-on-zero-frames-is-design-debt`** — already-captured lesson from v3.16.4 release notes. The Play() method's `if (_frames.Count == 0) return;` swallows the failure mode. This PATCH's `LogPlayNoFrames` warning will fire if the user hits Play before any ASC is loaded, giving the user a visible signal. **A loud failure on zero frames would be a better fix than a log line, but that fix is out of scope for this diagnostic PATCH.**

## NEXT (v3.17.0 MINOR — unchanged)

- **C-1 TabControl refactor**: per-source independent tabs.
- **`.tmtrace` schema v2**: watch list persistence.
- **▶ Play actual fix**: based on the logs from v3.16.7, ship v3.16.8 with the real fix.
- **Reset() should not unload sources**: when trace viewer closes, sources stay in the registry so re-opening shows playback controls again. (User reported: "重开 trace viewer 菜单栏内容丢了" — playback controls + scrubber are hidden by `Visibility="{Binding HasSources}"` because `Reset()` unloaded sources.)

## Files in this PATCH

```
src/Directory.Build.props                          (bump 3.16.6 -> 3.16.7)
src/PeakCan.Host.Core/Replay/ReplayTimeline.cs    (Play + OnTick logs via LoggerMessage source-gen)
src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs  (PlayCommand + OnAnyFrameEmitted logs)
docs/release-notes-v3.16.7.md                     (this file)
```
