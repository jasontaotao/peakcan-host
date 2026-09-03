# Restbus 统一节点模型与总线环境配置 — 设计文档

- 日期：2026-09-03
- 状态：Draft v3（v2 已过架构/产品双视角 review：3 CRITICAL + 6 HIGH + 6 MEDIUM 全部并入；v3 再补消歧：通道归属、FD 帧格式、信号状态生命周期、执行契约、J1939 身份不变式、UDS/模板落点、迁移发布门禁）
- 分支：`feat/restbus-unification`（建议）
- 相关先例：`2026-08-29-j1939tp-gbt27930-design.md`（SimulatedNode B1/B2、J1939TpLayer）、`2026-08-21-hil-multichannel-design.md`（ChannelConfig/TargetChannel）、`2026-08-27-hil-multichannel-wiring-and-ux-gaps-design.md`

---

## 1. 背景与目标

### 1.1 问题：同一概念有三个平行实现

"模拟总线上的一个 ECU"目前有三套互不知晓的机制：

| 机制 | 位置 | 行为丰度 | 配置方式 |
|---|---|---|---|
| `BackgroundFrame` + `BackgroundFrameSender` | hil-core / host.Infra | 哑：周期帧（死数据 + counter/checksum） | TestSuite JSON 字段，studio 可见 |
| `NodeConfig` + `RuleBasedBehavior` | host.App 私有 | 周期帧表 + ECA 规则（5 动作原语） | host UI 手工编辑，`{Name}.node.json`，**studio 不可见** |
| `EcuScript` + `EcuStateMachine` | hil-core 模型 / host 执行 | UDS 请求→响应状态机 | 独立文件 + fixture/generator 体系 |

查证结论（2026-09-03）：`HilRunnerService` 对 Nodes 零引用；`NodeHostService` 仅 App 层引用；studio 对 `NodeModel`/`NodeConfig` 零命中。三者唯一交点是 `ChannelRouter`（物理总线）——可以"碰巧同时跑"，但配置、生命周期、数据模型完全分离。

### 1.2 目标

1. **模型收敛**：三套机制统一为一个节点模型 `RestbusNode`（hil-core），`BackgroundFrame` 退役——哑节点 = 没有行为的节点，不再有独立概念。
2. **用户体验优先**：环境配置的入口在 studio"总线环境"页签，用户全程不碰 JSON、不抠字节、不管理文件（§4 三铁律）。
3. **执行统一**：host 执行侧一套调度器，`BackgroundFrameSender` 退役。

### 1.3 非目标（YAGNI）

- GBT27930 应用层完整状态机（CHM→CRM→BCP→充电→结束 的自动协商）——协议模板只做"够用的握手响应"，完整状态机后续独立 feature。
- 节点编辑 UI 从 host 迁移到 studio 的旧界面下线——新入口在 studio 建好后，host 旧 UI 再退役（M2 后评估）。
- record & replay（trace 滤自发帧回放）——依赖本设计的环境模型，作为后续独立 feature（§14 M5 仅留接口）。
- ISO-TP / J1939TP 统一抽象——维持 j1939tp spec 的既定决策（各自独立，共享工程模式）。

## 2. 范围

| 项 | 范围 |
|---|---|
| 统一模型 `RestbusNode` | hil-core 新增；吃 `BackgroundFrame` + `NodeConfig` + `EcuScript` 三种语义 |
| TestSuite 集成 | 新增 `Environment` 可选字段（null 默认，沿用 lockstep 惯例） |
| 执行器 | 以 Nodes 的 10ms 单扫描调度为底座；`BackgroundFrameSender` 退役 |
| studio | 新增"总线环境"页签（勾选节点 + 模板 + 信号改值 + 静态预览）；**试运行在 host**（review C1：studio 纯配置器零硬件能力） |
| J1939 | 模型层可表达（SA/PGN/TpMode）；执行零新增（`J1939TpLayer` 收发全套已存在，仅接线） |
| 协议模板 | 机制 + 首例模板 `gbt27930-charger` |
| DBC 勾选生成 | M4；前期由模板自带帧定义兜底 |

## 3. 现状资产盘点

### 3.1 三套机制精确清单

**A. BackgroundFrame（将退役）**
- `hil-core/HIL/BackgroundFrame.cs`：`Id / Data(≤8|≤64) / PeriodMs / Fd / AutoCounter / AutoChecksum`
- `hil-core/HIL/TestSuite.cs`：`BackgroundFrames` 可选字段
- `host.Infra/HIL/BackgroundFrameSender.cs`：每帧一个 `BackgroundFrameTimer`，`UpdateFrameData(id, data)`
- `hil-core/HIL/StepParams/ModifyBackgroundFrameStep.cs`：`Id + Data`（改数据不改周期）
- 牵连面（9 文件）：`StepValidatorRegistry`、`FrameAutoConfigProcessor`、`ReferenceCollector`、`StepParameters{,Factory,Exporter}` 等——全在模型层，可控。

**B. Nodes（将下沉为骨架）**
- `host.App/Services/Nodes/NodeModel.cs`：`NodeConfig(Name, Tag, Identity, Messages, Rules, AddressClaimEnabled)`；`NodeIdentity` 多态（`j1939` → SA）；持久化 `{Name}.node.json`
- `NodeMessage(MessageRef, IntervalMs, Payload, Enabled)` — 周期消息表
- `ResponseRule(MessageRef Trigger, BytePattern? Condition, NodeAction Action, int DelayMs)` — ECA 规则
- `MessageRef` 多态：`J1939MessageRef(Pgn, Priority, Mode?, Sa?, Da?)` / `CanMessageRef(Id, IsExtended)`
- `NodePayloadSource` 三源：`FixedHexSource` / `DbcSignalsSource(MessageName)` / `ScriptCallbackSource`
- `NodeAction` 五原语：`send / setSignal / start / stop / script`
- `RuleBasedBehavior`：10ms 单扫描定时器（`ScanIntervalMs`）+ `OnMessageArrived` 规则分发
- `NodeHostService`：DI singleton，有界队列（256, DropOldest），挂 `ChannelRouter`

**C. EcuScript（将并入节点的可选行为）**
- `hil-core/HIL/EcuScript.cs`：`Name / CanIds / StateMachine / DidValues? / InitialState`
- `EcuStateMachine`：`EcuStateTransition(FromState?, ServiceId, SubFunction?, DataMask/Pattern?, Response(Static|Dynamic), ToState?, ResponseDelayMs)` + `EcuContextStore`（跨请求状态）+ 动态生成器热替换（`ReplaceGenerators`）
- 接入方式：fixture/generator 体系（`BuiltInGenerators`、`HeadlessHostBuilder`），非 TestSuite 直接字段。

### 3.2 执行侧已有、零新增的资产

| 资产 | 位置 | 本设计中的角色 |
|---|---|---|
| `J1939TpLayer`（SendFlow/ReceiveFlow/RtsCtsFlow/WatchdogFlow） | host.Core | J1939 多帧收发，仅接线 |
| `J1939CyclicSendService`（TpMode BAM/RTS-CTS、`_inFlight` 闸） | host.App | 周期 TP 发送 |
| `ChannelRouter` fan-out（per-sink 异常隔离） | host.Infra | 环境运行时挂接点 |
| 表达式求值器（`signal.` sourceRef、递归下降 Evaluator） | hil-core | ECA `setSignal` 的求值底座 |
| `SequenceLibrary`（JSON、atomic write、`%APPDATA%`） | host.App | 环境预设/模板持久化模板 |
| `TestCase.GeneratedFromPrompt` | hil-core | 未来 AI 生成环境配置的 traceability 先例 |

## 4. 用户体验设计（先于技术）

> 设计原则：**好用是目标，技术是手段**。本节是验收的标尺，后续章节不得与之冲突。

### 4.1 用户故事

> 台架上只有 BMS，我要测充电流程，需要一个会握手的充电桩。

目标体验：**studio 配置（勾选/模板/改值）→ host 打开 suite 试运行 → 跑测试，全程 ≤ 5 步**，不碰 JSON、不抠字节、不记文件路径。

### 4.2 studio"总线环境"页签

TestSuite 编辑器新增页签（与 Cases 页签平级）：

```
┌─ 总线环境 ────────────────────────────────────────────┐
│ DBC: gbt27930.dbc ▾                                   │
│                                                       │
│ 可用节点（来自 DBC）        已启用环境                  │
│ ┌─────────────────┐        ┌──────────────────────┐  │
│ │ ☐ VCU           │        │ ⚡ Charger (充电桩)    │  │
│ │ ☑ Charger       │  ───►  │ ├ 12 条周期帧 ✓自动    │  │
│ │ ☐ OBC           │        │ ├ 行为: GB/T 27930     │  │
│ │ ☐ BMS (被测件)   │        │ │   充电桩握手 ✓模板    │  │
│ └─────────────────┘        │ └ [展开规则] [改信号值] │  │
│                            └──────────────────────┘  │
│ 预计总线负载: 23% (847 帧/秒)   ← 纯静态预览，无需硬件 │
└───────────────────────────────────────────────────────┘
```

静态负载使用节点通道的 `BaudRate/Fd`、DBC 报文长度、`IntervalMs` 和 J1939 TP 分段开销计算；不估算 bit stuffing，公式展示为“负载率 + 帧/秒”。若通道波特率或 DBC 报文长度缺失，显示“负载未知”，不显示伪精确值。


> **能力边界（review C1）**：studio 是纯配置器（全仓零 `ICanChannel`/`PCAN` 引用），页签只做**静态预览**（负载率、帧清单、模板规则展开）。试运行需要总线，归属 host（§4.3）。

用户操作只有：

1. **勾选节点**——周期帧、counter/checksum、初值自动从 DBC 来（M4；M4 之前由模板自带帧定义）。
2. **选行为模板**——如"GB/T 27930 充电桩"，ECA 规则自动填入（§8）。
3. **（可选）改信号值**——节点卡片上直接改信号（如 SOC=50%），底层走 `DbcSignalsSource` 编码，用户不碰字节。

### 4.3 试运行与诊断（host 侧，好用的关键一环）

**试运行入口在 host**：host 打开 suite 后提供"试运行环境"按钮（host 拥有通道/TP/trace 全套能力；studio 不做跨进程调用，避免架构债）。不跑正式测试，只拉起环境：

- trace 实时可见节点发帧、被测件是否回应；
- 模拟节点发的帧**灰色斜体标记 `sim`**，与真实帧一眼区分（核心帧类型新增 `FrameSource` 标记，改动面见 §6.5）；
- 失败给具体诊断而非哑巴失败，例如：

```
✗ 充电桩发出 CRM(aa) 后 500ms 未收到 BRM
  可能原因: ① 接线/通道选错  ② BMS 未上电  ③ SA 地址冲突
```

诊断规则来自节点内嵌的 `TrialContract`（模板应用时随节点快照持久化，见 §8）。host 不依赖 studio 内存模板或模板库文件。节点没有 `TrialContract` 时，试运行只展示帧流和计数器，不做握手通过/失败判定。
试运行使用 suite 的通道/DBC/硬件映射，与正式执行互斥；可取消，超时或断言失败后停止环境并输出诊断结果。


### 4.4 三条铁律（防"难用"滑坡）

1. **DBC 是总线几何/报文/信号的事实源**——普通 UI 禁止手填 CAN ID / 字节。模板自带帧定义也必须通过目标 DBC 校验，不得绕过 DBC 生成私有 ID。
2. **模板是普通 UI 的规则唯一来源**——不提供"新建空白 ECA 规则"入口。规则只能来自模板、复制现有模板后编辑、或在 DBC 生成节点上叠加模板；专家级字节/JSON 逃生口不进入普通 UI。用户可从配好的节点"另存为模板"沉淀。
3. **环境是 TestSuite 的属性**——复用通过 studio 内部"环境预设"实现，用户不管理 `.node.json` 文件。

### 4.5 正式执行零新增操作

host 跑 TestSuite = 现在这个按钮，什么都不变。环境随 suite 自动拉起、结束自动停止（试运行是可选的前置验证，不是必经步骤）。测试报告新增"环境节"：各节点发帧计数、规则命中次数、UDS 响应计数（模型落点见 §5.5）。

### 4.6 两端 UI 变化全景

现状盘点：

- **studio 面板**：`Suite Builder`（内含 BgFrames 编辑 popup）/ `ECU Simulator` / `Copilot` / `DBC Browser`
- **host Views**：`Dbc` / `Hil` / `Nodes` / `Record` / `Replay` / `Script` / `Send` / `Signal` / `Stats`

**studio（配置器——加法为主）**：

| 变化 | 时点 | 说明 |
|---|---|---|
| ➕ Suite Builder 新增"总线环境"页签 | M2 | §4.2 mockup |
| ➖ BgFrames popup 删除 | M2 | 被环境页签取代（哑节点 = 无行为节点，同一入口） |
| 🔧 step 编辑器信号级化 | M2 | `SetEnvironmentSignal`：节点→报文→信号三级下拉 + 数值（§6.3） |
| 🔧 ECU Simulator 面板重新挂载 | M2~M3 | EcuScript 并入节点 `UdsBehavior` 后，变为节点卡片"编辑 UDS 行为"入口（面板保留，挂载点变化） |
| ➕ "另存为模板" + 模板库管理 | M3 | 节点卡片上下文菜单（§8.3） |
| ➕ "从 DBC 生成节点" | M4 | §9 生成器入口 |
| ❌ 无试运行按钮 | — | review C1：纯配置器只做静态预览 |

**host（执行器——有增有减）**：

| 变化 | 时点 | 说明 |
|---|---|---|
| ➕ HilView"试运行环境"按钮 + 诊断输出 | M2 | §4.3；落点 R7 |
| ➕ HilView 环境状态指示 | M2 | 各节点运行中/发帧数/规则命中计数 |
| ➕ ReplayView/SignalView sim 帧灰显 | M2 | §6.5 `FrameSource` |
| ➕ 测试报告环境节 | M2 | §5.5 `NodeRunStats` |
| ➖ NodesView 退役 | M2 后（R4） | 编辑统一归 studio |
| ➖ ScriptView（EcuScript 编辑）退役或降级只读 | M2 后 | 编辑归 studio 的 ECU Simulator 面板 |
| ➕ "录制为环境"入口 | M5（可选） | trace 右键/工具菜单 |

**分工原则**：配置全部左移 studio，host 只保留需要硬件的能力（试运行/执行/可视化）。host 从"执行 + 节点配置双功能"收敛为**纯执行 + 验证**；studio 从"TestSuite 编辑器"升级为**"测试 + 环境的完整配置中心"**。

**退役迁移门禁（产品决策）**：NodesView / ScriptView 是减法，会打断现有用户的"host 里手工配节点"习惯。退役以 **studio 侧功能完备为前提**（R4），且发布说明必须含迁移路径（手工节点配置 → studio 环境页签）。不允许出现"host 已删、studio 未就绪"的两代 UI 断裂期。

## 5. 统一模型设计（hil-core）

### 5.1 `RestbusNode`

```
hil-core/HIL/Environment/
├── RestbusNode.cs          聚合根
├── NodeIdentity.cs         多态: j1939(SA) / raw-can；不再携带 Channel
├── NodeMessage.cs          周期帧: MessageRef + IntervalMs + Fd + PayloadSource + Counter/Checksum + Enabled
├── NodePayloadSource.cs    三源（从 host.App 下沉）
├── ResponseRule.cs         ECA 规则（下沉）
├── NodeAction.cs           五原语（下沉）
├── MessageRef.cs           多态: j1939 / can（下沉）
├── BytePattern.cs          条件（下沉）
├── EcuScriptDefinition.cs  UDS 行为的纯数据形态（新增，见 §6.6）
└── TrialContract.cs        模板试运行诊断快照（§8）
```

```csharp
public sealed record RestbusNode
{
    public required string Name { get; init; }
    public string? Tag { get; init; }                       // 分组标签，如 "gbt27930"
    public string? Channel { get; init; }                   // 唯一通道绑定；null=单通道 suite
    public required NodeIdentity Identity { get; init; }
    public IReadOnlyList<NodeMessage> Messages { get; init; } = [];
    public IReadOnlyList<ResponseRule> Rules { get; init; } = [];
    public EcuScriptDefinition? UdsBehavior { get; init; }  // 可选 UDS 行为；运行时构造状态机
    public bool AddressClaimEnabled { get; init; }
    public TrialContract? Trial { get; init; }              // 模板应用后随节点内嵌；host 试运行消费

    /// <summary>节点级信号初值覆盖。键格式 "MessageName.SignalName"。
    /// 只对 DbcSignalsSource 生效；作为 EnvironmentRuntime 启动时的初始信号状态。</summary>
    public IReadOnlyDictionary<string, double>? SignalOverrides { get; init; }
}
```

> **通道唯一事实源**：`RestbusNode.Channel` 是节点绑定通道的唯一位置。现有 `NodeIdentity.Channel` 在下沉时删除；不允许 `NodeIdentity` 与 `RestbusNode` 同时携带通道。
>
> **类型纪律**：`UdsBehavior` 的模型类型是纯数据 `EcuScriptDefinition`（transitions + DID 初值 + generator 引用），不是 `EcuStateMachine`。后者是有状态运行时 class，由 `EnvironmentRuntime` 在启动时构造（§6.6）。旧 `EcuScript` 文件继续作为导入源，但 suite/环境 JSON 使用 `EcuScriptDefinition`。

三种旧语义到新模型的映射：

| 旧 | 新 |
|---|---|
| `BackgroundFrame` | `RestbusNode { Messages=[NodeMessage { Ref=CanMessageRef(...), IntervalMs=PeriodMs, Payload=FixedHexSource(Data), Fd=Fd, AutoCounter=..., AutoChecksum=... }], Rules=[], UdsBehavior=null }` —— 哑节点 |
| `NodeConfig` | `RestbusNode` 直接对应；`NodeIdentity.Channel` 迁移到 `RestbusNode.Channel` |
| `EcuScript` | 先转为 `EcuScriptDefinition`，再嵌入 `RestbusNode.UdsBehavior`；运行时状态机由执行层构造 |

### 5.2 `NodeMessage`

```csharp
/// <param name="IntervalMs">发送周期。约束 ≥ 10：执行底座为 10ms 单扫描，实际发送存在 ≤10ms 抖动。</param>
/// <param name="Fd">CAN FD 帧格式。该字段是通道能力之外的帧级语义，不能从 payload 长度推断。</param>
public sealed record NodeMessage(
    MessageRef Ref,
    int IntervalMs,
    NodePayloadSource Payload,
    bool Fd = false,
    bool Enabled = true,
    CounterConfig? AutoCounter = null,
    ChecksumConfig? AutoChecksum = null);
```

`CounterConfig`/`ChecksumConfig` 已在 hil-core，直接复用。编码顺序锁定：**信号状态 + `SignalOverrides` 覆盖 → `DbcSignalsSource` 编码 → counter/checksum 应用 → 发送**。`FixedHexSource` 不做 DBC 编码，但仍会应用 counter/checksum。

#### 5.2.1 信号状态与生命周期

- `RestbusNode` 是不可变配置；`SignalOverrides` 不是运行时可变状态，而是 `EnvironmentRuntime.Start` 时的初始信号表。
- 环境在 suite 生命周期内创建一次。默认不按 case 自动 reset；case 之间的信号变化会保留。如未来需要隔离，必须显式新增 `ResetEnvironmentStep` 或 suite 配置，不隐式回滚。
- `SetSignalAction` 和 `SetEnvironmentSignalStep` 只修改 runtime 信号表，不回写 suite JSON 或 `SignalOverrides`。
- `SignalOverrides`、`SetSignalAction`、`SetEnvironmentSignalStep` 只允许寻址 `DbcSignalsSource` 的报文/信号；对 `FixedHexSource` 或 `ScriptCallbackSource` 使用信号级修改是配置错误，validator 拦启动。
- 初值编码失败（信号不存在、超物理范围、DBC 缺失）在 suite 加载期报 Critical。运行期通过 step 改成非法值时记录 Error 并保留上一次合法值；下一次收到合法值后恢复。

### 5.3 TestSuite 集成

```csharp
public sealed record TestSuite(
    ... 既有参数 ...,
    IReadOnlyList<ChannelConfig>? Channels = null,
    /// <summary>总线环境：restbus 节点列表，内嵌随 suite 文件走（0.17.0）。
    /// 旧 suite JSON 无此字段 → null（无环境，行为同今）。规范字段名只有 Environment，
    /// 不引入 EnvironmentInline/EnvironmentReference 等平行形态。</summary>
    IReadOnlyList<RestbusNode>? Environment = null);
```

- **`BackgroundFrames` 字段同步删除**（无存量原则，§10）。
- **只做内嵌，不做引用**：模板和环境预设只是设计期来源；suite 保存时写入完整节点快照。执行期不得读取 `%APPDATA%` 节点库补齐 suite 缺失数据。
- 如果用户选择 subset cases，环境仍按 suite 一次性拉起；不因被选 case 未引用某节点而裁剪环境。

### 5.4 校验器扩展（`StepValidatorRegistry`）

新增校验规则：

#### 通道与 DBC

- suite 无 `Channels` 时，所有节点 `Channel` 必须为 null，使用默认单通道。
- suite 有 `Channels` 时，节点 `Channel` 必填且必须按名命中；不允许回落到“第一通道”或默认通道。
- 多通道 suite 中，环境节点引用的通道必须配置 `DbcPath`；单通道 suite 使用 suite 级 DBC。
- `DbcSignalsSource.MessageName` 和 `SignalOverrides` 键按节点通道对应 DBC 解析；同一通道 DBC 内消息名必须唯一。
- DBC 缺报文/信号、`GenMsgCycleTime` 缺失或为 0 时，相关节点生成/校验失败；不使用静默默认周期。

#### ID / 身份 / 冲突

- 节点名唯一。
- `CanMessageRef` 发送冲突 key：`(Channel, Id, IsExtended)`。
- `J1939MessageRef` 发送冲突 key：`(Channel, Priority, PGN, Da, Sa)`。
- UDS 请求/响应 ID 与同一通道上的周期帧、规则动作目标一起进入 ID 冲突检查。
- J1939 发送上下文（`NodeMessage` 或 `SendMessageAction`）必须满足：
  - 节点 `Identity` 是 `J1939NodeIdentity`；
  - `MessageRef.Sa` 非空且等于 `Identity.Sa`；
  - `TpMode.Single` 且 PGN 属于 PDU1 时 `Da` 必填；
  - `TpMode.RtsCts` 时 `Da` 必填；
  - `TpMode.Bam` 不要求 `Da`。
- `ResponseRule.Trigger` 允许 `Sa == null` 表示宽容匹配，但发送侧不变式不放宽。
- `AddressClaimEnabled=true` 时，节点必须有 J1939 身份且 SA 已通过冲突检查。

#### 规则 / UDS / 信号

- `ResponseRule.Trigger` 的 `MessageRef` 合法；规则目标报文必须在同一节点内静态可解析。
- `SendMessageAction` 与周期帧使用相同的发送侧身份规则。
- `UdsBehavior` 的 DID 与 suite `DataSource` 对账；generator 引用必须在 host generator provider 中可解析。
- `SignalOverrides` 键格式必须为 `MessageName.SignalName`；目标报文必须是 `DbcSignalsSource`，信号必须存在。
- `SetEnvironmentSignalStep` 使用相同的节点/报文/信号解析规则。

### 5.5 结果模型

测试报告"环境节"的模型落点：

```csharp
/// <summary>单节点环境运行统计（随 TestSuiteResult 输出）。</summary>
public sealed record NodeRunStats(
    string NodeName,
    long FramesSent,
    long RulesMatched,
    long UdsResponses);
```

`TestSuiteResult` 新增 `IReadOnlyList<NodeRunStats>? EnvironmentStats = null`。

统计语义固定如下：

- `FramesSent`：成功写入通道的逻辑帧/TP 事务数；编码或发送失败不计入。
- `RulesMatched`：trigger 与 condition 匹配并进入动作执行的次数；不表示动作最终发送成功。
- `UdsResponses`：`EcuStateMachine.ProcessRequest` 生成响应的次数，包含正响应和 NRC 负响应。
- 该模型只用于正式 suite 结果；host 试运行返回独立的诊断结果，不复用 `TestSuiteResult`。

## 6. 执行架构（host）

### 6.1 统一调度器

`EnvironmentRuntime` 是 host.Infra 的执行聚合，替代 `BackgroundFrameSender`，并吸收 `RuleBasedBehavior` / `NodeHostService` 的执行语义。

```
EnvironmentRuntime
├── 控制面: Start(nodes, channels) / Stop() / UpdateFrameData(node, ref, data)
├── 周期调度: TimeProvider 10ms 单扫描驱动所有节点 NodeMessage 表
├── 规则分发: incoming frame queue → DBC 解码 → ResponseRule 匹配 → 动作执行
├── UDS 路由: EcuScriptDefinition → EcuStateMachine → ProcessRequest → 延迟响应
└── J1939: J1939 身份节点经 J1939TpLayer 收发；TP 分段/重组在 runtime 边界内完成
```

#### 接口契约

- `Start(IReadOnlyList<RestbusNode> nodes, IReadOnlyList<ChannelConfig>? channels)` 在通道连接完成后调用。
- `Stop()` 幂等；等待在飞 scan callback 结束后再返回。
- `UpdateFrameData(NodeName, MessageRef, byte[] data)` 只允许作用于 `FixedHexSource`；新字节数据从下一次发送生效，counter/checksum 仍会应用。
- 通道解析在 `Start` 内完成：
  - suite 无 `Channels`：所有节点 `Channel == null`；
  - suite 有 `Channels`：所有节点 `Channel` 必须命中一个 `ChannelConfig.Name`；
  - runtime 不做“第一通道”兜底。

#### 线程与队列契约

- 周期扫描、规则分发、UDS 分发和控制面共享同一把 runtime 执行锁；不允许两个规则动作并发修改同一节点状态。
- incoming frame 使用有界队列（容量 256，DropOldest）。丢帧必须计数并输出节流 warning。
- 节点自己发出的帧不得重新进入该节点的规则管线；其他模拟节点发出的帧可以进入规则管线。
- `EnvironmentRuntime` 发出的帧设置 `FrameSource.Environment`（§6.5）。

#### 周期语义

- 扫描量子为 10ms；实际周期 `quantum = max(10, ceil(IntervalMs / 10) * 10)`。
- `Start` 后，enabled 周期帧先立即发送一次，随后按 `previousDue + quantum` 调度；迟到时不补发多次，只发送一次并把后续 due 继续按计划推进。
- 实际发送时间相对量化 due 存在 0~10ms 抖动。
- 连续 10 次发送失败后停用该 `NodeMessage` 并报告 Error；同节点其他消息继续运行（沿用旧 `BackgroundFrameTimer` 策略）。

### 6.2 TestSuite 执行接线

`HilRunnerService` 环境生命周期固定为：

```
suite 开始
  → 解析 suite.Environment（唯一字段名；无 EnvironmentInline）
  → 静态校验
  → 连接通道
  → 解析 DBC / generator / UDS 定义
  → EnvironmentRuntime.Start(nodes, suite.Channels)
  → 执行 selected cases
  → finally EnvironmentRuntime.Stop()
  → 断开通道
```

- 环境与 selected cases 无关：只要 suite 声明环境，就按 suite 全量拉起。
- 正式执行和试运行互斥；host 必须阻止同时运行两套环境。
- 迁移期如果 host 旧 NodesView 仍有手工节点在运行，必须在正式执行/试运行前停止，或因通道/SA/ID 冲突拒绝启动；禁止两套执行器同时写总线。

### 6.3 step 语义迁移（信号级主形态）

`ModifyBackgroundFrameStep(Id, Data: byte[])` → **`SetEnvironmentSignalStep(NodeName, MessageName, SignalName, Value: double)`**：

- 信号级是 studio 主形态；按"节点 + 报文 + 信号"寻址。
- 执行语义：更新节点 runtime 信号表 → 下一次周期编码生效。
- 目标必须是 `DbcSignalsSource`；J1939 TP 事务在飞时不回填，只影响下一次发送/事务。
- 字节级 `ModifyEnvironmentFrameStep(NodeName, MessageRef, Data)` 保留为高级逃生口：
  - 仅 JSON/专家场景可用，studio UI 不提供入口；
  - 只允许目标 payload source 为 `FixedHexSource`；
  - 不改周期，不绕过 counter/checksum。
- `TestCaseStepKind.ModifyBackgroundFrame` 删除；新增 `SetEnvironmentSignal` 主形态与 `ModifyEnvironmentFrame` 逃生口（无存量原则，§10）。

### 6.4 J1939 执行

- 节点身份为 `J1939NodeIdentity` 时，周期帧和 `SendMessageAction` 的 `Sa` 必须等于节点身份；校验规则见 §5.4。
- `TpMode == null` 时：encoded payload ≤8 使用 `Single`，>8 使用 `Bam`；`RtsCts` 必须显式声明。
- `Bam` / `RTS-CTS` 多帧发送经 `J1939TpLayer`；`RTS-CTS` 必须有 `Da`，并复用 `_inFlight` 闸防止并发事务。
- 接收侧：`J1939TpSinkAdapter` 重组后的逻辑报文进入规则分发与 AssertSignal 解码；单帧 PDU1 缺 `Da` 仍由 validator 拦截。
- `AddressClaimEnabled=true` 时，`EnvironmentRuntime.Start` 先发送一次 Address Claimed 帧，再启动周期帧。本 feature 不做地址竞争/重 Claim 状态机；SA 冲突在 suite 校验期失败。

### 6.5 sim 帧标记

`sim` 标记需要帧携带来源信息——这是 hil-core 核心类型变更，lockstep 级别：

- `CanFrame` 新增 `FrameSource` 枚举字段（`Bus | Environment`），默认 `Bus`，序列化省略默认值；
- `EnvironmentRuntime` 发出的帧置 `Environment`；
- trace/record/replay 可以展示该标记；statistics 默认统计行为不改变；
- 旧 replay 文件没有该字段时按 `Bus` 解释。

### 6.6 UDS / `EcuScriptDefinition`

- `RestbusNode.UdsBehavior` 是纯数据 `EcuScriptDefinition`，不持有状态机、context store 或 generator 实例。
- `EcuScriptDefinition` 字段：UDS CAN ID 配置、transitions、DID 初值、initial state、generator 引用。
- `EnvironmentRuntime.Start` 按节点构造独立 `EcuStateMachine`；suite stop 后状态机销毁。
- generator 引用由 host generator provider 解析（内置 generator + fixture/ODX/外部注册）。未知名在加载期报 Critical。
- 本 feature 只支持 raw CAN UDS；J1939 diagnostic 协议不在范围。
- `UdsBehavior.CanIds` 沿用现有 `EcuScript`/`EcuScriptLoader` 的语义，不在 `RestbusNode` 上重复表达；其通道使用 `RestbusNode.Channel`。
- 正式 suite 运行期间 UDS 状态不按 case 自动 reset；如需 reset，必须后续显式定义 step 或配置。

## 7. 序列化与文件格式

### 7.1 格式

- `RestbusNode` JSON 多态沿用现有惯例（`kind` 判别符，与 `NodeIdentity`/`MessageRef`/`NodeAction` 一致）；
- `{Name}.node.json` / 环境预设只是 studio 设计期缓存或复用载体；suite 保存时必须嵌入完整 `RestbusNode` 快照。执行器不得读取节点库补齐 suite；
- `EcuScriptDefinition` 序列化只包含声明式 transitions、DID 初值、generator 引用；不序列化 `EcuStateMachine` 实例。

### 7.2 兼容性原则

- **无存量原则**：HIL 工具链无旧文件，schema 变更不做向后兼容（既定规则）。`BackgroundFrames` 字段、旧 step kind 直接删除，不写迁移层。
- **lockstep 惯例不变**：新增字段一律 null 默认；hil-core bump → host/studio 双 pin → 双侧 `InteropTests` 绿 → 合并。

## 8. 协议模板机制

### 8.1 模板格式

模板是设计期资产，应用后生成 suite 内嵌节点快照：

```jsonc
{
  "templateId": "gbt27930-charger",
  "displayName": "GB/T 27930 充电桩",
  "node": { /* RestbusNode 协议语义：Identity/Messages/Rules/UdsBehavior/AddressClaim */ },
  "handshake": [
    { "send": "CRM", "thenReceive": "BRM", "timeoutMs": 500,
      "possibleCauses": ["接线/通道选错", "BMS 未上电", "SA 地址冲突"] },
    { "send": "CTS", "thenReceive": "BCL", "timeoutMs": 250 }
  ],
  "requiredDbcMessages": ["CRM","BRM","CTS","BCL","CST","BST"]
}
```

模板应用时写入节点级 `TrialContract`：

```csharp
public sealed record TrialContract(
    string TemplateId,
    IReadOnlyList<HandshakeExpectation> Handshake,
    IReadOnlyList<string> RequiredDbcMessages);
```

因此 host 试运行不依赖 studio 内存中的模板，也不依赖 `%APPDATA%` 模板库；诊断契约随 suite/节点迁移。

### 8.2 首例模板 `gbt27930-charger`

- M2 seed 模板放在共享 hil-core 的纯数据定义中；studio 与 host 引用同一 `templateId`，不允许 studio 私有副本。
- 帧定义从 DBC 提取并沉淀；ECA 规则覆盖握手主链：CRM→BRM、CTS→BCL 跟随、CST→BST 应答。
- 完整 GBT27930 应用层状态机仍是非目标；模板只保证最小握手和充电保持。
- 应用模板默认替换协议语义：`Identity`、`Messages`、`Rules`、`UdsBehavior`、`AddressClaimEnabled`、`Trial`；保留用户已设置的 `Name`、`Tag`、`Channel`。目标字段为空时可采用模板默认值。

### 8.3 用户模板沉淀

- M3 支持"另存为模板"，保存到模板库（`SequenceLibrary` 模式：`%APPDATA%`、atomic write）。
- 另存时默认剥离 `SignalOverrides`；提供"保留信号初值"勾选供刻意固化。
- 用户模板生成新的稳定 `templateId`；应用时同样把 `TrialContract` 快照嵌入节点。
- 普通 UI 仍不提供"新建空白 ECA"；用户可复制模板后修改。

## 9. DBC 勾选生成（M4）

机械映射规则（`DbcRestbusGenerator`，hil-core 纯函数）：

| DBC | → RestbusNode |
|---|---|
| `BU_` 节点 | 节点骨架（Name） |
| 节点的 `BO_` 发送报文 | `NodeMessage`（ID → `CanMessageRef`/`J1939MessageRef`，`GenMsgCycleTime` → `IntervalMs`） |
| 信号默认值 | `DbcSignalsSource` 初值 |
| 命名/属性含 Cnt、CRC | `AutoCounter`/`AutoChecksum`（识别规则可配置，默认覆盖 GBT27930 惯例） |

**边界**：ECA 规则不从 DBC 来（协议行为不在 DBC 里）——只能来自模板或复制模板后编辑；普通 UI 不提供空白 ECA。生成后用户叠加模板。若 DBC 缺 `GenMsgCycleTime`，该报文不能自动生成周期节点，必须显式报错，不使用默认周期。

## 10. 迁移与退役清单

| 项 | 处置 |
|---|---|
| `hil-core: BackgroundFrame` | 删除（语义由哑节点承载） |
| `hil-core: TestSuite.BackgroundFrames` | 删除字段 |
| `hil-core: ModifyBackgroundFrameStep` | 替换为 `SetEnvironmentSignalStep`（主）+ `ModifyEnvironmentFrameStep`（逃生口），见 §6.3 |
| `host.Core: enum TpMode` | **下沉 hil-core**（review H4：`J1939MessageRef` 下沉的连带依赖；纯枚举无厂商依赖，无架构障碍） |
| `host.Infra: BackgroundFrameSender` | 删除（`EnvironmentRuntime` 替代） |
| `host.App: Services/Nodes/*` | 模型类下沉 hil-core；`NodeHostService`/`RuleBasedBehavior` 迁移至 `EnvironmentRuntime`（Infra 层）；`NodeIdentity.Channel` 删除 |
| `host.App: NodeEditorViewModel`（NodesView）+ `ScriptView`（EcuScript 编辑） | M2 后退役或降级只读（studio 新入口就绪且用户确认后；门禁见 §4.6） |
| `EcuScript` 独立文件 + fixture 接入 | 保留加载器（模板可引用），语义并入 `RestbusNode.UdsBehavior`；fixture 接线方式在实施时验证（§15 R3） |
| `hil-core: CanFrame.FrameSource` | 新增字段（§6.5，lockstep 级别变更） |
| 牵连 9 文件（validator/factory/exporter/collector） | 随字段删除同步修改 |
| 旧 `.node.json` / `EcuScript` 文件 | M2 studio 提供显式导入：节点库/EcuScript → suite 内嵌 `RestbusNode`；不做后台自动绑定，避免引用失效 |
| 旧 NodesView 手工节点 | 迁移期允许只读保留；正式执行/试运行前必须停止或消除冲突，禁止与 `EnvironmentRuntime` 并发写总线 |

## 11. 数据流

```
[配置期 studio]
DBC + 勾选 + 模板 → RestbusNode（内嵌进 TestSuite JSON）
                              │
[执行期 host]                 ▼
HilRunnerService → EnvironmentRuntime.Start(nodes, suite.Channels)
   ├─ 周期帧: 10ms 扫描 → 信号状态编码 → counter/checksum → ChannelRouter.Write
   ├─ 规则:   Router 帧 → 解码 → ResponseRule → 动作（send/setSignal/start/stop）
   ├─ UDS:    请求帧 → EcuScriptDefinition → EcuStateMachine.ProcessRequest → 延迟响应
   └─ J1939:  >8B → J1939TpLayer 分段; 接收 → 重组 → 逻辑帧入分发
                              │
[反馈]                        ▼
trace（sim 标记）/ 试运行诊断 / 报告环境节（发帧数、规则命中数）
```

## 12. 错误处理与诊断

| 场景 | 行为 |
|---|---|
| 环境引用未知名节点 | suite 加载期 validator 报 Critical，拦启动 |
| 节点/身份/UDS/信号/DBC 配置冲突或缺失 | suite 加载期 validator 报 Critical，拦启动；不等到 runtime 才暴露 |
| 规则动作目标帧不存在 | suite 加载期 validator 报 Critical；runtime 保留防御性检查 |
| 节点间 SA/ID 冲突 | suite 加载期报 Critical；`EnvironmentRuntime.Start` 保留防御性 `ArgumentException` |
| DBC 缺模板声明报文或周期 | 模板应用/加载期报错并列出缺失清单，禁止半成品环境 |
| 环境发帧连续失败 10 次 | 停用该 `NodeMessage`，报告/日志记录 Error；同节点其他消息继续 |
| incoming 队列溢出 | DropOldest，计数并输出节流 warning |
| 试运行握手超时 | 按 `TrialContract` 输出具体环节 + `possibleCauses`；试运行结束并停止环境 |
| step 运行期改出非法信号值 | 记 Error，保留上一次合法值；下一次合法值恢复 |

## 13. 测试策略（TDD）

- **模型层（hil-core，纯函数）**：序列化 round-trip、多态判别、validator 新规则（含 SA 非空、多 DBC 解析、SignalOverrides 键）、DBC 生成器映射表（表驱动用例）；
- **执行层（host）**：`EnvironmentRuntime` 周期调度（假时钟，沿用 RuleBasedBehavior 测试模式）、规则分发、UDS 路由（`EcuScriptDefinition` → `EcuStateMachine` 构造）、J1939 多帧分段接线、`SetEnvironmentSignal` step、连续发送失败和队列溢出策略；
- **M1 行为等价验收（review M4，具体化）**：①现有 suite 全量回归绿（无环境 suite 行为逐字节等价）；②帧级时序对比——同一哑节点配置分别经旧 `BackgroundFrameSender` 与新 `EnvironmentRuntime` 发送，对比首帧立即发送、周期抖动分布、counter 连续性、checksum 正确性、FD 标志、连续失败停发策略；③`IntervalMs` 收紧前全量扫描现有 suite。若发现 `<10ms` 用法，按升级 blocker 处理：必须人工调整为 ≥10ms 并记录影响；不引入 <10ms 运行时兼容分支；
- **Interop（lockstep 门禁）**：hil-core 模型在 host/studio 双侧序列化互认；
- **验收场景**（继承 j1939tp spec §10.1 推演）：模拟充电桩 + 真实 BMS log 回放对照——CRM 发出 → BRM 应答 → CTS/BCL 跟随 → 充电保持，环境行为与真实 log 逐段比对；
- 覆盖率门禁沿用仓规（hil-core 全绿 + host/studio InteropTests）。

## 14. 里程碑与 PR 切片

| 里程碑 | 内容 | 用户可见 |
|---|---|---|
| **M1** | hil-core `RestbusNode` 模型 + validator + `TpMode`/`FrameSource`；host `EnvironmentRuntime`；`BackgroundFrame`/`BackgroundFrameSender` 退役；step 替换；lockstep 发布 | 内部收敛；不得独立发布到用户通道 |
| **M2** | studio"总线环境"页签（勾选/共享 seed 模板/信号改值）+ host 试运行 + sim 帧标记 + 报告环境节 + 旧节点/EcuScript 导入 | **首次可用** |
| **M3** | 模板机制通用化（模板库加载 + "另存为模板" + 实例值剥离） | 任意协议模板可沉淀 |
| **M4** | `DbcRestbusGenerator` 勾选生成 | 任意 DBC 节点一键生成 |
| **M5**（可选） | host trace "录制为环境"（周期帧识别 + 滤被测件地址） | 现场→实验室复现 |

**发布门禁**：M1 和 M2 必须进入同一个用户可见 release。禁止“M1 已删 BgFrames/旧 step，M2 替代 UI 未发布”的用户断层。M1 可以在 feature branch 或 nightly lockstep 中先行合入，但不能单独作为产品 release。

M2 的"模板"不是 studio 私有数据；`gbt27930-charger` seed 及其 `TrialContract` 放在共享 hil-core 纯数据定义中，studio 与 host 同源消费。

## 15. 风险与待验证项

| # | 项 | 状态 / 剩余工作 |
|---|---|---|
| R1 | 10ms 扫描精度 | 已定案：`IntervalMs >= 10`，首帧立即发送，量化周期 + 0~10ms 抖动；`<10ms` 存量是升级 blocker |
| R2 | counter/checksum 与 DBC 信号交互 | 已定案：信号编码 → counter/checksum → 发送；实施用 BackgroundFrame 等价回归 |
| R3 | EcuScript fixture 体系接线 | 已定案模型形态：`EcuScriptDefinition` + generator 引用；实施时核对 generator provider 与导入器 |
| R4 | host 旧 Nodes UI 退役时机 | 已定案：M2 提供导入，M2 后只读过渡；同一 release 不允许 host 已删、studio 未就绪 |
| R5 | 多通道环境 | 已定案：通道只在 `RestbusNode.Channel`；多通道必填且必须命中 `ChannelConfig` |
| R6 | `EcuScript.CanIdConfig` 与节点身份关系 | 已定案：沿用 `EcuScript` 现有 ID 语义；通道取 `RestbusNode.Channel`；不在节点上重复 UDS ID |
| R7 | host 试运行 UI 落点 | 默认落在 HilView，最终布局实施时调整；契约是打开 suite 后一步可达 |

## 16. 接口契约通则（施工前必读）

- hil-core 模型变更 = 版本 bump + host/studio lockstep + 双侧 InteropTests 绿（仓规，不变）；
- 新增序列化字段一律可空默认（lockstep 期间平稳）；
- Core 层零 I/O、零厂商 SDK 依赖（NetArchTest 红线，不变）——`RestbusNode`/`EcuScriptDefinition`/`TrialContract` 模型内不得出现定时器、通道句柄或厂商类型；
- 表达式求值复用 hil-core Evaluator，禁止在执行层新写解析器；
- UI 入口遵守 §4.4 三铁律，新增入口需对照检查。
