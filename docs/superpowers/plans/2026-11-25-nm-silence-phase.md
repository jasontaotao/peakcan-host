# NM 静默相位推断实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (native) or superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 NM 分析的“整网状态”行、报告导出与 AI overview 中，按“NM 超时边界 + 发送者归属 + 总线是否静止”输出 `release_wait` / `prepare_sleep` / `sleep` 三个推断相位窗口；全部标 `inferred`，不出 finding。

**Architecture:** 新增纯函数 `NmSilencePhaseAnalyzer`，由 `NmGapAnalyzer` 在 episode 间隙和尾部静默两处调用；结果以 `NmReport.SilencePhases`（init 属性）暴露。App 网络行把通用静默带按相位区间扣除后追加三种新 kind；导出/AI 直接消费结构化相位。

**Tech Stack:** C# / .NET 10 / WPF / xUnit + FluentAssertions / ScottPlot（不涉及）。

**Spec:** `docs/superpowers/specs/2026-11-25-nm-silence-phase-design.md`

## Global Constraints

- **不 commit、不 push**：用户未指示前所有改动保持未提交。
- **只跑定向测试**：任何 `dotnet test` 必须带 `--filter`，禁止全量。
- **每轮代码改动后**调用 `code-reviewer` agent（Task 3 后、Task 6 后各一次，最后再终审一次）。
- **不修改** `peakcan-hil-core` 或任何其他仓库。
- 相位全部 `EvidenceKind = inferred`，**不得**产生 finding，不得写成 `measured`。
- 面向用户/业务注释用中文；技术 API/DBC/协议字段注释用英文。
- `tests/PeakCan.Host.Core.Tests/TestData/Nm/` 当前未被 git 跟踪，禁止删除或移动。
- 不静默降级：无法归属、无帧、默认时序都必须写进 `Unknowns` 或 `Detail`。
- 构建用 Debug（运行中的 App 可能锁 Release）：`dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug`。

## Review Focus

以下输入/失败模式最可能咬人，实现时每个都要有测试：

1. **超时后 NM 发送者仍发应用报文**：必须判 `Unknown`（矛盾），不得 `PrepareSleep`。
2. **清缓存尾巴 / 唤醒前应用帧**：落在 `DrainGraceMs` / `WakeupGraceMs` 内时不得触发矛盾。
3. **发送者无法归属**：不得升级结论；Detail 与 Unknowns 必须写明。
4. **未显式配置 WaitBusSleep**：不得输出 `Sleep`；必须写 `WaitBusSleepDefaulted`。
5. **未声明的 NM 报文被当成应用帧**：必须输出 Top 发送者/ID 与 `non_nm_may_include_undeclared_nm`，不得静默。
6. **首部静默 / 尾部截断**：首部不得出相位；尾部最后一段必须 `IsWindowTruncated = true`。

---

### Task 1: 数据模型与报告接线

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmSilencePhase.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmTiming.cs:43-70,150-184`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmReport.cs:203-291`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmGapAnalyzer.cs:15-19`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmAnalyzer.cs:12`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmTimingTests.cs`

**Interfaces:**
- Produces: `NmSilencePhase`, `NmSilencePhaseKind`, `NmSilenceBasis`, `NmStsEvidence`, `NmSenderActivity`
- Produces: `NmReport.SilencePhases { get; init; }`
- Produces: `NmGapResult.SilencePhases { get; init; }`, `NmGapResult.PhaseUnknowns { get; init; }`
- Produces: `NmGlobalTiming.WaitBusSleepDeclared`

- [ ] **Step 1: 写失败测试（WaitBusSleepDeclared）**

```csharp
[Fact]
public void FromProfile_WaitBusSleepExplicit_SetsDeclared()
{
    var timing = NmTimingDefaults.FromProfile(new NmTimings(WaitBusSleepMs: 2500), null);
    timing.WaitBusSleepMs.Should().Be(2500);
    timing.WaitBusSleepDeclared.Should().BeTrue();
}

[Fact]
public void FromProfile_WaitBusSleepAbsent_IsNotDeclared()
{
    var timing = NmTimingDefaults.FromProfile(null, null);
    timing.WaitBusSleepDeclared.Should().BeFalse();
}
```

- [ ] **Step 2: 跑测试确认 RED**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmTimingTests" -v minimal`
Expected: 编译失败（`WaitBusSleepDeclared` 未定义）。

- [ ] **Step 3: 加模型**

按 spec §6.1 原样添加 `NmSilencePhase.cs`。`NmSilencePhase` 的 `Senders` / `SendersTruncated` 用 init 属性。

`NmTiming.cs`：
- `NmGlobalTiming` 末尾加 `bool WaitBusSleepDeclared = false`（放在 `ImmediateCycleDeclared` 后）。
- `FromProfile` 返回值加：
  `WaitBusSleepDeclared: m1?.WaitBusSleepMs is not null || standard?.WaitBusSleepMs is not null`

`NmReport.cs` 在 `ReasonDictionaryHash` 前加：
```csharp
/// <summary>全网 NM 静默空档的相位窗口（全部 inferred）。空列表 = 未产生相位。</summary>
public IReadOnlyList<NmSilencePhase> SilencePhases { get; init; }
    = Array.Empty<NmSilencePhase>();
```

`NmGapResult` 加：
```csharp
public IReadOnlyList<NmSilencePhase> SilencePhases { get; init; } = Array.Empty<NmSilencePhase>();
public IReadOnlyList<string> PhaseUnknowns { get; init; } = Array.Empty<string>();
```

`NmAnalyzer.EngineVersion` 改 `"nm-m1-3"`。

- [ ] **Step 4: 跑测试确认 GREEN**

Run: 上一条命令
Expected: 全绿。

---

### Task 2: `NmSilencePhaseAnalyzer` 核心判据

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmSilencePhaseAnalyzer.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmSilencePhaseAnalyzerTests.cs`

**Interfaces:**
- Consumes: Task 1 的模型；`NmFrame`、`ReplayFrame`、`NmProfile`、`NmGlobalTiming`、`NmEpisode`、`DbcDocument`
- Produces:
```csharp
public sealed record NmSilencePhaseInput(
    NmEpisode PrecedingEpisode,
    double GapStart,
    double GapEnd,
    bool IsTail,
    IReadOnlyList<NmFrame> NmFrames,
    IReadOnlyList<ReplayFrame> AllFrames,
    NmProfile Profile,
    NmGlobalTiming Timing,
    double WindowEnd,
    PeakCan.HIL.Core.Dbc.DbcDocument? Dbc = null);

public sealed record NmSilencePhaseResult(
    IReadOnlyList<NmSilencePhase> Phases,
    IReadOnlyList<string> Unknowns);

public static class NmSilencePhaseAnalyzer
{
    public static NmSilencePhaseResult Analyze(NmSilencePhaseInput input);
}
```

- [ ] **Step 1: 写第一批失败测试（边界 + 基本分类）**

`NmSilencePhaseAnalyzerTests` 用 helper 构造（签名已按仓库实际类型核对）：

```csharp
private static readonly NmMessageDef NmDef = new(
    CanIdHex: "0x18FFFAF4", IsExtended: true, Name: "BMS_NWM", Sender: "BMS",
    Dlc: 8, IsNm: true, Reasons: Array.Empty<string>(), CycleMs: 500, TimeoutMs: 2000);

private static ReplayFrame AppFrame(double t, uint id, byte sa)
    => new(t, id | sa, Dlc: 8, Data: new byte[8], Flags: FrameFlags.None, IsExtended: true);

private static IReadOnlyList<ReplayFrame> FramesFrom(double from, double to, byte sa, int count)
{
    var frames = new List<ReplayFrame>(count);
    for (var i = 0; i < count; i++)
    {
        var t = from + (to - from) * i / Math.Max(count - 1, 1);
        frames.Add(AppFrame(t, 0x18FA0000u, sa));
    }
    return frames;
}

private static NmProfile Profile(
    NmMessageDef def, NmTimings? timings = null, NmBitRole[]? bitRoles = null)
    => new(NmProfileSource.Dbc, new[] { def }, timings ?? new NmTimings(),
        Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
        Array.Empty<string>(), new NmProvenance(),
        StandardBitRoles: bitRoles);

private static NmSilencePhaseResult AnalyzeDefault(
    double gapStart, double gapEnd, IReadOnlyList<ReplayFrame> appFrames,
    bool tail = false, NmGlobalTiming? timing = null)
    => AnalyzeWithDef(NmDef, gapStart, gapEnd, appFrames, tail, timing);

private static NmSilencePhaseResult AnalyzeWithDef(
    NmMessageDef def, double gapStart, double gapEnd,
    IReadOnlyList<ReplayFrame> appFrames, bool tail = false,
    NmGlobalTiming? timing = null, NmBitRole[]? bitRoles = null,
    byte[]? lastPayload = null)
{
    var payload = lastPayload ?? new byte[8];
    var nmFrame = new NmFrame(def,
        new ReplayFrame(gapStart - 0.5, 0x18FFFAF4u, 8, payload, FrameFlags.None, IsExtended: true));
    var episode = new NmEpisode(0, gapStart - 2, gapStart, "active", null,
        new[] { def.CanIdHex }, BoundaryStart: false, BoundaryEnd: false);
    var all = appFrames.Append(nmFrame.Frame).OrderBy(f => f.Timestamp).ToArray();
    return NmSilencePhaseAnalyzer.Analyze(new NmSilencePhaseInput(
        episode, gapStart, gapEnd, tail, new[] { nmFrame }, all,
        Profile(def, new NmTimings(CycleMs: 500, TimeoutMs: 2000), bitRoles),
        timing ?? NmTimingDefaults.FromProfile(new NmTimings(CycleMs: 500, TimeoutMs: 2000)),
        WindowEnd: gapEnd));
}

private static NmSilencePhaseResult AnalyzeWithNmSts(byte lastFrameByte3)
{
    var payload = new byte[8];
    payload[3] = lastFrameByte3;
    var roles = new[] { new NmBitRole(Byte: 3, Bit: 0, Name: "NmSts") };
    // 12.6–12.69 在下个 episode 起点 13 的唤醒宽限内，提供 w2 的活动证据。
    var app = FramesFrom(10.1, 11.8, sa: 0xF4, count: 10)
        .Concat(FramesFrom(12.6, 12.69, sa: 0xF4, count: 3)).ToArray();
    return AnalyzeWithDef(NmDef, 10, 13, app, lastPayload: payload, bitRoles: roles);
}
```

首批测试（完整断言）：

```csharp
[Fact]
public void Gap_WithNmSenderAppFramesInReleaseWindow_EmitsReleaseWaitStillActive()
{
    var r = AnalyzeDefault(10, 13, FramesFrom(10.1, 11.9, sa: 0xF4, count: 20));
    r.Phases.Should().ContainSingle(p => p.Kind == NmSilencePhaseKind.ReleaseWait
        && p.Basis == NmSilenceBasis.NmSenderStillActive);
    r.Phases[0].NmSenderAppFrameCount.Should().Be(20);
    r.Phases[0].EvidenceKind.Should().Be("inferred");
}

[Fact]
public void Gap_WithAppFramesStoppingAtTimeout_EmitsPrepareSleep()
{
    var app = FramesFrom(10.1, 11.8, sa: 0xF4, count: 20)
        .Concat(FramesFrom(12.6, 12.69, sa: 0xF4, count: 3)).ToArray();
    var r = AnalyzeDefault(10, 13, app);
    var prepare = r.Phases.Single(p => p.Kind == NmSilencePhaseKind.PrepareSleep);
    prepare.Start.Should().BeApproximately(12.0, 1e-9);
    prepare.End.Should().BeApproximately(13.0, 1e-9);
    prepare.NmSenderAppFrameCount.Should().Be(3, "宽限内的清缓存/唤醒帧仍计数但不阻断");
}

[Fact]
public void Gap_WithNmSenderAppFramesBeyondDrainGrace_EmitsUnknownContradiction()
{
    // gap 10–15：w2 = [12,14)，宽限外中间段 (12.5,13.5) 仍有帧 → 矛盾。
    var r = AnalyzeDefault(10, 15, FramesFrom(10.1, 13.4, sa: 0xF4, count: 40));
    r.Phases.Should().Contain(p => p.Kind == NmSilencePhaseKind.Unknown
        && p.Basis == NmSilenceBasis.AppTxAfterTimeout);
    r.Unknowns.Should().Contain("silence_phase_app_tx_after_timeout");
}

[Fact]
public void Gap_WithNoFrames_EmitsSingleUnknownNoPhases()
{
    var r = AnalyzeDefault(10, 13, Array.Empty<ReplayFrame>());
    r.Phases.Should().ContainSingle();
    r.Phases[0].Kind.Should().Be(NmSilencePhaseKind.Unknown);
    r.Phases[0].Basis.Should().Be(NmSilenceBasis.NoBusActivity);
    r.Unknowns.Should().Contain("silence_phase_no_bus_activity");
}
```

首部静默由 Task 3 的集成测试覆盖：`NmGapAnalyzer` 只在 episode 间隙与尾部调用本分析器。

- [ ] **Step 2: 跑测试确认 RED**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmSilencePhaseAnalyzerTests" -v minimal`
Expected: 编译失败（类型未定义）。

- [ ] **Step 3: 实现分析器**

按 spec §4/§5 实现，关键点：

1. 时间窗口半开区间 `[start, end)`。
2. `DrainGraceMs = timing.CycleMs`；`WakeupGraceMs = Math.Max(timing.CycleMs, timing.WakeUpMs)`。
3. 归属：
   - 先查 `Dbc` 的报文定义（`(Id & 0x1FFFFFFF, IsExtended)`）取 sender；
   - 扩展帧无 DBC sender 时用 `sa:0x{Id & 0xFF:X2}`（J1939）；
   - 其它 → null。
4. `nmSenders`：`PrecedingEpisode` 内 NM 帧的归属键集合。
5. 非 NM 帧：`AllFrames` 中不在本次 `NmFrames` 定义键集合内的帧。
6. w1：有 NM 发送者应用帧 → `ReleaseWait/NmSenderStillActive`；仅其它帧 → `ReleaseWait/OtherSenderActive`；无帧 → `Unknown/NoBusActivity`。
7. w2：在 `(w2.Start + DrainGrace, w2.End − WakeupGrace)` 内有任何帧 → `Unknown/AppTxAfterTimeout`；否则若 `w2.Start` 之后仍有帧 → `PrepareSleep/NmSenderStoppedAfterDrain`；否则 `Unknown/NoBusActivity`。
8. w3：`WaitBusSleepDeclared == false` → `Unknown/WaitBusSleepDefaulted`；w3 内有任何帧 → `Unknown/AppTxAfterTimeout`；否则若 `gapEnd` 处/之后有帧 → `Sleep/NmSenderStoppedAfterDrain`；否则 `Unknown/NoBusActivity`。
9. NMSts：只取 `PrecedingEpisode.End − TimeoutMs` 之后的 NM 报文末帧；任一可读 `Repeat` → 整个空档 `Unknown/NmStsRepeatMessage`；全部 `Ready Sleep` → `NmStsEvidence.ReadySleepAtLastFrame`。
10. 每个相位填 `Detail`（中文、含帧数/发送者/宽限/时序来源）、`Senders`（Top 8，截断标记）、`AfterEpisodeIndex = PrecedingEpisode.Index`、`IsWindowTruncated`（尾部且 End == WindowEnd）。
11. Unknowns 字符串常量：
    - `silence_phase_no_bus_activity`
    - `silence_phase_unattributed`
    - `silence_phase_nmsts_repeat`
    - `silence_phase_app_tx_after_timeout`
    - `silence_phase_wait_bus_sleep_defaulted`
    - `silence_phase_non_nm_may_include_undeclared_nm`
    - `silence_phase_sender_attribution_uses_j1939_sa`

- [ ] **Step 4: 跑测试确认 GREEN**

Run: 上一条命令
Expected: 全绿。

- [ ] **Step 5: 补齐剩余测试**

按 spec §11.1 的 1–20 条逐个实现；每条先 RED 后 GREEN。重点：

```csharp
[Fact]
public void Gap_WithOnlyQueueDrainTail_EmitsPrepareSleep()
{
    // 超时后 0.2s 内的尾巴属于清缓存宽限，不阻断 PrepareSleep。
    var r = AnalyzeDefault(10, 13, FramesFrom(12.05, 12.2, sa: 0xF4, count: 4));
    r.Phases.Should().ContainSingle(p => p.Kind == NmSilencePhaseKind.PrepareSleep);
}

[Fact]
public void Gap_WithUnattributedFrames_EmitsUnknownAndUnknownNote()
{
    // 标准帧 NM、无 DBC → nmSenders 无法归属。
    var def = NmDef with { CanIdHex = "0x500", IsExtended = false, Sender = null };
    var nmFrame = new NmFrame(def,
        new ReplayFrame(9.5, 0x500u, 8, new byte[8], FrameFlags.None, IsExtended: false));
    var app = new[]
    {
        new ReplayFrame(10.5, 0x123u, 8, new byte[8], FrameFlags.None, IsExtended: false),
        new ReplayFrame(12.5, 0x124u, 8, new byte[8], FrameFlags.None, IsExtended: false),
    };
    var episode = new NmEpisode(0, 8, 10, "active", null, new[] { "0x500" }, false, false);
    var r = NmSilencePhaseAnalyzer.Analyze(new NmSilencePhaseInput(
        episode, 10, 13, false, new[] { nmFrame }, app.Append(nmFrame.Frame).ToArray(),
        Profile(def), NmTimingDefaults.FromProfile(new NmTimings()), 13));
    r.Phases.Should().OnlyContain(p => p.Kind == NmSilencePhaseKind.Unknown);
    r.Unknowns.Should().Contain("silence_phase_unattributed");
}

[Fact]
public void Gap_WithNmStsRepeatAtLastFrame_EmitsUnknownNoPhases()
{
    var r = AnalyzeWithNmSts(lastFrameByte3: 0x00);
    r.Phases.Should().ContainSingle(p => p.Kind == NmSilencePhaseKind.Unknown
        && p.Basis == NmSilenceBasis.NmStsRepeatMessage);
    r.Phases.Should().NotContain(p => p.Kind == NmSilencePhaseKind.PrepareSleep);
    r.Unknowns.Should().Contain("silence_phase_nmsts_repeat");
}

[Fact]
public void Gap_WithNmStsReadySleep_StrengthensBasisButStillInferred()
{
    var r = AnalyzeWithNmSts(lastFrameByte3: 0x01);
    var p = r.Phases.Single(x => x.Kind == NmSilencePhaseKind.PrepareSleep);
    p.NmSts.Should().Be(NmStsEvidence.ReadySleepAtLastFrame);
    p.Detail.Should().Contain("NMSts=Ready Sleep");
    p.EvidenceKind.Should().Be("inferred");
}

[Fact]
public void Gap_WithoutNmStsRole_DoesNotMentionNmSts()
{
    var r = AnalyzeDefault(10, 13, FramesFrom(10.1, 11.8, sa: 0xF4, count: 10));
    r.Phases.Should().OnlyContain(p => !p.Detail.Contains("NMSts", StringComparison.Ordinal));
}

[Fact]
public void Gap_WithWakeupAppFramesWithinGrace_DoesNotContradict()
{
    // 12.6–12.69 在下个 episode 起点 13 的 500ms 唤醒宽限内。
    var app = FramesFrom(10.1, 11.8, sa: 0xF4, count: 10)
        .Concat(FramesFrom(12.6, 12.69, sa: 0xF4, count: 3)).ToArray();
    var r = AnalyzeDefault(10, 13, app);
    r.Phases.Should().Contain(p => p.Kind == NmSilencePhaseKind.PrepareSleep);
    r.Phases.Should().NotContain(p => p.Basis == NmSilenceBasis.AppTxAfterTimeout);
}

[Fact]
public void TailGap_LastPhase_IsWindowTruncated()
{
    var r = AnalyzeDefault(10, 14, FramesFrom(10.1, 12.5, sa: 0xF4, count: 10), tail: true);
    r.Phases.Should().Contain(p => p.IsWindowTruncated && p.End == 14);
}

[Fact]
public void SleepWindow_WithoutExplicitWaitBusSleep_EmitsUnknownDefaulted()
{
    var r = AnalyzeDefault(10, 17, FramesFrom(10.1, 11.8, sa: 0xF4, count: 10));
    r.Phases.Should().Contain(p => p.Kind == NmSilencePhaseKind.Unknown
        && p.Basis == NmSilenceBasis.WaitBusSleepDefaulted);
    r.Phases.Should().NotContain(p => p.Kind == NmSilencePhaseKind.Sleep);
    r.Unknowns.Should().Contain("silence_phase_wait_bus_sleep_defaulted");
}

[Fact]
public void SleepWindow_WithExplicitWaitBusSleepAndLiveBus_EmitsSleep()
{
    var timing = NmTimingDefaults.FromProfile(new NmTimings(WaitBusSleepMs: 2000));
    var app = FramesFrom(10.1, 11.8, sa: 0xF4, count: 10)
        .Append(AppFrame(17.0, 0x18FA0000u, 0xF4)).ToArray(); // gapEnd 处的下一 episode 帧
    var r = AnalyzeDefault(10, 17, app, tail: false, timing: timing);
    r.Phases.Should().Contain(p => p.Kind == NmSilencePhaseKind.Sleep);
}

[Fact]
public void PhaseBoundaries_UseGlobalTimingNotPerMessageOverride()
{
    // 报文级 TimeoutMs=5000 不得覆盖全局 2000。
    var def = NmDef with { TimeoutMs = 5000 };
    var app = FramesFrom(10.1, 11.8, sa: 0xF4, count: 10)
        .Concat(FramesFrom(12.6, 12.69, sa: 0xF4, count: 3)).ToArray();
    var r = AnalyzeWithDef(def, 10, 13, app);
    r.Phases.Single(p => p.Kind == NmSilencePhaseKind.PrepareSleep)
        .Start.Should().BeApproximately(12.0, 1e-9);
}

[Fact]
public void Phases_NeverProduceFindings()
{
    typeof(NmSilencePhaseResult).GetProperty("Findings").Should().BeNull();
}
```

- [ ] **Step 6: 跑全部新测试确认 GREEN**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmSilencePhaseAnalyzerTests" -v minimal`
Expected: 全部通过。

---

### Task 3: 接入 GapAnalyzer / Analyzer

**Files:**
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmGapAnalyzer.cs:3-19,42-98`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmAnalyzer.cs:157-230,420-427`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmGapAnalyzerTests.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmSilencePhaseRealTraceTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `NmSilencePhaseAnalyzer`
- Produces: `NmReport.SilencePhases` 被 `NmAnalyzer` 填充；`NmGapResult.PhaseUnknowns` 并入 `Unknowns`

- [ ] **Step 1: 写失败测试（间隙/尾部/相位接入）**

`NmGapAnalyzerTests`：

```csharp
[Fact]
public void Analyze_InterEpisodeGapWithAppFrames_ReturnsPhases()
{
    var input = GapInputWithAppFrames(gapStart: 10, gapEnd: 13, appSa: 0xF4);
    var r = NmGapAnalyzer.Analyze(input);
    r.SilencePhases.Should().Contain(p => p.Kind == NmSilencePhaseKind.ReleaseWait);
}

[Fact]
public void Analyze_TailGapWithAppFrames_ReturnsPhases()
{
    var input = GapInputWithTailAppFrames();
    NmGapAnalyzer.Analyze(input).SilencePhases.Should().NotBeEmpty();
}

[Fact]
public void Analyze_GapWithNoAppFrames_KeepsLegacyObservationAndNoPhaseUpgrade()
{
    // 仍产 all_nm_silent；Phase 最多一条 Unknown/NoBusActivity
}
```

`NmSilencePhaseRealTraceTests`（沿用 `NmReasonRealTraceTests` 的 `TestData/Nm` 加载方式）：

```csharp
private static string DataDir()
    => System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "Nm");

private static NmReport Analyze()
{
    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    var dbcBytes = File.ReadAllBytes(System.IO.Path.Combine(DataDir(), "bms.dbc"));
    var dbc = DbcParser.Parse(Encoding.GetEncoding("GBK").GetString(dbcBytes)).Value!;
    using var fs = File.OpenRead(System.IO.Path.Combine(DataDir(), "nm16.blf"));
    var frames = BlfParser.ParseAsync(fs, ReplayOptions.Default).GetAwaiter().GetResult();
    var profile = NmProfileBuilder.Build(dbc, null);
    return NmAnalyzer.Analyze(new NmAnalysisRequest(
        "real", null, frames, dbc, profile, 0, frames[^1].Timestamp + 1e-9, "full"));
}

[Fact]
public void RealTrace_Nm16_EmitsReleaseWaitAndPrepareSleep()
{
    var report = Analyze();

    var release = report.SilencePhases.Single(p => p.Kind == NmSilencePhaseKind.ReleaseWait);
    release.Start.Should().BeApproximately(9.713674, 0.001);
    // 该 DBC 的 NmAsrTimeoutTime=2000 → 11.713674。
    release.End.Should().BeApproximately(11.713674, 0.001);
    release.Basis.Should().Be(NmSilenceBasis.NmSenderStillActive);

    var prepare = report.SilencePhases.Single(p => p.Kind == NmSilencePhaseKind.PrepareSleep);
    prepare.Start.Should().BeApproximately(release.End, 0.001);
    prepare.End.Should().BeApproximately(12.677179, 0.001);
    prepare.Basis.Should().Be(NmSilenceBasis.NmSenderStoppedAfterDrain);

    report.SilencePhases.Should().OnlyContain(p => p.EvidenceKind == "inferred");
    report.Findings.Should().NotContain(f => f.Code.Contains("SILENCE_PHASE"));
}
```

- [ ] **Step 2: 跑测试确认 RED**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmGapAnalyzerTests|FullyQualifiedName~NmRealTraceContractTests" -v minimal`
Expected: 编译失败或断言失败（属性未接入）。

- [ ] **Step 3: 接入**

`NmGapAnalyzer`：
- `NmGapInput` 末尾加 `PeakCan.HIL.Core.Dbc.DbcDocument? Dbc = null`。
- 在 episode 间隙 `NM_ALL_NM_SILENT` 之后调用：
  ```csharp
  var phaseResult = NmSilencePhaseAnalyzer.Analyze(new NmSilencePhaseInput(
      PrecedingEpisode: input.Episodes[i],
      GapStart: gapStart,
      GapEnd: gapEnd,
      IsTail: false,
      NmFrames: input.NmFrames,
      AllFrames: input.AllFrames,
      Profile: input.Profile,
      Timing: globalTiming,
      WindowEnd: input.Window.End,
      Dbc: input.Dbc));
  phases.AddRange(phaseResult.Phases);
  phaseUnknowns.AddRange(phaseResult.Unknowns);
  ```
- 尾部同样调用（`PrecedingEpisode = input.Episodes[^1]`、`GapEnd = input.Window.End`、`IsTail: true`）。
- 返回 `new NmGapResult(findings, events, unknowns, observations) { SilencePhases = phases, PhaseUnknowns = phaseUnknowns }`。

`NmAnalyzer`：
- `NmGapInput` 构造传 `Dbc: request.Dbc`。
- `Finish` 的 `NmReport` 初始化器加 `SilencePhases = gapPhases`（用局部变量保存 `gap.SilencePhases`，因为 `gap` 在 `Finish` 局部作用域外；在 gap 分析后赋值给外层变量）。
- `unknowns.AddRange(gap.PhaseUnknowns)`。
- 相位非空时加 assumption：
  `"静默相位：清空发送缓存宽限 = 1 个 NM 周期，唤醒宽限 = max(周期, WakeUp)；相位均为 inferred。"`
- 未配置 WaitBusSleep 时加 assumption：
  `"未配置 WaitBusSleep，按 2000ms 处理；不推断睡眠相位。"`

- [ ] **Step 4: 跑测试确认 GREEN**

Run: 上一条命令
Expected: 全绿。

- [ ] **Step 5: code-reviewer**

调用 `code-reviewer` agent 审查 Task 1–3 的 diff。按用户规则处理 CRITICAL/HIGH/MEDIUM；LOW 记录到 ledger。

---

### Task 4: App kind / 画笔 / 图例

**Files:**
- Modify: `src/PeakCan.Host.App/Converters/NmSegmentBrushConverter.cs:15-74,87-176`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmPanelFlow.cs:167-261`
- Modify: `src/PeakCan.Host.App/Views/TraceViewerViewNmPanel.xaml`（图例 Run）
- Test: `tests/PeakCan.Host.App.Tests/Converters/NmSegmentBrushConverterTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void SilencePhaseKinds_HaveDistinctBrushes()
{
    var kinds = new[]
    {
        NmTimelineKinds.SilenceReleaseWait,
        NmTimelineKinds.SilencePrepareSleep,
        NmTimelineKinds.SilenceBusSleep,
        NmTimelineKinds.Silent,
        NmTimelineKinds.Unknown,
    };
    kinds.Select(NmSegmentBrushConverter.BrushFor).Distinct().Should().HaveCount(kinds.Length);
}

[Fact]
public void EveryEmittedKind_HasExplicitBrush()
{
    // 现有测试，更新 NmTimelineKinds.All 后仍绿；新增 kind 不得落 UnknownBrush
    NmSegmentBrushConverter.BrushFor(NmTimelineKinds.SilencePrepareSleep)
        .Should().NotBe(NmSegmentBrushConverter.BrushFor(NmTimelineKinds.Unknown));
}
```

- [ ] **Step 2: 跑测试确认 RED**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~NmSegmentBrushConverterTests" -v minimal`
Expected: 编译失败（常量未定义）。

- [ ] **Step 3: 实现**

- 常量：`silence_release_wait` / `silence_prepare_sleep` / `silence_bus_sleep`；加入 `All`。
- `BrushFor` 显式分支 + 三个 frozen brush；颜色彼此不同且不同于 Silent/Unknown。
- `NmTimelineText.SegmentKind`：`释放等待` / `准备休眠` / `总线睡眠`。
- `SegmentEvidence` 的 `"inferred"` 分支：
  - ReleaseWait → `推断（发送者仍在发应用报文）`
  - PrepareSleep → `推断（超时后总线静止 / 仅清缓存尾巴）`
  - Sleep → `推断（超时+等待后总线仍静止）`
- 图例：在现有“整网行”TextBlock 内追加 3 个 `Run`，`Foreground` 用新笔刷。**不新增 TextBlock**。

- [ ] **Step 4: 跑测试确认 GREEN**

Run: 上一条命令 + `--filter "FullyQualifiedName~NmViewLayoutTests"`（若图例断言受影响）
Expected: 全绿。

---

### Task 5: App 网络行与 hover

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmPanelFlow.cs:466-540,707-860,1960-2002`
- Modify: `src/PeakCan.Host.App/Views/TraceViewerViewNmPanel.xaml.cs:96-108`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/NmPanelFlowTests.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/NmSegmentHoverEvidenceTests.cs`

- [ ] **Step 1: 写失败测试**

先扩展测试 helper：

```csharp
private static NmReport MakeNetworkReport(
    NmEpisode[]? episodes = null, NmEvent[]? events = null,
    NmFinding[]? findings = null, NmWindow? window = null,
    NmSilencePhase[]? phases = null)
    => new(
        window ?? new NmWindow(0, 10, "full"), "src", null,
        episodes ?? Array.Empty<NmEpisode>(),
        Array.Empty<NmMessageReport>(), Array.Empty<NmNodeReport>(),
        events ?? Array.Empty<NmEvent>(), findings ?? Array.Empty<NmFinding>(),
        Array.Empty<string>(), Array.Empty<string>(),
        Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
        Array.Empty<NmTruncation>(), EngineVersion: NmAnalyzer.EngineVersion)
    { SilencePhases = phases ?? Array.Empty<NmSilencePhase>() };

private static NmSilencePhase Phase(
    double start, double end, NmSilencePhaseKind kind, NmSilenceBasis basis)
    => new(start, end, kind, basis, 0, 0, 0, 0, null,
        NmStsEvidence.NotAvailable, false, 0, "detail");
```

测试：

```csharp
[Fact]
public void BuildNetworkTimelineRow_SilencePhases_EmitThreeKinds()
{
    var report = MakeNetworkReport(
        window: new NmWindow(0, 20, "full"),
        phases: new[]
        {
            Phase(10, 12, NmSilencePhaseKind.ReleaseWait, NmSilenceBasis.NmSenderStillActive),
            Phase(12, 14, NmSilencePhaseKind.PrepareSleep, NmSilenceBasis.NmSenderStoppedAfterDrain),
            Phase(14, 16, NmSilencePhaseKind.Sleep, NmSilenceBasis.NmSenderStoppedAfterDrain),
        });
    var row = NmPanelFlow.BuildTimelineRows(report).Single(r => r.Label == "整网状态");
    row.Segments.Select(s => s.Kind).Should().Contain(new[]
    {
        NmTimelineKinds.SilenceReleaseWait,
        NmTimelineKinds.SilencePrepareSleep,
        NmTimelineKinds.SilenceBusSleep,
    });
}

[Fact]
public void BuildNetworkTimelineRow_NoPhases_KeepsLegacySilenceBand()
{
    var report = MakeNetworkReport(
        window: new NmWindow(0, 20, "full"),
        events: new[] { new NmEvent(10, "all_nm_silent", null, null, "x", 0, "measured") });
    var row = NmPanelFlow.BuildTimelineRows(report).Single(r => r.Label == "整网状态");
    row.Segments.Should().OnlyContain(s => s.Kind == NmTimelineKinds.Silence);
}

[Fact]
public void BuildNetworkTimelineRow_DoesNotMergeAcrossKinds()
{
    var report = MakeNetworkReport(
        window: new NmWindow(0, 20, "full"),
        events: new[] { new NmEvent(10, "all_nm_silent", null, null, "x", 0, "measured") },
        phases: new[]
        {
            Phase(10, 12, NmSilencePhaseKind.ReleaseWait, NmSilenceBasis.NmSenderStillActive),
        });
    var row = NmPanelFlow.BuildTimelineRows(report).Single(r => r.Label == "整网状态");
    row.Segments.Should().ContainSingle(s => s.Kind == NmTimelineKinds.SilenceReleaseWait);
    row.Segments.Should().NotContain(s => s.Kind == NmTimelineKinds.Silence
        && s.Start < 12 && s.End > 10, "相位覆盖过的区间不能再叠一条灰色带");
}

[Fact]
public void BuildNetworkTimelineRow_PartialCoverage_KeepsUnknownResidual()
{
    var report = MakeNetworkReport(
        window: new NmWindow(0, 20, "full"),
        events: new[] { new NmEvent(10, "all_nm_silent", null, null, "x", 0, "measured") },
        phases: new[]
        {
            Phase(10, 12, NmSilencePhaseKind.ReleaseWait, NmSilenceBasis.NmSenderStillActive),
        });
    var row = NmPanelFlow.BuildTimelineRows(report).Single(r => r.Label == "整网状态");
    row.Segments.Should().Contain(s => s.Kind == NmTimelineKinds.Silence && s.Start >= 12);
}

[Fact]
public void SetNmSegmentHover_PrepareSleep_ShowsDrainAndQuietEvidence()
{
    var vm = NewVm();
    vm.SetNmSegmentHover(NmTimelineKinds.SilencePrepareSleep, "整网状态",
        12, 14, "inferred", detail: "超时后仅 7 帧清缓存尾巴；0.92s 总线无帧");
    vm.NmHoverTitle.Should().Contain("准备休眠（推断）");
    vm.NmHoverHint.Should().Contain("清缓存");
    vm.NmHoverEvidence.Should().Contain("推断");
}
```

- [ ] **Step 2: 跑测试确认 RED**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~NmPanelFlowTests|FullyQualifiedName~NmSegmentHoverEvidenceTests" -v minimal`
Expected: 断言失败。

- [ ] **Step 3: 实现**

- `BuildNetworkTimelineRow`：
  1. 现有通用静默带生成、`MergeSilenceBands` 后，用新的 `SubtractRanges` 扣掉 `report.SilencePhases` 的区间。
  2. 对每个相位添加 `NmTimelineSegmentVm(ToX(start), max(ToX(end)-ToX(start),1), kind, start, end, tooltip, Evidence: "inferred")`。
  3. `Unknown` 相位用 `NmTimelineKinds.Silence`。
  4. 相位带不参与通用合并。
- `NmTimelineKinds.IsSilencePhaseKind(kind)` 帮助方法。
- `SetNmSegmentHover`：`isPhase` 时标题 = `SegmentKind(kind) + "（推断）"`；`NmHoverHint = detail + "；" + clickHint`。
- `TraceViewerViewNmPanel.xaml.cs`：传 `segment.Tooltip` 的条件改为 `IsReasonKind || IsSilencePhaseKind`。

- [ ] **Step 4: 跑测试确认 GREEN**

Run: 上一条命令
Expected: 全绿。

- [ ] **Step 5: 更新 kind 全量测试**

`BuildTimelineRows_EmitsOnlyDeclaredKinds` 的 fixture 必须包含三种相位，断言其 kind 在 `NmTimelineKinds.All` 中；更新该测试的构造数据。

---

### Task 6: 导出与 AI

**Files:**
- Modify: `src/PeakCan.Host.App/Services/Nm/NmReportWriter.cs:172-186,311-377`
- Modify: `src/PeakCan.Host.App/Services/ChatTools/GetNmOverviewTool.cs:195-210`
- Modify: `src/PeakCan.Host.App/Services/ChatTools/NmPromptRules.cs:22-26`
- Test: `tests/PeakCan.Host.App.Tests/Services/Nm/NmReportWriterTests.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/ChatTools/GetNmToolTests.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/ChatTools/NmPromptRulesTests.cs`

- [ ] **Step 1: 写失败测试**

```csharp
private static NmSilencePhase Phase()
    => new(11.714, 12.677, NmSilencePhaseKind.PrepareSleep,
        NmSilenceBasis.NmSenderStoppedAfterDrain,
        NonNmFrameCount: 10, NmSenderAppFrameCount: 7, OtherSenderFrameCount: 3,
        UnattributedFrameCount: 0, BusMaxGapSec: 0.920502,
        NmSts: NmStsEvidence.ReadySleepAtLastFrame, IsWindowTruncated: false,
        AfterEpisodeIndex: 0, Detail: "超时后仅 7 帧清缓存尾巴；0.92s 总线无帧");

[Fact]
public void Markdown_EmitsSilencePhasesSection()
{
    var md = NmReportWriter.WriteMarkdown(
        Snapshot(Report() with { SilencePhases = new[] { Phase() } }));
    md.Should().Contain("## Silence Phases").And.Contain("prepare_sleep");
}

[Fact]
public void Markdown_WithoutSilencePhases_OmitsSection()
{
    NmReportWriter.WriteMarkdown(Snapshot(Report()))
        .Should().NotContain("## Silence Phases");
}

[Fact]
public void Csv_AppendsSilencePhaseSection_FirstHeaderUnchanged()
{
    var csv = NmReportWriter.WriteCsv(Report() with { SilencePhases = new[] { Phase() } });
    csv.Split("\r\n")[0].Should().Be("time,type,subject,detail,evidence_kind,episode_index");
    csv.Should().Contain("# Silence Phases").And.Contain("prepare_sleep");
}

[Fact]
public async Task GetNmOverview_IncludesSilencePhases()
{
    var ctx = new FakeChatToolContext
    {
        NmSnapshotValue = Snapshot() with
        {
            Report = Report() with { SilencePhases = new[] { Phase() } },
        },
    };
    var json = JsonNode.Parse(await new GetNmOverviewTool(
        ctx, NullLogger<GetNmOverviewTool>.Instance).ExecuteAsync("{}", CancellationToken.None))!.AsObject();
    json["silence_phases"]!.AsArray().Should().HaveCount(1);
    json["silence_phases"]![0]!["phase"]!.GetValue<string>().Should().Be("prepare_sleep");
    json["silence_phases"]![0]!["is_inference"]!.GetValue<bool>().Should().BeTrue();
}

[Fact]
public void NmPromptRules_SilencePhasesAreInferred()
{
    var text = NmPromptRules.BuildM1();
    Assert.Contains("silence_phases", text);
    Assert.Contains("不得写成 measured", text);
    for (var i = 1; i <= 10; i++) Assert.Contains($"\n{i}. ", text);
}
```

- [ ] **Step 2: 跑测试确认 RED**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~NmReportWriterTests|FullyQualifiedName~GetNmToolTests|FullyQualifiedName~NmPromptRulesTests" -v minimal`
Expected: 失败。

- [ ] **Step 3: 实现**

- `NmReportWriter`：Markdown `## Silence Phases`（仅非空）；CSV 末尾 `# Silence Phases` 段（首行表头不变）。
- `GetNmOverviewTool`：`silence_phases` 数组，字段按 spec §8；Detail/Senders 经 `NmEventPrivacy.RedactText`。
- `NmPromptRules`：在证据分级段落追加一句，不新增编号规则。

- [ ] **Step 4: 跑测试确认 GREEN**

Run: 上一条命令
Expected: 全绿。

- [ ] **Step 5: code-reviewer**

审查 Task 4–6 的 diff；处理 CRITICAL/HIGH/MEDIUM。

---

### Task 7: 真实 trace 验收与定向回归

**Files:**
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmSilencePhaseRealTraceTests.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmRealTraceContractTests.cs`（合成基线不得回归）

- [ ] **Step 1: 运行真实 trace 测试**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmSilencePhaseRealTraceTests|FullyQualifiedName~NmRealTraceContractTests|FullyQualifiedName~NmRealTraceEndToEndTests" -v minimal`
Expected: 全绿；断言 `release_wait` 9.714–11.714、`prepare_sleep` 11.714–12.677、无相位 finding。

- [ ] **Step 2: 跑定向回归**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~Analysis.Nm" -v minimal
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~Nm" -v minimal
```
Expected: 全绿。**不许跑全量。**

- [ ] **Step 3: Debug 构建**

Run: `dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug`
Expected: 0 error。

- [ ] **Step 4: 终审**

调用 `code-reviewer` 审查整个分支 diff；记录所有 `Ruling` 与 deferred minor。

- [ ] **Step 5: 汇报**

向用户汇报：改动文件、定向测试数量、真实 trace 结果、审查结论、未提交状态。**不 commit。**

---

## 自检

- [ ] spec §5 的每个真值表行都有测试。
- [ ] 所有新 kind 在 `NmTimelineKinds.All` 中，`EveryEmittedKind_HasExplicitBrush` 绿。
- [ ] `NmReport` 现有 12 处构造点未改动（init 属性）。
- [ ] EngineVersion 已 bump，缓存测试（`NmCacheKeyTests`）绿。
- [ ] 没有新增 finding；没有 `measured` 相位。
- [ ] 首部静默不出相位；尾部截断标记正确。
- [ ] 导出/AI 字段与 spec §8 一致。
- [ ] 所有测试命令都带 `--filter`。
- [ ] 无 commit/push。
