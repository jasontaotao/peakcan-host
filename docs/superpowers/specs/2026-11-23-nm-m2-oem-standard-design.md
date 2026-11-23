# NM M2：企标包与语义层设计（Rev 4）

- 日期：2026-11-23（Rev 4，第三轮对抗式评审后修正）
- 状态：设计（待评审）
- 上游：`2026-11-19-nm-ai-analysis-design.md`（M1，已交付）、§6.5 企标包、§14.2 M2 分期
- 首批企标输入：`02 _ 吉利商用车CAN网络管理需求规范_V2.0_20250227.pdf`（「吉利规范」）
- 修正历史见 §16（Rev 2→3）、§17（Rev 3→4）、§18（Rev 1→2）

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
| NM_02 | 睡眠 + 远程唤醒 → RMS 正常发送 | P | `resume_after_silence` | 可观测「静默后以正常周期恢复」，唤醒源未知 |
| NM_03 | 睡眠 + 本地唤醒 → 快速发送 + AWB=1 | P | `burst_shape` + `bit_set` | 快发形态可验；AWB 值可验；唤醒源不可验 |
| NM_04 | 计数器到 0 → 正常发送子状态 | O | `burst_shape` | 快发段结束、间隔回到正常周期 |
| NM_05 | RMS 下 T_NM_TIMEOUT 超时 → 状态不变、定时器重置 | U | — | **内部定时器不可观测** |
| NM_06 | RMS T_REPEAT_MESSAGE 超时 → NOS | P | `nmsts_transition` | 靠 NMSts 位观测，需 NMSts 已配置 |
| NM_07 | NOS + 重复报文请求 → RMS 快速/正常 | U（跳转） | — | 规范明确「跳转逻辑均不开发」；**但 RMR 位取值本身是 O**（§5.4.3） |
| NM_08 | NOS 下 T_NM_TIMEOUT 超时 | U | — | 内部定时器不可观测 |
| NM_09 | NOS 满足睡眠条件 → RSS 停发 | P | `stop_sending` | 停发可观测；「满足睡眠条件」不可观测 |
| NM_10 | RSS + 本地唤醒 → NOS | P | `resume_after_silence` | 同上 |
| NM_11 | RSS + 重复报文请求 → RMS | U（跳转） | — | 同 NM_07 |
| NM_12 | RMS 超时 + 睡眠条件 → RSS | P | `stop_sending` | 同 NM_09 |
| NM_13 | RSS + T_NM_TIMEOUT 超时 → 预睡眠 | U | — | 内部定时器 |
| NM_14 | 成功收发一帧 → T_NM_TIMEOUT 重置 | U | — | 内部定时器 |
| NM_15 | 预睡眠 + 远程唤醒 → RMS 正常发送 | P | `resume_after_silence` | 同 NM_02 |
| NM_16 | 预睡眠 + 本地唤醒 → RMS 快速发送 | P | `burst_shape` | 同 NM_03 |
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

```csharp
/// <summary>一条企标规则。</summary>
public sealed record NmRuleDef(
    string Id,
    string Requirement,                  // 本地完整条款文本
    string Phase,                        // wakeup | normal | sleep
    NmObservability Observability,       // O | P | U
    NmRuleWhen When,
    NmRuleExpect Expect,
    string FindingCode,
    string? SourceRef = null);

/// <summary>条件与期望共用同一套 field selector。</summary>
public sealed record NmFieldSelector(
    byte Byte,
    int Bit = -1,        // -1 = 整个字节
    int Length = 8);     // 位宽；Bit>=0 时有效
```

参数用结构化 record 而非字符串字典：YAML 加载时由 YamlDotNet 直接转数值，类型错误在加载期暴露，不推迟到自定义校验器。

### 5.4 标准 profile 文件模型

```csharp
public sealed record NmStandardProfile(
    string StandardId,
    string? Vendor = null,
    string Version = "1.0",
    string? Extends = null,
    NmStandardSource? Source = null,
    // 三态 patch（见 §6.2）：承载 Timings/Layout/Features/BitRoles 的覆盖与删除
    NmStandardPatch? Patch = null,
    // 完整值（非 patch 模式时使用；与 Patch 二选一）
    NmStandardTimings? Timings = null,
    NmStandardLayout? Layout = null,
    NmStandardFeatures? Features = null,
    IReadOnlyList<NmMessageDef>? Messages = null,
    IReadOnlyList<NmNodeDef>? Nodes = null,
    IReadOnlyList<NmRuleDef>? Rules = null,
    IReadOnlyList<NmContentField>? Content = null,
    IReadOnlyList<NmExceptionDef>? Exceptions = null);

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

/// <summary>三态 patch：set 覆盖、remove 删除，未出现则继承。</summary>
public sealed record NmStandardPatch(
    NmStandardPatchSet? Set = null,
    IReadOnlyList<string>? Remove = null);

public sealed record NmStandardPatchSet(
    NmStandardTimings? Timings = null,
    NmStandardLayout? Layout = null,
    NmStandardFeatures? Features = null);
```

**展平后的输出**：`NmStandardProfile`（含继承链）→ `NmProfile`（现有类型）+ `NmGlobalTiming`（§5.2）。`NmGlobalTiming` 不是独立存储，是展平产物的派生物。

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

必须用**显式 patch DTO**：

```yaml
# 方式 A：显式 set/remove 块
patch:
  set:
    features: { pn_enabled: false }
  remove:
    - bit_roles[6]        # 删除 base 的 PNI 位
```

| 写法 | 语义 |
|---|---|
| `set` 块内的值 | 替换父值 |
| `remove` 列表 | 显式删除父值 |
| 未出现在两者中的字段 | 继承父值 |

若坚持用自然 YAML 结构（非 set/remove），则必须在反序列化前保留 **YAML 节点级 presence 信息**（`YamlMappingNode.Children` 的键是否存在），不能用最终 record 判断。S1 必须先做一个小实验验证 YamlDotNet 的行为，再定方案。

**继承差异不记冲突**。冲突只发生在**同一事实的多个独立来源**（DBC / sidecar / project profile）给出不同值且都非空时，记 `NmConfigConflict`。

`MustBeZero` 与具名角色**允许共存**（OEM 用 `MustBeZeroWhileDisabled` 覆盖 base 的 PNI 位是合法的），校验器不得拒绝。

### 6.3 v1 简化

若多 OEM 需求尚未出现，v1 可先做**单层 OEM 完整 profile + 项目绑定**，继承框架按上述语义实现但只用两层。避免先上未验证的通用框架。

## 7. 规则引擎

### 7.1 接口

```csharp
public sealed record NmRuleContext(
    IReadOnlyList<NmFrame> NmFrames,
    IReadOnlyList<ReplayFrame> RawFrames,      // 数据体检需要：未知 ID、重复帧、通道
    IReadOnlyList<NmEpisode> Episodes,
    NmProfile Profile,
    NmEffectiveTiming Timing,
    NmWindow Window,
    DbcDocument? Dbc,                           // §5.4.1 布局核对、信号位角色
    NmSelectionDiagnostics? Selection);

public sealed record NmRuleResult(
    IReadOnlyList<NmFinding> Findings,          // 违规
    IReadOnlyList<NmObservation> Observations,  // 事实，不声称违规
    IReadOnlyList<NmUnmodeledRule> Unmodeled,
    IReadOnlyList<NmTruncation> Truncations);
```

**`Finding` 与 `Observation` 必须分开**。这是 Rev 1 的结构性缺陷：把「静默 2.9s」直接当 finding 会误报。

### 7.1.1 报告契约必须先冻结（S1 前置）

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
    string Reason,                        // "内部定时器不可观测"
    string? RequirementId = null,
    string? SourceRef = null);
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

改为四态：

```csharp
public enum NmSilenceVerdict
{
    NotSilent = 0,              // 窗口末仍在发
    SilentWithOtherNmActive,    // 单节点静默且其它 NM 仍在发 → 可疑
    AllNmSilent,                // 全网 NM 静默 → 原因未知，非单节点违规
    IndeterminateAtBoundary,    // 窗口截断/共享 ID 无法拆分
}
```

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
| `normal_resume_shape` | `min_silence_ms`, `cycle_ms`, `pct`, `min_frames` | P | 静默后恢复为**正常周期**发送（NM_02/NM_15） |
| `stop_sending` | `timeout_ms` | P | 停发观察（不判违规，见 §4.1） |
| `max_gap_observe` | `value` | P | 间隔观察，仅产 observation |

**`normal_resume_shape` 是 Rev 3 新增**：Rev 2 用 `resume_after_silence`（只有 `min_silence_ms`）覆盖 NM_02/NM_15，但规范要求远程唤醒后进入 **RMS 正常发送子状态**并以 `T_NM_MessageCycle` 发送（`spec.txt:477-483`）。只判「静默后有帧」会把「恢复后不快发、周期错误、只发一帧」都当成合规。

**NM_06 的时长判定**：NMSts 跃迁（O 级 observation）作为边界锚点，时长由 `repeat_message_duration` 独立判定。缺少 NMSts 配置时只能 inferred，不能反过来说条款已验证。

**删除**：`bit_sequence`、`field_sequence`、`counter_increment`、`timeout_reset`、`node_silent`、`all_nodes_silent`、`frame_present/absent`、`min_gap_ms`、`frame_count`、`observe_silence_ms`。

理由：语义无法在不引入通用表达式语言的前提下精确定义，或依赖不可观测输入。NMSts 用 `nmsts_transition` 专用原语，不做通用序列匹配。

**未知 `when`/`expect` 原语（Rev 6 修正）**：分两个阶段处理，不再统一「不抛异常」。

| 阶段 | 行为 | 理由 |
|---|---|---|
| **加载期**（`NmStandardYaml.Load`） | 原语不在 `NmRulePrimitives` 闭集内 → **抛 `NmProfileValidationException`**，消息列出全部合法值 | 拼错的原语名（`cycle_tolerence`）若静默进 Unmodeled，用户会以为条款已被校验——这是最危险的静默失败 |
| **求值期**（`NmRuleEngine.Evaluate`） | 闭集内但 v1 尚无实现的原语 → 进 `Unmodeled`，不产 finding，不抛异常 | 引擎必须能处理「profile 合法但当前版本未实现」的情形，不能崩溃 |

Rev 5 及更早写的「未知原语一律进 Unmodeled 不抛异常」只覆盖了求值期，遗漏了加载期拼写错误的危害。闭集校验把这类错误前移到加载期，是本设计的刻意收紧。

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
        string Detector,         // "immediate" | "cycle_ratio_fallback"
        bool IsIndeterminate);   // 窗口截断/帧数不足
}
```

**`ImmediateCycleMs` 缺失时的回退**（这是原设计的循环依赖缺口）：

| 情形 | 检测方式 | Detector | Evidence |
|---|---|---|---|
| `ImmediateCycleMs` 已配置 | `≤ 2.0 × ImmediateCycleMs` | `immediate` | measured |
| `ImmediateCycleMs` 未配置 | `≤ 0.5 × CycleMs`（M1 现有行为） | `cycle_ratio_fallback` | **inferred** |
| 两者都无（CycleMs 也无） | 不检测快发 | — | 不选双窗口，用 M1 默认 |

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

| 检查 | 不通过处理 |
|---|---|
| 报文 ID 是否在 DBC 中 | `data_quality: unknown_id`，该报文不做语义判定 |
| 实测 DLC 与 DBC 是否一致 | 已有 `NM_DLC_MISMATCH`，升级为体检项 |
| NID 是否与 ID 映射一致 | `data_quality: nid_id_mismatch` |
| 时间戳是否单调 | 非单调 → `data_quality: non_monotonic`，记录被剔除区间 |
| 帧率是否合理 | 异常 → `data_quality: implausible_rate` |
| **重复帧**（同 ID 同 payload 同/近时间戳） | `data_quality: duplicate_frames`，归因不确定 |
| **Tx/Rx 回环** | `data_quality: loopback_suspected`，不去重后静默继续 |
| **时间跳变幅度** | 大正向跳变/基准重置 → `data_quality: time_jump` |
| **窗口首尾截断** | `data_quality: truncated_window`，相关规则抑制 |
| **多通道混录** | 保持显式通道选择，不自动混算 |

体检结果携带**具体受影响区间和规则抑制范围**。

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
| **S4** | P 级原语（`first_frame_latency`/`repeat_message_duration`/`normal_resume_shape`/`stop_sending`/`max_gap_observe`）+ episode/静默推断 | 合法释放负例集零误报；主动唤醒两阶段判定 |
| **S5** | 数据体检 + 真实 trace 端到端 | 体检项单测；contract test（必须出现 + 必须不出现） |
| **S6** | Activity 段模型（§7.5：NMSts 切段 + 快发子状态 + `Evidence` + 双窗口裁决）+ 笔刷/文案/图例 | §7.5.5 验收；`NmSpecClosureTests` 回归门 |
| **S7** | 只读选择器 + 标定面板 + 缓存 key + watcher | 面板结构测试；切 profile 必重算；canonical hash 稳定性测试 |

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
| **`NM_REPEAT_MESSAGE_DURATION_EXCEEDED`（第一段）** | RMS 实测 2.121s，无快发允许 1.440–1.560s，**超 +561ms** |
| **`NM_REPEAT_MESSAGE_DURATION_EXCEEDED`（第二段）** | RMS 实测 2.325s，有快发允许 1.640–1.760s，**超 +565ms** |
| observation `all_nm_silent` | 9.713674–12.677179，且标 `inferred`（原因未知） |

**必须不出现**：

| finding | 依据 |
|---|---|
| `NM_SENDER_SILENT_BEYOND_TIMEOUT` | 静默段全网 NM 静默，非单节点违规 |
| `NM_CYCLE_DEVIATION` | 稳态 514–537 ms 全在 450–550 ms 内 |
| `NM_WAKEUP_BURST_MISMATCH` | 快发段 10 帧 × 19–21 ms 合规 |
| `NM_RESERVED_BIT_SET` | 除 PMS 帧外，BMS 帧预留位均为 0 |

**两段 RMS 超差是真实违规**，不是基线噪声。第三轮评审发现：这两段都超出吉利的 RMS 上限约 560ms，说明这份 trace 的网络确实存在问题——这正是「真实 trace 可能有故障」的实例。contract test 必须断言这两条 finding 存在，否则 `repeat_message_duration` 实现了也验不出来。

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
| R3-1 | `Finding/Observation/Unmodeled` 只在规则结果层，`NmReport` 装不下 | CRITICAL | §7.1.1 冻结报告契约：`NmObservation` + `NmUnmodeledRule` 进 `NmReport`，全层复用 |
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
| R4-13 | 报告契约缺 `SilenceVerdict` 字段、`NmObservation.IsInference`、消费方改造点 | MEDIUM | §7.1.1 补字段 + 四个消费方清单 + AI 负例 |
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
