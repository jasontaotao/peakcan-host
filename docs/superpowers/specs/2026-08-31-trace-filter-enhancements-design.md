# Trace 页过滤扩展（P1–P4）— 设计文档

- 日期：2026-08-31
- 状态：Draft（待用户 review）
- 分支：`feat/trace-filter-enhancements`（自 `feat/j1939tp-gbt27930` 切出）
- 相关先例：`ReplayViewModel.CanIdFilterText`（allow-list 过滤 + 错误直显）、`CanIdListParser`（HIL.Core.Replay 共享解析器）、`NodeConfigAssembler`（hex 解析与条件校验语义）、`J1939NodeContext.OnFrame`（J1939Id 分解生产先例）、`NodeEditorViewModel.Bind`（VM 属性注入规避 DI 循环）

---

## 1. 背景与目标

Trace 页（`TraceView` / `TraceViewModel`，实时总线监视）当前过滤能力：ID hex 前缀过滤 + 通道下拉 + hex 前缀高亮 + 仅错误帧 + 暂停。对照 Vector CANoe 的过滤体系（Measurement Setup 过滤块 / Trace 窗口视图过滤 / Logging 过滤 三层）做过差距分析，结论：

**最大架构差距是"入口过滤"**——不匹配帧在 `ReceptionFlow.PassesFilters` 处直接被丢弃（不进 `Entries`），改过滤器找不回已丢帧，CANoe 的视图层过滤不丢数据。其余高价值缺口：J1939 原生（PGN/SA/DA）过滤、黑名单语义、多规则彩色高亮、ID 统计（VM 已有 `GetMessageIdStats` 但无 UI）。

目标四项（用户已批准范围 P1–P4）：

1. **P1 视图层过滤改造**：`ICollectionView` 非破坏性过滤 + `MaxRows` 默认 5000 且可调 + 导出"可见行/全部"双命令；
2. **P2 J1939 原生过滤条**：PGN / SA / DA / ID allow-list / DBC 消息名符号 / 排除开关 / 仅错误帧 / 通道；
3. **P3 多规则彩色高亮 + payload 字节模式过滤**：谓词引擎与过滤器共用；
4. **P4 ID 统计面板**：可折叠底栏 Top-N talker，点击行设为过滤。

## 2. 范围与决策

| 决策点 | 结论（用户已批） |
|---|---|
| 统计面板形态 | **可折叠底栏**（Expander，展开时随批次刷新） |
| 高亮规则编辑 | **高亮栏就地展开**的 5 列规则小表格 |
| MaxRows | **默认 5000 + 工具栏数字输入框**（校验 100–50000） |
| 过滤/高亮持久化 | **会话内保留**，重启重置；预设库不做（YAGNI） |
| Rx/Tx 方向过滤 | **不做**——`CanFrame`/`TraceEntry` 无方向字段，接收管线无此信息 |
| CANoe 式通用列过滤器 | **不做**——高价值列已被过滤条+统计面板覆盖；WPF DataGrid 无内建列过滤 UI，谓词 DSL 泛化性价比低（论证见 §12） |
| Decoded 包含文本过滤 | **不做**（用户终审砍掉，列后续候选） |

非目标（YAGNI，明确排除）：触发捕获（pre/post trigger）；CAPL 式可编程过滤/过滤块串联管线；TP 会话重组视图（PGN 排除已覆盖"隐藏 TP.CM/TP.DT 噪声"）；信号值条件过滤；全文搜索/定位；delta-time 列；OR 组合逻辑（AND 语义足够）。

## 3. 现状资产盘点

| 现有资产 | 位置 | 复用方式 |
|---|---|---|
| `CanIdListParser.Parse` → `CanIdParseResult`（tri-state：null=无过滤/空集=全非法/白名单 + InvalidTokens） | `PeakCan.HIL.Core.Replay` | ID 列表字段原样复用（含错误 token 收集） |
| `J1939Id`（`.Pgn/.Priority/.SourceAddress/.DestinationAddress/.IsPdu1`） | `PeakCan.HIL.Core.J1939` | PGN/SA/DA 分解；`J1939NodeContext.OnFrame:103` 生产先例 |
| `DbcService.Current.Messages`（`DbcLoaded` 事件） | App Services | DBC 消息名 → ID 符号解析；统计行 DBC 名 |
| `TraceViewModel.GetMessageIdStats(topN)`（已有测试） | `HighlightFilterFlow.cs:36` | 统计面板数据源原样复用 |
| `CanId.Raw` bit31 = IDE 标志，`& 0x7FFF_FFFF` 掩码 | `SignalFlow.cs:73` 先例 | ID 匹配统一掩码后比较 |
| `NodeConfigAssembler.TryParseHexUInt32/TryParseHexByte/TryBuildCondition` | App ViewModels.Nodes | PGN/SA/DA/payload 字段 hex 语义模板（0x 可选，无前缀按 hex） |
| `NodeEditorViewModel.Bind(host, ...)` 属性注入 | App ViewModels.Nodes | `TraceViewModel` 无参 ctor 下注入 `DbcService` 的同款模式 |
| `ReplayViewModel.OnCanIdFilterTextChanged` 错误模式 | App ViewModels | 非法输入不清用户文本、错误文本直显 |
| `PassesFilters` 提取为 internal 纯方法（MTA 可测） | `ReceptionFlow.cs:93` | 谓词引擎/`AppendBatchCore` 提取的同款先例 |

## 4. 总体架构：入口过滤 → 视图层过滤

```
现状（破坏性）：
CanFrame → TraceService(200ms批次) → AppendBatchAsync ──PassesFilters──→ 丢弃
                                                          ↓ 通过
                                                       Entries → DataGrid

目标（非破坏性）：
CanFrame → TraceService(200ms批次) → AppendBatchAsync(dispatcher hop)
                                          ↓
                                     AppendBatchCore（同步核心，MTA 可直驱）
                                          ↓ 除 IsPaused 外全部入列
                              计数 → TraceEntry{+Data,+HighlightColorIndex} → Entries → trim
                                                                          ↓
                                                        ListCollectionView(.Filter=谓词)
                                                                          ↓
                                                                       DataGrid
```

- `EntriesView = new ListCollectionView(Entries)` 在 `TraceViewModel` ctor 创建（VM 是单例、UI 线程解析，view 与 collection 同线程；测试单线程 MTA 直接构造）。DataGrid 改绑 `EntriesView`。
- 过滤谓词 `TraceFilterSpec.Matches(TraceEntry)` 为纯静态方法，**过滤器与高亮规则共用**（P3 复用落点）。
- spec 任一字段变更 → 解析组装新 `TraceFilterSpec`（不可变 record）→ `EntriesView.Filter = spec.IsEmpty ? null : spec.Matches` → `EntriesView.Refresh()`。
- **`IsPaused` 保持入口级破坏性语义**（暂停=不入列仍计数，刻意保留）；暂停期间仍可改过滤检视存量行（Refresh 作用于静态数据）。
- `FilteredCount` 语义消亡 → 状态文本 `显示 {VisibleCount} / {Entries.Count}（上限 {MaxRows}）｜总收 {TotalFrameCount}`，批次末与 Refresh 后更新。

**已知限制（诚实记录）**：`ListCollectionView.Refresh()` 后视口回顶部（WPF 默认行为）。追帧时自动滚动到底不受影响；暂停检视时改过滤会跳顶。本期不做视口锚定。

## 5. 组件设计

### 5.1 `TraceFilterSpec`（新文件 `ViewModels/Trace/TraceFilterSpec.cs`）

```csharp
public sealed record TraceFilterSpec
{
    public IReadOnlySet<uint>? IdAllowList { get; init; }   // null=不过滤；值为裸 ID（已掩 IDE 位）
    public IReadOnlySet<uint>? PgnList { get; init; }        // null=不过滤；18-bit PGN（≤0x3FFFF）
    public byte? Sa { get; init; }
    public byte? Da { get; init; }
    public ChannelId? Channel { get; init; }
    public bool ErrorsOnly { get; init; }
    public bool Exclude { get; init; }                       // 整体取反
    public TracePayloadPattern? Payload { get; init; }       // null=不过滤
    public static TraceFilterSpec Empty { get; }
    public bool IsEmpty { get; }
    public bool Matches(TraceEntry entry);                   // 静态纯谓词（见 §5.2）
}

public sealed record TracePayloadPattern(int Offset, byte Mask, byte Value);
```

### 5.2 谓词语义（`Matches`，AND + 整体取反）

逐条判定（全部通过才 match=true，最后 `Exclude` 取反）：

1. **IdAllowList**：`(entry.Id.Raw & 0x7FFF_FFFF) ∈ IdAllowList`（bit31=IDE 标志，`SignalFlow.cs:73` 同款掩码）。
2. **PgnList**：仅扩展帧可匹配——`entry.Id.IsExtended` 为假 → 不匹配；否则 `new J1939Id(entry.Id.Raw).Pgn ∈ PgnList`（`J1939Id.Pgn` 已处理 PDU1 的 DA 屏蔽，`J1939NodeContext.OnFrame:103` 先例）。
3. **Sa**：仅扩展帧可匹配；`J1939Id.SourceAddress == Sa`。
4. **Da**：仅扩展帧且 PDU1 可匹配；PDU2（广播无 DA）在设了 Da 条件时**不匹配**。
5. **Channel**：`entry.Channel == Channel`（null=全部通道，现语义不变）。
6. **ErrorsOnly**：`entry.IsError` 为假 → 不匹配。
7. **Payload**：`entry.Data.Length > Offset && (entry.Data[Offset] & Mask) == Value`；帧短于 offset → 不匹配（非错误）。
8. **Exclude**：对上述合取结果整体取反。

标准帧（11-bit）在设了 PGN/SA/DA 任一条件时不匹配（取反后则通过）——spec 钉死，防"以为过滤了 J1939 其实标准帧也混进来"。

`IsEmpty`：全部条件 null/false。`Empty` 单例供初始与"清除过滤"。

### 5.3 过滤字段解析与错误模型（新文件 `ViewModels/Trace/TraceFilterParser.cs`）

| UI 字段 | 语法 | 非法处理 |
|---|---|---|
| ID 列表 | `CanIdListParser`（逗号/空格分隔，0x=hex，**无前缀=十进制**——与 Replay/Viewer 一致） | InvalidTokens 进错误文本 |
| PGN | 逗号/空格分隔 hex（0x 可选，**无前缀按 hex**，≤0x3FFFF） | 超域/非 hex 进错误文本 |
| SA / DA | 单 hex 字节（0x 可选，无前缀按 hex） | 非 hex 进错误文本 |
| DBC 消息名 | 经 `DbcService.Current.Messages` case-insensitive 查名（`EncodeDbc` 先例），命中取 `Id & 0x7FFF_FFFF` **并入** IdAllowList（与手填 ID 取并集）；同名多消息取 First | DBC 未加载 / 名字未命中 → 字段错误 |
| payload | 三小字段（offset 十进制 / mask hex / value hex），**全空=无条件，部分填=错误**（`NodeConfigAssembler.TryBuildCondition` 先例） | 部分填/非数值 → 字段错误 |

字段语法分裂的合理性：ID 列表对齐 Replay/Viewer 既有习惯（无前缀十进制）；PGN/SA/DA 对齐节点编辑器 hex 习惯。工具栏标签分别注明 `(hex)` / ID 列表不注。

**错误模型（防意外放宽）**：任一字段非法 → **整体沿用上一有效 spec**（不做"非法字段按缺席处理"——用户敲错 PGN 时期望收窄，静默放宽成全显是危险方向）+ `FilterErrorText` 红字直显首个错误 + 用户输入文本保留不清。

### 5.4 `TraceEntry` 变更

- **+`byte[] Data`**：原始载荷拷贝（入列时 `f.Data.ToArray()`）。payload 模式过滤与"规则变更后全量重算高亮"都需要——现仅存的 `DataHex` 字符串不够用。内存：5000 行 × 8B 经典帧 ≈ 40KB + 数组开销，可忽略。
- **`IsHighlighted`(bool) → `HighlightColorIndex`(int，-1=无)**：多色高亮的每行载体。INPC 语义同现 `Decoded` 模式。
- 其余字段（Timestamp/Channel/Id/Dlc/DataHex/IsError/IsFd/IsRtr/Decoded/FrameType）不动。

### 5.5 `AppendBatchCore` 提取

现 `AppendBatchAsync` 的 dispatcher lambda 体提取为 `internal void AppendBatchCore(IReadOnlyList<CanFrame> batch)`（同步、UI 线程契约）；`AppendBatchAsync` 只剩 dispatcher hop → core。测试在 MTA 直驱 core（`PassesFilters` 提取同款先例，解决 xunit 无 `Application.Current` 问题）。

core 内每帧流程：计数（`_messageCounts`/`TotalFrameCount`）→ `IsPaused` 跳过 → 建 `TraceEntry`（含 `Data` 拷贝 + `HighlightColorIndex = EvaluateHighlight(...)`）→ Add → `_pendingDecode` 注册 → trim。core 末尾：`StatsExpanded` 时 `RefreshStats()` + 状态文本更新。

DBC 解码注册全量化：`PassesFilters` 消亡后所有入列帧都注册 `_pendingDecode`——与"未设过滤"的现行行为一致，解码服务容量已验证，无新风险。

### 5.6 高亮规则（新文件 `ViewModels/Trace/HighlightRuleRowViewModel.cs`）

- 行 VM（ObservableObject）：`Enabled`(默认 true) / `ColorIndex`(0..5，默认 0) / `IdListText` / `PgnListText`。**两文本全空 = 匹配全部**（刻意：可做"其余全部底色"兜底规则）；行内文本非法 → 该行视为不匹配 + 行内红字（不全局报错）。
- 求值 `EvaluateHighlight(TraceEntry) → int`：规则自上而下，**先匹配先赢**；无命中 → -1。谓词构造：每行现场组装一个 `TraceFilterSpec`（仅 IdAllowList/PgnList 两字段）复用 `Matches`——零重复谓词代码。
- 触发：新帧入列时对新行求值；规则集/行属性变更 → 遍历 `Entries` 全量重算（5000 行 × 廉价谓词，按键级实时无压力）。
- 调色板 6 色：索引 0 复用现有 `FrameBgHighlight`，新增 `TraceHl1..TraceHl5` 五个画刷资源（加到 `FrameBg*` 所在资源字典）。RowStyle：IsError/IsFd 触发器保留，`HighlightColorIndex` 0..5 六条 DataTrigger **排最后**（高亮盖过 Error/Fd 底色，与现行优先级一致）。
- VM 命令：`AddHighlightRule` / `RemoveHighlightRule`(参数=行)；`HighlightRules` ObservableCollection；收起时摘要文本 `N 条规则生效`。

### 5.7 统计面板（新文件 `ViewModels/Trace/MessageIdStatRow.cs`）

- 行：`IdHex` / `DbcName`(string?，刷新时经 DBC 解析，无则空) / `Count` / `Percent`。
- 数据源：现有 `GetMessageIdStats(topN: 20)` 原样复用；**展开时**（`StatsExpanded`=true）每次 `AppendBatchCore` 末尾全量重建 `StatsRows`（20 行 clear+refill，不增量）；收起不刷。
- 行命令 `SetFilterToIdCommand`(参数=行)：写 `IdListText = row.IdHex`（0x 前缀形式）→ 走正常 spec 重建管线。
- `Expander.IsExpanded` 双向绑 `StatsExpanded`（默认 false=收起）；展开瞬间立即刷一次。

### 5.8 MaxRows 与状态文本

- `_maxRows` 默认 **1000 → 5000**。`MaxRowsText` 字符串字段（TwoWay）：解析成功且 ∈ [100, 50000] → 应用 `MaxRows`；非法 → 状态区红字、`MaxRows` 不变、文本回退旧值。trim 在批次末按新值生效（调低后下一批次截断）。
- `StatusText`：`显示 X / 共 Y（上限 Z）｜总收 N`，批次末与 Refresh 后重算；`VisibleCount = EntriesView.Count`。

### 5.9 导出

- `ExportCsv`（改）：快照 **可见行**（沿 `EntriesView` 枚举，显示顺序）——所见即所得。
- `ExportAllCsvCommand`（新）：快照 `Entries` 全量。
- CSV 表头/`CsvEscape`/Task.Run 写盘模式不变；默认文件名 `trace-export.csv` / `trace-export-all.csv`。

### 5.10 DbcService 注入

`TraceViewModel` 无参 ctor 是 DI 循环规避设计（类注释钉死），不破坏：新增 `internal void BindDbc(DbcService dbc)` 属性注入（`NodeEditorViewModel.Bind` 同款），`AppHostBuilder` 启动接线一次。未绑定/未加载 DBC 时降级：符号解析报"DBC 未加载"、统计 DBC 名列空——其余功能不受影响。测试经 `BindDbc` + `SetCurrentForTests` 安装罐装文档。

## 6. UI 布局（`Views/TraceView.xaml`）

```
┌ 工具栏1(过滤) ────────────────────────────────────────────────────────┐
│ [清空帧][导出 CSV][导出全部]  ID列表[____] PGN(hex)[__] SA[__] DA[__] │
│ DBC消息[可编辑ComboBox▼] 通道[▼] [✓]仅错误帧 [ ]排除 [清除过滤]        │
│ ⚠FilterErrorText(红)        显示 X / 共 Y（上限 [Z]）｜总收 N         │
├ 工具栏2(高亮) ────────────────────────────────────────────────────────┤
│ [▾] 高亮规则（N 条生效） [+ 添加]                                      │
│ 展开时: [✓启用][颜色▼6色][ID列表____][PGN____][✕]  × N 行              │
├ DataGrid（ItemsSource=EntriesView，列不变） ──────────────────────────┤
├ 底栏 Expander[▾ 报文统计] ────────────────────────────────────────────┤
│ DataGrid: ID | DBC 消息 | 计数 | 占比 | [设为过滤]                     │
└────────────────────────────────────────────────────────────────────────┘
```

- 视觉风格沿用现状（`Margin=4`、`Padding=8,2`、`TextSecondary` 状态色）；错误文本红色小字随字段右侧。
- DBC 消息 ComboBox：`ItemsSource=DbcService.Current.Messages`（经 VM 投影 `DbcMessageNames`），可编辑（TextSearch + 自由文本，提交时解析验证）。
- DataGrid 列、行高、`OnScrollChanged` 自动滚动、交替行色全部不动。

## 7. 数据流

1. **入列**：TraceService 批次 → AppendBatchAsync → dispatcher → AppendBatchCore → 计数/paused/建行（Data+高亮求值）/Add/注册解码/trim → 视图自动对新行应用谓词 → （StatsExpanded 时）刷统计 → 状态文本。
2. **过滤变更**：任一字段 setter → `TryRebuildSpec()` → 解析（§5.3）→ 非法：红字+沿用旧 spec；合法：`EntriesView.Filter` 置换 + `Refresh()` + 状态文本。
3. **高亮变更**：规则行集/属性变更 → 遍历 Entries 重算 `HighlightColorIndex`（行内非法行跳过）。
4. **DBC 加载**：`DbcLoaded` 事件 → 刷新 `DbcMessageNames` 投影 + 统计 DBC 名列（若展开）+ 若 DBC 名字段非空则重解析 spec（加载后名字变可解析）。
5. **导出**：两命令各自快照（view / Entries）→ Task.Run 写盘。

## 8. 错误处理

| 场景 | 行为 |
|---|---|
| 过滤字段非法 | 沿用上一有效 spec + `FilterErrorText` 红字 + 用户文本保留 |
| DBC 名字段在 DBC 未加载/未命中 | 同上（字段错误文本） |
| 高亮行内非法 | 该行视为不匹配 + 行内红字；不影响其他行 |
| MaxRows 非法 | 红字 + 回退旧值 |
| payload offset 超帧长 | 不匹配（非错误） |
| 标准帧遇 PGN/SA/DA 条件 | 不匹配（语义钉死，§5.2） |
| 导出 IO 异常 | 现行为不变（Debug.WriteLine + 不崩） |
| DbcService 未绑定 | 符号解析/DBC 名降级（§5.10），不抛 |

## 9. API 破坏面与既有测试改写

**移除**：`TraceViewModel.FilterText` / `HighlightText` / `FilteredCount` / `PassesFilters` / `OnHighlightTextChanged` / `ApplyHighlight`；`TraceEntry.IsHighlighted`。
**并入 spec**：`ChannelFilter`、`ShowErrorsOnly`（保留 VM 字段名与绑定，参与 spec 构建）。
**新增**：`EntriesView` / `IdListText` / `PgnText` / `SaText` / `DaText` / `DbcMessageName` / `DbcMessageNames` / `ExcludeMatch` / `PayloadOffsetText` / `PayloadMaskHex` / `PayloadValueHex` / `FilterErrorText` / `StatusText` / `MaxRowsText` / `HighlightRules` / `StatsRows` / `StatsExpanded` / `ClearFiltersCommand` / `AddHighlightRuleCommand` / `RemoveHighlightRuleCommand` / `SetFilterToIdCommand` / `ExportAllCsvCommand` / `BindDbc` / `AppendBatchCore`。
**默认值变更**：`MaxRows` 1000 → 5000。

**既有测试改写清单**：
- `TraceViewModelTests`：`PassesFilters_*` 删除（方法消亡）；带 `FilteredCount` 断言的 `AppendBatch_*` 重写为视图断言；`ApplyHighlight_*`/`HighlightText_*` 重写为规则求值；`MaxRows` 默认值断言更新。
- `TraceEntryTests`：`IsHighlighted` → `HighlightColorIndex`。
- `ExportCsv_*`：拆可见/全部两命令。
- 不受影响：`DbcDecodeBackgroundServiceTests`（`RegisterForTesting` 直注）、`TraceServiceTests`（批次管线不变）、`GetMessageIdStats` 既有钉。

## 10. 测试计划（TDD）

新文件（`tests/PeakCan.Host.App.Tests/ViewModels/`）：

1. **`TraceFilterSpecTests`**——纯谓词矩阵：ID 掩 IDE 位匹配 / PGN×(PDU1·PDU2·标准帧) / SA / DA×(PDU1 命中·PDU2 不匹配) / 通道 / 仅错误 / payload(命中·mask·帧过短) / Exclude 整体取反 / Empty 全显。
2. **`TraceFilterParsingTests`**——逐字段非法→沿用旧 spec+错误文本；PGN 超 0x3FFFF；payload 部分填；DBC 名解析（命中并入 allow-list 取并集 / 未加载 / 未命中）；`BindDbc` 未绑降级。
3. **`TraceViewModelFilterTests`**——`AppendBatchCore` MTA 直驱：全量入列（过滤不丢帧）/ 视图可见计数 / Refresh 语义 / IsPaused 入口级 / MaxRows trim 与校验 / 状态文本。
4. **`TraceHighlightRuleTests`**——规则求值（先匹配先赢 / 全空=匹配全部 / 非法行跳过 / 禁用行跳过）；规则变更全量重算；新帧入列即带色。
5. **`TraceStatsTests`**——收起不刷 / 展开即刷 / Top20 排序 / DBC 名 / `SetFilterToId` 写 ID 字段。
6. 导出两命令（可见=过滤后行序、全部=Entries）。

## 11. 验证

1. `dotnet test tests/PeakCan.Host.App.Tests` 全套（含改写组回归）。
2. `dotnet build` 零新增警告。
3. 手动（`dotnet run` + 模拟节点或回放注入）：
   - J1939 流量下设 PGN 过滤 → 改条件 → 被隐藏行**找回**（非破坏性钉）；
   - 排除开关隐藏 TP.CM/TP.DT（PGN EB00/EC00）；
   - 双规则异色高亮 + 规则编辑实时重着色；
   - 统计面板展开 → 点击行设过滤闭环；
   - 导出可见 vs 全部行数差异；MaxRows 调 100 → 下一批次截断。

## 12. 非目标论证（备查）

- **通用列过滤器不做**：ID/通道列已被过滤条覆盖且语义更强（PGN 分解+符号）；distinct-ID 发现被统计面板覆盖（带计数/占比/DBC 名，是超集）；Data 列"大于"语义模糊，工程上用字节模式；WPF DataGrid 无内建列过滤 UI + 谓词 DSL 泛化 ≈ 本期最大单项成本，残余价值却最低。
- **Decoded 包含过滤不做**：唯一未覆盖且便宜的列过滤能力，用户终审砍掉，列后续候选。
- **方向过滤不做**：帧数据模型无 Rx/Tx 标记，不硬造。
- **触发捕获/TP 重组视图/信号值过滤/搜索**：或子系统级投入（YAGNI），或被现有功能覆盖，统一列入后续候选。
