# v3.52.1 PATCH — AI 推理 v1 cleanup（姐妹 pattern hardening + test fixture + CanExecute refresh）

> 状态：v3.52.1 PATCH 设计（基于 v3.52.0 MINOR ship 时的 3 个 Minor findings）
>
> 触发条件：v3.52.0 MINOR squash-merged at `e09cb83` on main，tag `v3.52.0` pushed，GH release published。最终 whole-branch review（opus）记录 4 个 Minor，本 spec 处理其中 3 个 Minor + 一个 sister-pattern 1/3 验证。

## 目标

v3.52.0 MINOR 落地后的姐妹 pattern hardening + test fixture 共享 + UI CanExecute refresh。3 个 Minor 项（Minor 4 trailing-newline pre-existing mixed baseline 明确不做，避免历史文件 drift）。

## 当前代码证据（v3.52.0 ship 后）

- `src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs:170-172` — `IFrameSourceProvider` 注册为 `sp.GetRequiredService<ITraceSessionRegistry>() as TraceSessionRegistry` 显式 cast。Minor 1。
- `tests/PeakCan.Host.App.Tests/ViewModels/AnalysisFlowTests.cs:1188-1210` — `MakeVm()` 5 个 `new ...()` fresh instances per test。Minor 2。
- `TraceViewerViewModel.cs` — `RunAnalysisCommand` 的 `CanExecute` 读 `CurrentAnchorSnapshot`，但 `CurrentAnchorSnapshot` setter 没有 `NotifyCanExecuteChanged`。`LockAnchor` 写入 `CurrentAnchorSnapshot` 后 UI 不重评估。Minor 3。
- Whole-branch review at `.superpowers/sdd/final-review-report.md:79-87` — 4 Minor 明细 + HOLD 状态。

## 3 个 D 决策（D1-D3）

| ID | 决策 | 选择 | 理由 |
|---|---|---|---|
| D1 | cast mitigation 策略 | 改 DI 注册：`AddSingleton<TraceSessionRegistry>(...)` concrete-first，再 `IFrameSourceProvider` + `ITraceSessionRegistry` 都 forward `sp.GetRequiredService<TraceSessionRegistry>()`。**移除**显式 cast。 | Minor 1 mitigation 的两种选项之一（per review）。concrete-first + 两个 forward 比 cast 更明确，且与 `RateLimitedSendService`（sister at `AppServicesFlow.cs:146-148`）pattern 一致。 |
| D2 | test fixture 共享 | 用 `Substitute.For<>()` 替换 5 个 `new ...()`，mock 化所有 5 个分析依赖。AnchorSnapshotFlowTests 同步替换（4 个 helper）。 | 减少 test 启动开销 + 显式边界 + 与既有 NSubstitute sister pattern 一致（`tests/.../Replay/TraceViewerServiceTests.cs:53` 用 `Substitute.For<ILogger<TraceViewerService>>()`）。 |
| D3 | CanExecute refresh 方案 | 在 `AnchorSnapshotFlow.LockAnchor` 写完 `CurrentAnchorSnapshot` 后，调用 `RunAnalysisCommand.NotifyCanExecuteChanged()`（通过 `RunAnalysisAsync` partial 的 `LockAnchor` 同步调）。 | 单一明确触发点；保留 T7 reviewer's sister-pattern 的"explicit trigger from action point"。不改 `CurrentAnchorSnapshot` setter，因为 AnchorSnapshot 来自外部输入（双锚拖动）已经通过 [NotifyCanExecuteChangedFor] 在 IsLoading 上 wired（T9 follow-up `e38f6dc`）。 |

## 硬边界（继承 v3.52.0 spec）

所有 v3.52.0 14 条 hard-boundary 仍然成立。本 PATCH 不引入新网络/安全/解析边界。

## 架构

```text
[Original AppServicesFlow.cs:170-172 cast pattern]
services.AddSingleton<ITraceSessionRegistry, TraceSessionRegistry>();
services.AddSingleton<IFrameSourceProvider>(sp =>
    (TraceSessionRegistry)sp.GetRequiredService<ITraceSessionRegistry>());

[After PATCH: concrete-first + dual forward]
services.AddSingleton<TraceSessionRegistry>();
services.AddSingleton<ITraceSessionRegistry>(sp =>
    sp.GetRequiredService<TraceSessionRegistry>());
services.AddSingleton<IFrameSourceProvider>(sp =>
    sp.GetRequiredService<TraceSessionRegistry>());
```

```text
[Original AnchorSnapshotFlow.LockAnchor]
CurrentAnchorSnapshot = new AnchorSnapshot(...);
OnPropertyChanged(nameof(CurrentAnchorSnapshot));
ErrorMessage = null;

[After PATCH: explicit CanExecute refresh for RunAnalysisCommand]
CurrentAnchorSnapshot = new AnchorSnapshot(...);
OnPropertyChanged(nameof(CurrentAnchorSnapshot));
RunAnalysisCommand.NotifyCanExecuteChanged();   // <-- new line
ErrorMessage = null;
```

```text
[Original AnalysisFlowTests.MakeVm]
return new TraceViewerViewModel(registry, dbc, logger, sessionLib,
    new EvidenceExtractor(),
    new LocalAnalyzer(),
    new AnalysisSessionRegistry(),
    new NotImplementedLlmProvider(),
    frameSource);

[After PATCH: mock all 5 dependencies]
return new TraceViewerViewModel(registry, dbc, logger, sessionLib,
    Substitute.For<EvidenceExtractor>(),
    Substitute.For<LocalAnalyzer>(),
    Substitute.For<AnalysisSessionRegistry>(),
    Substitute.For<ILlmProvider>(),
    frameSource);
```

## 数据契约

不引入新 record。改动纯 DI + 1 行 Command.NotifyCanExecuteChanged() + test mock 化。

## 组件拆分

修改文件（5 个）：

| 文件 | LoC delta | 改动 |
|---|---|---|
| `src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs` | -2 +6 | cast → concrete-first dual forward |
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnchorSnapshotFlow.cs` | +1 | `RunAnalysisCommand.NotifyCanExecuteChanged()` |
| `tests/PeakCan.Host.App.Tests/ViewModels/AnalysisFlowTests.cs` | -3 +5 | mock 5 dependencies |
| `tests/PeakCan.Host.App.Tests/ViewModels/AnchorSnapshotFlowTests.cs` | -1 +2 | mock 2 dependencies used by helper |
| `tests/PeakCan.Host.App.Tests/ViewModels/AnalysisFlowTests.cs` | +2 | new test: `RunAnalysisCommand_CanExecute_RefreshedAfterLockAnchor` |

总增量 ~+10 LoC（spec 估算）。

## UI 接线

不改动 UI。Minor 3 的 CanExecute refresh 在运行时已生效（用户 LockAnchor 后 RunAnalysis 按钮立即可点）。

## 错误处理

不引入新错误处理路径。cast → concrete-first 是 build-time 类型安全提升，不是错误处理。

## 测试策略

- 现有 `AnchorSnapshotFlowTests` 4 tests + `AnalysisFlowTests` 2 tests 全部保留并 PASS
- 新增：`AnalysisFlowTests.RunAnalysisCommand_CanExecute_RefreshedAfterLockAnchor` —— 1) 调用 `LockAnchorCommand.Execute(null)` 创建 snapshot，2) 直接断言 `RunAnalysisCommand.CanExecute(null) == true`（通过 mock `IFrameSourceProvider` 返回 1 个有效 evidence 让 flow 跑通）。
- 全套 1404+1 PASS / 0 FAIL。

## 复评/不做项

- **Minor 4 trailing-newline 统一**：明确不做。v3.52.0 文件 trailing newline 已自然混合（review 标记为"pre-existing mixed baseline"），drift 修复需要 `.editorconfig` + `git blame` 改写，工作量 12+ 文件 diff，不属于姐妹 pattern hardening PATCH。
- **P1 LLM PATCH**：明确推到后续 PATCH。
- **W36 god-class refactor**：明确推到后续。
- **Hard-boundary #13 Evidence ID whitelist 过滤**：明确推到 P1 PATCH（仅接口已声明，未实现）。

## 4 NEW 1/3 lesson candidates 观察

| 候选 | 期望观察点 |
|---|---|
| `concrete-first-dual-interface-di-registration-when-cast-was-needed` | T1 D1: 移除 cast 后，dual-binding 仍然 single-instance 复用 |
| `run-analysis-command-can-execute-refresh-must-trigger-from-action-not-from-setter` | T1 D3: setter-driven NotifyPropertyChanged 不够；必须 action point |
| `test-fixtures-should-mock-analysis-pipeline-instead-of-instantiating-real` | T2 D2: 5 mock 替换，测试仍全 pass + 启动时间下降 |
| `notify-can-execute-changed-after-snapshot-write-is-cross-partial-coordination` | T1 D3: AnchorSnapshotFlow partial → AnalysisFlow partial 跨 partial 显式触发 |

## 文件 / LoC 估算

| 文件 | LoC delta |
|---|---|
| `AppServicesFlow.cs` | +4 |
| `AnchorSnapshotFlow.cs` | +1 |
| `AnalysisFlowTests.cs` | +4 (mock + new test) |
| `AnchorSnapshotFlowTests.cs` | +1 (mock) |
| **总计** | **+10** |

## 待 SPEC 用户复核

本文为 spec 初稿。请 review 后批准，下一步进入 writing-plans 写实施计划。