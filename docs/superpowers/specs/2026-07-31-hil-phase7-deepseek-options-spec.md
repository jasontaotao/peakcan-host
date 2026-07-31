# HIL Phase 7 (Unit A): DeepSeekOptions Wiring + EnableAnalyze Auto-Analyze

> Spec date: 2026-07-31
> Depends: Phase 6 (complete — commit `579b02e`)
> Scope: **仅 WPF 路径**。单元 A 是 Phase 7 四个独立单元的第一个，执行顺序 A → B → C → D
> （B=Generator 热加载，C=Web 报告 UI，D=Multi-bus gateway，各自独立 spec → plan）。
>
> **Revision 2（2026-07-31）**：按 code-review 13 项（L1-L4 / B1-B4 / E1-E2 / T1-T5）
> 全部修正。变更点：L1 配置全局生效语义、L2/E2 timeout 从 options 读、L3 UseStreaming
> 语义、L4 插入点后移、B1 File Inventory 补 Sprint14Tests、B2 空套件边界、B3/T3 Headless
> 绑定节、B4 TrimEnd、E1 请求体差异声明、T1 具体 API、T2 JSON 示例、T4 门控安全依据、
> T5 接口不改声明。

---

## 1. Goals

Phase 6 交付了 HIL LLM 分析闭环（手动触发路径可用），但遗留两个缺口：

**A1. `HilAnalysisService` 硬编码 LLM 配置** — `HilAnalysisService.cs:16-17` 写死
`ApiEndpoint` 和 `Model`，换模型/供应商需改代码重编译。而 Core 已有
`DeepSeekOptions` 配置类，App 的 `DeepSeekProvider` / `DeepSeekChatProvider` 均已走
`IOptions<DeepSeekOptions>` 注入模式，HIL 分析服务是唯一还硬编码的。

**A2. `EnableAnalyze` 勾选框是空壳** — `HilView.xaml:45` CheckBox → `HilViewModel.cs:186`
传入 `HilRunRequest.EnableAnalyze`，但 `HilRunRequest.cs:17` 无任何消费者，勾选后
Run 完不会自动分析。Phase 6 spec 修了一半（CheckBox 已绑定），剩下接线。

---

## 2. Current State

### 2.1 证据

| 项 | 证据 |
|----|------|
| 硬编码 endpoint/model | `HilAnalysisService.cs:16-17`：`ApiEndpoint = "https://api.deepseek.com/chat/completions"`、`Model = "deepseek-v4-flash"` |
| 硬编码 stream=false | `HilAnalysisService.cs:57`：`stream = false`（匿名类型请求体，无 `ResponseFormat`） |
| `DeepSeekOptions` 已存在 | `Core/Analysis/DeepSeekOptions.cs:6-19`：`ApiBase`（默认 `https://api.deepseek.com`）、`Model`、`TimeoutSeconds`、`UseStreaming` |
| 参照模式（IOptions 注入） | `DeepSeekProvider.cs:37-47`：ctor 注入 `IOptions<DeepSeekOptions>` |
| endpoint 拼接模式 | `DeepSeekProvider.cs:139` / `DeepSeekChatProvider.cs:111`：`$"{ApiBase}/chat/completions"` |
| DeepSeekProvider 请求体 | `DeepSeekProvider.cs:84-91`：`DeepSeekRequest` DTO + `ResponseFormat = json_object`，无 temperature |
| **`IOptions<T>` 全局单例** | `DeepSeekProvider.cs:41`、`DeepSeekChatProvider.cs:58`、`AppServicesFlow.cs:206` 三处注入同一 `IOptions<DeepSeekOptions>` |
| DeepSeek named client 读超时 | `AppServicesFlow.cs:206-207`：`client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds * 5)` |
| HIL client 硬编码超时 | `AppServicesFlow.cs:224`、`HeadlessHostBuilder.cs:153`：`client.Timeout = TimeSpan.FromSeconds(150)` |
| WPF 空配置 | `AppServicesFlow.cs:236`：`Configure<DeepSeekOptions>(options => { })` |
| appsettings.json 无配置节 | grep `Llm\|DeepSeek` 在 appsettings.json 无匹配 |
| WPF host 构建 | `AppHostBuilder.cs:98` `Build()`；`:108` 已暴露 `builder.Configuration` 为 singleton |
| Headless host 构建 | `HeadlessHostBuilder.cs:30`：`Host.CreateApplicationBuilder()`（自动加载 appsettings.json） |
| `EnableAnalyze` 无消费者 | `HilRunRequest.cs:17` 定义；`HilViewModel.cs:186` 传入 `request`；全库 grep 无读取点 |
| `AllPassed` 空套件语义 | `TestSuiteResult.cs:18`：`AllPassed => TotalCases > 0 && FailedCases == 0 && SkippedCases == 0` |
| RunAsync 结构 | `HilViewModel.cs:168-212`：`IsRunning=true` → try（`:188` Run → `:190` `_lastResult` → `:193-196` 填充结果 → `:198-200` StatusMessage）→ finally（`:207-211`） |
| `IHilAnalysisService` 接口 | `Core/HIL/Analysis/IHilAnalysisService.cs:13`：仅 `AnalyzeAsync(TestSuiteResult, CancellationToken)`，无 ctor |

### 2.2 现有可复用实现

`HilViewModel.AnalyzeAsync`（`HilViewModel.cs:118-158`）已有完整实现：门控
（`:160-161`，`!IsRunning && !IsAnalyzing && _lastResult is { AllPassed: false }`）、超时
捕获 → "Analysis timed out."（`:140-147`，code-review M2）、异常降级、`IsAnalyzing`
状态。**方法体不依赖 `IsRunning`，仅依赖 `_lastResult`**——自动分析直接复用该方法
（方法调用而非 `AnalyzeCommand`），绕过 `CanAnalyze` 是安全的。

### 2.3 DI 注册现状

- WPF：`AppServicesFlow.cs:221-229` `AddHttpClient<IHilAnalysisService, HilAnalysisService>` + Polly retry
- Headless：`HeadlessHostBuilder.cs:150-155` `AddHttpClient<IHilAnalysisService, HilAnalysisService>` + `GetRetryPolicy()`

`AddHttpClient<TInterface, TImpl>` 通过 DI 自动解析构造函数依赖，新增
`IOptions<DeepSeekOptions>` 参数无需改注册处（`IOptions<T>` 由 MS.DI 默认注册）。

---

## 3. Design

### 3.1 A1: `HilAnalysisService` 去硬编码 → 读配置

**变更 `HilAnalysisService`**（`Infrastructure/HIL/Analysis/HilAnalysisService.cs`）：

- ctor 由 `(HttpClient httpClient, ICredentialStore credentialStore)` 改为
  `(HttpClient httpClient, ICredentialStore credentialStore, IOptions<DeepSeekOptions> options)`
  —— 与 `DeepSeekProvider`（`DeepSeekProvider.cs:37-47`）模式一致。**R3 存储方式**：参照
  `DeepSeekProvider`（`:35,46`），ctor 中解包存 `DeepSeekOptions` 字段
  （`_options = options?.Value ?? throw new ArgumentNullException(nameof(options));`），
  不存 `IOptions<T>` 包装。
- 删除常量 `ApiEndpoint`（`:16`）和 `Model`（`:17`）。
- `AnalyzeAsync` 中（`:49-58` 请求体构造处）：
  - endpoint：`$"{opts.ApiBase.TrimEnd('/')}/chat/completions"`（**B4**：`TrimEnd('/')`
    防用户配置尾斜杠产生 `//chat/completions` 双斜杠 URL）
  - model：`opts.Model`
  - **`stream` 保持 `false`，请求体保持匿名类型，不引入 `ResponseFormat`**（L3/E1，见下）
  - 默认值由 `DeepSeekOptions` 记录自身提供（`DeepSeekOptions.cs:8-9`），与现硬编码值一致。

**L3（code-review）三消费者 TrimEnd 一致性**：由于 L1 `Llm:DeepSeek` 节全局生效，若用户
配置 `ApiBase` 带尾斜杠，仅 HIL 分析 TrimEnd 会造成三消费者行为不一致。故
`DeepSeekProvider`（`DeepSeekProvider.cs:139,275`）与 `DeepSeekChatProvider`
（`DeepSeekChatProvider.cs:111`）的 `$"{ApiBase}/chat/completions"` 拼接**同步加
`TrimEnd('/')`**。默认 `ApiBase` 无尾斜杠，行为无变化。

**L3 UseStreaming 语义（显式声明）**：`UseStreaming` 对 `HilAnalysisService` **无效**。
HIL 分析需要一次性完整文本展示在 `AnalysisResult` TextBox，不做 SSE 增量。请求体始终
`stream = false`，这是有意为之；`UseStreaming` 仅驱动 `DeepSeekProvider`（`DeepSeekProvider.cs:103`）。

**E1 请求体差异（显式声明 intentional）**：`HilAnalysisService` 请求体
（匿名类型 + `temperature=0.3` + 无 `ResponseFormat`，`HilAnalysisService.cs:49-58`）
与 `DeepSeekProvider`（`DeepSeekRequest` DTO + `ResponseFormat=json_object`，
`DeepSeekProvider.cs:84-91`）**有意不同**：HIL 分析输出自然语言文本给工程师查看，
不需要结构化 JSON；`temperature=0.3` 是刻意保守。两服务共用的是**配置**（endpoint/model/
timeout），不是请求协议。本 spec 不改任何请求体。

**`TimeoutSeconds` 接线（L2/E2）**：HilAnalysisService 的 HTTP 超时不再硬编码 150s，
改为从 options 读取，与 DeepSeek named client 模式一致（`AppServicesFlow.cs:206-207`）：

```csharp
// AppServicesFlow.cs:221-229 与 HeadlessHostBuilder.cs:150-155 的 delegate 均改为
// （签名从 (_, client) 改为 (sp, client)，参照 AppServicesFlow.cs:206 named client 模式）：
var opts = sp.GetRequiredService<IOptions<DeepSeekOptions>>().Value;
client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds * 5);
```

`TimeoutSeconds` 默认 30 → `30 * 5 = 150s`，**与现状默认值完全一致**，行为无变化；
用户配置 `TimeoutSeconds` 后 HIL 分析与 DeepSeekProvider 同步响应，消除两服务超时不一致。

**配置来源（B3，WPF 与 Headless 一致绑定）**：

```csharp
// WPF：AppHostBuilder.Build()（AppHostBuilder.cs:98，builder.Configuration 可用）中，
// RegisterAppServices(builder.Services) 之后新增：
builder.Services.Configure<Core.Analysis.DeepSeekOptions>(
    builder.Configuration.GetSection("Llm:DeepSeek"));

// 同时删除 AppServicesFlow.cs:236 的空 Configure<DeepSeekOptions>(options => { })
// （T3：空 lambda 无意义且防不了回归，故移除而非保留）。

// Headless：HeadlessHostBuilder.Build()（:28，builder 来自 Host.CreateApplicationBuilder :30）中，
// 在 AddHttpClient<IHilAnalysisService,...> 注册前新增同样一行：
builder.Services.Configure<Core.Analysis.DeepSeekOptions>(
    builder.Configuration.GetSection("Llm:DeepSeek"));
```

**T2 appsettings.json JSON 结构（层级，非扁平 key）**：

```json
{
  "Llm": {
    "DeepSeek": {
      "ApiBase": "https://api.deepseek.com",
      "Model": "deepseek-v4-flash",
      "TimeoutSeconds": 30,
      "UseStreaming": true
    }
  }
}
```

> 注意：`GetSection("Llm:DeepSeek")` 匹配的是层级结构（`Llm` → `DeepSeek`），
> 不是扁平 key `"Llm:DeepSeek"`。

**L1 全局生效语义（修正 §6）**：`IOptions<DeepSeekOptions>` 是 DI 全局单例，绑定
`Llm:DeepSeek` 节后**三个消费者全部读取新值**——`DeepSeekProvider`（App AI Chat）、
`DeepSeekChatProvider`、`HilAnalysisService`，以及两个 AddHttpClient delegate 的 timeout。
这是**有意的统一配置**，不是缺陷。由于 appsettings.json **默认不含 `Llm:DeepSeek` 节**，
未配置时所有消费者回退 `DeepSeekOptions` 默认值，行为与现状完全一致；仅当用户显式添加
该节时才统一生效。本 spec 不做配置隔离（独立 options 类）——那会破坏"单一配置源"且超出
HIL 范围。

### 3.2 A2: `EnableAnalyze` 空壳 → 真实自动分析

**变更 `HilViewModel.RunAsync`**（`HilViewModel.cs`）：

- **B2 空套件边界**：自动分析条件用 `result.FailedCases > 0`，而非 `!result.AllPassed`。
  `AllPassed`（`TestSuiteResult.cs:18`）在 `TotalCases == 0` 时为 `false`，`!result.AllPassed`
  会错误触发空套件的无意义分析。`FailedCases > 0` 语义精确：有失败才分析。
- **L4 插入点**：放在 StatusMessage 设置（`:198-200`）**之后**、`finally`（`:207`）**之前**，
  确保结果 DataGrid（`:193-194`）、TreeView（`:196`）、状态文本（`:198-200`）全部先渲染，
  用户不会看到界面停在 "Running..." 干等 LLM 返回。

```csharp
// :198-200 StatusMessage 设置之后、try 块末尾：
var result = await _runner.RunAsync(request, progress, default);
// ... :190-200 现有 _lastResult / 结果填充 / StatusMessage ...
if (EnableAnalyze && result.FailedCases > 0)
    await AnalyzeAsync();
```

- 复用现有 `AnalyzeAsync()`（`:118`），含超时/异常处理，不新增逻辑。
- **T4 门控安全依据（显式声明）**：自动分析走**方法调用**而非 `AnalyzeCommand`，不经
  `CanAnalyze`（`:160-161`）门控。这是安全的：`AnalyzeAsync` 方法体**不检查 `IsRunning`**，
  仅依赖 `_lastResult`（`:120-121` 检查 `_lastResult is null || _lastResult.AllPassed`）；
  `IsAnalyzing=true` 会短暂禁用手动 Analyze 按钮（`CanAnalyze` 含 `!IsAnalyzing`），
  这是正确行为。`finally`（`:207-211`）随后置 `IsRunning=false` 并 `NotifyCanExecuteChanged`。
- 手动 Analyze 按钮（`HilView.xaml:54`）保留，行为不变。
- `HilRunRequest.EnableAnalyze`（`HilRunRequest.cs:17`）语义不变，从死代码变为有消费者。

**T5 接口声明**：`IHilAnalysisService`（`Core/HIL/Analysis/IHilAnalysisService.cs`）**无需改动**
——ctor 签名只在具体类 `HilAnalysisService` 上，接口仅暴露 `AnalyzeAsync`
（`IHilAnalysisService.cs:13`）。

---

## 4. File Inventory

| 文件 | 动作 |
|------|------|
| `src/PeakCan.Host.Infrastructure/HIL/Analysis/HilAnalysisService.cs` | MODIFY — ctor + 去硬编码（endpoint/model/TrimEnd/UseStreaming 声明） |
| `src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs` | MODIFY — 删 `:236` 空 Configure；`:224` delegate 超时改读 options |
| `src/PeakCan.Host.App/Services/LlmProvider/DeepSeekProvider.cs` | MODIFY — `:139`/`:275` 拼接加 `TrimEnd('/')`（code-review L3） |
| `src/PeakCan.Host.App/Services/ChatProvider/DeepSeekChatProvider.cs` | MODIFY — `:111` 拼接加 `TrimEnd('/')`（code-review L3） |
| `src/PeakCan.Host.App/Composition/AppHostBuilder.cs` | MODIFY — `Build()` 中新增 `Configure<DeepSeekOptions>(GetSection("Llm:DeepSeek"))` |
| `src/PeakCan.Host.Infrastructure/HIL/HeadlessHostBuilder.cs` | MODIFY - 新增同款 `Configure<DeepSeekOptions>`；`:153` delegate 超时改读 options；新增 `using Microsoft.Extensions.Options;`（当前 using 列表无此引用，delegate 中 `sp.GetRequiredService<IOptions<DeepSeekOptions>>()` 依赖它） |
| `src/PeakCan.Host.App/ViewModels/HilViewModel.cs` | MODIFY — `RunAsync` 自动分析（`FailedCases > 0`，插入点 `:200` 后） |
| `appsettings.json` | MODIFY — 新增 `Llm:DeepSeek` 层级节 |
| `tests/PeakCan.Host.Infrastructure.Tests/HIL/Analysis/HilAnalysisServiceRetryTests.cs` | MODIFY — `:67` ctor 调用点补 fake options；新增 endpoint/model/timeout 用例 |
| `tests/PeakCan.Host.Infrastructure.Tests/HIL/Analysis/Sprint14Tests.cs` | MODIFY — `:96`/`:108`/`:123` 三处 ctor 调用点补 fake options |
| `tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelAnalysisTests.cs` | MODIFY — EnableAnalyze 自动分析用例 + 空套件边界 |

> **B1**：`Sprint14Tests.cs` 3 处（`:96`/`:108`/`:123`）+ `HilAnalysisServiceRetryTests.cs:67`
> 共 **4 处** `new HilAnalysisService(httpClient, credentialStore)` 调用点，ctor 改 3 参数后
> 全部需补 `IOptions<DeepSeekOptions>`（NSubstitute `IOptions<T>.Value`）。

---

## 5. Testing (TDD)

| 用例 | 断言 |
|------|------|
| `HilAnalysisService` ctor 注入 options | 使用 `ApiBase.TrimEnd('/') + "/chat/completions"`、`Model` 来自 fake options（非硬编码） |
| `HilAnalysisService` 默认 options | 不传显式配置时使用 `DeepSeekOptions` 默认值 |
| `HilAnalysisService` ApiBase 尾斜杠 | `ApiBase = "https://api.deepseek.com/"` → endpoint 为 `.../chat/completions` 单斜杠 |
| AddHttpClient timeout（**代码审查验证**，非自动化测试） | typed client 的 HttpClient 不暴露给外部，无法在 unit test 中断言 `.Timeout`；通过 CR 确认 delegate 逻辑：`sp.GetRequiredService<IOptions<DeepSeekOptions>>().Value` -> `client.Timeout == opts.TimeoutSeconds * 5`（L2） |
| `HilViewModel`：EnableAnalyze=true + 有失败 | `RunAsync` 后自动调用 `_analysisService.AnalyzeAsync` |
| `HilViewModel`：EnableAnalyze=false + 有失败 | 不自动调用 |
| `HilViewModel`：EnableAnalyze=true + AllPassed | 不自动调用 |
| `HilViewModel`：EnableAnalyze=true + **TotalCases==0 空套件** | 不自动调用（B2，`FailedCases > 0` 边界） |
| `HilViewModel`：EnableAnalyze=true + 自动分析返回 Unavailable | `AnalysisResult` 显示 UnavailableReason，不抛异常 |

---

## 6. Out of Scope

- CLI/Headless 自动分析（后续独立加 `--analyze` flag）
- **App 侧 `DeepSeekProvider` / `DeepSeekChatProvider` 请求体改造**（E1：差异是 intentional，
  本 spec 不改任何请求体）
- **配置隔离**（为 HIL 分析单独建 options 类）——`IOptions<T>` 全局生效是统一配置设计
  （L1），本 spec 接受并文档化
- Generator 热加载（Phase 7 单元 B）
- Web 报告 UI（Phase 7 单元 C）
- Multi-bus gateway（Phase 7 单元 D）
