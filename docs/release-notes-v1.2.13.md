# Release Notes — PeakCan Host v1.2.13

**Date:** 2026-06-28

## Summary

v1.2.13 是一个 10 项 verified fixes 的 PATCH，源于 v1.2.12 PATCH 收尾时审出的 7 项 known follow-ups + 2 项 ship-time code-review 项；另含 1 项 pre-ship code-review 在最后阶段捕捉到的 latent race（见 §修复 #11）。**无新 UI、无新功能、无 public API 移除**；全部为 internal quality / safety 改进。

## 修复 (HIGH)

| # | ID | 组件 | 修复 |
|---|----|------|------|
| 1 | C-LO-1 | IsoTpLayer | `HandleConsecutiveFrameLocked` 的 `StartReceiveWatchdog` CTS race：嵌套 `WatchdogHandle` (sealed class, CTS + Generation + RefCount) + `CancelReceiveWatchdog` 将 `Dispose` 延迟到 ThreadPool；`Token.Register` 回调加 `_rxWatchdog is null` 短路保护 FF→FF Timer race（pre-ship code-review 捕捉）；CF→CF 路径保留 Generation 显式比较。1 新 RED → GREEN 测试 `HandleFirstFrame_After_FirstFrame_Timer_Fired_Does_Not_Get_Buffer_Cleared_By_Late_Timer_Callback`。 |
| 2 | C-HI-2 | UdsSession / UdsClient / AppHostBuilder | `UdsSession` 新增 `ILogger<UdsSession>` 参数 ctor；`UdsClient` ctor 改为 `(..., ILogger<UdsSession>)`；`AppHostBuilder` 注册 `ILogger<UdsSession>` + 透传 logger 给 `UdsClient` 与 `UdsSession`。production wire 修复 v1.2.12 H1 carry-over。 |
| 3 | C-HI-3 | UdsClient | `SendRequestInternalAsync` P2 timeout 改 `Token.Register` callback → `TrySetCanceled(_responseTcs)`；finally 严格顺序 (registration dispose → `_responseTcs=null` → `_responseCts=null` → cts dispose)；`OnMessageReceived` 加 `cts is not null && !IsCancellationRequested` fast-path + `ObjectDisposedException` try/catch。修复 v1.2.12 pre-existing bug (P2 timeout 不 cancel `_responseTcs.Task`)。 |
| 4 | A-HI-1 | IsoTpLayer | `SendCanFrameAsync` 多帧发送失败首次 throw `IsoTpSendFailedException : IOException`（含 `CanId` + `FrameIndex` + `InnerException`）；`SendMultiFrameAsync` 在 throw 前清空 `_cfCounter` 中断 CF burst；`AppHostBuilder` outer catch `when (!(ex is IsoTpSendFailedException))` 跳过重复日志。修复 v1.2.12 M-1 carry-over。 |

## 修复 (MEDIUM)

| # | ID | 组件 | 修复 |
|---|----|------|------|
| 5 | A-ME-2 | UdsSession | S3 keepalive OCE 分支加 `[LoggerMessage] LogS3KeepAliveShutdown` (EventId 5002, LogLevel.Debug) 与 Exception 分支对齐；Exception 分支 unchanged。EventId 5002 与 5001 (LogS3KeepAliveFailed) 同区块保留文件内连续。`TesterPresentAsync` virtual seam 仍 deferred。 |
| 6 | B-ME-1 | ScriptView | `OnLoaded` async void 加 `_isLoaded` guard（3 处 post-await 副作用 setter 短路）；订阅 `Unloaded` 事件，`OnUnloaded` dispose `EditorWebView` 并 null field；新增 `RaiseLoadedForTesting` / `RaiseUnloadedForTesting` internal helpers。修复 v1.2.12 L-1 carry-over。 |

## 修复 (LOW)

| # | ID | 组件 | 修复 |
|---|----|------|------|
| 7 | A-LO-3 | SendFrameLibrary | `_cachedCount` 字段 (init -1 sentinel) + `EnsureLoaded()` lazy init；所有 public mutator (`Add`/`Remove`/`Save(IEnumerable)`/`Save()`) 在 `_gate` 下维护不变量。2 新测试 (concurrent hammer + lazy load once)。 |
| 8 | A-LO-4 | SendFrameLibrary | `SaveUnlocked` 改 atomic `File.Move(tmp, _path, overwrite: true)`（消除 v1.2.12 `File.Replace` + `File.Exists` 分支 TOCTOU race）；新增 `AtomicSaveMoveCallCount` 内部计数器，测试用 delta 断言防回归。 |
| 9 | D-LO-5 | ChannelRouter | 新增 `LogChannelRouterSinkOnFrameFailed` source-gen (EventId 6010, Warning, `{SinkType}` template) 在 outer catch BEFORE `s.OnError(ex)` delegation；与现有 EventId 6004 (`LogSinkOnError`) 并存，operator 可追溯完整 exception chain (原始 OnFrame ex + 二次 OnError ex)。 |
| 10 | B-LO-6 | Test files | 4 个 App.Tests 文件 (AppShellViewModelTests / DbcViewModelTests / SignalViewModelTests / RecordViewModelTests) CRLF → LF 行尾 normalize（2355 行）；绕过 .editorconfig `tests/**` 的 `* text=auto` (git diff -w = 0 bytes；1:1 insertion:deletion = canonical CRLF swap)。**spec defect 揭示**: spec 称 "production code 已 LF" 但 `git grep -lI $'\r' src/` 返回 4 个 src/ CRLF 文件 (AssemblyInfo.cs + 3 .csproj) — **filing to v1.2.14 PATCH**；本 PATCH 仅 normalize `tests/`。 |

## 修复 (pre-ship code-review 捕捉)

| # | 组件 | 修复 |
|---|------|------|
| 11 | IsoTpLayer | pre-ship code-review 在 `IsoTpLayer.HandleConsecutiveFrameLocked` 路径发现 latent FF→FF Timer race：FF1 Timer 在 FF2 已 install 后 fire，`Token.Register` 回调仅 `Generation` 检查导致 FF1 回调错清 FF2's `_rxInProgress`/`_rxBuffer`。**修复**: Register 回调拆为 `_rxWatchdog is null` (FF→FF) + `Generation != expectedGeneration` (CF→CF) + `_rxInProgress` 三段独立 guard；1 新 RED → GREEN 测试 (`HandleFirstFrame_After_FirstFrame_Timer_Fired_Does_Not_Get_Buffer_Cleared_By_Late_Timer_Callback`)。Commit `b5aa88d`。 |

## TDD record

14 RED → GREEN / fix cycle commits on `feature/v1-2-13-patch`，squash-merge → main + tag `v1.2.13` + release：

- Task 1: IsoTpLayer watchdog CTS race (CRITICAL RefCount off-by-one + xmldoc + test harden + 1 fix-cycle；2 RED + 1 GREEN + 1 review-fix)
- Task 2: UdsSession logger wire-up (3 RED + GREEN)
- Task 3: UdsSession S3 OCE / Exception merge (1 RED + GREEN + INFO)
- Task 4: UdsClient P2 timeout cancel (2 RED + GREEN + 2 MEDIUM fix)
- Task 5: IsoTp send-fail throw (4 RED + GREEN; mirror-test rewrite)
- Task 6: ScriptView OnLoaded guard + WebView2 dispose (2 RED + GREEN)
- Task 7: SendFrameLibrary.Count cache (2 RED + GREEN)
- Task 8: SaveUnlocked atomic File.Move (1 RED + GREEN + AtomicSaveMoveCallCount fix)
- Task 9: ChannelRouter original-ex log (1 RED + GREEN + 1 existing update)
- Task 10: CRLF normalize tests/ (pure format; 0 RED)
- Pre-ship HIGH: IsoTpLayer Register null-state guard (1 RED + GREEN, commit `b5aa88d`)
- Ship prep (docs + final code-review)

## Test metrics

- **Total tests: 664 pass + 6 SKIP + 0 FAIL** (was 649 + 6 SKIP + 0 FAIL on v1.2.12 → **+15 net tests**)
  - Core: 238 pass + 0 SKIP (was 230 → +8: T1 watchdog race +1, T2 logger wire-up +2, T4 P2 timeout +2, T5 IsoTpSendFailed +1, T11 FF→FF race +1, T3 OCE +1)
  - Infrastructure: 84 pass + 2 SKIP (hardware SKIP unchanged; +1 new test)
  - App: 342 pass + 4 SKIP (was 336 → +6: T2 logger wire-up +1, T5 IsoTpSendFailed +1, T6 ScriptView +2, T9 ChannelRouter +1, T7 SendFrameLibrary +1)
- Coverage: ~85% (App project); Core / Infrastructure unchanged

## Pre-ship code-review

Pre-ship code-reviewer pass: **VERDICT BLOCK on initial pass → APPROVE after fix**。

Initial pass: 1 HIGH + 0 MEDIUM + 2 LOW。HIGH 已通过 fix subagent 在 commit `b5aa88d` 解决（FF→FF Timer race null-state guard + 1 RED → GREEN 测试）。LOW carry-over 至 v1.2.14 PATCH：
- L-1: `IsoTpLayer.SendFailureCount` 计数器无 test 断言（producer-only field）。建议 v1.2.14 加 `SendFailureCount == 1` 断言。
- L-2: `IsoTpLayer.SendMultiFrameAsync` finally 漏 `_txWaitingForFc = false`，Item 5 throw 后下个 `SendMultiFrameAsync` 会卡 `WaitForFlowControlAsync`。pre-existing（非本 PATCH 引入），建议 v1.2.14 finally 加 `lock (_txLock) { _txWaitingForFc = false; }`。

## Process notes

### Pre-ship code-review 捕捉真实 latent race

Item 11 (FF→FF Timer race) 是 pre-ship code-reviewer 在最后阶段利用 Phase 2.5 actual code exploration 捕捉到的真实 race —— 之前 v1.2.13 PATCH spec D-rev3 设计 Item 1 时假设 "Generation check 足够"，但实际 callback 代码未实现 `_rxWatchdog is null` 短路。**Lesson**：spec-time cross-validation 基于 memory 判断易漏 actual code 细节，必须 Phase 2.5 实地读 source；1 行测试 harden (`Task.Delay(5)` → `Task.Delay(150)`) 即可暴露 race。

### `dotnet format` CRLF finding

`dotnet format --verify-no-changes` 报 10 个 ENDOFLINE 错误，全部在 `src/PeakCan.Host.App/AssemblyInfo.cs`（属 v1.2.14 carry-over list 中的 "4 src/ CRLF files"）。**非本 PATCH 引入** (Item 10 仅 normalize `tests/`)，**scope-creep-free 决定不修**，仅在此记录。**Filing to v1.2.14 backlog.**

### Item 10 spec defect 揭示

Spec line 408 声称 "production code 已 LF" 与 `git grep -lI $'\r' src/` 结果不符。**Spec 时未做 Phase 2.5 actual-code exploration**（只 LF normalize `tests/` 是 spec §Out of Scope 决策，但 doc 未如实反映 src/ 现状）。**Process lesson**: spec 写 "已 LF" 类的 invariant 声明，必须 Phase 2.5 actual grep 验证；本 PATCH 仅 normalize `tests/` 范围交付，src/ CRLF 仍 deferred。

### 4 carry-over 收尾 + 1 new

v1.2.12 PATCH ship notes 列了 7 项 known follow-ups，本次 PATCH 关闭 4 (C-LO-1 watchdog race / C-HI-2 logger wire / C-HI-3 P2 timeout / A-HI-1 send-fail throw) + 1 (B-ME-1 ScriptView guard) + 1 (A-LO-4 SaveUnlocked atomic)。剩余 carry-over + pre-ship review 新的见下表。

## Known issues (deferred to v1.2.14 PATCH)

| # | 项目 | 来源 |
|---|------|------|
| 1 | `IsoTpLayer.SendFailureCount` 无 test 断言（producer-only） | Pre-ship code-review L-1 |
| 2 | `IsoTpLayer.SendMultiFrameAsync` finally 漏 reset `_txWaitingForFc = false` | Pre-ship code-review L-2 |
| 3 | 4 个 src/ 文件 (AssemblyInfo.cs + 3 .csproj) + .slnx CRLF 行尾 (Item 10 spec defect 揭示) | Item 10 spec defect |
| 4 | `TesterPresentAsync` virtual seam (S3 OCE 分支 end-to-end 测试必备) | Task 3 follow-up |
| 5 | `IsoTpLayer.HandleFirstFrame` 进度处理未对齐 Phase 2 进度 (Item 1 sub) | Task 1 minor |
| 6 | `SendFrameLibrary.Count` O(N) per call (已 cache 但 iter-style API 仍 O(N)) | v1.2.12 Task 1 minor carry-over |
| 7 | `CyclicSendService` async void throttling | v1.2.12 Task 10 minor carry-over |
| 8 | `ChannelRouter` OnError secondary-catch ordering 文档 | v1.2.12 Task 11 minor carry-over |

## Breaking changes

无。所有 PATCH 范围内变更，不改公开 API 签名。`Directory.Build.props` version bump 1.2.12 → 1.2.13 (Version / AssemblyVersion / FileVersion / InformationalVersion 同步)。`UdsClient` ctor signature 变更 (新增 `ILogger<UdsSession>` 参数) 是 internal-only composition 接线，不影响外部 callers。
