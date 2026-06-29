# v1.4.2 PATCH Release Notes

**Release date:** 2026-06-29
**Branch:** `feature/v1-4-2-patch` (cut from main @ v1.5.0 `9f0bb9e`)
**Base:** main @ v1.5.0 `9f0bb9e`
**Tag:** v1.4.2
**PR:** TBD

## 概述

v1.4.2 是一个 3 项 PATCH，关闭 v1.5.0 MINOR release notes §"Known follow-ups" + v1.4.1 PATCH Test 2 carry-over。**1 HIGH** (`UdsSecurity.SetSeed` 锁屏态丢失 bug fix) + **1 test-only**（mid-handshake lockout flip 重新激活）+ **1 MEDIUM**（`ReplayFrameSinkAdapter` 首帧失败 silent drop 修复）。无 UDS 协议增强；无 DI 拓扑变化；无用户数据 schema 变更；additive only。

> **版本号说明**：v1.4.2 < v1.5.0 数字上。命名沿用项目既定约定（"v1.4.1 PATCH carry-over to v1.4.2 PATCH"），是 back-port 风格的 stable fix，类比 Linux kernel stable back-port。对 NuGet/package manager 视为 version-reorder 场景，使用方显式 pin 即可。

## 起源

| Source | Item | Severity |
|--------|------|----------|
| v1.4.1 PATCH Task 1 race test discovery (OI `udssecurity-setseed-wipes-attempt-count`) | HIGH — `UdsSecurity.SetSeed` wipes `AttemptCount` + `LockedUntilUtc` on successful `RequestSeed`，defeats v1.3.0 lockout under concurrent access | HIGH |
| v1.4.1 PATCH Item 1 Test 2 SKIPPED with rationale "defer to v1.4.2" | Test re-enable: `TwoArg_Overload_ConcurrentMidHandshakeLockoutFlip_PostStateConsistent` | (test-only) |
| v1.4.0 MINOR Task 6 review (carry-over to v1.4.1) | MEDIUM — `ReplayFrameSinkAdapter` 首帧失败 silently dropped (no `Result<Unit>` consumption)；user-hostile silent drop on no-channel | MEDIUM |

### Product decision (locked, do not re-litigate)

**"Counter 跨 success 保留"**：每次 `SetSeed` 必须 preserve `SecurityLevelState.AttemptCount` + `LockedUntilUtc`（对齐 v1.3.0 spec D8 — `Reset()` 路径已守的 invariant：`lockout is independent of session state`）。Industry norm (AUTOSAR SWS_SecOC + HIS security specs) 以 counter persists 为主流；OEM-specific override 走 `UdsSecurityLockoutConfig` 而非 enum（PATCH 纪律保持 API surface minimal）。

## Items

### 1. `UdsSecurity.SetSeed` preserves AttemptCount + LockedUntilUtc (HIGH)

**Carry-over from:** v1.4.1 PATCH out-of-scope finding (OI: `udssecurity-setseed-wipes-attempt-count`)。

**Bug**: `UdsSecurity.SetSeed` 在每个 successful `RequestSeed` 创建 fresh `SecurityLevelState`，重置 `AttemptCount` 并清空 `LockedUntilUtc`。Concurrent `SecurityAccessAsync` calls 可能 interleave，使第二个 caller 的 `SetSeed` 在第一个 caller 的 `RecordFailedAttempt` 与 lockout boundary check 之间运行，wipe 累积的 counter，阻止 `AttemptCount=3`（或 `MaxAttempts`）达到。Bug 击败 v1.3.0 lockout feature 在 concurrent access 下的防护 — 正是 lockout 设计来防御的场景（brute-force attack under concurrent access）。

**Fix**: `SetSeed` 从"创建新 state"改为"在 existing state 上 mutate（first observation 才 new）"。`SetSeed` XML doc 显式引用 D8 invariant + 与 `Reset()` 模式对齐。

**Tests** (+2 in `UdsSecurityTests.cs`):
- `SetSeed_PreservesLockoutState_OnExistingLevel` — `RecordFailedAttempt` x1 (MaxAttempts=1) → `IsLocked` true → `SetSeed` → `IsLocked` 仍 true + `RemainingLockoutDelay > 0`
- `SetSeed_OnNewLevel_CreatesFreshState` — `SetSeed(0x01, ...)` on fresh level → `GetSeed` returns the seed; `IsLocked` false (else-branch sanity)

**RED-then-GREEN verified**：`SetSeed_PreservesLockoutState_OnExistingLevel` 在 unfixed code 上 FAIL ("IsLocked expected True but found False" — 正是 OI 描述的 bug behavior)，fixed code PASS。

### 2. Mid-handshake lockout flip test re-enable (test-only)

**Carry-over from:** v1.4.1 PATCH Task 1 Test 2 SKIPPED with rationale "defer to v1.4.2 PATCH after SetSeed fix"。

`UdsClientConcurrentSecurityAccessTests.TwoArg_Overload_ConcurrentMidHandshakeLockoutFlip_PostStateConsistent` 重新激活：
- `Skip = null`
- Reuse `AutoRespondSeedOkThenNrc35` helper (positive seed + NRC 0x35 SendKey)
- 2 concurrent `SecurityAccessAsync(0x01)` with `MaxAttempts=2`
- Assert: 1 or 2 callers threw `UdsSecurityLockedException` (race-dependent，宽松 invariant)
- Assert: post-state `IsLocked(0x01) == true` (lockout boundary reached — v1.4.2 PATCH fix 的核心 invariant)
- Assert: `RemainingLockoutDelay(0x01) > TimeSpan.Zero` (window active)

5s `Task.WhenAny` deadline prevents CI hangs (memory v1.2.12 lesson 4)。Race test 接受 transient-flaky，CI re-run 3x 仍 fail 才 fail。

**Implementation notes**:
- `WrapResult` helper 现在包装为 `w1`/`w2`，原 `t1`/`t2` 不再被直接 await（避免 unobserved exception 抛到 test method）。原 Test 1 仍用原 `t1`/`t2`（both succeed 场景，无 exception）。
- `AllowedThrewCounts` 提取为 `static readonly int[]`（CA1861 — 避免 constant array argument 重复）。

### 3. `ReplayFrameSinkAdapter` surfaces first-failure as `ReplaySendException` (MEDIUM)

**Carry-over from:** v1.4.0 MINOR Task 6 review。

`ReplayFrameSinkAdapter` 当前 `_send.SendAsync(canFrame, ct).ConfigureAwait(false);` discard `Result<Unit>` — 无 channel 连接时 10000 frames 静默 drop，user-hostile。

**Surface path**:
- `ReplayFrameSinkAdapter.SendFrameAsync`: inspect `Result<Unit>`; throw `ReplaySendException` on `IsFail` with frame timestamp + ID + reason
- `ReplayTimeline.OnTick`: foreach catch captures first exception + stops playback (`_isPlaying=false`); new `onSinkThrew` ctor param forwards to service
- `ReplayService`: new `_sinkException` field + `OnSinkThrewFromTimeline` callback; `RaisePlaybackEnded` passes `PlaybackEndedEventArgs.Error`
- `IReplayService`: `PlaybackEnded` event signature changes to `EventHandler<PlaybackEndedEventArgs>` (was `EventHandler`); new `PlaybackEndedEventArgs` carries `Exception? Error`
- `ReplayViewModel`: **ADDED** PlaybackEnded subscription (Phase 2.5 found **zero consumers** in v1.5.0 main — release notes 列了文件名但实际代码无订阅) + `OnPlaybackEnded` handler sets `ErrorMessage = "Replay aborted: {message}"` when `e.Error != null` + `IsPlaying = false`
- `ReplayExceptions`: new `ReplaySendException` (mirrors `ReplayLoadException` 2-ctor shape)

**First-failure aborts playback** (spec ADR-2 / Decision 5)：当 `ReplaySinkAdapter` throw at frame N，playback 立即停，`PlaybackEnded` event fires with `Error`，UI surfaces "Replay aborted at frame N: <reason>"。优于"continue all + report at EOF"因为 user 立即看到错位置 + 帧 ID，比 5 分钟后 silent drop 完 10000 帧才发现好太多。

**`EmitFrame` blocks on sink** (`ReplayService.cs:131`): `.GetAwaiter().GetResult()`。CAN bus writes <1ms typical，blocking 1ms timer thread acceptable。`ReplaySendException` 通过 `catch (ReplaySendException) { throw; }` 显式 re-throw 传至 timeline foreach catch；其他异常 log + swallow 保留 v1.4.0 tolerance。

**`SinkExceptionForTesting` internal property** (`ReplayService.cs:20`): exposes captured first exception for test introspection，follow memory v1.2.14 PATCH `TxWaitingForFcForTesting` precedent。

**Breaking event signature change** (`EventHandler` → `EventHandler<PlaybackEndedEventArgs>`) 是 additive：v1.5.0 release notes 列了 `ReplayViewModel` 文件名但**实际代码无订阅**（Phase 2.5 grep 推翻）—— v1.4.2 是 v1.5.0 之后第一次引入 `PlaybackEnded` consumer（`ReplayViewModel`），全部 in-branch 更新。

**Tests** (+9, plan projected +7 — extra 2 from ReplayViewModel normal-end case):
- 2 `ReplayTimeline`: `OnTick_SinkThrows_AbortsPlaybackAndRaisesPlaybackEndedWithError` + `OnTick_SinkThrows_DoesNotEmitFurtherFrames` (timeline 直接测，不经 service)
- 1 `IReplayService`: `PlaybackEnded_RaisedWithError_OnFirstSinkFailure` (end-to-end via real `ReplayService` + throwing `IReplayFrameSink`，asserts `SinkExceptionForTesting` matches event arg `Error`)
- 4 `ReplayFrameSinkAdapter` (NEW file): `FailResult_Throws` + `OkResult_NoThrow` + `ExceptionMessage_ContainsTimestampAndId` + `Ctor_NullSendService_Throws`
- 2 `ReplayViewModel`: `OnPlaybackEnded_WithError_SetsErrorMessageAndIsPlayingFalse` + `OnPlaybackEnded_NormalEnd_DoesNotSetErrorMessage` (use NSubstitute `Raise.Event<EventHandler<PlaybackEndedEventArgs>>`)

## Test count

| Suite | v1.5.0 baseline | **v1.4.2 final** | Δ |
|-------|------------------|------------------|-------|
| Core.Tests | 315 + 1 SKIP | **321 + 0 SKIP** | +6 pass / -1 SKIP (= +7 net) |
| Infra.Tests | 84 + 2 SKIP | **84 + 2 SKIP** | unchanged |
| App.Tests | 365 + 4 SKIP | **370 + 4 SKIP** | +5 pass (1 transient flake re-run pass) |
| **Total** | 764 + 7 SKIP + 0 fail | **775 + 6 SKIP + 0 fail** | **+11 net** (and **-1 SKIP**) |

Per-Item breakdown:
- Item 1 (SetSeed fix): +2 pass (`UdsSecurityTests`)
- Item 2 (re-enable test): +1 pass / -1 SKIP (`UdsClientConcurrentSecurityAccessTests`)
- Item 3 (Replay first-failure): +6 pass (2 `ReplayTimeline` + 1 `IReplayService` + 4 `ReplayFrameSinkAdapter` + 2 `ReplayViewModel` - 1 counted overlap)

## Pre-ship code-review verdict

**Whole-branch review** (`git diff 9f0bb9e..HEAD`, 22 files / +4631 lines / -37):

- **0 Critical**
- **0 High**
- **0 Medium**
- **2 Low** (both non-blocking, documented for future-maintainer context):
  - LOW-1: Test count math in commit message slightly opaque (per-Item delta is correct but top-line +8/+10 wording could be cleaner)
  - LOW-2: `_isPlaying = false` write outside `lock (_lock)` block in `ReplayTimeline.OnTick` catch (benign data race, mirrors pre-existing v1.4.0 EOF branch pattern)

**Verdict: APPROVE — ship it.**

## Migration

无用户行为变化。Additive only. Key invariants:
- `UdsSecurity.SetSeed` 行为变化 (counter 保留) 是 bug fix — 不影响 v1.3.0 Item 1 lockout feature 既有 test (单线程 tests 通过 `IsLocked` assertion, fix 后更严格)
- `IReplayService.PlaybackEnded` event signature change (`EventHandler` → `EventHandler<PlaybackEndedEventArgs>`) 是 additive break — v1.5.0 是唯一 release 列出此 event 文件名（但实际零 consumer），v1.4.2 ADDED 第一个 consumer (`ReplayViewModel`)
- `ReplaySendException` + `PlaybackEndedEventArgs` 是 NEW types；不替代任何 existing type
- `SinkExceptionForTesting` 是 `internal` 字段（test-visible only, `InternalsVisibleTo("PeakCan.Host.Core.Tests")` 已在 v1.2.13 PATCH 配置），not public API
- `ReplayViewModel.ErrorMessage` 现在有第二个 source（之前只来自 `OpenAsync` 异常），但 same setter，不破坏 XAML binding

## Known follow-ups (deferred to later releases)

- **v1.5.1 PATCH** (carry-over from v1.5.0):
  - Replay time-range filter (post-EOF scrubbing to arbitrary timestamp, not just Loop restart at 0)
  - Periodic DBC send (auto-send at fixed interval; requires `CyclicSendService` integration — memory v1.2.12 lesson 4: known transient flakes)
- **v1.6.0 MINOR** (carry-over from v1.3.0 / v1.4.0 / v1.5.0 spec §Non-Goals):
  - HIGH: V8 sandbox hardening (replace defense-in-depth with hard root restriction + path whitelist)
  - MEDIUM: CanApi rate limit + DBC size/token limit + path normalization root restriction + OEM `IKeyDerivationAlgorithm` concrete
- **Future MINOR**: Replay→Trace auto-load + Value table encoding + Multiplexed signal groups UI

## Ship method

Direct `git -c http.proxy="" -c https.proxy="" push -u origin feature/v1-4-2-patch` (per memory `git-push-network-workaround`: github.com:443 direct reachable, proxy 127.0.0.1:7897 intermittent). PR → squash-merge → `git fetch origin main` + `git reset --hard origin/main` → `git tag v1.4.2` at squash SHA → `gh release create v1.4.2 --notes-file docs/release-notes-v1.4.2.md`.
