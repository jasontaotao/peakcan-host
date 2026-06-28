# v1.2.14 PATCH Release Notes

**Ship date:** 2026-06-28
**Branch:** `feature/v1-2-14-patch` (cut from main @ v1.2.13 `742b73b`)
**Base:** main @ v1.2.13 `742b73b`
**Squash SHA:** (filled at ship)

## 概述

v1.2.14 是一个 4 项 verified fixes 的 PATCH（起源于 v1.2.13 §Known issues 列的 8 项 carry-over，经 Phase 2.5 actual-code exploration 后 4 项 drop 为 no-op，mid-execution 又 drop 1 项）。**1 项 HIGH** + **1 项 MEDIUM** + **2 项 LOW**。无新 UI、无新功能、无 public API 移除；全部为 internal quality / 文档改进。

## 起源

v1.2.13 PATCH ship 后, release notes §"Known issues (deferred to v1.2.14 PATCH)" 列出 8 项 carry-over（4 项 HIGH/MEDIUM + 4 项 LOW）。Phase 2.5 实地读代码后：

| 原 carry-over | Phase 2.5 发现 | 行动 |
|---------------|---------------|------|
| #1 SendFailureCount test assert | Field 仍 producer-only, 但 v1.2.13 spec 已 plan 加 assert, ship 时漏交付 | **修**：加 1 行 assert 到既有 test |
| #2 `_txWaitingForFc` finally leak | Leak 真实 + 根因更深：v1.2.13 Item 5 throw 改动把 leak 从 latent 变 acute | **修**（HIGH）：finally 加 `lock (_txLock) { _txWaitingForFc = false; }` |
| #4 TesterPresentAsync virtual seam | Release notes 错位置（指 `UdsSession`）；实际在 `UdsClient.cs:300` | **修**：加 `virtual` 关键字；`SendRequestAsync` 也 lift to `virtual` |
| #5 HandleFirstFrame FF→FF 进度 | Release notes 术语错（"Phase 2 进度" 实指 test-coverage progression，不是 runtime progress）；实际已有 test（`HandleConsecutiveFrame_During_Watchdog_Disposal_*`）覆盖 FF→FF race | **DROP**（mid-execution 验证） |
| #6 SendFrameLibrary.Count O(1) | v1.2.13 Item 7 已 ship；`_cachedCount` 字段 + 所有 mutator 在 `_gate` 下同步 | **DROP**（spec phase） |
| #7 CyclicSendService async void throttling | `OnTimerTick` 内部 try/catch（line 161-168）已处理 exceptions；TimerCallback ↔ async void 边界无实际缺口 | **DROP**（spec phase） |
| #3 src/ + .slnx CRLF normalize | Phase 2.5 grep 确认 4 src/ + 1 .slnx | **修**：5 文件 CRLF → LF |
| #8 ChannelRouter secondary-catch doc | Release notes 措辞松（"OnError" 实指 `IFrameSink.OnError` 委托）；实际在 `OnChannelFrame` line 138-173 | **修**（doc-only）：class `<summary>` insert 一段 `<para>` |

## Per-Item 修复详情

### Item 1 — `IsoTpLayer.SendMultiFrameAsync` finally-leak of `_txWaitingForFc` (HIGH)

**Symptom**: `IsoTpLayer.cs:432-435` finally 仅释放 `_sendGate`, 不清 `_txWaitingForFc`. v1.2.13 PATCH Item 5 (throw `IsoTpSendFailedException` 在 `SendCanFrameAsync` line 344) 把 leak 从 latent 变 acute. 修复: finally 加 `lock (_txLock) { _txWaitingForFc = false; }` 在 `_sendGate.Release()` 之前. 同时新增 `internal bool TxWaitingForFcForTesting` getter 验证 invariant.

**Tests**: +1 `SendMultiFrameAsync_After_SendFailure_Resets_TxWaitingForFc` (RED → GREEN).

### Item 2 — `SendFailureCount` test assertion (LOW)

**Symptom**: v1.2.13 PATCH Item 5 spec 已 plan 加 `SendFailureCount == 1` assert 到 `SendMultiFrameAsync_Send_Exception_Propagates_As_IsoTpSendFailedException`, 但 ship commit 漏. Field (`IsoTpLayer.cs:43`) 是 internal, `InternalsVisibleTo("PeakCan.Host.Core.Tests")` 已配.

**Tests**: modify existing test, +1 line.

### Item 4 — `UdsClient.TesterPresentAsync` + `SendRequestAsync` virtual seam (MEDIUM)

**Symptom**: 测试 double 需 override wire-level frame emit. 7 个 sibling UDS 方法 (`DiagnosticSessionControlAsync` 等) 已 `virtual`. `TesterPresentAsync` (`UdsClient.cs:300`) 与 `SendRequestAsync` (`line 116`) 之前都是 non-virtual. S3 keepalive 测试 (`UdsSession.cs:106`) 调 `client.TesterPresentAsync()`.

**Fix**: 加 `virtual` 关键字到两者. `SendRequestAsync` 保持 `public` 兼容既有 6 处 direct callers (in `UdsClientTests.cs`).

**Tests**: +1 `TesterPresentAsync_Dispatches_To_SendRequestAsync_Virtual` + nested `SpyUdsClient` class.

### Item 3 — 4 src/ + 1 .slnx CRLF → LF normalize (LOW)

**Symptom**: `git grep -lI $'\r' src/` 返回 4 文件 (`AssemblyInfo.cs` + 3 `.csproj`). `file PeakCan.Host.slnx` 输出 "ASCII text, with CRLF line terminators". v1.2.13 PATCH Item 10 spec line 408 错写 "production code 已 LF" — spec defect. `dotnet format --verify-no-changes` 报 10 ENDOFLINE 错 (`AssemblyInfo.cs`).

**Fix**: 5 文件 CRLF → LF via `sed -i 's/\r$//'`. `git diff -w = 0` (pure canonical swap, 113 inserts = 113 dels). `.editorconfig` `end_of_line = lf` 全 repo 一致.

**Tests**: no change.

### Item 6 — `ChannelRouter` secondary-catch ordering doc (LOW)

**Symptom**: Release notes 措辞松（"OnError"），ChannelRouter 自己无 `OnError` 方法. 实际 secondary-catch ordering 在 `OnChannelFrame` line 138-173 围绕 `IFrameSink.OnError` 委托. 两条 ordering invariant 未文档化:
1. Root cause exception (EventId 6010) 在 `s.OnError(ex)` **之前** log — 即使 OnError throw 根因仍存活
2. `DetachSink(s)` 在 inner catch **外**运行 — detach failure 不会 re-mask secondary exception

**Fix**: `ChannelRouter.cs:8-40` class `<summary>` insert 一段 `<para>` 文档化两条 invariant + reorder/move 警告.

**Tests**: no change (doc-only).

## Spec drops (post Phase 2.5 + mid-execution)

| # | 原 carry-over | Drop 原因 |
|---|---------------|----------|
| 5 | HandleFirstFrame FF→FF companion test | `HandleConsecutiveFrame_During_Watchdog_Disposal_Does_Not_Corrupt_Buffer`（v1.2.13 已 ship）实际就是 FF→FF race test — body 是 FF1 → FF2 → CF. "ConsecutiveFrame" 在名字里指 completing CF, 不是 race 场景. `HandleFirstFrame_After_FirstFrame_Timer_Fired_*` 已覆盖 timeout-driven variant. 无缺口. |
| 6 | SendFrameLibrary.Count O(N) → O(1) | v1.2.13 PATCH Item 7 已 ship. `_cachedCount` 字段 (`SendFrameLibrary.cs:75`), 所有 mutator (`Save`/`Add`/`Remove`/`EnsureLoaded`) 在 `_gate` 下同步. `Count` getter 直接读 cache, 已 O(1). |
| 7 | CyclicSendService async void throttling | `OnTimerTick` (`CyclicSendService.cs:127`) 内部 try/catch (line 161-168) 已处理 exceptions. TimerCallback ↔ async void 边界无实际缺口. |

## 测试

| Metric | v1.2.13 baseline | v1.2.14 final |
|--------|------------------|----------------|
| 总 tests | 664 + 6 SKIP + 0 fail | **667 + 6 SKIP + 0 fail** (+3 net) |
| Core.Tests | 238 | 240 (+2: SendFailureCount assert, virtual dispatch) |
| Infra.Tests | 84 | 84 (no change) |
| App.Tests | 342 | 343 (+1: HandleFirstFrame FF→FF companion — DROPPED) |

净 +3 tests: Item 1 (+1) + Item 4 (+1) + Item 2 (modify existing, no count delta).

## Pre-ship code-review

待 ship 前跑 `code-reviewer` subagent on 分支 diff.

## Known issues (deferred to v1.2.15 PATCH or later)

无 HIGH/MEDIUM 项 pending. v1.2.15 PATCH 暂不 plan. v1.3.0 MINOR 候选:
- OEM `IKeyDerivationAlgorithm` concrete implementations
- Replay (ASC parser + time-based replay + speed control)
- Send DBC signal encoding
- UDS SecurityAccess attempt counter + lockout
- V8 sandbox hardening + CanApi rate limit
- UDS EcuReset / RoutineControl confirm
- DBC size / token limit + path normalization
- Channel picker (UI channel 选择器)

## Breaking changes

无. 所有 PATCH 范围内变更, 不改公开 API 签名. `Directory.Build.props` version bump 1.2.13 → 1.2.14 (Version / AssemblyVersion / FileVersion / InformationalVersion 同步). `TesterPresentAsync` 和 `SendRequestAsync` 加 `virtual` 是 non-breaking（virtual 是 public method 的 superset capability）.