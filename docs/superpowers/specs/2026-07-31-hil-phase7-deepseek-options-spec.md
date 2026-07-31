# HIL Phase 7 (Unit A): DeepSeekOptions Wiring + EnableAnalyze Auto-Analyze

> Spec date: 2026-07-31
> Depends: Phase 6 (complete — commit `579b02e`)
> Scope: **仅 WPF 路径**。单元 A 是 Phase 7 四个独立单元的第一个，执行顺序 A → B → C → D
> （B=Generator 热加载，C=Web 报告 UI，D=Multi-bus gateway，各自独立 spec → plan）。

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
| `DeepSeekOptions` 已存在 | `Core/Analysis/DeepSeekOptions.cs:6-19`：`ApiBase`（默认 `https://api.deepseek.com`）、`Model`、`TimeoutSeconds`、`UseStreaming` |
| 参照模式（IOptions 注入） | `DeepSeekProvider.cs:37-47`：ctor 注入 `IOptions<DeepSeekOptions>` |
| endpoint 拼接模式 | `DeepSeekProvider.cs:139` / `DeepSeekChatProvider.cs:111`：`$"{ApiBase}/chat/completions"` |
| WPF 空配置 | `AppServicesFlow.cs:236`：`Configure<DeepSeekOptions>(options => { })` |
| appsettings.json 无配置节 | grep `Llm\|DeepSeek` 在 appsettings.json 无匹配 |
| `EnableAnalyze` 无消费者 | `HilRunRequest.cs:17` 定义；`HilViewModel.cs:186` 传入 `request`；全库 grep 无读取点 |
| 自动分析插入点 | `HilViewModel.cs:188-191`：`RunAsync` 拿到 `result` 后设置 `_lastResult` |

### 2.2 现有可复用实现

`HilViewModel.AnalyzeAsync`（`HilViewModel.cs:118-158`）已有完整实现：`CanAnalyze`
门控（`:160-161`）、超时捕获 → "Analysis timed out."（`:140-147`，code-review M2）、
异常降级、`IsAnalyzing` 状态。自动分析直接复用该方法，不新增逻辑。

### 2.3 DI 注册现状

- WPF：`AppServicesFlow.cs:221-229` `AddHttpClient<IHilAnalysisService, HilAnalysisService>` + Polly retry
- Headless：`HeadlessHostBuilder.cs:150-155` `AddHttpClient<IHilAnalysisService, HilAnalysisService>` + `GetRetryPolicy()`

`AddHttpClient<TInterface, TImpl>` 通过 DI 自动解析构造函数依赖，因此新增
`IOptions<DeepSeekOptions>` 参数无需改注册处（前提是 `IOptions<T>` 已注册）。

---

## 3. Design

### 3.1 A1: `HilAnalysisService` 去硬编码 → 读配置

**变更 `HilAnalysisService`**（`Infrastructure/HIL/Analysis/HilAnalysisService.cs`）：

- ctor 由 `(HttpClient httpClient, ICredentialStore credentialStore)` 改为
  `(HttpClient httpClient, ICredentialStore credentialStore, IOptions<DeepSeekOptions> options)`
  —— 与 `DeepSeekProvider`（`DeepSeekProvider.cs:37-47`）模式一致。
- 删除常量 `ApiEndpoint`（`:16`）和 `Model`（`:17`）。
- `AnalyzeAsync` 中：
  - endpoint：`$"{opts.ApiBase}/chat/completions"`
  - model：`opts.Model`
  - 默认值由 `DeepSeekOptions` 记录自身提供（`DeepSeekOptions.cs:8-9`），与现硬编码值一致。

**配置来源**：

| 路径 | 配置 |
|------|------|
| WPF | `AppServicesFlow.cs:236` 的 `Configure<DeepSeekOptions>(options => { })` 改为从 `appsettings.json` 绑定 `Llm:DeepSeek` 节 |
| Headless | `HeadlessHostBuilder.cs` 新增 `Configure<DeepSeekOptions>(options => { })`（显式默认；`IOptions<T>` 未配置也可解析默认值，但显式声明防回归） |

**appsettings.json**：新增 `Llm:DeepSeek` 节，含 `ApiBase` / `Model` / `TimeoutSeconds` /
`UseStreaming`，默认值与 `DeepSeekOptions` 记录一致（与 App 侧 AI Chat 共用同一节，
后续若 App 侧接真配置也走同节）。

**不改**：WPF 与 Headless 两处 `AddHttpClient` 注册（DI 自动解析新参数）；
Polly retry 策略；凭据链（`ChainedCredentialStore` / `SimpleCredentialStore`）。

### 3.2 A2: `EnableAnalyze` 空壳 → 真实自动分析

**变更 `HilViewModel.RunAsync`**（`HilViewModel.cs`，插入点在 `:188-191`）：

```csharp
var result = await _runner.RunAsync(request, progress, default);
_lastResult = result;
AnalyzeCommand.NotifyCanExecuteChanged();

// A2: EnableAnalyze=true 且结果有失败 → 自动分析（复用 AnalyzeAsync）
if (EnableAnalyze && !result.AllPassed)
    await AnalyzeAsync();
```

- 复用现有 `AnalyzeAsync()`（`:118`），含超时/异常处理，不新增逻辑。
- `AnalyzeAsync` 内部已检查 `_lastResult is null || AllPassed`（`:120-121`），双重保护。
- 手动 Analyze 按钮（`HilView.xaml:54`）保留，行为不变。
- `HilRunRequest.EnableAnalyze`（`HilRunRequest.cs:17`）语义不变，从死代码变为有消费者。
- `AnalyzeAsync` 走**方法调用**而非 `AnalyzeCommand`，不经 `CanAnalyze` 门控
  （`:160-161`），因此自动分析不受 `IsRunning`/`IsAnalyzing` 状态限制。

**时序**：插入点在 `RunAsync` 的 `try` 块内（`result` 赋值后、`:188-191` 之后），此时
`IsRunning` 仍为 `true`，但 `AnalyzeAsync` 不检查 `IsRunning`（直接 await
`_analysisService`），无冲突。`IsAnalyzing=true` 期间会短暂禁用手动 Analyze 按钮
（`CanAnalyze` 含 `!IsAnalyzing`），这是正确行为。`finally`（`:207-211`）随后置
`IsRunning=false` 并 `NotifyCanExecuteChanged`。

---

## 4. File Inventory

| 文件 | 动作 |
|------|------|
| `src/PeakCan.Host.Infrastructure/HIL/Analysis/HilAnalysisService.cs` | MODIFY — ctor + 去硬编码 |
| `src/PeakCan.Host.App/Composition/AppHostBuilder/AppServicesFlow.cs` | MODIFY — `:236` 绑定 `Llm:DeepSeek` 节 |
| `src/PeakCan.Host.Infrastructure/HIL/HeadlessHostBuilder.cs` | MODIFY — 新增 `Configure<DeepSeekOptions>` |
| `src/PeakCan.Host.App/ViewModels/HilViewModel.cs` | MODIFY — `RunAsync` 自动分析 |
| `appsettings.json` | MODIFY — 新增 `Llm:DeepSeek` 节 |
| `tests/PeakCan.Host.Infrastructure.Tests/HIL/Analysis/HilAnalysisServiceRetryTests.cs` | MODIFY — ctor 加 fake options |
| `tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelAnalysisTests.cs` | MODIFY — EnableAnalyze 自动分析用例 |

---

## 5. Testing (TDD)

| 用例 | 断言 |
|------|------|
| `HilAnalysisService` ctor 注入 options | 使用 `ApiBase`+`/chat/completions`、`Model` 来自 fake options（非硬编码） |
| `HilAnalysisService` 默认 options | 不传显式配置时使用 `DeepSeekOptions` 默认值 |
| `HilViewModel`：EnableAnalyze=true + 有失败 | `RunAsync` 后自动调用 `_analysisService.AnalyzeAsync` |
| `HilViewModel`：EnableAnalyze=false + 有失败 | 不自动调用 |
| `HilViewModel`：EnableAnalyze=true + AllPassed | 不自动调用 |
| `HilViewModel`：EnableAnalyze=true + 自动分析返回 Unavailable | `AnalysisResult` 显示 UnavailableReason，不抛异常 |

现有测试更新：`HilAnalysisServiceRetryTests` 所有 ctor 调用点补 fake
`IOptions<DeepSeekOptions>`（NSubstitute `IOptions<T>.Value`）。

---

## 6. Out of Scope

- CLI/Headless 自动分析（后续独立加 `--analyze` flag）
- App 侧 `DeepSeekProvider` / `DeepSeekChatProvider` 真接 appsettings（现有空配置保持，
  本 spec 只让 `Llm:DeepSeek` 节对 HIL 分析生效）
- Generator 热加载（Phase 7 单元 B）
- Web 报告 UI（Phase 7 单元 C）
- Multi-bus gateway（Phase 7 单元 D）
