# Plan: HIL 报告能力增强（4 合 1）

**Source**: 2026-08-10 用户需求"我都想要"——4 个报告改进方案
**Complexity**: Medium（4 个独立单元，可并行/顺序实施）
**Last Reviewed**: 2026-08-10（多视角 review：软件工程师 / 测试工程师 / 产品工程师 / 架构师）

### Review 变更记录（2026-08-10）

1. **P0** §3/§5 单元 A：新增 `HeadlessDbcLookup.cs` 创建步骤（类定义文件缺失，被引用 5 处但无定义）
2. **P0** §5 单元 A：示例代码 `MergeIdeBit(frame)` → `DbcLookupKey.ToLookupKey(frame.Id.Raw, frame.Id.IsExtended)`（前者不存在）
3. **P0** §5 单元 A：删除 `GetAllMessages` 错误引用 —— 更正：`IDbcLookup` 实际含 `FindMessage(uint)` + `GetAllMessages()` 两个方法（反射验证 0.6.4），原"仅此一个方法"陈述有误，已在本版 §3 更正
4. **P0** §5 单元 A：增加 DI lambda 内注册的风险提示 + 备选方案
5. **P0** §7：新增 3 个 P0 risk（缺失类 / DI 不可达 / 外部包 API 不确定）
6. **P1** §5 单元 B：增加负测试渲染产品决策（Status=Passed → badge 在绿色行，需独立 CSS class）
7. **P1** §4/§5 单元 C：增加 HTML 结构层变更说明（`data-status` 属性 + 列 CSS class，JS 选择器前置）
8. **P1** §5 单元 D：增加 SVG 可访问性（Y 轴编码为主、颜色为辅、`<title>` tooltip）+ 复杂度 cap（信号 > 8 截断）
9. **P2** §9：新增 P0 Spike 阶段（0.5 天），总工时 3 天 → 4 天
10. **P2** §5 单元 A WPF 接线：修正跨层依赖方向（Infrastructure 不能引用 App 层 `DbcService`）
11. **P1** 2026-08-10 二次评审修订：§3/§7 更正 `IDbcLookup` 接口描述；废弃 lambda 内 `AddSingleton`（MS DI 构建后集合只读）；修正风险表"CLI 无 --dbc"伪场景（`CliArgsParser.cs:116` 强制 `--dbc`）；§5 补 WPF DBC 数据源一致性方案（`DbcService.Current` 与 `DbcPath` 双数据源）；§5 补单元 C details 默认收起语义、单元 D 信号集选择规则、帧表 thead/colspan 同步；§9 Spike 缩减至 0.25 天（API 已反射验证）
12. **P0** 2026-08-10 三次修订（二次 review 修复）：§4/§5 WPF DBC 数据源从 `DbcService.Current` 改为 `IHilRunnerService.LastDbcDocument`（避免双重解析 + 解码错 DBC）；§4 新增 `IHilRunnerService.cs` / `HilRunnerService.cs` 变更；§3 补 `IDbcLookup` 反射与 spec 文档差异说明；§5 限定"CLI 强制 --dbc"为正常/simulate 模式（ODX 例外）；§5 统一单元 D 信号排序为 `(消息名, 信号名)` 字典序；§5 修正 `DbcParser.Parse` 签名；§9 Spike 明确"仅验证确认、建类归入单元 A"，单元 A 调为 1.75 天，总工时 4 天

## 1. 产品定位

当前报告已具备基础能力（HTML 单文件、summary card、步骤表、帧转储、趋势图、WPF WebView2 查看），但相比 vTESTstudio 有 3 个关键差距：
1. 失败现场只有原始 hex，无 DBC 信号解码
2. 负测试（WasNegatedTest）未在报告中标识
3. HTML 报告是静态表格，无交互搜索/过滤
4. 无信号时序图

本 plan 用 4 个独立单元逐一补齐。单元间无依赖，可并行实施。

## 2. 排除范围

- 报告模板定制——留到有真实需求时
- 截图捕获——WPF/WebView2 截图，低价值/高成本
- 需求追溯（ASPICE SWE.4）——独立立项
- 多报告对比——留到趋势数据积累后

## 3. Patterns to Mirror

| Category | Source | Pattern |
|----------|--------|---------|
| HTML 报告生成 | `HtmlReportGenerator.cs:22-66` | StringBuilder 拼接、内嵌 CSS/JS、单文件自包含 |
| 信号解码 | `SignalDecoder.Decode(data, signal)` | 返回 double 工程值（外部包 `PeakCan.HIL.Core`） |
| DBC 消息查找 | `DbcDocument.MessagesById[key]` | key = `rawId \| 0x80000000u`（Extended）或 `rawId`（Standard） |
| DBC lookup key 转换 | `DbcLookupKey.ToLookupKey(rawId, isExtended)` | Infrastructure 内部静态方法，IDE bit 31 合并 |
| `IDbcLookup` 接口 | `FindMessage(uint canId)` + `IEnumerable<Message> GetAllMessages()` | **两个方法**（反射验证 0.6.4；spec 文档 `2026-07-29-hil-sprint1-design.md:362` 仅记录 `FindMessage`，未覆盖 `GetAllMessages`，以反射结果为准）；报告解码用 `FindMessage` 即可 |
| 报告 DBC 参数 | `GenerateHtml(result, trends)` | 现有签名，追加可选 `DbcDocument?` 参数 |
| CLI 报告接线 | `Program.cs:151-188` | `switch(cli.Format)` 分支 |
| WPF 报告服务 | `HilReportService.Generate(result)` | 调 `HtmlReportGenerator` + 落盘 + 趋势 |
| 负测试标识 | `StepResult.WasNegatedTest` | init-only bool；引擎设置时同步将 Status 提升为 Passed |
| 帧转储 | `HtmlReportGenerator.cs:128-157` | 内联 `<table>`，capped 50 帧 |
| DBC DI 注册 | `HeadlessHostBuilder.cs:85-92` | 当前仅注册 `IDbcLookup`（`HeadlessDbcLookup`），`DbcDocument` 未注册 |
| DBC 服务访问（WPF） | `DbcService.Current` | `DbcDocument?` 属性，null 时 DBC 未加载；**trace 面板状态，报告不用它**（报告用 `_runner.LastDbcDocument`） |
| ⚠️ 缺失类 | `HeadlessDbcLookup` | 被 `HeadlessHostBuilder` 和测试引用，但**类定义文件不存在** |

## 4. Files to Change

### 单元 A: DBC 信号解码进报告

| File | Action | Change |
|------|--------|--------|
| `Infrastructure/HIL/HeadlessDbcLookup.cs` | **CREATE** | **P0 前置**：实现 `IDbcLookup`，构造函数接受 `DbcDocument`（类定义缺失，见 §3 ⚠️） |
| `Infrastructure/Cli/Reporting/HtmlReportGenerator.cs` | EDIT | `GenerateHtml` 加 `DbcDocument?` 参数；帧渲染时解码信号 |
| `Infrastructure/HIL/HeadlessHostBuilder.cs` | EDIT | **P0 修复**：改为两个独立 lambda —— `DbcDocument` 工厂单例 + `IDbcLookup` 依赖它（见 §5 单元 A） |
| `Host.Cli/Program.cs` | EDIT | 从 DI 拿 `DbcDocument` 传入 `GenerateHtml` |
| `Infrastructure/HIL/Reporting/HilReportService.cs` | EDIT | `Generate` 加 `DbcDocument?` 可选参数（`= null`，现有调用方不改） |
| `Infrastructure/HIL/Reporting/IHilReportService.cs` | EDIT | 接口签名追加可选参数（`DbcDocument? = null`） |
| `Infrastructure/HIL/IHilRunnerService.cs` | EDIT | 新增 `DbcDocument? LastDbcDocument { get; }` —— 暴露运行实际解析的 DBC（见 §5 单元 A） |
| `Infrastructure/HIL/HilRunnerService.cs` | EDIT | `RunAsync` 内 `HeadlessHostBuilder.Build` 后取 `DbcDocument` 赋给 `LastDbcDocument` |
| `App/ViewModels/HilViewModel.cs` | EDIT | 调用 `_reportService.Generate` 时传入 DBC（从 `_runner.LastDbcDocument` 拿，无需注入 `DbcService`） |
| 测试 | `HtmlReportGeneratorTests.cs` | 加 DBC 解码测试用例（含无 DBC 回落路径） |

### 单元 B: WasNegatedTest 标识

| File | Action | Change |
|------|--------|--------|
| `Infrastructure/Cli/Reporting/HtmlReportGenerator.cs` | EDIT | 步骤行加 `WasNegatedTest` badge；**注意**：负测试步骤 Status=Passed，badge 出现在绿色行上，需独立 CSS class 区分 |
| 测试 | `HtmlReportGeneratorTests.cs` | 加负测试步骤渲染测试（验证 badge 仅对 `WasNegatedTest=true` 显示） |

### 单元 C: HTML 交互（搜索/过滤）

| File | Action | Change |
|------|--------|--------|
| `Infrastructure/Cli/Reporting/HtmlReportGenerator.cs` | EDIT | **结构层**：步骤 `<tr>` 加 `data-status` 属性 + label/message 列加 CSS class；**JS 层**：`EmbedJs()` 加搜索/过滤/切换逻辑；**CSS 层**：搜索框/按钮样式 |
| 测试 | `HtmlReportGeneratorTests.cs` | HTML 有效性检查——输出含 `<script>`、搜索框 `<input>`、状态筛选按钮、`data-status` 属性；JS 交互行为手动测试用例（不自动化） |

### 单元 D: 信号时序图

| File | Action | Change |
|------|--------|--------|
| `Infrastructure/Cli/Reporting/HtmlReportGenerator.cs` | EDIT | 失败步骤加 SVG 时间轴渲染；Y 轴=信号值，颜色仅作多信号区分辅助，`<title>` tooltip 显示精确值 |
| 测试 | `HtmlReportGeneratorTests.cs` | SVG 输出含正确信号名 + 坐标点数量；视觉检查为辅 |

## 5. Implementation Phases

### 单元 A: DBC 信号解码进报告（1.5 天）

**P0 前置步骤 1：创建 `HeadlessDbcLookup` 类**（类定义文件缺失）

`HeadlessHostBuilder.cs:91` 和测试共 5 处引用 `new HeadlessDbcLookup(doc.Value!)`，但类定义不存在。
需新建 `Infrastructure/HIL/HeadlessDbcLookup.cs`：
```csharp
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL.Contracts;

namespace PeakCan.Host.Infrastructure.HIL;

/// <summary>
/// Headless/CLI 模式的 DBC 查找适配器。包装 DbcDocument，实现 IDbcLookup。
/// 注册到 DI 后供 PeakCanAssertionContext / HILAssertionContext 消费。
/// </summary>
internal sealed class HeadlessDbcLookup : IDbcLookup
{
    private readonly DbcDocument _doc;
    public HeadlessDbcLookup(DbcDocument doc) => _doc = doc;

    public Message? FindMessage(uint canId)
        => _doc.MessagesById.TryGetValue(canId, out var msg) ? msg : null;
}
```

**签名变更**：
```csharp
// HtmlReportGenerator.cs
public static string GenerateHtml(TestSuiteResult result, IReadOnlyList<TrendEntry>? trends = null,
    DbcDocument? dbc = null)
```

**帧渲染加信号解码**（`RenderCase` 内 `framesAroundFailure` 渲染块）：
```csharp
// 在现有 CAN ID / Data hex / Timestamp 列之后，加"Decoded Signals"列
// 查找 key 使用 DbcLookupKey.ToLookupKey（IDE bit 31 合并），与 AssertionContext 保持一致
if (dbc is not null)
{
    var lookupKey = DbcLookupKey.ToLookupKey(frame.Id.Raw, frame.Id.IsExtended);
    if (dbc.MessagesById.TryGetValue(lookupKey, out var msg))
    {
        var decoded = string.Join(", ", msg.Signals.Select(s =>
        {
            // P0：信号名/枚举文本必须 HtmlEncode，防 DBC 内容注入 HTML
            // P1：SignalDecoder.Decode 对 >64bit 信号抛 ArgumentOutOfRangeException，
            //   加 try/catch 避免单条信号解码失败导致整个报告生成失败
            try
            {
                var val = SignalDecoder.Decode(frame.Data.Span, s);
                var enumText = s.ValueTableName is not null
                    ? SignalDecoder.TryDecodeEnumText(s, val, dbc)
                    : null;
                return $"{HtmlEncode(s.Name)}={HtmlEncode(enumText ?? val.ToString("G", CultureInfo.InvariantCulture))}";
            }
            catch (ArgumentOutOfRangeException)
            {
                return $"{HtmlEncode(s.Name)}=ERR";
            }
        }));
        sb.Append($"<td class=\"mono\">{HtmlEncode(decoded)}</td>");
    }
}
```

> **注意**：加第 4 列需同步修改帧表 thead（`HtmlReportGenerator.cs:132` 当前 3 列）
> 与 "more frames" 行 colspan（`:152` 当前 colspan=3 → 4）；外层 `<td colspan="8">`（`:130`）不变。

**P0 前置步骤 2：DBC 文档注册到 DI**

现状：`HeadlessHostBuilder.cs:85-92` 只注册 `IDbcLookup`，`DbcDocument` 本身（含 `ValueTables`）
在 DI 中不可达 —— `SignalDecoder.TryDecodeEnumText` 需要 `DbcDocument.ValueTables` 查 VAL_ 文本，
但 `IDbcLookup` 只暴露 `FindMessage(uint)` / `GetAllMessages()`，无 `ValueTables` 能力。

**（修订）主方案：两个独立 lambda，不嵌套注册。**
MS DI 的 provider 在首次解析时构建，构建后集合只读 —— lambda 内 `AddSingleton` 大概率抛
`InvalidOperationException`，原"lambda 内注册"方案废弃。正常模式和 simulate 模式强制 `--dbc`
（`CliArgsParser.cs:116` 和 `:110-111`）；ODX import 模式（`:96-101`，`dbc ?? ""`）例外但不生成报告，与本 feature 无关。

```csharp
// HeadlessHostBuilder.cs:85-92 改：DbcDocument 工厂单例 + IDbcLookup 依赖它
builder.Services.AddSingleton(sp =>
{
    var text = File.ReadAllText(args.DbcPath);
    var doc = DbcParser.Parse(text, cancellationToken: default);   // 反射验证 0.6.4 签名：Parse(string, CancellationToken = default)
    if (!doc.IsSuccess)
        throw new InvalidOperationException($"DBC parse failed for '{args.DbcPath}': {doc.Error?.Message}");
    return doc.Value!;
});
builder.Services.AddSingleton<IDbcLookup>(sp =>
    new HeadlessDbcLookup(sp.GetRequiredService<DbcDocument>()));
```

> 注：`DbcDocument` 为纯数据 record（反射验证 0.6.4），注册单例零风险；
> parse 由"首次解析 IDbcLookup 时"变为"首次解析 DbcDocument 时"，量级毫秒级，可接受。

**CLI 接线**（`Program.cs:151-188` 的 `html`/`html+junit` 分支）：从 DI 拿 `DbcDocument` 传入：
```csharp
var dbcDoc = host2.Services.GetService<DbcDocument>();  // CLI 强制 --dbc，此处实际非 null；null 分支仅防御
var html = HtmlReportGenerator.GenerateHtml(result, trends, dbcDoc);
```

**WPF 接线**：`IHilReportService.Generate` 加 `DbcDocument? = null` 可选参数，与 `HtmlReportGenerator` 一致。
`HilViewModel` 从 `_runner.LastDbcDocument` 取文档后传入。

> ⚠️ **架构约束**：`HilReportService` 在 Infrastructure 层，`DbcService` 在 App 层。
> Infrastructure **不能**反向引用 App 层。因此**不能**在 `HilReportService` 构造注入 `DbcService`。
> 正确做法：`HilViewModel`（App 层）取文档后传给 `Generate`。

> ✅ **（二次修订）DBC 数据源：用 `HilRunnerService.LastDbcDocument`，废弃 `DbcService.Current` 方案。**
>
> **为什么不用 `DbcService.Current`**：`DbcService.Current`（`DbcService.cs:53`）是 trace 面板的加载状态，
> 而 HIL 运行链路 `HilViewModel.DbcPath → HilRunRequest → CliArgs → HeadlessHostBuilder.Build()` 独立解析 DBC ——
> 两者完全无关，可能指向不同文件。直接取 `Current` 会解码错 DBC 或为 null。
>
> **正确方案**：`HilRunnerService` 在 `RunAsync` 内 `Build()` 之后、运行之前，从 DI 取出本次解析的
> `DbcDocument` 缓存到 `LastDbcDocument` 属性。`HilViewModel` 已注入 `IHilRunnerService`，
> 在 `_reportService.Generate(result)` 前直接取 `_runner.LastDbcDocument` 传入。
>
> 好处：报告用的 DBC **就是运行实际使用的那个**，零双重解析、零不一致风险、`HilViewModel` 构造函数零改动。

**`IHilRunnerService` 接口新增**：
```csharp
// IHilRunnerService.cs
public interface IHilRunnerService
{
    Task<TestSuiteResult> RunAsync(HilRunRequest request, IProgress<TestProgress>? progress = null, CancellationToken ct = default);
    /// <summary>最近一次 RunAsync 实际解析的 DBC 文档；未运行或无 DBC 时为 null。</summary>
    DbcDocument? LastDbcDocument { get; }
}
```

**`HilRunnerService` 实现**：
```csharp
// HilRunnerService.cs
public DbcDocument? LastDbcDocument { get; private set; }

public async Task<TestSuiteResult> RunAsync(...)
{
    using var host = HeadlessHostBuilder.Build(HilRunRequestExtensions.ToCliArgs(request));
    LastDbcDocument = host.Services.GetService<DbcDocument>();  // 我们 P0 修复会注册它
    // ... engine, channel, ctx, sender 等不变 ...
}
```

**`HilViewModel` 接线**（`RunAsync` 内，`:309` 附近）：
```csharp
// 替换 var report = _reportService.Generate(result);
var dbcDoc = _runner.LastDbcDocument;  // 运行实际使用的 DBC，可能 null
var report = _reportService.Generate(result, dbcDoc);
```

> 注：`_runner` 已在构造函数注入（`HilViewModel.cs:19,80`），无需新增依赖。

### 单元 B: WasNegatedTest 标识（0.5 天）

> ⚠️ **产品决策点**：负测试步骤在引擎中被提升为 `Status = Passed`（`TestSuiteEngine.cs:180`），
> 因此 badge 会出现在**绿色通过行**上。用户看到"通过 + [negated]"可能产生困惑。
>
> **推荐方案**：为负测试步骤增加独立 CSS class `negated-pass`（黄/橙色左边框），
> 与纯通过（绿色）和纯失败（红色）区分。实施前确认此方案可接受。

```csharp
// RenderCase 步骤行 — 在 Status 列后追加
// 注意：负测试步骤 Status=Passed，stepClass="pass"，需额外判断 WasNegatedTest 覆盖 class
var rowClass = step.WasNegatedTest ? "negated-pass" : stepClass;
// ... <tr> 使用 rowClass ...

if (step.WasNegatedTest)
{
    sb.Append($" <span class=\"badge negated\" title=\"负测试：预期失败确实发生\">[negated]</span>");
}
```

CSS 追加：
```css
.badge.negated { background: rgba(255, 193, 7, 0.15); color: #ffc107; }
tr.negated-pass td { border-left: 3px solid #ffc107; }  /* 黄色左边框区分 */
```

### 单元 C: HTML 交互（0.5 天）

**HTML 结构层变更**（`RenderCase` 步骤行）：
- 每个步骤 `<tr>` 加 `data-status` 属性（`pass`/`fail`/`skipped`/`comment`），供 JS 过滤选择
- label 列加 `<td class="step-label">`、message 列加 `<td class="step-message">`，供 JS 文本搜索

**JS 层变更**（`EmbedJs()` 内嵌）：
- 搜索框：按 `.step-label` / `.step-message` 文本过滤步骤行（`data-status` 行内匹配）
- 状态切换按钮：全部/通过/失败/跳过（通过 `data-status` 属性筛选）
- 自动展开失败 case（配合默认收起，见下方注意）
- 纯前端，不刷新页面

> **注意**：当前 `<tr>` 无 `data-status`、列无 class，JS 写完后选择器找不到元素。
> 结构层变更必须先于或与 JS 同步实施。

> **（修订）默认展开语义**：现有 `<details open>`（`HtmlReportGenerator.cs:50`）默认全展开，
> 此时"自动展开失败 case"无意义。需移除 `open` 属性改为默认收起，JS 在 `DOMContentLoaded`
> 中按 case 是否含失败步骤（`data-status="fail"`）展开失败 case。产品上如需要保留
> "全部展开/收起"切换按钮，属可选加分项，不在本单元验收范围。

### 单元 D: 信号时序图（1 天）

**范围限制：** `FramesAroundFailure` 只在 `!step.Passed` 时捕获（`TestSuiteEngine.cs:197`），
因此时序图**仅在失败步骤可用**，全部通过时无图。负测试步骤（Status=Passed）同样无帧数据。
此限制与 vTESTstudio 的"全程信号轨迹"不同——本实现仅覆盖失败前后 ≤50 帧。
**实施前确认此限制可接受。**

在失败步骤的 `FramesAroundFailure` 区域，内嵌 SVG 时间轴：
- 水平时间轴，标记每个帧的时间戳（相对时间，首帧为 0 µs）
- **Y 轴位置编码信号值**（主线）；颜色仅作多信号区分辅助（色盲可访问）
- 每个信号曲线配 `<title>` tooltip，hover 显示精确值（信号名 + 值 + 时间戳）
- 只对 DBC 解码后的信号画（依赖单元 A 实现）
- 用纯 SVG 实现，不引入外部图表库

**（修订）信号集选择规则**：帧集内出现的所有 CAN ID 查 `MessagesById` 得消息集合，
取其中全部信号，按 `(消息名, 信号名)` 字典序排序；同一信号名跨消息不合并。
**Y 轴映射**：每个信号按其帧集内的 min/max 独立归一化，映射到 SVG 高度等分的
独立 Y 区间（避免量级差异互相压扁）；单帧无有效值（解码 ERR/缺失）处断线不插值。

**复杂度 cap**：信号数 > 8 时仅渲染排序后的前 8 个，避免 SVG 过大/卡顿；
报告头部显示"showing 8/N signals"提示。

## 6. 验证

```bash
# 单元 A + B: 编译 + 测试
dotnet test peakcan-host/tests/PeakCan.Host.Infrastructure.Tests/ --filter "HtmlReport"

# 单元 C + D: 视觉检查
# 用浏览器打开生成的 HTML 报告，验证搜索/过滤和时序图

# 全量回归
dotnet build peakcan-host/src/PeakCan.Host.Cli/PeakCan.Host.Cli.csproj -v q --nologo
dotnet test peakcan-host/tests/PeakCan.Host.Core.Tests/
dotnet test peakcan-host/tests/PeakCan.Host.Infrastructure.Tests/
```

## 7. Risks

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| DBC 文档在报告生成时不可用（CLI 正常/simulate 模式强制 `--dbc`（`CliArgsParser.cs:116`/`:110-111`）；WPF 侧真风险是 `LastDbcDocument` 为 null — 运行未完成或 DI 注册失败） | 低 | 低 | `DbcDocument?` 可选参数，null 时回落 hex 显示；WPF 数据源见 §5 单元 A（`_runner.LastDbcDocument`） |
| **P0** `HeadlessDbcLookup` 类定义文件缺失 | 高 | 高 | 实施前创建该类（见 §5 单元 A 前置步骤 1） |
| **P0** `DbcDocument` 在 DI 中不可达（`HeadlessHostBuilder` 只注册 `IDbcLookup`） | 高 | 高 | 两个独立 lambda 注册（`DbcDocument` 工厂单例 + `IDbcLookup` 依赖它，见 §5 单元 A 前置步骤 2） |
| **P0** `SignalDecoder`/`DbcDocument` 的 API 签名与 Plan 假设不符（外部包） | ~~中~~ **已消除** | 高 | **已完成**：反射验证 peakcan.hil.core 0.6.4（`MessagesById`/`ValueTables`/`Decode`/`TryDecodeEnumText`/`IDbcLookup` 两方法），见 §9 Spike 项 1 |
| ~~lambda 内 AddSingleton~~（废弃） | **确定**（MS DI provider 构建后集合只读） | 中 | 主方案：两个独立 lambda（见 §5 单元 A 前置步骤 2），无需运行时注册 |
| HTML 报告内嵌 JS 超过 WebView2 2MB 上限 | 低 | 低 | 报告已落盘，WPF 用 `Navigate(fileUri)` 无限制 |
| SVG 时序图在复杂信号下渲染卡顿 | 低 | 低 | 信号数 > 8 时仅渲染前 8 个；帧 capped 50 |
| 报告文件过大（100+ case 多失败步骤） | 低 | 低 | SVG 复杂度 cap + 信号数限制，极大报告场景待观察 |
| `IHilReportService` 接口签名变更影响面 | 低 | 低 | 用可选参数（`DbcDocument? = null`），现调用方零改动 |
| **P1** 时序图仅在失败步骤周围有数据，非全程信号轨迹 | 确定 | 中 | 已在 §5 单元 D 标注范围限制，实施前确认可接受 |
| **P1** 负测试步骤 Status=Passed，badge 出现在绿色行致语义混淆 | 中 | 中 | 独立 CSS class `negated-pass` + tooltip 解释（见 §5 单元 B） |
| DBC 信号名/枚举文本含 HTML 特殊字符 → XSS 注入 | 低 | 中 | 信号解码输出全部 `HtmlEncode`（§5 单元 A 示例已修复） |
| `SignalDecoder.Decode` 对 >64bit 信号抛异常 | 低 | 低 | 解码 try/catch，失败显示 `ERR` 不中断报告 |

## 8. Acceptance

### 单元 A
- [ ] 报告步骤表帧转储区显示 DBC 解码信号值（`Msg.Signal=Value` 格式）
- [ ] 无 DBC 时回落 hex 显示（向后兼容）
- [ ] CLI `--dbc` + `--format html` 生成的报告包含解码信号
- [ ] WPF 面板生成的报告包含解码信号（如果 DBC 已加载）
- [ ] **（发现 9）** 信号名/枚举文本经 `HtmlEncode` 输出（XSS 防护）
- [ ] **（发现 10）** >64bit 信号解码失败时显示 `ERR`，不中断报告生成

### 单元 B
- [ ] `WasNegatedTest = true` 的步骤在报告中有 `[negated]` 标识
- [ ] `WasNegatedTest = false` 的步骤不显示标识（向后兼容）
- [ ] **产品决策验收**：负测试步骤以 `negated-pass` class（非纯绿/纯红）呈现，用户可区分"预期失败的通过"与"正常通过"
- [ ] badge 含 tooltip 解释"负测试：预期失败确实发生"

### 单元 C
- [ ] 报告有搜索框，可按 label/message 过滤步骤
- [ ] 报告有状态筛选按钮（全部/通过/失败/跳过）
- [ ] 失败的 case 默认展开
- [ ] 搜索/过滤不刷新页面（纯前端 JS）
- [ ] HTML 有效性检查测试：输出含 `<script>`、搜索框 `<input>`、状态筛选按钮、`data-status` 属性
- [ ] 步骤 `<tr>` 含 `data-status` 属性，label/message 列含对应 CSS class

### 单元 D
- [ ] 失败步骤帧转储区有 SVG 信号时序图
- [ ] 时序图显示信号名 + 时间轴 + 值变化
- [ ] Y 轴位置编码信号值（非仅颜色），`<title>` tooltip 显示精确值
- [ ] 无 DBC 时不显示时序图（向后兼容）
- [ ] 帧数 > 50 时截断
- [ ] 信号数 > 8 时仅渲染前 8 个，头部显示"showing 8/N signals"
- [ ] SVG 输出含正确信号名 + 坐标点数量的单元测试

## 9. 实施建议

4 个单元依赖关系：
```
单元 A (DBC 解码) ── 单元 D (时序图) 依赖 A
单元 B (负测试标识) ── 独立（但产品决策先行）
单元 C (交互) ── 独立
```

**P0 Spike（0.25 天，必须在任何编码前完成）**：
1. **（已完成，2026-08-10 评审）** `PeakCan.HIL.Core` 0.6.4 API 已通过 nuget 缓存反射验证：
   `DbcDocument.MessagesById: IReadOnlyDictionary<uint,Message>`、`ValueTables: IReadOnlyDictionary<string,ValueTable>`、
   `SignalDecoder.Decode(ReadOnlySpan<byte>,Signal)→double`、`TryDecodeEnumText(Signal,double,DbcDocument)→string`、
   `IDbcLookup` 含 `FindMessage(uint)` + `GetAllMessages()`（spec 文档未覆盖 `GetAllMessages`，以反射为准）。无需再反编译。
2. 跑 `dotnet build` 确认 baseline：当前 `HeadlessDbcLookup` 缺失（5 处引用无定义），预期编译失败——
   记录编译错误，确认**仅** `HeadlessDbcLookup` 一个缺失类（无其他隐藏问题）
3. 实施 §5 单元 A 前置步骤 2 的 DI 双 lambda 注册，跑相关测试验证 MS DI 容器行为（确认不抛 `InvalidOperationException`）

> Spike 仅做验证确认，不建类。"建 `HeadlessDbcLookup` 类 + 帧解码 + 三处接线"归入单元 A 工时。

**建议实施顺序**：

| 阶段 | 单元 | 工时 | 理由 |
|------|------|------|------|
| 0 | **Spike** | 0.25 天 | API 已反射验证；Spike = baseline 编译确认 + DI 双 lambda 行为验证 |
| 1 | 单元 B | 0.5 天 | 最独立、快速见效；产品决策在 Spike 阶段同步确认 |
| 2 | 单元 A | 1.75 天 | 建缺失类 + 帧解码 + DI 三处接线 + `IHilRunnerService.LastDbcDocument` 暴露；核心能力 |
| 3 | 单元 C | 0.5 天 | JS + HTML 结构 + CSS 三层联动 |
| 4 | 单元 D | 1 天 | 依赖 A 完成；SVG 布局 + 复杂度 cap |

> **总工时估算**：4 天（含 0.25 天 Spike）。单元 A 从 1.5 天调为 1.75 天，涵盖"建缺失类 + `LastDbcDocument` 暴露"。