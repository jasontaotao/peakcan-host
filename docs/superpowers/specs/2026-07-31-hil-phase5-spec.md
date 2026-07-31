# HIL Phase 5 Spec (v5)

> **Created**: 2026-07-31
> **Author**: Claude
> **Status**: DRAFT — pending review
> **Round**: v5 — fixes 10 R4 findings (L1-R4, L2-R4, L3-R4, L4-R4, L5-R4, B1-R4, B2-R4, E1-R4, E2-R4, T1-R4)

---

## 1. Goals

Phase 5 补齐 HIL 框架的六个短板：

1. **ODX 导入不完整** — Phase 4 只生成扁平 rules，没利用 SecurityAccess/DID/Routine
2. **Generator 不可扩展** — 只有 3 个硬编码 built-in，无法加载外部 DLL，无法读写 ODX DID
3. **报告可读性差** — 只有 console 汇总 + JUnit XML，无 HTML / 趋势 / 故障帧导出
4. **WPF 面板是原型** — 纯文本输入 / 无文件浏览 / 无实时进度 / 无结果树
5. **无法测试真实总线时序** — 模拟器与测试共享 VirtualChannel，Phase 4 spec 明确 deferred
6. **失败分析靠人工** — 缺 LLM 辅助定位根因

---

## 2. Current State

### 2.1 Delivered (Phase 1-4)

| Phase | Sprint | Key Deliverables |
|-------|--------|-----------------|
| 1 | 1 | TestCase/TestSuite/StepParameters, TestSuiteEngine skeleton, Diff engine, parameterized templates |
| 1 | 2 | TraceDrivenChannel, CLI Runner, headless DI host |
| 1 | 3 | JUnit XML, WaitForFrame/AssertDtc/AssertNrc/AssertResponseTime executors, BLF, PeakCanAssertionContext, HilView, FramesAroundFailure |
| 3 | 4 | VirtualChannel, VirtualEcu (stateless), EcuScriptLoader |
| 3 | 5 | FaultInjector (TX), InjectFaultStep/ClearFaultStep |
| 3 | 6 | EcuMatrix, MatrixConfigLoader |
| 4 | 7 | EcuStateMachine, EcuContextStore, IEcuResponseGenerator, StatefulVirtualEcu, built-in generators |
| 4 | 8 | ReceivePathFaultInjector, InjectFaultStep.Direction, OdxEcuScriptImporter (flat only) |

### 2.2 Architectural Constraints

- `VirtualChannel` `SingleReader = true`
- `HeadlessHostBuilder` 四模式互斥：hardware / virtual-ECU / matrix / trace-replay
- `HilRunRequest` → `HilRunRequestExtensions.ToCliArgs` → `CliArgs`

### 2.3 Existing ODX Infrastructure

| 已有类型 | 位置 | 提供能力 |
|---------|------|---------|
| `SecurityAccessExtractor` | `Core/Uds/Odx/SecurityAccessExtractor.cs` | `Extract(XDocument, XNamespace) → SecurityAccessConfig?` (Level + SeedLength) |
| `DidDop` | `Core/Uds/Odx/DidDop.cs` | `TryMap(XElement, out ...) → DidDefinition?` |
| `RequestBasedMappers` | `Core/Uds/Odx/RequestBasedMappers.cs` | `ExtractDids` (0x22/0x2E), `ExtractRoutines` (0x31) |
| `RoutineDefinition` | `Core/Uds/Database/RoutineDefinition.cs` | `ushort Id, string Name, string Description, bool Startable, bool Stoppable` (无 Queryable 字段) |
| `OdxParser` | `Core/Uds/Odx/OdxParser.cs` | `Parse(XDocument, out warnings) → OdxDocument` |

### 2.4 Existing LLM Infrastructure

| 已有类型 | 位置 |
|---------|------|
| `IChatProvider` | `Core/Analysis/Chat/IChatProvider.cs` |
| `ICredentialStore` | `Core/Analysis/ICredentialStore.cs` |
| `DeepSeekChatProvider` | `App/Services/ChatProvider/DeepSeekChatProvider.cs` (IChatProvider 实现) |
| `DeepSeekOptions` | `App/Services/ChatProvider/DeepSeekOptions.cs` (`Model = "deepseek-v4-flash"`) |

### 2.5 Output Writer Locations

| 已有类型 | 实际位置 |
|---------|---------|
| `CliArgs` / `CliArgsParser` | `src/PeakCan.Host.Infrastructure/Cli/CliArgs.cs` |
| `JUnitWriter` / `ConsoleProgress` / `ResultWriter` / `Program` | `src/PeakCan.Host.Cli/` |

### 2.6 DI Registration

`HilViewModel` 由 `AppHostBuilder.cs` DI 容器注册：`AddTransient<HilViewModel>()`。

### 2.7 EcuStateMachine.DataMask/DataPattern Semantics

`EcuStateMachine.MatchesRequest`（`EcuStateMachine.cs:85-93`）：

```csharp
if (t.DataMask is not null && t.DataMask.Length > 0)
{
    if (request.Length < 2 + t.DataMask.Length) return false;
    for (int i = 0; i < t.DataMask.Length; i++)
    {
        if ((request[2 + i] & t.DataMask[i]) != t.DataPattern![i])  // NRE if DataPattern is null
            return false;
    }
}
```

- `DataMask = null` → 跳过 data 匹配
- `DataMask` 非 null → `DataPattern` 必须非 null 且等长
- **不存在 "DataMask 非 null + DataPattern = null = 匹配任何值" 的语义**

---

## 3. Design Decisions

### 3.1 ODX 导入策略

**复用已有 Extractor + Adapter 适配**。Sprint 9 新增 `OdxToEcuScriptAdapter`：
1. `XDocument.Load(path)` + `root.Name.Namespace` 解析
2. 调用 `SecurityAccessExtractor.Extract(xdoc, ns)` + `RequestBasedMappers.Extract*(xdoc, ns)`
3. 输出 `List<EcuStateTransition>`

**SecurityAccess transition 生成规则**：

```
if (secConfig is { } cfg && cfg.SeedLength is { } seedLen && seedLen > 0):
    // Seed (0x27 0x01)
    transition: serviceId=0x27, subFunction=0x01,
               response=dynamic "SecurityAccessSeed", toState="seedSent"
               // DataMask=null, DataPattern=null — 仅靠 subFunction 匹配

    // Key verify (0x27 0x02)
    transition: serviceId=0x27, subFunction=0x02,
               response=dynamic "SecurityAccessVerifyKey", toState="unlocked"
               // DataMask=null — Generator 内部验证 XOR 0xAA
```

**SeedLength 为 null 时**：跳过 SecurityAccess 状态生成，记录 warning。

**Routine transition 生成规则（fix L1-R4, L2-R4）**：

`RoutineDefinition` 只有 `Startable` (bool) + `Stoppable` (bool)，**无 Queryable 字段**。`RequestBasedMappers.ExtractRoutines` 内部计算的 `queryable` 仅用于构造 `Description` 字符串，未持久化。

Adapter 生成策略（不依赖 Queryable）：

```
对每个 RoutineDefinition (id: ushort, name: string):
    // Start (subFunc=0x01) — 始终生成（即使 Startable=false 也生成；ECU 未实现时由 test case 避免触发）
    transition: serviceId=0x31, subFunction=0x01,
               dataMask=[0xFF, 0xFF], dataPattern=[(id >> 8) & 0xFF, id & 0xFF],
               response=static [0x71, 0x01]

    // Stop (subFunc=0x02) — 仅当 Stoppable=true
    if (routine.Stoppable):
        transition: serviceId=0x31, subFunction=0x02,
                   dataMask=[0xFF, 0xFF], dataPattern=[(id >> 8) & 0xFF, id & 0xFF],
                   response=static [0x71, 0x02]

    // RequestResults (subFunc=0x03) — 始终生成（Result 查询是 UDS 标准子功能）
    transition: serviceId=0x31, subFunction=0x03,
               dataMask=[0xFF, 0xFF], dataPattern=[(id >> 8) & 0xFF, id & 0xFF],
               response=static [0x71, 0x03]
```

**注**：不尝试从 ODX POS-RESPONSE 提取 routine response 字节。`RequestBasedMappers.ExtractDidFields` 硬编码只处理 0x22/0x2E（`RequestBasedMappers.cs:226-227`），不支持 0x31。为保持简洁且避免引入新的 ODX walk 逻辑，所有 routine response 使用 `[0x71, subFunc]`（RoutineControl positive response 的通用格式）。Phase 6 可引入真正的 POS-RESPONSE 解析。

**DID Read transition 生成规则（fix L3-R4）**：

```
对每个 DID (id: ushort)：
    // 0x22 ReadDataByIdentifier — 仅靠 DID id 匹配
    transition: serviceId=0x22,
               dataMask=[0xFF, 0xFF], dataPattern=[(id >> 8) & 0xFF, id & 0xFF],
               response=dynamic "DidReadout"  // DidReadoutGenerator 从 IEcuContext 读取值
```

DidReadoutGenerator 内部逻辑：
- 从 `IEcuContext.Get<Dictionary<ushort, byte[]>>("DidValues")` 获取值
- 如果找到该 DID：返回 `[0x62, DID_Hi, DID_Lo, ...value...]`
- 如果未找到：返回 `new byte[] { 0x7F, 0x22, 0x31 }`（NRC requestOutOfRange，与现有 Generator 返回 NRC byte[] 的模式一致，不抛异常）

**状态合并策略**：
- SecurityAccess → `locked` / `seedSent` / `unlocked` 三态
- DID Read + Routine Control → `default` 状态通配规则（FromState=null）
- 全部挂到 `default`（不做 STATE-CHART，Phase 6 规划）

### 3.2 Generator 插件机制

**调用链穿透**：

```
EcuScriptLoader.Load(path, externalGenerators?)
  → Parse(json, externalGenerators)
    → ParseEcuScript(element, externalGenerators)
      → ParseStateMachine(statesEl, MergeGenerators(externalGenerators))
```

**同名覆盖**：external-first override（external 覆盖 built-in）：

```csharp
var merged = builtIn.ToDictionary(g => g.Name);
foreach (var ext in external ?? Enumerable.Empty<IEcuResponseGenerator>())
    merged[ext.Name] = ext;
return merged.Values;
```

**didValues 注入责任**：

`EcuScriptLoader.ParseEcuScript` 构造 `EcuStateMachine` 后立即注入：

```csharp
if (didValues is { Count: > 0 })
    stateMachine.Context.Set("DidValues", didValues);
```

`EcuMatrix.AddEcu` 补充：如果 `script.DidValues` 非空且 Context 中无 `"DidValues"` key，注入一次。

### 3.3 报告格式

| 格式 | 触发 | 帧上限 |
|------|------|--------|
| console summary | 默认 | — |
| JUnit XML | `--format junit` | — |
| HTML | `--format html` | 50 |
| HTML + JUnit | `--format html+junit` | 50 |
| .asc | `--export-frames <dir>` | 50 |

**故障帧**：`StepResult.FramesAroundFailure`。

**趋势 JSON 并发**：

使用 named `Mutex`（`Global\hil-trends-mutex`）+ 多层异常处理：

```csharp
try
{
    if (mutex.WaitOne(TimeSpan.FromSeconds(5)))
    {
        try
        {
            var entries = ReadSafely(path);
            entries.Add(entry);
            File.WriteAllText(path, JsonSerializer.Serialize(entries));
        }
        finally { mutex.ReleaseMutex(); }
    }
}
catch (AbandonedMutexException)
{
    var entries = ReadSafely(path);
    entries.Add(entry);
    File.WriteAllText(path, JsonSerializer.Serialize(entries));
    mutex.ReleaseMutex();
}

static List<TrendEntry> ReadSafely(string path)
{
    try { return JsonSerializer.Deserialize<List<TrendEntry>>(File.ReadAllText(path)) ?? new(); }
    catch (Exception ex) when (ex is JsonException or FileNotFoundException or IOException)
    {
        // 文件不存在或损坏：尝试备份（若文件存在），然后从空列表重新开始
        if (File.Exists(path))
            try { File.Move(path, path + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: true); } catch { }
        return new();
    }

```
**趋势 JSON 文件路径**：固定 `./hil-trends.json`（工作目录）。HTML 报告读取同目录文件渲染 sparkline。

**EcuSimulatorHost（fix L5-R4）**：

```csharp
public sealed class EcuSimulatorHost
{
    private readonly ICanChannel _channel;
    private readonly StatefulVirtualEcu _ecu;

    public EcuSimulatorHost(ICanChannel channel, CanIdConfig canIds, EcuStateMachine stateMachine, ILogger? logger = null)
    {
        _channel = channel;
        _ecu = new StatefulVirtualEcu(channel, canIds, stateMachine, logger);
    }

    /// <summary>
    /// 启动模拟器并阻塞运行，直到 CancellationToken 被取消（Ctrl+C）。
    /// 内部调用 channel.ConnectAsync，订阅取消信号，等待信号后 DisconnectAsync。
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        await _channel.ConnectAsync(BaudRate.CanFd1Mbps, fd: true, ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // 阻塞直到外部取消
            await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        finally
        {
            await _channel.DisconnectAsync(CancellationToken.None);
        }
    }
}
```

**Program.cs simulate 分支**：

```csharp
if (cli.Simulate)
{
    var ecuScript = EcuScriptLoader.Load(cli.EcuScriptPath!);
    var channel = new PeakCanChannel(new ChannelHandle(HeadlessHostBuilder.ParseChannelHandle(cli.HardwareChannel!)), logger);
    var host = new EcuSimulatorHost(channel, ecuScript.CanIds, ecuScript.StateMachine, logger);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    Console.WriteLine($"Simulating ECU '{ecuScript.Name}' on {cli.HardwareChannel}. Press Ctrl+C to exit.");
    Console.WriteLine($"Listening on CAN ID 0x{ecuScript.CanIds.ResponseId:X3} (ECU receives on 0x{ecuScript.CanIds.RequestId:X3}).");

    await host.RunAsync(cts.Token);
    return 0;
}
```

**FakeCanChannel（fix E2-R3）**：Sprint 13 测试不直接复用 HILAssertionContextTests.cs 中的 `FakeCanChannel`。改为在 `Infrastructure.Tests/HIL/FakeCanChannel.cs` 创建独立的 `FakeCanChannel.cs` 文件。

### 3.5 LLM 分析

**实现位置**：

`HilAnalysisService` 在 **Infrastructure** 层（`Infrastructure/HIL/Analysis/HilAnalysisService.cs`）。

**ICredentialStore 实现（fix B1-R4, B2-R4）**：

新增 `SimpleCredentialStore`（Infrastructure/HIL/Analysis/SimpleCredentialStore.cs）：

```csharp
public sealed class SimpleCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _store = new();

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        // 1. 检查内存 store（SetAsync 写入）
        if (_store.TryGetValue(key, out var val))
            return Task.FromResult<string?>(val);

        // 2. 检查环境变量
        var env = Environment.GetEnvironmentVariable("HIL_DEEPSEEK_API_KEY");
        if (env is not null) return Task.FromResult<string?>(env);

        // 3. 检查 ~/.hil/credentials 文件
        var credPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".hil", "credentials");
        if (File.Exists(credPath))
        {
            try
            {
                var json = File.ReadAllText(credPath);
                var creds = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (creds is { } c && c.TryGetValue(key, out var fileVal))
                    return Task.FromResult<string?>(fileVal);
            }
            catch { /* 文件损坏或无权限 — 降级 */ }
        }

        return Task.FromResult<string?>(null);
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _store.Remove(key);
        return Task.CompletedTask;
    }
}
```

**关键（fix B1-R4）**：GetAsync 先检查 `_store`（内存），再检查环境变量，最后检查文件。SetAsync 写入 `_store`。GetAsync 能取回 SetAsync 写入的值。

**关键（fix B2-R4）**：DeleteAsync 返回 `Task.CompletedTask`（不是 `Task.FromResult(Task.CompletedTask)`）。

**HTTP 请求格式（fix L4-R4, E1-R4, T1-R4）**：

HilAnalysisService 内部直接构造 HTTP 请求（不依赖 ChatCompletionRequest 类）：

```csharp
var requestBody = new
{
    model = "deepseek-v4-flash",  // 与 DeepSeekOptions.Model 一致
    messages = new[]
    {
        new { role = "system", content = "You are an automotive ECU diagnostic test failure analyst. Analyze the test failure and suggest root causes." },
        new { role = "user", content = prompt }
    },
    stream = false,
    temperature = 0.3
};

var json = JsonSerializer.Serialize(requestBody);
var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/chat/completions")
{
    Content = new StringContent(json, Encoding.UTF8, "application/json")
};
httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
```

**关键（fix E1-R4, T1-R4）**：模型名 `deepseek-v4-flash` 与 `DeepSeekOptions.cs:9` 一致。不凭空捏造 `deep-chat`。

**响应解析（non-streaming, fix L4-R4）**：

```csharp
httpClient.Timeout = TimeSpan.FromSeconds(150);
httpClient.DefaultRequestHeaders.Add("User-Agent", "peakcan-host/hil-analyze");

using var response = await httpClient.SendAsync(httpRequest, ct);
var responseJson = await response.Content.ReadAsStringAsync(ct);
response.EnsureSuccessStatusCode();

// 解析 non-streaming JSON response
using var doc = JsonDocument.Parse(responseJson);
var root = doc.RootElement;
var content = root.GetProperty("choices")[0]
                  .GetProperty("message")
                  .GetProperty("content")
                  .GetString();
return content ?? "";
```

**HilPromptBuilder（fix L4-R4）**：

```csharp
public sealed class HilPromptBuilder
{
    /// <summary>
    /// 构造发送给 LLM 的分析 prompt 文本。返回 string（纯文本），
    /// HilAnalysisService 将其包装为 ChatMessage。
    /// </summary>
    public string Build(TestSuiteResult result, EcuScript? ecuScript)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Failed Test Cases");
        foreach (var c in result.CaseResults.Where(c => !c.Passed))
        {
            sb.AppendLine($"- Case: {c.TestCaseName}");
            sb.AppendLine($"  Reason: {c.FailureReason}");
            foreach (var s in c.StepResults.Where(s => s.Status == StepStatus.Failed))
            {
                sb.AppendLine($"  Step {s.StepIndex} ({s.Kind}): {s.Message}");
                if (s.ActualValue is not null)
                    sb.AppendLine($"    Actual: {s.ActualValue}, Expected: {s.ExpectedValue}");
            }
        }
        if (ecuScript is not null)
        {
            sb.AppendLine("## ECU States");
            // 从 EcuScript 提取状态名列表
            var stateNames = string.Join(", ", GetStateNames(ecuScript));
            sb.AppendLine($"States: {stateNames}");
        }
        return sb.ToString();
    }
}
```

HilPromptBuilder 返回 `string`。HilAnalysisService 将其包装为 `{ role: "user", content: prompt }` 发送给 API。

**CLI vs WPF credential store 不一致（fix E2-R4）**：

| 模式 | ICredentialStore 实现 | 行为 |
|------|---------------------|------|
| CLI (`--simulate` / `--analyze`) | SimpleCredentialStore | 读环境变量 + ~/.hil/credentials |
| WPF (AppHostBuilder) | WindowsCredentialManagerStore | 读 Windows Credential Manager |

文档说明：用户在 WPF 中配置的 API key 不会自动对 CLI 模式可见。CLI 模式需要独立配置（环境变量或 ~/.hil/credentials）。Phase 6 可统一。

### 3.6 HilRunRequest 模式选择器

新增 `HilMode` 枚举 + `HilRunRequest.Mode` + `EnableAnalyze`。

### 3.7 M2/M3 修复

**M2**：`_faultHandles` → `ConcurrentDictionary`，snapshot + TryRemove。

**M3**：新增 `_delayCts`，Dispose 时 Cancel。延迟任务 catch `OperationCanceledException` 静默退出。

### 3.8 IFileDialogService 注入

`HilViewModel` 构造函数新增 `IFileDialogService fileDialog` 参数。DI 自动解析。

### 3.9 ECU 脚本 JSON 编辑器

plain TextBox。Save & Run → temp file → `EcuScriptPath = tempPath`。

---

## 4. Sprint Breakdown

### Sprint 9: ODX→Stateful EcuScript 完整生成

**新增**：`OdxToEcuScriptAdapter`

**修改**：`OdxEcuScriptImporter.ImportToJson`

**预估测试**：10

### Sprint 10: Generator 可扩展性 + DID 读写

**新增**：`GeneratorPluginLoader` / `DidReadoutGenerator` / `DidWriteGenerator`

**修改**：`EcuScriptLoader` 四层签名 + `EcuScript.DidValues` + `EcuMatrix.AddEcu` + `HeadlessHostBuilder`

**预估测试**：11

### Sprint 11: 报告增强 + M2/M3

**新增**（`Infrastructure/Cli/Reporting/`）：`HtmlReportGenerator` / `TrendTracker` / `TrendEntry` / `ConsoleSummaryFormatter` / `FrameCaptureExporter`

**修改**：`Infrastructure/Cli/CliArgs.cs` / `PeakCan.Host.Cli/Program.cs` / `HILAssertionContext` / `ReceivePathFaultInjector`

**预估测试**：15

### Sprint 12: WPF HIL 面板升级

**新增**：`HilResultNode` / `HilMode` 枚举

**修改**：`HilRunRequest` / `HilRunRequestExtensions` / `HilViewModel` / `HilView.xaml`

**预估测试**：10

### Sprint 13: 独立模拟器进程

**新增**：`Infrastructure/HIL/EcuSimulatorHost.cs`（含 `RunAsync(CancellationToken)`）/ `Infrastructure.Tests/HIL/FakeCanChannel.cs`

**修改**：`Infrastructure/Cli/CliArgs.cs` / `PeakCan.Host.Cli/Program.cs` / `docs/hil-simulator-usage.md`

**预估测试**：8

### Sprint 14: LLM 辅助分析

**新增**：
- `Core/HIL/Analysis/IHilAnalysisService.cs`
- `Infrastructure/HIL/Analysis/HilAnalysisService.cs`（`new HttpClient()` + hardcoded model `deepseek-v4-flash`）
- `Infrastructure/HIL/Analysis/SimpleCredentialStore.cs`（内存 + 环境变量 + ~/.hil/credentials，GetAsync 先从内存读）
- `Infrastructure/HIL/Analysis/HilPromptBuilder.cs`（返回 string）

**修改**：`App/AppHostBuilder.cs` / `Infrastructure/HIL/HeadlessHostBuilder.cs` / `HilViewModel` / `HilView.xaml` / `PeakCan.Host.Cli/Program.cs`

**预估测试**：8

---

## 5. Dependencies

```
Sprint 9 → Sprint 10
Sprint 11
Sprint 12 → Sprint 14
Sprint 13 独立
```

---

## 6. Risks

| Risk | Severity | Mitigation |
|------|----------|------------|
| HTML > 1MB | LOW | 帧限 50 |
| 趋势 JSON 并发 | MEDIUM | Mutex + Abandoned + corruption backup |
| WPF 重构 | MEDIUM | RunAsync 核心不变 |
| 双 PCAN | LOW | CI mock |
| LLM 网络依赖 | MEDIUM | 降级"不可用" |
| CLI vs WPF key 不一致 | MEDIUM | 文档说明 |
| new HttpClient 无 retry | LOW | Phase 6 |

---

## 7. Out of Scope

- ODX STATE-CHART（Phase 6）
- ODX 编辑/回写
- Multi-bus gateway
- Generator 热加载
- Web 报告 UI
- ECU 脚本语法高亮
- DeepSeekOptions 依赖
- Polly retry（Phase 6）
- Routine POS-RESPONSE 解析（Phase 6）

---

## 8. File Inventory

### Sprint 9
| File | Action |
|------|--------|
| `Infrastructure/HIL/Odx/OdxToEcuScriptAdapter.cs` | NEW |
| `Infrastructure/HIL/Odx/OdxEcuScriptImporter.cs` | MODIFY |
| `Infrastructure.Tests/HIL/Odx/OdxToEcuScriptAdapterTests.cs` | NEW |

### Sprint 10
| File | Action |
|------|--------|
| `Infrastructure/HIL/Generators/GeneratorPluginLoader.cs` | NEW |
| `Infrastructure/HIL/Generators/DidReadoutGenerator.cs` | NEW |
| `Infrastructure/HIL/Generators/DidWriteGenerator.cs` | NEW |
| `Infrastructure/HIL/EcuScriptLoader.cs` | MODIFY |
| `Infrastructure/HIL/EcuMatrix.cs` | MODIFY |
| `Infrastructure/HIL/HeadlessHostBuilder.cs` | MODIFY |
| `Infrastructure.Tests/HIL/Generators/GeneratorPluginLoaderTests.cs` | NEW |
| `Infrastructure.Tests/HIL/Generators/DidReadoutGeneratorTests.cs` | NEW |
| `Infrastructure.Tests/HIL/Generators/DidWriteGeneratorTests.cs` | NEW |
| `Infrastructure.Tests/HIL/EcuScriptLoaderPluginTests.cs` | NEW |

### Sprint 11
| File | Action |
|------|--------|
| `Infrastructure/Cli/Reporting/HtmlReportGenerator.cs` | NEW |
| `Infrastructure/Cli/Reporting/TrendTracker.cs` | NEW |
| `Infrastructure/Cli/Reporting/TrendEntry.cs` | NEW |
| `Infrastructure/Cli/Reporting/ConsoleSummaryFormatter.cs` | NEW |
| `Infrastructure/Cli/Reporting/FrameCaptureExporter.cs` | NEW |
| `Infrastructure/Cli/CliArgs.cs` | MODIFY |
| `PeakCan.Host.Cli/Program.cs` | MODIFY |
| `Infrastructure/HIL/HILAssertionContext.cs` | MODIFY |
| `Infrastructure/Channel/ReceivePathFaultInjector.cs` | MODIFY |
| `Infrastructure.Tests/Cli/Reporting/HtmlReportGeneratorTests.cs` | NEW |
| `Infrastructure.Tests/Cli/Reporting/TrendTrackerTests.cs` | NEW |
| `Infrastructure.Tests/Cli/Reporting/ConsoleSummaryFormatterTests.cs` | NEW |
| `Infrastructure.Tests/Cli/Reporting/FrameCaptureExporterTests.cs` | NEW |
| `Infrastructure.Tests/HIL/HILAssertionContextConcurrencyTests.cs` | NEW |
| `Infrastructure.Tests/Channel/ReceivePathFaultInjectorExceptionTests.cs` | NEW |

### Sprint 12
| File | Action |
|------|--------|
| `App/ViewModels/HilResultNode.cs` | NEW |
| `App/ViewModels/HilViewModel.cs` | REFACTOR |
| `App/Views/HilView.xaml` | REFACTOR |
| `Core/HIL/HilRunRequest.cs` | MODIFY |
| `Infrastructure/HIL/HilRunRequestExtensions.cs` | MODIFY |
| `App.Tests/ViewModels/HilViewModelTests.cs` | NEW |

### Sprint 13
| File | Action |
|------|--------|
| `Infrastructure/HIL/EcuSimulatorHost.cs` | NEW (含 RunAsync) |
| `Infrastructure.Tests/HIL/FakeCanChannel.cs` | NEW (独立 fake) |
| `Infrastructure/Cli/CliArgs.cs` | MODIFY |
| `PeakCan.Host.Cli/Program.cs` | MODIFY |
| `docs/hil-simulator-usage.md` | NEW |
| `Infrastructure.Tests/HIL/EcuSimulatorHostTests.cs` | NEW |

### Sprint 14
| File | Action |
|------|--------|
| `Core/HIL/Analysis/IHilAnalysisService.cs` | NEW |
| `Infrastructure/HIL/Analysis/HilAnalysisService.cs` | NEW (HttpClient + hardcoded model) |
| `Infrastructure/HIL/Analysis/SimpleCredentialStore.cs` | NEW |
| `Infrastructure/HIL/Analysis/HilPromptBuilder.cs` | NEW (返回 string) |
| `App/AppHostBuilder.cs` | MODIFY (注册 IHilAnalysisService) |
| `Infrastructure/HIL/HeadlessHostBuilder.cs` | MODIFY |
| `App/ViewModels/HilViewModel.cs` | MODIFY |
| `App/Views/HilView.xaml` | MODIFY |
| `PeakCan.Host.Cli/Program.cs` | MODIFY |
| `Infrastructure.Tests/HIL/Analysis/HilPromptBuilderTests.cs` | NEW |
| `Infrastructure.Tests/HIL/Analysis/HilAnalysisServiceTests.cs` | NEW |

---

## 9. Test Summary

| Sprint | Count |
|--------|-------|
| 9 | 10 |
| 10 | 11 |
| 11 | 15 |
| 12 | 10 |
| 13 | 8 |
| 14 | 8 |
| **Total** | **62** |

---

## 10. Definition of Done

- [ ] ODX → states（DataMask=null 对 SecurityAccess；Routine 用 [0x71, subFunc] 默认 response）
- [ ] 外部 Generator DLL + DID 读写
- [ ] HTML + .asc（帧限 50）
- [ ] WPF 面板（HilMode / 文件浏览 / 进度 / 结果树）
- [ ] --simulate 独立进程（RunAsync(CancellationToken) + FakeCanChannel）
- [ ] LLM 分析（Infrastructure 实现 + SimpleCredentialStore 内存 + model=deepseek-v4-flash + AppHostBuilder 注册）
- [ ] M2 + M3
- [ ] ~62 新测试通过

---

## 11. Review Traceability

### v1 → v2 (22 findings, 已修复)
L1-L7, B1-B5, E1-E5, T1-T5

### v2 → v3 (11 findings, 已修复)
L1-R2, L2-R2, L3-R2, L4-R2, L5-R2, L6-R2, B1-R2, B2-R2, E1-R2, E3-R2, T1-R2

### v3 → v4 (10 findings, 已修复)
L1-R3, L2-R3, L3-R3, L4-R3, L5-R3, B1-R3, B2-R3, E1-R3, E2-R3, T1-R3

### v4 → v5 (10 findings, 本版本修复)
| Finding | Severity | Fix Location |
|---------|----------|-------------|
| L1-R4 | CRITICAL | RoutineDefinition 无 Queryable 字段 | §3.1 — 始终生成 Start/Stop/Results 三个 transition，不依赖 Queryable |
| L2-R4 | CRITICAL | ExtractDidFields 不支持 0x31 | §3.1 — Routine 默认 response=[0x71, subFunc]，不提取 POS-RESPONSE |
| L3-R4 | HIGH | DID Read response 未定义 | §3.1 — dynamic "DidReadout" Generator，未找到值时抛 NRC 0x31 |
| L4-R4 | HIGH | HTTP 请求/响应格式未定义 | §3.5 — 完整 request JSON schema + 响应解析代码 |
| L5-R4 | MEDIUM | EcuSimulatorHost 运行 API 未定义 | §3.4 — RunAsync(CancellationToken) + Program.cs simulate 分支伪代码 |
| B1-R4 | HIGH | SimpleCredentialStore Set/Get 不同源 | §3.5 — GetAsync 先检查 _store 再检查 env/file |
| B2-R4 | MEDIUM | Task.FromResult(Task.CompletedTask) | §3.5 — 改为 return Task.CompletedTask |
| E1-R4 | HIGH | 模型名 "deep-chat" 不存在 | §3.5 — 改为 "deepseek-v4-flash" (DeepSeekOptions.cs:9) |
| E2-R4 | MEDIUM | CLI vs WPF key 不一致 | §3.5 — 文档表格说明差异 |
| T1-R4 | MEDIUM | "deep-chat" 无来源 | §3.5 — 同 E1-R4 |
