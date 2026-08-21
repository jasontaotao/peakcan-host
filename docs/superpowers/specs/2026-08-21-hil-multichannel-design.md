# Design: HIL 多 CAN 通道支持（一次 run 内多路总线收发监控）

> Spec date: 2026-08-21
> Depends: 现有 HIL 引擎（`PeakCan.Host.Core/HIL`）+ `PeakCanAssertionContext` + `HeadlessHostBuilder` + `AscFrameSink`/`AscFileFormat` + `HtmlReportGenerator` + hil-core 包 0.11.0（`PeakCan.HIL.Core` NuGet，双仓库 host+studio 消费）
> Scope: **一次 HIL run 内连接多路 CAN 通道（多台 CAN 盒 / 单卡多通道混用），用例步骤可指定"发到哪路 / 监控哪路"，并贯穿 case log 与报告的通道区分**。AppShell 多通道不在本期（另立项，HIL 独立 host 链路天然不阻塞）。
> Status: DRAFT（2026-08-21 用户确认 8 个决策点 Q1–Q8，见 §5）

---

## 1. Goals

当前 HIL 引擎是**严格单通道**模型：一次 run 一个 `ICanChannel` 单例、一个 `IAssertionContext`、一个 DBC、一个 UDS 配置。用例步骤无法表达"发到通道 B / 监控通道 A"。

本设计目标：

**G1. 多通道会话** — 一次 run 连接 N 路 CAN（PEAK/ZLG 混插或单卡多通道），每路独立句柄/波特率/FD 模式/DBC。
**G2. 步骤级目标声明** — 发送类与帧监控类步骤可指定 `TargetChannel`（逻辑名，引用 run 配置的通道别名）；缺省 = 唯一/默认通道。
**G3. 监控按通道分桶** — `ExpectFrame`/`AssertNoFrame`/`AssertFrameCount`/`AssertCycleTime` 监控指定通道的帧流；`FrameStatistics` 按通道独立。
**G4. case log 通道可分** — 每 case 一个 `.asc`，多路帧合并写入，asc 行带 channel 列（PEAK asc 惯例）。
**G5. 报告通道可分** — `StepResult` 带 channel；HTML 报告失败帧块标通道；失败附近帧按通道 DBC 解码。
**G6. 完全向后兼容** — 旧 suite 无 `Channels` / 步骤无 `TargetChannel` → 隐式单通道默认，语义零变化。

### 范围界定（Q1 决策）

本期 **MVP 仅 6 类步骤**加 `TargetChannel`：`SendFrame` / `SendSequence` / `ExpectFrame` / `AssertNoFrame` / `AssertFrameCount` / `AssertCycleTime`。

**信号断言（`AssertSignal`/`AssertRange`/`WaitForSignal`）与 UDS 步骤（`ReadDid`/`WriteDid`/`RoutineControl` 等）本期不加 `TargetChannel`**——信号断言依赖 per-channel DBC 语义（每通道独立 DBC），UDS 绑定 per-channel UDS 配置，二者改动面与语义复杂度另立项。本期这些步骤在"唯一通道"场景下行为不变；多通道场景下它们默认作用于 suite 声明的第一个通道（兜底，validator 会 warn）。

---

## 2. Current State（证据 —— 五层单通道约束）

| # | 层 | 单通道约束 | 证据 |
|---|---|---|---|
| 1 | 契约 | `IAssertionContext.SendFrameAsync(CanFrame, ct)` 无通道参数；`SubscribeDecodedFrames` 单一解码帧流 | `PeakCan.Host.Core/HIL/Contracts/IAssertionContext.cs:39` |
| 2 | 执行器 | `IStepExecutor.ExecuteAsync(step, ctx, ct)` 三参签名无通道；`SendFrameStepExecutor.cs:20` 构造 `CanFrame(p.Id, payload, flags, default, default)` channel 写死 `default`(None) | `PeakCan.Host.Core/HIL/StepExecutor/SendFrameStepExecutor.cs:20` |
| 3 | DI | `HeadlessHostBuilder` 只注册一个 `ICanChannel` 单例；7 处消费全 `GetRequiredService<ICanChannel>()`（BackgroundFrameSender/PeakCanAssertionContext/HILAssertionContext/FrameStatisticsCollector/IsoTpLayer/HilIsoTpBridge/HilRunnerService） | `HeadlessHostBuilder.cs:40-82,103-160` |
| 4 | run 配置 | `HilRunRequest` 单个 `HardwareChannel`(string?) / 单个 `UdsRequestId`/`UdsResponseId` | `PeakCan.Host.Core/HIL/HilRunRequest.cs:7-9` |
| 5 | 监控桶 | `AssertionPrimitives` 全基于单一 `_ctx` 订阅；`FrameStatisticsCollector` 单通道全局聚合 | `PeakCan.Host.Core/HIL/Assertions/AssertionPrimitives.cs:10-12`；`PeakCan.Host.Infrastructure/Statistics/BusStatisticsCollector.cs:47-59` |

### 链路末端缺口（调查发现，原设计漏点）

| 链路段 | 现状 | 多通道缺口 |
|---|---|---|
| case log | `AscFrameSink.Write` → `AscFileFormat.WriteFrameLine` 输出 `{seconds} 1 {id}x Rx d {dlc} {data}` | **第 2 字段硬编码 `1`**（`AscFileFormat.cs:24`），完全没用 `frame.Channel` → 两路帧混进一个 .asc 全标 channel 1，无法区分 |
| 报告 | `HtmlReportGenerator.RenderCase(c, dbc)` 收**单个** `DbcDocument` 解码失败帧；`StepResult` 无 channel 字段 | 失败帧块无法标通道；多通道下按哪个 DBC 解码？`StepResult` 失败信息不含通道 |
| DBC | `DbcDocument` 只有 `Messages`+`Nodes`（strings 证实无 `Networks`）—— **DBC 文件标准即单网络** | 每通道隶属一个网络 → 每通道必须独立 DBC，现 DI 只注册单个 `DbcDocument`+`IDbcLookup` |

### 代码归属（决定改动是否触发跨仓库 bump）

| 层 | 位置 | 内容 |
|---|---|---|
| 实体数据模型 | **hil-core NuGet 包**（0.11.0，`D:\nuget-local`，host+studio 双消费） | `CanFrame`/`ChannelId`/`BaudRate`/`FrameFlags`/`TestCaseStep`/`SendFrameStep`/`ExpectFrameStep`/`TestCaseStepKind`/`TestSuite`/`TestSuiteResult`/`StepResult`/`TestCaseStepJsonConverter` + 9 接口 |
| HIL 引擎 | **host 本地** `src\PeakCan.Host.Core`（命名空间沿用 `PeakCan.HIL.Core`） | `IStepExecutor`/`IAssertionContext`/`AssertionPrimitives`/`TestSuiteEngine`/全部 executor/`HilRunRequest`/validators/expressions/`IFrameStatistics`/`IHilFrameSink`/`IHasFrameSink` |
| 基础设施 | **host 本地** `src\PeakCan.Host.Infrastructure` | `PeakCanAssertionContext`/`HILAssertionContext`/`FrameStatisticsCollector`/`BackgroundFrameSender`/`HeadlessHostBuilder`/`AscFrameSink`/`AscFileFormat`/`HilReportService`/`HtmlReportGenerator`(CLI Reporting) |

> **关键**：改包内数据模型 = 触发 host+studio 双仓库 bump + pack/feed 序列（吸取 0.5.1 vs 0.6.0 pin 漂移教训，Q4）；改 host 本地层 = 单仓库即可。

---

## 3. 设计

### 3.1 hil-core 包改动（0.11.0 → 0.12.0，触发双仓库 bump）

**全部新字段可空、缺省=单通道兼容，旧 JSON 无障碍读取。**

```
// TestSuite 顶层新增
TestSuite.Channels : List<ChannelConfig>?     // null/空 = 单通道默认
ChannelConfig {
    string Name;          // 逻辑别名，如 "bus-a" / "bus-b"
    string Handle;        // 通道句柄（hex，如 "51" / "C600"）
    BaudRate? BaudRate;    // 波特率预设；null = suite 默认
    bool Fd;               // CAN FD 模式
    string? DbcPath;       // 该通道独立 DBC（Q8：DBC 单网络，每通道必独立）
    uint? UdsRequestId;   // per-channel UDS 覆盖（null = 全局默认）
    uint? UdsResponseId;
}

// 6 类步骤加 TargetChannel（string?，null/""=唯一/默认通道）
SendFrameStep.TargetChannel
SendSequenceStep.TargetChannel
ExpectFrameStep.TargetChannel
AssertNoFrameStep.TargetChannel
AssertFrameCountStep.TargetChannel
AssertCycleTimeStep.TargetChannel

// 结果带通道（Q6）
StepResult.Channel : string?    // 执行该步骤的通道别名；null = 默认
```

`TestCaseStepJsonConverter` + `TestSuite`/`StepResult` 的 JSON 序列化都要支持新字段；旧 JSON 缺这些字段时反序列化为 null（单通道兼容）。

### 3.2 引擎契约演进（host `PeakCan.Host.Core`，无需 bump）

**重构点 2（Q 决策）**：`IAssertionContext` 本身就在 host 本地，直接演进（不引入 `IMultiChannelAssertionContext` 继承+强转）：

```csharp
public interface IAssertionContext
{
    // 原有保留（默认实现转发到 channelName=null）
    IDisposable SubscribeDecodedFrames(Action<DecodedFrame> onFrame);
    double? GetSignalValue(string signalName, int maxAgeMs = 5000);
    double CurrentTimestamp { get; }
    ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct);
    IReadOnlyList<DecodedFrame> GetRecentDecodedFrames();

    // 新增：按通道（channelName null/空 = 唯一/默认通道，兼容单通道）
    ValueTask<Result<Unit>> SendFrameAsync(string? channelName, CanFrame frame, CancellationToken ct);
    IDisposable SubscribeDecodedFrames(string? channelName, Action<DecodedFrame> onFrame);
    IReadOnlyList<DecodedFrame> GetRecentDecodedFrames(string? channelName);
}
```

**重构点 4**：`IFrameStatistics`（host 本地）分桶：

```csharp
public interface IFrameStatistics
{
    // 原有保留（channelName null = 默认）
    long Now { get; }
    long CountSince(long sinceTicks, string? channelName = null);
    IntervalStats GetIntervalStats(long sinceTicks, string? channelName = null);
}
```

`IHasFrameSink.SetFrameSink` 多挂（Q5 合并方案）：`SetFrameSink(string? channelName, IHilFrameSink? sink)`，context 内部把同一 sink 接到所有通道的 consumer（每通道帧都写进同一 .asc，靠 asc 行 channel 列区分）。

### 3.3 引擎实现（host `PeakCan.Host.Core`）

**重构点 1（Q7）—— `PeakCanAssertionContext` 组合化**：

现状 `PeakCanAssertionContext` 230 行、6 重职责（帧缓冲队列+信号缓存+DBC 解码 consumer+recent frames+sink 挂载+variables）。多通道分桶若在它内部加 N 份桶 → 12 重职责怪物。

重构为：

```
SingleChannelContext          // 提取自 PeakCanAssertionContext，保留 6 职责，单通道语义不变
  : IAssertionContext (单通道视图，channelName 忽略/校验)
  + IHasRecentFrames + IStepVariableStore + IHasFrameSink + IDisposable

MultiChannelAssertionContext  // 新，组合 N 个 SingleChannelContext
  : IAssertionContext (按 channelName 路由到对应 SingleChannelContext)
  + 路由层：channelName → SingleChannelContext 字典
  + 默认通道 = suite.Channels[0] 或唯一通道
```

- `AssertionPrimitives` 加按通道重载：`WaitForFrameAsync(CanId, byte[]?, int, string? channelName, ct)`、`AssertSignal(..., string? channelName)` 等；原签名转发到 `channelName=null`。
- executor 路由：
  - `SendFrameStepExecutor`/`SendSequenceStepExecutor`：`CanFrame` 构造不再 `default` channel —— 传 `ctx.ResolveChannelId(p.TargetChannel)` 得到正确 `ChannelId`；`StepResult.Channel = p.TargetChannel ?? default`
  - `ExpectFrameStepExecutor`/`AssertNoFrame`/`AssertFrameCount`/`AssertCycleTime`：调 `AssertionPrimitives` 按通道重载，`StepResult.Channel` 记录
- `TestSuiteEngine`：case 边界清 variables 照旧；新增前置 validator —— "suite 未声明 `Channels` + 步骤带 `TargetChannel`" → 报错（Q3）；"引用未声明的通道名" → 报错。
- `HilRunRequest` 加 `HardwareChannels : IReadOnlyList<ChannelConfig>?`（null = 旧单通道 `HardwareChannel` 路径不变）。

### 3.4 基础设施（host `PeakCan.Host.Infrastructure`，无需 bump）

- **DI**：`ICanChannel` 单例 → `IReadOnlyDictionary<string, ICanChannel>`（按逻辑名）；`HeadlessHostBuilder` 硬件模式从 `HilRunRequest.HardwareChannels` 逐项经 `CompositeChannelFactory` 打开（PEAK/ZLG 按 handle 路由）。
- **DBC**：`DbcDocument` 单例 → `Dictionary<string, DbcDocument>`；`IDbcLookup` 每通道一个 `HeadlessDbcLookup`；`MultiChannelAssertionContext` 每个 `SingleChannelContext` 注入自己的 `IDbcLookup`（天然隔离，Q8）。
- **FrameStatistics**：`FrameStatisticsCollector` 单通道 → 每通道一个实例，`IFrameStatistics` 实现按 name 字典路由。
- **UDS**：`UdsRequestId/ResponseId` 保留全局默认，`ChannelConfig` 可 per-channel 覆盖；`HilIsoTpBridge`/`IsoTpLayer` 本期只挂默认通道（UDS 多通道另立项，§1 范围界定）。
- **context 实现**：`MultiChannelAssertionContext` + `SingleChannelContext`（§3.3）；单通道场景 `HeadlessHostBuilder` 仍可构造单 `SingleChannelContext`（零回归）。

### 3.5 case log（host `PeakCan.Host.Infrastructure`）

`AscFileFormat.WriteFrameLine` 改（Q5 合并）：

```csharp
// 现：{seconds,12:F6} 1  {id,-12}x Rx d {dlc} {data}   ← channel 硬编码 1
// 改：{seconds,12:F6} {channelNum}  {id,-12}x Rx d {dlc} {data}  ← 用 frame.Channel
```

`frame.Channel.Handle` 映射到 PEAK asc 的 channel 号（PEAK 0x51→1、0x52→2；ZLG 按枚举顺序分配 ≥3）。`AscFileSink` 单 sink 接所有通道 consumer（`MultiChannelAssertionContext` fan-out）。一个 case 一个 .asc，多路帧合并，channel 列区分。

### 3.6 报告（host `PeakCan.Host.Infrastructure` CLI Reporting）

- `HtmlReportGenerator.RenderCase(c, dbc)` → `RenderCase(c, IReadOnlyDictionary<string, DbcDocument>? dbcs)`：失败附近帧按 `frame.Channel` 选对应 `DbcDocument` 解码（Q8）。
- 帧块标 channel（渲染 `channel: bus-a` 标签）。
- `StepResult.Channel` 在步骤结果行展示（如"通道 B 上等待帧 0x123 超时"）。
- summary 保持 suite 级（趋势不含 channel，可接受）。
- `HilReportService.Generate(TestSuiteResult, DbcDocument?)` → `Generate(TestSuiteResult, IReadOnlyDictionary<string, DbcDocument>?)`。

### 3.7 UI

- **host `HilView`/`HilViewModel`**：run 配置区从"单个通道文本框"扩展为"通道列表编辑器"（名称+句柄+波特率+FD+DBC 路径，≥1 项）；`HardwareChannel` 字符串 → `HardwareChannels` 列表构造 `HilRunRequest`。
- **studio**（单边）：`EditableTestCaseStep` 透传 `TargetChannel`；6 个步骤描述符加 TargetChannel ComboBox（选项来自 suite 的 `Channels` 声明，离线时可选"默认通道"）；Copilot 模板同步；`StepValidatorRegistry` 加通道引用校验。

---

## 4. 实施顺序与仓库序列（吸取 cross-repo 教训显式写死）

1. **hil-core**：加字段（`ChannelConfig`/`TargetChannel`/`StepResult.Channel`）+ converter + 序列化兼容测试 → `0.12.0` → pack 到 `D:\nuget-local`。
2. **host + studio 两侧同时** bump `Directory.Packages.props` → restore（Q4：先核对当前 pin，避免漂移）。
3. **host 引擎**（§3.2/3.3/3.4）+ 单通道回归测试全绿（零回归是硬约束）。
4. **host case log + 报告**（§3.5/3.6）。
5. **studio UI**（§3.7）。
6. **端到端**：双通道 loopback 用例（A 路发帧 → B 路 `ExpectFrame` 监控 → 报告/case log 标通道）验证。

---

## 5. 决策点（用户 2026-08-21 确认）

| Q | 决策 | 理由 |
|---|---|---|
| Q1 | MVP 6 类步骤（发送+帧监控）；信号断言/UDS 多通道后置 | 信号断言依赖 per-channel DBC 语义，UDS 绑 per-channel 配置，复杂度另立项 |
| Q2 | AppShell 多通道另立项，本期不阻塞 | HIL 独立 host 链路天然与 AppShell 单连接解耦 |
| Q3 | 旧 suite 无 `Channels` + 步骤带 `TargetChannel` → validator 报错 | 防手误，文档说明 |
| Q4 | 动手前核对 host/studio 双仓库当前 pin 版本 | 吸取 0.5.1 vs 0.6.0 漂移教训 |
| Q5 | case log 一个 .asc 合并两路 + channel 列 | 符合 PEAK asc 惯例，工程师一文件看全貌 |
| Q6 | `StepResult` 加包内 `Channel` 字段（结构化） | 报告/diff/studio 可结构化展示，非塞 message 字符串 |
| Q7 | `PeakCanAssertionContext` 重构为 `SingleChannelContext`+`MultiChannelAssertionContext` 组合 | 避免单类 12 重职责怪物，单通道零回归 |
| Q8 | 每通道独立 DBC（DBC 标准单网络，strings 证实 `DbcDocument` 无 `Networks`） | 每通道隶属一个网络 → 每通道必独立 DBC，`ChannelConfig` 加 `DbcPath` |

---

## 6. 风险

| 风险 | 缓解 |
|---|---|
| hil-core 双消费仓库 pin 漂移 | §4 步骤 2 显式双 bump；Q4 先核对 |
| `PeakCanAssertionContext` 重构破坏单通道行为 | `SingleChannelContext` 提取为行为不变的纯搬迁；单通道回归测试先行（§4 步骤 3） |
| asc channel 号映射（PEAK 0x51→1、ZLG 分配）需稳定 | 用 `ChannelId` 到 asc channel 号的显式映射表，单测覆盖 |
| 多通道并发打开同一物理设备（HIL + AppShell 同时） | ZLG 侧 `ZlgDeviceManager` 引用计数已支持；PEAK 侧 `ConnectFlow` 并发行为待查（开放项） |
| UDS 步骤多通道场景兜底语义（作用于首通道） | validator warn；UDS 多通道另立项 |

---

## 7. 开放问题（实施时确认）

1. PEAK 侧 `PeakCanChannel/ConnectFlow.partial.cs` 同进程同通道并发 `Initialize` 行为（HIL+AppShell 共享物理设备时）。
2. asc channel 号映射表的 ZLG 分配规则（devIdx/canIdx → asc channel 号）。
3. studio 当前 pin hil-core 版本（Q4 核对项，动手第一步）。
