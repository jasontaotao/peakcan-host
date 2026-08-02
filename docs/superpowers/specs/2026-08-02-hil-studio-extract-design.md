# HIL Configurator Studio 独立仓库提取 设计文档

> 日期: 2026-08-02
> 状态: 待用户 review
> 关联: `docs/superpowers/plans/2026-08-02-hil-configuration-studio-phase3.md`（Phase 3 实现 plan，已完成）

## 目标

把 peakcan-host 的 HIL Configurator Studio（DBC Browser + Test Suite Builder + ECU Simulator 三面板）从 `PeakCan.Host.App` 中提取出来，建成**独立的 private GitHub 仓库 `peakcan-studio`**，自包含、可独立构建运行。

peakcan-host 同步**移除** Studio 的 16 个 UI 文件 + 相关测试，瘦身只保留 HIL 执行引擎 + DBC View + 脚本编辑器。

## 非目标（明确不做）

- **不搬 HIL 执行引擎**：`TestSuiteEngine`、`StepExecutor/*`、`EcuSimulatorHost`、`VirtualEcu`/`StatefulVirtualEcu`、`HilRunnerService`、`HILAssertionContext`、`MatrixConfig*`、`CircularBuffer`、`Diff/*`、`Analysis/*`、`Reporting/*` 全部留在 peakcan-host。
- **不抽 NuGet 包**：不引入包版本管理。
- **不改 suite/script JSON 文件格式**：`HILJsonOptions` 的 `$type` 多态 + 现有字段保持不变。
- **不做 submodule**：两仓库完全独立，无 git 耦合。

## 架构决策（已与用户确认）

| # | 决策 | 选项 | 理由 |
|---|---|---|---|
| 1 | 仓库形态 | 独立 git 仓库（**private**） | 用户要求，不开源 |
| 2 | 模型共享 | **复制进 Studio**（自包含） | 独立构建/发布；接受格式分叉风险，用冻结约束缓解 |
| 3 | Studio 边界 | 纯配置器 | 引擎留主 App，提取面最小 |
| 4 | peakcan-host 处置 | 移除 UI，保留引擎 | 两边职责清晰，主 App 仍执行 Studio 产物 |

## 仓库结构

```
peakcan-studio/
├─ .github/                          # CI（dotnet build + test）——非目标范围，可后续补
├─ src/
│  ├─ PeakCan.Studio.Core/           # net10.0 领域模型+解析（复制自 PeakCan.Host.Core 子集 + Infrastructure 子集）
│  └─ PeakCan.Studio.App/            # net10.0-windows WPF UI（复制自 PeakCan.Host.App 子集）
├─ tests/
│  ├─ PeakCan.Studio.Core.Tests/     # 复制的 HIL 模型/解析测试
│  └─ PeakCan.Studio.App.Tests/      # 复制的 Studio VM 测试
├─ Directory.Build.props             # 从 peakcan-host 复制，适配
├─ Directory.Packages.props          # 从 peakcan-host 复制，裁剪未用包
├─ .editorconfig
├─ .gitignore
├─ README.md
└─ PeakCan.Studio.slnx
```

### Namespace 映射（全量替换）

| 源 | 目标 |
|---|---|
| `PeakCan.Host.Core` | `PeakCan.Studio.Core` |
| `PeakCan.Host.Infrastructure` | `PeakCan.Studio.Core`（Infrastructure/HIL 子集并入 Core 层） |
| `PeakCan.Host.App` | `PeakCan.Studio.App` |

> 说明：原 Infrastructure/HIL 的 loader/generators/odx 子集是纯逻辑无 WPF 依赖，并入 Studio 的 Core 层即可，不保留三层结构（YAGNI）。

## 复制边界（peakcan-host → Studio）

### A. 领域模型（→ Studio.Core）

| 源路径 | 内容 | 说明 |
|---|---|---|
| `Core/HIL/StepParams/*` | 全部 16 文件 | suite step 序列化模型，**格式真相** |
| `Core/HIL/Serialization/*` | `HILJsonOptions.cs` `ByteArrayJsonConverter.cs` | 序列化选项，`$type` 多态 |
| `Core/HIL/TestCase.cs` `TestCaseStep.cs` `TestCaseStepKind.cs` `TestCaseStepJsonConverter.cs` `TestSuite.cs` `TestSuiteConfig.cs` | 套件模型 | Studio 编辑 + 保存所需 |
| `Core/HIL/Contracts/` **子集** | `EcuResponse.cs` `EcuStateMachine.cs` `EcuStateTransition.cs` `FaultDirection.cs` `FaultRule.cs` `IEcuResponseGenerator.cs` `UdsResponseRule.cs` 等 | 脚本模型；**以编译依赖闭包为准**，plan 阶段精确补齐 |
| `Core/Dbc/*` 全量 | 21 文件（~1991 行） | DBC Browser + 套件信号下拉 |
| `Core/Uds/Odx/*` 子集 | `OdxParser` `OdxDocument` `DiagLayer` `DiagService` `DidDop` `DtcDop` `EcuJob` `RequestBasedMappers` `SecurityAccessExtractor` `PdxReader` 等 | ODX 导入链路；编译依赖闭包为准 |
| `Core/Uds/IsoTp/IsoTpLayer.cs:144` | **提取 `CanIdConfig`** 为独立文件 | 藏在大文件内，提取后复制 |
| `Core/IFileDialogService.cs` | 接口 | 对话框抽象 |
| `Infrastructure/HIL/EcuScript.cs` `EcuScriptLoader.cs` `DbcLookupKey.cs` `HeadlessDbcLookup.cs` | 脚本解析 + CanId 交换 | 约束 #1 视角转换 |
| `Infrastructure/HIL/Generators/*` | 全部 8 文件 | `EcuScriptLoader` 依赖 `BuiltInGenerators` + PluginLoader |
| `Infrastructure/HIL/Odx/*` | `OdxEcuScriptImporter.cs` `OdxToEcuScriptAdapter.cs` | ODX→EcuScript |

### B. UI + 服务（→ Studio.App）

| 源路径 | 内容 |
|---|---|
| `App/ViewModels/HilStudioViewModel.cs` + `HilStudioViewModel/DbcLoadingFlow.partial.cs` `DbcSearchFlow.partial.cs` | 主 VM |
| `App/ViewModels/HilStudioDbcMessageRow.cs` `HilStudioDbcSignalRow.cs` | DBC 行模型 |
| `App/ViewModels/TestSuiteBuilder/*`（8 文件） | 套件编辑器 VM + 模型 |
| `App/ViewModels/EcuSimulator/*`（6 文件） | ECU 模拟器 VM + Editable 模型 |
| `App/Windows/HilStudioWindow.xaml` `.cs` | 主窗口（610 行 XAML） |
| `App/Controls/EcuStatePreview.cs` `EcuResponseModeToVisibilityConverter.cs` | 控件 + converter |
| `App/Composition/Converters/NullToVisibilityConverter.cs` | 唯一 App 全局资源依赖，Studio 自带 |
| `App/Services/DbcService.cs` `DbcOptions.cs` | DBC 加载（依赖 Core，无 WPF；**复制**，peakcan-host 侧因 DBC View 保留原份） |
| `App/Services/Trace/WpfMessageBoxPrompt.cs` + 等价 `IMessageBoxPrompt` 接口 | MessageBox 确认框（Import ODX 用） |
| `App/Services/WpfFileDialogService.cs` | 文件对话框 WPF 实现 |

### C. 测试（→ Studio 仓库 tests/）

- App.Tests：`HilStudioProjectionTests` `HilStudioViewModelTests` `TestSuiteBuilder/*`（4）`EcuSimulator/*`（2）
- Core.Tests：HIL 模型/序列化/解析相关（`StepParams` `Serialization` `Contracts` `Dbc` 子集），编译依赖为准

## peakcan-host 移除清单（保留引擎）

### 删除文件（16 个 UI + 服务）

- `ViewModels/HilStudioViewModel.cs` + `DbcLoadingFlow.partial.cs` + `DbcSearchFlow.partial.cs`
- `ViewModels/HilStudioDbcMessageRow.cs` `HilStudioDbcSignalRow.cs`
- `ViewModels/TestSuiteBuilder/*`（8）
- `ViewModels/EcuSimulator/*`（6）
- `Windows/HilStudioWindow.xaml` `.cs`
- `Controls/EcuStatePreview.cs` `EcuResponseModeToVisibilityConverter.cs`
- `Composition/Converters/NullToVisibilityConverter.cs`（若仅 Studio 用，先 grep 确认）

### 代码删除

- `ViewSwitchFlow.cs`：`ShowHilStudioCommand` → `SyncEcuScriptPath` 整段（含 `OnEcuScriptPathSetExternally`/`OnEcuSimulatorPropertyChanged`/`SyncEcuScriptPath`/`_hilStudioWindow` field）；**保留** `ShowEcuScriptEditorCommand` 及 EcuScriptEditor 同步
- `AppHostBuilder.cs:305,357-358`：`HilStudioViewModel` DI 注册
- `App.xaml`：`NullToVisibilityConverter` 资源声明（若仅 Studio 用）

### 保留（不动）

- `HilViewModel` 引擎（TestSuiteEngine/StepExecutor/VirtualEcu/EcuSimulatorHost 消费链）
- `DbcViewModel` / DBC View + `DbcService`/`DbcOptions`（DBC View 继续用）
- `EcuScriptEditorViewModel` + `EcuScriptEditorWindow`（独立脚本编辑器）
- `HIL` 执行相关全部 Core/Infrastructure 类型

## 验证策略

1. **Studio 仓库**：`dotnet build` + 全测试绿（Core.Tests + App.Tests 复制集）
2. **peakcan-host**：移除后 `dotnet build` + 全测试绿（编译错误驱动清理残留引用）
3. **E2E 互操作**：Studio 保存 `ecu-script.json` + suite → 主 App `HilViewModel` 加载执行（格式冻结验证）

## 约束

### 格式冻结（关键约束）

Studio 与 peakcan-host 各持一份模型代码。**suite/script JSON 格式的模型签名（字段/类型/`$type` 名）变更必须跨仓库同步**，否则一边保存一边加载会崩。此约束写入两仓库 README。

### 其他

- `CanIdConfig` 从 `IsoTpLayer.cs:144` 提取为独立文件（不复制整个 IsoTpLayer）。
- 禁止复制引擎执行类型；若复制过程中编译发现 Studio 意外依赖引擎类型，属于**设计偏差**，停下评估而非悄悄带过。
- 两个仓库均使用 central package management（`Directory.Packages.props`）；Studio 裁剪掉 App 不需要的包（PCAN.NET、OxyPlot、Polly、OpenAI 等）。

## 风险与减险

| 风险 | 影响 | 减险 |
|---|---|---|
| `Core/Uds/Odx` 依赖链牵出额外 Core 类型 | 复制边界扩大 | plan 阶段编译依赖闭包精确锁定，禁止顺手带引擎 |
| namespace 批量替换遗漏 | 编译失败 | 全量 build 驱动，禁止手写替换 |
| `CanIdConfig` 提取动作 | 误伤 IsoTpLayer | 先提取 + 原处引用改为指向提取文件，验证再复制 |
| 双端格式分叉 | suite 互操作崩溃 | 格式冻结约束 + E2E 验证 |
| 测试断言引用已删类型 | App.Tests 失败 | 删除 Studio 测试时同步核对残留引用 |

## 任务预估

约 6 个任务：① 建仓库 + 脚手架 ② Studio.Core 复制 + namespace 改名 ③ Studio.App 复制 + 窗口/服务 ④ Studio 测试复制 + 全绿 ⑤ peakcan-host 移除 + 全绿 ⑥ E2E 互操作验证 + README 冻结约束。

---
