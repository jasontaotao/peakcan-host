# NM 静默相位推断设计（ReleaseWait / PrepareSleep / Sleep）

> 状态：草稿，待用户评审
> 关联规格：`docs/superpowers/specs/2026-11-19-nm-ai-analysis-design.md`（M1）、`docs/superpowers/specs/2026-11-23-nm-m2-oem-standard-design.md`（M2 / 吉利企标）
> OEM 依据：`02 _ 吉利商用车CAN网络管理需求规范_V2.0_20250227.pdf` §5.3.1 / §5.3.2

## 1. 问题

NM 面板的“整网状态”行把每个“全部 NM 报文静默 ≥ 超时”的空档画成一条灰色静默带，原因写“下电/记录停止/睡眠不可区分”。用户问：**为什么没有“准备休眠”阶段？**

现状确实没有建模这个阶段。但直接加三段会犯两类错误：

1. **把“有应用报文”当成“准备休眠”的证据**：静默期只要有应用报文就整段标准备休眠。
2. **忽略发送者归属**：应用报文可能来自 NM 发送者本身（说明它还醒着），也可能来自其它节点（只能证明总线活着）。

真实 trace（`tests/PeakCan.Host.Core.Tests/TestData/Nm/nm16.blf`）给出了反例：

| 区间（相对首帧） | 实测事实 |
|---|---|
| 9.714 – 11.714 s（前 2 s） | 366 帧非 NM 帧中的 **354 帧全部来自 BMS 自己**（SA=0xF4，58 种应用报文），BMS 明显醒着 |
| 11.714 – 12.667 s | 应用报文停掉（只剩 11.717–11.747 的 7 帧尾巴）；11.747–12.667 总线 **0.92 s 一帧没有** |
| 12.667 – 12.677 s | 3 帧应用报文 + NM 快发恢复 → 在 T_WAIT_BUS_SLEEP 内被唤醒 |

前 2 s 若被标成“准备休眠”，与“BMS 仍在发送 354 帧应用报文”直接矛盾。

## 2. 目标与非目标

### 目标

- 对每个候选静默空档，按 **NM 超时边界 + 发送者归属 + 应用报文活动** 推断三个相位窗口：
  - `release_wait`：NM 停发后的首个 `TimeoutMs` 窗口（正常释放下为 RSS）。
  - `prepare_sleep`：之后 `WaitBusSleepMs` 窗口（正常释放下为预睡眠）。
  - `sleep`：再之后的剩余静默（正常释放下为睡眠）。
- 在 NM 面板“整网状态”行分色显示；hover 给出帧数、发送者、最大总线空档、时序来源和“推断”依据。
- 导出 Markdown/CSV、AI overview 同步输出相位。
- 修订现有 M1/M2 规格中“不能用应用报文推断状态”的绝对表述。

### 非目标

- 不声称任何节点“已睡眠/已进入预睡眠”：内部定时器仍不可观测。
- 不产生 finding（尤其不加 `warn`）；相位只进报告的结构化数据与 observation/UI。
- 不改变 `NM_ALL_NM_SILENT` 的产生条件、严重级别和 `all_nm_silent` observation 的现有契约。
- 不处理首部静默（记录边界）。
- 不为非 J1939 标准帧凭空猜发送者；DBC sender 缺失时标“未归属”。

## 3. 可观测性依据

### 3.1 OEM 规范（已读原文）

- §5.3.1 睡眠模式：NM 报文和应用报文禁止发送，且不能对总线报文 ACK。
- §5.3.2 预睡眠模式：启动 `T_WAIT_BUS_SLEEP`；总线活动需静止；**NM 报文和应用报文禁止发送（为了清空发送缓存，允许将发送队列中已有的报文发完）**，但需对总线报文 ACK。
- §5.3.3 网络模式：RSS/NOS 期间应用报文可以正常发送；`T_NM_TIMEOUT` 由成功收发的 NM 报文重置。

因此：

| 观测 | 能证明 | 不能证明 |
|---|---|---|
| NM 静默 + **NM 发送者**持续发应用报文 | 该发送者仍在网络模式（醒着） | 其它节点状态 |
| NM 静默满 `TimeoutMs` 后发送者应用报文停止（仅剩清缓存尾巴） | 与预睡眠行为一致（推断） | 真的进入了预睡眠 |
| 静默期总线一帧没有 | 记录可能停止/断电/睡眠 | 三者不可区分 |
| 静默期其它发送者发应用报文 | 总线有电、记录未停 | 本 NM 网络已释放/睡眠 |

### 3.2 不可观测项（继续出现在 Unknowns）

- 节点内部 `T_NM_TIMEOUT` / `T_WAIT_BUS_SLEEP` 是否到期。
- 节点真实状态机状态（RSS / 预睡眠 / 睡眠）。
- 接收端本地状态。
- 未声明 NM 报文是否被误算成“非 NM 帧”。

## 4. 定义

### 4.1 候选静默空档

- 只处理两类：**episode 间隙** `[episode[i].End, episode[i+1].Start]` 与**尾部** `[lastEpisode.End, Window.End]`。
- 长度 `< TimeoutMs` 的空档不处理（与 `NM_ALL_NM_SILENT` 同门槛）。
- **首部静默不处理**：没有“最后一个 NM 帧”作为超时锚点，且属于记录边界。

### 4.2 发送者归属

- 扩展帧（J1939）：`SA = Id & 0xFF`；发送者键 `sa:0xF4`。
- 标准帧：用 DBC 的 `BO_ ... sender`；DBC 不可用或无该报文 → `unknown`。
- `nmSenders`：本空档前一个 episode 内出现过 NM 报文的发送者集合（按同一归属规则）。
- 非 NM 帧 = 选中帧中不属于本次分析的任何 NM 报文 `(numericId, IsExtended)` 的帧。
- 未归属 = 无 SA 且无 DBC sender 的帧。

### 4.3 两个宽限

| 宽限 | 取值 | 依据 |
|---|---|---|
| `DrainGraceMs` | `CycleMs`（默认 500 ms） | §5.3.2 允许清空发送缓存；取 1 个 NM 周期 |
| `WakeupGraceMs` | `max(CycleMs, WakeUpMs)`（默认 500 ms） | 下个 episode 首帧前的应用报文属于唤醒序列（§5.3.1.1 / T_STARTx_AppFrame） |

两个宽限都由 `NmGlobalTiming` 派生，写进报告 assumptions，不用魔法数字。

### 4.4 相位窗口

设 `s = gapStart`、`e = gapEnd`、`t1 = s + TimeoutMs`、`t2 = t1 + WaitBusSleepMs`：

| 窗口 | 区间 |
|---|---|
| w1 `release_wait` | `[s, min(t1, e))` |
| w2 `prepare_sleep` | `[t1, min(t2, e))` |
| w3 `sleep` | `[t2, e)` |

零长窗口不发射。

## 5. 判定真值表

### 5.1 前置

1. 整个空档内**没有任何选中帧** → 发射 1 条 `Unknown` 覆盖整个空档，`Basis = NoBusActivity`，不切三段。
2. `nmSenders` 无法归属（例如 NM 报文是标准帧且无 DBC sender）→ 发射 `Unknown` 覆盖整个空档，`Basis = Unattributed`，并加 unknown `silence_phase_unattributed`。
3. 前一个 episode 的最后一帧 NMSts（逐报文读取，使用 `Profile.StandardBitRoles`）：
   - 只取**末帧时刻在 `episode.End − TimeoutMs` 之后**的报文。明显早于 episode 末帧的报文已由 `NM_NODE_DROPOUT` / `NM_SENDER_SILENT_BEYOND_TIMEOUT` 归因，不能拿它的局部异常否决整个网络的释放推断。
   - 任一可读且为 `Repeat Message State` → 整个空档 `Unknown`，`Basis = NmStsRepeatMessage`，加 unknown `silence_phase_nmsts_repeat`。理由：RMS 停发不是 NM_09 的合规释放，不能借应用报文洗成正常。
   - 全部可读且为 `Ready Sleep` → `NmStsEvidence = ReadySleepAtLastFrame`，只在 Detail/证据行加强，不改变 inferred。
   - 未声明/读不出 → `NotAvailable` / `UnreadableAtLastFrame`，不额外加 unknown（全局已有“节点内部状态机不可观测”）。

### 5.2 w1（释放等待 / RSS 窗口）

| 条件 | Kind | Basis | Detail 要点 |
|---|---|---|---|
| NM 发送者的应用报文 > 0 | `ReleaseWait` | `NmSenderStillActive` | “NM 已停发；发送者仍在发 N 帧应用报文 → 仍处于网络模式（推断）” |
| 无 NM 发送者应用报文，但其它**已归属**发送者帧 > 0 | `ReleaseWait` | `OtherSenderActive` | “NM 已停发；未见本网络发送者的应用报文” |
| 只有**未归属**帧 | `Unknown` | `Unattributed` | “发送者无法归属，不能判断本网络发送者是否仍在发报文”；加 unknown |
| 窗口内无任何帧 | `Unknown` | `NoBusActivity` | “该窗口总线无帧，无法判定” |

### 5.3 w2（准备休眠窗口）

OEM §5.3.2 的判据是“总线活动需静止下来，最终达到总线上没有活动”，且只允许清空发送缓存。因此 w2 的判据是**总线是否静止**，不是只看 NM 发送者：

判定顺序（先排除“观察不足以判断”，再判总线是否静止）：

1. **`w2` 时长 < `DrainGraceMs`**（短窗口，review HIGH-1/2）：
   - 有帧 → `Unknown / WindowTooShort`，Detail 写“窗口短于清缓存宽限，观察不到发送者是否在宽限后停发；不得据此判准备休眠”。加 unknown `silence_phase_window_too_short`。
   - 无帧 → `Unknown / NoBusActivity`。
2. **判据窗 `(w2.Start + DrainGrace, w2.End − WakeupGrace)` 非空**：

| 条件 | Kind | Basis | 说明 |
|---|---|---|---|
| 判据窗内仍有**任何**选中帧（任何发送者），**或最长安静跨度 ≤ `DrainGrace`** | `Unknown` | `AppTxAfterTimeout` | 与 §5.3.2 矛盾；Detail 写“超时后仍有 N 帧”或“最长安静跨度 ≤ 宽限” |
| 判据窗内无帧，最长安静跨度 > `DrainGrace`，且 `w2.Start` 之后仍有活动证据 | `PrepareSleep` | `NmSenderStoppedAfterDrain` | Detail 写“最长安静跨度 X s（> 清缓存宽限）”与窗口内帧数 |
| 整个空档在 `w2.Start` 之后再无任何帧 | `Unknown` | `NoBusActivity` | 无法区分准备休眠/断电/停录 |

3. **判据窗被两个宽限夹空**（`w2` 长于 `DrainGrace` 但不足 `DrainGrace + WakeupGrace`）：
   - 用 **最长安静跨度**（窗口首尾到首/末帧的空白 + 帧间最大间隔）判断：
     - `≤ DrainGrace` → `Unknown / AppTxAfterTimeout`（总线持续有帧，未静止；**必须用 ≤**，500ms 周期流量的安静跨度恰好 0.5s，不能用 `<` 放过——review NEW-M1）
     - `> DrainGrace` 且有活动证据 → `PrepareSleep`
     - 否则 → `Unknown / NoBusActivity`

> 宽限内帧仍会逐条列在 Detail 中（帧数、时刻），供人工判断是不是清缓存；但它们不触发矛盾。宽度由 `DrainGraceMs` / `WakeupGraceMs` 决定。发送者是否归属不影响 w2：判据是**总线是否静止**；未归属只在 Detail 注明。

### 5.4 w3（睡眠窗口）

| 条件 | Kind | Basis |
|---|---|---|
| `WaitBusSleepMs` 未显式配置 | `Unknown` | `WaitBusSleepDefaulted`（加 unknown） |
| w3 内仍有**任何**选中帧（任何发送者） | `Unknown` | `AppTxAfterTimeout`（总线未静止，睡眠不成立） |
| w3 内无任何帧，且 `gapEnd` 处/之后仍有选中帧（下一 episode 起点；证明记录仪仍在工作） | `Sleep` | `NmSenderStoppedAfterDrain` |
| w3 内无任何帧，且空档是尾部、`gapEnd` 之后没有更多证据 | `Unknown` | `NoBusActivity`（`IsWindowTruncated = true`） |

> “总线睡眠”始终只是**窗口推断**，不是实测。ACK 不是帧，不在此列。

### 5.5 截断

- 尾部空档的最后一个相位终点等于 `Window.End` → `IsWindowTruncated = true`，时长是下限。
- episode 间隙的最后相位终点等于下一 episode 起点 → 边界已知，不截断。

## 6. 数据模型

### 6.1 新类型（`src/PeakCan.Host.Core/Analysis/Nm/NmSilencePhase.cs`）

```csharp
public enum NmSilencePhaseKind
{
    ReleaseWait = 0,
    PrepareSleep = 1,
    Sleep = 2,
    Unknown = 3,
}

public enum NmSilenceBasis
{
    NmSenderStillActive = 0,
    NmSenderStoppedAfterDrain = 1,
    OtherSenderActive = 2,
    NoBusActivity = 3,
    Unattributed = 4,
    NmStsRepeatMessage = 5,
    AppTxAfterTimeout = 6,
    WaitBusSleepDefaulted = 7,
    WindowTooShort = 8,
}

public enum NmStsEvidence
{
    NotAvailable = 0,
    ReadySleepAtLastFrame = 1,
    RepeatMessageAtLastFrame = 2,
    UnreadableAtLastFrame = 3,
    Mixed = 4,
}

public sealed record NmSenderActivity(string Sender, bool IsNmSender, int FrameCount);

public sealed record NmSilencePhase(
    double Start,
    double End,
    NmSilencePhaseKind Kind,
    NmSilenceBasis Basis,
    int NonNmFrameCount,
    int NmSenderAppFrameCount,
    int OtherSenderFrameCount,
    int UnattributedFrameCount,
    double? BusMaxGapSec,
    NmStsEvidence NmSts,
    bool IsWindowTruncated,
    int AfterEpisodeIndex,
    string Detail)
{
    public string EvidenceKind => "inferred";
    public bool IsInference => true;
    public IReadOnlyList<NmSenderActivity> Senders { get; init; } = Array.Empty<NmSenderActivity>();
    public bool SendersTruncated { get; init; }
}
```

`Senders` 截断到前 8 个（按帧数降序），截断时 `SendersTruncated = true`。

### 6.2 报告接入

- `NmReport` 增加 init 属性（**不加 positional 参数**，避免破坏 12 处现有构造）：
  `public IReadOnlyList<NmSilencePhase> SilencePhases { get; init; } = Array.Empty<NmSilencePhase>();`
- `NmGapResult` 增加同一 init 属性与 `PhaseUnknowns`。
- `NmAnalyzer.Finish` 合并 `gap.SilencePhases` 与 `gap.PhaseUnknowns`。
- `EngineVersion` 从 `nm-m1-2` bump 到 `nm-m1-3`（缓存 key 含 EngineVersion，必须 bump）。
- `NmGlobalTiming` 增加 `WaitBusSleepDeclared`（与 `ImmediateCycleDeclared` 同一模式），由 `NmTimingDefaults.FromProfile` 显式带上来；**不能用值是否等于 2000 反推**。

### 6.3 新分析器

`src/PeakCan.Host.Core/Analysis/Nm/NmSilencePhaseAnalyzer.cs`：纯函数，输入候选空档、episode、nmFrames、allFrames、profile、timing、DBC、窗口终点；输出 `NmSilencePhaseResult(Phases, Unknowns)`。`NmGapAnalyzer` 在现有 episode 间隙与尾部两处调用。

`NmGapInput` 增加可选 `Dbc`（标准帧 sender 解析）；只影响 1 个构造点，默认 null。

## 7. UI 呈现

### 7.1 网络行

- 新 kind（`NmSegmentBrushConverter.NmTimelineKinds`）：
  - `silence_release_wait`
  - `silence_prepare_sleep`
  - `silence_bus_sleep`
  - `Unknown` 相位复用现有 `silence`
- `Unknown` 相位用独立 kind `silence_phase_unknown`（灰色、与残余静默带 `silence` 区分），
  **不得**复用 `silence`——否则 `IsSilencePhaseKind` 不认它，hover 会丢掉 Detail 并显示
  “超时未达静默阈值”的错误证据行（review NEW-M2）。
- 四种颜色必须彼此不同，且不同于 `SilentBrush` / `UnknownBrush`；加入 `NmTimelineKinds.All`。
- `BuildNetworkTimelineRow`：
  1. 先按现有逻辑生成并合并通用静默带（`MergeSilenceBands` 保持现有语义）。
  2. 用 `SubtractRanges` 从通用静默带中扣掉 `report.SilencePhases` 覆盖的区间；残余段仍是 `silence`。
  3. 按相位发射带，tooltip = 相位详情 + 区间 + “推断”；相位带之间不合并（各自保留完整证据），只与残余通用带按区间邻接。
- hover 信息卡：
  - 标题：`释放等待（推断）` / `准备休眠（推断）` / `总线睡眠（推断）` / `静默（无法判定）`
  - 证据行：kind 对应的依据；正文列出 NM 发送者应用帧数、其它帧数、未归属数、Top 发送者、最大总线空档、Timeout/WaitBusSleep 及来源、宽限。
  - 结尾固定：`节点内部状态不可观测；相位按 NM 超时/等待时间推断，不是实测状态。`
  - 点击仍把绿色锚点移到该段起点。

### 7.2 图例

在现有“整网行”图例 TextBlock 内追加 3 个 `Run`（不新增 TextBlock，避免 `NmViewLayoutTests` 的计数断言连锁改动）：

`■ 释放等待（推断）  ■ 准备休眠（推断）  ■ 总线睡眠（推断）  ■ 无法判定（推断）`

### 7.3 文案

- `NmTimelineText.SegmentKind`：`释放等待` / `准备休眠` / `总线睡眠` / `静默`。
- 不在标签里重复“推断”（证据行已经写）；hover 标题带“（推断）”。
- `EvPrepareSleep` 等证据行必须含“推断”，不得出现 `measured`。

## 8. 导出与 AI

- `NmReportWriter` Markdown：新增 `## Silence Phases`（仅非空时输出，旧报告逐字节不变）。
- `NmReportWriter` CSV：末尾追加 `# Silence Phases` 段（首行表头契约不变）。
- `GetNmOverviewTool`：新增 `silence_phases` 数组，字段 `phase/start/end/basis/non_nm_frame_count/nm_sender_app_frame_count/other_sender_frame_count/unattributed_frame_count/bus_max_gap_ms/nmsts/is_window_truncated/detail/senders`。
- `NmPromptRules`：在“证据分级”段落追加一句（不新增编号规则）：
  `silence_phases 全部是 inferred；NM 发送者的应用报文证明该发送者仍在网络模式，不能写成 measured，也不能据此确认节点已睡眠。`
- 隐私：`Detail` 与 `senders` 经 `NmEventPrivacy.RedactText` 处理 CAN ID。

## 9. 真实 trace 验收

`nm16.blf` 的 9.714–12.677 空档必须得到：

| 相位 | 区间（允许 ±1 个采样误差） | Kind | 依据 |
|---|---|---|---|
| 释放等待 | 9.714 – 11.714 | `ReleaseWait` | 354 帧 BMS 应用报文（`NmSenderStillActive`） |
| 准备休眠 | 11.714 – 12.677 | `PrepareSleep` | 仅 11.717–11.747 的 7 帧清缓存尾巴；之后 0.92 s 总线无帧（`NmSenderStoppedAfterDrain`） |

- w3 不发射（12.677 前已唤醒）。
- `NM_ALL_NM_SILENT` 仍为 info；`all_nm_silent` observation 仍为 inferred。
- 12.667–12.668 的 3 帧应用报文落在 `WakeupGraceMs` 内，**不得**触发 `AppTxAfterTimeout`。
- 相位不得产生任何 finding。

## 10. 规格修订

### 10.1 M2 规格

`2026-11-23-nm-m2-oem-standard-design.md`：

- §3.1 第 4 条：保留“全网 NM 静默不判违规/不判正常”，补充“有发送者归属的非 NM 帧可排除断电/停录，并可推断发送者是否仍醒着；相位窗口见新 spec”。
- §14.5：把“不能用有应用报文推断 BMS 处于网络模式”改为更精确的表述：
  - 有应用报文只证明**其发送者**仍醒着，不能证明 BMS（若发送者不是 BMS）处于网络模式；
  - 若应用报文来自 BMS 本身，则证明 BMS 仍醒着，不能把该区间判为准备休眠；
  - 本 trace 的前 2 s 属此情形；超时后应用报文停止（仅清缓存尾巴），与预睡眠行为一致，但内部状态仍不可观测。
- §14.6 回归基线：新增“本 trace 应产 `release_wait` + `prepare_sleep` 两条相位，不产 finding”。

### 10.2 M1 规格

`2026-11-19-nm-ai-analysis-design.md`：

- §7.5：新增“静默相位推断”小节，引用本 spec。
- §7.9：把“真正的总线睡眠（只能观察 NM 静默，不能证明睡眠）”改为“总线睡眠仍不可直接观测；相位是按超时/等待时间的推断，必须标 inferred”。

## 11. 测试计划

### 11.1 Core（`NmSilencePhaseAnalyzerTests`）

1. `Gap_WithNmSenderAppFramesInReleaseWindow_EmitsReleaseWaitStillActive`
2. `Gap_WithAppFramesStoppingAtTimeout_EmitsPrepareSleep`
3. `Gap_WithNmSenderAppFramesBeyondDrainGrace_EmitsUnknownContradiction`
4. `Gap_WithOnlyQueueDrainTail_EmitsPrepareSleep`
5. `Gap_WithNoFrames_EmitsSingleUnknownNoPhases`
6. `Gap_WithUnattributedFrames_EmitsUnknownAndUnknownNote`
7. `Gap_WithNmStsRepeatAtLastFrame_EmitsUnknownNoPhases`
8. `Gap_WithNmStsReadySleep_StrengthensBasisButStillInferred`
9. `Gap_WithoutNmStsRole_DoesNotMentionNmSts`
10. `Gap_WithWakeupAppFramesWithinGrace_DoesNotContradict`
11. `TailGap_LastPhase_IsWindowTruncated`
12. `InterEpisodeGap_BoundaryKnown_NotTruncated`
13. `SleepWindow_WithoutExplicitWaitBusSleep_EmitsUnknownDefaulted`
14. `SleepWindow_WithExplicitWaitBusSleepAndLiveBus_EmitsSleep`
15. `SleepWindow_WithNmSenderAppFrames_EmitsUnknownContradiction`
16. `HeadSilence_EmitsNoPhases`
17. `PhaseBoundaries_UseGlobalTimingNotPerMessageOverride`
18. `Phases_NeverProduceFindings`
19. `PartialReport_BeforeGap_PhasesEmpty`
20. `RealTrace_Nm16_EmitsReleaseWaitAndPrepareSleep`（真实 BLF，`[Trait("Manual","true")]` + 缺文件 skip）
21. `Gap_ShortPrepareWindowWithContinuousTraffic_EmitsUnknown`（review HIGH-1）
22. `TailGap_ShorterThanDrainGraceWithFrames_EmitsUnknownWindowTooShort`（review HIGH-2）
23. `Gap_WithOnlyUnattributedFramesInReleaseWindow_EmitsUnknown`（review MEDIUM-4）
24. `Gap_ShortPrepareWindowWith500msTraffic_EmitsUnknown`（review NEW-M1）
25. `SleepWindow_WithNmSenderAppFrames_EmitsUnknownContradiction`
26. `InterEpisodeGap_BoundaryKnown_NotTruncated`
27. `Analyze_CancelAfterEpisodes_*` 断言 `SilencePhases` 为空（partial 报告）

### 11.2 App

1. `BuildNetworkTimelineRow_SilencePhases_EmitThreeKinds`
2. `BuildNetworkTimelineRow_PhasesReplaceGenericSilenceBand`
3. `BuildNetworkTimelineRow_PartialCoverage_KeepsUnknownResidual`
4. `BuildNetworkTimelineRow_NoPhases_KeepsLegacySilenceBand`
5. `BuildNetworkTimelineRow_DoesNotMergeAcrossKinds`
6. `BuildNetworkTimelineRow_TailPhase_MarksTruncated`
7. `SetNmSegmentHover_ReleaseWait_ShowsSenderEvidence`
8. `SetNmSegmentHover_PrepareSleep_ShowsDrainAndQuietEvidence`
9. `SetNmSegmentHover_Sleep_ShowsCaveatNotMeasured`
10. `Legend_ListsSilencePhaseKinds`
11. `SilencePhaseBrushes_AreDistinct`
12. `BuildTimelineRows_EmitsOnlyDeclaredKinds`（更新 kind 列表）
13. `Markdown_EmitsSilencePhasesSection`
14. `Markdown_WithoutSilencePhases_OmitsSection`
15. `Csv_AppendsSilencePhaseSection_FirstHeaderUnchanged`
16. `GetNmOverview_IncludesSilencePhases`
17. `NmPromptRules_SilencePhasesAreInferred`（不新增编号规则）

## 12. 风险与缓解

| 风险 | 缓解 |
|---|---|
| 未声明的 NM 报文被当成应用帧，误判“仍在网络模式” | 输出 Top 发送者与 CAN ID；加 unknown `silence_phase_non_nm_may_include_undeclared_nm`；不做自动豁免 |
| 应用报文 sender 归属错误（网关转发） | 只把 J1939 SA/DBC sender 当“归属证据”，Detail 写“归属依据”；未归属不升级 |
| 清空发送缓存/唤醒宽限取值是假设 | 由 `CycleMs`/`WakeUpMs` 派生并写入 assumptions；profile 可覆盖 |
| 预睡眠期间仍有少量应用报文是合规的 | 宽限内允许；超过宽限标 `AppTxAfterTimeout`，不是 finding，只是 Unknown |
| 尾部窗口无后续证据 | 不推断 `Sleep`；标 `Unknown` + 截断 |
| 短 w2（< 清缓存宽限）观察到连续流量 | `Unknown / WindowTooShort`，不得升级为 `PrepareSleep`（review HIGH-1/2） |
| 判据窗被宽限夹空 | 改用最长安静跨度；无 ≥ DrainGrace 的安静段 → `AppTxAfterTimeout` |
| `WaitBusSleep` 默认值被当成配置 | `WaitBusSleepDeclared` 显式传递；未声明不出 `Sleep` |
| 报告缓存命中旧结果 | EngineVersion bump |
| 图例/kind 测试连锁 | 只加 Run 不加 TextBlock；更新 `EmitsOnlyDeclaredKinds` 与 `All` |

## 13. 决策记录

| 编号 | 决策 | 理由 |
|---|---|---|
| D1 | 相位是 `inferred`，永不出 finding | 内部定时器不可观测；项目硬约束“绝不猜” |
| D2 | 必须做发送者归属，不能只看“有无应用报文” | 真实 trace 前 2 s 的 354 帧全来自 BMS；朴素规则会误标 |
| D3 | 清缓存宽限取 1 个 NM 周期 | §5.3.2 允许清空发送缓存；周期是报告里已有的时序参数 |
| D4 | 唤醒宽限取 `max(CycleMs, WakeUpMs)` | 下个 episode 前的应用报文属于唤醒序列，不能算“睡眠期违规” |
| D5 | 未显式配置 WaitBusSleep 时不推断 Sleep | 默认值不能当结论 |
| D6 | 首部静默不做相位 | 没有超时锚点，属记录边界 |
| D7 | `NM_ALL_NM_SILENT` 保持不变 | 现有契约、AI 输出、真实 trace 回归门不动 |
| D8 | 新增 `NmSilencePhase` 而非复用 `NmEvent`/`NmObservation` | 需要区间、依据、归属统计、截断、NMSts 证据；事件只有单点 |
| D9 | `NmReport` 用 init 属性而非 positional 参数 | 12 处现有构造点零破坏 |
| D10 | 短 w2 有帧时输出 `WindowTooShort`，不判 `PrepareSleep` | 短窗口看不到“清缓存后是否停发”；连续流量曾实测被误升级（review HIGH-1/2） |
| D11 | w1 只有未归属帧时输出 `Unknown/Unattributed` | 未归属帧可能包含本网络发送者自己的应用报文，不能作为释放依据（review MEDIUM-4） |
