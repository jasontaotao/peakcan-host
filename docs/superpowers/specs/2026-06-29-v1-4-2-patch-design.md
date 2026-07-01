# v1.4.2 PATCH — UdsSecurity SetSeed preserves lockout state + ReplayFrameSinkAdapter surfaces first-failure

**Date:** 2026-06-29
**Branch:** `feature/v1-4-2-patch` (cut from main @ v1.5.0 `9f0bb9e`)
**Target version:** v1.4.2 (PATCH)
**Spec owner:** main agent
**Reviewer:** code-reviewer subagent (pre-ship)
**Pre-flight:** Phase 2.5 actual code exploration (UdsSecurity, UdsClient, ReplayFrameSinkAdapter, ReplayExceptions, ReplayService, ReplayTimeline, IReplayService)

## 概述

v1.4.2 是一个 3 项 PATCH，关闭 v1.5.0 release notes §"Known follow-ups" + OI memory `udssecurity-setseed-wipes-attempt-count` 列出 2 项 carry-over（1 项 HIGH + 1 项 MEDIUM）。1 项 test-only 重新激活 v1.4.1 PATCH Item 1 SKIPPED 测试（同一 fix 让原本失败的 lockout-flip race 现在可观测）。无 UDS 协议增强；无 DI 拓扑变化；无用户数据 schema 变更；additive only。

## 起源

| Source | Item | Severity |
|--------|------|----------|
| v1.4.1 PATCH Task 1 race test discovery (OI `udssecurity-setseed-wipes-attempt-count`) | HIGH — `UdsSecurity.SetSeed` wipes `AttemptCount` + `LockedUntilUtc` on successful `RequestSeed`, defeating v1.3.0 lockout under concurrent access | HIGH |
| v1.4.1 PATCH Item 1 Test 2 SKIPPED with rationale "defer to v1.4.2" | Test re-enable: `TwoArg_Overload_ConcurrentMidHandshakeLockoutFlip_PostStateConsistent` | (test-only) |
| v1.4.0 MINOR Task 6 review (carry-over to v1.4.1) | MEDIUM — `ReplayFrameSinkAdapter` first-failure silently dropped (no `Result<Unit>` consumption); user-hostile silent drop on no-channel | MEDIUM |

### Decomposition context (v1.5.0 release notes §"Decomposition")

v1.4.2 PATCH 是 v1.5.0 MINOR spec 显式标出 3 项 future work 中的 2 项（SetSeed + ReplaySinkAdapter）+ v1.4.1 PATCH Test 2 重新激活。v1.4.1 文档原话："v1.4.2 PATCH (planned): HIGH — `UdsSecurity.SetSeed` wipes lockout counter (carry-over from v1.4.1 out-of-scope finding) + MEDIUM `ReplaySinkAdapter` surface first-failure as `ReplayException`"。3 个 planned MINOR（v1.5.1 PATCH: Replay time-range filter + Periodic DBC send; v1.6.0 MINOR: V8 sandbox + CanApi rate limit + DBC size limit + path root restriction + OEM KeyDerivation concrete）保持 deferred。

## Product decision (state, do NOT re-litigate)

**"Counter 跨 success 保留"**：每次 `SetSeed` 必须 preserve `SecurityLevelState.AttemptCount` + `LockedUntilUtc`（对齐 v1.3.0 spec D8 — `Reset()` 路径已守的 invariant：`lockout is independent of session state`）。`SetSeed` 从"创建新 state"改为"在 existing state 上 mutate（first observation 才 new）"。

**Rationale**:
- v1.3.0 D8 spec 显式：lockout 是 security policy，session 变化都不能 reset（attacker 可通过 session change 抹除 counter）。
- `RequestSeed` success（ECU 返回 seed bytes）不等于"successful authentication" — 它只是 flow control。`SendKey` success 才是（`SetAuthenticated` + `ResetLockout`）。中间穿插的 `SetSeed` 不应 reset counter。
- Industry norm（AUTOSAR SWS_SecOC, HIS security specs）以 counter persists 为主流；OEM-specific override 走 `UdsSecurityLockoutConfig` 而不是把"是否 persist"做成 enum（避免 API surface bloat — PATCH 纪律）。

**Rejected alternative**: "Counter resets on success" — 维持现状的话，concurrent attacker 只需在两个 fail 之间插一个 success 就能 reset counter；lockout 在 TOCTOU window 内实质无效。

## Phase 2.5 actual code exploration findings

实际读 v1.5.0 shipped 代码（main @ `9f0bb9e`）确认 brief 描述准确 + 锁定关键 design points：

| Assumption | Phase 2.5 actual |
|---|---|
| **Item 1**: `UdsSecurity.SetSeed` 当前 `lock (_levels) { _levels[level] = new SecurityLevelState { Seed, IsAuthenticated = false }; }` | **确认**。`UdsSecurity.cs:28-38` line 32 创建新 `SecurityLevelState`（无显式 `AttemptCount`/`LockedUntilUtc` 赋值 → `default(int)=0` / `default(DateTime?)=null`）。其他 call site: `GetSeed` (line 19-25), `SetAuthenticated` (line 41-50), `Reset` (line 66-80, **已 preserve AttemptCount/LockedUntilUtc per D8**), `IsLocked` (line 87-95), `RecordFailedAttempt` (line 119-132, line 130 `_levels[level] = state` 写入), `ResetLockout` (line 140-150). |
| **Item 1**: `SecurityLevelState` 是 `private sealed class`（line 152-160），4 fields: `Seed`/`IsAuthenticated`/`AttemptCount`/`LockedUntilUtc` | **确认**。`private sealed class` 在 `UdsSecurity` 内 nested。`RecordFailedAttempt` 用 `var state = _levels.TryGetValue(level, out var s) ? s : new SecurityLevelState();` pattern（line 123）— 同一个 mutate-existing-or-create-new pattern 应该应用到 `SetSeed`。 |
| **Item 1**: `UdsClient.SecurityAccessAsync` (3-arg) line 312 调 `Security.SetSeed(level, response[1..])` after `RequestSeed` leg | **确认**。`UdsClient.cs:309-314` if-branch。`SecurityAccessAsync` 2-arg overload line 381-408: line 398 `await SecurityAccessAsync(requestLevel, key: null, ct)` 走 3-arg overload 的 `key==null` 分支，触发 `SetSeed`；line 407 再调 `SecurityAccessAsync(requestLevel, key, ct)` 走 `key!=null` 分支，调 `SetAuthenticated` + `ResetLockout`（line 318-319）。**Race window**: line 398 和 line 407 之间，另一个并发 caller 的 `SetSeed` 可能 reset counter。 |
| **Item 1**: v1.3.0 spec D8 invariant "lockout independent of session" 已由 `Reset()` 守 | **确认**。`UdsSecurity.cs:75-78` XML doc + `Reset()` body 显式保留 `AttemptCount` 和 `LockedUntilUtc`。`SetSeed` 必须对齐同一 invariant。 |
| **Item 2**: SKIPPED 测试 line 250 `[Fact(Skip = "Phase 2.5 discovered pre-existing SetSeed-wipes-state bug; defer to v1.4.2")]` + body `await Task.CompletedTask;` | **确认**。`UdsClientConcurrentSecurityAccessTests.cs:250-255`。Body 是 placeholder（v1.4.1 留下的"测试 stub 形态"）。Test 1 (line 171) `TwoArg_Overload_TwoConcurrentCalls_ProduceExactlyFourWireFrames` 提供 harness pattern (IsoTpLayer + capture + `AutoRespondSeedOkThenNrc35` helper line 135-161 + `Task.WhenAny` deadline line 188-192 + `WrapResult` helper line 263-273)。 |
| **Item 2**: 2-arg overload TOCTOU window documented at `UdsClient.cs:369-379` XML doc `<remarks>` | **确认**。显式说 "between the RequestSeed leg completing and the SendKey leg starting, a concurrent caller may exhaust the lockout counter on the same level"。**这是 fix 前的 race 行为**。fix 后 race 仍存在但 lockout 不会被 wipe。 |
| **Item 3**: `ReplayFrameSinkAdapter` 当前 `_send.SendAsync(canFrame, ct).ConfigureAwait(false);` line 69 discard `Result<Unit>` | **确认**。`ReplayFrameSinkAdapter.cs:56-70` `SendFrameAsync`. Line 67 design comment 显式说 "timer callback cannot await user error policy" + "matches the design intent of IReplayFrameSink"。**该 design comment 是 OI 标记为需要改的原因**：v1.4.0 添加 sink-throw tolerance 时，timer thread 仍 fire-and-forget，user error policy 退化到 "silent drop"。 |
| **Item 3**: `SendService.SendAsync` 返回 `ValueTask<Result<Unit>>` | **确认**。`SendService.cs:73` `public virtual ValueTask<Result<Unit>> SendAsync(CanFrame frame, CancellationToken ct = default)`. `Result<T>` 在 `Core/Result.cs:13` 是 `readonly record struct` with `IsSuccess` + `TryGetValue` + `Match`. Adapter 当前 1 行 discard，必须改为 inspect + propagate. |
| **Item 3**: `ReplayService` 当前 sink-throw tolerance at `ReplayTimeline.OnTick` (line 184-186) `try { _emit(frame); } catch { /* sink errors must not stall playback */ }` | **确认**。`ReplayTimeline.cs:184-186` — **这层 catch 会吞 `ReplaySendException` 抛**！直接 throw from `ReplayFrameSinkAdapter.SendFrameAsync` 会被 `OnTick` 的 `catch { }` 吞掉。**必须在 `ReplayService` 层（不是 timeline 层）重新设计 error propagation**：要么 (a) timeline 把 sink exception capture 后存到 `ReplayService` 暴露的 `LastSinkException` field, PlaybackEnded 时 fire event; 要 (b) 改 timeline to NOT swallow specific exception; 要 (c) adapter 内部吞 + 存到 `LastSinkException` + throw at end-of-stream. **Recommendation**: option (a) — 在 `ReplayService` 加 `public Exception? LastSinkException { get; private set; }` + `event EventHandler<PlaybackEndedEventArgs>` (new) with `Error` property, 然后在 `OnTick` 后 replay-end 时 check `LastSinkException` and pass through. |
| **Item 3**: `IReplayService` 当前 `event EventHandler? PlaybackEnded` (line 54) with empty `EventArgs` | **确认**。`IReplayService.cs:54`. Add new `EventHandler<PlaybackEndedEventArgs>? PlaybackEnded` 替代 OR overload — **推荐 overwrite `PlaybackEnded` event signature 到 `EventHandler<PlaybackEndedEventArgs>` (breaking event signature change, OK 因为同一 release 中无 consumer outside App shell)**. UI code 在 `ReplayView.xaml` / `ReplayViewModel.cs` 检查 subscription. |
| **Item 3**: `ReplayExceptions.cs` 当前 2 types: `ReplayLoadException` + `ReplayFormatException` (both inherit `ReplayException`) | **确认**。`ReplayExceptions.cs:1-22`. Add new `public sealed class ReplaySendException : ReplayException` with `SendFailureReason` property (Error from Result.Error). |
| **Item 3**: `IReplayFrameSink.SendFrameAsync` signature `ValueTask SendFrameAsync(ReplayFrame frame, CancellationToken ct = default)` (line 11) | **确认**。`IReplayFrameSink.cs:11`. No public API break needed for Item 3. |

### Brief drift cautions (memory `phase-2-5-brief-drift-correction` 9-of-9+)

1. **`SecurityLevelState` is `private`** (line 152). Tests cannot directly read `AttemptCount`/`LockedUntilUtc` field. **Use** `UdsSecurity.IsLocked(level)` + `UdsSecurity.RemainingLockoutDelay(level)` for white-box assertions (both already public per v1.3.0 Item 1). For dark-box test of Item 1 fix, use the mid-handshake race scenario (Item 2 test) — race test is the right end-to-end test.
2. **`UdsSecurity` does not expose `AttemptCount` getter** (only `IsLocked` + `RemainingLockoutDelay`). Item 1 dedicated test asserting "counter preserved across SetSeed" can verify via: (a) `RecordFailedAttempt` x2 → `IsLocked` true → `SetSeed` → `IsLocked` **still true** (positive assertion). Do NOT need to read raw counter.
3. **`ResetLockout` is `public`** — Item 1 fix may want to use it; current proposal does not need to. Align with `Reset()` pattern (D8) — just preserve state.
4. **DO NOT** add a constructor parameter to `UdsSecurity` for "persist counter on success vs reset" flag — that's API surface bloat (PATCH 纪律). Just align `SetSeed` to D8 invariant.
5. **`ReplayService.LastSinkException` is NEW public field** — confirm App shell doesn't read it (it doesn't, per ReplayViewModel.cs grep). Adding it is additive; no consumer regression.
6. **`PlaybackEnded` event signature change** (`EventHandler` → `EventHandler<PlaybackEndedEventArgs>`) is the cleanest API. UI consumers must update handler signature; v1.5.0 has no external consumer. (Re-verify with `grep -rn "PlaybackEnded" src/ tests/` before deciding.)
7. **`ReplaySendException` placement**: in `src/PeakCan.Host.Core/Replay/ReplayExceptions.cs` (alongside existing 2 exception types). NO new file. NO DI registration (exceptions are static types).

## Scope

3 PATCH items = 1 src fix (HIGH) + 1 test re-enable (LOW, same fix) + 1 src fix (MEDIUM). MINOR 纪律保持（additive only, except Item 3's `PlaybackEnded` event signature change which is API-additive — old `EventHandler` consumer is `null` and the new `EventHandler<PlaybackEndedEventArgs>` is a compile-time break for any future consumer, but currently zero consumers).

| # | Item | 组件 | 工作 | Source | Severity |
|---|------|------|------|--------|----------|
| 1 | **`UdsSecurity.SetSeed` preserves lockout state** | Core: `UdsSecurity.cs` line 28-38 | Mutate existing `SecurityLevelState` in place; only create fresh on first observation. Add 2 unit tests (single-threaded + concurrent-mid-handshake). | v1.4.1 out-of-scope finding (OI) | HIGH |
| 2 | **Mid-handshake lockout flip test re-enable** | Core.Tests: `UdsClientConcurrentSecurityAccessTests.cs` line 250-255 | Flip `Skip = null`, write full test body, reuse `AutoRespondSeedOkThenNrc35` helper, assert 1 caller throws + `IsLocked` consistent. | v1.4.1 PATCH Test 2 carry-over | (test-only) |
| 3 | **`ReplayFrameSinkAdapter` surface first-failure as `ReplayException`** | Core: `ReplayExceptions.cs` (new type) + `IReplayService.cs` (event sig) + `ReplayService.cs` (`LastSinkException` field) + `ReplayTimeline.cs` (capture exception); App: `ReplayFrameSinkAdapter.cs` (throw on first fail) | Add `ReplaySendException` + new `PlaybackEndedEventArgs.Error` + adapter throws on first `IsSuccess==false`. UI may surface error in status bar. | v1.4.0 Task 6 review (carry-over to v1.4.1) | MEDIUM |

## Non-Goals

- **v1.4.2 PATCH scope (carry-overs NOT included)**: any other deferred from v1.5.0 release notes §"Known follow-ups" beyond the 2 items (SetSeed + ReplaySinkAdapter) + the test re-enable. Specifically:
  - Replay time-range filter (post-EOF scrubbing to arbitrary timestamp) — **v1.5.1 PATCH**.
  - Periodic DBC send (auto-send at fixed interval) — **v1.5.1 PATCH**.
  - V8 sandbox hardening — **v1.6.0 MINOR**.
  - CanApi rate limit — **v1.6.0 MINOR** (requires benchmark first).
  - DBC size/token limit — **v1.6.0 MINOR**.
  - Path normalization root restriction — **v1.6.0 MINOR** (replace v1.5.0 defense-in-depth with hard sandbox).
  - OEM `IKeyDerivationAlgorithm` concrete — **v1.6.0 MINOR**.
  - Replay→Trace auto-load + Value table encoding + Multiplexed signal groups UI — **future MINOR**.
- **Public API removal** — MINOR 纪律 (additive only).
- **NuGet package upgrade** — 不动。
- **`UdsSecurityLockoutConfig` API change** — 不动. (产品决策已定: counter persists 是 default 行为, 不做 enum 配置.)
- **Per-handshake mutex on `UdsClient.SecurityAccessAsync`** — 不动. v1.3.0 spec 显式记录 TOCTOU window 是 intentional; PATCH 关闭 counter wipe bug, 但保留 wire-level interleaving 容忍 (via `_requestLock` 串行化).
- **`UdsSecurity.SetSeed` 之外的 wipe 路径** — 检查 `UdsClient.cs:309-322` 之外是否有其他 `new SecurityLevelState` 创建点. **Phase 2.5 确认: 仅 `SetSeed` + `RecordFailedAttempt` (line 123 内部) 创建. RecordFailedAttempt 创建时 lock (_levels) 内 TryGetValue 优先, 不 wipe existing state.** 无其他 wipe 路径需要修.
- **Replay first-failure 后半段行为** — 当 `ReplaySinkAdapter` throw at frame N, **不**自动 continue 剩余 frames. Spec ADR-2 (open — 见下): 选项 A "first-failure aborts playback" (推荐) vs 选项 B "continue all + 报 first-failure at end". 推荐 A (User OI intent: "surface first-failure as `ReplayException`") — playback stops, `PlaybackEnded` event fires with `Error`, UI surfaces to user.

## 设计决策 (open / proposed)

### Decision 1: Item 1 — `SetSeed` mutate-in-place pattern (no API surface change)

**选项 A** (推荐): align `SetSeed` to `RecordFailedAttempt` (line 119-132) pattern — `var state = _levels.TryGetValue(level, out var s) ? s : new SecurityLevelState(); state.Seed = seed; state.IsAuthenticated = false; _levels[level] = state;`. Else-branch creates fresh (no prior state to preserve). Inside `lock (_levels)`.

**选项 B**: introduce a `bool preserveLockout` parameter to `SetSeed` (API break, public).

**决策**: A. 理由: 跟 `RecordFailedAttempt` 同样的 mutate-in-place pattern; 不引入新参数; 不破坏 v1.3.0 D8 invariant; 行为对所有 caller 一致 (production + tests).

### Decision 2: Item 1 — D8 invariant 在 SetSeed doc-comment 显式说明

**选项 A** (推荐): 在 `SetSeed` XML doc 显式引用 D8 + `Reset()` 的 XML doc line 75-78 ("lockout is a policy that should not be bypassed"), 跟同 file 内 `Reset()` / `RecordFailedAttempt` / `ResetLockout` 风格一致.

**选项 B**: 不加注释, 行为对齐就够.

**决策**: A. 理由: 跟同 file 已有 D8 引用 pattern 一致 (`Reset` line 75-78, `IsLocked` line 85, `ResetLockout` line 138); 未来 maintainer 看到 `new SecurityLevelState()` 会下意识问 "为什么不 mutate in place".

### Decision 3: Item 2 — Mid-handshake lockout flip test 新 body

**选项 A** (推荐): Reuse `AutoRespondSeedOkThenNrc35` helper (line 135-161); 2 concurrent `SecurityAccessAsync(0x01)`; `LockoutConfig = (MaxAttempts=2, LockoutDuration=5s)`. Both RequestSeed succeed (positive seed). Both SendKey fail (NRC 0x35). **First** SendKey failure → `RecordFailedAttempt` → `AttemptCount=1` (not locked). **Second** SendKey failure → `RecordFailedAttempt` → `AttemptCount=2` ≥ `MaxAttempts=2` → lockout set, `AttemptCount` reset to 0. One of the two callers sees `UdsSecurityLockedException` on subsequent `SendKey` (or on next handshake); the other already passed the pre-check. **Post-state**: `IsLocked(0x01) == true` OR `RemainingLockoutDelay(0x01) > TimeSpan.Zero`.

**选项 B**: 手动调 `RecordFailedAttempt` pre-set `AttemptCount=1`, 然后 2 concurrent `SecurityAccessAsync` + 1 failure inject. 测的是 boundary condition.

**决策**: A. 理由: 测试 v1.3.0 spec Item 1 的核心 invariant (lockout 在 concurrent 下触发), 不依赖 pre-set state. 测的是 fix 后行为 (counter 不被 SetSeed wipe), 所以 SetSeed fix 后 `AttemptCount` 累加正常到 2 → 触发 lockout. fix 前 wipe → 永远到不了 2 → 测试 fail (RED 验证 fix 前行为, GREEN 验证 fix 后行为).

### Decision 4: Item 2 — Skip rationale in XML doc (preserve across releases)

**选项 A** (推荐): 新 test body 顶部 XML `<remarks>` block 引 OI memory + v1.4.1 carry-over 起源 + fix commit SHA. Re-enable rationale 显式记录, 未来 maintainer 看 git blame 知道为什么之前 skip + 为什么现在 flip.

**选项 B**: 不加注释.

**决策**: A. 理由: memory v1.4.1 lesson "SKIP-with-rationale preserves author intent across releases"; v1.4.2 PATCH flip 之前必须更新 rationale 文本, 不能简单 `Skip = null` 就完事 (lesson 2 in OI memory).

### Decision 5: Item 3 — first-failure aborts playback (推荐) vs continue-and-report-at-end

**选项 A (推荐)**: "First failure aborts playback". `ReplayFrameSinkAdapter.SendFrameAsync` 立即 throw `ReplaySendException` on first `Result<Unit>.IsFail`. `ReplayTimeline.OnTick` 当前 `try { _emit(frame); } catch { }` 吞 exception — 改为 `catch (Exception ex) { _sinkException = ex; _sinkExceptionFrameIndex = index; throw; }` 然后 timeline throw 出来 at frame N, ReplayService 顶层 catch + 标记 `_lastSinkException` + 调 `_onPlaybackEnded` (this also sets state Stopped). Replay UI sees `PlaybackEnded` event with `Error != null` and surfaces "Replay aborted at frame N: <reason>".

**选项 B**: "Continue all + report first-failure at end". Adapter 吞失败 (类似 current behavior), 存 `_lastSinkException` field. ReplayService 顶层在 `_onPlaybackEnded` 之前 check `_lastSinkException != null` and pass to event.

**决策**: A. 理由: OI 显式说 "surface first-failure as `ReplayException` (user-hostile silent drop on no-channel)" — silent drop 继续到 EOF = 10000 frames 后才知道错 vs frame 5 立即停 + 清楚错误信息. User explicit OI intent: stop at first failure. Memory v1.4.0 lesson "end-to-end producer/consumer tests catch gaps that unit tests miss" 适用 — UI 立即知道错位置 + 帧 ID, 比 5 分钟后才知道好太多. 风险: 10000-frame file 中 frame 1 失败, 用户 experience 是 "为什么 Replay 跑 1 帧就停了" — 但这恰好是 user-hostile silent drop 的反面: clear error + frame index + reason.

**Rejected alternative C**: "Continue + eventually throw at EOF" — essentially 选项 B with event raise. Doesn't stop early. Bad UX for large files.

### Decision 6: Item 3 — `PlaybackEnded` event signature change to `EventHandler<PlaybackEndedEventArgs>`

**选项 A** (推荐): overwrite `IReplayService.PlaybackEnded` to `event EventHandler<PlaybackEndedEventArgs>?` (new `PlaybackEndedEventArgs : EventArgs { Exception? Error { get; } }`). Breaking event signature change. UI consumers (ReplayViewModel) update handler signature `(s, e) => OnPlaybackEnded(e)`.

**选项 B**: add NEW event `PlaybackEndedWithError` alongside existing `PlaybackEnded`. Two events fire.

**选项 C**: keep `PlaybackEnded` empty + add `LastSinkException` getter. UI polls or subscribes to `PlaybackEnded` and reads `service.LastSinkException` after.

**决策**: A. 理由: 跟 v1.5.0 spec "PlaybackEnded exactly-once invariant" 一致 (单一 event). 跟 v1.5.0 spec "承载" 模式一致 (event args 携带 payload). Breaking 是 additive — 旧 `EventHandler` consumer 在 v1.5.0 时期是 null (UI 还没订阅; UI 在 v1.5.0 task 才加 subscription). Verify: `grep -rn "PlaybackEnded" src/ tests/` 应只 2 处: `IReplayService.cs:54` declaration + `ReplayService.cs:43-49` raise + `ReplayViewModel.cs` (v1.5.0 task 5 新加) + `ReplayServiceTests` (v1.5.0 +3 test). 总共 4-5 处, 全部在 feature/v1-4-2-patch branch 内可控.

### Decision 7: Item 3 — `ReplaySendException` 构造 shape

**选项 A** (推荐): `public sealed class ReplaySendException : ReplayException { public ReplaySendException(string message) : base(message) { } public ReplaySendException(string message, Exception inner) : base(message, inner) { } }`. Mirror `ReplayLoadException` (line 11-15) shape.

**决策**: A. 理由: 跟 `ReplayLoadException` 100% 对称 (same 2 ctors). Inner exception 保留 optional (production `Result<Unit>.Fail` 的 `Error` 是 in-memory `Error` 记录, not exception, 所以可能不需要 inner).

### Decision 8: Item 3 — ReplayService 暴露 `LastSinkException` 还是 only via event

**选项 A** (推荐): 只通过 `PlaybackEnded` event 携带. 不暴露 `LastSinkException` field.

**选项 B**: 同时暴露 `LastSinkException` field for test/debug introspection.

**决策**: A. 理由: state 通过 events 暴露, 跟 v1.5.0 spec "PlaybackEnded exactly-once" 一致. 测试用 reflection 看 internal `_sinkException` field (memory v1.2.14 PATCH `TxWaitingForFcForTesting` precedent + v1.5.0 PATCH `SetLoop_PropagatesToInternalTimeline` precedent).

## 架构

### Item 1 — `UdsSecurity.SetSeed` mutate-in-place

```
Caller (e.g. UdsClient.SecurityAccessAsync line 312):
  Security.SetSeed(level, response[1..])
    ↓
  UdsSecurity.SetSeed (NEW):
    lock (_levels)
      if (_levels.TryGetValue(level, out var state))
        state.Seed = seed;
        state.IsAuthenticated = false;
        // AttemptCount and LockedUntilUtc preserved (D8 invariant)
      else
        _levels[level] = new SecurityLevelState { Seed = seed, IsAuthenticated = false };
    ↓
  // No public API change. All callers continue to work.
  // v1.3.0 D8 invariant now uniformly enforced: lockout survives
  //   SetSeed (new) and Reset (existing).
```

### Item 2 — Mid-handshake lockout flip test re-enable

```
Test (UdsClientConcurrentSecurityAccessTests.cs:250 → re-enabled):
  // Arrange
  var (iso, sent) = NewIsoWithCapture();
  var algo = new FakeKeyDerivationAlgorithm();
  using var client = new UdsClient(iso, algo);
  client.Security.LockoutConfig = new UdsSecurityLockoutConfig(
    MaxAttempts: 2,
    LockoutDuration: TimeSpan.FromSeconds(5));
  using var auto = AutoRespondSeedOkThenNrc35(client, sent);

  // Act
  var t1 = client.SecurityAccessAsync(0x01, CancellationToken.None);
  var t2 = client.SecurityAccessAsync(0x01, CancellationToken.None);

  // 5s deadline per memory v1.2.12 lesson 4
  var completed = await Task.WhenAny(
    Task.WhenAll(WrapResult(t1), WrapResult(t2)),
    Task.Delay(TimeSpan.FromSeconds(5)));
  Assert.True(completed is not null, "Race test timed out after 5s");

  // Assert — at least one threw UdsSecurityLockedException
  //   (or both succeeded if both SendKey legs happened before lockout boundary — accept either)
  var r1 = await t1;  // null if threw
  var r2 = await t2;  // null if threw
  var threwCount = (r1 is null ? 1 : 0) + (r2 is null ? 1 : 0);
  threwCount.Should().BeOneOf(new[] { 1, 2 },
    "at least one caller must observe lockout via UdsSecurityLockedException");

  // Assert — post-state: IsLocked(0x01) is true OR at least one caller's
  //   SendKey leg observed lockout (consistent with race)
  client.Security.IsLocked(0x01).Should().BeTrue(
    "after 2 SendKey failures, lockout boundary (MaxAttempts=2) must be reached");
```

### Item 3 — `ReplayFrameSinkAdapter` first-failure surfaces as `ReplaySendException`

```
IReplayService.PlaybackEnded (CHANGED):
  event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;

PlaybackEndedEventArgs (NEW in src/PeakCan.Host.Core/Replay/PlaybackEndedEventArgs.cs):
  public class PlaybackEndedEventArgs : EventArgs
  {
    public Exception? Error { get; }
    public PlaybackEndedEventArgs(Exception? error = null) { Error = error; }
  }

ReplayExceptions.cs (MODIFIED, add 1 type):
  public sealed class ReplaySendException : ReplayException
  {
    public ReplaySendException(string message) : base(message) { }
    public ReplaySendException(string message, Exception inner) : base(message, inner) { }
  }

ReplayService.cs (MODIFIED):
  // NEW private field
  private Exception? _sinkException;
  internal Exception? SinkExceptionForTesting => _sinkException;

  // NEW private state captured from timeline
  internal void OnSinkThrewFromTimeline(Exception ex) => _sinkException = ex;

  // MODIFIED PlaybackEnded event signature + RaisePlaybackEnded
  public event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded;
  private void RaisePlaybackEnded()
    => PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs(_sinkException));

ReplayTimeline.cs (MODIFIED):
  // NEW ctor param: Action<Exception>? onSinkThrew (carries exception to service)
  public ReplayTimeline(
    Action<ReplayFrame> emit,
    Action<PlaybackEndedEventArgs>? onPlaybackEnded = null,
    Action<Exception>? onSinkThrew = null)
  { _emit = emit; _onPlaybackEnded = onPlaybackEnded; _onSinkThrew = onSinkThrew; }

  // MODIFIED OnTick sink-call branch
  foreach (var frame in toEmit)
  {
    try { _emit(frame); }
    catch (Exception ex)
    {
      // v1.4.0 sink-throw tolerance: capture and forward to service, do not swallow silently
      _sinkException = ex;
      _onSinkThrew?.Invoke(ex);
      // Re-throw? Or swallow and let OnTick finish current batch?
      // Spec ADR: re-throw to stop the current tick's frame emission immediately
      throw;
    }
  }

  // EOF branch: pass Error to onPlaybackEnded
  if (endReached)
  {
    var args = new PlaybackEndedEventArgs(_sinkException);
    _onPlaybackEnded?.Invoke(args);
  }

ReplayFrameSinkAdapter.cs (MODIFIED):
  public async ValueTask SendFrameAsync(ReplayFrame frame, CancellationToken ct = default)
  {
    var canFrame = new CanFrame(...);
    var result = await _send.SendAsync(canFrame, ct).ConfigureAwait(false);
    if (!result.IsSuccess)
    {
      throw new ReplaySendException(
        $"Replay frame send failed at t={frame.Timestamp}s, id=0x{frame.Id:X}: {result.Error?.Message ?? "unknown"}");
    }
  }

ReplayViewModel.cs (MODIFIED — v1.5.0 task 5 + v1.4.2 fix):
  // OnPlaybackEnded handler signature changes from (s, e) to (s, e) where e is PlaybackEndedEventArgs
  // Add ErrorMessage set when e.Error != null
  void OnPlaybackEnded(object? sender, PlaybackEndedEventArgs e)
  {
    ((Action)(() =>
    {
      if (e.Error is not null)
      {
        ErrorMessage = $"Replay aborted: {e.Error.Message}";
      }
      // Reset Loop / speed etc. — keep existing logic
    })).RunOnUi();
  }
```

## 组件

### Core 修改

| File | Change |
|------|--------|
| `src/PeakCan.Host.Core/Uds/UdsSecurity.cs` line 28-38 | `SetSeed` mutate-in-place: if exists, mutate; else create fresh. Add XML doc referencing D8 invariant. |
| `src/PeakCan.Host.Core/Replay/PlaybackEndedEventArgs.cs` (NEW) | `public class PlaybackEndedEventArgs : EventArgs` with `Exception? Error` property. |
| `src/PeakCan.Host.Core/Replay/ReplayExceptions.cs` (MODIFIED) | Add `public sealed class ReplaySendException : ReplayException` (2 ctors matching `ReplayLoadException`). |
| `src/PeakCan.Host.Core/Replay/IReplayService.cs` line 54 | `event EventHandler? PlaybackEnded` → `event EventHandler<PlaybackEndedEventArgs>? PlaybackEnded`. |
| `src/PeakCan.Host.Core/Replay/ReplayService.cs` | Add `_sinkException` field + `SinkExceptionForTesting` getter + `OnSinkThrewFromTimeline` callback wiring in ctor + `RaisePlaybackEnded` passes `new PlaybackEndedEventArgs(_sinkException)`. |
| `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs` | Add `Action<Exception>? onSinkThrew` ctor param; capture in field; in `OnTick` foreach, catch + capture + rethrow; in EOF branch, build `PlaybackEndedEventArgs` with `_sinkException`. |

### App 修改

| File | Change |
|------|--------|
| `src/PeakCan.Host.App/Composition/ReplayFrameSinkAdapter.cs` line 56-70 | `SendFrameAsync` checks `result.IsSuccess` and throws `ReplaySendException` with frame timestamp + ID + `Error.Message`. |
| `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` | Update `OnPlaybackEnded` handler signature to `EventHandler<PlaybackEndedEventArgs>`. Set `ErrorMessage = $"Replay aborted: {e.Error.Message}"` when `e.Error != null`. |

### Core.Tests 修改

| File | Change |
|------|--------|
| `tests/PeakCan.Host.Core.Tests/Uds/UdsSecurityTests.cs` (line 7-105) | +2 tests: `SetSeed_PreservesLockoutState_OnExistingLevel` + `SetSeed_OnNewLevel_CreatesFreshState`. |
| `tests/PeakCan.Host.Core.Tests/Uds/UdsClientConcurrentSecurityAccessTests.cs` (line 250-255) | Re-enable: `Skip = null` + replace placeholder body with full concurrent-mid-handshake test reusing `AutoRespondSeedOkThenNrc35` helper. Update XML doc `<remarks>` with v1.4.2 fix context. |
| `tests/PeakCan.Host.Core.Tests/Replay/ReplayTimelineTests.cs` (NEW assertions or NEW file) | +2 tests: `OnTick_SinkThrows_AbortsPlaybackAndRaisesPlaybackEndedWithError` + `OnSinkThrew_CapturesException_ForServiceAccess`. |
| `tests/PeakCan.Host.Core.Tests/Replay/IReplayServiceTests.cs` (existing) | +1 test: `PlaybackEnded_RaisedWithError_OnFirstSinkFailure`. |

### App.Tests 修改

| File | Change |
|------|--------|
| `tests/PeakCan.Host.App.Tests/Composition/ReplayFrameSinkAdapterTests.cs` (NEW) | ~5 tests covering: `SendFrameAsync_FailResult_ThrowsReplaySendException` + `SendFrameAsync_OkResult_DoesNotThrow` + exception message contains frame timestamp + ID + reason + `SendService_SentToNSubstitute_PropagatedCorrectly` + null `SendService` ctor guard. |
| `tests/PeakCan.Host.App.Tests/ViewModels/ReplayViewModelTests.cs` (existing) | Update existing PlaybackEnded handler to new signature; +1 test `OnPlaybackEnded_WithError_SetsErrorMessage` (or equivalent). |

### DI 注册（`AppHostBuilder.cs`）

无变化。`ReplayFrameSinkAdapter` + `IReplayService` + `IReplayFrameSink` 全部已注册 singleton。`PlaybackEndedEventArgs` 是 plain class, 无需 DI。`ReplaySendException` 是 exception, 无 DI。

### Documentation 修改

| File | Change |
|------|--------|
| `docs/release-notes-v1.4.2.md` | new (Task 6 ships this) |
| `Directory.Build.props` line 3 | version bump 1.5.0 → 1.4.2 (last step of Task 6 — 故意不预先 bump 以保持 ship-prep 一致性) |

## 数据流

### Item 1 happy path (single-threaded, regression coverage)

1. Test: `new UdsSecurity { LockoutConfig = (MaxAttempts: 1, TimeSpan: 5s) }`
2. Test: `sut.RecordFailedAttempt(0x01)` → `IsLocked(0x01) == true`
3. Test: `sut.SetSeed(0x01, new byte[] { 0xAA, 0xBB })`
4. Assert: `sut.IsLocked(0x01) == true` (state preserved)
5. Assert: `sut.RemainingLockoutDelay(0x01) > TimeSpan.Zero`
6. (Sanity) Test: `sut.SetSeed(0x03, ...)` on fresh level → `IsLocked(0x03) == false` (else-branch creates fresh)

### Item 1 race scenario (mid-handshake flip) — covered by Item 2 re-enabled test

1. Two concurrent `SecurityAccessAsync(0x01)` with `MaxAttempts=2`
2. t1's SetSeed runs (fresh state — `AttemptCount=0`)
3. t1's SendKey fails (NRC 0x35) → `RecordFailedAttempt` → `AttemptCount=1`
4. t2's SetSeed runs (mutate existing state) — `AttemptCount=1` preserved, `IsAuthenticated=false`
5. t2's SendKey fails (NRC 0x35) → `RecordFailedAttempt` → `AttemptCount=2` → `IsLocked=true`, `AttemptCount=0`
6. t1's NEXT call (e.g. a hypothetical third caller) sees `IsLocked=true` → throws `UdsSecurityLockedException`
7. Post-state: `IsLocked(0x01) == true` (consistency assertion)

### Item 3 first-failure happy path (send succeeds, playback continues)

1. `ReplayService.LoadAsync("test.asc")` parses 10 frames
2. `_replayService.Play()`
3. `OnTick` emits frame 0 → `_adapter.SendFrameAsync` → `SendService.SendAsync` → `Result.Ok(Unit)` → no throw
4. `OnTick` emits frame 1 → ... → ... continues
5. End of frames → `OnTick` EOF branch → `IsLocked` not applicable, `_sinkException == null` → `RaisePlaybackEnded(new PlaybackEndedEventArgs(null))` → `PlaybackEnded` event fires → `ReplayViewModel.OnPlaybackEnded` sets `IsPlaying=false` (existing v1.5.0 behavior)

### Item 3 first-failure failure path (send fails at frame N)

1. Same as above
2. `OnTick` emits frame 4 → `_adapter.SendFrameAsync` → `SendService.SendAsync` → `Result.Fail(InvalidState, "no active channel")` → adapter throws `ReplaySendException("Replay frame send failed at t=0.005s, id=0x100: no active channel")`
3. `OnTick`'s `try { _emit(frame); } catch (Exception ex) { _sinkException = ex; _onSinkThrew?.Invoke(ex); throw; }` → catches, stores, re-throws
4. `OnTick` outer scope: rethrown exception propagates out of `OnTick` (Timer callback handler — `System.Threading.Timer` catches + logs + drops per BCL semantics, but the state is preserved: `_sinkException != null` + `_isPlaying=false` from existing pause-after-tick behavior... actually check this)
5. **Subtle**: re-throw from `OnTick` is caught by `System.Threading.Timer` infrastructure and dropped (BCL behavior). So the actual "stop playback" mechanism is the `_isPlaying = false` set in EOF branch OR explicit "stop on sink throw" check. **Refine Decision 5**: in addition to re-throw, set `_isPlaying = false` in the catch block so playback stops; raise `PlaybackEnded` from outside the lock with `Error`. The `throw` is removed (was a 1st design; revise to "store + set isPlaying=false + continue OnTick to EOF" to allow `PlaybackEnded` to fire normally).
6. Revised `OnTick` foreach:
   ```csharp
   foreach (var frame in toEmit)
   {
     try { _emit(frame); }
     catch (Exception ex)
     {
       if (_sinkException is null)  // capture first only
       {
         _sinkException = ex;
         _onSinkThrew?.Invoke(ex);
         _isPlaying = false;  // stop playback
       }
     }
   }
   ```
7. Next tick: `_isPlaying=false` → early return → no more frames emitted
8. UI: when does `PlaybackEnded` fire? Need a "service is idle" trigger. **Options**:
   - (a) timeline raises `PlaybackEnded` immediately on `_isPlaying=false` set (not at EOF). Service subscribes to `_onSinkThrew` and raises `PlaybackEnded` itself.
   - (b) timeline `OnTick` after foreach, check `if (!_isPlaying && _sinkException != null) { _onPlaybackEnded?.Invoke(new PlaybackEndedEventArgs(_sinkException)); }` — but this fires per tick, may fire multiple times. Not OK.
   - (c) move `PlaybackEnded` raise to service layer: service subscribes to `_onSinkThrew` and raises `PlaybackEnded` from there. Timeline only raises `PlaybackEnded` at EOF (existing behavior).
   - **Decision 9 (revised)**: option (c). Service gets `_onSinkThrew` callback. When fired, service sets `_sinkException` and raises `PlaybackEnded` immediately (with `Error`). Timeline's existing `PlaybackEnded` at EOF unchanged (no error path there since `_isPlaying=false` ensures no sink throw at EOF).
9. **Final design**:
   - ReplayService wires `onSinkThrew` to private method that:
     ```csharp
     private void OnSinkThrew(Exception ex)
     {
       _sinkException = ex;
       _timeline.Pause();  // set isPlaying=false
       RaisePlaybackEnded();  // fires event with Error
     }
     ```
   - ReplayService's `RaisePlaybackEnded` uses `_sinkException`:
     ```csharp
     private void RaisePlaybackEnded()
       => PlaybackEnded?.Invoke(this, new PlaybackEndedEventArgs(_sinkException));
     ```
   - Timeline's `OnTick` foreach catch does NOT re-throw; just stores in `_sinkException` + invokes callback. Timeline's `OnTick` EOF branch raises `PlaybackEnded` as before (with no error since `_isPlaying` already false at this point).
10. **Test invariants**:
    - `OnTick_SinkThrows_AbortsPlaybackAndRaisesPlaybackEndedWithError`: subscribe to `PlaybackEnded`, trigger sink throw, assert `PlaybackEnded` fired with `args.Error != null && args.Error is ReplaySendException`.
    - `PlaybackEnded_RaisedWithError_OnFirstSinkFailure`: end-to-end test via `ReplayService` with mock `IReplayFrameSink` that throws on second call; assert event raised with `Error` of correct type + message.
    - `SendFrameAsync_FailResult_ThrowsReplaySendException`: unit test for adapter; `SendService` NSubstitute returns `Result<Unit>.Fail(ErrorCode.InvalidState, "no channel")`; assert `ReplaySendException` thrown with message containing "no channel".

## 错误处理

| Error | Source | Handling |
|-------|--------|----------|
| Item 1: `SetSeed` mutate on disposed `UdsSecurity` | None — `UdsSecurity` is non-disposable singleton | No error path |
| Item 2: race test deadlock | Concurrent SecurityAccessAsync | 5s `Task.WhenAny` deadline (per memory v1.2.12 lesson 4 + v1.4.1 PATCH) |
| Item 2: race test flake | Timing-sensitive interleaving | Re-run 3x in CI; transient-flaky acceptable (memory v1.2.12 lesson 4) |
| Item 3: `SendService.SendAsync` returns `Result<Unit>.Fail` | No active channel / PEAK error | Adapter throws `ReplaySendException`; timeline captures + service raises `PlaybackEnded` with `Error`; UI sets `ErrorMessage` |
| Item 3: `SendService.SendAsync` throws (not returns Fail) | Catastrophic (e.g. disposed SendService) | Adapter lets exception propagate (current fire-and-forget path is wrong; new design treats any throw as first-failure) |
| Item 3: timeline `OnTick` exception from sink callback | `OnSinkThrew` callback throws (e.g. service disposed) | Timeline `try/catch` swallows; `_sinkException` already captured so event fires with whatever error was set |
| Item 3: UI dispatcher shutdown | `ReplayViewModel` disposed during playback | `RunOnUi` is null-safe in `DispatcherExtensions` (existing pattern); `ErrorMessage` setter is null-safe |

**No silent failures**. All sink failures are surfaced via `PlaybackEnded` event + `ErrorMessage` in UI.

## 测试策略

### Item 1 tests (~2 tests in Core.Tests, `UdsSecurityTests.cs`)

| Test | Scenario |
|------|----------|
| `SetSeed_PreservesLockoutState_OnExistingLevel` | `RecordFailedAttempt` x1 (MaxAttempts=1) → `IsLocked` true → `SetSeed` → `IsLocked` still true + `RemainingLockoutDelay > 0` |
| `SetSeed_OnNewLevel_CreatesFreshState` | `SetSeed(0x01, ...)` on fresh level → `GetSeed(0x01)` returns the seed; `IsLocked(0x01)` false (sanity) |

### Item 2 tests (1 test re-enable in Core.Tests, `UdsClientConcurrentSecurityAccessTests.cs`)

| Test | Scenario |
|------|----------|
| `TwoArg_Overload_ConcurrentMidHandshakeLockoutFlip_PostStateConsistent` (re-enable) | 2 concurrent `SecurityAccessAsync(0x01)` + `MaxAttempts=2` + seed-OK + SendKey-NRC35; assert 1 or 2 throw `UdsSecurityLockedException` + post-state `IsLocked(0x01)==true` |

### Item 3 tests (~7 tests total: 2 Core + 1 IReplayService + ~4 App adapter)

| Test | Location | Scenario |
|------|----------|----------|
| `OnTick_SinkThrows_AbortsPlaybackAndRaisesPlaybackEndedWithError` | `ReplayTimelineTests.cs` (NEW or extend) | Wire timeline with throwing sink; call `OnTick` directly; assert `PlaybackEnded` callback fires with `args.Error != null` |
| `OnTick_SinkThrows_TimelineStopsEmittingFurtherFrames` | `ReplayTimelineTests.cs` | After sink throw, call `OnTick` again; assert no more frames emitted |
| `PlaybackEnded_RaisedWithError_OnFirstSinkFailure` | `IReplayServiceTests.cs` | End-to-end via `ReplayService` + mock `IReplayFrameSink` that throws on second frame; assert event with `Error` |
| `SendFrameAsync_FailResult_ThrowsReplaySendException` | `ReplayFrameSinkAdapterTests.cs` (NEW) | NSubstitute `SendService` returns `Result.Fail`; assert `ReplaySendException` thrown |
| `SendFrameAsync_OkResult_DoesNotThrow` | `ReplayFrameSinkAdapterTests.cs` | NSubstitute `SendService` returns `Result.Ok`; assert no throw |
| `SendFrameAsync_ExceptionMessage_ContainsFrameTimestampAndId` | `ReplayFrameSinkAdapterTests.cs` | Assert message includes `t=` + `id=0x100` style for traceability |
| `OnPlaybackEnded_WithError_SetsErrorMessage` | `ReplayViewModelTests.cs` (extend) | Trigger `PlaybackEnded` with `Error`; assert `ErrorMessage` updated |

### Test count expectations

| Suite | v1.5.0 baseline | **v1.4.2 final** | Δ |
|-------|------------------|------------------|-------|
| Core.Tests | 315 + 1 SKIP | **~320 + 0 SKIP** | +5 (UdsSecurity 2 + ReplayTimeline 2 + IReplayService 1; SKIP re-enable -1 → 0 SKIP) |
| Infra.Tests | 84 + 2 SKIP | **84 + 2 SKIP** | unchanged |
| App.Tests | 365 + 4 SKIP | **~369 + 4 SKIP** | +4 (ReplayFrameSinkAdapter 3 + ReplayViewModel 1) |
| **Total** | 764 + 7 SKIP + 0 fail | **~773 + 6 SKIP + 0 fail** | **+9 net** (and **-1 SKIP** = net +10 from baseline) |

### TDD discipline guard

- **Item 1**: `SetSeed_PreservesLockoutState_OnExistingLevel` is **pure RED-then-GREEN**: must FAIL on unfixed code (counter wiped) and PASS on fixed code. Single-threaded test, deterministic, zero flake.
- **Item 2**: race test re-enable — **RED** against unfixed code (lockout never reached due to counter wipe), **GREEN** against fixed code. Transient-flaky acceptable.
- **Item 3**: `SendFrameAsync_FailResult_ThrowsReplaySendException` is **pure RED-then-GREEN**: must FAIL on unfixed code (discarded Result, no throw) and PASS on fixed code. Deterministic.
- **Item 3**: `OnTick_SinkThrows_*` tests must use REAL `ReplayTimeline` + real `Action<Exception>` callback wiring (not NSubstitute on the timeline). The `EmitFrame` callback is a plain `Action<ReplayFrame>` — supply one that throws.
- **Item 3**: `IReplayServiceTests.PlaybackEnded_RaisedWithError_OnFirstSinkFailure` must construct REAL `ReplayService` with REAL `ReplayTimeline` + mock `IReplayFrameSink` that throws (per v1.5.0 reflection precedent `SetLoop_PropagatesToInternalTimeline`).

## 关键文件

### 新增
- `src/PeakCan.Host.Core/Replay/PlaybackEndedEventArgs.cs`
- `tests/PeakCan.Host.App.Tests/Composition/ReplayFrameSinkAdapterTests.cs`

### 修改
- `src/PeakCan.Host.Core/Uds/UdsSecurity.cs` (SetSeed line 28-38 mutate-in-place)
- `src/PeakCan.Host.Core/Replay/ReplayExceptions.cs` (+1 type: ReplaySendException)
- `src/PeakCan.Host.Core/Replay/IReplayService.cs` (PlaybackEnded event signature)
- `src/PeakCan.Host.Core/Replay/ReplayService.cs` (_sinkException + SinkExceptionForTesting + RaisePlaybackEnded)
- `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs` (onSinkThrew ctor param + foreach catch)
- `src/PeakCan.Host.App/Composition/ReplayFrameSinkAdapter.cs` (SendFrameAsync throws on Fail)
- `src/PeakCan.Host.App/ViewModels/ReplayViewModel.cs` (OnPlaybackEnded handler signature + ErrorMessage set)
- `tests/PeakCan.Host.Core.Tests/Uds/UdsSecurityTests.cs` (+2 tests)
- `tests/PeakCan.Host.Core.Tests/Uds/UdsClientConcurrentSecurityAccessTests.cs` (re-enable Test 2)
- `tests/PeakCan.Host.Core.Tests/Replay/ReplayTimelineTests.cs` (+2 tests)
- `tests/PeakCan.Host.Core.Tests/Replay/IReplayServiceTests.cs` (+1 test)
- `tests/PeakCan.Host.App.Tests/ViewModels/ReplayViewModelTests.cs` (update handler sig + +1 test)
- `Directory.Build.props` (version 1.5.0 → 1.4.2, last step of Task 6)
- `docs/release-notes-v1.4.2.md` (new, Task 6)

## 风险与缓解

| Risk | Probability | Impact | Mitigation |
|------|-------------|--------|------------|
| **Item 1**: `SetSeed` mutate-in-place 行为变化破坏已有 caller | LOW | MEDIUM | 旧行为 (counter wipe) 是 BUG (OI). 修复后行为与 `Reset()` 对齐. 已有 `UdsClient.cs:312` 是唯一 caller (per grep), 测试覆盖其他 caller. |
| **Item 2**: race test re-enable 持续 flake | MEDIUM | LOW | 5s deadline; 3x CI re-run acceptable (memory v1.2.12 lesson 4) |
| **Item 2**: race test 在 fix 前 RED 在 fix 后 GREEN, 但 race timing 不稳定导致 GREEN 时 flaky | MEDIUM | LOW | Assertion 是 "1 or 2 callers threw" (loose) + "post-state IsLocked==true" (deterministic, not timing-dependent) |
| **Item 3**: `PlaybackEnded` event signature change break consumer | LOW | LOW | v1.5.0 release notes §"PlaybackEnded exactly-once invariant" 已 document; 全部 consumer 在 v1.4.2 branch 内 (UI 在 v1.5.0 加 subscription) |
| **Item 3**: `ReplayService.PlaybackEnded` 在 timeline EOF vs sink-throw 两条路径都 fire, 双重 fire | MEDIUM | MEDIUM | Refined architecture: timeline EOF 路径 raise `PlaybackEnded` with null error; sink-throw 路径由 service 调 `_timeline.Pause()` + 立即 `RaisePlaybackEnded` with error. Service 保证单一 raise per playback session (state machine: `_sinkException` set → `RaisePlaybackEnded` → flag `_playbackEndedFired` → 后续 raise 跳过). |
| **Item 3**: `_sinkException` 在多次 sink throw 时被 overwrite | MEDIUM | LOW | Spec Decision 5 revised: 只 capture FIRST exception. ReplayService 内 `_sinkException` set-only-once. |
| **Item 3**: 10000-frame file 中 frame 1 失败, 用户 experience "跑 1 帧就停" 但可能不立即看到 ErrorMessage | LOW | LOW | `ErrorMessage` setter 是 immediate (synchronous); `RunOnUi` 是 best-effort (immediate if dispatcher available). User visible within 1 frame. |
| **Item 3**: UI `RunOnUi` 在 test 上下文不 fire (no dispatcher) | MEDIUM | LOW | `DispatcherExtensions.RunOnUi` precedent: if no dispatcher, execute synchronously (per existing impl). Test must use NSubstitute mocks, not real WPF controls (memory v1.2.11 STA-WPF lesson). |
| **Item 3**: 旧 `EventHandler PlaybackEnded` consumer 在 v1.4.2 期间添加 | LOW | LOW | grep verify 0 consumer outside v1.4.2 branch; spec §Phase 2.5 显式列 |
| **Network proxy during ship** | LOW | MEDIUM | Per memory `git-push-network-workaround`: `git -c http.proxy="" -c https.proxy="" push` + `gh api` for tag/release fallback |
| **Phase 2.5 missed API surface assumption** | LOW | LOW | v1.5.0 ship verified 11-of-11+ brief drift structural. PATCH items 3/3 Phase 2.5 verified. |
| **DI registration drift** | LOW | LOW | Spec §DI explicit: no new DI. 旧 `ReplayFrameSinkAdapter` + `IReplayService` + `IReplayFrameSink` already singleton. |

## Ship process

1. `git checkout main && git pull` (verify clean main @ v1.5.0 `9f0bb9e`)
2. `git checkout -b feature/v1-4-2-patch` from main @ `9f0bb9e`
3. Per-Item TDD (3 implementation tasks) — each with code-reviewer subagent
4. Pre-ship code-review (whole-branch `git diff 9f0bb9e..HEAD`)
5. Bump `Directory.Build.props` 1.5.0 → 1.4.2
6. Write `docs/release-notes-v1.4.2.md`
7. Push → PR → squash → tag v1.4.2 → release
8. Update memory `peakcan-host-v1-4-2-shipped.md` + `MEMORY.md` index

## 后续 (deferred to later releases)

- **v1.5.1 PATCH** (carry-over from v1.5.0 release notes §"Known follow-ups"):
  - Replay time-range filter (post-EOF scrubbing to arbitrary timestamp, not just Loop restart at 0).
  - Periodic DBC send (auto-send DBC message at fixed interval; requires `CyclicSendService` integration — memory v1.2.12 lesson 4: known transient flakes).
- **v1.6.0 MINOR** (carry-over from v1.3.0 / v1.4.0 / v1.5.0 spec §Non-Goals):
  - HIGH: V8 sandbox hardening (replace defense-in-depth with hard root restriction + path whitelist).
  - MEDIUM: CanApi rate limit (require benchmark + spec review before implementation).
  - MEDIUM: DBC size/token limit (prevent DoS via 100MB DBC files).
  - MEDIUM: Path normalization root restriction (replace v1.5.0 defense-in-depth with `Path.GetFullPath` rooted-at-`baseDirectory`).
  - MEDIUM: OEM `IKeyDerivationAlgorithm` concrete (requires OEM cooperation for algorithm spec).
- **Future MINOR**: Replay→Trace auto-load + Value table encoding + Multiplexed signal groups UI.
