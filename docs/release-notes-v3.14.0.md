# Release Notes v3.14.0 — code-review bug bash (7 HIGH fixes) (MINOR)

**Released:** 2026-07-08
**Parent:** v3.13.3 PATCH (`1b3f5760`)
**Tag:** v3.14.0
**Branch:** `feature/v3-12-0-minor`

## Highlights

This MINOR closes **all 7 HIGH bugs** surfaced by the 2026-07-08 full-codebase code review (773 C# files, 40,187 lines). All fixes are zero-public-API, zero-DI-signature, zero-schema-change. The MINOR title is "code-review bug bash" — it's a defensive correctness/safety pass, not a feature MINOR.

| Commit | Bug | Fix | Behavior change |
|--------|-----|-----|------|
| `f778203` | **A1** [DATA CORRUPTION] | SignalDecoder 64-bit signed returns correct bit pattern | 64-bit signed signals in DBCs now decode correctly (was always returning 0) |
| `62df020` | **A5** [BUS KILLER] | ChannelRouter.DetachSink must not escape sink-error catch | A sink-error-induced DetachSink race can no longer kill the read loop |
| `fa647cf` | **A2+A3+A4** [MEM LEAK] | VM Dispose cancels 3 singleton event subscriptions | Replay/Trace Viewer VM close+reopen no longer leaks; ctor xmldoc reversed (corrected backwards "singleton is fine" defense) |
| `2e9f173` | **A6+A7** [PLAYBACK HANG / CPU 100%] | ReplayService timer fire-and-forget + ReplayTimeline rejects invalid loop region | Slow sink no longer blocks timeline; invalid Start>End loop region no longer burns CPU |

**Test delta:** 1298 + 5 SKIP / 0 fail → **1302 + 5 SKIP / 0 fail** (+4 active: 1 SignalDecoder 64-bit + 1 ChannelRouter DetachSink + 1 Dispose event-subscription leak + 1 LoopRegion validation; +1 deferred to A6 timer test if implementer added it; see per-commit diffs)
**Code stats:** +~480 / -10 LoC net across 9 files

## Per-bug detail

### A1 — SignalDecoder 64-bit signed data corruption

**File:** `src/PeakCan.Host.Core/Dbc/SignalDecoder.cs:69`
**Pre-fix:** `ValueType.Signed => (ulong)SignExtend(raw, signal.Length) & ((1UL << signal.Length) - 1UL)`. When `signal.Length == 64`, `1UL << 64 == 1UL << (64 % 64) == 1UL << 0 == 1` per C# shift semantics; mask = 0; `raw & 0 == 0`. Any 64-bit signed signal in any DBC decoded as 0.
**Fix:** special-case `signal.Length >= 64` before the `1UL << len` mask line.
**Lesson:** `shift-left-by-width-is-zero-csharp-modular-shift-rule-causes-mask-off` (NEW 1-of-1)

### A5 — ChannelRouter DetachSink bus killer

**File:** `src/PeakCan.Host.Infrastructure/Channel/ChannelRouter.cs:231`
**Pre-fix:** `DetachSink(s)` inside `catch (Exception onErrorEx)` with no protection. A multi-threaded race (concurrent AttachSink mutating `_sinks` while DetachSink does `Array.IndexOf + Array.Copy + Volatile.Write`) throws → escapes to `ReadLoopAsync` → `consecutiveIterationsWithFailure++` → 100 iterations later the entire read loop gives up + the CAN bus appears dead.
**Fix:** inner try/catch around the `DetachSink(s)` call; failure routes through `ILogger` (v1.2.12 PATCH Item 11 pattern). The class xmldoc (which forbade the wrap) is contradicted by implementation; the spec's intent ("secondary exception must be observable") is satisfied by logging, not by escaping.
**Lesson:** `detachsink-inside-outer-catch-needs-inner-try-catch-otherwise-read-loop-kills-bus` (NEW 1-of-1)

### A2+A3+A4 — VM Dispose singleton event subscription leaks

**Files:**
- `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` (A2 `LoopRewound` + A3 `RecentSessions.PropertyChanged`)
- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` (A4 `DbcLoaded`)

**Pre-fix:** 3 events subscribed in ctor, never cancelled in Dispose. All 3 sources are DI singletons (`IReplayService` / `RecentSessionsService` / `DbcService`) → closure chain `singleton → handler → old-VM → old-Frames/ObservableCollections` prevents old-VM GC. Each tab open+close leaks a full VM.

**Fixes:**
- A2: add `_service.LoopRewound -= OnLoopRewound;` in `ReplayViewModel.Dispose()`.
- A3: promote the lambda `(_, __) => RefreshRecentEntries()` to a named method `OnRecentSessionsPropertyChanged` (lambdas can't be `-=`'d by reference), then `-=` it in Dispose.
- A4: add `_dbcService.DbcLoaded -= OnDbcLoaded;` in `TraceViewerViewModel.Dispose()`. **Also rewrite the v3.13.2 PATCH F5 ctor xmldoc** which incorrectly argued "singleton means no unsubscribe" — backwards reasoning. Singleton is the source of the leak, not the subscriber.

**Lesson:** `di-singleton-event-subscribers-must-unsubscribe-in-dispose` (NEW 1-of-1)

### A6 — ReplayService timer sync wait

**File:** `src/PeakCan.Host.Core/Replay/ReplayService.cs:225`
**Pre-fix:** `EmitFrameToSinkAsync(frame).GetAwaiter().GetResult();` on the 1ms timer thread. PEAK driver `Write*` calls can block for hundreds of ms on USB unplug / driver busy; the sync wait pins the timer thread → next tick burst-emits all accumulated frames. The xmldoc at lines 238-247 self-contradicted (declared "intentionally fire-and-forget" but code was sync wait).
**Fix:** replaced with `_ = Task.Run(async () => { try { await EmitFrameToSinkAsync(frame).ConfigureAwait(false); } catch (ReplaySendException ex) { _onSinkThrew?.Invoke(ex); } catch (Exception ex) { LogSinkThrew(_logger, ex, frame.Id, frame.Timestamp); } });`. The timer thread is no longer pinned. Deleted the now-obsolete self-contradicting xmldoc.
**Lesson:** `timer-thread-must-fire-and-forget-on-sink-writes` (NEW 1-of-1)

### A7 — ReplayTimeline loop-region death spiral

**File:** `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs:281-310`
**Pre-fix:** the loop-region rewind block checks `_currentTimestamp >= r.End` and rewinds to `r.Start`. If user-configured `r.Start > r.End` (no client-side validation), the rewind check immediately re-triggers on the next tick, burning 100% CPU in an infinite rewind loop with no user-visible playback.
**Fix:** added a `r.Start > r.End` guard in the OnTick rewind block that logs a warning via `LogInvalidLoopRegion` and skips the rewind (playback continues to natural EOF). The ReplayViewModel setter is not validated (out of scope — defensive guard is sufficient).
**Lesson:** `user-configured-time-range-must-be-validated-against-start-end-reversal` (NEW 1-of-1)

## What's NOT in this MINOR (deferred to v3.14.x PATCH)

- **B1-B11 MEDIUM** (11 items): Pause() timer leak, VAL_ minus, LoadAsync naming, thread safety, async state machine, OnAnyFrameEmitted frame unused, SetSpeed cross-lock, DbcViewModel ExportCsv, DbcTokenizer block comments + 2-byte BOM, 0.5.6 float token, RelayCommand CanExecute audit.
- **C1-C5 LOW** (5 items): Signal StartBit comment, TPCANTimestamp micro verify, TraceViewerService.Dispose, AppShellViewModel connect-async readability, BusStatisticsCollector fps/80.
- All deferred to a separate v3.14.1+ PATCH chain. None are safety-critical; all are tightening/clarity.

## Upgrade notes

No breaking changes:
- No public API surface change.
- No DI signature change.
- No schema change.
- No XAML change.
- All 7 fixes are internal behavior corrections.

**Behavior changes that ARE user-visible:**
- A1: 64-bit signed signals in DBCs now decode correctly (was always returning 0 — affects any 64-bit signed signal in a CAN FD DBC).
- A5: a single sink race-condition no longer kills the CAN bus (would have required Disconnect+Connect to recover).
- A2+A3+A4: Replay tab and Trace Viewer close+reopen no longer leak VM memory.
- A6: a slow PEAK driver write no longer pins the 1ms timeline timer (was: burst-emit on next tick; now: timer keeps firing at 1ms).
- A7: a malformed loop region no longer burns 100% CPU (was: infinite rewind loop; now: warning logged, playback continues to EOF).

## NEXT

- v3.14.1 PATCH: B1-B11 MEDIUM items. Smaller scope (~5 PATCHes), single-bug each.
- v3.15.0 MINOR: candidate scope TBD (DBC view UX polish, project-wide `ConfigureAwait` audit, streaming AscParser + progress callback).
- Continue code-review cycle: re-run the same full-codebase review on the v3.14.0 state; expect 0 HIGH (all closed), ~10-15 MEDIUM (some B1-B11 closed in v3.14.1+), ~3-5 LOW.
