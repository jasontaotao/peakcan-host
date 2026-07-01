# Release Notes — v1.6.1 PATCH

**Date:** 2026-06-29
**Version:** v1.6.1 (PATCH)
**Previous:** v1.5.1 (PATCH)
**Commits since v1.5.1 (`35a3967`):** 5 (1 plan + 1 erratum + 4 task commits → squash)

## 概述

v1.6.1 PATCH 关闭 v1.5.1 PATCH release notes §"Known follow-ups" 段落列出的 4 项 ship-new follow-ups。**全部为 LOW** (production 内部 invariant / test infra / API rename),**无 user-facing UX 变化**(除 Item 2 修复 Start>End 静默接受的不一致行为)。4 items ship in 4 commits:

| # | Item | Severity | User-facing |
|---|------|----------|-------------|
| 1 | `CyclicDbcSendService` mid-tick cancel (defensive `_isRunning` re-check) | LOW | No (race-window internal) |
| 2 | `DbcSendViewModel` Start>End UX gap (manual validator in property setter) | LOW | Yes (subtle: invalid range input no longer sticks) |
| 3 | `CyclicTimerTestHarness` (test-only) for race-test wait/retry | LOW | No (test infra) |
| 4 | `BaudRate.FromDescriptor` → `FromFdDescriptor` rename + remove `[Obsolete]` | LOW | No (API rename) |

v1.6.0 MINOR 5 项 carry-over 仍 deferred (V8 sandbox + CanApi rate limit + DBC size/token + path root + OEM `IKeyDerivationAlgorithm`);v1.6.1 PATCH scope 严守 4 项 LOW。

## Items

### Item 1 — CyclicDbcSendService mid-tick cancel

**Component:** `src/PeakCan.Host.App/Services/CyclicDbcSendService.cs`

**Background**: `OnTimerTick` 在 snapshot lock (line 151-159) 和 Message.Id check lock (line 186-199) 之间、encode (line 208) 与 send (line 236) 之前,有 2 个 race window — 如果用户 `Stop()` 在 lock 释放后、encode/send 之前调用,in-flight tick 仍会完成 encode+send,产生 1 个 extra frame。

**Fix**: 在 encode 前 + send 前各加 1 次 `lock(this) { if (!_isRunning) return; }` re-check。最小改动,匹配现有 lock 风格,2 行 LOC,perf 开销 < 1μs。

**Limitation (documented)**: 真正 abort in-flight `SendAsync` await 需要 `CancellationToken`,**本 PATCH 不实现**。用户 click Stop 后 1 个 frame 仍会 send (已知行为,UI 可接受)。CancellationToken 改造 deferred 到 v1.6.x 后续 PATCH(需要更新所有 `SendService.SendAsync` caller,scope 超出 LOW)。

**No new test**: 1-μs race window 不能 deterministically race inject(Stop 在 re-check 之前 → re-check 捕获;Stop 在 re-check 之后 → re-check 已通过,in-flight tick 仍会完成)。现有 `CyclicDbcSendServiceRaceTests.OnTimerTick_After_Stop_Does_Not_Send` 已通过 drain wait 验证 "Stop 后无新 tick" 不变量。

### Item 2 — DbcSendViewModel Start>End UX gap

**Component:** `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs`

**Background**: v1.5.1 PATCH 用 `[ObservableProperty]` + `OnXxxChanged` partial callback 实现 `StartTimestamp` / `EndTimestamp`。Source-gen setter 顺序是 `set backing field → raise OnXxxChanged`。Partial `return` 拒绝时 backing field 已被覆盖,UI TextBox 读 binding 时显示**新值**(invalid),service 保留**旧值** — UI 和 service 失同步。

**Fix**: 去掉 `[ObservableProperty]` + 2 个 `OnXxxChanged` partial methods,改手写 property:

```csharp
public double? StartTimestamp
{
    get => _startTimestamp;
    set
    {
        if (!IsValidRange(value, _endTimestamp))
        {
            RangeFilterError = "Start must be ≤ End";
            return;  // backing field NOT touched, service NOT called
        }
        if (!EqualityComparer<double?>.Default.Equals(_startTimestamp, value))
        {
            _startTimestamp = value;
            OnPropertyChanged();
        }
        _service.StartTimestamp = value;
        RangeFilterError = null;
    }
}
```

Validator 抽 `private static bool IsValidRange(double?, double?)` (DRY,Start/End setter 共用)。`EqualityComparer<double?>.Default.Equals` 跳过 no-op writes。

**Erratum (vs spec)**: Spec Decision 3 假设 `CommunityToolkit.Mvvm 8.4+` 提供 `SetProperty<T>(ref T, T, Func<T,T,bool> validator)` overload,**错误**。该 overload 实际在 **8.5+** 引入 (project 当前 8.4.2)。Pivot 为 manual validation pattern,保留 `IsValidRange` 静态 helper。**后续 spec 不要假设 8.4+ 有此 overload**。

### Item 3 — CyclicTimerTestHarness (test-only)

**Component:** `tests/PeakCan.Host.App.Tests/TestHelpers/CyclicTimerTestHarness.cs` (new) + 4 tests + race-test migration

**Background**: `CyclicSendServiceRaceTests` + `CyclicDbcSendServiceRaceTests` 已知 transient-flaky (memory v1.2.12 lesson 4,CI 3× re-run if fails)。两 race-test 文件的 `await Task.Delay + predicate` 模式 1:1 mirror。

**Fix**: 抽 `CyclicTimerTestHarness` (internal static class) 提供:
- `WaitUntilAsync(Func<bool>, TimeSpan)` — 5ms polling,返回 bool
- `AssertWithinAsync(Func<bool>, TimeSpan, string what)` — 3 次 internal retry (每次 timeout 后 `Task.Delay(10ms)`),最终失败抛 `XunitException` 带 diagnostic message。Log `[CyclicTimerTestHarness] retry N/2 waiting for 'X' after Yms` 到 `Console.Error`,flak 频率可见。

**Migration**: 每个 race-test 文件的 "timer ticks at least once before Stop" baseline check (1 处) 改为 harness。其他 `Task.Delay` 是 deliberate drain waits (let in-flight tick complete),**保留不变** per plan guidance。

**Production code 完全不动** (Decision 7:CyclicSendService + CyclicDbcSendService 保持独立,no `CyclicTimer<T>` base class extraction)。

### Item 4 — BaudRate.FromDescriptor → FromFdDescriptor rename

**Component:** `src/PeakCan.Host.Core/ICanChannel.cs`

**Background**: `BaudRate.FromDescriptor(string, string)` 标 `[Obsolete]` 提示未来 4-arg overload (with classicCode),实际无 caller(grep 验证)。Method 强制 `isFd=true`,但 API 名字让人误以为能造经典 CAN rate。

**Fix**: rename `FromDescriptor` → `FromFdDescriptor` + 去 `[Obsolete]` (rename 后 API 名字显式说明 "FD only",no longer "less capable than promised") + 改写 `BaudRate` class-level XML doc 显式说明经典自定义速率约束 (NetArchTest rule 2 决定 Core 不能 reference `TPCANBaudrate`,所以 3-arg overload awaits Core-safe PEAK code mapping,即 v1.6.x MINOR scope)+ 删除 method-level `// TODO: reintroduce...` block。

**No breaking call site**: 4 个 preset (`Can125kbps` / `Can250kbps` / `Can500kbps` / `Can1Mbps` / `CanFd1Mbps` / `CanFd2Mbps` / `CanFd5Mbps`) 全部用 `new BaudRate(...)` ctor,**不**调 `FromDescriptor`。`ResolveClassicCode(BaudRate)` 在 Infrastructure 层保持按 Name 字符串映射到 `TPCANBaudrate` (cover 4 个 classic preset)。

## Test counts

| Suite | v1.5.1 baseline | v1.6.1 PATCH | Delta |
|---|---|---|---|
| Core | 335 | 338 | +3 (Item 4) |
| App | 395 | 403 | +8 (Item 2: 4, Item 3 harness: 4) |
| Harness (in App.Tests) | — | — | (0 separate; folded into App.Tests assembly) |
| Infra | 84 | 84 | 0 |
| **Total** | **814** | **825** | **+11** |

**Erratum vs plan**: Plan projected +12 (Core +3, App +5, Harness +4 separate). Execution: +11 (Item 1 has 0 tests; production fix only,re-check race window not deterministically testable). Harness suite lives in `App.Tests/TestHelpers/`,所以 fold 进 App.Tests assembly 而非独立 suite。

## Process lessons (NEW)

1. **CT.Mvvm 8.4.2 has no `SetProperty<T>(ref T, T, Func<T, T, bool> validator)` overload** (added in 8.5+). 8.4.2 has `SetProperty<T>(ref T, T, string)`, `SetProperty<T>(ref T, T, IEqualityComparer<T>, string)`, and post-set `Action<T>` callbacks. Spec assumed 8.4+ — pivot to manual validation in property setter. Lesson: **grep actual library version's XML doc** before assuming an API exists.
2. **`DbcEncodeService` is `sealed` + `Encode` not `virtual`** — cannot be mocked via subclass. Project uses NSubstitute for interface mocks + `SendService` subclass for `virtual SendAsync`. Lesson: when planning tests, check `sealed` + virtual status of target methods first.
3. **Re-check pattern can't abort in-flight `SendAsync` await** — the re-check happens BEFORE `await SendAsync`, so by the time SendAsync is awaiting, the re-check has already passed. True abort requires `CancellationToken` (v1.6.x follow-up). Lesson: distinguish "prevent new" from "abort in-flight" in race fix designs.
4. **Test file migration: most `Task.Delay` calls in race tests are drain waits, not poll-then-assert** — only 1 in each file (the "timer ticks at least once before Stop" baseline check) is a true poll-then-assert. Other `Task.Delay` calls are deliberate grace periods to let in-flight ticks complete. Lesson: read each `Task.Delay` + subsequent assertion carefully; preserve drain semantics.
5. **Project pre-existing race-test flake persists** — `CyclicDbcSendServiceRaceTests.Encode_Failure_*` + `CyclicSendServiceRaceTests.Send_Failure_*` known transient-flaky per memory v1.2.12 lesson 4. Harness migration didn't eliminate it (only migrated 2 of ~14 `Task.Delay` calls). Full migration deferred to future PATCH.
6. **Item 1 deviation: 0 tests instead of planned 1** — `GatedSendService` subclass design is achievable (override virtual `SendAsync` with `TaskCompletionSource` gate) but the test CAN'T deterministically verify the re-check works (re-check passes before SendAsync is called; Stop after that point doesn't unblock the in-flight await). Documented in commit `b115560` body + spec erratum.

## Brief-vs-source drift (12-of-12+ confirmed)

1. **CT.Mvvm `SetProperty` validator overload provenance**: spec claimed 8.4+,actual 8.5+. Erratum added to spec + plan.
2. **Item 1 test count**: plan said +1, execution 0 (GatedSendService design doesn't work for the in-flight assertion). Documented in plan erratum.
3. **Harness lives in App.Tests, not separate suite**: plan table split Harness / App, actual single assembly. Test counts re-aggregated.
4. **ReplayViewModel property pattern**: spec said `SetProperty(ref, value, validator)`, actual manual property + `EqualityComparer` + `OnPropertyChanged()` (pivoted after 8.4.2 doesn't have validator overload). Code matches spec intent (rejection = no field touch), mechanism different.

## Files changed

```
 docs/release-notes-v1.6.1.md                       (new, this file)
 docs/superpowers/plans/2026-06-29-v1-6-1-patch.md  (new, plan)
 docs/superpowers/specs/2026-06-29-v1-6-1-patch-design.md  (new, spec)
 src/PeakCan.Host.App/Services/CyclicDbcSendService.cs         (+21)
 src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs            (± 116)
 src/PeakCan.Host.Core/ICanChannel.cs                          (± 24)
 tests/PeakCan.Host.App.Tests/Services/CyclicDbcSendServiceRaceTests.cs  (+ 13)
 tests/PeakCan.Host.App.Tests/Services/CyclicSendServiceRaceTests.cs     (+ 10)
 tests/PeakCan.Host.App.Tests/TestHelpers/CyclicTimerTestHarness.cs      (new, 83)
 tests/PeakCan.Host.App.Tests/TestHelpers/CyclicTimerTestHarnessTests.cs (new, 58)
 tests/PeakCan.Host.App.Tests/ViewModels/ReplayViewModelTests.cs         (+ 71)
 tests/PeakCan.Host.Core.Tests/ICanChannelTests.cs               (new, 49)
```

## Known follow-ups

- **v1.6.0 MINOR 5 items still deferred** (V8 sandbox hardening + CanApi rate limit + DBC size/token + path root + OEM `IKeyDerivationAlgorithm` concrete).
- **`CancellationToken` 改造 for `SendService.SendAsync` + `CyclicDbcSendService`**: true abort in-flight send, requires all SendService caller updates. Deferred to v1.6.x PATCH.
- **Race-test full migration to `CyclicTimerTestHarness`** (12+ remaining `Task.Delay` calls in 2 race-test files). v1.6.1 PATCH only migrated 1 per file (the "timer ticks at least once" baseline check); drain waits intentionally left as `Task.Delay` per plan guidance. Full migration + `[Retry(3)]` xUnit attribute 仍是 future work。
- **Long-term Non-Goals** (自 v1.4.0 起):DBC Value table encoding + Multiplexed signal groups UI + Replay→Trace auto-load。
- **Core-safe PEAK classic-code mapping**: enables `BaudRate.FromDescriptor(descriptor, name, classicCode)` 3-arg overload (currently impossible in Core per NetArchTest rule 2). Deferred to v1.6.x MINOR (paired with v1.6.0 MINOR scope)。

## Ship method

```
1. git checkout -b feature/v1-6-1-patch (from main @ 35a3967)    [DONE]
2. 4 task commits + 1 erratum commit                              [DONE]
3. Pre-ship code-reviewer subagent (0C/0H/2M/2L)                [DONE]
4. docs/release-notes-v1.6.1.md (this file)                      [DONE]
5. git push -u origin feature/v1-6-1-patch                        [pending]
6. gh pr create --base main                                       [pending]
7. gh pr merge --squash --delete-branch                          [pending]
8. git fetch origin main + git reset --hard origin/main          [pending]
9. git tag v1.6.1 + git push origin v1.6.1                        [pending]
10. gh release create v1.6.1 --notes-file docs/release-notes-v1.6.1.md  [pending]
11. Update MEMORY.md + write peakcan-host-v1-6-1-shipped.md      [pending]
```

## Open Questions

- None. PATCH scope is closed; all 4 items ship together as v1.6.1.
