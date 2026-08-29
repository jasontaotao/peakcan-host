# Plan: HIL 多通道接线缺口与 UX 收口 — TDD 实施计划

> Plan date: 2026-08-28
> Spec: `docs/superpowers/specs/2026-08-27-hil-multichannel-wiring-and-ux-gaps-design.md`（CONFIRMED，2026-08-28 review 修复后无遗留决策点）
> 方法: TDD（RED → GREEN → IMPROVE），每阶段 git checkpoint（`test:` / `feat:` / `refactor:`）
> 范围: host（`D:\claude_proj2\peakcan-host`）+ studio（`D:\claude_proj2\peakcan-studio`）双仓 App/VM/Infra 层；**不涉及 hil-core，无 NuGet，无双仓 lockstep**

## 批次与任务（spec §8 顺序）

| 批次 | 任务 | 级别 | 前置 |
|---|---|---|---|
| **1 正确性** | T1 GetSignalValue 通道路由（G1） | HIGH | — |
| 1 | T2 per-channel DbcPath/UDS ID 双仓贯穿（G2） | HIGH | T1（host 集成用例依赖采样路由） |
| 2 | T3 USBx 下拉绑已连接通道（G3） | MED | 独立 |
| 2 | T4 文件后缀区分 + 硬校验（G4） | MED | 独立 |
| 3 | T5 prompt/结果树补 Channel/Actual/Expected（G5/G6） | LOW | — |

每 Task：host + studio 同 commit 内完成；GREEN 后即 code-reviewer。

---

## Task 1 — GetSignalValue 通道路由（G1）

### 改动文件

**产品（host）**
- `src/PeakCan.Host.Core/HIL/Contracts/IAssertionContext.cs` — 追加 DIM 重载（契约裁决：null/空转发单通道版、非 null 抛 `NotSupportedException`）
- `src/PeakCan.Host.Core/HIL/StepExecutor/AssertSignalWithinStepExecutor.cs:35` — 采样 `ctx.GetSignalValue(p.TargetChannel, p.SignalName, maxAgeMs: 5000)`
- `src/PeakCan.Host.Core/HIL/StepExecutor/AssertStableStepExecutor.cs:34` — 同上
- `src/PeakCan.Host.Infrastructure/HIL/MultiChannelAssertionContext.cs` — 显式实现 → `ResolveChannel(channelName).GetSignalValue(...)`（未知名抛 KeyNotFoundException）
- `src/PeakCan.Host.Infrastructure/HIL/SingleChannelContext.cs` — 显式实现 → `AcceptsChannelName ? GetSignalValue(...) : null`

**测试（host）**
- `tests/PeakCan.Host.Core.Tests/HIL/StepExecutor/TemporalAssertionExecutorTests.cs` — `ManualAssertionContext` 通道感知化（`ChannelValues` 字典，2 参数版=默认通道，3 参数版=按通道）；`_RoutesToTargetChannel` 补采样值断言；新增 bus-a/bus-b 同名不同值反例
- `tests/PeakCan.Host.Infrastructure.Tests/HIL/Multichannel/MultiChannelAssertionContextTests.cs` — 补 `GetSignalValue(channelName)` 路由 + 未知通道抛异常用例
- `tests/PeakCan.Host.Infrastructure.Tests/HIL/Multichannel/SingleChannelContextTests.cs` — 补命名通道不匹配返回 null、null 通道=自身
- `tests/PeakCan.Host.Infrastructure.Tests/HIL/Multichannel/DualChannelLoopbackE2E.cs` — 补时间窗断言用例（bus-a/bus-b 各自周期发同名信号不同值，`AssertSignalWithin(TargetChannel=bus-b)` 断 bus-b 值 Pass、断错通道值 Fail）

### TDD 步骤

```
RED  1. IAssertionContext 加 DIM 重载（编译基础，契约）
     2. ManualAssertionContext 通道感知化 + _RoutesSamplingToTargetChannel 用例
        （bus-a=100/bus-b=200，TargetChannel=bus-b，Expected=200±5 → executor 仍走 2 参数版=默认通道 → 断言失败）
     3. 运行 TemporalAssertionExecutorTests → RED ✓
     4. commit test: add sampling-routing reproducer for time-window assertions (G1)
GREEN 5. executor 采样改 3 参数版 + MultiChannel/SingleChannel 显式实现
     6. 运行同 target → GREEN ✓
     7. commit feat(hil): route GetSignalValue sampling by TargetChannel (G1)
IMPROVE 8. 全量回归（Core + Infra）；DualChannelLoopbackE2E 补时间窗用例
     9. code-reviewer
```

---

## Task 2 — per-channel DbcPath/UDS ID 双仓贯穿（G2）

### 改动文件

**host 读侧**
- `src/PeakCan.Host.App/ViewModels/HilViewModel.cs` `BuildHardwareChannels`（164-221）— 弱解析读 `name/dbcPath/udsRequestId/udsResponseId` 四字段，构造 `ChannelConfig` 透传（Handle 仍空按索引映射）；状态栏提示各通道 DBC/UDS 绑定概况（明示"界面 DBC 已被 suite 覆盖"）
- `tests/PeakCan.Host.App.Tests/...HilViewModelTests` — 补 BuildHardwareChannels 透传用例（suite 带三字段 → ChannelConfig 非 null）
- `tests/PeakCan.Host.Infrastructure.Tests/HIL/Multichannel/HeadlessHostBuilderMultiChannelTests.cs` — 补"suite 带 per-channel UDS ID → 双通道独立栈"集成用例（复用 DualChannelUdsLoopbackE2E 虚拟通道构造）

**studio 写侧**
- `src/PeakCan.Studio.App/ViewModels/TestSuiteBuilder/ChannelConfigRow.cs` — 加 `DbcPath`/`UdsRequestId`/`UdsResponseId` 字段；`ToChannelConfig`/`From` 透传；UDS ID 裸 hex 输入保存转 uint JSON 数字（裁决）；通道名重名校验
- 对应 XAML（ChannelConfigRow 编辑模板）
- `tests/PeakCan.Studio.Core.Tests` / `App.Tests` — ChannelConfigRow round-trip（DbcPath/UDS ID 写 suite JSON + 读回）；重名校验
- studio `InteropTests` — 补 `channels[].dbcPath/udsRequestId/udsResponseId` 序列化断言

### TDD 步骤

```
host 侧:
RED  1. HilViewModelTests 补透传用例（先写测试）→ RED ✓ → commit test
GREEN 2. BuildHardwareChannels 透传 → GREEN ✓ → commit feat
studio 侧:
RED  3. ChannelConfigRow round-trip 测试 + InteropTests 序列化断言 → RED ✓ → commit test
GREEN 4. ChannelConfigRow 字段 + ToChannelConfig/From + XAML → GREEN ✓ → commit feat
IMPROVE 5. HeadlessHostBuilder 集成用例；全量回归双仓；code-reviewer
```

---

## Task 3 — USBx 下拉绑已连接通道（G3）

- `HilViewModel.cs` — `AvailableChannels` 动态刷新（`_connectedChannels()` 快照 → `USB{n}（已连接·500kbps{·FD}）`）；保留上次选择；Loaded + Run 前刷新；无已连接 → 下拉空 + 提示；suite 多通道（declaredCount>1）→ **下拉置灰** + "通道由套件声明按序绑定"
- `HilView.xaml` — 下拉模板 + 置灰绑定
- `HilViewModelTests` — provider N 路 → 下拉 N 项 + 默认首项/记忆项；provider 空 → 空 + 提示；选中 → `"USB{n}"`；多通道 → 置灰
- TDD 同 Task 1 节奏

## Task 4 — 文件后缀区分 + 硬校验（G4）

- 后缀约定：`.suite.json` / `.ecu.json` / `.matrix.json` / `.result.json`（无兜底）
- 保存侧 defaultExt + filter：studio `RoundTripFlow.partial.cs:116`（suite）、`EcuSimulatorViewModel.cs:176`（ecu）；host `EcuScriptEditorViewModel.cs:89`（ecu）、CLI `Program.cs:36/184`（ecu/result）
- 打开侧 filter：host `HilViewModel` BrowseSuite(:126)/BrowseEcu(:245)/BrowseMatrix(:256) + `EcuScriptEditorViewModel`；studio `RoundTripFlow.partial.cs:92`、`EcuSimulatorViewModel.cs:110`、`ResultAnalysisViewModel.cs:102/126`
- 内容硬校验（裁决字段）：suite 顶层 `cases`；ECU 顶层 `name`+`canIds.requestId/responseId`（`EcuScriptLoader.cs:32/83/92-93`）；矩阵顶层 `name`+`ecus[]`（`MatrixConfigLoader.cs:25/28`）
- `LoadCaseList` 删静默 catch
- 测试：双仓文件对话框 filter 单测 + 内容校验单测（错误文件 → 明确提示）

## Task 5 — prompt / 结果树补 Channel/Actual/Expected（G5/G6）

- **现状修正**：executor 已填 `Channel: p.TargetChannel`，本任务只剩补 `ActualValue/ExpectedValue`
- `HilPromptBuilder.cs:24-40` — 渲染追加 `Channel: {Channel}`（非空时）
- `AssertSignalWithinStepExecutor`/`AssertStableStepExecutor` — 构造 StepResult 补 `ActualValue`（如 `hits 3/5 samples` / `max-min=4.2`）/`ExpectedValue`（插值后实际值，Tolerance 空不带 `±`）
- `HilResultNode.StepNode`（`HilResultNode.cs:25-30`）+ `BuildResultsTree`（`HilViewModel.cs:472-502`）+ `HilView.xaml` 结果树模板（Channel 徽标 + Actual/Expected 行，仅 Failed 步骤渲染）
- 测试：executor 补 Actual/Expected 断言；HilPromptBuilder 渲染 Channel 用例；VM 结果树填充用例

---

## 实施纪律

1. **严格 TDD**：每 Task RED 先验证失败（编译期或运行期），再最小实现 GREEN，再重构
2. **git checkpoint**：每 RED/GREEN/IMPROVE 一个 commit（`test:` / `feat(hil):` / `refactor:`），当前分支（新建 feature 分支）
3. **双仓同步**：Task 2/4 跨双仓改动同 commit 内完成（各自仓库），格式冻结约束（suite/script JSON 字段跨仓同步）
4. **每 Task GREEN 后 code-reviewer**；CRITICAL/HIGH 必修
5. **不触碰**：hil-core、NuGet 发布、lockstep
