# DBC 页字节位图（Byte Layout）设计

日期：2026-11-25
状态：已确认（mockup 评审通过）
Mockup：`docs/dbc-byte-layout-mockup.html`（单文件，双击即开，交互形态以它为准）

## 1. 背景与目标

DBC 页（Ctrl+2）当前消息行详情里信号只是一串格式化文本（`DbcView.xaml:73-83`），看不出信号在 8 字节载荷里的位分布。目标是选中一条消息即可看到字节分布 layout，**且不占持续 CPU**——静态布局一次渲染，零定时器、零逐帧重绘。

## 2. 现状事实（探查结论）

- DBC 页为纯 WPF DataGrid（行/列虚拟化），无任何实时刷新；实时信号值在 Signal 页，走 33ms 节流链路（`SignalViewModel/FrameIngestFlow.cs`）。
- `DbcMessageViewModel.Signals` 是 `IReadOnlyList<string>`（`DbcMessageViewModel.cs:44`），不携带原始 `Signal` 对象；`DbcViewModel` 无 `SelectedMessage` 绑定。
- 解析层在 sibling 仓库 peakcan-hil-core：`Signal` record 含 `StartBit/Length/Order/...`（`Dbc/Signal.cs:27-43`）。
- 全仓无"信号→位分布"现成函数；位序唯一事实源是 `SignalDecoder.ReadLittleEndian/ReadBigEndian`（`Dbc/SignalDecoder.cs:161-192`）。
- 可复用渲染先例：NM 时间线的 Canvas + Rectangle + Viewbox + hover Popup（`Views/TraceViewerViewNmPanel.xaml:398-588`）。

## 3. 范围

**一期（本 spec 交付）**：静态位图。选中消息 → 计算布局快照 → 一次性渲染；mux 下拉切换分支；hover 显示信号详情；下方信号表与色块颜色对应。

**二期（另行确认后实施）**：实时点亮。仅当前选中消息走 33ms 节流更新背景/数值，其余消息不动；无选中消息时节流器空转返回。

**不做**：全消息逐帧染色；ScottPlot/OxyPlot heatmap 方案（即时模式重绘，为静态图付动态成本）；FD 消息的额外展示优化（经典 CAN 与 FD 统一按实际 DLC 多行渲染，上限 512 格，不另做压缩/滚动增强）。

## 4. 组件与改动

| 位置 | 改动 |
|---|---|
| peakcan-hil-core `Dbc/SignalBitLayout.cs`（新增） | 纯函数：输入 `Message` + 当前 mux 值，输出每 bit 格归属 + 每信号色块段。不可变布局快照 |
| `DbcMessageViewModel` | `Signals` 改为携带原始 `Signal` 对象（保留现有文本格式化用于 tooltip/表） |
| `DbcViewModel` | 新增 `SelectedMessage`、`SelectedMuxValue`；选中变化时调用 `SignalBitLayout` 生成快照 |
| `Views/ByteLayoutView.xaml`（新增控件） | Canvas + Viewbox 渲染快照；hover Popup；照 NM 时间线模式 |
| `DbcView.xaml` | RowDetails 换成 ByteLayoutView + 信号表；DataGrid `SelectedItem` 绑定 |

## 5. 位映射契约（正确性核心）

位序走法与 `SignalDecoder` **逐行同构**，不另立规则：

```
对信号第 i 位（i = 0 .. Length-1）：
  absBit = StartBit + i            // 两种字节序相同
  byte   = absBit >> 3
  bit    = Intel    ? absBit & 7         // LSB0
         : Motorola ? 7 - (absBit & 7)   // MSB0，StartBit 本身即 MSB0 编号
```

注意：这不是教科书 Motorola 锯齿起始位换算——本仓库解码器语义即如此（peakcan-studio 曾栽过起始位换算的 P0）。布局必须与解码一致，哪怕与外部工具画法不同；若将来修正解码器位序，布局函数跟随同一改动。

mux 规则：复用器信号（`IsMultiplexor`）始终绘制；`IsMultiplexed` 信号仅当 `MultiplexValue == SelectedMuxValue` 时上色，其余分支不进快照。无 mux 消息忽略该维度。

越界规则：信号位超出 DLC 时越界格不入快照，信号表该信号标注"超出 DLC"。

## 6. 渲染与性能预算

- 快照计算仅在选中消息/mux 变化时执行：一条消息 ≤64 bit 格 + 数十信号，微秒级。
- 渲染为 WPF 保留模式图形：≤512 Rectangle + 每信号色块，一次性创建，之后零重绘。
- 一期无 Timer、无 Dispatcher 封送、无帧订阅。DBC 重新加载时沿用现有 `DbcLoaded` 重建路径，快照随选中项失效而丢弃。
- 二期预算上限：30Hz × 当前选中消息 ≤64 格的属性赋值，复用 `RunOnUiPost` 模式；原始帧由 `DbcDecodeBackgroundService` 顺手按消息 ID 存最新 8 字节（一次字典写）。

## 7. 错误处理

- 未选中消息 / DBC 未加载：沿用现有空态面板，不渲染位图。
- DLC=0 或信号为空：显示"无信号/无载荷"占位，不抛异常。
- mux 消息无复用器定义（数据不一致）：按非 mux 消息渲染全部信号，重叠位后写先胜并在 tooltip 标注重叠。

## 8. 测试

`SignalBitLayout` 纯函数单元测试（xUnit，Core 测试工程）：

1. Intel 跨字节（如 `0|12@1` → B0 全部 + B1 bit0-3）。
2. Motorola 跨字节（如 `55|8@0` → B6 bit0 + B7 bit7-1，与解码器语义一致）。
3. **布局↔解码一致性**：对每个用例构造特征字节序列，断言 `DecodeRaw` 提取的位与快照位序列逐一对应——两边互为判据。
4. mux 分支过滤：非当前 mux 信号不进快照；复用器始终在。
5. 起始位在字节边界、Length=1、DLC<8、信号越界 DLC。

UI 侧手动验证：打开 mockup 同款示例 DBC，逐消息对比位图与 mockup 渲染一致。

## 9. 交付顺序

1. Core `SignalBitLayout` + 单测（含解码一致性用例）。
2. VM 改造（`SelectedMessage` / 原始 Signal 携带 / mux 状态）。
3. `ByteLayoutView` + DbcView 集成，手动对拍 mockup。
