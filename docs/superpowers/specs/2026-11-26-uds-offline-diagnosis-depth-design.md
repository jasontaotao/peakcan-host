# UDS 离线分析：诊断深度补充设计

**日期:** 2026-11-26
**前置文档:** [2026-11-25-uds-offline-log-analysis-design.md](2026-11-25-uds-offline-log-analysis-design.md)
**关系:** 本文补充前置文档，不推翻其数据契约与流水线阶段，只在其后追加一个 `diagnose` 阶段并扩展解释模型。

## 1. 背景

首版离线分析上线后，实际使用反馈：

> 我感觉 UI 上展示出来的分析报告不够充分，看不大出来问题分析，比方说会话模式，错误方是谁等等，最后的失败原因也看不大出来。

排查结论：**这不是 UI 布局问题，是分析引擎停在半路。**

`UdsOfflineAnalyzer` 的流水线是：

```text
classify → reassemble → pair → timing → interpret
```

`interpret` 之后就没有了。产出的东西是「每个事务的字段」，不是「这段诊断出了什么问题」。用户能查到 `NRC 0x33` 这个数值，但查不到「哪个 DID、在什么会话下、前面有没有解锁失败」。

只加 XAML 绑定解决不了，因为要展示的数据根本不存在。

### 1.1 已确认的欠账（不是设计取舍）

前置文档 §6.4 要求事务「至少包含**结论、不可知项和下一步检查提示**」。现状：

| 要求 | 现状 | 证据 |
|---|---|---|
| 不可知项 | `UdsOfflineTransaction.Unknowns` 声明了，构造时写死 `Array.Empty<string>()`，全仓零填充点 | `UdsTransactionBuilder.cs:30` |
| 下一步检查提示 | 不存在该字段 | `UdsInterpretation.cs:8-20` |
| 结论 | 只有 `SelectedConclusion` 拼出的 5 行枚举分支 | `UdsLogAnalysisViewModel.cs:162-170` |

连带后果：`UdsOfflineAiPromptBuilder.cs:55` 的 `Unknowns.Count == 0 ? DefaultFacts : transaction.Unknowns` 永远走左边，交给 AI 的永远是「见报告」。

### 1.2 被丢弃的计算结果

`UdsResponseValidator.TryValidate` 产出了 `DidEcho` / `SessionTypeEcho` / `BlockSequenceEcho` / `PayloadTooShort` / `RequestTooShort` 等具体失败原因，调用方用下划线丢弃：

```csharp
// UdsTransactionBuilder.cs:117
if (!UdsResponseValidator.TryValidate(target.Request, pdu, out _))
```

用户只看到「响应校验失败 3」，不知道失败在哪一帧、哪个字段。

## 2. 目标与非目标

### 2.1 目标

1. 报告能回答：**这段日志里换了几次会话，最后确认停在哪个会话。**
2. 报告能回答：**流程终止在哪一步，直接失败是什么。**
3. 报告能回答：**有几个独立问题，各自优先排查哪一侧。**
4. 报告能明确说：**哪些事情仅凭这份日志无法判定。**
5. 结论、不可知项和下一步检查提示在 UI 上**默认可见**，不依赖用户逐行拼读，也不依赖用户去点 AI。

### 2.2 非目标（本次明确不做）

| 不做 | 原因 |
|---|---|
| 断言硬件根因（「key 算法写错了」） | CAN 日志只能证明 ECU 拒绝了 key。算法是否算错需要本地按同一 seed 重算比对，不在日志范围内 |
| 判责二选一（「是客户端的错」/「是 ECU 的错」） | `0x7F` 由 ECU 发出，只证明 ECU 拒绝了请求，不证明拒绝不合理 |
| 单一根因链（「A 导致 B 导致 C」） | 会被反例证伪，见 §2.3 |
| 跨日志关联 | 一份报告只描述一份日志 |
| 多 ECU 并行 / CAN FD ISO-TP / DoIP | 沿用前置文档 §2.2 |
| ~~功能寻址~~ | **2026-11-27 已支持**（单 ECU 场景），见 §8.5 |
| 在线诊断行为改动 | 本文只动离线分析 |

### 2.3 为什么不做单一根因链

考虑这段刷写日志：

```text
t0  0x10 03          → 成功（编程会话）
t1  0x27 01          → 成功（拿到 seed）
t2  0x27 02          → NRC 0x35 InvalidKey
t3  0x34             → 成功（RequestDownload）
t4  0x36 block=01    → 成功
t5  0x36 block=02    → 成功
t6  0x36 block=03    → NRC 0x73 WrongBlockSequenceCounter
t7  0x10 01          → 成功（回默认会话）
```

如果实现「最早失败 → 后续连带」的单一根因链，会输出「安全访问失败导致后续失败」。但 **`0x34` 在解锁失败后成功了**，直接推翻这个因果。而且 `0x73` 是块序号问题，与 key 算错没有证据上的关联。

硬串成一条链，用户照着查会白跑一趟。

正确输出是**多个独立问题并列 + 各自的责任侧 + 明确标注哪些关联没有证据**：

```text
流程终止点：TransferData 第 3 块被拒，之后未出现 0x37，刷写未完成
独立异常 1：安全访问 key 被拒（0x35）→ 优先排查客户端 key 计算/参数
独立异常 2：块序号被拒（0x73）→ 优先排查客户端块序号递增 / 日志完整性
两项异常之间无证据构成同一根因
不可判定：仅凭 CAN 日志无法确定 0x73 是客户端递增错、ECU 期望值不同，还是日志漏帧
```

## 3. 已确认决策

| 决策 | 结论 | 原因 |
|---|---|---|
| D1 | 新增 `diagnose` 阶段，插在 `interpret` 之后 | 现有阶段职责清晰，不应混入结论生成 |
| D2 | 一次定义 `UdsOperationFacts` 共享事实层 | 若 P0 只加显示字段，P1 的状态机要再扩一次，必然返工 |
| D3 | 结论模型照 `NmFinding` 的语义原则，**不复用 record** | `NmFinding` 的 `Node` / `CanIdHex` / `EpisodeIndex` 不适用事务；但 `IsInference` / `SuggestedNextCheck` / 证据引用的设计可直接沿用 |
| D4 | 责任归属分**两栏**（直接动作 / 优先排查侧），不做二选一判责 | `0x7F` 只证明 ECU 拒绝，不证明拒绝不合理 |
| D5 | 独立异常列表 + 保守关联，不做单一根因链 | 见 §2.3 |
| D6 | 刷写流程终止点提到 P0 | 用户问「最后的失败原因」，不显示「走到哪一步断的」等于没回答 |
| D7 | 结论摘要区默认可见 | 用户说「看不出来」，不能指望他自己逐行拼，也不能指望他点 AI |
| D8 | AI handoff 传完整结构化事实，AI 只做解释不做事实提取 | 现有 prompt 只传单个事务，AI 缺做跨事务归因的输入 |

## 4. 数据契约

### 4.1 共享事实层

一次定义，P0 的 UI、P1 的状态机、AI prompt 全部消费同一份。

```csharp
/// <summary>协议层提取的操作对象。不含显示文案，UI 自行格式化。</summary>
public sealed record UdsOperationFacts(
    byte Sid,
    string ServiceName,
    byte? SessionType,          // 0x10
    byte? SecurityLevel,        // 0x27 子功能 & 0x7F 去掉 suppress 位
    UdsSecurityStep? SecurityStep,
    ushort? DidId,              // 0x22 / 0x2E
    ushort? RoutineId,          // 0x31
    byte? RoutineControlType,   // 0x31 子功能
    byte? BlockSequence,        // 0x36
    byte? Nrc,
    string? NrcName);           // 取自 UdsNrcSemantics，未知值 null

public enum UdsSecurityStep
{
    RequestSeed = 0x01,
    SendKey = 0x02,
}
```

**关键：负响应也必须带操作对象。** 现状 `UdsServiceInterpreter.cs:41-43` 遇到负响应立即返回，`DidName` / `RoutineName` 都置空——用户看到 `0x2E → NRC 0x33` 却不知道写的是哪个 DID。

> **实现落点（2026-11-27 核对）**：这条由 `UdsOperationFacts` + `UdsOfflineTransaction.TargetDisplay`
> 满足，**不是**由 `UdsInterpretation.DidName` 满足——解释器对负响应仍然提前返回。
> 两者消费方不同（解释器给表格的「DID 名 / 解码值」，事实层给「操作的是什么」），
> 不要指望改解释器来解决这条。

### 4.2 观测事实

```csharp
/// <summary>可量化、可复核的观测事实，不声称违规。语义同 NmObservation。</summary>
public sealed record UdsObservation(
    string Kind,                    // 当前实际产出三种：
                                    // negative_response / unclosed_request /
                                    // flash_incomplete
    int? TransactionIndex,
    int FirstEvidenceIndex,
    int LastEvidenceIndex,
    double Timestamp,
    UdsOperationFacts? Facts,
    string Detail,
    bool IsInference = false);
```

`IsInference` 区分实测与推断：`0x35` 收到负响应是实测；「该安全等级未解锁」是从未观察到 `0x27 02` 正响应推断出来的。

> **实现修正（2026-11-27 核对）**：上面列出的 `validation_failed` / `session_switch` /
> `security_attempt` / `transfer_block` / `p2_exceeded` / `transport_warning` 六种 Kind
> **本版都没有产出点**——它们是 P1 状态机规划的类型。当前 `UdsFindingGenerator` 只发
> `negative_response` / `unclosed_request` / `flash_incomplete`。读报告时不要假设
> 存在会话切换类的观测。

### 4.3 责任归属

拆成两个正交枚举，不做二选一：

```csharp
/// <summary>日志直接观测到的动作方——这是事实，不是判断。</summary>
public enum UdsObservedAction
{
    EcuRejected,            // ECU 回了非 0x78 的负响应
    EcuRespondedLate,       // 只收到 0x78 且**确实超过** P2* 仍无最终响应
    EcuStillProcessing,     // 只收到 0x78 但日志在 P2* 内结束（还在处理，没超时）
    ClientOverlapped,       // 同 SID 请求重叠 / 重复最终响应（**当前无产出点**）
    ClientSkippedStep,      // 测试仪跳过了本该发的步骤，自身仍在继续
    ClientStopped,          // 测试仪未等待该请求的响应：已发下一条，或干脆停发
    RecordingIncomplete,    // ISO-TP 不完整 / 日志截断
    RecordingInvalid,       // 回显校验失败（可能是并行诊断源）
}

/// <summary>优先排查方向——这是推断，不是断言。</summary>
public enum UdsSuspect
{
    ClientParameters,       // 请求参数、key 计算、块序号递增
    ClientSequencing,       // 会话/安全前置顺序
    EcuTiming,              // ECU 响应慢（**当前无产出点**，见下）
    EcuInternalState,       // 会话/安全状态不符
    LogIntegrity,           // 日志本身漏帧/截断/混入
    Indeterminate,          // 无法判定
}
```

> **实现修正（2026-11-27 核对）**：
> - `ClientSkippedStep` / `ClientStopped` 是 2026-11-27 归因改造新增的。原设计把
>   「测试仪没发下一步」硬塞进 `ClientOverlapped`，而 UI 把该值渲染成「请求重叠」——
>   日志里根本没有重叠，用户会去查一个不存在的现象。**两者不能共用一个值。**
> - **`EcuRespondedLate` 与 `EcuStillProcessing` 必须分开**（2026-11-27 二次核对新增）。
>   两者都只收到 `0x78`，但只有前者真的超过了 P2\*。改前无条件返回 `EcuRespondedLate`，
>   于是「日志在 P2\* 内结束」的 finding 一边说「延长记录时间」、一边在「直接动作」行
>   说「ECU 超过响应窗口」——两行直接矛盾，且后者是没有证据的事实。
> - `EcuRespondedLate` 的窗口是 **P2\***（`0x78` 之后的续期），不是 P2。
>   P2 是「请求→首个响应」，由「测试仪有没有等」那条分支使用。
> - **`ClientStopped` 同时覆盖两种相反形态**：「没等就发了下一条」和「干脆停发」。
>   因此文案不能写「未继续发送」（与前者矛盾），必须写「未等待该请求的响应」。
> - `UdsSuspect.EcuTiming` 与 `UdsObservedAction.ClientOverlapped` **当前都是零产出点**：
>   没有任何 finding 规则会返回它们。重叠目前由事务标记（`IsAmbiguous` /
>   `HasDuplicateFinalResponse`）陈述，不产生 finding。保留枚举值是为后续规则预留，
>   不要以为现有报告会给出这两种值。

NRC 到 `UdsSuspect` 的映射是**客户端策略**，不是协议判定，必须逐条写清依据：

| NRC | 名称 | 优先排查侧 | 依据 |
|---|---|---|---|
| `0x10` | GeneralReject | Indeterminate | 协议未定义具体条件 |
| `0x11` | ServiceNotSupported | ClientParameters | ECU 不支持该 SID，通常是客户端发错服务 |
| `0x12` | SubFunctionNotSupported | ClientParameters | 子功能不支持 |
| `0x13` | IncorrectMessageLength | ClientParameters | 请求长度错，客户端可自检 |
| `0x14` | ResponseTooLong | ClientParameters | 响应超出请求声明的长度上限 |
| `0x22` | ConditionsNotCorrect | EcuInternalState | 条件不满足，需结合会话/安全/车辆状态 |
| `0x24` | RequestSequenceError | ClientSequencing | 请求顺序错，通常是前置步骤未完成 |
| `0x25` | NoResponseFromSubnet | EcuInternalState | ECU 的下级组件无响应 |
| `0x26` | FailurePreventsExecution | EcuInternalState | ECU 内部故障阻止执行 |
| `0x31` | RequestOutOfRange | ClientParameters | 参数越界（DID/例程 ID 不存在或不可写） |
| `0x33` | SecurityAccessDenied | ClientSequencing | 安全等级未解锁，检查前置 0x27 |
| `0x35` | InvalidKey | ClientParameters | key 未被接受，优先检查 key 计算/seed 使用/等级参数 |
| `0x36` | ExceededNumberOfAttempts | ClientParameters | 尝试次数超限，通常是前面反复用错 key |
| `0x37` | RequiredTimeDelayNotExpired | ClientSequencing | 未等够延时，客户端时序问题 |
| `0x70` | UploadDownloadNotAccepted | ClientParameters | 下载/上传请求的参数（格式、大小）未被接受 |
| `0x71` | TransferDataSuspended | **EcuInternalState** | ECU 自己暂停了传输，不是客户端参数问题 |
| `0x72` | GeneralProgrammingFailure | **EcuInternalState** | ECU 写 Flash 失败，需查 ECU 侧 |
| `0x73` | WrongBlockSequenceCounter | ClientParameters | 块序号不被接受（客户端递增或日志漏帧） |
| `0x78` | RequestCorrectlyReceivedResponsePending | Indeterminate | **不是失败**：ECU 表示仍在处理，P2* 重新计时 |
| `0x7E` / `0x7F` | 会话不支持 | ClientSequencing | 当前会话不允许该服务 |
| 其它 | — | Indeterminate | 未知或 OEM 范围，不猜 |

**这张表不得反向使用**：不能因为 NRC 是 `0x35` 就在 UI 上写「根因：key 算错」。UI 文案必须是「优先排查：客户端 key 计算/参数」。

### 4.4 结论

```csharp
public enum UdsFindingSeverity { Info, Warn, Error }

public sealed record UdsFinding(
    string Code,
    UdsFindingSeverity Severity,
    string Message,
    UdsObservedAction? ObservedAction,
    UdsSuspect Suspect,
    IReadOnlyList<int> ObservationIndexes,
    string? SuggestedNextCheck,
    bool IsInference,
    IReadOnlyList<string> Unknowns);

public enum UdsFindingRelation
{
    /// <summary>证据表明是同一前置问题（例如同一安全等级未解锁 → 后续 0x33）。</summary>
    LikelySharedCause,
    /// <summary>时间相邻，但无证据构成因果。</summary>
    TemporallyAdjacent,
    /// <summary>已确认无关联（例如中间夹了成功的事务）。</summary>
    Independent,
}

public sealed record UdsFindingLink(
    string FromCode,
    string ToCode,
    UdsFindingRelation Relation,
    string Basis);      // 建立该关系的日志依据，必须可复核
```

### 4.5 会话时间线

```csharp
public enum UdsSessionObservation
{
    /// <summary>收到与请求匹配的 0x50 正响应。</summary>
    Confirmed,
    /// <summary>发了 0x10，但没有通过校验的响应。</summary>
    RequestedNotConfirmed,
    /// <summary>之后出现 0x11 ECUReset，会话可能已改变。</summary>
    ResetMayHaveChanged,
    /// <summary>日志开始时 ECU 已处于某会话，无法从日志确定是哪个。</summary>
    UnknownAtLogStart,
}

public sealed record UdsSessionInterval(
    byte? SessionType,
    UdsSessionObservation Observation,
    double StartTimestamp,
    int StartEvidenceIndex,
    double? EndTimestamp,
    int? EndEvidenceIndex);
```

**`UnknownAtLogStart` 是必须的。** 日志通常从会话中途开始，不能默认断言「ECU 处于默认会话」。

### 4.6 刷写流程

```csharp
public enum UdsFlashStageKind
{
    ProgrammingSession,     // 0x10 03
    SecurityAccess,         // 0x27
    EraseOrPreCheck,        // 0x31
    RequestDownload,        // 0x34
    TransferData,           // 0x36
    RequestTransferExit,    // 0x37
    Verify,                 // 0x31（校验类例程）
    EcuReset,               // 0x11
}

public sealed record UdsFlashStage(
    UdsFlashStageKind Kind,
    bool Observed,
    bool Succeeded,
    int? TransactionIndex,
    double? StartTimestamp,
    string? FailureDetail);

public sealed record UdsFlashFlow(
    bool LooksLikeFlash,
    IReadOnlyList<UdsFlashStage> Stages,
    UdsFlashStageKind? LastSucceededStage,
    UdsFlashStageKind? BlockingStage,
    byte? LastTransferBlockSequence,
    int TransferBlockCount,
    bool Completed);
```

`LooksLikeFlash` 判据：**整份日志**里出现 `0x34` / `0x36` / `0x37` 任一。否则 `Stages` 为空、`LooksLikeFlash = false`，UI 不显示刷写区块。

> **实现修正（2026-11-27 核对）**：原文的「或出现 `0x10 03`」已删除。`0x10 03` 只说明
> 进了编程会话——很多 OEM 在编程会话里写 VIN / 读 DTC，仅凭它会把普通诊断日志判成
> 刷写日志，进而报出「刷写未完成」。判据因此收紧为「必须出现数据传输类服务」。
> 另外「看整份日志」也是必需的：只看最后一次尝试会让「第一次完整传完、第二次只发了
> 个 `0x31` 就停」被判成不是刷写。

`Completed` 判据：`RequestTransferExit` 成功 **且** 之后有成功的校验例程或 `0x11`。缺 `0x37` 一律 `false`——用户说的「刷写到底成功没有」，不能靠猜。

### 4.7 报告扩展

```csharp
public sealed record UdsOfflineReport(...)   // 既有字段不变
{
    public UdsSessionTiming? Timing { get; init; }

    // 新增
    public IReadOnlyList<UdsObservation> Observations { get; init; } = Array.Empty<UdsObservation>();
    public IReadOnlyList<UdsFinding> Findings { get; init; } = Array.Empty<UdsFinding>();
    public IReadOnlyList<UdsFindingLink> FindingLinks { get; init; } = Array.Empty<UdsFindingLink>();
    public IReadOnlyList<UdsSessionInterval> SessionTimeline { get; init; } = Array.Empty<UdsSessionInterval>();
    public UdsFlashFlow? FlashFlow { get; init; }
    public IReadOnlyList<UdsResponseValidationFailure> ValidationFailures { get; init; }
        = Array.Empty<UdsResponseValidationFailure>();
}

/// <summary>回显校验失败明细。此前 TryValidate 的 out 参数被丢弃。</summary>
public sealed record UdsResponseValidationFailure(
    string FailedCheck,                     // DidEcho / SessionTypeEcho / BlockSequenceEcho / ...
    int ResponseEvidenceIndex,
    int TransactionRequestEvidenceIndex);   // 该响应归属的请求，用于反查
```

> **实现修正（2026-11-27 核对）**：实际签名是 3 个参数。原设计的
> `int? TransactionIndex` + `UdsOperationFacts? Facts` 没有落地——校验失败发生在
> **配对阶段**（`UdsTransactionBuilder`），那时 `UdsOperationFacts` 还没生成（它在
> `diagnose` 阶段才产出）。硬塞 `Facts` 只能留 null，反而让读者以为它有值。
> 改为记请求的证据序号，消费方需要事实时按序号回查即可。

`UdsOfflineTransaction.Unknowns` 从恒空改为真实填充。

## 5. 流水线

```text
classify → reassemble → pair → timing → interpret → diagnose
```

`diagnose` 阶段内部顺序（前一步的输出是后一步的输入）：

1. **`UdsFactsExtractor`** — 为每个事务生成 `UdsOperationFacts`（含负响应）
2. **`UdsSessionStateTracker`** — 消费 `0x10` / `0x11` 事实，产出 `SessionTimeline`
3. **`UdsFlashFlowAnalyzer`** — 消费刷写相关事实，产出 `UdsFlashFlow`
4. **`UdsFindingGenerator`** — 消费以上全部，产出 `Observations` / `Findings` / `FindingLinks`

取消语义沿用前置文档 §2.1 第 8 条：`diagnose` 被取消时返回部分报告，`CompletedStages` 不含 `diagnose`，`Findings` 为空数组而不是半成品。

## 6. UI

### 6.1 结论摘要区（默认可见）

放在现有工具栏下方、事务表上方，不折叠：

```text
┌─ 结论 ─────────────────────────────────────────────────────────┐
│ 会话：切换 2 次（编程 → 默认），最后确认处于默认会话            │
│ 流程：刷写未完成 — 停在 TransferData 第 3 块，之后未出现 0x37   │
│                                                                  │
│ ⚠ 独立异常 1：安全访问 key 被拒（0x35）                          │
│    优先排查：客户端 key 计算 / seed 使用 / 安全等级              │
│    证据：#182–#184                                               │
│ ⚠ 独立异常 2：块序号被拒（0x73）                                 │
│    优先排查：客户端块序号递增 / 日志完整性                       │
│    证据：#201–#203                                               │
│    ↳ 与异常 1 无证据构成同一根因                                 │
│                                                                  │
│ 不可判定：仅凭本日志无法确定 0x73 是客户端递增错、ECU 期望值      │
│           不同，还是日志漏帧                                     │
└──────────────────────────────────────────────────────────────────┘
```

~~每条 finding 的证据范围可点击 → 复用现有 `SelectedEvidence` 区域高亮。~~

> **未实现（2026-11-27 核对）**：`UdsFinding.ObservationIndexes` 在 XAML 里**零消费者**，
> finding 的证据范围目前不可点击。证据序号已经算好并存在数据里，接 UI 是纯展示工作，
> 但没有做。不要按「已实现」理解这一段。

### 6.2 事务表新增列

| 列 | 绑定 | 说明 |
|---|---|---|
| 时间(s) | `Request.FirstTimestamp` | 定位问题区间 |
| 对象 | 新属性 `TargetDisplay` | `DID F190` / `例程 FF00` / `会话 03` / `block 03` |
| NRC 含义 | 新属性 `NrcName` | `0x33 SecurityAccessDenied` |
| ~~会话~~ | ~~新属性 `SessionAtTime`~~ | **未实现**，见下 |

> **实现修正（2026-11-27 核对）**：`SessionAtTime` 列**不存在**。会话信息在右栏
> 「选中事务」页签里以 `SelectedSessionAtTime` 呈现（即「选中哪条才显示哪条的会话」），
> 不是表格列。原文按列写会让人以为整表都能一眼看到会话。时间列的实际单位是**秒**
> （`StringFormat=F3`），不是毫秒。

### 6.3 右侧面板补充

- `Report.Timing` 的 P2 / P2* / 来源（现在只在导出里有）
- `Report.Warnings` 明细（现在只在导出里有）
- `ValidationFailures` 明细（新增）
- `IsPartial` 醒目提示（现在只藏在 Status 文案里）

### 6.3.1 布局结构（2026-11-26 追加）

**问题**：改前右栏是一根长 `ScrollViewer`，把「报告统计」和「选中事务详情」混在同一个滚动流里。点一条事务后要滚过 5 个无关区块才看得到详情；结论区展开时又会把整个事务表推出窗口。

**结构**：

```text
┌──────────────────────────────────────────────────────────┐
│ [打开][重新分析][取消][导出][AI解读]        分析完成：12 个事务│
│ 通道[1] 请求[7E0] 响应[7E8] □扩展 [筛选____]  ODX状态｜离线只读│
├──────────────────────────────────────────────────────────┤
│ ▾ 结论  错误2 警告1 提示0 · 刷写未完成，卡在 TransferData     │
│   ┌─ MaxHeight=220，内部滚动 ───────────────────────────┐ │
│   │ 直接动作 / 优先排查 / 下一步 / 不可判定…              │ │
│   └─────────────────────────────────────────────────────┘ │
├─────────────────────────────┬────────────────────────────┤
│ 事务表（虚拟化，列宽固定）    │ [选中事务][报告]            │
│ 时间 服务 对象 结果 NRC 校验 │ 0x2E 写 DID F190 → NRC 0x33 │
│ …                          │ 会话@时间 / 校验强度 / 证据   │
│            ▲ GridSplitter   │                             │
└─────────────────────────────┴────────────────────────────┘
```

三条硬约束：

1. **结论带必须限高内滚**（`MaxHeight="220"` + 内部 `ScrollViewer`）。刷写日志的 finding 能到十几条，自然撑开会把事务表推出可视区——那就等于没改。
2. **右栏必须是 Tab，且不能再套外层 `ScrollViewer`**。外层滚动会让内层 `DataGrid` 失去虚拟化。
3. **页签索引是硬编码的**。`UdsViewModel.LogAnalysisTabIndex` 靠数字判断 `IsOnlineTab`，加页签不会报错、只会静默错位。`UdsWindowLayoutTests.Tab_index_constants_match_the_actual_tab_order` 按 XAML 实际顺序核对常量，移位必然变红。

**通信参数搬位**：从 `DockPanel.Dock="Top"` 的 `Expander` 改成独立页签（索引 4，日志分析顺延到 5）。原来展开时会把整个 `TabControl` 压扁到只剩几行，而它恰是「改完要对照当前生效值」的面板，必须边改边看。

### 6.4 导出与 AI

Markdown / CSV 增加「结论」「会话时间线」「刷写流程」三节。

`UdsOfflineAiPromptBuilder` 改为携带：会话序列、独立异常列表及其关系、流程终止点、不可判定项。AI 的职责是**把这些结构化事实组织成人话并给检查顺序**，不是重新做事实提取。

## 7. 分层实施

### P0 — 事实层 + 让失败可读

| 改动 | 文件 |
|---|---|
| 新增 `UdsOperationFacts` + `UdsFactsExtractor` | `Core/Analysis/Uds/UdsOperationFacts.cs`（新）、`UdsFactsExtractor.cs`（新） |
| NRC 语义表（§4.3） | `Core/Analysis/Uds/UdsNrcSemantics.cs`（新） |
| 接住 `TryValidate` 的 `out failed`，产出 `ValidationFailures` | `UdsTransactionBuilder.cs:117` |
| 填充 `Unknowns`（未闭合原因分类） | `UdsTransactionBuilder.cs` / `UdsOfflineAnalyzer.cs` |
| 新增 `UdsObservation` / `UdsFinding` / `UdsFindingLink` 契约 | `Core/Analysis/Uds/UdsFinding.cs`（新） |
| 刷写流程终止点 | `Core/Analysis/Uds/UdsFlashFlowAnalyzer.cs`（新） |
| 三条确定性 finding 规则（负响应 + 排查侧 / 未闭合归因 / 刷写未完成） | `UdsFindingGenerator.cs`（新） |
| 报告扩展字段 | `UdsOfflineReport.cs` |
| `diagnose` 阶段接线 | `UdsOfflineAnalyzer.cs` |
| 结论摘要区 + 事务表新列 + 右侧补充 | `UdsLogAnalysisView.xaml`、`UdsLogAnalysisViewModel.cs` |
| AI prompt 携带结构化事实 | `UdsOfflineAiPromptBuilder.cs` |
| 导出增加三节 | `UdsOfflineReportWriter.cs` |

**P0 不生成跨事务关联**（`FindingLinks` 为空数组）。这是刻意的：关联需要状态机，放到 P1。

> **实现修正（2026-11-27 核对）**：原文写「两条规则（会话切换、直接失败）」，实际落地的是
> **三条**，且**没有**「会话切换」这条 finding——会话变化由 `SessionTimeline` 陈述，
> 不产生 finding。会话切换作为「异常」来报需要状态机判断（P1），P0 只陈述事实。

### P1 — 状态机 + 保守关联

| 改动 | 文件 |
|---|---|
| 会话/安全状态时间线 | `Core/Analysis/Uds/UdsSessionStateTracker.cs`（新） |
| 关联规则（同一安全等级未解锁 → 后续 0x33；未进所需会话 → 0x7E/0x7F；前序失败 → 0x24） | `UdsFindingGenerator.cs` 扩展 |
| 时间轴 / 分段视图 | `UdsLogAnalysisView.xaml` |
| 新增设计文档翻案前置文档 §2.2 的「自动根因判断」 | 本文即为此文档 |

### P2 — 刷写阶段完整规则 + P2* 判定

- 块序号连续性统计（重复 / 倒退 / 中断）
- `ExceedsP2Star`（前置文档 §9.2 要求了，现只有 `ExceedsP2`）
- 按会话 / 阶段分段统计

## 8. 验收标准

1. 一段含 `0x27` 失败后 `0x34` 成功的日志，报告必须输出**两个独立异常**，且明确标注两者无证据构成同一根因——不得合并成一条链。
2. 一段末尾有 `0x10 01` 成功的日志，报告必须说「最后确认处于默认会话」；末尾是 `0x11` 的，必须说「复位后会话未知」，不得沿用切换前的会话。
3. 一段从传输中途开始的日志，`SessionTimeline` 首项必须是 `UnknownAtLogStart`，不得默认 `Default`。
4. 刷写日志缺 `0x37` 时 `FlashFlow.Completed == false` 且 `BlockingStage` 非空。
5. 每个 `Negative` 事务必须能查到操作对象（DID / 例程 / 会话 / 块序号之一），不得为空。
   <br/>*（2026-11-27 核对：实际落点是 `UdsOperationFacts` + `UdsOfflineTransaction.TargetDisplay`，
   **不是** `Interpretation.DidName`——解释器对负响应仍提前返回。原文按 `Interpretation` 写，
   会让人去改解释器，那是另一处代码、另一种职责。）*
6. `Unknowns` 在未闭合事务上非空，且能区分「日志截断」「校验失败」「ISO-TP 不完整」「地址不匹配」。
7. `ValidationFailures` 的 `FailedCheck` 必须来自 `UdsResponseValidator` 的实际返回值，不得重新推断。
8. 所有 finding 文案不得出现「根因是」「一定是」；必须区分「日志事实」「优先排查」「无法判定」。
9. 结论摘要区在无 finding 时显示「未发现异常」，不得留空。
10. `IsPartial == true` 时 `Findings` 为空数组，且 UI 醒目提示报告不完整。

### 8.1 前置 spec（2026-11-25）缺口补齐（2026-11-26 追加）

对照前置 spec 逐条核查后补齐的三条：

| 前置 spec 条款 | 要求 | 实现 |
|---|---|---|
| §8.5 最后一条 | 表外服务「明确记为未校验，不冒充已校验」 | `UdsEchoCheckLevel` + `UdsResponseValidator.EchoCheckLevelFor(sid)`；事务暴露 `EchoCheck` / `EchoCheckDisplay`；界面、Markdown、CSV、AI 提示词四处均标注 |
| §6.2 | 证据行含方向（TX/RX/Unknown）与最终分类桶（噪声带原因） | `UdsFrameEvidence.DirectionText` / `ClassText`，并入 `Display` |
| §6.5 / §12 | 孤立响应、重复最终响应「单列计数**和证据**」 | 报告新增 `DuplicateFinalResponsePdus`；VM 新增 `OrphanResponseRows` / `DuplicateFinalResponseRows`；右栏「报告」页签列出明细 |
| §6.6 / §15.2 | 「Top-N 被截断时必须显式标注『聚合已截断』及完整 ID 数」 | `AggregationTruncated` 此前**算出来零消费者**。现补：AI 提示词警告 + Markdown 标注 + CSV 两个 `#` 行。**界面侧不需要**——见下 |

> **实现修正（2026-11-27 核对）**：原文声称补了界面 `BusTrafficTruncationHint`，**该属性不存在**
> （全仓 grep 零命中）。原因：界面用的 `BusTraffic.Ids` 是 `UdsBusTrafficProfiler` 的**全量**输出，
> 不截断；被截断的是 `Noise.TopIds`（`MaxTopIds = 20`），而它只在导出里渲染。
> 两个列表不是同一个东西——原文把它们混为一谈，于是凭空要求了一个界面提示。
> 若将来界面改用 `Noise.TopIds`，必须同时补上提示。

**§13.3 脱敏经复核已满足**，不需要改：现有实现是 `redact=true` 时把所有 payload 整段替换为「（已脱敏）」，比 spec 要求的「按敏感服务判断」更严。原样导出带出原始数据是 spec 明确允许的（「原样导出只影响本地文件」）。

### 8.2 代码审查发现的缺陷（2026-11-26，全部已修）

| 严重度 | 缺陷 | 修法 |
|---|---|---|
| HIGH | 传输警告列表绑 `{Binding}`，而 `WarningSummaryRow` 没重写 `ToString()` → 界面显示类名重复 N 行（**改前就有的 bug**；VM 测试断言 `.Display`，所以一直绿） | 改绑 `Display`；补 XAML 层守卫测试「DataTemplate 内不得出现裸 Binding」 |
| HIGH | `EchoCheck` 只看请求 SID，负响应 / 未闭合事务被标成「已校验回显」——而配对器对 `0x7F` 只验长度，从不比对 DID | 新增 `UdsEchoCheckLevel.NotChecked`；`EchoCheck` 按 `FinalKind` 分支，只有 `Positive` 才查表 |
| MEDIUM | 部分报告在导出 / AI 里说「未发现异常」「未出现会话切换」（界面层早已防，导出层漏了） | 三处都加 `IsPartial` 分支 |
| MEDIUM | 截断说明放错位置：读者能看到的列表是 `BusTraffic.Ids`（无上限），而说明指向 `Noise.TopIds` | 导出新增独立的「非诊断 ID Top-N（含证据序号）」小节，截断说明紧跟其后 |
| MEDIUM | 事务表固定列宽合计 1110px > 左栏可用 ~660px，`服务` 列被压到 MinWidth | 三布尔列合并为「标记」短码 + ToolTip；DID/降级列移入右栏；右栏默认 420→340；补列宽预算守卫测试 |
| LOW | 校验强度文案三处各拼一套，已漂移 | 统一走 `UdsOfflineTransaction.EchoCheckDisplay` |
| LOW | `KeyText`/`ValueText` 样式定义后无人使用 | 删除 |
| LOW | `SelectedSessionAtTime` 在 `OnReportChanged` 不发通知 | 补通知 |
| LOW | 重复最终响应明细只在 UI，导出没有 | Markdown 新增「重复最终响应」小节 |
| LOW | CSV 注释行的 `\|` 未转义，破坏字段切分 | `EscapeCsv` 增加 `\|` → `｜` |
| LOW | 测试名把「表内但无可回显字段」与「表外服务」混为一谈 | 拆成两条 Theory，注释写清区别 |

### 8.3 归责链路的两个缺失维度（2026-11-27，用户生产日志发现）

**现象**：日志 `150635_log.asc`（28.6s 刷写）中，测试仪在 19.671s 完全停发，ECU 在 19.687s 回了最后一个请求，日志随后又记了 8.9s 的其它 ECU 背景报文。分析器报：

```
[Warn] #6 请求未闭合：日志在请求后又记录了 22.8s，ECU 未在 P2*（5.0s）内响应
       action=RecordingIncomplete  suspect=EcuInternalState
       建议：检查 ECU 是否处于可响应状态（会话、供电、诊断使能）
```

**两处独立错误**：

1. `#6` 是 `3E 80` —— TesterPresent 带 `suppressPosRspMsgIndicationBit`。ISO 14229-1 规定 ECU **不得**回正响应。分析器不认识这一位，把「按协议不回」报成故障。
2. 「22.8s」是 `logEnd - 请求时刻`，衡量的是**日志还记了多久**，不是**ECU 沉默了多久**。测试仪在 19.671s 就停了，日志末尾那段与 ECU 无关。

**根因**：归责链路缺两个维度——不认识子功能抑制位、所有静默归因都只看 `logEnd`。

**修复**：

| 新增/改动 | 作用 |
|---|---|
| `UdsSubFunction`（新） | 哪些 SID 带子功能、`SuppressesPositiveResponse`、`EffectiveSubFunction` |
| `UdsTesterActivity`（新） | 从事务里提取测试仪最后一条请求的时间 |
| `UdsFinalKind.Suppressed`（新值） | 「请求声明了抑制位，没有响应是预期」，与「未闭合」分开 |
| `UdsTransactionBuilder` | 抑制请求**仍进等待队列**，但标记 `ExpectsNoPositiveResponse`；正响应不得闭合它（详见下方反例） |
| `UdsResponseValidator` | 回显比对统一掩码 `& 0x7F`；`EchoCheckLevelFor` 对 11 个 SID 判 `FullEcho`（0x10/0x11/0x19/0x22/0x27/0x28/0x2E/0x31/0x36/0x3E/0x85） |
| `UdsOfflineTransaction` | 暴露 `ValidationFailedResponses`/`IncompleteResponses` 计数；`EchoCheck` 按 `FinalKind` 分支 |
| `UdsFindingGenerator.UnclosedFinding` | 7 个分派分支（校验失败 / 重组截断 / 仅 0x78 / 没等 / 日志太短 / 测试仪停止 / ECU 沉默） |
| `UdsFindingGenerator.FlashFinding` | 用最后一条刷写请求的时间，不再用阶段**首次**出现时间 |
| `UdsFlashFlowAnalyzer` | 阶段记录 `LastTimestamp`；重试成功清除失败；中段停止补出下一步 |
| `UdsObservedAction.ClientStopped`（新值） | 「测试仪未继续发送」，与「请求重叠」区分 |

**判据（`UnclosedFinding` 的分派顺序，前面的优先）**：

1. 请求声明抑制位 → 不产生 finding（builder 已归为 `Suppressed`）
2. 收到过响应但校验不过 → `RecordingInvalid`，不是「没响应」
3. 响应重组不完整 → `RecordingIncomplete` + `Indeterminate`
4. 只收到 0x78 → `EcuRespondedLate`（ECU 明确在说「还在处理」）
5. 测试仪在下一条请求前给的间隔 < P2 → `ClientStopped`（测试仪没等）
6. 日志在 P2* 内结束 → `LogIntegrity`（窗口不足以判定）
7. 该请求之后测试仪一帧没再发 → `ClientStopped` + `ClientSequencing`（**用户日志的形态**）
8. 以上都不成立 → `EcuInternalState`（这才轮到 ECU）

**P2 与 P2\* 的分工**：P2（首个响应时限，典型 50ms）判「测试仪有没有等」；P2*（0x78 续期，典型 5000ms）判「ECU 有没有足够时间」。改前混用一个窗口。

**测试**（2026-11-27 实测计数，原文档写的数不对）：`UdsSubFunctionTests` 31、`UdsUnclosedAttributionTests` 21（整组重写——旧 fixture 全是「只有一条请求 + 很大的 logEnd」，正是错误形态）、`UdsOfflineDiagnoseStageTests` 9、`UdsFlashFlowAnalyzerTests` 19。

### 8.3.1 反例：抑制请求**不能**移出等待队列（2026-11-27 追加）

> **这一节是为了防止有人照着「更干净」的直觉把 bug 改回来。**

看到「抑制请求不需要响应」，很容易得出「那就别让它进等待队列」的结论。**这是错的。**

反例（真实可复现）：

```text
31 81 FF00    ← 擦除例程 + 抑制正响应
7F 31 22      ← ECU 回 NRC 0x22 ConditionsNotCorrect
```

`suppressPosRspMsgIndicationBit` **只抑制正响应**，负响应照发。若把抑制请求直接归入 `closed`：

1. `7F 31 22` 找不到可配对的请求 → 变成孤立响应，**NRC 丢失**；
2. 该事务的 `FinalKind` 是 `Suppressed` → 刷写流程判 **该步骤成功**；
3. 最终报告：一次被 ECU 明确拒绝的擦除，显示成刷写顺利推进。

这正是本次改造要消灭的「静默吞错」，只是方向相反。

**正确实现**（`UdsTransactionBuilder.cs:118-127`、`:170`、`:218-221`）：

- 抑制请求**进 `open` 队列**，置 `ExpectsNoPositiveResponse = true`；
- 负响应照常闭合它（`Nrc` 与 `Final = Negative`）；
- **正响应跳过它**（`FirstOrDefault(x => x.Sid == expectedReqSid && !x.ExpectsNoPositiveResponse)`）——
  否则它会吃掉下一条同 SID 请求的响应，把真实请求变成「未闭合」；
- 日志结束仍在 `open` 的抑制请求 → `Final = Suppressed`（不是 `None`：`None` 会触发「ECU 未响应」归因，
  而这里没有响应正是协议要求的）。

回归测试：`A_negative_response_still_closes_a_suppress_bit_request`、
`A_suppress_bit_request_does_not_swallow_the_next_requests_response`。

**验证**：真实日志上，122 个事务 = 121 Positive + 1 Suppressed，零假故障；唯一结论是「测试仪在 t=19.671s 之后停止发送」+ `ClientSequencing`。

### 8.4 spec 与实现的逐条核对（2026-11-27，收紧本轮）

对着代码重读本文，发现**文档说了、代码没做**或**文档说反了**的若干处。全部按代码改正，
并把「未实现」标成未实现——文档承诺一个不存在的功能，比没有文档更危险。

**代码缺陷（本轮修复，各自 RED→GREEN→反向验证）**：

| 缺陷 | 用户会看到什么 | 修法 |
|---|---|---|
| `SessionSummary` 的 `RequestedResponseSuppressed` 落进默认分支 | 日志中途的 `10 83` 被说成「**日志开始时** ECU 已在某会话中」——句子与事实反向 | 补专用分支，说明「请求了 X 会话但抑制了正响应，ECU 按协议未回，会话很可能已切换但无响应证据」 |
| 刷写「跳过步骤」借用 `ClientOverlapped` | 一条「测试仪没发 `0x37`」的结论显示「直接动作：**请求重叠**」——日志里根本没有重叠 | 新增 `UdsObservedAction.ClientSkippedStep`，UI 与导出补映射 |
| `EcuRespondedLate` 文案写「晚于其声明的 **P2**」 | 产生点判的是 **P2\*** 窗口，用户会去核一个不参与判定的参数 | 文案改为「超过自己声明的响应窗口（P2\*）仍未给出最终响应」 |
| **只改文案不够**：`PendingCount > 0` 时无条件返回 `EcuRespondedLate` | 日志在 P2\* 内结束时，同一 finding 一边说「延长记录时间」，一边说「ECU 超过响应窗口」——**两行直接矛盾**，且后者无证据 | 新增 `EcuStillProcessing`，动作值跟着 `pendingWindowExceeded` 走 |
| `ClientStopped` 文案写「测试仪**未继续发送**」 | 该值也用于「没等就发了下一条」的形态，而那类 finding 的 Message 写着「20 ms 后就发出了下一条请求」——上下行矛盾 | 改为「未等待该请求的响应（已发下一条或已停止发送）」 |
| 「直接动作」文案在 UI 与导出各存一份，已漂移（`RecordingIncomplete` 一处带括号一处不带），且两处都无测试 | 同一份报告在界面和导出里对同一条结论给出不同措辞 | 下沉到 `UdsNrcSemantics.DescribeAction`，三处共用；补「每个动作值都有文案」与「两个 pending 值措辞必须不同」两条守卫测试 |
| `SessionSummary` 的默认分支是万能兜底 | 任何新增观测值都会被说成「日志开始时 ECU 已在某会话中」——本轮第一个 bug 正是这么来的，不修就还会再来一次 | 默认分支限定为 `UnknownAtLogStart`，另加中性的「无法确定」兜底 |
| 右栏 `SelectedSessionAtTime` 把「请求了但未确认」的会话当事实 | 摘要说「没有响应证据」，右栏却显示「发生时会话：编程」——同一份报告两种确定性 | 对 `RequestedResponseSuppressed` 加「（未确认）」后缀 |

**文档纠正**：

| 原表述 | 实际 |
|---|---|
| §8.3 表：「抑制请求**不进**等待队列」 | **反了**。必须进队列，否则负响应变孤立、NRC 丢失、刷写误判成功。已补 §8.3.1 反例专节 |
| §8.1：补了界面 `BusTrafficTruncationHint` | 该属性**不存在**。界面用全量的 `BusTraffic.Ids`，不截断；截断的是导出用的 `Noise.TopIds` |
| §6.1：「每条 finding 的证据范围可点击」 | **未实现**。`ObservationIndexes` 在 XAML 里零消费者 |
| §6.2：「会话」列 `SessionAtTime` | **不存在**。会话在右栏「选中事务」里显示（`SelectedSessionAtTime`）。时间列单位是**秒**不是毫秒 |
| §4.2 / `UdsFinding.cs`：列了 7 种 `Kind` | 实际只产出 3 种：`negative_response` / `unclosed_request` / `flash_incomplete` |
| §4.3 `UdsObservedAction` 只有 5 个值 | 实际 9 个（新增 `ClientStopped` / `ClientSkippedStep` / `EcuStillProcessing`） |
| §4.3 `UdsSuspect.EcuTiming`「ECU 响应慢」 | **零产出点**，没有任何规则返回它。`UdsObservedAction.ClientOverlapped` 同样是零产出点 |
| §4.7 `UdsResponseValidationFailure` 4 参数含 `Facts` | 实际 3 参数。校验失败发生在配对阶段，那时 `UdsOperationFacts` 还没生成 |
| §4.1 `NrcName` 来自 `UdsNegativeResponseCode` | 实际来自 `UdsNrcSemantics`（与界面/导出/AI 同一张表） |
| §4.6 `LooksLikeFlash`：含 `0x10 03` | 已收紧为「必须出现 `0x34`/`0x36`/`0x37`」。`0x10 03` 会把写 VIN 的普通诊断判成刷写 |
| §7 P0：「两条 finding 规则（会话切换、直接失败）」 | 实际三条，且**没有**会话切换 finding——会话由 `SessionTimeline` 陈述 |
| §8.3 测试计数（27 / 16 / +6 / 2） | 实测 31 / 21 / 19 / 9 |
| §8.3 掩码列表「8 个 SID」 | 掩码用于回显比对；`EchoCheckLevelFor` 的 `FullEcho` 集合是 **11 个** SID。两件事不要混写 |
| §4.3 NRC 表：「`0x70`–`0x73` 一律 ClientParameters」 | `0x71`（传输被 ECU 暂停）与 `0x72`（ECU 编程失败）实际映射 **EcuInternalState**。按原表会让用户去查客户端参数，而问题在 ECU 侧。已拆成四行 |
| 验收第 5 条：「`Interpretation` 必须带操作对象」 | 实际落点是 `TargetDisplay` / `UdsOperationFacts`，解释器对负响应仍提前返回 |

**代码注释同步**：`UdsFindingGenerator` 重复的 `<summary>` 块（前一块描述的是已被推翻的判据）、
`UdsLogAnalysisViewModel` 无主的 `BusTrafficTruncationHint` 注释块，均已删除。

### 8.5 功能寻址支持（2026-11-27 追加）

**问题**：`UdsOfflineAddress` 只认「物理请求 ID → 物理响应 ID」一对。测试仪用功能寻址
（如 `7DF`）发出的请求被判成背景流量丢掉，而 ECU 在物理响应 ID 上回的响应仍被收下
——于是报告里凭空多出「孤立响应」，且**说不清来源**。

真实日志 `150635_log.asc` 的证据：

```text
3.685877  7DFx  Tx  02 10 03            ← 测试仪的功能广播
3.702107  71Ex  Rx  06 50 03 00 32 01 F4  ← ECU 的正响应（物理 ID）
3.728115  7DFx  Tx  02 85 02
3.744824  71Ex  Rx  02 C5 02
3.749869  7DFx  Tx  03 28 03 01
3.778837  71Ex  Rx  02 68 03
```

改前跑出 `事务=122 孤儿=3`，那三条孤儿正是上面三条响应。用户看到「孤立响应 3」
会以为 ECU 在发没人要的响应——实际上是测试仪自己发的三个请求的响应。

**修法**：`UdsOfflineAddress` 增加可选 `FunctionalId`。

| 改动 | 说明 |
|---|---|
| `UdsOfflineAddress.FunctionalId`（新，可空） | 功能寻址 CAN ID；null = 不解析 |
| `IsFunctional(frame)` | 判定「这一帧是不是功能广播」 |
| `Matches` / `RoleOf` | 功能广播算**请求**方向（它确实是测试仪发出的） |
| 离线页新增「功能 ID」输入框 | 留空 = 不解析；填了非法值**报错**而不是静默忽略 |

**响应侧不需要额外配置**：功能寻址的响应回到 ECU 自己的物理响应 ID，已有的
`ResponseId` 覆盖它。

**验证**：同一份真实日志，配 `7DF` 后 `事务 122→125、孤儿 3→0`，计数闭合仍为 `True`。

**仍不支持**（保持前置文档 §2.2 的边界）：多 ECU 并行分析——一份报告仍只描述
一个 ECU 的请求/响应对；功能广播的响应若来自多个 ECU（多个响应 ID），只会收下
配置里那一个。这需要「一对多」的配对模型，不在本次范围。

## 9. 风险

| 风险 | 应对 |
|---|---|
| 关联规则误判 | P0 不生成关联；P1 的每条关联必须带 `Basis` 说明依据，且 `LikelySharedCause` 需有「中间无成功事务」的反证检查 |
| 责任侧映射被读成判责 | 枚举名用 `Suspect` 而非 `Culprit`；UI 文案固定为「优先排查」；验收标准第 8 条强制 |
| 刷写判据过窄（OEM 私有服务） | `LooksLikeFlash` 用「出现任一刷写服务」而非「必须完整序列」；不匹配时 `Stages` 为空，UI 不显示该区块，不报错 |
| 会话起点未知被误当默认 | 验收标准第 3 条；`UnknownAtLogStart` 独立枚举值 |
| P0 字段与 P1 状态机不匹配导致返工 | D2：`UdsOperationFacts` 一次定义，P0 只消费不扩展 |
| 报告体积膨胀 | `Observations` 只收异常与状态切换，不收每个成功事务；沿用噪声 Top-N 的截断思路 |
