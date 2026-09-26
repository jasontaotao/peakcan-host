# PeakCan S6 — 回放 UI / 曲线 / MAP 可视化 设计 v0.1（草稿）

- 日期：2026-09-26
- 状态：**v0.2 已定案（2026-09-26，D1–D7 全按推荐拍板）**
- 上游：S1 spec（`a2l-editor/docs/superpowers/specs/2026-09-20-peakcan-asap2-package-design.md`）+ S4 spec（MDF 记录）+ S5 spec（标定写回）
- 落地仓库：`peakcan-host`，分支 `s6-visualization-replay`

## 0. 范围

S6 是 S1–S6 主线的最后一段（"A2L 变体 = 基线 + delta"为 S6 之后的新立项）。S6 把 S4 产出的记录文件变成可看的，把 S1/S5 的数据变成可读的图形：

1. **回放 UI 与曲线渲染**：加载 MF4（主记录 + 触发记录文件）→ 通道曲线绘制、缩放/游标、空窗呈现。
2. **MAP/3D 只读可视化**：15 个 MAP 对象的曲面/热图只读渲染（S1 §2.1 钉子：编辑走表格，3D 只做只读可视化）。
3. **参数集 diff 可视化**：S5 `CalibrationParameterSet` 的两份文件对比展示（S5 非目标划给 S6 的尾巴）。
4. **多段对象写扩展**：S5 附录 C-4 挂账——MAP/部分 CURVE 的跨段写编排（是否进 S6 → D6）。

**非目标**：3D 曲面编辑（S1 钉死）、A2L 变体基线+delta（S1 四钉钉在"S6 之后"）、通用 MDF 生态兼容（只回放自家 writer 产物 → D1 拍板确认）、在线实时示波器（采集态已有归因合并表，S3 口径）、视频/ADAS 叠加、微秒级时间戳同步（S1 不做清单）。

## 1. 上游钉子（原文回执）

| 钉子 | 出处 | 对 S6 的约束 |
| --- | --- | --- |
| 回放 UI 与曲线渲染不在 S4 | S4 spec 非目标行 | 本 spec 的第 1 块 |
| MDF 读取"回放阶段再定" | S4 spec 非目标行 | D1 决策点 |
| 记录文件只存原始值，换算定义走版本化快照 | S1 增补 A1（§5.3） | 回放必须绑 A2L 指纹或内嵌快照（S4 已有 snapshot attachment 自包含先例） |
| 空窗画成空窗，不连线 | S1 A1 / S4 备选记录 | 回放渲染必须消费 invalidation bits + 归因事件条目 |
| gap 时间戳 = OnGap 到达时刻 | S4 附录 C-1 | 空窗区间按到达序，非协议时刻；UI 提示口径沿用 |
| 3D 只读、编辑走表格 | S1 §2.1 不做清单 | MAP 可视化禁止提供写路径 |
| 参数集明文可 diff | S1 四钉之一（S5 交付） | diff 可视化的输入格式已存在，不新造 |
| 多段对象 v0.1 不支持，S6 扩展跨段编排 | S5 附录 C-4 | D6 决策点 |
| 通道 unit 有 asammdf 显示层差异 | S4 附录 C-3 | 自研读取则此差异天然消除（D1 佐证） |

## 2. D 决策点（v0.2 已定案）

### D1：MDF 读取方案

- **拍板：自研最小 MF4 读取器**（Core 层，只覆盖自家 `Mdf4StreamWriter` 的子集：ID/HD/DG/CG/SI/DT/SD/事件/invalidation bits/attachment）。理由：与 S4 writer 形成 round-trip 闭环测试；零外部依赖零许可风险；S4 附录 C-2/C-3 的 asammdf 显示层差异天然不引入。代价：只读自家文件，通用 MDF 支持另立项。
- 备选 A：NuGet MDF 库（Mdf4Lib 等）——通用性好，但引入依赖 + 行为不受控 + 许可审查。
- 备选 B：asammdf 导出 CSV 再读——UX 割裂，违背"加载即看"。

### D2：曲线渲染技术选型

- **拍板：ScottPlot 5（WPF 控件）**。理由：MIT 许可、百万点级性能、缩放/游标/图例/多轴现成；WPF 绑定成熟。代价：新 NuGet 依赖（第一个图表库）。
- 备选：WPF 自绘 StreamGeometry——零依赖，但缩放/游标/图例全自造，工作量数倍于引入库。

### D3：回放数据口径

- 输入 = S4 主记录 MF4 + 触发记录 MF4（同一读取器）。
- 通道↔A2L 对象映射按通道名（S4 写入口径）+ A2L 指纹校验：指纹不符时降级为"原始值曲线"并警示，物理值换算拒绝（沿用 S5 `EnsureMatches` 语义）。
- 物理值换算走包侧 `ValueContract.Decode`；无换算快照时提示加载 A2L。
- 空窗渲染 = invalidation bits 置位区间断线 + 归因事件条目在时间轴上标注（MissingCause 五值 + AcquisitionInterrupted）。

### D4：MAP/3D 可视化数据源

- **拍板：在线 UPLOAD 读值渲染**——连接 ECU 后选 MAP 对象，UPLOAD 拉全元素（读不涉及 S5 写门禁），按两轴点数构造网格，曲面/热图只读渲染 + 当前值游标。
- 备选：离线渲染参数集/A2L 静态值——无连接可用，但"可视化看 ECU 现状"的场景价值低。
- 3D 控件：跟随 D2——ScottPlot 有 heatmap/contour，够只读用，不引 3D 专用库。

### D5：参数集 diff 可视化形态

- **拍板：双文件表格 diff**（对象名 / 原值 / 新值 / 单位 / 上下限 / 越限标红）+ 选中行定位到 XcpView 对象卡片。只读展示；"应用"仍走 S5 既有 apply 链路，diff 面不新增写路径。
- 不做：树状结构 diff、三方合并（参数集是每对象一行的平面 JSON，表格够用）。

### D6：多段写扩展是否进 S6

- **拍板：进，作为独立任务（S6 末位）**。S5 附录 C-4 钉了"S6 扩展跨段编排"；MAP 15 个对象是 BMS 日常曲线的家。范围：`XcpCalibrationWriter` 按 `MEMORY_SEGMENT` 地址映射拆分写序列 + 回读比对分段执行；reconciler/卡片门禁同步解除 MultiSegmentUnsupported 拒绝。
- 备选：砍到 S7 单独立项——S6 主题是可视化，混协议工作有 scope 漂移风险。

### D7：UI 落点

- **拍板：`XcpView` 主区（Grid.Row=3）改 TabControl**：`实时采集`（现归因合并表迁移）/ `回放` / `MAP 可视化` / `参数集 diff`。顶部四行控件带（连接/记录/写回）不动。
- 备选：独立 XcpReplayView 顶栏切换——与 DBC `ReplayView`（v3.8 遗产，CAN 域）命名撞车且割裂 XCP 上下文。

## 3. 验收判据（v0.2）

1. 回放：加载 S4 产 MF4，曲线可见、可缩放/游标；空窗断线 + 归因标注可对上文件（round-trip 测试钉）。
2. 指纹门禁：A2L 不符 → 原始值降级 + 警示，不出现错误物理值。
3. MAP 只读：在线 UPLOAD 渲染 15 个真机 MAP 对象之一，只读（无写入口）。
4. diff：两份参数集对比行列出、越限标红、定位联动。
5. 多段写（D6 已定进）：真机形态 MAP 对象写回 → 回读比对全绿 → MultiSegmentUnsupported 拒绝解除有回归钉。
6. 门禁：全仓无过滤 ≥ 4350 通过 / 0 失败；分层守卫回归。

## 4. 已知限制（v0.1 预填）

- 只回放自家 writer 产物（D1 已定案）。
- gap 时间戳口径继承 S4 附录 C-1（到达序）。
- ScottPlot 依赖引入（D2 已定案；T0 落地时补版本与 lock 记录）。
- 在线 MAP 渲染的上传耗时与 DAQ 并发行为 = 台架核实项（S5 A-x 挂账延伸）。

## T6 补记（2026-09-26，两个实施口径裁定）

1. **MultiSegmentUnsupported 是死门**：包侧 `ValueContractFactory.SegmentList` 恒返回单段
   （对象起始地址 + 总字节数；多段列表是给 Task 22 DAQ 打包预留的）。S5 reconciler 的
   `Segments.Count != 1` 拒绝在实际合同上永不触发。跨 MEMORY_SEGMENT 的真正风险是：
   对象逻辑区间被切进多个 ADDRESS_MAPPING 时**物理地址不连续**，而 S5 writer 按
   "起始物理地址连续写 TotalByteLength"——会把第二段的数据错写到第一段映射后面。
2. **实施 = run 切分（Core，零包侧变更）**：新增 `CalibrationRunPlanner.PlanWriteRuns`
   （`Xcp/Calibration/`）——按 ADDRESS_MAPPING 覆盖把对象逻辑区间切成连续 run
   （各带物理地址）；映射空洞 / 重叠 / addrExt≠0 / 长度≤0 → null（宁可不写）。
   writer 新入口 `WriteAsync(contract, A2lDocument, value)`：单 run 退化为 S5 原分片路径
   （帧序不变，回归钉钉住）；多 run 逐段"SET_MTA → DOWNLOAD → 重臂 → UPLOAD 比对"，
   **单段失败中断后续段**，Detail 带段序号。reconciler 读当前值同步改逐 run 读再拼接。
3. **多元素 = 广播语义**（S5 枚举注释"S6 扩元素广播"的兑现）：`Encode` 只填首元素，
   writer 以同值广播填满 TotalByteLength（需 TotalByteLength 是元素字节整数倍，否则拒绝），
   P1-1 的静默清零问题就此解除而非简单放行。MultiElementUnsupported / MultiSegmentUnsupported
   枚举值保留（结果单消费者兼容），新结果恒不产生。卡片门禁（CanWrite）不感知该变化，
   行为随 writer 自动解除；MAP 卡片行内写值=整对象广播，Detail 明示"广播 N 元素"。
4. 台架挂账（并入附录 C）：真机跨段对象的映射形态（VAL_BLK/MAX_ODT 拆分是否产生
   多段合同）与广播写对真机标定页的影响待验。

## 变更记录

| 版本 | 内容 |
| --- | --- |
| v0.1 | 初稿，D1–D7 开放 |
| v0.2 | D1–D7 全按推荐定案（自研 MF4 读取器 / ScottPlot 5 / 指纹门禁回放 / 在线 UPLOAD MAP / 表格 diff / 多段写进 S6 / XcpView 主区 TabControl） |

## T0 补记（2026-09-26，选型落地预检 verdict）

1. **ScottPlot 裁定：可行，选 5.1.59**。spike 证据（`artifacts/spike_scottplot/`，本地不提交）：net8.0-windows 下 `Plot.Add.Heatmap` 无头渲染 PNG 成功 + `ScottPlot.WPF.WpfPlot` STA 构造/Refresh 成功。**关键发现**：传递依赖 `SkiaSharp.Views.WPF 3.119.0` 仅含 .NET Framework 资产，还原触发 NU1701 回退警告；仓库根 `Directory.Build.props` 全局 `TreatWarningsAsErrors=true` 会使其变 error。处置：消费 csproj（App）加 `<WarningsNotAsErrors>$(WarningsNotAsErrors);NU1701</WarningsNotAsErrors>`（警告可见、不致命，T3 落地）。
2. **MF4 块清单裁定**（`Mdf4StreamWriter.cs` 通读）：写面 = `##HD`（104B/6链）+ `##DG`（64B/4链，每对象一 DG）+ `##CG`（104B/6链）+ `##CN`（160B/8链）+ `##DL`（1024 槽）+ `##DT`（16MB 目标，24B 头）+ `##SD`（4 条事件通道 VLSD：kind/cause/detail/receive_kind）+ `##AT`（附件链）+ `##TX`（md 元数据/通道名）。**无 CC 块**（纯原始值，S1 A1 口径）、**无 EV 块**（事件走 SD 通道）。Reader 范围 = 上述集合 + 未知块显式报错。
3. **MAP 读路径裁定**：`XcpMaster` 公开面只有 `SendAsync` + `EnterMemorySequenceAsync`，无多元素 UPLOAD 便携手；S5 reconciliation 已有"SET_MTA+UPLOAD×⌈n/4⌉ 持门"成熟模式。T4 按同模式建 Core 只读 helper（不新开并发面）。

## T2 补记（2026-09-26，D3 口径修正）

**原 D3 文字"物理值换算走包侧 `ValueContract.Decode`"与 S4 实现事实不符**：S4 sink 落盘的是
receive 链（`XcpReceiveLoop`）已经 `ValueContract.Decode` 过的**物理值**（`XcpMdfRecordSink.cs:306`
写 `sample.Value`），记录文件里没有原始值可解。快照附件承载的是元数据（unit/限值/类别/轴结构）
与指纹，不是回放期换算依据。**修正后的 D3 执行口径**：

1. 回放层不做数值 Decode——文件值即物理值，曲线直接画。
2. 元数据装配：内嵌快照（`ContractSnapshotCodec.Decode` → `Asap2PackageApi.ImportSnapshot`）优先，
   外部 `ContractSet` 兜底，都无则元数据缺失。
3. 指纹门禁：快照 `A2lSha256` 与当前 A2L 比对，不符 → 元数据整面拒绝（unit/限值/MAP 轴结构不应用）
   + 警示；曲线仍可用（数值本身无歧义）。"降级原始值曲线"原措辞废止。
4. 空窗面：Invalid 行（NaN 值）+ 归因事件直通渲染层。

实现：`Xcp/Replay/XcpReplayDecoder`（6 测试钉：Trusted/Mismatched/外部合同/无来源/空窗标注/单通道降级）。

## T4 补记（2026-09-26，D2 实现裁定修正 + MAP 结构事实）

1. **D2 裁定修正**：ScottPlot **早已是本仓 Trace Viewer 的渲染引擎**（`ScottPlot.Wpf 5.0.55`，
   v3.62 引入，`Directory.Packages.props:44`）。T0 补记"第一个图表库依赖 / NU1701 豁免预案"作废
   ——T4/T3 直接复用 5.0.55（已过全仓 gate 的版本），不升级不新增依赖。
2. **MAP 第二轴结构事实**：包侧 `ValueContract.Axis` 只承载第一个 AXIS_DESCR（`ValueContractFactory`
   MAP 分支 `axis = AxisOf(c.AxisDescrs[0])`），第二轴的点数/引用只能从 `contracts.Document` 模型直读。
   `XcpMapReader` 据此实现（文档模型取两轴 AXIS_PTS → 合同解析换算 → 地址翻译 → UPLOAD）。
3. **只读红线钉**：`XcpMapReaderTests` 断言线上零 DOWNLOAD（0xF0）帧；App 层不构造协议命令（分层守卫）。
4. **离线兜底口径**：A2L/参数集均无 MAP 元素静态值（S5 参数集对多元素对象拒绝导出）——离线渲染
   = 索引轴 + NaN 网格（仅结构，Detail 明示"离线模式"），不伪造数据（§4.8 同口径）。
