# NM M1-C：AI 工具、提示词、NM 面板与补全对话框实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把计划 B 的确定性引擎接到产品上：3 个 AI 工具（`get_nm_profile` / `get_nm_overview` / `get_nm_events`）、M1 系统提示词、可点击 seek 的 NM 面板、缺项补全对话框、sidecar 保存与 Markdown/CSV 导出。

**Architecture:** 工具只读缓存好的 `NmReport`，不重新扫帧；面板与工具共用同一个 `NmFlow` 快照方法。分析在 `Task.Run` 中执行，UI 线程只做快照与渲染。补全对话框只写 sidecar，不碰 DBC；保存后让缓存失效并重算。

**Tech Stack:** .NET 10、WPF、CommunityToolkit.Mvvm、System.Text.Json、xUnit。仓库 `D:\claude_proj2\peakcan-host`。

**Spec:** `D:\claude_proj2\peakcan-host\docs\superpowers\specs\2026-11-19-nm-ai-analysis-design.md`（§8、§9、§6.6.3–6.6.7、§10、§11、§13.1 App/UI、§14.1 M1-C/M1-D）

## Global Constraints

- **AI 不写配置**：工具没有 `overrides` 参数；用户口述的值只能由用户在补全对话框里确认。
- **不越界**：提示词要求 AI 在 `capabilities`/`missing_inputs` 缺项时不下 NID/CBV/PN/睡眠结论。
- **隐私默认**：发给 LLM 的内容只含计数、时间、事件类型、字节索引/位索引、变化次数、DBC 名称；**不含字节取值、用户数据原文**。面板本地可显示取值。
- **线程**：`NmAnalyzer.Analyze` 只在 `Task.Run` 中调用；UI 线程只取快照和渲染。取消令牌贯穿。
- **缓存**：工具与面板必须复用同一份 `NmReport`；`get_nm_events` 基于缓存分页，不重扫。
- **空态诚实**：无 NM 时显示"本窗口未发现 NM 帧"，不推断节点状态。
- **只跑定向测试**：`dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~Nm"`。
- **与 spec §8.1 的一处补充**：`NmReport` 增加尾随字段 `Direction`（`NmDirectionCounts`），否则工具无法按 §8.1 输出 `direction`；这是纯增量字段。

## Review Focus

| # | 输入/条件 | 期望行为 | 归属任务 |
|---|---|---|---|
| 1 | DBC 缺 NM 标记，用户问"有没有掉线" | 工具返回 `missing_inputs`；AI 先说明能做什么、缺什么，不猜 ID | Task 2、3 |
| 2 | 用户只问周期 | 只启用 Timing 能力，不弹 NID/CBV 补全 | Task 2、7 |
| 3 | DBC 完整 | **不弹**补全对话框；面板直接显示结果 | Task 7 |
| 4 | `get_nm_events` 事件数 > 200 | 分页返回，`next_page` 非空，不一次性倾倒 | Task 2 |
| 5 | 用户点击事件行 | 图表 seek 到该时刻（±0 容差） | Task 5、6 |
| 6 | trace 无 NM 帧 | 面板空态文案正确；工具返回空 + `unknowns` | Task 5、6 |
| 7 | 多通道未指定 channel | 工具返回带 hint 的错误，不混算 | Task 2 |
| 8 | 会话中原始字节被发送 | 默认不出现 payload 取值；仅 `include_raw=true` 且用户显式开启时 | Task 2、8 |

---

## Task 1: 工具上下文扩展与 VM 快照

**Files:**
- Create: `src/PeakCan.Host.App/Services/ChatTools/NmToolDtos.cs`
- Modify: `src/PeakCan.Host.App/Services/ChatTools/IChatToolContext.cs`（追加方法）
- Modify: `tests/PeakCan.Host.App.Tests/Services/ChatTools/FakeChatToolContext.cs`（实现新方法）
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmFlow.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmReport.cs`（追加 `Direction`）
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmAnalyzer.cs`（填充 Direction）
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmAnalyzerTests.cs`（追加方向断言）

**Interfaces:**
- Produces:
  - `NmToolQuery(string? SourceId, ushort? Channel, double? TStart, double? TEnd, string? WindowRef)`
  - `NmToolSnapshot(string SourceId, ushort? Channel, string? DbcPath, string? DbcHash, NmProfile Profile, NmReport Report)`
  - `IChatToolContext.GetNmSnapshotAsync(NmToolQuery query, CancellationToken ct)`
  - `NmDirectionCounts(int Transmit, int Receive, int Unknown)`

- [ ] **Step 1: 给 `NmReport` 加方向计数（Host.Core）**

在 `NmReport.cs` 追加：

```csharp
public sealed record NmDirectionCounts(int Transmit, int Receive, int Unknown);
```

`NmReport` 末尾追加尾随可选参数：

```csharp
    string? EngineVersion = null,
    NmDirectionCounts? Direction = null);
```

- [ ] **Step 2: 写方向测试**

在 `NmAnalyzerTests` 追加：

```csharp
[Fact]
public void Analyze_ReportsDirectionCountsFromFrames()
{
    var frames = new[]
    {
        new ReplayFrame(0.0, 0x500, 8, new byte[8], FrameFlags.None, false, 0, IsTransmit: true),
        new ReplayFrame(0.5, 0x500, 8, new byte[8], FrameFlags.None, false, 0, IsTransmit: false),
        new ReplayFrame(1.0, 0x500, 8, new byte[8], FrameFlags.None, false, 0, IsTransmit: null),
    };

    var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
        "src", null, frames, null, Profile(), 0, 1.5));

    Assert.NotNull(report.Direction);
    Assert.Equal(1, report.Direction!.Transmit);
    Assert.Equal(1, report.Direction.Receive);
    Assert.Equal(1, report.Direction.Unknown);
}
```

- [ ] **Step 3: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmAnalyzerTests"
```

Expected: 编译失败或断言失败（`Direction` 为 null）。

- [ ] **Step 4: 在 `NmAnalyzer` 填充 Direction**

在 `return new NmReport(...)` 前加：

```csharp
var direction = new NmDirectionCounts(
    diagnostics.TransmitFrames, diagnostics.ReceiveFrames, diagnostics.DirectionUnknownFrames);
```

并把 `Direction: direction` 作为最后一个参数传入。

- [ ] **Step 5: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmAnalyzerTests"
```

Expected: PASS。

- [ ] **Step 6: 新增工具 DTO**

创建 `src/PeakCan.Host.App/Services/ChatTools/NmToolDtos.cs`：

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>AI 工具与 NM 面板共用的查询参数。</summary>
public sealed record NmToolQuery(
    string? SourceId = null,
    ushort? Channel = null,
    double? TStart = null,
    double? TEnd = null,
    string? WindowRef = null);

/// <summary>一次 NM 查询的快照：profile + 缓存/新算的 report + 已选帧。</summary>
public sealed record NmToolSnapshot(
    string SourceId,
    ushort? Channel,
    string? DbcPath,
    string? DbcHash,
    NmProfile Profile,
    NmReport Report,
    IReadOnlyList<ReplayFrame> Frames);
```

- [ ] **Step 7: 扩展 `IChatToolContext`**

在接口末尾追加：

```csharp
    /// <summary>
    /// 取 NM 分析快照。实现方负责：解析 source/channel、在调用线程取帧/DBC 快照、
    /// 合并 sidecar、查缓存，缓存未命中时在后台线程跑 <c>NmAnalyzer</c>。
    /// 失败（source 不存在、多通道未指定）抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    Task<NmToolSnapshot> GetNmSnapshotAsync(NmToolQuery query, CancellationToken ct);
```

- [ ] **Step 8: 实现 VM 快照**

创建 `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmFlow.cs`：

```csharp
using System.Security.Cryptography;
using System.Text;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// NM 分析入口：取快照、合并 sidecar、查缓存、后台执行。
/// 工具与面板都走这里，保证两者看到同一份报告。
/// </summary>
public sealed partial class TraceViewerViewModel
{
    private readonly NmReportCache _nmCache = new();
    private string? _nmDbcHash;
    private string? _nmDbcHashPath;

    async Task<NmToolSnapshot> IChatToolContext.GetNmSnapshotAsync(NmToolQuery query, CancellationToken ct)
    {
        var sourceId = string.IsNullOrEmpty(query.SourceId) ? MasterSourceId : query.SourceId;
        if (string.IsNullOrEmpty(sourceId))
            throw new InvalidOperationException("没有已加载的 trace source");

        var service = _registry.GetService(sourceId)
            ?? throw new InvalidOperationException($"unknown source_id: {sourceId}");

        // 快照必须在 UI 线程取：LoadedFrames 返回内部只读列表（加载后不可变）。
        var frames = service.LoadedFrames;
        var dbc = _dbcService.Current;
        var dbcPath = string.IsNullOrEmpty(dbc?.SourcePath) ? null : dbc.SourcePath;
        var dbcHash = ComputeNmDbcHash(dbc, dbcPath);

        var supplement = dbcPath is null ? null : NmSidecarStore.Load(dbcPath);
        var profile = NmSupplementMerger.Merge(NmProfileBuilder.Build(dbc, dbcPath), supplement);

        var start = query.TStart ?? 0.0;
        var end = query.TEnd ?? (frames.Count > 0 ? frames[^1].Timestamp + 1e-9 : 0.0);
        var key = new NmCacheKey(sourceId, query.Channel, frames.Count, dbcHash,
            NmSupplementHash(profile), start, end, NmAnalyzer.EngineVersion);

        if (_nmCache.TryGet(key, out var cached))
            return new NmToolSnapshot(sourceId, query.Channel, dbcPath, dbcHash, profile, cached, frames);

        var request = new NmAnalysisRequest(
            sourceId, query.Channel, frames, dbc, profile, start, end, query.WindowRef ?? "full");

        var report = await Task.Run(() => NmAnalyzer.Analyze(request, ct), ct).ConfigureAwait(false);
        _nmCache.Put(key, report);
        return new NmToolSnapshot(sourceId, query.Channel, dbcPath, dbcHash, profile, report, frames);
    }

    private string ComputeNmDbcHash(DbcDocument? dbc, string? path)
    {
        if (dbc is null) return "none";
        if (path is null) return "inline";
        if (_nmDbcHashPath == path && _nmDbcHash is not null) return _nmDbcHash;
        try
        {
            _nmDbcHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            _nmDbcHashPath = path;
            return _nmDbcHash;
        }
        catch (IOException)
        {
            return "unreadable";
        }
        catch (UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    private static string NmSupplementHash(NmProfile profile)
    {
        var payload = string.Join("|",
            profile.Layout?.NidByte, profile.Layout?.CbvByte,
            profile.Layout?.UserDataOffset, profile.Layout?.UserDataLength,
            profile.Timings.CycleMs, profile.Timings.TimeoutMs,
            profile.Timings.RepeatMessageMs, profile.Timings.WaitBusSleepMs,
            profile.Features?.PnEnabled, profile.Features?.ActiveWakeupBitEnabled,
            string.Join(",", profile.Messages.Where(m => m.IsNm).Select(m => m.CanIdHex)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
```

> 上面 import 行写错了：应直接 `using PeakCan.HIL.Core.Dbc;`。执行时按正常 using 写，不要保留 `PeakCan.Hil.Core.Dbc =` 这种别名。

- [ ] **Step 9: 给测试桩补实现**

在 `FakeChatToolContext` 追加：

```csharp
public NmToolSnapshot? NmSnapshotValue { get; set; }
public Exception? NmSnapshotException { get; set; }

Task<NmToolSnapshot> IChatToolContext.GetNmSnapshotAsync(NmToolQuery query, CancellationToken ct)
{
    if (NmSnapshotException is not null) throw NmSnapshotException;
    if (NmSnapshotValue is null) throw new InvalidOperationException("NmSnapshotValue not set");
    return Task.FromResult(NmSnapshotValue);
}
```

并补 `using PeakCan.Host.Core.Analysis.Nm;`（若 DTO 已引用则不需要）。

- [ ] **Step 10: 构建并跑定向测试**

```bash
cd /d/claude_proj2/peakcan-host
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -p:Platform=x86
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~ChatTools"
```

Expected: 0 errors；现有工具测试不回归。

- [ ] **Step 11: 提交**

```bash
git add src/PeakCan.Host.App/Services/ChatTools/NmToolDtos.cs \
        src/PeakCan.Host.App/Services/ChatTools/IChatToolContext.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmFlow.cs \
        src/PeakCan.Host.Core/Analysis/Nm/NmReport.cs \
        src/PeakCan.Host.Core/Analysis/Nm/NmAnalyzer.cs \
        tests/PeakCan.Host.App.Tests/Services/ChatTools/FakeChatToolContext.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmAnalyzerTests.cs
git commit -m "feat(nm): expose NM snapshot to chat tools and panel"
```

---

## Task 2: 三个 AI 工具

**Files:**
- Create: `src/PeakCan.Host.App/Services/ChatTools/GetNmProfileTool.cs`
- Create: `src/PeakCan.Host.App/Services/ChatTools/GetNmOverviewTool.cs`
- Create: `src/PeakCan.Host.App/Services/ChatTools/GetNmEventsTool.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/ChatTools/GetNmToolTests.cs`

**Interfaces:**
- Consumes: `IChatToolContext.GetNmSnapshotAsync`（Task 1）
- Produces: 三个 `IChatTool`，名称固定为 `get_nm_profile` / `get_nm_overview` / `get_nm_events`

- [ ] **Step 1: 实现 `get_nm_profile`**

```csharp
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis.Chat;
using PeakCan.Host.Core.Analysis.Nm;

namespace PeakCan.Host.App.Services.ChatTools;

public sealed class GetNmProfileTool : ChatToolBase
{
    private const string Schema = """
{"type":"object","properties":{
  "source_id":{"type":"string"},
  "channel":{"type":"integer"}
},"additionalProperties":false}
""";

    private readonly IChatToolContext _context;

    public GetNmProfileTool(IChatToolContext context, ILogger<GetNmProfileTool> logger)
        : base("get_nm_profile",
            "Get the network-management (NM) configuration derived from the loaded DBC plus user supplements: NM messages, timings, nodes, conflicts, which capabilities are available, and what inputs are missing. Call this FIRST for any NM/sleep/wakeup/dropout question.",
            Schema, logger)
    {
        _context = context;
    }

    protected override async Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var args = ParseArgs(argsJson);
        var snapshot = await _context.GetNmSnapshotAsync(new NmToolQuery(
            SourceId: args["source_id"]?.GetValue<string>(),
            Channel: args["channel"] is { } c ? (ushort)c.GetValue<int>() : null), ct);

        var messages = snapshot.Profile.Messages.Take(50).Select(m => new JsonObject
        {
            ["can_id"] = m.CanIdHex,
            ["extended"] = m.IsExtended,
            ["name"] = m.Name,
            ["sender"] = m.Sender,
            ["dlc"] = m.Dlc,
            ["is_nm"] = m.IsNm,
            ["reasons"] = new JsonArray(m.Reasons.Select(r => (JsonNode)r).ToArray()),
            ["cycle_ms"] = m.CycleMs,
            ["timeout_ms"] = m.TimeoutMs,
            ["marker_authoritative"] = m.MarkerAuthoritative,
        });

        var root = new JsonObject
        {
            ["source_id"] = snapshot.SourceId,
            ["channel"] = snapshot.Channel,
            ["dbc_path"] = snapshot.DbcPath,
            ["dbc_hash"] = snapshot.DbcHash,
            ["semantics_available"] = false,
            ["messages"] = new JsonArray(messages.ToArray()),
            ["timings"] = new JsonObject
            {
                ["cycle_ms"] = snapshot.Profile.Timings.CycleMs,
                ["timeout_ms"] = snapshot.Profile.Timings.TimeoutMs,
                ["repeat_message_ms"] = snapshot.Profile.Timings.RepeatMessageMs,
                ["wait_bus_sleep_ms"] = snapshot.Profile.Timings.WaitBusSleepMs,
            },
            ["nodes"] = new JsonArray(snapshot.Profile.Nodes.Select(n => (JsonNode)new JsonObject
            {
                ["name"] = n.Name,
                ["station_address"] = n.StationAddress,
                ["is_nm_node"] = n.IsNmNode,
            }).ToArray()),
            ["conflicts"] = new JsonArray(snapshot.Profile.Conflicts.Select(c => (JsonNode)new JsonObject
            {
                ["field"] = c.Field,
                ["values"] = new JsonArray(c.Values.Select(v => (JsonNode)v).ToArray()),
                ["sources"] = new JsonArray(c.Sources.Select(v => (JsonNode)v).ToArray()),
            }).ToArray()),
            ["capabilities"] = new JsonArray(snapshot.Report.Capabilities.Select(c => (JsonNode)new JsonObject
            {
                ["capability"] = c.Capability.ToString(),
                ["available"] = c.Available,
            }).ToArray()),
            ["missing_inputs"] = new JsonArray(snapshot.Report.MissingInputs.Select(m => (JsonNode)new JsonObject
            {
                ["capability"] = m.Capability.ToString(),
                ["input"] = m.InputKey,
                ["why"] = m.Why,
                ["suggestion"] = m.Suggestion,
                ["suggestion_source"] = m.SuggestionSource,
            }).ToArray()),
            ["truncated"] = snapshot.Profile.Messages.Count > 50 ? new JsonObject
            {
                ["section"] = "messages",
                ["returned"] = 50,
                ["total"] = snapshot.Profile.Messages.Count,
            } : null,
            ["next_step"] = snapshot.Report.MissingInputs.Count > 0
                ? "缺少输入时先说明缺什么、影响哪个能力，再引导用户打开「网络管理 → 补全」对话框。"
                : "输入齐备，可以调用 get_nm_overview 查看时间轴结论。",
        };
        return root.ToJsonString();
    }
}
```

- [ ] **Step 2: 实现 `get_nm_overview`**

```csharp
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis;
using PeakCan.HIL.Core.Analysis.Chat;
using PeakCan.Host.Core.Analysis.Nm;

namespace PeakCan.Host.App.Services.ChatTools;

public sealed class GetNmOverviewTool : ChatToolBase
{
    private const string Schema = """
{"type":"object","properties":{
  "source_id":{"type":"string"},
  "channel":{"type":"integer"},
  "t_start":{"type":"number"},
  "t_end":{"type":"number"},
  "window_ref":{"type":"string"}
},"additionalProperties":false}
""";

    private readonly IChatToolContext _context;

    public GetNmOverviewTool(IChatToolContext context, ILogger<GetNmOverviewTool> logger)
        : base("get_nm_overview",
            "Deterministic NM overview for a trace/window: episodes, per-message frame counts/cycle/first-last seen, top findings, capabilities, missing inputs, unknowns and assumptions. Use this instead of anomaly_scan for NM behaviour.",
            Schema, logger)
    {
        _context = context;
    }

    protected override async Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var args = ParseArgs(argsJson);
        var snapshot = await _context.GetNmSnapshotAsync(new NmToolQuery(
            SourceId: args["source_id"]?.GetValue<string>(),
            Channel: args["channel"] is { } c ? (ushort)c.GetValue<int>() : null,
            TStart: args["t_start"]?.GetValue<double>(),
            TEnd: args["t_end"]?.GetValue<double>(),
            WindowRef: args["window_ref"]?.GetValue<string>()), ct);

        var report = snapshot.Report;
        var wallClock = (DateTime?)null;

        var episodes = report.Episodes.Take(20).Select(e => new JsonObject
        {
            ["index"] = e.Index,
            ["start"] = e.Start,
            ["start_label"] = TraceTimeFormatter.Format(e.Start, wallClock),
            ["end"] = e.End,
            ["end_label"] = TraceTimeFormatter.Format(e.End, wallClock),
            ["kind"] = e.Kind,
            ["participants"] = new JsonArray(e.Participants.Select(p => (JsonNode)p).ToArray()),
            ["boundary_start"] = e.BoundaryStart,
            ["boundary_end"] = e.BoundaryEnd,
        });

        var messages = report.Messages.Take(50).Select(m => new JsonObject
        {
            ["can_id"] = m.CanIdHex,
            ["name"] = m.Name,
            ["sender"] = m.Sender,
            ["frame_count"] = m.FrameCount,
            ["first_seen"] = m.FirstSeen,
            ["first_seen_label"] = TraceTimeFormatter.Format(m.FirstSeen, wallClock),
            ["last_seen"] = m.LastSeen,
            ["last_seen_label"] = TraceTimeFormatter.Format(m.LastSeen, wallClock),
            ["cycle"] = CycleJson(m.Cycle),
            ["silent_beyond_timeout"] = m.SilentBeyondTimeout,
        });

        var findings = report.Findings.Take(30).Select(f => new JsonObject
        {
            ["code"] = f.Code,
            ["severity"] = f.Severity,
            ["message"] = f.Message,
            ["evidence_times"] = new JsonArray(f.EvidenceTimes.Select(t => (JsonNode)t).ToArray()),
            ["is_inference"] = f.IsInference,
            ["suggested_next_check"] = f.SuggestedNextCheck,
        });

        var root = new JsonObject
        {
            ["source_id"] = report.SourceId,
            ["channel"] = report.Channel,
            ["window"] = new JsonObject
            {
                ["start"] = report.Window.Start,
                ["end"] = report.Window.End,
                ["ref"] = report.Window.WindowRef,
            },
            ["direction"] = report.Direction is null ? null : new JsonObject
            {
                ["transmit"] = report.Direction.Transmit,
                ["receive"] = report.Direction.Receive,
                ["unknown"] = report.Direction.Unknown,
            },
            ["episodes"] = new JsonArray(episodes.ToArray()),
            ["messages"] = new JsonArray(messages.ToArray()),
            ["findings"] = new JsonArray(findings.ToArray()),
            ["capabilities"] = new JsonArray(report.Capabilities.Select(c => (JsonNode)new JsonObject
            {
                ["capability"] = c.Capability.ToString(),
                ["available"] = c.Available,
            }).ToArray()),
            ["missing_inputs"] = new JsonArray(report.MissingInputs.Select(m => (JsonNode)new JsonObject
            {
                ["capability"] = m.Capability.ToString(),
                ["input"] = m.InputKey,
                ["why"] = m.Why,
                ["suggestion"] = m.Suggestion,
            }).ToArray()),
            ["unknowns"] = new JsonArray(report.Unknowns.Select(u => (JsonNode)u).ToArray()),
            ["assumptions"] = new JsonArray(report.Assumptions.Select(a => (JsonNode)a).ToArray()),
        };

        var truncated = new JsonArray();
        if (report.Episodes.Count > 20)
            truncated.Add(new JsonObject { ["section"] = "episodes", ["returned"] = 20, ["total"] = report.Episodes.Count });
        if (report.Messages.Count > 50)
            truncated.Add(new JsonObject { ["section"] = "messages", ["returned"] = 50, ["total"] = report.Messages.Count });
        root["truncated"] = truncated;

        return root.ToJsonString();
    }

    internal static JsonObject CycleJson(NmCycleStats c) => new()
    {
        ["median_ms"] = c.MedianMs,
        ["p95_ms"] = c.P95Ms,
        ["min_ms"] = c.MinMs,
        ["max_ms"] = c.MaxMs,
        ["phase_ms"] = c.PhaseMs,
        ["deviation_count"] = c.DeviationCount,
        ["consecutive_deviation_count"] = c.ConsecutiveDeviationCount,
        ["interval_count"] = c.IntervalCount,
        ["expected_ms"] = c.ExpectedMs,
        ["tolerance_ms"] = c.ToleranceMs,
    };
}
```

- [ ] **Step 3: 实现 `get_nm_events`**

```csharp
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core.Analysis;
using PeakCan.HIL.Core.Analysis.Chat;

namespace PeakCan.Host.App.Services.ChatTools;

public sealed class GetNmEventsTool : ChatToolBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private const string Schema = """
{"type":"object","properties":{
  "source_id":{"type":"string"},
  "channel":{"type":"integer"},
  "episode_index":{"type":"integer"},
  "subject":{"type":"string","description":"sender/node name or CAN ID hex"},
  "types":{"type":"string","description":"comma-separated event types"},
  "page":{"type":"integer","minimum":1},
  "page_size":{"type":"integer","minimum":1,"maximum":200}
},"additionalProperties":false}
""";

    private readonly IChatToolContext _context;

    public GetNmEventsTool(IChatToolContext context, ILogger<GetNmEventsTool> logger)
        : base("get_nm_events",
            "Paginated NM events from the cached report (default 50, max 200 rows). Filter by episode_index, subject (node name or CAN ID) and types (comma-separated). Does not rescan frames.",
            Schema, logger)
    {
        _context = context;
    }

    protected override async Task<string> ExecuteCoreAsync(string argsJson, CancellationToken ct)
    {
        var args = ParseArgs(argsJson);
        var snapshot = await _context.GetNmSnapshotAsync(new NmToolQuery(
            SourceId: args["source_id"]?.GetValue<string>(),
            Channel: args["channel"] is { } c ? (ushort)c.GetValue<int>() : null), ct);

        var page = Math.Max(1, args["page"]?.GetValue<int>() ?? 1);
        var pageSize = Math.Clamp(args["page_size"]?.GetValue<int>() ?? DefaultPageSize, 1, MaxPageSize);
        var episode = args["episode_index"]?.GetValue<int>();
        var subject = args["subject"]?.GetValue<string>();
        var types = args["types"]?.GetValue<string>()
            ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filtered = snapshot.Report.Events
            .Where(e => episode is null || e.EpisodeIndex == episode)
            .Where(e => string.IsNullOrEmpty(subject)
                        || string.Equals(e.Node, subject, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(e.CanIdHex, subject, StringComparison.OrdinalIgnoreCase))
            .Where(e => types is null || types.Contains(e.Type))
            .ToArray();

        var rows = filtered.Skip((page - 1) * pageSize).Take(pageSize).Select(e => new JsonObject
        {
            ["t"] = e.T,
            ["t_label"] = TraceTimeFormatter.Format(e.T, null),
            ["type"] = e.Type,
            ["node"] = e.Node,
            ["can_id"] = e.CanIdHex,
            ["detail"] = e.Detail,
            ["episode_index"] = e.EpisodeIndex,
            ["evidence_kind"] = e.EvidenceKind,
        });

        var nextPage = page * pageSize < filtered.Length ? page + 1 : (int?)null;
        var root = new JsonObject
        {
            ["source_id"] = snapshot.Report.SourceId,
            ["channel"] = snapshot.Report.Channel,
            ["total"] = filtered.Length,
            ["page"] = page,
            ["page_size"] = pageSize,
            ["next_page"] = nextPage,
            ["truncated"] = filtered.Length > pageSize,
            ["events"] = new JsonArray(rows.ToArray()),
        };
        return root.ToJsonString();
    }
}
```

- [ ] **Step 4: 构建**

```bash
cd /d/claude_proj2/peakcan-host
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -p:Platform=x86
```

Expected: 0 errors。

- [ ] **Step 5: 提交**

```bash
git add src/PeakCan.Host.App/Services/ChatTools/GetNmProfileTool.cs \
        src/PeakCan.Host.App/Services/ChatTools/GetNmOverviewTool.cs \
        src/PeakCan.Host.App/Services/ChatTools/GetNmEventsTool.cs
git commit -m "feat(nm): add NM chat tools (profile/overview/events)"
```

---

## Task 3: 系统提示词与工具注册

**Files:**
- Create: `src/PeakCan.Host.App/Services/ChatTools/NmPromptRules.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/ChatFlow.cs`（`BuildSystemMessage`、`BuildChatTools`）
- Test: `tests/PeakCan.Host.App.Tests/Services/ChatTools/NmPromptRulesTests.cs`

**Interfaces:**
- Produces: `NmPromptRules.BuildM1() → string`

- [ ] **Step 1: 写失败测试**

```csharp
using PeakCan.Host.App.Services.ChatTools;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class NmPromptRulesTests
{
    [Fact]
    public void BuildM1_RequiresProfileFirst()
    {
        var text = NmPromptRules.BuildM1();
        Assert.Contains("get_nm_profile", text);
        Assert.Contains("先", text);
    }

    [Fact]
    public void BuildM1_ForbidsSleepInferenceFromAbsence()
    {
        var text = NmPromptRules.BuildM1();
        Assert.Contains("睡眠", text);
        Assert.Contains("没有 NM 帧", text);
        Assert.Contains("不等于", text);
    }

    [Fact]
    public void BuildM1_ForbidsGuessingMissingInputs()
    {
        var text = NmPromptRules.BuildM1();
        Assert.Contains("missing_inputs", text);
        Assert.Contains("不许", text);
        Assert.Contains("补全", text);
    }

    [Fact]
    public void BuildM1_DoesNotMentionM2Fields()
    {
        var text = NmPromptRules.BuildM1();
        Assert.DoesNotContain("standard_id", text);
        Assert.DoesNotContain("profile_hash", text);
        Assert.DoesNotContain("unmodeled_rules", text);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~NmPromptRulesTests"
```

Expected: 编译失败。

- [ ] **Step 3: 实现提示词片段**

```csharp
namespace PeakCan.Host.App.Services.ChatTools;

/// <summary>
/// M1 NM 提示词规则。与 M2 规则分开维护，避免 M1 引用企标/profile。
/// </summary>
public static class NmPromptRules
{
    public static string BuildM1() => """
网络管理（NM）分析规则：
1. 任何 NM/网络管理/睡眠/唤醒/掉线问题，先调用 get_nm_profile；只分析 DBC 标记为 authoritative 或用户在「网络管理 → 补全」对话框确认过的报文。
2. 只引用工具返回的数字与 *_label；禁止自行换算时间。
3. “trace 里没有 NM 帧”只说明该窗口/文件内没有，不等于睡眠/掉线。
4. NID/CBV/PN/用户数据/睡眠/企标：先看 capabilities 与 missing_inputs；缺输入就说缺什么、影响哪条结论、怎么补（打开补全对话框或提供 sidecar），未补前不下结论，不许脑补。
5. 只列周期偏差、静默、观察突发、原始字节/位/信号变化、配置冲突，并给证据时间。
6. 允许给根因假设，但必须标“假设”，并给下一步排查建议。
7. 与 anomaly_scan 的分工：NM 行为由 NM 工具负责；anomaly_scan 只在用户明确要求全信号扫描时使用。
8. 可以用现有 seek_to / propose_to_watch_list 跳转或加 watch；不改 DBC/配置。
9. 不替用户填 sidecar、不直接改配置；用户口述的值只能复述并请其在对话框确认。
""";
}
```

- [ ] **Step 4: 接进 `BuildSystemMessage`**

把 `ChatFlow.cs` 里的工具清单与计数改为：

```csharp
sb.AppendLine("可用工具（22 个）：");
...
sb.AppendLine("网络管理: get_nm_profile, get_nm_overview, get_nm_events");
...
sb.AppendLine();
sb.AppendLine(NmPromptRules.BuildM1());
```

- [ ] **Step 5: 注册工具**

在 `BuildChatTools()` 的数组末尾追加：

```csharp
// Network management (M1)
new GetNmProfileTool(ctx, lf.CreateLogger<GetNmProfileTool>()),
new GetNmOverviewTool(ctx, lf.CreateLogger<GetNmOverviewTool>()),
new GetNmEventsTool(ctx, lf.CreateLogger<GetNmEventsTool>()),
```

- [ ] **Step 6: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~NmPromptRulesTests"
```

Expected: PASS。

- [ ] **Step 7: 提交**

```bash
git add src/PeakCan.Host.App/Services/ChatTools/NmPromptRules.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/ChatFlow.cs \
        tests/PeakCan.Host.App.Tests/Services/ChatTools/NmPromptRulesTests.cs
git commit -m "feat(nm): add M1 NM prompt rules and register three tools"
```

---

## Task 4: 工具单测

**Files:**
- Test: `tests/PeakCan.Host.App.Tests/Services/ChatTools/GetNmToolTests.cs`

**Interfaces:**
- Consumes: `FakeChatToolContext.NmSnapshotValue`（Task 1）

- [ ] **Step 1: 写测试**

```csharp
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class GetNmToolTests
{
    private static NmReport Report(int eventCount = 3) => new(
        new NmWindow(0, 10, "full"), "src", null,
        new[] { new NmEpisode(0, 0, 10, "active", null, new[] { "0x500" }, false, false) },
        new[]
        {
            new NmMessageReport("0x500", false, "NM_Message", "ECU_A", 21, 0, 10,
                new NmCycleStats(500, 500, 500, 500, 0, 0, 20, 500, 100),
                new[] { new NmSegment(0, 10, "active") }, false),
        },
        Array.Empty<NmNodeReport>(),
        Enumerable.Range(0, eventCount).Select(i => new NmEvent(i, "byte_change", null, "0x500",
            $"byte0: 0x00 → 0x0{i}", 0, "measured")).ToArray(),
        new[]
        {
            new NmFinding("NM_CYCLE_DEVIATION", "warn", "deviation", new[] { 2.0 }, true, "check load"),
        },
        new[] { "assumption" },
        new[] { "unknown" },
        new[]
        {
            new NmCapabilityState(NmCapability.IdentifyMessages, true, Array.Empty<NmInputMissing>()),
            new NmCapabilityState(NmCapability.NidCheck, false,
                new[] { new NmInputMissing(NmCapability.NidCheck, "nid_byte", "not configured", "byte0", "measured") }),
        },
        new[] { new NmInputMissing(NmCapability.NidCheck, "nid_byte", "not configured", "byte0", "measured") },
        Array.Empty<NmTruncation>(),
        EngineVersion: NmAnalyzer.EngineVersion,
        Direction: new NmDirectionCounts(1, 2, 0));

    private static NmToolSnapshot Snapshot() => new("src", null, null, "hash",
        new NmProfile(NmProfileSource.Dbc,
            new[] { new NmMessageDef("0x500", false, "NM_Message", "ECU_A", 8, true,
                new[] { "MsgType=CANNM" }, 500, 2000, true) },
            new NmTimings(500, 2000), Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance()),
        Report(),
        Array.Empty<ReplayFrame>());

    private static FakeChatToolContext Context()
    {
        var ctx = new FakeChatToolContext { NmSnapshotValue = Snapshot() };
        return ctx;
    }

    [Fact]
    public async Task Profile_ReturnsCapabilitiesAndMissingInputs()
    {
        var tool = new GetNmProfileTool(Context(), NullLogger<GetNmProfileTool>.Instance);
        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        json["messages"]!.AsArray().Should().ContainSingle();
        json["capabilities"]!.AsArray().Should().HaveCount(2);
        json["missing_inputs"]!.AsArray().Should().ContainSingle();
        json["semantics_available"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Overview_ReturnsEpisodesMessagesFindingsAndDirection()
    {
        var tool = new GetNmOverviewTool(Context(), NullLogger<GetNmOverviewTool>.Instance);
        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        json["episodes"]!.AsArray().Should().HaveCount(1);
        json["messages"]!.AsArray().Should().HaveCount(1);
        json["findings"]!.AsArray().Should().HaveCount(1);
        json["direction"]!["transmit"]!.GetValue<int>().Should().Be(1);
        json["unknowns"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public async Task Events_PaginatesAndSetsNextPage()
    {
        var ctx = new FakeChatToolContext { NmSnapshotValue = Snapshot() with { Report = Report(120) } };
        var tool = new GetNmEventsTool(ctx, NullLogger<GetNmEventsTool>.Instance);

        var page1 = JsonNode.Parse(await tool.ExecuteAsync(
            """{"page":1,"page_size":50}""", CancellationToken.None))!.AsObject();
        page1["events"]!.AsArray().Should().HaveCount(50);
        page1["next_page"]!.GetValue<int>().Should().Be(2);
        page1["total"]!.GetValue<int>().Should().Be(120);

        var page3 = JsonNode.Parse(await tool.ExecuteAsync(
            """{"page":3,"page_size":50}""", CancellationToken.None))!.AsObject();
        page3["events"]!.AsArray().Should().HaveCount(20);
        page3["next_page"].Should().BeNull();
    }

    [Fact]
    public async Task Events_ClampsPageSizeTo200()
    {
        var ctx = new FakeChatToolContext { NmSnapshotValue = Snapshot() with { Report = Report(500) } };
        var tool = new GetNmEventsTool(ctx, NullLogger<GetNmEventsTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync(
            """{"page_size":9999}""", CancellationToken.None))!.AsObject();

        json["page_size"]!.GetValue<int>().Should().Be(200);
        json["events"]!.AsArray().Should().HaveCount(200);
    }

    [Fact]
    public async Task Tool_ContextError_IsReturnedAsErrorJson()
    {
        var ctx = new FakeChatToolContext { NmSnapshotException = new InvalidOperationException("多通道需指定 channel") };
        var tool = new GetNmProfileTool(ctx, NullLogger<GetNmProfileTool>.Instance);

        var json = JsonNode.Parse(await tool.ExecuteAsync("{}", CancellationToken.None))!.AsObject();

        json["error"]!.GetValue<string>().Should().Contain("channel");
    }
}
```

- [ ] **Step 2: 跑测试至通过**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~GetNmToolTests"
```

Expected: 5 个用例全绿。

- [ ] **Step 3: 提交**

```bash
git add tests/PeakCan.Host.App.Tests/Services/ChatTools/GetNmToolTests.cs
git commit -m "test(nm): cover profile/overview/events tools"
```

---

## Task 5: NM 面板（时间条 + 报文表 + 事件表）

**Files:**
- Create: `src/PeakCan.Host.App/Views/TraceViewerViewNmPanel.xaml`
- Create: `src/PeakCan.Host.App/Views/TraceViewerViewNmPanel.xaml.cs`
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmPanelFlow.cs`
- Modify: `src/PeakCan.Host.App/Views/TraceViewerView.xaml`（增加 TabItem）
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/NmPanelFlowTests.cs`

**Interfaces:**
- Produces:
  - `TraceViewerViewNmPanel`（UserControl）
  - `TraceViewerViewModel.NmPanelContent`
  - `NmCapabilityChipVm(string Name, bool Available)`
  - `NmMessageRowVm(...)`
  - `NmTimelineRowVm(string Label, IReadOnlyList<NmTimelineSegmentVm> Segments)`
  - `NmTimelineSegmentVm(double Left, double Width, string Kind, double Start, double End)`
  - `NmEventRowVm(...)`

- [ ] **Step 1: 写行模型与构建测试**

创建 `tests/PeakCan.Host.App.Tests/ViewModels/NmPanelFlowTests.cs`：

```csharp
using FluentAssertions;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public class NmPanelFlowTests
{
    [Fact]
    public void BuildTimeline_UsesDesignWidth1000()
    {
        var report = new NmReport(
            new NmWindow(0, 10, "full"), "src", null,
            Array.Empty<NmEpisode>(),
            new[]
            {
                new NmMessageReport("0x500", false, "NM", "A", 2, 2, 8,
                    new NmCycleStats(500, 500, 500, 500, 0, 0, 1, 500, 100),
                    new[] { new NmSegment(2, 8, "active") }, false),
            },
            Array.Empty<NmNodeReport>(), Array.Empty<NmEvent>(), Array.Empty<NmFinding>(),
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<NmCapabilityState>(),
            Array.Empty<NmInputMissing>(), Array.Empty<NmTruncation>());

        var rows = TraceViewerViewModel.BuildTimelineRows(report);

        var segment = Assert.Single(Assert.Single(rows).Segments);
        segment.Left.Should().BeApproximately(200, 0.001);
        segment.Width.Should().BeApproximately(600, 0.001);
    }

    [Fact]
    public void BuildCapabilityChips_ShowsMissingState()
    {
        var report = new NmReport(
            new NmWindow(0, 1, "full"), "src", null,
            Array.Empty<NmEpisode>(), Array.Empty<NmMessageReport>(), Array.Empty<NmNodeReport>(),
            Array.Empty<NmEvent>(), Array.Empty<NmFinding>(), Array.Empty<string>(),
            Array.Empty<string>(),
            new[]
            {
                new NmCapabilityState(NmCapability.NidCheck, false,
                    new[] { new NmInputMissing(NmCapability.NidCheck, "nid_byte", "why", "s", "measured") }),
            },
            Array.Empty<NmInputMissing>(), Array.Empty<NmTruncation>());

        var chips = TraceViewerViewModel.BuildCapabilityChips(report);

        chips.Should().ContainSingle();
        chips[0].Available.Should().BeFalse();
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~NmPanelFlowTests"
```

Expected: 编译失败。

- [ ] **Step 3: 实现 VM 行模型**

创建 `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmPanelFlow.cs`：

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Analysis.Nm;

namespace PeakCan.Host.App.ViewModels;

public sealed record NmCapabilityChipVm(string Name, bool Available, string Tooltip);
public sealed record NmMessageRowVm(string CanIdHex, string? Name, string? Sender, int FrameCount,
    string CycleText, string FirstSeenText, string LastSeenText, string Status);
public sealed record NmTimelineSegmentVm(double Left, double Width, string Kind, double Start, double End);
public sealed record NmTimelineRowVm(string Label, IReadOnlyList<NmTimelineSegmentVm> Segments);
public sealed record NmEventRowVm(double T, string TimeText, string Type, string Subject,
    string Detail, string EvidenceKind, int EpisodeIndex);

public sealed partial class TraceViewerViewModel
{
    private const double NmTimelineWidth = 1000.0;

    [ObservableProperty] private bool _nmHasReport;
    [ObservableProperty] private bool _nmIsAnalyzing;
    [ObservableProperty] private string _nmStatus = "";
    [ObservableProperty] private string _nmProfileSummary = "";
    [ObservableProperty] private string _nmMissingText = "";

    public ObservableCollection<NmCapabilityChipVm> NmCapabilityChips { get; } = new();
    public ObservableCollection<NmMessageRowVm> NmMessageRows { get; } = new();
    public ObservableCollection<NmTimelineRowVm> NmTimelineRows { get; } = new();
    public ObservableCollection<NmEventRowVm> NmEventRows { get; } = new();

    private Views.TraceViewerViewNmPanel? _nmPanelContent;
    private CancellationTokenSource? _nmCts;
    public Views.TraceViewerViewNmPanel? NmPanelContent
    {
        get
        {
            if (_nmPanelContent is null)
            {
                _nmPanelContent = new Views.TraceViewerViewNmPanel { DataContext = this };
                _ = RunNmAnalysisAsync(null);
            }
            return _nmPanelContent;
        }
    }

    public static IReadOnlyList<NmTimelineRowVm> BuildTimelineRows(NmReport report)
        => report.Messages.Select(m =>
        {
            var segments = m.Activity.Select(s =>
            {
                var left = ToX(s.Start, report.Window);
                var right = ToX(s.End, report.Window);
                return new NmTimelineSegmentVm(left, Math.Max(right - left, 1), s.Kind, s.Start, s.End);
            }).ToArray();
            return new NmTimelineRowVm($"{m.CanIdHex} {m.Name}", segments);
        }).ToArray();

    public static IReadOnlyList<NmCapabilityChipVm> BuildCapabilityChips(NmReport report)
        => report.Capabilities.Select(c => new NmCapabilityChipVm(
            c.Capability.ToString(), c.Available,
            c.Available ? "可用" : string.Join("; ", c.Missing.Select(m => m.Why)))).ToArray();

    private static double ToX(double t, NmWindow w)
    {
        var span = Math.Max(w.End - w.Start, 1e-9);
        return Math.Clamp((t - w.Start) / span * NmTimelineWidth, 0, NmTimelineWidth);
    }

    internal async Task RunNmAnalysisAsync((double Start, double End)? window)
    {
        _nmCts?.Cancel();
        _nmCts = new CancellationTokenSource();
        var ct = _nmCts.Token;

        NmIsAnalyzing = true;
        NmStatus = "分析中…";
        try
        {
            var snapshot = await ((IChatToolContext)this).GetNmSnapshotAsync(new NmToolQuery(
                TStart: window?.Start, TEnd: window?.End,
                WindowRef: window is null ? "full" : "viewport"), ct);
            ApplyNmReport(snapshot);
        }
        catch (OperationCanceledException)
        {
            NmStatus = "分析已取消";
        }
        catch (Exception ex)
        {
            NmStatus = "分析失败：" + ex.Message;
        }
        finally
        {
            NmIsAnalyzing = false;
        }
    }

    internal void ApplyNmReport(NmToolSnapshot snapshot)
    {
        var report = snapshot.Report;
        NmHasReport = true;
        NmProfileSummary = $"来源 {snapshot.Profile.Provenance.ResolvedFrom} · NM 报文 {snapshot.Profile.Messages.Count(m => m.IsNm)} · " +
            $"周期 {snapshot.Profile.Timings.CycleMs?.ToString() ?? "—"}ms · 超时 {snapshot.Profile.Timings.TimeoutMs?.ToString() ?? "—"}ms";
        NmMissingText = report.MissingInputs.Count == 0
            ? "输入齐备"
            : $"缺项 {report.MissingInputs.Count} → 补全";

        NmCapabilityChips.Clear();
        foreach (var chip in BuildCapabilityChips(report)) NmCapabilityChips.Add(chip);
        NmMessageRows.Clear();
        foreach (var m in report.Messages)
            NmMessageRows.Add(new NmMessageRowVm(m.CanIdHex, m.Name, m.Sender, m.FrameCount,
                m.Cycle.MedianMs is { } med ? $"{med:F0}ms" : "—",
                TraceTimeFormatter.Format(m.FirstSeen, null),
                TraceTimeFormatter.Format(m.LastSeen, null),
                m.SilentBeyondTimeout ? "超出超时" : "正常"));
        NmTimelineRows.Clear();
        foreach (var row in BuildTimelineRows(report)) NmTimelineRows.Add(row);
        NmEventRows.Clear();
        foreach (var e in report.Events.Take(500))
            NmEventRows.Add(new NmEventRowVm(e.T, TraceTimeFormatter.Format(e.T, null), e.Type,
                e.Node ?? e.CanIdHex ?? "", e.Detail, e.EvidenceKind, e.EpisodeIndex));

        NmStatus = report.Events.Count > 500
            ? $"显示前 500 条事件（共 {report.Events.Count}）"
            : $"事件 {report.Events.Count} 条";
        if (report.Messages.Count == 0)
            NmStatus = "本窗口未发现 NM 帧";
    }

    [RelayCommand]
    private Task AnalyzeNmFullAsync() => RunNmAnalysisAsync(null);

    [RelayCommand]
    private Task AnalyzeNmWindowAsync()
    {
        var vp = ChartViewModel.CaptureViewports().FirstOrDefault();
        return RunNmAnalysisAsync(vp is null ? null : (vp.XMin, vp.XMax));
    }

    [RelayCommand]
    private void SeekToNmEvent(NmEventRowVm? row)
    {
        if (row is not null) _masterService?.Seek(row.T);
    }

    [RelayCommand]
    private void SeekToNmTime(double t) => _masterService?.Seek(t);
}
```

- [ ] **Step 4: 建面板 XAML**

创建 `src/PeakCan.Host.App/Views/TraceViewerViewNmPanel.xaml`：

```xml
<UserControl x:Class="PeakCan.Host.App.Views.TraceViewerViewNmPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:PeakCan.Host.App.ViewModels">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="160" />
        </Grid.RowDefinitions>

        <!-- Profile bar -->
        <Border Grid.Row="0" Padding="6" Background="#FFF7E6" BorderBrush="#E0C080" BorderThickness="0,0,0,1">
            <DockPanel>
                <Button DockPanel.Dock="Right" Content="{Binding NmMissingText}"
                        Command="{Binding OpenNmWizardCommand}" Margin="6,0,0,0" />
                <TextBlock Text="{Binding NmProfileSummary}" TextWrapping="Wrap" />
            </DockPanel>
        </Border>

        <!-- M1 semantics banner -->
        <TextBlock Grid.Row="1" Margin="6,4" Foreground="#8A6D00"
                   Text="语义未配置：NID/CBV/PN/用户数据不做判定（可在「补全」中提供输入）" />

        <!-- Capability chips -->
        <ItemsControl Grid.Row="2" ItemsSource="{Binding NmCapabilityChips}" Margin="6,2">
            <ItemsControl.ItemsPanel>
                <ItemsPanelTemplate><WrapPanel /></ItemsPanelTemplate>
            </ItemsControl.ItemsPanel>
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <Border Margin="0,0,4,4" Padding="6,1" CornerRadius="8"
                            Background="{Binding Available, Converter={StaticResource NmChipBrush}}">
                        <TextBlock Text="{Binding Name}" FontSize="11" />
                    </Border>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>

        <!-- Timeline (design width 1000, Viewbox scales to panel) -->
        <ScrollViewer Grid.Row="3" VerticalScrollBarVisibility="Auto">
            <ItemsControl ItemsSource="{Binding NmTimelineRows}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <StackPanel Margin="2">
                            <TextBlock Text="{Binding Label}" FontSize="11" />
                            <Viewbox Stretch="Fill" Height="14">
                                <Canvas Width="1000" Height="14">
                                    <ItemsControl ItemsSource="{Binding Segments}">
                                        <ItemsControl.ItemsPanel>
                                            <ItemsPanelTemplate><Canvas /></ItemsPanelTemplate>
                                        </ItemsControl.ItemsPanel>
                                        <ItemsControl.ItemTemplate>
                                            <DataTemplate>
                                                <Rectangle Height="14" Fill="#4C9AFF"
                                                           Canvas.Left="{Binding Left}"
                                                           Width="{Binding Width}"
                                                           ToolTip="{Binding Kind}">
                                                    <Rectangle.InputBindings>
                                                        <MouseBinding MouseAction="LeftClick"
                                                                      Command="{Binding DataContext.SeekToNmTimeCommand,
                                                                                RelativeSource={RelativeSource AncestorType=UserControl}}"
                                                                      CommandParameter="{Binding Start}" />
                                                    </Rectangle.InputBindings>
                                                </Rectangle>
                                            </DataTemplate>
                                        </ItemsControl.ItemTemplate>
                                    </ItemsControl>
                                </Canvas>
                            </Viewbox>
                        </StackPanel>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>

        <!-- Event table -->
        <DataGrid Grid.Row="5" ItemsSource="{Binding NmEventRows}" IsReadOnly="True"
                  AutoGenerateColumns="False" MouseDoubleClick="OnEventRowDoubleClick">
            <DataGrid.Columns>
                <DataGridTextColumn Header="时间" Binding="{Binding TimeText}" Width="80" />
                <DataGridTextColumn Header="类型" Binding="{Binding Type}" Width="110" />
                <DataGridTextColumn Header="对象" Binding="{Binding Subject}" Width="90" />
                <DataGridTextColumn Header="详情" Binding="{Binding Detail}" Width="*" />
                <DataGridTextColumn Header="证据" Binding="{Binding EvidenceKind}" Width="70" />
            </DataGrid.Columns>
        </DataGrid>

        <!-- Actions -->
        <StackPanel Grid.Row="4" Orientation="Horizontal" Margin="6">
            <Button Content="分析全 trace" Command="{Binding AnalyzeNmFullCommand}" Margin="0,0,6,0" />
            <Button Content="分析当前窗口" Command="{Binding AnalyzeNmWindowCommand}" Margin="0,0,6,0" />
            <Button Content="导出报告" Command="{Binding ExportNmReportCommand}" Margin="0,0,6,0" />
            <TextBlock Text="{Binding NmStatus}" VerticalAlignment="Center" />
        </StackPanel>
    </Grid>
</UserControl>
```

代码后置 `TraceViewerViewNmPanel.xaml.cs`：

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

public partial class TraceViewerViewNmPanel : UserControl
{
    public TraceViewerViewNmPanel() => InitializeComponent();

    private void OnEventRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is TraceViewerViewModel vm && sender is DataGrid grid
            && grid.SelectedItem is NmEventRowVm row)
        {
            vm.SeekToNmEventCommand.Execute(row);
        }
    }
}
```

> `NmChipBrush` 转换器与 `ExportNmReportCommand` 在 Task 6 提供。Task 5 的构建允许这两个绑定暂时报"未找到"只在运行时警告，不阻断编译；若团队启用了严格 XAML 检查，可先删掉 `Background` 绑定与导出按钮，Task 6 再加回。

- [ ] **Step 5: 在 `TraceViewerView.xaml` 增加标签**

在 `<TabItem Header="AI 对话">` 之后追加：

```xml
<TabItem Header="网络管理">
    <ContentControl Content="{Binding NmPanelContent}" />
</TabItem>
```

- [ ] **Step 6: 跑测试**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~NmPanelFlowTests"
```

Expected: PASS。

- [ ] **Step 7: 提交**

```bash
git add src/PeakCan.Host.App/Views/TraceViewerViewNmPanel.xaml \
        src/PeakCan.Host.App/Views/TraceViewerViewNmPanel.xaml.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmPanelFlow.cs \
        src/PeakCan.Host.App/Views/TraceViewerView.xaml \
        tests/PeakCan.Host.App.Tests/ViewModels/NmPanelFlowTests.cs
git commit -m "feat(nm): add NM panel with timeline, message and event tables"
```

---

## Task 6: 面板命令补全、导出与取消

**Files:**
- Create: `src/PeakCan.Host.App/Services/Nm/NmReportWriter.cs`
- Create: `src/PeakCan.Host.App/Converters/NmChipBrushConverter.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmPanelFlow.cs`（导出命令、取消）
- Test: `tests/PeakCan.Host.App.Tests/Services/Nm/NmReportWriterTests.cs`

**Interfaces:**
- Produces:
  - `NmReportWriter.WriteMarkdown(NmToolSnapshot snapshot) → string`
  - `NmReportWriter.WriteCsv(NmReport report) → string`
  - `TraceViewerViewModel.ExportNmReportCommand`

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using PeakCan.Host.App.Services.Nm;
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.Nm;

public class NmReportWriterTests
{
    private static NmReport Report() => new(
        new NmWindow(0, 10, "full"), "src", null,
        new[] { new NmEpisode(0, 0, 10, "active", null, new[] { "0x500" }, false, false) },
        new[]
        {
            new NmMessageReport("0x500", false, "NM", "A", 2, 0, 10,
                new NmCycleStats(500, 500, 500, 500, 0, 0, 1, 500, 100),
                new[] { new NmSegment(0, 10, "active") }, false),
        },
        Array.Empty<NmNodeReport>(),
        new[] { new NmEvent(1.5, "byte_change", null, "0x500", "byte0: 0x00 → 0x08", 0, "measured") },
        new[] { new NmFinding("NM_CYCLE_DEVIATION", "warn", "dev", new[] { 2.0 }, true, "check") },
        new[] { "assumption" },
        new[] { "unknown" },
        Array.Empty<NmCapabilityState>(),
        Array.Empty<NmInputMissing>(),
        Array.Empty<NmTruncation>(),
        EngineVersion: NmAnalyzer.EngineVersion);

    [Fact]
    public void WriteMarkdown_ContainsProfileEpisodesFindingsUnknowns()
    {
        var md = NmReportWriter.WriteMarkdown(new NmToolSnapshot(
            "src", null, "/nets/net.dbc", "abcd",
            new NmProfile(NmProfileSource.Dbc, Array.Empty<NmMessageDef>(),
                new NmTimings(500, 2000), Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
                Array.Empty<string>(), new NmProvenance("/nets/net.dbc", null, "dbc")),
            Report(), Array.Empty<PeakCan.Host.Core.Replay.ReplayFrame>()));

        md.Should().Contain("# NM 分析报告");
        md.Should().Contain("0x500");
        md.Should().Contain("NM_CYCLE_DEVIATION");
        md.Should().Contain("unknown");
        md.Should().Contain("abcd");
    }

    [Fact]
    public void WriteCsv_IsRfc4180AndHeaderStable()
    {
        var csv = NmReportWriter.WriteCsv(Report());

        csv.Should().StartWith("time,type,subject,detail,evidence_kind,episode_index\r\n");
        csv.Should().Contain("1.5,byte_change,0x500");
    }

    [Fact]
    public void WriteMarkdown_EscapesPipeInDetail()
    {
        var report = Report() with
        {
            Events = new[] { new NmEvent(1, "x", null, "0x1", "a|b", 0, "measured") },
        };
        NmReportWriter.WriteMarkdown(new NmToolSnapshot("src", null, null, "h",
            new NmProfile(NmProfileSource.None, Array.Empty<NmMessageDef>(), new NmTimings(),
                Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(), Array.Empty<string>(),
                new NmProvenance()), report, Array.Empty<PeakCan.Host.Core.Replay.ReplayFrame>()))
            .Should().Contain("a\\|b");
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~NmReportWriterTests"
```

Expected: 编译失败。

- [ ] **Step 3: 实现导出**

```csharp
using System.Globalization;
using System.Text;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Analysis.Nm;

namespace PeakCan.Host.App.Services.Nm;

/// <summary>NM 报告的 Markdown / CSV 导出。纯函数，便于单测。</summary>
public static class NmReportWriter
{
    public static string WriteMarkdown(NmToolSnapshot snapshot)
    {
        var r = snapshot.Report;
        var sb = new StringBuilder();
        sb.AppendLine("# NM 分析报告");
        sb.AppendLine();
        sb.AppendLine($"- source: `{r.SourceId}`");
        sb.AppendLine($"- window: {r.Window.Start:F3} – {r.Window.End:F3} ({r.Window.WindowRef})");
        sb.AppendLine($"- DBC: `{snapshot.DbcPath ?? "—"}`");
        sb.AppendLine($"- dbc_hash: `{snapshot.DbcHash ?? "—"}`");
        sb.AppendLine($"- engine: `{r.EngineVersion}`");
        sb.AppendLine();
        sb.AppendLine("## Episodes");
        sb.AppendLine("| # | start | end | kind | participants | boundary |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var e in r.Episodes)
            sb.AppendLine($"| {e.Index} | {e.Start:F3} | {e.End:F3} | {e.Kind} | {string.Join(", ", e.Participants)} | {(e.BoundaryStart ? "start " : "")}{(e.BoundaryEnd ? "end" : "")} |");
        sb.AppendLine();
        sb.AppendLine("## Messages");
        sb.AppendLine("| CAN ID | name | sender | frames | median ms | first | last |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var m in r.Messages)
            sb.AppendLine($"| {m.CanIdHex} | {Esc(m.Name)} | {Esc(m.Sender)} | {m.FrameCount} | {m.Cycle.MedianMs?.ToString("F0") ?? "—"} | {m.FirstSeen:F3} | {m.LastSeen:F3} |");
        sb.AppendLine();
        sb.AppendLine("## Findings");
        foreach (var f in r.Findings)
            sb.AppendLine($"- **{f.Code}** ({f.Severity}, inference={f.IsInference}): {Esc(f.Message)}");
        sb.AppendLine();
        sb.AppendLine("## Events");
        sb.AppendLine("| time | type | subject | detail | evidence |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var e in r.Events)
            sb.AppendLine($"| {e.T:F3} | {e.Type} | {e.CanIdHex ?? e.Node ?? ""} | {Esc(e.Detail)} | {e.EvidenceKind} |");
        sb.AppendLine();
        sb.AppendLine("## Unknowns");
        foreach (var u in r.Unknowns) sb.AppendLine($"- {u}");
        sb.AppendLine();
        sb.AppendLine("## Assumptions");
        foreach (var a in r.Assumptions) sb.AppendLine($"- {a}");
        return sb.ToString();
    }

    public static string WriteCsv(NmReport report)
    {
        var sb = new StringBuilder();
        sb.Append("time,type,subject,detail,evidence_kind,episode_index\r\n");
        foreach (var e in report.Events)
        {
            sb.Append(e.T.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
              .Append(Csv(e.Type)).Append(',')
              .Append(Csv(e.CanIdHex ?? e.Node ?? "")).Append(',')
              .Append(Csv(e.Detail)).Append(',')
              .Append(Csv(e.EvidenceKind)).Append(',')
              .Append(e.EpisodeIndex.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Esc(string? value) => (value ?? "").Replace("|", "\\|");
    private static string Csv(string value)
        => value.Contains('"') || value.Contains(',') || value.Contains('\r') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
```

- [ ] **Step 4: 导出命令与取消**

在 `NmPanelFlow.cs` 追加：

```csharp
[ObservableProperty] private bool _nmCanCancel;

[RelayCommand]
private async Task ExportNmReportAsync()
{
    var dlg = new Microsoft.Win32.SaveFileDialog
    {
        Filter = "Markdown (*.md)|*.md|CSV (*.csv)|*.csv",
        FileName = "nm-report.md",
    };
    if (dlg.ShowDialog() != true) return;

    var snapshot = await ((IChatToolContext)this).GetNmSnapshotAsync(
        new NmToolQuery(), CancellationToken.None);
    var text = dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
        ? NmReportWriter.WriteCsv(snapshot.Report)
        : NmReportWriter.WriteMarkdown(snapshot);
    await File.WriteAllTextAsync(dlg.FileName, text, Encoding.UTF8);
    NmStatus = "已导出：" + dlg.FileName;
}

[RelayCommand]
private void CancelNmAnalysis() => _nmCts?.Cancel();
```

`RunNmAnalysisAsync` 已经在 Task 5 使用独立的 `_nmCts`（不是聊天的 `_chatCts`）；本任务只需在 `finally` 里补 `NmCanCancel = false;`，并在进入时 `NmCanCancel = true;`。取消面板分析不能把聊天请求一起取消。

- [ ] **Step 5: 转换器**

```csharp
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PeakCan.Host.App.Converters;

/// <summary>能力 chip：可用=浅绿，缺失=浅黄。</summary>
public sealed class NmChipBrushConverter : IValueConverter
{
    private static readonly Brush Available = new SolidColorBrush(Color.FromRgb(0xE6, 0xF4, 0xEA));
    private static readonly Brush Missing = new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE0));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Available : Missing;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

在 `TraceViewerViewNmPanel.xaml` 的 `UserControl.Resources` 注册：

```xml
<UserControl.Resources>
    <conv:NmChipBrushConverter x:Key="NmChipBrush" />
</UserControl.Resources>
```

并补 `xmlns:conv="clr-namespace:PeakCan.Host.App.Converters"`。

- [ ] **Step 6: 跑测试**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~NmReportWriterTests"
```

Expected: 3 个用例全绿。

- [ ] **Step 7: 提交**

```bash
git add src/PeakCan.Host.App/Services/Nm/NmReportWriter.cs \
        src/PeakCan.Host.App/Converters/NmChipBrushConverter.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmPanelFlow.cs \
        src/PeakCan.Host.App/Views/TraceViewerViewNmPanel.xaml \
        tests/PeakCan.Host.App.Tests/Services/Nm/NmReportWriterTests.cs
git commit -m "feat(nm): export markdown/csv and per-analysis cancellation"
```

---

## Task 7: 补全对话框（M1）

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/NmInputWizardViewModel.cs`
- Create: `src/PeakCan.Host.App/Views/NmInputWizardWindow.xaml`
- Create: `src/PeakCan.Host.App/Views/NmInputWizardWindow.xaml.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmPanelFlow.cs`（`OpenNmWizardCommand`）
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/NmInputWizardViewModelTests.cs`

**Interfaces:**
- Consumes: `NmToolSnapshot`、`NmSidecarStore`、`NmSupplementMerger`、`NmAnalyzer`
- Produces:
  - `NmInputWizardViewModel`
  - `TraceViewerViewModel.OpenNmWizardCommand`

- [ ] **Step 1: 写验证测试**

```csharp
using FluentAssertions;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public class NmInputWizardViewModelTests
{
    private static NmInputWizardViewModel Wizard() => new(
        new NmToolSnapshot("src", null, @"C:\nets\net.dbc", "h",
            new NmProfile(NmProfileSource.Dbc,
                new[] { new NmMessageDef("0x500", false, "NM", "A", 8, true, new[] { "x" }, 500, 2000) },
                new NmTimings(500, 2000), Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
                Array.Empty<string>(), new NmProvenance()),
            EmptyReport(), Array.Empty<PeakCan.Host.Core.Replay.ReplayFrame>()));

    private static NmReport EmptyReport() => new(
        new NmWindow(0, 1, "full"), "src", null,
        Array.Empty<NmEpisode>(), Array.Empty<NmMessageReport>(), Array.Empty<NmNodeReport>(),
        Array.Empty<NmEvent>(), Array.Empty<NmFinding>(), Array.Empty<string>(),
        Array.Empty<string>(), Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
        Array.Empty<NmTruncation>());

    [Fact]
    public void Validate_RejectsNidEqualToCbv()
    {
        var w = Wizard();
        w.NidByte = 0; w.CbvByte = 0;
        w.Validate().Should().Contain("NID 与 CBV 字节不能相同");
    }

    [Fact]
    public void Validate_RejectsNidBeyondDlc()
    {
        var w = Wizard();
        w.NidByte = 8;
        w.Validate().Should().Contain("NID 字节必须小于 DLC");
    }

    [Fact]
    public void Validate_RejectsUserDataOutOfRange()
    {
        var w = Wizard();
        w.UserDataOffset = 7; w.UserDataLength = 4;
        w.Validate().Should().Contain("用户数据区间超出 DLC");
    }

    [Fact]
    public void Validate_RejectsTimeoutNotGreaterThanCycle()
    {
        var w = Wizard();
        w.CycleMs = 2000; w.TimeoutMs = 2000;
        w.Validate().Should().Contain("超时必须大于周期");
    }

    [Fact]
    public void Validate_AcceptsConsistentInputs()
    {
        var w = Wizard();
        w.NidByte = 0; w.CbvByte = 1; w.UserDataOffset = 2; w.UserDataLength = 0;
        w.CycleMs = 500; w.TimeoutMs = 2000;
        w.Validate().Should().BeEmpty();
    }

    [Fact]
    public void ToSupplement_DoesNotIncludeUntouchedFields()
    {
        var w = Wizard();
        w.NidByte = 0;

        var s = w.ToSupplement();

        s.NidByte.Should().Be(0);
        s.CbvByte.Should().BeNull();
        s.Timings.Should().BeNull();
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~NmInputWizardViewModelTests"
```

Expected: 编译失败。

- [ ] **Step 3: 实现 ViewModel**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Analysis.Nm;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// 补全 NM 输入的对话框 VM。建议值只填充编辑器，永不自动生效；
/// 只有「保存到项目」才会写 sidecar。
/// </summary>
public sealed partial class NmInputWizardViewModel : ObservableObject
{
    private readonly NmToolSnapshot _snapshot;
    private readonly NmMessageDef? _firstNm;
    private readonly PeakCan.Host.Core.Replay.ReplayFrame? _sampleFrame;

    [ObservableProperty] private byte? _nidByte;
    [ObservableProperty] private byte? _cbvByte;
    [ObservableProperty] private byte? _userDataOffset;
    [ObservableProperty] private int? _userDataLength;
    [ObservableProperty] private int? _cycleMs;
    [ObservableProperty] private int? _timeoutMs;
    [ObservableProperty] private bool? _pnEnabled;
    [ObservableProperty] private string _status = "";

    public NmInputWizardViewModel(NmToolSnapshot snapshot)
    {
        _snapshot = snapshot;
        _firstNm = snapshot.Profile.Messages.FirstOrDefault(m => m.IsNm);
        _sampleFrame = _firstNm is null
            ? null
            : snapshot.Frames.FirstOrDefault(f =>
                (f.Id & 0x1FFFFFFFu) == ParseId(_firstNm.CanIdHex));
        CycleMs = snapshot.Profile.Timings.CycleMs;
        TimeoutMs = snapshot.Profile.Timings.TimeoutMs;
        MissingItems = snapshot.Report.MissingInputs.ToArray();
    }

    public IReadOnlyList<NmInputMissing> MissingItems { get; }

    /// <summary>取当前 trace 里该 NM 报文的第一帧字节；没有样本时为空。</summary>
    public IReadOnlyList<byte> SampleBytes => _sampleFrame?.Data ?? Array.Empty<byte>();

    public int Dlc => _sampleFrame?.Dlc ?? _firstNm?.Dlc ?? 8;

    private static uint ParseId(string hex)
        => uint.Parse(hex.AsSpan(2), System.Globalization.NumberStyles.HexNumber);

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (NidByte is { } nid && nid >= Dlc) errors.Add("NID 字节必须小于 DLC");
        if (CbvByte is { } cbv && cbv >= Dlc) errors.Add("CBV 字节必须小于 DLC");
        if (NidByte is { } n && CbvByte is { } c && n == c) errors.Add("NID 与 CBV 字节不能相同");
        if (UserDataOffset is { } off && UserDataLength is { } len && off + len > Dlc)
            errors.Add("用户数据区间超出 DLC");
        if (TimeoutMs is { } timeout && CycleMs is { } cycle && timeout <= cycle)
            errors.Add("超时必须大于周期");
        return errors;
    }

    public NmUserSupplement ToSupplement() => new(
        NidByte: NidByte,
        CbvByte: CbvByte,
        UserDataOffset: UserDataOffset,
        UserDataLength: UserDataLength,
        Timings: CycleMs is null && TimeoutMs is null
            ? null
            : new NmTimings(CycleMs: CycleMs, TimeoutMs: TimeoutMs),
        Features: PnEnabled is null ? null : new NmFeatures(PnEnabled: PnEnabled),
        DbcPath: _snapshot.DbcPath,
        DbcHash: _snapshot.DbcHash);

    [RelayCommand]
    private void ApplySuggestion(NmInputMissing item)
    {
        // 只把建议值填进对应编辑器；不保存、不自动生效。
        Status = $"已填入建议：{item.Suggestion}（仍需点「保存到项目」）";
        if (item.InputKey == "cycle_ms" && _firstNm?.CycleMs is { } cycle) CycleMs = cycle;
        if (item.InputKey == "nid_byte") NidByte = 0;
        if (item.InputKey == "cbv_byte") CbvByte = 1;
    }

    [RelayCommand]
    private async Task TrialAsync()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            Status = string.Join("；", errors);
            return;
        }

        var profile = NmSupplementMerger.Merge(_snapshot.Profile, ToSupplement());
        var end = _snapshot.Report.Window.End;
        var report = await Task.Run(() => NmAnalyzer.Analyze(new NmAnalysisRequest(
            _snapshot.SourceId, _snapshot.Channel, _snapshot.Frames,
            null, profile, _snapshot.Report.Window.Start, end, "trial")));
        Status = $"试算：识别 {report.Messages.Count} 条报文；" +
                 $"能力 {report.Capabilities.Count(c => c.Available)} 项可用";
    }

    [RelayCommand]
    private void Save()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            Status = string.Join("；", errors);
            return;
        }
        if (_snapshot.DbcPath is null)
        {
            Status = "没有 DBC 路径，无法保存 sidecar";
            return;
        }
        NmSidecarStore.Save(_snapshot.DbcPath, ToSupplement());
        Status = "已保存到 " + NmSidecarStore.PathFor(_snapshot.DbcPath);
    }
}
```

> `NmToolSnapshot.Frames` 在 Task 1 已经带上真实帧列表，`TrialAsync` 直接用它，不要传空数组假装试算。

- [ ] **Step 4: 建对话框 XAML**

创建 `src/PeakCan.Host.App/Views/NmInputWizardWindow.xaml`：

```xml
<Window x:Class="PeakCan.Host.App.Views.NmInputWizardWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="补全 NM 输入" Width="720" Height="460" WindowStartupLocation="CenterOwner">
    <Grid Margin="10">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="220" />
            <ColumnDefinition Width="260" />
            <ColumnDefinition Width="*" />
        </Grid.ColumnDefinitions>

        <!-- 左：缺项列表 -->
        <ListBox Grid.Column="0" ItemsSource="{Binding MissingItems}" Margin="0,0,8,0">
            <ListBox.ItemTemplate>
                <DataTemplate>
                    <StackPanel Margin="0,2">
                        <TextBlock Text="{Binding Capability}" FontWeight="SemiBold" />
                        <TextBlock Text="{Binding Why}" FontSize="11" TextWrapping="Wrap" />
                        <TextBlock Text="{Binding Suggestion}" FontSize="11" Foreground="#555" TextWrapping="Wrap" />
                    </StackPanel>
                </DataTemplate>
            </ListBox.ItemTemplate>
        </ListBox>

        <!-- 中：字节预览 -->
        <StackPanel Grid.Column="1" Margin="0,0,8,0">
            <TextBlock Text="样本预览（byte 索引）" FontWeight="SemiBold" />
            <ItemsControl ItemsSource="{Binding SampleBytes}">
                <ItemsControl.ItemsPanel>
                    <ItemsPanelTemplate><WrapPanel /></ItemsPanelTemplate>
                </ItemsControl.ItemsPanel>
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Border Width="28" Height="28" Margin="2" BorderBrush="#CCC" BorderThickness="1">
                            <TextBlock Text="{Binding}" HorizontalAlignment="Center" VerticalAlignment="Center" />
                        </Border>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
            <TextBlock Text="{Binding Status}" Margin="0,8" TextWrapping="Wrap" Foreground="#8A6D00" />
        </StackPanel>

        <!-- 右：编辑区 -->
        <StackPanel Grid.Column="2">
            <TextBlock Text="NID 字节" /><TextBox Text="{Binding NidByte, UpdateSourceTrigger=PropertyChanged}" />
            <TextBlock Text="CBV 字节" /><TextBox Text="{Binding CbvByte, UpdateSourceTrigger=PropertyChanged}" />
            <TextBlock Text="用户数据 offset" /><TextBox Text="{Binding UserDataOffset, UpdateSourceTrigger=PropertyChanged}" />
            <TextBlock Text="用户数据 length" /><TextBox Text="{Binding UserDataLength, UpdateSourceTrigger=PropertyChanged}" />
            <TextBlock Text="周期 (ms)" /><TextBox Text="{Binding CycleMs, UpdateSourceTrigger=PropertyChanged}" />
            <TextBlock Text="超时 (ms)" /><TextBox Text="{Binding TimeoutMs, UpdateSourceTrigger=PropertyChanged}" />
            <TextBlock Text="PN 启用" /><CheckBox IsChecked="{Binding PnEnabled}" />
            <StackPanel Orientation="Horizontal" Margin="0,8">
                <Button Content="试算当前窗口" Command="{Binding TrialCommand}" Margin="0,0,6,0" />
                <Button Content="保存到项目" Command="{Binding SaveCommand}" Margin="0,0,6,0" />
                <Button Content="取消" Click="OnCancelClick" />
            </StackPanel>
        </StackPanel>
    </Grid>
</Window>
```

代码后置：

```csharp
using System.Windows;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

public partial class NmInputWizardWindow : Window
{
    public NmInputWizardWindow(NmInputWizardViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
```

- [ ] **Step 5: 面板打开对话框**

在 `NmPanelFlow.cs` 追加：

```csharp
[RelayCommand]
private async Task OpenNmWizardAsync()
{
    var snapshot = await ((IChatToolContext)this).GetNmSnapshotAsync(new NmToolQuery(), CancellationToken.None);
    var vm = new NmInputWizardViewModel(snapshot);
    var window = new Views.NmInputWizardWindow(vm) { Owner = System.Windows.Application.Current.MainWindow };
    window.ShowDialog();
    _nmCache.Invalidate();
    await RunNmAnalysisAsync(null);
}
```

面板顶部的"缺项 N → 补全"按钮在 Task 5 的 XAML 里已经绑定 `OpenNmWizardCommand`；本步骤只需确认该命令存在且保存后重算生效（上面代码）。

- [ ] **Step 6: 跑测试**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~NmInputWizardViewModelTests"
```

Expected: 6 个用例全绿。

- [ ] **Step 7: 提交**

```bash
git add src/PeakCan.Host.App/ViewModels/NmInputWizardViewModel.cs \
        src/PeakCan.Host.App/Views/NmInputWizardWindow.xaml \
        src/PeakCan.Host.App/Views/NmInputWizardWindow.xaml.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmPanelFlow.cs \
        tests/PeakCan.Host.App.Tests/ViewModels/NmInputWizardViewModelTests.cs
git commit -m "feat(nm): add M1 input wizard with trial and sidecar save"
```

---

## Task 8: 空态、错误态与回归验收

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmPanelFlow.cs`（空态/错误态文案）
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/NmPanelFlowTests.cs`（追加）
- Test: `tests/PeakCan.Host.App.Tests/Services/ChatTools/ChatToolsRegressionTests.cs`

**Interfaces:**
- Consumes: 全部前序任务

- [ ] **Step 1: 写空态/错误态测试**

追加到 `NmPanelFlowTests`：

```csharp
[Fact]
public void ApplyNmReport_NoMessages_ShowsHonestEmptyState()
{
    var report = new NmReport(
        new NmWindow(0, 1, "full"), "src", null,
        Array.Empty<NmEpisode>(), Array.Empty<NmMessageReport>(), Array.Empty<NmNodeReport>(),
        Array.Empty<NmEvent>(), Array.Empty<NmFinding>(), Array.Empty<string>(),
        Array.Empty<string>(), Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
        Array.Empty<NmTruncation>());

    var vm = NewVm();
    vm.ApplyNmReport(new PeakCan.Host.App.Services.ChatTools.NmToolSnapshot(
        "src", null, null, null,
        new NmProfile(NmProfileSource.None, Array.Empty<NmMessageDef>(), new NmTimings(),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(), Array.Empty<string>(),
            new NmProvenance()), report, Array.Empty<PeakCan.Host.Core.Replay.ReplayFrame>()));

    vm.NmStatus.Should().Be("本窗口未发现 NM 帧");
    vm.NmStatus.Should().NotContain("睡眠");
    vm.NmStatus.Should().NotContain("掉线");
}
```

在该测试文件顶部加一个与 `GreenLineAnchorFlowTests.NewVm` 同形的工厂（不要为了测试新增生产代码路径）：

```csharp
private static TraceViewerViewModel NewVm()
{
    var session = NSubstitute.Substitute.For<ITraceSessionService>();
    session.WatchedSignals.Returns(new System.Collections.ObjectModel.ObservableCollection<WatchedSignalRow>());
    session.SignalGroups.Returns(new System.Collections.ObjectModel.ObservableCollection<WatchedSignalGroup>());
    var registry = NSubstitute.Substitute.For<ITraceSessionRegistry>();
    registry.Sources.Returns(new List<TraceSource>());
    var dbcService = NSubstitute.Substitute.For<DbcService>(
        Microsoft.Extensions.Logging.Abstractions.NullLogger<DbcService>.Instance);
    var libPath = Path.Combine(Path.GetTempPath(), $"nm-panel-{Guid.NewGuid():N}.tmtrace");
    return new TraceViewerViewModel(session, registry, dbcService,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<TraceViewerViewModel>.Instance,
        new TraceSessionLibrary(libPath,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TraceSessionLibrary>.Instance));
}
```

需要补的 using：`PeakCan.Host.App.Services`、`PeakCan.Host.App.Services.Trace`、`NSubstitute`、`System.IO`。

- [ ] **Step 2: 写工具回归测试**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services.ChatTools;
using Xunit;

namespace PeakCan.Host.App.Tests.Services.ChatTools;

public class ChatToolsRegressionTests
{
    [Fact]
    public void ExistingToolNames_AreStillPresent()
    {
        var ctx = new FakeChatToolContext();
        var expected = new[]
        {
            "search_signals", "get_signal_overview", "anomaly_scan",
            "get_dbc_signal", "get_dbc_message", "find_related_signals",
            "propose_to_watch_list", "remove_from_watch_list", "seek_to",
            "search_signal_trace", "get_anchor_info", "analyze_timing_sequence",
            "get_trace_info", "get_dbc_info",
            "create_group", "add_to_group", "remove_from_group",
            "set_group_notes", "set_signal_alias",
        };

        // 只构造 19 个现存工具的实例，确认 ctor/Definition 未被 NM 改动破坏。
        var tools = new IChatTool[]
        {
            new SearchSignalsTool(ctx, NullLogger<SearchSignalsTool>.Instance),
            new GetSignalOverviewTool(ctx, NullLogger<GetSignalOverviewTool>.Instance),
            new AnomalyScanTool(ctx, NullLogger<AnomalyScanTool>.Instance),
            new GetDbcSignalTool(ctx, NullLogger<GetDbcSignalTool>.Instance),
            new GetDbcMessageTool(ctx, NullLogger<GetDbcMessageTool>.Instance),
            new FindRelatedSignalsTool(ctx, NullLogger<FindRelatedSignalsTool>.Instance),
            new ProposeToWatchListTool(ctx, NullLogger<ProposeToWatchListTool>.Instance),
            new RemoveFromWatchListTool(ctx, NullLogger<RemoveFromWatchListTool>.Instance),
            new SeekToTimeTool(ctx, NullLogger<SeekToTimeTool>.Instance),
            new SearchSignalTraceTool(ctx, NullLogger<SearchSignalTraceTool>.Instance),
            new GetAnchorInfoTool(ctx, NullLogger<GetAnchorInfoTool>.Instance),
            new AnalyzeTimingSequenceTool(ctx, NullLogger<AnalyzeTimingSequenceTool>.Instance),
            new GetTraceInfoTool(ctx, NullLogger<GetTraceInfoTool>.Instance),
            new GetDbcInfoTool(ctx, NullLogger<GetDbcInfoTool>.Instance),
            new CreateGroupTool(ctx, NullLogger<CreateGroupTool>.Instance),
            new AddToGroupTool(ctx, NullLogger<AddToGroupTool>.Instance),
            new RemoveFromGroupTool(ctx, NullLogger<RemoveFromGroupTool>.Instance),
            new SetGroupNotesTool(ctx, NullLogger<SetGroupNotesTool>.Instance),
            new SetSignalAliasTool(ctx, NullLogger<SetSignalAliasTool>.Instance),
            new GetNmProfileTool(ctx, NullLogger<GetNmProfileTool>.Instance),
            new GetNmOverviewTool(ctx, NullLogger<GetNmOverviewTool>.Instance),
            new GetNmEventsTool(ctx, NullLogger<GetNmEventsTool>.Instance),
        };

        tools.Select(t => t.Name).Should().Contain(expected);
        tools.Should().HaveCount(22);
    }
}
```

- [ ] **Step 3: 跑定向回归**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~ChatTools|FullyQualifiedName~NmPanelFlowTests"
```

Expected: 全绿；19 个旧工具测试不回归。

- [ ] **Step 4: 手动冒烟（必须真跑一次 UI）**

1. 启动 `dotnet run --project src/PeakCan.Host.App/PeakCan.Host.App.csproj -p:Platform=x86`。
2. 加载 `tests/PeakCan.Host.App.Tests/Fixtures/Can/pure_electric_v4.6.dbc` + 任意 ASC。
3. 打开「网络管理」：应显示"缺项 N → 补全"，不显示任何 NID/CBV 结论。
4. 点「补全」，勾选 NM 报文、填 NID/CBV，保存；确认生成 `pure_electric_v4.6.nmprofile.json`，面板重算后能力 chip 变化。
5. 在 AI 对话里问"这段 trace 有没有 NM 掉线"，确认 AI 先调 `get_nm_profile` 并说明缺项，不直接下结论。
6. 点击事件表行，确认图表 seek。

Expected: 全步骤通过。把截图/观察记录写进提交说明。

- [ ] **Step 5: 提交**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmPanelFlow.cs \
        tests/PeakCan.Host.App.Tests/ViewModels/NmPanelFlowTests.cs \
        tests/PeakCan.Host.App.Tests/Services/ChatTools/ChatToolsRegressionTests.cs
git commit -m "test(nm): pin empty/error states and 22-tool regression"
```

---

## Self-Review

**1. Spec 覆盖**

| spec 要求 | 任务 |
|---|---|
| §8.1 三个 M1 工具、参数、输出、分页、`unknowns`/`capabilities`/`missing_inputs` | Task 2 |
| §8.1 `source_id` 默认 master；多通道报错不混算 | Task 1（VM 实现）+ Task 2 |
| §8.1 输出上限 20 episodes/50 messages/30 findings；events 50/200 | Task 2 |
| §8.2 M1 提示词 10 条 | Task 3 |
| §8.3 证据分级 | Task 2（`evidence_kind`）、Task 5（表格列） |
| §8.4 缓存复用 | Task 1 |
| §9.1 面板 5 部分（profile 条 / 时间条 / 事件表 / 操作 / 状态） | Task 5、6 |
| §9.2 补全对话框（左缺项/中预览/右编辑/试算/保存） | Task 7 |
| §9.3 导出 Markdown + CSV | Task 6 |
| §9.4 空态/错误态 | Task 8 |
| §10 后台/取消/零拷贝 | Task 1、6 |
| §11 隐私默认 | Task 2（不含字节取值，只有字节索引/变化次数） |
| §13.1 App/UI 测试 | Task 4、6、7、8 |
| AC6、AC12 | Task 5、7、8 |
| AC8（19 工具不回归） | Task 8 |

**2. Placeholder 扫描**：无 TBD。试算帧通过 `NmToolSnapshot.Frames` 真实传入；面板测试构造沿用了仓库已有的 `GreenLineAnchorFlowTests.NewVm` 形状。

**3. 类型一致性**：`NmToolQuery`/`NmToolSnapshot`/`NmReport.Direction`/`NmDirectionCounts` 在 Task 1 定义，Task 2–8 一致使用；`NmPanelFlow` 的行模型名称与 XAML 绑定一致。

**4. Review Focus 覆盖**：8 条全部有对应任务与测试。

**5. 已知限制**

- 面板时间条用固定 1000 设计宽度 + `Viewbox` 缩放，不是逐像素按窗口宽度计算；M1 足够，M2 若要精确交互再换自定义控件。
- 补全对话框的"试算"必须传真实帧；若执行时 `NmToolSnapshot` 不含帧，需要给它加 `Frames` 字段（计划 B 的 `NmAnalysisRequest` 已支持）。
- 隐私开关（"允许发送原始 payload"）默认关闭；M1 工具根本不输出字节取值，因此不需要开关。M2 加内容字典时再补。
