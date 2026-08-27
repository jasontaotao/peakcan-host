# Design: HIL 引擎还债与时间窗断言（多通道 UDS 收尾 + AssertSignalWithin/AssertStable）

> Spec date: 2026-08-27
> Depends: hil-core 0.13.0（`ChannelConfig.UdsRequestId/UdsResponseId` 已定义）、2026-08-21-hil-multichannel-design.md（阶段一已 ship v3.65.0）、TestSuiteEngine 解释器（v11 H1 单路径）
> Scope: 三件事 —— (A) 解释器边界分支补测（已完成，待回归）；(B) 多通道 UDS 执行链接线（multichannel spec 明确"另立项"的正主）；(C) 信号维度时间窗断言（hil-core 0.14.0 bump）。
> Status: DRAFT——§2 内有 4 个决策点（Q1–Q4）需确认后才动工程。
> Review: 2026-08-27 双视角复审（架构师+PM）修订——Task B 确认同样拖 hil-core 冻结面（UDS step record 无 TargetChannel 字段），B+C 合并同车 0.14.0；补充样本定义消歧与 engine 层 UDS 依赖（详见 §0/§6）。
> Trigger: HIL 引擎能力横向对比审计（本 session）后的优先序：还债 > 断言表达力 > 编排增强。

---

## 0. 审计修正记录（防后续 session 再误判）

本 spec 成形过程中推翻了三个此前的判断，记录如下：

| 曾以为 | 实际 | 证据 |
|---|---|---|
| TestSuiteEngine 主循环无覆盖测试 | If/Repeat Fixed+While 正常路径/Loop 端点/StopCase 三条传播/golden 语义基线全覆盖；codegraph "no covering tests" 是私有方法间接调用链漏报 | `tests/PeakCan.Host.Core.Tests/HIL/TestSuiteEngineInterpreterTests.cs`（37KB 全文核对） |
| 缺 setup/teardown 语义 | suite/case 两级 fixture 已有（SetupAsync/TeardownAsync 倒序）+ `ContinueAfterSetupFailure` 配置 | `TestSuiteEngine.cs:65-103,133-145` |
| 缺周期一致性断言 | `AssertCycleTime` 已落地且质量高：逐区间判定（无均值掩盖）、MinSamples、per-channel 路由；另有 `AssertResponseTime` | `AssertCycleTimeStepExecutor.cs` + `FrameStatisticsExecutorTests.cs` |
| Task B "纯接线、无需 bump"（2026-08-27 复审推翻） | UDS 类 step record（ReadDidStep 等）没有 TargetChannel 字段——加字段+JSON converter+校验 switch 扩展 = 冻结面变更，必须 bump | `StepValidatorRegistry.TryGetTargetChannel` switch 仅认 SendFrame/ExpectFrame/AssertNoFrame/AssertFrameCount/AssertCycleTime 五类，注释自证"仅 5 个 MVP 帧步骤类型有此字段" |
| Q1 前提 "executor 统一吃 IUdsSession"（2026-08-27 复审推翻） | 依赖形态混杂：AssertDtc 吃接口 IUdsSession（仅 2 方法），ReadDid/WriteDid 吃 concrete UdsClient | `AssertDtcStepExecutor.cs:10-12` vs `ReadDidStepExecutor.cs:13-15` / `WriteDidStepExecutor.cs:11-13` |

**教训**：优化清单必须先过一遍该 feature 域的既有 spec 目录与本仓库命名相近的 executor 列表，再做结论。

## 1. Task A — 解释器边界分支补测 ✅✅（2026-08-27 回归绿灯）

新增 `tests/PeakCan.Host.Core.Tests/HIL/TestSuiteEngineInterpreterBoundaryTests.cs`，9 用例钉住四个此前无锚定的错误分支：

1. Repeat While 守卫恒真 → "did not converge within MaxIterations"（body 每轮真执行）
2. Repeat MaxIterations 非法值 ×3（非数字 / 0 / 负数）→ 先于 body 失败
3. Repeat Count 引用 undefined → 不进入迭代
4. Loop step ≤ 0 ×2 / From undefined / 空 range（from>to 静默零执行，文档化现行为）

断言口径：容器 Status + body 执行次数 + 消息关键词；依赖求值器内部判定的消息用 FluentAssertions `Match` 通配双兼容。
已知风险一条：`(got -1)` 用例赌 `"0 - 1"` 可求值（Assign 用过加法、减法未证实）——红了换字面量形式即可。
✅ 回归实证（2026-08-27）：过滤 `FullyQualifiedName~TestSuiteEngineInterpreter` 跑 27 通过/1 跳过（RegenerateGoldenBaseline 为刻意 golden 基线跳过）/0 失败；边界类 9 用例逐条绿，含风险用例 `stepExpr: "0 - 1", expectedFragment: "(got -1)"`——求值器减法实证可用，风险解除，无需换字面量。Task A 关闭。

## 2. Task B — 多通道 UDS 执行链接线（Phase II）

### 2.1 现状（证据，2026-08-27 复核修订）

- **suite/通道侧数据模型已完成**：hil-core 0.13.0 `ChannelConfig` 带 `UdsRequestId/UdsResponseId` 可空字段（multichannel spec §3.1 兑现）。⚠️ 当前填了没有执行链消费、也无校验警告——**已发布的死配置**，这是本任务的产品紧迫性来源（用户配了没反应会怀疑自己拓扑配错，比缺功能更伤口碑）。
- **step 侧路由字段缺失**：`TargetChannel` 只存在于 5 个 MVP 帧步骤 record；ReadDid/WriteDid/RoutineControl/SessionControl/DTC 系 step record 均无此字段。补齐 = per-type property + JsonConverter 分支 + 校验 switch 扩展 = **hil-core 冻结面变更**。
- **executor 依赖形态混杂**：`AssertDtcStepExecutor` 依赖接口 `IUdsSession`（仅 ReadDtcInformation/SendRequestAsync 两方法）；`ReadDidStepExecutor`/`WriteDidStepExecutor` 直接依赖 concrete `UdsClient`。resolver 化前必须先统一抽象（见 Q1 第一步），否则 resolver 的返回类型无处安放。
- **engine 层第三条 UDS 依赖**：`TestSuiteEngine` ctor 可选参数 `Contracts.IUdsSession? uds`（TestSuiteEngine.cs:30），用于 case 开始预查 active DTC 填 `dtcPresentSet` → 表达式函数 `dtcPresent()`。该函数无 channel 参数，多通道下语义未定义（处置见 Q4）。
- 执行链路仍单套：一条 `IsoTpLayer → UdsClient` 链被全部 UDS 类 executor 共享。
- 后果：多通道 suite 中只有第一路通道上的 ECU 可做诊断；第二路的帧能看（per-channel DBC 已通）但不能读写 DID/Routine/DTC。

### 2.2 目标行为

- suite `Channels[].UdsRequestId/UdsResponseId` 非空的通道，各获得独立 UDS 栈（独立 IsoTp 过滤 ID、独立安全访问锁状态机）；
- UDS 类步骤（ReadDid/WriteDid/RoutineControl/SessionControl/SecurityAccess 相关/AssertDtc/AssertNrc/ECUReset/IOControl/ClearDtc —— 2026-08-27 实核修正：原列的 ClearFault 是 fault-injection（IFaultInjectionContext），不走 UDS 栈，从本清单剔除）经 `TargetChannel`（复用阶段一字段语义）路由到对应通道的 `IUdsSession`；
- `TargetChannel = null` → 全局默认 UDS 栈（现状），旧 suite 零变化；
- 表达式函数 `dtcPresent()`：v1 维持全局默认栈语义并在 case 报告附注（不给表达式语言加 channel 参数——复杂度不成比例；per-channel 版本等真实需求立项）；
- 报告 `StepResult.Channel` 带路由结果（✅ 已核 2026-08-27：字段已在 hil-core `StepResult.cs` 末位可选参数 `string? Channel = null`，语义 = 通道名字符串；现状 executor 构造仅传至 ElapsedMs → 恒 null。实现 = executor 按 TargetChannel 解析结果填充该参数，record 本体不动、无额外冻结面）。

### 2.3 决策点（实施前必须拍板）

- **Q1 注入形态（两步走）**：第一步统一 executor 抽象——扩大 `IUdsSession` 接口覆盖 ReadDataByIdentifierAsync/WriteDataByIdentifierAsync 等（adapter 包 `UdsClient`），把 ReadDid/WriteDid 从 concrete `UdsClient` 迁到接口；第二步再引入 `IUdsSessionResolver { IUdsSession Resolve(string? channelName) }` 替换各 executor ctor 固定实例。
  （✅ 接口归属已核：`IUdsSession` 本体在 **host 仓** `PeakCan.Host.Core/HIL/Contracts/IUdsSession.cs`，仅 ReadDtcInformation/SendRequestAsync 两方法——namespace 与 hil-core 同名但程序集不同；扩接口不碰 hil-core 冻结面，“第一步纯 host 侧”成立。adapter `UdsSessionAdapter(client)` 已存在于 HeadlessHostBuilder.RegisterUdsServices。
  倾向 resolver 而非扩展 `IAssertionContext`：改动局限在 executor 构造与注册处，ctx 契约不被多通道细节污染。
- **Q2 锁状态隔离**：每通道独立 `UdsSecurityState`（30s 安全访问延迟 per ECU 天然隔离）。确认无需跨通道共享。
- **Q3 步骤级并发**：本期明确**串行**——同一时刻只有一个 UDS 步骤在途（引擎本就是单路径递归解释器），不引入 per-channel 并发诊断（总线仲裁 + 共享变量存储都会变成正确性雷区）。
- **Q4 engine 层 uds 参数**：`TestSuiteEngine` ctor 的 `IUdsSession? uds`（active DTC 预查用）保持绑默认栈，还是也改经 resolver？倾向：本期保持默认栈（与 `dtcPresent()` v1 策略一致），不为它新增 ctor 参数；未来 per-channel 化时让 engine 注入同一个 resolver，不开新口子。

### 2.4 校验器

- `TryGetTargetChannel` pattern-match switch 扩展：5 类 → 全部 UDS/DTC 步骤类型（MC-1/MC-2 校验自动覆盖新类型）；
- `TargetChannel` 指向的通道无 UDS ID 配置 → High issue（仿照已有的通道引用校验分级）；
- 同通道配置 RequestId == ResponseId → High；
- 与其他通道 UDS ID 冲突 → Medium。

### 2.5 验收

- 双通道 VirtualEcu 集成测试（仿 `DualChannelLoopbackE2E.cs`）：bus-a 挂虚拟 ECU-A（DID F190=AAA），bus-b 挂 ECU-B（F190=BBB），同 case 内分别 ReadDid 且互不串扰；
- 单通道旧 suite 全量回归不变绿变红数量 = 0。

## 3. Task C — 信号维度时间窗断言（AssertSignalWithin / AssertStable）

### 3.1 与现有兄弟步骤的分工（避免重复造轮子）

| 已有 | 语义 | 维度 |
|---|---|---|
| `WaitForSignal` | 等到首份满足条件的解码样本即返回 | 出现型 |
| `ExpectFrame` | 等 raw 帧 | 帧 |
| `AssertCycleTime` | 窗口内帧间隔统计 | 帧 |
| **缺** | **信号值随时间的集合语义** | 信号×时间 |

### 3.2 两个新 step kind（hil-core 0.14.0）

```
AssertSignalWithinStep {
    string SignalName;        // "Message.Signal"
    string Expected;          // ${} 可插值（B.5 惯例）
    string Tolerance;
    string WindowMs;
    MatchMode Mode;           // Any: 窗口内≥1个样本命中 | All: 全部样本命中
}
AssertStableStep {
    string SignalName;
    string WindowMs;          // 观察窗
    string MaxDelta;          // 窗口内 max-min ≤ 此值判稳定
    string MinSamples;        // 样本下限（不足即 Failed，学 AssertCycleTime L38）
}
```

### 3.3 执行模型

对齐 `AssertCycleTime` 的窗口结构：`since = ctx.CurrentTimestamp` → 订阅 `SubscribeDecodedFrames(targetChannel)` 收集窗口样本 → `Task.Delay(WindowMs)` → 集合判定 → detach。

**样本定义（消歧裁决，2026-08-27 复审补）**：样本值 = 回调时刻 `ctx.GetSignalValue(SignalName)` 的缓存快照（与既有 `WaitForSignalAsync` 同一取值口径），**不是**逐帧重新解码——不给 `IAssertionContext` 冻结面扩新 API。取舍记录：无关帧触发多余快照检查无害（集合判定天然幂等）；报文整体缺失时快照为 null → 不计入样本，由下方零样本规则与 `MinSamples` 兜底。逐帧级精度如将来需要，另立任务扩 ctx 契约。

**零样本规则**：`MatchMode.All` 且窗口内零有效样本 → Failed（防空窗口 vacuous pass：不在线的总线不应该让 All 断言变绿）；`MatchMode.Any` 零命中自然 Failed，无需额外规则。
Any 模式修的是现有瞬时快照断言的 flaky 根因（总线调度抖动让恰好一拍的 AssertSignal 必然间歇失败）；All 模式 + AssertStable 覆盖"持续保持"类需求（如模式切换后转速回落稳定）。

### 3.4 冻结面影响（重要成本项）

Task B（UDS step record 补 TargetChannel）与 Task C（新增 `TestCaseStepKind` 成员 + JsonConverter 分支 + step record）**都是冻结面变更 → 合并同车上 hil-core 0.13.0 → 0.14.0**：双仓 lockstep 只走一次发布链，studio `InteropTests` 一轮同步（0.11→0.12 pin 漂移事故是先例；两次 lockstep 成本翻倍，不合车的理由不存在）。validator 新增：SignalName 存在于目标通道 per-channel DBC、WindowMs>0、MaxDelta/Tolerance 数值合法。

## 4. Non-goals（本期明确不做）

- case 并行执行：共享总线介质，并行必然制造不可复现的总线竞争假失败；
- setup/teardown 增强：fixture 体系已够用，等真实痛点；
- While/Loop 增加 break 语句（控制流复杂度换收益不明）；JS 节点仿真（另一条线：CAPL-lite spec，待起草）。

## 5. 执行顺序（含进度）

- ✅ A 回归绿灯（2026-08-27，commit 0c859bc：9 边界用例全绿，风险用例减法实证可用）
- ✅ B 第一步接口统一（2026-08-27，commit 37d821c：IUdsSession +DID 两方法；UdsSessionAdapter 迁入 host Core；ReadDid/WriteDid executor 接口化；Core.Tests 924/924 + Infra.Tests 594/594 绿；review APPROVE，MEDIUM 双重前缀已修）
- ✅ hil-core 0.14.0 打包（2026-08-27，commit 98e2bc5：11 个 UDS step record 补 TargetChannel + MatchMode/AssertSignalWithin/AssertStable 两 step kind + [JsonDerivedType] 注册 + Factory/Exporter 对称 + SignalName 校验/引用收集对齐；hil-core.Tests 255/255 绿；review APPROVE——MEDIUM ReferenceCollector 盲区已修，LOW×2 测试已补。测试修复 2 处：dynamic+FluentAssertions 改显式 switch、IReadOnlyDictionary 赋值包 new Dictionary）
- → host/studio lockstep 升级落地后做 B 第二步 resolver 接线 + C 两个 executor 实现。
- ⏳ 留待 lockstep 阶段：§3.4 validator 新增的 per-channel DBC 信号名路由 + WindowMs>0 / MaxDelta/Tolerance 数值合法性校验（依赖 per-channel DBC lookup 上下文，与 host 侧 DBC 路由一起做）

原序：A 回归绿灯 → B 第一步接口统一（扩 `IUdsSession` + executor 迁移，纯 host 侧）→ hil-core 0.14.0 打包（B 的 step 字段 + C 的新 step kind 同车）→ host/studio lockstep 升级落地后做 B 第二步 resolver 接线 + C 两个 executor 实现。

## 6. 实施前待核清单（✅ 两项已核，2026-08-27 续 session）

- ✅ **`StepResult.Channel`**：字段已在 hil-core（`peakcan-hil-core/src/PeakCan.HIL.Core/HIL/StepResult.cs` 尾部可选参数），尾部注释自证 "通道标识：步骤执行在哪个 CAN 通道上（如 bus-a/bus-b）"；现有 executor 构造仅传至 ElapsedMs → 现状恒 null。Task B 无需改 record，各 UDS executor 填充即可。
- ✅ **suite `Channels[].UdsRequestId/UdsResponseId` 消费状况**：确认零消费。原问句中 "BuildUdsAsync" 名称不存在，实际对应物是 `HeadlessHostBuilder.RegisterUdsServices` —— 其 IsoTpLayer 工厂吃的是 CLI request 层 `args.UdsRequestId/UdsResponseId`（顶层单一 UDS 栈），而非 per-channel 字段；多通道路径注册处注释自证 "UDS multi-channel is deferred (§3.4): IsoTpLayer/UdsClient bind to default"。§2.1 "死配置" 表述成立且更精确：per-channel UDS ID 与实际生效的 request 层 ID 是两个互不相干的来源，前者从未被读取。

两项结论均不与本 spec 冲突（反而强化 §2.1 紧迫性论证），其余断言无需回改。
