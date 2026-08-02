# PeakCan Host

<img src="src/PeakCan.Host.App/Assets/peakcan-host-banner.png" alt="PeakCan Host 标志 — 连接器框架中的 CAN 总线波形风格，PEAK 橙色 + 深蓝 + 信号青色" width="640">

Windows 专用 WPF 桌面主机，适配 **PEAK PCAN-USB FD / Pro FD** — 通用
CAN 总线监控工具，支持 DBC 解码、手动发送、实时信号视图
和 1 Hz 总线统计。

> **状态:** v3.62.0 — 最新发布。支持 AI 聊天工具、UDS 诊断栈、脚本引擎、
> 多通道录制与回放。**~1228 个单元测试通过**（Core 421 + Infrastructure 84 + App 723）；
> 5 个跳过；NetArchTest 强制执行 5 条架构规则；每次推送 `main` 自动运行 CI。

## 功能特性

- **探测 + 连接 / 断开** — 检测 PEAK PCAN-USB FD，打开 1 Mbps CAN FD 通道，
  注册到进程内帧路由器，随时通过**断开**按钮释放硬件。
- **Trace 视图** — 每个接收帧的虚拟化 DataGrid（时间戳、通道、ID、DLC、十六进制数据、解码行）。
- **Trace 查看器 + 会话持久化（v3.6.0）** — 从 **视图 → Trace 查看器…** 打开；
  加载多个 `.asc` 录音文件并排显示，同步回放，**文件 → 保存会话… / 打开会话…**
  将整个多 Trace 会话保存到 `.tmtrace` 包中。**文件 → 打开最近** 保留最近 5 个包。
- **回放选项卡 + 会话持久化（v3.7.0）** — 循环 / 速度 / 进度条 / CAN-ID 过滤器；
  支持帧级步进、Ctrl+B 书签、命名循环区域。
- **DBC 文件加载** — 在 UI 线程外解析 `.dbc` 文件；显示带发送者、DLC、信号列表的消息表。
- **信号视图** — 每条消息的 DBC 解码实时信号，显示原始十六进制和物理值（系数/偏移量）。
- **手动发送** — 输入 CAN ID + 十六进制数据，点击**发送**；支持 CAN FD 标志切换；
  标准帧（11 位）和扩展帧（29 位）。
- **总线统计** — 1 Hz 帧率 + 总线负载 % 图表；总计数 + 错误帧计数器。
- **AI 聊天（v3.54.0+）** — 集成 LLM 驱动的聊天助手，支持信号搜索、时序分析、
  异常检测、分组管理、信号别名等工具。
- **UDS 诊断栈（v1.1.0）** — ISO 14229 诊断服务，包括会话控制、ECU 复位、
  DID 读写、安全访问、例程控制、DTC 读取与清除、Flash 编程。
- **脚本引擎（v1.0.0）** — JavaScript 脚本，支持 `can.*` / `dbc.*` API，
  CodeMirror 6 编辑器，6 个预置示例脚本。
- **帧录制（v0.5.0）** — 将接收帧录制到 ASC（Vector ASCII）或 CSV 格式。
- **循环发送（v0.5.0）** — 按配置的间隔周期性发送 CAN 帧。
- **Serilog 滚动日志** — 位于 `%LocalAppData%\PeakCan.Host\logs\`。

## HIL 配置器 Studio（独立仓库）

HIL Configurator Studio（TestSuiteBuilder / EcuSimulator / OdxImport 三面板 UI）已移至独立仓库
**`peakcan-studio`**（private，[jasontaotao/peakcan-studio](https://github.com/jasontaotao/peakcan-studio)）；
本仓库保留 HIL 执行引擎（`EcuScriptLoader` / `HILJsonOptions` / 执行与报告）。
**格式冻结约束：** suite/script JSON 模型签名（字段名、camelCase 序列化、step `$kind` 多态判别器、
ECU 脚本 `canIds`/`states|rules` 结构等）由两仓库共享模型保证一致，任何一侧变更必须 lockstep 同步到
另一侧并通过互操作测试（`peakcan-studio` 的 `InteropTests`），否则跨仓库加载直接失败。

## 系统要求

- **Windows 10（1809+）或 Windows 11**（WPF 应用）
- **[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**（开发用；
  发布后的 exe 是自包含的，目标机器无需安装运行时）
- **PEAK PCAN 驱动**（硬件运行需安装，
  [PEAK-System 下载页面](https://www.peak-system.com/PCAN-USB-FD.366.0.html)）

## 构建

```bash
dotnet build PeakCan.Host.slnx -c Release
```

解决方案包含 3 个生产项目（Core / Infrastructure / App）和 3 个测试项目（每层一个）。
构建输出在 `src/<project>/bin/Release/<TFM>/`。

## 运行（从源码）

```bash
dotnet run --project src/PeakCan.Host.App
```

Shell 窗口打开到 **Trace** 选项卡。点击**探测**检测 PCAN 设备，然后**连接**开始接收帧。

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

输出：**~1228 通过 + 5 跳过**（Core 421 / Infrastructure 84 / App ~723 — 3 个硬件跳过 + 2 个无关跳过）。
使用 `dotnet test --collect:"XPlat Code Coverage"` 可生成每个测试项目的 `cobertura.xml` 覆盖率报告。

## 架构

3 层分离，由 NetArchTest 强制执行
（[`tests/PeakCan.Host.Infrastructure.Tests/Architecture/LayeringRulesTests.cs`](tests/PeakCan.Host.Infrastructure.Tests/Architecture/LayeringRulesTests.cs)）：

```
   PeakCan.Host.App            （WPF, MVVM, BackgroundService, DI 组合）
            │
            ▼  使用
   PeakCan.Host.Infrastructure （PEAK SDK 适配器, ChannelRouter, BusStatistics）
            │
            ▼  使用
   PeakCan.Host.Core           （CanFrame, DBC 解析器, SignalDecoder, Result）
```

App 层禁止直接引用 PEAK SDK；所有硬件调用通过 Infrastructure 中的 `IChannelProbe` / `PeakCanChannel`。
CI 会拒绝任何违反边界的 PR。

## DBC 解析器范围

- **支持的关键字**: `VERSION`, `NS_`, `BS_`, `BU_`, `BO_`, `SG_`,
  `VAL_`, `VAL_TABLE_`, `CM_`, `BA_DEF_`, `BA_`, `SIG_GROUP_`, `EV_`
- **多路复用信号 (M / m)** — 完全支持（v0.6.0）。提取多路复用器值；
  仅解码匹配的多路复用信号。
- **IEEE float / double**（Vector 扩展）— 接受；若关键字无法识别则回退到 int 解码。
- **值表** — 完全支持（v0.6.0）。信号视图在"值"列中显示解码后的值-名称对。
- **自定义属性** — 接受 `BA_DEF_`；解码层忽略（暂无消费者）。

## 架构决策

| 决策 | 理由 |
|---|---|
| **.NET 10**（非 8） | 开发机只有 10.0.300 SDK；8.0 不可用。发布的 exe 自包含，目标机器无需特定运行时。 |
| **`Peak.PCANBasic.NET` 5.0.1**（非 `Peak.Can.Basic`） | 旧版 `Peak.Can.Basic` NuGet 包在 nuget.org 上无法找到。`Peak.PCANBasic.NET` 是 PEAK-System 官方替代品（12.7 万次下载）。 |
| **OxyPlot.Wpf 2.2.0**（非 LiveChartsCore 2.0.4） | LiveCharts 2.0.4 的原生依赖（OpenTK + SkiaSharp.Views.WPF）仅面向 .NET Framework，在 .NET 10 上运行时失败。OxyPlot 是纯托管代码，工作正常。 |
| **单硬编码通道 (`0x51`)** | MVP 探测并连接 PCAN-USB FD 的第一个句柄。多通道枚举在 v0.4.0 中添加。 |
| **`IFileDialogService` 接缝** | `DbcViewModel.OpenAsync` 使用 `IFileDialogService`（v0.7.0）替代直接使用 `OpenFileDialog`。测试注入假实现；之前跳过的取消测试现已启用。 |
| **VM 不实现 `IDisposable`** | 所有 ViewModel 都是 DI 单例，生命周期与进程相同。释放它们会取消订阅永不被释放的单例服务——这是一个潜在的隐患。VM 和服务在进程退出时一起消亡。 |

## 路线图

- **v1.0** — 实时信号图表、脚本自动化（CodeMirror 6 + 脚本引擎）。
- **v1.1** — UDS 诊断栈。
- **v2.0** — J1939 / CANopen，跨平台（Linux + SocketCAN）。

## 许可证

项目内部使用。PCAN-Basic SDK 按 PEAK-System 条款使用。