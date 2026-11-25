# DBC 页字节位图（Byte Layout）设计

日期：2026-11-25
状态：已修订（评审意见已合并；待实施）
Mockup：`docs/dbc-byte-layout-mockup.html`（单文件，双击即开）。视觉/交互参考；规则以本 spec §5 为准，与 mockup 的差异见 §5.7。

## 1. 背景与目标

DBC 页（Ctrl+2）当前消息行详情里信号只是一串格式化文本（`DbcView.xaml:73-83`），看不出信号在载荷（经典 CAN 8 字节 / CAN FD 最多 64 字节）里的位分布。目标是选中一条消息即可看到字节分布 layout，**且不占持续 CPU**——静态布局一次渲染，零定时器、零逐帧重绘。

## 2. 现状事实（探查结论）

- DBC 页为纯 WPF DataGrid（行/列虚拟化，`VirtualizationMode="Recycling"`，`DbcView.xaml:55`），无任何实时刷新；实时信号值在 Signal 页，走 33ms 节流链路（`SignalViewModel/FrameIngestFlow.cs`）。
- `DbcMessageViewModel.Signals` 是 `IReadOnlyList<string>`（`DbcMessageViewModel.cs:44`），不携带原始 `Signal` 对象；`Dlc` 是格式化字符串（`DbcMessageViewModel.cs:25`）；`DbcViewModel` 无 `SelectedMessage` 绑定，`DbcView.xaml:49` 的 DataGrid 也没有 `SelectedItem`。
- 解析层在 sibling 仓库 peakcan-hil-core：`Signal` record 含 `StartBit`（ushort，FD 上限 511）/`Length`（byte）/`Order`/`IsMultiplexor`/`IsMultiplexed`/`MultiplexValue`（`Dbc/Signal.cs:27-43`）；`Message` 含 `byte Dlc`、`Signals`、`ushort? MultiplexorSignalIndex`（`Dbc/Message.cs:22,26`）。
- 全仓无"信号→位分布"现成函数；位序唯一事实源是 `SignalDecoder.ReadLittleEndian/ReadBigEndian`（`Dbc/SignalDecoder.cs:161-192`）。
- `Message.Dlc` 是字节数（`DbcParser/ParseMessageFlow.cs:53` 直接 `ParseByte`），不需要 CAN FD DLC 编码换算；解析器未校验 ≤64，可能到 255。
- mux 解析只有单层：`M` 或 `m0..m15` 二选一（`DbcParser/ParseSignalFlow.cs:39-63`）；`m16` 及以上解析期报错；嵌套 mux（如 `m0M`）解析期报错。
- 消费 `DbcMessageViewModel.Signals` 的既有代码：搜索过滤 `DbcViewModel/SearchFlow.partial.cs:30`、RowDetails `DbcView.xaml:75-78`。
- DBC 重载走 `DbcViewModel/LoadingFlow.partial.cs:140-147`：`Messages.Clear()` + `_allMessages.Clear()` + `FilteredMessages.Clear()` 后重填；搜索过滤 `SearchFlow.partial.cs:23-35` 每次也 Clear 后重填。
- 可复用渲染先例：NM 时间线的 Canvas + Rectangle + Viewbox + hover Popup（`Views/TraceViewerViewNmPanel.xaml:398-588` 时间线，hover Popup 在 778-822）。

## 3. 范围

**一期（本 spec 交付）**：静态位图。选中消息 → 计算布局快照 → 一次性渲染；mux 下拉切换分支；hover 显示信号详情；下方信号表与色块颜色对应。

**二期（另行确认后实施）**：实时点亮。仅当前选中消息走 33ms 节流更新背景/数值，其余消息不动；无选中消息时节流器空转返回。

**不做**：全消息逐帧染色；ScottPlot/OxyPlot heatmap 方案（即时模式重绘，为静态图付动态成本）；FD 消息的额外展示优化（经典 CAN 与 FD 统一按实际 DLC 多行渲染，上限 512 格 / 64 字节，不另做压缩/滚动增强）。

## 4. 组件与改动

| 位置 | 改动 |
|---|---|
| `src/PeakCan.Host.Core/Analysis/Dbc/SignalBitLayout.cs`（新增） | 纯函数：输入 `Message` + 当前 mux 值，输出每 bit 格归属 + 每信号色块段 + 诊断。不可变布局快照。**不修改 peakcan-hil-core。** |
| `DbcMessageViewModel` | 新增 `Message? RawMessage`（携带原始 `Signal` / `Dlc`）；现有 `Signals` 字符串列表与 `Dlc` 字符串**原样保留**（搜索零改动；旧 RowDetails 在步骤 2 仍可用，步骤 3 再替换） |
| `DbcViewModel` | 新增 `SelectedMessage`、`SelectedMuxValue`、`MuxValues`、`HasMux`、`LayoutSnapshot`、`VisibleSignals`（当前 mux 分支的信号行：原始 `Signal` + `PaletteIndex` + 诊断文本，供信号表绑定）；选中 / mux 变化时重算快照；DBC 重载清空选中；搜索过滤后按 `Id + Name` 尝试恢复选中 |
| `Views/ByteLayoutView.xaml`（新增控件） | Canvas + Viewbox 渲染快照；hover Popup；照 NM 时间线模式，但 Viewbox 用 `Stretch="Uniform"` |
| `DbcView.xaml` | DataGrid 增加 `SelectedItem="{Binding SelectedMessage, Mode=TwoWay}"`；RowDetails 换成 `ByteLayoutView` + 信号表 |

接线说明：DataGrid 行的 DataContext 是 `DbcMessageViewModel`；RowDetails 模板里的 `ByteLayoutView` 通过 `RelativeSource AncestorType=DataGrid` 绑定 `DbcViewModel.LayoutSnapshot` / `VisibleSignals` / `SelectedMuxValue`，因为快照只对应当前选中行。

**落点决策（D1）**：`SignalBitLayout` 放在 `PeakCan.Host.Core`，不放 peakcan-hil-core。理由：

1. 目前只有 host 消费；peakcan-studio 钉的是 hil-core NuGet `0.22.0`，不需要这个 API。
2. host CI 对 hil-core 有 lockstep guard（`.github/workflows/ci.yml` 打包默认分支并校验包版本 == `Directory.Packages.props` 的 pin 0.23.1）；新增 hil-core 公共类型必须走 bump → pack → feed → host 改 pin，一期不值得引入这条发布链。
3. 位序语义不靠物理位置保证，而靠 §8 的布局↔解码一致性测试；host 同层已有 `NmReasonAnalyzer.Fits`（`Analysis/Nm/NmReasonAnalyzer.cs:287`）这类与解码器语义对齐的纯函数先例。

## 5. 位映射契约（正确性核心）

### 5.1 位序

位序走法与 `SignalDecoder` **逐行同构**，不另立规则：

```
对信号第 i 位（i = 0 .. Length-1；i 递增即信号位序的组装顺序）：
  absBit = StartBit + i            // 两种字节序相同
  byte   = absBit >> 3
  bit    = Intel    ? absBit & 7         // LSB0
         : Motorola ? 7 - (absBit & 7)   // MSB0，StartBit 本身即 MSB0 编号
```

渲染列固定为 bit 7 → bit 0（左→右），行按 byte 0 → byte N-1。Intel 信号按 i=0 为 LSB 组装，Motorola 信号按 i=0 为 MSB 组装（与 `ReadLittleEndian`/`ReadBigEndian` 的移位方向一致）。

注意：这不是教科书 Motorola 锯齿起始位换算——本仓库解码器语义即如此（peakcan-studio 曾栽过起始位换算的 P0）。布局必须与解码一致，哪怕与外部工具画法不同；若将来修正解码器位序，布局函数跟随同一改动，并由 §8 的一致性测试兜底。

示例：`0|12@1` → B0 bit0-7 + B1 bit0-3；`55|8@0` → B6 bit0 + B7 bit7-1。

### 5.2 行数与 DLC

行数 = `min(Message.Dlc, 64)`。`Message.Dlc` 直接作为字节数（解析器已直接存字节数，不做 `DlcToBytes` 换算）。若 `Dlc > 64`（畸形 DBC）：只渲染前 64 字节，并在位图上方显示可见警告“DLC 声明 N 字节，超出 CAN FD 上限，仅显示前 64 字节”。不静默截断。

### 5.3 信号范围与异常值

- 信号的位在 `[0, RenderedDlc*8)` 内：正常入图。
- 部分越界：界内格入图，界外格丢弃；信号表标注“部分超出 DLC（x/y 位在界内）”。
- 全部越界：不入图；信号表标注“全部超出 DLC”。
- `Length == 0`：不入图；信号表标注“长度为 0”。
- `Length > 64`：`SignalBitLayout` **不抛异常**（布局是 UI 路径，不能因一条畸形信号炸整页）；该信号不入图，信号表标注“长度 N 超过 64，不支持”。解码器 `DecodeRaw` 对 >64 抛 `ArgumentOutOfRangeException`（`SignalDecoder.cs:51-58`），这是两条路径的刻意差异，测试需分别锁定。

### 5.4 重叠

普通消息也可能重叠（mockup 示例自己就撞位：`CellVolt_2 24|16@1` 与 `BalanceFlags 39|8@0` 在 B4 bit0 重叠）。规则：

- mux 过滤后，按 `Message.Signals` **声明顺序**遍历；后声明者覆盖先声明者的格。
- 快照为每格记录 `OwnerSignal` 与 `OverlappedSignals`（被覆盖的先声明信号列表）。
- 信号表与 hover tooltip 对重叠信号标注“与 X 重叠”；不改变颜色归属（格子的颜色 = 最终 owner）。
- 该规则对所有消息生效，不再限定“mux 消息无复用器定义”这一种情况。

### 5.5 mux 规则

- 复用器信号（`IsMultiplexor`）始终绘制。
- `IsMultiplexed` 信号仅在 `MultiplexValue == SelectedMuxValue` 时入图；其余分支不进快照。
- `SelectedMuxValue` 默认取该消息所有 `IsMultiplexed` 信号 `MultiplexValue` 的**最小值**（不是固定 0），切换消息时重置为最小值；无 muxed 信号时忽略该维度，`SelectedMuxValue = null`。`MuxValues` 为去重升序列表。
- 若选中的 mux 值没有任何匹配信号：复用器照画，muxed 信号全部不画，产生 `mux_no_match` 警告并显示“mux=N 无匹配信号”（不能显示成普通空态）。
- 一期只支持单层 mux。解析器 `M`/`m0..m15` 互斥，`m16+` 与嵌套 mux 在解析期即失败，布局函数不处理这些输入。
- 与解码路径的差异：`FrameIngestFlow`/`DbcEncodeService` 在**帧里读不到 mux 值**时照常解码所有 muxed 信号；静态布局没有“读不到”的状态（用户总选中一个分支），因此不复制该兜底逻辑。

### 5.6 快照最小契约

```csharp
public sealed record BitCell(
    int ByteIndex, int BitIndex,
    string? OwnerSignal, IReadOnlyList<string> OverlappedSignals);

public sealed record SignalSegment(
    string SignalName, int StartBit, int Length, ByteOrder Order,
    int PaletteIndex, IReadOnlyList<BitCell> Cells,
    int InRangeBitCount, int OutOfRangeBitCount);

public sealed record SignalDiagnostic(string SignalName, string Code, string Text);

public sealed record LayoutWarning(string Code, string Text);

public sealed record SignalBitLayoutSnapshot(
    int DeclaredDlc,
    int RenderedDlc,
    ushort? EffectiveMuxValue,
    IReadOnlyList<BitCell> Cells,          // RenderedDlc*8 格；无归属格 OwnerSignal=null
    IReadOnlyList<SignalSegment> Segments, // mux 过滤后的全部信号，按 Message.Signals 声明顺序
    IReadOnlyList<SignalDiagnostic> Diagnostics, // 单信号级：越界 / 长度异常 / 重叠
    IReadOnlyList<LayoutWarning> Warnings);      // 消息级：DLC>64 / mux 无匹配
```

`Compute(Message message, ushort? selectedMuxValue)`：

- 消息有 muxed 信号且 `selectedMuxValue == null` 时取最小值，并写入 `EffectiveMuxValue`；无 muxed 信号时忽略入参，`EffectiveMuxValue = null`。
- `Segments` 包含 mux 过滤后的所有信号；不入图的信号 `Cells` 为空，并带对应 `Diagnostic`。
- `Diagnostic.Code` 取值：`out_of_dlc`、`partial_out_of_dlc`、`length_zero`、`length_unsupported`、`overlap`。
- `LayoutWarning.Code` 取值：`dlc_truncated`、`mux_no_match`。
- 颜色分配：按声明顺序、在 mux 过滤之后顺序取 `PaletteIndex`，过滤掉的信号不消耗索引；UI 映射到 `ByteLayoutSignal{n}` 资源，超过调色板数量循环复用，信号名仍是非颜色主标识。
- 信号表数据源：`VisibleSignals`（按 `Message.Signals` 声明顺序、同一 mux 过滤规则）；列沿用 mockup（名称 / Start|Len@Order / factor / offset / 单位 / 注释）；颜色由 `PaletteIndex` 决定；旧 `Signals` 字符串列表只服务搜索。

### 5.7 与 mockup 的差异（以实现规则为准）

| 项 | mockup 现状 | 本 spec |
|---|---|---|
| 默认 mux | 固定 0（`state.mux = 0`） | 取 muxed 信号最小值 |
| 重叠 | 后写覆盖，无提示 | 后声明覆盖 + 信号表 / tooltip 标注重叠 |
| 无匹配 mux | 无提示 | 显示“mux=N 无匹配信号” |
| DLC>64 | 无（示例均 8） | 只渲染 64 字节 + 可见警告 |

视觉 / 布局形态仍以 mockup 为准；以上四项属于规则补齐。

## 6. 渲染、生命周期与性能预算

- 快照计算仅在选中消息 / mux 变化时执行；一次计算为 O(Dlc*8 + 信号位数)，微秒到毫秒级。
- **容器回收**：`DbcView.xaml:55` 是 `VirtualizationMode="Recycling"`，`RowDetailsVisibilityMode="VisibleWhenSelected"`。滚动时 RowDetails 可能被回收并重建，因此“一次性创建”只保证**单次实现内**零重绘；滚动回来允许重建，按同一预算。
- 元素预算：经典 CAN ≤64 格；FD ≤512 格 + 位号 / 信号名，单次实现约 1.1k 可视元素。目标：快照 ≤5ms，首次实现 ≤30ms（中端机，手动测量）。位号优先合并到 `DrawingVisual`/`DrawingGroup`；若保留每格 `TextBlock`，按 1.1k 元素预算验收。
- Viewbox 用 `Stretch="Uniform"`，保持格子等比；不得照抄 NM 时间线的 `Stretch="Fill"`（`TraceViewerViewNmPanel.xaml:417`，会横向压扁）。
- 一期无 Timer、无 Dispatcher 封送、无帧订阅。
- 快照**不缓存**：选中 / mux 变化即重算；DBC 重载时 `SelectedMessage` 置 null，快照随之为空。
- 搜索过滤（`ApplyFilter` 会 Clear 后重填 `FilteredMessages`）不得让已选中的消息丢失：按 `Id + Name` 尝试恢复；找不到则清空选中。该行为一期实现并测试。
- 二期预算上限：30Hz × 当前选中消息 ≤512 格的属性赋值，复用 `RunOnUiPost` 模式；原始帧由 `DbcDecodeBackgroundService` 按消息 ID 存最新一帧的完整载荷（经典 8 字节 / FD 最多 64 字节，不是固定 8 字节）。

## 7. 错误处理（按快照结果分状态）

- 未选中消息 / DBC 未加载 / `RawMessage == null`：沿用现有空态面板，不渲染位图。
- `Dlc == 0` 或 `Signals.Count == 0`：显示“无信号/无载荷”占位，不抛异常。
- `Signals.Count > 0` 但快照无任何可绘制位（全部越界 DLC / 长度为 0 / 长度不支持）：显示“N 个信号均无可绘制位”，并按 §5.3 在信号表逐条标注，不能显示成普通“无信号”。
- mux 有定义但选中值无匹配分支：复用器照画，显示“mux=N 无匹配信号”。
- `Dlc > 64`：只渲染 64 字节 + 警告（§5.2）。
- 单信号 `Length=0/>64`、部分越界：按 §5.3 在信号表标注，不影响其他信号渲染。
- 布局函数对畸形输入不抛异常；所有降级都是**可见标注**，不静默丢弃。

## 8. 测试

`SignalBitLayout` 纯函数单元测试（xUnit，Core 测试工程）：

1. Intel 跨字节：`0|12@1` → B0 全部 + B1 bit0-3。
2. Motorola 跨字节：`55|8@0` → B6 bit0 + B7 bit7-1；列序 7→0 单独断言，防列反转。
3. **布局↔解码一致性**：特征字节序列必须是**手写常量**，禁止由布局函数生成。至少覆盖三组，均按 §5.1 手算：
   - `4|4@0`，payload byte0 = `0x5A` → `DecodeRaw` = `10`；
   - `300|16@0`，payload 长度 40，byte37 = `0x0F`，其余 0 → `0xF000`；
   - `300|8@1`，payload 长度 39，byte37 = `0xF0`，byte38 = `0x0F` → `0xFF`。
   对每个用例，断言 `SignalDecoder.DecodeRaw` 的值与按快照位序列手工组装的位模式一致（Intel i=0 为 LSB，Motorola i=0 为 MSB）。布局函数自身另用绝对 `(byte,bit)` 期望表断言，不能两边同源。
4. 重叠：普通消息两个信号撞位（如 `24|16@1` + `39|8@0` 撞 B4 bit0）→ 后声明者为 owner，先声明者在 `OverlappedSignals`，信号表标注重叠。
5. mux：非当前分支不入图；复用器始终在；默认值取最小值；无匹配分支产生 `mux_no_match` 警告；单层 mux 之外不构造输入（解析器已拒绝）。
6. 边界：Length=1、StartBit 在字节边界、Length=64、Length=0、Length>64 不抛异常且标注、DLC<8、部分越界、全部越界、DLC>64 截断并警告。
7. 颜色索引：mux 过滤后的信号按声明顺序取索引，过滤掉的信号不消耗索引；表与格一致。
8. 快照不可变性与确定性：同一 `Message` + mux 值重复计算结果相等。

UI 侧手动验证：打开 `tests/PeakCan.Host.Core.Tests/TestData/Dbc/byte-layout-demo.dbc`（与 mockup `MSGS` 等价的 ASCII DBC fixture，LF 换行，随测试数据入仓），逐消息对比位图与 mockup 渲染一致；并验证搜索过滤后选中不丢。mux 无匹配提示是防御路径（下拉只列已有分支，UI 选不到无匹配值），由 Core / VM 单测覆盖。

## 9. 交付顺序与验收

1. Core `SignalBitLayout` + 单测（§8 1-8）。验收：全部测试绿；`dotnet build` 0 warning；不修改 peakcan-hil-core。
2. VM 改造（`RawMessage` / `SelectedMessage` / `SelectedMuxValue` / 快照重算 / 重载清空 / 搜索恢复）。验收：新增 VM 测试覆盖选中、mux 切换、搜索恢复、重载清空。
3. `ByteLayoutView` + DbcView 集成。验收：手动对拍 mockup；打开 `byte-layout-demo.dbc` 的每条消息位图正确；搜索一个字后选中与位图保留；mux 无匹配提示由 Core / VM 单测覆盖（下拉只列已有分支，UI 无法触发）。

## 10. 评审决策（2026-11-25）

- D1 落点：`PeakCan.Host.Core`，不进 hil-core（§4）。
- D2 数据模型：保留 `Signals` 字符串，新增 `RawMessage`（§4）。
- D3 重叠：后声明覆盖，格记录 `OverlappedSignals`（§5.4）。
- D4 mux：默认最小值、无匹配提示、一期单层（§5.5）。
- D5 畸形输入：不抛异常，可见标注（§5.3 / §7）。
- D6 性能：按“每次实现”计预算，承认回收虚拟化会重建（§6）。
