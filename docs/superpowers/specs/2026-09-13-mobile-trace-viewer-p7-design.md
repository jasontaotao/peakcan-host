# 移动端 Trace Viewer P7（缓存查询增强：search_signal_trace + PGN 下推）设计

> 日期：2026-09-13
> 状态：草案 v2（v1 经架构/产品双视角 review 修订：§4 补空集 tri-state 语义（v1 有一处回归级缺陷）、§5 补解码 API 与消息解析精确语义、§6 补锚点错误行、§3 补兼容性；验收补 Fake 更新与 UI 文案项。实施计划待本 spec 确认后另出）
> 前置：P6（AI Chat）已合并 main（7146f21f）。
> 参考：[2026-09-10-mobile-trace-viewer-p5-design.md](2026-09-10-mobile-trace-viewer-p5-design.md) §8.1、[2026-09-10-mobile-trace-viewer-p6-design.md](2026-09-10-mobile-trace-viewer-p6-design.md) §8.1。

## 1. 目标

P5/P6 各留下一个被记录在案的候选，且都落在同一个 SQLite 回放缓存查询面上，合为一期：

1. **`search_signal_trace` 工具**（P6 §8.1）：AI 按信号取缓存内时间序列。P6 从桌面 19 个工具移植了 7 个，唯一砍掉的就是它——当时需要新的缓存查询。产品价值：AI 从"锚点单点快照"升级到"窗口趋势/跳变分析"。
2. **PGN 过滤 SQL 下推**（P5 §8.1）：Browse 模式 PGN 过滤目前是内存后过滤（`TraceBrowseViewModel.ApplyMemoryFilter`），keyset 分页的 `HasMore` 语义失真——一页 80 行内存过滤后可能只剩 3 行却仍显示有下一页，PGN 命中稀疏时翻页体验崩坏。彻底解法：`frames` 表加 `pgn` 生成列 + 索引 + `FrameQuery` 下推。

## 2. 关键设计决策

| # | 决策 | 说明 |
|---|---|---|
| 1 | `pgn` 为 **VIRTUAL 生成列** | `ALTER TABLE ADD COLUMN` 只支持 VIRTUAL 生成列（SQLite 限制），且 VIRTUAL + 索引即可把计算值物化进索引，读路径零重复计算；写入路径不加成本。新库 DDL 与旧库迁移统一用 VIRTUAL |
| 2 | 迁移走幂等 ALTER | `TraceCacheStore.InitializeAsync` 已是幂等 DDL（`IF NOT EXISTS`）。生成列对旧库用 `PRAGMA table_info(frames)` 检查后 `ALTER TABLE frames ADD COLUMN pgn …`，同样幂等。前置：确认 `Microsoft.Data.Sqlite` 打包的 e_sqlite3 ≥ 3.31（生成列最低版本），plan 阶段列为首个验证 task |
| 3 | SQL 表达式与 `J1939Id.Pgn` 逐位对拍 | 生成列表达式按 `J1939Id.Pgn` 公式展开（R/EDP<<17 \| DP<<16 \| PF<<8 \| PDU2 才含 PS），先 `& 0x1FFFFFFF` 剥位。测试用随机 ID fuzz 对拍 C# 实现 |
| 4 | **tri-state 下推纪律：null = 无过滤，空集 = 全拒** | 现有 `CanIdListParser` 是 tri-state（null=未过滤；**空集=all-invalid 全拒**），而 store 现有 `CanIds is { Count: > 0 }` 判断把空集等同于无过滤——今天靠 Browse 内存过滤路径兜住，删掉内存路径后若照搬会把 all-invalid 过滤变成"显示全部帧"（回归）。下推后 store 必须区分：null → 不生成子句；空集 → 生成恒假子句（`AND 0`）。两个过滤集合同时为空 → 空页，与 C# `PassesBrowseFilter` 语义逐字一致 |
| 5 | `FrameQuery` 加 `PgnAllowList`，Browse 不再放弃 ID 下推 | 现 `QueryCanIds` 在存在 PGN 过滤时把 ID 下推一并禁掉（连带退化）。下推后两者独立生效；同时存在时用 OR 谓词 `AND (can_id IN (…) OR pgn IN (…))`（OR 语义与现 `PassesBrowseFilter` 逐字一致，空集=恒假分支） |
| 6 | OR 组合查询计划实测，保留退路 | 双集合 OR + `ORDER BY idx` 有退化到 TEMP B-TREE 排序的风险。plan 阶段用 `EXPLAIN QUERY PLAN` 实测：若 multi-index OR 不可用，退路为"双集合并存（罕见组合）时单条 SQL 内联 OR 子查询/UNION"，禁止退回整页内存后过滤（那会带回 HasMore 失真） |
| 7 | `search_signal_trace` 参数用 `{message, signal}` 命名 | 桌面用 `0x182.SignalName` hex key（源自桌面多 source 体系）；移动端工具集统一 `{message, signal}`（对齐 `get_dbc_signal`），消息名大小写不敏感解析（对齐 `search_signals`） |
| 8 | 解码用 `SignalDecoder.Decode`（double），**不用** `DbcCatalog.Decode` | `DbcCatalog.Decode` 返回 `SignalDisplay.Value` 是**格式化字符串**（enum 文本或 `0`/`0.###`，≤3 位小数）——供气泡展示，做时间序列会毁掉数值精度。工具内直接 `SignalDecoder.Decode(payload, signal)` 取 double 原值；enum 信号只出数值，标签由 AI 经 `get_dbc_signal` 查（记 §8 限制） |
| 9 | 多路复用信号跳过选择子不匹配的帧 | 复用 `DbcCatalog.IsSignalActive`（internal，同程序集可用），与锚点面板解码语义一致：multiplexed 信号只统计 selector 命中的帧 |
| 10 | 消息解析遵循 `DbcCatalog` 位语义 | DBC `message.Id` 可能带 bit31 IDE 约定位：bit31=1 → 扩展帧且 canId=`Id & 0x7FFFFFFF`。解析结果为 `(canId, isExtended)` 二元组；缓存 `frames.can_id` 存 29 位裸值（`GetAnchorValuesTool` 直接拿 `CachedFrame.CanId/IsExtended` 喂 `DbcCatalog.Decode` 佐证），等值查询直接用 |
| 11 | 复用 `Host.Core` 的 `LttbDownsampler`（零改动） | 纯静态函数、net10.0，Mobile.Core 已引用 Host.Core。降采样 + 统计（min/max/mean/首末值）在工具内组合 |
| 12 | 原始行上限 20 000 + `truncated` 标记 | 工具仍在 UI 线程执行（P6.1 语义：工具触碰 session 状态）。20k 行 查询+解码+LTTB 在手机上约百毫秒级，超出即截断并如实告知 AI（截断样本仍具代表性——LTTB 按序采样） |
| 13 | 缓存不完整返回 `warning` 不报错 | `traces.complete=0` 且窗口上界越过已缓存 `duration` 时，响应附 `warning: "cache incomplete"`，AI 可据此提示用户；与 `get_anchor_values` 的"未缓存区间提示"语义一致 |
| 14 | 工具总数 7→8，系统提示词同步 | `BuildSystemMessage` 的"可用工具（7 个）"改 8 个。桌面写 19 个、各自独立，不共享文本（P6 §8.5 语义不变） |

## 3. Schema：`pgn` 生成列

```sql
-- 新库：并入 CREATE TABLE frames（…, pgn INTEGER GENERATED ALWAYS AS (…) VIRTUAL）
-- 旧库：InitializeAsync 内 PRAGMA 检查后 ALTER TABLE frames ADD COLUMN pgn INTEGER GENERATED ALWAYS AS (…) VIRTUAL
-- 索引（新旧库统一 IF NOT EXISTS 幂等补齐）：
CREATE INDEX IF NOT EXISTS idx_frames_pgn_idx ON frames(trace_id, pgn, idx);

-- 表达式（与 J1939Id.Pgn 逐位一致；非扩展帧 CASE 无 ELSE → NULL，天然不被任何 IN 命中）：
pgn = CASE WHEN is_extended = 1 THEN
  ((((can_id & 536870911) >> 25) & 1) << 17)
  | ((((can_id & 536870911) >> 24) & 1) << 16)
  | ((((can_id & 536870911) >> 16) & 255) << 8)
  | (CASE WHEN ((can_id & 536870911) >> 16) & 255 < 240
          THEN 0 ELSE (can_id & 536870911) >> 8 & 255 END)
END
```

- 非 J1939 语义的扩展帧（任意 29 位 ID）同样计算——PGN 过滤本就是 ID 空间上的位运算视图，与 `PassesBrowseFilter` 现行为一致。
- 生成列迁移只加列不动数据，`AppendFramesAsync` 的 INSERT 显式列清单不变（生成列禁止显式赋值，天然兼容）。
- **兼容性**：schema 变更纯 additive。旧版 app 读迁移后的库不受影响（所有 SELECT/INSERT 均显式列清单）；新版 app 打开旧库时在 `InitializeAsync` 幂等迁移。可安全回滚。

## 4. `FrameQuery` 扩展与 Browse 行为

```csharp
public sealed record FrameQuery(
    long? AfterIndex = null,
    long? BeforeIndex = null,
    IReadOnlySet<uint>? CanIds = null,
    IReadOnlySet<uint>? PgnAllowList = null,   // 新增；tri-state 同 CanIds
    int Limit = 80);
```

- `TraceCacheStore.GetFramesAsync`：按决策 4 的 tri-state 纪律生成子句——null → 不生成；空集 → `AND 0`；非空 → `AND can_id IN (…)` / `AND pgn IN (…)`。双集合均非 null 时合并为 `AND (can_id IN (…) OR pgn IN (…))`（空集分支恒假）。
- `TraceBrowseViewModel`：删除 `QueryCanIds` 的"有 PGN 过滤则 ID 置 null"分支与整个 `ApplyMemoryFilter`/`PassesBrowseFilter` 内存路径，两个集合直接进 `FrameQuery`。语义回归由测试锁定（见 §9 对拍项）。
- `HasMore` 语义归正：分页计数只统计命中行，spec §1 的"80→3 行仍显示有下一页"问题随之消失。
- **UI 文案（一行 XAML，随本节顺带修）**：`BrowsePage.xaml` 过滤框 Placeholder 现为"ID 过滤 (hex, 逗号分隔)"——P5 加 `pgn:` token 时漏改提示，用户无从发现该能力。改为"ID/PGN 过滤 (0x123, pgn:F004)"。搜索框"搜索 CAN ID (hex)"不动（§8.4 搜索仍限 CAN ID）。

## 5. `search_signal_trace` 工具

### 5.1 定义

| 项 | 值 |
|---|---|
| 名称 | `search_signal_trace`（与桌面同名，AI 可见语义对齐） |
| 输入 | `signals`: `[{message, signal}]`（1–8 项）；`t_start`/`t_end`（秒，默认 0 → `DurationSeconds ?? 缓存 traces.duration`）；`window_ref`: `absolute`(默认) \| `anchor`（移动端单锚点，无桌面 green/blue 之分）；`max_points`: 10–1000，默认 200 |
| 输出 | `signals[]`: 每信号 `{message, signal, unit, sample_count, stats{min,max,mean,first,last}, samples[{t,t_label,v}], t_range}`；`backend_info{raw_frame_count, truncated, downsample_method:"LTTB", window_ref, t_start, t_end}`；可选 `warning` |
| 依赖 | 新 `ITraceCacheStore.GetFramesForCanIdAsync` + `IMobileChatToolContext` 新方法 + `SignalDecoder` + `LttbDownsampler` |

### 5.2 执行流程

1. DBC 未加载 → `{"error":"未加载 DBC"}`；`TraceId` 为 null（无缓存 session）→ `{"error":"no cache"}`。
2. 解析每个 `{message, signal}`：消息名大小写不敏感遍历 `DbcCatalog.Document.Messages`，按决策 10 的位语义得到 `(canId, isExtended)`；找不到 → 该条目返回 `{..., "error":"message not found"}`（单条失败不影响其余，对齐桌面 per-key error 语义）。
3. 每个唯一 canId 调一次 `GetFramesForCanIdAsync(canId, tStart, tEnd)`：`WHERE trace_id=? AND can_id=? AND timestamp BETWEEN ? AND ? ORDER BY timestamp ASC, idx ASC LIMIT 20001`（走 `idx_frames_cid_ts`），`HasMore` → `truncated=true`。
4. 逐帧解码：multiplexed 消息先 `DbcCatalog.IsSignalActive` 过滤选择子不匹配的帧，再 `SignalDecoder.Decode(payload, signal)` 取 **double 物理值**（决策 8）；`(t, v)` 序列按 t 升序（查询已排序）。窗口内命中 0 帧 → `{"error":"no frames in window"}`。
5. `LttbDownsampler.Downsample(points, maxPoints)` + 统计（min/max/mean/first/last）；样本 `t` 保留 4 位小数、`t_label` 用 `TsLabel`（F4，系统提示词时间约定）、`v` 保留 4 位。
6. `window_ref=anchor` 时窗口整体平移 `AnchorTimestamp`；未设锚点 → `{"error":"no anchor set","hint":"请先设置锚点，或使用 absolute 模式"}`（对齐桌面 hint 风格）。
7. `complete=0` 且 `t_end > traces.duration` → 附 `warning:"cache incomplete"`。

### 5.3 接口新增

```csharp
// ITraceCacheStore
Task<FramePage> GetFramesForCanIdAsync(long traceId, uint canId,
    double? tStart, double? tEnd, int limit = 20000, CancellationToken ct = default);

// IMobileChatToolContext（实现于 TraceSessionViewModel.Chat.cs，委托 store；无缓存 session 返回空列表）
Task<IReadOnlyList<CachedFrame>> GetFramesForCanIdAsync(uint canId,
    double? tStart, double? tEnd, CancellationToken ct);
```

复用 `FramePage`（`HasMore` 即 `truncated`），不新增结果类型。**接口影响面**：`IMobileChatToolContext` 新增方法会破坏全部实现方——`TraceSessionViewModel`（显式接口实现）与测试替身 `FakeContext` 必须同步更新；`ITraceCacheStore` 的 `FakeStore`/`ThrowingStore` 测试替身同理。

## 6. 错误处理（沿用 P6 §7 约定：返回 `{"error":"..."}` 不抛异常）

| 场景 | 行为 |
|---|---|
| DBC 未加载 / 无缓存 session | `{"error":"未加载 DBC"}` / `{"error":"no cache"}` |
| `signals` 缺失 | `{"error":"missing 'signals'"}`；超 8 项截取前 8 项 |
| 消息名/信号名未找到 | 该条目 `{"error":"message not found"}` / `"signal not found"`，其余条目继续 |
| `window_ref=anchor` 且未设锚点 | `{"error":"no anchor set","hint":"请先设置锚点，或使用 absolute 模式"}` |
| 窗口内无帧 | `{"error":"no frames in window"}` |
| 原始行超 20 000 | 截断 + `backend_info.truncated=true`（不报错） |
| 缓存不完整且窗口越界 | 附 `warning:"cache incomplete"` |
| `max_points` 越界 | clamp 到 [10, 1000] |

## 7. 已知限制（记录在案，非本期处理）

1. **结果不进图表**：`search_signal_trace` 只回 JSON 供 AI 解读；把结果序列推到移动端图表 tab 需要新的 UI 通道（chat → chart 绑定），记后续候选。
2. **enum 信号只出数值**：值表标签不进时间序列（决策 8），AI 需要标签时经 `get_dbc_signal` 查枚举表自行映射。
3. **UI 线程执行**：工具沿 P6.1 语义在 UI 线程执行；20k 行上限即为此预算服务。若真机实测出现可感知卡顿，后续可把纯查询+解码段移出 UI 线程（store 无 session 状态，天然线程安全）。
4. **桌面 Replay 仍不支持 `pgn:` 过滤**（P5 §8.3 记录的桌面行为变化备注不变），如需支持另立期次。
5. **搜索跳转仍限 CAN ID**（P5 §8.2 不变）：`JumpToAsync`/`FindFrameAsync` 不扩展 PGN。

## 8. 非目标

- 桌面端任何改动（`LttbDownsampler` 复用零改动；`J1939Id` 零改动）
- 异常扫描 / 导出（P5 §7 记录的另外两个"后续期次"项）
- 聊天记录持久化 / Browse 模式聊天入口（P6 §8 原样保留）
- PGN 搜索（`pgn:` token 进搜索框定位）——本期只修 Browse 过滤分页与提示文案

## 9. 验收标准

- **正确性**：生成列表达式 vs `J1939Id.Pgn` 随机 fuzz 对拍（≥1000 个扩展 ID，含 PDU1/PDU2 边界 0xEF/0xF0）；迁移幂等（旧库 ALTER 后重开不重复加列）；`GetFramesForCanIdAsync` 窗口边界（BETWEEN 闭区间）与截断标记。
- **tri-state 回归**：all-invalid 过滤（如 `zzz`）在 Browse 显示 0 帧（v1 方案此处会错误显示全部帧，测试显式锁定）；空过滤/纯 ID/纯 PGN/ID+PGN 混合四种输入与 P5 内存语义逐帧对拍。
- **分页归正**：构造 800 帧中 PGN 命中仅 3 帧的缓存，Browse PGN 过滤翻页 `HasNext` 语义正确（旧实现此处失真）。
- **查询计划**：`EXPLAIN QUERY PLAN` 断言 PGN 过滤查询走 `idx_frames_pgn_idx`；双集合 OR 组合无 TEMP B-TREE（若退化，按决策 6 退路处理并记录）。
- **工具**：未加载 DBC / 无缓存 / 消息未找到 / 窗口空 / 未设锚点 / 截断 / warning 各错误路径单测；LTTB 输出点数 = min(max_points, 原始点数)；多路复用信号只统计 selector 命中帧；接口新增后全部测试替身（FakeContext / FakeStore / ThrowingStore）同步更新。
- **UI**：过滤框 Placeholder 更新后模拟器截图验收；BrowsePage 其余绑定（HasNext/HasPrevious/Rows/PageStatus）不受 VM 内部路径删除影响（XAML 无直接绑定内存过滤内部，已核对）。
- **性能**：模拟器上 20k 行窗口的 `search_signal_trace` 端到端 <500ms；PGN 过滤翻页无可感知卡顿。
- `Mobile.Core.Tests` 全绿；`Host.Core`、`HIL.Core` 零改动（diff 仅 Mobile.Core / Mobile / 测试）。
