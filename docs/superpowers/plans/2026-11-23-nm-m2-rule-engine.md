# NM M2 规则引擎实施计划（S1 补齐 → S4）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 NM 分析在选定企标 profile 后，能按条款产出「违规 finding」与「观测事实 observation」两类结果，并在无 profile 时与 M1 输出逐字节一致。

**Architecture:** 在现有 `NmAnalyzer` 门面上加一层规则引擎：profile 经 YAML 加载 → 三层展平 → 闭集原语逐条求值 → `NmRuleResult(Findings, Observations, Unmodeled, Truncations)`。违规与事实严格分离（`NmFinding` vs `NmObservation`），不可观测条款只进 `NmUnmodeledRule`。所有时序阈值统一走 `NmEffectiveTiming`，禁止分析阶段再写 `?? 500` / `?? 2000`。

**Tech Stack:** C# / .NET 10、xUnit、FluentAssertions、YamlDotNet 18.1.0（已在 `Directory.Packages.props` 声明）、WPF（仅 S7，本计划不含）

**Spec:** `docs/superpowers/specs/2026-11-23-nm-m2-oem-standard-design.md`（Rev 5）

## Global Constraints

- **注释语言**：业务/用户可见注释用中文；技术 API、协议字段用英文。
- **禁止静默降级**：捕获异常要么处理，要么抛 `NmProfileValidationException` 上报。未知枚举值、未知原语一律报错或进 `Unmodeled`，不得静默当成默认值。
- **无 profile 时行为不变**：`NmSpecClosureTests`（496 行）与 M1 全部 169 项 NM 测试必须保持绿。任何一处输出漂移都算回归。
- **只跑定向测试**：`dotnet test ... --filter "FullyQualifiedName~Nm"`。**绝不跑全量测试套件**，绝不逐条排查全量失败清单。
- **commit 需用户明确同意**：每个 Task 的提交步骤必须先向用户确认，不得自行提交或推送。
- **代码中不得出现裸位掩码常量** `0x40` / `0x10` 表示 CBV 语义（spec §13 风险表）。位语义只能来自 profile 的 `bit_roles`。
- **`NmSegment` 三字段（`Evidence`/`IsWindowTruncated`/`StateBoundaryKnown`）归 S6 所有**，本计划任何 Task 不得新增或引用它们。
- **工作区共享**：`peakcan-host` 可能被并行会话（`claude-proj2-1a`）同时修改。每个 Task 开始前先 `git status --short` 确认没有他人未提交的改动混入。

## Review Focus

按 spec 推断、但没有任何 Task 的测试直接覆盖的失败模式，按可能性从高到低：

1. **profile 里写了规则但原语名拼错** → 用户以为条款已被验证，实际该规则进了 `Unmodeled` 且界面上不显眼。期望行为：原语不在闭集时该规则必须出现在 `UnmodeledRules` 里并带条款号，且报告顶部有可见提示。
2. **`bit_rule` 作用于 DLC 不足的帧** → 部分帧字节越界，实现若整段跳过会让整条规则静默不生效。期望行为：越界帧单独计数并产 `bit_rule_unreadable` observation（不是 finding），合法帧仍照常判定。
3. **同一 CAN ID 多 NID 交替发送（共享 ID）** → 按报文切段会把两个节点的状态混成一条。期望行为：`nmsts_transition` 与 `bit_rule` 在检测到共享 ID 时抑制 finding，只产 observation 并标 `inferred`。
4. **profile 指定了 `nmsts` 位但该位在 DBC 里是有符号或位宽≠1** → 若直接当单 bit 读，会得到反向值。期望行为：校验器在加载期拒绝该 profile。**本计划只能覆盖 profile 自身的 `bit ∈ [0,7]`**；DBC 侧的位宽/符号校验要等 profile 与 DBC 合并时（S6）才有输入，此处必须如实标注为未覆盖。
5. **`repeat_message_duration` 的锚点落在窗口边界上** → 段被窗口切断，实测时长只是下限。期望行为：产 `Indeterminate` 而不是 `Exceeded`。

---

## Task 0: 固化已有 S1/S2 变更（既成事实，不是计划产出）

**这不是计划的一部分，而是对流程偏差的补救。**

superpowers 的 architectural 路径要求「写 spec → 用户审 spec → 写实施计划 → 用户批计划 → 才开始写代码」。S1 与 S2 的代码是在**没有实施计划**的情况下先写出来的：用户回「2」（在我当时给的三个选项里选「直接开 S1」），我把这当成批准跳过了计划环节。

所以：**Task 1 起才是本计划真正约束的产出。** Task 0 只是把那批已有代码纳入版本控制，让它成为后续 Task 的干净基线。

用户如果认为 S1/S2 该重做，应在 Task 0 之前叫停——Task 1 之后的改动全部建立在它们之上。

**背景**：工作区有 6 个已修改文件 + 11 个未跟踪文件（S1/S2 成果），从未提交。后续 Task 全部建立在它们之上，必须先固化。

**Files:**
- 已修改：`Directory.Packages.props`、`src/PeakCan.Host.Core/PeakCan.Host.Core.csproj`、`NmAnalyzer.cs`、`NmContentAnalyzer.cs`、`NmCycleAnalyzer.cs`、`NmGapAnalyzer.cs`、`NmProfile.cs`
- 未跟踪：`NmStandardFlattener.cs`、`NmStandardProfile.cs`、`NmStandardValidator.cs`、`NmStandardYaml.cs`、`NmTiming.cs`、5 个测试文件、spec 文档
- 需排除：`.tmp/`（临时产物）

**Interfaces:**
- Consumes: 无
- Produces: 干净的基线，后续所有 Task 从 `main` 的该 commit 出发

- [ ] **Step 1: 确认工作区没有并行会话的改动**

```bash
cd /d/claude_proj2/peakcan-host && git status --short && git branch --show-current
```

预期：分支 `main`，变更列表与上文一致。若出现本计划未列出的文件，**停下来问用户**——可能是 `claude-proj2-1a` 的未提交工作。

- [ ] **Step 2: 把 `.tmp/` 排除出提交范围**

```bash
cd /d/claude_proj2/peakcan-host && grep -n "^\.tmp" .gitignore || echo ".tmp/" >> .gitignore
```

- [ ] **Step 3: 跑定向测试确认基线绿**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~Analysis.Nm" -v minimal
```

预期：`Passed!  - Failed: 0, Passed: 205`

若失败：**不要**继续后续 Task。把失败输出原样贴给用户。

- [ ] **Step 4: 向用户确认后提交**

```bash
cd /d/claude_proj2/peakcan-host && git add Directory.Packages.props \
  src/PeakCan.Host.Core/PeakCan.Host.Core.csproj \
  src/PeakCan.Host.Core/Analysis/Nm/NmStandardFlatten*.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmStandardProfile.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmStandardValidator.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmStandardYaml.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmTiming.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmAnalyzer.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmContentAnalyzer.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmCycleAnalyzer.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmGapAnalyzer.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmProfile.cs \
  tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmStandard*.cs \
  tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmTiming*.cs \
  docs/superpowers/specs/2026-11-23-nm-m2-oem-standard-design.md \
  .gitignore
```

**提交前必须得到用户明确的「提交」指令。** 得到后：

```bash
git commit -m "$(cat <<'EOF'
feat(nm): add OEM standard profile loading, flattening and unified timing

- NmStandardProfile / NmStandardYaml / NmStandardFlattener / NmStandardValidator:
  strict YAML load, three-layer inheritance, three-state patch (set/remove)
- NmTiming: single default-injection point; M1 project layer > OEM > defaults
- NmAnalyzer / NmGapAnalyzer / NmContentAnalyzer / NmCycleAnalyzer migrated to
  NmEffectiveTiming; no-profile path keeps M1 behaviour byte-for-byte

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 1: 报告契约三类型 + 静默判定器

**背景**：spec §7.1.1 要求 `NmObservation` / `NmUnmodeledRule` 进 `NmReport`，§7.1.2 要求 `SilentBeyondTimeout` 从 bool 改为四态。现有 `NmMessageReport.SilentBeyondTimeout` 只看「末帧到窗口末尾的时长」，不检查其它 NM 是否仍在发——真实 trace 的全网静默会被它标成 `true` 并发给 AI，这是误报源头。

**Files:**
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmReport.cs:19-41,93-114`
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmSilenceClassifier.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmSilenceClassifierTests.cs`

**Interfaces:**
- Consumes: `NmFrame`、`NmEpisode`（已有）
- Produces:
  - `enum NmSilenceVerdict { NotSilent, SilentWithOtherNmActive, AllNmSilent, IndeterminateAtBoundary }`
  - `record NmObservation(string Kind, string? Node, string? CanIdHex, double Start, double End, string Detail, string EvidenceKind, bool IsInference = false, string? RuleId = null)`
  - `record NmUnmodeledRule(string Clause, string Reason, string? RequirementId = null, string? SourceRef = null)`
  - `NmMessageReport` 新增 `NmSilenceVerdict SilenceVerdict = NmSilenceVerdict.NotSilent`
  - `NmReport` 新增 `IReadOnlyList<NmObservation>? Observations = null` 与 `IReadOnlyList<NmUnmodeledRule>? UnmodeledRules = null`
  - `NmSilenceClassifier.Classify(IReadOnlyList<NmFrame> allNmFrames, string canIdHex, bool isExtended, double lastSeen, double episodeEnd, double windowEnd, double timeoutSec) -> NmSilenceVerdict`

- [ ] **Step 1: 写失败的测试**

创建 `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmSilenceClassifierTests.cs`：

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §7.1.2：静默必须多态。真实 trace 的全网 NM 静默（9.714–12.677s）
/// 不得被判成单节点违规，否则 AI 会把它当故障解释。
/// </summary>
public class NmSilenceClassifierTests
{
    private static readonly string[] TestReasons = ["test"];

    [Fact]
    public void Classify_LastFrameNearWindowEnd_NotSilent()
    {
        var frames = new[] { Frame(0.0, 0x500), Frame(0.5, 0x500) };

        var verdict = NmSilenceClassifier.Classify(
            frames, "0x500", false, lastSeen: 0.5, episodeEnd: 0.5,
            windowEnd: 0.6, timeoutSec: 2.0);

        Assert.Equal(NmSilenceVerdict.NotSilent, verdict);
    }

    [Fact]
    public void Classify_SilentWhileOthersActive_ReportsNodeSilence()
    {
        // 0x500 在 0.5s 停发，0x510 一直发到 3.0s → 单节点静默，可疑。
        var frames = new[] { Frame(0.0, 0x500), Frame(0.5, 0x500), Frame(2.9, 0x510) };

        var verdict = NmSilenceClassifier.Classify(
            frames, "0x500", false, lastSeen: 0.5, episodeEnd: 3.0,
            windowEnd: 3.0, timeoutSec: 2.0);

        Assert.Equal(NmSilenceVerdict.SilentWithOtherNmActive, verdict);
    }

    [Fact]
    public void Classify_AllNmSilent_ReportsAllSilentNotNodeViolation()
    {
        // 全部 NM 在 0.5s 停发，窗口到 3.0s → 全网静默，原因未知。
        var frames = new[] { Frame(0.0, 0x500), Frame(0.5, 0x500), Frame(0.5, 0x510) };

        var verdict = NmSilenceClassifier.Classify(
            frames, "0x500", false, lastSeen: 0.5, episodeEnd: 0.5,
            windowEnd: 3.0, timeoutSec: 2.0);

        Assert.Equal(NmSilenceVerdict.AllNmSilent, verdict);
    }

    [Fact]
    public void Classify_SharedId_IsIndeterminate()
    {
        // 同一 CAN ID 下两个 NID 交替：按报文拆不出节点，不能判单节点静默。
        var frames = new[]
        {
            Frame(0.0, 0x500, nid: 0x11), Frame(0.5, 0x500, nid: 0x22),
            Frame(1.0, 0x500, nid: 0x11), Frame(2.9, 0x510),
        };

        var verdict = NmSilenceClassifier.Classify(
            frames, "0x500", false, lastSeen: 1.0, episodeEnd: 3.0,
            windowEnd: 3.0, timeoutSec: 2.0, nidByte: 0);

        Assert.Equal(NmSilenceVerdict.IndeterminateAtBoundary, verdict);
    }

    [Fact]
    public void Classify_EpisodeEndsAtWindowEnd_IsIndeterminate()
    {
        // episode 自身被窗口切断：末帧之后到窗口末尾的静默无法判明。
        var frames = new[] { Frame(0.0, 0x500), Frame(0.5, 0x500) };

        var verdict = NmSilenceClassifier.Classify(
            frames, "0x500", false, lastSeen: 0.5, episodeEnd: 0.5,
            windowEnd: 0.5, timeoutSec: 2.0, episodeTruncated: true);

        Assert.Equal(NmSilenceVerdict.IndeterminateAtBoundary, verdict);
    }

    [Fact]
    public void SilentBeyondTimeout_IsDerivedFromVerdict()
    {
        // spec §7.1.2：兼容属性由 Verdict 派生，不可独立赋值。
        var allSilent = new NmMessageReport(
            "0x500", false, "NM", "A", 2, 0, 0.5,
            new NmCycleStats(null, null, null, null, null, 0),
            Array.Empty<NmSegment>(),
            SilenceVerdict: NmSilenceVerdict.AllNmSilent);

        var nodeSilent = allSilent with { SilenceVerdict = NmSilenceVerdict.SilentWithOtherNmActive };

        Assert.False(allSilent.SilentBeyondTimeout);
        Assert.True(nodeSilent.SilentBeyondTimeout);
    }

    private static NmFrame Frame(double t, uint id, byte nid = 0x11)
        => new(
            new NmMessageDef($"0x{id:X}", false, "NM", "A", 8, true, TestReasons),
            new ReplayFrame(t, id, 8, new byte[] { nid, 0, 0, 0, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None));
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmSilenceClassifierTests" -v minimal
```

预期：编译失败，`NmSilenceClassifier` / `NmSilenceVerdict` / `SilenceVerdict` 不存在。

- [ ] **Step 3: 加三个契约类型到 `NmReport.cs`**

在 `NmReport.cs` 的 `NmSegment` 定义之前插入：

```csharp
/// <summary>
/// 静默判定（spec §7.1.2）。bool 装不下「全网静默」与「单节点静默」的区别，
/// 前者是原因未知的事实，后者才是可疑。
/// </summary>
public enum NmSilenceVerdict
{
    /// <summary>窗口末仍在发送。</summary>
    NotSilent = 0,

    /// <summary>该报文静默且其它 NM 报文仍在发送 → 可疑。</summary>
    SilentWithOtherNmActive,

    /// <summary>全部 NM 静默 → 原因未知（释放/下电/记录停止/bus sleep 无法区分）。</summary>
    AllNmSilent,

    /// <summary>窗口截断 / 共享 ID 无法拆分 → 不可判明。</summary>
    IndeterminateAtBoundary,
}

/// <summary>观测事实：可量化、可复核，但不声称违规（spec §7.1.1）。</summary>
public sealed record NmObservation(
    string Kind,
    string? Node,
    string? CanIdHex,
    double Start,
    double End,
    string Detail,
    string EvidenceKind,
    bool IsInference = false,
    string? RuleId = null);

/// <summary>未建模条款：显式可见，AI 不得据此给结论（spec §7.1.1）。</summary>
public sealed record NmUnmodeledRule(
    string Clause,
    string Reason,
    string? RequirementId = null,
    string? SourceRef = null);
```

把 `NmMessageReport` 改成（**`SilentBeyondTimeout` 从位置参数改为计算属性**——否则调用方仍能传 `true` 与 `SilenceVerdict` 矛盾，两个来源必然漂移）：

```csharp
public sealed record NmMessageReport(
    string CanIdHex,
    bool IsExtended,
    string? Name,
    string? Sender,
    int FrameCount,
    double FirstSeen,
    double LastSeen,
    NmCycleStats Cycle,
    IReadOnlyList<NmSegment> Activity,
    NmSilenceVerdict SilenceVerdict = NmSilenceVerdict.NotSilent)
{
    /// <summary>
    /// 兼容属性：语义改为「单节点静默」（spec §7.1.2）。由 <see cref="SilenceVerdict"/> 派生，
    /// 不可独立赋值——避免调用方传 true 而 verdict 是 AllNmSilent 的自相矛盾状态。
    /// </summary>
    public bool SilentBeyondTimeout => SilenceVerdict == NmSilenceVerdict.SilentWithOtherNmActive;
}
```

在 `NmReport` 末尾追加两个可选参数（放在 `IsPartial` 之后）：

```csharp
    // spec §7.1.1：事实与违规必须分开，否则必然误报。
    IReadOnlyList<NmObservation>? Observations = null,
    IReadOnlyList<NmUnmodeledRule>? UnmodeledRules = null);
```

**这是破坏性签名变更，必须同批修所有构造点**。已知调用点：

| 文件 | 改法 |
|---|---|
| `src/PeakCan.Host.Core/Analysis/Nm/NmAnalyzer.cs:273` | 删 `SilentBeyondTimeout: ...`，改传 `SilenceVerdict: verdict`（Task 2 Step 3 处理） |
| `tests/PeakCan.Host.App.Tests/Services/ChatTools/GetNmToolTests.cs:56,192,866,868` | 删 `SilentBeyondTimeout` 实参；需要「单节点静默」语义的传 `NmSilenceVerdict.SilentWithOtherNmActive`，否则省略 |
| `tests/PeakCan.Host.App.Tests/Services/Nm/NmReportWriterTests.cs:24,175,216,240` | 同上；这些测试不关心静默，直接删该实参 |
| `tests/PeakCan.Host.App.Tests/ViewModels/NmPanelFlowTests.cs:40` | 同上 |

`NmNodeReport.SilentBeyondTimeout` 是**另一个类型**，本 Task 不动它。

- [ ] **Step 4: 实现 `NmSilenceClassifier`**

创建 `src/PeakCan.Host.Core/Analysis/Nm/NmSilenceClassifier.cs`：

```csharp
namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 单报文静默归因（spec §7.1.2 / §4.1）。唯一实现：分析器与空档归因共用，
/// 避免两处各自判断「其它 NM 是否还在发」得出相反结论。
/// </summary>
public static class NmSilenceClassifier
{
    /// <param name="allNmFrames">窗口内全部 NM 帧（用于判断其它报文是否仍在发送）。</param>
    /// <param name="lastSeen">该报文最后一帧的时刻。</param>
    /// <param name="episodeEnd">该报文所属 episode 的结束时刻。</param>
    /// <param name="windowEnd">分析窗口结束时刻。</param>
    /// <param name="timeoutSec">该报文的生效超时（秒）。</param>
    /// <param name="nidByte">NID 所在字节；null 表示 profile 未声明，不做共享 ID 判定。</param>
    /// <param name="episodeTruncated">episode 是否被窗口切断（结束时刻不可判明）。</param>
    public static NmSilenceVerdict Classify(
        IReadOnlyList<NmFrame> allNmFrames,
        string canIdHex,
        bool isExtended,
        double lastSeen,
        double episodeEnd,
        double windowEnd,
        double timeoutSec,
        byte? nidByte = null,
        bool episodeTruncated = false)
    {
        // 尾随静默不足一个超时：仍在正常发送节奏内。
        if (windowEnd - lastSeen <= timeoutSec) return NmSilenceVerdict.NotSilent;

        // episode 被窗口切断时，末帧之后的静默可能只是窗口裁剪，不是真静默。
        if (episodeTruncated) return NmSilenceVerdict.IndeterminateAtBoundary;

        // 共享 ID：同一 CAN ID 下多个 NID 交替，按报文拆不出节点。
        if (nidByte is { } nb && HasMultipleNids(allNmFrames, canIdHex, isExtended, nb))
            return NmSilenceVerdict.IndeterminateAtBoundary;

        // 末帧之后仍有其它 NM 报文在发 → 单节点静默，可疑。
        var othersActive = allNmFrames.Any(f =>
            !(string.Equals(f.Def.CanIdHex, canIdHex, StringComparison.Ordinal)
              && f.Def.IsExtended == isExtended)
            && f.Frame.Timestamp > lastSeen && f.Frame.Timestamp <= episodeEnd);

        return othersActive
            ? NmSilenceVerdict.SilentWithOtherNmActive
            : NmSilenceVerdict.AllNmSilent;
    }

    private static bool HasMultipleNids(
        IReadOnlyList<NmFrame> frames, string canIdHex, bool isExtended, byte nidByte)
        => frames
            .Where(f => string.Equals(f.Def.CanIdHex, canIdHex, StringComparison.Ordinal)
                        && f.Def.IsExtended == isExtended
                        && f.Frame.Data.Length > nidByte)
            .Select(f => f.Frame.Data[nidByte])
            .Distinct()
            .Take(2)
            .Count() > 1;
}
```

- [ ] **Step 5: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmSilenceClassifierTests" -v minimal
```

预期：6 项通过。

- [ ] **Step 6: 跑 NM 全量定向测试，确认无回归**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~Analysis.Nm" -v minimal
```

预期：211 项通过（205 + 6）。若 `NmMessageReport` 构造点编译失败，按 Step 3 的退一步方案处理。

- [ ] **Step 7: 向用户确认后提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmReport.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmSilenceClassifier.cs \
  tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmSilenceClassifierTests.cs
git commit -m "$(cat <<'EOF'
feat(nm): add observation/unmodeled contracts and polymorphic silence verdict

NmMessageReport.SilentBeyondTimeout only measured time since last frame and
never checked whether other NM messages were still active, so a network-wide
NM silence was reported as a single-node violation and shipped to the AI.

- NmObservation / NmUnmodeledRule enter NmReport (spec 7.1.1)
- NmSilenceVerdict four-state replaces the bool semantics (spec 7.1.2)
- NmSilenceClassifier is the single implementation of the attribution

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: 分析器与消费方接线

**背景**：Task 1 只加了类型。若不改消费方，observation 永远为空、`silent_beyond_timeout` 仍是旧语义——等于契约加了但没生效。

**Files:**
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmAnalyzer.cs:196-278`
- Modify: `src/PeakCan.Host.App/Services/ChatTools/GetNmOverviewTool.cs:71-97`
- Modify: `src/PeakCan.Host.App/Services/Nm/NmReportWriter.cs:81`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmSilenceWiringTests.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/ChatTools/GetNmToolTests.cs`（追加）

**Interfaces:**
- Consumes: `NmSilenceClassifier.Classify`、`NmObservation`、`NmSilenceVerdict`
- Produces: `NmReport.Observations` 在分析后非空（至少含 `all_nm_silent`）；`GetNmOverviewTool` 输出 `silence_verdict` 字符串字段

- [ ] **Step 1: 写失败的测试**

创建 `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmSilenceWiringTests.cs`：

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §7.1.1 AI 输出负例：全网 NM 静默时不得把单节点字段置真。
/// </summary>
public class NmSilenceWiringTests
{
    private static readonly string[] TestReasons = ["test"];

    [Fact]
    public void Analyze_AllNmSilent_MessageVerdictIsAllNmSilent()
    {
        // 两个 NM 报文都在 1.0s 停发，窗口到 6.0s（> 2s 超时）。
        var profile = Profile(Def("0x500"), Def("0x510"));
        var frames = new[]
        {
            Frame(0.0, 0x500), Frame(0.5, 0x500), Frame(1.0, 0x500),
            Frame(0.0, 0x510), Frame(0.5, 0x510), Frame(1.0, 0x510),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 6.0));

        Assert.All(report.Messages,
            m => Assert.Equal(NmSilenceVerdict.AllNmSilent, m.SilenceVerdict));
        Assert.All(report.Messages, m => Assert.False(m.SilentBeyondTimeout));
    }

    [Fact]
    public void Analyze_SingleNodeSilent_VerdictIsSilentWithOtherActive()
    {
        // 0x500 在 1.0s 停发，0x510 持续到 5.5s（超过 2s 超时）。
        var profile = Profile(Def("0x500"), Def("0x510"));
        var frames = new[]
        {
            Frame(0.0, 0x500), Frame(0.5, 0x500), Frame(1.0, 0x500),
            Frame(0.0, 0x510), Frame(0.5, 0x510), Frame(1.0, 0x510),
            Frame(2.0, 0x510), Frame(3.0, 0x510), Frame(4.0, 0x510),
            Frame(5.0, 0x510), Frame(5.5, 0x510),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 6.0));

        var silent = Assert.Single(report.Messages, m => m.CanIdHex == "0x500");
        Assert.Equal(NmSilenceVerdict.SilentWithOtherNmActive, silent.SilenceVerdict);
        Assert.True(silent.SilentBeyondTimeout);
    }

    [Fact]
    public void Analyze_AllNmSilent_ObservationsContainAllNmSilentMarkedInferred()
    {
        var profile = Profile(Def("0x500"), Def("0x510"));
        var frames = new[]
        {
            Frame(0.0, 0x500), Frame(0.5, 0x500), Frame(1.0, 0x500),
            Frame(0.0, 0x510), Frame(0.5, 0x510), Frame(1.0, 0x510),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 6.0));

        Assert.NotNull(report.Observations);
        var obs = Assert.Single(report.Observations!, o => o.Kind == "all_nm_silent");
        // spec §14.5：静默原因不可证明，必须标 inferred。
        Assert.True(obs.IsInference);
        Assert.Equal("inferred", obs.EvidenceKind);
    }

    private static NmProfile Profile(params NmMessageDef[] defs)
        => new(NmProfileSource.Dbc, defs, new NmTimings(),
            new[] { new NmNodeDef("A", 0xF4, null, true) },
            Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance());

    private static NmMessageDef Def(string id)
        => new(id, false, "NM", "A", 8, true, TestReasons, CycleMs: 500, TimeoutMs: 2000);

    private static ReplayFrame Frame(double t, uint id)
        => new(t, id, 8, new byte[] { 0xF4, 0, 0, 0, 0, 0, 0, 0 },
            PeakCan.HIL.Core.FrameFlags.None);
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmSilenceWiringTests" -v minimal
```

预期：3 项失败（`Verdict` 恒为 `NotSilent`、`Observations` 为 null）。

- [ ] **Step 3: 在 `NmAnalyzer` 里接线**

在 `NmAnalyzer.cs` 的 `NmReport Finish(bool partial)` 之前，声明两个集合：

```csharp
        var observations = new List<NmObservation>();
```

把 `Finish` 里的 `NmReport` 构造改为：

```csharp
                IsPartial: partial,
                Observations: observations.Count == 0 ? null : observations,
                UnmodeledRules: null);
```

在逐报文循环里（`NmAnalyzer.cs:272` 附近），把 `SilentBeyondTimeout:` 那一行替换为：

```csharp
                    SilenceVerdict: verdict);
```

并在 `messageReports.Add(...)` 之前插入：

```csharp
                var lastEpisode = episodes.LastOrDefault(e =>
                    e.Start <= timestamps[^1] && e.End >= timestamps[^1]);
                var verdict = NmSilenceClassifier.Classify(
                    nmFrames, def.CanIdHex, def.IsExtended,
                    lastSeen: timestamps[^1],
                    episodeEnd: lastEpisode?.End ?? timestamps[^1],
                    windowEnd: request.End,
                    timeoutSec: effectiveTimeoutMs / 1000.0,
                    nidByte: request.Profile.Layout?.NidByte,
                    episodeTruncated: lastEpisode?.BoundaryEnd ?? false);
```

在 `NmGapAnalyzer` 调用之后（`NmAnalyzer.cs:292` 附近），把 gap 的事件与 unknown 转成 observation。因为 `NmGapAnalyzer` 内部已经算出 `all_nm_silent` 事件，最省事且不重复实现的接法是在 `NmGapResult` 里加 `IReadOnlyList<NmObservation> Observations`：

修改 `src/PeakCan.Host.Core/Analysis/Nm/NmGapAnalyzer.cs`：

```csharp
public sealed record NmGapResult(
    IReadOnlyList<NmFinding> Findings,
    IReadOnlyList<NmEvent> Events,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<NmObservation> Observations);
```

在 `Analyze` 内声明 `var observations = new List<NmObservation>();`，并在每个 `events.Add(new NmEvent(..., "all_nm_silent", ...))` 旁边加：

```csharp
            observations.Add(new NmObservation(
                "all_nm_silent", null, null, gapStart, gapEnd,
                $"全部 NM 报文静默 {gapEnd - gapStart:F2}s（≥ 超时 {timeoutMs}ms）",
                "inferred", IsInference: true));
```

返回语句改为：

```csharp
        return new NmGapResult(findings, events, unknowns.Distinct().ToArray(), observations);
```

`NmAnalyzer` 里补：

```csharp
        observations.AddRange(gap.Observations);
```

- [ ] **Step 4: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmSilenceWiringTests" -v minimal
```

预期：3 项通过。

- [ ] **Step 5: 改 `GetNmOverviewTool`**

在 `messages` 投影里把 `["silent_beyond_timeout"] = m.SilentBeyondTimeout` 改为：

```csharp
            ["silence_verdict"] = m.SilenceVerdict.ToString(),
            ["silent_beyond_timeout"] = m.SilentBeyondTimeout,
```

在 `root["findings"] = ...` 之后追加：

```csharp
        root["observations"] = report.Observations is null
            ? new JsonArray()
            : new JsonArray(report.Observations.Select(o => (JsonNode)new JsonObject
            {
                ["kind"] = o.Kind,
                ["node"] = o.Node,
                ["can_id"] = o.CanIdHex,
                ["start"] = o.Start,
                ["end"] = o.End,
                ["detail"] = NmEventPrivacy.RedactText(o.Detail, allowedCanIds),
                ["evidence_kind"] = o.EvidenceKind,
                ["is_inference"] = o.IsInference,
                ["rule_id"] = o.RuleId,
            }).ToArray());
        // spec §15：Unmodeled 只发条款号，不发条款正文。
        root["unmodeled_rules"] = report.UnmodeledRules is null
            ? new JsonArray()
            : new JsonArray(report.UnmodeledRules.Select(u => (JsonNode)new JsonObject
            {
                ["clause"] = u.Clause,
                ["reason"] = u.Reason,
                ["requirement_id"] = u.RequirementId,
            }).ToArray());
```

- [ ] **Step 6: 改 `NmReportWriter`**

在 `## Findings` 段之后追加：

```csharp
        sb.AppendLine();
        sb.AppendLine("## Observations");
        if (r.Observations is null || r.Observations.Count == 0)
        {
            sb.AppendLine("- （无观测事实）");
        }
        else
        {
            sb.AppendLine("| kind | can id | start | end | evidence | detail |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var o in r.Observations)
            {
                sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"| {o.Kind} | {o.CanIdHex ?? "—"} | {o.Start:F3} | {o.End:F3} | {o.EvidenceKind} | {Esc(red.Apply(o.Detail))} |"));
            }
        }
        sb.AppendLine();
        sb.AppendLine("## Unmodeled Rules");
        if (r.UnmodeledRules is null || r.UnmodeledRules.Count == 0)
        {
            sb.AppendLine("- （无未建模条款）");
        }
        else
        {
            foreach (var u in r.UnmodeledRules)
                sb.AppendLine($"- **{u.Clause}**：{Esc(u.Reason)}");
        }
```

- [ ] **Step 7: 追加 App 层测试**

在 `tests/PeakCan.Host.App.Tests/Services/ChatTools/GetNmToolTests.cs` 末尾追加：

```csharp
    [Fact]
    public async Task GetNmOverview_AllNmSilent_EmitsVerdictAndObservation()
    {
        // spec §7.1.1 AI 输出负例：全网静默时 silent_beyond_timeout 必须为 false。
        var report = new NmReport(
            new NmWindow(0, 6, "full"), "src", null,
            Array.Empty<NmEpisode>(),
            new[]
            {
                new NmMessageReport("0x500", false, "NM", "A", 3, 0, 1.0,
                    new NmCycleStats(null, null, null, null, null, 0),
                    Array.Empty<NmSegment>(),
                    SilenceVerdict: NmSilenceVerdict.AllNmSilent),
            },
            Array.Empty<NmNodeReport>(), Array.Empty<NmEvent>(),
            Array.Empty<NmFinding>(), Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
            Array.Empty<NmTruncation>(),
            Observations: new[]
            {
                new NmObservation("all_nm_silent", null, null, 1.0, 6.0,
                    "NM silent", "inferred", IsInference: true),
            });

        var json = await RunToolAsync(report);
        var root = JsonNode.Parse(json)!;

        Assert.Equal("AllNmSilent", root["messages"]![0]!["silence_verdict"]!.GetValue<string>());
        Assert.False(root["messages"]![0]!["silent_beyond_timeout"]!.GetValue<bool>());
        Assert.Equal("all_nm_silent", root["observations"]![0]!["kind"]!.GetValue<string>());
        Assert.True(root["observations"]![0]!["is_inference"]!.GetValue<bool>());
    }
```

若 `RunToolAsync` 辅助方法不存在，参照同文件已有测试的构造方式（`GetNmOverviewToolTests` 类内已有实例化 `GetNmOverviewTool` 的写法），复用同一个 stub `IChatToolContext`。

- [ ] **Step 8: 跑定向测试**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~Analysis.Nm" -v minimal
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~GetNmOverview|FullyQualifiedName~NmReportWriter" -v minimal
```

预期：Core 211 通过；App 相关全绿。

- [ ] **Step 9: 向用户确认后提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmAnalyzer.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmGapAnalyzer.cs \
  src/PeakCan.Host.App/Services/ChatTools/GetNmOverviewTool.cs \
  src/PeakCan.Host.App/Services/Nm/NmReportWriter.cs \
  tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmSilenceWiringTests.cs \
  tests/PeakCan.Host.App.Tests/Services/ChatTools/GetNmToolTests.cs
git commit -m "$(cat <<'EOF'
feat(nm): wire silence verdict and observations into analyzer and consumers

- analyzer derives NmSilenceVerdict per message via NmSilenceClassifier
- gap analyzer emits all_nm_silent as an inferred observation
- chat tool exposes silence_verdict / observations / unmodeled_rules
- markdown report gains observation and unmodeled sections

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: 规则参数模型 + AUTOSAR base / 吉利 profile

**背景**：`NmRuleDef` 目前只有 `WhenKind`/`ExpectKind` 两个字符串，没有参数。闭集原语需要 `byte`/`bit`/`cycle_ms`/`pct` 等实参。同时仓库里**没有任何 `.nmstd.yaml` 文件**，S1 声称产出的 AUTOSAR base + 吉利 profile 并不存在。

**Files:**
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmStandardProfile.cs:112-123`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmStandardYaml.cs:259-289`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmStandardValidator.cs:139-156`
- Create: `profiles/base/autosar-cannm@4.1.3.nmstd.yaml`
- Create: `profiles/oem/geely-cv@2.0.nmstd.yaml`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleParamsTests.cs`

**Interfaces:**
- Consumes: `NmStandardYaml.Load`、`NmStandardFlattener.Flatten`
- Produces:
  - `record NmRuleWhen(string Kind, byte? Byte = null, int? Bit = null, int? Length = null, string? Role = null, string? LatencyKind = null, int? MinSilenceMs = null, int? CycleMs = null, double? Pct = null, int? MinFrames = null)`
  - `record NmRuleExpect(string Kind, string? Value = null, byte? Byte = null, int? Bit = null, int? Count = null, int? IntervalMs = null, double? Pct = null, int? MinMs = null, int? MaxMs = null, string? Anchor = null)`
  - `NmRuleDef` 改为持有 `NmRuleWhen When` / `NmRuleExpect Expect`
  - `static class NmRulePrimitives`：`IReadOnlySet<string> WhenKinds`、`IReadOnlySet<string> ExpectKinds`（闭集）

- [ ] **Step 1: 写失败的测试**

创建 `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleParamsTests.cs`：

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §5.3：规则参数必须是结构化 record，类型错误在加载期暴露，
/// 不能推迟到运行期从字符串字典里解析。
/// </summary>
public class NmRuleParamsTests
{
    private const string WithParams = """
standard:
  id: test-std
  version: "1.0"
overrides:
  timings:
    cycle_ms: 500
    timeout_ms: 2000
rules:
  - id: NM_TEST_CYCLE
    requirement: 周期偏差不得超过 ±10%
    phase: normal
    observability: observed
    when:
      kind: cycle_tolerance
      cycle_ms: 500
      pct: 0.1
    expect:
      kind: within_pct
    finding: NM_CYCLE_DEVIATION
    source_ref: §5.5
""";

    [Fact]
    public void Load_StructuredWhenParams_AreParsed()
    {
        var profile = NmStandardYaml.Load(WithParams);

        var rule = Assert.Single(profile.Rules!);
        Assert.Equal("cycle_tolerance", rule.When.Kind);
        Assert.Equal(500, rule.When.CycleMs);
        Assert.Equal(0.1, rule.When.Pct);
        Assert.Equal("within_pct", rule.Expect.Kind);
        Assert.Equal("§5.5", rule.SourceRef);
    }

    [Fact]
    public void Load_UnknownWhenKind_IsRejectedAtLoad()
    {
        // 拼错的原语名必须报错，否则该规则静默进 Unmodeled 而用户以为已校验。
        var bad = WithParams.Replace("kind: cycle_tolerance", "kind: cycle_tolerence",
            StringComparison.Ordinal);

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
        Assert.Contains("cycle_tolerence", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_UnobservableRule_IsAcceptedAndKept()
    {
        var yaml = WithParams.Replace("observability: observed", "observability: unobservable",
            StringComparison.Ordinal);

        var profile = NmStandardYaml.Load(yaml);

        Assert.Equal(NmObservability.Unobservable, Assert.Single(profile.Rules!).Observability);
    }

    [Fact]
    public void PrimitiveClosedSet_ContainsSpecKinds()
    {
        Assert.Contains("layout_check", NmRulePrimitives.WhenKinds);
        Assert.Contains("nmsts_transition", NmRulePrimitives.WhenKinds);
        Assert.Contains("normal_resume_shape", NmRulePrimitives.WhenKinds);
        Assert.DoesNotContain("bit_sequence", NmRulePrimitives.WhenKinds);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmRuleParamsTests" -v minimal
```

预期：编译失败（`rule.When.Kind` 不存在，`NmRulePrimitives` 不存在）。

- [ ] **Step 3: 改 `NmRuleDef` 与新增闭集**

把 `NmStandardProfile.cs:112-123` 的 `NmRuleDef` 替换为：

```csharp
/// <summary>一条企标规则（spec §5.3 / §7.2）。</summary>
public sealed record NmRuleDef(
    string Id,
    string Requirement,
    string Phase,
    NmRuleWhen When,
    NmRuleExpect Expect,
    string FindingCode)
{
    public string? SourceRef { get; init; }
    public NmObservability Observability { get; init; } = NmObservability.Observed;
}

/// <summary>
/// 规则条件。字段是闭集原语参数的并集——结构化而非字符串字典，
/// 类型错误在 YAML 加载期暴露（spec §5.3）。
/// </summary>
public sealed record NmRuleWhen(
    string Kind,
    byte? Byte = null,
    int? Bit = null,
    int? Length = null,
    string? Role = null,
    /// <summary>first_frame_latency 的前置状态：start_nm_tx | wakeup。</summary>
    string? LatencyKind = null,
    int? MinSilenceMs = null,
    int? CycleMs = null,
    double? Pct = null,
    int? MinFrames = null);

/// <summary>规则期望。同上，闭集原语参数的并集。</summary>
public sealed record NmRuleExpect(
    string Kind,
    string? Value = null,
    byte? Byte = null,
    int? Bit = null,
    int? Count = null,
    int? IntervalMs = null,
    double? Pct = null,
    int? MinMs = null,
    int? MaxMs = null,
    /// <summary>repeat_message_duration 的锚点：nmsts | burst_end。</summary>
    string? Anchor = null);

/// <summary>
/// 原语闭集（spec §7.2）。不在集合内的 kind 在加载期报错——
/// 静默进 Unmodeled 会让用户以为条款已被校验。
/// </summary>
public static class NmRulePrimitives
{
    public static readonly IReadOnlySet<string> WhenKinds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "layout_check", "nid_id_mapping", "bit_rule", "cycle_tolerance",
            "burst_shape", "first_frame_latency", "repeat_message_duration",
            "nmsts_transition", "normal_resume_shape", "stop_sending", "max_gap_observe",
        };

    public static readonly IReadOnlySet<string> ExpectKinds =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "within_pct", "equals", "is_zero", "matches_id_low_byte",
            "count_equals", "interval_within_pct", "duration_within_ms",
            "present", "absent", "observe",
        };
}
```

- [ ] **Step 4: 改 YAML DTO 与映射**

把 `NmStandardYaml.cs:259-289` 的 `RuleDto` / `RuleClauseDto` / `MapRule` 替换为：

```csharp
    private sealed class RuleDto
    {
        public string? Id { get; set; }
        public string? Requirement { get; set; }
        public string? Phase { get; set; }
        public RuleWhenDto? When { get; set; }
        public RuleExpectDto? Expect { get; set; }
        public string? Finding { get; set; }
        public string? SourceRef { get; set; }
        public string? Observability { get; set; }
    }

    private sealed class RuleWhenDto
    {
        public string? Kind { get; set; }
        public byte? Byte { get; set; }
        public int? Bit { get; set; }
        public int? Length { get; set; }
        public string? Role { get; set; }
        public string? LatencyKind { get; set; }
        public int? MinSilenceMs { get; set; }
        public int? CycleMs { get; set; }
        public double? Pct { get; set; }
        public int? MinFrames { get; set; }
    }

    private sealed class RuleExpectDto
    {
        public string? Kind { get; set; }
        public string? Value { get; set; }
        public byte? Byte { get; set; }
        public int? Bit { get; set; }
        public int? Count { get; set; }
        public int? IntervalMs { get; set; }
        public double? Pct { get; set; }
        public int? MinMs { get; set; }
        public int? MaxMs { get; set; }
        public string? Anchor { get; set; }
    }

    private static NmRuleDef MapRule(RuleDto r)
    {
        RequireNonEmpty(r.Id, "rules[].id");
        RequireNonEmpty(r.Requirement, $"规则 {r.Id} 的 requirement（条款可追溯性）");
        RequireNonEmpty(r.When?.Kind, $"规则 {r.Id} 的 when.kind");
        RequireNonEmpty(r.Expect?.Kind, $"规则 {r.Id} 的 expect.kind");
        RequireNonEmpty(r.Finding, $"规则 {r.Id} 的 finding code");

        // 闭集校验：未知原语在加载期报错，不静默进 Unmodeled（spec §7.2）。
        if (!NmRulePrimitives.WhenKinds.Contains(r.When!.Kind!))
        {
            throw new NmProfileValidationException(
                $"规则 {r.Id} 的 when.kind「{r.When.Kind}」不在闭集内，合法值：" +
                string.Join(" | ", NmRulePrimitives.WhenKinds.Order(StringComparer.Ordinal)));
        }
        if (!NmRulePrimitives.ExpectKinds.Contains(r.Expect!.Kind!))
        {
            throw new NmProfileValidationException(
                $"规则 {r.Id} 的 expect.kind「{r.Expect.Kind}」不在闭集内，合法值：" +
                string.Join(" | ", NmRulePrimitives.ExpectKinds.Order(StringComparer.Ordinal)));
        }

        var w = r.When;
        var e = r.Expect;
        return new NmRuleDef(
            r.Id!, r.Requirement!, r.Phase ?? "normal",
            new NmRuleWhen(w.Kind!, w.Byte, w.Bit, w.Length, w.Role, w.LatencyKind,
                w.MinSilenceMs, w.CycleMs, w.Pct, w.MinFrames),
            new NmRuleExpect(e.Kind!, e.Value, e.Byte, e.Bit, e.Count, e.IntervalMs,
                e.Pct, e.MinMs, e.MaxMs, e.Anchor),
            r.Finding!)
        {
            SourceRef = r.SourceRef,
            Observability = ParseObservability(r.Observability),
        };
    }
```

- [ ] **Step 5: 校验器补两条硬约束**

在 `NmStandardValidator.ValidateRules` 里追加：

```csharp
            // spec §3：U 级条款不可观测，产出 finding 就是编造结论。
            if (rule.Observability == NmObservability.Unobservable
                && !string.IsNullOrWhiteSpace(rule.FindingCode))
            {
                throw new NmProfileValidationException(
                    $"规则 {rule.Id} 标为 unobservable 却声明了 finding code「{rule.FindingCode}」；" +
                    "不可观测条款只能进 Unmodeled");
            }

            // spec §7.5.1.2：nmsts_transition 必须声明 byte 与 bit，
            // 否则原语无法定位状态位，会在运行期静默失效。
            if (rule.When.Kind == "nmsts_transition"
                && (rule.When.Byte is null || rule.When.Bit is null))
            {
                throw new NmProfileValidationException(
                    $"规则 {rule.Id} 的 nmsts_transition 必须声明 when.byte 与 when.bit");
            }
```

在 `ValidateLayout` 里追加 NMSts 位的可读性校验：

```csharp
        // spec §7.5.1.2：NMSts 必须是无符号、长度恰好 1 的位。
        // 位宽≠1 或有符号会把状态值读错，必须在加载期拒绝而不是运行期猜。
        if (layout.BitRoles is { } bitRoles)
        {
            foreach (var role in bitRoles.Where(r =>
                string.Equals(r.Name, "NmSts", StringComparison.Ordinal)))
            {
                if (role.Bit is < 0 or > 7)
                {
                    throw new NmProfileValidationException(
                        $"NMSts 位的 bit（{role.Bit}）必须在 [0,7]");
                }
            }
        }
```

**说明**：profile 的 `NmBitRole` 只描述单 bit，没有位宽/符号字段——DBC 信号才可能有 `24|2` 或 `@0-`。真正的位宽/符号校验发生在 profile 与 DBC 合并时（S6 阶段），本 Task 只能校验 profile 自身的 `bit ∈ [0,7]`。**这一点必须写进最终报告**，不能声称已覆盖 §7.5.1.2 的全部七类不可读情形。

- [ ] **Step 5b: 写 NMSts 校验的测试**

在 `NmRuleParamsTests.cs` 追加：

```csharp
    [Fact]
    public void Validate_NmstsTransitionWithoutByte_IsRejected()
    {
        var bad = WithParams.Replace(
            """
                when:
                  kind: cycle_tolerance
                  cycle_ms: 500
                  pct: 0.1
                expect:
                  kind: within_pct
                finding: NM_CYCLE_DEVIATION
            """,
            """
                when:
                  kind: nmsts_transition
                expect:
                  kind: observe
                finding: ""
            """, StringComparison.Ordinal);

        var ex = Assert.Throws<NmProfileValidationException>(() => NmStandardYaml.Load(bad));
        Assert.Contains("nmsts_transition", ex.Message, StringComparison.Ordinal);
    }
```

- [ ] **Step 6: 创建 AUTOSAR base profile**

创建 `profiles/base/autosar-cannm@4.1.3.nmstd.yaml`：

```yaml
# AUTOSAR CanNm 基础 profile。只声明标准语义，不含 OEM 约束。
standard:
  id: autosar-cannm
  version: "4.1.3"
  vendor: AUTOSAR
  source:
    doc: AUTOSAR_SWS_CANNetworkManagement
    revision: "4.1.3"

overrides:
  timings:
    cycle_ms: 500
    timeout_ms: 2000
    wait_bus_sleep_ms: 2000
    repeat_message_with_burst_ms: 1700
    repeat_message_without_burst_ms: 1500
    immediate_cycle_ms: 20
    immediate_count: 10
    start_nm_tx_ms: 10
    wakeup_ms: 120
    tolerance_ratio: 0.2
  layout:
    nid_byte: 0
    cbv_byte: 1
    bit_roles:
      - byte: 1
        bit: 0
        name: RepeatMessageRequest
        meaning: "1=请求进入 Repeat Message State"
        constraint: must_be_zero_while_disabled
      - byte: 1
        bit: 4
        name: ActiveWakeup
        meaning: "1=主动唤醒"
        constraint: none
      - byte: 1
        bit: 6
        name: PartialNetworkInfo
        meaning: "1=本帧携带部分网络信息"
        constraint: none
  features:
    active_wakeup_bit_enabled: true
    repeat_msg_ind_enabled: true
    pn_enabled: true

rules:
  - id: NM_CYCLE
    requirement: NM 报文应以 T_NM_MessageCycle 周期发送，容差 ±20%
    phase: normal
    observability: observed
    when:
      kind: cycle_tolerance
      cycle_ms: 500
      pct: 0.2
    expect:
      kind: within_pct
    finding: NM_CYCLE_DEVIATION
    source_ref: AUTOSAR SWS CanNm 4.1.3 §9
```

- [ ] **Step 7: 创建吉利 profile**

创建 `profiles/oem/geely-cv@2.0.nmstd.yaml`：

```yaml
# 吉利商用车 CAN 网络管理需求规范 V2.0（2025-02-27）。
standard:
  id: geely-cv
  version: "2.0"
  vendor: Geely Commercial Vehicle
  extends: autosar-cannm@4.1.3
  source:
    doc: 吉利商用车CAN网络管理需求规范_V2.0
    revision: "2.0"

overrides:
  timings:
    cycle_ms: 500
    timeout_ms: 2200
    repeat_message_with_burst_ms: 1700
    repeat_message_without_burst_ms: 1500
    immediate_cycle_ms: 20
    immediate_count: 10
    tolerance_ratio: 0.1
  layout:
    nid_byte: 0
    cbv_byte: 1
    user_data_offset: 4
    user_data_length: 4
    bit_roles:
      # 吉利 §5.4.3 无 PNI：删除 base 的 bit6 声明。
      - byte: 3
        bit: 0
        name: NmSts
        meaning: "0=Repeat Message State, 1=Normal Operation State"
        constraint: none
  features:
    pn_enabled: false
  remove:
    - bit_roles[1.6]

rules:
  - id: NM_01
    requirement: NM 报文 ECU 地址（NID）应与 CAN ID 低字节一致（扩展帧）
    phase: normal
    observability: observed
    when:
      kind: nid_id_mapping
    expect:
      kind: matches_id_low_byte
    finding: NM_NID_MISMATCH
    source_ref: §5.4.2

  - id: NM_05
    requirement: 预留位必须为 0
    phase: normal
    observability: observed
    when:
      kind: bit_rule
      role: RepeatMessageRequest
    expect:
      kind: is_zero
    finding: NM_RMR_SET_WHILE_DISABLED
    source_ref: §5.4.3

  - id: NM_06
    requirement: Repeat Message State 持续时长；含快发 1700±60ms，不含快发 1500±60ms
    phase: wakeup
    observability: partial
    when:
      kind: repeat_message_duration
      cycle_ms: 500
    expect:
      kind: duration_within_ms
      min_ms: 1440
      max_ms: 1760
      anchor: nmsts
    finding: NM_REPEAT_MESSAGE_DURATION_EXCEEDED
    source_ref: §5.5

  - id: NM_09
    requirement: 进入 Ready Sleep State 后停止发送 NM 报文
    phase: sleep
    observability: partial
    when:
      kind: stop_sending
    expect:
      kind: observe
    finding: ""
    source_ref: §5.9
```

**注意**：`NM_09` 的 `finding` 为空字符串。若校验器拒绝空 finding，改为不写该键并让 `MapRule` 对 `unobservable`/`observe` 规则放宽。实际做法：`MapRule` 里对 `Expect.Kind == "observe"` 的规则允许 `Finding` 为空，此时 `FindingCode` 置为空串，规则只产 observation。

- [ ] **Step 8: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmRuleParamsTests|FullyQualifiedName~NmStandard" -v minimal
```

预期：全绿。已有的 `NmStandardProfileYamlTests` 里若构造了旧格式的 `when: { kind: ... }`，需要补参数或保持不变（只写 kind 仍合法，参数为 null）。

- [ ] **Step 9: 加一个展平真实 profile 的测试**

在 `NmRuleParamsTests.cs` 追加：

```csharp
    [Fact]
    public void Flatten_GeelyOverAutosar_RemovesPniAndKeepsCycleRule()
    {
        var baseYaml = File.ReadAllText(RepoPath("profiles/base/autosar-cannm@4.1.3.nmstd.yaml"));
        var geelyYaml = File.ReadAllText(RepoPath("profiles/oem/geely-cv@2.0.nmstd.yaml"));
        var registry = new Dictionary<string, NmStandardProfile>(StringComparer.Ordinal)
        {
            ["autosar-cannm@4.1.3"] = NmStandardYaml.Load(baseYaml),
            ["geely-cv@2.0"] = NmStandardYaml.Load(geelyYaml),
        };

        var flat = NmStandardYaml.LoadAndFlatten(geelyYaml, registry);

        // remove 生效：PNI 位不再存在。
        Assert.DoesNotContain(flat.Layout!.BitRoles!, r => r.Name == "PartialNetworkInfo");
        // NMSts 位被加入。
        Assert.Contains(flat.Layout.BitRoles!, r => r.Name == "NmSts");
        // 吉利容差覆盖 base 的 0.2。
        Assert.Equal(0.1, flat.Timings!.ToleranceRatio);
        // 规则按 id 合并。
        Assert.Contains(flat.Rules!, r => r.Id == "NM_CYCLE");
        Assert.Contains(flat.Rules!, r => r.Id == "NM_06");
    }

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }
```

- [ ] **Step 10: 跑测试并提交**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmRuleParamsTests" -v minimal
```

向用户确认后：

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmStandardProfile.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmStandardYaml.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmStandardValidator.cs \
  profiles/ \
  tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleParamsTests.cs
git commit -m "$(cat <<'EOF'
feat(nm): structured rule params, closed primitive set, AUTOSAR/Geely profiles

- NmRuleWhen / NmRuleExpect carry typed parameters instead of a string dict
- NmRulePrimitives enforces the closed set at load time; typos now fail loudly
- validator rejects unobservable rules that declare a finding code
- add profiles/base/autosar-cannm@4.1.3 and profiles/oem/geely-cv@2.0

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: `NmBurstDetector` — 快发检测的唯一实现

**背景**：spec §7.5.3.1 指出三处阈值互相冲突：`NmActivityBuilder` 用 `0.5×cycle`、§7.3 候选检测用 `2.0×ImmediateCycleMs`、§7.4 合规判定用 `1.5×ImmediateCycleMs`。必须收敛成一个检测器，返回「候选段 + 是否合规」两个结果。

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmBurstDetector.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmBurstDetectorTests.cs`

**Interfaces:**
- Consumes: `NmEffectiveTiming`
- Produces:
  - `record BurstDetection(int FrameCount, int IntervalCount, double Start, double End, bool IsCandidate, bool IsCompliant, string Detector, bool IsIndeterminate)`
  - `NmBurstDetector.Detect(IReadOnlyList<double> timestamps, NmEffectiveTiming timing) -> BurstDetection`
  - `NmBurstDetector.DetectInEpisode(IReadOnlyList<double> timestamps, double episodeStart, double episodeEnd, NmEffectiveTiming timing) -> BurstDetection`

- [ ] **Step 1: 写失败的测试**

创建 `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmBurstDetectorTests.cs`：

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §7.4 / §7.5.3.1：快发检测只有一个实现。10 帧 = 9 个间隔，
/// 跨度 181.5ms（真实 trace §14.3），不是「10 × 20ms = 200ms」。
/// </summary>
public class NmBurstDetectorTests
{
    private static readonly NmEffectiveTiming Geely = new(
        CycleMs: 500, TimeoutMs: 2200, WaitBusSleepMs: 2000,
        RepeatMessageWithBurstMs: 1700, RepeatMessageWithoutBurstMs: 1500,
        ImmediateCycleMs: 20, ImmediateCount: 10,
        StartNmTxMs: 10, WakeUpMs: 120, ToleranceRatio: 0.1);

    [Fact]
    public void Detect_RealTraceBurst_IsCompliant()
    {
        // 真实 trace 第二段：10 帧，9 个间隔 19.021–20.996ms。
        var ts = new double[10];
        ts[0] = 12.677179;
        var intervals = new[] { 19.5, 20.0, 20.5, 19.0, 21.0, 20.2, 19.8, 20.4, 20.9 };
        for (var i = 1; i < ts.Length; i++) ts[i] = ts[i - 1] + intervals[i - 1] / 1000.0;

        var d = NmBurstDetector.Detect(ts, Geely);

        Assert.True(d.IsCandidate);
        Assert.True(d.IsCompliant);
        Assert.Equal(10, d.FrameCount);
        Assert.Equal(9, d.IntervalCount);
        Assert.Equal("immediate", d.Detector);
        Assert.False(d.IsIndeterminate);
    }

    [Fact]
    public void Detect_NineFrames_IsCandidateButNotCompliant()
    {
        var ts = Enumerable.Range(0, 9).Select(i => i * 0.020).ToArray();

        var d = NmBurstDetector.Detect(ts, Geely);

        Assert.True(d.IsCandidate);
        Assert.False(d.IsCompliant);
    }

    [Fact]
    public void Detect_SlowIntervals_IsNotCandidate()
    {
        var ts = Enumerable.Range(0, 10).Select(i => i * 0.5).ToArray();

        var d = NmBurstDetector.Detect(ts, Geely);

        Assert.False(d.IsCandidate);
        Assert.Equal(0, d.FrameCount);
    }

    [Fact]
    public void Detect_IntervalOutOfTolerance_IsCandidateButNotCompliant()
    {
        // 10 帧但有一个间隔 30ms（> 20×1.1=22ms）→ 检测得到候选，判不合规。
        var ts = new double[10];
        ts[0] = 0;
        for (var i = 1; i < ts.Length; i++) ts[i] = ts[i - 1] + (i == 5 ? 0.030 : 0.020);

        var d = NmBurstDetector.Detect(ts, Geely);

        Assert.True(d.IsCandidate);
        Assert.False(d.IsCompliant);
    }

    [Fact]
    public void Detect_NoImmediateCycle_FallsBackToCycleRatio()
    {
        // ImmediateCycleMs 缺失：回退 0.5×cycle 检测，标 cycle_ratio_fallback + inferred。
        var timing = Geely with { ImmediateCycleMs = 0 };
        var ts = Enumerable.Range(0, 10).Select(i => i * 0.100).ToArray();

        var d = NmBurstDetector.Detect(ts, timing);

        Assert.True(d.IsCandidate);
        Assert.Equal("cycle_ratio_fallback", d.Detector);
        // 回退路径不得判「合规」——它证明不了符合 N_ImmediateNM_TIMES。
        Assert.False(d.IsCompliant);
    }

    [Fact]
    public void Detect_NoCycleAtAll_ReturnsIndeterminate()
    {
        var timing = Geely with { ImmediateCycleMs = 0, CycleMs = 0 };
        var ts = new double[] { 0, 0.02, 0.04 };

        var d = NmBurstDetector.Detect(ts, timing);

        Assert.True(d.IsIndeterminate);
        Assert.False(d.IsCandidate);
    }

    [Fact]
    public void DetectInEpisode_BurstCutByWindowEnd_IsIndeterminate()
    {
        // 快发段延伸到 episode 结束仍未停 → 可能被窗口切断。
        var ts = Enumerable.Range(0, 5).Select(i => i * 0.020).ToArray();

        var d = NmBurstDetector.DetectInEpisode(ts, episodeStart: 0, episodeEnd: ts[^1], timing: Geely);

        Assert.True(d.IsCandidate);
        Assert.True(d.IsIndeterminate);
        Assert.False(d.IsCompliant);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmBurstDetectorTests" -v minimal
```

预期：编译失败。

- [ ] **Step 3: 实现 `NmBurstDetector`**

创建 `src/PeakCan.Host.Core/Analysis/Nm/NmBurstDetector.cs`：

```csharp
namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>快发段检测结果（spec §7.4）。</summary>
public sealed record BurstDetection(
    int FrameCount,
    int IntervalCount,
    double Start,
    double End,
    /// <summary>宽松检测：连续 ≥2 个间隔 ≤ 2.0×ImmediateCycleMs。</summary>
    bool IsCandidate,
    /// <summary>严格判定：count == ImmediateCount 且每个间隔 ∈ 1±ToleranceRatio。</summary>
    bool IsCompliant,
    /// <summary>immediate | cycle_ratio_fallback | none。</summary>
    string Detector,
    /// <summary>段被 episode/窗口边界切断，时长只是下限。</summary>
    bool IsIndeterminate);

/// <summary>
/// 快发检测的唯一实现（spec §7.5.3.1）。所有调用方（活动段、主动唤醒候选、
/// 快发形态合规）必须复用，不得各自写阈值。
/// </summary>
public static class NmBurstDetector
{
    /// <summary>检测阶段的宽松倍数：只为抓到候选，不做合规判定。</summary>
    private const double CandidateFactor = 2.0;

    /// <summary>回退路径（无 ImmediateCycleMs）沿用的 M1 阈值。</summary>
    private const double CycleRatioFactor = 0.5;

    public static BurstDetection Detect(IReadOnlyList<double> timestamps, NmEffectiveTiming timing)
        => DetectInEpisode(timestamps, double.NegativeInfinity, double.PositiveInfinity, timing);

    public static BurstDetection DetectInEpisode(
        IReadOnlyList<double> timestamps,
        double episodeStart,
        double episodeEnd,
        NmEffectiveTiming timing)
    {
        var none = new BurstDetection(0, 0, 0, 0, false, false, "none", false);
        if (timestamps.Count < 2) return none;

        var ordered = timestamps.OrderBy(t => t).ToArray();

        // 检测阈值与检测器名：优先用 ImmediateCycleMs，缺失时回退到周期比例。
        double detectThreshold;
        string detector;
        var canJudgeCompliance = false;

        if (timing.ImmediateCycleMs > 0)
        {
            detectThreshold = timing.ImmediateCycleMs * CandidateFactor / 1000.0;
            detector = "immediate";
            canJudgeCompliance = true;
        }
        else if (timing.CycleMs > 0)
        {
            detectThreshold = timing.CycleMs * CycleRatioFactor / 1000.0;
            detector = "cycle_ratio_fallback";
        }
        else
        {
            return none with { IsIndeterminate = true };
        }

        // 找最长的一段连续短间隔。起止用「间隔两端」的帧时刻。
        var bestStart = -1;
        var bestCount = 0;
        var runStart = -1;
        var runCount = 0;

        for (var i = 1; i < ordered.Length; i++)
        {
            var delta = ordered[i] - ordered[i - 1];
            if (delta <= detectThreshold)
            {
                if (runStart < 0) runStart = i - 1;
                runCount++;
                if (runCount > bestCount)
                {
                    bestCount = runCount;
                    bestStart = runStart;
                }
            }
            else
            {
                runStart = -1;
                runCount = 0;
            }
        }

        // 候选要求 ≥2 个间隔（spec §7.3 阶段 1）。
        if (bestCount < 2 || bestStart < 0) return none;

        var start = ordered[bestStart];
        var end = ordered[bestStart + bestCount];
        var frameCount = bestCount + 1;

        // 合规判定只在 immediate 检测器下做：回退路径证明不了帧数语义。
        var isCompliant = false;
        if (canJudgeCompliance && frameCount == timing.ImmediateCount)
        {
            var lo = timing.ImmediateCycleMs * (1 - timing.ToleranceRatio) / 1000.0;
            var hi = timing.ImmediateCycleMs * (1 + timing.ToleranceRatio) / 1000.0;
            isCompliant = true;
            for (var i = bestStart + 1; i <= bestStart + bestCount; i++)
            {
                var delta = ordered[i] - ordered[i - 1];
                if (delta < lo || delta > hi) { isCompliant = false; break; }
            }
        }

        // 段尾触到 episode/窗口边界：后面可能还有快发帧被切断。
        var truncated = end >= episodeEnd - 1e-9;

        return new BurstDetection(
            frameCount, bestCount, start, end,
            IsCandidate: true,
            IsCompliant: isCompliant && !truncated,
            Detector: detector,
            IsIndeterminate: truncated);
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmBurstDetectorTests" -v minimal
```

预期：7 项通过。

- [ ] **Step 5: 向用户确认后提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmBurstDetector.cs \
  tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmBurstDetectorTests.cs
git commit -m "$(cat <<'EOF'
feat(nm): single burst detector with candidate and compliance verdicts

Three conflicting burst thresholds (0.5x cycle, 2.0x immediate, 1.5x immediate)
existed in the codebase. NmBurstDetector is now the only implementation: loose
detection plus strict compliance, with a documented fallback when
ImmediateCycleMs is not configured.

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: 规则执行器骨架 + `layout_check` / `nid_id_mapping` / `bit_rule`

**背景**：原语必须先有执行器骨架才能落地。这三个是 O 级（直接可观测），也是真实 trace 上唯二能产出确定违规的原语（PMS 帧的 NID 与 RMR）。

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmRuleEngine.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Bits.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleEngineTests.cs`

**Interfaces:**
- Consumes: `NmRuleDef`、`NmRuleWhen`、`NmRuleExpect`、`NmGlobalTiming`、`NmProfile`
- Produces:
  - `record NmRuleContext(IReadOnlyList<NmFrame> NmFrames, IReadOnlyList<NmEpisode> Episodes, NmProfile Profile, NmGlobalTiming Timing, NmWindow Window)`，带 `NmEffectiveTiming Resolve(NmMessageDef def)` 方法
  - `record NmRuleResult(IReadOnlyList<NmFinding> Findings, IReadOnlyList<NmObservation> Observations, IReadOnlyList<NmUnmodeledRule> Unmodeled, IReadOnlyList<NmTruncation> Truncations)`
  - `NmRuleEngine.Evaluate(IReadOnlyList<NmRuleDef> rules, NmRuleContext context) -> NmRuleResult`

**关于 `Evaluate(rules, ctx)` 与 `ctx.Rules` 的重复**：形参 `rules` 是实际求值集合，`ctx.Rules` 供 evaluator 内部回溯（如把 `RuleId` 写进 observation）。两者必须一致——测试统一写 `Evaluate(ctx.Rules, ctx)`。后续可收敛成只留一个，本计划不做该重构。

**与 spec §7.1 的偏差（有意为之）**：spec 写 `NmRuleContext.Timing` 是 `NmEffectiveTiming`，但规则引擎是**跨报文**求值的（`cycle_tolerance`/`burst_shape`/`nmsts_transition` 都按 `(CAN ID, extended)` 分组）。传单个 `NmEffectiveTiming` 会让多报文场景用错周期。因此 context 持 `NmGlobalTiming`，evaluator 在分组内调 `ctx.Resolve(def)` 拿该报文的生效时序。这与 spec §5.2「所有分析阶段必须接收 NmEffectiveTiming」的精神一致——**每个报文拿到的是自己的 NmEffectiveTiming**，不是随便挑一个。

- [ ] **Step 1: 写失败的测试**

创建 `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleEngineTests.cs`：

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §12：每个原语必须「注入故障必触发、合法场景零误报」。
/// </summary>
public class NmRuleEngineTests
{
    private static readonly string[] TestReasons = ["test"];

    [Fact]
    public void BitRule_MustBeZeroViolated_ProducesFinding()
    {
        // 真实 trace 的 PMS 帧：byte1.bit0 = 1，但该位标了 must_be_zero_while_disabled。
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x18FFFA27, 0x08, 0x01) },
            rules: new[]
            {
                Rule("NM_05", When("bit_rule", role: "RepeatMessageRequest"),
                    Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
            },
            roles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_RMR_SET_WHILE_DISABLED", f.Code);
        Assert.Equal("NM_05", f.RuleId);
        Assert.Equal(0.0, Assert.Single(f.EvidenceTimes));
    }

    [Fact]
    public void BitRule_MustBeZeroSatisfied_NoFinding()
    {
        var ctx = Context(
            frames: new[] { Frame(0.0, 0x500, 0xF4, 0x00) },
            rules: new[]
            {
                Rule("NM_05", When("bit_rule", role: "RepeatMessageRequest"),
                    Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
            },
            roles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void BitRule_DlcTooShort_ProducesUnreadableObservationNotSilence()
    {
        // DLC 不足时该帧不可读：必须显式可见，不能静默跳过整条规则。
        var ctx = Context(
            frames: new[] { new NmFrame(Def("0x500"),
                new ReplayFrame(0.0, 0x500, 2, new byte[] { 0xF4, 0x00 },
                    PeakCan.HIL.Core.FrameFlags.None)) },
            rules: new[]
            {
                Rule("NM_05", When("bit_rule", role: "RepeatMessageRequest"),
                    Expect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
            },
            roles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        Assert.Contains(result.Observations, o => o.Kind == "bit_rule_unreadable");
    }

    [Fact]
    public void NidIdMapping_ExtendedFrameLowByteMismatch_ProducesFinding()
    {
        // 真实 trace：ID 0x18FFFA27 低字节 0x27，但 byte0 NID = 0x08。
        var ctx = Context(
            frames: new[]
            {
                new NmFrame(Def("0x18FFFA27", isExtended: true),
                    new ReplayFrame(0.0, 0x18FFFA27, 8,
                        new byte[] { 0x08, 0x01, 0, 0, 0, 0, 0, 0 },
                        PeakCan.HIL.Core.FrameFlags.None, IsExtended: true)),
            },
            rules: new[]
            {
                Rule("NM_01", When("nid_id_mapping"), Expect("matches_id_low_byte"),
                    "NM_NID_MISMATCH"),
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_NID_MISMATCH", f.Code);
    }

    [Fact]
    public void NidIdMapping_MatchingNid_NoFinding()
    {
        var ctx = Context(
            frames: new[]
            {
                new NmFrame(Def("0x18FFFA27", isExtended: true),
                    new ReplayFrame(0.0, 0x18FFFA27, 8,
                        new byte[] { 0x27, 0x00, 0, 0, 0, 0, 0, 0 },
                        PeakCan.HIL.Core.FrameFlags.None, IsExtended: true)),
            },
            rules: new[]
            {
                Rule("NM_01", When("nid_id_mapping"), Expect("matches_id_low_byte"),
                    "NM_NID_MISMATCH"),
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void UnknownPrimitive_GoesToUnmodeledNotFinding()
    {
        // 执行器遇到不在实现表里的原语：进 Unmodeled，不抛异常、不产 finding。
        var rule = new NmRuleDef("NM_X", "测试条款", "normal",
            new NmRuleWhen("max_gap_observe"), new NmRuleExpect("observe"), "")
        {
            Observability = NmObservability.Partial,
        };
        var ctx = Context(frames: new[] { Frame(0.0, 0x500, 0xF4, 0x00) },
            rules: new[] { rule });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.Empty(result.Findings);
        var u = Assert.Single(result.Unmodeled);
        Assert.Equal("NM_X", u.Clause);
        Assert.Contains("max_gap_observe", u.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnobservableRule_NeverProducesFinding()
    {
        var rule = new NmRuleDef("NM_U", "不可观测条款", "normal",
            new NmRuleWhen("bit_rule", Role: "RepeatMessageRequest"),
            new NmRuleExpect("is_zero"), "SHOULD_NOT_APPEAR")
        {
            Observability = NmObservability.Unobservable,
        };
        var ctx = Context(frames: new[] { Frame(0.0, 0x500, 0xF4, 0x01) },
            rules: new[] { rule },
            roles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
            });

        var result = NmRuleEngine.Evaluate(ctx.Rules, ctx);

        Assert.DoesNotContain(result.Findings, f => f.Code == "SHOULD_NOT_APPEAR");
        Assert.Contains(result.Unmodeled, u => u.Clause == "NM_U");
    }

    // ==== 构造辅助 ====

    private static NmRuleContext Context(
        IReadOnlyList<NmFrame> frames,
        IReadOnlyList<NmRuleDef> rules,
        IReadOnlyList<NmBitRole>? roles = null)
    {
        var defs = frames.Select(f => f.Def).Distinct().ToArray();
        var profile = new NmProfile(
            NmProfileSource.Dbc, defs,
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance(),
            Layout: roles is null ? null : new NmLayout(NidByte: 0, CbvByte: 1),
            StandardBitRoles: roles);
        var timing = NmTimingDefaults.FromProfile(profile.Timings, null);
        return new NmRuleContext(frames, Array.Empty<NmEpisode>(), profile, timing,
            new NmWindow(0, 10, "test")) { Rules = rules };
    }

    private static NmRuleWhen When(string kind, string? role = null)
        => new(kind, Role: role);

    private static NmRuleExpect Expect(string kind)
        => new(kind);

    private static NmRuleDef Rule(
        string id, NmRuleWhen when, NmRuleExpect expect, string finding)
        => new(id, "测试条款", "normal", when, expect, finding);

    private static NmMessageDef Def(string id, bool isExtended = false)
        => new(id, isExtended, "NM", "A", 8, true, TestReasons, CycleMs: 500, TimeoutMs: 2000);

    // byte0 写 0xF4 只在「规则表不含 nid_id_mapping」时安全：
    // 标准帧 0x500 的期望 NID 是 0x00，一旦跑 NM_01 就会报 NM_NID_MISMATCH。
    // 本文件所有用例的规则表都不含 NM_01，故不受影响。
    private static NmFrame Frame(double t, uint id, byte b0, byte b1)
        => new(Def($"0x{id:X}"),
            new ReplayFrame(t, id, 8, new byte[] { b0, b1, 0, 0, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None));
}
```

**注意**：测试里用了 `NmProfile.StandardBitRoles` 与 `NmRuleContext.Rules` 两个计划新加的成员，Step 3 会补上。

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmRuleEngineTests" -v minimal
```

预期：编译失败（`NmRuleEngine`、`StandardBitRoles` 不存在）。

- [ ] **Step 3: 给 `NmProfile` 加位角色，给 `NmRuleContext` 加规则表**

在 `NmProfile.cs` 的 `NmProfile` record 末尾追加：

```csharp
    // 展平后的企标位角色；null 表示未选企标（M1 行为）。
    IReadOnlyList<NmBitRole>? StandardBitRoles = null);
```

- [ ] **Step 4: 实现 `NmRuleEngine`**

创建 `src/PeakCan.Host.Core/Analysis/Nm/NmRuleEngine.cs`：

```csharp
namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>规则求值输入（spec §7.1）。</summary>
public sealed record NmRuleContext(
    IReadOnlyList<NmFrame> NmFrames,
    IReadOnlyList<NmEpisode> Episodes,
    NmProfile Profile,
    // 持全局时序而非单个 NmEffectiveTiming：规则是跨报文求值的，
    // 传单个生效时序会让多报文场景用错周期（见计划 Interfaces 的偏差说明）。
    NmGlobalTiming Timing,
    NmWindow Window)
{
    /// <summary>待求值的规则；由调用方从展平 profile 取。</summary>
    public IReadOnlyList<NmRuleDef> Rules { get; init; } = Array.Empty<NmRuleDef>();

    /// <summary>该报文的生效时序（含报文级覆盖，spec §5.2）。</summary>
    public NmEffectiveTiming Resolve(NmMessageDef def) => NmTiming.Resolve(def, Timing);
}

/// <summary>规则求值结果（spec §7.1）。违规与事实严格分开。</summary>
public sealed record NmRuleResult(
    IReadOnlyList<NmFinding> Findings,
    IReadOnlyList<NmObservation> Observations,
    IReadOnlyList<NmUnmodeledRule> Unmodeled,
    IReadOnlyList<NmTruncation> Truncations);

/// <summary>
/// 规则引擎（spec §7.2 闭集原语）。未知原语进 Unmodeled，不产 finding、不抛异常；
/// U 级条款无论如何都不产 finding（spec §3）。
/// </summary>
public static class NmRuleEngine
{
    public static NmRuleResult Evaluate(IReadOnlyList<NmRuleDef> rules, NmRuleContext context)
    {
        var findings = new List<NmFinding>();
        var observations = new List<NmObservation>();
        var unmodeled = new List<NmUnmodeledRule>();

        foreach (var rule in rules)
        {
            // spec §3：不可观测条款只能进 Unmodeled。
            if (rule.Observability == NmObservability.Unobservable)
            {
                unmodeled.Add(new NmUnmodeledRule(
                    rule.Id, "条款不可观测（U 级），只能推断，不产 finding",
                    RequirementId: rule.Id, SourceRef: rule.SourceRef));
                continue;
            }

            if (!NmRuleEvaluators.TryEvaluate(rule, context, findings, observations))
            {
                unmodeled.Add(new NmUnmodeledRule(
                    rule.Id, $"原语「{rule.When.Kind}」在 v1 闭集中无实现",
                    RequirementId: rule.Id, SourceRef: rule.SourceRef));
            }
        }

        return new NmRuleResult(findings, observations, unmodeled,
            Array.Empty<NmTruncation>());
    }
}
```

- [ ] **Step 5: 实现三个 O 级原语**

创建 `src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Bits.cs`：

```csharp
using System.Globalization;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>规则原语的实现表（spec §7.2 闭集）。按原语拆分到 partial 文件。</summary>
public static partial class NmRuleEvaluators
{
    /// <summary>返回 false 表示该原语未实现，调用方据此进 Unmodeled。</summary>
    internal static bool TryEvaluate(
        NmRuleDef rule, NmRuleContext ctx,
        List<NmFinding> findings, List<NmObservation> observations)
        => rule.When.Kind switch
        {
            "bit_rule" => BitRule(rule, ctx, findings, observations),
            "nid_id_mapping" => NidIdMapping(rule, ctx, findings),
            "layout_check" => LayoutCheck(rule, ctx, findings),
            _ => false,
        };

    private static bool BitRule(
        NmRuleDef rule, NmRuleContext ctx,
        List<NmFinding> findings, List<NmObservation> observations)
    {
        var role = FindRole(ctx, rule.When);
        if (role is null) return true; // 位不存在：该规则对本 profile 不适用。

        var unreadable = new List<double>();
        var violations = new List<double>();

        foreach (var frame in ctx.NmFrames)
        {
            var data = frame.Frame.Data;
            if (data.Length <= role.Byte) { unreadable.Add(frame.Frame.Timestamp); continue; }

            var bitSet = (data[role.Byte] & (1 << role.Bit)) != 0;

            // 约束来自 profile（OEM 权威），不来自 DBC 位定义本身（spec §5.1）。
            var mustBeZero = role.Constraint is NmBitConstraint.MustBeZero
                or NmBitConstraint.MustBeZeroWhileDisabled;

            if (mustBeZero && bitSet) violations.Add(frame.Frame.Timestamp);
        }

        // 逐帧不可读必须显式可见，不能静默让规则失效（spec §7.5.1.2）。
        if (unreadable.Count > 0)
        {
            observations.Add(new NmObservation(
                "bit_rule_unreadable", null, null,
                unreadable.Min(), unreadable.Max(),
                $"{unreadable.Count} 帧 DLC 不足，无法读取 byte{role.Byte}.bit{role.Bit}（{role.Name}）",
                "measured", IsInference: false, RuleId: rule.Id));
        }

        if (violations.Count > 0 && !string.IsNullOrEmpty(rule.FindingCode))
        {
            findings.Add(new NmFinding(
                rule.FindingCode, "warn",
                $"{role.Name}（byte{role.Byte}.bit{role.Bit}）在 {violations.Count} 帧中为 1，" +
                $"但企标要求为 0",
                violations.Take(10).ToArray(),
                IsInference: rule.Observability == NmObservability.Partial,
                SuggestedNextCheck: "核对该节点是否误启用该功能位",
                RuleId: rule.Id,
                RequirementId: rule.Id,
                SourceRef: rule.SourceRef));
        }

        return true;
    }

    private static bool NidIdMapping(
        NmRuleDef rule, NmRuleContext ctx, List<NmFinding> findings)
    {
        var nidByte = ctx.Profile.Layout?.NidByte;
        if (nidByte is null) return true; // 未声明 NID 字节：规则不适用。

        // spec §14.4：扩展帧 NID == CAN ID 低字节；标准帧 NID == ID - 0x400。
        var violations = new List<double>();
        foreach (var frame in ctx.NmFrames)
        {
            var data = frame.Frame.Data;
            if (data.Length <= nidByte.Value) continue;

            var expected = frame.Def.IsExtended
                ? (byte)(frame.Frame.Id & 0xFF)
                : (byte)((frame.Frame.Id - 0x400) & 0xFF);

            if (data[nidByte.Value] != expected) violations.Add(frame.Frame.Timestamp);
        }

        if (violations.Count > 0 && !string.IsNullOrEmpty(rule.FindingCode))
        {
            findings.Add(new NmFinding(
                rule.FindingCode, "warn",
                $"{violations.Count} 帧 NID 与 CAN ID 映射不一致",
                violations.Take(10).ToArray(), IsInference: false,
                SuggestedNextCheck: "核对发送节点的 ECU 地址配置",
                RuleId: rule.Id, RequirementId: rule.Id, SourceRef: rule.SourceRef));
        }

        return true;
    }

    private static bool LayoutCheck(
        NmRuleDef rule, NmRuleContext ctx, List<NmFinding> findings)
    {
        var dlc = ctx.Profile.Messages.FirstOrDefault()?.Dlc ?? 8;
        var wrong = ctx.NmFrames
            .Where(f => f.Frame.Dlc != f.Def.Dlc)
            .Select(f => f.Frame.Timestamp)
            .ToArray();

        if (wrong.Length > 0 && !string.IsNullOrEmpty(rule.FindingCode))
        {
            findings.Add(new NmFinding(
                rule.FindingCode, "warn",
                $"{wrong.Length} 帧 DLC 与矩阵（{dlc}）不一致",
                wrong.Take(10).ToArray(), IsInference: false,
                SuggestedNextCheck: "核对帧格式配置",
                RuleId: rule.Id, RequirementId: rule.Id, SourceRef: rule.SourceRef));
        }

        return true;
    }

    private static NmBitRole? FindRole(NmRuleContext ctx, NmRuleWhen when)
    {
        var roles = ctx.Profile.StandardBitRoles;
        if (roles is null) return null;

        if (when.Role is { } name)
            return roles.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));

        if (when.Byte is { } b && when.Bit is { } bit)
            return roles.FirstOrDefault(r => r.Byte == b && r.Bit == bit);

        return null;
    }
}
```

- [ ] **Step 6: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmRuleEngineTests" -v minimal
```

预期：7 项通过。

- [ ] **Step 7: 向用户确认后提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmRuleEngine.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Bits.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmProfile.cs \
  tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleEngineTests.cs
git commit -m "$(cat <<'EOF'
feat(nm): rule engine skeleton with bit_rule, nid_id_mapping, layout_check

Unknown primitives fall into Unmodeled instead of throwing; unobservable
clauses never produce findings. Unreadable frames are reported as an explicit
observation rather than silently disabling the rule.

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: `cycle_tolerance` / `burst_shape` / `nmsts_transition`

**背景**：补齐剩余三个 O 级原语。`nmsts_transition` 只产 observation（spec §7.2），是 NM_06 时长判定的锚点。

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Timing.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Bits.cs`（`TryEvaluate` 的 switch）
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleEvaluatorsTimingTests.cs`

**Interfaces:**
- Consumes: `NmBurstDetector.Detect`、`NmCycleAnalyzer`
- Produces: 三个原语实现；`nmsts_transition` 产 `NmObservation(Kind: "nmsts_transition")`

- [ ] **Step 1: 写失败的测试**

创建 `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleEvaluatorsTimingTests.cs`：

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>spec §7.2：cycle_tolerance / burst_shape / nmsts_transition。</summary>
public class NmRuleEvaluatorsTimingTests
{
    private static readonly string[] TestReasons = ["test"];

    [Fact]
    public void CycleTolerance_DeviationBeyondPct_ProducesFinding()
    {
        // 12 帧、580ms 间隔，容差 ±10% = 50ms → 超差。
        var frames = Enumerable.Range(0, 12)
            .Select(i => Frame(i * 0.58, 0x500, 0xF4, 0))
            .ToArray();

        var result = Evaluate(frames,
            new NmRuleDef("NM_CYCLE", "周期偏差", "normal",
                new NmRuleWhen("cycle_tolerance", CycleMs: 500, Pct: 0.1),
                new NmRuleExpect("within_pct"), "NM_CYCLE_DEVIATION"));

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_CYCLE_DEVIATION", f.Code);
    }

    [Fact]
    public void CycleTolerance_WithinPct_NoFinding()
    {
        var frames = Enumerable.Range(0, 12)
            .Select(i => Frame(i * 0.52, 0x500, 0xF4, 0))
            .ToArray();

        var result = Evaluate(frames,
            new NmRuleDef("NM_CYCLE", "周期偏差", "normal",
                new NmRuleWhen("cycle_tolerance", CycleMs: 500, Pct: 0.1),
                new NmRuleExpect("within_pct"), "NM_CYCLE_DEVIATION"));

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void BurstShape_Compliant_NoFinding()
    {
        var frames = Enumerable.Range(0, 10)
            .Select(i => Frame(i * 0.020, 0x500, 0xF4, 0))
            .ToArray();

        var result = Evaluate(frames,
            new NmRuleDef("NM_03", "快发形态", "wakeup",
                new NmRuleWhen("burst_shape", CycleMs: 500),
                new NmRuleExpect("count_equals", Count: 10, IntervalMs: 20, Pct: 0.1),
                "NM_WAKEUP_BURST_MISMATCH"));

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void BurstShape_WrongFrameCount_ProducesFinding()
    {
        var frames = Enumerable.Range(0, 6)
            .Select(i => Frame(i * 0.020, 0x500, 0xF4, 0))
            .ToArray();

        var result = Evaluate(frames,
            new NmRuleDef("NM_03", "快发形态", "wakeup",
                new NmRuleWhen("burst_shape", CycleMs: 500),
                new NmRuleExpect("count_equals", Count: 10, IntervalMs: 20, Pct: 0.1),
                "NM_WAKEUP_BURST_MISMATCH"));

        Assert.Single(result.Findings);
    }

    [Fact]
    public void NmstsTransition_ProducesObservationOnly()
    {
        // 0→1 跃迁在 2.0s。规则只产 observation，绝不产 finding。
        var frames = new[]
        {
            Frame(0.0, 0x500, 0xF4, 0x00), Frame(1.0, 0x500, 0xF4, 0x00),
            Frame(2.0, 0x500, 0xF4, 0x01), Frame(3.0, 0x500, 0xF4, 0x01),
        };

        var result = Evaluate(frames,
            new NmRuleDef("NM_06", "NMSts 状态段", "wakeup",
                new NmRuleWhen("nmsts_transition", Byte: 3, Bit: 0),
                new NmRuleExpect("observe"), ""),
            roles: new[] { new NmBitRole(3, 0, "NmSts") });

        Assert.Empty(result.Findings);
        var obs = Assert.Single(result.Observations, o => o.Kind == "nmsts_transition");
        Assert.Equal(2.0, obs.Start);
    }

    [Fact]
    public void NmstsTransition_SharedId_Suppressed()
    {
        // 同一 CAN ID 下两个 NID 交替：切段会切碎，必须抑制。
        var frames = new[]
        {
            Frame(0.0, 0x500, 0x11, 0x00), Frame(1.0, 0x500, 0x22, 0x01),
            Frame(2.0, 0x500, 0x11, 0x00), Frame(3.0, 0x500, 0x22, 0x01),
        };

        var result = Evaluate(frames,
            new NmRuleDef("NM_06", "NMSts 状态段", "wakeup",
                new NmRuleWhen("nmsts_transition", Byte: 3, Bit: 0),
                new NmRuleExpect("observe"), ""),
            roles: new[] { new NmBitRole(3, 0, "NmSts") });

        Assert.Empty(result.Observations.Where(o => o.Kind == "nmsts_transition"));
    }

    private static NmRuleResult Evaluate(
        IReadOnlyList<NmFrame> frames, NmRuleDef rule, IReadOnlyList<NmBitRole>? roles = null)
    {
        var defs = frames.Select(f => f.Def).Distinct().ToArray();
        var profile = new NmProfile(
            NmProfileSource.Dbc, defs, new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1), StandardBitRoles: roles);
        var timing = NmTimingDefaults.FromProfile(profile.Timings, null);
        var ctx = new NmRuleContext(frames, Array.Empty<NmEpisode>(), profile, timing,
            new NmWindow(0, 10, "test")) { Rules = new[] { rule } };
        return NmRuleEngine.Evaluate(ctx.Rules, ctx);
    }

    private static NmFrame Frame(double t, uint id, byte b0, byte b1)
        => new(new NmMessageDef($"0x{id:X}", false, "NM", "A", 8, true,
                TestReasons, CycleMs: 500, TimeoutMs: 2000),
            new ReplayFrame(t, id, 8, new byte[] { b0, b1, 0, b1, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None));
}
```

**注意**：测试里 `Frame` 把 `b1` 同时写进 byte1 和 byte3，是为了让 `nmsts_transition`（读 byte3）和 `bit_rule`（读 byte1）都能取到值。

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmRuleEvaluatorsTimingTests" -v minimal
```

预期：6 项失败（原语未实现 → 全进 Unmodeled，findings 为空但 assertion 期望有 finding）。

- [ ] **Step 3: 实现三个原语**

创建 `src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Timing.cs`：

```csharp
namespace PeakCan.Host.Core.Analysis.Nm;

public static partial class NmRuleEvaluators
{
    private static bool CycleTolerance(
        NmRuleDef rule, NmRuleContext ctx, List<NmFinding> findings)
    {
        var perMessage = ctx.NmFrames
            .GroupBy(f => (f.Def.CanIdHex, f.Def.IsExtended));

        foreach (var group in perMessage)
        {
            // 周期与容差都按报文生效值取：规则显式配了才覆盖（spec §5.2）。
            var eff = ctx.Resolve(group.First().Def);
            var cycleMs = rule.When.CycleMs ?? eff.CycleMs;
            var pct = rule.When.Pct ?? eff.ToleranceRatio;
            if (cycleMs <= 0) continue;

            var toleranceMs = Math.Max(cycleMs * pct, eff.ToleranceAbsMs);
            var times = group.Select(f => f.Frame.Timestamp).OrderBy(t => t).ToArray();
            if (times.Length < 2) continue;

            var bad = new List<double>();
            for (var i = 1; i < times.Length; i++)
            {
                var deltaMs = (times[i] - times[i - 1]) * 1000.0;
                if (Math.Abs(deltaMs - cycleMs) > toleranceMs) bad.Add(times[i]);
            }

            // spec §7.4：偏差占比 > 5% 或连续 3 帧才报。
            if (bad.Count == 0) continue;
            if (bad.Count * 100.0 / (times.Length - 1) <= 5.0 && bad.Count < 3) continue;
            if (string.IsNullOrEmpty(rule.FindingCode)) continue;

            findings.Add(new NmFinding(
                rule.FindingCode, "warn",
                $"{group.Key.CanIdHex} 周期偏差 {bad.Count}/{times.Length - 1} 次" +
                $"（期望 {cycleMs}ms ±{toleranceMs:F0}ms）",
                bad.Take(10).ToArray(),
                IsInference: rule.Observability == NmObservability.Partial,
                SuggestedNextCheck: "检查调度/负载/时钟",
                RuleId: rule.Id, RequirementId: rule.Id, SourceRef: rule.SourceRef));
        }

        return true;
    }

    private static bool BurstShape(
        NmRuleDef rule, NmRuleContext ctx, List<NmFinding> findings)
    {
        var perMessage = ctx.NmFrames.GroupBy(f => (f.Def.CanIdHex, f.Def.IsExtended));

        foreach (var group in perMessage)
        {
            var times = group.Select(f => f.Frame.Timestamp).OrderBy(t => t).ToArray();
            var burst = NmBurstDetector.Detect(times, ctx.Resolve(group.First().Def));

            if (!burst.IsCandidate) continue;
            // 段被边界切断时不能判形态不合规——只是没看全。
            if (burst.IsIndeterminate) continue;
            if (burst.IsCompliant) continue;
            if (string.IsNullOrEmpty(rule.FindingCode)) continue;

            var expected = rule.Expect.Count ?? ctx.Resolve(group.First().Def).ImmediateCount;
            findings.Add(new NmFinding(
                rule.FindingCode, "warn",
                $"{group.Key.CanIdHex} 快发段 {burst.FrameCount} 帧 / {burst.IntervalCount} 个间隔，" +
                $"期望 {expected} 帧",
                new[] { burst.Start },
                IsInference: rule.Observability == NmObservability.Partial,
                SuggestedNextCheck: "核对 N_ImmediateNM_TIMES 与 T_NM_ImmediateCycleTime 配置",
                RuleId: rule.Id, RequirementId: rule.Id, SourceRef: rule.SourceRef));
        }

        return true;
    }

    private static bool NmstsTransition(
        NmRuleDef rule, NmRuleContext ctx, List<NmObservation> observations)
    {
        var byteIndex = rule.When.Byte;
        var bitIndex = rule.When.Bit;
        if (byteIndex is null || bitIndex is null) return true;

        var perMessage = ctx.NmFrames.GroupBy(f => (f.Def.CanIdHex, f.Def.IsExtended));
        var nidByte = ctx.Profile.Layout?.NidByte;

        foreach (var group in perMessage)
        {
            var ordered = group.OrderBy(f => f.Frame.Timestamp).ToArray();

            // spec §7.5.1 M2 边界：共享 ID 时按报文切段会切碎，抑制。
            // 共享判定必须读 NID 字节，不能读状态位所在字节（那是要判断的量本身）。
            if (nidByte is { } nb)
            {
                var nids = ordered
                    .Where(f => f.Frame.Data.Length > nb)
                    .Select(f => f.Frame.Data[nb])
                    .Distinct().Take(2).Count();
                if (nids > 1) continue;
            }

            int? prev = null;
            for (var i = 0; i < ordered.Length; i++)
            {
                var data = ordered[i].Frame.Data;
                if (data.Length <= byteIndex.Value) continue;

                var value = (data[byteIndex.Value] >> bitIndex.Value) & 1;
                if (prev is not null && value != prev)
                {
                    observations.Add(new NmObservation(
                        "nmsts_transition", ordered[i].Def.Sender, group.Key.CanIdHex,
                        ordered[i].Frame.Timestamp, ordered[i].Frame.Timestamp,
                        $"NMSts {prev}→{value}",
                        "measured", IsInference: false, RuleId: rule.Id));
                }
                prev = value;
            }
        }

        return true;
    }
}
```

- [ ] **Step 4: 把三个原语加进分派表**

把 `NmRuleEvaluators.Bits.cs` 的 `TryEvaluate` 改为：

```csharp
    internal static bool TryEvaluate(
        NmRuleDef rule, NmRuleContext ctx,
        List<NmFinding> findings, List<NmObservation> observations)
        => rule.When.Kind switch
        {
            "bit_rule" => BitRule(rule, ctx, findings, observations),
            "nid_id_mapping" => NidIdMapping(rule, ctx, findings),
            "layout_check" => LayoutCheck(rule, ctx, findings),
            "cycle_tolerance" => CycleTolerance(rule, ctx, findings),
            "burst_shape" => BurstShape(rule, ctx, findings),
            "nmsts_transition" => NmstsTransition(rule, ctx, observations),
            _ => false,
        };
```

- [ ] **Step 5: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmRuleEvaluatorsTimingTests|FullyQualifiedName~NmRuleEngineTests" -v minimal
```

预期：13 项通过。

- [ ] **Step 6: 向用户确认后提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Timing.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Bits.cs \
  tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleEvaluatorsTimingTests.cs
git commit -m "$(cat <<'EOF'
feat(nm): add cycle_tolerance, burst_shape and nmsts_transition primitives

nmsts_transition only emits observations (spec 7.2) and is suppressed on shared
CAN IDs where per-message segmentation would fragment the state machine.

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: 规则引擎接入分析器

**背景**：Task 5/6 的原语还没接到 `NmAnalyzer`。没有这一步，规则永远是死代码。

**Files:**
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmAnalyzer.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmProfile.cs`（加 `StandardRules`）
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleIntegrationTests.cs`

**Interfaces:**
- Consumes: `NmRuleEngine.Evaluate`
- Produces: `NmReport.Findings` 含规则产出的 finding（带 `RuleId`）；`NmReport.UnmodeledRules` 非空

- [ ] **Step 1: 写失败的集成测试**

创建 `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleIntegrationTests.cs`：

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §12：合成 fixture 上「注入故障必触发、合法场景零误报」。
/// 真实 trace 只验证据可解释，不拿它调规则。
/// </summary>
public class NmRuleIntegrationTests
{
    private static readonly string[] TestReasons = ["test"];

    [Fact]
    public void Analyze_ReservedBitSet_ProducesRuleFindingWithTraceability()
    {
        var frames = new[]
        {
            new ReplayFrame(0.0, 0x500, 8, new byte[] { 0xF4, 0x01, 0, 0, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(frames, rules: true), 0, 1.0));

        var f = Assert.Single(report.Findings, x => x.Code == "NM_RMR_SET_WHILE_DISABLED");
        // 条款可追溯：报告必须带规则号与出处。
        Assert.Equal("NM_05", f.RuleId);
        Assert.Equal("§5.4.3", f.SourceRef);
    }

    [Fact]
    public void Analyze_NoStandardProfile_NoRuleFindings()
    {
        var frames = new[]
        {
            new ReplayFrame(0.0, 0x500, 8, new byte[] { 0xF4, 0x01, 0, 0, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(frames, rules: false), 0, 1.0));

        Assert.DoesNotContain(report.Findings, x => x.RuleId is not null);
        Assert.True(report.UnmodeledRules is null || report.UnmodeledRules.Count == 0);
    }

    [Fact]
    public void Analyze_UnobservableClause_AppearsInUnmodeledOnly()
    {
        var frames = new[]
        {
            new ReplayFrame(0.0, 0x500, 8, new byte[] { 0xF4, 0x00, 0, 0, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None),
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(frames, rules: true), 0, 1.0));

        Assert.NotNull(report.UnmodeledRules);
        Assert.Contains(report.UnmodeledRules!, u => u.Clause == "NM_U1");
    }

    private static NmProfile Profile(IReadOnlyList<ReplayFrame> frames, bool rules)
    {
        var def = new NmMessageDef("0x500", false, "NM", "A", 8, true,
            TestReasons, CycleMs: 500, TimeoutMs: 2000);
        return new NmProfile(
            NmProfileSource.Dbc, new[] { def }, new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            new[] { new NmNodeDef("A", 0xF4, null, true) },
            Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1),
            StandardBitRoles: rules
                ? new[]
                {
                    new NmBitRole(1, 0, "RepeatMessageRequest")
                        { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
                }
                : null,
            StandardRules: rules
                ? new[]
                {
                    new NmRuleDef("NM_05", "预留位必须为 0", "normal",
                        new NmRuleWhen("bit_rule", Role: "RepeatMessageRequest"),
                        new NmRuleExpect("is_zero"), "NM_RMR_SET_WHILE_DISABLED")
                    {
                        SourceRef = "§5.4.3",
                    },
                    new NmRuleDef("NM_U1", "内部定时器", "normal",
                        new NmRuleWhen("stop_sending"), new NmRuleExpect("observe"), "")
                    {
                        Observability = NmObservability.Unobservable,
                    },
                }
                : null);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmRuleIntegrationTests" -v minimal
```

预期：编译失败（`StandardRules` 不存在）。

- [ ] **Step 3: 给 `NmProfile` 加规则**

在 `NmProfile.cs` 的 `NmProfile` record 末尾追加：

```csharp
    // 展平后的企标规则；null 表示未选企标（不跑规则引擎）。
    IReadOnlyList<NmRuleDef>? StandardRules = null);
```

- [ ] **Step 4: 在 `NmAnalyzer` 里调用规则引擎**

在 `NmContentAnalyzer` 调用之后（`NmAnalyzer.cs:301` 附近）插入：

```csharp
        // 企标规则（spec §7）：只有选中 profile 时才跑；结果与 M1 finding 合并，
        // 但保留 RuleId/SourceRef 以便条款追溯。传全局时序，evaluator 按报文自行 resolve。
        if (request.Profile.StandardRules is { Count: > 0 } standardRules)
        {
            var ruleResult = NmRuleEngine.Evaluate(standardRules,
                new NmRuleContext(nmFrames, episodes, request.Profile, globalTiming, window)
                {
                    Rules = standardRules,
                });

            findings.AddRange(ruleResult.Findings);
            observations.AddRange(ruleResult.Observations);
            unmodeled.AddRange(ruleResult.Unmodeled);
            truncations.AddRange(ruleResult.Truncations);
        }
```

在 `Finish` 之前声明 `var unmodeled = new List<NmUnmodeledRule>();`，并把 `Finish` 里的 `UnmodeledRules: null` 改为：

```csharp
                UnmodeledRules: unmodeled.Count == 0 ? null : unmodeled);
```

- [ ] **Step 5: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmRuleIntegrationTests" -v minimal
```

预期：3 项通过。

- [ ] **Step 6: 跑 NM 全量定向测试**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~Analysis.Nm" -v minimal
```

预期：全绿。**若 `NmSpecClosureTests` 变红，说明无 profile 路径被污染**——`StandardRules` 为 null 时规则引擎完全不跑，检查 `if` 条件。

- [ ] **Step 7: 向用户确认后提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmAnalyzer.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmProfile.cs \
  tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleIntegrationTests.cs
git commit -m "$(cat <<'EOF'
feat(nm): run standard rules inside the analyzer when a profile is selected

Rule findings carry RuleId and SourceRef so the report stays clause-traceable.
Unobservable clauses surface in UnmodeledRules only. Without a standard profile
the engine does not run at all, keeping M1 output unchanged.

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: P 级原语 `first_frame_latency` / `repeat_message_duration`

**背景**：这两个是 §14.6 contract test 的硬需求——真实 trace 两段 RMS 各超差约 560ms，必须能被 `repeat_message_duration` 检出。

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Latency.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Bits.cs`（分派表）
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleEvaluatorsLatencyTests.cs`

**Interfaces:**
- Consumes: `NmEpisode`、`NmBurstDetector`
- Produces: 两个 P 级原语；产出的 finding `IsInference = true`

- [ ] **Step 1: 写失败的测试**

创建 `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleEvaluatorsLatencyTests.cs`：

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §14.6：真实 trace 两段 RMS 实测 2.121s / 2.325s，允许窗 1.440–1.560 / 1.640–1.760。
/// 必须能检出，否则 repeat_message_duration 实现了也验不出来。
/// </summary>
public class NmRuleEvaluatorsLatencyTests
{
    private static readonly string[] TestReasons = ["test"];

    [Fact]
    public void RepeatMessageDuration_NoBurst_OverLimit_ProducesFinding()
    {
        // 无快发：允许 1440–1560ms，实测 2121ms（真实 trace 第一段 §14.6）。
        var frames = Steady(0.0, count: 6, stepSec: 0.42);   // 跨 2.1s

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "nmsts"),
            burstObserved: false);

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_REPEAT_MESSAGE_DURATION_EXCEEDED", f.Code);
        Assert.True(f.IsInference, "P 级原语必须标推断");
    }

    [Fact]
    public void RepeatMessageDuration_WithBurst_OverLimit_ProducesFinding()
    {
        // 有快发：允许 1640–1760ms。真实 trace 第二段 = 10 帧快发 + 稳态，
        // 实测 2325ms（§14.6）。这里构造同样的形态：快发段之后接 536ms 稳态。
        var burst = Enumerable.Range(0, 10).Select(i => i * 0.020).ToArray();
        var steady = Enumerable.Range(1, 4).Select(i => 0.180 + i * 0.536).ToArray();
        var frames = burst.Concat(steady).Select(t => FrameAt(t)).ToArray();

        var result = Evaluate(frames, Rule(min: 1640, max: 1760, anchor: "nmsts"),
            burstObserved: true);

        var f = Assert.Single(result.Findings);
        Assert.Equal("NM_REPEAT_MESSAGE_DURATION_EXCEEDED", f.Code);
    }

    [Fact]
    public void RepeatMessageDuration_WithinLimit_NoFinding()
    {
        var frames = Steady(0.0, count: 4, stepSec: 0.42);   // 1.26s

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "nmsts"),
            burstObserved: false);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void RepeatMessageDuration_SegmentCutByWindow_IsIndeterminate()
    {
        // 段延伸到窗口末尾仍未结束：时长只是下限，不能判超差。
        var frames = Steady(0.0, count: 6, stepSec: 0.42);

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "nmsts"),
            burstObserved: false, windowEnd: frames[^1].Frame.Timestamp);

        Assert.Empty(result.Findings);
        Assert.Contains(result.Observations, o => o.Kind == "repeat_message_duration");
    }

    [Fact]
    public void RepeatMessageDuration_NoNmsts_ObservationOnlyNoFinding()
    {
        // 没有 NMSts 锚点时，首末帧跨度把 NOS 也算了进去，
        // 用它判超差会在「正常释放」场景误报（spec §7.2）。
        var frames = Steady(0.0, count: 6, stepSec: 0.42);

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "nmsts"),
            withNmsts: false);

        Assert.Empty(result.Findings);
        Assert.Contains(result.Observations,
            o => o.Kind == "repeat_message_duration" && o.Detail.Contains("NMSts 不可读",
                StringComparison.Ordinal));
    }

    [Fact]
    public void RepeatMessageDuration_EpisodeBoundaryEnd_IsIndeterminate()
    {
        // episode 自身被 trace 边界切断（BoundaryEnd）：同样只能给下限。
        var frames = Steady(0.0, count: 6, stepSec: 0.42);
        var episode = new NmEpisode(0, frames[0].Frame.Timestamp,
            frames[^1].Frame.Timestamp + 1.0, "active", null,
            new[] { "A" }, BoundaryStart: false, BoundaryEnd: true);

        var result = Evaluate(frames, Rule(min: 1440, max: 1560, anchor: "burst_end"),
            episodes: new[] { episode }, windowEnd: frames[^1].Frame.Timestamp + 5);

        Assert.Empty(result.Findings);
        Assert.Contains(result.Observations, o => o.Kind == "repeat_message_duration");
    }

    [Fact]
    public void FirstFrameLatency_BeyondMax_ProducesFinding()
    {
        // episode 从 0 开始，首帧在 0.3s，允许 10ms。
        var frames = Steady(0.3, count: 4, stepSec: 0.5);
        var episode = new NmEpisode(0, 0.0, 2.0, "active", null,
            new[] { "A" }, false, false);

        var result = Evaluate(frames,
            new NmRuleDef("NM_02", "首帧延迟", "wakeup",
                new NmRuleWhen("first_frame_latency", LatencyKind: "start_nm_tx"),
                new NmRuleExpect("within_pct", MaxMs: 10),
                "NM_FIRST_FRAME_LATENCY"),
            episodes: new[] { episode });

        Assert.Single(result.Findings);
    }

    private static NmRuleDef Rule(int min, int max, string anchor)
        => new("NM_06", "RMS 持续时长", "wakeup",
            new NmRuleWhen("repeat_message_duration", CycleMs: 500),
            new NmRuleExpect("duration_within_ms", MinMs: min, MaxMs: max, Anchor: anchor),
            "NM_REPEAT_MESSAGE_DURATION_EXCEEDED");

    /// <summary>
    /// 带 NMSts 位的 profile：byte3.bit0。测试帧把 NMSts 恒写 0
    /// （即整段都是 Repeat Message State），这样时长判定有确定锚点。
    /// </summary>
    private static NmRuleResult Evaluate(
        IReadOnlyList<NmFrame> frames, NmRuleDef rule,
        bool burstObserved = false,
        IReadOnlyList<NmEpisode>? episodes = null,
        double? windowEnd = null,
        bool withNmsts = true)
    {
        // burstObserved 只影响测试用例的帧构造（快发 vs 稳态），
        // 检测器本身从时间戳判断，不需要 profile 开关。
        _ = burstObserved;
        var defs = frames.Select(f => f.Def).Distinct().ToArray();
        var profile = new NmProfile(
            NmProfileSource.Dbc, defs, new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1),
            StandardBitRoles: withNmsts ? new[] { new NmBitRole(3, 0, "NmSts") } : null);
        var timing = NmTimingDefaults.FromProfile(profile.Timings, null);
        // 默认 episode 的终点必须**大于**末帧，否则「段尾触边界 → 时长只是下限」
        // 的抑制会吞掉全部判定，超差用例永远红。只有显式要验截断的用例
        // 才让 episode.End 等于末帧（见 RepeatMessageDuration_SegmentCutByWindow）。
        var eps = episodes ?? new[]
        {
            new NmEpisode(0, frames[0].Frame.Timestamp, frames[^1].Frame.Timestamp + 1.0,
                "active", null, new[] { "A" }, false, false),
        };
        var ctx = new NmRuleContext(frames, eps, profile, timing,
            new NmWindow(0, windowEnd ?? frames[^1].Frame.Timestamp + 5, "test"))
        { Rules = new[] { rule } };
        return NmRuleEngine.Evaluate(ctx.Rules, ctx);
    }

    private static NmFrame[] Steady(double start, int count, double stepSec)
        => Enumerable.Range(0, count).Select(i => FrameAt(start + i * stepSec)).ToArray();

    // byte3 = 0x00 → NMSts=0，整段都是 Repeat Message State（有确定锚点）。
    // byte0 = 0x00 → 标准帧 0x500 的期望 NID，(0x500-0x400)&0xFF = 0x00。
    private static NmFrame FrameAt(double t)
        => new(new NmMessageDef("0x500", false, "NM", "A", 8, true,
                TestReasons, CycleMs: 500, TimeoutMs: 2000),
            new ReplayFrame(t, 0x500, 8,
                new byte[] { 0x00, 0, 0, 0x00, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None));
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmRuleEvaluatorsLatencyTests" -v minimal
```

预期：5 项失败。

- [ ] **Step 3: 实现两个 P 级原语**

创建 `src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Latency.cs`：

```csharp
namespace PeakCan.Host.Core.Analysis.Nm;

public static partial class NmRuleEvaluators
{
    private static bool RepeatMessageDuration(
        NmRuleDef rule, NmRuleContext ctx,
        List<NmFinding> findings, List<NmObservation> observations)
    {
        var minMs = rule.Expect.MinMs ?? 0;
        var maxMs = rule.Expect.MaxMs ?? int.MaxValue;

        foreach (var episode in ctx.Episodes)
        {
            var frames = ctx.NmFrames
                .Where(f => f.Frame.Timestamp >= episode.Start
                            && f.Frame.Timestamp <= episode.End)
                .OrderBy(f => f.Frame.Timestamp)
                .ToArray();
            if (frames.Length < 2) continue;

            var times = frames.Select(f => f.Frame.Timestamp).ToArray();
            var burst = NmBurstDetector.DetectInEpisode(
                times, episode.Start, episode.End, ctx.Resolve(frames[0].Def));

            // anchor=burst_end 时，RMS 起点 = 快发段结束；无快发则用首帧。
            var start = rule.Expect.Anchor == "burst_end" && burst.IsCandidate
                ? burst.End
                : times[0];
            var end = times[^1];
            var durationMs = (end - start) * 1000.0;

            // 没有 NMSts 锚点时，首帧到末帧的跨度把 NOS 也算了进去，
            // 用它判 RMS 超差必然在「正常释放」场景误报（spec §7.2 的
            // 「缺少 NMSts 只能 inferred」在此落地为：只产 observation）。
            var nmstsReadable = TryReadNmstsSpan(ctx, frames, out var nmstsStart, out var nmstsEnd);
            if (rule.Expect.Anchor == "nmsts" && !nmstsReadable)
            {
                observations.Add(new NmObservation(
                    "repeat_message_duration", frames[0].Def.Sender, frames[0].Def.CanIdHex,
                    times[0], times[^1],
                    "NMSts 不可读，无法界定 Repeat Message State 终点；" +
                    "首末帧跨度含 NOS，不作为违规证据",
                    "inferred", IsInference: true, RuleId: rule.Id));
                continue;
            }

            if (nmstsReadable)
            {
                start = nmstsStart;
                end = nmstsEnd;
                durationMs = (end - start) * 1000.0;
            }

            // 截断判据用 episode 的边界标志与窗口终点，不用「末帧 == episode.End」：
            // 后者在正常切片下恒成立，会把每一次判定都吞掉（Review Focus #5）。
            var truncated = episode.BoundaryEnd || end >= ctx.Window.End - 1e-9;

            observations.Add(new NmObservation(
                "repeat_message_duration", frames[0].Def.Sender, frames[0].Def.CanIdHex,
                start, end,
                $"RMS 实测 {durationMs:F0}ms（允许 {minMs}–{maxMs}ms，" +
                $"{(burst.IsCandidate ? "含" : "不含")}快发）" +
                (truncated ? "，窗口截断：时长为下限" : ""),
                burst.IsCandidate ? "measured" : "inferred",
                IsInference: !burst.IsCandidate,
                RuleId: rule.Id));

            // minMs 只用于文案；下限违规不单独报——低于下限通常意味着 RMS 被提前
            // 截断（节点提前停发），已由 NM_NODE_DROPOUT / 空档归因覆盖。
            _ = minMs;

            // 段被窗口/episode 边界切断：时长只是下限，不能判超差（Review Focus #5）。
            if (truncated) continue;

            if (durationMs > maxMs && !string.IsNullOrEmpty(rule.FindingCode))
            {
                findings.Add(new NmFinding(
                    rule.FindingCode, "warn",
                    $"{frames[0].Def.CanIdHex} Repeat Message State 持续 {durationMs:F0}ms，" +
                    $"超出上限 {maxMs}ms（+{durationMs - maxMs:F0}ms）",
                    new[] { start, end }, IsInference: true,
                    SuggestedNextCheck: "核对 T_REPEAT_MESSAGE 与 NOS 进入条件",
                    RuleId: rule.Id, RequirementId: rule.Id, SourceRef: rule.SourceRef));
            }
        }

        return true;
    }

    /// <summary>
    /// 读 NMSts 位，返回该 episode 内 NMSts=0（Repeat Message State）的起止。
    /// 位不可读或全程无 NMSts=0 段时返回 false。
    /// </summary>
    private static bool TryReadNmstsSpan(
        NmRuleContext ctx, IReadOnlyList<NmFrame> frames,
        out double start, out double end)
    {
        start = 0;
        end = 0;

        var role = ctx.Profile.StandardBitRoles?
            .FirstOrDefault(r => string.Equals(r.Name, "NmSts", StringComparison.Ordinal));
        if (role is null) return false;

        double? first = null;
        double? last = null;
        foreach (var frame in frames)
        {
            var data = frame.Frame.Data;
            if (data.Length <= role.Byte) continue;
            // NMSts=0 → Repeat Message State（spec §7.5.1）。
            if (((data[role.Byte] >> role.Bit) & 1) != 0) continue;

            first ??= frame.Frame.Timestamp;
            last = frame.Frame.Timestamp;
        }

        if (first is null || last is null) return false;
        start = first.Value;
        end = last.Value;
        return true;
    }

    private static bool FirstFrameLatency(
        NmRuleDef rule, NmRuleContext ctx, List<NmFinding> findings)
    {
        var maxMs = rule.Expect.MaxMs ?? 0;
        if (maxMs <= 0) return true;

        foreach (var episode in ctx.Episodes)
        {
            var first = ctx.NmFrames
                .Where(f => f.Frame.Timestamp >= episode.Start
                            && f.Frame.Timestamp <= episode.End)
                .OrderBy(f => f.Frame.Timestamp)
                .FirstOrDefault();
            if (first is null) continue;

            var latencyMs = (first.Frame.Timestamp - episode.Start) * 1000.0;
            if (latencyMs <= maxMs) continue;
            if (string.IsNullOrEmpty(rule.FindingCode)) continue;

            findings.Add(new NmFinding(
                rule.FindingCode, "warn",
                $"episode {episode.Index} 首帧延迟 {latencyMs:F0}ms（上限 {maxMs}ms）",
                new[] { episode.Start, first.Frame.Timestamp }, IsInference: true,
                SuggestedNextCheck: "核对 T_START_NM_TX / T_WakeUp 配置",
                RuleId: rule.Id, RequirementId: rule.Id, SourceRef: rule.SourceRef));
        }

        return true;
    }
}
```

- [ ] **Step 4: 加进分派表**

`NmRuleEvaluators.Bits.cs` 的 `TryEvaluate` 追加两行：

```csharp
            "repeat_message_duration" => RepeatMessageDuration(
                rule, ctx, findings, observations),
            "first_frame_latency" => FirstFrameLatency(rule, ctx, findings),
```

- [ ] **Step 5: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmRuleEvaluatorsLatencyTests" -v minimal
```

预期：7 项通过（6 项时长判定 + 1 项首帧延迟）。

- [ ] **Step 6: 向用户确认后提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Latency.cs \
  src/PeakCan.Host.Core/Analysis/Nm/NmRuleEvaluators.Bits.cs \
  tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleEvaluatorsLatencyTests.cs
git commit -m "$(cat <<'EOF'
feat(nm): add first_frame_latency and repeat_message_duration primitives

Both are P-level: findings are marked inferred. A segment that reaches the
episode boundary is treated as indeterminate because the measured duration is
only a lower bound.

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: 合法场景负例集（零误报门）

**背景**：spec §12 要求「合法被动唤醒、合法主动唤醒、合法释放/睡眠、RSS 停发、窗口截断 —— 均不得报违规」。没有这组负例，规则很容易在真实数据上全面误报。

**Files:**
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleNegativeSetTests.cs`

**Interfaces:**
- Consumes: 全部已实现原语
- Produces: 5 个合法场景的零 finding 断言

- [ ] **Step 1: 写测试**

创建 `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleNegativeSetTests.cs`：

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §12 合法场景负例集。这些场景在真实网络上完全正常，
/// 任何一条产出 finding 都是误报，必须挡住。
/// </summary>
public class NmRuleNegativeSetTests
{
    private static readonly string[] TestReasons = ["test"];

    [Fact]
    public void LegalPassiveWakeup_NoViolation()
    {
        // 被动唤醒：AWB=0，无快发。RMS 段 NMSts=0 持续 1.5s（在 1440–1560 内），
        // 之后 NMSts=1 进入 NOS。若把整段都写 NMSts=0，跨度会超 1760ms，
        // 那是 fixture 不真实，不是误报。
        var frames = new[]
        {
            Frame(0.1, awb: 0, nmsts: 0), Frame(0.6, awb: 0, nmsts: 0),
            Frame(1.1, awb: 0, nmsts: 0), Frame(1.6, awb: 0, nmsts: 0),
            Frame(2.1, awb: 0, nmsts: 1), Frame(2.6, awb: 0, nmsts: 1),
            Frame(3.1, awb: 0, nmsts: 1), Frame(3.6, awb: 0, nmsts: 1),
        };

        var result = Evaluate(frames, windowEnd: 6.0);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void LegalActiveWakeup_NoViolation()
    {
        // 主动唤醒：AWB=1，10 帧快发（20ms，合规），之后 500ms 稳态。
        var burst = Enumerable.Range(0, 10)
            .Select(i => Frame(0.1 + i * 0.020, awb: 1, nmsts: 0));
        var steady = Enumerable.Range(0, 6)
            .Select(i => Frame(0.4 + i * 0.5, awb: 1, nmsts: i < 2 ? 0 : 1));
        var frames = burst.Concat(steady).ToArray();

        var result = Evaluate(frames, windowEnd: 5.0);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void LegalReleaseToSleep_NoViolation()
    {
        // 正常释放：RMS 1.5s → NOS → 2.0s 后停发，窗口到 6.0s（全网静默）。
        var frames = new[]
        {
            Frame(0.0, awb: 0, nmsts: 0), Frame(0.5, awb: 0, nmsts: 0),
            Frame(1.0, awb: 0, nmsts: 0), Frame(1.5, awb: 0, nmsts: 0),
            Frame(2.0, awb: 0, nmsts: 1),
        };

        var result = Evaluate(frames, windowEnd: 6.0);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void RssStopSending_NoViolation()
    {
        // NM_09：进入 RSS 后停止发送是合规的，不能判违规。
        var frames = new[]
        {
            Frame(0.0, awb: 0, nmsts: 0), Frame(0.5, awb: 0, nmsts: 0),
            Frame(1.0, awb: 0, nmsts: 0), Frame(1.5, awb: 0, nmsts: 1),
        };
        var rules = Rules().Where(r => r.Id != "NM_05").ToArray();

        var result = Evaluate(frames, windowEnd: 5.0, rules: rules);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public void TruncatedWindow_NoViolation()
    {
        // 窗口在 RMS 中间结束：时长只有下限，不得判超差。
        // 6 帧全 NMSts=0 跨度 2.5s，若未被截断本会超差——正是要验的抑制路径。
        var frames = Steady(0.0, count: 6, stepSec: 0.5, awb: 0, nmsts: 0);

        // 窗口正好落在末帧上 → episode 被切断。
        var result = Evaluate(frames, windowEnd: frames[^1].Frame.Timestamp);

        Assert.Empty(result.Findings);
    }

    private static NmRuleResult Evaluate(
        IReadOnlyList<NmFrame> frames, double windowEnd,
        IReadOnlyList<NmRuleDef>? rules = null)
    {
        var defs = frames.Select(f => f.Def).Distinct().ToArray();
        var profile = new NmProfile(
            NmProfileSource.Dbc, defs, new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            new[] { new NmNodeDef("A", 0xF4, null, true) },
            Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance(),
            Layout: new NmLayout(NidByte: 0, CbvByte: 1),
            StandardBitRoles: new[]
            {
                new NmBitRole(1, 0, "RepeatMessageRequest")
                    { Constraint = NmBitConstraint.MustBeZeroWhileDisabled },
                new NmBitRole(1, 4, "ActiveWakeup"),
                new NmBitRole(3, 0, "NmSts"),
            },
            StandardRules: rules ?? Rules());

        var timing = NmTimingDefaults.FromProfile(profile.Timings, null);
        var episodes = NmEpisodeBuilder.Build(
            frames, timing.TimeoutMs, 0, windowEnd);
        var ctx = new NmRuleContext(frames, episodes, profile, timing,
            new NmWindow(0, windowEnd, "test"))
        { Rules = profile.StandardRules! };
        return NmRuleEngine.Evaluate(ctx.Rules, ctx);
    }

    private static NmRuleDef[] Rules() => new[]
    {
        new NmRuleDef("NM_01", "NID 与 ID 映射一致", "normal",
            new NmRuleWhen("nid_id_mapping"),
            new NmRuleExpect("matches_id_low_byte"), "NM_NID_MISMATCH"),
        new NmRuleDef("NM_05", "预留位必须为 0", "normal",
            new NmRuleWhen("bit_rule", Role: "RepeatMessageRequest"),
            new NmRuleExpect("is_zero"), "NM_RMR_SET_WHILE_DISABLED"),
        new NmRuleDef("NM_06", "RMS 持续时长", "wakeup",
            new NmRuleWhen("repeat_message_duration", CycleMs: 500),
            new NmRuleExpect("duration_within_ms", MinMs: 1440, MaxMs: 1760,
                Anchor: "nmsts"), "NM_REPEAT_MESSAGE_DURATION_EXCEEDED")
        {
            Observability = NmObservability.Partial,
        },
        new NmRuleDef("NM_03", "快发形态", "wakeup",
            new NmRuleWhen("burst_shape", CycleMs: 500),
            new NmRuleExpect("count_equals", Count: 10, IntervalMs: 20, Pct: 0.1),
            "NM_WAKEUP_BURST_MISMATCH")
        {
            Observability = NmObservability.Partial,
        },
    };

    private static NmFrame[] Steady(double start, int count, double stepSec, byte awb, byte nmsts)
        => Enumerable.Range(0, count)
            .Select(i => Frame(start + i * stepSec, awb, nmsts))
            .ToArray();

    /// <summary>
    /// 标准帧 0x500 的期望 NID = (0x500 - 0x400) &amp; 0xFF = 0x00，故 byte0 必须写 0x00，
    /// 否则 NM_01 会正确报 NM_NID_MISMATCH。NMSts 在 byte3.bit0。
    /// </summary>
    private static NmFrame Frame(double t, byte awb, byte nmsts)
        => new(new NmMessageDef("0x500", false, "NM", "A", 8, true,
                TestReasons, CycleMs: 500, TimeoutMs: 2000),
            new ReplayFrame(t, 0x500, 8,
                new byte[] { 0x00, (byte)(awb << 4), 0, nmsts, 0, 0, 0, 0 },
                PeakCan.HIL.Core.FrameFlags.None));
}
```

- [ ] **Step 2: 跑测试**

```bash
cd /d/claude_proj2/peakcan-host && dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmRuleNegativeSetTests" -v minimal
```

预期：5 项通过。

**若失败，不要改测试去迁就实现。** 失败说明原语在合法场景误报，是真 bug。按 `superpowers:systematic-debugging` 找根因：
- `LegalReleaseToSleep` 失败 → `repeat_message_duration` 没正确抑制 episode 边界。
- `LegalActiveWakeup` 失败 → `burst_shape` 把 10 帧 + 稳态混成一段。
- `TruncatedWindow` 失败 → `NmEpisodeBuilder.Build` 的 `BoundaryEnd` 没传进规则上下文。

- [ ] **Step 3: 向用户确认后提交**

```bash
git add tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRuleNegativeSetTests.cs
git commit -m "$(cat <<'EOF'
test(nm): add legal-scenario negative set to guard against false positives

Covers passive wakeup, active wakeup, release to sleep, RSS stop-sending and a
truncated window. None of them may produce a violation.

Co-Authored-By: Claude Code <noreply@anthropic.com>
EOF
)"
```

---

## 后续计划（不在本计划范围）

以下切片依赖本计划的产物，但各自成篇：

- **S4 剩余**：`normal_resume_shape` / `stop_sending` / `max_gap_observe` — 需要 `normal_resume_shape` 的周期+容差+最少帧数判定，与 `cycle_tolerance` 共用逻辑，单独计划避免本计划膨胀。
- **S5**：数据体检（§9 十项）+ 真实 trace 端到端 contract test（§14.6）。
- **S6**：Activity 段模型（§7.5）— **由并行会话 `claude-proj2-1a` 实现**，本计划任何 Task 不得触碰 `NmActivityBuilder`、`NmSegment` 三字段、`NmPanelFlow`。
- **S7**：只读选择器 + 标定面板 + `NmCacheKey` 改造（§10.1）。

**S5 有一个已知依赖，执行时必须知道**：§14.6 的「必须不出现 `NM_SENDER_SILENT_BEYOND_TIMEOUT`」这条断言，依赖的是 `NmGapAnalyzer` 现有的 `OthersActiveBetween` / `OverlapsAllNmSilent` 语义——**本计划完全没有改动那条产出路径**（Task 1/2 只改了 `NmMessageReport` 的判定字段，没碰 gap 归因）。

如果 S5 届时这条断言失败，根因在 `NmGapAnalyzer`，不是本计划新增的契约引入的。别去改 `NmSilenceVerdict` 找问题。
