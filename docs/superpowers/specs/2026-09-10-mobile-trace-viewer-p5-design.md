# 移动端 Trace Viewer P5（锚点 / J1939 / 搜索跳转）设计

- 日期：2026-09-10
- 状态：已确认方向（用户 2026-09-10 逐项拍板）
- 前置：[2026-09-07-mobile-trace-viewer-design.md](2026-09-07-mobile-trace-viewer-design.md)（P0–P4 已全部落地 main）

## 1. 目标

三项功能，全部服务"现场快速确认"定位：

1. **单锚点 + 锚点信号值面板**——解决用户原话痛点："想看某个时刻的所有信号的采样点值非常困难"
2. **完整 J1939**：PGN/SA 列 + TP 重组（BAM + RTS/CTS，含截断/丢包容错）+ PGN 过滤
3. **帧搜索跳转**：按 ID 跳"首次出现 / 下一处"，播放模式 Seek、Browse 模式定位分页

## 2. 关键设计决策（用户已确认）

| # | 决策 | 结论 |
|---|------|------|
| 1 | 后台补全缓存 | **不做**。锚点/搜索只覆盖已缓存区间；未缓存区域提示。依据：能看到数据的地方必然已缓存（曲线/表格/缓存同一上游），看不到的地方设锚点也显示不出值 |
| 2 | 值面板形态 | **极简平铺列表**：消息名.信号名 + 值 + 单位，按消息名排序，CollectionView 虚拟化。无分组、无搜索、无零值折叠 |
| 3 | 锚点数量 | 单锚点（不做双锚点 Δ） |
| 4 | J1939 TP 重组 | **完整做**（用户否决了"只显示 PGN 列"的最小集） |
| 5 | Browse 模式 J1939 tab | 不做（YAGNI）；Browse 模式只做搜索跳转 |

## 3. 锚点

### 3.1 语义

- 锚点 = 全局唯一时刻 `AnchorTimestamp`（double 秒，trace 时间基准，与 `CurrentTimeText` 同源）
- 值语义 = **zero-order hold**：每个 CAN ID 取"t 之前最后一帧"，DBC 全信号解码——CANoe value-at-cursor 等价
- 与播放游标（现有 `ChartCursor`，橙色 section）并存、互相独立

### 3.2 数据来源（已验证）

- 新增 `ITraceCacheStore.GetLatestFramesBeforeAsync(traceId, timestamp)`，SQL：
  `SELECT ... FROM frames f JOIN (SELECT can_id, MAX(timestamp) mt, MAX(idx) mi FROM frames WHERE trace_id=@t AND timestamp<=@ts GROUP BY can_id) x ON f.trace_id=@t AND f.can_id=x.can_id AND f.timestamp=x.mt AND f.idx=x.mi`
  （`timestamp` 主选——ASC 流式与 BLF 2 秒重排窗口下都正确；`idx` 仅破同刻并列。单选 `MAX(idx)` 在 BLF 重排下会取到"到达更晚但时间更早"的错位帧）
- 图表缩放**不**触发缓存读写（`GetViewportRenderPoints` 只读内存 `SignalSeriesStore`）——锚点可用范围 = 已播放/已缓存范围，两者天然一致
- Browse 模式（complete trace）整库可查，锚点任意位置可设

### 3.3 交互

- **设置入口 1（图表）**：非框选模式下单击图表 → 设锚点。tap 判定：press→release 位移 <10px 且时长 <400ms（与 pan 手势区分）；坐标转换用现有 `CartesianChartEngine.ScalePixelsToData`
- **设置入口 2（表格）**：行点击已弹 `FrameDetailSheet` → 详情页加"设为锚点"按钮（构造传 `Action<double>` 回调 + 该帧 timestamp；MAUI 无内置长按手势，不引入新交互）
- **显示**：图表画第二条 `RectangularSection`（绿色，与橙色游标区分）；SeekSlider 下方加锚点行（`HasAnchor` 时可见）：时刻文本 + [信号值] + [清除]
- **[信号值]** push `AnchorValuesPage`（仿 `FrameDetailSheet` 的 Navigation push 模式），打开时查询 + 解码 + 平铺列表
- **清除语义**：DBC 变更 / 重新播放 / Stop / Seek **不清除**锚点（锚点是用户主动放置的书签，与播放状态无关；值查询总是实时执行，不受缓存增长影响）

### 3.4 无 DBC 降级

无 DBC 时面板显示"每 ID 最新帧"原始列表（ID + Data hex），不显示信号行。

## 4. J1939

### 4.1 复用策略（已验证）

- **核心零新写**：`PeakCan.Host.Core/J1939/`（`J1939Id` 位运算、`J1939TpLayer`含 `J1939TpOptions.Offline` 离线模式 + `FlushPendingSessions` 离线结算）纯 net10.0，Mobile.slnx 已引用 Host.Core
- **不搬桌面 `J1939ReassemblyService`**：它是同步批处理语义（一次性返回排序列表），移动端需要流式语义（随播放生长 + seek 重置）——语义差异大，Mobile.Core 新写 `StreamingJ1939Reassembler`，内部组合 `J1939TpLayer`。桌面端零改动
- `ReplayFrame → CanFrame` 6 行转换照抄桌面 `J1939ReassemblyService.ToCanFrame`（双侧单测钉住，不提取公共 helper——桌面类注释里的既定决策）

### 4.2 流式重组器

```csharp
// src/PeakCan.Host.Mobile.Core/Services/StreamingJ1939Reassembler.cs
public sealed class StreamingJ1939Reassembler
{
    public event Action<J1939ReassembledRow>? MessageReassembled;  // 完成/截断/丢包统一出口
    public void Ingest(ReplayFrame frame);   // 播放器线程，过滤前 tap
    public void Reset();                     // Seek/Stop/重播/换文件 → 丢弃进行中会话
    public void Flush();                     // EOF 时结算未闭合会话（Truncated/PacketLoss）
}
```

- `Ingest` 只喂扩展帧（`J1939TpLayer.ProcessFrame` 自身静默忽略非扩展/非 TP 帧，但提前过滤省去无谓调用）
- `J1939ReassembledRow`：PgnText / SaText / DaText / ModeText / LengthText / CompletedText / StatusText（Complete/Truncated/PacketLoss）——全部为预格式化字符串（FrameRow 同款缓存理由）
- `Reset()` = 丢弃旧 layer 实例 new 一个（不逐会话清理，简单且正确）

### 4.3 接线点

- `TraceSessionViewModel.OnFrameEmitted`：**过滤前** tap（ID 过滤不能截断 TP 会话）
- EOF（`PlaybackEnded` 无 Error）→ `Flush()`
- **重置触发点（四处，收束为一个 helper）**：Seek / Stop / 重新播放 / **打开或切换文件**——每次先 Flush 结算（未闭合会话以截断/丢包行留存可见）再 Reset + 清空 rows。漏掉"换文件"会导致上一个 trace 的进行中 TP 会话漂进新 session
- Seek/Stop/重新播放/打开新文件 → `Reset()` + 清空 rows

### 4.4 表格 PGN/SA 列

- `FrameRow` 加 `PgnSaText`：仅扩展帧有值，格式 `F004·11`（PGN hex 大写 + `·` + SA hex 两位）；非扩展帧空串
- 表格 Grid 加第 6 列（窄列，等宽字体 12 同现有列）

### 4.5 PGN 过滤

- `CanIdListParser`（Host.Core，桌面共享）**additive 扩展**：token 前缀 `pgn:` 解析为 PGN（hex，0x 可选，≤0x3FFFF），`CanIdParseResult` 加 `PgnAllowList` 属性。旧输入产出逐字节不变（桌面回归零风险）
- 移动端 `PassesFilter`：`idFilter` 与 `pgnFilter` 均空=全过；否则 (ID 命中) OR (扩展帧且 PGN 命中)
- Browse VM 的 `FilterText` 走同一 parser，自动获得 PGN 过滤

### 4.6 J1939 tab

- TracePage 底部 tab 栏加第三项"J1939"：rows CollectionView（PGN/SA/DA/模式/长度/完成时刻/状态 7 窄列等宽字体），随播放实时生长（marshal 到 UI 线程批量追加，沿用 100ms drain 节奏）；空态提示"无 J1939 TP 会话"

## 5. 搜索跳转

### 5.1 语义

- 输入：单个 ID（复用 `CanIdListParser` 单 token 解析；hex/dec）
- **首次**：`SELECT ... WHERE trace_id=@t AND can_id=@id ORDER BY idx ASC LIMIT 1`
- **下一处**：当前时刻之后第一帧（`timestamp > @cur`，无则提示"已到最后一处"）
- 查询范围 = 已缓存区间（同锚点 §3.2）；未命中提示"缓存范围内未找到该 ID"

### 5.2 两模式行为

| 模式 | 跳转动作 |
|---|---|
| 播放（TracePage） | `StreamingTracePlayer.SeekAsync(t)`（现有快进扫描带进度）；暂停态 seek 后停在目标帧（现有"回到进入前状态"语义） |
| Browse（BrowsePage） | `TraceBrowseViewModel.JumpTo(frameIndex)`：以目标 `idx` 为起点重置 keyset 分页（`FrameQuery(AfterIndex: idx-1)`），目标行高亮 |

### 5.3 UI

- 表格 tab 控制区加"定位"toggle（▸/▾），展开两行紧凑工具行（默认折叠，不占纵向空间）：
  - 行 A 搜索：ID Entry + [首次] + [下一处]
  - 行 B 锚点：时刻文本 + [信号值] + [清除]
- BrowsePage 顶部同样加折叠定位行（仅搜索行）

## 6. 错误处理

| 场景 | 行为 |
|---|---|
| 锚点设在未缓存区间 | [信号值] 面板显示"该区域尚未缓存"空态（查询结果空即触发，不预检） |
| 搜索 ID 解析失败 | 复用现有 `IdFilterText` 的 invalid token 提示模式 |
| 搜索未命中 | Snackbar"缓存范围内未找到该 ID" |
| J1939 会话跨 Seek | Reset 后旧会话行保留（标 Truncated 由 Flush 结算——Seek 时先 Flush 再 Reset，截断会话可见而非消失） |
| TP 数据畸形 | `ProcessFrame` 抛 `ArgumentException` → Ingest 内窄捕获 + 计数（沿用桌面 malformed 模式） |

## 7. 非目标

- 后台补全缓存（决策 1）
- 双锚点 / Δ 测量 / watch list
- 值面板分组/搜索/零值折叠（决策 2）
- Browse 模式 J1939 tab（决策 5）
- 桌面端任何改动（`CanIdListParser` additive 扩展除外）
- AI Chat / 异常扫描 / 导出（后续期次）

## 8. 已知限制（记录在案，非本期处理）

1. **Browse 模式的 PGN 过滤为内存后过滤**：`FrameQuery.CanIds` 只支持 ID 下推；PGN token 存在时不走 SQL，而是分页结果在 VM 内按谓词过滤。其后果是 keyset 分页的 `HasMore` 语义失真（一页 80 行过滤后可能只剩 3 行仍显示有下一页），PGN 命中稀疏时翻页体验下降。彻底解法是 `frames` 表加生成列 `pgn` + 索引 + `FrameQuery.PgnAllowList` 下推——schema 变更，记为后续期次候选。
2. **搜索仅支持 CAN ID（不支持 PGN）**：PGN 搜索需逐帧匹配，无法走 `idx_frames_id` 索引；输入 `pgn:` token 时明确提示"搜索仅支持 CAN ID"。
