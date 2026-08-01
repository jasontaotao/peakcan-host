# HIL Phase 7 Unit D TDD Plan: Multi-bus Gateway

> Spec: `docs/superpowers/specs/2026-08-01-hil-phase7-unit-d-gateway-spec.md` (Rev 4, 0 CRITICAL)
> Created: 2026-08-01
> Sprints: 2 | Increments: 5 | Tests: 16

---

## Pre-checks (verify before coding)

| # | Check | Command | Expected |
|---|-------|---------|----------|
| 0 | Build passes | `dotnet build` | 0 errors |
| 1 | HIL tests green | `dotnet test --filter "FullyQualifiedName~HIL"` | 0 new failures |
| 2 | `ICanChannel.FrameReceived` is `Action<CanFrame>?` | grep `event Action<CanFrame>` in `ICanChannel.cs` | 1 match |
| 3 | `CanFrame` is `readonly record struct` with `with` support | grep `readonly record struct CanFrame` in `CanFrame.cs` | 1 match |
| 4 | `VirtualChannel.Id` is `ChannelId.None` | grep `ChannelId.None` in `VirtualChannel.cs` | 1 match |
| 5 | `HILJsonOptions.Default` is CamelCase | grep `CamelCase` in `HILJsonOptions.cs` | 1 match |
| 6 | `CliArgsParser` in `CliArgs.cs` (same file) | grep `class CliArgsParser` in `CliArgs.cs` | 1 match |
| 7 | 3 `new CliArgs(` constructor calls | grep -n `new CliArgs(` in `CliArgs.cs` | 3 matches (:87,100,117) |
| 8 | `CanBusGateway` does not exist | grep `CanBusGateway` in src/ | 0 matches |
| 9 | `GatewayConfig` does not exist | grep `GatewayConfig` in src/ | 0 matches |
| 10 | `HeadlessHostBuilder.ParseChannelHandle` is public static | grep `public static.*ParseChannelHandle` in `HeadlessHostBuilder.cs` | 1 match |

---

## Sprint 1: 核心组件 (12 tests)

### Inc 1: `GatewayConfig` + `GatewayConfigLoader` + 测试

**Files**: `Core/HIL/Gateway/GatewayConfig.cs` (NEW), `Infrastructure/Channel/Gateway/GatewayConfigLoader.cs` (NEW), `Infrastructure.Tests/Channel/Gateway/GatewayConfigLoaderTests.cs` (NEW)

| Test | Description |
|------|-------------|
| `Parse_ValidJson_ReturnsConfig` | 完整 JSON（小驼峰键）-> `TargetChannel`/`Bidirectional`/`MinCanId`/`MaxCanId`/`MapToCanId` 字段正确 |
| `Parse_DefaultValues` | 缺省 `Bidirectional`/`MinCanId`/`MaxCanId`/`MapToCanId` -> `false`/`null`/`null`/`null` |
| `Parse_TargetMissing_Throws` | `TargetChannel` 缺失 -> `ArgumentException` |
| `Parse_InvalidChannel_Throws` | `"USB17"` / `"COM1"` / `""` -> `ArgumentException`（信息含 `TargetChannel ... invalid`） |
| `Parse_MinGreaterThanMax_Throws` | `MinCanId=100, MaxCanId=50` -> `ArgumentException` |
| `Parse_MapToCanIdOverflow_Throws` | `MapToCanId=0x20000000`（>29 位） -> `ArgumentException` |

**Implementation**:
- `GatewayConfig.cs`：`record GatewayConfig(string TargetChannel, bool Bidirectional = false, uint? MinCanId = null, uint? MaxCanId = null, uint? MapToCanId = null)`
- `GatewayConfigLoader.cs`：
  - `Load(string path)` -> `File.ReadAllText` -> `Parse`
  - `Parse(string json)` -> `JsonSerializer.Deserialize<GatewayConfig>(json, HILJsonOptions.Default)` -> 校验
  - `ValidateChannelName`：USB + 1..16 自校验（不调 `ParseChannelHandle`，B2）
  - 校验 `MinCanId ≤ MaxCanId`、`MapToCanId ≤ 0x1FFFFFFF`（B1）

### Inc 2: `CanBusGateway` + 测试

**Files**: `Infrastructure/Channel/Gateway/CanBusGateway.cs` (NEW), `Infrastructure.Tests/Channel/Gateway/CanBusGatewayTests.cs` (NEW)

| Test | Description |
|------|-------------|
| `Forward_SingleDirection_TargetReceivesFrame` | source 收到帧 -> target.WriteAsync 收到该帧（Data 一致、Channel 重写为 target.Id） |
| `Forward_CanIdFilter_RangeExcluded` | `MinCanId/MaxCanId` 范围外帧不转发、范围内（含边界）转发 |
| `Forward_MapToCanId_RewritesId` | `MapToCanId ≤ 0x7FF` -> Standard 格式；`> 0x7FF` -> Extended 格式（B1） |
| `Forward_Bidirectional_TargetToSource` | `Bidirectional=true` -> target 帧转发回 source |
| `AntiLoopback_Bidirectional_NoInfiniteLoop` | 双向 + 两个 loopback 通道 -> 帧只转发有限次、无无限环 |
| `AntiLoopback_TimeWindow_Dedup` | 窗口内同 (Id, Data, Flags) 重复帧丢弃；窗口外（>100ms）再次转发 |
| `AntiLoopback_FingerprintExcludesChannelTimestamp` | 同 Id+Data+Flags 但不同 Channel/Timestamp -> 去重命中 |
| `AntiLoopback_MapToCanId_Bidirectional_FirstRoundHit` | ID 映射 + 双向 -> 回环第一轮即命中（R1：指纹用转发帧映射后 Id） |
| `Forward_WriteFails_NoThrow` | target.WriteAsync 返回失败 / 抛异常 -> 网关不抛、读线程不中断（H2） |
| `Dispose_Unsubscribes_ChannelsAlive` | Dispose 后 source.FrameReceived 不再触发转发；source/target channel 未被 dispose（M2） |

**Implementation**:
- `CanBusGateway(ICanChannel source, ICanChannel target, GatewayConfig config, ILogger<CanBusGateway>? logger)` : `IAsyncDisposable`
- `Start()`：幂等，订阅 `source.FrameReceived`（双向时也订阅 `target.FrameReceived`）
- `Forward(frame, destination)`：过滤（原始 Id）-> 映射 -> `TryMarkRecent(forwarded)`（R1：用转发帧指纹）-> fire-and-forget `WriteSafeAsync`
- `TryMarkRecent(CanFrame)`：指纹 = `(Id.Raw, HashCode(Data.Span, Flags))`，`lock(_recentLock)`，100ms 窗口清理
- `WriteSafeAsync`：async Task，内部 try/catch 全部异常
- `DisposeAsync()`：只退订事件，不 dispose channel

**Key constraint (R1)**: `TryMarkRecent(forwarded)` 在映射之后调用，用转发帧（映射后 Id）的指纹。回环中收到的帧 Id 与此一致 -> 第一轮即命中。

**测试通道**：用本地 fake `ICanChannel`（记录 `WriteAsync` 调用 + 计数 + 可控 `FrameReceived` 触发），或 `VirtualChannel`（loopback 验证防回环）。

---

## Sprint 2: CLI 接线 (4 tests)

### Inc 3: `CliArgs` + `CliArgsParser` 改动

**Files**: `Infrastructure/Cli/CliArgs.cs` (MODIFY)

| Test | Description |
|------|-------------|
| `CliArgsParser_GatewayPath_ParsesFlag` | `--gateway config.json` sets `GatewayPath` |
| (编译期验证) | 3 处 `new CliArgs(...)` 均传 `GatewayPath: gatewayPath` |

**Implementation**:
- `CliArgs` record 末尾加 `string? GatewayPath = null`
- `CliArgsParser.Parse`：新增 `string? gatewayPath = null;` + `case "--gateway": gatewayPath = args[++i]; break;`
- **三处** `new CliArgs(...)` 构造（`:87,100,117`）加 `GatewayPath: gatewayPath`（M4/T5）
- `PrintHelp` 加 `--gateway <path>` 行

### Inc 4: `Program.cs` HIL 模式 gateway 分支

**Files**: `PeakCan.Host.Cli/Program.cs` (MODIFY)

| Test | Description |
|------|-------------|
| (集成验证) | `--hw USB1 --gateway config.json --suite tests.json` -> target channel 创建 + 连接 + 网关启动 + engine 执行 + finally dispose |

**Implementation** (H1 时序表 8->9->10 dispose 顺序):
```csharp
CanBusGateway? gateway = null;
PeakCanChannel? targetChannel = null;
if (cli.GatewayPath is not null)
{
    var config = GatewayConfigLoader.Load(cli.GatewayPath);
    // 自转发校验（--hw 模式）
    if (cli.HardwareChannel is not null &&
        string.Equals(cli.HardwareChannel, config.TargetChannel, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Gateway source and target cannot be the same channel.");
    var logger = host2.Services.GetRequiredService<ILogger<PeakCanChannel>>();
    targetChannel = new PeakCanChannel(new ChannelId(HeadlessHostBuilder.ParseChannelHandle(config.TargetChannel)), logger);
    await targetChannel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
    var gwLogger = host2.Services.GetService<ILogger<CanBusGateway>>();
    gateway = new CanBusGateway(channel2, targetChannel, config, gwLogger);
    gateway.Start();
}
try { await channel2.ConnectAsync(...); /* engine.ExecuteAsync */ }
finally
{
    if (gateway is not null) await gateway.DisposeAsync();      // 8: 退订
    if (targetChannel is not null) await targetChannel.DisconnectAsync(); // 9: target
    await channel2.DisconnectAsync();                           // 10: source
}
```

### Inc 5: `Program.cs` --simulate 模式 gateway 分支

**Files**: `PeakCan.Host.Cli/Program.cs` (MODIFY, continued)

| Test | Description |
|------|-------------|
| (集成验证) | `--simulate --hw USB1 --ecu script.json --gateway config.json` -> target 创建 + 网关桥接 ECU 模拟器 |

**Implementation** (R3: `using var host` 在 try 外):
```csharp
CanBusGateway? gateway = null;
PeakCanChannel? targetChannel = null;
if (cli.GatewayPath is not null)
{
    var config = GatewayConfigLoader.Load(cli.GatewayPath);
    if (string.Equals(cli.HardwareChannel, config.TargetChannel, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Gateway source and target cannot be the same channel.");
    targetChannel = new PeakCanChannel(new ChannelId(HeadlessHostBuilder.ParseChannelHandle(config.TargetChannel)), null);
    await targetChannel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true);
    gateway = new CanBusGateway(channel, targetChannel, config, null);
    gateway.Start();
}
// R3: host 放 try 外 -- dispose 在 finally 之后（source 断开在网关退订 + target 断开之后）
using var host = new EcuSimulatorHost(channel, ecuScript.CanIds, ecuScript.StateMachine, null);
try { await host.RunAsync(cts.Token); }
finally
{
    if (gateway is not null) await gateway.DisposeAsync();
    if (targetChannel is not null) await targetChannel.DisconnectAsync();
}
```

---

## Post-checks (verify after coding)

| # | Check | Command | Expected |
|---|-------|---------|----------|
| 0 | Build passes | `dotnet build` | 0 errors |
| 1 | All new tests green | `dotnet test --filter "FullyQualifiedName~CanBusGateway\|FullyQualifiedName~GatewayConfigLoader\|FullyQualifiedName~CliArgsParser_GatewayPath"` | 0 failed |
| 2 | Existing HIL tests green | `dotnet test --filter "FullyQualifiedName~HIL"` | 0 new failures (既有 TraceViewer 失败除外) |
| 3 | `CanBusGateway` exists | grep `class CanBusGateway` in src/ | 1 match |
| 4 | `GatewayConfig` exists | grep `record GatewayConfig` in src/ | 1 match |
| 5 | `TryMarkRecent(forwarded)` uses forwarded frame | grep `TryMarkRecent(forwarded)` in `CanBusGateway.cs` | 1 match (R1) |
| 6 | `CanBusGateway` does not dispose channels | grep `DisposeAsync` in `CanBusGateway.cs` | only `DisposeAsync` method, no channel dispose |
| 7 | `CliArgsParser` 3 constructor calls pass `GatewayPath` | grep `GatewayPath: gatewayPath` in `CliArgs.cs` | 3 matches |
| 8 | `Program.cs` has gateway dispose order 8->9->10 | grep -A5 `gateway.DisposeAsync` in `Program.cs` | `gateway` before `targetChannel` before `channel2` |
| 9 | `--simulate` host is outside try | grep -B2 `using var host = new EcuSimulatorHost` in `Program.cs` | not inside try block (R3) |
| 10 | `HeadlessHostBuilder.cs` unchanged | `git diff HeadlessHostBuilder.cs` | no changes |

---

## Risk Notes

- **R1 防回环 + ID 映射**：`TryMarkRecent(forwarded)` 用转发帧（映射后 Id）的指纹。回环中收到的帧 Id 与此一致 -> 第一轮即命中。不映射时 Id 不变，同样第一轮命中。
- **fire-and-forget**：`_ = WriteSafeAsync(...)` 是 `async Task`（非 `async void`），内部 try/catch 捕获全部异常。`Task` 不会以 `Faulted` 状态完成，不触发 `UnobservedTaskException`。
- **帧率假设**：诊断场景低帧率（几十帧/秒），`lock` 竞争与 O(n) 清理可接受。CAN 满载场景 Out of Scope。
- **`--simulate` 无 DI 容器**：target channel 和 `CanBusGateway` 传 `null` logger（与现有 `--simulate` 的 source channel 一致）。HIL 模式从 `host2.Services` 获取 logger。
- **`MapToCanId` 双向都映射**：当前设计双向网关两个方向都用同一个 `MapToCanId`。如果用户只想单方向映射，需配置两个网关（Out of Scope：多网关拓扑）。
