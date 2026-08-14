---
topic: window-ux
created: 2026-08-14
updated: 2026-08-14
status: draft
covers: 主窗口信息架构（Trace 主视图 + 右侧实时面板）+ 硬件连接抽离 + 二级窗口生命周期统一 + 窗口状态持久化 + Window 菜单
related: docs/superpowers/specs/2026-07-11-app-shell-view-model-god-class-refactor.md
---

# 设计文档：窗口与 UI UX 优化（Window UX）

> 本文档是实现基准。所有命名、时序、归属、例外均已写明；如有歧义以本文档为准。
> 2026-08-14 自查修订：堵住 8 处可能让实现者理解偏差的缺口（见 §9 自查记录）。

## 1. 背景与问题

peakcan-host 功能层完整（Trace / DBC / Send / Signal / UDS / Flashing / HIL / Replay / AI Chat），但**窗口治理**和**硬件连接**两块是短板：

1. **视图入口分裂** — 同族功能被拆成 tab 和窗口两种形态，分界是实现历史不是用户逻辑（Script tab vs ECU Script Editor 窗口、Send tab vs Multi-frame 窗口）。
2. **核心工作流断裂** — "发送报文 → 看总线反应"是最高频动作循环（发→看→再发→再看），但 Trace 和 Send 是**互斥 tab**，发一帧就得切走，看不到即时反应。
3. **工作区碎片化** — 4 个二级窗口浮在主窗口上方互相遮挡，无"列出已开窗口并唤回"入口。
4. **窗口状态零持久化** — 每次启动回到硬编码尺寸。
5. **硬件连接平铺在工具栏** — 探测/通道/波特率/FD/连接/断开 7 个控件挤一条工具栏；且 VM 硬编码 PEAK handle（`PcanUsbFdFirstHandle` 0x51），后续适配其他 CAN 盒要改代码。

## 2. 目标与非目标

### 目标

1. 主窗口信息架构：**Trace 主视图 + 右侧常驻实时面板**（发送/信号/统计），"发→看"不再切页。
2. 硬件连接**抽离**：工具栏收敛为状态胶囊 + 入口；连接设置面板字段由**设备能力**动态生成；适配新 CAN 盒 UI 零改动。
3. 统一二级窗口生命周期（单例缓存 + 关闭重建），修 Multi-frame 双入口。
4. 窗口状态（位置/尺寸/最大化）持久化，重启恢复。
5. **Window 菜单**列出已开窗口一键唤回。
6. 全中文 UI；CAN 专有名词（CAN FD / DBC / ID / HIL / UDS）保留英文。

### 非目标

- 不做 AvalonDock 完整 docking / 布局持久化（成本收益不成比例，见 §6 Phase 2 方向）。
- 不动 UDS Flashing 面板内部（保持窗口形态）。
- 不动 Trace Viewer 图表面板内部（ScottPlot / watch list / AI chat）。
- 不做视觉主题统一（Colors.xaml / 图标）——独立后续 spec。
- 不改 Core / Infrastructure 层**现有**接口的契约（NetArchTest）；**允许新增**接口/类型（如 D6 的 `ICanDeviceProvider`）。

## 3. 现状（已验证）

### 3.1 窗口/视图清单

| Surface | 形态 | 生命周期 | 现状 |
|---|---|---|---|
| AppShell | 主窗口 | 进程级 | 菜单+工具栏(7 硬件控件)+状态栏+单 ContentControl |
| Trace/DBC/Send/Signal/Stats/Script/Replay | 视图(7) | `ViewSwitcher.Show` 缓存 | 互斥切换，一次看一个 |
| TraceViewer / UDS / EcuScript / MultiFrame / **HIL** | 独立窗口(5) | `ViewSwitcher.ShowWindow` 缓存 | Multi-frame 双入口（AppShell 新建 / SendView 缓存）；HIL 由 tab 迁窗口（见 §9） |
| DbcTreePicker | 模态对话框 | Owner+ShowDialog | 保持，不进 WindowHostService |

### 3.2 已验证关键事实

- **零持久化**：`src` 全树无 `RestoreBounds`/`WindowState` 保存；XAML 静态尺寸（AppShell 1280×720 等）。
- **Multi-frame 双入口**：`ViewSwitchFlow.cs:169` 每次新建；`SendViewModel/LibraryFlow.cs:149` 缓存单例。注释自认（`ViewSwitchFlow.cs:161`）。
- **持久化先例**：`RecentSessionsService` → `%APPDATA%/PeakCan.Host/recent-sessions.json`，`{version,...}` + 原子写 + 容错（`RecentSessionsService.cs:35-79`）。
- **硬件抽象已就位一半**：Core 已定义 `IChannelProbe` / `IChannelEnumerator` / `ICanChannel`；PEAK 实现在 `Infrastructure/Peak/`（`PeakCanChannel`/`PeakChannelProbe`/`PeakChannelEnumerator`）；NetArchTest 强制 Core 不依赖 PEAK SDK。**缺口**：(a) VM 层 `ChannelFlow.cs` 硬编码 `PcanUsbFdFirstHandle`；(b) 无"设备能力"描述，UI 固定写死通道/波特率/FD 控件。
- **概念模型（2026-08-14 修正）**：标准帧/扩展帧是**帧级属性**（同一通道必然共存，发送时选），非连接配置；CAN FD 是**通道级**能力（需数据段速率参数），在连接设置配置。
- 无主题；`App.xaml` 仅 converter；硬编码色散落各 XAML。

## 4. 设计决策

### D1 — WindowStateStore（窗口状态持久化）

- 模式照抄 `RecentSessionsService`：`%APPDATA%/PeakCan.Host/window-state.json`；schema `{ "version":"window-state/v1", "windows":{ "<key>":{left,top,width,height,state} } }`。
- Key 由**共享枚举 `WindowKey`** 定义：`AppShell`/`TraceViewer`/`Uds`/`EcuScriptEditor`/`MultiFrame`。此枚举同时被 D2 `WindowHostService` 用作窗口注册 key，**两处必须一致**（单一来源，禁止各写各的字符串）。
- **恢复时机**：窗口 `SourceInitialized` 时应用已保存尺寸（避免启动后布局闪烁）；`WindowStateStore` 为 DI 单例，App 启动时 `LoadAsync()`，启动即恢复。
- 写入时机：各窗口 `Closed`（AppShell 用 `Closing`，避免最小化/被盖时误写）。
- 恢复校验：越界/不可见（含多屏拔出）→ 落回该窗口 XAML 默认尺寸。
- 容错契约：损坏文件→默认值(Error 日志)；缺文件→默认值；写失败→静默 Warning。同 `RecentSessionsService`。
- 测试 ctor 注入路径参数（同 `RecentSessionsService.cs:98` 的 `overridePath`）。

### D2 — WindowHostService（统一二级窗口生命周期 + 注册表）

- DI 单例 `Services/Ui/WindowHostService`：持有 5 个二级窗口 cache（`Dictionary<WindowKey, Window>`：TraceViewer/Uds/EcuScript/MultiFrame/Hil）。
- `Show(key, factory)` 语义：缓存存在且存活→`Activate` 置前；不存在→`factory()` 建窗 + 缓存 + 订阅 `Closed`→清 cache。
- **实现注意**：因 cache 集中持有，`ViewSwitcher.ShowWindow` 的 `ref` 参数形式不适用，**service 内联该逻辑**（工厂+缓存+Closed-reset），**不改 `ViewSwitcher` 静态类本身**。
- **Owner 归属**：统一在 service 内赋值（取 `Application.Current.MainWindow`，仅在 Show 时设，窗口非自身时）；调用方不再处理 Owner。
- **`WindowEntry.IsActive`**：由 `Window.Activated`/`Deactivated` 事件驱动更新。
- 暴露 `ObservableCollection<WindowEntry>`（`DisplayName`/`IsActive`/`ActivateCommand`），供 D3 Window 菜单绑定。
- 迁移点：`AppShellViewModel` 的 `ShowUds`/`ShowTraceViewer`/`ShowEcuScriptEditor`/`ShowHil`/`OpenMultiFrame` 与 `SendViewModel.OpenMultiFrameSend` 全部改调 `service.Show(key, factory)`，删除各自缓存字段 → **Multi-frame 双入口收敛为单例；HIL 由 tab 迁窗口**。
- 模态窗口（`DbcTreePicker`、连接设置面板 D6）**不进**此 service。

### D3 — Window 菜单

- 菜单栏加 `Window` 项，`ItemsSource` 绑 `WindowHostService.OpenWindows`。
- 只列 4 个二级窗口：未打开=点击打开，已打开=点击置前，激活中=Checked。
- 主窗口内的 tab 切换由 tab strip 承担，不在 Window 菜单重复列出。

### D4 — 主界面信息架构（Trace 主视图 + 右侧实时面板）

基于产品分析（用户是台架工程师，"发→看"是核心循环）：

```
┌ 菜单栏: 文件 视图 窗口 ────────────────────────────────┐
├ 工具栏: [⚙设备设置] [●已连接·PCAN·CH0·500k] [断开] │ [●录制] │
├────────────────────────────────┬─────────────────────┤
│                                │ [发送|信号|统计]      │
│   追踪 主视图（最大面积）        │  发送面板             │
│   ID过滤/高亮/暂停/导出        │  循环发送             │
│                                │  （高频实时操作常驻）  │
├────────────────────────────────┴─────────────────────┤
└ 状态栏 ───────────────────────────────────────────────┘
```

- **实现结构：两个独立 `TabControl` 并存，不互斥**——主区域 TabControl（Trace 默认 tab + DBC/脚本/回放 次级 tab），右侧独立 TabControl（发送/信号/统计）。切换右侧不影响主区域选中 tab，反之亦然。HIL 已迁独立窗口（§9），不入主区域。
- 主区域：Trace 主视图占最大面积；`GridSplitter` 可调左右比例并记忆（持久化 key 走 D1 的 `window-state.json`）。
- 右侧窄面板（~300px）：发送/信号/统计 小 tab，**永远与 Trace 同屏**。**现有 `SendView`/`SignalView`/`StatsView` 需做窄布局适配**——SendView 现为 `ScrollViewer`+`StackPanel` 长视图（199 行），窄栏需重排为紧凑纵向表单；SignalView 表+图+统计需压缩。**这是实现者最容易低估的工作量，单独列任务。**
- 次级 tab（DBC/脚本/回放）：复用现有 UserControl 直接挂 tab。**DBC/回放默认保留 tab**（DBC 是数据源服务，不宜窗口，user 判定）；**脚本保留 tab（user 未表态，默认保留，逻辑同 HIL 可后续迁窗口）**。HIL 已迁窗口。
- 视图实例复用：`ViewSwitcher.Show` 语义保留——切 tab 用缓存实例，**禁止重建**（ScriptView 内 WebView2 重建代价高），用 `ReferenceEquals` 单测守。

### D5 — 入口语义规则（沉淀）

> **需要与主界面持续并看 → 常驻面板/tab；独立生命周期/高密度工作区/大工具 → 窗口。**

- 常驻右侧面板：发送/信号/统计（高频实时）。
- 主区域次级 tab：DBC（数据源服务）、脚本、回放（低频任务）。
- 独立窗口：Trace Viewer（离线分析）/ UDS（诊断刷写）/ Multi-frame（批量）/ ECU Script Editor（编辑）/ **HIL（测试执行——需与主窗口 Trace 并排观察数据链路层通讯，user 2026-08-14 判定迁窗口）**。
- 例外：DbcTreePicker 是模态 picker，不属窗口类。

### D6 — 硬件连接抽离

**UI**：工具栏 7 个硬件控件 → **状态胶囊**（`● 已连接 · PCAN-USB FD · CH0 · 500 kbit/s`）+ `[连接/断开]` + `[⚙ 设备设置]`。连接设置面板（模态）字段由设备能力驱动：

```
设备类型 ▾ PCAN-USB FD (PEAK)      ＋可扩展（其他 CAN 盒）
通道     ▾ CH0 (0x51) / CH1 (0x52)
波特率   ▾ 500 kbit/s
CAN 模式    ☑ 启用 CAN FD
FD 数据段 ▾ 2 Mbit/s              （仅 FD 生效；速率列表来自设备能力，非硬编码）
注：标准/扩展帧是每帧属性，发送时选，不在此配置
```

**架构**：
- 新增 Core 接口 `ICanDeviceProvider`（`EnumerateDevices() → DeviceDescriptor[]`）。`DeviceDescriptor` 含：显示名 / 枚举的通道 / 支持的波特率列表 / FD 能力 / **FD 数据段速率列表（2/5/8 Mbit/s 等，来自设备，不硬编码）** / 默认通道与默认波特率。
- Peak 实现 `PeakCanDeviceProvider` 在 `Infrastructure/Peak`，映射现有 `IChannelEnumerator` + 静态能力表。
- UI（连接设置面板 VM）只绑 `DeviceDescriptor`。
- **VM 清掉 `PcanUsbFdFirstHandle` 硬编码**（`ChannelFlow.cs` 的 legacy fallback 与日志处）：默认 handle 改由 Peak provider 提供；legacy 无 enumerator 路径保留但 handle 来源改为 provider。
- 换 CAN 盒 = 新增 provider + DI 注册，UI/VM 零改动。

### D7 — 语言策略

全中文；CAN 专有名词保留英文。迁移顺序：Phase 0.6 先 AppShell 菜单/工具栏/状态栏；Phase 1.6 其余视图（Trace/Send/Signal/Stats/DBC 等）。

## 5. 文件影响面

| 类型 | 文件 | 改动 |
|---|---|---|
| 新增 | `Core/Devices/ICanDeviceProvider.cs` + `DeviceDescriptor`（Core 新目录 `Devices`，命名跟随 Core 现有约定） | D6 架构 |
| 新增 | `Infrastructure/Peak/PeakCanDeviceProvider.cs` | D6 Peak 实现 |
| 新增 | `App/Services/Ui/WindowStateStore.cs` / `WindowHostService.cs` / `WindowEntry.cs` / `WindowKey.cs` | D1/D2/D3 |
| 新增 | `App/Windows/ConnectionSettingsWindow.xaml(.cs)` + `ViewModels/ConnectionSettingsViewModel.cs` | D6 连接设置面板 |
| 新增 | 单测：`WindowStateStoreTests` / `WindowHostServiceTests` / `DeviceProviderTests` / `ConnectionSettingsViewModelTests` | §7 |
| 修改 | `App/AppShell.xaml(.cs)` | D4 双 TabControl + D3 菜单 + D1 状态 + D6 工具栏 |
| 修改 | `App/ViewModels/AppShellViewModel/ViewSwitchFlow.cs` | D2 改走 service + D4 tab 选中 |
| 修改 | `App/ViewModels/AppShellViewModel/ChannelFlow.cs` | D6 清硬编码、走 provider |
| 修改 | `App/ViewModels/SendViewModel/LibraryFlow.cs` | D2 改走 service |
| 修改 | `App/Views/{SendView,SignalView,StatsView}.xaml` | D4 右侧窄面板布局适配 |
| 修改 | 4 个 `App/Windows/*.xaml.cs`（TraceViewer/Uds/EcuScript/MultiFrame） | D1 持久化挂点 + D2 Owner 移除 |
| 修改 | `App/Composition/AppHostBuilder/AppServicesFlow.cs` | DI 注册 |

## 6. 分阶段实施

- **Phase 0 — 窗口治理**（增量，不碰主结构，风险最低）：0.1 D1 状态持久化 → 0.2 D2 生命周期统一（含 Multi-frame 修复）→ 0.3 D3 Window 菜单 → 0.4 D1 各窗口挂点 → 0.5 D7 中文第一批（菜单/工具栏/状态栏）。
- **Phase 1 — 主界面重构 + 硬件抽离**（结构性）：1.1 D6 架构（provider + descriptor）→ 1.2 D6 连接设置面板 → 1.3 D6 工具栏收敛 → 1.4 D6 清硬编码 → 1.5 D4 主内容区重构（双 TabControl + 窄面板适配）→ 1.6 D5 入口规则 doc + D7 中文剩余。
- **Phase 2 — 视觉**（独立 spec，延后）：Color 令牌 / 图标 / 布局持久化方向评估。

依赖：Phase 0 全部不依赖 Phase 1；Phase 1.5 依赖 1.1-1.4（工具栏与主结构一起动，避免两次触碰 AppShell）。

## 7. 测试计划

- `WindowStateStoreTests`：往返序列化；损坏 JSON→默认；越界/不可见→钳制落回默认；超大文件上限（镜像 `MaxLoadFileBytes` 1MB）；写失败不抛。
- `WindowHostServiceTests`：缓存复用；Show→Activate 置前；Close→清 cache→重建；`OpenWindows` 增删与 `IsActive` 更新实时；Owner 正确挂主窗口。
- `DeviceProviderTests`：Peak provider 枚举通道/能力正确；无硬件→空列表不抛；descriptor 默认值完整。
- `ConnectionSettingsViewModelTests`：字段由 descriptor 驱动；切换设备 descriptor 后选项联动。
- `AppShellViewModelTests`：Window 菜单项随开窗更新；**主区域 tab 切换命中缓存实例（`ReferenceEquals`，不重建）**；清硬编码后 legacy 路径行为不变（核对现有 `ConnectCommand_Uses_SelectedChannel_Handle` 等断言）。
- 手动：摆 4 窗口重启恢复；多屏钳制；发→看同屏（发送后 Trace 出现回应）；右侧窄栏各 tab 在 300px 宽可操作。

## 8. 风险与缓解

| 风险 | 缓解 |
|---|---|
| 右侧窄面板下 SendView 现有布局严重挤压 | 单独列"窄布局适配"任务；重排为紧凑纵向表单，不缩小字号硬塞 |
| 双 TabControl 导致缓存 view 重建（WebView2 重） | 显式赋缓存实例 + `ReferenceEquals` 单测 |
| 现有 `AppShellViewModelTests` 依赖单 ContentControl/`CurrentView` | Phase 1 改完先跑该套，同步更新断言 |
| 硬编码清理引入 legacy 路径回归 | 现有连接测试覆盖，核对后保留 |
| 持久化在 Closed 写、崩溃丢布局 | 接受（同 RecentSessionsService 契约）；AppShell 另加 Closing 兜底 |
| WindowKey 两处集合漂移 | 单一枚举共享（D1），禁字符串散写 |

## 9. 决策记录

**已拍板（2026-08-14 user）**：语言全中文；主界面 = Trace 主视图 + 右侧常驻面板（发送/信号/统计）；连接设置面板（模态）形态；扩展帧归属帧级（发送时选）不在连接配置；硬件抽离走 `ICanDeviceProvider`；Phase 2 视觉延后独立 spec。

**自查修订（2026-08-14）**——8 处堵理解偏差缺口：
1. §2 非目标澄清：允许新增接口/类型，仅禁止改现有接口契约。
2. D1 补恢复时机（`SourceInitialized` 应用）与 `WindowStateStore` 注入/加载入口。
3. D1/D2 引入共享 `WindowKey` 枚举，杜绝字符串散写漂移。
4. D2 明确 `ViewSwitcher.ShowWindow` 的 ref 形式不适用、service 内联该逻辑；Owner 归属统一在 service。
5. D2 补 `WindowEntry.IsActive` 的事件驱动机制。
6. D4 明确"双 TabControl 并存不互斥"实现结构 + 现有 SendView/SignalView/StatsView 需窄布局适配（工作量风险）。
7. D6 明确 FD 数据段速率列表来自 descriptor，不硬编码。
8. §5/§7 补 D4 窄适配与连接设置面板的受影响文件与测试。

**已决（2026-08-14 user）**：
1. **次级视图归属**：DBC 保留 tab（数据源服务，不宜迁窗口）；**HIL 迁独立窗口**（测试中需与主 Trace 并排看数据链路层通讯）；回放保留 tab（用户未深度使用，成本最低，未来有场景再定）；脚本保留 tab（用户未表态，默认保留，未来如需可与 HIL 同逻辑迁窗口）。
2. **状态胶囊默认**：`● 已连接 · PCAN-USB FD · CH0 · 500 kbit/s`（设备名+通道+波特率），保持 mockup 现状；如需精简随时可改（非实现阻塞）。

**待定（实现前确认）**：无阻塞项。注意 HIL 迁窗口后，Window 菜单共列 5 个窗口。

## 10. 实施清单（Phase 0 / Phase 1）

> 每个任务有明确验收条件；顺序执行。Phase 0 不依赖 Phase 1；P1-5 依赖 P1-1..P1-4（工具栏与主结构一起动，避免两次触碰 AppShell）。每完成一个任务跑对应单测 + 手动验收项。

### Phase 0 — 窗口治理（增量，不动主结构）

**P0-1 · WindowStateStore 服务**
- 新增 `src/PeakCan.Host.App/Services/Ui/WindowStateStore.cs` + `WindowKey.cs`（枚举：AppShell/TraceViewer/Uds/EcuScriptEditor/MultiFrame/Hil）。
- 模式照抄 `RecentSessionsService`：`%APPDATA%/PeakCan.Host/window-state.json`；schema `{version:"window-state/v1", windows:{key:{left,top,width,height,state}}}`。
- API：`LoadAsync()` / `Get(WindowKey)` / `Set(WindowKey, dto)` / `SaveAsync()`；边界钳制 + 损坏/越界容错。
- **验收**：`WindowStateStoreTests` 全绿（往返 / 损坏JSON→默认 / 越界→钳制 / 超大 1MB 上限 / 写失败不抛）。

**P0-2 · WindowHostService 服务**
- 新增 `src/PeakCan.Host.App/Services/Ui/WindowHostService.cs` + `WindowEntry.cs`。
- `Dictionary<WindowKey, Window>` 缓存；`Show(key, factory)` = 缓存复用 + Closed→清缓存 + Activate 置前（内联 ViewSwitcher 逻辑，不改静态类）。
- Owner 统一赋值（`Application.Current.MainWindow`）；`IsActive` 由 `Activated`/`Deactivated` 驱动；暴露 `ObservableCollection<WindowEntry>`。
- **验收**：`WindowHostServiceTests` 全绿（缓存复用 / Show→Activate / Close→重建 / OpenWindows 与 IsActive 实时 / Owner 正确）。

**P0-3 · 视图迁移到 service（Multi-frame 修复 + HIL 迁窗口）**
- `ViewSwitchFlow.cs`：`ShowUds`/`ShowTraceViewer`/`ShowEcuScriptEditor`/`ShowHil`/`OpenMultiFrame` → `WindowHostService.Show`。
- `SendViewModel/LibraryFlow.cs`：`OpenMultiFrameSend` → `WindowHostService.Show`；删除各自缓存字段。
- **验收**：AppShell 与 SendView 打开 Multi-frame 为同一实例（双入口收敛）；HIL 从主 tab 变为窗口；`AppShellViewModelTests` 绿。

**P0-4 · Window 菜单**
- `AppShell.xaml` 加 `Window` 菜单，ItemsSource = `OpenWindows`；每项 DisplayName + IsActive(Checked) + ActivateCommand。
- **验收**：手动——开 5 窗口后菜单正确列示 / 激活中勾选 / 点击唤回。

**P0-5 · 窗口状态持久化挂点**
- 5 个 `Windows/*.xaml.cs`：`SourceInitialized` 恢复 + `Closed` 保存；AppShell 用 `Closing`。
- **验收**：手动——摆 5 窗口 + 最大化后重启全部恢复；拔副屏后主屏内钳制。

**P0-6 · 中文第一批**
- AppShell 菜单 / 工具栏 / 状态栏文案。
- **验收**：启动界面全中文（CAN 专有名词除外）。

### Phase 1 — 主界面重构 + 硬件抽离（结构性）

**P1-1 · ICanDeviceProvider（Core）+ Peak 实现**
- 新增 `src/PeakCan.Host.Core/Devices/ICanDeviceProvider.cs` + `DeviceDescriptor`（显示名 / 通道列表 / 波特率列表 / FD 能力 / FD 数据段速率列表 / 默认通道与默认波特率）。
- 新增 `src/PeakCan.Host.Infrastructure/Peak/PeakCanDeviceProvider.cs`（映射现有 `IChannelEnumerator` + 静态能力表）。
- **验收**：`DeviceProviderTests` 绿（枚举 / 能力完整 / 无硬件空返回不抛 / 默认值完整）。

**P1-2 · 连接设置面板**
- 新增 `App/Windows/ConnectionSettingsWindow.xaml(.cs)` + `ViewModels/ConnectionSettingsViewModel.cs`；字段由 `DeviceDescriptor` 驱动（设备类型 / 通道 / 波特率 / CAN FD / FD 数据段速率）+ 应用并连接；DI 注册。
- **验收**：`ConnectionSettingsViewModelTests` 绿（descriptor 驱动 + 切换设备字段联动）；手动——选不同设备字段变化。

**P1-3 · 工具栏收敛**
- `AppShell.xaml` 工具栏：状态胶囊（`●已连接·设备·通道·波特率`）+ `[连接/断开]` + `[⚙ 设备设置]` + `[●录制]`；删除探测/通道/FD/波特率控件。
- **验收**：工具栏硬件控件收敛；界面整洁。

**P1-4 · 清 PcanUsbFdFirstHandle 硬编码**
- `ChannelFlow.cs`：legacy fallback 与日志处 handle 来源改由 `PeakCanDeviceProvider` 默认提供。
- **验收**：现有连接测试（`ConnectCommand_Uses_SelectedChannel_Handle` 等）仍绿。

**P1-5 · 主内容区重构（双 TabControl + 窄面板）**
- `AppShell.xaml`：主区域 TabControl（Trace 默认 + DBC/脚本/回放）+ 右侧独立 TabControl（发送/信号/统计）+ GridSplitter（宽度记忆）。
- `Views/{SendView,SignalView,StatsView}.xaml` 窄布局适配（SendView 重排紧凑纵向表单）。
- `ViewSwitchFlow.cs` tab 选中 + 缓存实例复用（禁止重建）。
- **验收**：发→看同屏；`AppShellViewModelTests` 缓存 `ReferenceEquals` 测试绿（切 tab 不重建 WebView2）；手动——右侧 300px 各 tab 可操作。

**P1-6 · 入口规则 + 中文剩余**
- `ViewSwitchFlow.cs` 顶部 doc comment（D5 规则）；其余视图中文。
- **验收**：界面全中文；入口规则已沉淀。

### Phase 2 — 视觉（独立 spec，延后）

Color 令牌 / 图标 / 布局持久化方向评估。
