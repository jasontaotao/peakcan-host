# v1.6.2 PATCH — Race-test fire-timer → predicate-based migration + CancellationToken true-abort in `CyclicSendService` + `CyclicDbcSendService`

**Date:** 2026-06-29
**Branch:** `feature/v1-6-2-patch` (cut from main @ v1.6.1 squash `0556843` after `git reset --hard origin/main` to align)
**Target version:** v1.6.2 (PATCH)
**Spec owner:** main agent
**Reviewer:** code-reviewer subagent (pre-ship)
**Pre-flight:** Phase 2.5 actual code exploration (2 production files + 2 race-test files + 1 harness 文件已实际读取)

## 概述

v1.6.2 是一个 **2 项 PATCH**（基于 Phase 2.5 实际发现修订 memory "3 items" 草稿），**全部 LOW-to-MEDIUM severity**，关闭 v1.6.1 PATCH release notes "Known follow-ups" 段落列出的 race-test + true-abort 两项 carry-over。**v1.6.0 MINOR（5 项 security/limits）保持 deferred，不进 v1.6.2 PATCH**。

| # | Item | User-facing | Severity |
|---|------|-------------|----------|
| 1 | **`CyclicSendService` + `CyclicDbcSendService` CancellationToken refactor** — 两 service 内引入 `CancellationTokenSource` 字段；`Start()` 创建新 CTS（dispose 旧），`Stop()` 通过 `cts.Cancel()` abort in-flight `SendAsync` await；`OnTimerTick` 把 `cts.Token` 传给 `_sendService.SendAsync(frame, ct)`。**用户 click Stop 后 in-flight 的 SendAsync 立即被 channel 取消，不再"send 完 1 帧后才返回"** | Yes (UX: Stop 立即停止，不再有最后一帧延迟) | MEDIUM |
| 2 | **Race-test fire-timer `Task.Delay` → `WaitUntilAsync` predicate-based migration** — `CyclicSendServiceRaceTests` + `CyclicDbcSendServiceRaceTests` 中 **9 处** `await Task.Delay(N)` 后面紧跟 counter assertion 的 pattern 改为 `await CyclicTimerTestHarness.WaitUntilAsync(predicate, timeout)`；**4 处 drain wait 保留为 `Task.Delay`**（无 predicate，harness 不适用） | No (test stability) | LOW |

### memory vs spec scope reconciliation

memory `MEMORY.md` "v1.6.2 PATCH (planned)" 列了 3 items：
1. race-test full migration to `CyclicTimerTestHarness` (~12 remaining `Task.Delay` calls)
2. `[Retry(3)]` xUnit attribute
3. `CancellationToken` refactor for `SendService.SendAsync` + `CyclicDbcSendService` (true abort in-flight)

**Phase 2.5 修订**：
1. "race-test full migration" — 实际只有 9 处可迁移（其余 4 处 drain wait 无 predicate 可 poll；v1.6.1 PATCH line 102 lesson 已明确记录）。本 spec Item 2 改名为 "fire-timer → predicate-based migration"，scope 修订为 9 处可迁移子集，明确排除 drain wait。
2. `[Retry(3)]` xUnit attribute — **删除**。v1.6.1 PATCH spec line 80 + line 175-182 + line 130 三处明确决议"保持 0-NuGet 依赖惯例；harness 内部 3-retry 足夠"。本 spec Item 2 的 predicate-based 改造从源头减少 time-based flake，比"加重试"更根本。如 Item 2 后 race tests 仍 flaky，再考虑增量治标。
3. CT refactor — **保留并扩展**。memory 只说 "SendService.SendAsync + CyclicDbcSendService"，但 `SendService.SendAsync` 已接受 `CancellationToken ct = default`（line 73），无需重构。真正缺 CT 的是 `CyclicSendService.OnTimerTick` (line 137) + `CyclicDbcSendService.OnTimerTick` (line 257)，**两者都调用 `SendAsync` 但没传 ct**。本 spec Item 1 改这两个 caller + Stop cancel path，scope 比 memory 准确。

## 起源

| Source | Item | Severity |
|--------|------|----------|
| v1.6.1 PATCH release notes "Known follow-ups" line 142 | MEDIUM — `CancellationToken` 改造 for `SendService.SendAsync` + `CyclicDbcSendService` (true abort in-flight) | MEDIUM |
| v1.6.1 PATCH release notes "Known follow-ups" line 143 | LOW — race-test full migration to `CyclicTimerTestHarness` (~12 remaining `Task.Delay` calls) + `[Retry(3)]` xUnit attribute | LOW |
| v1.6.1 PATCH process lesson 5 (line 102) | (background) — `Task.Delay` 在 race tests 多为 drain wait，不是 poll-then-assert；plan 估 "~6 occurrences" 实际 1 per file | (constraint) |
| v1.6.1 PATCH code-review MEDIUM #1 (Item 1 test deviation) | (background) — "Stop catches in-flight tick at 1-μs re-check window" deterministic test 不可构造；真 abort 需 CT | (drives Item 1) |
| v1.6.1 PATCH code-review lesson 3 (line 100) | (background) — "Re-check pattern can't abort in-flight `SendAsync` await" | (drives Item 1) |

### Brief drift history (12-of-12+ 记录)

memory "v1.6.2 PATCH (planned)" 列的 3 items **与 source code 不 1:1 吻合**（brief drift 已识别并修订）：

- **"SendService.SendAsync + CyclicDbcSendService" CT refactor**：实际 `SendService.SendAsync` (line 73) 已接受 `CancellationToken ct = default` 并 propagate 给 `ch.WriteAsync(frame, ct)`（line 83）；**`SendService.SendAsync` 不需改**。真缺 CT 的是 `CyclicSendService.OnTimerTick` (line 137) + `CyclicDbcSendService.OnTimerTick` (line 257) 两个 caller — 它们调 `_sendService.SendAsync(frame)` 不传 ct。**修订：scope = 改 2 个 cyclic service caller + Stop cancel path，不动 SendService**。
- **"race-test full migration ~12 remaining Task.Delay"**：v1.6.1 PATCH line 102 已明确实际只有 1 per file 是 poll-then-assert（v1.6.1 PATCH Item 3 已 ship 迁移）；其余 13 处 = 4 drain wait + 9 fire-timer sleep。**修订：scope = migrate 9 fire-timer sleep → WaitUntilAsync predicate-based；保留 4 drain wait 为 Task.Delay**。
- **"[Retry(3)] xUnit attribute"**：v1.6.1 PATCH spec line 80 + line 175-182 + line 130 三处明确决议"保持 0-NuGet 依赖；harness 内部 3-retry 足夠"。xUnit 2.9.3 无内建 `[Retry]`，要实现需引 `XunitRetryFact` NuGet（推翻 0-NuGet）或自定义 attribute 继承 xunit 内部接口（高风险）。**修订：删除本 item；保持 v1.6.1 决议**。

### Phase 2.5 actual code exploration findings

实际读 v1.6.1 squash `0556843` source code 确认 brief 描述 + 锁定关键 design points：

### Item 1 — CT refactor scope

| Assumption | Phase 2.5 actual |
|---|---|
| `SendService.SendAsync` 已接受 CT | **确认**。`src/PeakCan.Host.App/Services/SendService.cs:73`: `public virtual ValueTask<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct = default)`, line 83 `return ch.WriteAsync(frame, ct);`。无需重构 SendService。 |
| `SendService.SendAsync` callers 中 1/9 已传 CT | **确认**。Grep `SendAsync\(` 在 src/ 返回 9 命中：1 (ReplayFrameSinkAdapter.cs:68) 已传 `ct`；6 (AppHostBuilder.cs:290, CyclicSendService.cs:137, CyclicDbcSendService.cs:257, CanApi.cs:90, DbcSendViewModel.cs:224, SendViewModel.cs:201) 不传。**Item 1 scope 仅改 2 个 cyclic service（line 137 + line 257），其余 4 个 manual-send caller 不在 v1.6.2 scope**（不引入 regression risk）。 |
| `CyclicSendService.OnTimerTick` 当前调 `SendAsync(frame)` 不传 ct | **确认**。`src/PeakCan.Host.App/Services/CyclicSendService.cs:137`: `var result = await _sendService.SendAsync(frame).ConfigureAwait(false);`。Lock + generation check 在 line 123-133；encode 后 send 在 line 137；count increment 在 line 138-150。 |
| `CyclicDbcSendService.OnTimerTick` 当前调 `SendAsync(frame)` 不传 ct | **确认**。`src/PeakCan.Host.App/Services/CyclicDbcSendService.cs:257`: `var result = await _sendService.SendAsync(frame).ConfigureAwait(false);`。Lock(snapshot) line 151-159；Message.Id check line 186-199；encode line 208-220；send line 257；count increment line 258-271。 |
| `Stop()` 当前不 cancel in-flight `SendAsync` await | **确认**。`CyclicSendService.StopInner` (line 106-116) 与 `CyclicDbcSendService.StopInner` 内部只 `_isRunning = false; _generation++; _timer?.Dispose();`，无 CT 取消。`Timer.Dispose()` 不 cancel 已 queue 的 callback。 |
| `SendService.SendAsync` 内部 `ch.WriteAsync(frame, ct)` 接受 ct | **确认**。`ICanChannel.WriteAsync` 签名（PEAK 基础设施）已接受 CT；`SendService` line 83 直接 pass-through。**只需在 2 个 cyclic service 把 CT 透传到 SendAsync 即可，channel 层会响应 cancel**。 |
| `OnTimerTick` 是 `async void` 不 catch `OperationCanceledException` | **确认**。`CyclicSendService.OnTimerTick` (line 118-160) + `CyclicDbcSendService.OnTimerTick` 有 `try { await SendAsync } catch (Exception ex)` (CyclicSendService line 152-159)，CyclicDbcSendService line 250-271。**Item 1 必须加 `catch (OperationCanceledException) { /* expected on Stop, count as canceled */ }`** — 否则 `async void` 未捕获 OCE 会 crash process。 |
| `Start()` 当前 dispose 旧 timer 但不 dispose 旧 CTS | **确认**。`CyclicSendService.Start` (line 73-95) + `CyclicDbcSendService.Start` 内部 `StopInner` 调 `_timer?.Dispose();`。**Item 1 必须加 `_cts?.Dispose()` + 创建新 CTS**。 |

### Item 2 — predicate-based migration scope

| Assumption | Phase 2.5 actual |
|---|---|
| `CyclicDbcSendServiceRaceTests.cs` 有 7 处 `Task.Delay` | **确认**。line 79, 96, 98, 105, 125, 141, 159。 |
| `CyclicSendServiceRaceTests.cs` 有 6 处 `Task.Delay` | **确认**。line 86, 101, 105, 111, 131, 144。 |
| **4 处 drain wait**（无 predicate，harness 不适用） | **确认**。<br>• `CyclicDbcSendServiceRaceTests.cs:79` — Stop 后等 in-flight tick 完成（随后 assert leak ≤ 1）<br>• `CyclicDbcSendServiceRaceTests.cs:98` — gen 1 Stop 后等 drain<br>• `CyclicSendServiceRaceTests.cs:86` — Stop 后等 in-flight tick 完成<br>• `CyclicSendServiceRaceTests.cs:105` — gen 1 Stop 后等 drain<br>**保留为 `Task.Delay`**，理由见 Decision 3。 |
| **9 处 fire-timer sleep**（紧跟 counter assertion）可迁移 | **确认**：<br>• `CyclicDbcSendServiceRaceTests.cs:96` — fire gen 1（后续 Stop + gen 2 启动）<br>• `CyclicDbcSendServiceRaceTests.cs:105` — fire gen 2（后续 Stop + 断言 `SentIds` 只含 0x200）<br>• `CyclicDbcSendServiceRaceTests.cs:125` — fire encode-fail（后续断言 `FailureCount > 0`）<br>• `CyclicDbcSendServiceRaceTests.cs:141` — fire send-success（后续断言 `SuccessCount > 0`）<br>• `CyclicDbcSendServiceRaceTests.cs:159` — fire send-fail（后续断言 `FailureCount > 0`）<br>• `CyclicSendServiceRaceTests.cs:101` — fire gen 1（后续 Stop + gen 2 启动）<br>• `CyclicSendServiceRaceTests.cs:111` — fire gen 2（后续 Stop + 断言 `SentIds` 只含 0x200）<br>• `CyclicSendServiceRaceTests.cs:131` — fire send-fail（后续断言 `FailureCount > 0`）<br>• `CyclicSendServiceRaceTests.cs:144` — fire send-success（后续断言 `SuccessCount > 0`）<br>**9 处**全部迁移到 `WaitUntilAsync(predicate, timeout)`。 |
| `CyclicTimerTestHarness.WaitUntilAsync(predicate, timeout)` 返回 bool，predicate 满足立刻返回 true | **确认**。`tests/PeakCan.Host.App.Tests/TestHelpers/CyclicTimerTestHarness.cs:34-48`：5ms 轮询，到 deadline 返回 false。**满足 predicate 立刻返回 → 比 `Task.Delay(120)` 后断言更稳定**：CI 慢/快都不影响（CI 慢时 120ms 可能不够 fire 6 次；predicate-based 自动等到满足）。 |
| `WaitUntilAsync` 无 internal retry（与 `AssertWithinAsync` 不同） | **确认**。Line 34-48 简单 5ms 轮询，无 retry。**`WaitUntilAsync` 用于"fire until predicate"，`AssertWithinAsync` 用于"必须为 true 否则失败"**——本 spec Item 2 用 `WaitUntilAsync`（fire-then-act 模式），不是 `AssertWithinAsync`（assert-then-fail 模式）。 |

### Brief drift cautions (memory `phase-2-5-brief-drift-correction` 12-of-12+)

1. **"SendService.SendAsync + CyclicDbcSendService" CT refactor** 严格指 `CyclicDbcSendService.OnTimerTick` 把 CT 透传到 SendAsync + Stop 取消 in-flight，**不**指 `SendService.SendAsync` 本身（已接受 CT）。Item 1 scope 不动 SendService；改动只在 2 个 cyclic service 文件。
2. **"race-test full migration ~12 remaining Task.Delay"** 严格指 fire-timer sleep 改 predicate-based，**不**指 drain wait 强制迁 harness（破坏语义）。Item 2 scope 9 处可迁移 + 4 处保留；PR plan 必做 grep 验证每个 `Task.Delay` 是否有 predicate 后续 assertion。
3. **"[Retry(3)] xUnit attribute"** 严格指保持 v1.6.1 0-NuGet 决议（spec line 80 + 175-182 + 130 三处），**不**指"引 XunitRetryFact NuGet"。Item 3 已删除。如下一 PATCH 真发现 race-test 仍 flaky（Item 2 predicate-based 改造后），再单独评估。
4. **CT refactor 必须 catch `OperationCanceledException`**：`OnTimerTick` 是 `async void`，未捕获 OCE 会 crash process（line 152-159 现有 catch 只接 `Exception`）。Item 1 必须明确 `catch (OperationCanceledException) { /* expected on Stop */ }`，否则 regression。
5. **CT refactor 必须 dispose 旧 CTS**：`Start()` 当前 dispose 旧 timer，**新增** dispose 旧 CTS（避免 resource leak）+ 创建新 CTS。Item 1 PR plan 必须 verify Dispose 路径完整。
6. **Item 2 predicate-based migration 不改 assertion 顺序**：原 fire-then-assert 顺序是 `Task.Delay → Stop → assert`，迁移后变 `WaitUntil(predicate) → Stop → assert`。Stop 必须在 predicate 满足**之后**（不能前置），保证"至少有 N 次 send 才 Stop"。PR plan 必做 manual review。
7. **Item 2 timeout 设值**：原 `Task.Delay(120)` 等价 timeout 设 500ms（harness 内 `WaitUntilAsync` 接受 timeout 参数），留 4× buffer。`Task.Delay(80)` 等价 timeout 设 500ms（6× buffer）。`Task.Delay(60)` / `Task.Delay(80)` (fire gen 1/2) 等价 timeout 设 500ms。**保留 500ms 默认**——CI 上 500ms 内 `CallCount > 0` 应足够（timer interval 20ms × 25 次 = 500ms 25 fires）。
8. **drain wait 保留的判断标准**：原 code 注释明确写 "drain" / "let in-flight callback finish" / "let old-generation in-flight callbacks finish" 等字样的 → 保留 Task.Delay。无注释但**后续无 counter assertion**（仅 SentIds Skip-N 查询） → 也是 drain 性质，保留。原 code 注释写 "let timer fire" / 后续立即 counter assertion → 迁移 predicate-based。
9. **PATCH 纪律**：除 Item 1 surgical production fix + Item 2 test-only migration 外，不动其他生产代码。`SendService.SendAsync` 不动（已接受 CT）；`DbcSendViewModel` / `SendViewModel` / `CanApi` / `AppHostBuilder` 4 个 manual-send caller 不动（不在 CT scope；不引入 regression risk）。
10. **v1.6.0 MINOR scope 不进 PATCH**：5 项明确 deferred，本 PATCH 严守 scope。

## Scope

2 PATCH items = 1 surgical production fix (Item 1) + 1 test-only migration (Item 2)。

| # | Item | 组件 | 工作 | Source | Severity |
|---|------|------|------|--------|----------|
| 1 | **`CyclicSendService` + `CyclicDbcSendService` CT refactor** | App: `CyclicSendService.cs` (`_cts` 字段 + `Start` dispose/create + `Stop` cancel + `OnTimerTick` 传 ct + catch OCE) / App: `CyclicDbcSendService.cs` (mirror) / Tests: `CyclicSendServiceRaceTests.cs` (+1 test) / Tests: `CyclicDbcSendServiceRaceTests.cs` (+1 test) | Add `CancellationTokenSource` field; cancel on Stop; propagate token to SendAsync; catch OperationCanceledException | v1.6.1 release notes "Known follow-ups" line 142 + v1.6.1 PATCH code-review lesson 3 | MEDIUM |
| 2 | **Race-test fire-timer → predicate-based migration** | Tests: `CyclicDbcSendServiceRaceTests.cs` (5 migrations) / Tests: `CyclicSendServiceRaceTests.cs` (4 migrations) | Replace 9 `await Task.Delay(N);` + subsequent counter assertion with `await CyclicTimerTestHarness.WaitUntilAsync(predicate, timeout);`. Preserve 4 drain waits as `Task.Delay`. | v1.6.1 release notes "Known follow-ups" line 143 + v1.6.1 PATCH process lesson 5 (line 102) | LOW |

## Non-Goals

- **v1.6.0 MINOR 全部 carry-overs**：V8 sandbox hardening + CanApi rate limit + DBC size/token + path norm root + OEM `IKeyDerivationAlgorithm` concrete — 明确 deferred，不进 v1.6.2 PATCH。
- **长期 Non-Goals 保持**：DBC Value table encoding + Multiplexed signal groups UI + Replay→Trace auto-load — pre-existing since v1.4.0。
- **4 个 manual-send caller CT 改造**：`AppHostBuilder.cs:290` + `CanApi.cs:90` + `DbcSendViewModel.cs:224` + `SendViewModel.cs:201` 当前调 `SendAsync` 不传 ct。**不在 v1.6.2 scope**：(a) 这 4 个是 manual click-to-send，user 体验是"按一下 send 一帧"，无 Stop 中断概念；(b) 加 CT 参数是 API surface change，blast radius 大；(c) v1.6.2 PATCH discipline 禁止 scope creep。**Defer to v1.6.x future PATCH**（与 v1.6.0 MINOR 同 cycle 评估）。
- **`SendService.SendAsync` 本身改造**：已接受 `CancellationToken ct = default`（line 73）+ propagate 给 `ch.WriteAsync(frame, ct)`（line 83），无需重构。
- **`[Retry(3)]` xUnit attribute 引入**：保持 v1.6.1 PATCH 0-NuGet 决议；harness 内部 3-retry 已足夠；Item 2 predicate-based 改造后 race-test 稳定性预期提升。如后续真 flaky 再单独评估。
- **`CyclicTimer<T>` base class 抽取（production）**：v1.5.1 PATCH Decision 7 明确否决，本 PATCH 严守。Item 1 + Item 2 不抽 base class。
- **Drain wait 强制迁 harness**：4 处 drain wait（line 79/98/86/105）保留为 `Task.Delay`。drain 是"sleep 让 in-flight 完成"语义，harness poll-then-assert 模式不适用。YAGNI: 不抽 `DrainAsync(TimeSpan)` helper。
- **`async void` OnTimerTick 重构为 `async Task` + Timer callback wrapper**：超出 PATCH scope。现有 `async void` + `OperationCanceledException` catch 是 .NET Timer callback 标准 pattern，保留。
- **`OnTimerTick` 取消语义测试（assert OCE thrown）**：OCE 应 silently swallowed（不 increment counter），不抛到 timer callback 外。**Item 1 new tests 验证"Stop 后 in-flight SendAsync 收到 cancel signal"**（`CountingSendService` 验证 `_sendService.SendAsync` 被调用时 ct.IsCancellationRequested == true），**不**验证 OCE propagation。
- **DI 拓扑变更**：Item 1 不引入新 DI（CTS 在 service 内部创建/销毁，与 SendService / ICanChannel 解耦）。
- **新 exception type / 事件**：本 PATCH 不引入新 exception type；Item 1 的 OCE 是 .NET BCL exception（`System.Threading`），service 不新定义。
- **UDS carry-over / UdsSecurity hardening**：v1.4.2 PATCH 已 ship HIGH；不动。

## 设计决策 (open / proposed)

### Decision 1: Item 1 — `CancellationTokenSource` 字段位置

**选项 A** (推荐): `_cts` 字段在 `CyclicSendService` / `CyclicDbcSendService` 内部私有；`Start()` 创建新 CTS（dispose 旧），`Stop()` 调 `_cts.Cancel()`；`OnTimerTick` 读 `_cts.Token` 传 `SendAsync(frame, ct)`。**`SendService.SendAsync` 不动**。

**选项 B**: 把 CTS 提到 `ICyclicSendService` / `ICyclicDbcSendService` 接口（`CancellationToken Start(..., CancellationToken externalCt)`）；外部 caller（如 VM）持有 token，可从多个 source cancel。

**选项 C**: 用 `IHostApplicationLifetime.ApplicationStopping` token；service 通过 DI 注入，统一管理。

**决策**: A. 理由: (a) 选项 B 把 CTS 提升到接口 = API surface change（每个 caller 都需更新）+ 测试需要 mock token；blast radius 大；(b) 选项 C 引入 `IHostApplicationLifetime` 依赖（service 当前是 free-standing，ctor 只接收 `ILogger`）；(c) 选项 A 最小改动 + 不破坏 v1.6.0 MINOR scope；(d) `OnTimerTick` 当前已经在 `lock(this)` 内读 `_isRunning`/`_frame`/`_generation`，新增 `_cts.Token` 在同一 lock 内读，保持一致性。

### Decision 2: Item 1 — `Start()` CTS 生命周期

**选项 A** (推荐): `Start()` 内 `lock(this) { StopInner(); ...; _cts?.Dispose(); _cts = new CancellationTokenSource(); _timer = new Timer(OnTimerTick, ...); }`。`Dispose()` 内 `StopInner()` + `_cts?.Dispose()`。

**选项 B**: `_cts` 是 readonly，Start() 不 dispose；只 Cancel。**泄漏风险**：每次 Start 都新建 CTS 但不 Dispose → 长时间使用累积。

**选项 C**: 用 `using var cts = new CancellationTokenSource()` scope-local，但 `_cts.Token` 需要在 `OnTimerTick` 中跨 Start/Stop 边界访问 → 不能 scope-local。

**决策**: A. 理由: (a) 选项 B 泄漏 `CancellationTokenSource`（内部有 `ManualResetEvent` 等 native handle）；(b) 选项 C 不可行（lifetime 不在 Start scope 内）；(c) 选项 A 与现有 `_timer?.Dispose()` 模式一致（v1.2.12 PATCH 已有 `_timer?.Dispose(); _timer = null;` 模式）。

### Decision 3: Item 2 — Drain wait 保留（不迁 harness）

**选项 A** (推荐): 4 处 drain wait（`CyclicDbcSendServiceRaceTests.cs:79, 98` + `CyclicSendServiceRaceTests.cs:86, 105`）保留为 `await Task.Delay(N)`。**只迁 9 处 fire-timer sleep**（后续紧跟 counter assertion）。

**选项 B**: 加 `CyclicTimerTestHarness.DrainAsync(TimeSpan)` helper，纯 record elapsed time + 带 retry counter on timeout。

**选项 C**: 把 4 处 drain wait 改为 `WaitUntilAsync(() => true, TimeSpan.FromMilliseconds(N))`（强制 sleep via predicate）。

**决策**: A. 理由: (a) 选项 B 是 YAGNI — 测试只 sleep 不 assert，加 helper 是过度工程；(b) 选项 C 滥用 WaitUntilAsync（predicate 永远 true 立刻返回 ≠ sleep N ms）；(c) drain wait 不是 flake 源（v1.6.1 PATCH process lesson 5 已确认），保留它们不会让 CI 更稳定，反而让 harness 复杂化；(d) 4 处保留的注释/位置明确（grep 即可识别），PR plan 必做 grep 验证。

### Decision 4: Item 2 — `WaitUntilAsync` timeout 设值

**选项 A** (推荐): 原 `Task.Delay(N)` 改为 `WaitUntilAsync(predicate, TimeSpan.FromMilliseconds(500))`。**固定 500ms timeout** 给所有 9 处（不管原 N 是 60/80/120）。

**选项 B**: timeout = 原 N × 5（heuristic：原 80ms → 400ms；原 120ms → 600ms；原 60ms → 300ms）。

**选项 C**: timeout = `Math.Max(N * 4, 500)`（floor 500ms，dynamic 上限）。

**决策**: A. 理由: (a) 选项 B/C 引入 magic number × 4/5，没清晰语义；(b) 原 `Task.Delay(N)` 是启发式时间（timer interval 20ms × K 次 fire 的 heuristic），predicate-based 改造后 timeout 只要"足够大到 CI 上能 fire 几次"，500ms 已是 25× timer interval = 25 fires 上限；(c) 固定值便于 review（"全部 500ms timeout" 一目了然）；(d) 与 v1.6.1 PATCH Decision 6 "harness API 简单 + 内部常量 hard-coded" 风格一致。

### Decision 5: Item 1 — `OnTimerTick` 取消时是否 increment counter

**选项 A** (推荐): OCE 抛出后 silently swallowed（不 increment SuccessCount / FailureCount）；catch 块注释"expected on Stop, do not count as failure"。

**选项 B**: OCE 抛出后 increment `FailureCount`（"cancel 也是失败"）。

**选项 C**: 新增 `_canceledCount` 字段单独计数。

**决策**: A. 理由: (a) 选项 B 把"用户主动 Stop"等同于"硬件失败"语义错误，UI 会误报 "X frames failed"；(b) 选项 C 增加 surface area（property + log），scope creep；(c) 选项 A 与 `async void` Timer callback pattern 一致（OCE 是 timer disposal 的 expected outcome）；(d) v1.6.2 之后 race tests 仍 assert `FailureCount == 0` / `SuccessCount > 0` 不变（因为 cancel 不计数）。

### Decision 6: Item 1 — `Stop()` 后 `OnTimerTick` callback 还可能 fire 吗？

**选项 A** (推荐): `Stop()` 内 `_timer?.Dispose()` 后，已 queue 但未执行的 callback 仍可能被 ThreadPool 执行（Timer.Dispose 不 cancel queued callbacks）。`OnTimerTick` 入口 `lock(this) { if (!_isRunning) return; }` 检查 → 直接 return（不再 encode/send）。Item 1 的 CT cancel 处理的是 **已过 lock check + 已 await SendAsync** 的 in-flight tick。

**选项 B**: 用 `Timer.Dispose(WaitHandle)` 同步等待所有 queued callback 完成，再 cancel CTS。

**选项 C**: `Stop()` 内先 `_cts.Cancel()` 再 `_timer?.Dispose()`（与选项 A 顺序相反）。

**决策**: A. 理由: (a) 选项 B 阻塞 Stop 调用方（user click → UI freeze），UX regression；(b) 选项 C 顺序不影响语义（Cancel 在 ThreadPool callback 触发时已生效）；(c) 选项 A 与现有 `_timer?.Dispose()` 模式一致（v1.2.12 PATCH 已 ship）；(d) Item 1 new test 验证 "in-flight tick 的 SendAsync 调用时 ct.IsCancellationRequested == true"（不验证 Stop 后 queued callback 不 fire，那是现有 lock check 行为，v1.2.12 PATCH Item 10 已 ship）。

### Decision 7: Item 1 — 测试侧 `CountingSendService` 需扩展验证 CT

**选项 A** (推荐): `CyclicSendServiceRaceTests.CountingSendService` (line 34-47) + `CyclicDbcSendServiceRaceTests.CountingSendService` (line 34-47) 增加 `CancellationToken LastObservedCt { get; private set; }` 字段；`override SendAsync(frame, ct)` 内 `LastObservedCt = ct;`。Item 1 new test 验证"Stop 后 in-flight tick 调用 SendAsync 时 `LastObservedCt.IsCancellationRequested == true`"。

**选项 B**: 用 NSubstitute mock `SendService` 并 verify `SendAsync(Arg.Any<CanFrame>(), Arg.Any<CancellationToken>())`。**问题**：v1.6.1 PATCH lesson 2 "DbcEncodeService sealed+non-virtual blocks mock" + `SendService` 部分 virtual（`SendAsync` 是 virtual），NSubstitute 可 mock 抽象/接口但 SendService 是具体 class，partial mock 在 xunit + NSubstitute 4+ 可用但需 `Mock.Of<SendService>()` + `protected_members` 配置。

**选项 C**: 不验证 CT 传递（只验证"Stop 后 in-flight SendAsync 不 increment counter"）。**太弱**：不能区分"re-check 阻止 send" vs "CT cancel 阻止 send"。

**决策**: A. 理由: (a) 选项 B 引入 NSubstitute SendService partial mock = test infrastructure change；现有 `CountingSendService` 子类 pattern (v1.2.12 PATCH 已 ship) 沿用；(b) 选项 C 不能验证 Item 1 真正的 CT 传递路径（re-check 已 ship v1.6.1 PATCH Item 1，单独验证 CT cancel 是 Item 1 增量）；(c) 选项 A 加 `LastObservedCt` 是 1 字段，最小改动。

### Decision 8: Item 1 — new test 用什么断言方式？

**选项 A** (推荐): 2 个 new test（每 service 1 个），方法 `Stop_during_inflight_tick_cancels_SendAsync`：start timer → `WaitUntilAsync(callcount > 0)` 等到 1 次 send 发生 → Stop → 短暂 drain（`Task.Delay(50)` 让 cancel signal propagate） → 断言 `LastObservedCt.IsCancellationRequested == true`。

**选项 B**: 2 个 new test 用 `AssertWithinAsync`（v1.6.1 PATCH Item 1 的 0 tests 教训——"deterministic test of Stop catches in-flight tick at 1-μs re-check window is not constructible"，同理 Stop 触发 CT 也不是 deterministic）——断言 last-observed CT。

**选项 C**: 0 个 new test（v1.6.1 PATCH Item 1 0 tests 教训）。Item 1 纯 production 改造，无新测试覆盖。

**决策**: A. 理由: (a) 选项 B/C 不可行原因——CT 传递**是** deterministic 的（OnTimerTick 同步读 `_cts.Token` 传 SendAsync，ct 字段同步可见）；不像 re-check 1μs race window 不可构造；(b) Item 1 的可测 invariant 是"OnTimerTick 把 _cts.Token 传给 SendAsync"——drain 50ms 后读 `LastObservedCt.IsCancellationRequested`；(c) 与 v1.6.1 PATCH Item 1 的 0 tests 不同——v1.6.1 测试 re-check race window 是 non-deterministic，Item 1 测试 CT 传递是 deterministic。

### Decision 9: Item 1 — `Dispose()` 路径

**选项 A** (推荐): `CyclicSendService` 已 `implements IDisposable` (line 32)。Item 1 在 `Dispose()` 内 `lock(this) { StopInner(); _cts?.Dispose(); }`。`CyclicDbcSendService` 当前没 `IDisposable` (line 47-ish) — **v1.6.2 必加 `IDisposable` 实现** + mirror Dispose pattern。

**选项 B**: `Dispose()` 只调 `StopInner()`，不 dispose CTS（泄漏 CTS）。

**决策**: A. 理由: (a) 选项 B 泄漏 `CancellationTokenSource`（内部 `ManualResetEvent` 等 native handle）；(b) v1.6.2 PATCH 引入 CTS = 同时引入 Dispose 责任，与既有 `IDisposable` 模式一致；(c) `CyclicDbcSendService` 当前无 `IDisposable` 是技术债（v1.6.2 顺手还）；DI 容器（如果未来接入）需要 IDisposable。

## Architecture / API surface

### Item 1 — CT refactor (production 改动, 2 files)

```csharp
// src/PeakCan.Host.App/Services/CyclicSendService.cs (modify)

// 1. ADD field after _isRunning:
private CancellationTokenSource? _cts;

// 2. Modify Start (line 73-95) — dispose old CTS, create new:
public void Start(CanFrame frame, TimeSpan interval)
{
    long gen;
    lock (this)
    {
        StopInner();
        _frame = frame;
        _interval = interval;
        _isRunning = true;
        Interlocked.Exchange(ref _sendSuccessCount, 0);
        Interlocked.Exchange(ref _sendFailureCount, 0);
        // v1.6.2 PATCH Item 1: dispose old CTS + create new for fresh
        // cancellation scope. Without this, a previous Start's CTS would
        // already be cancelled and any new tick would throw OCE on SendAsync.
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        gen = ++_generation;
        _timer = new Timer(OnTimerTick, gen, interval, interval);
    }
    LogCyclicStarted(_logger, frame.Id, interval.TotalMilliseconds);
}

// 3. Modify StopInner (line 106-116) — cancel CTS:
private void StopInner()
{
    if (!_isRunning) return;
    _isRunning = false;
    _generation++;
    _timer?.Dispose();
    _timer = null;
    // v1.6.2 PATCH Item 1: cancel in-flight SendAsync by cancelling CTS.
    // OnTimerTick reads _cts.Token under lock; cancel propagates to
    // ch.WriteAsync(frame, ct) which honors the token.
    _cts?.Cancel();
    LogCyclicStopped(_logger, SuccessCount);
}

// 4. Modify OnTimerTick (line 118-160) — pass ct + catch OCE:
private async void OnTimerTick(object? state)
{
    CanFrame frame;
    TimeSpan interval;
    long generation;
    CancellationToken ct;
    lock (this)
    {
        if (!_isRunning) return;
        frame = _frame;
        interval = _interval;
        generation = _generation;
        // v1.6.2 PATCH Item 1: snapshot CTS.Token under same lock as
        // _isRunning + _frame + _generation. ct reflects current
        // cancellation state at tick start.
        ct = _cts?.Token ?? CancellationToken.None;
    }
    if (state is long tickGen && tickGen != generation) return;

    try
    {
        var result = await _sendService.SendAsync(frame, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            Interlocked.Increment(ref _sendSuccessCount);
        }
        else
        {
            var count = Interlocked.Increment(ref _sendFailureCount);
            if (count % 100 == 0 && _logger is not null)
            {
                LogCyclicSendFailed(_logger, frame.Id, result.Error!.Code, result.Error.Message);
            }
        }
    }
    catch (OperationCanceledException)
    {
        // v1.6.2 PATCH Item 1: expected on Stop — do not count as failure.
        // async void timer callback would crash process if uncaught.
    }
    catch (Exception ex)
    {
        var count = Interlocked.Increment(ref _sendFailureCount);
        if (count % 100 == 0 && _logger is not null)
        {
            LogCyclicSendThrew(_logger, frame.Id, ex);
        }
    }
}

// 5. Modify Dispose (line 162-168) — dispose CTS:
public void Dispose()
{
    lock (this)
    {
        StopInner();
        _cts?.Dispose();
        _cts = null;
    }
}
```

```csharp
// src/PeakCan.Host.App/Services/CyclicDbcSendService.cs (mirror — same shape as CyclicSendService)

// 1. ADD field + IDiposable (was missing — see Decision 9):
public sealed partial class CyclicDbcSendService : ICyclicDbcSendService, IDisposable
{
    // ... existing fields ...
    private CancellationTokenSource? _cts;

    // ... existing ctor unchanged ...

// 2. Modify StartInner — dispose old CTS, create new (mirror CyclicSendService.Start)

// 3. Modify StopInner — cancel CTS (mirror CyclicSendService.StopInner)

// 4. Modify OnTimerTick — pass ct + catch OCE (mirror CyclicSendService.OnTimerTick)

// 5. ADD Dispose (was missing — see Decision 9):
public void Dispose()
{
    lock (this)
    {
        StopInner();
        _cts?.Dispose();
        _cts = null;
    }
}
```

### Item 2 — Race-test fire-timer → predicate-based migration (test-only, 9 sites)

```csharp
// tests/PeakCan.Host.App.Tests/Services/CyclicDbcSendServiceRaceTests.cs (modify 5 sites)

// Site 1 (line 96): OnTimerTick_Generation_Mismatch_Does_Not_Send, fire gen 1
// BEFORE:
//     svc.Start(() => (MakeMessage(0x100), ...), TimeSpan.FromMilliseconds(20));
//     await Task.Delay(60);  // let timer fire for gen 1
//     svc.Stop();
// AFTER:
//     svc.Start(() => (MakeMessage(0x100), ...), TimeSpan.FromMilliseconds(20));
//     await CyclicTimerTestHarness.WaitUntilAsync(
//         () => send.CallCount > 0,
//         TimeSpan.FromMilliseconds(500));
//     svc.Stop();

// Site 2 (line 98): drain after gen 1 Stop — PRESERVE as Task.Delay (Decision 3)
//     await Task.Delay(40);  // drain gen 1

// Site 3 (line 105): OnTimerTick_Generation_Mismatch_Does_Not_Send, fire gen 2
// BEFORE:
//     svc.Start(() => (MakeMessage(0x200), ...), TimeSpan.FromMilliseconds(20));
//     await Task.Delay(80);  // let timer fire for gen 2
//     svc.Stop();
// AFTER:
//     svc.Start(() => (MakeMessage(0x200), ...), TimeSpan.FromMilliseconds(20));
//     await CyclicTimerTestHarness.WaitUntilAsync(
//         () => send.SentIds.Any(id => id == 0x200u),
//         TimeSpan.FromMilliseconds(500));
//     svc.Stop();

// Site 4 (line 125): Encode_Failure_Increments_FailureCount_Not_SuccessCount, fire encode-fail
// BEFORE:
//     svc.Start(() => (msg, new Dictionary<string, double> { ["S"] = 200.0 }),
//               TimeSpan.FromMilliseconds(20));
//     await Task.Delay(120);
//     svc.Stop();
// AFTER:
//     svc.Start(() => (msg, new Dictionary<string, double> { ["S"] = 200.0 }),
//               TimeSpan.FromMilliseconds(20));
//     await CyclicTimerTestHarness.WaitUntilAsync(
//         () => svc.FailureCount > 0,
//         TimeSpan.FromMilliseconds(500));
//     svc.Stop();

// Site 5 (line 141): Send_Success_Increments_SuccessCount_Not_FailureCount, fire send-success
// BEFORE:
//     svc.Start(...);
//     await Task.Delay(120);
//     svc.Stop();
// AFTER:
//     svc.Start(...);
//     await CyclicTimerTestHarness.WaitUntilAsync(
//         () => svc.SuccessCount > 0,
//         TimeSpan.FromMilliseconds(500));
//     svc.Stop();

// Site 6 (line 159): Send_Failure_Increments_FailureCount_Not_SuccessCount, fire send-fail
// BEFORE:
//     svc.Start(...);
//     await Task.Delay(120);
//     svc.Stop();
// AFTER:
//     svc.Start(...);
//     await CyclicTimerTestHarness.WaitUntilAsync(
//         () => svc.FailureCount > 0,
//         TimeSpan.FromMilliseconds(500));
//     svc.Stop();
```

```csharp
// tests/PeakCan.Host.App.Tests/Services/CyclicSendServiceRaceTests.cs (modify 4 sites)

// Site 1 (line 86): OnTimerTick_After_Stop_Does_Not_Send, drain after Stop — PRESERVE as Task.Delay (Decision 3)
//     await Task.Delay(150);  // drain

// Site 2 (line 101): OnTimerTick_Generation_Mismatch_Does_Not_Send, fire gen 1
// AFTER:
//     svc.Start(BuildFrame(0x100), TimeSpan.FromMilliseconds(20));
//     await CyclicTimerTestHarness.WaitUntilAsync(
//         () => send.CallCount > 0,
//         TimeSpan.FromMilliseconds(500));
//     svc.Stop();

// Site 3 (line 105): drain after gen 1 Stop — PRESERVE as Task.Delay (Decision 3)
//     await Task.Delay(40);  // drain

// Site 4 (line 111): OnTimerTick_Generation_Mismatch_Does_Not_Send, fire gen 2
// AFTER:
//     svc.Start(BuildFrame(0x200), TimeSpan.FromMilliseconds(20));
//     await CyclicTimerTestHarness.WaitUntilAsync(
//         () => send.SentIds.Any(id => id == 0x200u),
//         TimeSpan.FromMilliseconds(500));
//     svc.Stop();

// Site 5 (line 131): Send_Failure_Increments_FailureCount_Not_SuccessCount, fire send-fail
// AFTER:
//     svc.Start(BuildFrame(0x100), TimeSpan.FromMilliseconds(20));
//     await CyclicTimerTestHarness.WaitUntilAsync(
//         () => svc.FailureCount > 0,
//         TimeSpan.FromMilliseconds(500));
//     svc.Stop();

// Site 6 (line 144): Send_Success_Increments_SuccessCount_Not_FailureCount, fire send-success
// AFTER:
//     svc.Start(BuildFrame(0x100), TimeSpan.FromMilliseconds(20));
//     await CyclicTimerTestHarness.WaitUntilAsync(
//         () => svc.SuccessCount > 0,
//         TimeSpan.FromMilliseconds(500));
//     svc.Stop();
```

### Item 1 — new tests (2)

```csharp
// tests/PeakCan.Host.App.Tests/Services/CyclicSendServiceRaceTests.cs (add 1 new test)

// v1.6.2 PATCH Item 1: verify Stop() cancels in-flight SendAsync via CT.
[Fact]
public async Task Stop_during_inflight_tick_cancels_SendAsync_via_ct()
{
    var send = new CountingSendService();
    var svc = new CyclicSendService(send, NullLogger<CyclicSendService>.Instance);
    svc.Start(BuildFrame(0x100), TimeSpan.FromMilliseconds(20));

    // Wait for at least one tick so SendAsync has been called once.
    await CyclicTimerTestHarness.WaitUntilAsync(
        () => send.CallCount > 0,
        TimeSpan.FromMilliseconds(500));

    svc.Stop();

    // Drain briefly so any in-flight SendAsync callback completes its
    // observation of _cts.Token. 50ms is more than enough — the call
    // returns synchronously from our CountingSendService.
    await Task.Delay(50);

    // The LAST SendAsync invocation's observed CT must be cancelled.
    send.LastObservedCt.IsCancellationRequested.Should().BeTrue(
        "Stop() must cancel the CTS so in-flight SendAsync receives a cancelled token");
}

// Modify CountingSendService (line 34-47):
private sealed class CountingSendService : SendService
{
    public CountingSendService() : base(NullLogger<SendService>.Instance) { }
    public Result<Unit> NextResult { get; set; } = Result<Unit>.Ok(default);
    public int CallCount { get; private set; }
    public List<uint> SentIds { get; } = new();
    // v1.6.2 PATCH Item 1: track the CT observed by the most recent SendAsync.
    public CancellationToken LastObservedCt { get; private set; }

    public override ValueTask<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct = default)
    {
        CallCount++;
        SentIds.Add(frame.Id.Raw);
        LastObservedCt = ct;
        return ValueTask.FromResult(NextResult);
    }
}
```

```csharp
// tests/PeakCan.Host.App.Tests/Services/CyclicDbcSendServiceRaceTests.cs (add 1 new test)

// v1.6.2 PATCH Item 1: verify Stop() cancels in-flight SendAsync via CT.
[Fact]
public async Task Stop_during_inflight_tick_cancels_SendAsync_via_ct()
{
    var send = new CountingSendService();
    var svc = new CyclicDbcSendService(
        new DbcEncodeService(), send, NullLogger<CyclicDbcSendService>.Instance);
    svc.Start(() => (MakeMessage(0x100), new Dictionary<string, double> { ["S"] = 1 }),
              TimeSpan.FromMilliseconds(20));

    await CyclicTimerTestHarness.WaitUntilAsync(
        () => send.CallCount > 0,
        TimeSpan.FromMilliseconds(500));

    svc.Stop();
    await Task.Delay(50);

    send.LastObservedCt.IsCancellationRequested.Should().BeTrue(
        "Stop() must cancel the CTS so in-flight SendAsync receives a cancelled token");
}

// Modify CountingSendService mirror: add LastObservedCt property + capture in SendAsync override.
```

## 测试策略汇总

| Suite | v1.6.1 baseline | v1.6.2 PATCH | Delta |
|---|---|---|---|
| Core | 338 | 338 | 0 |
| App | 403 | 405 | +2 (Item 1: 1 new test per service × 2 services) |
| Infra | 84 | 84 | 0 |
| **Total** | **825** | **827** | **+2** (4 SKIP unchanged → 827 + 4 SKIP) |

Item 2 是 migration-only（替换 9 处 `Task.Delay` 为 `WaitUntilAsync`），不增加 test 数量；保留 4 处 drain wait 不变。

### Item 1 tests (2 new)

- `CyclicSendServiceRaceTests.Stop_during_inflight_tick_cancels_SendAsync_via_ct`：start → WaitUntil(CallCount > 0) → Stop → drain 50ms → assert `LastObservedCt.IsCancellationRequested == true`
- `CyclicDbcSendServiceRaceTests.Stop_during_inflight_tick_cancels_SendAsync_via_ct`：mirror 同上

### Item 2 migrations (9 sites, 0 new tests)

- 5 sites in `CyclicDbcSendServiceRaceTests.cs`: lines 96, 105, 125, 141, 159
- 4 sites in `CyclicSendServiceRaceTests.cs`: lines 101, 111, 131, 144

每个 site 的 predicate 模式：
- fire gen 1/gen 2: `() => send.CallCount > 0` / `() => send.SentIds.Any(id => id == <gen-id>)`
- fire encode-fail: `() => svc.FailureCount > 0`
- fire send-success: `() => svc.SuccessCount > 0`
- fire send-fail: `() => svc.FailureCount > 0`

Timeout 统一 500ms（Decision 4）。

### Item 1 test stability

2 new test 都用 `WaitUntilAsync`（已 ship） + drain 50ms + 同步断言（`LastObservedCt.IsCancellationRequested` 是同步 property）。**不**依赖 time-based 启发式。CI 慢/快都不影响。

## Risks

### Item 1

- **MEDIUM**: `async void` OnTimerTick 未捕获 OCE 会 crash process — 必须 catch `OperationCanceledException`（已纳入 Decision 5 + Architecture 段 line 4）。
- **LOW**: `Start()` 必须 dispose 旧 CTS + 创建新 — 否则 Start 第二次时旧 CTS 已被 cancel，新 tick 立即 OCE（已纳入 Decision 2）。
- **LOW**: `Dispose()` 必须 dispose CTS — 否则 CancellationTokenSource 内部 native handle 泄漏（已纳入 Decision 9）。
- **LOW**: OCE 不 increment FailureCount — UI "X frames failed" 不会被用户主动 Stop 污染（已纳入 Decision 5）。
- **LOW**: 2 个 manual-send caller 不传 CT — 不在 v1.6.2 scope，blast radius 受限（Non-Goals 段）。
- **LOW**: `_cts` 字段在 `lock(this)` 内读 — 与现有 `_isRunning` / `_frame` / `_generation` 一致（Decision 1）。
- **LOW**: `Timer.Dispose()` 后 queued callback 仍可能 fire — 已 ship 的 lock check 兜底（Decision 6）。

### Item 2

- **LOW**: `WaitUntilAsync(predicate, timeout=500ms)` 总等待 ≤ 500ms — 比 `Task.Delay(120)` × N 处总等待 960ms (Dbc) + 640ms (Send) = 1600ms **更快**。
- **LOW**: predicate false 永真时 `WaitUntilAsync` 返回 false（不抛）—— caller 必须紧跟 stop + assert，PR plan 必做 review。
- **LOW**: 4 处 drain wait 保留 — 与 v1.6.1 PATCH Decision 5 "preserve drain semantics" 一致（Decision 3）。
- **LOW**: timeout 500ms 是 fixed constant — 与 v1.6.1 PATCH Decision 6 "hard-coded constants" 风格一致（Decision 4）。

## Cross-cutting concerns (from v1.6.1 PATCH + project conventions)

| Concern | Action |
|---|---|
| Source-gen LoggerMessage | Item 1 不引入新 log。 |
| SynchronizationContext capture in VMs | Item 1 不变（service 内部，与 VM 无关）。 |
| `PathNormalizer` defense-in-depth | Item 1/2 不动 IO，natural compliance。 |
| STA-WPF test discipline | Item 1 new test 用 `CountingSendService` subclass + `WaitUntilAsync`（已 ship）；无 WPF。 |
| NetArchTest rule 2 (Core no PEAK SDK) | Item 1 不引入 PEAK SDK；只动 App layer。 |
| Race-test transient-flaky caveat | Item 2 predicate-based 改造从源头减少 time-based flake；harness 内部 3-retry 兜底。 |
| `[Obsolete]` 是 safe-to-remove signal | 本 PATCH 不引入 / 不移除 `[Obsolete]`。 |
| CSharp `lock(this)` anti-pattern | Item 1 复用现有 `lock(this)` (service 是 sealed, 无外部继承)。 |
| CommunityToolkit.Mvvm version | Item 1/2 不依赖 CT.Mvvm。 |
| xunit.retry NuGet | Item 1/2 不引入（保持 0-NuGet 决议）。 |
| v1.6.0 MINOR scope | 全部 5 项明确 deferred，本 PATCH 严守 scope。 |

## Ship method (mirror v1.6.1 PATCH)

1. `git checkout -b feature/v1-6-2-patch` (cut from main `0556843`, already reset to v1.6.1 squash).
2. Spec + plan committed to feature branch (this file + `docs/superpowers/plans/2026-06-29-v1-6-2-patch.md`).
3. TDD per-item commits (RED → GREEN → IMPROVE × 2 items):
   - Item 1 first (production CT refactor, biggest blast radius; needs RED test first).
     - RED: write `Stop_during_inflight_tick_cancels_SendAsync_via_ct` × 2; run → fail (CT not propagated yet).
     - GREEN: implement CT refactor in `CyclicSendService` + `CyclicDbcSendService`; run → pass.
     - IMPROVE: catch OCE; dispose CTS in Start + Dispose; manual review lock-then-snapshot ct.
   - Item 2 second (test-only migration, low risk):
     - Migrate 9 sites × 2 commits (per file): replace `Task.Delay` with `WaitUntilAsync`, keep drain waits.
4. Pre-ship `code-reviewer` subagent → address 0C/0H; MEDIUM findings expected to be ≤ 2 (per pattern).
5. `docs/release-notes-v1.6.2.md` authored (mirror v1.6.1 format: items + tests + process lessons + brief-vs-source drift).
6. `git -c http.proxy="" -c https.proxy="" push -u origin feature/v1-6-2-patch` (mirror v1.6.1 network workaround).
7. `gh pr create --base main --title "v1.6.2 PATCH: Cyclic* CT refactor + race-test fire-timer → predicate-based migration" --body-file docs/release-notes-v1.6.2.md`.
8. `gh pr merge --squash --delete-branch` (expect proxy-flaky on first attempt; recover per v1.6.1 ship method).
9. `git fetch origin main + git reset --hard origin/main` (v1.6.1 ship method: ship 后第一次 reset 必先 fetch).
10. Tag `v1.6.2` + `git push origin v1.6.2`.
11. `gh release create v1.6.2 --notes-file docs/release-notes-v1.6.2.md --title "v1.6.2 PATCH"`.
12. Update `~/.claude/projects/D--claude-proj2/memory/MEMORY.md` index + write `peakcan-host-v1-6-2-shipped.md` topic file (mirror v1.6.1).

## Open Questions

- **Item 1**: `OnTimerTick` 是否需新增 `_cts?.Token` 在 lock 外的二次快照？——本 spec 仅在 lock 内读一次，理由：(a) lock 内读与现有 `_isRunning`/`_frame`/`_generation` 一致；(b) cancel 后 thread 进入 OnTimerTick body 时 ct 字段已更新（Cancel 是同步）；(c) 二次快照是 YAGNI。PR plan 必做 code-review 验证 thread-safety。
- **Item 1**: `CyclicDbcSendService` 当前无 `IDisposable` (Decision 9) — 加 `IDisposable` 是否会破坏现有 DI 注册？——grep DI 注册（`AppHostBuilder.cs` / 任何 `IServiceCollection` 注册）确认无 DI 注册；如果纯手工 `new`（看现有 race test 实例化 pattern），加 `IDisposable` 是非破坏。
- **Item 2**: `WaitUntilAsync(predicate, timeout=500ms)` 4 处 fire gen 1/2 的 predicate 是 `() => send.SentIds.Any(id => id == <gen-id>)` (2 个 service 各 2 处: `CyclicDbcSendServiceRaceTests.cs:96, 105` + `CyclicSendServiceRaceTests.cs:101, 111`)——这会 fire 1 次就 return。如果 timer interval 20ms + 首 tick 5ms 抖动，predicate 在 25ms 内 true → 比 `Task.Delay(60/80)` 更快。**无 flake 风险**（更快 = 更好）。
- **Item 2**: timeout 500ms 是否覆盖 CI 慢场景？——timer interval 20ms × 25 fires 上限 = 500ms；CI 慢到 25 fires 需 > 500ms 是极端。后续如发现 CI 慢到 500ms 不够，提升 timeout 到 1000ms 是 1 行修改（harness 调用点 timeout 参数）。
- **v1.6.0 MINOR scope**: 5 项是否在 v1.6.2 PATCH 之后立即启动？——不在本 spec 范围。memory `peakcan-host-v1-6-1-shipped.md` 明确 deferred 到 v1.6.x MINOR。
- **未来 CT 完整化**：4 个 manual-send caller（`SendViewModel` / `DbcSendViewModel` / `CanApi` / `AppHostBuilder`）+ 4 个 service 自身 CT 化 → 完整 "user click Stop → all in-flight cancelled" 链路。**不在 v1.6.2 scope**（Non-Goals 段）。Defer to v1.6.x future PATCH 评估。