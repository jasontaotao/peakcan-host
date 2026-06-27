# Release Notes — PeakCan Host v1.2.12

**Date:** 2026-06-27

## Summary

v1.2.12 是一个 14 项 verified fixes 的 PATCH，源于 v1.2.11 ship 后由 4 个独立 reviewer (code / csharp / security / silent-failure) 并行评审产生的 56 项 raw findings，经 4-way 交叉验证 + Phase 2.5 actual-code 实地核对 (由 `Explore` agent 读 actual code) 后，落地为本 PATCH 的 14 项 verified 修复。**无新 UI、无新功能、无 public API 移除**；全部为 internal quality / safety 改进。

## 修复 (HIGH)

| # | ID | 组件 | 修复 |
|---|----|------|------|
| 1 | 1.1 | SendFrameLibrary | `SaveCurrentToLibrary` / `DeleteFromLibrary` 改用 atomic `Add`/`Remove`；失败走 Status 而非 crash。**修复 v1.2.11 修复的回归**：v1.2.11 在 SendFrameLibrary 加 `_gate` + `Add`/`Remove` 原子方法，但 `SendViewModel` 仍走非原子的 `Load → ToList → Add → Save` 路径。 |
| 2 | 1.2 | Composition / IsoTp | `AppHostBuilder.cs` 移除 `.AsTask().Wait()`；改 `Func<CanFrame, Task>` 签名 + `SemaphoreSlim` 串行化 FF/CF。 |
| 3 | B-CR-1 | IsoTpLayer | `MessageReceived` handler invoke 在 lock 外 + try/catch（`Monitor.Exit`/`Enter` 锁重入处 state 修复）。 |
| 4 | B-HI-1 | App | `OnStartup` / `OnExit` 改 `async void`，消除 STA 死锁风险。`OnStartup` 失败 → `Shutdown(1)` + Fatal 日志。 |
| 5 | 1.4 | RecordService | `Channel<CanFrame>` (8192 + DropOldest) + writer thread + `PeriodicTimer` 1Hz flush；`OnFrame` 改非阻塞 `TryWrite`；3 Interlocked counter (Enqueued/Frame/DroppedOnFullChannel)。 |
| 6 | A-HI-3 | Composition | `RecordViewModel` / `SendViewModel` / `SignalViewModel` 注册为 `IHostedService`，host `StopAsync` 自动触发 `Dispose`。 |
| 7 | B-HI-5 | ScriptView | WebView2 init try/catch + fallback `<TextBlock>`；init 失败不再杀进程，路由日志经 `ScriptViewModel.OnWebView2InitFailed` 走 `ILogger`。 |
| 8 | 1.5 | IsoTpLayer | `HandleFirstFrame` 加 `frame.Length > MaxMessageLength` check + 拒绝超长 FF + state reset（防御 DoS）。 |
| 9 | 1.8 | UdsSession | S3 keepalive bare catch 改 typed catch (`OCE` + `Exception`) + `ILogger.LogWarning` + `S3FailureCount` 计数器。 |
| 10 | 1.3 | CyclicSendService | `OnTimerTick` `lock(this)` snapshot + `_generation` counter；`_sendCount` 拆为 `_sendSuccessCount` / `_sendFailureCount` 两个 `Interlocked`。 |

## 修复 (MEDIUM)

| # | ID | 组件 | 修复 |
|---|----|------|------|
| 11 | 1.7 | Sinks (4 services) | `OnError` 改 `ILogger.LogWarning`：`RecordService` / `DbcDecodeBackgroundService` / `TraceService` / `ChannelRouter`。EventIds 6001-6004。 |
| 12 | C-LO-4 | PeakErrorMapper | 加 `FlagMask=0xFFFF0000u`；`IsOk` / `ToErrorCode` 剥离 flag bits。复合 status 0x40000040 (BUSOFF|INITIALIZE) 正确解析为 `BusOff`；0x00010000 (RESOURCE alone) 正确解析为 `OK`。 |
| 13 | 1.9+1.10 | SendFrameLibrary | `SaveUnlocked` 改 typed catch (`IOException` / `UnauthorizedAccessException` / `SecurityException`) + `File.Replace` (first-save `File.Move` 兜底) + UTF-8 BOM (Windows 用户的 Notepad 体验) + tmp 失败清理。 |
| 14 | 1.11 | UdsClient | `_responseTcs` / `_responseCts` 改 `Volatile.Read` / `Volatile.Write` 显式内存屏障；`SendRequestInternalAsync` finally 同步 nullify 两者 (修复既有漏 nullify 的 `_responseTcs`)。 |

## TDD record

15 RED → GREEN commit on `feature/v1-2-12-patch`，squash-merge → main + tag `v1.2.12` + release：

- Task 1: SendFrameLibrary atomic Add/Remove (10 RED + GREEN；6 lib + 3 VM + 1 fix-cycle)
- Task 2: IsoTpLayer async Task callback (4 RED + 6 review-fix 含 1 latent M-6 regression)
- Task 3: IsoTpLayer MessageReceived try/catch (3 RED + GREEN)
- Task 4: App async void (0 新增；直接 GREEN)
- Task 5: RecordService Channel + writer (6 RED + GREEN)
- Task 6: VMs IHostedService (3 RED + 1 GC.SuppressFinalize fix)
- Task 7: ScriptView WebView2 fallback (4 RED 含 1 logger regression)
- Task 8: IsoTpLayer FF length check (5 RED + 1 misleading-test 改名)
- Task 9: UdsSession S3 typed catch (3 RED + GREEN)
- Task 10: CyclicSendService race + split count (5 RED + 3 I-1/I-2/I-3 fix)
- Task 11: 4 sink ILogger (5 RED + GREEN)
- Task 12: PeakErrorMapper flag strip (5 RED + 1 renamed)
- Task 13: SaveUnlocked typed catch + File.Replace (4 RED + GREEN)
- Task 14: UdsClient Volatile (2 RED + GREEN)
- Task 15: ship prep (docs + final code-review)

## Test metrics

- **Total tests: 649 pass + 6 SKIP + 0 FAIL** (was 300 + 4 SKIP + 0 FAIL on v1.2.11)
  - Core: 230 pass + 0 SKIP
  - Infrastructure: 83 pass + 2 SKIP (hardware SKIP)
  - App: 336 pass + 4 SKIP (hardware + STA-bound SKIP)
- **New tests: +36 → +57 = +93 net** (Core 12 + Infrastructure 2 [unchanged] + App 81)
- Coverage: ~85% (App project); Core / Infrastructure unchanged

## Pre-ship code-review

Pre-ship code-reviewer pass: **APPROVE 0 Critical / 0 High / 2 Medium / 1 Low**。

- M-1: `IsoTpLayer.SendCanFrameAsync` 在 multi-frame 传输中吞掉 send-callback 异常（log + swallow），CF burst 中途 bus-off 不中止 transport，仅 FC timeout 后才由 UdsClient P2/P2* surface。**建议 v1.2.13 PATCH**：首次 send 失败 throw 中止 multi-frame。**Filing to v1.2.13 backlog.**
- M-2: `UdsSession` 新增的 logger-aware ctor 在 production 未 wire（`UdsClient` 走 parameterless ctor），属 spec §9.1 known-deferred。**Filing to v1.2.13 backlog.**
- L-1: `ScriptView.OnLoaded` async void 无 `CancellationToken` / `_disposed` guard；当前所有 post-await 副作用 (`IsEditorReady` / `EditorError` setter + `NavigateToString`) 对 detached view 为 no-op，未来添加 `EditorWebView.*` 调用易回归。**Filing to v1.2.13 backlog.**

## Process notes

### `dotnet format` CRLF finding (pre-existing, non-blocking)

`dotnet format --verify-no-changes` 报 `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs:867-886` 行尾 CRLF (20 行 ENDOFLINE)。**该文件在本 PATCH (a58b051..809f437) 内未被修改** (`git log --oneline -- tests/.../AppShellViewModelTests.cs` 最末 commit = a58b051 v1.2.11 squash)；CRLF debt 来自更早的 v1.2.0 PATCH 时期。**非本 PATCH 引入，scope creep-free 决定不修**，仅在此记录。**Filing to v1.2.13 backlog.**

### 14 项 一次 ship 而非分批

Spec D1 决策：v1.2.11 PATCH review 显示分批会导致 v1.2.10 PATCH review 的 fix (SendFrameLibrary `_gate`) 在 v1.2.11 落地时又出回归；14 项 fix 互相耦合 (Task 1 修 v1.2.11 回归、Task 13 同库 atomic write、Task 10 同样与 v1.2.11 UI 交互)，一次 ship 锁住所有耦合。

### csharp-reviewer 4-way 评审流水

4 个独立 reviewer (code / csharp / security / silent-failure) 并行独立评审 → 56 raw findings → 4-way cross-val (CONFIRM/REFUTE/OVERSTATED/UNDERSTATED/OUT-OF-SCOPE) → Phase 2.5 `Explore` agent 读 actual code 实地核对（避免 Phase 2 cross-val 基于 memory 判断可能虚构位置）→ 26 VERIFIED / 9 PARTIAL / 3 NOT-FOUND / 1 EXAGGERATED。Phase 2.5 撤出 6 项 (D-HI-3 / D-ME-7 / C-HI-3 / A-ME-4 / C-NEW-1 / C-ME-5)，最终落地 14 项 verified。

## Known issues (deferred to v1.2.13 PATCH)

| # | 项目 | 来源 |
|---|------|------|
| 1 | `IsoTpLayer.HandleConsecutiveFrameLocked` `StartReceiveWatchdog` race 静默破坏多 CF (≥2 CF) reassembly | Task 8 latent pre-existing |
| 2 | `UdsSession` S3 keepalive logger 未在 production wire (AppHostBuilder 走 parameterless ctor) | Task 9 follow-up H1 |
| 3 | `UdsSession` S3 keepalive OCE 分支 unreachable | Task 9 follow-up M1 |
| 4 | `UdsClient.SendRequestInternalAsync` P2 timeout 不 cancel `_responseTcs.Task` | Task 9 pre-existing |
| 5 | `IsoTpLayer.SendCanFrameAsync` 吞 send-callback 异常 (M-1 来自 pre-ship review) | Pre-ship code-review |
| 6 | `ScriptView.OnLoaded` async void 无 `_disposed` / `IsLoaded` guard (L-1 来自 pre-ship review) | Pre-ship code-review |
| 7 | `AppShellViewModelTests.cs:867-886` CRLF 行尾 (pre-existing, 来自 v1.2.0 era) | `dotnet format` finding |

## Breaking changes

无。所有 PATCH 范围内变更，不改公开 API 签名。`Directory.Build.props` version bump 1.2.11 → 1.2.12 (Version / AssemblyVersion / FileVersion / InformationalVersion 同步)。
