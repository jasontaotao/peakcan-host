# HIL Phase 7 (Unit D): Multi-bus Gateway — 总线间帧转发网关

> Spec date: 2026-08-01
> Depends: Phase 6 (commit `579b02e`) + 单元 A/B/C（`e427acf`/`a1e1ede`/`377c20b`）
> Scope: **CanBusGateway 总线间帧转发 + CLI `--gateway` 接线**。单元 D 是 Phase 7 四个独立单元的最后一个
> （A=DeepSeekOptions ✅，B=Generator 热加载 ✅，C=Web 报告 UI ✅，D=Multi-bus gateway）。
>
> **Revision 2（2026-08-01）**：修正 H1 target 生命周期时序、H2 fire-and-forget（拒 async void）、H3 指纹不含
> Channel/Timestamp、H4 指纹集合 lock、M1 适用模式、M2 Dispose 只退订、M3 字段类型、L1/L2 说明。
>
> **Revision 4（2026-08-01）**：R1 防回环指纹改用转发帧（映射后 Id）的指纹，回环第一轮即命中（原代码用传入帧原始 Id，映屄后 Id 变了不命中）；R2 HIL 模式 CanBusGateway 传 ILogger（转发错误可观测）；R3 --simulate 的 host using 移到 try 外（dispose 顺序对齐 H1 时序表）。
>
> **Revision 3（2026-08-01）**：按 spec review 第二轮修正 —— L1/L3/T1 **移除 `GatewayConfig.SourceChannel`**
> （source 由 CLI 决定：`--hw`→`cli.HardwareChannel`、`--ecu`→`VirtualChannel`、`--matrix`→`EcuMatrix.Channel`；
> 配置只描述 target + 规则），自转发校验移 Program.cs（`--hw`/`--simulate` 比较 `cli.HardwareChannel` vs
> `config.TargetChannel`）；L2 **支持 `--simulate` + `--gateway`**（长运行 ECU 模拟器桥接）；B1 MapToCanId 按值自动
> 选帧格式（>0x7FF→Extended）+ loader 校验 ≤0x1FFFFFFF；B2 GatewayConfigLoader **自校验通道格式**（复制
> USB 语义，错误信息用配置语义，不调 ParseChannelHandle 避免误导）；B3 声明帧率假设；E1 **JSON 示例改小驼峰**
> （`HILJsonOptions.Default` 是 `CamelCase`）；E2 HIL 模式 target 传 `host2` 的 `ILogger<PeakCanChannel>`；
> T3 说明 `Start()` 分离理由；T4 适用模式含 Matrix；T5 File Inventory 只列 `CliArgs.cs`（parser 同文件 :37）。

---

## 1. Goals

HIL 已支持单通道（硬件 / 虚拟 ECU / Matrix / Trace 各注册一个 `ICanChannel`），但**无总线间转发**：
`ChannelRouter` 只是单 router fan-out（多 channel → 多 sink 广播，`ChannelRouter.cs`），
不具备"通道 A 收到帧 → 转发写入通道 B"的网关语义。

**硬件闭环场景**：被测 ECU 挂在 PCAN-B，HIL 引擎/测试工具在 PCAN-A。网关把 A 的帧转发写入 B
（诊断请求给 ECU），把 B 的响应转发回 A。测试引擎只对 A 操作，网关在 A/B 之间透明桥接。

本单元目标：

**D1. `CanBusGateway` 帧转发核心** — 订阅 source channel `FrameReceived`，按规则
（CAN-ID 过滤 / ID 映射 / 单向或双向）转发写入 target channel，带防回环。

**D2. 配置模型** — `GatewayConfig`（JSON，target/双向/ID 过滤/ID 映射）+ `GatewayConfigLoader`（解析 + 校验）。

**D3. CLI 接线** — `--gateway <config.json>`，HIL 硬件模式（`--hw`/`--ecu`/`--matrix`）与 `--simulate` 模式
构造 target 通道 + 网关桥。

---

## 2. Current State

### 2.1 证据

| 项 | 证据 |
|----|------|
| `ICanChannel` 接口 | `WriteAsync(CanFrame, ct)` / `ConnectAsync(baud, fd, ct)` / `Id` / `IsConnected` / `FrameReceived` / `ReadLoopError`（`Core/ICanChannel.cs:19-67`） |
| 现有转发能力 | `ChannelRouter` 只 fan-out（`Infrastructure/Channel/ChannelRouter.cs`），无 bus-to-bus 转发 |
| 硬件通道 | `PeakCanChannel(ChannelId, logger)`（`Infrastructure/Peak/PeakCanChannel.cs:97`），`ConnectAsync(BaudRate, fd, ct)` |
| 通道解析 | `HeadlessHostBuilder.ParseChannelHandle("USB1") → 0x51`（`HeadlessHostBuilder.cs:228`） |
| 虚拟回环通道 | `VirtualChannel`：WriteAsync → bounded Channel → ConsumerLoop → `FrameReceived`（`Infrastructure/Channel/VirtualChannel.cs:66-102`），`Id = ChannelId.None`（`:19`）—— 天然 loopback |
| Matrix 通道 | `EcuMatrix.Channel`（VirtualChannel），`HeadlessHostBuilder.cs:70` 注册 `ICanChannel = matrix.Channel` |
| Trace 通道写路径 | `TraceDrivenChannel` 类注释 `:12` 写 "WriteAsync is a no-op (Sprint 2)"，但 **Sprint 3 已改为 loopback**（`:157-167`）—— 写路径语义不符 |
| 帧结构 | `CanFrame(CanId Id, ReadOnlyMemory<byte> Data, FrameFlags Flags, ChannelId Channel, Timestamp)`（`Core/CanFrame.cs:15`）—— record struct，`frame with { Channel = ... }` 重写来源 |
| CAN ID | `CanId.Raw`（uint），`new CanId(uint raw, FrameFormat format)`；标准 ID ≤ 0x7FF（`Core/CanId`） |
| JSON 命名策略 | `HILJsonOptions.Default` = **CamelCase** + indented + ignore null（`Core/HIL/Serialization/HILJsonOptions.cs`）—— JSON 键用小驼峰 |
| CLI 参数 | `CliArgs` positional record，新增字段加末尾（`CliArgs.cs:32`）；`CliArgsParser` **同文件**（`CliArgs.cs:37`） |
| HIL 模式执行 | `Program.cs:64-135`：`HeadlessHostBuilder.Build(cli)` → 取 `ICanChannel`（source）→ `ConnectAsync` → `engine.ExecuteAsync` → `DisconnectAsync` |
| `--simulate` 模式 | `Program.cs:44-56`：`PeakCanChannel` + `EcuSimulatorHost` 长运行（Ctrl+C 退出） |
| 测试 Fake 通道 | 各测试文件的 `private sealed class FakeChannel : ICanChannel`；`VirtualChannel` 可作 loopback 测试双通道 |

### 2.2 现状结论

- 转发所需接口齐备（`FrameReceived` 订阅 + `WriteAsync` 写回），核心是**新的 `CanBusGateway` 组合类**。
- `ChannelRouter` 是 fan-out 广播，不是转发——不修改（保持 42 个调用方稳定），新建独立网关类。
- `VirtualChannel` 天然 loopback，是防回环测试载体（两个 VirtualChannel 桥接形成 A→B→A 环）。
- **source 通道由 CLI 决定，不属于 GatewayConfig**：`--hw`→`cli.HardwareChannel` 的 `PeakCanChannel`，
  `--ecu`→`VirtualChannel`，`--matrix`→`EcuMatrix.Channel`，`--simulate`→`PeakCanChannel(cli.HardwareChannel)`。
  网关的 target 始终是物理 `PeakCanChannel`（由 `config.TargetChannel` 指定）。
- **适用模式**：`--hw`/`--ecu`/`--matrix`/`--simulate` 的 source `WriteAsync` 均有效，网关可用；
  `--trace`（`TraceDrivenChannel`）source 是回放 trace，网关无实际用途（文档说明，不硬校验）。

---

## 3. Design

### 3.1 配置模型（`Core/HIL/Gateway/GatewayConfig.cs`）

**L1/T1：移除 `SourceChannel`** —— source 由 CLI 决定（`--hw`/`--ecu`/`--matrix`/`--simulate`），
`GatewayConfig` 只描述**转发目标** + 规则，避免"source 是哪个通道"的歧义：

```csharp
namespace PeakCan.Host.Core.HIL.Gateway;

/// <summary>
/// 总线间转发网关配置。描述转发目标通道 + 规则；source 通道由 CLI 模式决定（--hw/--ecu/--matrix/--simulate）。
/// </summary>
public sealed record GatewayConfig(
    string TargetChannel,       // 目标通道名 "USB2"（转发写入的物理通道；source 不在此配置）
    bool Bidirectional = false, // 双向转发（默认单向 source→target）
    uint? MinCanId = null,      // CAN-ID 范围过滤（含边界）
    uint? MaxCanId = null,
    uint? MapToCanId = null);   // 可选 CAN-ID 映射（转发时改写 Id；null = 不映射）
```

**E1：JSON 键用小驼峰**（`HILJsonOptions.Default` 是 `CamelCase`）：

```json
{
  "targetChannel": "USB2",
  "bidirectional": true,
  "minCanId": 4096,
  "maxCanId": 8191
}
```

- 过滤是**范围**（Min/Max，含边界）；映射是**单 ID**（MapToCanId 把通过的帧改写为该 ID）。
- Core 层纯数据 record，无外部依赖。

### 3.2 `GatewayConfigLoader`（`Infrastructure/Channel/Gateway/GatewayConfigLoader.cs`）

```csharp
namespace PeakCan.Host.Infrastructure.Channel.Gateway;

public static class GatewayConfigLoader
{
    /// <summary>从 JSON 文件加载并校验 GatewayConfig。非法配置抛 ArgumentException。</summary>
    public static GatewayConfig Load(string path);
    /// <summary>从 JSON 字符串加载（测试用）。</summary>
    public static GatewayConfig Parse(string json);
}
```

**B2：通道格式自校验（不调用 `ParseChannelHandle`）** —— 复制 USB 语义但包装成配置错误信息：

```csharp
// 校验 TargetChannel："USB" + 1..16（复制 HeadlessHostBuilder.ParseChannelHandle 的格式语义，
// 但错误信息用配置语义，不传播 "hardware channel" 措辞误导）
private static void ValidateChannelName(string channel)
{
    if (string.IsNullOrWhiteSpace(channel) ||
        !channel.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ||
        !ushort.TryParse(channel[3..], out var n) ||
        n is < 1 or > 16)
        throw new ArgumentException($"GatewayConfig.TargetChannel '{channel}' is invalid. Expected USB1..USB16.");
}
```

校验规则：
- `TargetChannel` 非空、`USB1..USB16`（自校验，错误信息 `GatewayConfig.TargetChannel ... is invalid`，B2）。
- `MinCanId ≤ MaxCanId`（两者都提供时）。
- **B1：`MapToCanId ≤ 0x1FFFFFFF`**（29 位 CAN ID 上限）。
- **自转发校验不在 loader**（source 不在 config）——由 Program.cs 在 `--hw`/`--simulate` 模式比较
  `cli.HardwareChannel` vs `config.TargetChannel`（§3.4）。

### 3.3 `CanBusGateway`（`Infrastructure/Channel/Gateway/CanBusGateway.cs`）

**完整实现（H2/H3/H4/B1/M2/L1/T3）**：

```csharp
namespace PeakCan.Host.Infrastructure.Channel.Gateway;

/// <summary>
/// 总线间帧转发网关：订阅 source.FrameReceived，按 GatewayConfig 过滤/映射后写入 target.WriteAsync。
/// 双向时对称订阅。防回环用"最近转发指纹 + 时间窗去重"。
/// </summary>
public sealed class CanBusGateway : IAsyncDisposable
{
    private const int AntiLoopbackWindowMs = 100;
    private readonly ICanChannel _source;
    private readonly ICanChannel _target;
    private readonly GatewayConfig _config;
    private readonly ILogger<CanBusGateway>? _logger;

    // H4: 最近转发指纹集合。双向网关的 OnSourceFrame/OnTargetFrame 分别在 source/target
    //     读循环线程执行（两线程并发），所有访问必须持 _recentLock。
    private readonly List<(uint Id, int Hash, DateTime Timestamp)> _recent = new();
    private readonly object _recentLock = new();
    private bool _started;

    // T3: 构造只做 null 校验 + 存依赖（无副作用）；Start() 显式启动订阅 —— 测试可构造后控制订阅时机，
    //     构造不自动开始（对齐 ChannelRouter.RegisterChannel 显式调用的惯例）。
    public CanBusGateway(ICanChannel source, ICanChannel target, GatewayConfig config,
        ILogger<CanBusGateway>? logger = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger;
    }

    /// <summary>订阅 source（双向时也订阅 target）FrameReceived。幂等。</summary>
    public void Start()
    {
        if (_started) return;
        _source.FrameReceived += OnSourceFrame;
        if (_config.Bidirectional) _target.FrameReceived += OnTargetFrame;
        _started = true;
    }

    // M2: DisposeAsync 只退订事件，不 dispose source/target channel —— channel 生命周期归调用方
    //     （Program.cs 负责 target；source 由 HeadlessHostBuilder/host 管理）。
    public ValueTask DisposeAsync()
    {
        if (_started)
        {
            _source.FrameReceived -= OnSourceFrame;
            _target.FrameReceived -= OnTargetFrame;
            _started = false;
        }
        return ValueTask.CompletedTask;
    }

    private void OnSourceFrame(CanFrame frame) => Forward(frame, _target);
    private void OnTargetFrame(CanFrame frame) => Forward(frame, _source);

    private void Forward(CanFrame frame, ICanChannel destination)
    {
        // 1. CAN-ID 范围过滤（含边界，用原始 Id）
        if (_config.MinCanId is { } min && frame.Id.Raw < min) return;
        if (_config.MaxCanId is { } max && frame.Id.Raw > max) return;

        // 2. ID 映射 + 重写 Channel 为目标通道。
        //    B1: 按 MapToCanId 值自动选帧格式 -- map > 0x7FF 时目标必须是扩展帧（标准 ID 上限 0x7FF）。
        //    L1: Channel 改写对 PeakCanChannel 无实际效果（WriteAsync 只用 Id/Data/Flags），
        //        但保持帧来源标记一致；对 VirtualChannel loopback 有意义。
        var id = _config.MapToCanId is { } map
            ? new CanId(map, map > 0x7FF ? FrameFormat.Extended : FrameFormat.Standard)
            : frame.Id;
        var forwarded = frame with { Id = id, Channel = destination.Id };

        // 3. 防回环：用**转发帧**指纹去重（R1：映射后的 Id，回环中收到的帧 Id 与此一致 -> 命中）。
        //    不含方向 -- 环中任意方向的重复帧都被丢弃。
        if (!TryMarkRecent(forwarded)) return;

        // 4. H2: fire-and-forget async Task（非 async void）-- 异常在 WriteSafeAsync 内部捕获，
        //    不冒泡到 SynchronizationContext。读线程不阻塞。
        _ = WriteSafeAsync(destination, forwarded);
    }

    // H3: 指纹 = (Id.Raw, HashCode(Data.Span, Flags))。
    //     不含 Channel（转发会改写）、不含 Timestamp（PeakCanChannel 读循环每次打新时间戳）。
    //     含 Flags 区分 RTR/FD 等，避免同 Id 同 Data 不同 Flags 的帧被误杀。
    private bool TryMarkRecent(CanFrame frame)
    {
        var hash = DataHash(frame.Data.Span, frame.Flags);
        lock (_recentLock)
        {
            var cutoff = DateTime.UtcNow.AddMilliseconds(-AntiLoopbackWindowMs);
            _recent.RemoveAll(r => r.Timestamp < cutoff);
            foreach (var r in _recent)
                if (r.Id == frame.Id.Raw && r.Hash == hash)
                    return false;   // 窗口内重复 → 丢弃（防回环）
            _recent.Add((frame.Id.Raw, hash, DateTime.UtcNow));
            return true;
        }
    }

    private static int DataHash(ReadOnlySpan<byte> data, FrameFlags flags)
    {
        var hc = new HashCode();
        hc.AddBytes(data);
        hc.Add(flags);
        return hc.ToHashCode();
    }

    // H2: 转发写。async Task 方法（非 async void）—— 所有异常在方法内捕获。
    private async Task WriteSafeAsync(ICanChannel channel, CanFrame frame)
    {
        try
        {
            var result = await channel.WriteAsync(frame).ConfigureAwait(false);
            if (!result.IsSuccess)
                _logger?.LogWarning("Gateway forwarding failed: {Error}", result.Error?.Message);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Gateway forwarding threw on {Channel}", channel.Id);
        }
    }
}
```

**防回环语义（关键）**：
- 指纹**不含方向**：A→B 转发记录 `(Id.Raw, Hash, ts)`，B 收到（loopback 或物理回环）→ B→A 转发时检查同一指纹 → 命中 → 丢弃。环在第一次转发后即终止。
- 时间窗 100ms：正常 ECU 响应间隔 >100ms，不误杀；诊断请求/响应同帧 id+data 在窗口内重复罕见。
- **H3 明确**：指纹基于 `Id` + `Data` + `Flags`，**不含 `Channel` / `Timestamp`**（这两个字段转发/读循环会变）。

**H4 线程安全契约**：`OnSourceFrame` 在 source 读循环线程、`OnTargetFrame` 在 target 读循环线程（双向时两个线程并发）。`_recent` 指纹集合所有访问（`RemoveAll` / `foreach` / `Add`）都在 `lock (_recentLock)` 内。
**B3 帧率假设**：诊断场景帧率低（几十帧/秒），lock 竞争与 O(n) 清理可接受；不做高帧率优化（CAN 满载场景
非本单元目标，声明在 Out of Scope）。

**H2 明确**：转发写用 `async Task WriteSafeAsync` + `_ = ...` fire-and-forget。**禁止 `async void`**——未观察异常
会冒泡到 `SynchronizationContext`，在 console 应用终止进程。`WriteSafeAsync` 内部 try/catch 捕获全部异常
（含 `WriteAsync` 抛异常与返回失败 `Result`）。

**错误处理**：转发失败记录日志，不影响读线程（读线程高优先级，对齐 `ChannelRouter` 的 sink 隔离模式）。

### 3.4 CLI 接线（`Infrastructure/Cli/CliArgs.cs` + `PeakCan.Host.Cli/Program.cs`）

**`CliArgs`** 末尾加（positional record，现有调用零改动）：

```csharp
string? GatewayPath = null);   // Multi-bus gateway 配置 JSON 路径
```

**`CliArgsParser`**（`CliArgs.cs:37` 同文件）：`case "--gateway": gatewayPath = args[++i]; break;` + PrintHelp。
**M4/T5：三处** `new CliArgs(...)` 构造调用点（`:87,100,117`）加 `GatewayPath: gatewayPath`（参照 Phase 7 B
`GeneratorDir` precedent）。

**自转发校验（L3）**：`--hw`/`--simulate` 模式（source 是物理通道）若 `cli.HardwareChannel`（大小写不敏感）
== `config.TargetChannel` → 抛 `ArgumentException("Gateway source and target cannot be the same channel.")`。
`--ecu`/`--matrix`（source 是虚拟通道，无通道名）不做此校验。

**HIL 模式（`--hw`/`--ecu`/`--matrix` + `--gateway`）**：

**H1 target 生命周期时序表**：

| 步骤 | 动作 | 时机 |
|------|------|------|
| 1 | `config = GatewayConfigLoader.Load(cli.GatewayPath)` | gateway 分支 |
| 2 | 自转发校验（`--hw`：`cli.HardwareChannel == config.TargetChannel` → 抛） | 创建 target 前 |
| 3 | `targetChannel = new PeakCanChannel(ParseChannelHandle(config.TargetChannel), logger)` | 从 `host2.Services` 拿 `ILogger<PeakCanChannel>`（E2） |
| 4 | `await targetChannel.ConnectAsync(CanFd1Mbps, fd: true)` | source connect **之前**（gateway 需要 target 可写） |
| 5 | `gateway = new CanBusGateway(channel2, targetChannel, config)` + `gateway.Start()` | target connect 后、engine 前 |
| 6 | `await channel2.ConnectAsync(CanFd1Mbps, fd: true)` | 现有 |
| 7 | `engine.ExecuteAsync(...)` | 现有 |
| 8 | `await gateway.DisposeAsync()` | finally **最先**（退订，停止转发） |
| 9 | `await targetChannel.DisconnectAsync()` | finally 其次（断开 target 物理 handle） |
| 10 | `await channel2.DisconnectAsync()` | finally 最后（现有） |

```csharp
using var host2 = HeadlessHostBuilder.Build(cli);
var engine = host2.Services.GetRequiredService<TestSuiteEngine>();
var channel2 = host2.Services.GetRequiredService<ICanChannel>();   // source (--hw/--ecu/--matrix)

CanBusGateway? gateway = null;
PeakCanChannel? targetChannel = null;
if (cli.GatewayPath is not null)
{
    var config = GatewayConfigLoader.Load(cli.GatewayPath);
    if (cli.HardwareChannel is not null &&
        string.Equals(cli.HardwareChannel, config.TargetChannel, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Gateway source and target cannot be the same channel.");
    // E2: target channel 也传 ILogger，连接/写/读循环错误可观测（非 null）。
    var logger = host2.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PeakCanChannel>>();
    targetChannel = new PeakCanChannel(new ChannelId(HeadlessHostBuilder.ParseChannelHandle(config.TargetChannel)), logger);
    await targetChannel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);          // 步骤 4
    var gwLogger = host2.Services.GetService<ILogger<CanBusGateway>>();
    gateway = new CanBusGateway(channel2, targetChannel, config, gwLogger);
    gateway.Start();                                                          // 步骤 5
}
try
{
    await channel2.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);               // 步骤 6
    // ... 现有 suite 反序列化 + engine.ExecuteAsync + 报告 ...
}
finally
{
    if (gateway is not null) await gateway.DisposeAsync();                    // 步骤 8
    if (targetChannel is not null) await targetChannel.DisconnectAsync();     // 步骤 9
    await channel2.DisconnectAsync();                                         // 步骤 10
}
```

**H1 异常路径**：`Load` 抛（配置非法）→ try 前退出，无 target 创建；`targetChannel.ConnectAsync` 抛（无硬件）→
未连接（`IsConnected=false`），`PeakCanChannel` 未持有 PCAN handle，无需清理，`host2` 由 `using` 管理；
`engine.ExecuteAsync` 抛 → finally 执行 8→9→10。

**`--simulate` + `--gateway`（L2）**：长运行 ECU 模拟器桥接到 target 物理通道（最常用场景 —— ECU 持续运行，
网关让模拟 ECU 同时服务两条总线）。复用 `CanBusGateway` 核心类，`--simulate` 分支（`Program.cs:44-56`）扩展：

```csharp
using var manager = new GeneratorPluginManager(cli.GeneratorDir);
var ecuScript = EcuScriptLoader.Load(cli.EcuScriptPath!, manager.Current);
manager.ApplyTo(ecuScript.StateMachine);
var handle = HeadlessHostBuilder.ParseChannelHandle(cli.HardwareChannel!);
var channel = new PeakCanChannel(new ChannelId(handle), null);

// L2: --simulate + --gateway（长运行场景网关最有价值）。target 生命周期本分支管理。
CanBusGateway? gateway = null;
PeakCanChannel? targetChannel = null;
if (cli.GatewayPath is not null)
{
    var config = GatewayConfigLoader.Load(cli.GatewayPath);
    if (string.Equals(cli.HardwareChannel, config.TargetChannel, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Gateway source and target cannot be the same channel.");
    // --simulate 分支无 DI 容器，target 保持 null logger（与现有 channel 创建一致，`:54`）。
    targetChannel = new PeakCanChannel(new ChannelId(HeadlessHostBuilder.ParseChannelHandle(config.TargetChannel)), null);
    await targetChannel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
    gateway = new CanBusGateway(channel, targetChannel, config, null);
    gateway.Start();
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
Console.WriteLine($"Simulating ECU '{ecuScript.Name}' on {cli.HardwareChannel}. Press Ctrl+C to exit.");
// R3: host 放 try 外 -- using dispose 在 finally 之后（source 断开在网关退订 + target 断开之后），
//     对齐 H1 时序表“先停网关 -> 断 target -> 断 source”。
using var host = new EcuSimulatorHost(channel, ecuScript.CanIds, ecuScript.StateMachine, null);
try
{
    await host.RunAsync(cts.Token);
}
finally
{
    // 长运行结束（Ctrl+C）：先停网关退订 → 断开 target → host using 逆序 dispose。
    if (gateway is not null) await gateway.DisposeAsync();
    if (targetChannel is not null) await targetChannel.DisconnectAsync();
}
```

- **T4 适用模式**：`--hw`/`--ecu`/`--matrix`/`--simulate` 均支持网关（source 写路径有效）。
  `--trace` 不校验拒绝但无实际用途（`TraceDrivenChannel.WriteAsync` 实为 loopback，`:157-167`，类注释 `:12`
  "no-op" 陈旧）——文档说明。
- **L2 波特率**：source/target 均固定 `BaudRate.CanFd1Mbps`（对齐 `Program.cs:80`）。跨波特率桥接 Out of Scope。

---

## 4. File Inventory

| 文件 | 动作 |
|------|------|
| `src/PeakCan.Host.Core/HIL/Gateway/GatewayConfig.cs` | NEW — 配置 record（§3.1） |
| `src/PeakCan.Host.Infrastructure/Channel/Gateway/GatewayConfigLoader.cs` | NEW — 解析 + 校验（§3.2） |
| `src/PeakCan.Host.Infrastructure/Channel/Gateway/CanBusGateway.cs` | NEW — 转发核心（§3.3） |
| `src/PeakCan.Host.Infrastructure/Cli/CliArgs.cs` | MODIFY — `GatewayPath` 字段（record 末尾）+ `CliArgsParser`（**同文件 :37**）`--gateway` 解析 + help + 三处构造点传参（T5） |
| `src/PeakCan.Host.Cli/Program.cs` | MODIFY — HIL 模式 + `--simulate` 模式 gateway 分支（§3.4） |
| `tests/PeakCan.Host.Infrastructure.Tests/Channel/Gateway/CanBusGatewayTests.cs` | NEW |
| `tests/PeakCan.Host.Infrastructure.Tests/Channel/Gateway/GatewayConfigLoaderTests.cs` | NEW |

> `HeadlessHostBuilder.cs` **不在 File Inventory**（gateway 接线在 Program.cs 层，HeadlessHostBuilder 不动）。
> **M4/T5 关键约束**：`CliArgsParser` 三处 `new CliArgs(...)`（`:87,100,117`）全部加 `GatewayPath: gatewayPath`；
> parser 与 record 同在 `CliArgs.cs`，无独立文件。

---

## 5. Testing (TDD)

**`CanBusGateway`（用两个可控 fake/loopback 通道）**：

| 用例 | 断言 |
|------|------|
| 单向转发 | source 收到帧 → target.WriteAsync 收到该帧（数据一致、Channel 重写为 target.Id） |
| ID 过滤 | `MinCanId/MaxCanId` 范围外帧不转发、范围内（含边界）转发 |
| ID 映射（B1） | `MapToCanId ≤ 0x7FF` 帧 Id 改写为 map、格式 Standard；`MapToCanId > 0x7FF` 帧格式变 Extended |
| 双向转发 | `Bidirectional=true` → target 帧转发回 source |
| 双向防回环 | 双向 + 两个 loopback 通道 → 帧只转发一次、无无限环（转发计数 ≤ 预期） |
| 时间窗去重（H3） | 窗口内同 (Id, Data, Flags) 重复帧丢弃；窗口外（>100ms）再次转发 |
| 指纹不含 Channel/Timestamp（H3） | 同 Id+Data+Flags 但不同 Channel/Timestamp 的帧 → 去重命中（证明指纹不含这两字段） |
| 转发失败不抛（H2） | target.WriteAsync 返回失败 / 抛异常 → 网关不抛、读线程不中断 |
| Dispose 退订（M2） | Dispose 后 source.FrameReceived 不再触发转发；source/target channel 未被 dispose（调用方可继续用） |
| 线程安全（H4） | 双向 + 并发帧注入（多线程）→ 不抛、无死锁、转发计数正确 |

**`GatewayConfigLoader`**：

| 用例 | 断言 |
|------|------|
| 合法配置解析 | 完整 JSON（小驼峰键）→ GatewayConfig 字段正确 |
| 默认值 | 缺省 Bidirectional/MinCanId 等 → 默认 false/null |
| Target 缺失 | 抛 ArgumentException（错误信息含 `TargetChannel`） |
| 非法通道格式（B2） | `"USB17"` / `"COM1"` / `""` → 抛 ArgumentException（信息 `GatewayConfig.TargetChannel ... invalid`） |
| Min > Max | 抛 ArgumentException |
| MapToCanId 越界（B1） | `MapToCanId = 0x20000000`（> 29 位）→ 抛 ArgumentException |

- 测试通道：`VirtualChannel`（loopback）用于防回环；或定义本地 fake 记录 `WriteAsync` 调用 + 计数。
- 时钟依赖：时间窗去重用真实 `DateTime.UtcNow`（100ms 窗口足够稳定）。

---

## 6. Out of Scope

- **WPF 网关配置 UI** — 本单元 CLI 闭环；WPF 面板加网关配置（HilView 扩展）留后续
- **多网关拓扑管理** — 本单元单 source→target 网关；任意 N×N 路由后续
- **`ChannelRouter` 改造** — 保持 fan-out 语义不变，不并入转发能力
- **帧内容变换**（非 ID 映射的数据改写）— 仅支持 CAN-ID 过滤 + 映射
- **跨波特率桥接**（L2）— source/target 固定 `CanFd1Mbps`；不同波特率 source/target 后续
- **Trace replay 模式网关**（M1）— `TraceDrivenChannel` 写路径语义不符，文档说明无实际用途
- **高帧率优化**（B3）— 诊断场景低帧率，lock 竞争可接受；CAN 满载优化后续
