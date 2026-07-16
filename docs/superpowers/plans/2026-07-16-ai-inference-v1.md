# v3.52.0 MINOR — AI 推理 v1（本地证据 + AnalysisSession + 面板 + ILlmProvider 接口）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 Trace Viewer 中加入 AI 辅助故障分析 v1。P0 仅本地证据 + AnalysisSession + UI 面板 + `ILlmProvider` 接口 contract（无 Provider 实现），为 P1 接 DeepSeek 预留接口骨架。

**Architecture:** 9 个 Core 层纯函数式 record + 分析器（`src/PeakCan.Host.Core/Analysis/`）+ 2 个 App 层 VM partial + 1 个 XAML Tab。`AnchorSnapshot` 在用户点"锁定 anchor 状态"时由 `LockAnchorCommand` 创建（**双锚齐全才能锁定**，蓝锚缺失时弹提示阻止）；`EvidenceExtractor` 通过 `ITraceSessionRegistry.GetFrames` 复制边界读帧（per-source 独立）；`LocalAnalyzer` 做候选关联 + per-source 归一化排序；`AnalysisSession` 自带版本号，独立于 `TraceViewerViewModel.Reset()`。

**Tech Stack:** C# .NET 10 / WPF / CommunityToolkit.Mvvm / FluentAssertions / NSubstitute / xUnit / OxyPlot 2.2.0（仅消费，不扩展）

## Global Constraints

来自 `docs/superpowers/specs/2026-07-16-ai-inference-v1-design.md`，所有 task 必须遵守：

- AI 严格走 `ITraceViewerService`（INSPECTION ONLY），**绝不**经过 `IReplayService` 写总线。`TraceViewerServiceTests.cs:28-43` 反射守住。
- `AnchorSnapshot` 不持 `Signal` / `DbcDocument` / `WatchedSignalRow` 引用，仅原始值（`double` + enum text string + signalKey）。
- `SignalKey` 格式 `{idHex}.{signalName}[.{sourceId}]`，跨源必须带 sourceId。
- 蓝锚缺失时 `AnchorSnapshot` 阻止锁定（不构造 only-green 单点）—— UI 弹提示"必须先设比较锚"。
- 双锚齐全是 `LockAnchorCommand` 的 `CanExecute` 前提；点击触发后创建 `AnchorSnapshot`。
- 候选排序 per-source 归一化（每个 source 内独立计算 Δ / 变化率 / 状态切换次数），避免帧数多 source 霸榜。
- 大日志路径必须走 `ITraceSessionRegistry.GetFrames` 复制边界或 windowed/indexed reader，不直接遍历 `LoadedFrames`。
- `AnalysisSession` 自带 `Version` int；任一输入变化（fault event 时间 / 窗口 / source 集合 / 双锚时间 / 信号选择）→ 新 session，**不静默修改**旧结果。
- `AnalysisSession` 不入 `.tmtrace`（D5 决策），VM singleton 内存存活，重启后重新点"锁定 anchor"重建。
- `EvidenceExtractor` 读 `GetFrames` 返回 `Array.Empty<ReplayFrame>()` 是合法空源处理（`TraceSessionRegistry.cs:122-125`），不抛异常。
- `LocalReport.Summary` 必须明确"无归因，仅特征"标识；候选为空时合法输出"未发现可靠关联"，**不是** error state。
- `ILlmProvider` 是接口预留；P0 仅 `NotImplementedLlmProvider` stub，`AnalyzeAsync` 抛 `NotImplementedException`。P1 PATCH 填真实 Provider。
- `FaultEvent` 默认 ±500ms 窗口（可调；最小 +10ms，最大 +5s）。
- Evidence 列表 E-xxx ID 必须严格单调递增（`E-0001` 起始），不允许复用。
- 测试必须 RED → GREEN → IMPROVE，覆盖率 ≥ 80%（per-project rule）。
- 不修改 `ReplayFrame.cs` / `ITraceViewerService.cs` / `WatchedSignalRow.cs`（只读消费）。

## File Structure

新增到 `src/PeakCan.Host.Core/Analysis/`（**Core 层纯函数式，无 DI 依赖**）：

| 文件 | LoC | 职责 |
|---|---|---|
| `AnchorSnapshot.cs` | 30 | `AnchorSnapshot` record + `AnchoredSignalValue` record |
| `FaultEvent.cs` | 25 | `FaultEvent` record（中心时间点 + 窗口 + 描述 + CreatedAtUtc） |
| `FaultAnalysisEvidence.cs` | 30 | `FaultAnalysisEvidence` record（E-xxx ID + signalKey + sourceId + type + timestamp + value + enumText + description） |
| `LocalReport.cs` | 40 | `LocalReport` record + `CandidateSignal` record |
| `AnalysisSession.cs` | 25 | `AnalysisSession` record（SessionId + Version + FaultEvent + AnchorSnapshot + Report + CreatedAtUtc） |
| `ILlmProvider.cs` | 20 | `ILlmProvider` interface + `NotImplementedLlmProvider` stub |
| `EvidenceExtractor.cs` | 150 | `EvidenceExtractor` 类 + `Extract(faultEvent, anchorSnapshot, registry, dbcDocument)` 方法 |
| `LocalAnalyzer.cs` | 120 | `LocalAnalyzer` 类 + `Analyze(evidence, faultEvent, registry)` 方法 |
| `AnalysisSessionRegistry.cs` | 50 | `AnalysisSessionRegistry` 类（version-aware session store，独立于 VM Reset） |

新增到 `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/`：

| 文件 | LoC | 职责 |
|---|---|---|
| `AnchorSnapshotFlow.cs` | 80 | `LockAnchorCommand`（双锚校验 + 创建 AnchorSnapshot）+ `AnchorSnapshot` bindable property |
| `AnalysisFlow.cs` | 120 | `RunAnalysisCommand`（创建 session + 调用 EvidenceExtractor + LocalAnalyzer）+ `CurrentSession` bindable property + `LlmProvider` 注入 |

新增到 `src/PeakCan.Host.App/Views/`：

| 文件 | LoC | 职责 |
|---|---|---|
| `TraceViewerView.AIPanel.xaml` | 80 | 第 3 个 Tab "AI Analysis"（sister of Watch List + Sampling Table） |

修改：

| 文件 | LoC delta | 改动 |
|---|---|---|
| `src/PeakCan.Host.App/Views/TraceViewerView.xaml` | +5 | 加 1 个 `<TabItem Header="AI Analysis" Content="{Binding AIPanelContent}">` |
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` | +3 | 加 `AIPanelContent` property（指向 `TraceViewerView.AIPanel.xaml` 加载的 UserControl） |
| `src/PeakCan.Host.App/Composition/AppServicesFlow.cs` | +15 | DI 注册 `ILlmProvider` → `NotImplementedLlmProvider` singleton + `AnalysisSessionRegistry` singleton + `EvidenceExtractor` + `LocalAnalyzer` |

测试（新增到 `tests/PeakCan.Host.Core.Tests/Analysis/` + `tests/PeakCan.Host.App.Tests/ViewModels/`）：

| 文件 | LoC | 覆盖 |
|---|---|---|
| `AnchorSnapshotTests.cs` | ~80 | 双锚 / only-green 退化（spec 测试 D2 阻止） / SignalKey 格式 |
| `EvidenceExtractorTests.cs` | ~120 | 窗口裁剪 / per-source 独立 / 空源 / 大窗口边界 |
| `LocalAnalyzerTests.cs` | ~120 | 候选排序 per-source 归一化 / Δ ≠ 0 / 状态切换 / 周期异常 / 丢帧 / 越界 / 空候选合法 |
| `AnalysisSessionRegistryTests.cs` | ~80 | Version 自增 / 输入变化生成新 session / Reset 不清空 |
| `AnchorSnapshotFlowTests.cs` | ~100 | 双锚齐全锁定 / 蓝锚缺失阻止 + ErrorMessage / 锁定后 INPC 触发 |
| `AnalysisFlowTests.cs` | ~100 | 完整 session 创建 / `LocalReport` 渲染 / `ILlmProvider` 路径 |
| **总计** | **~600** | |

总增量 ~1370 LoC（spec 估算）。

---

### Task 0: Branch + spec verify + plan commit

**Files:**
- Branch from `main` at current HEAD
- Verify spec file exists at `docs/superpowers/specs/2026-07-16-ai-inference-v1-design.md`
- Create: `docs/superpowers/plans/2026-07-16-ai-inference-v1.md` (this file)

- [ ] **Step 1: Verify spec commit exists**

Run:
```bash
git log --oneline -5
git show --stat 56d3518
```
Expected: commit `56d3518` is visible with `docs/superpowers/specs/2026-07-16-ai-inference-v1-design.md` +293 insertions.

- [ ] **Step 2: Create feature branch**

Run:
```bash
git checkout -b feature/v3-52-0-ai-inference-v1 main
```
Expected: switched to new branch `feature/v3-52-0-ai-inference-v1`.

- [ ] **Step 3: Verify baseline build is green**

Run:
```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug --no-restore
```
Expected: `Build succeeded. 0 Warning(s), 0 Error(s)`.

- [ ] **Step 4: Verify baseline test is green**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --no-restore --nologo -c Debug --filter "FullyQualifiedName~ReplayFrame" --logger "console;verbosity=minimal"
```
Expected: tests pass (baseline confirmation).

- [ ] **Step 5: Commit plan**

```bash
git add docs/superpowers/plans/2026-07-16-ai-inference-v1.md
git commit -m "v3.52.0 plan: AI 推理 v1 (9 Core files + 2 App partials + 1 XAML + 6 test files; TDD; per-source 归一化 + 双锦锁定校验 + version-aware session)"
```

---

### Task 1: AnchorSnapshot + FaultEvent + FaultAnalysisEvidence records (RED → GREEN)

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/AnchorSnapshot.cs`
- Create: `src/PeakCan.Host.Core/Analysis/FaultEvent.cs`
- Create: `src/PeakCan.Host.Core/Analysis/FaultAnalysisEvidence.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/AnchorSnapshotTests.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/FaultEventTests.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/FaultAnalysisEvidenceTests.cs`

**Interfaces:**
- Consumes: nothing (pure records)
- Produces:
  - `public sealed record AnchorSnapshot(double GreenTimestampSeconds, double BlueTimestampSeconds, IReadOnlyList<AnchoredSignalValue> Signals, DateTime CapturedAtUtc, int Version);`
  - `public sealed record AnchoredSignalValue(string SignalKey, string SourceId, double LatestValue, double BlueLatestValue, double DeltaValue, string LatestText, string BlueText, string DeltaText);`
  - `public sealed record FaultEvent(double CenterTimestampSeconds, TimeSpan WindowBefore, TimeSpan WindowAfter, string Description, DateTime CreatedAtUtc);`
  - `public sealed record FaultAnalysisEvidence(string EvidenceId, string SignalKey, string SourceId, string Type, double TimestampSeconds, double Value, string? EnumText, string Description);`

- [ ] **Step 1: Write the failing test for AnchorSnapshot dual-anchor + SignalKey format**

Create `tests/PeakCan.Host.Core.Tests/Analysis/AnchorSnapshotTests.cs`:
```csharp
using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class AnchorSnapshotTests
{
    [Fact]
    public void Constructor_DualAnchors_PopulatesAllFields()
    {
        // Arrange
        var signals = new List<AnchoredSignalValue>
        {
            new("0x100.EngineRPM.asc-1", "asc-1", 1500.0, 2080.0, 580.0,
                "1500.00", "2080.00", "580.00"),
        };

        // Act
        var snap = new AnchorSnapshot(
            GreenTimestampSeconds: 1.234,
            BlueTimestampSeconds: 1.350,
            Signals: signals,
            CapturedAtUtc: new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc),
            Version: 1);

        // Assert
        snap.GreenTimestampSeconds.Should().Be(1.234);
        snap.BlueTimestampSeconds.Should().Be(1.350);
        snap.Signals.Should().HaveCount(1);
        snap.Signals[0].DeltaValue.Should().Be(580.0);
        snap.Version.Should().Be(1);
    }

    [Fact]
    public void AnchoredSignalValue_SignalKey_Format_IsIdHexDotSignalNameDotSourceId()
    {
        // Per spec hard-boundary #7: SignalKey = {idHex}.{signalName}[.{sourceId}]
        var key = "0x100.EngineRPM.asc-1";
        var sig = new AnchoredSignalValue(key, "asc-1", 1.0, 2.0, 1.0, "1", "2", "1");
        sig.SignalKey.Should().Be(key);
    }
}
```

- [ ] **Step 2: Run test to verify it fails (RED)**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~AnchorSnapshotTests"
```
Expected: FAIL with `error CS0246: The type or namespace name 'AnchorSnapshot' could not be found`.

- [ ] **Step 3: Write minimal AnchorSnapshot implementation**

Create `src/PeakCan.Host.Core/Analysis/AnchorSnapshot.cs`:
```csharp
namespace PeakCan.Host.Core.Analysis;

/// <summary>
/// v3.52.0 MINOR: immutable snapshot of the green + blue anchor values at the
/// moment the user clicks "锁定 anchor 状态". Per spec hard-boundary #9 + #10:
/// - DOES NOT hold Signal / DbcDocument / WatchedSignalRow references (those
///   can be cleared by TraceViewerViewModel.Reset via _signalByKey.Clear()).
/// - Holds ONLY raw values (double + enum text string + signalKey).
/// - Version bumps when re-captured; consumers compare Version to detect staleness.
/// </summary>
public sealed record AnchorSnapshot(
    double GreenTimestampSeconds,
    double BlueTimestampSeconds,
    IReadOnlyList<AnchoredSignalValue> Signals,
    DateTime CapturedAtUtc,
    int Version);

/// <summary>Per-signal value at both anchor moments.
/// SignalKey format per hard-boundary #7: {idHex}.{signalName}[.{sourceId}].</summary>
public sealed record AnchoredSignalValue(
    string SignalKey,
    string SourceId,
    double LatestValue,
    double BlueLatestValue,
    double DeltaValue,
    string LatestText,
    string BlueText,
    string DeltaText);
```

- [ ] **Step 4: Write the failing test for FaultEvent**

Create `tests/PeakCan.Host.Core.Tests/Analysis/FaultEventTests.cs`:
```csharp
using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class FaultEventTests
{
    [Fact]
    public void Constructor_Default500msWindow_PopulatesFields()
    {
        // Per spec D3: default ±500ms window
        var evt = new FaultEvent(
            CenterTimestampSeconds: 1.234,
            WindowBefore: TimeSpan.FromMilliseconds(500),
            WindowAfter: TimeSpan.FromMilliseconds(500),
            Description: "BmsFaultState fault",
            CreatedAtUtc: new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc));

        evt.WindowBefore.Should().Be(TimeSpan.FromMilliseconds(500));
        evt.WindowAfter.Should().Be(TimeSpan.FromMilliseconds(500));
        evt.Description.Should().Be("BmsFaultState fault");
    }
}
```

- [ ] **Step 5: Write minimal FaultEvent implementation**

Create `src/PeakCan.Host.Core/Analysis/FaultEvent.cs`:
```csharp
namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: a user-identified fault moment.
/// CenterTimestampSeconds is in seconds-from-recording-start (matches
/// ReplayFrame.Timestamp unit). Default window is ±500ms per spec D3.</summary>
public sealed record FaultEvent(
    double CenterTimestampSeconds,
    TimeSpan WindowBefore,
    TimeSpan WindowAfter,
    string Description,
    DateTime CreatedAtUtc);
```

- [ ] **Step 6: Write the failing test for FaultAnalysisEvidence**

Create `tests/PeakCan.Host.Core.Tests/Analysis/FaultAnalysisEvidenceTests.cs`:
```csharp
using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class FaultAnalysisEvidenceTests
{
    [Fact]
    public void Constructor_EvidenceId_MonotonicAndUnique()
    {
        var e1 = new FaultAnalysisEvidence(
            EvidenceId: "E-0001",
            SignalKey: "0x100.EngineRPM.asc-1",
            SourceId: "asc-1",
            Type: "state-transition",
            TimestampSeconds: 1.234,
            Value: 1.0,
            EnumText: "Active",
            Description: "BmsFaultState → Active");

        var e2 = new FaultAnalysisEvidence(
            EvidenceId: "E-0002",
            SignalKey: "0x101.InverterReady.asc-1",
            SourceId: "asc-1",
            Type: "delta-spike",
            TimestampSeconds: 1.300,
            Value: -1.0,
            EnumText: null,
            Description: "InverterReady fell edge");

        e1.EvidenceId.Should().Be("E-0001");
        e2.EvidenceId.Should().Be("E-0002");
        e1.EnumText.Should().Be("Active");
        e2.EnumText.Should().BeNull();
    }
}
```

- [ ] **Step 7: Write minimal FaultAnalysisEvidence implementation**

Create `src/PeakCan.Host.Core/Analysis/FaultAnalysisEvidence.cs`:
```csharp
namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: a single piece of locally-extracted evidence inside
/// a fault window. EvidenceId format E-NNNN must be monotonically increasing
/// within a session (no reuse across sessions). Type is one of:
/// "state-transition", "delta-spike", "cycle-anomaly", "frame-loss",
/// "out-of-range", "freeze", "stalled-counter". EnumText is null for
/// numeric signals or when the value doesn't match a VAL_ entry.</summary>
public sealed record FaultAnalysisEvidence(
    string EvidenceId,
    string SignalKey,
    string SourceId,
    string Type,
    double TimestampSeconds,
    double Value,
    string? EnumText,
    string Description);
```

- [ ] **Step 8: Run all 3 test files to verify they pass (GREEN)**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~Analysis" --logger "console;verbosity=minimal"
```
Expected: 4 tests pass (2 AnchorSnapshot + 1 FaultEvent + 1 FaultAnalysisEvidence).

- [ ] **Step 9: Commit**

```bash
git add src/PeakCan.Host.Core/Analysis/AnchorSnapshot.cs \
        src/PeakCan.Host.Core/Analysis/FaultEvent.cs \
        src/PeakCan.Host.Core/Analysis/FaultAnalysisEvidence.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/AnchorSnapshotTests.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/FaultEventTests.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/FaultAnalysisEvidenceTests.cs
git commit -m "v3.52.0 T1: AnchorSnapshot + FaultEvent + FaultAnalysisEvidence records (Core; 4 tests pass)"
```

---

### Task 2: LocalReport + CandidateSignal + AnalysisSession records (RED → GREEN)

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/LocalReport.cs`
- Create: `src/PeakCan.Host.Core/Analysis/AnalysisSession.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/LocalReportTests.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/AnalysisSessionTests.cs`

**Interfaces:**
- Consumes: Task 1 records (`AnchoredSignalValue`, `FaultEvent`, `FaultAnalysisEvidence`)
- Produces:
  - `public sealed record LocalReport(IReadOnlyList<FaultAnalysisEvidence> Evidence, IReadOnlyList<CandidateSignal> Candidates, IReadOnlyList<string> DataQualityNotes, string Summary, DateTime GeneratedAtUtc);`
  - `public sealed record CandidateSignal(string SignalKey, string SourceId, double Score, string ReasonText, IReadOnlyList<string> EvidenceIds);`
  - `public sealed record AnalysisSession(Guid SessionId, int Version, FaultEvent FaultEvent, AnchorSnapshot AnchorSnapshot, LocalReport Report, DateTime CreatedAtUtc);`

- [ ] **Step 1: Write the failing test for LocalReport**

Create `tests/PeakCan.Host.Core.Tests/Analysis/LocalReportTests.cs`:
```csharp
using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class LocalReportTests
{
    [Fact]
    public void Constructor_EmptyCandidates_SummaryIndicatesNoFinding()
    {
        // Per spec D4: "未发现可靠关联" 是合法输出，不是 error state
        var report = new LocalReport(
            Evidence: Array.Empty<FaultAnalysisEvidence>(),
            Candidates: Array.Empty<CandidateSignal>(),
            DataQualityNotes: new[] { "no frames in window" },
            Summary: "未发现可靠关联（仅本地特征）",
            GeneratedAtUtc: new DateTime(2026, 7, 16, 12, 0, 0, DateTimeKind.Utc));

        report.Summary.Should().Contain("未发现可靠关联");
        report.Candidates.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_WithCandidates_PopulatesFields()
    {
        var candidates = new[]
        {
            new CandidateSignal(
                SignalKey: "0x101.InverterReady.asc-1",
                SourceId: "asc-1",
                Score: 0.92,
                ReasonText: "Δ=-1 (Ready→NotReady) 116ms before fault",
                EvidenceIds: new[] { "E-0002" }),
        };
        var report = new LocalReport(
            Evidence: Array.Empty<FaultAnalysisEvidence>(),
            Candidates: candidates,
            DataQualityNotes: Array.Empty<string>(),
            Summary: "本地推断（无归因）：1 candidate",
            GeneratedAtUtc: DateTime.UtcNow);

        report.Candidates.Should().HaveCount(1);
        report.Candidates[0].Score.Should().Be(0.92);
    }
}
```

- [ ] **Step 2: Run test to verify it fails (RED)**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~LocalReportTests"
```
Expected: FAIL with `error CS0246: The type or namespace name 'LocalReport' could not be found`.

- [ ] **Step 3: Write minimal LocalReport implementation**

Create `src/PeakCan.Host.Core/Analysis/LocalReport.cs`:
```csharp
namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: the local-only deterministic analysis output.
/// Per spec D4: Summary MUST explicitly mark "no attribution" so the UI
/// can render a visual distinction vs an LLM-attributed report (P1 PATCH).
/// "未发现可靠关联" is a legal Summary value when no candidate scores
/// above the per-source normalization threshold.</summary>
public sealed record LocalReport(
    IReadOnlyList<FaultAnalysisEvidence> Evidence,
    IReadOnlyList<CandidateSignal> Candidates,
    IReadOnlyList<string> DataQualityNotes,
    string Summary,
    DateTime GeneratedAtUtc);

/// <summary>Per-signal candidate for association with the fault event.
/// Score is per-source-normalized in [0, 1] per hard-boundary #14
/// (high-frame-count sources must not dominate the global ranking).
/// EvidenceIds reference FaultAnalysisEvidence.EvidenceId within the same
/// LocalReport — UI uses them to navigate from candidate → evidence detail.</summary>
public sealed record CandidateSignal(
    string SignalKey,
    string SourceId,
    double Score,
    string ReasonText,
    IReadOnlyList<string> EvidenceIds);
```

- [ ] **Step 4: Write the failing test for AnalysisSession**

Create `tests/PeakCan.Host.Core.Tests/Analysis/AnalysisSessionTests.cs`:
```csharp
using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class AnalysisSessionTests
{
    [Fact]
    public void Constructor_AllFieldsPopulated()
    {
        var faultEvent = new FaultEvent(1.234, TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMilliseconds(500), "test", DateTime.UtcNow);
        var snapshot = new AnchorSnapshot(1.0, 1.5,
            Array.Empty<AnchoredSignalValue>(), DateTime.UtcNow, 1);
        var report = new LocalReport(Array.Empty<FaultAnalysisEvidence>(),
            Array.Empty<CandidateSignal>(), Array.Empty<string>(),
            "未发现可靠关联", DateTime.UtcNow);

        var session = new AnalysisSession(
            SessionId: Guid.NewGuid(),
            Version: 1,
            FaultEvent: faultEvent,
            AnchorSnapshot: snapshot,
            Report: report,
            CreatedAtUtc: DateTime.UtcNow);

        session.Version.Should().Be(1);
        session.FaultEvent.Should().Be(faultEvent);
        session.AnchorSnapshot.Should().Be(snapshot);
    }
}
```

- [ ] **Step 5: Write minimal AnalysisSession implementation**

Create `src/PeakCan.Host.Core/Analysis/AnalysisSession.cs`:
```csharp
namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: a single deterministic analysis pass for one
/// (fault event, anchor snapshot) pair. Per spec D5: NOT persisted to
/// .tmtrace (memory-only, lives in AnalysisSessionRegistry which is
/// independent of TraceViewerViewModel.Reset). Version increments when
/// any input changes — consumers compare Version to detect staleness.</summary>
public sealed record AnalysisSession(
    Guid SessionId,
    int Version,
    FaultEvent FaultEvent,
    AnchorSnapshot AnchorSnapshot,
    LocalReport Report,
    DateTime CreatedAtUtc);
```

- [ ] **Step 6: Run all 2 test files to verify they pass (GREEN)**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~Analysis" --logger "console;verbosity=minimal"
```
Expected: 6 tests pass total (Task 1's 4 + Task 2's 2).

- [ ] **Step 7: Commit**

```bash
git add src/PeakCan.Host.Core/Analysis/LocalReport.cs \
        src/PeakCan.Host.Core/Analysis/AnalysisSession.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/LocalReportTests.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/AnalysisSessionTests.cs
git commit -m "v3.52.0 T2: LocalReport + CandidateSignal + AnalysisSession records (Core; 6 tests pass total)"
```

---

### Task 3: ILlmProvider interface + NotImplementedLlmProvider stub

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/ILlmProvider.cs`
- Create: `src/PeakCan.Host.Core/Analysis/LlmAnalysisResult.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/ILlmProviderTests.cs`

**Interfaces:**
- Consumes: Task 2 records (`AnalysisSession`)
- Produces:
  - `public interface ILlmProvider { string DisplayName { get; } Task<LlmAnalysisResult> AnalyzeAsync(AnalysisSession session, CancellationToken ct); }`
  - `public sealed class NotImplementedLlmProvider : ILlmProvider`
  - `public sealed record LlmAnalysisResult(string Summary, IReadOnlyList<string> AttributedEvidenceIds, string RawResponseJson);`

- [ ] **Step 1: Write the failing test for ILlmProvider stub**

Create `tests/PeakCan.Host.Core.Tests/Analysis/ILlmProviderTests.cs`:
```csharp
using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class ILlmProviderTests
{
    [Fact]
    public void NotImplementedLlmProvider_DisplayName_IndicatesP0LocalOnly()
    {
        var provider = new NotImplementedLlmProvider();
        provider.DisplayName.Should().Contain("P0 local-only");
    }

    [Fact]
    public async Task NotImplementedLlmProvider_AnalyzeAsync_ThrowsNotImplemented()
    {
        var provider = new NotImplementedLlmProvider();
        var session = new AnalysisSession(Guid.NewGuid(), 1,
            new FaultEvent(1.0, TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(500), "test", DateTime.UtcNow),
            new AnchorSnapshot(1.0, 1.5, Array.Empty<AnchoredSignalValue>(),
                DateTime.UtcNow, 1),
            new LocalReport(Array.Empty<FaultAnalysisEvidence>(),
                Array.Empty<CandidateSignal>(), Array.Empty<string>(),
                "test", DateTime.UtcNow),
            DateTime.UtcNow);

        await Assert.ThrowsAsync<NotImplementedException>(
            () => provider.AnalyzeAsync(session, CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run test to verify it fails (RED)**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~ILlmProviderTests"
```
Expected: FAIL with `error CS0246: The type or namespace name 'ILlmProvider' could not be found`.

- [ ] **Step 3: Write minimal ILlmProvider interface + stub + result type**

Create `src/PeakCan.Host.Core/Analysis/ILlmProvider.cs`:
```csharp
namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: LLM provider contract (interface only in P0).
/// P1 PATCH will add: DeepSeekProvider, AzureOpenAIProvider, LocalOllamaProvider.
/// All implementations MUST:
/// - Validate Evidence IDs against the input session (whitelist filter per
///   hard-boundary #13: drop invalid ID references AND their associated
///   claims; only reject whole response if all claims are invalid).
/// - NOT log Authorization headers or full response bodies.
/// - Surface 401/429/timeout/JSON-parse errors as LlmAnalysisResult.Error
///   (NOT exception), so the caller can show degraded local-only results.</summary>
public interface ILlmProvider
{
    string DisplayName { get; }
    Task<LlmAnalysisResult> AnalyzeAsync(AnalysisSession session, CancellationToken ct);
}

/// <summary>P0 stub. P1 PATCH will replace with concrete providers.</summary>
public sealed class NotImplementedLlmProvider : ILlmProvider
{
    public string DisplayName => "(no LLM — P0 local-only)";

    public Task<LlmAnalysisResult> AnalyzeAsync(
        AnalysisSession session, CancellationToken ct) =>
        throw new NotImplementedException(
            "P1 PATCH will implement LLM Provider; see ILlmProvider contract.");
}
```

Create `src/PeakCan.Host.Core/Analysis/LlmAnalysisResult.cs`:
```csharp
namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR (P0 stub) / P1 PATCH (concrete): LLM provider output.
/// Summary is the LLM's natural-language summary. AttributedEvidenceIds is
/// the whitelist-filtered list of Evidence IDs the LLM actually cited —
/// entries not in the input session's Evidence list are dropped per
/// hard-boundary #13. RawResponseJson is the verbatim provider response
/// (without Authorization headers), used for diagnostics / replay. Error
/// is non-null when the provider failed (401/429/timeout/JSON-parse) —
/// callers should fall back to LocalReport and surface Error in the UI.</summary>
public sealed record LlmAnalysisResult(
    string Summary,
    IReadOnlyList<string> AttributedEvidenceIds,
    string RawResponseJson,
    string? Error);
```

- [ ] **Step 4: Run test to verify it passes (GREEN)**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~ILlmProviderTests"
```
Expected: 2 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/Analysis/ILlmProvider.cs \
        src/PeakCan.Host.Core/Analysis/LlmAnalysisResult.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/ILlmProviderTests.cs
git commit -m "v3.52.0 T3: ILlmProvider interface + NotImplementedLlmProvider stub (P0; P1 PATCH fills concrete)"
```

---

### Task 4: EvidenceExtractor — per-source window cropping + local features

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/EvidenceExtractor.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/EvidenceExtractorTests.cs`

**Interfaces:**
- Consumes: Task 1-3 records + `ITraceSessionRegistry.GetFrames` + `DbcDocument` (for enum decode)
- Produces:
  - `public sealed class EvidenceExtractor { public IReadOnlyList<FaultAnalysisEvidence> Extract(FaultEvent faultEvent, AnchorSnapshot snapshot, ITraceSessionRegistry registry, DbcDocument dbc, string dbcIdToSourceIdMap); }`

- [ ] **Step 1: Write the failing test for window cropping + per-source independence**

Create `tests/PeakCan.Host.Core.Tests/Analysis/EvidenceExtractorTests.cs`:
```csharp
using FluentAssertions;
using NSubstitute;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class EvidenceExtractorTests
{
    private static ReplayFrame Frame(double t, uint id, byte dlc, params byte[] data)
        => new(t, id, dlc, data, FrameFlags.None);

    [Fact]
    public void Extract_PerSource_IndependentWindowCropping()
    {
        // Two sources, different frame counts, same window
        var registry = Substitute.For<ITraceSessionRegistry>();
        var framesA = new[]
        {
            Frame(1.0, 0x100, 8, 0x10, 0, 0, 0, 0, 0, 0, 0),
            Frame(1.2, 0x100, 8, 0x20, 0, 0, 0, 0, 0, 0, 0),
            Frame(1.4, 0x100, 8, 0x00, 0, 0, 0, 0, 0, 0, 0),
            Frame(1.6, 0x100, 8, 0x00, 0, 0, 0, 0, 0, 0, 0),
        };
        var framesB = new[]
        {
            Frame(1.1, 0x100, 8, 0x55, 0, 0, 0, 0, 0, 0, 0),
            Frame(1.5, 0x100, 8, 0x66, 0, 0, 0, 0, 0, 0, 0),
        };
        registry.GetFrames("asc-A").Returns(framesA);
        registry.GetFrames("asc-B").Returns(framesB);

        var faultEvent = new FaultEvent(1.3,
            TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200),
            "test", DateTime.UtcNow);
        var snapshot = new AnchorSnapshot(1.0, 1.5,
            Array.Empty<AnchoredSignalValue>(), DateTime.UtcNow, 1);

        var extractor = new EvidenceExtractor();
        var evidence = extractor.Extract(faultEvent, snapshot, registry,
            dbc: null, dbcIdToSourceIdMap: new Dictionary<uint, string>
            {
                [0x100] = "0x100.EngineRPM",
            });

        // Both sources contribute; per-source evidence independent
        evidence.Should().Contain(e => e.SourceId == "asc-A");
        evidence.Should().Contain(e => e.SourceId == "asc-B");
    }

    [Fact]
    public void Extract_EmptySource_ReturnsEmptyList_NoThrow()
    {
        var registry = Substitute.For<ITraceSessionRegistry>();
        registry.GetFrames("empty").Returns(Array.Empty<ReplayFrame>());

        var faultEvent = new FaultEvent(1.0,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500),
            "test", DateTime.UtcNow);
        var snapshot = new AnchorSnapshot(1.0, 1.5,
            Array.Empty<AnchoredSignalValue>(), DateTime.UtcNow, 1);

        var extractor = new EvidenceExtractor();
        var evidence = extractor.Extract(faultEvent, snapshot, registry,
            dbc: null, dbcIdToSourceIdMap: new Dictionary<uint, string>());

        evidence.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run test to verify it fails (RED)**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~EvidenceExtractorTests"
```
Expected: FAIL with `error CS0246: The type or namespace name 'EvidenceExtractor' could not be found`.

- [ ] **Step 3: Write minimal EvidenceExtractor implementation**

Create `src/PeakCan.Host.Core/Analysis/EvidenceExtractor.cs`:
```csharp
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;

namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: extracts per-source evidence inside the fault
/// window. Per hard-boundary #6: reads via ITraceSessionRegistry.GetFrames
/// (which copies at the registry boundary, source-unload-safe). Per
/// hard-boundary #2: does NOT re-decode signals via SignalDecoder — uses
/// the signalKey already produced by the AnchorSnapshot flow. Per
/// hard-boundary #14: produces evidence per source, normalized independently
/// downstream by LocalAnalyzer.</summary>
public sealed class EvidenceExtractor
{
    public IReadOnlyList<FaultAnalysisEvidence> Extract(
        FaultEvent faultEvent,
        AnchorSnapshot snapshot,
        ITraceSessionRegistry registry,
        DbcDocument? dbc,
        IReadOnlyDictionary<uint, string> dbcIdToSourceIdMap)
    {
        ArgumentNullException.ThrowIfNull(faultEvent);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(dbcIdToSourceIdMap);

        var result = new List<FaultAnalysisEvidence>();
        int nextId = 1;
        double windowStart = faultEvent.CenterTimestampSeconds - faultEvent.WindowBefore.TotalSeconds;
        double windowEnd = faultEvent.CenterTimestampSeconds + faultEvent.WindowAfter.TotalSeconds;

        // Per spec: per-source independent extraction. Walk every SourceId
        // appearing in the anchor snapshot (covers multi-source sessions).
        var sourceIds = snapshot.Signals.Select(s => s.SourceId).Distinct();

        foreach (var sourceId in sourceIds)
        {
            var frames = registry.GetFrames(sourceId);
            if (frames.Count == 0) continue;

            // Window-crop
            var inWindow = frames
                .Where(f => f.Timestamp >= windowStart && f.Timestamp <= windowEnd)
                .ToList();

            // For each frame, extract a "state-transition" evidence entry
            // when the byte[0] (assume signal byte 0 for now) differs from
            // the prior in-window frame. Frame-loss / out-of-range checks
            // are deferred to LocalAnalyzer (which has the full picture).
            byte? prevByte0 = null;
            foreach (var frame in inWindow)
            {
                if (frame.Data.Length == 0) continue;
                byte currByte0 = frame.Data[0];
                if (prevByte0.HasValue && currByte0 != prevByte0.Value)
                {
                    var signalKey = dbcIdToSourceIdMap.TryGetValue(frame.Id, out var prefix)
                        ? $"{prefix}.{sourceId}"
                        : $"0x{frame.Id:X}.UnknownSignal.{sourceId}";
                    result.Add(new FaultAnalysisEvidence(
                        EvidenceId: $"E-{nextId++:D4}",
                        SignalKey: signalKey,
                        SourceId: sourceId,
                        Type: "state-transition",
                        TimestampSeconds: frame.Timestamp,
                        Value: currByte0,
                        EnumText: null,
                        Description: $"byte[0] {prevByte0.Value:X2}→{currByte0:X2} @ {frame.Timestamp:F3}s"));
                }
                prevByte0 = currByte0;
            }
        }

        return result;
    }
}
```

- [ ] **Step 4: Run test to verify it passes (GREEN)**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~EvidenceExtractorTests"
```
Expected: 2 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/Analysis/EvidenceExtractor.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/EvidenceExtractorTests.cs
git commit -m "v3.52.0 T4: EvidenceExtractor per-source window cropping + state-transition detection (Core; 2 tests pass)"
```

---

### Task 5: LocalAnalyzer — candidate association + per-source normalization

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/LocalAnalyzer.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/LocalAnalyzerTests.cs`

**Interfaces:**
- Consumes: Task 4 `EvidenceExtractor` output (`IReadOnlyList<FaultAnalysisEvidence>`)
- Produces:
  - `public sealed class LocalAnalyzer { public LocalReport Analyze(IReadOnlyList<FaultAnalysisEvidence> evidence, FaultEvent faultEvent, AnchorSnapshot snapshot); }`

- [ ] **Step 1: Write the failing test for per-source normalization ranking**

Create `tests/PeakCan.Host.Core.Tests/Analysis/LocalAnalyzerTests.cs`:
```csharp
using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class LocalAnalyzerTests
{
    [Fact]
    public void Analyze_TwoSourcesOneHighFrameCount_RankingPerSourceNormalized()
    {
        // Per spec hard-boundary #14: high-frame-count sources must NOT dominate
        var evidence = new List<FaultAnalysisEvidence>
        {
            // Source A: 100 transitions (would dominate if global normalization)
            new("E-0001", "0x100.SignalA.srcA", "srcA", "state-transition", 1.1, 1, null, "..."),
            new("E-0002", "0x100.SignalA.srcA", "srcA", "state-transition", 1.2, 2, null, "..."),
            new("E-0003", "0x100.SignalA.srcA", "srcA", "state-transition", 1.3, 3, null, "..."),
            // Source B: 1 transition (rare → high score in per-source view)
            new("E-0004", "0x200.SignalB.srcB", "srcB", "state-transition", 1.15, 99, null, "..."),
        };
        var faultEvent = new FaultEvent(1.2,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500),
            "test", DateTime.UtcNow);
        var snapshot = new AnchorSnapshot(1.0, 1.5,
            Array.Empty<AnchoredSignalValue>(), DateTime.UtcNow, 1);

        var analyzer = new LocalAnalyzer();
        var report = analyzer.Analyze(evidence, faultEvent, snapshot);

        // Per-source normalization: srcB's 1 transition ranks higher than
        // srcA's 3 transitions within its source. Global candidate count
        // is 1 per source (SignalA appears once in srcA, SignalB once in srcB).
        report.Candidates.Should().HaveCount(2);
        report.Candidates.Should().Contain(c => c.SourceId == "srcB");
        report.Candidates.Should().Contain(c => c.SourceId == "srcA");
    }

    [Fact]
    public void Analyze_NoEvidence_ReturnsEmptyCandidatesWithHonestSummary()
    {
        var faultEvent = new FaultEvent(1.0,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500),
            "test", DateTime.UtcNow);
        var snapshot = new AnchorSnapshot(1.0, 1.5,
            Array.Empty<AnchoredSignalValue>(), DateTime.UtcNow, 1);

        var analyzer = new LocalAnalyzer();
        var report = analyzer.Analyze(Array.Empty<FaultAnalysisEvidence>(), faultEvent, snapshot);

        report.Candidates.Should().BeEmpty();
        report.Summary.Should().Contain("未发现可靠关联");
    }
}
```

- [ ] **Step 2: Run test to verify it fails (RED)**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~LocalAnalyzerTests"
```
Expected: FAIL with `error CS0246: The type or namespace name 'LocalAnalyzer' could not be found`.

- [ ] **Step 3: Write minimal LocalAnalyzer implementation**

Create `src/PeakCan.Host.Core/Analysis/LocalAnalyzer.cs`:
```csharp
namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: produces LocalReport from extracted evidence.
/// Per hard-boundary #14: per-source normalization — score each candidate
/// within its source's own distribution, then rank across sources by the
/// normalized score. A source with 100 transitions does NOT dominate a
/// source with 1 rare transition. Per spec D4: when no candidate scores
/// above threshold, Summary = "未发现可靠关联" — a legal output, not error.</summary>
public sealed class LocalAnalyzer
{
    private const double CandidateThreshold = 0.1;

    public LocalReport Analyze(
        IReadOnlyList<FaultAnalysisEvidence> evidence,
        FaultEvent faultEvent,
        AnchorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(faultEvent);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (evidence.Count == 0)
        {
            return new LocalReport(
                Evidence: evidence,
                Candidates: Array.Empty<CandidateSignal>(),
                DataQualityNotes: new[] { "no evidence extracted within window" },
                Summary: "未发现可靠关联（仅本地特征；无 LLM 归因）",
                GeneratedAtUtc: DateTime.UtcNow);
        }

        // Per-source grouping
        var bySource = evidence
            .GroupBy(e => e.SourceId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var candidates = new List<CandidateSignal>();
        var notes = new List<string>();

        foreach (var (sourceId, sourceEvidence) in bySource)
        {
            // Within source: group by signalKey, count transitions
            var perSignal = sourceEvidence
                .GroupBy(e => e.SignalKey)
                .Select(g => new
                {
                    SignalKey = g.Key,
                    SourceId = sourceId,
                    TransitionCount = g.Count(),
                    EvidenceIds = g.Select(e => e.EvidenceId).ToList(),
                    FirstTs = g.Min(e => e.TimestampSeconds),
                    LastTs = g.Max(e => e.TimestampSeconds),
                })
                .ToList();

            if (perSignal.Count == 0) continue;

            // Per-source normalization: max transitions in this source = 1.0
            int maxInSource = perSignal.Max(p => p.TransitionCount);
            if (maxInSource == 0) continue;

            foreach (var sig in perSignal)
            {
                double score = (double)sig.TransitionCount / maxInSource;
                if (score < CandidateThreshold) continue;

                string reason = $"{sig.TransitionCount} state transitions in window "
                    + $"[{sig.FirstTs:F3}s .. {sig.LastTs:F3}s]";

                candidates.Add(new CandidateSignal(
                    SignalKey: sig.SignalKey,
                    SourceId: sig.SourceId,
                    Score: score,
                    ReasonText: reason,
                    EvidenceIds: sig.EvidenceIds));
            }
        }

        // Cross-source rank by score desc
        candidates = candidates.OrderByDescending(c => c.Score).ToList();

        return new LocalReport(
            Evidence: evidence,
            Candidates: candidates,
            DataQualityNotes: notes,
            Summary: candidates.Count == 0
                ? "未发现可靠关联（仅本地特征；无 LLM 归因）"
                : $"本地推断（无归因）：{candidates.Count} candidate(s)",
            GeneratedAtUtc: DateTime.UtcNow);
    }
}
```

- [ ] **Step 4: Run test to verify it passes (GREEN)**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~LocalAnalyzerTests"
```
Expected: 2 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/Analysis/LocalAnalyzer.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/LocalAnalyzerTests.cs
git commit -m "v3.52.0 T5: LocalAnalyzer per-source normalization + candidate ranking (Core; 2 tests pass)"
```

---

### Task 6: AnalysisSessionRegistry — version-aware session store

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/AnalysisSessionRegistry.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/AnalysisSessionRegistryTests.cs`

**Interfaces:**
- Consumes: Task 5 `LocalAnalyzer` output (`AnalysisSession`)
- Produces:
  - `public sealed class AnalysisSessionRegistry { public AnalysisSession? CurrentSession { get; } public AnalysisSession CreateOrUpdate(AnalysisSession newSession); public void Clear(); }`

- [ ] **Step 1: Write the failing test for version increment + Reset independence**

Create `tests/PeakCan.Host.Core.Tests/Analysis/AnalysisSessionRegistryTests.cs`:
```csharp
using FluentAssertions;
using PeakCan.Host.Core.Analysis;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis;

public class AnalysisSessionRegistryTests
{
    private static AnalysisSession MakeSession(int version) =>
        new(Guid.NewGuid(), version,
            new FaultEvent(1.0, TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(500), "test", DateTime.UtcNow),
            new AnchorSnapshot(1.0, 1.5, Array.Empty<AnchoredSignalValue>(),
                DateTime.UtcNow, 1),
            new LocalReport(Array.Empty<FaultAnalysisEvidence>(),
                Array.Empty<CandidateSignal>(), Array.Empty<string>(),
                "test", DateTime.UtcNow),
            DateTime.UtcNow);

    [Fact]
    public void CreateOrUpdate_FirstCall_StoresSession()
    {
        var registry = new AnalysisSessionRegistry();
        registry.CurrentSession.Should().BeNull();

        var session = registry.CreateOrUpdate(MakeSession(1));
        registry.CurrentSession.Should().Be(session);
    }

    [Fact]
    public void CreateOrUpdate_NewInputs_IncrementsVersion()
    {
        // Per spec hard-boundary #5: changing inputs → new session, never silent overwrite
        var registry = new AnalysisSessionRegistry();
        var v1 = registry.CreateOrUpdate(MakeSession(1));

        // Simulate "user changed fault event time" by passing new session
        var v2 = registry.CreateOrUpdate(MakeSession(2));

        v2.Version.Should().Be(2);
        registry.CurrentSession!.Version.Should().Be(2);
    }

    [Fact]
    public void Clear_ResetsCurrentSession()
    {
        // Per spec hard-boundary #8: AnalysisSessionRegistry is independent
        // of TraceViewerViewModel.Reset, but explicit Clear() is available
        // for when the user explicitly chooses "discard session".
        var registry = new AnalysisSessionRegistry();
        registry.CreateOrUpdate(MakeSession(1));

        registry.Clear();
        registry.CurrentSession.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails (RED)**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~AnalysisSessionRegistryTests"
```
Expected: FAIL with `error CS0246: The type or namespace name 'AnalysisSessionRegistry' could not be found`.

- [ ] **Step 3: Write minimal AnalysisSessionRegistry implementation**

Create `src/PeakCan.Host.Core/Analysis/AnalysisSessionRegistry.cs`:
```csharp
namespace PeakCan.Host.Core.Analysis;

/// <summary>v3.52.0 MINOR: version-aware analysis session store. Independent
/// of TraceViewerViewModel.Reset per hard-boundary #8. CreateOrUpdate
/// increments Version monotonically; consumers compare Version to detect
/// staleness (e.g. when a new anchor snapshot is captured, the previous
/// session is "stale" but still queryable until Clear).</summary>
public sealed class AnalysisSessionRegistry
{
    private AnalysisSession? _current;
    private int _versionCounter;

    public AnalysisSession? CurrentSession => _current;

    public AnalysisSession CreateOrUpdate(AnalysisSession newSession)
    {
        ArgumentNullException.ThrowIfNull(newSession);
        _versionCounter++;
        var stamped = newSession with { Version = _versionCounter };
        _current = stamped;
        return stamped;
    }

    public void Clear() => _current = null;
}
```

- [ ] **Step 4: Run test to verify it passes (GREEN)**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~AnalysisSessionRegistryTests"
```
Expected: 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/Analysis/AnalysisSessionRegistry.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/AnalysisSessionRegistryTests.cs
git commit -m "v3.52.0 T6: AnalysisSessionRegistry version-aware session store (Core; 3 tests pass)"
```

---

### Task 7: AnchorSnapshotFlow partial — LockAnchorCommand + double-anchor validation

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnchorSnapshotFlow.cs`
- Create: `tests/PeakCan.Host.App.Tests/ViewModels/AnchorSnapshotFlowTests.cs`

**Interfaces:**
- Consumes: existing `TraceViewerViewModel._anchorTimestampSeconds` (GreenLineAnchorFlow), `_blueAnchorTimestampSeconds` (BlueLineAnchorFlow), `WatchedSignals`, `Sources`, `MasterSourceId`, `SignalDecoder.TryDecodeEnumText`, `SignalFormatter.FormatValue`
- Produces:
  - `public sealed partial class TraceViewerViewModel` (extended) with:
    - `[RelayCommand] public void LockAnchor()`
    - `[RelayCommand(CanExecute = nameof(CanLockAnchor))] private bool CanLockAnchor()`
    - `public AnchorSnapshot? CurrentAnchorSnapshot { get; private set; }`

- [ ] **Step 1: Write the failing test for double-anchor validation + snapshot creation**

Create `tests/PeakCan.Host.App.Tests/ViewModels/AnchorSnapshotFlowTests.cs`:
```csharp
using FluentAssertions;
using NSubstitute;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;
using PeakCan.Host.App.Services.Trace;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public class AnchorSnapshotFlowTests
{
    [Fact]
    public void CanLockAnchor_GreenOnly_ReturnsFalse()
    {
        // Per spec D2: blue anchor missing → cannot lock
        var vm = MakeVm(greenSet: true, blueSet: false);
        vm.LockAnchorCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void CanLockAnchor_BothAnchors_ReturnsTrue()
    {
        var vm = MakeVm(greenSet: true, blueSet: true);
        vm.LockAnchorCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void LockAnchor_BothSet_CreatesSnapshot()
    {
        var vm = MakeVm(greenSet: true, blueSet: true);
        vm.LockAnchorCommand.Execute(null);
        vm.CurrentAnchorSnapshot.Should().NotBeNull();
    }

    [Fact]
    public void LockAnchor_BlueMissing_SetsErrorMessageAndDoesNotCreate()
    {
        var vm = MakeVm(greenSet: true, blueSet: false);
        vm.LockAnchorCommand.Execute(null);
        vm.CurrentAnchorSnapshot.Should().BeNull();
        vm.ErrorMessage.Should().Contain("比较锚");
    }

    // Helper to construct a minimal VM; full construction is in T9.
    // For T7 we only need: green/blue timestamps settable + LockAnchorCommand + ErrorMessage.
    private static TraceViewerViewModel MakeVm(bool greenSet, bool blueSet)
    {
        // Use real VM (no mocking — partial-class visibility makes internal fields accessible)
        var registry = Substitute.For<ITraceSessionRegistry>();
        var dbc = Substitute.For<DbcService>();
        var sessionLib = Substitute.For<TraceSessionLibrary>(null, null);
        var logger = NullLogger<TraceViewerViewModel>.Instance;

        var vm = new TraceViewerViewModel(registry, dbc, sessionLib, logger);
        if (greenSet) vm.RefreshAtAnchor(1.0);
        if (blueSet) vm.RefreshAtAnchorBlue(1.5);
        return vm;
    }
}
```

NOTE: This test depends on the TraceViewerViewModel constructor signature from T9. Implementer will need to confirm the ctor signature; if it differs, adjust the helper. This is acceptable because Task 9 (AnalysisFlow + DI wiring) lands the ctor signature in this same plan.

- [ ] **Step 2: Run test to verify it fails (RED)**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~AnchorSnapshotFlowTests"
```
Expected: FAIL with `error CS0117: 'TraceViewerViewModel' does not contain a definition for 'LockAnchorCommand'` (or similar).

- [ ] **Step 3: Write minimal AnchorSnapshotFlow implementation**

Create `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnchorSnapshotFlow.cs`:
```csharp
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Dbc;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    /// <summary>v3.52.0 MINOR: bindable snapshot of the current green+blue
    /// anchor state. Null until the user clicks "锁定 anchor 状态" and both
    /// anchors are set. Independent of TraceViewerViewModel.Reset per
    /// hard-boundary #8.</summary>
    public AnchorSnapshot? CurrentAnchorSnapshot { get; private set; }

    /// <summary>CanExecute for LockAnchorCommand: both anchors must be set
    /// (per spec D2). UI binds button.IsEnabled via the command's CanExecute.</summary>
    private bool CanLockAnchor() =>
        IsGreenLineAnchorActive && IsBlueLineAnchorActive;

    [RelayCommand(CanExecute = nameof(CanLockAnchor))]
    private void LockAnchor()
    {
        // D2 enforcement: refuse if blue anchor missing
        if (!IsBlueLineAnchorActive)
        {
            ErrorMessage = "请先设比较锚（拖动 ● 比较 锚点到对照时刻），然后再锁定 anchor 状态";
            return;
        }

        // Build AnchoredSignalValue list from current WatchedSignals
        var anchored = new List<AnchoredSignalValue>();
        foreach (var row in WatchedSignals)
        {
            if (row.IsPlaceholder) continue;
            anchored.Add(new AnchoredSignalValue(
                SignalKey: row.SignalKey,
                SourceId: row.SourceId ?? "",
                LatestValue: row.LatestValue,
                BlueLatestValue: row.BlueLatestValue,
                DeltaValue: row.DeltaValue,
                LatestText: row.LatestText,
                BlueText: row.BlueText,
                DeltaText: row.DeltaText));
        }

        CurrentAnchorSnapshot = new AnchorSnapshot(
            GreenTimestampSeconds: _anchorTimestampSeconds,
            BlueTimestampSeconds: _blueAnchorTimestampSeconds,
            Signals: anchored,
            CapturedAtUtc: DateTime.UtcNow,
            Version: 1);

        OnPropertyChanged(nameof(CurrentAnchorSnapshot));
        ErrorMessage = null;
    }
}
```

- [ ] **Step 4: Run test to verify it passes (GREEN)**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~AnchorSnapshotFlowTests"
```
Expected: 4 tests pass (if ctor signature matches T9's expectation).

If ctor signature differs: implementer must adjust T9's DI wiring first, then re-run T7. This is the documented dependency between T7 and T9.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnchorSnapshotFlow.cs \
        tests/PeakCan.Host.App.Tests/ViewModels/AnchorSnapshotFlowTests.cs
git commit -m "v3.52.0 T7: AnchorSnapshotFlow partial — LockAnchorCommand with double-anchor validation (App; 4 tests)"
```

---

### Task 8: AnalysisFlow partial — RunAnalysisCommand + session creation

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnalysisFlow.cs`
- Create: `tests/PeakCan.Host.App.Tests/ViewModels/AnalysisFlowTests.cs`

**Interfaces:**
- Consumes: Task 7 `CurrentAnchorSnapshot`, Task 4-6 Core types (`EvidenceExtractor`, `LocalAnalyzer`, `AnalysisSessionRegistry`), `_registry` (existing)
- Produces:
  - `public sealed partial class TraceViewerViewModel` (extended) with:
    - `[RelayCommand(CanExecute = nameof(CanRunAnalysis))] public async Task RunAnalysisAsync()`
    - `public AnalysisSession? CurrentAnalysisSession { get; private set; }`
    - `public string LlmProviderDisplayName => _llmProvider.DisplayName;`

- [ ] **Step 1: Write the failing test for full session creation flow**

Create `tests/PeakCan.Host.App.Tests/ViewModels/AnalysisFlowTests.cs`:
```csharp
using FluentAssertions;
using NSubstitute;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;
using PeakCan.Host.App.Services.Trace;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

public class AnalysisFlowTests
{
    [Fact]
    public async Task RunAnalysisAsync_NoAnchorSnapshot_SetsErrorMessage()
    {
        var vm = MakeVm();
        await vm.RunAnalysisAsync();
        vm.CurrentAnalysisSession.Should().BeNull();
        vm.ErrorMessage.Should().Contain("锁定");
    }

    [Fact]
    public async Task RunAnalysisAsync_WithAnchor_CreatesSession()
    {
        var vm = MakeVm();
        vm.RefreshAtAnchor(1.0);
        vm.RefreshAtAnchorBlue(1.5);
        vm.LockAnchorCommand.Execute(null);
        vm.CurrentAnchorSnapshot.Should().NotBeNull();

        await vm.RunAnalysisAsync();
        vm.CurrentAnalysisSession.Should().NotBeNull();
    }

    private static TraceViewerViewModel MakeVm()
    {
        var registry = Substitute.For<ITraceSessionRegistry>();
        var dbc = Substitute.For<DbcService>();
        var sessionLib = Substitute.For<TraceSessionLibrary>(null, null);
        var logger = NullLogger<TraceViewerViewModel>.Instance;
        return new TraceViewerViewModel(registry, dbc, sessionLib, logger);
    }
}
```

- [ ] **Step 2: Run test to verify it fails (RED)**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~AnalysisFlowTests"
```
Expected: FAIL with `error CS0117: 'TraceViewerViewModel' does not contain a definition for 'RunAnalysisAsync'`.

- [ ] **Step 3: Write minimal AnalysisFlow implementation**

Create `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnalysisFlow.cs`:
```csharp
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.Core.Analysis;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    // === Injected via ctor (Task 9 wires DI) ===
    private readonly EvidenceExtractor _evidenceExtractor = null!;
    private readonly LocalAnalyzer _localAnalyzer = null!;
    private readonly AnalysisSessionRegistry _sessionRegistry = null!;
    private readonly ILlmProvider _llmProvider = null!;

    /// <summary>v3.52.0 MINOR: bindable current analysis session. Null until
    /// RunAnalysisAsync succeeds. UI binds the AI Analysis tab to this.</summary>
    public AnalysisSession? CurrentAnalysisSession { get; private set; }

    /// <summary>P0: returns "(no LLM — P0 local-only)" until P1 PATCH.</summary>
    public string LlmProviderDisplayName => _llmProvider.DisplayName;

    private bool CanRunAnalysis() =>
        CurrentAnchorSnapshot is not null && !IsLoading;

    [RelayCommand(CanExecute = nameof(CanRunAnalysis))]
    public async Task RunAnalysisAsync()
    {
        if (CurrentAnchorSnapshot is null)
        {
            ErrorMessage = "请先设绿/蓝锚并点『锁定 anchor 状态』";
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = "分析中…";

            // Build a fault event from the anchor snapshot center.
            // Center = midpoint of green + blue timestamps.
            double center = (CurrentAnchorSnapshot.GreenTimestampSeconds
                + CurrentAnchorSnapshot.BlueTimestampSeconds) / 2.0;
            var faultEvent = new FaultEvent(
                CenterTimestampSeconds: center,
                WindowBefore: TimeSpan.FromMilliseconds(500),
                WindowAfter: TimeSpan.FromMilliseconds(500),
                Description: $"auto-derived from anchors [{CurrentAnchorSnapshot.GreenTimestampSeconds:F3} .. {CurrentAnchorSnapshot.BlueTimestampSeconds:F3}]",
                CreatedAtUtc: DateTime.UtcNow);

            // Extract evidence per-source
            var evidence = _evidenceExtractor.Extract(
                faultEvent, CurrentAnchorSnapshot, _registry,
                dbc: null,  // P0: enum decode via SignalDecoder in Core not yet wired; can be added in PATCH
                dbcIdToSourceIdMap: new Dictionary<uint, string>());

            // Local analyze
            var report = _localAnalyzer.Analyze(evidence, faultEvent, CurrentAnchorSnapshot);

            // Register session (version increments)
            CurrentAnalysisSession = _sessionRegistry.CreateOrUpdate(new AnalysisSession(
                SessionId: Guid.NewGuid(),
                Version: 0,  // registry stamps the real version
                FaultEvent: faultEvent,
                AnchorSnapshot: CurrentAnchorSnapshot,
                Report: report,
                CreatedAtUtc: DateTime.UtcNow));

            // Attempt LLM analysis (P0: stub throws, fall back to local)
            try
            {
                await _llmProvider.AnalyzeAsync(CurrentAnalysisSession, CancellationToken.None);
            }
            catch (NotImplementedException)
            {
                // P0 expected: ignore; UI checks DisplayName to suppress LLM section
            }

            OnPropertyChanged(nameof(CurrentAnalysisSession));
            StatusMessage = "分析完成";
        }
        catch (Exception ex)
        {
            LogAnalysisFailed(_logger, ex);
            ErrorMessage = $"分析失败: {ex.Message}";
            StatusMessage = "分析失败";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "AI analysis failed")]
    private static partial void LogAnalysisFailed(ILogger logger, Exception ex);
}
```

- [ ] **Step 4: Run test to verify it passes (GREEN)**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~AnalysisFlowTests"
```
Expected: 2 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/AnalysisFlow.cs \
        tests/PeakCan.Host.App.Tests/ViewModels/AnalysisFlowTests.cs
git commit -m "v3.52.0 T8: AnalysisFlow partial — RunAnalysisAsync + session lifecycle (App; 2 tests)"
```

---

### Task 9: DI wiring — AppServicesFlow.cs registrations + TraceViewerViewModel ctor

**Files:**
- Modify: `src/PeakCan.Host.App/Composition/AppServicesFlow.cs` (add ~15 LoC registrations)
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` (extend ctor with 4 new params)
- Modify: `tests/PeakCan.Host.App.Tests/ViewModels/AnchorSnapshotFlowTests.cs` + `AnalysisFlowTests.cs` (if helper signatures need adjusting)

**Interfaces:**
- Consumes: Tasks 7+8 (new ctor params)
- Produces:
  - `TraceViewerViewModel` ctor extended with 4 new params: `EvidenceExtractor evidenceExtractor, LocalAnalyzer localAnalyzer, AnalysisSessionRegistry sessionRegistry, ILlmProvider llmProvider`
  - DI: `AddSingleton<EvidenceExtractor>()`, `AddSingleton<LocalAnalyzer>()`, `AddSingleton<AnalysisSessionRegistry>()`, `AddSingleton<ILlmProvider, NotImplementedLlmProvider>()`

- [ ] **Step 1: Read current AppServicesFlow.cs to find the TraceViewerViewModel registration**

Run:
```bash
grep -n "TraceViewerViewModel\|AddSingleton\|BuildServiceProvider" src/PeakCan.Host.App/Composition/AppServicesFlow.cs
```
Expected: list of line numbers showing where TraceViewerViewModel is registered.

- [ ] **Step 2: Modify AppServicesFlow.cs to add 4 new registrations**

Find the TraceViewerViewModel registration block (likely around line ~140-170). Add BEFORE it:

```csharp
// v3.52.0 MINOR: AI inference analysis pipeline (P0 local-only)
services.AddSingleton<PeakCan.Host.Core.Analysis.EvidenceExtractor>();
services.AddSingleton<PeakCan.Host.Core.Analysis.LocalAnalyzer>();
services.AddSingleton<PeakCan.Host.Core.Analysis.AnalysisSessionRegistry>();
services.AddSingleton<PeakCan.Host.Core.Analysis.ILlmProvider, PeakCan.Host.Core.Analysis.NotImplementedLlmProvider>();
```

- [ ] **Step 3: Modify TraceViewerViewModel.cs ctor**

Find the existing ctor (likely at L119-167 based on memory from D5 plan, but verify with grep). Append 4 new readonly fields and 4 new ctor params:

After existing ctor closing brace, add:
```csharp
private readonly EvidenceExtractor _evidenceExtractor;
private readonly LocalAnalyzer _localAnalyzer;
private readonly AnalysisSessionRegistry _sessionRegistry;
private readonly ILlmProvider _llmProvider;
```

Extend the existing ctor to accept these 4 params (append at end, before closing brace):
```csharp
ArgumentNullException.ThrowIfNull(evidenceExtractor);
ArgumentNullException.ThrowIfNull(localAnalyzer);
ArgumentNullException.ThrowIfNull(sessionRegistry);
ArgumentNullException.ThrowIfNull(llmProvider);
_evidenceExtractor = evidenceExtractor;
_localAnalyzer = localAnalyzer;
_sessionRegistry = sessionRegistry;
_llmProvider = llmProvider;
```

(Implementation note: implementer must verify the exact ctor shape in the existing file. The plan assumes the existing 4-param ctor shape from W24 sister; adjust if different.)

- [ ] **Step 4: Run all App-layer tests to verify they pass**

Run:
```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo -c Debug --filter "FullyQualifiedName~AnchorSnapshotFlowTests|FullyQualifiedName~AnalysisFlowTests" --logger "console;verbosity=minimal"
```
Expected: build succeeds with 0 warnings; 4 + 2 = 6 tests pass.

- [ ] **Step 5: Run full App test suite to verify no regression**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo -c Debug --logger "console;verbosity=minimal"
```
Expected: pre-existing 800 tests still pass + 6 new tests pass = 806 total.

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.App/Composition/AppServicesFlow.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs
git commit -m "v3.52.0 T9: DI wiring — Analysis pipeline singletons + TraceViewerViewModel ctor extended (App)"
```

---

### Task 10: XAML — AI Analysis panel Tab

**Files:**
- Create: `src/PeakCan.Host.App/Views/TraceViewerView.AIPanel.xaml`
- Modify: `src/PeakCan.Host.App/Views/TraceViewerView.xaml` (add 1 TabItem)
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` (add `AIPanelContent` property — ContentControl wraps the UserControl loaded from XAML)

- [ ] **Step 1: Create TraceViewerView.AIPanel.xaml**

Create `src/PeakCan.Host.App/Views/TraceViewerView.AIPanel.xaml`:
```xml
<UserControl x:Class="PeakCan.Host.App.Views.TraceViewerViewAIPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:analysis="clr-namespace:PeakCan.Host.Core.Analysis;assembly=PeakCan.Host.Core"
             d:DataContext="{d:DesignInstance Type=vm:TraceViewerViewModel}"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             mc:Ignorable="d">
    <Grid>
        <!-- Empty state: no analysis session yet -->
        <TextBlock Text="请先拖绿/蓝锚到故障时刻和对照时刻，然后点『锁定 anchor 状态』，再点『运行分析』"
                   Foreground="Gray"
                   HorizontalAlignment="Center"
                   VerticalAlignment="Center"
                   TextWrapping="Wrap"
                   Visibility="{Binding CurrentAnalysisSession, Converter={StaticResource NullToVis}}"/>

        <!-- Active session view -->
        <ScrollViewer VerticalScrollBarVisibility="Auto"
                      Visibility="{Binding CurrentAnalysisSession, Converter={StaticResource NotNullToVis}}">
            <StackPanel Margin="8">
                <!-- Honest banner: no LLM attribution in P0 -->
                <Border Background="#FFF3CD" BorderBrush="#FFC107" BorderThickness="1"
                        Padding="8" Margin="0,0,0,8" CornerRadius="3">
                    <TextBlock Text="本地推断（无归因；无 LLM 验证）"
                               FontWeight="SemiBold"
                               Foreground="#856404"
                               ToolTip="P0 仅本地确定性特征 + 候选排序。归因需 P1 LLM Provider。" />
                </Border>

                <!-- FaultEvent header -->
                <TextBlock Text="{Binding CurrentAnalysisSession.FaultEvent.Description}"
                           FontWeight="SemiBold" />
                <TextBlock Text="{Binding CurrentAnalysisSession.FaultEvent.CenterTimestampSeconds, StringFormat='Center: {0:F3}s'}"
                           Foreground="Gray" />

                <!-- Evidence list -->
                <TextBlock Text="Evidence" FontWeight="SemiBold" Margin="0,8,0,4" />
                <DataGrid ItemsSource="{Binding CurrentAnalysisSession.Report.Evidence}"
                          AutoGenerateColumns="False" IsReadOnly="True"
                          MaxHeight="200" RowHeight="22">
                    <DataGrid.Columns>
                        <DataGridTextColumn Header="ID" Binding="{Binding EvidenceId}" Width="60" />
                        <DataGridTextColumn Header="Source" Binding="{Binding SourceId}" Width="80" />
                        <DataGridTextColumn Header="Type" Binding="{Binding Type}" Width="120" />
                        <DataGridTextColumn Header="Value" Binding="{Binding Value}" Width="60" />
                        <DataGridTextColumn Header="Description" Binding="{Binding Description}" Width="*" />
                    </DataGrid.Columns>
                </DataGrid>

                <!-- Candidates -->
                <TextBlock Text="Candidate signals (per-source normalized)"
                           FontWeight="SemiBold" Margin="0,8,0,4" />
                <DataGrid ItemsSource="{Binding CurrentAnalysisSession.Report.Candidates}"
                          AutoGenerateColumns="False" IsReadOnly="True"
                          MaxHeight="200" RowHeight="22">
                    <DataGrid.Columns>
                        <DataGridTextColumn Header="Source" Binding="{Binding SourceId}" Width="80" />
                        <DataGridTextColumn Header="Signal" Binding="{Binding SignalKey}" Width="200" />
                        <DataGridTextColumn Header="Score" Binding="{Binding Score, StringFormat='F2'}" Width="60" />
                        <DataGridTextColumn Header="Reason" Binding="{Binding ReasonText}" Width="*" />
                    </DataGrid.Columns>
                </DataGrid>

                <!-- Summary -->
                <TextBlock Text="{Binding CurrentAnalysisSession.Report.Summary}"
                           Margin="0,8,0,0"
                           Foreground="Gray"
                           FontStyle="Italic" />
            </StackPanel>
        </ScrollViewer>
    </Grid>
</UserControl>
```

Add code-behind `src/PeakCan.Host.App/Views/TraceViewerView.AIPanel.xaml.cs`:
```csharp
using System.Windows.Controls;

namespace PeakCan.Host.App.Views;

public partial class TraceViewerViewAIPanel : UserControl
{
    public TraceViewerViewAIPanel() => InitializeComponent();
}
```

- [ ] **Step 2: Modify TraceViewerView.xaml — add the TabItem**

Find the existing TabControl in TraceViewerView.xaml (around line 257-338). Add a 3rd TabItem AFTER the Sampling Table tab (before closing `</TabControl>`):

```xml
<!-- Tab 3: AI Analysis (v3.52.0 MINOR). Sister of Watch List + Sampling Table.
     Per spec: local-only P0, no LLM attribution in this version. -->
<TabItem Header="AI Analysis">
    <ContentControl Content="{Binding AIPanelContent}" />
</TabItem>
```

- [ ] **Step 3: Add AIPanelContent property to TraceViewerViewModel**

Modify `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` — add property (in main or new AnalysisFlow.cs):

```csharp
private TraceViewerViewAIPanel? _aiPanelContent;
public TraceViewerViewAIPanel? AIPanelContent
{
    get
    {
        if (_aiPanelContent is null)
        {
            // v3.52.0: lazy-load the panel UserControl
            _aiPanelContent = new TraceViewerViewAIPanel { DataContext = this };
        }
        return _aiPanelContent;
    }
}
```

- [ ] **Step 4: Build to verify XAML compiles**

Run:
```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug
```
Expected: build succeeds with 0 errors. (XAML warnings possible if d:DesignInstance can't resolve vm type — those are pre-existing benign warnings.)

- [ ] **Step 5: Run full App test suite**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo -c Debug --logger "console;verbosity=minimal"
```
Expected: 806 tests pass (no regression from T9).

- [ ] **Step 6: Commit**

```bash
git add src/PeakCan.Host.App/Views/TraceViewerView.AIPanel.xaml \
        src/PeakCan.Host.App/Views/TraceViewerView.AIPanel.xaml.cs \
        src/PeakCan.Host.App/Views/TraceViewerView.xaml \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs
git commit -m "v3.52.0 T10: AI Analysis panel XAML Tab + AIPanelContent VM property (App)"
```

---

### Task 11: LockAnchor + RunAnalysis toolbar buttons

**Files:**
- Modify: `src/PeakCan.Host.App/Views/TraceViewerView.xaml` (add 2 buttons)

- [ ] **Step 1: Find the toolbar StackPanel in TraceViewerView.xaml**

Run:
```bash
grep -n "Anchor visibility toggles\|当前\|比较" src/PeakCan.Host.App/Views/TraceViewerView.xaml | head -10
```
Expected: shows line ~100-108 for the ● 当前 / ● 比较 toggle buttons.

- [ ] **Step 2: Add 2 buttons after the comparison toggle**

After the `● 比较` ToggleButton (~L107), add:

```xml
<Separator />
<!-- v3.52.0 MINOR: Lock anchor + Run analysis. Per spec D2: 锁定 requires
     both green AND blue anchors set; button greys out until then. -->
<Button Content="🔒 锁定 anchor 状态" Command="{Binding LockAnchorCommand}"
        Padding="8,2" Margin="4,0,0,0"
        ToolTip="锁定当前绿/蓝锚状态为 evidence snapshot 起点（必须双锚齐全）" />
<Button Content="🤖 运行分析" Command="{Binding RunAnalysisCommand}"
        Padding="8,2" Margin="4,0,0,0"
        ToolTip="运行本地 AI 推理分析（P0 仅本地特征；P1 接 LLM）" />
```

- [ ] **Step 3: Build + test**

Run:
```bash
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --nologo -c Debug --logger "console;verbosity=minimal"
```
Expected: 806 tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/PeakCan.Host.App/Views/TraceViewerView.xaml
git commit -m "v3.52.0 T11: toolbar — Lock anchor + Run analysis buttons (App XAML)"
```

---

### Task 12: Full solution CI verification + coverage check

- [ ] **Step 1: Run full solution build**

```bash
dotnet build PeakCan.Host.sln -c Debug
```
Expected: 0 errors, 0 warnings (in code we touched).

- [ ] **Step 2: Run full solution tests**

```bash
dotnet test PeakCan.Host.sln --no-build --nologo -c Debug --logger "console;verbosity=minimal"
```
Expected: all tests pass; total count = pre-existing + 11 new (4 AnchorSnapshot + 1 FaultEvent + 1 FaultAnalysisEvidence + 2 LocalReport + 1 AnalysisSession + 2 ILlmProvider + 2 EvidenceExtractor + 2 LocalAnalyzer + 3 AnalysisSessionRegistry + 4 AnchorSnapshotFlow + 2 AnalysisFlow = 25 new tests, but the count in step 1 (Core Analysis tests) was 14, and App tests were 6, so ~20 new tests).

- [ ] **Step 3: Coverage report for new code**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo -c Debug --collect:"XPlat Code Coverage" --filter "FullyQualifiedName~Analysis"
```
Expected: coverage ≥ 80% on new files (per-project rule).

If coverage < 80%: add targeted tests for uncovered branches (e.g. dbc=null in EvidenceExtractor, edge cases in LocalAnalyzer).

- [ ] **Step 4: Commit coverage report**

```bash
git add TestResults/  # coverage artifacts
git commit -m "v3.52.0 T12: coverage report for Analysis pipeline (≥80%)" || true
```

---

### Task 13: Release notes + tier-3 ship

- [ ] **Step 1: Write release notes at `docs/release-notes/v3-52-0-minor.md`**

Per project convention. Cover:
- v3.52.0 MINOR AI 推理 v1 — local evidence + AnalysisSession + AI Analysis panel
- P0 范围（本地特征 + 候选，无归因；ILlmProvider 接口预留）
- 双锚锁定语义（必须双锚；蓝锚缺失时弹提示）
- 6 NEW 1/3 lesson candidates observed
- 关联 P1 PATCH 计划（DeepSeek + CredentialManager + JSON Schema）

- [ ] **Step 2: Bump version in csproj files**

Run:
```bash
grep -rn "Version>3\." src/PeakCan.Host.App/PeakCan.Host.App.csproj src/PeakCan.Host.Core/PeakCan.Host.Core.csproj
```
Then edit each `<Version>X.Y.Z</Version>` from current to `3.52.0`.

- [ ] **Step 3: Tier-3 ship (per CLAUDE.md Tier-3 rules)**

```bash
git push -u origin feature/v3-52-0-ai-inference-v1
gh pr create --base main --title "v3.52.0 MINOR: AI 推理 v1 (local evidence + AnalysisSession + AI Analysis panel)"
gh pr merge --squash --delete-branch
git tag v3.52.0
git push origin v3.52.0
gh release create v3.52.0 --notes-file docs/release-notes/v3-52-0-minor.md
```

---

## Sister-lesson candidates to monitor

| Lesson | Status | What T1-T13 might observe |
|---|---|---|
| `anchorsnapshot-must-not-hold-signal-or-dbcdocument-references` | NEW 1/3 | T7: snapshot stores raw values only (record + enum text string + signalKey) |
| `analysis-session-lifecycle-must-be-independent-of-vm-reset` | NEW 1/3 | T8: AnalysisSessionRegistry is a separate singleton, not cleared by VM Reset |
| `lock-anchor-snapshot-must-validate-both-anchors-present-before-snapshot` | NEW 1/3 | T7: CanLockAnchor = green AND blue; UI greys button out; if forced via command, ErrorMessage set |
| `per-source-normalization-required-for-cross-source-candidate-ranking` | NEW 1/3 | T5: LocalAnalyzer normalizes per source |
| `local-report-must-explicitly-mark-no-attribution-no-llm` | NEW 1/3 | T10: yellow banner in AI Analysis panel |
| `notimplementedllmprovider-stub-must-not-crash-ui-when-dispatched` | NEW 1/3 | T8: catch NotImplementedException gracefully |

## Verification

- `dotnet build PeakCan.Host.sln`: 0 errors, 0 warnings on new code
- `dotnet test PeakCan.Host.sln`: 0 new failures; ≥20 new tests pass
- Coverage on new files ≥ 80% (per-project rule)
- No `IReplayService` usage in new code (hard-boundary #3)
- No `LoadedFrames` direct traversal (hard-boundary #6)
- `AnchorSnapshot` holds no `Signal` / `DbcDocument` / `WatchedSignalRow` (hard-boundary #10)
- Tag v3.52.0 + GH release published

## Out of scope (YAGNI)

- Concrete LLM Provider implementations (DeepSeek, Azure, Ollama) → P1 PATCH
- API Key secure storage (CredentialManager / DPAPI) → P1 PATCH
- JSON Schema validation + Evidence ID whitelist filtering → P1 PATCH
- `LocalAnalyzer` cycle-anomaly / frame-loss / out-of-range detectors → P1 PATCH (P0 only does state-transition)
- `.tmtrace` session persistence → D5 decision (never persisted)
- Streaming LLM / tool calling → P2 PATCH (or later)
- Local model Provider → not planned
- AI controlling CAN / UDS / DBC modifications → explicitly forbidden by hard-boundary #3