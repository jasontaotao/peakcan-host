# SecOc 遗留项二连：studio per-channel security 编辑 UI + HIL 多通道表达式逐通道路由

## Context

2026-09-17 跨三仓库交付已闭环：hil-core `ChannelConfig.Security` 尾参（0.22.0）、studio round-trip 保真、host per-channel 绑定。devlog Next 留下两项遗留：

1. **studio per-channel security 编辑 UI**（用户拍板先做）：round-trip 保真已够，但另一会话的 SecOcPanel 只编辑 suite **顶层** `security` 块——`channels[].security` 无法在 studio 编辑，只能手写 JSON。用户选了「先 studio 再 H2」。
2. **HIL 多通道 secoc 表达式逐通道路由**（H2，后做）：多通道下 `secocAccepted/secocRejected/secocLastReason` 只看默认通道 stats。用户选了「跟着步骤走」（不改表达式语法，按步骤 TargetChannel 自动路由）。

---

## 项 1：studio per-channel security 编辑 UI

### 现状（已核实的代码事实）

- `SecOcViewModel`（studio）持有**单个** `EditableSecOcBlock _block`；`ToModel()` 返回单个 `SecOcBlock?`（顶层）。
- 接线（`HilStudioViewModel.cs:330-333`）：
  ```csharp
  SuiteBuilder.SecurityProvider = () => SecOc.ToModel();          // 顶层块
  SuiteBuilder.SecurityErrorsProvider = () => SecOc.ValidationErrors;
  SecOc.Edited += SuiteBuilder.MarkSecurityEdited;
  SuiteBuilder.SuiteLoaded += (_, _) => SecOc.Load(SuiteBuilder.LoadedSecurity);
  ```
- `TestSuiteBuilderViewModel`（studio）的 `Channels` 是 `ObservableCollection<ChannelConfigRow>`；`ChannelConfigRow` 已有 `[ObservableProperty] SecOcBlock? _security`（round-trip 透传）。**每行 Security 就是 per-channel 块，UI 缺编辑入口。**
- `ChannelConfigRow.ToChannelConfig()` 已把 `Security` 传给 hil-core 模型；`From()` 已读回。**数据通路已通，只差 UI。**
- `SecOcPanel.xaml` 是单块 PDU DataGrid + 导入/生成攻击套件工具栏；Flyout 里每通道行只有 Name/DBC/UDS/删。

### 设计

**核心思路：把「顶层块」泛化为「通道级块」，复用现有 EditableSecOcBlock 编辑能力。**

- `SecOcViewModel` 增加：
  - `ObservableCollection<ChannelSecurityRow> ChannelBlocks`（每通道一个编辑块；「全局（默认）」为第 0 项，映射顶层 `TestSuite.Security`）。
  - 每项 = `(string ChannelName, EditableSecOcBlock Block)`；ChannelName 空 = 全局。
  - `SecurityProvider` 逻辑改为：**给 TestSuiteBuilder 返回一个「按通道取块」的委托**（channel 名 → SecOcBlock?），由 TestSuiteBuilder 在 `ToSuite()` 时对每个 `ChannelConfigRow` 调用。
- **方案取舍**：现有 `SecurityProvider = Func<SecOcBlock?>` 只支持顶层。两个子方案：
  - **B1（推荐，最小侵入）**：加新属性 `PerChannelSecurityProvider = Func<string, SecOcBlock?>?`，TestSuiteBuilder 的 `ToSuite()` 里对每个 channel 行调用 `PerChannelSecurityProvider(row.Name)` 填充 `Security`；顶层仍走 `SecurityProvider`。加载时 `ChannelSecurityProvider` 回填各通道块 + 顶层块。SecOcViewModel 维护 `Dictionary<string, EditableSecOcBlock>`（含 key="" 全局）。
  - B2（重构）：把 SecurityProvider 统一成一个 `Func<SecOcModel>`（含全局 + per-channel），改动面大，不选。
- **UI**：`SecOcPanel.xaml` 顶部加通道选择 ComboBox（「全局」「bus-a」「bus-b」…），切换时 PDU 网格绑定到对应块的 `Pdus`。数据从 `SecOcViewModel.ChannelBlocks` 来。
- `HilStudioViewModel` 接线改为双向：SuiteLoaded 时把 `LoadedSecurity`（顶层）填全局块 + 每个 channel row 的 Security 填对应块；编辑任一 → `MarkSecurityEdited`。
- `ChannelConfigRow` 无需改（已有 Security 属性）。

### 测试（TDD）

- `SecOcViewModelTests`：加载含全局 + per-channel 块的 suite → `ChannelBlocks` 正确填充；切换选中 → `ToModel`/`PerChannelSecurityProvider` 返回对应块。
- `ChannelSecurityRow` 单元测试（新增类型）。
- 端到端：构造带 `channels[].security` + 顶层 `security` 的 suite → SecOcViewModel.Load → 编辑某通道块 → TestSuiteBuilder.ToSuite → 序列化 JSON 两处块都在且值正确。

### 文件

- `src/PeakCan.Studio.App/ViewModels/SecOc/SecOcViewModel.cs`（+ChannelBlocks/PerChannelSecurityProvider）
- `src/PeakCan.Studio.App/ViewModels/SecOc/ChannelSecurityRow.cs`（新）
- `src/PeakCan.Studio.App/Views/SecOcPanel.xaml`（+通道选择 ComboBox）
- `src/PeakCan.Studio.App/ViewModels/TestSuiteBuilder/TestSuiteBuilderViewModel.cs`（ToSuite 用 PerChannelSecurityProvider）
- `src/PeakCan.Studio.App/ViewModels/HilStudioViewModel.cs`（接线调整）
- `tests/PeakCan.Studio.App.Tests/ViewModels/SecOc/SecOcViewModelTests.cs`、新增 `ChannelSecurityRowTests.cs`

---

## 项 2：HIL 多通道 secoc 表达式逐通道路由（H2，后做）

### 现状（已核实的代码事实）

- 表达式求值：`SecOcFunctionRegistry`（host）构造时持**单个** `ISecOcStats`；`StepScopeFactory.Create` 从 `ctx as ISecOcStatsSource` 拿 `SecOcStats`（默认通道）建 registry。
- `StepScope` 在 **case 级**创建一次（`TestSuiteEngine.cs:191`），if/while/Assign/插值都复用同一 scope——**scope 不含步骤级 channel 上下文**。
- `MultiChannelAssertionContext.SecOcStats` 只透出**默认通道**；每通道 `SingleChannelContext.SecOcStats` 已独立实例。
- 步骤执行：`ExecuteStepListAsync` 逐 step，step 有 `TargetChannel`（hil-core 模型，studio 已暴露 13 个步骤的 TargetChannel 编辑）。

### 设计（方案 B「跟着步骤走」）

**核心：让 secoc registry 能「按通道名」取 stats，并在步骤执行时把步骤的 TargetChannel 传下去。**

- `SecOcFunctionRegistry` 构造改为接收 `Func<string, ISecOcStats?>? statsResolver`（null = 默认通道），`TryInvoke` 时用它解析。同时保留单通道构造重载（向后兼容 Cli/trace 路径）。
- `ISecOcStatsSource` 增加能力：`ISecOcStats? SecOcStatsFor(string? channelName)`（host Core 契约，`MultiChannelAssertionContext`/`SingleChannelContext` 实现）。**注意：这是 Core 契约变化，需评估旧实现兼容（提供 DIM 默认实现返回 SecOcStats）。**
- `StepScopeFactory.Create` 增加可选参数 `Func<IAssertionContext, string?, ISecOcStats?>? secocResolver`；secoc registry 用它。默认仍走 `ctx as ISecOcStatsSource`。
- `TestSuiteEngine`：`ExecuteStepListAsync` 每个 step 执行前，若 step 有 `TargetChannel` 且与当前 scope 的 secoc registry 通道不同 → 重建 scope（`scope with { FunctionRegistry = ... }`，把 TargetChannel 传给 resolver）。控制流容器（if/while）条件仍用默认通道（用户已接受此局限）。
- 表达式求值器（hil-core `ExpressionEvaluator`/`StepScope`）**零改动**——通道解析完全在 host 侧 registry 层。

### 测试（TDD）

- `SecOcFunctionRegistryTests`：传 resolver，`secocAccepted(0x123)` 在不同 resolver 下返回不同通道 bucket。
- `MultiChannelAssertionContextTests`：`SecOcStatsFor("bus-b")` 返回 bus-b 的 stats，`SecOcStatsFor(null)` 返回默认。
- 集成：2 通道 fake channel，bus-b 上 PDU 被拒 → 步骤带 `TargetChannel="bus-b"` 的 `if secocRejected(0x123)` 为 true；不带 TargetChannel 的步骤为 false（默认通道无拒绝）。

### 文件

- `src/PeakCan.Host.Core/HIL/Contracts/ISecOcStats.cs`（+`SecOcStatsFor` DIM 默认）
- `src/PeakCan.Host.Core/HIL/Expressions/SecOcFunctionRegistry.cs`（+resolver 重载）
- `src/PeakCan.Host.Core/HIL/Expressions/StepScopeFactory.cs`（+resolver 参数）
- `src/PeakCan.Host.Core/HIL/TestSuiteEngine.cs`（step 级 scope 重建）
- `src/PeakCan.Host.Infrastructure/HIL/MultiChannelAssertionContext.cs`、`SingleChannelContext.cs`（+SecOcStatsFor）
- 测试：上述 3 个测试文件

---

## 验证

**项 1**：`dotnet test tests/PeakCan.Studio.App.Tests`（现有 1244 + 新增）全绿；手动打开带 per-channel security 的 suite → SecOc 面板可切换通道编辑 → 保存 → JSON 保留。

**项 2**：`dotnet test tests/PeakCan.Host.Core.Tests` + `PeakCan.Host.Infrastructure.Tests`（现有 1853 + 新增）全绿；集成测试验证 bus-b 拒绝驱动带 TargetChannel 的 secoc 表达式为 true。

## 顺序

先项 1（studio，单仓库），提交 + 测试后再项 2（hil-core + host，跨仓库 lockstep）。两项均按 TDD：RED → GREEN → commit。
