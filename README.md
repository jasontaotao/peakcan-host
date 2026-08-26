# PeakCan Host

<img src="src/PeakCan.Host.App/Assets/peakcan-host-banner.png" alt="PeakCan Host 标志 — 连接器框架中的 CAN 总线波形风格，PEAK 橙色 + 深蓝 + 信号青色" width="640">

Windows 专用 WPF 桌面主机，适配 **PEAK PCAN-USB FD / Pro FD** 与 **ZLG USBCAN FD 200U** —
通用 CAN 总线监控工具，支持 DBC 解码、手动/循环/多帧发送、实时信号视图、
ASC/BLF 回放、UDS 诊断与 Flash 编程、HIL 测试执行。

> **状态:** v3.65.0。双厂商 CAN 驱动（PEAK + ZLG）、Trace 查看器 + AI 聊天/推理、
> UDS 诊断栈 + Flash Pipeline、脚本引擎、HIL 测试执行（单/多通道）、
> 多通道录制与回放、报告侧 per-channel DBC 解码。
> **~2760 个单元测试通过**（Core ~908 + Infrastructure ~589 + App ~1226 + Cli ~35）；
> 依赖 **PeakCan.HIL.Core 0.13.0**（控制流/参数化/多通道模型）；NetArchTest 强制执行架构规则；
> 每次推送 `main` 自动运行 CI。

## 功能特性

### 硬件与驱动

- **双厂商驱动** — PEAK PCAN-USB FD / Pro FD（`Peak.PCANBasic.NET`）+ **ZLG USBCAN FD 200U**
  （`zlgcan.dll`，v3.65.0 新增），同一 `ICanChannel` 抽象下可混插。
- **多连接会话** — 连接设置弹窗一次配置/打开多台设备（PEAK / ZLG 混插），尽力式连接
  （任一组失败标红跳过，不阻塞其余）；Trace 按通道过滤、Stats 按通道聚合、发送按目标通道。
- **探测 + 连接 / 断开** — 检测已插入的设备，打开 CAN FD 通道，注册到进程内帧路由器。

### Trace 与回放

- **Trace 视图** — 每个接收帧的虚拟化 DataGrid（时间戳、通道、ID、DLC、十六进制数据、解码行）。
- **Trace 查看器 + 会话持久化** — 加载多个 `.asc` 录音文件并排显示，同步回放；会话保存为
  `.tmtrace` 包（master source / CAN-ID 过滤器 / watch list / signal groups 由
  `ITraceSessionService` 持久化）。
- **回放** — 支持 **ASC + BLF**（v3.51.0，Vector 二进制日志）两种格式；循环 / 速度 / 进度条 /
  CAN-ID 过滤器 / 帧级步进 / Ctrl+B 书签 / 命名循环区域。
- **Trace Viewer AI 聊天 Agent（v3.55.0+）** — 20+ 工具（信号搜索、时序分析、异常扫描、
  watch list 管理、信号别名、分组等）。
- **AI 推理 v1（v3.52.0）** — 本地证据（DID 数据 + trace 片段）驱动的失败原因分析，AI Analysis 面板。

### DBC 与信号

- **DBC 文件加载** — UI 线程外解析 `.dbc`；消息表带发送者、DLC、信号列表；值表枚举。
- **信号视图 + 图表** — 实时 DBC 解码（原始 hex + 物理值），OxyPlot / ScottPlot 双引擎图表，
  green-line 锚点 watch 同步。
- **发送面板** — 手动发送（CAN ID + hex，CAN FD 标志，标准/扩展帧）、**循环发送**、
  **DBC 循环发送**、**ISO-TP 多帧发送**、序列发送、速率限制发送；多通道场景下按目标通道发送。

### UDS 诊断栈

- **ISO 14229 全套** — 会话控制、ECU 复位、DID 读写、安全访问（含 ODX 自动推导 level/seed 长度）、
  例程控制、DTC 读取/清除、Flash 编程。
- **Flash Pipeline** — 闪烁配置（profiles + 步骤编排）+ 二次引导栈；UDS 日志控制台。
- **ODX 导入（v3.50.0+）** — `OdxImportService` 解析 ODX 数据，自动填充
  SecurityAccess 参数（🔗 ODX 标记自动 vs 手动配置）。

### HIL 测试执行

- **HIL 执行引擎** — `HilRunnerService` + `HeadlessHostBuilder`，4 种模式：
  Hardware / TraceReplay / VirtualEcu / Matrix；用例选择（SelectedCaseNames）。
- **多通道执行（hil-core 0.13.0）** — suite 声明 `Channels`，步骤 `TargetChannel` 指定发/监控哪路；
  每通道独立 DBC；`HardwareChannels` 按索引顺序绑定已连接设备；报告按通道解码。
- **控制流 + 参数化** — `If / Repeat / Loop / Assign` 步骤 + suite/case 参数 + `${name}` 插值 +
  表达式求值器（`signal.` / `did.` / `param.` sourceRef）+ `dtcPresent()` 内置函数；
  引擎为单路径递归解释器（`ExecuteStepListAsync` + `ExecuteLeafAsync`）。
- **每 case 报文 log** — `CaptureCaseLogs` 把每个 case 的全量报文流式写入独立 `.asc` 文件。
- **HTML 报告** — DBC 信号解码、否定断言 badge、SVG 时序图、按 Path/Iteration 分组、
  趋势记录（trends.json）；`--format json` CLI 输出供 peakcan-studio Copilot 分析，
  另有 JUnit 输出。
- **步骤校验框架** — AI 生成步骤的自动校验（多通道通道引用、DBC ID/信号存在性、
  Session 类型、SecurityAccess 顺序等），结果分级（High/Medium/Low）。

### AI 与脚本

- **AI 聊天（v3.54.0+）** — 多厂商 LLM（DeepSeek / GLM / Kimi / 自定义），
  可在聊天设置面板切换；支持信号搜索、时序分析等工具。
- **AI Copilot（peakcan-studio）** — 自然语言生成测试步骤、分析失败根因，
  支持控制流 step kinds、DBC 数据脱敏、表达式沙箱（dry-run + 重试）。
- **脚本引擎（v1.0.0）** — ClearScript V8 驱动的 JavaScript 脚本，`can.*` / `dbc.*` API，
  CodeMirror 6 编辑器，6 个预置示例脚本。

## HIL 配置器 Studio（独立仓库）

HIL Configurator Studio（TestSuiteBuilder / EcuSimulator / OdxImport 三面板 UI）已移至独立仓库
**`peakcan-studio`**（private，[jasontaotao/peakcan-studio](https://github.com/jasontaotao/peakcan-studio)）；
本仓库保留 HIL 执行引擎与报告。
**格式冻结约束：** suite/script JSON 模型签名（字段名、camelCase 序列化、step `$kind` 多态判别器、
ECU 脚本 `canIds`/`states|rules` 结构、`channels` 声明等）由两仓库共享模型保证一致，
任何一侧变更必须 lockstep 同步到另一侧并通过互操作测试（`peakcan-studio` 的 `InteropTests`），
否则跨仓库加载直接失败。
**模型包：** 共享模型现在通过 NuGet 包 **`PeakCan.HIL.Core`**（0.13.0）消费，
host / studio 双 pin 同一版本。

## 系统要求

- **Windows 10（1809+）或 Windows 11**（WPF 应用）
- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**（开发用；
  发布后的 exe 是自包含的，目标机器无需安装运行时）
- **PEAK PCAN 驱动**（PEAK 硬件运行需安装，
  [PEAK-System 下载页面](https://www.peak-system.com/PCAN-USB-FD.366.0.html)）
- **ZLG 驱动**（ZLG USBCAN FD 200U 运行需安装厂商驱动，`zlgcan.dll` 随发布包分发）

## 构建

```bash
dotnet build PeakCan.Host.slnx -c Release
```

解决方案包含 3 个生产项目（Core / Infrastructure / App）和 3 个测试项目（每层一个）。
另有独立于解决方案的 `src/PeakCan.Host.Cli`（HIL CLI 入口）与其测试项目。
构建输出在 `src/<project>/bin/Release/<TFM>/`。

## 运行（从源码）

```bash
dotnet run --project src/PeakCan.Host.App
```

Shell 窗口打开到 **Trace** 选项卡。点击**探测**检测设备，然后**连接**开始接收帧。

## 运行（自包含发布 exe）

```bash
dotnet publish src/PeakCan.Host.App -c Release -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true \
    -o artifacts/win-x64/

artifacts/win-x64/PeakCan.Host.exe
```

输出是一个约 66 MB 的单个 `.exe`，包含 .NET 10 运行时。详见 [artifacts/README.md](artifacts/README.md)。

## 测试

```bash
dotnet test PeakCan.Host.slnx -c Debug
```

输出：**~2760 通过**（Core ~908 / Infrastructure ~589 / App ~1226 /
Cli ~35 — 独立于 slnx 单独跑）。使用 `dotnet test --collect:"XPlat Code Coverage"`
可生成每个测试项目的 `cobertura.xml` 覆盖率报告。

## 项目结构

```
src/PeakCan.Host.App             WPF UI（MVVM、DI 组合、窗口/视图）
src/PeakCan.Host.Infrastructure  PEAK / ZLG 驱动适配器、HIL 引擎（HilRunnerService /
                                 HeadlessHostBuilder / 断言上下文）、报告、CLI 输出
src/PeakCan.Host.Core            DOM 层（CanFrame、DBC、UDS 会话、HIL run 请求契约、
                                 TestSuiteEngine、表达式/校验）、引用 PeakCan.HIL.Core 包
src/PeakCan.Host.Cli             独立于 slnx 的 CLI 入口（HIL --format json / JUnit）
tests/                           每层一个测试项目 + Cli.Tests
```

## 架构

分层由 NetArchTest 强制执行
（[`tests/PeakCan.Host.Infrastructure.Tests/Architecture/LayeringRulesTests.cs`](tests/PeakCan.Host.Infrastructure.Tests/Architecture/LayeringRulesTests.cs)）：

```
   PeakCan.Host.App            （WPF, MVVM, BackgroundService, DI 组合）
            │
            ▼  使用
   PeakCan.Host.Infrastructure （PEAK / ZLG SDK 适配器, HIL 引擎, 报告）
            │
            ▼  使用
   PeakCan.Host.Core           （CanFrame, DBC 解析器, UDS, HIL 契约与引擎）
            │
            ▼  使用
   PeakCan.HIL.Core (NuGet)    （0.13.0 — 共享模型: ChannelConfig / 控制流步骤 /
                                 表达式求值器 / StepResult）
```

App 层禁止直接引用厂商 SDK（PEAK / ZLG）；所有硬件调用通过 Infrastructure 中的
`IChannelProbe` / `PeakCanChannel` / `ZlgCanChannel`。CI 会拒绝任何违反边界的 PR。

## DBC 解析器范围

- **支持的关键字**: `VERSION`, `NS_`, `BS_`, `BU_`, `BO_`, `SG_`,
  `VAL_`, `VAL_TABLE_`, `CM_`, `BA_DEF_`, `BA_`, `SIG_GROUP_`, `EV_`
- **多路复用信号 (M / m)** — 完全支持。提取多路复用器值；仅解码匹配的多路复用信号。
- **IEEE float / double**（Vector 扩展）— 接受；若关键字无法识别则回退到 int 解码。
- **值表** — 完全支持。信号视图在"值"列中显示解码后的值-名称对。
- **自定义属性** — 接受 `BA_DEF_`；解码层忽略（暂无消费者）。

## 技术选型

| 决策 | 理由 |
|---|---|
| **.NET 10** | 开发机只有 10.0.x SDK。发布的 exe 自包含，目标机器无需特定运行时。 |
| **PEAK: `Peak.PCANBasic.NET` 5.0.1** | PEAK-System 官方包（旧 `Peak.Can.Basic` 在 nuget.org 无法找到）。 |
| **ZLG: `zlgcan.dll` P/Invoke** | 厂商原生库随发布分发，`ZlgNative` 抽象隔离 SDK 细节。 |
| **`PeakCan.HIL.Core` NuGet 包（0.13.0）** | hil-core 抽包后 host / studio 双 pin 同一模型包，格式冻结由版本号强制。 |
| **OxyPlot.Wpf 2.2.0 + ScottPlot.Wpf** | OxyPlot 为时序/统计主引擎；ScottPlot 承接 Trace 图表（v3.16.x 迁移）。 |
| **ClearScript V8** | 脚本引擎宿主（CodeMirror 6 编辑器 + 沙箱执行）。 |
| **WebView2** | HIL HTML 报告内嵌展示。 |
| **`IFileDialogService` 接缝** | 文件对话框可测试注入（v0.7.0）。 |
| **VM 不实现 `IDisposable`** | 所有 ViewModel 都是 DI 单例，生命周期与进程相同。释放会取消订阅永不被释放的单例服务。 |

## 路线图

- **v3.61–v3.65（已完成）** — ZLG USBCAN FD 200U 驱动、BLF 解析器、AI 推理、
  Trace Viewer AI 聊天、ODX 导入 + SecurityAccess 桥接、Flash Pipeline、
  HIL 多通道（spec §3.4 执行接线 + 报告 per-channel DBC 解码）、控制流/参数化 lockstep。
- **近期** — HIL 多通道 UDS（`IsoTpLayer`/`UdsClient` 目前绑定默认通道，多通道化待做）。
- **远期** — J1939 / CANopen，跨平台（Linux + SocketCAN）。

## 许可证

项目内部使用。PCAN-Basic SDK 按 PEAK-System 条款使用；ZLG 驱动按 ZLG 条款使用。