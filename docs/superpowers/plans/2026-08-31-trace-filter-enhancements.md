# Trace 页过滤扩展（P1–P4）— 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: 按本仓库既有约定（见 `2026-08-29-j1939tp-gbt27930.md`）逐任务 TDD 实施；步骤用 checkbox（`- [ ]`）语法跟踪。每一步 RED→GREEN→IMPROVE，跑通对应测试再进下一步。

**Goal:** 把 Trace 页的"入口过滤"（不匹配帧在 `PassesFilters` 处被丢弃、改过滤找不回）改造为"视图层过滤"（`ListCollectionView` 非破坏性谓词过滤），并落地 J1939 原生过滤、多规则彩色高亮、payload 字节模式过滤、ID 统计面板（spec 批准范围 P1–P4）。

**Architecture:** `TraceViewModel` 新增 `EntriesView = new ListCollectionView(Entries)`（ctor 建，同线程）。核心谓词 `TraceFilterSpec.Matches(TraceEntry)` 为纯谓词实例方法，过滤器与高亮规则共用。`AppendBatchAsync` 的 dispatcher lambda 提取为 `internal void AppendBatchCore(IReadOnlyList<CanFrame>)`（同步、MTA 可测），`PassesFilters` 消亡，除 `IsPaused` 外全部入列。新增 `TraceFilterSpec` / `TraceFilterParser` / `HighlightRuleRowViewModel` / `MessageIdStatRow` 四文件（均在 `ViewModels/TraceViewModel/`）。`TraceEntry` 增 `byte[] Data`、`IsHighlighted`→`HighlightColorIndex`。`DbcService` 经 `internal void BindDbc(DbcService)` 属性注入（规避 DI 循环）。UI 在 `TraceView.xaml` 重排工具栏 + 底栏统计 Expander。

**Tech Stack:** .NET 10（App 层 net10.0-windows x86 / WPF + CommunityToolkit.Mvvm 8.4.2）、PeakCan.HIL.Core 0.14.0（`CanFrame`/`CanId`/`ChannelId`/`J1939Id`/`BytePattern`）、xUnit + FluentAssertions。**`ListCollectionView` 命名空间 `System.Windows.Data`**（WPF）。

**Spec:** `docs/superpowers/specs/2026-08-31-trace-filter-enhancements-design.md`（已 review 修订定稿，实施以 spec 为准；本计划补充实现级要点，不与 spec 冲突）。

---

## 规格修订（本计划相对 spec 的补充说明，均为实现级澄清，非语义偏差）

spec 已经过 review 修订，无技术修正项。以下补充实现环境事实，避免实施者踩坑：

1. **`TraceEntry` 是手写 `INotifyPropertyChanged`，非 `[ObservableProperty]`**（`TraceEntry.cs` 全文手动 setter + `PropertyChanged?.Invoke`）。新增 `Data`（`init`-only 即可，无需 INPC）与 `HighlightColorIndex`（需 INPC，仿现有 `Decoded` 的 setter 模式：仅值变化才触发）。不要给 `TraceEntry` 加 `partial`/`ObservableProperty`——它是普通 class。
2. **`ListCollectionView` 在 `System.Windows.Data` 命名空间**；`TraceViewModel` 需新增 `using System.Windows.Data;`。ctor 中 `EntriesView = new ListCollectionView(Entries)`。`EntriesView.Count` 即可见数。
3. **`BytePattern` 已存在**（`Services/Nodes/NodeModel.cs:46`，`public sealed record BytePattern(int Offset, byte Mask, byte Value)`，同 App 层）。直接 `using PeakCan.Host.App.Services.Nodes;`，不新造类型。
4. **`J1939Id` 是 `readonly record struct`，ctor 不抛异常**；`DestinationAddress` 为 `byte?`（PDU2→null）。谓词里直接 `new J1939Id(entry.Id.Raw)`。
5. **`_messageCounts` key 是裸 `f.Id.Raw`**（ReceptionFlow.cs:43）；`GetMessageIdStats` 产出 `IdHex` 已是 `0x`-前缀裸 ID（HighlightFilterFlow.cs:36-50）——统计闭环无需掩码，直接复用。
6. **`DbcLoaded` 在线程池线程触发**（DbcService 契约），处理器必须 `Dispatcher` 封送回 UI 线程（spec §7.4 已钉）。
7. **错误处理本期全走 UI 文本，不新增 `[LoggerMessage]` EventId**——过滤/解析失败进 `FilterErrorText`/`MaxRowsErrorText`/行 `ErrorText`（spec §8），无日志事件。仅 `ExportCsv` 的 IO 异常保留现有 `Debug.WriteLine` 行为不变。
8. **测试隔离**：`TraceViewModelTests` 属于 `WpfAppTestCollection`（防 WPF Application 冲突）。新增测试文件若触碰 `EntriesView`（`ListCollectionView` 是 WPF 类型但非 UI 线程专用），在 MTA 直驱 `AppendBatchCore` 即可，无需 STA；仅当触碰 `Application.Current.Dispatcher` 时才需要 STA + 加入同一 Collection。
9. **`AppendBatchAsync` 的 dispatcher hop 保留**：`AppendBatchAsync` 仍 `Application.Current?.Dispatcher` 判断 + `InvokeAsync(() => AppendBatchCore(batch))`；测试走 MTA 直驱 `AppendBatchCore`。

---

## Global Constraints

- `TreatWarningsAsErrors=true`、`AnalysisMode=Recommended`：公共成员必须有 xmldoc（新文件的 record/VM 类都加）；禁止未使用字段。
- 不可变性：`TraceFilterSpec` 为 `sealed record`，`init`-only；字段变更走"组装新 spec"而非改旧对象（spec §4）。
- 线程契约：`Entries`/`EntriesView`/`StatsRows` 只在 UI 线程或 MTA 测试线程修改；`DbcLoaded` 处理器封送（§7.4）。
- 谓词求值零 I/O、零共享状态（`Matches` 纯谓词）。
- 中文注释用于业务/UI 语义（对齐 spec 与现有 `TraceViewModel` 中文注释风格）；技术 API 注释英文。

---

## Task List

### T1 — `TraceFilterSpec` + 纯谓词（新文件 + 新测试）

- [ ] 新建 `src/PeakCan.Host.App/ViewModels/TraceViewModel/TraceFilterSpec.cs`
  - [ ] `public sealed record TraceFilterSpec`（`IdAllowList`/`PgnList`/`Sa`/`Da`/`Channel`/`ErrorsOnly`/`Exclude`/`Payload`），`init`-only，字段语义见 spec §5.1
  - [ ] `Payload` 类型为既有 `BytePattern`（`using PeakCan.Host.App.Services.Nodes;`）
  - [ ] `public static TraceFilterSpec Empty { get; }`（全 null/false 单例）与 `public bool IsEmpty`
  - [ ] `public bool Matches(TraceEntry entry)` 纯谓词，逐条实现 spec §5.2 第 1–8 条，注意：
    - IdAllowList 无掩码（`CanId.Raw` 不含 IDE 位）
    - PGN/SA/DA 仅扩展帧可匹配；标准帧设任一 J1939 条件 → 不匹配
    - DA：仅 PDU1 可匹配（`J1939Id.DestinationAddress` 为 null → 不匹配）
    - Payload：`entry.Data.Length > Offset && (entry.Data[Offset] & Mask) == Value`
    - 末尾 `Exclude` 对合取结果整体取反
- [ ] 新建 `tests/PeakCan.Host.App.Tests/ViewModels/TraceFilterSpecTests.cs`
  - [ ] 纯谓词矩阵（spec §10.1）：ID 匹配 / PGN×(PDU1·PDU2·标准帧) / SA / DA×(PDU1 命中·PDU2 不匹配) / 通道 / 仅错误 / payload(命中·mask·帧过短) / Exclude 整体取反 / Empty 全显
- [ ] 跑 `TraceFilterSpecTests` 全绿

### T2 — `TraceFilterParser`（新文件 + 新测试）

- [ ] 新建 `src/PeakCan.Host.App/ViewModels/TraceViewModel/TraceFilterParser.cs`
  - [ ] `internal static class TraceFilterParser`，输入各字段文本 + 当前 DBC（`DbcDocument?`），输出 `(TraceFilterSpec? spec, string? error)`
  - [ ] ID 列表：`CanIdListParser.Parse`，无前缀十进制 / 0x=hex，用户输入不掩码；InvalidTokens → error
  - [ ] PGN：分隔符 `{',',' ','\t','\n','\r'}`，hex（0x 可选），≤0x3FFFF 校验（复用 `NodeConfigAssembler.TryParseHexUInt32` 语义，但需补 ≤0x3FFFF 域检）
  - [ ] SA/DA：`NodeConfigAssembler.TryParseHexByte` 语义；空 → null（不过滤）
  - [ ] DBC 消息名：`DbcService.Current.Messages` case-insensitive 查名（`EncodeDbc` 先例），命中取 `Id & 0x7FFF_FFFF` 并入 IdAllowList（与手填取并集）；同名取 First；未加载/未命中 → error
  - [ ] payload：offset 十进制 / mask·value hex；全空=无条件，部分填/非数值 → error；offset 不校验上界
  - [ ] 任一字段非法 → 整体返回 null spec + error（沿用上一有效 spec，见 T5 的 VM 组装）
- [ ] 新建 `tests/PeakCan.Host.App.Tests/ViewModels/TraceFilterParsingTests.cs`
  - [ ] 逐字段非法→error+null；PGN 超 0x3FFFF；payload 部分填；DBC 名（命中并入取并集 / 未加载 / 未命中）；`BindDbc` 未绑降级
- [ ] 跑全绿

### T3 — `TraceEntry` 变更 + 测试改写

- [ ] 改 `src/PeakCan.Host.App/ViewModels/TraceEntry.cs`
  - [ ] 增 `public byte[] Data { get; init; }`（`init`-only，无需 INPC）
  - [ ] `IsHighlighted`(bool) → `HighlightColorIndex`(int，-1=无)；setter 仿 `Decoded` 模式（仅值变化触发 PropertyChanged）
  - [ ] 更新类 xmldoc（v0.9.2 高亮注释改为多色索引语义）
- [ ] 改 `tests/PeakCan.Host.App.Tests/ViewModels/TraceEntryTests.cs`
  - [ ] `IsHighlighted` 相关测试改为 `HighlightColorIndex`（断言 -1 默认 / setter 触发 / 同值不触发）
- [ ] 跑全绿

### T4 — `AppendBatchCore` 提取（核心重构）

- [ ] 改 `src/PeakCan.Host.App/ViewModels/TraceViewModel/ReceptionFlow.cs`
  - [ ] 移除 `PassesFilters`（含 v0.6.0/v0.9.2/Task7 注释与 `FilterText`/`ShowErrorsOnly`/`ChannelFilter` 旧过滤逻辑）
  - [ ] dispatcher lambda 体提取为 `internal void AppendBatchCore(IReadOnlyList<CanFrame> batch)`：
    - 计数（`_messageCounts`/`TotalFrameCount`）→ `IsPaused` 跳过 → 建 `TraceEntry`（含 `Data = f.Data.ToArray()` + `HighlightColorIndex = EvaluateHighlight(...)`，T6 前先置 -1）→ Add → `_pendingDecode` 注册 → trim
    - core 末尾：`StatsExpanded` 时 `RefreshStats()` + 状态文本更新（T7 前先留钩子）
  - [ ] `AppendBatchAsync` 只剩 dispatcher hop → `AppendBatchCore`
- [ ] 改 `src/PeakCan.Host.App/ViewModels/TraceViewModel.cs`：移除 `FilterText`/`HighlightText`/`FilteredCount`/`OnHighlightTextChanged`；`Clear()` 删 `FilteredCount = 0;`；`MaxRows` 默认 1000→5000
- [ ] 删 `HighlightFilterFlow.cs` 的 `OnHighlightTextChanged`/`ApplyHighlight`（保留 `GetMessageIdStats`/`FormatHexWithSpaces`）
- [ ] 改 `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewModelTests.cs`
  - [ ] `PassesFilters_*` 删除；带 `FilteredCount` 断言的 `AppendBatch_*` 重写为视图断言（`EntriesView.Count`）；`ApplyHighlight_*`/`HighlightText_*` 移除；`MaxRows` 默认值断言更新
- [ ] 跑全绿

### T5 — 过滤 VM 状态 + `TryRebuildSpec`（绑定过滤条）

- [ ] 在 `TraceViewModel`（新 partial 或既有文件）加 VM 属性（`[ObservableProperty]`）：
  - [ ] `IdListText`/`PgnText`/`SaText`/`DaText`/`DbcMessageName`/`ExcludeMatch`/`PayloadOffsetText`/`PayloadMaskHex`/`PayloadValueHex`（string 或 bool）
  - [ ] `FilterErrorText`(string?)、`DbcMessageNames`(ObservableCollection<string> 投影)
  - [ ] `ClearFiltersCommand`：清空全部过滤字段 + 置 `EntriesView.Filter = null` + 重置 spec 为 `Empty` + 状态文本
  - [ ] `TryRebuildSpec()`：收集字段文本 → `TraceFilterParser` 解析 → 非法：`FilterErrorText` 红字 + 保留上一有效 spec（不置换 view）；合法：置换 `EntriesView.Filter` + `Refresh()` + 清 `FilterErrorText` + 状态文本
  - [ ] 各过滤字段的 `On<Field>Changed` partial 触发 `TryRebuildSpec()`（`ExcludeMatch`/`ChannelFilter`/`ShowErrorsOnly` 同）
- [ ] `EntriesView` 属性：`public ListCollectionView EntriesView { get; }`（ctor 建，`using System.Windows.Data;`）
- [ ] 新建 `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewModelFilterTests.cs`
  - [ ] `AppendBatchCore` MTA 直驱：全量入列（过滤不丢帧）/ 视图可见计数 / Refresh 语义 / IsPaused 入口级 / 状态文本
- [ ] 跑全绿

### T6 — 多规则彩色高亮（新文件 + 新测试）

- [ ] 新建 `src/PeakCan.Host.App/ViewModels/TraceViewModel/HighlightRuleRowViewModel.cs`
  - [ ] 行 VM（ObservableObject）：`Enabled`(默认 true) / `ColorIndex`(0..5，默认 0) / `IdListText` / `PgnListText` / `ErrorText`(string?)
  - [ ] 行内文本非法 → `ErrorText` + 该行视为不匹配（不全局报错）；两文本全空 = 匹配全部（兜底规则，须放最后）
- [ ] `TraceViewModel` 增 `HighlightRules`(ObservableCollection<HighlightRuleRowViewModel>) + `AddHighlightRuleCommand` / `RemoveHighlightRuleCommand` + `HighlightSummaryText`（"N 条规则生效"）
  - [ ] `EvaluateHighlight(TraceEntry) → int`：规则自上而下，先匹配先赢，无命中 → -1；每行现场组装 `TraceFilterSpec`（仅 IdAllowList/PgnList）复用 `Matches`
  - [ ] 规则集/行属性变更 → 遍历 `Entries` 全量重算 `HighlightColorIndex`（行内非法行跳过）；新帧入列即带色（T4 钩子接上）
- [ ] 新建 `tests/PeakCan.Host.App.Tests/ViewModels/TraceHighlightRuleTests.cs`
  - [ ] 规则求值（先匹配先赢 / 全空=匹配全部 / 非法行跳过 / 禁用行跳过）；规则变更全量重算；新帧入列即带色
- [ ] 跑全绿

### T7 — MaxRows + 状态文本（VM 字段）

- [ ] `TraceViewModel` 增 `MaxRowsText`(string，TwoWay) / `MaxRowsErrorText`(string?)
  - [ ] `MaxRowsText` 解析成功且 ∈ [100,50000] → 应用 `MaxRows`；非法 → `MaxRowsErrorText` 红字 + `MaxRows` 不变 + 文本回退旧值
  - [ ] `StatusText`：`显示 X / 共 Y（上限 Z）｜总收 N`，`X = EntriesView.Count`，批次末与 Refresh 后重算
- [ ] 在 `TraceViewModelFilterTests` 补 MaxRows 校验 + 状态文本用例
- [ ] 跑全绿

### T8 — 导出拆两命令 + 测试

- [ ] 改 `src/PeakCan.Host.App/ViewModels/TraceViewModel/ExportFlow.cs`
  - [ ] `ExportCsv` 改：快照**可见行**（沿 `EntriesView` 枚举，显示顺序）——所见即所得
  - [ ] 新增 `ExportAllCsvCommand`：快照 `Entries` 全量
  - [ ] 默认文件名 `trace-export.csv` / `trace-export-all.csv`；`CsvEscape`/Task.Run 写盘模式不变
- [ ] 改 `tests/PeakCan.Host.App.Tests/ViewModels/` 的 `ExportCsv_*` 测试：拆可见/全部两命令
- [ ] 跑全绿

### T9 — `BindDbc` 注入 + DI 接线

- [ ] `TraceViewModel` 增 `internal void BindDbc(DbcService dbc)`（属性注入，`NodeEditorViewModel.Bind` 同款）：
  - [ ] 存 `_dbcService`；订阅 `dbc.DbcLoaded`（处理器内部经 `Dispatcher` 封送回 UI 线程，见规格修订 6）
  - [ ] 处理器：刷新 `DbcMessageNames` 投影 + 统计 DBC 名列（若展开）+ 若 DBC 名字段非空重解析 spec
  - [ ] `DbcMessageNames` 初始投影（`_dbcService.Current?.Messages` 名称列表）
- [ ] 改 `src/PeakCan.Host.App/Composition/AppHostBuilder/ViewModelsBatch2Flow.cs`：`AddSingleton<TraceViewModel>()` → 工厂，解析 `DbcService` 后调 `BindDbc`
- [ ] 未绑定/未加载 DBC 降级：符号解析报"DBC 未加载"、统计 DBC 名列空（spec §5.10）；不抛
- [ ] `TraceFilterParsingTests` 补 `BindDbc` 未绑降级 + `DbcLoaded` 处理器线程封送用例（见 T2）
- [ ] 跑全绿

### T10 — UI 布局（`Views/TraceView.xaml` + 资源）

- [ ] `Themes/Colors.xaml`：新增 `TraceHl1..TraceHl5` 五个画刷资源（加在 `FrameBg*` 同处，索引 0 复用 `FrameBgHighlight`）
- [ ] `Views/TraceView.xaml`：
  - [ ] 工具栏 1（过滤）按 spec §6 布局：清空/导出 CSV/导出全部 + ID列表/PGN(hex)/SA/DA + DBC消息可编辑ComboBox + 通道 + 仅错误帧 + 排除 + 暂停 + 清除过滤 + 错误文本（`FilterErrorText`/`MaxRowsErrorText` 红）+ 状态文本（`StatusText`）+ MaxRows 输入
  - [ ] 工具栏 2（高亮）：`[▾] 高亮规则（N 条生效）[+ 添加]`，展开时 5 列规则小表格（启用/颜色6色/ID列表/PGN/✕）
  - [ ] DataGrid `ItemsSource` 改绑 `EntriesView`；RowStyle 增 `HighlightColorIndex` 0..5 六条 DataTrigger（排最后，高亮盖过 Error/Fd）
  - [ ] 底栏统计 `Expander`：`IsExpanded` 双向绑 `StatsExpanded`，内含 ID|DBC消息|计数|占比|[设为过滤] DataGrid
- [ ] `dotnet build` 零新增警告
- [ ] 手动布局冒烟（`dotnet run`）：工具栏重排无遮挡、高亮展开表格可编辑、统计 Expander 展开/收起

### T11 — 统计面板数据源（新文件 + 新测试，依赖 T4 钩子）

- [ ] 新建 `src/PeakCan.Host.App/ViewModels/TraceViewModel/MessageIdStatRow.cs`
  - [ ] 行 VM（ObservableObject）：`IdHex` / `DbcName`(string?) / `Count` / `Percent`
- [ ] `TraceViewModel` 增 `StatsRows`(ObservableCollection<MessageIdStatRow>) / `StatsExpanded`(bool，默认 false) / `SetFilterToIdCommand`(参数=行)
  - [ ] `RefreshStats()`：`GetMessageIdStats(topN: 20)` 原样复用 → 20 行 clear+refill；每行经 DBC 解析 `DbcName`（无则空）
  - [ ] 展开瞬间刷一次；`AppendBatchCore` 末尾若 `StatsExpanded` 则 `RefreshStats()`（接 T4 钩子）；收起不刷
  - [ ] `SetFilterToIdCommand`：**覆盖**写 `IdListText = row.IdHex`（spec §5.7 已钉）→ 走正常 `TryRebuildSpec()` 管线
- [ ] 新建 `tests/PeakCan.Host.App.Tests/ViewModels/TraceStatsTests.cs`
  - [ ] 收起不刷 / 展开即刷 / Top20 排序 / DBC 名 / `SetFilterToId` 写 ID 字段（覆盖语义）
- [ ] 跑全绿

### T12 — 既有测试全量回归 + 手动验证

- [ ] `dotnet test tests/PeakCan.Host.App.Tests` 全套（含改写组回归，spec §11.1）
- [ ] `dotnet build` 零新增警告（spec §11.2）
- [ ] 手动验证（spec §11.3）：
  - [ ] J1939 流量下设 PGN 过滤 → 改条件 → 被隐藏行**找回**（非破坏性钉）
  - [ ] 排除开关隐藏 TP.CM/TP.DT（PGN EB00/EC00）
  - [ ] 双规则异色高亮 + 规则编辑实时重着色
  - [ ] 统计面板展开 → 点击行设过滤闭环
  - [ ] 导出可见 vs 全部行数差异；MaxRows 调 100 → 下一批次截断
  - [ ] 改 spec 提交后确认计划与 spec 一致（本 plan 的规格修订即对照项）

