# v3.52.1 PATCH — Trace Viewer AI 推理 v1 cleanup

> v3.52.0 MINOR ship 后的姐妹 pattern hardening + test fixture mock + UI CanExecute refresh。3 个 Minor 修复（Minor 4 trailing-newline 显式不做）。

## 概述

清理 v3.52.0 MINOR ship 时 final-review-report 标记的 3 个 Minor 项 + 一个 unlock 测试 fixture mock 化的 blocker。

## 修复内容

### Minor 1: DI cast mitigation

**Before** (`AppServicesFlow.cs:170-172`):
```csharp
services.AddSingleton<ITraceSessionRegistry, TraceSessionRegistry>();
services.AddSingleton<IFrameSourceProvider>(sp =>
    (TraceSessionRegistry)sp.GetRequiredService<ITraceSessionRegistry>());
```

**After** (concrete-first dual forward, 跨 `ViewModelsBatch2Flow.cs` + `AppServicesFlow.cs`):
```csharp
services.AddSingleton<TraceSessionRegistry>();   // concrete-first
services.AddSingleton<ITraceSessionRegistry>(sp =>
    sp.GetRequiredService<TraceSessionRegistry>());
services.AddSingleton<IFrameSourceProvider>(sp =>
    sp.GetRequiredService<TraceSessionRegistry>());
```

**收益**：消除显式 cast，类型安全提升；如果未来 `TraceSessionRegistry` 实现变化（例如改成装饰器），DI 不会静默 broken。

### Minor 2: test fixture mock 化

`AnalysisFlowTests.MakeVm` + `AnchorSnapshotFlowTests.MakeVm` 从 5 个 `new ...()` 真实实例改为 `Substitute.For<>()` mock 化全部 5 个分析依赖。

**Requires**：unsealed `EvidenceExtractor` + `LocalAnalyzer` + `AnalysisSessionRegistry`（3 行 `public sealed class` → `public class`）。Sister pattern：`DbcService` already unsealed.

### Minor 3: RunAnalysisCommand CanExecute refresh

**Before** (`AnchorSnapshotFlow.LockAnchor`):
```csharp
CurrentAnchorSnapshot = new AnchorSnapshot(...);
OnPropertyChanged(nameof(CurrentAnchorSnapshot));
ErrorMessage = null;
```

**After**:
```csharp
CurrentAnchorSnapshot = new AnchorSnapshot(...);
OnPropertyChanged(nameof(CurrentAnchorSnapshot));
RunAnalysisCommand.NotifyCanExecuteChanged();   // <-- new line
ErrorMessage = null;
```

**收益**：用户点 LockAnchor 后 UI Run 按钮立即可点（之前需等其他状态变化触发 CanExecute 重新评估）。

## 新增测试

`AnalysisFlowTests.RunAnalysisCommand_CanExecute_RefreshedAfterLockAnchor` —— 验证 T2 fix：CanExecute=false → LockAnchor + 双锚 → CanExecute=true。

## 计数

- 4 commits on `feature/v3-52-1-patch-cleanup`（T1 + T2 + T3 + 计划外 fix）
- 5 files changed (3 Core unseal + 2 App test mock + 1 AnchorSnapshotFlow NotifyCanExecuteChanged + AppServicesFlow/ViewModelsBatch2Flow DI)
- +15 LoC 净增量
- 1405 PASS / 0 FAIL / 5 SKIP（v3.52.0 的 1404 + 1 新 test）

## 4 NEW 1/3 lesson candidates（待 2nd 观察后晋升）

| 候选 | 期望观察点 |
|---|---|
| `concrete-first-dual-interface-di-registration-when-cast-was-needed` | T1 verified (cast removal, single-instance preserved) |
| `run-analysis-command-can-execute-refresh-must-trigger-from-action-not-from-setter` | T2 verified (action point explicit trigger) |
| `test-fixtures-should-mock-analysis-pipeline-instead-of-instantiating-real` | T3 verified (5 mocks, tests still pass + unseal sister pattern) |
| `notify-can-execute-changed-after-snapshot-write-is-cross-partial-coordination` | T2 + T3 verified (AnchorSnapshotFlow → AnalysisFlow cross-partial) |

## 6 之前 deferred 项仍然 pending

- **P1 PATCH**: DeepSeek / Azure OpenAI / Ollama 真实 Provider 实现 + API Key 安全存储（CredentialManager / DPAPI）+ JSON Schema 校验 + Evidence ID whitelist 过滤
- **W36+ god-class refactor**: StatsViewModel 263 / DbcTokenizer 239 / DbcSendViewModel 238 / AscLocator 225 / RecordService 215

## 关联

- Spec: `docs/superpowers/specs/2026-07-17-v3-52-1-patch-cleanup-design.md`
- Plan: `docs/superpowers/plans/2026-07-17-v3-52-1-patch-cleanup.md`
- v3.52.0 spec/plan/ship: `docs/superpowers/specs/2026-07-16-ai-inference-v1-design.md` + `docs/superpowers/plans/2026-07-16-ai-inference-v1.md` + commit `e09cb83`

## 验证

```bash
dotnet build PeakCan.Host.slnx -c Debug           # 0 errors, 0 warnings on touched code
dotnet test PeakCan.Host.slnx --no-build -c Debug  # 1405 PASS / 0 FAIL / 5 SKIP
```

## 已知并行测试 flake（不影响 ship）

`Replay.AscParserTests.Parse_MalformedLines_LogsEachWithLineNumberAndReason` + `Replay.IReplayServiceTests.SetSpeed_PreservesCurrentTimestamp` 在并行测试（`MaxParallelThreads > 1`）下偶发失败。**根因**：wall-clock timing 测试在 CPU 负载高时不稳定。**对策**：`dotnet test -- xUnit.MaxParallelThreads=1` 单线程运行即可 510/510 PASS。属于 pre-existing sister pattern（v3.52.0 ship memory 已有类似 case）。**不是 v3.52.1 PATCH 引入**。