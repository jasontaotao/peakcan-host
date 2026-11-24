# NM M2：企标包与语义层设计（Rev 5）

- 日期：2026-11-23（Rev 5，S1–S3 与 S4 部分实施后的回溯对齐）
- 状态：设计（Rev 5 待评审）
- 上游：`2026-11-19-nm-ai-analysis-design.md`（M1，已交付）、§6.5 企标包、§14.2 M2 分期
- 首批企标输入：`02 _ 吉利商用车CAN网络管理需求规范_V2.0_20250227.pdf`（「吉利规范」）
- 修正历史见 §16（Rev 2→3）、§17（Rev 3→4）、§18（Rev 1→2）、§19（Rev 4→5）

## 1. 目标与边界

### 1.1 目标

1. 把企标差异从代码搬进**声明式、可版本化、可校验**的 profile 文件；换一家车企换一份文件。
2. 让引擎按 profile 产出**可追溯到条款**的 finding（`rule_id` + `requirement` + `source_ref`）。
3. 让 `bit_change` 这类原始事件带上**位语义**（「byte1.bit4（主动唤醒位）」）。
4. **明确区分「观测」与「违规」**：不可观测的内部状态一律标 `Unmodeled`，不得当作违规。

### 1.2 非目标

- 不做通用规则表达式语言（闭集原语）。
- 不做完整企标编辑器（本轮只读选择器 + 标定面板）。
- 不建模节点内部状态机、定时器状态、唤醒源、网关跨网段协同（见 §3）。
- 不做 Byte2–7 唤醒原因的业务语义（吉利规范明确「由零部件功能规范/信号列表提供」）。

### 1.3 已确认前提

| 项 | 结论 |
|---|---|
| OEM 范围 | 吉利商用车为首个；schema 预留多厂（三层继承） |
| 验证数据 | 真实 trace + DBC；网络本身可能存在问题（这是要检出的对象） |
| Profile 格式 | YAML（引入 YamlDotNet） |
| UI 范围 | 只读企标选择器 + 标定试算面板 |

## 2. 吉利规范事实

### 2.1 报文布局 §5.4.1

| 字节 | 含义 |
|---|---|
| Byte0 | ECU Address（源节点标识） |
| Byte1 | Control Bit Vector |
| Byte2–7 | User Data 0–5 |

- 扩展帧 ID：`0x18FFFA00 ~ 0x18FFFAFF`，低字节 = 源地址 SA
- 标准帧 ID：`0x400 ~ 0x47F`，ID = 0x400 + ECU Address
- 未使用位置按 0 填充

**字节编号 0-based，已用真实 DBC 的 start bit 双向核对**（§14.2）。

### 2.2 CBV 位定义 §5.4.3

| 位 | 名称 | 语义 | 本项目 |
|---|---|---|---|
| bit0 | 重复报文请求（RMR） | 0=未请求，1=请求 | **均未使用，要求填 0** |
| bit1–3 | 预留 | — | 必须填 0 |
| bit4 | 主动唤醒位（AWB） | 0=被动唤醒，1=主动唤醒 | 使用 |
| bit5–7 | 预留 | — | 必须填 0 |

**吉利规范没有 PNI 位**。AUTOSAR 的 bit6 PNI 在吉利是预留位。`NM_PNI_WHILE_PN_DISABLED` 必须由 profile 声明驱动，不能硬编码。

### 2.3 用户数据 §5.4.4

- Byte2–7：用户自定义，吉利用于「保持网络唤醒状态的原因」，**具体含义由零部件功能规范/信号列表提供**。
- Byte3 bit0 = NMSts：0=Repeat Message State，1=Normal Operation State；bit1–7 预留填 0。
- NMSts 是吉利特有标志，AUTOSAR 标准无此位。

### 2.4 时序参数 §5.5

| 参数 | 标称值 | 容差 | 前置状态 |
|---|---|---|---|
| T_NM_MessageCycle | 500 ms | ±10% | 正常发送子状态 / 常规操作状态 |
| T_NM_ImmediateCycleTime | 20 ms | ±10% | 快速发送子状态 |
| N_ImmediateNM_TIMES | 10 次 | — | 快速发送帧数 |
| T_REPEAT_MESSAGE | 1600 ms | ±10% | **含快发 1700±60ms，不含快发 1500±60ms** |
| T_NM_TIMEOUT | 2000 ms | ±10% | 网络模式内部定时器 |
| T_WAIT_BUS_SLEEP | 2000 ms | ±10% | 预睡眠定时器 |
| T_START_NM_TX | 10 ms | — | 预睡眠/准备睡眠/常规操作 → RMS 首帧 |
| T_WakeUp | 120 ms | — | **睡眠模式 → 网络模式首帧** |
| T_STARTx_AppFrame | 20 ms | — | 首帧 NM 后应用报文 |
| TWait_DiagReq | 5000 ms | ±10% | 诊断请求保活 |

**T_REPEAT_MESSAGE 有两个实际执行区间**，不是单一 1600±10%。

## 3. 可观测性分级（本设计的核心约束）

任何规则必须先声明它属于哪一类。**这是 Rev 1 最主要的错误来源**。

| 级别 | 含义 | 处理 |
|---|---|---|
| **O（直接观测）** | 帧内容/时序可直接量出 | 可产 finding，必须带证据时刻 |
| **P（部分观测）** | 能观测行为，但语义有歧义 | 可产 finding，**必须标 `IsInference=true`** 且文案写明歧义 |
| **U（不可观测）** | 依赖内部状态/定时器/唤醒源 | **只能进 `Unmodeled`**，禁止产 finding |

### 3.1 判定原则

1. **节点内部定时器状态不可观测**：T_NM_TIMEOUT / T_WAIT_BUS_SLEEP 是否重置、是否到期，trace 里看不到。只能看到「有没有发帧」。
2. **「停止发送」不等于「违规」**：规范 NM_09/NM_12 要求节点满足睡眠条件后**停止发送** NM，这是正常行为。发送间隔变大可能是释放，也可能是掉线，二者不可区分。
3. **唤醒源不可观测**：只能从行为（是否快发、AWB 值）间接推断，且推断不唯一。
4. **单节点静默只有在「其它 NM 节点仍在发」时才有歧义可言**；全网 NM 静默**不判违规，也不判「正常」**——释放/下电/记录停止/bus sleep 四种原因不可区分，只能标 inferred。

## 4. 条款覆盖表（NM_01–NM_17 + 各节）

| 条款 | 内容 | 级别 | 原语 | finding / 说明 |
|---|---|---|---|---|
| NM_01 | KL30 上电初始化 → 睡眠 | U | — | 不可观测 |
| NM_02 | 睡眠 + 远程唤醒 → RMS 正常发送 | P | `normal_resume_shape` | 可观测「静默后以正常周期恢复」，唤醒源未知 |
| NM_03 | 睡眠 + 本地唤醒 → 快速发送 + AWB=1 | P | `burst_shape` + `bit_rule`（`is_one`） | 快发形态可验；AWB 值可验；唤醒源不可验。**v1 只实现快发形态**：`bit_rule` 仅支持 `is_zero`，AWB=1 需 `is_one` + wakeup 作用域，未实现 |
| NM_04 | 计数器到 0 → 正常发送子状态 | O | `burst_shape` | 快发段结束、间隔回到正常周期 |
| NM_05 | RMS 下 T_NM_TIMEOUT 超时 → 状态不变、定时器重置 | U | — | **内部定时器不可观测** |
| NM_06 | RMS T_REPEAT_MESSAGE 超时 → NOS | P | `repeat_message_duration`（内部读 NMSts 作锚点） | NMSts 不是独立规则，是 `repeat_message_duration` 内部通过 `FindNmstsRole` 读取的锚点（§7.2）。双区间拆成 `with_burst=true/false` 两条规则 |
| NM_07 | NOS + 重复报文请求 → RMS 快速/正常 | U（跳转） | — | 规范明确「跳转逻辑均不开发」；**但 RMR 位取值本身是 O**（§5.4.3） |
| NM_08 | NOS 下 T_NM_TIMEOUT 超时 | U | — | 内部定时器不可观测 |
| NM_09 | NOS 满足睡眠条件 → RSS 停发 | P | `stop_sending` | 停发可观测；「满足睡眠条件」不可观测 |
| NM_10 | RSS + 本地唤醒 → NOS | P | `normal_resume_shape` | 恢复后目标是 **NOS（NMSts=1）**，与 NM_02/15 的 RMS（NMSts=0）不同。用 `expect_nmsts=1` 区分（§7.2） |
| NM_11 | RSS + 重复报文请求 → RMS | U（跳转） | — | 同 NM_07 |
| NM_12 | RMS 超时 + 睡眠条件 → RSS | P | `stop_sending` | 同 NM_09 |
| NM_13 | RSS + T_NM_TIMEOUT 超时 → 预睡眠 | U | — | 内部定时器 |
| NM_14 | 成功收发一帧 → T_NM_TIMEOUT 重置 | U | — | 内部定时器 |
| NM_15 | 预睡眠 + 远程唤醒 → RMS 正常发送 | P | `normal_resume_shape` | 同 NM_02 |
| NM_16 | 预睡眠 + 本地唤醒 → RMS 快速发送 | P | `burst_shape` + `bit_rule`（`is_one`） | 同 NM_03（AWB 半边同样未实现） |
| NM_17 | 预睡眠 + T_WAIT_BUS_SLEEP 超时 → 睡眠 | U | — | 内部定时器 |
| §5.4.1 | 报文布局、ID 范围 | O | `layout_check` | `NM_LAYOUT_VIOLATION` |
| §5.4.2 | NID = ID 映射（扩展帧低字节 / 标准帧 ID−0x400） | O | `nid_id_mapping` | `NM_NID_MISMATCH` |
| §5.4.3 | CBV 位值；预留位填 0；RMR 填 0 | O | `bit_rule` | `NM_RESERVED_BIT_SET` / `NM_RMR_SET_WHILE_DISABLED` |
| §5.4.4 | NMSts 状态位；预留位填 0 | O | `bit_rule` | `NM_RESERVED_BIT_SET` |
| §5.4.4 | 用户数据业务含义 | U | — | 依赖信号列表，不在本工具 |
| §5.5 | 周期 500ms ±10% | O | `cycle_tolerance` | `NM_CYCLE_DEVIATION` |
| §5.5 | 快发 10×20ms ±10% | O | `burst_shape` | `NM_WAKEUP_BURST_MISMATCH` |
| §5.5 | T_START_NM_TX 10ms / T_WakeUp 120ms | P | `first_frame_latency` | 只能测相邻帧间隔，不能测相对唤醒请求的延迟 |
| §5.5 | T_REPEAT_MESSAGE 双区间 | P | `repeat_message_duration` | `NM_REPEAT_MESSAGE_DURATION_EXCEEDED`；需知道是否含快发，靠 NMSts/行为推断 |
| §5.8 | 故障处理（总线不可用） | U | — | 总线不可用与静默不可区分 |
| §5.9 | 诊断报文不唤醒、TWait_DiagReq 5s | U | — | 需诊断帧寻址配置，M3 |
| 附录 A | 网关跨网段协同 | U | — | 需多通道 + 拓扑，M3 |

**`Unmodeled` 清单必须出现在报告与面板中**，AI 不得对未建模条款给结论。

### 4.0 规则 ID 与条款号是两个不同的东西（Rev 5 明确）

§4 表里多条条款共用一个原语、同一份证据：

| 原语 | 覆盖条款 |
|---|---|
| `normal_resume_shape` | NM_02 / NM_10 / NM_15 |
| `burst_shape` | NM_03 / NM_04 / NM_16 |
| `stop_sending` | NM_09 / NM_12 |

因此**不能用条款号当规则 ID**——那会为同一份判据造出多条重复规则，同一条 finding 报多遍。

**约定**：

- **规则 ID 是描述性的**（`NM_BURST_SHAPE` / `NM_RESUME_NORMAL` / `NM_STOP_SENDING` / `NM_NID_MAPPING` / `NM_RMR` / `NM_RMS_DURATION_BURST` / `NM_RMS_DURATION_NOBURST` / …），只用于定位规则、不承担条款语义。
- **条款号由规则上的 `clauses` 字段显式声明**（见 §5.3），一条规则可挂多条条款。
- **finding 的条款号是列表，不是单值**（见下）。

**为什么必须是列表**：`burst_shape` 覆盖 NM_03 / NM_04 / NM_16。若 finding 只带一个 `RequirementId`，就必须挑一条当「主条款」——但 `NmBurstDetector` 只观测「10 帧 × 20ms」形态，**分不出是睡眠唤醒、预睡眠唤醒还是计数器归零**（唤醒源不可观测，§3）。挑 NM_03 等于断言「这是睡眠 + 本地唤醒」，证据不支持。

这不是「丢信息」，是**造假归因**。而且「无 finding」会被 AI 读成「NM_04/16 通过」——正是 §3 最忌讳的静默通过。

因此 `NmFinding` 新增 `IReadOnlyList<string> Clauses`，新代码一律读 `Clauses`。

**`RequirementId` 的兼容策略：不能用 `Clauses[0]`。** 列表顺序一变（比如 YAML 里重排 `clauses:`），旧字段就换了一个条款——缓存、报告、断言全部漂移，而这是一次「无害重排」引发的。Rev 5 决定：

- `RequirementId` **标记为已弃用**，新报告不填。
- 若必须保留一段时间，取值规则必须是**确定性**的（如按 ordinal 排序后的第一个），不能依赖声明顺序。
- 消费方（`GetNmOverviewTool`）改用 `Clauses`。

**值域必须先定（Rev 5 未闭合，见 §19.5）**

`rule.clauses` 里存什么，有两种互斥选择，**必须二选一并在全文统一**：

| 方案 | 值示例 | 优点 | 缺点 |
|---|---|---|---|
| A. 存复合 `Id` | `GEELY:§5.5/cycle` | 唯一、可继承、跨文档不撞 | 冗长；报告与 AI 看到的是长串，需额外映射回 `Clause` 展示 |
| B. 存裸 `Clause` | `§5.5` | 简洁、直接可展示 | **撞键**：§4 表里 `§5.5` 出现 4 次、`§5.4.4` 出现 2 次，无法定位到具体行 |

**Rev 5 倾向方案 A**（唯一性优先），但**本节暂不锁定**——因为下游展示链路未定义，锁了会制造返工。

**四条契约测试（在值域确定前，其中两条必然空转）**：

| 检查 | 做法 | 现状 |
|---|---|---|
| 形状合法 | 锚定正则。方案 A 用 `\A[A-Z]+:...\z`，方案 B 用 `\ANM_\d{2}\z` 等 | **取决于值域选择** |
| 成员资格 | 每个条款引用必须存在于目录，加载期报错 | 需先打通展平链路（`Flattened` 无 `Clauses`，见 §19.4） |
| 完整性 | 每条 OEM 规则的 `Clauses` 非空 | 需先能区分 OEM/base（当前只能靠 `Extends == null`，会误判） |
| 不得是规则 ID | 断言引用项 ≠ 任何规则 ID | **方案 A 下恒真**（复合 Id 与裸规则名永不相等）→ 空转 |

**必须承认的局限（Rev 5）**：这四条能挡住「引用了不存在的条款」，**挡不住「引用了存在但语义错误的条款」**。仓库里已有活反例——`geely-cv@2.0.nmstd.yaml` 的 `NM_09` 写 `source_ref: §5.9`，但 §4 表里 §5.9 是「诊断报文不唤醒」（U 级、M3），NM_09 是「RSS 停发」。四条检查没有一条能发现它。

**要挡住语义错挂，只能靠人工 review 或 CI 解析 §4 表做双向对照**（§11.1 的「追溯矩阵完整」，目前不存在）。不要宣称这四条测试提供了语义正确性保证。

**当前已是 active bug（不是未来风险）**：`NM_01` 规则实际是 §5.4.2 NID 映射、`NM_05` 规则实际是 §5.4.3 RMR 位——而 spec §4 里 `NM_01` 是 KL30 初始化、`NM_05` 是 T_NM_TIMEOUT，**两者都是 U 级不可观测**。今天的报告会给一个「不可观测条款」发 NID finding。

### 4.1 关键修正：单节点静默不是违规

Rev 1 把「某节点 2.964s 未发」当作 T_NM_TIMEOUT 违规，**错误**。规范 §5.3.3.3 明确要求准备睡眠状态下停止发送 NM，随后经 T_NM_TIMEOUT、T_WAIT_BUS_SLEEP 正常进入睡眠。

正确判定（沿用 M1 `NmGapAnalyzer` 的既有逻辑，**两个阈值不可混淆**）：

| 检查 | 阈值 | 现有 code | 说明 |
|---|---|---|---|
| 全网 NM 静默 | `TimeoutMs`（2000ms） | `NM_ALL_NM_SILENT`（info） | `NmGapAnalyzer.cs:29,42` |
| 全总线无任何帧 | `max(2×cycle, 500ms)` | `NM_BUS_WIDE_SILENCE`（warn） | `NmGapAnalyzer.cs:32,143` |
| 单节点静默 | 该报文 `TimeoutMs` | `NM_SENDER_SILENT_BEYOND_TIMEOUT`（warn，inferred） | `NmGapAnalyzer.cs:99-137` |

| 情形 | 结论 |
|---|---|
| 单节点静默 > timeout，**且其它 NM 节点仍在发** | `NM_SENDER_SILENT_BEYOND_TIMEOUT`（inferred，warn） |
| 单节点静默 > timeout，**全网 NM 都静默** | **不报违规**，产 observation；原因不可区分（释放/下电/记录停止/bus sleep） |
| 全网 NM 静默 > `TimeoutMs` | `NM_ALL_NM_SILENT`（info，原因未知）；应用报文是否存在只作归因证据，**不是必要条件** |
| 全总线无帧 > `max(2×cycle,500ms)` | `NM_BUS_WIDE_SILENCE`（warn）；不得据此判断节点掉线 |

**静默分析的单位必须是 `(CAN ID, extended, NID)`，不是 CAN ID。** 现有 `NmGapAnalyzer.cs:87` 要求 `perMessage.Length > 1` 才做单报文静默分析，共享 ID 下多个 NID 被合并成一组，会漏掉「一个节点停、另一个同 ID 节点仍发」。M2 必须按 NID 拆分；无法可靠拆分时显式标 `indeterminate`，不得视为无问题。

## 5. 数据模型

### 5.1 位角色

```csharp
/// <summary>CBV/内容字段的一位语义。profile 声明哪一位存在，代码不假设。</summary>
public sealed record NmBitRole(
    byte Byte,
    int Bit,
    string Name,                  // "ActiveWakeup" / "RepeatMessageRequest" / "NmSts"
    string? Meaning = null,       // "0=被动唤醒, 1=主动唤醒"
    NmBitConstraint Constraint = NmBitConstraint.None);

public enum NmBitConstraint
{
    None = 0,                // 有合法语义（如 NMSts 的 0/1 都合法）
    MustBeZero,              // 预留位：违反报 NM_RESERVED_BIT_SET
    MustBeZeroWhileDisabled, // 已定义但本项目禁用（如 RMR）：违反报 NM_RMR_SET_WHILE_DISABLED
}
```

`NmLayout` 增加 `IReadOnlyList<NmBitRole>? BitRoles = null`。

**位角色的两个维度必须分开合并**（Rev 2 说「DBC 优先」会让 profile 无法施加 OEM 约束）：

| 维度 | 权威来源 |
|---|---|
| **位置与名称**（byte/bit/Name） | DBC 信号优先；DBC 无信号时用 profile |
| **约束与禁用语义**（Constraint） | **OEM profile 权威**，可覆盖 DBC |

合并粒度是**单个 `(byte, bit)`**，不是「整个 DBC 角色优先」。这样 OEM 能对 DBC 已定义的位施加「必须为 0」约束。

**禁止**从 `user_data_offset` 反推位角色。

### 5.1.1 校验器与 DBC 信号重叠的处理

DBC 有信号定义 ≠ 可以豁免 OEM 的显式零约束。真实 BMS 的 byte4–7 有 `BMS_Stayawake_reasons:32|32`，sidecar 声明 `user_data_offset=4`。

| 情形 | 处理 |
|---|---|
| DBC 有信号、无零约束 | 跳过通用「未使用位」检查 |
| OEM 显式 `MustBeZero` 且与 DBC 信号重叠 | **仍执行**约束检查；重叠记 `NM_CONFIG_CONFLICT` |
| profile 声明 `MustBeZero` 但 DBC 无信号 | 正常执行 |

### 5.2 容差与统一时序

```csharp
/// <summary>全局默认时序（来自 profile，不含报文级覆盖）。</summary>
public sealed record NmGlobalTiming(
    int CycleMs,
    int TimeoutMs,
    int WaitBusSleepMs,
    int RepeatMessageWithBurstMs,      // 1700
    int RepeatMessageWithoutBurstMs,   // 1500
    int ImmediateCycleMs,              // 20
    int ImmediateCount,                // 10
    int StartNmTxMs,                   // 10
    int WakeUpMs,                      // 120
    double ToleranceRatio);            // 0.1

/// <summary>单报文的生效时序：报文级优先，其次全局。所有分析阶段必须用这个。</summary>
public sealed record NmEffectiveTiming(
    int CycleMs,
    int TimeoutMs,
    int WaitBusSleepMs,
    int RepeatMessageWithBurstMs,
    int RepeatMessageWithoutBurstMs,
    int ImmediateCycleMs,
    int ImmediateCount,
    int StartNmTxMs,
    int WakeUpMs,
    double ToleranceRatio);

/// <summary>报文级覆盖优先于全局（现有 NmAnalyzer.cs:210-211 的 def.TimeoutMs ?? profile 顺序）。</summary>
public static NmEffectiveTiming Resolve(NmMessageDef def, NmGlobalTiming global);
```

**关键裁决（影响 S2/S3/S6）**：

| 概念 | 含义 | 谁能拿到 |
|---|---|---|
| `NmGlobalTiming` | profile 展平后的全局值，**不含报文级覆盖** | profile 加载层 |
| `NmEffectiveTiming` | `Resolve(def, global)` 的结果，**已含报文级覆盖** | 所有分析阶段 |

**所有分析阶段（含 `NmActivityBuilder`）必须接收 `NmEffectiveTiming`，不得接收 `NmGlobalTiming`。** 否则会丢掉报文级 `GenMsgCycleTime` / `NmAsrCanMsgCycleTime` 的优先级，`NmSpecClosureTests` 会红。

现有 `NmAnalyzer.cs:255` 传的是 `def.CycleMs ?? cycleMs`（报文级优先），改造后必须保持这个语义——`Resolve` 内部就是做这件事。

#### 5.2.1 默认值注入点（唯一）

新类型字段全部非空，但现有代码有 `?? 500` / `?? 2000` 兜底（`NmAnalyzer.cs:170-171`）。**默认值只在一处注入**：

```csharp
public static class NmTimingDefaults
{
    // M1 默认值（spec §15.2 O6）
    public const int CycleMs = 500;
    public const int TimeoutMs = 2000;
    public const int WaitBusSleepMs = 2000;
    public const int RepeatMessageWithBurstMs = 1700;
    public const int RepeatMessageWithoutBurstMs = 1500;
    public const int ImmediateCycleMs = 20;
    public const int ImmediateCount = 10;
    public const int StartNmTxMs = 10;
    public const int WakeUpMs = 120;
    public const double ToleranceRatio = 0.2;   // M1 的 max(0.2×cycle,30ms)

    /// <summary>唯一默认值注入点：profile 值优先，缺失时用 M1 默认。</summary>
    public static NmGlobalTiming FromProfile(NmTimings? timings, NmLayout? layout, NmFeatures? features);
}
```

| 层 | 职责 |
|---|---|
| `NmTimingDefaults.FromProfile` | profile 值 ?? M1 默认 → 非空 `NmGlobalTiming` |
| `Resolve(def, global)` | 报文级覆盖（`def.CycleMs` / `def.TimeoutMs`）→ `NmEffectiveTiming` |
| 分析阶段 | 只读 `NmEffectiveTiming`，**不得再写 `?? 500`** |

**`ToleranceRatio` 的 M1 默认是 0.2，不是 0.1**：现有 `max(0.2×cycle, 30ms)`，吉利 profile 显式配 0.1 才收紧。这样无 profile 时行为不变。

`RepeatMessageWithBurstMs`/`WithoutBurstMs` 在 M1 无对应配置，默认 1700/1500 只在 inferred 路径使用，且 `NmTimings.RepeatMessageMs` 有值时优先（兼容 M1 的单一 `RepeatMessageMs`）。

**所有阈值调用点共用**（Rev 2 只列了 7 处，仍漏 5 处）：

| 调用点 | 现有阈值 | 位置 |
|---|---|---|
| `NmCycleAnalyzer` 周期容差 | `max(0.2×cycle, 30)` | `:113-114` |
| `NmCycleAnalyzer` 漏帧剔除 | `1.5×cycle` | `:31` |
| `NmAnalyzer` 报文 timeout | `def.TimeoutMs ?? 2000` | `:209-211` |
| `NmAnalyzer` SilentBeyondTimeout | 同上 | `:261-265` |
| `NmEpisodeBuilder` 切分 | `profile.Timings.TimeoutMs ?? 2000` | 切分阈值 |
| `NmGapAnalyzer` 单报文静默 | `def > profile > 2000` | `:99-102` |
| `NmGapAnalyzer` 全网 NM 静默 | `timeoutMs` | `:29,42` |
| `NmGapAnalyzer` 全总线静默 | `max(2×cycle, 500)` | `:32,143` |
| `NmContentAnalyzer` 漏帧边界 | `1.5×cycle / timeout` | `:168-183` |
| `NmContentAnalyzer` 快发检测 | `cycle / 0.5×cycle` | `:76-99` |
| `NmActivityBuilder` 唤醒窗 | `repeatMessageMs ?? cycleMs` | `:29,77` |
| `NmActivityBuilder` 快发识别 | `0.5×cycle` | `:123-140` |
| `NmCapabilityGate` 周期/超时建议 | `3×cycle` | `:50-82` |

**无 profile 时全部回落到 M1 默认值**，行为不变；四种组合必须有测试（无 profile / 报文级覆盖 / sidecar / OEM 容差）。

### 5.3 规则

**本节代码块与 `NmStandardProfile.cs` 实际定义一致（Rev 5 契约对齐）**。此前 Rev 4 的草案把 `Observability` 写成第 4 个位置参数、把 `Phase`/`SourceRef` 位置错排，按草案实施会重排全部 34 个构造点。

```csharp
/// <summary>一条企标规则。</summary>
public sealed record NmRuleDef(
    string Id,                           // 描述性规则名，不是条款号（§4.0）
    string Requirement,                  // 本地完整条款文本
    string Phase,                        // wakeup | normal | sleep（**不驱动求值作用域**，见下）
    NmRuleWhen When,
    NmRuleExpect Expect,
    string FindingCode)
{
    public string? SourceRef { get; init; }
    public NmObservability Observability { get; init; } = NmObservability.Observed;

    // Rev 5 新增。见 §4.0。
    // 值域：条款目录的 NmClauseDef.Id（复合键，含文档前缀）。
    // OEM profile 必填非空；base profile 允许为空（其条款不在 OEM 命名空间内）。
    // 用 required init 属性而非位置参数：不改变现有 6 个位置参数的顺序，
    // 34 个构造点全部编译失败并给出清晰错误（这是刻意的摩擦，不静默漏填）。
    public required IReadOnlyList<string> Clauses { get; init; }
}
```

**`Phase` 的现状与裁决（Rev 5 收紧）**：引擎**全窗口求值所有规则**，`phase` 不驱动作用域——`NmStandardProfile.cs` 的 `NmRulePhases` 注释已写明「spec 未定义按阶段过滤的语义」。校验器只拒绝拼错的值。

**光加警告不够。** `phase` 是每条规则的必填字段，YAML 里写 `phase: wakeup` 的用户自然会认为作用域生效；而它会让 S4/S5 的抑制与覆盖统计出错（一条 `phase: sleep` 的规则在全窗口跑，产出的 finding 会被归到错误的阶段）。

**Rev 5 裁决：采用方案 B（改名 + 标注）。**

| 项 | 决定 |
|---|---|
| C# 属性 | `Phase` → `DeclaredPhase`（编译器强制 34 个构造点更新，不静默漏改） |
| YAML 首选键 | `declared_phase` |
| YAML 兼容键 | `phase` 仍接受，但加载期产一条 assumption：「`phase` 已改名 `declared_phase`，且该字段不驱动求值作用域」 |
| 报告 | `NmReport.Assumptions` 固定输出一条：「规则 declared_phase 为声明性元数据，它不驱动求值作用域，引擎对全部规则在全窗口求值」——只要引擎实际求值的规则集（`NmProfile.StandardRules`）里有非 `normal` 的 `DeclaredPhase`。文案用 YAML 键名而非 C# 属性名，与加载期提示同一口径 |
| 语义 | 仅展示与筛选，**不参与求值作用域** |

**为什么不选 A（删字段）**：现有 profile 已用 `phase: wakeup` / `sleep` 表达设计意图，删掉会丢信息；且删字段的破坏面与改名相同，收益更小。

**为什么不选 C（实现作用域）**：spec 从未定义 `wakeup`/`normal`/`sleep` 的时间范围与切换条件。凭空实现等于发明规范，会让现有报告结论变化。

**改名而不是只加警告**：fable 评审指出「必填字段却不生效，警告不够」——用户看到 `phase: wakeup` 会认为作用域有效。名字本身必须诚实。

**连带影响**：`expect_nmsts`（§7.2）也受此限制——只在「静默恢复后」检查 NMSts，不等于已限定「正确的 wakeup 状态」。NM_10 的完整语义需要作用域，本轮无法闭合。

**`NmFieldSelector` 在 Core 中不存在**（Rev 5 契约对齐）：实际用的是 `NmRuleWhen` 的 `Byte`/`Bit`/`Length` 字段。Rev 4 草案里的 `NmFieldSelector` 是未落地的设计，删除。

参数用结构化 record 而非字符串字典：YAML 加载时由 YamlDotNet 直接转数值，类型错误在加载期暴露，不推迟到自定义校验器。

### 5.4 标准 profile 文件模型

**本节代码块与 `NmStandardProfile.cs` 实际定义一致（Rev 5 契约对齐）**。此前 Rev 4 的草案写的是 `Patch` / `NmMessageDef` / `NmNodeDef` / `NmContentField` / `NmExceptionDef`——前三个与实现不符，后两个**在 Core 中根本不存在**。

```csharp
public sealed record NmStandardProfile
{
    public required string StandardId { get; init; }
    public string? Vendor { get; init; }
    public string Version { get; init; } = "1.0";
    public string? Extends { get; init; }
    public NmStandardSource? Source { get; init; }
    // 三态 patch 的实际承载者（见 §6.2）。Rev 4 草案叫 Patch，实现叫 Overrides。
    public NmStandardOverrides? Overrides { get; init; }
    public IReadOnlyList<NmStandardMessage>? Messages { get; init; }
    public IReadOnlyList<NmStandardNode>? Nodes { get; init; }
    public IReadOnlyList<NmRuleDef>? Rules { get; init; }

    // 加载期提示（§5.3 旧键 phase 改名）。逐文件产生；报告读的是展平后
    // NmStandardFlattener.Flattened.LoadAssumptions（链级合并去重），不是这一层。
    public IReadOnlyList<string> LoadAssumptions { get; init; } = Array.Empty<string>();

    // 条款目录（见 §4.0 / §5.5）——**尚未落地**：`NmClauseDef` 在 Core 中不存在，
    // 条款目前只以字符串形式挂在 NmRuleDef.Clauses 上。见 §19.5「条款目录全链路」。

    // 便利访问器：绝大多数调用方只关心展平后的值，不需要知道它们在 Overrides 下。
    public NmStandardTimings? Timings => Overrides?.Timings;
    public NmStandardLayout? Layout => Overrides?.Layout;
    public NmStandardFeatures? Features => Overrides?.Features;
}
```

**`content` / `exceptions` 两段在实现中不存在**（Rev 5 契约对齐）。Rev 4 的 §11 边界表说它们「可选段，暂不解析，S3 补解析」——实际从未落地，Core 里没有 `NmContentField` / `NmExceptionDef` 类型。Rev 5 明确：**它们是未实现的设计，不是「暂缓解析的可选段」**。若将来要加，需先定义类型与语义。

**`NmStandardOverrides`（实现中的三态 patch 承载者）**：

```csharp
public sealed record NmStandardOverrides
{
    public NmStandardTimings? Timings { get; init; }
    public NmStandardLayout? Layout { get; init; }
    public NmStandardFeatures? Features { get; init; }

    /// <summary>要显式删除的父级项。支持 bit_roles[byte.bit] / rules[id] / messages[id] / nodes[name]。</summary>
    public IReadOnlyList<string>? Remove { get; init; }
}
```

**`ApplyRemove` 的既有缺陷（Rev 5 记录）**：`rules.Remove(key)` 不检查 key 是否存在（`NmStandardFlattener.cs:107-109`），笔误或规则重命名后旧 `remove:` 静默变 no-op。`messages` / `nodes` / `bit_roles` 同理。规则重命名（§19 修订 #14）会放大这个缺陷，必须同批修为「remove 不存在的 key 报错」。

/// <summary>
/// 条款目录的一条（Rev 5）。数据驱动而非代码硬编码——换一家车企换一份 profile，
/// 不换一行代码（§1.1 目标 1）。
/// <para>
/// <b>ID 必须是文档内唯一键</b>：§4 表里 `§5.5` 出现 4 次（周期/快发/首帧/T_REPEAT）、
/// `§5.4.4` 出现 2 次（NMSts 位 / 用户数据业务含义）。若直接用节号当键，
/// 要么撞键、要么合并成一行导致覆盖状态失真。
/// </para>
/// <para>
/// 且 base 与 OEM 的条款不在同一命名空间：base 的 `source_ref` 是
/// 「AUTOSAR SWS CanNm 4.1.3 §9」，OEM 是「§5.x」。没有文档维度会把 AUTOSAR §9
/// 误读成吉利 §9。
/// </para>
/// </summary>
public sealed record NmClauseDef(
    // 复合唯一键：<文档>:<节号>[/<细分>]，如 GEELY:§5.5/cycle、AUTOSAR:§9。
    string Id,
    // 面向人的条款号（AI 与报告展示用）：NM_02 / §5.5 / 附录A。
    // 可重复（§5.5 有 4 条），因此**不能**当字典键。
    string Clause,
    NmObservability Level,               // O | P | U
    string Requirement,                  // 条款原文摘要
    string? SourceRef = null);

/// <summary>YAML 中的 timing 段，字段可空以表达「未覆盖」。</summary>
public sealed record NmStandardTimings(
    int? CycleMs = null,
    int? TimeoutMs = null,
    int? WaitBusSleepMs = null,
    int? RepeatMessageWithBurstMs = null,
    int? RepeatMessageWithoutBurstMs = null,
    int? ImmediateCycleMs = null,
    int? ImmediateCount = null,
    int? StartNmTxMs = null,
    int? WakeUpMs = null,
    double? ToleranceRatio = null);

/// <summary>布局 + 位角色。</summary>
public sealed record NmStandardLayout(
    byte? NidByte = null,
    byte? CbvByte = null,
    byte? UserDataOffset = null,
    int? UserDataLength = null,
    IReadOnlyList<NmBitRole>? BitRoles = null);

public sealed record NmStandardFeatures(
    bool? ActiveWakeupBitEnabled = null,
    bool? RepeatMsgIndEnabled = null,
    bool? PnEnabled = null);
```

**Rev 4 草案里的 `NmStandardPatch` / `NmStandardPatchSet` 在实现中不存在**——实际用的是 `NmStandardOverrides`（见上）。删掉，避免实施者按不存在的类型写代码。

**展平后的输出**：`NmStandardProfile`（含继承链）→ `NmProfile`（现有类型）+ `NmGlobalTiming`（§5.2）。`NmGlobalTiming` 不是独立存储，是展平产物的派生物。

**条款目录的消费规则（Rev 5 重做）**

> **⚠ 前置依赖未满足：本节的覆盖模型当前不可实现。**
>
> 三态覆盖要求引擎输出**逐规则状态**（`EvaluatedNoFinding` 等），而现有 `NmRuleResult` 只有四个聚合列表，合规规则不产任何输出（见 §7.1.1）。在 `NmRuleEvaluation` 落地之前，本节只能作为设计意图，不能作为实施依据。
>
> 同样未定的还有：`rule.clauses` 存复合 `Id` 还是裸 `Clause`（两者在 §4.0 的正则与 §5.3 的注释里不一致）、条款目录在 `Flattened`/`ApplyRemove`/`Bind`/`NmProfile`/hash 五处的链路（均未打通）。
>
> **Rev 5 决定**：本节保留为设计目标，**S5 实施不以本节为准**；待 §7.1.1 的逐规则状态落地后再回来补。见 §19.4。

「覆盖」必须是**三态**而非布尔——「求值成功」不等于「这条 trace 上验证了」：

| 状态 | 判据 | 进 `UnmodeledRules` |
|---|---|---|
| `Covered` | 有规则覆盖该条款，且其 verdict 为 `Pass` 或 `Violation`（**不是「产出了证据」**） | 否 |
| `Partial` | 规则 verdict 为 `Indeterminate`（样本不足/截断/缺配置），且无其它适用结果 | **是**，原因码 `partial_evidence` |
| `NotCovered` | 没有任何规则覆盖该条款 | **是**，原因码 `uncovered` |

**`Covered` 的判据不能写成「产出了 finding 或 observation」**：合规规则**零输出**——`BitRule` / `NidIdMapping` / `LayoutCheck` / `CycleTolerance` / `BurstShape` 在合法场景下都是 `return true` 就结束，既不产 finding 也不产 observation。按「有输出才算覆盖」的定义，**真实合规且充分求值的规则反而永远不是 Covered**。

所以覆盖必须由 §7.1.1 的**逐规则 verdict**（`Pass` / `Violation` / `NotApplicable` / `Indeterminate`）判定，不能从「findings/observations 是否非空」反推。observation 只能作为证据补充，不能替代 verdict。

**这也是本节标「不可实施」的原因**——见本节开头的警告与 §19.5。

**必须处理的七种情形（Rev 4 的五情形表漏了全部七种）**：

| # | 情形 | 状态 | 说明 |
|---|---|---|---|
| 1 | 规则成功且产 finding/observation | `Covered` | 正常 |
| 2 | 规则**部分**成功：某些报文样本不足 | `Partial` | `RepeatMessageDuration` 按报文收集 `reasons`，会同时产 finding 和 Unmodeled。**同一条规则既覆盖又未覆盖** |
| 3 | 规则返回 success 但产 `*_not_applicable` 观测 | **不参与聚合**（`NotApplicable`） | `with_burst` 不匹配时走这条路径。它既不是「覆盖」也不是「未覆盖」——是「本条 trace 上这条规则不适用」 |
| 4 | 同一条款被多条规则覆盖，结果不同 | 见下方聚合规则 | **不能简单「取最差」**，见下 |
| 5 | 目录标 `level: U`，但有规则覆盖它 | `NotCovered` + `ReasonCode = unobservable` | U 级条款无论如何不产 finding（§3）。**原因码必须是 `unobservable` 不是 `uncovered`**——两者在 §9.4 是不同码。同时加载期应拒绝「O/P 级规则挂 U 级条款」 |
| 6 | `rule.clauses` 引用了目录中不存在的 ID | **加载期报错** | 静默丢弃会让 finding 带一个查不到的条款号 |
| 7 | profile 声明了规则但完全没有 `clauses:` 段 | **加载期报错**（不是警告） | 有规则却无目录 = 所有条款都无法归因，核心特性完全失效。只有纯 base 或显式 opt-out 才允许缺目录 |

**多规则聚合规则（Rev 5 修正「取最差」）**

「取最差」是错的，而且错在主路径上：吉利的 `NM_06` / `NM_06_NOBURST` 是一对 `with_burst` 变体，**每条 trace 必然有一条 `not_applicable`**。若它参与最差聚合，加上「finding 也算 Covered」，真实违规会被稳定判成 `Partial`——§14.6 要求检出的两段真实 RMS 违规会全部降级。

正确聚合：

1. **先剔除 `NotApplicable`**——它不代表任何验证结论。
2. 剩余结果里：
   - 有任一 `Violation` 或 `Pass` → 该条款至少被验证过，可判 `Covered`（finding 仍照常产出）。
   - 只有 `Indeterminate`（样本不足/截断/缺配置），且无 `Pass`/`Violation` → `Partial`。
   - 剔除后**为空**（全部 `NotApplicable`）→ `Partial`，原因 `all_rules_not_applicable`。
3. 只有**零条规则覆盖**才是 `NotCovered`。

**对「一条 Covered、一条 NotCovered」**：若两者确实适用于同一作用域，判 `Partial`（部分已验证），不判 `NotCovered`——`NotCovered` 的定义是「没有任何规则覆盖」。

**继承与删除语义（Rev 4 未定义）**：

- 子 profile `remove: rules[<id>]` 删掉父规则后，父的条款目录仍在 → 该条款变 `NotCovered`。这是正确行为，但**必须可区分于「从未声明」**，故 `NotCovered` 的原因码要写明「规则已被 remove」。
- ~~`ApplyRemove` 对不存在的 key 静默无效~~ **已修**：`rules` / `messages` / `nodes` / `bit_roles` 四个分支在 key 不存在时一律抛 `NmProfileValidationException`，空项同样报错（`NmStandardFlattener.ApplyRemove`）。规则重命名后旧的 `remove:` 不再变成 no-op。
- 条款目录本身支持 `remove: clauses[<id>]`，语义同 rules。

**注意（含一个实现阻碍）**：U 级条款不必在 profile 的 `rules:` 里造一条不产 finding 的假规则——只在 `clauses:` 目录里声明 `level: U` 即可。

**但这在当前代码里行不通**：`NmAnalyzer.cs:352` 的门是 `if (request.Profile.StandardRules is { Count: > 0 })`——**只有 `clauses:` 目录、没有 `rules:` 的 profile 根本不会调用规则引擎**，U 级条款永远进不了 `UnmodeledRules`。

修法：把门改成 `StandardRules.Count > 0 || StandardClauses.Count > 0`，且引擎在「无规则但有目录」时仍要输出目录中未覆盖的条款。这与 §19.5「条款目录全链路未打通」是同一批改动。

**base profile 的条款归属（Rev 5 修正判据）**：base 的 `NM_CYCLE` 规则没有吉利条款可填（它是 `AUTOSAR SWS CanNm 4.1.3 §9`）。

**不能用 `Extends == null` 区分 base/OEM**——一个不继承任何东西的独立 OEM profile 会被误判成 base。当前 `NmStandardProfile` 只有 `StandardId`/`Vendor`/`Extends`，没有类型标志。

**正确判据是按规则的实际归因**，不按 profile 类型：

| 规则情形 | 处理 |
|---|---|
| `Clauses` 非空 | 每项必须在目录中解析，否则加载期报错 |
| `Clauses` 为空 | 该规则**不参与条款覆盖统计**（既不是 Covered 也不是 NotCovered） |

若产品上要求「OEM 规则必须全部可归因」，就加显式字段 `kind: base|oem`，**不要从 `Extends` 推断**。

**`Clauses` 的必填性**：`required` 只保证构造时赋值，不保证非空。非空约束由加载期校验器执行，且按上述「按归因」而非「按类型」判定。

## 6. 三层继承与展平

```
profiles/base/autosar-cannm@4.1.3.nmstd.yaml
  └── profiles/oem/geely-cv@2.0.nmstd.yaml
        └── 项目覆盖（DBC/sidecar 的报文 ID、站地址、布局）
```

### 6.1 展平规则

1. 从 `extends` 递归解析，visited set 检测循环。
2. 逐字段覆盖：项目 > OEM > base。
3. 列表字段按显式 key 合并：`messages`→`(can_id, extended)`，`nodes`→`name`，`rules`→`id`，`content.fields`→`(byte, bit)`，`exceptions`→`id`。
4. 每次覆盖记录来源到 `Provenance.ResolvedFrom`（`"autosar-cannm@4.1.3 < geely-cv@2.0 < dbc"`）。
5. 展平结果仍是 `NmProfile`，**下游消费方签名不变**。
6. `NmReport.StandardId` = 展平链的 `standard_id@version`；`ProfileHash` = **展平后 canonical profile 的 SHA256**。

### 6.2 三态 patch（区分「合法覆盖」与「事实源冲突」）

Rev 1 的「所有差异都记冲突」会让正常 overlay 全是冲突。

**三态语义不能建在最终 record 的可空属性上**：YamlDotNet 反序列化时「字段缺失」与「显式 null」对可空属性都会得到 `null`，展平阶段无法区分继承还是删除。

**实现采用的形态（Rev 5 契约对齐）**：自然 YAML 结构 + `overrides.remove` 列表，**不用 `patch:` 块**。Rev 4 的 `patch: { set: ..., remove: ... }` 草案从未落地。

```yaml
overrides:
  timings:
    cycle_ms: 500          # 出现 = 覆盖
  layout:
    bit_roles:
      - { byte: 3, bit: 0, name: NmSts, constraint: none }
  remove:
    - bit_roles[1.6]       # 显式删除 base 的 PNI 位
```

| 写法 | 语义 |
|---|---|
| `overrides` 下出现的字段 | 替换父值 |
| `overrides.remove` 列表 | 显式删除父值 |
| 两者都未出现的字段 | 继承父值 |

**为什么这个形态能区分「缺失」与「删除」**：删除必须写进 `remove:` 列表，而不是靠「把字段设为 null」——后者与「字段缺失」在反序列化后无法区分。这与 §6.2 开头的要求一致。

**`remove` 的 key 不存在时必须报错**（此前是静默无效，已修，见 §5.5 的 `ApplyRemove` 记录）：静默 no-op 会让「写了 remove 却没删掉」毫无迹象，规则改名后尤其危险。

**继承差异不记冲突**。冲突只发生在**同一事实的多个独立来源**（DBC / sidecar / project profile）给出不同值且都非空时，记 `NmConfigConflict`。

`MustBeZero` 与具名角色**允许共存**（OEM 用 `MustBeZeroWhileDisabled` 覆盖 base 的 PNI 位是合法的），校验器不得拒绝。

### 6.3 v1 简化

若多 OEM 需求尚未出现，v1 可先做**单层 OEM 完整 profile + 项目绑定**，继承框架按上述语义实现但只用两层。避免先上未验证的通用框架。

## 7. 规则引擎

### 7.1 接口

**实现现状（Rev 5 契约对齐）**——Rev 4 草案里多出的四个字段从未落地：

```csharp
public sealed record NmRuleContext(
    IReadOnlyList<NmFrame> NmFrames,
    IReadOnlyList<NmEpisode> Episodes,
    NmProfile Profile,
    // 持全局时序而非单个 NmEffectiveTiming：规则跨报文求值，传单个生效时序会让多报文场景用错周期。
    NmGlobalTiming Timing,
    NmWindow Window)
{
    public IReadOnlyList<NmRuleDef> Rules { get; init; } = Array.Empty<NmRuleDef>();
    public NmEffectiveTiming Resolve(NmMessageDef def) => NmTiming.Resolve(def, Timing);
}

public sealed record NmRuleResult(
    IReadOnlyList<NmFinding> Findings,          // 违规
    IReadOnlyList<NmObservation> Observations,  // 事实，不声称违规
    IReadOnlyList<NmUnmodeledRule> Unmodeled,
    IReadOnlyList<NmTruncation> Truncations);
```

**Rev 4 草案里有、实现中不存在的字段**：`RawFrames` / `Dbc` / `Selection`（`NmRuleContext`），以及 `Timing` 用的是 `NmEffectiveTiming` 而非 `NmGlobalTiming`。

**这个缺口影响什么、不影响什么（Rev 5 明确）**：

| 影响 | 不影响 |
|---|---|
| 任何需要原始帧或 DBC 的**规则原语**都做不了 | 数据体检——它走独立的 `NmDataQualityAnalyzer`，入参是 `NmAnalysisRequest`（含 `Frames`/`Dbc`/`Profile`/`Standard`），不经规则引擎 |
| §5.4.1 布局核对无法在规则里查 DBC 信号重叠 | 现有六个原语都不需要这些字段 |

**结论**：`RawFrames`/`Dbc`/`Selection` 要么在将来切片补上，要么从本节删掉——但**不能假装它们存在**。Rev 5 不补，因为它们对 S4/S5 不是阻塞项。

**`Finding` 与 `Observation` 必须分开**。这是 Rev 1 的结构性缺陷：把「静默 2.9s」直接当 finding 会误报。

### 7.1.1 逐规则状态是缺失的（Rev 5 新增，阻断条款覆盖）

`NmRuleResult` 只有四个聚合列表，**没有「这条规则求值成功了」的逐规则状态**。`TryEvaluate` 返回的 `true` 被引擎消费后即丢弃。

后果：**合规的规则不产任何输出**。`BitRule` / `NidIdMapping` / `LayoutCheck` / `CycleTolerance` / `BurstShape` 在合法场景下都是 `return true`，既不产 finding 也不产 observation。因此无法从 `NmRuleResult` 反推「哪些条款被验证过」。

**这是 §5.5 条款覆盖模型的前置依赖**：覆盖状态必须由引擎显式输出，不能由「findings/observations 是否非空」反推。

```csharp
public sealed record NmRuleEvaluation(
    string RuleId,
    NmRuleStatus Status,
    string? ReasonCode,                       // 与 NmUnmodeledRule.ReasonCode 同一词表
    IReadOnlyList<string> EvidenceRefs);      // 产出的 finding/observation 标识

public enum NmRuleStatus
{
    EvaluatedNoFinding,      // 求值成功、未发现违规（这是最常见的合法情形）
    EvaluatedWithFinding,
    Partial,                 // 部分报文/段有结论，其余不可判
    NotApplicable,           // 本条 trace 上不适用（如 with_burst 不匹配）
    Unmodeled,
    Suppressed,              // 被数据体检抑制
}
```

`NmRuleResult` 增加 `IReadOnlyList<NmRuleEvaluation> Evaluations`。**没有这一项，§5.5 的 `Covered` 无法计算**——见 §19.4。

### 7.1.2 报告契约必须先冻结（S1 前置）

Rev 2 的 `Observations`/`Unmodeled` 只存在于规则结果层，现有 `NmReport` 装不下（`NmReport.cs:93-114` 只有 Findings/Unknowns/Assumptions）。若不先扩契约，实现时只能把 observation 塞回 finding、把 Unmodeled 压成字符串——正是 Rev 1 的错误。

```csharp
/// <summary>观测事实：可量化、可复核，但不声称违规。</summary>
public sealed record NmObservation(
    string Kind,                          // "all_nm_silent" / "burst_shape" / "nmsts_transition"
    string? Node, string? CanIdHex,
    double Start, double End,
    string Detail,                        // 人类可读
    string EvidenceKind,                  // measured | inferred | boundary
    bool IsInference = false,             // §14.5 要求恢复行为 observation 标 inferred
    string? RuleId = null);

/// <summary>未建模条款：显式可见，AI 不得据此给结论。</summary>
public sealed record NmUnmodeledRule(
    string Clause,                        // "NM_05" / "§5.9" / "附录A"
    string Reason,                        // 人类可读原因
    string? RequirementId = null,
    string? SourceRef = null)
{
    // Rev 5 新增：Clause 改成条款号后，「哪条规则未实现/被抑制」会从结构化输出里消失，
    // Reason 只是自由文本，AI 无法区分「未实现」和「被数据体检抑制」。
    public string? RuleId { get; init; }

    // 与 §9.4 的七值词表一致，供 AI 与测试做结构化判定。
    public string? ReasonCode { get; init; }
}
```

`NmReport` 增加 `Observations` 与 `UnmodeledRules` 两个集合，`NmMessageReport` 增加 `SilenceVerdict`：

```csharp
public sealed record NmMessageReport(
    // ... 现有字段
    bool SilentBeyondTimeout,          // 兼容保留：等价于 Verdict == SilentWithOtherNmActive
    NmSilenceVerdict SilenceVerdict = NmSilenceVerdict.NotSilent);
```

**「全层复用」不是改 record 就自动成立**，必须逐个改消费方：

| 消费方 | 现有 | 改动 |
|---|---|---|
| `GetNmOverviewTool.cs:71-129` | 手写 messages/findings JSON | 补 observations/unmodeled_rules 数组；`silent_beyond_timeout` 改用 `SilenceVerdict` |
| `NmReportWriter.cs:81` | Markdown 表格 | 加 observation 表、unmodeled 表 |
| 面板（`NmPanelFlow`） | 只读 findings | 加 observation 列表与未建模条款区 |
| `NmReportCache` | 序列化整个 report | 新字段参与序列化，key 见 §10.1 |

**AI 输出负例测试**：全网 NM 静默场景下 `silent_beyond_timeout` 必须为 `false`，`observations` 必须含 `all_nm_silent` 且 `IsInference=true`。

### 7.1.2 静默状态必须多态，不能是 bool

现有 `NmMessageReport.SilentBeyondTimeout`（`NmReport.cs:41`）只看窗口末尾距末帧的时间（`NmAnalyzer.cs:265`），**不检查其它 NM 是否仍活动**，且已输出给 AI（`GetNmOverviewTool.cs:82`）和导出（`NmReportWriter.cs:81`）。真实 trace 的全网静默会被它标成 true。

改为多态（M2 收尾时为五态）：

```csharp
public enum NmSilenceVerdict
{
    NotSilent = 0,              // 窗口末仍在发
    SilentWithOtherNmActive,    // 单节点静默且其它 NM 仍在发 → 可疑
    AllNmSilent,                // 全网 NM 静默 → 原因未知，非单节点违规
    IndeterminateAtBoundary,    // 窗口截断/共享 ID 无法拆分
    ReleasedInReadySleep,       // 末帧 NMSts=1 后停发，其它 NM 仍在发 → 合规释放（NM_09）
}
```

`ReleasedInReadySleep` 与 `SilentWithOtherNmActive` 必须分开：节点确实不再发送，但这是 NM_09 允许的合规释放，AI 不能把它读成可疑静默。只能在明确读到末帧 `NMSts=1` 时使用；位角色缺失或 DLC 不足（未知）必须保持可疑判定。`silent_beyond_timeout` 仍只对 `SilentWithOtherNmActive` 为 true。

`SilentBeyondTimeout` 字段保留一个版本做兼容，但**语义改为 `Verdict == SilentWithOtherNmActive`**，并加 AI 输出负例测试（全网静默时该字段必须为 false）。

### 7.2 原语（闭集 v1）

按可观测性重新设计，删掉无法可靠实现的泛化原语。

| 原语 | 参数 | 级别 | 用途 |
|---|---|---|---|
| `layout_check` | — | O | ID 范围、DLC、未使用位 |
| `nid_id_mapping` | `mode` | O | NID 与 ID 映射一致（§5.4.2） |
| `bit_rule` | `role`, `expect` | O | 位值校验（含预留位、RMR、NMSts） |
| `cycle_tolerance` | `cycle_ms`, `pct` | O | 周期偏差 |
| `burst_shape` | `count`, `interval_ms`, `pct` | O | 快发段形态 |
| `first_frame_latency` | `kind`(start_nm_tx / wakeup), `max_ms` | P | 首帧延迟（区分两种前置状态） |
| `repeat_message_duration` | `with_burst`, `min_ms`, `max_ms`, `anchor` | P | RMS 持续时长；anchor 取 `nmsts` 或 `burst_end` |
| `nmsts_transition` | `byte`, `bit` | O | NMSts 状态段变化（只产 observation） |
| `normal_resume_shape` | `min_silence_ms`, `cycle_ms`, `pct`, `min_frames` | P | 静默后恢复为**正常周期**发送（NM_02/NM_10/NM_15） |
| `stop_sending` | `timeout_ms` | P | 停发观察（不判违规，见 §4.1） |
| `max_gap_observe` | `value` | P | 间隔观察，仅产 observation。**v1 不实现**：无 §4 条款映射、`value` 语义未定义（见下） |

**`normal_resume_shape` 是 Rev 3 新增**：Rev 2 用 `resume_after_silence`（只有 `min_silence_ms`）覆盖 NM_02/NM_15，但规范要求远程唤醒后进入 **RMS 正常发送子状态**并以 `T_NM_MessageCycle` 发送（`spec.txt:477-483`）。只判「静默后有帧」会把「恢复后不快发、周期错误、只发一帧」都当成合规。

**Rev 5 补正**：上句只写了 NM_02/NM_15，实际 §4 表里 **NM_10 也挂 `normal_resume_shape`**。但三者目标状态不同——NM_02/15 恢复后进入 **RMS（NMSts=0）**，NM_10 恢复后进入 **NOS（NMSts=1）**。

**NM_10 的目标状态差异可以判定，但这不等于「规则可以启用」。** NMSts 已被三处读取（`RepeatMessageDuration` 的 `FindNmstsRole`、`NmstsTransition`、静默分类器），加一个参数就能区分：

| 规则 | `expect_nmsts` | 覆盖条款 |
|---|---|---|
| `NM_RESUME_TO_RMS` | `0` | NM_02 / NM_15 |
| `NM_RESUME_TO_NOS` | `1` | NM_10 |

`expect_nmsts` 缺省为 null = 不检查目标状态（此时该规则对三条条款都标 `Partial`，原因码 `target_state_unverified`）。

**`expect_nmsts` 的判别力边界（Rev 5 之后实测补正，2026-11-24）**：上表容易读成「加了参数就能区分三条」，**这是错的**。

- NMSts 分得出 NM_10（`=1`）。
- **NMSts 分不出 NM_02/15 与 NM_03/16**：本地唤醒的快发（NM_03/16）与远程唤醒的正常发送（NM_02/15）**NMSts 同为 0**，因为两者都处于 Repeat Message State。区分它们靠的是快发形态（20ms vs 500ms），不是状态位。
- 真实 trace（§14.6）的恢复段正是 **20ms 快发**，即 NM_03/16 的合规形态。因此把 `NM_RESUME_TO_RMS` 设成 `expect_nmsts: 0` 并启用，它**仍会因周期不符而误报**。

**结论：真正的阻塞点是 §7.3 主动唤醒判定（AWB + episode 首帧作用域），不是 `expect_nmsts`。** 后者已实现并有测试钉住（`Resume_ExpectNmstsZero_StillRejectsLocalWakeupBurst` 专门证明它替代不了唤醒源判定），但吉利 profile 的 `NM_RESUME_TO_RMS` 必须**继续标 `unobservable`**，直到 §7.3 落地。

**不要用「一条规则覆盖三条、报告加个 caveat」糊过去**：finding 是单次产出的结构化数据，caveat 写在 message 里 AI 读不到，写在目录里又落不到 finding 上。

**`max_gap_observe` 的 v1 状态（Rev 5 明确）**：§4 条款表里**没有任何条款**映射到它，且 spec 只写了「间隔观察」四个字，没有定义 `value` 的单位、语义、阈值来源。强行实现只能造出无法回溯到企标原文的结论。**v1 明确不实现**，规则若声明它会进 Unmodeled（`ReasonCode = unimplemented_primitive`，这是正确行为，不是缺陷）。

**NM_06 的时长判定**：NMSts 跃迁（O 级 observation）作为边界锚点，时长由 `repeat_message_duration` 独立判定。缺少 NMSts 配置时只能 inferred，不能反过来说条款已验证。

**删除**：`bit_sequence`、`field_sequence`、`counter_increment`、`timeout_reset`、`node_silent`、`all_nodes_silent`、`frame_present/absent`、`min_gap_ms`、`frame_count`、`observe_silence_ms`。

理由：语义无法在不引入通用表达式语言的前提下精确定义，或依赖不可观测输入。NMSts 用 `nmsts_transition` 专用原语，不做通用序列匹配。

**未知 `when`/`expect` 原语（早期评审轮次修正；「Rev 6」指当时的一次内部修订，不是本文件版本号）**：分两个阶段处理，不再统一「不抛异常」。

| 阶段 | 行为 | 理由 |
|---|---|---|
| **加载期**（`NmStandardYaml.Load`） | 原语不在 `NmRulePrimitives` 闭集内 → **抛 `NmProfileValidationException`**，消息列出全部合法值 | 拼错的原语名（`cycle_tolerence`）若静默进 Unmodeled，用户会以为条款已被校验——这是最危险的静默失败 |
| **求值期**（`NmRuleEngine.Evaluate`） | 闭集内但 v1 尚无实现的原语 → 进 `Unmodeled`，不产 finding，不抛异常 | 引擎必须能处理「profile 合法但当前版本未实现」的情形，不能崩溃 |

本文件早期版本写的「未知原语一律进 Unmodeled 不抛异常」只覆盖了求值期，遗漏了加载期拼写错误的危害。闭集校验把这类错误前移到加载期，是本设计的刻意收紧。（本条修正发生在当前 Rev 5 之前的一次内部修订，不是「Rev 6 已修正」——本文件的版本号就是 Rev 5。）

### 7.3 主动唤醒判定（降级为「疑似不一致」）

Rev 1 的四象限表过强。规范只规定 NM_03/NM_16（从睡眠/预睡眠本地唤醒）必须快发且 AWB=1，未规定 AWB 在 NOS 期间是否保持。

**两阶段判定**（Rev 2 单阶段会漏掉「快发不完整」的情况）：

**阶段 1 — 检测候选**（宽松阈值，不依赖合规性）

- 锚点：**该节点在该 episode 的首帧**，不是全网 episode 首帧。
- 候选条件：锚点后连续 ≥2 个间隔 ≤ `ImmediateCycleMs × 2.0`。
- 窗口截断 / 节点在窗口前已活动 / 单帧 → `indeterminate`，不判。

**阶段 2 — 分别判两项**

| 检查 | 条件 | 结论 |
|---|---|---|
| AWB 一致性 | 有候选段且锚点 AWB=0 | `NM_ACTIVE_WAKEUP_SUSPECT`（inferred，info） |
| AWB 一致性 | 锚点 AWB=1 但**无候选段** | `NM_ACTIVE_WAKEUP_SUSPECT`（inferred，info） |
| 快发形态 | 有候选段 → 交给 `burst_shape` 判合规 | `NM_WAKEUP_BURST_MISMATCH`（若不合规） |

**两处阈值必须一致**：检测用 `2.0×`（宽松，抓候选），合规用 `1+ToleranceRatio`（严格，判形态）。Rev 2 的 §7.3 与 §7.4 用了两个不同值且未说明关系，已统一为「先宽松抓、后严格判」。

后续 NOS 段不检查 AWB 保持性——规范未规定。文案必须写「疑似不一致（唤醒源不可观测，仅从行为反推）」，**不得声称验证了主动/被动唤醒**。

### 7.4 快发段判定

两层阈值，必须分开：

| 层 | 阈值 | 用途 |
|---|---|---|
| 检测 | 连续 ≥3 帧，间隔 ≤ `ImmediateCycleMs × 1.5` | 找到快发段 |
| 合规 | `count == ImmediateCount`，间隔 ∈ `ImmediateCycleMs × (1±ToleranceRatio)` | 判定是否合规 |

结果模型记录：`FrameCount`、`IntervalCount`（= FrameCount−1）、首末帧时刻、缺失位置、是否被窗口截断。

**10 帧 = 9 个间隔**，不能写成「10 帧 ×20ms = 200ms」。实测快发段跨度为 181.497ms（§14.3）。

### 7.5 Activity 段模型（时间条正确性）

**问题**：判定层（finding）正确不等于时间条画得对。现有 `NmActivityBuilder` 只做时间推断（`wakeup → active → released/silent`），完全不吃 NMSts，导致真实 trace 上两段 Repeat Message State 被画错：

| episode | 实际（NMSts） | 当前画法 | 错误 |
|---|---|---|---|
| 第一段 | Repeat 0.094675–2.215708 | wakeup 0.095–1.695 + active 1.695–2.216 | 无快发时用 `RepeatMessageMs` 兜底，把 2.12s 的 RMS 画成 1.6s 唤醒 |
| 第二段 | Repeat 12.677179–15.002182 | wakeup 12.677–12.859 + active 12.859–15.002 | 有快发时只认突发段，把剩下 2.14s 的 RMS 画成「发送中」 |

#### 7.5.1 A1：Activity 段吃 NMSts，新增 `repeat` / `normal`

profile 声明了 NMSts 位（`BMS_NMSts` = `24|1`）时：

1. **切段规则**：episode 内按每帧的 NMSts 值切段。起点 = episode 内该报文首帧或变化帧；终点 = 下一次变化帧；末段到该报文在 episode 内的末帧。
2. **NMSts=0 → `repeat`（Repeat Message State）**；**NMSts=1 → `normal`（Normal Operation State）**。
3. **跨 episode 不合并**（沿用现有 per-episode 切片，`NmActivityBuilder.cs:50-67`）。
4. **有状态位时以状态位为准**：同一区间不再画 `wakeup`，避免「wakeup + repeat」叠两段。
5. **`repeat` 段内部保留快发子状态**（见 §7.5.1.1）。
6. **尾段逻辑不变**：`released`/`silent` 仍在末帧之后按 episode/timeout 推断，不受 NMSts 影响。
6. **退回条件（只看位是否可读，不看窗口覆盖范围）**：

| 情形 | 处理 |
|---|---|
| 无 NMSts 配置 | 退回时间推断，`Evidence = inferred` |
| 位不可读（DLC 不足 / 字节越界 / 信号未定义） | 退回时间推断，`Evidence = inferred` |
| **有 NMSts 且值可读，但窗口内只有单一状态** | **仍按该值画单段**：`Evidence = measured`，另标 `StateBoundaryKnown = false`（未见状态跃迁）。**不得**退回推断，也不得因此置 `IsWindowTruncated` |

**为什么不能按「窗口覆盖不到变化帧」退回推断**：窗口裁剪是常见交互（分析当前窗口）。若只覆盖 NOS 的窗口被退回时间推断，`ResolveWakeupEnd` 会把它重新画成 `wakeup`——比现状更错，等于状态位在最有用的场景下失效。

**注意**：NMSts=`repeat` 但无快发段是**合法的**（NM_02 远程唤醒进入 RMS **正常发送**子状态）。不得据此报违规，最多产 observation。

**M2 边界（共享 ID）**：本节切段按**报文**进行。共享 ID（同一 CAN ID 下多个 NID 交替发送）时 NMSts 可能逐帧不同，按报文切段会切碎。M1 不保证该场景的切段语义；M2 按 `(CAN ID, extended, NID)` 分组后再细化。实现者不得自行发挥成「按报文强行合并」。

#### 7.5.1.1 `repeat` 段内必须保留快发子状态

吉利 §5.6.2 明确 RMS 内含**两个子状态**（`spec.txt:292-307`）：

| 子状态 | 发送周期 | 持续时间 |
|---|---|---|
| 快速发送子状态（NM Immediate Transmit） | `T_NM_ImmediateCycleTime`（20ms） | `N_ImmediateNM_TIMES`（10 帧） |
| 正常发送子状态（NM Normal Transmit） | `T_NM_MessageCycle`（500ms） | 至 RMS 结束 |

**只画一个 `repeat` 段会把快发信息抹掉**——而快发段恰恰是 NM_03/NM_04 的直接证据，也是 §7.5.3 选 1700/1500 的依据。真实 trace 第二段就是这种情况：

| 区间 | 实测 | 归属 |
|---|---|---|
| 12.677179–12.858676 | 10 帧 × 19–21ms | 快速发送子状态 |
| 12.858676–15.002182 | 间隔 536ms | 正常发送子状态 |

**方案：`repeat` 段内再切子段**，kind 增加 `immediate` / `normal_transmit`：

```csharp
// repeat 段 [12.677, 15.002] 内部再切：
//   immediate        [12.677, 12.859]  measured（观测到快发）
//   normal_transmit  [12.859, 15.002]  measured（NMSts 仍是 0）
```

- 子段 kind 仅用于渲染与 hover，**不影响 finding**。
- 无快发时 `repeat` 只有 `normal_transmit` 一个子段（不画 `immediate`）。
- 实现可选：子段作为独立 `NmSegment` 追加（推荐，复用现有渲染），或作为 `repeat` 段的属性。**推荐前者**，因为现有 `NmSegment` 渲染链路不需要改结构。

#### 7.5.1.3 `normal` 段只代表「发送段」，不是完整 NOS 生命周期

NMSts=1 表示 Normal Operation State，但 NM_09 规定 NOS 满足睡眠条件后进入 RSS，**RSS 期间停止发送 NM**（`spec.txt:527-535`）。RSS 里没有帧，NMSts 无从读取。

因此 `normal` 段的终点 = **该报文在 episode 内的末帧**，它代表的是「观测到的常规操作发送段」，**不是** NOS 的完整内部生命周期。

| 概念 | 可观测 | 时间条表达 |
|---|---|---|
| NOS 发送段 | ✅ 有帧、NMSts=1 | `normal` 段 |
| RSS 内部状态 | ❌ 无帧、无状态位 | 末帧之后的 `released`/`silent` 段（推断） |

**hover 文案必须写明**：「常规操作发送段（观测至最后一帧）」，不得显示成「常规操作状态」这种暗示完整生命周期的措辞。这与 §4 的 `stop_sending`（NM_09，P 级）不冲突——两者都承认 RSS 只能推断。

#### 7.5.1.2 NMSts 可读性校验（穷尽不可读情形）

§7.5.1 的退回条件只列了三种，不够。**必须校验 DBC 信号的完整编码**：

| 不可读情形 | 检测方式 |
|---|---|
| DLC 不足 | `frame.Data.Length <= byte` |
| 字节越界 | `byte >= DLC` |
| 信号未定义 | profile/DBC 无该位 |
| **位宽 ≠ 1** | DBC 信号 `24\|2` 之类 → 不可当作单 bit |
| **有符号** | `@0-`（Motorola signed）或 DBC 符号属性 → 语义不同 |
| **字节序不匹配** | Intel `@1+` vs Motorola `@0+` 的 bit 编号方向相反 |
| **逐帧不可读** | 部分帧 DLC 不足 → 该帧区间标 `unknown`，**不降级整段** |

**逐帧不可读的处理**：该帧所在的极小片段标 `unknown`（`Evidence = inferred`），其余实测帧仍为 `measured`。**不允许**因为少数帧不可读就把整段降级为推断。

校验器（§8）必须补：NMSts 位必须是无符号、长度恰好 1、编码可解码。违反时 profile 加载失败。

#### 7.5.2 A2：`NmSegment` 增加证据等级

```csharp
public sealed record NmSegment(
    double Start,
    double End,
    string Kind,
    // 与 NmEvent.EvidenceKind 同一词表（NmReport.cs:55）：measured | inferred | boundary
    string Evidence = "inferred",
    // 段的终点恰好等于窗口/报告边界（End == windowEnd 或报告裁剪点）。
    // 严格定义：只有真正被边界切断才为 true，不是「状态可能在窗口外延续」。
    bool IsWindowTruncated = false,
    // 段内是否观测到状态跃迁（NMSts 变化）。
    // false = 窗口内只有单一状态，状态边界不可判明（不代表被截断）。
    bool StateBoundaryKnown = false);
```

**`IsWindowTruncated` 与 `StateBoundaryKnown` 严格区分**（这是 Rev 3 第一版混为一谈的地方）：

| 情形 | IsWindowTruncated | StateBoundaryKnown |
|---|---|---|
| 段终点 = windowEnd（窗口切断） | `true` | 视有无跃迁 |
| 段终点 = 下一 episode 起点（报文停发） | `false` | `true` |
| 段终点 = NMSts 变化帧 | `false` | `true` |
| 窗口内只有单一状态、终点是末帧 | `false` | `false` |

真实 trace 第一段 `normal` 从 2.215708 到 9.713674（episode 末帧），终点由报文停发界定，**不是窗口截断** → `IsWindowTruncated = false`。

**加独立字段，不复用 `Kind`**。理由：kind 与 evidence 正交——同一个 `repeat` 段既可能由 NMSts 实测，也可能由 `T_REPEAT_MESSAGE` 推断；把证据塞进 kind 会让枚举翻倍（`repeat_measured` / `repeat_inferred`…），并打断现有的笔刷/文案映射与测试。

| Evidence | 来源 |
|---|---|
| `measured` | NMSts 位值；观测到的快发段 |
| `inferred` | `T_REPEAT_MESSAGE` 兜底窗；时间推断的 active/released |
| `boundary` | **段本身只因窗口/报告边界才存在，内部没有任何可用证据** |

默认值 `inferred`：M1 现有段全部来自时间推断，语义正确，且 `NmSegment` 是 record 带默认值，构造点无需重写。

**`boundary` 与两个 bool 的分工（不得混用）**：

| 情形 | Evidence | IsWindowTruncated | StateBoundaryKnown |
|---|---|---|---|
| 段有实测证据，终点被窗口切断 | `measured` | `true` | 视跃迁 |
| 段有推断证据，终点被窗口切断 | `inferred` | `true` | 视跃迁 |
| 段本身只因边界存在（内部无任何证据） | `boundary` | `false` | `false` |

即：**`Evidence = boundary` 只在「没有证据、纯边界产物」时使用**。凡是段有 measured/inferred 证据，一律用 bool 标记截断，不改 Evidence。

#### 7.5.2.1 hover 与文案需要 `Evidence`

现有 `SetNmSegmentHover(string kind, string rowLabel, double start, double end)`（`NmPanelFlow.cs:1018`）只收 kind，证据等级传不进去，「实测/推断」区分落不了地。

**证据字段的完整数据通路**（只改 hover 签名不够）：

```
NmSegment (Core, 三新字段)
  → NmTimelineSegmentVm (App VM, 必须加字段)
  → BuildTimelineRows 映射 (NmPanelFlow.cs:264-272)
  → SetNmSegmentHover (hover 文案)
  → XAML 绑定 (透明度/斜纹)
```

现有 `NmTimelineSegmentVm` 只有 `Left/Width/Kind/Start/End/Tooltip`（`NmPanelFlow.cs:16-17`），**必须补**：

```csharp
public sealed record NmTimelineSegmentVm(
    double Left, double Width, string Kind,
    double Start, double End, string Tooltip = "",
    string Evidence = "inferred",
    bool IsWindowTruncated = false,
    bool StateBoundaryKnown = false);
```

`BuildTimelineRows`（`NmPanelFlow.cs:264-272`）必须同步复制这三个字段，否则 hover 与视觉区分都拿不到数据。

**必须同批改签名**：`SetNmSegmentHover(string kind, string evidence, bool isTruncated, string rowLabel, double start, double end)`，调用点 `TraceViewerViewNmPanel.xaml.cs:99` 从 `NmTimelineSegmentVm` 取字段。文案：

| Evidence | hover 文案 |
|---|---|
| `measured` | 实测（NMSts） |
| `inferred` | 推断（T_REPEAT_MESSAGE） |
| `boundary` | 边界截断 |

**`IsWindowTruncated = true` 时追加后缀**「，窗口截断」：如「实测（NMSts），窗口截断」。后缀与 Evidence 文案**拼接而非替换**，两者正交。

视觉区分（`inferred` 降透明度或斜纹）与文案同批，不允许只做其一。

#### 7.5.3 A3：无快发时的窗口取值裁决

§5.2 定义了 `RepeatMessageWithBurstMs`(1700) 与 `RepeatMessageWithoutBurstMs`(1500)，但 `NmActivityBuilder` 只收单个 `repeatMessageMs`，1500 从未被使用。

**裁决：两值并存，按是否观测到快发选择。**

| 条件 | 兜底窗 | 依据 |
|---|---|---|
| episode 内有快发段 | `RepeatMessageWithBurstMs`（1700） | 吉利 §5.5「RMS 包含快发时按 1700ms ±60ms」 |
| episode 内无快发段 | `RepeatMessageWithoutBurstMs`（1500） | 吉利 §5.5「RMS 不包含快发时按 1500ms ±60ms」 |

两者都必须 `min(phaseLimit, ...)` 夹到下一 episode 起点或 `windowEnd`（现有 `NmActivityBuilder.cs:62,115` 已这样做），**禁止重叠**。

**仅用于 `inferred` 路径**：NMSts 可用时，窗口值不参与切段。

#### 7.5.3.1 快发检测的唯一实现（消除三处阈值冲突）

Rev 3 第一版有三套快发阈值，互相冲突：

| 位置 | 阈值 |
|---|---|
| `NmActivityBuilder.cs:130`（现有） | `0.5 × CycleMs` |
| §7.3 主动唤醒候选检测 | `2.0 × ImmediateCycleMs` |
| §7.4 快发形态合规 | `1.5 × ImmediateCycleMs` |

**裁决：三处共用同一个检测器，返回「候选段」+「是否合规」两个结果。**

```csharp
/// <summary>快发检测的唯一实现。所有调用方复用，不得各自实现。</summary>
public static class NmBurstDetector
{
    /// <param name="timing">生效时序；ImmediateCycleMs 为 null 时走回退路径</param>
    public static BurstDetection Detect(
        IReadOnlyList<double> timestamps, NmEffectiveTiming timing);

    public sealed record BurstDetection(
        int FrameCount, int IntervalCount,
        double Start, double End,
        bool IsCandidate,        // 宽松检测：≥2 个间隔 ≤ 2.0×Immediate
        bool IsCompliant,        // 严格判定：count==ImmediateCount 且间隔 ∈ 1±ratio
        string Detector,         // "immediate" | "cycle_ratio_fallback" | "none"
        // 段尾触到 episode/窗口边界：实测时长只是下限，既不得判合规也不得判不合规。
        // 注意：**不含「帧数不足」**——那是 IsCandidate=false 的语义，不是不可判明。
        // 「两个阈值都缺失」也置 true（此时 IsCandidate=false），表示检测器无法判定。
        bool IsIndeterminate);
}
```

**`ImmediateCycleMs` 缺失时的回退**（这是原设计的循环依赖缺口）：

| 情形 | 检测方式 | Detector | Evidence |
|---|---|---|---|
| `ImmediateCycleMs` 已配置 | `≤ 2.0 × ImmediateCycleMs` | `immediate` | measured |
| `ImmediateCycleMs` 未配置 | `< 0.5 × CycleMs`（**严格小于**，与 M1 的 `NmActivityBuilder.cs:130` 一致） | `cycle_ratio_fallback` | **inferred** |
| 两者都无（CycleMs 也无） | 不检测快发 | `none`，`IsIndeterminate = true` | 不选双窗口，用 M1 默认 |

两行的比较方向**不同**且都是刻意的：`immediate` 路径用 `≤`（spec §7.4 原文），回退路径用严格 `<`（M1 在 `delta >= 0.5×cycle` 时断开连续段）。恰好等于阈值时两者结论相反，这是沿用各自原有语义的结果，不是笔误。

**回退时不得选 1700**：`cycle_ratio_fallback` 检测到的快发不足以证明「符合 N_ImmediateNM_TIMES」，按 1500 兜底并标 `inferred`。

**签名变更（破坏性）**：`NmActivityBuilder.Build` 现在只收单个 `repeatMessageMs`（`NmActivityBuilder.cs:19`），调用点 `NmAnalyzer.cs:253` 传 `request.Profile.Timings.RepeatMessageMs`。双窗口必须改签名：

```csharp
// 收 NmEffectiveTiming（已含报文级覆盖），不是 NmGlobalTiming。
// 见 §5.2：传全局值会丢掉 def.CycleMs ?? cycleMs 的报文级优先级。
public static IReadOnlyList<NmSegment> Build(
    IReadOnlyList<NmFrame> messageFrames,
    NmMessageDef def,
    IReadOnlyList<NmEpisode> episodes,
    NmEffectiveTiming timing,
    IReadOnlyList<NmFrame> allNmFrames,
    double windowEnd);
```

调用点：`NmAnalyzer.cs:253` 传 `Resolve(def, global)` 的结果；测试直接调用点 `NmMultiEpisodeActivityTests.cs:97,123` 与 `NmSpecClosureTests.cs:416` 也要改。这与 S2「统一时序」是同一批改动，建议合并实施。

#### 7.5.4 UI 同批改动（否则会显示品红）

`NmSegmentBrushConverter.BrushFor` 的 `_ => UnknownBrush` 会把新 kind 画成品红（`NmSegmentBrushConverter.cs:70,91`）。新增 kind 必须同批：

| 位置 | 改动 |
|---|---|
| `NmSegmentBrushConverter` | 新增 `RepeatBrush` / `NormalBrush`，加入 `BrushFor` 映射 |
| `NmTimelineText.SegmentKind` | `repeat` → 「重复报文」；`normal` → 「常规操作」（`NmPanelFlow.cs:69-84`） |
| `NmTimelineKinds` | 新增 `Repeat` / `Normal` 常量 |
| 图例 | 加两行 |
| hover 文案 | 「实测（NMSts）」/「推断（T_REPEAT_MESSAGE）」；`inferred` 段视觉降透明度或斜纹 |
| 证据分级同源 | 与 `Observation`/`Finding` 的 measured/inferred/boundary 用同一词表 |

#### 7.5.5 验收

与 §14.3 同源（真实 trace 实测 NMSts 变化点）：

| episode | 期望切段 |
|---|---|
| 第一段 | `repeat` 0.094675–2.215708，`normal` 从 2.215708 起 |
| 第二段 | `repeat` 12.677179–15.002182，`normal` 从 15.002182 起 |

- 无 profile / 无 NMSts 时 **M1 行为不变**，`NmSpecClosureTests` 三条 wakeup 测试作回归门。
- 新增测试：NMSts 恒 0、恒 1、**窗口只覆盖单一状态（必须仍为 measured + `IsWindowTruncated=true`，不得退回 wakeup）**、位不可读（DLC 不足）、跨 episode 不合并、`Evidence` 三值、`boundary` 只用于纯边界段、hover 文案含截断后缀。

**S6 实施补记（2026-11-24）**——三处 spec 未写清、实现时必须裁决的点：

| 点 | 裁决 | 依据 / 代价 |
|---|---|---|
| `normal_transmit` 的命名 | 实现为 `repeat`，不发射 `normal_transmit` | §7.5.1.1 把无快发的那部分叫 `normal_transmit`，§7.5.5 验收表对同一区间写 `repeat`。两者同义同区间，取验收表（硬验收门），且并存会让时间条出现两个同义 kind。代价：偏离 §7.5.1.1 措辞。 |
| §7.5.3 双窗口 vs §7.5.5「M1 行为不变」 | `NmEffectiveTiming` 加**三态** `RepeatWindowSource`（None / M1Single / StandardDual）：None 用周期当观察窗；M1Single 沿用「唤醒终点 = 快发段末帧」的 M1 语义；只有 StandardDual 才「快发只选窗，唤醒段 = 1700/1500 整段」 | 两者直接冲突：`NmTimingDefaults` 在无人声明时也会填 1700/1500，无法用值反推是否声明。**单 bool 不够**——M1 的单一值与企标双值语义不同，用同一开关会让既有项目的唤醒段突然变长（实测：单 bool 版本让 `Analyze_Activity_ObservedBurstEndsWakeup` 红）。代价：多一个枚举。 |
| 快发阈值同理 | 加 `ImmediateCycleDeclared`：未显式声明 `ImmediateCycleMs` 时走 M1 的 `<0.5×CycleMs`，而不是默认的 `≤2.0×20ms` | §7.5.3.1 回退表以「是否配置 ImmediateCycleMs」为分界，但该字段默认值就是 20，同样无法用值反推。不加这个标志，`Analyze_Activity_ObservedBurstEndsWakeup` 直接红（实测过）。 |

**`NmStsResolver` 只校验 DBC 能给出的三类冲突**（位宽≠1 / Motorola / 有符号）。§7.5.1.2 表里的「信号未定义」在本数据模型下不构成冲突——profile 的单 bit 声明本身就是充分的读法；「DLC 不足」「字节越界」由 `NmStsProbe.Read` 逐帧返回 null 处理。没有 DBC 时不做编码交叉校验（无从校验，不臆造冲突）。

**已知缺口：共享 ID 未按 NID 细化（S6 有意不含）**。§7.5.1 的「M2 边界」要求按 `(CAN ID, extended, NID)` 分组后再切段；S6 的切段仍按报文进行。共享 ID（同一 CAN ID 下多 NID 交替、NMSts 逐帧不同）时会产生 repeat/normal 交替的碎片段。**这是记录在案的缺口，不是漏做**：`NmAnalyzer` 已识别 sharedId 并用于抑制周期结论，切段侧的 NID 分组留待后续切片。

**§7.5.1 退回条件第 3 行与 §7.5.5 验收项的措辞差异（不是冲突，实现按此判）**：

| 出处 | 措辞 | 判定 |
|---|---|---|
| §7.5.1 第 3 行 | 「窗口内只有单一状态」→ 不得置 `IsWindowTruncated` | 该行的场景是**末帧 < windowEnd**（报文自己停了），段没被切断 |
| §7.5.5 验收项 | 「窗口只覆盖单一状态」→ **必须** `IsWindowTruncated=true` | 该行的场景是**末帧 == windowEnd**（窗口把它切了） |

两行的区别只在末帧是否等于窗口终点，因此判据是 `|end − windowEnd| ≤ 容差`，与「有几个状态」无关。`StateBoundaryKnown` 则只看「终点是不是 NMSts 变化帧」（字段定义本身就是「段内是否观测到状态跃迁」），与截断正交——这一点 §7.5.2 已强调过。

**base profile 声明 1700/1500 是有意的**：这两个值是 AUTOSAR CanNm 标准的 T_REPEAT_MESSAGE（含/不含快发），不是吉利专属。base 自称「不含 OEM 约束」指 layout/bit_roles 层面（PNI 位、预留位），timings 是标准语义。因此只选 base 的用户也会走 `StandardDual`——这是期望行为，不是串味。

## 8. 校验器

加载/保存时校验，失败不允许进入分析：

| 类别 | 规则 |
|---|---|
| 布局 | NID/CBV 字节 < DLC；NID 与 CBV 不重叠；user data 区间在 DLC 内 |
| 位角色 | `byte` < DLC；`bit` ∈ [0,7]；同一 (byte,bit) 不得重复声明；`MustBeZero` 与具名角色允许共存 |
| **重叠** | DBC 信号区间与 `MustBeZero` 位重叠时：**仍执行** OEM 显式约束（§5.1.1），同时记 `NM_CONFIG_CONFLICT`。仅「DBC 有信号但 OEM 未声明零约束」时才跳过通用未使用位检查 |
| 时序 | `timeout > cycle`；`repeat_message_with_burst >= cycle`；immediate 序列总时长合理 |
| 报文 | 报文 ID/扩展标志与 DBC 一致（若提供 DBC）；DLC 1–64 |
| 节点 | 站地址 0–255 |
| 规则 | 引用的位/字节存在；finding code 已注册；`requirement`/`source_ref` 必填；原语在闭集内 |
| 继承 | `extends` 可解析、无循环；patch 三态合法 |

校验失败：UI 标红 + 字段位置；测试抛 `NmProfileValidationException`（不静默降级）。

## 9. 数据体检

跑规则前先做一致性检查，结果放报告顶部。**体检不通过不阻断分析，但明示「数据存疑，结论仅供参考」**。

### 9.1 实现范围（Rev 5 收敛）

Rev 4 列了十项，但其中多项 spec 从未定义判据。Rev 5 按「判据是否可复核」重新划分：

**v1 实现（五项）**：

| 检查 | 状态 | 依赖 | 判据是否已定义 |
|---|---|---|---|
| 报文 ID 是否在 DBC 中 | **新检测器** | 原始帧 + DBC | ✅ 帧 ID ∉ DBC 报文集合 |
| 窗口首尾截断 | **新检测器** | 原始帧 + 窗口 | ❌ **未定义，见下** |
| 时间戳是否单调 | 检测器已有（`NmFrameSelector` 的 bool），**缺区间** | 原始帧 | ✅ 后一帧时间戳 < 前一帧 |
| 实测 DLC 与 DBC 是否一致 | 检测器已有（`NM_DLC_MISMATCH`），**缺 DQ 外壳** | 原始帧 + DBC | ✅ 逐帧 `Frame.Dlc != Def.Dlc` |
| NID 是否与 ID 映射一致 | 检测器已有（`nid_id_mapping` 规则），**仅选中企标 profile 时可用** | profile + 帧 | ✅ 扩展帧 NID==ID 低字节；标准帧 NID==ID−0x400 |

**`truncated_window` 的判据缺失（Rev 5 自认）**：本节开头说「只保留判据可复核的五项」，但这一项**没有可计算定义**。「首帧/末帧落在窗口边界」会让每一份录制都算截断，等于永远报警。

必须补一个能区分的定义，例如：**窗口首（或末）一个 cycle 内存在 NM 帧，但窗口外没有数据可供判定该段是否延续**——即「段的起点/终点恰好是窗口边界，且无法确认它在窗口外是否继续」。

**在这个定义补上之前，该项不得实施**；宁可先做四项，也不要造一个永远为真的告警。

**v1 明确不做（五项）**：

| 检查 | 不做的理由 |
|---|---|
| `implausible_rate` | spec 未定义「帧率合理」的判据（相对什么基准？偏差多少算异常？） |
| `duplicate_frames` | 「同/近时间戳」的「近」未定义 |
| `loopback_suspected` | Tx/Rx 回环的匹配窗口未定义 |
| `time_jump` | 「大正向跳变」的阈值未定义 |
| 多通道混录 | 已在选择层按显式通道处理（`NmFrameSelector`），不是体检项 |

**这五项不是「暂时没空做」，是「判据未定义」**。强行实现会产出无法回溯到任何企标条款的结论，违背 §3 的可观测性纪律。要做得先在 spec 里定义判据。

### 9.2 报告模型：三态检查表，不是问题列表（Rev 5 重做）

> **⚠ 本节有四个未定的强制点，实施前必须补齐（Rev 5 记录）：**
>
> 1. **`Code` 是开放字符串** —— 重蹈 spec 自己修过的「未注册 finding code」问题，应建闭集。
> 2. **「固定五项每项必有一行」没有机制保证** —— 只能靠测试，或改成五个具名属性。
> 3. **`null` vs 空列表表达「不适用 vs 查了没抑制」太脆** —— record 相等性对 `IReadOnlyList` 是引用相等，任何消费方写 `?? Array.Empty<string>()` 就抹掉语义。建议改显式结构（如 `NmRuleSuppression(bool Applicable, IReadOnlyList<string> RuleIds)`）。
> 4. **抑制映射只定义了两项** —— `unknown_id` / `truncated_window` 抑制谁写了，`dlc_mismatch` / `non_monotonic` / `nid_mapping` 失败时抑制谁**完全没写**。
>
> 另外 **DQ 检查哪一份帧集未定义**：`NmFrameSelector` 在多通道未指定时返回空帧集，五项检查会在空集上全部 `Passed`——正是本节要消灭的假通过。必须写明 DQ 看原始帧还是选择后的帧。

**Rev 4 的模型实现不了 S5。** 一个「问题列表」无法区分两件事：

- 检查跑了、通过了
- 检查压根没跑（无 DBC / 无 profile / 缺 `nid_byte`）

§9.1 自己要求「无 profile 时 `nid_mapping` 明示未检查，不得记为通过」——问题列表装不下这个语义。

```csharp
/// <summary>单项体检结果。status 三态，不是「有没有 issue」。</summary>
public sealed record NmDataQualityCheck(
    string Code,                    // unknown_id / dlc_mismatch / nid_mapping / ...
    NmDataQualityStatus Status,     // Passed | Failed | NotChecked
    string Detail,                  // 人类可读说明；NotChecked 时必须写清缺什么
    // 受影响区间可能有多个（non_monotonic 多段），单值装不下。
    // 用具名 record 而非 ValueTuple：后者在 JSON 序列化与 record 相等性上都有坑。
    IReadOnlyList<NmTimeRange> Ranges,
    IReadOnlyList<string> AffectedCanIds,
    // null = 该检查不做规则抑制；空列表 = 算了但没抑制任何规则。
    // 用 null 与空列表区分「不适用」与「查了没抑制」，避免二义性。
    IReadOnlyList<string>? SuppressedRuleIds);

public sealed record NmTimeRange(double Start, double End);

public enum NmDataQualityStatus
{
    Passed = 0,
    Failed = 1,
    // 不是 Failed：数据没问题是「通过了」，数据不够是「没查成」，两者结论强度不同。
    NotChecked = 2,
}
```

**`NmReport.DataQualityChecks` 是固定五项**（按 §9.1 的 v1 范围），每项必有一行，`Status` 必填。不通过时另附 `Detail` 与区间。

**为什么不用「issue 列表 + 空即通过」**：`NotChecked` 会被误读成 `Passed`，而 AI 会据此对未检查项下结论——这正是 §3 要消灭的静默通过。

### 9.3 计算位置：独立的 `NmDataQualityAnalyzer`

**不做成规则原语的理由（Rev 5 修正措辞）**：

1. **职责划分**：数据体检不是企标条款，没有 `clause` 可挂；OEM 规范不定义它，它是工具层的输入质量保证。
2. **现有引擎没有规则间依赖/调度模型**：`NmRuleEngine.Evaluate` 只顺序遍历规则并直接调 `TryEvaluate`（`NmRuleEngine.cs:33-67`）。要实现「规则 A 抑制规则 B」需要引入分阶段求值与优先级——**这不是理论上的悖论，只是 v1 不愿扩展现有调度模型**。
3. **输入需求不同**：体检需要原始帧与 DBC，而 `NmRuleContext` 没有这两样（§7.1）。

**Rev 5 更正**：此前把第 2 条写成「顺序悖论」，是把设计选择伪装成理论不可能。正确表述是「v1 选择独立前置 analyzer，以避免扩展现有规则调度模型」。

**也不能塞进 `NmAnalyzer`**（现已 458 行）。新增独立类：

```csharp
public static class NmDataQualityAnalyzer
{
    // 入参是 NmAnalysisRequest（已含 Frames / Dbc / Standard），不需要扩 NmRuleContext。
    public static NmDataQualityResult Analyze(NmAnalysisRequest request);
}
```

`NmAnalyzer` 在顶部调用一次，把 `SuppressedRuleIds` 传给引擎。

**关于 §7.1 的 `NmRuleContext` 缺 `RawFrames`/`Dbc`/`Selection`**：这是既有缺口，但**与本设计无关**——体检不走规则引擎。§7.1 那三个字段要么在将来切片补上，要么删掉；Rev 5 不处理，但要在此注明它不影响 S5。

### 9.4 规则抑制必须前置于求值

报告顶部写「数据存疑」、下面照样列出基于该数据的 warn finding——这种自相矛盾必须避免。

**执行顺序**：U 级规则 → 抑制集 → `TryEvaluate`。被抑制的规则在求值**之前**转成 `NmUnmodeledRule` 并 `continue`。

**`NmUnmodeledRule` 必须新增 `RuleId` 与 `ReasonCode`**：Clause 改成条款号后，「哪条规则被抑制/未实现」在结构化输出里会消失，`Reason` 只是自由文本，AI 无法区分：

| ReasonCode | 含义 |
|---|---|
| `unimplemented_primitive` | 原语在闭集中但 v1 无实现 |
| `missing_profile_data` | 缺参数/缺位角色 |
| `unobservable` | U 级条款 |
| `uncovered` | 目录中有、无规则覆盖 |
| `partial_evidence` | 规则部分成功 |
| `data_quality_suppressed` | 被体检抑制 |
| `not_applicable` | 本条 trace 不适用 |
| `internal_inconsistent` | **引擎缺陷**：evaluator 给了原因却没给原因码。不是 profile 问题，不能让用户去改 profile（2026-11-24 补） |

**`partial_evidence` 的首个实际用途（2026-11-24）**：`normal_resume_shape` 声明了 `expect_nmsts`、但恢复帧 DLC 不足读不出状态位时用它。根因是 trace 字节不够，不是 profile 缺声明——用 `missing_profile_data` 会让 AI 指引用户去改一份没问题的 profile。该情形下**周期判据照常执行并产出 finding**，`partial_evidence` 只说明「目标状态这部分没判成」，两者并存。

**`AffectedCanIds → SuppressedRuleIds` 的映射不能自动算**：`NmRuleDef` 没有 message/node scope（`phase` 也不驱动作用域）。要么给规则加 scope，要么由 `NmDataQualityAnalyzer` 手工映射。**v1 选后者**。

**但映射必须按「输入缺失使哪项判据不可判定」精确推导，不能按「受影响 ID 一刀切」（Rev 5 修正）。**

两个具体的反例，都证明「一刀切抑制」会删掉真实结论：

| 反例 | 一刀切的后果 | 正确做法 |
|---|---|---|
| `unknown_id` → 抑制该 ID 的**所有**语义规则 | `layout_check` 正是查「ID 是否落在 OEM 允许区间」的规则——ID 不在 DBC 时它**恰恰最该跑**。抑制后只剩一条泛化的 DQ failure，用户看不到「这个 ID 越界了」 | 只抑制**依赖 DBC 解码**的规则（如需要 DBC 信号定义的）；OEM profile 自带的 ID 区间 / DLC / 位约束照常执行 |
| `truncated_window` → 统一抑制 `repeat_message_duration` / `normal_resume_shape` / `stop_sending` | 现有代码**已经**做了精确降级：段首截断时「超上限」仍可判（真实起点更早，超了只会更超），只有段尾截断才整体跳过（`NmRuleEvaluators.Latency.cs:159-199`）。统一抑制会把这个能力退掉 | 抑制信息必须携带 **start/end 方向**与受影响的具体判据；或让 evaluator 接收 DQ 上下文自行精确降级，而不是引擎统一 `continue` |

**结论**：`SuppressedRuleIds` 不能由 `AffectedCanIds` 机械推导。映射表必须逐项论证「这条规则是否真的因为该输入缺失而不可判」，并写进注释。做不到精确映射时，**宁可标 `Partial` 也不标 `Suppressed`**——少判一项好过删掉真实结论。

**与「体检不阻断分析」的张力（Rev 5 记录）**：§9 顶部说「体检不通过不阻断分析」，而本节要求抑制规则——两者是不同安全策略。裁决：**抑制只用于「该项判据确实不可判定」的情形**，其余一律降级为 `Partial` 或保留结论并标注数据存疑。

### 9.5 消费方

新字段 `NmReport.DataQualityChecks` 必须同步接入：

| 消费方 | 现状 | 要做什么 |
|---|---|---|
| `GetNmOverviewTool`（AI 工具 JSON） | 正在输出 `finding.RequirementId` 与 `unmodeled.RequirementId`（`GetNmOverviewTool.cs:100-101,148`） | 改 `Clauses` 后必须同步改这里，否则 AI 拿到旧/空值；`NmUnmodeledRule` 新增的 `RuleId` 也要输出 |
| `NmReportWriter`（Markdown） | **只输出 `rule=` 和 `source=`，从未输出 `RequirementId`**（`NmReportWriter.cs:201-214`） | 新增条款输出（与 DQ 字段同批）。注意：§4.0 曾说它是「追溯出口」——**这句不成立**，它从来没输出过 |
| 面板 | 无 unmodeled 区、无 DQ 区 | **本轮不做**，归 S7（§11 文件所有权表把 `NmPanelFlow` / XAML 划给 S6） |

**`NM_03` / `NM_16` 的条款必须拆分（Rev 5）**：§4.0 说 `burst_shape` 覆盖 NM_03/04/16，但 §4 表同时承认 AWB 半边未实现。若目录按 §4 表一行一条，`burst_shape` 会声称覆盖整个 NM_03 并报 `Covered`，而 AWB 从未检查——这是本节要消灭的静默通过换了个入口。

**现在就要拆**：`GEELY:NM_03/burst`（Covered）+ `GEELY:NM_03/awb`（NotCovered，原因 `unimplemented_primitive`）。不能等 AWB 实现。

**缓存必须纳入新数据**：`NmFlow` 的 supplement hash 只拼了 `StandardTimings`/`StandardBitRoles`/`StandardRules`/`StandardLayout`/`StandardFeatures`。条款目录（`StandardClauses`）不在其中 → 改目录不失效缓存，旧报告被复用。**必须把 `StandardClauses` 加进 `NmProfile`、`NmStandardProfileBinding` 和 hash**（按 Id 排序）。

## 10. UI（本轮范围）

### 10.1 企标选择器

NM 面板顶部只读下拉：

- 列出可用 profile（base + oem + 项目覆盖）
- 显示 `standard_id@version`、继承链、`profile_hash` 前 8 位
- 选中后触发重分析
- 未选择时显示「未选择企标：仅做观测，不做企标判定」

**Profile 加载链路必须定义**：

- 搜索顺序：会话显式选择 > DBC 同目录 > `%APPDATA%\PeakCan\profiles\` > 安装目录 `profiles\`
- `extends` 相对**当前 profile 文件所在目录**解析
- 同 ID/version 多份文件 → 报错让用户选，禁止静默择一
- 文件变化通过 `FileSystemWatcher` 失效缓存

**缓存 key 修正**：现有 `NmSupplementHash`（`NmFlow.cs:182-214`）不包含 rules/bit roles/content/profile version，切 profile 会命中旧报告。

**不要重复哈希**：DBC 与 sidecar 的报文/节点/布局/时序已被展平进 `NmProfile`（`NmFlow.cs:55-82` 先 merge 再分析）。若 key 再拼 DBC hash + sidecar hash，会造成「DBC 只改注释也失效」和「不同输入产生相同 profile 却被拆成两项」。

**但也不能只留 profile**：现有 `NmReportCache` 的 key 含 `SourceId/Channel/FrameCount/DbcHash/Start/End/WindowRef`（`NmReportCache.cs:3-13`）。这些**不是 profile 的重复**——同一 source 重新加载成帧数相同、起止相同的新 trace，会复用旧报告。

正确 key：

```csharp
public sealed record NmCacheKey(
    string SourceId,
    ushort? Channel,
    double Start, double End, string WindowRef,
    // trace 内容版本：现有 FrameCount + 新增内容哈希（或 load generation）
    int FrameCount,
    string TraceContentHash,        // 新增：帧内容摘要，防同帧数不同内容
    // profile 语义：canonical 展平结果
    string CanonicalProfileHash,
    string EngineVersion);
```

**移除** `DbcHash`（已被 `CanonicalProfileHash` 覆盖），**保留** source/channel/窗口/frame count，**新增** `TraceContentHash`。

### 10.1.1 canonicalization 规范（否则 hash 不稳定）

必须定义确定性规则，并配「键序/列表序/默认值变化不改变 hash」测试：

| 项 | 规则 |
|---|---|
| map key | 按 ordinal 升序 |
| 列表 | 有业务 key 的按 key 排序（messages→can_id+ext，nodes→name，rules→id）；无 key 的保序 |
| 数值 | 整数不带小数点；浮点用 invariant `R` 格式；百分比统一存为小数 |
| 缺省值 | 与显式默认值等价（先归一化再哈希） |
| 删除 | `remove` 后的结果参与哈希，`remove` 列表本身不参与 |
| 排除字段 | `Provenance`、文件路径、`CreatedAt`、`ProfilePath` |
| 序列化 | 固定 JSON 序列化（snake_case、无缩进、UTF-8） |

### 10.1.2 失效与并发

- `FileSystemWatcher` 事件去抖（≥200ms）；profile 写入中不读（先试 `FileShare.None`）。
- `extends` 父文件变化 → 整个依赖链失效。
- 任务结果回写时校验 **selection generation + profile hash** 双条件，防止旧结果覆盖新选择。

### 10.2 标定面板

- **规则表**：每条规则 → 触发 / 未触发 / `Unmodeled`，点击 seek 到证据时刻
- **观测 vs 期望**：周期、快发、超时等数值对比
- **数据体检**：§9 结果
- **未建模条款**：显式列出，附条款号

不做：规则编辑、profile 新建、导入导出向导。

## 11. 实现切片

Rev 2 的顺序有问题：S2 的原语依赖统一时序，却排在 S3 之前；S3 同时含位义解码和 13 个调用点改造，验收粒度过大。重排如下：

| 片 | 内容 | 验收 |
|---|---|---|
| **S1** | 报告契约（`NmObservation`/`NmUnmodeledRule`/`NmSilenceVerdict`）+ schema + YAML 加载 + 三层继承 + 校验器 + AUTOSAR base + 吉利 profile | 加载/展平/校验单测；坏 profile 必报错；三态 patch 可区分缺失与删除 |

**S1 的 schema 边界（Rev 5 明确）**：

| 段 | S1 是否解析 | S1 是否消费 | 接线切片 |
|---|---|---|---|
| `standard` / `overrides.timings` / `.layout` / `.features` | ✅ | ✅ 展平 + 校验 | S1 |
| `overrides.remove` | ✅ | ✅ 展平期删除 | S1 |
| `messages` / `nodes` | ✅（含 `dlc`、ID 格式校验） | 部分（展平合并） | S1 → S2 转 `NmMessageDef` |
| `rules` | ✅（含必填校验） | ❌ 只存不执行 | S3/S4 |
| `content` / `exceptions` | ⏸ 可选段，暂不解析 | ❌ | S3（content）/ M3（exceptions） |

`content` / `exceptions` 是**可选段**：profile 不写就不存在，已有文件无需改格式。S1 不解析它们，加载时若出现会报「未知字段」——这是**有意为之**，避免「写了但没生效」的静默失败。S3 补解析时再放开。

**S1 不产出 `NmProfile`**：展平产物 `NmStandardFlattener.Flattened` 是中间 DTO，转 `NmProfile` 需要位角色合并与 timing 解析，归 S2。
| **S2** | `NmGlobalTiming`/`NmEffectiveTiming`/`Resolve` + 13 个调用点迁移（**保持 M1 行为不变**，报文级优先语义不得丢） | 四组合回归测试；无 profile 时输出逐字节一致；`NmSpecClosureTests` 全绿 |
| **S3** | O 级原语（`layout_check`/`nid_id_mapping`/`bit_rule`/`cycle_tolerance`/`burst_shape`/`nmsts_transition`）+ 合成 fixture | 每原语：注入故障必触发、合法场景零误报 |
| **S4** | P 级原语（`first_frame_latency`/`repeat_message_duration`/`normal_resume_shape`/`stop_sending`）+ episode/静默推断 | **Rev 5 据实修正**：截至 Rev 5，S4 只完成 `first_frame_latency` + `repeat_message_duration`；`normal_resume_shape` / `stop_sending` **未实现**（`TryEvaluate` 无对应 case，走 `default: return false`），验收不含它们。`max_gap_observe` 不做（§7.2）。主动唤醒两阶段判定**未实现**——`bit_rule` 只支持 `is_zero`，AWB=1 需要 `is_one` + wakeup 作用域 |
| **S5** | 数据体检（§9.1 五项）+ 真实 trace 端到端 | 体检项单测；contract test（必须出现 + 必须不出现）。**Rev 5 收敛**：§9.1 后半五项因判据未定义不做。报告模型见 §9.2（三态检查表） |
| **S6** | Activity 段模型（§7.5：NMSts 切段 + 快发子状态 + `Evidence` + 双窗口裁决）+ 笔刷/文案/图例 | §7.5.5 验收；`NmSpecClosureTests` 回归门 |
| **S7** | 只读选择器 + 标定面板 + 缓存 key + watcher | 面板结构测试；切 profile 必重算；canonical hash 稳定性测试。**已完成**，实施裁决见下方「S7 实施补记」 |

**S6 状态（2026-11-24 更新）**：已实现并合入 `fix/nm-m2-spec-gaps`。Core 侧（`NmSegment` 三字段 / `NmActivityBuilder` 双窗口 + NMSts 切段 / `NmStsResolver` / 快发子段）与 App 侧（VM 三字段 / hover 证据行 / 笔刷 / 文案 / 图例）同批落地，Core NM 测试 478 绿。§7.5.5 的两条硬冲突裁决见该节末尾的实施补记。

### S7 实施补记（2026-11-24）

四块内容与三处设计裁决，外加一轮 code-reviewer 审查的 2 HIGH / 4 MEDIUM / 2 LOW 修复。

**测试**：Core 2154 绿 / App 2365 绿；新增 8 个 Core 测试类 + 5 个 App 测试类，关键分支做过 12 次反向验证。

#### 一、`NmCacheKey` 重塑（§10.1）

```csharp
public sealed record NmCacheKey(
    string SourceId, ushort? Channel,
    double Start, double End, string WindowRef,
    int FrameCount,
    int TraceLoadVersion,        // 新增：trace 内容版本
    string CanonicalProfileHash, // 新增：profile 语义摘要
    string EngineVersion);       // 移除 DbcHash / SupplementHash
```

**`TraceLoadVersion` 用「加载代数」而非「帧内容哈希」**。核实过：`TraceSessionRegistry.LoadAsync` 每次分配新 GUID 的 sourceId，从不复用 service，所以「同一 sourceId 重载」今天不会发生——spec §10.1 自身也给了「或 load generation」这个替代方案。对全部帧做哈希的成本随帧数线性增长，而收益只在一种当前不可达的场景里兑现。代数由 `TraceSource.LoadVersion` 承载（`Interlocked.Increment`，默认 0 保证 16 个既有构造点不改），存在意义是让那种场景一旦出现就自动失效。

**`DbcHash` 仍然计算**，只是不再进 key：`NmSidecarStore` 的陈旧检测（`AppendDbcHashConflict`）与 `ClaimNmWizardAutoOpen` 的去重键都依赖它。移除的是 key 身份，不是这个值本身。

#### 二、canonical hash 搬到 Core（§10.1.1）

`NmCanonicalProfileHash.Compute(NmProfile, string? standardId)` 取代 App 层的 `NmSupplementHash`（后者已删除，其 8 条测试迁到 Core 的 `NmCanonicalProfileHashTests`）。

**用显式 DTO + JSON 序列化，不直接序列化 `NmProfile`**：
- `NmProvenance` 带文件路径与 DBC hash，直接序列化会让「同一份内容换个路径」拆成两个缓存项；
- `NmProfile.Source` 是「怎么来的」而非内容；
- 旧实现用 `string.Join("|")`，自由文本含 `|` 时会拼出同一串（`["x|y"]` 与 `["x","y"]` 碰撞）。JSON 的引号与转义消除这个风险。

排序规则按 §10.1.1 落实：messages→can_id+ext、nodes→name、rules→id、bit_roles→byte+bit、conflicts→field，无业务 key 的字符串集合（Reasons / MissingFields / LoadAssumptions / Clauses）一律 ordinal 排序。

**排除字段**：`Provenance`（路径/哈希）、`Source`（来源标志）、`ResolvedFrom`。有测试钉住「换路径 hash 不变」「改 Source 标志 hash 不变」。

#### 三、locator 三级搜索与多候选语义（§10.1）

`NmStandardProfileLocator.Search(dbcPath, NmProfileSearchOptions)` 按 `Explicit > DbcDirectory > AppData > InstallDirectory` 逐层搜索，`TryLoad(dbcPath)` 保留为「只搜 DBC 同目录」的兼容包装（S1/S2 的既有调用与测试依赖该签名）。

**多候选语义按 spec 而非按旧实现**（此处推翻了既有行为，经用户确认）：

| 情形 | 旧实现 | 新实现 |
|---|---|---|
| 同目录 1 份 | 选中 | 选中 |
| 同目录多份**不同** id@version | 抛异常 | 返回候选列表，`Selected = null`，由选择器让用户选 |
| 同目录多份**同一** id@version | 抛异常 | 抛异常（事实源冲突） |

依据：§10.1 要求选择器「列出可用 profile（base + oem + 项目覆盖）」，复数；而「多份文件 → 报错让用户选」限定在「同 ID/version」。两者不是一回事。

**`BuildRegistry` 的起点必须是选中文件的真实路径**：首版用 `options.ExplicitPath` 当起点，同目录/AppData 命中时它是 null，`extends` 链找不到父级——三条既有 locator 测试因此变红（`TryLoad_ProfileWithExtends_ResolvesParentChain` 等）。改为接收 `selectedPath` 参数后修复。

**两个全局根目录由参数注入而非读环境变量**：Core 层不碰 `Environment.SpecialFolder`，否则测试无法在不污染真实 `%APPDATA%` 的前提下验证「AppData 优先于安装目录」。生产值由 `NmPanelFlow` 提供（`NmAppDataProfilesRoot` / `NmInstallProfilesRoot`，两者都是 internal 可覆写属性）。

#### 四、标定面板（§10.2）

四块全部实现，数据构建放在 Core 的 `NmCalibrationTable`（与 WPF 无关，可不启 UI 测）：

| 方法 | 内容 |
|---|---|
| `Build(rules, report)` | 规则三态表：触发 / 未触发 / 未建模 |
| `BuildComparisons(report)` | 观测 vs 期望（周期中位数/P95 vs `NmCycleStats.ExpectedMs` ± `ToleranceMs`） |
| `BuildDataQuality(report)` | §9 三态体检表 |
| `BuildUnmodeledClauses(report)` | 未建模条款（用 `Clauses` 全列表，不用单值 `Clause`） |

**三态映射只看 `RuleId`，不看文案**：finding 的 message 会随措辞调整变化，用它判断会让「触发」在改一句话后静默变成「未触发」。

**「三者皆无」的判定取决于报告是否 partial**（这里修正了首版的一个真缺陷）：

| 报告 | 该规则无 finding / observation / unmodeled | 判定 |
|---|---|---|
| 完整 | 求值器返回 true 且未给原因 = **跑过、未违规** | 未触发 |
| partial | 引擎可能没跑到这条 | 未建模 |

首版把两种情况都判成「未建模」。问题在于 `layout_check` / `cycle_tolerance` / `burst_shape` 这类原语**只在违规时产出、通过时静默**（它们连 `observations` 列表都不接收，见 `NmRuleEvaluators.Bits.cs:36-41` 的分发签名）。于是合规规则会在面板上显示成「未建模」，让用户以为漏了一条本来检查过的规则——把「查过没问题」报成「没查」，是标定面板最不能出的错。测试 `RuleEvaluatedButSilent_IsNotTriggered_NotUnmodeled` 与 `RuleAbsentFromReport_IsUnmodeled` 分别钉住两侧。

**观测 vs 期望只输出「有期望值」且「样本充分」的指标**：没有声明周期的报文拿中位数自比自、或有效间隔 < 5（`InsufficientSamples`）时报一个伪精确的「520ms」，都比不显示更误导。

#### 五、watcher（§10.1.2）

按 advisor 建议抽成三个纯逻辑部件 + 一个真 `FileSystemWatcher` 包装，**测试不靠 `Thread.Sleep`**：

| 部件 | 职责 | 测试方式 |
|---|---|---|
| `NmProfileChangeDebounce` | ≥200ms 去抖，判定基于「上次**接受**」而非「上次收到」（滑窗会让持续刷新的目录永不触发） | 注入时间戳 |
| `NmProfileFileReader.TryRead` | 用 `FileShare.None` 试开，判定「是否正在写」 | 独占句柄模拟写入中 |
| `NmProfileDependencyGraph.DependentsOf` | `extends` 反向依赖 BFS，父变更 → 整条链失效 | 纯数据结构 |
| `NmProfileWatcher`（`INmProfileWatcher`） | 真 `FileSystemWatcher` + 去抖 + 可读性检查 | VM 接线用假实现 |

**双条件回写校验改为「CTS + generation」而非「CTS + profile hash」**：首版用哈希比对，但 `ApplyNmReport` 才写入当前哈希，首次运行时它是 null，导致所有首次分析的结果被丢弃（`RunNmAnalysisAsync_SetsAnalyzingFalseAndHasReportTrue` 抓到）。改用 `_nmProfileGeneration` 后没有顺序依赖：watcher 失效时 `Interlocked.Increment`，在途运行启动时记下的代数与之不符即被拒。

**已声明的局限**：watcher 只监听选中 profile 所在目录，不监听 `extends` 父文件所在目录（父级通常在 `profiles/base`，需要多 watcher）。代码注释与 `NmProfileWatcherStatus` 都不谎称父文件变化会自动生效。

#### 六、审查修复（2026-11-24，code-reviewer 两轮）

| 级别 | 问题 | 修法 |
|---|---|---|
| HIGH | 移除 `DbcHash` 后，DBC 信号定义改动会命中旧报告（分析器直接消费 `NmSignalSummary` / `NmStsResolver` / `SignalDecode`，而 `NmProfile.Messages` 不含信号） | 新增 `NmDbcSignalHash`：只取影响解码的字段（名/起始位/长度/字节序/类型/因子/偏移），排除 Comment/Unit/Receivers。拼进 key；`OnDbcLoaded` 同时清缓存并重算 |
| HIGH | `Dispose` 不释放 profile watcher → 关窗后句柄泄漏且继续重算 | `Dispose` 末尾调 `DisposeNmProfileWatcher()` |
| MED | 去抖把「判定通过但因文件被锁而跳过」的事件也算作已接受 → 保存一次 profile 可能永不触发 | `ShouldRaise` 只判定、`MarkRaised` 才推进；且「不在关注集合」与「文件正在写」两步都前移到推进之前 |
| MED | 自动发现单份 profile 时选「未选择」不重算（`ExplicitPath` 两侧都是 null） | 新增 `NmProfileOptOut` 独立状态；opt-out 时跳过整个 Search |
| MED | 规则行无点击 seek、未建模条款不显示条款号（§10.2 两条硬要求） | 规则行加 `MouseBinding` → `SeekToNmTimeOrIgnoreCommand`（`CanSeek` 驱动 Cursor）；`NmUnmodeledClauseRow.ClausesText` |
| MED | `extends` 父文件变化 → 整链失效未接线 | **明确延期**，`NmProfileWatcherLimitText` 在选中 profile 时始终显示该限制（不静默） |
| LOW | 显式选择后候选列表塌缩成 1 项 | Explicit 分支仍调 `EnumerateAllCandidates`，显式选中项不在结果里时补进去 |
| LOW | 同时有 observation 与 unmodeled 的规则显示「未触发」 | 优先序改为 Triggered > **Unmodeled** > NotTriggered |

三条 HIGH/MED 修复均做过反向验证（删实现 → 确认对应测试变红）。

**未修的已知项**：`App.xaml` 里注册的 `NonEmptyStringToVis` / `CountToVis` 目前无 Application 级消费者（面板已在 `UserControl.Resources` 局部注册）。冗余但无害，留给后续统一清理时一并处理。

#### 七、UI

- 选择器常驻顶部（不随 `NmHasReport` 隐藏）：「未选择企标（仅观测）」是第一项且永远可选，用户能退回只观测状态。
- 重建候选列表时用 `_nmApplyingProfileChoices` 抑制选中回调——清空再填充会让 `SelectedItem` 短暂变 null，不抑制会每次都触发一次「切换到未选择」的重分析。
- 标定面板默认折叠（`Expander IsExpanded="False"`）：四块表常驻会吃掉 360px 面板绝大部分垂直空间。
- 两个新转换器（`NonEmptyStringToVisibilityConverter` / `CountToVisibilityConverter`）定义在 **UserControl.Resources** 而不只是 App.xaml：面板级 XAML 冒烟测试会独立实例化 UserControl，依赖全局资源会抛 `XamlParseException`。这个坑在实现时被 `NmViewXamlResourceTests` 与 `NmHoverCardTests` 抓到（首版把资源 key 写成 `NonEmptyToVis` 而注册的是 `NonEmptyStringToVis`）。

#### 八、CBV byte1.bit6 预留位漏检（2026-11-24 补修）

**这是既有缺陷，不是 S7 引入的**，但它与 §10.2 的标定面板直接相关——标定面板会把「没建模」如实显示出来，而这一位的真实问题是**建模了却不检查**，面板无从反映。

§5.4.3 的表格里 Byte1 的 Bit1/2/3/5/6/7 都是「预留位填充 0」。吉利 profile 的 `bit_roles` 声明了 1/2/3/5/7，**独缺 bit6**：

- base profile（AUTOSAR CanNm）把 bit6 声明为 `PartialNetworkInfo`（PNI）
- 吉利 profile 用 `overrides.remove: bit_roles[1.6]` 删掉它（吉利 §5.4.3 确实没有 PNI 概念）
- **但删完没有补回「bit6 必须为 0」** → 该位处于「无角色、无规则」状态

后果：某个 ECU 在 byte1.bit6 填 1 时，报告里完全没有痕迹。这是漏检，不是误报，且没有任何信号。

**修复的两个坑**：

1. **不能只加角色声明**。展平器 `ApplyRemove` 在 `MergeLayout` **之后**执行，`remove: bit_roles[1.6]` 会把子层刚声明的 `Reserved_B1_6` 一并删掉。正解是**删掉 remove 行**：`MergeLayout` 本来就按 `(byte,bit)` 合并，同位子覆盖父，子层声明已足够覆盖 PNI。
2. **PNI 检查按角色名定位**（`NmProfilePrecedence.EffectivePniRole` 找 `PartialNetworkInfo`），改名成 `Reserved_B1_6` 后 PNI 检查自然不执行，不需要额外处理。

**反向验证时抓到的测试假绿**：首版测试只断言「(1,6) 有位角色」，删掉 `Reserved_B1_6` 后仍绿——因为 base 的 `PartialNetworkInfo` 也占 (1,6)，`Assert.Contains` 匹配到了它。判据必须**同时断言 `Constraint == MustBeZero`**，否则「父层角色顶替子层预留位」这种正是要防的情况测不出来。

新增 `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmReservedBitCoverageTests.cs`（5 条），钉住 §5.4.3 的 byte1 bit1/2/3/5/6/7 与 §5.4.4 的 byte3 bit1–7 逐位覆盖，并反向守卫 bit0（RMR）/ bit4（AWB）不得被误判成预留位。

#### 九、BLF 解析器把 DIR 位当成了 RTR 位（2026-11-24 补修）

**这是既有缺陷，与 S4/S5 无关，但卡住了 S5 的「真实 trace 端到端」验收。**

§11 的 S5 验收要求「真实 trace 端到端」，而 §14.6 的「必须出现」表里有两条依赖 PMS 帧（`0x18FFFA27`）：

| finding | 依据 |
|---|---|
| `NM_NID_MISMATCH` | PMS 帧 byte0=0x08 ≠ 0x27 |
| `NM_RMR_SET_WHILE_DISABLED` | 同一帧 byte1.bit0=1 |

这帧在真实 trace 里是 **Tx 帧**（`can_flags` bit0=DIR=1），带 8 字节有效数据。而三个 BLF 解析器都写成 `if ((flags & 0x01) != 0) ff |= FrameFlags.Rtr`——把 **DIR 位当成了 RTR 位**。于是这一帧被 `NmFrameSelector` 当远程帧丢弃，两条基线 finding 永远不产出。

**权威依据**（python-can `can/io/blf.py:93-98` 与 `vblf_constants.py` 的 `CanFdFlags`）：

| 位 | 常量 | 含义 |
|---|---|---|
| `0x01` | `DIR` | 方向位，1 = 本机 Tx |
| `0x80` | `REMOTE_FLAG` | RTR 远程帧 |

用 python-can 交叉验证真实 trace：5671 帧里 **0 个 `is_remote_frame`**，`is_rx` 除 1 帧外全为 True——与「PMS 帧是唯一的 Tx 帧」完全吻合。

**同时修正的两处**：

1. **`CanMessageFlow` / `CanMessage2Flow`**：RTR 改读 `0x80`，并补上 `IsTransmit`（此前从不设置，方向信息全丢）。这两个 flow 的字段偏移（`HBBI8s` / `HBBI8sIBBH`：flags@2、id@4、data@8）经 vblf 与 python-can 双向核对无误。

2. **`CanFdMessageFlow` / `CanFdMessage64Flow` 未改**——它们的**字段偏移整体是错的**，属于既存债（`BlfFormat.cs` 已标注「test-compatible layout，v3.52.0 再对齐」），不能在错误地基上修位语义：

| 字段 | vblf `HBBIIBBBBI64sI` 真实 offset | `CanFdMessageFlow` 当前读取 |
|---|---|---|
| `flags` | 2（1 字节） | 2 ✓ |
| `frame_id` | **4** | 8 ✗（读到 frame_length） |
| `frame_length` | **8** | 16 ✗ |
| `canfd_flags` | **13（1 字节）** | 4（4 字节）✗（读到 frame_id） |
| `data` 起点 | **20** | 22 ✗ |

后果：真实 obj100 帧 payload 是 88 字节，而 `CanFdMessageDataSize=90` 会直接抛 `ReplayFormatException` 并整帧丢弃——**任何含 CAN FD 帧的 BLF 目前都在静默丢帧**，只表现为 corruption 计数。这不是本次引入的，但必须明确记录：**FD 路径不可用，不是「已修正」**。

**为什么本轮不在错误地基上顺手改位语义**：在 `fdFlags = frame_id` 的前提下，`fdFlags & 0x2` 实际是在读 CAN ID 的 bit1，把 ID 的位当成 BRS——改动只会让「看起来修了」比「明显没修」更危险。偏移与位语义必须同批修（v3.52.0）。

**修复后真实 trace 的输出**：`NM_NID_MISMATCH` 与 `NM_RMR_SET_WHILE_DISABLED` 都产出，`nid_mapping` 体检从 `Passed` 变 `Failed`，§14.6「必须不出现」的四条仍全部守住。真实 trace 全部是经典 CAN（python-can 核对：5671 帧、`is_fd` 全 False），所以 FD 路径的缺陷不影响本次验收。

**新增测试**：`BlfParserTests` 加 5 条（DIR 位不是 RTR、`0x80` 才是 RTR、DIR=0 是 Rx、CanMessage2 同理、Tx 帧带数据不被丢弃）；新增 `NmRealTraceEndToEndTests`（2 条，`[Trait("Manual")]`，CI 无 fixture 时跳过）把 §14.6 的「必须出现 / 必须不出现」在**真实** BLF 上钉住。

**为什么合成 fixture 兜不住**：`NmRealTraceContractTests` 用手搓时间戳数组，根本不经过 BLF 解析器。这正是这个缺陷能潜伏至今的原因——两个 contract test 分工不同，缺一不可。

**遗留：FD 路径的既存缺陷（不在本轮范围）**。`CanFdMessageFlow` 与 `CanFdMessage64Flow` 的字段偏移整体错误（见上表），且 `CanFdMessage64Flow` 的 `frame_id` 读自 offset 16（真实 offset 4），也不读 direction 字节。`BlfFormat.cs` 已把这两处标为「test-compatible layout，v3.52.0 再对齐」。本轮**不动**，理由：

- 真实 trace 无 FD 帧，不影响 S5 验收；
- 偏移与位语义必须同批修，单独修位语义会在错误地基上造出更隐蔽的错；
- 现有 FD 测试（`BlfParser_CanFdMessage_Parsed` 等）用的是 90 字节伪造 fixture，改真实布局会同时要求改测试，属于独立一轮。

**不得把 FD 路径记作「已修正」**——它目前会丢帧。

**S6 硬依赖 S2**：`NmActivityBuilder.Build` 接收 `NmEffectiveTiming`，该类型由 S2 产出。**S2 必须先合并，S6 才能开工。** 不允许并行会话自行补一份时序实现。

**文件所有权**（S2 与 S6 涉及同一批文件，必须划分）：

| 文件 | S2 负责人 | S6 负责人 |
|---|---|---|
| `NmTiming.cs`（新：`NmGlobalTiming`/`NmEffectiveTiming`/`Resolve`） | 全部 | 只读引用 |
| `NmAnalyzer.cs` | timing 解析（`:255` 附近） | `Build` 调用（`:253`） |
| `NmActivityBuilder.cs` | 不碰 | 全部 |
| `NmReport.cs`（`NmSegment` 字段） | 不碰 | 全部 |
| `NmPanelFlow.cs` / `NmSegmentBrushConverter.cs` / `TraceViewerViewNmPanel.xaml(.cs)` | 不碰 | 全部 |
| 测试（`NmSpecClosureTests` / `NmMultiEpisodeActivityTests` / `NmPanelFlowTests`） | 时序相关 | 段相关 |

**`NmSegment` 字段由 S6 一次落全**，S2 不得引用未存在的字段。

**S6 归属待确认（Rev 5 补记）**：旧计划 `2026-11-23-nm-m2-rule-engine.md` 第 3622 行声明 S6「由并行会话 `claude-proj2-1a` 实现」。**该声明无法核实**——`git worktree list` 只有一个工作树，但并行会话完全可以在同一工作目录内修改文件（该计划第 22 行自己也这么写）。因此：

- 本 spec 不改变上述文件所有权划分。
- 任何后续切片在动 `NmActivityBuilder` / `NmSegment` / `NmPanelFlow` / `TraceViewerViewNmPanel.xaml(.cs)` 之前，**必须先向用户确认 S6 的实际归属**，不得自行认领。
- S4/S5 的实施（Rev 5 范围）**不触碰**这些文件。

S3/S4 的规则逻辑只用合成数据验，避免「用坏数据调规则」。

### 11.1 测试产物组织（Rev 2 声称做了但没做）

每个 profile 目录配套：

```
profiles/oem/geely-cv@2.0.nmstd.yaml
profiles/oem/geely-cv@2.0/
  ├── golden/                     # 合成正常场景，每个 ≤200 帧
  │   ├── passive-wakeup.blf-fixture.json
  │   ├── active-wakeup.blf-fixture.json
  │   └── release-to-sleep.blf-fixture.json
  ├── faults/                     # 逐规则注入故障
  │   ├── cycle-deviation.json
  │   ├── burst-missing.json
  │   └── reserved-bit-set.json
  ├── expected.json               # 每个 fixture 必须出现的 finding
  ├── forbidden.json              # 每个 fixture 必须不出现的 finding
  └── calibration.json            # 标定报告（观测 vs 期望、未建模条款）
```

- fixture 用 JSON 描述帧序列，不提交 BLF（避免大文件和涉密矩阵进仓）。
- 真实 trace 只保留**受控外部路径 + SHA256**，CI 通过环境变量指向。
- CI 校验：每个 profile 的规则 ID 唯一、`expected`/`forbidden` 覆盖全部规则、追溯矩阵完整。

## 12. 测试策略

| 层 | 数据 | 标准 |
|---|---|---|
| 原语逻辑 | 合成 fixture | 注入故障必触发；**合法场景零误报** |
| 合法场景负例集 | 合成 | 合法被动唤醒、合法主动唤醒、合法释放/睡眠、RSS 停发、窗口截断 —— **均不得报违规** |
| 位义解码 | 合成 | 文案含角色名；预留位违规报 `NM_RESERVED_BIT_SET` |
| 时序统一 | 合成 | 无 profile / 报文级 / sidecar / OEM 容差四组合 |
| 继承展平 | 多层 YAML | 覆盖顺序、三态 patch、循环检测 |
| 校验器 | 非法 profile 集 | 每类错误都报 |
| 真实 trace | 吉利 trace + DBC | contract test：**必须出现**和**必须不出现**的 finding 都断言，**同时断言归因 observation**（`all_nm_silent` 存在且 `IsInference=true`、原因未知；`NM_SENDER_SILENT_BEYOND_TIMEOUT` 不存在不能仅因单报文旁路偶然满足） |
| Activity 段 | 合成 + 真实 | NMSts 切段边界、快发子状态、`IsWindowTruncated` 与 `StateBoundaryKnown` 四种组合、逐帧不可读降级 |

## 13. 风险

| 风险 | 缓解 |
|---|---|
| 把正常睡眠判成违规 | §3 可观测性分级 + §4.1 静默判定表 + 合法释放负例集 |
| 真实 trace 有故障被当成规则写错 | 规则逻辑只用合成数据验；真实 trace 验「证据可解释」 |
| YamlDotNet 新增依赖 | 仅 Core 引用；严格反序列化、禁止未知字段；canonical hash 规则 |
| 位义硬编码残留 | 代码中不得出现 `0x40`/`0x10` 位掩码常量 |
| M2 范围过大 | 五片独立验收；S5 最后做 |

## 14. 真实数据基线

材料：

- DBC：`C:\Users\13777\Desktop\can_log\LCV&微卡平台_BMS通信矩阵_20260706.dbc`
- Trace：`C:\Users\13777\Desktop\can_log\微卡26商改网络测试NM_16测试Log.blf`
- sidecar：`..._20260706.nmprofile.json`（`nid_byte=0, cbv_byte=1, user_data_offset=4, user_data_length=4, pn_enabled=false`）

### 14.1 全局计数

| 项 | 值 |
|---|---|
| 整份 trace | 32.570 s，5671 帧 |
| NM 帧 | 63 条（BMS 62、PMS 1、TMRU 0） |
| BMS NM 活动 | +0.095 s → +30.536 s，跨度 30.442 s |

### 14.2 字节映射（DBC 独立核对）

| PDF | DBC 信号 | start bit | 字节/位 |
|---|---|---|---|
| ECU Address | `BMS_Address` | 0 | byte0 |
| CBV bit0 RMR | `BMS_RMR` | 8 | byte1.bit0 |
| CBV bit4 AWB | `BMS_AWB` | 12 | byte1.bit4 |
| 唤醒原因 | `BMS_Wakeup_reasons` | 16 | byte2 |
| Byte3.bit0 NMSts | `BMS_NMSts` | 24 | byte3.bit0 |
| 保持唤醒原因 | `BMS_Stayawake_reasons` | 32 | byte4–7 |

**byte4–7 有 DBC 信号定义**，sidecar 的 `user_data_offset=4` 是用户声明的观察范围，不是「无信号区」。校验器必须检查 DBC 信号与零填充规则的重叠。

### 14.3 实测特征

| 时刻/区间 | 现象 | 判定 |
|---|---|---|
| +0.000 s | PMS 单帧 `08 01 00 00 00 00 00 00` | 见 §14.4 |
| +0.095 s | BMS 首帧，byte1=0x00（AWB=0） | 被动唤醒 |
| +0.609 s | byte2 首次 0x30 | 唤醒原因出现 |
| +2.216 s | byte3.bit0 → 1（进入 NOS） | NMSts 变化 |
| **+9.714 → +12.677 s** | **NM 静默 2.963505 s** | **原因未知，非单节点违规**（§14.5；不推断为「正常释放」） |
| +12.677 → +12.859 s | 快发段 10 帧，9 个间隔 19.021–20.996 ms，跨度 181.497 ms，AWB=1 | 合规 |
| 稳态 | 51 个间隔，中位 535.502 ms，范围 514.054–537.038 ms | 全部在 450–550 ms 内 |

### 14.4 PMS 单帧

payload `08 01 00 00 00 00 00 00`，ID `0x18FFFA27`（低字节 0x27）：

| 字段 | 实测 | 期望 | 预期 finding |
|---|---|---|---|
| byte0 NID | 0x08 | 0x27（§5.4.2 扩展帧低字节） | `NM_NID_MISMATCH` |
| byte1 bit0 RMR | 1 | 0（§5.4.3） | `NM_RMR_SET_WHILE_DISABLED` |

**NID 判据的三层关系**（Rev 2 只说「ID 映射优先」，没说清与 DBC 站地址的关系）：

1. **OEM 规范决定算法**：扩展帧 `NID == CAN ID 低字节`，标准帧 `NID == ID − 0x400`。
2. **DBC 站地址是独立事实源**，仅用于交叉校验（`NmStationAddress BMS 244` = 0xF4，与 BMS ID 低字节一致）。
3. **两者不一致时产 `NM_CONFIG_CONFLICT`**，不得让 DBC 覆盖规范算法，也不得静默选一方。

**capability gate 必须同步修改**：现有 `NmCapabilityGate.cs:103-110` 在「所有节点都没有站地址」时直接禁用 `NidCheck`。按新方案，只要 NID 字节与 ID mapping 已知就应启用，站地址缺失只降级为「不做交叉校验」。

### 14.5 静默段的定性（只到证据能证明的程度）

静默期间（+9.714 → +12.677 s）**可证明**的事实：

- 全网没有任何 NM 帧
- 区间内有 364 帧应用报文、57 种 CAN ID
- 全总线最大空档仅 0.920502 s
- 12.677 s 后 AWB=1 且有 10 帧快发

**不可证明**：这 364 帧应用报文的 sender 未归因到 BMS；企标 §5.3.1/§5.3.2 规定睡眠和预睡眠模式禁止发送应用报文（`spec.txt:247-262`），因此不能用「有应用报文」推断 BMS 处于网络模式。

正确表述：

> NM 全静默，原因未知（释放/下电/记录停止/bus sleep 无法区分）；随后观察到 AWB=1 + 快发的恢复行为。是否符合释放/重唤醒条款为 **inferred**，不能确认正常。

**不得报 `NM_SENDER_SILENT_BEYOND_TIMEOUT`**：该 finding 的前提是「其它 NM 节点仍在发送」，此处不成立。正确输出：`NM_ALL_NM_SILENT`（info）+ observation。

### 14.6 回归基线（contract test）

**必须出现**：

| finding / observation | 依据 |
|---|---|
| `NM_NID_MISMATCH` | PMS 帧 byte0=0x08 ≠ 0x27 |
| `NM_RMR_SET_WHILE_DISABLED` | 同一帧 byte1.bit0=1 |
| **`NM_REPEAT_MESSAGE_DURATION_EXCEEDED`（第一段）** | RMS 实测 1.584s，无快发允许 1.440–1.560s，**超 +24ms** |
| **`NM_REPEAT_MESSAGE_DURATION_EXCEEDED`（第二段）** | RMS 实测 1.789s，有快发允许 1.640–1.760s，**超 +29ms** |
| observation `all_nm_silent` | 9.713674–12.677179，且标 `inferred`（原因未知） |

**必须不出现**：

| finding | 依据 |
|---|---|
| `NM_SENDER_SILENT_BEYOND_TIMEOUT` | 静默段全网 NM 静默，非单节点违规 |
| `NM_CYCLE_DEVIATION` | 稳态 514–537 ms 全在 450–550 ms 内 |
| `NM_WAKEUP_BURST_MISMATCH` | 快发段 10 帧 × 19–21 ms 合规 |
| `NM_RESERVED_BIT_SET` | 除 PMS 帧外，BMS 帧预留位均为 0 |

**两段 RMS 超差是真实违规**，不是基线噪声。两段都超出吉利的 RMS 上限（+24ms / +29ms），说明这份 trace 的网络确实存在问题——这正是「真实 trace 可能有故障」的实例。contract test 必须断言这两条 finding 存在，否则 `repeat_message_duration` 实现了也验不出来。

**口径修正（2026-11-24，真实 trace 实测）**：本表原先写「2.121s / 2.325s，超 +561ms / +565ms」，那是**状态切换时刻**的口径（首个 NMSts=1 帧减首个 NMSts=0 帧）。实现采用的口径是**最后一个 NMSts=0 帧**作为 RMS 终点，得到 1584ms / 1789ms。

两个口径都不是错的，但含义不同，必须说清：

| 口径 | 第一段 | 第二段 | 含义 |
|---|---|---|---|
| 状态切换（首个 NMSts=1） | 2.121s | 2.325s | 节点**离开** RMS 的时刻；含切换前最后一个周期的等待 |
| 末个 RMS 帧（实现采用） | 1.584s | 1.789s | 能观测到的**最后一个处于 RMS 的帧**；是真实持续时长的**下界** |

实现取下界是刻意的：NMSts=1 的帧证明状态**已**切换，但「最后一个 NMSts=0 帧」与「首个 NMSts=1 帧」之间那段时间里，节点是否仍在 RMS 不可观测（可能早已切换但没发帧，也可能一直发到切换）。用上界会**凭空多算约一个周期**（本例 537ms / 536ms）——一个恰好合规在 1560ms 上限的 RMS，按上界量就成了约 2097ms，被误判超差；用下界最多漏判一个周期，但不会冤枉合规段。

**这也是为什么两段的超差只有 +24ms / +29ms**——真实 trace 的 RMS 只是略微超出，而原文档写的 +560ms 会让读者以为网络问题很严重。修正后的数字更接近事实，但结论（两段都超差）不变。

## 15. 未决项

| 项 | 默认 | 需确认 |
|---|---|---|
| PMS 单帧来源（记录残留 / 台架模拟 / 真实 PMS） | 不推断来源，只报两条违规事实 | 可后续核对 |
| profile 目录约定 | `profiles/<vendor>/<name>@<version>.nmstd.yaml` | 可调 |
| 快发检测阈值（3 帧 / 1.5×） | 见 §7.4 | 可调 |
| `Unmodeled` 是否进 AI 输出 | **是**（只发条款号，不发条款正文） | 已按 M1 §8.1 落实 |

## 16. Rev 2 → Rev 3 修正清单

第二轮评审对 Rev 2 的判定：**不通过**。Rev 1 的 4 条 CRITICAL 中 #4 已解决，#1/#2/#3 只部分解决。Rev 3 修正如下。

| # | Rev 2 问题 | 严重度 | Rev 3 修正 |
|---|---|---|---|
| R3-1 | `Finding/Observation/Unmodeled` 只在规则结果层，`NmReport` 装不下 | CRITICAL | §7.1.2 冻结报告契约：`NmObservation` + `NmUnmodeledRule` 进 `NmReport`，全层复用（Rev 5 后该节号从 7.1.1 改为 7.1.2） |
| R3-2 | NM_02/NM_15 只有 `resume_after_silence`，判不了「以正常周期发送」 | CRITICAL | §7.2 新增 `normal_resume_shape`（周期+容差+最少帧数） |
| R3-3 | §4.1 把 `NM_ALL_NM_SILENT`（timeoutMs）与 `NM_BUS_WIDE_SILENCE`（max(2×cycle,500)）阈值写混 | HIGH | §4.1 按 `NmGapAnalyzer.cs:29-32` 实际代码分开列出 |
| R3-4 | §14.5 把「正常释放」当事实，超出可证明范围 | HIGH | §14.5 改为「原因未知，恢复行为为 inferred」 |
| R3-5 | NID 改 ID 映射后未同步 capability gate 与冲突优先级 | HIGH | §14.4 三层关系 + gate 修改要求 |
| R3-6 | 主动唤醒漏检「快发不完整」，两处阈值不一致 | HIGH | §7.3 两阶段（宽松检测 2.0× / 严格判形态 1+ratio），锚点改为节点级 |
| R3-7 | `SilentBeyondTimeout` 仍会在正常释放时置 true，且已输出给 AI | HIGH | §7.1.2 改三态 `NmSilenceVerdict` + AI 负例测试 |
| R3-8 | NM_06 的 `nmsts_transition` 判不了 T_REPEAT_MESSAGE 双区间 | HIGH | §7.2 拆分：NMSts 只作锚点（O），时长由 `repeat_message_duration` 判 |
| R3-9 | 三态 patch 与 record 反序列化不兼容 | HIGH | §6.2 改显式 set/remove DTO；或保留 YAML 节点 presence |
| R3-10 | canonical SHA256 无规范 | HIGH | §10.1.1 七条 canonicalization 规则 + 稳定性测试 |
| R3-11 | 缓存 key 重复哈希 DBC/sidecar | HIGH | §10.1 key = canonical profile hash + EngineVersion |
| R3-12 | 单 NM 报文场景漏检共享 ID 下的节点停发 | HIGH | §4.1 静默单位改为 `(CAN ID, extended, NID)` |
| R3-13 | §5.2 仍漏 5 个阈值调用点，且丢掉报文级周期优先级 | MEDIUM | §5.2 补齐 13 处 + `Resolve(def, global)` |
| R3-14 | 位角色「DBC 优先」让 profile 无法施加 OEM 约束 | MEDIUM | §5.1 位置/名称与约束语义分开合并 |
| R3-15 | `layout_check` 未使用位与校验器豁免互相抵消 | MEDIUM | §5.1.1 三分支表 |
| R3-16 | 测试负例集不足，「不出现」可能偶然满足 | MEDIUM | §12 扩负例集 + 同时断言归因 observation |
| R3-17 | 实现切片顺序不合理，S3 过大 | MEDIUM | §11 重排为 S1–S6 |
| R3-18 | watcher/DBC hash memo 并发规则未定义 | MEDIUM | §10.1.2 去抖 + generation guard |
| R3-19 | §2.4 时序表「前置状态」概念混用 | LOW | 统一为 NOS / RMS 术语；±10% 与 ±60ms 分栏 |
| R3-20 | §16 虚报「fixture 组织已解决」 | LOW | §11.1 真正定义产物目录与 CI 契约 |
| R3-21 | Activity 段模型完全没覆盖，时间条把 RMS 画成「唤醒+发送中」 | HIGH | §7.5 新增：NMSts 切段 `repeat`/`normal`、`Evidence` 字段、双窗口裁决（1500/1700 并存）、UI 笔刷同批 |

## 17. Rev 3 → Rev 4 修正清单（第三轮评审）

第三轮评审判定：Rev 3 **暂不能进入实现**。§7.5 的四个验收时间点经 BLF 复算全部正确，但发现两条 CRITICAL 与八条 HIGH。

| # | Rev 3 问题 | 严重度 | Rev 4 修正 |
|---|---|---|---|
| R4-1 | `NmStandardProfile` 没有 `Timings`/`Layout`/`Features`，装不下 §5.2 与 §6.2 的 patch | CRITICAL | §5.4 补齐 `NmStandardTimings`/`Layout`/`Features`/`PatchSet` |
| R4-2 | S6 依赖 S2 却写「并行且 S1–S5 不动」 | CRITICAL | §11 改为「S2 是 S6 硬前置」，并给出文件所有权表 |
| R4-3 | `repeat` 段吞掉快发子状态，等于另一种形式丢信息 | HIGH | §7.5.1.1 新增 `immediate`/`normal_transmit` 子段 |
| R4-4 | 三处快发阈值互相冲突（0.5×cycle / 2.0×Immediate / 1.5×Immediate） | HIGH | §7.5.3.1 统一为 `NmBurstDetector`，含 `ImmediateCycleMs` 缺失回退 |
| R4-5 | `IsWindowTruncated` 定义与生成边界不符（单状态被误标截断） | HIGH | §7.5.2 拆出 `StateBoundaryKnown`，严格定义 `IsWindowTruncated` |
| R4-6 | NMSts 不可读情形未穷尽（位宽/符号/字节序/逐帧） | HIGH | §7.5.1.2 补 7 类情形 + 逐帧降级规则 |
| R4-7 | 非空时序类型没规定 500/2000 默认值注入点 | HIGH | §5.2.1 `NmTimingDefaults.FromProfile` 唯一注入点 |
| R4-8 | 缓存 key 去掉 DBC hash 后漏了 trace 内容版本 | HIGH | §10.1 新增 `TraceContentHash`，保留 source/channel/窗口 |
| R4-9 | 真实 trace 两段 RMS 各超差约 560ms，contract test 未断言 | HIGH | §14.6 新增两条 `NM_REPEAT_MESSAGE_DURATION_EXCEEDED`；§4 补该 finding |
| R4-10 | `normal` 段被当成完整 NOS 生命周期 | MEDIUM | §7.5.1.3 明确「发送段」，hover 文案限定 |
| R4-11 | §5.1.1 与 §8 对 DBC 信号重叠给出相反指令 | MEDIUM | §8 改为「仍执行 OEM 约束 + 记冲突」 |
| R4-12 | UI 只改 hover 签名，`NmTimelineSegmentVm` 无证据字段 | MEDIUM | §7.5.2.1 补 VM 三字段 + `BuildTimelineRows` 映射 |
| R4-13 | 报告契约缺 `SilenceVerdict` 字段、`NmObservation.IsInference`、消费方改造点 | MEDIUM | §7.1.2 补字段 + 四个消费方清单 + AI 负例（Rev 5 后该节号从 7.1.1 改为 7.1.2） |
| R4-14 | R3-16 声称的 observation 断言未进 §12 | MEDIUM | §12 补一行 |
| R4-15 | §14.3/§14.6 仍称「正常释放」 | LOW | 改为「原因未知」 |

## 17.1 S1 实现评审（代码评审，非设计评审）

S1 落地后 code-reviewer 提 5 HIGH / 7 MEDIUM，均已修或明确降级：

| 问题 | 处理 |
|---|---|
| `Load` 不调 `Validate` | 加载即校验；新增 `LoadAndFlatten` 做展平后校验 |
| 三态 patch 无法表达 `remove` | `NmStandardOverrides.Remove` + flattener 消费 |
| 未知 `constraint`/`observability` 静默降级 | 改为抛异常并列出合法值 |
| schema 缺 `Dlc`，展平产物非 `NmProfile` | 补 `Dlc` + ID 校验；`content`/`exceptions`/`NmProfile` 转换**显式推迟**（见 §11 S1 边界表） |
| `FromProfile` 丢 `RepeatMessageMs` 兼容 | 加 `m1RepeatMessageMs` 参数，M1 单值优先 |
| 判环用裸 id 误判版本链 | 改用 `id@version` |
| messages 合并 key 忽略 `Extended` | key 改为 `(Id, Extended)` |
| null 列表元素抛 NRE | 统一抛 `NmProfileValidationException` |
| 列表顺序不确定 | 展平结果按业务 key 排序 |

## 18. Rev 1 → Rev 2 修正清单

| # | Rev 1 错误 | 严重度 | Rev 2 修正 |
|---|---|---|---|
| 1 | 把 T_NM_TIMEOUT 当发送间隔上限，2.964s 静默判违规 | CRITICAL | §3 可观测性分级 + §4.1 + §14.5：内部定时器 U 级，静默按全网/单节点区分 |
| 2 | 主动唤醒「AWB ⟺ 快发」四象限表 | CRITICAL | §7.3 降级为「疑似不一致」，仅 episode 首段，标 inferred |
| 3 | 要求 PMS 帧报 `NM_NID_MISMATCH`，但依赖 DBC 站地址 | CRITICAL | §14.4：改按 §5.4.2 ID 映射规则判 |
| 4 | T_WakeUp 与 T_START_NM_TX 合并为 120ms | CRITICAL | §2.4 + §7.2 分开建模 |
| 5 | 「user_data_offset=4 = 无信号区」错误推论 | HIGH | §14.2：byte4–7 有 `Stayawake_reasons` 信号 |
| 6 | NM_05/08/14 用 `timeout_reset` 原语 | HIGH | §4 全部标 U，删除该原语 |
| 7 | 遗漏 NM_06/09/10/12/13/15/16、§5.9 | HIGH | §4 补全 NM_01–17 + 各节覆盖表 |
| 8 | `field_sequence` 读整字节表达不了 bit0 | HIGH | §5.3 `NmFieldSelector` 带 Bit/Length |
| 9 | 只改 `NmCycleAnalyzer` 容差 | HIGH | §5.2 统一 `NmEffectiveTiming`，7 个调用点 |
| 10 | 快发计数/边界/容差未定案 | HIGH | §7.4 两层阈值 + 结果模型 |
| 11 | profile 选择/加载/缓存链路未定义 | HIGH | §10.1 搜索顺序 + 缓存 key 修正 |
| 12 | 三层继承「所有差异都记冲突」 | HIGH | §6.2 三态 patch，继承差异不记冲突 |
| 13 | 测试方案把违规 trace 当正常基线 | HIGH | §14.6 contract test：必须出现 + 必须不出现 |
| 14 | `NmRuleDef` 只有 when/expect 两段 | MEDIUM | §5.3 + §7.2 按可观测性重定义原语集 |
| 15 | `NmRuleContext` 缺 DBC/原始帧/选择诊断 | MEDIUM | §7.1 补齐 |
| 16 | 数据体检 5 项，缺重复帧/回环/跳变 | MEDIUM | §9 扩到 10 项 |
| 17 | AI 输出与隐私留作未决 | MEDIUM | §15 落实：只发条款号 |
| 18 | 参数字典全字符串化 | MEDIUM | §5.3 改结构化 record |
| 19 | fixture/标定产物组织缺失 | MEDIUM | **Rev 2 虚报已解决**，Rev 3 在 §11.1 真正定义 |
| 20 | trace 时间描述不精确 | LOW | §14 全部三位小数 + 明确基准 |
| 21 | RMR 用 `NM_RESERVED_BIT_SET` 语义不准 | LOW | `NmBitConstraint.MustBeZeroWhileDisabled` + `NM_RMR_SET_WHILE_DISABLED` |

### 18.1 评审确认无误的部分（保留）

- 字节编号 0-based，PDF 与 DBC 完全一致
- 吉利 CBV 无 PNI 位，bit6 是预留
- trace 帧数、快发段 10 帧 × 19–21ms、稳态 535ms、PMS payload 均核对无误
- 规则与 finding 需带 `rule_id`/`requirement`/`source_ref` 可追溯

## 19. Rev 4 → Rev 5 修正清单

Rev 4 之后的 S1–S4 实施与一次独立设计评审，暴露出 Rev 4 内部不一致与若干「写了但无法实现」的条款。Rev 5 逐条收敛。

| # | Rev 4 的问题 | 严重度 | Rev 5 修正 |
|---|---|---|---|
| 1 | **spec 声称「S1–S4 实施后」，实际 S4 只完成一半**：`normal_resume_shape` / `stop_sending` 在 `TryEvaluate` 里无 case，走 `default: return false`；`NmRealTraceContractTests` 仍断言 `NM_09` 在 Unmodeled | CRITICAL | 标题改「S1–S3 与 S4 部分」；§11 S4 验收据实修正，明确两项未实现 |
| 2 | **规则 ID 与条款号未区分，且已是 active bug**：`NM_01` 规则实为 §5.4.2 NID 映射、`NM_05` 规则实为 §5.4.3 RMR——而 §4 表里这两条都是 U 级不可观测。今天的报告给不可观测条款发 NID finding | CRITICAL | 新增 §4.0；`NmFinding` 加 `Clauses` **列表**（单值会造假归因）；契约测试锚定正则 + 成员资格 + 完整性 |
| 3 | **条款目录 ID 模型不成立**：§4 表里 `§5.5` 出现 4 次、`§5.4.4` 出现 2 次，节号不能当唯一键；base 的 `AUTOSAR ... §9` 与吉利 `§5.x` 不在同一命名空间 | HIGH | `NmClauseDef` 改复合键 `Id` + 可重复的 `Clause`；区分文档维度 |
| 4 | **未覆盖的条款在报告里完全不可见**：引擎只遍历 `rules`，未声明的条款既无 finding 也无 Unmodeled | CRITICAL | `NmStandardProfile.Clauses` 目录（数据驱动，不硬编码进 Core）；引擎把「目录中有、无规则覆盖」写进 `UnmodeledRules` |
| 5 | 五情形覆盖表漏 7 类，其中 `not_applicable` 最严重：`with_burst` 不匹配时规则返回 success + 观测，按布尔模型算「已覆盖」，实际没验证 | HIGH | 覆盖改三态 `Covered/Partial/NotCovered` + 原因码；补 7 情形 + 继承/remove 语义 |
| 6 | §9 十项体检中五项判据未定义 | HIGH | §9.1 收敛为五项可实现项，其余标「判据未定义，v1 不做」 |
| 7 | **§9 的报告模型实现不了 S5**：问题列表无法区分「通过」与「没查」，与 §9.1 自己要求的「无 profile 时明示未检查」矛盾 | HIGH | §9.2 改三态 `NmDataQualityCheck`（Passed/Failed/NotChecked）+ 固定五项；`SuppressedRuleIds` 用 null/空区分 |
| 8 | §9 未说明体检在哪层算 | HIGH | §9.3 独立 `NmDataQualityAnalyzer`；明确**不做成规则原语**（无 clause 可挂、现有引擎无规则调度模型、非企标条款） |
| 9 | `NmUnmodeledRule` 没有 `RuleId`/`ReasonCode`，Clause 改成条款号后「哪条规则未实现」在结构化输出里消失 | HIGH | §9.4 新增 `RuleId` + `ReasonCode` 七值枚举 |
| 10 | §7.2 `max_gap_observe` 无 §4 条款映射，`value` 语义未定义 | MEDIUM | 明确 v1 不实现 |
| 11 | §11 S4 验收含「主动唤醒两阶段判定」，但 `bit_set` 不是闭集原语（闭集里是 `bit_rule`，只支持 `is_zero`） | HIGH | S4 验收删去此项；诊断改为「缺 `is_one` + wakeup 作用域」 |
| 12 | NM_10 的「不区分目标状态」被当成能力限制 | MEDIUM | 实际可判定（NMSts 已被三处读取）：加 `expect_nmsts`，拆成 `NM_RESUME_TO_RMS` / `NM_RESUME_TO_NOS` |
| 13 | 条款目录不进缓存 hash | HIGH | `StandardClauses` 加进 `NmProfile` / `NmStandardProfileBinding` / `NmFlow` hash |
| 14 | `ApplyRemove` 对不存在的 key 静默无效，规则重命名后旧 `remove:` 变 no-op | MEDIUM | **已修**：四个分支 + 空项一律抛 `NmProfileValidationException` |
| 15 | `NmReportWriter` 从未输出 `RequirementId`，§4.0 声称的「Markdown 追溯出口」不成立 | MEDIUM | writer 需新增 clause 输出（与 DQ 字段同批） |
| 16 | §11 声称 S6 归并行会话 `claude-proj2-1a`，无法核实 | MEDIUM | 不改所有权划分，但要求动这些文件前必须向用户确认归属 |

### 19.1 Rev 5 明确「不是缺陷」的两件事

1. **`NM_06` 用 `repeat_message_duration` 判时长不是挂错**——§7.2 第 541 行明确要求锚点与时长分离；PDF 的 NM_06 也是 T_REPEAT_MESSAGE 超时。Rev 4 的 §4 表才是错的。但 NMSts **不是独立规则**，是 `repeat_message_duration` 内部读的锚点（`FindNmstsRole`）。
2. **U 级条款不建 profile 规则**——只在 `clauses:` 目录里标 `level: U`。不需要为「让它出现」而塞 10 条不产 finding 的假规则。**但前提是 `NmAnalyzer` 的 `StandardRules.Count > 0` 门要放宽**（见 §5.5 的阻碍说明），否则只有目录的 profile 不进引擎。

### 19.2 Rev 5 撤回的一个错误判断

Rev 5 初稿曾写「`NM_10` 与 NM_02/15 共用一条规则，接受精度损失」——**这个判断是错的**。NMSts 可观测且已被多处读取，区分 RMS/NOS 只需加一个 `expect_nmsts` 参数。撤回。

**但撤回的理由只说对了一半（2026-11-24 补记）**：`expect_nmsts` 确实实现了（`NmRuleExpect.ExpectNmsts`，加载期校验 0/1，求值期按恢复帧首帧比对，位不可读时进 Unmodeled），但它只解决了 **NM_10 的目标状态**，**没有**让 `NM_RESUME_TO_RMS` 变得可启用。原因是 NM_02/15 与 NM_03/16 的 NMSts 同为 0，只能靠快发形态区分，而唤醒源判别属于 §7.3。详见 §7.2 的「判别力边界」段。

### 19.3 未解决、留给后续切片的问题

| 问题 | 影响 | 能否留给后续 |
|---|---|---|
| `NmRuleContext` 缺 `RawFrames`/`Dbc`/`Selection` | 规则引擎无法做任何需要原始帧的判定 | **可留**——不影响 S5（体检走独立分析器）；但其它需要原始帧的原语仍是硬缺口 |
| §7.3 主动唤醒两阶段判定整节未实现 | `NM_ACTIVE_WAKEUP_SUSPECT` 只有注册无产出者 | **可留，但有条件**：NM_03/NM_16 的 AWB 半边必须保持 `Partial`/`NotCovered`，**不能因为 `burst_shape` 实现了就报 Covered**（见 §9.5 的条款拆分要求） |
| §11.1 的「追溯矩阵完整」CI 校验不存在 | 条款目录靠手工维护，作者忘写就静默失效 | **完整版可留，最低版不能留**：至少要有「目录中每条 O/P 条款必须有规则覆盖或明确 Partial/NotCovered」的 machine-readable 自检，否则漏写仍然静默 |
| `phase` 不驱动求值作用域 | 必填字段却不生效，用户会误以为作用域有效；且会让 S4/S5 的抑制与覆盖统计出错 | **不可留**——必须在 §5.3 的三选一中裁决（删字段/改名/实现作用域） |

### 19.4 契约对齐：Rev 4 类型草案与实现不符的六处

Rev 5 曾把新机制（条款目录、三态覆盖、DQ 三态）写进 §5.3 / §5.4 / §7.1 的类型草案——而这些草案**本身就和代码不一致**。按草案实施会改错。已逐一对齐：

| # | Rev 4 草案 | 实现 | 处理 |
|---|---|---|---|
| 1 | `NmRuleDef` 位置参数含 `Observability`（第 4 位）、`Clauses`（第 8 位） | 6 个位置参数 + `SourceRef`/`Observability` 两个 init 属性 | 已改为实际定义；`Clauses` 用 `required` init 属性，不重排 |
| 2 | `NmStandardProfile` 是位置 record，含 `Patch` / `NmMessageDef` / `NmNodeDef` | property-init 类，含 `Overrides` / `NmStandardMessage` / `NmStandardNode` | 已改为实际定义 |
| 3 | `NmContentField` / `NmExceptionDef` | **Core 中不存在** | 已删除，标注为「未实现的设计」 |
| 4 | `NmStandardPatch` / `NmStandardPatchSet` | **不存在**，实际是 `NmStandardOverrides` | 已删除 |
| 5 | §6.2 用 `patch: { set:, remove: }` 块 | 实际是 `overrides:` + `overrides.remove` | 已改为实际形态 |
| 6 | §7.1 `NmRuleContext` 含 `RawFrames`/`Dbc`/`Selection`，`Timing` 是 `NmEffectiveTiming` | 三个字段都不存在，`Timing` 是 `NmGlobalTiming` | 已改为实际定义，缺口单独标注 |
| 7 | §7.2 正文写「Rev 6 修正」 | 本文件是 Rev 5 | 已加注「指当时内部修订轮次，非本文件版本」 |

### 19.5 Rev 5 之后仍不可实施的部分（明确记录，避免误当已完成）

| 设计 | 阻断原因 | 解锁条件 |
|---|---|---|
| ~~§5.5 三态条款覆盖~~ | ~~引擎无逐规则状态~~ | **2026-11-24 已解锁**：`NmRuleResult.Evaluations` 与 `NmClauseCoverage` 落地，见 §19.6 |
| §4.0 四条契约测试 | 正则按 `Clause` 写、成员资格按 `Id` 查，两者不自洽；且复合 Id 下「不得是规则 ID」恒真 | 先定 `rule.clauses` 存 `Id` 还是 `Clause` |
| §9.2 DQ 三态模型 | 四个强制点未定（Code 闭集、五项保证、null/空语义、抑制映射） | 补齐强制点 + 定义 DQ 帧集 |
| 条款目录全链路 | `Flattened`/`ApplyRemove`/`Bind`/`NmProfile`/hash 五处均未打通 | 五处同批改 |

**这四项不是「v1 不做」，是「设计未闭合」。** 写进本节是为了让后续实施者知道：看到 §5.5 / §4.0 / §9.2 时不能直接照着写代码。

### 19.6 逐规则状态与条款覆盖的实施记录（2026-11-24）

**解锁了 §19.5 的第一项**，但只到「有规则覆盖的条款」为止，**不是完整的三态覆盖**。范围收窄如实记录如下。

#### 一、逐规则求值状态（§7.1.1）

`NmRuleResult` 新增 `Evaluations`，`Evaluate` 为每条规则产出一项。**没有改 51 个 `return` 点**——状态由引擎按「本规则产出增量」推导，`TryEvaluate` 的 `implemented` 标志已足够。

枚举命名为 `NmRuleVerdict` 而非 spec 写的 `NmRuleStatus`：后者已被标定面板的呈现三态（`Triggered`/`NotTriggered`/`Unmodeled`）占用。两者语义不同——一个是**求值**结论，一个是由报告反推的**呈现**状态。

**`*_not_applicable` 观测是判定的陷阱**（实现时踩到）：`repeat_message_duration` 在形态不匹配时产一条 `repeat_message_duration_not_applicable` 观测然后 `continue`，既不产 finding 也不给 `unmodeledReason`。首版按「有观测 = 判过了」把它判成 `EvaluatedNoFinding`，而真相是**这一段压根没判**——后果是条款覆盖把它算成 `Covered`。

正确判定要区分三种情形：

| 本规则的观测构成 | 状态 |
|---|---|
| 只有 `*_not_applicable` | `NotApplicable` |
| `*_not_applicable` 与事实观测并存 | `Partial`（有的段判了、有的没判） |
| 全是事实观测 | `EvaluatedNoFinding` / `EvaluatedWithFinding` |

第二条最容易漏：一条 episode 里同时有快发段与非快发段时，原语会对两段各产一条观测。首版测试构造的场景恰好是这种混合，暴露后补了 `ClassifyObservations`。

#### 二、条款覆盖（§5.5）

`NmClauseCoverage.Build(rules, evaluations)` 聚合出 `Covered` / `Partial` / `NotCovered`。

**聚合不取最差**：吉利的 `NM_RMS_DURATION_BURST` / `NM_RMS_DURATION_NOBURST` 是一对 `with_burst` 变体，每条 trace 必然有一条 `NotApplicable`。取最差会让真实违规稳定降级成 `Partial`——§14.6 要求检出的两段 RMS 违规全部失效。正确做法是先剔除 `NotApplicable`，再看剩余结果里有没有 `Pass`/`Violation`。

**范围收窄（必须明确）**：本实现只统计**有规则覆盖的条款**。spec §5.5 要求的「目录里有但无规则覆盖 → `NotCovered`」**做不到**——profile 没有独立的条款目录（`NmClauseDef` / `StandardClauses`），那需要 §19.5 第四项「条款目录全链路」的 `Flattened`/`ApplyRemove`/`Bind`/`NmProfile`/hash 五处同批改。

因此 `NmClauseStatus.NotCovered` 目前**永远不会产出**。这个枚举值保留是为了将来打通目录后直接可用，不是「已实现」。代码注释里已如实写明。

**`clauses` 值域仍未裁决**：profile 里 `§5.4.2`（章节号）与 `NM_03`（条款编号）混用。本轮**没有统一**——统一会改动全部现有 profile 与 478 条既有断言，而当前统计按「字符串相等」聚合仍可用（只是 `§5.5` 会在多条规则里重复出现，无法定位到具体条款行）。这是有意的范围控制，不是遗漏。

#### 三、输出

`NmReport` 新增 `RuleEvaluations` 与 `ClauseCoverage`，两处消费方（`GetNmOverviewTool` 的 JSON、`NmReportWriter` 的 Markdown）都输出。AI 侧尤其重要：只看 `findings` 为空，AI 会说「没有违规」，而真相可能是「这些规则根本没跑」。
