# HIL Phase 4: Stateful ECU Simulation & Receive-Path Fault Injection

> Date: 2026-07-30
> Status: Draft (v2 — fixed 17 spec issues from two review rounds)
> Depends: Phase 3 (complete — v3.64.0)
> Scope: 有状态 ECU 状态机、接收方向故障注入、ODX 导入

---

## 1. Goal

Phase 3 交付了无状态 ECU 模拟器（`UdsResponseRule` -- 每次请求返回固定响应）和发送方向故障注入。但真实 ECU 是有状态的：

- **SecurityAccess (0x27)**：先返回 seed，收到 key 后验证，验证通过才解锁写入
- **RoutineControl (0x31)**：先 requestRoutineResult，再 requestRoutineExit
- **CommunicationControl (0x28)**：启用/禁用通信后，响应行为变化
- **DTC 状态**：ClearDtc 后 DTC 列表变化

Phase 4 补齐这些能力：

- **Sprint 7: 有状态 ECU 模拟** — 状态机驱动的 VirtualEcu，支持状态转换、条件响应、副作用
- **Sprint 8: 接收方向故障注入 + ODX 导入** — FaultInjector 拦截 FrameReceived；ODX 文件自动生成 ECU 脚本

Out of scope: 独立模拟器进程（双 PCAN 设备，留给 Phase 5）、LLM 辅助分析（Phase 5）、ECU 仿真模型形式化验证（学术范畴）。

---

## 2. Sprint Positioning

```
+----------+-------------------+-------------+-----------------------+
|  Phase   |     ECU 模拟      |  故障注入   |        数据源         |
+----------+-------------------+-------------+-----------------------+
| Phase 3  | 无状态规则引擎    | 发送方向    | 手动 JSON             |
| Phase 4  | 有状态状态机      | 发送 + 接收 | 手动 JSON + ODX 导入  |
| Phase 5  | 有状态 + 独立进程  | 全方向      | ODX + DBC             |
+----------+-------------------+-------------+-----------------------+
```

---

## 3. Sprint 7: Stateful ECU Simulation

### 3.1 状态机模型

引入 `EcuStateMachine` -- 一个有限状态机，根据当前状态 + UDS 请求决定响应和状态转换。

```csharp
// Core/HIL/Contracts/EcuStateTransition.cs

/// <summary>
/// A state transition rule: when in a given state and a matching UDS request arrives,
/// emit a response and transition to a new state.
/// </summary>
public sealed record EcuStateTransition
{
    /// <summary>
    /// Current state name. null = wildcard (matches any state, used for stateless fallback).
    /// Using null avoids conflict with a user-defined state named "default".
    /// </summary>
    public string? FromState { get; init; }

    /// <summary>UDS Service ID to match.</summary>
    public required byte ServiceId { get; init; }

    /// <summary>Sub-function to match, or null = any.</summary>
    public byte? SubFunction { get; init; }

    /// <summary>AND-mask for request bytes [2..N]. Null = don't care.</summary>
    public byte[]? DataMask { get; init; }

    /// <summary>Expected value after masking. Must match DataMask length.</summary>
    public byte[]? DataPattern { get; init; }

    /// <summary>
    /// Response generator. Two modes:
    /// - Static: fixed byte[] response (same as UdsResponseRule)
    /// - Dynamic: function that receives the request + current context, returns response
    /// </summary>
    public required EcuResponse Response { get; init; }

    /// <summary>Next state after this transition. null = stay in current state.</summary>
    public string? ToState { get; init; }

    /// <summary>Simulated ECU processing delay (ms).</summary>
    public int ResponseDelayMs { get; init; }
}

/// <summary>
/// Response specification: either static bytes or a dynamic generator.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(StaticResponse), "static")]
[JsonDerivedType(typeof(DynamicResponse), "dynamic")]
public abstract record EcuResponse;

/// <summary>Fixed response payload.</summary>
public sealed record StaticResponse(byte[] Data) : EcuResponse;

/// <summary>
/// Dynamic response: a named generator invoked by VirtualEcu.
/// Generator name maps to a registered C# function via IEcuResponseGenerator.
/// </summary>
public sealed record DynamicResponse(string GeneratorName) : EcuResponse;
```

### 3.2 IEcuResponseGenerator

Dynamic response 需要执行 C# 逻辑（如 SecurityAccess seed/key 验证）。通过接口注入，避免在 JSON 中嵌入代码：

```csharp
// Core/HIL/Contracts/IEcuResponseGenerator.cs

/// <summary>
/// Generates a dynamic UDS response based on the request and ECU context.
/// Used for stateful responses that cannot be expressed as static byte[]
/// (e.g., SecurityAccess seed/key, DTC status after ClearDtc).
/// </summary>
public interface IEcuResponseGenerator
{
    /// <summary>Generator name (matches DynamicResponse.GeneratorName).</summary>
    string Name { get; }

    /// <summary>
    /// Generate response bytes for the given request.
    /// </summary>
    /// <param name="request">Complete UDS request payload.</param>
    /// <param name="currentState">Current ECU state name.</param>
    /// <param name="context">Shared ECU context (key-value store for stateful data).</param>
    /// <returns>Response payload (SID|0x40 + data) or NRC ([0x7F, SID, nrc]).</returns>
    byte[] Generate(byte[] request, string currentState, IEcuContext context);
}

/// <summary>
/// Shared ECU context for stateful data (seed values, unlock counters, etc.).
/// Thread-safe: uses ConcurrentDictionary internally.
/// </summary>
public interface IEcuContext
{
    /// <summary>Get a stored value, or default.</summary>
    T? Get<T>(string key);

    /// <summary>Store a value.</summary>
    void Set<T>(string key, T value);

    /// <summary>Check if a key exists.</summary>
    bool HasKey(string key);

    /// <summary>Clear all stored values.</summary>
    void Clear();
}
```

### 3.3 EcuStateMachine

```csharp
// Core/HIL/Contracts/EcuStateMachine.cs

public sealed class EcuStateMachine
{
    private readonly List<EcuStateTransition> _transitions;
    private readonly Dictionary<string, IEcuResponseGenerator> _generators;
    private readonly EcuContextStore _context = new();
    private string _currentState = "default";

    public string CurrentState => _currentState;
    public IEcuContext Context => _context;

    public EcuStateMachine(
        IEnumerable<EcuStateTransition> transitions,
        IEnumerable<IEcuResponseGenerator>? generators = null)
    {
        _transitions = transitions.ToList();
        _generators = generators?.ToDictionary(g => g.Name) ?? new();
    }

    /// <summary>
    /// Process a UDS request: find matching transition, generate response,
    /// update state. Returns NRC 0x11 if no match.
    /// Returns (response, delayMs) tuple so caller can apply delay before sending.
    /// </summary>
    public (byte[] Response, int DelayMs) ProcessRequest(byte[] request)
    {
        if (request.Length == 0)
            return (new byte[] { 0x7F, 0x00, 0x13 }, 0); // NRC 0x13 incorrectMessageLength

        var sid = request[0];

        foreach (var t in _transitions)
        {
            if (!MatchesState(t) || !MatchesRequest(t, request))
                continue;

            // Generate response
            byte[] response = t.Response switch
            {
                StaticResponse s => s.Data,
                DynamicResponse d => _generators.TryGetValue(d.GeneratorName, out var gen)
                    ? gen.Generate(request, _currentState, _context)
                    : new byte[] { 0x7F, sid, 0x72 }, // NRC 0x72 generalProgrammingFailure
                _ => new byte[] { 0x7F, sid, 0x72 }
            };

            // State transition
            if (t.ToState is not null)
                _currentState = t.ToState;

            return (response, t.ResponseDelayMs);
        }

        // No matching transition -> NRC 0x11 (serviceNotSupported)
        return (new byte[] { 0x7F, sid, 0x11 }, 0);
    }

    /// <summary>
    /// Wildcard: null FromState matches any state.
    /// This avoids conflict with a user-defined state named "default".
    /// </summary>
    private bool MatchesState(EcuStateTransition t)
        => t.FromState is null || t.FromState == _currentState;

    private bool MatchesRequest(EcuStateTransition t, byte[] request)
    {
        if (request[0] != t.ServiceId)
            return false;

        if (t.SubFunction.HasValue && (request.Length < 2 || request[1] != t.SubFunction.Value))
            return false;

        if (t.DataMask is not null && t.DataMask.Length > 0)
        {
            if (request.Length < 2 + t.DataMask.Length)
                return false;
            for (int i = 0; i < t.DataMask.Length; i++)
            {
                if ((request[2 + i] & t.DataMask[i]) != t.DataPattern![i])
                    return false;
            }
        }

        return true;
    }

    /// <summary>Reset to initial state.</summary>
    public void Reset()
    {
        _currentState = "default";
        _context.Clear();
    }

    /// <summary>
    /// Convert stateless UdsResponseRule list to a stateful machine.
    /// All rules go into null-FromState (wildcard) transitions for backward compatibility.
    /// </summary>
    public static EcuStateMachine FromRules(IEnumerable<UdsResponseRule> rules)
    {
        var transitions = rules.Select(r => new EcuStateTransition
        {
            FromState = null, // wildcard: matches any state
            ServiceId = r.ServiceId,
            SubFunction = r.SubFunction,
            DataMask = r.DataMask,
            DataPattern = r.DataPattern,
            Response = new StaticResponse(r.ResponseData),
            ResponseDelayMs = r.ResponseDelayMs,
            ToState = null,
        });
        return new EcuStateMachine(transitions);
    }
}

/// <summary>
/// Thread-safe context store using ConcurrentDictionary.
/// </summary>
internal sealed class EcuContextStore : IEcuContext
{
    private readonly ConcurrentDictionary<string, object?> _values = new();

    public T? Get<T>(string key) => _values.TryGetValue(key, out var v) && v is T t ? t : default;
    public void Set<T>(string key, T value) => _values[key] = value;
    public bool HasKey(string key) => _values.ContainsKey(key);
    public void Clear() => _values.Clear();
}
```

### 3.4 StatefulVirtualEcu

替换 Phase 3 的无状态 VirtualEcu，使用 `EcuStateMachine` 代替 `List<UdsResponseRule>`：

```csharp
// Infrastructure/HIL/StatefulVirtualEcu.cs

public sealed class StatefulVirtualEcu : IDisposable
{
    private readonly ICanChannel _channel;
    private readonly IsoTpLayer _isoTp;
    private readonly EcuStateMachine _stateMachine;
    private readonly CanIdConfig _ecuCanIds;
    private readonly ILogger<StatefulVirtualEcu>? _logger;
    private int _disposed;

    public static int InstanceCount;

    public string CurrentState => _stateMachine.CurrentState;

    /// <summary>
    /// ECU's send CAN ID (HIL listens here). Maps to CanIds.ResponseId (ECU perspective).
    /// </summary>
    public uint SendCanId => _ecuCanIds.ResponseId;

    public StatefulVirtualEcu(ICanChannel channel, CanIdConfig ecuCanIds,
        EcuStateMachine stateMachine, ILogger<StatefulVirtualEcu>? logger = null)
    {
        _channel = channel;
        _ecuCanIds = ecuCanIds;
        _stateMachine = stateMachine;
        _logger = logger;
        Interlocked.Increment(ref InstanceCount);

        _isoTp = new IsoTpLayer(ecuCanIds, SendFrameAsync, logger: null);
        _isoTp.MessageReceived += OnUdsRequestReceived;
        _channel.FrameReceived += OnCanFrameReceived;
    }

    private void OnCanFrameReceived(CanFrame frame)
    {
        try { _isoTp.ProcessFrame(frame); }
        catch (ArgumentException) { /* frame filtered by CAN ID - normal */ }
    }

    private void OnUdsRequestReceived(byte[] request)
    {
        var (response, delayMs) = _stateMachine.ProcessRequest(request);
        _ = SendResponseAsync(response, delayMs);
    }

    private async Task SendResponseAsync(byte[] data, int delayMs)
    {
        if (delayMs > 0)
            await Task.Delay(delayMs).ConfigureAwait(false);

        await _isoTp.SendMessageAsync(data).ConfigureAwait(false);
    }

    private Task SendFrameAsync(CanFrame frame)
        => _channel.WriteAsync(frame, CancellationToken.None).AsTask();

    public void Reset() => _stateMachine.Reset();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _channel.FrameReceived -= OnCanFrameReceived;
        _isoTp.MessageReceived -= OnUdsRequestReceived;
        _isoTp.Dispose();
    }
}
```

### 3.5 ECU Script JSON Format（扩展）

**互斥规则**：`states` 和 `rules` 字段互斥。如果 JSON 同时包含两者，抛出 `JsonException("Cannot specify both 'states' and 'rules'")`。迁移时需手动选择一种格式。

无状态格式（Phase 3 向后兼容）：
```json
{
  "name": "BMS",
  "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
  "rules": [ { "serviceId": "0x3E", "subFunction": 0, "responseData": [126] } ]
}
```

有状态格式（Phase 4 新增）：
```json
{
  "name": "BMS_Secure",
  "canIds": { "requestId": "0x7E0", "responseId": "0x7E8" },
  "states": [
    {
      "name": "locked",
      "transitions": [
        {
          "serviceId": "0x27",
          "subFunction": 1,
          "response": { "$type": "dynamic", "generatorName": "SecurityAccessSeed" },
          "toState": "seedSent"
        },
        {
          "serviceId": "0x27",
          "subFunction": 2,
          "response": { "$type": "static", "data": [127, 39, 35] },
          "comment": "NRC 0x22 = conditionsNotCorrect (must request seed first)"
        },
        {
          "serviceId": "0x2E",
          "response": { "$type": "static", "data": [127, 46, 34] },
          "comment": "NRC 0x22 = securityAccessDenied (writeData requires unlock)"
        }
      ]
    },
    {
      "name": "seedSent",
      "transitions": [
        {
          "serviceId": "0x27",
          "subFunction": 1,
          "response": { "$type": "dynamic", "generatorName": "SecurityAccessSeed" },
          "toState": "seedSent"
        },
        {
          "serviceId": "0x27",
          "subFunction": 2,
          "response": { "$type": "dynamic", "generatorName": "SecurityAccessVerifyKey" },
          "toState": "unlocked"
        }
      ]
    },
    {
      "name": "unlocked",
      "transitions": [
        {
          "serviceId": "0x2E",
          "dataMask": [255, 255],
          "dataPattern": [241, 144],
          "response": { "$type": "static", "data": [110, 241, 144] },
          "comment": "WriteDataByIdentifier 0xF190 (VIN) - positive response"
        },
        {
          "serviceId": "0x27",
          "subFunction": 2,
          "response": { "$type": "static", "data": [103] },
          "comment": "Already unlocked - positive response"
        }
      ]
    }
  ]
}
```

### 3.6 内置响应生成器

Phase 4 提供常用生成器，用户无需自己实现 `IEcuResponseGenerator`：

```csharp
// Infrastructure/HIL/Generators/SecurityAccessSeedGenerator.cs

public sealed class SecurityAccessSeedGenerator : IEcuResponseGenerator
{
    public string Name => "SecurityAccessSeed";

    public byte[] Generate(byte[] request, string currentState, IEcuContext context)
    {
        if (!context.HasKey("SecuritySeed"))
        {
            var seed = new byte[4];
            Random.Shared.GetBytes(seed);
            context.Set("SecuritySeed", seed);
        }

        var seedBytes = context.Get<byte[]>("SecuritySeed")!;
        return new byte[] { 0x67, 0x01 }  // SID|0x40 + subFunc
            .Concat(seedBytes).ToArray();
    }
}

// Infrastructure/HIL/Generators/SecurityAccessVerifyKeyGenerator.cs

public sealed class SecurityAccessVerifyKeyGenerator : IEcuResponseGenerator
{
    public string Name => "SecurityAccessVerifyKey";

    public byte[] Generate(byte[] request, string currentState, IEcuContext context)
    {
        if (!context.HasKey("SecuritySeed"))
            return new byte[] { 0x7F, 0x27, 0x22 }; // NRC conditionsNotCorrect

        var seed = context.Get<byte[]>("SecuritySeed")!;
        var expectedKey = seed.Select(b => (byte)(b ^ 0xAA)).ToArray();

        if (request.Length < 4)
            return new byte[] { 0x7F, 0x27, 0x13 }; // NRC incorrectMessageLength

        var receivedKey = request.Skip(2).Take(expectedKey.Length).ToArray();
        if (!receivedKey.SequenceEqual(expectedKey))
            return new byte[] { 0x7F, 0x27, 0x35 }; // NRC invalidKey

        context.Set("SecurityUnlocked", true);
        return new byte[] { 0x67, 0x02 }; // positive response
    }
}

// Infrastructure/HIL/Generators/ClearDtcGenerator.cs

public sealed class ClearDtcGenerator : IEcuResponseGenerator
{
    public string Name => "ClearDtc";

    public byte[] Generate(byte[] request, string currentState, IEcuContext context)
    {
        context.Set("DtcList", new List<(uint Code, byte Status)>());
        return new byte[] { 0x54 }; // positive response for ClearDiagnosticInformation
    }
}
```

### 3.7 EcuScript record 迁移

`EcuScript` record 从 `IReadOnlyList<UdsResponseRule>` 迁移到 `EcuStateMachine`：

```csharp
// Infrastructure/HIL/EcuScript.cs

/// <summary>
/// Parsed ECU simulator script. CanIdConfig is in ECU perspective (IDs swapped from HIL perspective).
/// StateMachine encapsulates both stateless (Phase 3) and stateful (Phase 4) rules.
/// </summary>
public sealed record EcuScript(
    string Name,
    CanIdConfig CanIds,
    EcuStateMachine StateMachine);
```

### 3.8 EcuScriptLoader 扩展

`Load` 和 `ParseEcuScript` 方法在 §3.9 中定义。以下为辅助方法，由 `ParseEcuScript` 调用：

```csharp
// Infrastructure/HIL/EcuScriptLoader.cs (辅助方法)

/// <summary>
/// Parse canIds from HIL perspective and swap to ECU perspective.
/// ECU listens on HIL's RequestId, sends on HIL's ResponseId.
/// </summary>
private static CanIdConfig ParseCanIds(JsonElement canIdsEl)
{
    var requestIdHil = ParseCanId(canIdsEl.GetProperty("requestId"));
    var responseIdHil = ParseCanId(canIdsEl.GetProperty("responseId"));
    var isExtended = canIdsEl.TryGetProperty("isExtendedFrame", out var ext) && ext.GetBoolean();

    return new CanIdConfig
    {
        RequestId = responseIdHil,   // ECU sends on HIL's ResponseId
        ResponseId = requestIdHil,   // ECU receives on HIL's RequestId
        IsExtendedFrame = isExtended
    };
}

/// <summary>
/// Parse states array into EcuStateMachine.
/// Uses JsonSerializer with HILJsonOptions for polymorphic EcuResponse deserialization.
/// </summary>
private static EcuStateMachine ParseStateMachine(JsonElement statesEl, List<IEcuResponseGenerator> generators)
{
    var allTransitions = new List<EcuStateTransition>();

    foreach (var stateEl in statesEl.EnumerateArray())
    {
        var stateName = stateEl.GetProperty("name").GetString()!;
        var transitionsEl = stateEl.GetProperty("transitions");

        foreach (var transitionEl in transitionsEl.EnumerateArray())
        {
            var transition = ParseTransition(transitionEl, stateName);
            allTransitions.Add(transition);
        }
    }

    return new EcuStateMachine(allTransitions, generators);
}

/// <summary>
/// Parse a single transition from JSON.
/// Uses HILJsonOptions for polymorphic EcuResponse deserialization ($type discriminator).
/// </summary>
private static EcuStateTransition ParseTransition(JsonElement el, string stateName)
{
    var serviceIdEl = el.GetProperty("serviceId");
    var serviceId = serviceIdEl.ValueKind == JsonValueKind.Number
        ? serviceIdEl.GetByte()
        : ParseHexString(serviceIdEl.GetString()!);

    byte? subFunction = null;
    if (el.TryGetProperty("subFunction", out var subFunc) && subFunc.ValueKind != JsonValueKind.Null)
    {
        subFunction = subFunc.ValueKind == JsonValueKind.Number
            ? subFunc.GetByte()
            : ParseHexString(subFunc.GetString()!);
    }

    byte[]? dataMask = null;
    byte[]? dataPattern = null;
    if (el.TryGetProperty("dataMask", out var mask))
    {
        dataMask = mask.EnumerateArray().Select(b => b.GetByte()).ToArray();
        dataPattern = el.GetProperty("dataPattern").EnumerateArray().Select(b => b.GetByte()).ToArray();
    }

    // Parse polymorphic response using HILJsonOptions
    var responseEl = el.GetProperty("response");
    var response = JsonSerializer.Deserialize<EcuResponse>(responseEl.GetRawText(), HILJsonOptions.Default)
        ?? throw new JsonException("Failed to parse response in transition.");

    string? toState = null;
    if (el.TryGetProperty("toState", out var toStateEl) && toStateEl.ValueKind != JsonValueKind.Null)
    {
        toState = toStateEl.GetString();
    }

    var delayMs = el.TryGetProperty("responseDelayMs", out var delay) ? delay.GetInt32() : 0;

    return new EcuStateTransition
    {
        FromState = stateName,
        ServiceId = serviceId,
        SubFunction = subFunction,
        DataMask = dataMask,
        DataPattern = dataPattern,
        Response = response,
        ToState = toState,
        ResponseDelayMs = delayMs
    };
}

private static List<IEcuResponseGenerator> GetBuiltInGenerators()
{
    return new()
    {
        new SecurityAccessSeedGenerator(),
        new SecurityAccessVerifyKeyGenerator(),
        new ClearDtcGenerator(),
    };
}
```

### 3.9 EcuMatrix + HeadlessHostBuilder 扩展

EcuMatrix 完整迁移到 StatefulVirtualEcu：

```csharp
// Infrastructure/HIL/EcuMatrix.cs

using Microsoft.Extensions.Logging;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.CanChannels;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Multiple StatefulVirtualEcu instances sharing a single VirtualChannel.
/// Each ECU responds to different CAN ID pairs.
/// </summary>
public sealed class EcuMatrix : IDisposable
{
    private readonly List<StatefulVirtualEcu> _ecus = new();
    private readonly VirtualChannel _channel;
    private int _disposed;

    public EcuMatrix(int channelCapacity = 1000)
    {
        _channel = new VirtualChannel(channelCapacity);
    }

    public void AddEcu(EcuScript script, ILogger<StatefulVirtualEcu>? logger = null)
    {
        var ecu = new StatefulVirtualEcu(_channel, script.CanIds, script.StateMachine, logger);

        // CAN ID conflict detection: two ECUs cannot send on the same CAN ID
        var newSendId = ecu.SendCanId;
        if (_ecus.Any(e => e.SendCanId == newSendId))
        {
            ecu.Dispose();
            throw new InvalidOperationException(
                $"CAN ID conflict: send ID 0x{newSendId:X3} already assigned to another ECU");
        }

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

HeadlessHostBuilder 修改：

**关键组装约束**：`ReceivePathFaultInjector` 必须是最外层装饰器，所有消费者（`StatefulVirtualEcu` + `HILAssertionContext`）都订阅它的 `FrameReceived`，确保 `VirtualChannel` 只有一个直接订阅者（满足 `SingleReader = true`）。

```csharp
// Infrastructure/HIL/HeadlessHostBuilder.cs (修改)

private static void RegisterVirtualEcuMode(HostApplicationBuilder builder, CliArgs args)
{
    var script = EcuScriptLoader.Load(args.EcuScriptPath!);
    var channel = new VirtualChannel();

    // 如果启用故障注入，包裹通道：ReceivePathFaultInjector(FaultInjector(channel))
    ICanChannel effectiveChannel = channel;
    ReceivePathFaultInjector? rxFault = null;
    if (args.EnableFaultInjection)
    {
        var txFault = new FaultInjector(channel);
        rxFault = new ReceivePathFaultInjector(txFault);
        effectiveChannel = rxFault;
    }

    // 关键：注册 effectiveChannel（而非原始 channel）到 DI 容器
    // 这样 HILAssertionContext 和 StatefulVirtualEcu 都订阅同一个 ReceivePathFaultInjector
    // VirtualChannel 只有 1 个直接订阅者（ReceivePathFaultInjector），满足 SingleReader=true
    builder.Services.AddSingleton<ICanChannel>(effectiveChannel);

    // StatefulVirtualEcu 订阅 effectiveChannel
    var ecu = new StatefulVirtualEcu(effectiveChannel, script.CanIds, script.StateMachine, logger: null);
    builder.Services.AddSingleton(ecu);

    // HILAssertionContext DI 注册：注入 faultInjector 和 receiveFaultInjector
    builder.Services.AddSingleton<IAssertionContext>(sp =>
    {
        var ch = sp.GetRequiredService<ICanChannel>();  // = effectiveChannel
        var dbc = sp.GetRequiredService<IDbcLookup>();
        return new HILAssertionContext(ch, dbc, args.EnableFaultInjection, txFault, rxFault);
    });

    RegisterUdsServices(builder, args);
}
```

注意：`HILAssertionContext` 不再自己创建 `ReceivePathFaultInjector`——它由 `HeadlessHostBuilder` 在更高层组装。`HILAssertionContext` 通过构造函数注入 `ICanChannel`（已经是装饰后的通道）和 `ReceivePathFaultInjector`（由 DI 注入）。完整实现见下方 §4.2。

// Matrix 模式同样改用 StatefulVirtualEcu
// 注意：Matrix 模式暂不支持故障注入（EcuMatrix 内部多个 StatefulVirtualEcu 共享通道，
// 无法在外部包裹 ReceivePathFaultInjector）。如需故障注入，使用 --ecu 单 ECU 模式。
else if (args.MatrixPath is not null)
{
    if (args.EnableFaultInjection)
        throw new ArgumentException("--enable-faults is not supported with --matrix. Use --ecu for single ECU with fault injection.");

    // 不传 generators：MatrixConfigLoader 内部调用 EcuScriptLoader 时，
    // ParseEcuScript 的 generators ?? GetBuiltInGenerators() fallback 自动使用内置生成器
    var config = MatrixConfigLoader.LoadFromFile(args.MatrixPath);
    var matrix = new EcuMatrix();
    foreach (var script in config.Ecus)
        matrix.AddEcu(script);
    builder.Services.AddSingleton(_ => matrix);
    builder.Services.AddSingleton<ICanChannel>(_ => matrix.Channel);
}
```

MatrixConfigLoader 扩展（注入生成器到 EcuScriptLoader）：

```csharp
// Infrastructure/HIL/MatrixConfigLoader.cs (扩展)

public static MatrixConfig LoadFromFile(string path, List<IEcuResponseGenerator>? generators = null)
{
    var json = File.ReadAllText(path);
    var basePath = Path.GetDirectoryName(Path.GetFullPath(path));
    return Parse(json, basePath, generators);
}

public static MatrixConfig Parse(string json, string? basePath = null,
    List<IEcuResponseGenerator>? generators = null)
{
    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    var name = root.GetProperty("name").GetString()!;
    var ecus = new List<EcuScript>();

    foreach (var ecuEl in root.GetProperty("ecus").EnumerateArray())
    {
        EcuScript ecuScript;
        if (ecuEl.TryGetProperty("scriptPath", out var scriptPathEl))
        {
            var scriptPath = scriptPathEl.GetString()!;
            var fullPath = basePath is not null
                ? Path.Combine(basePath, scriptPath)
                : scriptPath;
            // Path traversal guard
            if (basePath is not null)
            {
                var baseFullPath = Path.GetFullPath(basePath);
                var resolvedFullPath = Path.GetFullPath(fullPath);
                if (!resolvedFullPath.StartsWith(baseFullPath + Path.DirectorySeparatorChar)
                    && resolvedFullPath != baseFullPath)
                    throw new InvalidOperationException($"scriptPath escapes base directory: {scriptPath}");
            }
            ecus.Add(EcuScriptLoader.Load(fullPath, generators));
        }
        else
        {
            ecus.Add(EcuScriptLoader.ParseEcuScript(ecuEl, generators));
        }
    }

    return new MatrixConfig(name, ecus);
}
```

EcuScriptLoader.ParseEcuScript 扩展（接收 generators）：

```csharp
// Infrastructure/HIL/EcuScriptLoader.cs (扩展签名)

public static EcuScript Load(string path, List<IEcuResponseGenerator>? generators = null)
{
    var json = File.ReadAllText(path);
    using var doc = JsonDocument.Parse(json);
    return ParseEcuScript(doc.RootElement, generators);
}

public static EcuScript ParseEcuScript(JsonElement element,
    List<IEcuResponseGenerator>? generators = null)
{
    var name = element.GetProperty("name").GetString()!;
    var canIds = ParseCanIds(element.GetProperty("canIds"));

    var hasStates = element.TryGetProperty("states", out _);
    var hasRules = element.TryGetProperty("rules", out _);
    if (hasStates && hasRules)
        throw new JsonException("Cannot specify both 'states' and 'rules' in ECU script.");

    EcuStateMachine stateMachine;

    if (hasStates)
    {
        stateMachine = ParseStateMachine(element.GetProperty("states"),
            generators ?? GetBuiltInGenerators());
    }
    else if (hasRules)
    {
        var rules = ParseRules(element.GetProperty("rules"));
        stateMachine = EcuStateMachine.FromRules(rules);
    }
    else
    {
        throw new JsonException("ECU script must specify either 'states' or 'rules'.");
    }

    return new EcuScript(name, canIds, stateMachine);
}
```

---

## 4. Sprint 8: Receive-Path Fault Injection + ODX Import

### 4.1 ReceivePathFaultInjector

Phase 3 的 `FaultInjector` 只装饰 `WriteAsync`。接收方向需要一个中间层拦截 `FrameReceived` 事件：

```csharp
// Infrastructure/Channel/ReceivePathFaultInjector.cs

public sealed class ReceivePathFaultInjector : ICanChannel
{
    private readonly ICanChannel _inner;
    private readonly object _faultsLock = new();
    private readonly List<FaultRule> _receiveFaults = new();
    private readonly object _subscribersLock = new();
    private Action<CanFrame>? _subscribers;
    private int _subscriberCount;

    public ChannelId Id => _inner.Id;
    public bool IsConnected => _inner.IsConnected;

    public event Action<CanFrame>? FrameReceived
    {
        add
        {
            lock (_subscribersLock) _subscribers += value;
            if (Interlocked.CompareExchange(ref _subscriberCount, 1, 0) == 0)
                _inner.FrameReceived += OnInnerFrameReceived;
            else
                Interlocked.Increment(ref _subscriberCount);
        }
        remove
        {
            lock (_subscribersLock) _subscribers -= value;
            // Guard against underflow: only unsubscribe from inner when count reaches 0
            if (Interlocked.Decrement(ref _subscriberCount) <= 0)
            {
                _inner.FrameReceived -= OnInnerFrameReceived;
                _subscriberCount = 0; // Clamp to 0 to prevent permanent negative
            }
        }
    }

    public event Action<ReadLoopError>? ReadLoopError
    {
        add => _inner.ReadLoopError += value;
        remove => _inner.ReadLoopError -= value;
    }

    public ReceivePathFaultInjector(ICanChannel inner) => _inner = inner;

    public IDisposable AddReceiveFault(FaultRule fault)
    {
        lock (_faultsLock) _receiveFaults.Add(fault);
        return new FaultHandle(() => { lock (_faultsLock) _receiveFaults.Remove(fault); });
    }

    private readonly ConcurrentDictionary<int, Task> _pendingDelayTasks = new();
    private int _taskIdCounter;

    private void OnInnerFrameReceived(CanFrame frame)
    {
        // Single snapshot for both Delay and non-Delay processing (avoids double-lock inconsistency)
        List<FaultRule> snapshot;
        lock (_faultsLock) snapshot = _receiveFaults.ToList();

        // Handle Delay faults first (same pattern as FaultInjector.WriteAsync)
        int maxDelay = snapshot
            .Where(f => f.Type == FaultType.Delay && f.Matches(frame))
            .Select(f => f.DelayMs)
            .DefaultIfEmpty(0)
            .Max();

        if (maxDelay > 0)
        {
            // Async delay: capture subscribers snapshot, delay, then dispatch
            Action<CanFrame>? handler;
            lock (_subscribersLock) handler = _subscribers;
            if (handler is not null)
            {
                var taskId = Interlocked.Increment(ref _taskIdCounter);
                var task = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(maxDelay).ConfigureAwait(false);
                        ApplyAndDispatch(frame, handler, snapshot);
                    }
                    finally
                    {
                        _pendingDelayTasks.TryRemove(taskId, out _);
                    }
                });
                _pendingDelayTasks[taskId] = task;
            }
            return;
        }

        ApplyAndDispatch(frame, null, snapshot);
    }

    /// <summary>
    /// Apply non-Delay faults from the pre-taken snapshot and dispatch to subscribers.
    /// Uses the snapshot passed from OnInnerFrameReceived to ensure consistency.
    /// </summary>
    private void ApplyAndDispatch(CanFrame frame, Action<CanFrame>? handlerOverride,
        List<FaultRule> snapshot)
    {
        var frames = new List<CanFrame> { frame };
        foreach (var fault in snapshot.Where(f => f.Type != FaultType.Delay))
        {
            if (!fault.Matches(frame)) continue;
            var next = new List<CanFrame>();
            foreach (var f in frames)
                next.AddRange(fault.Apply(f));
            frames = next;
        }

        Action<CanFrame>? handler = handlerOverride;
        if (handler is null)
        {
            lock (_subscribersLock) handler = _subscribers;
        }
        if (handler is null) return;

        foreach (var f in frames)
        {
            foreach (var sub in handler.GetInvocationList())
            {
                try { sub.DynamicInvoke(f); }
                catch { /* isolate per-subscriber exceptions */ }
            }
        }
    }

    /// <summary>
    /// Wait for all pending delay tasks to complete (called from Dispose path).
    /// </summary>
    internal async Task WaitForPendingDelaysAsync(TimeSpan timeout)
    {
        var tasks = _pendingDelayTasks.Values.ToArray();
        if (tasks.Length > 0)
            await Task.WhenAll(tasks).WaitAsync(timeout).ConfigureAwait(false);
    }

    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        => _inner.WriteAsync(frame, ct);

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        => _inner.ConnectAsync(baud, fd, ct);

    public Task DisconnectAsync(CancellationToken ct = default)
        => _inner.DisconnectAsync(ct);

    public async ValueTask DisposeAsync()
    {
        // Wait for pending delay tasks to complete before disposing inner channel
        try { await WaitForPendingDelaysAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { /* force continue with disposal */ }
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}
```

设计约束：
- `ReceivePathFaultInjector` 独立于 `FaultInjector`（发送方向）。两者可以组合使用。
- Delay 故障通过 `Task.Run` 异步分发，不阻塞底层 ConsumerLoop。
- `FrameReceived` 事件不再直接转发到底层通道 -- 而是通过 `OnInnerFrameReceived` 中间层。
- 计数器下界保护：`Interlocked.Decrement` 后 clamp 到 0，防止双次 remove 导致永久失效。

### 4.2 组合发送 + 接收方向故障注入

```csharp
var sendFault = new FaultInjector(channel);
var bothFault = new ReceivePathFaultInjector(sendFault);
// bothFault.WriteAsync -> FaultInjector.WriteAsync -> channel.WriteAsync (发送方向故障)
// channel.FrameReceived -> ReceivePathFaultInjector.OnInnerFrameReceived -> 订阅者 (接收方向故障)
```

**组装顺序**：`ReceivePathFaultInjector` 必须是最外层装饰器。所有消费者（`StatefulVirtualEcu` + `HILAssertionContext`）都订阅 `ReceivePathFaultInjector.FrameReceived`，确保 `VirtualChannel` 只有一个直接订阅者（`ReceivePathFaultInjector`），满足 `SingleReader = true` 约束。

`IFaultInjectionContext` 扩展：

```csharp
// Core/HIL/Contracts/IFaultInjectionContext.cs

public interface IFaultInjectionContext
{
    /// <summary>Add a send-direction fault rule. Returns a disposable handle for removal.</summary>
    IDisposable AddFault(FaultRule fault);

    /// <summary>Add a receive-direction fault rule. Returns a disposable handle for removal.</summary>
    IDisposable AddReceiveFault(FaultRule fault);

    /// <summary>Tag a fault handle with an ID for targeted clearing.</summary>
    void TagFault(string faultId, IDisposable handle);

    /// <summary>Remove all faults, or only those matching the given ID.</summary>
    void ClearFaults(string? faultId = null);
}
```

HILAssertionContext 修改（接收 ReceivePathFaultInjector 通过 DI 注入，不再自己创建）：

```csharp
// Infrastructure/HIL/HILAssertionContext.cs (修改)

internal sealed class HILAssertionContext : IAssertionContext, IFaultInjectionContext, IHasRecentFrames, IDisposable
{
    private readonly ICanChannel _channel;
    private readonly FaultInjector? _faultInjector;
    private readonly ReceivePathFaultInjector? _receiveFaultInjector;
    // ... 其余字段 ...

    /// <summary>
    /// channel: 装饰后的通道（由 HeadlessHostBuilder 组装，已是 ReceivePathFaultInjector 或原始 VirtualChannel）
    /// faultInjector: 发送方向故障注入器（由 HeadlessHostBuilder 创建并注入）
    /// receiveFaultInjector: 接收方向故障注入器（由 HeadlessHostBuilder 创建并注入，可能为 null）
    /// </summary>
    public HILAssertionContext(ICanChannel channel, IDbcLookup dbcLookup,
        bool enableFaultInjection = false,
        FaultInjector? faultInjector = null,
        ReceivePathFaultInjector? receiveFaultInjector = null)
    {
        _channel = channel;
        _dbcLookup = dbcLookup;
        _faultInjector = faultInjector;
        _receiveFaultInjector = receiveFaultInjector;

        // 发送方向故障路径：HILAssertionContext.SendFrameAsync -> _channel.WriteAsync
        //   -> ReceivePathFaultInjector.WriteAsync -> FaultInjector.WriteAsync -> VirtualChannel
        // faultInjector 由 HeadlessHostBuilder 创建并注入，_rules 通过 AddFault 添加到此实例。

        _frameChannel = System.Threading.Channels.Channel.CreateBounded<CanFrame>(...);
        _frameSubscription = new FrameReceivedSubscription(channel, OnFrame);
        _consumerTask = Task.Run(() => ConsumerLoop(_consumerCts.Token));
    }

    public ValueTask<Result<Unit>> SendFrameAsync(CanFrame frame, CancellationToken ct = default)
        => _channel.WriteAsync(frame, ct);

    // --- IFaultInjectionContext ---

    public IDisposable AddFault(FaultRule fault)
    {
        if (_faultInjector is null)
            throw new InvalidOperationException("Fault injection not enabled");
        return _faultInjector.AddFault(fault);
    }

    public IDisposable AddReceiveFault(FaultRule fault)
    {
        if (_receiveFaultInjector is null)
            throw new InvalidOperationException("Receive fault injection not enabled");
        return _receiveFaultInjector.AddReceiveFault(fault);
    }

    public void TagFault(string faultId, IDisposable handle)
        => _faultHandles[faultId] = handle;  // CompositeHandle cleans up both directions

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

### 4.3 InjectFaultStep 扩展 + Executor 修改

```csharp
// Core/HIL/StepParams/InjectFaultStep.cs

public sealed record InjectFaultStep(
    CanId CanId,
    FaultType FaultType,
    double Probability,
    int DelayMs,
    int[]? CorruptByteIndices,
    byte CorruptXorMask,
    string? FaultId,
    FaultDirection Direction = FaultDirection.Send  // Phase 4 新增，默认向后兼容
) : StepParameters(TestCaseStepKind.InjectFault);

/// <summary>
/// Fault injection direction. Send = HIL -> ECU, Receive = ECU -> HIL.
/// Defined in PeakCan.Host.Core.HIL namespace.
/// </summary>
public enum FaultDirection
{
    Send,       // HIL -> ECU (Phase 3)
    Receive,    // ECU -> HIL (Phase 4)
    Both        // 双向 (Phase 4)
}
```

InjectFaultStepExecutor 修改：

```csharp
// Core/HIL/StepExecutor/InjectFaultStepExecutor.cs (修改)

public async Task<StepResult> ExecuteAsync(TestCaseStep step, Contracts.IAssertionContext ctx, CancellationToken ct)
{
    var p = (InjectFaultStep)step.Parameters;

    if (ctx is not IFaultInjectionContext faultCtx)
    {
        return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
            "Context does not support fault injection", null, null, 0);
    }

    try
    {
        // FaultRule is a sealed record with init-only properties — use object initializer
        var rule = new FaultRule
        {
            Type = p.FaultType,
            TargetCanId = p.CanId.Raw == 0 ? null : p.CanId.Raw,
            Probability = p.FaultType == FaultType.Drop ? p.Probability : 1.0,
            DelayMs = p.DelayMs,
            CorruptByteIndices = p.CorruptByteIndices,
            CorruptXorMask = p.CorruptXorMask,
        };

        IDisposable? sendHandle = null;
        IDisposable? receiveHandle = null;

        switch (p.Direction)
        {
            case FaultDirection.Send:
                sendHandle = faultCtx.AddFault(rule);
                break;
            case FaultDirection.Receive:
                // AddReceiveFault is on IFaultInjectionContext (Phase 4 extension) — no cast needed
                receiveHandle = faultCtx.AddReceiveFault(rule);
                break;
            case FaultDirection.Both:
                sendHandle = faultCtx.AddFault(rule);
                receiveHandle = faultCtx.AddReceiveFault(rule);
                break;
        }

        // CompositeHandle disposes both handles
        var handle = new CompositeHandle(sendHandle, receiveHandle);

        if (p.FaultId is not null)
            faultCtx.TagFault(p.FaultId, handle);

        return new StepResult(0, step.Kind, step.Label, StepStatus.Passed,
            $"Fault injected: {p.FaultType} ({p.Direction})", null, null, 0);
    }
    catch (Exception ex)
    {
        return new StepResult(0, step.Kind, step.Label, StepStatus.Failed,
            $"Inject fault failed: {ex.Message}", null, null, 0);
    }
}

/// <summary>
/// Wraps up to two disposable handles (send + receive) into a single IDisposable.
/// </summary>
internal sealed class CompositeHandle : IDisposable
{
    private IDisposable? _send;
    private IDisposable? _receive;

    internal CompositeHandle(IDisposable? send, IDisposable? receive)
    {
        _send = send;
        _receive = receive;
    }

    public void Dispose()
    {
        _send?.Dispose();
        _receive?.Dispose();
    }
}
```

### 4.4 ODX 导入

```csharp
// Infrastructure/HIL/Odx/OdxEcuScriptImporter.cs

public static class OdxEcuScriptImporter
{
    public static string ImportToJson(
        string odxPath, string ecuName, uint requestId, uint responseId)
    {
        var doc = XDocument.Load(odxPath);
        var services = ParseOdxServices(doc);

        if (services.Count == 0)
            throw new InvalidOperationException($"No UDS services found in ODX file: {odxPath}");

        var rules = services.Select(s => new
        {
            serviceId = $"0x{s.Sid:X2}",
            subFunction = s.SubFunction,
            responseData = s.PositiveResponseBytes,
            responseDelayMs = 10
        });

        var script = new
        {
            name = ecuName,
            canIds = new { requestId = $"0x{requestId:X3}", responseId = $"0x{responseId:X3}" },
            rules
        };

        // Use HILJsonOptions for consistent formatting (camelCase, ByteArrayJsonConverter)
        return JsonSerializer.Serialize(script, HILJsonOptions.Default);
    }

    private record OdxService(byte Sid, byte? SubFunction, byte[] PositiveResponseBytes);

    /// <summary>
    /// Parse ODX DIAG-COMM elements to extract UDS services.
    /// Supports ODX 2.0/2.2 format. Unknown elements are skipped with a warning.
    /// </summary>
    private static List<OdxService> ParseOdxServices(XDocument doc)
    {
        var services = new List<OdxService>();

        // ODX 2.0/2.2: services defined under <DIAG-COMM-SPEC>/<DIAG-COMM>
        var diagComms = doc.Descendants().Where(e =>
            e.Name.LocalName == "DIAG-COMM" || e.Name.LocalName == "DIAG-COMM-SPEC");

        foreach (var comm in diagComms)
        {
            // Extract SID from <REQUEST-REF> or <DIAG-SERVICE> elements
            var requestRef = comm.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "REQUEST-REF");
            if (requestRef is null) continue;

            var sidAttr = requestRef.Attribute("ID-REF");
            if (sidAttr is null) continue;

            // Parse SID from ODX service ID format (e.g., "SID_0x22" or hex value)
            var sid = ParseSidFromOdx(sidAttr.Value);
            if (sid is null) continue;

            // Extract positive response bytes from <RESPONSE> elements
            var responseBytes = ParsePositiveResponseBytes(comm);

            services.Add(new OdxService(sid.Value, null, responseBytes));
        }

        return services;
    }

    private static byte? ParseSidFromOdx(string odxId)
    {
        // ODX format: "SID_0x22" or just "0x22"
        if (odxId.StartsWith("SID_", StringComparison.OrdinalIgnoreCase))
            odxId = odxId[4..];
        if (odxId.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return byte.Parse(odxId[2..], NumberStyles.HexNumber);
        return byte.TryParse(odxId, out var sid) ? sid : null;
    }

    private static byte[] ParsePositiveResponseBytes(XElement comm)
    {
        // Extract response bytes from <POS-RESPONSE> or <RESPONSE> elements
        var responseEl = comm.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "POS-RESPONSE");
        if (responseEl is null) return Array.Empty<byte>();

        // Parse byte pattern from response element
        // (simplified: extract from <PARAM> elements with coded value)
        var bytes = new List<byte>();
        foreach (var param in responseEl.Descendants().Where(e => e.Name.LocalName == "PARAM"))
        {
            var codedValue = param.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "CODED-VALUE");
            if (codedValue is not null && byte.TryParse(codedValue.Value, out var b))
                bytes.Add(b);
        }
        return bytes.ToArray();
    }
}
```

已知限制：
- ODX 格式复杂（ODX 2.0/2.2，PDX 打包），Sprint 8 只支持基本 UDS 服务提取
- SecurityAccess 等有状态服务需要手动添加到 states 部分
- ODX 导入生成无状态规则；有状态部分需要手动补充

### 4.5 CLI 扩展

CliArgs 新增字段（追加到现有 record 末尾，不改变现有参数顺序或类型）：

```csharp
// Infrastructure/Cli/CliArgs.cs (扩展)

// 保持现有 CliArgs 签名不变（required positional parameters），仅在末尾追加新字段：
public sealed record CliArgs(
    string? DbcPath = null,
    string? SuitePath = null,
    string? TracePath = null,
    string? OutputPath = null,
    string Format = "console",
    string? HardwareChannel = null,
    ushort UdsRequestId = 0x7DF,
    ushort UdsResponseId = 0x7E8,
    string? EcuScriptPath = null,
    bool EnableFaultInjection = false,
    string? MatrixPath = null,
    // Phase 4 新增:
    string? ImportOdxPath = null,
    string? ImportOdxEcuName = null,
    ushort ImportOdxRequestId = 0x7E0,
    ushort ImportOdxResponseId = 0x7E8
);
```

CliArgsParser 新增解析（在现有 switch 中追加 cases，不劫持 --uds-req/--uds-resp）：

```csharp
// Infrastructure/Cli/CliArgsParser.cs (扩展)

public static CliArgs Parse(string[] args)
{
    // ... 现有解析（包含验证：if (dbc is null || suite is null) throw ArgumentException）...
    // ... 现有 cases（--uds-req/--uds-resp 设置 udsReq/udsResp 不变）...
    string importOdx = null;
    string importEcuName = null;
    ushort importReq = 0x7E0;
    ushort importResp = 0x7E8;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            // ... 现有 cases ...
            case "--import-odx": importOdx = args[++i]; break;
            case "--ecu-name": importEcuName = args[++i]; break;
            case "--import-uds-req": importReq = ParseUdsId(args[++i]); break;  // 独立参数，不劫持 --uds-req
            case "--import-uds-resp": importResp = ParseUdsId(args[++i]); break;
        }
    }

    // 验证：非 ODX 导入模式时，DbcPath 和 SuitePath 必填
    if (importOdx is null && (dbc is null || suite is null))
        throw new ArgumentException("Must specify --dbc and --suite (or --import-odx for ODX import mode).");

    return new CliArgs(dbc, suite, trace, output, format, hw, udsReq, udsResp,
        ecu, enableFaults, matrix,
        importOdx, importEcuName, importReq, importResp);
}
```

Program.Main 新增 import-odx 分支：

```csharp
// PeakCan.Host.Cli/Program.cs (扩展)

public static async Task<int> Main(string[] args)
{
    try
    {
        var cli = CliArgsParser.Parse(args);

        // ODX 导入模式：不需要 DI 容器
        if (cli.ImportOdxPath is not null)
        {
            var json = OdxEcuScriptImporter.ImportToJson(
                cli.ImportOdxPath,
                cli.ImportOdxEcuName ?? "ImportedECU",
                cli.ImportOdxRequestId,
                cli.ImportOdxResponseId);

            if (cli.OutputPath is not null)
            {
                await File.WriteAllTextAsync(cli.OutputPath, json);
                Console.WriteLine($"ECU script written to {cli.OutputPath}");
            }
            else
            {
                Console.WriteLine(json);
            }
            return 0;
        }

        // ... 现有 HIL 测试模式 ...
    }
    catch (Exception ex) { ... }
}
```

CLI 使用示例：

```bash
# 从 ODX 生成 ECU 脚本
peakcan-hil --import-odx bms.odx --ecu-name BMS --import-uds-req 0x7E0 --import-uds-resp 0x7E8 --output bms_sim.json

# 然后正常使用
peakcan-hil --suite tests.json --ecu bms_sim.json
```

### 4.6 依赖项

Sprint 8 不需要额外添加项目引用。`System.Xml.Linq` 已通过 .NET 10 的 `Microsoft.NETCore.App` 框架引用自动包含。只需在 `OdxEcuScriptImporter.cs` 中添加 `using System.Xml.Linq;` 即可。

---

## 5. Architecture Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| P4-D1 | `EcuStateMachine` 独立于 `VirtualEcu` | 状态机逻辑可单测，不依赖 ISO-TP/channel |
| P4-D2 | 动态响应用 `IEcuResponseGenerator` 接口，非 JSON 嵌入代码 | JSON 不适合表达逻辑；接口注入可测试、可扩展 |
| P4-D3 | 无状态规则自动转换为 wildcard FromState 的静态转换 | 向后兼容 Phase 3 ECU 脚本，零迁移成本 |
| P4-D4 | `ReceivePathFaultInjector` 独立于 `FaultInjector` | 单一职责；两者可独立使用或组合 |
| P4-D5 | `InjectFaultStep` 新增 `Direction` 字段，默认 `Send` | 向后兼容 Phase 3 测试套件 JSON |
| P4-D6 | ODX 导入生成无状态规则 | ODX 不包含状态机语义；有状态部分需手动补充 |
| P4-D7 | 内置生成器在构造时注入 | 用户可直接使用 `generatorName` 引用 |
| P4-D8 | `EcuStateMachine.Reset()` 重置状态和上下文 | 支持 "每个 test case 独立状态" 的场景 |
| P4-D9 | FromState 通配符用 null 而非 "default" | 避免与用户定义的状态名 "default" 冲突 |
| P4-D10 | `EcuContextStore` 使用 `ConcurrentDictionary` | 防御性线程安全，即使生成器在异步上下文中访问 |
| P4-D11 | `ReceivePathFaultInjector` 是最外层装饰器 | 确保 `VirtualChannel` 只有一个直接订阅者，满足 SingleReader |
| P4-D12 | `ProcessRequest` 返回 `(response, delayMs)` tuple | 不破坏 ConsumerLoop 语义，延迟由调用方处理 |

---

## 6. Risk Register

| Risk | Severity | Mitigation |
|------|----------|------------|
| 状态机 JSON 格式复杂，测试作者学习成本 | MEDIUM | 提供示例库 + ODX 导入工具减少手写需求 |
| `DynamicResponse` 的 `generatorName` 在 JSON 中写错 | MEDIUM | 运行时返回 NRC 0x72，日志标注未知生成器名 |
| `ReceivePathFaultInjector` 的 `FrameReceived` 中间层引入延迟 | LOW | 延迟仅为故障规则匹配（~µs 级），不影响正常路径 |
| ODX 格式版本差异导致解析失败 | MEDIUM | 支持 ODX 2.0/2.2；不支持的元素跳过并日志警告 |
| 状态机并发访问（多个请求同时到达） | HIGH | `IsoTpLayer` 串行化消息处理（`_sendGate` semaphore），状态机不需要自己的锁 |
| SecurityAccess seed/key 算法过于简单（XOR） | LOW | 内置生成器是测试用模拟，不是安全实现 |
| `ReceivePathFaultInjector` Delay 异步分发丢失帧 | MEDIUM | `Task.Run` 分发在 `TaskScheduler` 上执行，正常退出时等待所有 pending task |
| `EcuScript` 签名变更导致 Phase 3 脚本不兼容 | LOW | `FromRules` 自动转换，零迁移成本 |

---

## 7. Sprint 7 TDD Increment Plan

| Inc | Component | Tests | Description |
|-----|-----------|-------|-------------|
| 0 | EcuContextStore | 3 | Get/Set/HasKey、Clear 重置、泛型类型安全 |
| 1 | EcuStateMachine (static) | 5 | 匹配 SID+subFunc、匹配 DataMask、状态转换、wildcard fallback、无匹配 NRC 0x11 |
| 2 | EcuStateMachine (dynamic) | 4 | DynamicResponse 调用生成器、未知生成器 NRC 0x72、生成器访问 IEcuContext、Reset |
| 3 | StatefulVirtualEcu | 5 | 单帧请求->状态转换->响应、SecurityAccess 完整流程、ClearDtc、向后兼容、Dispose |
| 4 | EcuScriptLoader (states) | 4 | 有状态 JSON 解析、无状态自动转换、canIds 交换、states+rules 互斥错误 |
| 5 | 内置生成器 | 4 | SecurityAccessSeed 生成+复用、VerifyKey 正确/错误、ClearDtc |
| 6 | CLI + EcuMatrix 集成 | 3 | --ecu 有状态端到端、多 ECU、向后兼容 |

**Total: ~28 tests**

---

## 8. Sprint 8 TDD Increment Plan

| Inc | Component | Tests | Description |
|-----|-----------|-------|-------------|
| 0 | ReceivePathFaultInjector | 7 | 透传、Drop、Corrupt、Duplicate、Delay、多订阅者隔离、双次 remove 安全 |
| 1 | InjectFaultStep Direction + Executor | 4 | Receive、Both、Send 向后兼容、executor 分发 |
| 2 | OdxEcuScriptImporter | 4 | 服务提取、responseData 生成、canIds、空 ODX 错误 |
| 3 | CLI --import-odx | 2 | 端到端、导入后可直接 --ecu |

**Total: ~17 tests**

---

## 9. Definition of Done

- [ ] ~45 new tests passing
- [ ] `dotnet build` 无 error
- [ ] Phase 3 ECU 脚本（无状态 JSON）向后兼容，无需修改
- [ ] SecurityAccess 完整流程 E2E 通过（seed → key → unlock → write）
- [ ] 接收方向故障注入 E2E 通过（Drop/Corrupt/Duplicate/Delay）
- [ ] ODX 导入端到端通过（导入 → 生成 JSON → --ecu 运行）
- [ ] `EcuStateMachine` 独立可测（不依赖 VirtualEcu/ICanChannel）
- [ ] `ReceivePathFaultInjector` 双次 remove 不破坏订阅
- [ ] `OdxEcuScriptImporter` 使用 `using System.Xml.Linq;`（.NET 10 框架已包含，无需额外引用）
