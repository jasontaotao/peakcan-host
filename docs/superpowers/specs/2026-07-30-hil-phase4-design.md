# HIL Phase 4: Stateful ECU Simulation & Receive-Path Fault Injection

> Date: 2026-07-30
> Status: Draft (v1)
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
┌─────────┬───────────────────┬─────────────┬──────────────────────┐
│  Phase  │     ECU 模拟      │  故障注入   │        数据源        │
├─────────┼───────────────────┼─────────────┼──────────────────────┤
│ Phase 3 │ 无状态规则引擎    │ 发送方向    │ 手动 JSON            │
├─────────┼───────────────────┼─────────────┼──────────────────────┤
│ Phase 4 │ 有状态状态机      │ 发送 + 接收 │ 手动 JSON + ODX 导入 │
├─────────┼───────────────────┼─────────────┼──────────────────────┤
│ Phase 5 │ 有状态 + 独立进程 │ 全方向      │ ODX + DBC            │
└─────────┴───────────────────┴─────────────┴──────────────────────┘
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
    /// <summary>Current state name. "default" matches any state (fallback).</summary>
    public required string FromState { get; init; }

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
/// </summary>
public interface IEcuContext
{
    /// <summary>Get a stored value, or default.</summary>
    T? Get<T>(string key);

    /// <summary>Store a value.</summary>
    void Set<T>(string key, T value);

    /// <summary>Check if a key exists.</summary>
    bool HasKey(string key);
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
    /// </summary>
    public byte[] ProcessRequest(byte[] request)
    {
        if (request.Length == 0)
            return new byte[] { 0x7F, 0x00, 0x13 }; // NRC 0x13 incorrectMessageLength

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

            return response;
        }

        // No matching transition -> NRC 0x11 (serviceNotSupported)
        return new byte[] { 0x7F, sid, 0x11 };
    }

    private bool MatchesState(EcuStateTransition t)
        => t.FromState == "default" || t.FromState == _currentState;

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
    /// Convert stateless UdsResponseRule list to a stateful machine (all in "default" state).
    /// Provides backward compatibility with Phase 3 ECU scripts.
    /// </summary>
    public static EcuStateMachine FromRules(IEnumerable<UdsResponseRule> rules)
    {
        var transitions = rules.Select(r => new EcuStateTransition
        {
            FromState = "default",
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

internal sealed class EcuContextStore : IEcuContext
{
    private readonly Dictionary<string, object?> _values = new();

    public T? Get<T>(string key) => _values.TryGetValue(key, out var v) ? (T?)v : default;
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
    private readonly ILogger<StatefulVirtualEcu>? _logger;
    private int _disposed;

    public string CurrentState => _stateMachine.CurrentState;

    public StatefulVirtualEcu(ICanChannel channel, CanIdConfig ecuCanIds,
        EcuStateMachine stateMachine, ILogger<StatefulVirtualEcu>? logger = null)
    {
        _channel = channel;
        _stateMachine = stateMachine;
        _logger = logger;

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
        var response = _stateMachine.ProcessRequest(request);
        _ = SendResponseAsync(response);
    }

    private async Task SendResponseAsync(byte[] data)
    {
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

向后兼容：无状态 ECU 脚本（只有 `rules` 数组）通过 `EcuStateMachine.FromRules` 自动转换为 default 状态下的静态转换规则。`EcuScriptLoader` 检测 JSON 中是否有 `states` 字段：
- 有 `states` → 解析有状态转换规则
- 只有 `rules` → 调用 `FromRules` 转换

### 3.5 ECU Script JSON Format（扩展）

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

### 3.7 EcuScriptLoader 扩展

```csharp
// Infrastructure/HIL/EcuScriptLoader.cs (扩展)

public static EcuScript ParseEcuScript(JsonElement element)
{
    var canIds = ParseCanIds(element.GetProperty("canIds"));
    var name = element.GetProperty("name").GetString()!;

    if (element.TryGetProperty("states", out var statesEl))
    {
        // Phase 4: 有状态格式
        var stateMachine = ParseStateMachine(statesEl, GetBuiltInGenerators());
        return new EcuScript(name, canIds, stateMachine);
    }
    else
    {
        // Phase 3: 无状态格式（向后兼容）
        var rules = ParseRules(element.GetProperty("rules"));
        var stateMachine = EcuStateMachine.FromRules(rules);
        return new EcuScript(name, canIds, stateMachine);
    }
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

`EcuScript` record 统一为有状态格式：

```csharp
public sealed record EcuScript(
    string Name,
    CanIdConfig CanIds,
    EcuStateMachine StateMachine);
```

### 3.8 EcuMatrix + HeadlessHostBuilder 扩展

`EcuMatrix.AddEcu` 改用 `StatefulVirtualEcu`。`HeadlessHostBuilder` 注册内置生成器：

```csharp
private static void RegisterVirtualEcuMode(HostApplicationBuilder builder, CliArgs args)
{
    var script = EcuScriptLoader.Load(args.EcuScriptPath!);
    var channel = new VirtualChannel();

    var ecu = new StatefulVirtualEcu(channel, script.CanIds, script.StateMachine, logger: null);
    builder.Services.AddSingleton<ICanChannel>(channel);
    builder.Services.AddSingleton(ecu);
    // ... 其余不变 ...
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
            if (Interlocked.Decrement(ref _subscriberCount) == 0)
                _inner.FrameReceived -= OnInnerFrameReceived;
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

    private void OnInnerFrameReceived(CanFrame frame)
    {
        List<FaultRule>? snapshot;
        lock (_faultsLock) snapshot = _receiveFaults.Count > 0 ? _receiveFaults.ToList() : null;

        var frames = new List<CanFrame> { frame };
        if (snapshot is not null)
        {
            foreach (var fault in snapshot)
            {
                if (!fault.Matches(frame)) continue;
                var next = new List<CanFrame>();
                foreach (var f in frames)
                    next.AddRange(fault.Apply(f));
                frames = next;
            }
        }

        Action<CanFrame>? handler;
        lock (_subscribersLock) handler = _subscribers;
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

    public ValueTask<Result<Unit>> WriteAsync(CanFrame frame, CancellationToken ct = default)
        => _inner.WriteAsync(frame, ct);

    public Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
        => _inner.ConnectAsync(baud, fd, ct);

    public Task DisconnectAsync(CancellationToken ct = default)
        => _inner.DisconnectAsync(ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
```

设计约束：
- `ReceivePathFaultInjector` 独立于 `FaultInjector`（发送方向）。两者可以组合使用。
- Delay 故障在接收方向需要异步分发（`OnInnerFrameReceived` 改为 `async void`，或用 `Task.Run` 分发）。
- `FrameReceived` 事件不再直接转发到底层通道 -- 而是通过 `OnInnerFrameReceived` 中间层。

### 4.2 组合发送 + 接收方向故障注入

```csharp
var sendFault = new FaultInjector(channel);
var bothFault = new ReceivePathFaultInjector(sendFault);
// bothFault.WriteAsync -> FaultInjector.WriteAsync -> channel.WriteAsync (发送方向故障)
// channel.FrameReceived -> ReceivePathFaultInjector.OnInnerFrameReceived -> 订阅者 (接收方向故障)
```

`IFaultInjectionContext` 扩展：

```csharp
public interface IFaultInjectionContext
{
    IDisposable AddFault(FaultRule fault);          // 发送方向 (Phase 3)
    IDisposable AddReceiveFault(FaultRule fault);   // 接收方向 (Phase 4)
    void TagFault(string faultId, IDisposable handle);
    void ClearFaults(string? faultId = null);
}
```

### 4.3 InjectFaultStep 扩展

```csharp
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

public enum FaultDirection
{
    Send,       // HIL -> ECU (Phase 3)
    Receive,    // ECU -> HIL (Phase 4)
    Both        // 双向 (Phase 4)
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

        return JsonSerializer.Serialize(script, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private record OdxService(byte Sid, byte? SubFunction, byte[] PositiveResponseBytes);

    private static List<OdxService> ParseOdxServices(XDocument doc)
    {
        var services = new List<OdxService>();
        var diagComms = doc.Descendants().Where(e =>
            e.Name.LocalName == "DIAG-COMM" || e.Name.LocalName == "DIAG-COMM-SPEC");

        foreach (var comm in diagComms)
        {
            // Extract SID from REQUEST/RESPONSE byte patterns
            // (ODX parsing logic depends on ODX version)
        }

        return services;
    }
}
```

已知限制：
- ODX 格式复杂（ODX 2.0/2.2，PDX 打包），Sprint 8 只支持基本 UDS 服务提取
- SecurityAccess 等有状态服务需要手动添加到 states 部分
- ODX 导入生成无状态规则；有状态部分需要手动补充

### 4.5 CLI 扩展

```bash
# 从 ODX 生成 ECU 脚本
peakcan-hil --import-odx bms.odx --ecu-name BMS --uds-req 0x7E0 --uds-resp 0x7E8 --output bms_sim.json

# 然后正常使用
peakcan-hil --suite tests.json --ecu bms_sim.json
```

---

## 5. Architecture Decisions

| ID | Decision | Rationale |
|----|----------|-----------|
| P4-D1 | `EcuStateMachine` 独立于 `VirtualEcu` | 状态机逻辑可单测，不依赖 ISO-TP/channel |
| P4-D2 | 动态响应用 `IEcuResponseGenerator` 接口，非 JSON 嵌入代码 | JSON 不适合表达逻辑；接口注入可测试、可扩展 |
| P4-D3 | 无状态规则自动转换为 default 状态的静态转换 | 向后兼容 Phase 3 ECU 脚本，零迁移成本 |
| P4-D4 | `ReceivePathFaultInjector` 独立于 `FaultInjector` | 单一职责；两者可独立使用或组合 |
| P4-D5 | `InjectFaultStep` 新增 `Direction` 字段，默认 `Send` | 向后兼容 Phase 3 测试套件 JSON |
| P4-D6 | ODX 导入生成无状态规则 | ODX 不包含状态机语义；有状态部分需手动补充 |
| P4-D7 | 内置生成器在构造时注入 | 用户可直接使用 `generatorName` 引用 |
| P4-D8 | `EcuStateMachine.Reset()` 重置状态和上下文 | 支持 "每个 test case 独立状态" 的场景 |

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

---

## 7. Sprint 7 TDD Increment Plan

| Inc | Component | Tests | Description |
|-----|-----------|-------|-------------|
| 0 | EcuContextStore | 3 | Get/Set/HasKey、Clear 重置、泛型类型安全 |
| 1 | EcuStateMachine (static) | 5 | 匹配 SID+subFunc、匹配 DataMask、状态转换、default fallback、无匹配 NRC 0x11 |
| 2 | EcuStateMachine (dynamic) | 4 | DynamicResponse 调用生成器、未知生成器 NRC 0x72、生成器访问 IEcuContext、Reset |
| 3 | StatefulVirtualEcu | 5 | 单帧请求->状态转换->响应、SecurityAccess 完整流程、ClearDtc、向后兼容、Dispose |
| 4 | EcuScriptLoader (states) | 3 | 有状态 JSON 解析、无状态自动转换、canIds 交换 |
| 5 | 内置生成器 | 4 | SecurityAccessSeed 生成+复用、VerifyKey 正确/错误、ClearDtc |
| 6 | CLI + EcuMatrix 集成 | 3 | --ecu 有状态端到端、多 ECU、向后兼容 |

**Total: ~27 tests**

---

## 8. Sprint 8 TDD Increment Plan

| Inc | Component | Tests | Description |
|-----|-----------|-------|-------------|
| 0 | ReceivePathFaultInjector | 6 | 透传、Drop、Corrupt、Duplicate、多订阅者隔离、组合 FaultInjector |
| 1 | InjectFaultStep Direction | 3 | Receive、Both、Send 向后兼容 |
| 2 | OdxEcuScriptImporter | 4 | 服务提取、responseData 生成、canIds、空 ODX 错误 |
| 3 | CLI --import-odx | 2 | 端到端、导入后可直接 --ecu |

**Total: ~15 tests**

---

## 9. Definition of Done

- [ ] ~42 new tests passing
- [ ] `dotnet build` 无 error
- [ ] Phase 3 ECU 脚本（无状态 JSON）向后兼容，无需修改
- [ ] SecurityAccess 完整流程 E2E 通过（seed → key → unlock → write）
- [ ] 接收方向故障注入 E2E 通过（Drop/Corrupt/Duplicate）
- [ ] ODX 导入端到端通过（导入 → 生成 JSON → --ecu 运行）
- [ ] `EcuStateMachine` 独立可测（不依赖 VirtualEcu/ICanChannel）
