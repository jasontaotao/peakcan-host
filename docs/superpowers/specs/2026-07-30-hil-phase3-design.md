# HIL Phase 3: ECU Simulation, Fault Injection & Multi-ECU Matrix

> Date: 2026-07-30
> Status: Draft (v2 — fixed 20 spec errors from code review)
> Depends: Sprint 1 (complete), Sprint 2 (complete), Sprint 3 (complete — v3.63.0)
> Scope: VirtualEcu 模拟器、故障注入框架、多 ECU 矩阵

---

## 1. Goal

Sprint 1-3 交付了 HIL 测试框架的核心能力：领域模型、trace 回放、CLI runner、UDS 断言、硬件在环、WPF 面板。但测试执行仍依赖两种数据源：预录 trace（只读、无交互）或真实 ECU 硬件（成本高、不可重复）。

Phase 3 补齐进程内 ECU 模拟能力和故障注入能力，使 HIL 框架能在无硬件环境下执行交互式测试：

- **Sprint 4: ECU 模拟器** — VirtualEcu 监听 CAN 帧，按规则自动响应 UDS 请求
- **Sprint 5: 故障注入** — FaultInjector 装饰通道，注入 Drop/Delay/Corrupt/Duplicate
- **Sprint 6: 多 ECU 矩阵** — 多个 VirtualEcu 协同，测试总线交互场景

Out of scope: LLM 辅助分析（Phase 5）、真实双硬件模拟器进程（Phase 4 可选扩展）、ECU 行为模型形式化验证（学术范畴）、接收方向故障注入（Phase 4）、ECU 有状态行为模拟（Phase 4）。

---

## 2. Sprint Positioning

```
┌──────────┬───────────────────────┬───────────────────┬──────────────────────────────┬──────────────┐
│  Sprint  │        Channel        │     ECU 响应      │           故障注入           │   ECU 数量   │
├──────────┼───────────────────────┼───────────────────┼──────────────────────────────┼──────────────┤
│ Sprint 1 │ Mock                  │ Mock              │ 无                           │ 0            │
│ Sprint 2 │ TraceDrivenChannel    │ 无（trace 只读）  │ 无                           │ 0            │
│ Sprint 3 │ TraceDriven / PeakCan │ 真实硬件          │ 无                           │ 1 (真实)     │
│ Sprint 4 │ VirtualChannel        │ VirtualEcu        │ 无                           │ 1 (虚拟)     │
│ Sprint 5 │ 任意 + FaultInjector  │ VirtualEcu / 真实 │ Drop/Delay/Corrupt/Duplicate │ 1+           │
│ Sprint 6 │ VirtualChannel        │ 多个 VirtualEcu   │ 可选                         │ N (虚拟矩阵) │
└──────────┴───────────────────────┴───────────────────┴──────────────────────────────┴──────────────┘
```

---

## 3. Sprint 4: ECU Simulator

### 3.1 VirtualChannel

新增 `VirtualChannel : ICanChannel`，纯进程内帧路由器，不依赖 trace 文件或硬件。

```csharp
// Infrastructure/Channel/VirtualChannel.cs
public sealed class VirtualChannel : ICanChannel
{
    private readonly Channel<CanFrame> _frameChannel;
    private readonly object _subscribersLock = new();
    private Action<CanFrame>? _frameReceived;
    private int _isConnected; // 0=disconnected, 1=connected (CAS)

    public ChannelId Id => ChannelId.None; // 虚拟通道无硬件句柄
    public bool IsConnected => Volatile.Read(ref _isConnected) == 1;

    public event Action<CanFrame>? FrameReceived
    {
        add { lock (_subscribersLock) _frameReceived += value; }
        remove { lock (_subscribersLock) _frameReceived -= value; }
    }

    public event Action<ReadLoopError>? ReadLoopError
    {
        add { /* 虚拟通道无硬件读取循环，忽略 */ }
        remove { /* 虚拟通道无硬件读取循环，忽略 */ }
    }

    public VirtualChannel(int capacity = 1000)
    {
        _frameChannel = Channel.CreateBounded<CanFrame>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _isConnected, 1);
        _ = ConsumerLoop(ct);
        return Task.FromResult(Result<Unit>.Ok(default));
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _isConnected, 0);
        _frameChannel.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        // DropOldest 模式下 TryWrite 只在 channel 完成时返回 false
        if (!_frameChannel.Writer.TryWrite(frame))
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "Virtual channel closed"));
        return ValueTask.FromResult(Result<Unit>.Ok(default));
    }

    private async Task ConsumerLoop(CancellationToken ct)
    {
        await foreach (var frame in _frameChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            Action<CanFrame>? handler;
            lock (_subscribersLock) handler = _frameReceived;
            handler?.Invoke(frame);
        }
    }

    public ValueTask DisposeAsync()
    {
        _frameChannel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _frameChannel.Writer.TryComplete();
    }
}
```

**关键约束：**
- `WriteAsync` 非阻塞 — 帧入队后立即返回，消费者线程异步触发 `FrameReceived`
- `DropOldest` 有界 channel — 与 `HILAssertionContext` 一致，避免 OOM
- `FrameReceived` 在 lock 内取快照后释放锁再 invoke — 避免回调中订阅/取消订阅导致死锁
- `ConnectAsync` 启动消费者循环；`DisconnectAsync` 停止
- 实现 `ICanChannel` 全部成员：`Id`、`IsConnected`、`ConnectAsync`、`DisconnectAsync`、`WriteAsync`、`FrameReceived`、`ReadLoopError`
- 实现 `IAsyncDisposable`（`DisposeAsync`）+ 同步 `Dispose`

**与 TraceDrivenChannel loopback 的区别：**

| 属性 | TraceDrivenChannel loopback | VirtualChannel |
|---|---|---|
| 数据源 | trace 文件 + WriteAsync 回环 | 仅 WriteAsync |
| 定时器 | 有（OnTick 按 trace 时间戳发帧） | 无 |
| 用途 | 回放预录场景 + 发帧 | 纯交互式测试 |
| 帧延迟 | trace 时间戳驱动 | 消费者线程调度（~µs 级） |

### 3.2 VirtualEcu Architecture

VirtualEcu 是一个响应式 ECU 模拟器：监听 `ICanChannel.FrameReceived`，匹配请求帧，生成响应帧。

```csharp
// Infrastructure/HIL/VirtualEcu.cs
public sealed class VirtualEcu : IDisposable
{
    private readonly ICanChannel _channel;
    private readonly IsoTpLayer _isoTp;
    private readonly List<UdsResponseRule> _rules;
    private readonly ILogger<VirtualEcu>? _logger;
    private readonly CanIdConfig _ecuCanIds;
    private int _disposed;

    public uint RequestId => _ecuCanIds.ResponseId; // ECU 监听的是 HIL 的发送 ID

    public VirtualEcu(ICanChannel channel, CanIdConfig ecuCanIds,
        IEnumerable<UdsResponseRule> rules, ILogger<VirtualEcu>? logger = null)
    {
        _channel = channel;
        _ecuCanIds = ecuCanIds;
        _rules = rules.ToList();
        _logger = logger;

        // ECU 端 IsoTpLayer — CanIdConfig 与 HIL 端相同，但语义反转（见 §3.2.1）
        _isoTp = new IsoTpLayer(_ecuCanIds, SendFrameAsync, logger);
        _isoTp.MessageReceived += OnUdsRequestReceived;
        _channel.FrameReceived += OnCanFrameReceived;
    }

    private void OnCanFrameReceived(CanFrame frame)
    {
        try { _isoTp.ProcessFrame(frame); }
        catch (ArgumentException ex)
        {
            _logger?.LogDebug("VirtualEcu: frame rejected by IsoTpLayer: {Error}", ex.Message);
        }
    }

    private void OnUdsRequestReceived(byte[] request)
    {
        if (request.Length == 0) return;
        var sid = request[0];

        foreach (var rule in _rules)
        {
            if (rule.TryMatch(request, out var responseData))
            {
                _ = SendUdsResponseAsync(responseData, rule.ResponseDelayMs);
                return;
            }
        }

        // 无匹配规则 -> NRC 0x11 (serviceNotSupported)
        // NRC 格式（ISO 14229-1 §11.3.2）: [0x7F, originalSID, nrc]
        _ = SendUdsResponseAsync(new byte[] { 0x7F, sid, 0x11 }, 0);
    }

    private async Task SendUdsResponseAsync(byte[] data, int delayMs)
    {
        if (delayMs > 0)
            await Task.Delay(delayMs).ConfigureAwait(false);

        await _isoTp.SendMessageAsync(data).ConfigureAwait(false);
    }

    private Task SendFrameAsync(CanFrame frame)
        => _channel.WriteAsync(frame, CancellationToken.None).AsTask();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _channel.FrameReceived -= OnCanFrameReceived;
        _isoTp.MessageReceived -= OnUdsRequestReceived;
        _isoTp.Dispose();
    }
}
```

#### 3.2.1 IsoTpLayer 方向说明（修正）

**CanIdConfig 始终从 HIL/客户端视角定义：**

```csharp
// IsoTpLayer.cs 源码注释
// RequestId: "CAN ID for request frames (client → ECU)"
// ResponseId: "CAN ID for response frames (ECU → client)"
```

IsoTpLayer 内部语义（验证自源码）：
- `ProcessFrame(CanFrame frame)` → 只处理 `frame.Id.Raw == _config.ResponseId` 的帧（ReceiveFlow.cs:28-30）
- `SendMessageAsync` → 发送帧的 CAN ID 使用 `_config.RequestId`（SendFlow.cs:58-61）

即：**IsoTpLayer 用 ResponseId 过滤接收，用 RequestId 标记发送。**

**HIL 端**（发起请求，接收响应）：
- RequestId = 0x7E0 → HIL 发送请求帧的目标 ID
- ResponseId = 0x7E8 → HIL 接收响应帧的过滤 ID

**ECU 端**（接收请求，发送响应）：
- ECU 需要接收 HIL 发到 0x7E0 的帧 → ECU 的 `ProcessFrame` 必须过滤 0x7E0 → ECU 的 ResponseId = 0x7E0
- ECU 需要发送响应到 0x7E8（让 HIL 收到）→ ECU 的 `SendMessageAsync` 必须发到 0x7E8 → ECU 的 RequestId = 0x7E8

**结论：ECU 端 CanIdConfig 必须交换 RequestId/ResponseId：**

```csharp
// HIL 端
var hilConfig = new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 };

// ECU 端 — 交换 ID
var ecuConfig = new CanIdConfig { RequestId = 0x7E8, ResponseId = 0x7E0 };
```

两个 IsoTpLayer 实例使用**镜像的** CanIdConfig，各自独立处理。

### 3.3 UDS Response Rule Engine

```csharp
// Core/HIL/Contracts/UdsResponseRule.cs

/// <summary>
/// UDS response rule: matches request by SID + sub-function + optional data pattern,
/// returns predefined response data with optional delay.
/// </summary>
public sealed record UdsResponseRule
{
    /// <summary>UDS Service ID to match (e.g. 0x22 = ReadDataByIdentifier).</summary>
    public required byte ServiceId { get; init; }

    /// <summary>Sub-function byte to match, or null = match any sub-function.</summary>
    public byte? SubFunction { get; init; }

    /// <summary>AND-mask for bytes [2..N] of request. Null = don't care.</summary>
    public byte[]? DataMask { get; init; }

    /// <summary>Expected value after masking. Must be same length as DataMask.</summary>
    public byte[]? DataPattern { get; init; }

    /// <summary>Response payload (SID|0x40 + data). E.g. [0x62, 0xF1, 0x90, ...VIN...].</summary>
    public required byte[] ResponseData { get; init; }

    /// <summary>Simulated ECU processing delay before sending response.</summary>
    public int ResponseDelayMs { get; init; }

    /// <summary>
    /// Test if a complete UDS request matches this rule.
    /// </summary>
    public bool TryMatch(byte[] request, out byte[] responseData)
    {
        responseData = Array.Empty<byte>();

        if (request.Length == 0 || request[0] != ServiceId)
            return false;

        // Sub-function check (byte[1], if present)
        if (SubFunction.HasValue && (request.Length < 2 || request[1] != SubFunction.Value))
            return false;

        // Data pattern check (bytes [2..N])
        if (DataMask is not null && DataMask.Length > 0)
        {
            if (request.Length < 2 + DataMask.Length)
                return false;

            for (int i = 0; i < DataMask.Length; i++)
            {
                if ((request[2 + i] & DataMask[i]) != DataPattern![i])
                    return false;
            }
        }

        responseData = ResponseData;
        return true;
    }
}
```

**匹配优先级**：规则按列表顺序匹配，第一个匹配的规则生效。这允许"通用规则在后、特定规则在前"的覆盖模式。

**NRC 模拟**：如果需要模拟 ECU 返回 NRC，直接在 ResponseData 中填 NRC 字节。NRC 响应格式（ISO 14229-1 §11.3.2）：`[0x7F, originalSID, nrc]`。

示例 — 模拟 NRC 0x31 (requestOutOfRange) 对 ReadDataByIdentifier (0x22) 的响应：
```json
{ "serviceId": "0x22", "responseData": [127, 34, 49] }
// 0x7F = NegativeResponse SID, 0x22 = original SID, 0x31 = requestOutOfRange
```

**Positive response vs NRC 区分规则**：
- ResponseData[0] == `0x7F` → NRC 响应（格式：`[0x7F, originalSID, nrc]`）
- ResponseData[0] == `SID | 0x40` → Positive response（格式：`[SID|0x40, ...data]`）
- 测试作者负责填对字节，规则引擎不做校验

### 3.4 ECU Script JSON Format

ECU 模拟器脚本是一个 JSON 文件，定义 VirtualEcu 的 CAN ID 配置和响应规则列表。

```json
{
  "$schema": "virtual-ecu-v1.json",
  "name": "BMS_Simulator",
  "canIds": {
    "requestId": "0x7E8",
    "responseId": "0x7E0",
    "isExtendedFrame": false
  },
  "rules": [
    {
      "serviceId": "0x22",
      "subFunction": null,
      "dataMask": [255, 255],
      "dataPattern": [241, 144],
      "responseData": [98, 241, 144, 87, 65, 85, 84, 90, 90, 90, 57, 67, 49, 50, 51, 52, 53, 54, 55, 56],
      "responseDelayMs": 10
    },
    {
      "serviceId": "0x19",
      "subFunction": 2,
      "responseData": [89, 2, 8, 0, 0, 0, 9],
      "responseDelayMs": 15
    },
    {
      "serviceId": "0x3E",
      "subFunction": 0,
      "responseData": [126]
    }
  ]
}
```

字段说明：
- `canIds`: ECU 端 IsoTpLayer 的 CanIdConfig。**注意：requestId/responseId 与 HIL 端交换**（ECU 的 requestId = HIL 的 responseId = 0x7E8，ECU 的 responseId = HIL 的 requestId = 0x7E0）。EcuScriptLoader 负责解析时交换。
- `rules`: UdsResponseRule 列表，按顺序匹配
- 规则 1: ReadDataByIdentifier (0x22)，匹配 DID 0xF190（VIN），返回 17 字节 VIN 数据
- 规则 2: ReadDtcInformation (0x19) 子函数 0x02，返回 1 个 DTC（0x000009, statusByte 0x08）
- 规则 3: TesterPresent (0x3E) 子函数 0x00，返回 positive response

### 3.5 EcuScriptLoader

```csharp
// Infrastructure/HIL/EcuScriptLoader.cs

/// <summary>
/// Loads an ECU simulator script from JSON. Parses CAN IDs and response rules.
/// Swaps RequestId/ResponseId to produce ECU-perspective CanIdConfig.
/// </summary>
public static class EcuScriptLoader
{
    /// <summary>
    /// Load ECU script from a JSON file path.
    /// </summary>
    /// <param name="path">Absolute or relative path to the .json ECU script.</param>
    /// <returns>Parsed EcuScript with ECU-perspective CanIdConfig (IDs swapped).</returns>
    /// <exception cref="FileNotFoundException">Script file not found.</exception>
    /// <exception cref="JsonException">JSON malformed or missing required fields.</exception>
    public static EcuScript Load(string path)
    {
        var json = File.ReadAllText(path);
        return Parse(json);
    }

    /// <summary>
    /// Parse ECU script from JSON string.
    /// </summary>
    public static EcuScript Parse(string json)
    {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 解析 canIds（HIL 视角）
        var canIdsHil = root.GetProperty("canIds");
        var requestIdHil = ParseCanId(canIdsHil.GetProperty("requestId"));
        var responseIdHil = ParseCanId(canIdsHil.GetProperty("responseId"));
        var isExtended = canIdsHil.TryGetProperty("isExtendedFrame", out var ext) && ext.GetBoolean();

        // ECU 端交换 RequestId/ResponseId
        var ecuCanIds = new CanIdConfig
        {
            RequestId = responseIdHil,   // ECU 发送用 HIL 的 ResponseId
            ResponseId = requestIdHil,   // ECU 接收用 HIL 的 RequestId
            IsExtendedFrame = isExtended
        };

        // 解析 rules
        var rules = new List<UdsResponseRule>();
        foreach (var ruleEl in root.GetProperty("rules").EnumerateArray())
        {
            rules.Add(ParseRule(ruleEl));
        }

        return new EcuScript(
            name: root.GetProperty("name").GetString()!,
            canIds: ecuCanIds,
            rules: rules);
    }

    private static uint ParseCanId(JsonElement element)
    {
        var s = element.GetString()!;
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            => uint.Parse(s[2..], NumberStyles.HexNumber)
            : uint.Parse(s);
    }

    private static UdsResponseRule ParseRule(JsonElement el) { /* ... */ }
}

/// <summary>
/// Parsed ECU simulator script. CanIdConfig is already in ECU perspective (IDs swapped).
/// </summary>
public sealed record EcuScript(
    string Name,
    CanIdConfig CanIds,
    IReadOnlyList<UdsResponseRule> Rules);
```

### 3.6 CLI Integration

CLI 新增 `--ecu` 参数，指定 ECU 脚本文件路径：

```bash
# 纯虚拟模式（无 trace、无硬件）
peakcan-hil --suite tests.json --ecu bms_sim.json --output results.xml

# trace + 虚拟 ECU 混合模式
peakcan-hil --suite tests.json --trace recording.blf --ecu bms_sim.json --output results.xml
```

**模式互斥规则：**

| --trace | --hw | --ecu | 有效？ | 说明 |
|---|---|---|---|---|
| ✗ | ✗ | ✗ | 无效 | 必须指定至少一个数据源 |
| ✓ | ✗ | ✗ | ✓ | 纯 trace 回放（Sprint 2） |
| ✗ | ✓ | ✗ | ✓ | 纯硬件模式（Sprint 3） |
| ✗ | ✗ | ✓ | ✓ | 纯虚拟模式（Sprint 4 新增） |
| ✓ | ✗ | ✓ | ✓ | trace + 虚拟 ECU 混合 |
| ✗ | ✓ | ✓ | 无效 | 硬件 + 虚拟 ECU 冲突（同一总线上真实 ECU 和虚拟 ECU 会竞争响应） |
| ✓ | ✓ | * | 无效 | trace + 硬件互斥（Sprint 3 规则） |

**VirtualChannel + VirtualEcu 组装流程（纯虚拟模式）：**

1. 解析 `--ecu` 脚本 → `EcuScript` (canIds + rules) — EcuScriptLoader 已交换 ID
2. 创建 `VirtualChannel`
3. 创建 `VirtualEcu(channel, script.CanIds, script.Rules)`
4. 创建 `HILAssertionContext(channel)` — 订阅 FrameReceived
5. VirtualEcu 和 HILAssertionContext 共享同一个 VirtualChannel
6. `channel.ConnectAsync()` → 启动消费者循环
7. TestSuiteEngine 执行：
   - SendFrame 步骤 → `ctx.SendFrameAsync` → `channel.WriteAsync` → ConsumerLoop → FrameReceived
   - VirtualEcu 收到 FrameReceived → IsoTpLayer 重组 → 匹配规则 → WriteAsync 响应帧
   - HILAssertionContext 收到响应帧 FrameReceived → WaitForFrame / AssertDtc 通过

### 3.7 HilRunRequest 扩展

在 Sprint 3 的 HilRunRequest 中新增 ECU 脚本路径：

```csharp
// Core/HIL/HilRunRequest.cs (扩展)
public sealed record HilRunRequest
{
    // ... existing fields (SuitePath, TracePath, HardwareChannel, Format, UdsRequestId, UdsResponseId) ...

    /// <summary>
    /// Path to ECU simulator script JSON. null = no virtual ECU.
    /// Mutually exclusive with HardwareChannel.
    /// </summary>
    public string? EcuScriptPath { get; init; }
}
```

### 3.8 HeadlessHostBuilder 扩展

```csharp
// Infrastructure/HIL/HeadlessHostBuilder.cs (扩展)
// 注意：HeadlessHostBuilder.Build(CliArgs args) 是入口方法，新增逻辑在 Build 内部

public static IHost Build(CliArgs args)
{
    // ... existing setup code ...

    if (args.HardwareChannel is not null)
    {
        // 硬件模式 (Sprint 3) — 注册硬件通道 + UDS 全套
        RegisterHardwareMode(builder, args);
    }
    else if (args.EcuScriptPath is not null)
    {
        // 虚拟 ECU 模式 (Sprint 4) — 注册 VirtualChannel + VirtualEcu + UDS 全套
        RegisterVirtualEcuMode(builder, args);
    }
    else
    {
        // 纯 trace 回放 (Sprint 2) — 仅注册 HILAssertionContext
        RegisterTraceOnlyMode(builder, args);
    }

    // ... rest of setup ...
}

private static void RegisterVirtualEcuMode(HostBuilder builder, CliArgs args)
{
    // 1. VirtualChannel
    builder.Services.AddSingleton<ICanChannel>(sp => new VirtualChannel());

    // 2. VirtualEcu（订阅 FrameReceived，自动响应）
    builder.Services.AddSingleton(sp =>
    {
        var channel = sp.GetRequiredService<ICanChannel>();
        var script = EcuScriptLoader.Load(args.EcuScriptPath!);
        var logger = sp.GetService<ILogger<VirtualEcu>>();
        return new VirtualEcu(channel, script.CanIds, script.Rules, logger);
    });

    // 3. UDS 全套（虚拟 ECU 模式下 AssertDtc/AssertNrc 仍可用）
    //    IsoTpLayer + UdsClient + IUdsSession + HilIsoTpBridge + AssertDtc/Nrc executors
    RegisterUdsServices(builder, args);

    // 4. HILAssertionContext（订阅 FrameReceived 用于断言）
    builder.Services.AddSingleton<Core.HIL.Contracts.IAssertionContext>(sp =>
    {
        var channel = sp.GetRequiredService<ICanChannel>();
        var dbcLookup = sp.GetRequiredService<Core.HIL.Contracts.IDbcLookup>();
        return new HILAssertionContext(channel, dbcLookup);
    });
}

private static void RegisterUdsServices(HostBuilder builder, CliArgs args)
{
    // IsoTpLayer（HIL 端视角：RequestId=0x7E0, ResponseId=0x7E8）
    builder.Services.AddSingleton<IsoTpLayer>(sp =>
    {
        var config = new CanIdConfig { RequestId = 0x7E0, ResponseId = 0x7E8 };
        var channel = sp.GetRequiredService<ICanChannel>();
        return new IsoTpLayer(config,
            async frame => { await channel.WriteAsync(frame, default).ConfigureAwait(false); });
    });

    // UdsClient + IUdsSession
    builder.Services.AddSingleton<UdsClient>(sp =>
    {
        var isoTp = sp.GetRequiredService<IsoTpLayer>();
        return new UdsClient(isoTp);
    });
    builder.Services.AddSingleton<IUdsSession>(sp =>
    {
        var client = sp.GetRequiredService<UdsClient>();
        return new UdsSessionAdapter(client);
    });

    // ISO-TP frame bridge
    builder.Services.AddSingleton<HilIsoTpBridge>(sp =>
    {
        var channel = sp.GetRequiredService<ICanChannel>();
        var isoTp = sp.GetRequiredService<IsoTpLayer>();
        return new HilIsoTpBridge(channel, isoTp);
    });

    // UDS executors
    builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, AssertDtcStepExecutor>();
    builder.Services.AddSingleton<Core.HIL.StepExecutor.IStepExecutor, AssertNrcStepExecutor>();
}
```

### 3.9 混合模式（trace + VirtualEcu）

trace + 虚拟 ECU 混合模式下，trace 帧和 VirtualEcu 交互帧需要在同一个通道上共存。

方案：VirtualChannel 作为主通道，trace 帧通过后台任务注入：

```csharp
private static async Task InjectTraceFramesAsync(
    VirtualChannel channel, IReadOnlyList<ReplayFrame> frames, CancellationToken ct)
{
    double baseTimestamp = frames.Count > 0 ? frames[0].Timestamp : 0;

    foreach (var frame in frames)
    {
        // 按 trace 时间戳间隔延迟
        var delay = frame.Timestamp - baseTimestamp;
        if (delay > 0)
            await Task.Delay(TimeSpan.FromSeconds(delay), ct).ConfigureAwait(false);

        var canFrame = new CanFrame(
            new CanId(frame.Id),
            new ReadOnlyMemory<byte>(frame.Data),
            frame.Flags,
            ChannelId.None,
            new Timestamp(TimeSpan.FromSeconds(frame.Timestamp)));
        await channel.WriteAsync(canFrame, ct).ConfigureAwait(false);

        baseTimestamp = frame.Timestamp;
    }
}
```

**已知限制**：trace 帧注入是 wall-clock 驱动的（Task.Delay），精度受 OS 调度影响（±15ms on Windows）。对于毫秒级时序要求的测试，应使用纯虚拟模式或硬件模式。

### 3.10 WPF HIL Panel 扩展

Sprint 3 的 HIL Panel 新增 ECU 脚本路径选择：

```xml
<!-- HilView.xaml (扩展) -->
<StackPanel Orientation="Horizontal" Margin="0,4,0,0">
    <Label Content="ECU Script:" Width="80"/>
    <TextBox Text="{Binding EcuScriptPath}" Width="300"
             IsReadOnly="True"
             ToolTip="Virtual ECU script JSON path"/>
    <Button Content="Browse..." Command="{Binding BrowseEcuScriptCommand}" Margin="4,0,0,0"/>
</StackPanel>
```

ECU 脚本路径与 trace/hw 模式的互斥规则在 ViewModel 中校验，与 CLI 一致。

---

## 4. Sprint 5: Fault Injection

### 4.1 FaultInjector（Channel Decorator）

FaultInjector 是 ICanChannel 的装饰器，包裹底层通道，在帧流中注入故障：

```csharp
// Infrastructure/Channel/FaultInjector.cs
public sealed class FaultInjector : ICanChannel
{
    private readonly ICanChannel _inner;
    private readonly object _faultsLock = new();
    private readonly List<FaultRule> _activeFaults = new();

    public ChannelId Id => _inner.Id;
    public bool IsConnected => _inner.IsConnected;

    public event Action<CanFrame>? FrameReceived
    {
        add => _inner.FrameReceived += value;
        remove => _inner.FrameReceived -= value;
    }

    public event Action<ReadLoopError>? ReadLoopError
    {
        add => _inner.ReadLoopError += value;
        remove => _inner.ReadLoopError -= value;
    }

    public FaultInjector(ICanChannel inner) => _inner = inner;

    /// <summary>Add a fault rule. Returns a disposable handle for removal.</summary>
    public FaultHandle AddFault(FaultRule fault)
    {
        lock (_faultsLock) _activeFaults.Add(fault);
        return new FaultHandle(() => RemoveFault(fault));
    }

    private void RemoveFault(FaultRule fault)
    {
        lock (_faultsLock) _activeFaults.Remove(fault);
    }

    public async ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
    {
        List<FaultRule>? snapshot;
        lock (_faultsLock) snapshot = _activeFaults.Count > 0 ? _activeFaults.ToList() : null;

        if (snapshot is null || snapshot.Count == 0)
            return await _inner.WriteAsync(frame, ct).ConfigureAwait(false);

        // 检查 Delay 故障 — 取最大延迟
        int maxDelay = snapshot
            .Where(f => f.Type == FaultType.Delay && f.Matches(frame))
            .Select(f => f.DelayMs)
            .DefaultIfEmpty(0)
            .Max();

        if (maxDelay > 0)
            await Task.Delay(maxDelay, ct).ConfigureAwait(false);

        // 应用非 Delay 故障
        var frames = new List<CanFrame> { frame };
        foreach (var fault in snapshot.Where(f => f.Type != FaultType.Delay))
        {
            if (!fault.Matches(frame)) continue;
            var next = new List<CanFrame>();
            foreach (var f in frames)
                next.AddRange(fault.Apply(f));
            frames = next;
        }

        // 如果所有帧都被 Drop 掉，直接返回成功
        if (frames.Count == 0)
            return Result<Unit>.Ok(default);

        foreach (var f in frames)
        {
            var result = await _inner.WriteAsync(f, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return result;
        }

        return Result<Unit>.Ok(default);
    }

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        => _inner.ConnectAsync(baud, fd, ct);

    public Task DisconnectAsync(CancellationToken ct = default)
        => _inner.DisconnectAsync(ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
    public void Dispose() => _inner.Dispose();
}

public sealed record FaultHandle(Action Remove) : IDisposable
{
    public void Dispose() => Remove();
}
```

**设计约束：**
- FaultInjector 只装饰 WriteAsync（发送方向）。接收方向的故障注入通过订阅 FrameReceived 的中间层实现，但 Sprint 5 范围内仅支持发送方向 — 理由：HIL 测试引擎通过 SendFrameAsync 发送激励帧，故障注入作用于激励帧更有意义（测试 ECU 对异常输入的处理）。接收方向故障注入留给 Phase 4。
- FrameReceived 事件直接透传到底层通道 — FaultInjector 不拦截接收帧
- AddFault / RemoveFault 线程安全 — 测试步骤线程添加故障，消费者线程读取故障列表
- Delay 故障取最大延迟值（P3-D11）— 多个 Delay 故障同时匹配时，取最大值而非累加，避免延迟爆炸
- 实现 `ICanChannel` 全部成员：`Id`、`IsConnected`、`ConnectAsync`、`DisconnectAsync`、`WriteAsync`、`FrameReceived`、`ReadLoopError`
- 实现 `IAsyncDisposable` + `Dispose`

### 4.2 Fault Types

```csharp
// Core/HIL/Contracts/FaultRule.cs

/// <summary>
/// Fault injection rule: matches frames by CAN ID, applies a fault transformation.
/// </summary>
public sealed record FaultRule
{
    public required FaultType Type { get; init; }

    /// <summary>Target CAN ID. null = match all frames.</summary>
    public uint? TargetCanId { get; init; }

    /// <summary>Drop probability (0.0-1.0). For Drop type only.</summary>
    public double Probability { get; init; } = 1.0;

    /// <summary>Delay in ms. For Delay type only.</summary>
    public int DelayMs { get; init; }

    /// <summary>Byte positions to corrupt. For Corrupt type only.</summary>
    public int[]? CorruptByteIndices { get; init; }

    /// <summary>XOR mask for corruption. For Corrupt type only.</summary>
    public byte CorruptXorMask { get; init; } = 0xFF;

    public bool Matches(CanFrame frame)
        => TargetCanId is null || frame.Id.Raw == TargetCanId.Value;

    public IReadOnlyList<CanFrame> Apply(CanFrame frame)
    {
        return Type switch
        {
            FaultType.Drop => ApplyDrop(frame),
            FaultType.Corrupt => ApplyCorrupt(frame),
            FaultType.Duplicate => ApplyDuplicate(frame),
            _ => new[] { frame }
        };
    }

    private IReadOnlyList<CanFrame> ApplyDrop(CanFrame frame)
    {
        // Random.Shared 是线程安全的
        if (Random.Shared.NextDouble() < Probability)
            return Array.Empty<CanFrame>(); // 丢帧
        return new[] { frame };
    }

    private IReadOnlyList<CanFrame> ApplyCorrupt(CanFrame frame)
    {
        if (CorruptByteIndices is null || CorruptByteIndices.Length == 0)
            return new[] { frame };

        // CanFrame.Data 是 ReadOnlyMemory<byte>，需要复制到数组修改后重新包装
        var data = frame.Data.ToArray();
        foreach (var idx in CorruptByteIndices)
        {
            if (idx >= 0 && idx < data.Length)
                data[idx] ^= CorruptXorMask;
        }
        return new[] { frame with { Data = new ReadOnlyMemory<byte>(data) } };
    }

    private IReadOnlyList<CanFrame> ApplyDuplicate(CanFrame frame)
        => new[] { frame, frame }; // 发两遍
}

public enum FaultType
{
    /// <summary>Drop frame (optionally probabilistic).</summary>
    Drop,

    /// <summary>Delay frame by N ms.</summary>
    Delay,

    /// <summary>Corrupt specific byte positions via XOR.</summary>
    Corrupt,

    /// <summary>Send frame twice.</summary>
    Duplicate,
}
```

**Corrupt 故障能力覆盖**：通过配置 `CorruptByteIndices` 和 `CorruptXorMask`，Corrupt 类型可以模拟：
- Bit Flip：`CorruptByteIndices: [byteIdx]`, `CorruptXorMask: 0x01 << bitIdx`
- Byte Replace：`CorruptByteIndices: [byteIdx]`, `CorruptXorMask: 0xFF`（XOR 全 1 = 按位取反，配合原值计算可替换任意值）
- CRC 错误：`CorruptByteIndices: [crcByteIdx]`, `CorruptXorMask: 0x01`

### 4.3 InjectFaultStep + ClearFaultStep

新增 2 个 TestCaseStepKind：

```csharp
public enum TestCaseStepKind
{
    // ... existing values (SendFrame, WaitForFrame, AssertSignal, etc.) ...
    InjectFault,    // Phase 3 Sprint 5
    ClearFault,     // Phase 3 Sprint 5
}
```

```csharp
// Core/HIL/StepParams/InjectFaultStep.cs
public sealed record InjectFaultStep(
    uint CanId,                    // 目标 CAN ID, 0 = 全部
    FaultType FaultType,           // 枚举类型，JSON 反序列化用 JsonStringEnumConverter
    double Probability,            // Drop 概率 (0-1)
    int DelayMs,                   // Delay 毫秒
    int[]? CorruptByteIndices,     // Corrupt 字节位置
    byte CorruptXorMask,           // Corrupt XOR 掩码
    string? FaultId                // 可选标识符，用于 ClearFault 定向清除
) : StepParameters;

// Core/HIL/StepParams/ClearFaultStep.cs
public sealed record ClearFaultStep(
    string? FaultId    // null = 清除所有故障, 非空 = 只清除指定 ID 的故障
) : StepParameters;
```

### 4.4 IFaultInjectionContext 接口

Executor 需要新接口。当前 `IAssertionContext` 没有暴露故障注入能力。

方案：新增可选接口 `IFaultInjectionContext`，由 `HILAssertionContext` 在 FaultInjector 存在时实现：

```csharp
// Core/HIL/Contracts/IFaultInjectionContext.cs
public interface IFaultInjectionContext
{
    /// <summary>Add a fault rule. Returns a disposable handle for removal.</summary>
    IDisposable AddFault(FaultRule fault);

    /// <summary>Tag a fault handle with an ID for targeted clearing.</summary>
    void TagFault(string faultId, IDisposable handle);

    /// <summary>Remove all faults, or only those matching the given ID.</summary>
    void ClearFaults(string? faultId = null);
}
```

InjectFaultStepExecutor 通过 cast 检查：

```csharp
// Core/HIL/StepExecutor/InjectFaultStepExecutor.cs
public sealed class InjectFaultStepExecutor : IStepExecutor
{
    public TestCaseStepKind Kind => TestCaseStepKind.InjectFault;

    public Task<StepResult> ExecuteAsync(TestCaseStep step, IAssertionContext ctx, CancellationToken ct)
    {
        if (ctx is not IFaultInjectionContext faultCtx)
            return Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
                "Context does not support fault injection", null, null, 0));

        var p = (InjectFaultStep)step.Parameters;
        var rule = new FaultRule
        {
            Type = p.FaultType,  // 已是枚举，无需 Enum.Parse
            TargetCanId = p.CanId == 0 ? null : p.CanId,
            Probability = p.Probability,
            DelayMs = p.DelayMs,
            CorruptByteIndices = p.CorruptByteIndices,
            CorruptXorMask = p.CorruptXorMask,
        };

        var handle = faultCtx.AddFault(rule);

        if (p.FaultId is not null)
            faultCtx.TagFault(p.FaultId, handle);

        return Task.FromResult(new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
            $"Fault injected: {p.FaultType}", null, null, 0));
    }
}
```

### 4.5 HILAssertionContext 扩展

当 HilRunRequest 指定 `EnableFaultInjection = true` 时，HILAssertionContext 内部用 FaultInjector 包裹 channel：

```csharp
// Infrastructure/HIL/HILAssertionContext.cs (扩展)
// 注意：HILAssertionContext 保持 internal sealed。IFaultInjectionContext 接口定义在 Core 层，
// Infrastructure 的 internal 类可以实现 Core 的 public 接口。
// InjectFaultStepExecutor（Core 层）通过 `ctx is IFaultInjectionContext` cast 检查 —
// 因为 IFaultInjectionContext 是 public 接口，cast 不需要引用 Infrastructure 程序集。

public sealed class HILAssertionContext : IAssertionContext, IFaultInjectionContext, IHasRecentFrames, IDisposable
{
    private readonly ICanChannel _effectiveChannel;
    private readonly FaultInjector? _faultInjector;
    private readonly Dictionary<string, IDisposable> _faultHandles = new();

    public HILAssertionContext(ICanChannel channel, IDbcLookup dbcLookup, bool enableFaultInjection = false)
    {
        if (enableFaultInjection)
        {
            _faultInjector = new FaultInjector(channel);
            _effectiveChannel = _faultInjector;
        }
        else
        {
            _effectiveChannel = channel;
        }

        _effectiveChannel.FrameReceived += OnFrameReceived;
        // ... rest of initialization ...
    }

    public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct)
        => _effectiveChannel.WriteAsync(frame, ct);

    // IFaultInjectionContext
    public IDisposable AddFault(FaultRule fault)
    {
        if (_faultInjector is null)
            throw new InvalidOperationException("Fault injection not enabled");
        return _faultInjector.AddFault(fault);
    }

    public void TagFault(string faultId, IDisposable handle)
        => _faultHandles[faultId] = handle;

    public void ClearFaults(string? faultId = null)
    {
        if (faultId is null)
        {
            foreach (var h in _faultHandles.Values) h.Dispose();
            _faultHandles.Clear();
        }
        else if (_faultHandles.TryGetValue(faultId, out var h))
        {
            h.Dispose();
            _faultHandles.Remove(faultId);
        }
    }
}
```

### 4.6 CLI Integration

```bash
# 启用故障注入
peakcan-hil --suite tests.json --ecu bms_sim.json --enable-faults --output results.xml

# FaultInjector 也可以包裹硬件通道
peakcan-hil --suite tests.json --hw USB1 --enable-faults --output results.xml
```

`--enable-faults` 标志使 HILAssertionContext 用 FaultInjector 包裹底层通道。故障规则通过测试套件中的 InjectFault 步骤动态添加，不需要 CLI 参数。

---

## 5. Sprint 6: Multi-ECU Matrix

### 5.1 EcuMatrix

多个 VirtualEcu 共享同一个 VirtualChannel，各自响应不同 CAN ID 对的请求：

```csharp
// Infrastructure/HIL/EcuMatrix.cs
public sealed class EcuMatrix : IDisposable
{
    private readonly List<VirtualEcu> _ecus = new();
    private readonly VirtualChannel _channel;
    private int _disposed;

    public EcuMatrix(int channelCapacity = 1000)
    {
        _channel = new VirtualChannel(channelCapacity);
    }

    public void AddEcu(EcuScript script, ILogger? logger = null)
    {
        // CAN ID 冲突检测
        var newRequestId = script.CanIds.RequestId;
        if (_ecus.Any(e => e.RequestId == newRequestId))
            throw new InvalidOperationException(
                $"CAN ID conflict: request ID 0x{newRequestId:X3} already assigned to another ECU");

        var ecu = new VirtualEcu(_channel, script.CanIds, script.Rules, logger);
        _ecus.Add(ecu);
    }

    public ICanChannel Channel => _channel;

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        => _channel.ConnectAsync(baud, fd, ct);

    public Task DisconnectAsync(CancellationToken ct = default)
        => _channel.DisconnectAsync(ct);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        foreach (var ecu in _ecus) ecu.Dispose();
        _channel.Dispose();
    }
}
```

### 5.2 Matrix Configuration

矩阵配置文件是一个 JSON 数组，每个元素是一个 ECU 脚本路径：

```json
{
  "$schema": "ecu-matrix-v1.json",
  "name": "Powertrain_Matrix",
  "ecus": [
    { "script": "bms_sim.json" },
    { "script": "mcu_sim.json" },
    { "script": "vcu_sim.json" }
  ]
}
```

或者直接内联：

```json
{
  "name": "Powertrain_Matrix",
  "ecus": [
    {
      "name": "BMS",
      "canIds": { "requestId": "0x7E8", "responseId": "0x7E0" },
      "rules": []
    },
    {
      "name": "MCU",
      "canIds": { "requestId": "0x7EA", "responseId": "0x7E2" },
      "rules": []
    }
  ]
}
```

**注意**：canIds 中的 requestId/responseId 已经是 ECU 视角（与 HIL 端交换）。

### 5.3 CLI Integration

```bash
# 多 ECU 矩阵模式
peakcan-hil --suite tests.json --matrix powertrain.json --output results.xml

# 矩阵 + 故障注入
peakcan-hil --suite tests.json --matrix powertrain.json --enable-faults --output results.xml
```

`--matrix` 与 `--ecu` 互斥（`--ecu` 是单 ECU 简写，`--matrix` 是多 ECU 完整配置）。

### 5.4 交互场景测试

多 ECU 矩阵支持跨 ECU 交互测试：

```json
{
  "name": "InterEcu_Communication",
  "cases": [
    {
      "id": "case_bms_to_mcu",
      "steps": [
        {
          "kind": "sendFrame",
          "id": "0x7E0",
          "data": [34, 241, 144],
          "comment": "Read VIN from BMS"
        },
        {
          "kind": "waitForFrame",
          "id": "0x7E8",
          "timeoutMs": 500,
          "comment": "Expect BMS VIN response"
        },
        {
          "kind": "sendFrame",
          "id": "0x7E2",
          "data": [34, 241, 144],
          "comment": "Read VIN from MCU"
        },
        {
          "kind": "waitForFrame",
          "id": "0x7EA",
          "timeoutMs": 500,
          "comment": "Expect MCU VIN response"
        }
      ]
    }
  ]
}
```

---

## 6. New StepKinds Summary

| StepKind | Sprint | 参数 Record | Executor | 说明 |
|---|---|---|---|---|
| InjectFault | 5 | InjectFaultStep | InjectFaultStepExecutor | 在通道上注入故障规则 |
| ClearFault | 5 | ClearFaultStep | ClearFaultStepExecutor | 清除故障规则 |

**不新增的 StepKind：**
- `waitForEcuResponse` — 已由 `waitForFrame`（Sprint 3）覆盖，VirtualEcu 的响应帧就是普通 CAN 帧
- `sendUdsRequest` — 已由 `sendFrame` + ISO-TP 分帧覆盖（通过 UdsSessionAdapter 在 AssertDtc/AssertNrc 中隐式使用）

**StepParameters 的 [JsonDerivedType] 列表新增：**

```csharp
[JsonDerivedType(typeof(InjectFaultStep), "injectFault")]
[JsonDerivedType(typeof(ClearFaultStep), "clearFault")]
```

`TestCaseStepJsonConverter` 和 `StepParametersFactory` 同步更新。

---

## 7. Architecture Decisions

| ID | Decision | Rationale |
|---|---|---|
| P3-D1 | VirtualEcu 复用 IsoTpLayer 处理 ISO-TP 分帧 | 避免 reimplement ISO-TP 重组/分帧逻辑；IsoTpLayer 已经过 Sprint 3 审查 |
| P3-D2 | ECU 端 CanIdConfig 交换 RequestId/ResponseId | IsoTpLayer 用 ResponseId 过滤接收、RequestId 标记发送；ECU 接收 HIL 的请求（0x7E0）需 ResponseId=0x7E0，发送响应（0x7E8）需 RequestId=0x7E8 |
| P3-D3 | FaultInjector 使用 Decorator 模式包裹 ICanChannel | 透明叠加，不影响现有通道实现；可在 trace/hardware/virtual 任意通道上使用 |
| P3-D4 | 故障注入仅作用于发送方向（WriteAsync） | HIL 测试引擎发送激励帧，故障注入测试 ECU 对异常输入的处理；接收方向故障注入留给 Phase 4 |
| P3-D5 | InjectFault/ClearFault 作为 StepKind 而非通道配置 | 故障注入需要在测试序列的特定时点触发/清除，StepKind 提供精确控制 |
| P3-D6 | IFaultInjectionContext 可选接口，cast 检查 | 不是所有 IAssertionContext 都支持故障注入（如 trace-only 模式）；cast 失败返回明确错误 |
| P3-D7 | EcuMatrix 共享单个 VirtualChannel | 多 ECU 在同一虚拟总线上通信；VirtualChannel 的 FrameReceived 广播给所有订阅者 |
| P3-D8 | --ecu 与 --hw 互斥 | 虚拟 ECU 和真实 ECU 在同一总线上会竞争响应同一请求 ID，导致不可预测行为 |
| P3-D9 | ECU 脚本 JSON 格式，非代码定义 | 非程序员可编写；CI 可版本控制；与 HIL 测试套件 JSON 格式一致 |
| P3-D10 | VirtualChannel.WriteAsync 非阻塞（入队即返回） | 与 TraceDrivenChannel.WriteAsync loopback 一致；避免阻塞测试引擎线程 |
| P3-D11 | Delay 故障取最大延迟值 | 多个 Delay 故障同时匹配时，取最大值而非累加 — 避免延迟爆炸 |
| P3-D12 | VirtualEcu 无匹配规则时返回 NRC 0x11 | 符合 ISO 14229-1 §11.3.2 — ECU 对不支持的 SID 返回 serviceNotSupported |
| P3-D13 | Corrupt 故障通过字节索引 + XOR 掩码实现通用篡改 | 单一 Corrupt 类型覆盖 bit flip / byte replace / CRC 错误等场景，避免故障类型膨胀 |
| P3-D14 | EcuScriptLoader 负责交换 RequestId/ResponseId | 测试作者在 JSON 中填 HIL 视角的 ID（requestId=0x7E0, responseId=0x7E8），Loader 自动交换为 ECU 视角 |
| P3-D15 | InjectFaultStep.FaultType 用 FaultType 枚举而非 string | 编译时检查，JSON 反序列化用 JsonStringEnumConverter（已有 HILJsonOptions 全局配置） |
| P3-D16 | HILAssertionContext 保持 internal sealed | IFaultInjectionContext 是 Core 层 public 接口，Infrastructure 的 internal 类可实现；Core 层 cast 检查无需引用 Infrastructure |

---

## 8. Risk Register

| Risk | Severity | Mitigation |
|---|---|---|
| VirtualEcu IsoTpLayer 与 HIL 端 IsoTpLayer 竞争同一帧 | HIGH | ECU 端 ResponseId=0x7E0（接收请求），HIL 端 ResponseId=0x7E8（接收响应）— 过滤 ID 不同，不竞争 |
| VirtualChannel 消费者线程延迟影响时序敏感测试 | MEDIUM | 消费者线程调度延迟 ~µs 级；时序敏感测试应使用硬件模式；文档标注已知限制 |
| FaultInjector 的 lock(_faultsLock) 在高帧率下成为瓶颈 | MEDIUM | 测试步骤中添加/清除故障是低频操作；帧发送时取快照后释放锁 — 锁持有时间极短 |
| 多 ECU 矩阵中 broadcast 帧（如 0x7DF）触发所有 ECU 响应 | LOW | UDS 功能寻址（0x7DF）允许多 ECU 响应；如果不需要，测试用例应使用物理寻址（0x7E0 等） |
| VirtualEcu 响应规则 JSON 中 responseData 手写字节易出错 | MEDIUM | 可选：后续提供 odx/dbc 导入工具自动生成规则；Sprint 4 范围内手动编写 |
| CanFrame.Data 是 ReadOnlyMemory\<byte\>，Corrupt 故障需要复制-修改-重新包装 | HIGH | ApplyCorrupt 实现：`frame.Data.ToArray()` → 修改 → `new ReadOnlyMemory<byte>(data)` → `frame with { Data = ... }` |
| 混合模式 trace 帧注入使用 Task.Delay，精度受 OS 调度影响 | LOW | 文档标注 ±15ms on Windows；纯虚拟模式无此问题 |
| Random 线程安全问题 | MEDIUM | 使用 `Random.Shared`（.NET 6+ 线程安全），避免 `new Random()` 并发问题 |
| DropOldest 模式下错误消息误导 | LOW | 修正错误消息为 "Virtual channel closed"（TryWrite 返回 false 的唯一原因是 channel 已完成） |

---

## 9. Sprint 4 TDD Increment Plan

| Inc | Component | Tests | Description |
|---|---|---|---|
| 0 | VirtualChannel | 6 | 连接/断开、WriteAsync 回环、FrameReceived 多订阅者、DropOldest 满载、Dispose 幂等、Id/ReadLoopError 成员存在 |
| 1 | UdsResponseRule | 4 | SID 匹配、子函数匹配、DataMask 匹配、无匹配返回 false |
| 2 | EcuScriptLoader | 4 | JSON 反序列化、canIds 解析（0x 前缀）、rules 列表解析、ID 交换验证 |
| 3 | VirtualEcu | 6 | 单帧 UDS 请求→响应、多帧 ISO-TP 重组→响应、无匹配规则 NRC 0x11、ResponseDelayMs、Dispose 取消订阅、多规则优先级 |
| 4 | CLI --ecu 模式 | 4 | 纯虚拟模式端到端、--ecu 与 --hw 互斥校验、ECU 脚本不存在错误处理、JUnit 输出验证 |
| 5 | WPF Panel | 0 | ECU 脚本路径选择 + 互斥校验（手动验证） |

**Total: ~24 tests**

---

## 10. Sprint 5 TDD Increment Plan

| Inc | Component | Tests | Description |
|---|---|---|---|
| 0 | FaultRule | 5 | Drop 100%/0%、Corrupt 指定字节、Duplicate 帧数 x2、Delay 标记、TargetCanId 匹配/不匹配 |
| 1 | FaultInjector | 7 | 无故障透传、Drop 丢帧、Corrupt 篡改、Duplicate 双发、Delay 延迟、多故障叠加、Id/ReadLoopError 透传 |
| 2 | InjectFaultStepExecutor | 3 | 添加故障成功、Context 不支持故障注入→失败、FaultId 标记 |
| 3 | ClearFaultStepExecutor | 3 | 清除指定 FaultId、清除全部、无 FaultId 不报错 |
| 4 | CLI --enable-faults | 2 | 故障注入端到端、FaultInjector 包裹硬件通道 |

**Total: ~20 tests**

---

## 11. Sprint 6 TDD Increment Plan

| Inc | Component | Tests | Description |
|---|---|---|---|
| 0 | EcuMatrix | 4 | 添加多 ECU、CAN ID 冲突检测、Channel 属性暴露、Dispose 清理所有 ECU |
| 1 | MatrixConfigLoader | 2 | 外部引用模式加载、内联模式加载 |
| 2 | CLI --matrix | 3 | 多 ECU 端到端、跨 ECU 交互测试、--matrix 与 --ecu 互斥 |

**Total: ~9 tests**

---

## 12. Out of Scope (Phase 4/5)

| Item | Phase | Rationale |
|---|---|---|
| 接收方向故障注入（篡改 ECU→HIL 的响应帧） | Phase 4 | 需要 FrameReceived 中间层，复杂度高于发送方向 |
| 独立模拟器进程（双 PCAN 设备） | Phase 4 | 进程内 VirtualEcu 覆盖 90% 场景；真实总线时序需要独立进程 + 物理总线 |
| ECU 行为状态机（有状态响应：先 unlock 再 write） | Phase 4 | Sprint 4 的规则引擎是无状态的；有状态模拟需要状态机模型 |
| ODX/DBC 自动生成 ECU 脚本 | Phase 4 | 手动编写 JSON 足够 Sprint 4-6；自动化导入是工具链问题 |
| LLM 辅助失败分析 | Phase 5 | 独立于 ECU 模拟和故障注入 |
| ECU 仿真模型验证（形式化） | 学术 | 超出工具链范围 |
