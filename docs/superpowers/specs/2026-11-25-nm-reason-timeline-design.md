# NM 唤醒原因 / 保持唤醒原因时间线（第一版）

- 日期：2026-11-25
- 范围：peakcan-host NM 分析（Core + App NM 面板 + 导出）
- 状态：第一版设计，待用户审阅

## 1. 要解决的问题

分析 BMS 网络管理日志时，用户需要回答：网络为什么醒、BMS 自报的原因是什么、原因持续多久、原因消失后网络有没有释放。

现有工具能显示 `BMS_Wakeup_reasons: 0 → 48` 这类数值变化，但不能把原始值翻成人能理解的原因，也不能按“出现 / 变化 / 清除 / 持续”串成时间线。

第一版只做**原始值时间线**：把用户显式声明的字段画到现有 NM 时间轴上，DBC 值表能精确命中就显示文字，命中不了就明确写“含义未配置”。不做位掩码展开，不做推断。

## 2. 真实数据事实（已核对）

权威 DBC：`C:\Users\13777\Desktop\can_log\LCV&微卡平台_BMS通信矩阵_20260706.dbc`（SHA256 `23d121d65b103a1a4ea5cde027b6124649e23a92d52d55715e6f4a8e4ae7d43f`，与同目录 `.nmprofile.json` 记录的 `dbc_hash` 一致）。
探针副本：`peakcan-host\.tmp\nm-repro\bms.dbc` 与上述权威文件逐字节一致（SHA256 相同）。
Trace：`C:\Users\13777\Desktop\can_log\微卡26商改网络测试NM_16测试Log.blf`（探针副本 `peakcan-host\.tmp\nm-repro\nm16.blf`）。

- `BMS_Wakeup_reasons`：扩展帧 `0x18FFFAF4` 的 byte2，8 位无符号。DBC 值表：`0 Default` / `1 Waked up by KL15` / `33 AC Charging Gun Plug In` / `34 DC Charging Gun Plug In` / `48 Waked up by NW PDU`。
- `BMS_Stayawake_reasons`：同帧 byte4–7，32 位无符号。DBC 值表：`1 Reserved` / `2 KL15 ON` / `16 Delay monitoring` / `524288 AC Charging in Process` / `1048576 DC Charging in Process` / `16777216 Cooling Fan` / `1073741824 Diagnosis service`。全部是 2 的幂，但本版**不使用这一形态推断语义**。
- 实测逐帧：
  - `byte2 = 0x00` 于 `0.095 s`；`0.609 s` 起变为 `0x30`，持续到末帧 `30.536 s`。
  - `byte4–7 = 0x00000001` 于 `0.095–9.714 s`；`0x00000002` 于 `12.677–19.826 s`；`0x00000001` 于 `20.363–30.536 s`。
  - `9.714–12.677 s` 没有任何 NM 帧（跨 episode）。
- 工作区临时目录 `D:\claude_proj2\.tmp\dbc-verify\head\Demo\Demo.roundtrip.dbc`（不在 peakcan-host 仓库内）的 `BMS_Wakeup_reasons` 值表是另一套（0–6，无 33/34/48），证明解释必须钉在 DBC hash 上。

## 3. 已确认的决策

1. 解释字典是**独立文件**，例如 `xxx.nmreasons.json`，与 `.nmprofile.json` 同目录。
2. 字典通过 `nmprofile.json` 新增的可选字段 `reasons_path` **显式引用**，不按文件名自动发现。
3. 第一版**不填含义条目、不声明枚举/位掩码**，字典只声明“要观察哪些字段”。
4. 时间条只画本次实际出现过的值。
5. CSV 导出保持现有 Events 表结构不变，原因数据放在独立表段。

## 4. 第一版范围

### 做

- 读取显式字段声明；逐帧解码原始值；按值变化生成时间线。
- 与现有 NM 时间条共用 X 轴，新增原因行。
- 事件表新增结构化 `reason_change` 行，可单击跳转原始报文。
- Markdown 报告与 CSV 导出增加原因段，保留原始值、解释来源、报文时间、完整性标记。
- 无字典 / 字段不可读 / 窗口截断 / 跨 episode 时明确降级，不猜。

### 不做

- 位掩码逐位展开（第二版）。
- AI 归因、实时监控、诊断保活/充电信号等外部事件归因。
- 因果结论；只表达时间关系。
- 面板内字典编辑（第一版用文本编辑器维护）。
- 重做主动唤醒检测、状态切段、企标规则。

## 5. 字典文件格式

路径：由 `nmprofile.json` 的 `reasons_path` 指向；相对路径相对 `nmprofile.json` 所在目录解析，绝对路径原样使用。

```jsonc
{
  "version": 1,
  "source": { "doc": "LCV&微卡平台_BMS通信矩阵", "revision": "20260706" },
  "fields": [
    {
      "signal": "BMS_Wakeup_reasons",
      "can_id": "0x18FFFAF4",
      "extended": true,
      "kind": "raw",          // 可选：raw（缺省）| enum | bitmask
      "entries": { "48": "NM PDU 唤醒" }   // 可选，第一版允许为空
    }
  ]
}
```

规则：

- `signal`、`can_id`、`extended` 必填。`extended` 必须显式写，避免标准帧/扩展帧同 ID 混淆。
- `kind` 缺省为 `raw`：整值精确匹配，不展开位。
- 第一版加载器接受 `raw` / `enum`；遇到 `bitmask` 时明确报“第一版不支持位掩码解析”，并降级为整值显示（报告 Unknowns 记录，不静默）。
- `entries` 可选。解释优先级：**DBC 值表精确匹配 > 字典 entries**。两者对同一原始值给出不同文本时，记录冲突，UI 与导出同时显示两个文本和各自来源（DBC / 字典），不静默择一。
- 字典文件本身有内容 SHA256，报告记录 `ReasonDictionaryPath` / `ReasonDictionaryHash`；同时记录已有的 `DbcHash`。
- 坏 JSON、路径不存在、字段在 DBC 中找不到：报告 Unknowns，面板显示原因行不可用，不阻断其它 NM 分析。

## 6. 数据模型

### 6.1 字典（Core）

```csharp
public enum NmReasonKind { Raw, Enum, Bitmask }

public sealed record NmReasonSource(string? Doc, string? Revision);

public sealed record NmReasonFieldDef(
    string Signal,
    string CanIdHex,
    bool IsExtended,
    NmReasonKind Kind = NmReasonKind.Raw,
    IReadOnlyDictionary<long, string>? Entries = null);

public sealed record NmReasonDictionary(
    int Version,
    IReadOnlyList<NmReasonFieldDef> Fields,
    NmReasonSource? Source = null);
```

### 6.2 分析结果（Core，挂到 `NmReport`）

```csharp
public enum NmReasonSegmentEnd
{
    Changed,      // 同一 episode 内，下一个值出现
    EpisodeEnd,   // episode 结束，后续仍有 NM 帧
    Unreadable,   // 下一帧 DLC 不足或字段读不出
    WindowEnd,    // 之后没有任何可观测帧（日志/窗口结束）
}

/// <summary>一条解释文本及其来源：Source = "dbc" | "dictionary"。</summary>
public sealed record NmReasonLabel(string Text, string Source);

public sealed record NmReasonSegment(
    double Start,
    double End,                // 最后确认该值仍存在的帧时刻（不覆盖下一个值）
    ulong RawValue,
    IReadOnlyList<NmReasonLabel> Labels,   // 空列表 = 含义未配置；冲突时含多条
    bool StartUnknown,         // 首段起点等于字段首次可读帧，窗口前状态未知
    bool EndUnknown,           // 仅 EndReason=WindowEnd 时为 true
    NmReasonSegmentEnd EndReason,
    double? NextObservedAt,    // 清除/中断后首次重新可观测的帧；同时是清除区间右端
    IReadOnlyList<byte> FirstFrameBytes);  // 该段首帧原始字节

public sealed record NmReasonTransition(
    double T,                  // 新值首次出现的帧
    string Signal,
    string CanIdHex,
    bool IsExtended,
    ulong OldRaw,
    ulong NewRaw,
    IReadOnlyList<NmReasonLabel> OldLabels,
    IReadOnlyList<NmReasonLabel> NewLabels);

public sealed record NmReasonTimeline(
    string Signal,
    string CanIdHex,
    bool IsExtended,
    NmReasonKind DeclaredKind,
    NmReasonKind EffectiveKind,    // bitmask 未实现时降级为 Raw，消费方看这个
    IReadOnlyList<NmReasonSegment> Segments,
    IReadOnlyList<NmReasonTransition> Transitions,
    IReadOnlyList<double> UnreadableFrameTimes,
    IReadOnlyList<string> Unknowns);

// NmReport 新增（init 属性，保持既有构造兼容）
public IReadOnlyList<NmReasonTimeline> ReasonTimelines { get; init; } = [];
public string? ReasonDictionaryPath { get; init; }
public string? ReasonDictionaryHash { get; init; }
```

- `Labels` 为空时，UI/导出写“含义未配置（原始值 0xXX）”。
- `Labels` 含多条时，每条都带来源，UI/导出并列显示，不做择一。
- `DeclaredKind` 是字典声明值，`EffectiveKind` 是实际生效值；第一版 `bitmask` 只会出现在 `DeclaredKind`，且报告 Unknowns 明确记录降级。

`NmReport.Assumptions` 固定追加一条：原因解释来自 DBC 值表或项目字典，是 BMS 自报字段的翻译；与网络行为只表达时间关系，不代表根因。

## 7. 分析算法

入口：`NmReasonAnalyzer.Analyze(dictionary, dbc, frames, episodes, window)`，纯函数。

1. 只分析字典 `fields` 声明的字段。没有字典 → 返回空列表，不产生原因行。
2. 按 `(CanIdHex, IsExtended)` 找帧。DBC 查找必须用**裸 ID**（去掉 bit31 IDE 位）查 `DbcDocument.MessagesById`，再用 `message.Id & 0x80000000u` 与 `IsExtended` 交叉核对；不得直接用带 IDE 位的 ID 查（会查不到）。口径同 `NmSignalSummary.Build:47-53` 与 `NmStsResolver.TryFindMessage`。找不到消息/信号时写 Unknowns，跳过该字段并继续其它字段。
3. 逐帧读取原始值：
   - 先检查 `data.Length` 是否覆盖信号的起止位；不覆盖 → 该帧不可读，记录时刻到 `UnreadableFrameTimes`，**不继承值、不把 0 当成值**；当前段以最后一个可读帧结束（见步骤 4）。
   - 用 `SignalDecoder.DecodeRaw`（返回无符号位模式），与 DBC 值表 / 字典条目按 `long` 键精确匹配。
4. 切段（`End` 一律取“最后确认该值仍存在的帧”，不覆盖下一个值）：
   - 同一 episode 内值变化：旧段 `Changed`，`NextObservedAt` = 新值首帧。
   - episode 结束且后续仍有 NM 帧：旧段 `EpisodeEnd`，`NextObservedAt` = 下一 episode 首帧；即使下一 episode 首帧同值也另起一段。
   - 下一帧不可读：旧段 `Unreadable`，`NextObservedAt` = 下一次可读帧。
   - 之后没有任何可观测帧：旧段 `WindowEnd`，`EndUnknown=true`，`NextObservedAt=null`。
   - 首段起点等于字段首次可读帧 → `StartUnknown=true`。
   - `EndReason` 与 `EndUnknown` 不重叠：只有 `WindowEnd` 才置 `EndUnknown`；中间断开用 `NextObservedAt` 表达。
5. 时长与清除区间：
   - 所有时长都表述为“观测到的持续时间”——即 `End - Start`，含义是“该值至少持续到 End 帧”。
   - 时长一律表述为观测下界：默认 UI/导出写 `≥ X s`；仅当起点有界且 `EndReason=Changed` 时可省略 `≥`，但必须注明“观测区间”，不得表述为“精确持续 X 秒”。
   - `EndReason=Changed` 或 `EpisodeEnd` 时给出“清除发生区间” `[End, NextObservedAt]`，明确这是相邻两次观测之间的区间，不是精确时刻；`Unreadable` 时改为“不可观测区间”，不得称清除；`WindowEnd` 时写“之后无可观测帧，无法判断”。
6. 事件：
   - 同一 episode 内值变化 → 一条结构化 `reason_change`，`T` 取新值首帧。
   - 同值跨 episode 或不可读后恢复同值 → **不产** `reason_change`（值没变），但段边界与 `NextObservedAt` 可见。
   - 跨 episode 且值变化 → 一条 `reason_change`，`T` 取新值首帧。
   - 字典声明的字段不再产 `signal_change` 事件；`NmSignalSummary.Build` 增加可选跳过集合，避免事件表重复。
7. 限流：每个字段最多 200 段 / 200 条变化（与现有 `MaxEventsPerMessage` 同口径）。截断写入 `NmTruncation`。

## 8. UI

- `BuildTimelineRows` 在报文行之后、网络行之前追加原因行：
  - 行标签：`原因 {信号名}`，例如 `BMS_Wakeup_reasons`。
  - 段：每个原始值一种颜色（`NmTimelineKinds.ReasonValue(index)`，按首次出现顺序分配，8 色循环）；确认区间只画到数据 `End`（最后确认旧值的帧）。`End → NextObservedAt` 之间另画浅色「未观测区间」段（`ReasonUncertain(index)`，同色相 30% 透明度），表示值可能在这段区间内变化，但无法确认精确时刻；`WindowEnd` 不画未观测段。tooltip 含起止、时长、原始值、标签来源、完整性、首帧原始载荷（逐字节）。
  - 跨 episode 下一段仍为同一原始值时，文案必须写「仍为同一值…无法确认是否曾变化」，不得声称“清除/变化”。
  - 无字典：不画行，面板输入区提示“原因字典未加载”。
  - 字典有字段但 DBC 无该信号/DLC 读不出：行内显示“字段不可读”；有段但存在字段级降级（bitmask 未展开等）时，顶部仍须显示「部分原因字段已降级」。
- 事件表新增 `reason_change` 行；单击跳转复用 `SeekToNmEventCommand`。回放已废除，跳转的可见效果是**移动绿色锚点**（图表竖线 + 监视列表「最新值」重算），面板上必须给出「已把绿色锚点移到 X s」的状态反馈；原因行 tooltip 同时给出该段首帧原始载荷。
- `reason_change` 必须同时在 `NmPanelFlow.MarkerKind` 与 `NmEventShapeConverter.ShapeFor` 注册分支；未注册会落到未知形状（品红六边形），被误读为异常。
- 不新增固定高度条带，不另起窗口；沿用现有可滚动时间轴。

## 9. 导出

- Markdown（`NmReportWriter.WriteMarkdown`）新增 `## Reason Timelines`：
  - 报告头增加 `reasons_path` / `reason_dict_hash`。
  - 表格列：信号、CAN ID、起始、结束、时长、原始值、解释、解释来源、完整性。
  - 表后列出 Unknowns 与“清除发生区间”。
- CSV：现有 Events 表头与列不变；在其后空两行追加独立 `Reason` 表段，含同样的字段。
- 导出中所有解释都必须带来源（`dbc` / `dictionary`）；无解释时写“含义未配置”。同一值有多个来源时全部列出，不择一。

## 10. 缓存、性能、错误处理

- `NmCacheKey` 增加 `ReasonDictionaryHash { get; init; } = "none"` 属性，**不改位置参数**，避免破坏既有 `new NmCacheKey(...)` 构造点；`EngineVersion` 从 `nm-m1-1` 升为 `nm-m1-2`。
- 分析复杂度 O(字段数 × 该字段帧数)，字段数受字典限制；沿用 `NmFrameSelector` 的帧集。
- 字典加载失败不阻断 NM 分析：记 Unknowns，原因时间线为空。
- 不吞 IO 异常；坏 JSON / 缺字段 / 未知 kind 都在加载期给出明确错误或报告条目。

## 11. 测试与验收

### Core 单测（合成帧）

- 值变化切段、首帧已存在（`StartUnknown` 且时长为 `≥`）、末帧未清除（`EndUnknown` 只在 `WindowEnd`）。
- 跨 episode 同值不合并，`NextObservedAt` 正确；跨 episode 同值不产 `reason_change`。
- DLC 不足一帧：不产生假 0 值段，旧段 `EndReason=Unreadable`，`NextObservedAt` 指向恢复帧。
- DBC 有值表 → `Labels` 含 `("文本","dbc")`；无值表 → `Labels` 为空。
- 字典 entries 补充 DBC 缺失值；与 DBC 冲突时 `Labels` 含两条来源。
- `kind=bitmask` → `DeclaredKind=Bitmask`、`EffectiveKind=Raw`，Unknowns 明确提示，不产生逐位结论。
- 字段在 DBC 中不存在 → Unknowns，跳过该字段。
- 段数达到上限 → 截断记录。

### 真实数据回归

数据从桌面权威文件复制到 `tests/PeakCan.Host.Core.Tests/TestData/Nm/`（`nm16.blf`、`bms.dbc`）。测试文件需断言 DBC 的 SHA256 等于 `23d121d65b103a1a4ea5cde027b6124649e23a92d52d55715e6f4a8e4ae7d43f`，防止将来被替换后结论静默变义。测试初始化必须注册 `CodePagesEncodingProvider`，用 GBK 读取 `bms.dbc`（同 `.tmp/nm-repro/Program.cs:9`）：

- `BMS_Wakeup_reasons`：`0x00` 段 `[0.095, 0.095]`（只有一帧），`EndReason=Changed`、`NextObservedAt=0.609`；`0x30` 段 `[0.609, 9.714]`（`EpisodeEnd`、`NextObservedAt=12.677`）与 `[12.677, 30.536]`（`WindowEnd`、`EndUnknown=true`）；`0x30` 的 `Labels` 含 `("Waked up by NW PDU","dbc")`。
- `BMS_Stayawake_reasons`：`0x1` 段 `[0.095, 9.714]`（`EpisodeEnd`、`NextObservedAt=12.677`）、`0x2` 段 `[12.677, 19.826]`（`Changed`、`NextObservedAt=20.363`）、`0x1` 段 `[20.363, 30.536]`（`WindowEnd`）。
- 去掉 DBC `VAL_` 的同数据副本 → `Labels` 为空，文案“含义未配置（0x30）”。

### App 测试

- `BuildTimelineRows` 生成原因行、行标签、无字典不生成。
- 原因行点击跳转（ViewModel 层）。
- Markdown/CSV 导出包含原因段与来源。

### 反向验证

去掉实现中的关键判断（跨 episode 切段、DLC 检查、`signal_change` 抑制），对应测试必须变红。

### 最小验收映射

1. 真实日志 0.609 s 标出 `0x30`；无值表时显示“含义未配置” → 真实数据回归。
2. 已知原因测试数据出现/变化/清除/持续正确 → Core 单测。
3. 点击原因变化跳到原始报文 → App 测试。
4. 原因清除与网络释放只显示时间关系 → 报告 Assumptions + 导出文案断言。
5. 不完整/不可读/截断显示“无法判断” → Core 单测 + UI 降级测试。

## 12. 风险与边界

- 两份 DBC 值表冲突（工作区临时目录 `D:\claude_proj2\.tmp\dbc-verify\head\Demo\Demo.roundtrip.dbc` 与生产矩阵；该文件不在 peakcan-host 仓库内）：解释一律按当前加载 DBC + `DbcHash`，换 DBC 结论可能变义，报告必须带 hash。
- 静默期（如 9.714–12.677 s）原因不可观测：不合并同值段，不声称“已清除”；DLC 不足的帧同理，切段而不是继承值。
- 周期 500 ms 下“清除时刻”只能落在相邻两帧之间：输出区间，不输出精确时刻。
- 第一版不做位掩码；用户若在字典写 `bitmask`，明确提示未支持，不产生逐位结论。
- 无零部件功能规范：本版不写语义结论，后续需人工确认后扩展 `kind=bitmask`。
- 生产 DBC 为 GBK 编码，`CM_` 中文注释可能乱码：行标签优先用信号名，注释无法正确解码时退化为信号名，不显示乱码。

## 13. 实现落点

| 层 | 文件 | 改动 |
|---|---|---|
| Core | `Analysis/Nm/NmReasonDictionary.cs`（新） | 字典模型、加载、校验、SHA256 |
| Core | `Analysis/Nm/NmReasonAnalyzer.cs`（新） | 时间线分析 |
| Core | `NmReport.cs` | 新增 `ReasonTimelines` / `ReasonDictionaryPath` / `ReasonDictionaryHash` |
| Core | `NmProfile.cs` | `NmUserSupplement` 增加 `ReasonsPath` |
| Core | `NmAnalysisRequest.cs` | 增加可选 `NmReasonDictionary? Reasons` |
| Core | `NmAnalyzer.cs` | 调用原因分析；抑制被覆盖的 `signal_change`；bump EngineVersion |
| Core | `NmSignalSummary.cs` | `Build` 增加可选跳过集合 |
| Core | `NmReportCache.cs` | 缓存键增加 `ReasonDictionaryHash { get; init; } = "none"` 属性，不改位置参数 |
| App | `NmFlow.cs` | 读 sidecar 后按 `reasons_path` 加载字典 |
| App | `NmInputWizardViewModel.cs:663` | 试算路径同样加载字典（复用同一加载函数，不能只在主分析路径生效） |
| App | `NmPanelFlow.cs` | 时间轴原因行、事件行、无字典提示；`MarkerKind` 增加 `reason_change` |
| App | `NmEventShapeConverter.cs` | `ShapeFor` 增加 `reason_change` 形状 |
| App | `NmReportWriter.cs` | Markdown / CSV 原因段 |
| App | `NmTimelineKinds` | 新增 `Reason` 种类与配色 |
| App | `NmToolDtos.cs` / ChatTools | 第一版**不**把 `ReasonTimelines` 暴露给 AI 工具；序列化结构保持现状 |
| Tests | Core / App | 合成数据、真实数据回归、反向验证 |
</content>
</Write>
