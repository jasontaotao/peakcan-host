# HIL Configuration Studio — 把 ECU Script 文本编辑器改造为可视化配置工具

> Date: 2026-08-01 · Status: draft（等待实现）
> 前置：本 spec 由 brainstorming 流程产出（superpowers 插件）。对应 repo：`peakcan-host`。

## Context

**为什么做这个改动**：用户想把 peakcan-host 的 ECU Script 文本编辑器（一个 TextBox + JSON 校验，`EcuScriptEditorViewModel` 243 行）升级成 HIL 测试全流程的可视化配置工具，覆盖三层：数据准备（DBC Browser）、测试逻辑（Test Suite Builder）、虚拟 ECU（ECU Simulator）。

**代码现状（evidence-first，三个 Explore agent 已核）**：

- **ECU Script 编辑器很小**：`EcuScriptEditorViewModel.cs`（243 行）= TextBox + `EcuScriptLoader.Parse` 校验 + Open/Save/SaveAs/Format。替换低风险——保持 `FilePath / IsValidEcuScript / HasUnsavedChanges / LoadInitialPath / LoadExternalAsync / Reset` 契约，AppShell↔HilViewModel 三路同步零改动。
- **三块数据模型已 80% 就位**：
  - **DBC**：`DbcService`（单例，`Current` + `DbcLoaded/LoadFailed` 事件，Volatile 跨线程）、`DbcDocument/Message/Signal/ValueTable`（immutable record）、`DbcView.xaml` + `DbcTreePickerWindow` 是现成浏览/选择范式。
  - **Test Suite**：12 种 step（SendFrame/ExpectFrame/AssertSignal/AssertDTC/InjectFault/ClearFault…）+ 完整 suite.json schema + `TestSuiteEngine` + 12 个 executor **全已存在**；**但没有构建 UI**（现在手写 JSON + 运行时勾选）。
  - **ECU Script**：`EcuStateMachine` FSM 引擎 + `states[].transitions[]` 模型 + 5 个内置动态生成器（SecurityAccessSeed/VerifyKey/DidReadout/DidWrite/ClearDtc）+ `OdxToEcuScriptAdapter`（ODX STATE-CHART→脚本）**全已存在**；编辑 UI 是纯文本。
  - **ODX→ECU 脚本是 CLI-only**：`OdxEcuScriptImporter.ImportToJson(odxPath, ecuName, requestId, responseId)` 只在 `PeakCan.Host.Cli/Program.cs:25-31` 经 `--import-odx` 调用，**WPF 层无任何 UI**。UDS 窗口的 "Load ODX…"（`UdsWindow.xaml:30-32`）走 `OdxImportService`，导入 DID/Routine/DTC 数据库 + flash 配置，是**另一条链路**，不生成 ECU 脚本。→ Phase 3 的 Import ODX 按钮补上这个 GUI 缺口。
- **全库无节点画布/拖拽控件**（grep `AllowDrop` 零命中）；UDS 窗口 Flashing tab 的"步骤 ListBox + 按 Kind 显隐属性面板"（`NullToVisibilityConverter`）是唯一已验证的可视化配置范式。
- **UI 栈**：net10.0-windows WPF，CommunityToolkit.Mvvm 8.4.2，Microsoft.Extensions.Hosting DI，`ViewSwitcher.ShowWindow` 非模态窗口模式，无第三方 UI 控件。

**已拍板的决策（用户确认）**：
1. 分阶段交付：DBC → Suite → ECU。
2. ECU 状态机 = 表单编辑 + 只读图形预览（不做交互式画布）。
3. Test Suite = 列表级拖拽（Toolbox 拖入/点击添加 + 列表内排序 + 属性面板，不做画布自由摆放）。

**验收目标**：Studio 导出的 `suite.json` / `ecu-script.json` 被现有 runtime（`TestSuiteEngine` / `StatefulVirtualEcu`）直接消费，**引擎零改动**。

---

## 整体架构（三阶段愿景）

新增非模态窗口 `HilStudioWindow`，三栏 Grid + 2 个 GridSplitter：

```
┌─ DBC Browser ──────┐ ┌─ Test Suite Builder ────────┐ ┌─ ECU Simulator ────────┐
│ Message→Signal 树   │ │ Toolbox[12种]→点击/拖入→步骤  │ │ 状态卡片+转移表(表单)    │
│ 值表 VAL_ 二级展开  │ │  ↑↓排序 + 属性面板(按Kind)   │ │  DID 字节 + 生成器下拉   │
│ 搜索/筛选           │ │  参数从 DBC 下拉选           │ │  Import ODX（补 GUI 缺口）│
│                    │ │                            │ │  只读图形预览(Canvas)   │
└────────────────────┘ └─────────────────────────────┘ └───────────────────────┘
           导出: suite.json + ecu-script.json（现有 runtime 直接消费，引擎零改动）
```

导航：AppShell View 菜单加 "HIL Configuration Studio"，走 `ViewSwitcher.ShowWindow` 缓存模式。Phase 1 只填 DBC 栏，后两栏占位。现有 ECU Script Editor 窗口不动（Phase 3 吸收）。

---

## Phase 1 — Studio 壳 + DBC Browser（本阶段执行）

### 新建文件（7 个源文件）
| 路径 | 职责 |
|---|---|
| `src/PeakCan.Host.App/Windows/HilStudioWindow.xaml` | 3 列 Grid + 2 GridSplitter；col0 内联 DBC 面板（工具栏+搜索+DataGrid+双层 RowDetails）；col2/4 占位 Border |
| `src/PeakCan.Host.App/Windows/HilStudioWindow.xaml.cs` | 极薄 code-behind：ctor 收 `HilStudioViewModel` 设 DataContext（镜像 `EcuScriptEditorWindow`） |
| `src/PeakCan.Host.App/ViewModels/HilStudioViewModel.cs` | 主 VM：集合、`[ObservableProperty]` 状态、ctor 订阅 DbcService 事件、选择同步钩子 |
| `src/PeakCan.Host.App/ViewModels/HilStudioViewModel/DbcLoadingFlow.partial.cs` | `[RelayCommand] OpenAsync` + `OnLoaded`（投影+`RunOnUi` 封送）+ `OnLoadFailed` + `RefreshFromCurrent()` |
| `src/PeakCan.Host.App/ViewModels/HilStudioViewModel/DbcSearchFlow.partial.cs` | `OnSearchTextChanged` → `ApplyFilter`（substring + OrdinalIgnoreCase，镜像 `DbcViewModel/SearchFlow.partial.cs`） |
| `src/PeakCan.Host.App/ViewModels/HilStudioDbcMessageRow.cs` | Message 行投影（plain class + `init`）：`Source`、Id/Name/Dlc/Sender/SignalCount/Comment、结构化 `Signals` |
| `src/PeakCan.Host.App/ViewModels/HilStudioDbcSignalRow.cs` | Signal 行投影 + 值表条目：`Source`、Name/BitLayout/FactorOffset/MinMax/Unit/Comment、`ValueTableName?`、`ValueTableEntries?` |

### 修改文件（5 生产 + 2 测试）
| 路径 | 修改 |
|---|---|
| `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` | `EcuScriptEditorViewModel` 注册后加 `services.AddSingleton<ViewModels.HilStudioViewModel>();`；AppShellViewModel factory lambda 加 `sp.GetRequiredService<ViewModels.HilStudioViewModel>(),` |
| `src/PeakCan.Host.App/ViewModels/AppShellViewModel.cs` | ctor 加 required `HilStudioViewModel _hilStudioViewModel`（放 `_ecuScriptEditorViewModel` 后）+ cache 字段 `private HilStudioWindow? _hilStudioWindow;` |
| `src/PeakCan.Host.App/ViewModels/AppShellViewModel/ViewSwitchFlow.cs` | `[RelayCommand] ShowHilStudio()`：`ViewSwitcher.ShowWindow(factory: () => { new HilStudioWindow(_hilStudioViewModel); _hilStudioViewModel.RefreshFromCurrent(); }, cache: ref _hilStudioWindow)` + Owner/Show/Activate（镜像 `ShowEcuScriptEditorWindow`） |
| `src/PeakCan.Host.App/AppShell.xaml` | View 菜单 "ECU Script Editor" 后加 `<MenuItem Header="HIL Configuration Studio" Command="{Binding ShowHilStudioCommand}" />` |
| `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelTests.cs` | 7 处 `new AppShellViewModel(...)` 补 `HilStudioViewModel` 实参（FakeDbcService + NullLogger + Substitute） |
| `tests/PeakCan.Host.App.Tests/ViewModels/AppShellViewModelMessageBoxPromptTests.cs` | 同（1 处） |
| `tests/PeakCan.Host.App.Tests/ViewModels/HilStudioViewModelTests.cs`（新建，可选） | 驱动 DbcService 事件验证集合重建/过滤/选择清理/值表解析（对齐 DbcViewModelTests 手法） |

### VM 要点
- 集合：`ObservableCollection<HilStudioDbcMessageRow> Messages`（全量）+ `FilteredMessages`（DataGrid 绑定）+ private `List<HilStudioDbcMessageRow> _allMessages`
- 状态：`SearchText / Status / LoadedPath / TotalMessages / TotalSignals`
- **选中钩子**：`SelectedMessage`（Phase 2/3 用）、`SelectedSignal`（Phase 3 用）；`partial void OnSelectedMessageChanged → SelectedSignal = null` 清残留
- 命令：`OpenAsync`（`_fileDialog.ShowOpenDialog("DBC files (*.dbc)|*.dbc|All files|*.*")` → `LoadAsync`，`ConfigureAwait(true)`）
- `RefreshFromCurrent()`：`_svc.Current` 非空时在 UI 线程重建投影（种子加载，镜像 `LoadInitialPath` 先例）
- 订阅 `DbcLoaded/LoadFailed` **永不退订**（单例随进程退出）；`OnLoaded` 幂等重建

### DataGrid 列设计
- 消息级（绑 `FilteredMessages`）：`Id`（"0x123"/扩展 8 hex）/ `Name` / `Dlc` / `Sender` / `SignalCount` / `Comment`(Gray/Italic)；`IsReadOnly`、`EnableRowVirtualization`、`RowDetailsVisibilityMode="VisibleWhenSelected"`
- 一层 RowDetails（绑 `Signals`，嵌套 DataGrid）：`Name` / `BitLayout`（"0|16@1+"）/ `FactorOffset` / `MinMax` / `Unit` / `ValueTableName` / `Comment`；`SelectedItem` 用 `RelativeSource AncestorType=Window` 上抛到 `VM.SelectedSignal`
- 值表 VAL_ 展开（**信号级嵌套 DataGrid 的 `RowDetailsTemplate`**，非额外再嵌一层，见约束 #10）：`Border Visibility` 绑 `ValueTableEntries` + `NullToVisibilityConverter`（null → 收拢）；`ItemsControl` 显示 `key = label`，**按 key 升序**（Entries 是字典无序）
- 值表解析在投影期：`s.ValueTableName is { } name && tables.TryGetValue(name, out var vt)` → entries；表缺失/悬空引用 → null → 自动收拢，不抛异常

### 接线顺序（依赖序）
1. AppHostBuilder 注册 VM
2. AppShellViewModel ctor + cache 字段
3. ViewSwitchFlow `ShowHilStudio`（factory 内 `RefreshFromCurrent`）
4. AppShell.xaml 菜单
5. 测试同步编译（8 处调用点）
6. （可选）HilStudioViewModelTests

### Phase 1 风险与规避
| 风险 | 规避 |
|---|---|
| DbcService 事件在 worker 线程触发，直接改 ObservableCollection 抛异常 | 严格复用 `DbcViewModel.OnLoaded` 的 `((Action)(...)).RunOnUi()` 封送；`OpenAsync` `.ConfigureAwait(true)` |
| AppShellViewModel ctor 破坏性变更（8 处调用点编译失败） | 同 commit 同步改 Build() factory + 全部测试调用点 |
| 嵌套 DataGrid（RowDetails 内 RowDetails）对大 DBC 内存/布局压力 | 外层 `EnableRowVirtualization`；行投影 plain class 无 INPC；值表字典只读共享不复制 |
| `SelectedSignal` 上抛绑定（AncestorType=Window）脆弱 | 顶层窗口内 Window 祖先唯一，安全；`OnSelectedMessageChanged` 清残留兜底 |
| 值表悬空引用（畸形 DBC） | `TryGetValue` 失败 → entries null → 二级展开收拢 |
| 窗口打开时 DBC 已加载（错过事件） | `ShowHilStudio` factory 内 `RefreshFromCurrent()` 种子加载 |
| 与主 DBC 选项卡共享单例 | 这是特性（双向同步）；`OnLoaded` 幂等 |

---

## Phase 2 — Test Suite Builder（后续阶段，概要）

- **round-trip**：`HILJsonOptions.Default` + `TestSuite/TestCase/TestCaseStep` 模型 + `TestCaseStepJsonConverter`（`$kind` 判别式）+ `StepParametersFactory`（字典→强类型），保证读写一致
- **子布局**：Case 列表 → 选中 Case 的步骤列表（Toolbox 拖入/点击添加 + ↑↓排序）→ 按 Kind 显隐属性面板（复用 Flashing tab 的 `NullToVisibilityConverter` 范式）
- **参数下拉（不手写）**：CAN ID ← `DbcDocument.Messages`；SignalName ← `"{Msg}.{Sig}"` 全名（与 `IAssertionContext.GetSignalValue` 一致）；Expected 值 ← 值表建议
- **加分项（阶段末评估）**：`DbcEncodeService.Encode(Message, Dictionary<string,double>)` 做"选报文→填信号→生成 SendFrame 的 Data 字节"
- 保存 suite.json 后接回现有 `HilViewModel` 路径（Browse 已选路径）

## Phase 3 — ECU Simulator（后续阶段，概要）

- **round-trip（约束 #1）**：加载 `EcuScriptLoader.Load(path)` → `EcuScript`，再把 `CanIds` **反交换为文件视角**进表单；保存序列化文件视角，**不再经 `Parse`**。表单模型 = `Name / CanIds(文件视角) / StateMachine / DidValues / InitialState`。
- **表单编辑**：状态卡片列表 + 转移表（FromState/SID/SubFunction/DataMask/DataPattern/Response/ToState/ResponseDelayMs）+ DID 字节编辑器（HexConverter）+ 响应类型二选一（static bytes / dynamic generator 下拉，BuiltInGenerators 5 个 + 插件 DLL）
- **Import ODX 按钮（补 CLI-only 缺口）**：`IFileDialogService` 选 `*.odx;*.pdx` → 对话框要 ECU 名 + 请求/响应 ID（默认 0x7E0/0x7E8，**文件视角**，见约束 #1）→ `OdxEcuScriptImporter.ImportToJson(path, ecuName, requestId, responseId)` 返回 JSON → 走与加载 ecu-script.json 相同的 round-trip 路径进入状态机编辑器。**必须 try/catch `InvalidOperationException` + ODX 解析异常 → 用户可见错误消息，不崩溃**（约束 #3）。参数 UI 复用 CLI 同款四个参数（`CliArgs.cs:23-26`）。
- **只读图形预览**：Canvas 画状态圆角矩形 + 条件箭头（参考 `ReplayView` 的 Canvas 绘制），编辑走表单
- **吸收编辑器**：Studio ECU 面板取代 `EcuScriptEditorWindow`；保持 `FilePath/IsValidEcuScript/HasUnsavedChanges/LoadInitialPath/LoadExternalAsync/Reset` 契约 → AppShell 三路同步零改动

---

## 设计约束（对抗审查修复，跨阶段）

以下约束是为防止"最蠢最死板的实现"在关键路径崩溃，Phase 1-3 必须遵守：

1. **canId 视角单一化（Phase 3 致命项）**：文件格式 = HIL 视角（requestId=0x7E0 / responseId=0x7E8）；`EcuScriptLoader` 解析时做 HIL↔ECU 交换；运行时内存模型（`EcuScript.CanIds`）= ECU 视角。编辑器表单**持文件视角**：加载用 `EcuScriptLoader` 后把 `CanIds` 反交换回文件视角（或直接读 JSON 的 canIds），保存序列化文件视角；**绝不把内存模型再喂 `EcuScriptLoader.Parse`**（否则每次保存双交换，ID 永久写反）。`EcuScriptLoader` 只归 runtime 用。Import ODX 写文件视角 id（CLI 已如此）→ 一致。
2. **响应序列化走强类型（Phase 3）**：response 编辑必须 round-trip 经过 `EcuResponse`（`[JsonPolymorphic]`）+ `HILJsonOptions.Default`（`$type` 判别式），**禁止手拼 JSON**；`{"$type":"static","data":[...]}` / `{"$type":"dynamic","generatorName":...}` 由序列化器产出。
3. **Import ODX 异常必须处理（Phase 3）**：`OdxEcuScriptImporter.ImportToJson` 在无 UDS 服务时 throw `InvalidOperationException`（`OdxEcuScriptImporter.cs:23-24`），ODX 解析也可抛文件/XML 异常 → UI 必须 try/catch + 用户可见错误消息，不崩溃。
4. **JSON↔表单单向（Phase 3）**：表单是 source of truth；加载 `rules` 格式自动迁移为 `states`（格式变化需文档化）；JSON 视图只做预览/粘贴导入，**不做双向实时同步**（否则破坏用户手写格式）。
5. **选中契约（Phase 1）**：`SelectedMessage/SelectedSignal` 在过滤重建或重新加载时会变 null（DataGrid 丢选中 → 双向绑定写回 null）；Phase 2/3 消费时必须接受 null 输入。Phase 1 不保证跨过滤保留选中。
6. **搜索语义漂移（Phase 1）**：结构化 `Signal.Name` 匹配**不等于**现有 `DbcViewModel` 的格式化串匹配（`SearchFlow.partial.cs:19-33`，格式化串含 bit/scale 文本，如 "0.1"、"16@"）。这是有意改进，需在 release note 标注，不得声称"行为等价"。
7. **批量重建与性能边界（Phase 1）**：`ApplyFilter` 沿用 DbcViewModel 的全量 Clear+重建先例；但对超大 DBC，`OnLoaded` 投影 O(N×S) + 搜索重建的 UI 线程代价要认，必要时加 `DbcOptions.MaxMessageCount` 上限防御或批处理。
8. **依赖路径点名（Phase 1）**：`RunOnUi` = `src/PeakCan.Host.App/ViewModels/DispatcherExtensions.cs:61`（dead-dispatcher 时回退内联执行）；`IFileDialogService` = `src/PeakCan.Host.Core/IFileDialogService.cs:10`；`NullToVisibilityConverter` = App.xaml 全局注册。不得自行重写。
9. **命名消歧（Phase 1）**：`SignalCount`=单消息信号数；`TotalSignals`=全库信号数；投影行 `Source` 暴露 Core record（`Message`/`Signal`）供 Phase 2/3 结构化消费——区别于 `DbcMessageViewModel` 的格式化 `Signals`（`IReadOnlyList<string>`）。
10. **值表容器（Phase 1）**：值表 VAL_ 展开是**信号级嵌套 DataGrid 的 `RowDetailsTemplate`**（Border Visibility 绑 `ValueTableEntries` + `NullToVisibilityConverter`），不是外层再加一层；禁止三层嵌套 DataGrid。

## Verification

### Phase 1（本阶段验收）
1. `dotnet build` 通过（含测试工程编译），运行 app 无 DI 解析异常
2. 手动清单：
   - 窗口开合：View 菜单打开非模态窗口；重复点 → 复用缓存并 Activate；关闭重开 → 新实例
   - 加载 DBC：主窗先加载 .dbc → 打开 Studio 自动显示（RefreshFromCurrent）；或在 Studio 内 Open DBC
   - 双向同步：主窗 DBC 选项卡重新加载 → Studio 自动刷新（共享单例）
   - 浏览：选消息 → RowDetails 展开信号表；选带 VAL_ 信号 → 二级展开值表 key=label
   - 搜索：消息名/发送方/信号名 substring 实时过滤；清空恢复
   - 选中暴露：`SelectedMessage`/`SelectedSignal` 非空
   - 占位与分栏：col2/4 显示 "(Phase 2)"/"(Phase 3)"；拖 GridSplitter 正常
   - 异常路径：损坏 DBC → Status 显示 `FAIL: <Code> <Message>`，不崩溃
3. 可选单测：`HilStudioViewModelTests` 驱动事件验证

### Phase 2/3（后续验收）
- **round-trip 单测**：编辑→序列化→现有 loader 反序列化→断言无数据丢失；用现有 fixtures（`tests/.../Fixtures/suite.json`、`Fixtures/e2e-ecu/bms_sim.json`）回归
- 手动：Studio 构建 case 存 suite.json → HIL view 以 VirtualEcu 模式跑通（现有引擎消费）
