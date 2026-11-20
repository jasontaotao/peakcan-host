# NM M1-B：确定性分析引擎（PeakCan.Host.Core）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 `PeakCan.Host.Core` 里实现纯确定性的 NM 分析引擎：从原始帧 + DBC + 用户补充输入产出 episode、逐报文周期/空档、观察突发、原始字节/位变化、DBC 信号摘要、能力门控与 `missing_inputs`，结果可缓存、可截断、不依赖 WPF。

**Architecture:** 引擎放在 `PeakCan.Host.Core/Analysis/Nm/`，不引用 App/WPF；输入是一次性快照（帧只读列表 + DBC + profile + 窗口），输出是不可变 `NmReport`。所有时间计算用秒（`ReplayFrame.Timestamp`），全部走单次遍历 + 按报文累加器。UI 线程只负责取快照与渲染，分析在 `Task.Run` 中执行。

**Tech Stack:** .NET 10、C# 12 record、xUnit、FluentAssertions。仓库 `D:\claude_proj2\peakcan-host`。

**Spec:** `D:\claude_proj2\peakcan-host\docs\superpowers\specs\2026-11-19-nm-ai-analysis-design.md`（§5、§6.6、§7、§10、§13.1–13.2、§14.1 M1-B）

## Global Constraints

- **代码算，模型说**：周期、空档、位翻转、NID 校验全部在引擎里算；引擎不做任何"猜测性结论"，推断必须带 `IsInference=true`。
- **不可知就写 unknowns**：接收端超时、总线睡眠、节点内部状态一律进 `Unknowns`。
- **只分析一个 source/channel**：`channel` 未指定且帧含多个通道时，返回 `error` 语义的报告（`Assumptions` 里写明），不混算。
- **输入快照**：`NmAnalyzer.Analyze` 不访问 registry、不读文件、不碰 UI；调用方负责快照与 `Task.Run`。
- **零逐帧对象**：不保存每帧解码结果；事件按"变化段"压缩；每报文事件上限 200，超出写入 `Truncations`。
- **只跑定向测试**：`--filter "FullyQualifiedName~Nm"`，不要裸 `dotnet test`。
- **性能目标不是 CI 断言**：AC7（100 万帧 < 2s）写成手动测量步骤，不在单测里用秒表断言（CI 机器必然 flaky）。
- **与 spec §5.1 的偏离（有意）**：`NmProfile` 在 M1 保留 `Layout` 与 `Features`（可空），因为 §6.6 能力矩阵与 AC12 要求补全后可开启 NID/CBV/PN/用户数据能力；`Standard`/`Content`/`Rules`/`Exceptions`/`Unmodeled` 等 M2 字段**不声明**，避免 M1 死代码。
- **与 spec §7.8 的偏离（有意）**：`NM_NID_MISMATCH` / `NM_NID_CHANGED` / `NM_UNKNOWN_NID` / `NM_PNI_WHILE_PN_DISABLED` / `NM_USER_DATA_UNUSED_NONZERO` 在 M1 中**只在对应输入齐备时产出**（§7.8 末句"阶段是交付批次，不是启用门"）。输入缺失时只出现在 `missing_inputs`，不产出 finding。
- **finding 只描述观测 + 方向**：`Severity ∈ {info, warn}`，`EvidenceTimes` 必须非空（文字性 missing_inputs 例外，用 Assumptions）。

## Review Focus

| # | 输入/条件 | 期望行为 | 归属任务 |
|---|---|---|---|
| 1 | 全部 NM 报文静默 2.2s（正常释放/下电） | 只给 `NM_ALL_NM_SILENT`（info），**不得**对每条报文报 `NM_NODE_DROPOUT` / `NM_SENDER_SILENT_BEYOND_TIMEOUT` | Task 8 |
| 2 | 2.2s 内所有 CAN ID 都消失 | 给 `NM_BUS_WIDE_SILENCE` + `unknowns: recorder_gap_or_bus_sleep`；不判 dropout；只有时间戳回退等可验证证据才加 `NM_LOG_GAP_SUSPECTED` | Task 8 |
| 3 | 只有 3 个有效间隔（样本 < 5） | 只报 `insufficient_samples`，不下 `NM_CYCLE_DEVIATION` | Task 7 |
| 4 | 共享 ID（多节点同 ID） | 抑制按节点的周期结论；按 payload 分组仅在 NID 输入齐备时进行 | Task 7、Task 9 |
| 5 | 报文 DLC 与 DBC 不一致 / 帧 DLC < 2 | M1 只报 `NM_DLC_MISMATCH`；`NM_FRAME_TOO_SHORT` 在 spec §7.8 标为 M2，M1 不产出；任何情况下不越界读 `Data` | Task 9、Task 11 |
| 6 | 帧 `Flags` 含 `ErrFrame` / `Rtr` | 不计入周期与空档；错误帧单列计数 | Task 5 |
| 7 | 时间戳回退 | 先排序再分析，报告 `NM_TIMESTAMP_REGRESSION` | Task 5 |
| 8 | `channel` 未指定且存在多通道 | 不混算；返回空结果 + 说明 | Task 5 |

---

## Task 1: 数据契约

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmProfile.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmReport.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmAnalysisRequest.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmContractTests.cs`

**Interfaces:**
- Produces（后续所有任务与计划 C 都依赖这些名字）:
  - `NmProfile`, `NmProfileSource`, `NmMessageDef`, `NmTimings`, `NmNodeDef`, `NmConfigConflict`, `NmProvenance`, `NmLayout`, `NmFeatures`, `NmIdMappingMode`
  - `NmReport`, `NmWindow`, `NmEpisode`, `NmMessageReport`, `NmNodeReport`, `NmEvent`, `NmFinding`, `NmCycleStats`, `NmSegment`, `NmTruncation`, `NmCapabilityState`, `NmInputMissing`, `NmCapability`
  - `NmAnalysisRequest`

- [ ] **Step 1: 写模型（先建文件，测试随后）**

创建 `src/PeakCan.Host.Core/Analysis/Nm/NmProfile.cs`：

```csharp
namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>NM profile 的字段来源。位标志，可同时来自多个来源。</summary>
[Flags]
public enum NmProfileSource
{
    None = 0,
    Dbc = 1,
    Sidecar = 2,
    Session = 4,
    Overrides = 8,
}

/// <summary>CAN ID 与节点站地址的映射方式。</summary>
public enum NmIdMappingMode
{
    Auto,
    BasePlusNode,
    LowByteSourceAddress,
    SharedId,
    Explicit,
}

/// <summary>M1 只需要下列能力；OemRules 在 M2 才会可用。</summary>
public enum NmCapability
{
    IdentifyMessages,
    Timing,
    Activity,
    RawContent,
    SignalDecode,
    NidCheck,
    CbvDecode,
    PnCheck,
    UserData,
    OemRules,
}

public sealed record NmTimings(
    int? CycleMs = null,
    int? TimeoutMs = null,
    int? RepeatMessageMs = null,
    int? WaitBusSleepMs = null,
    int? ImmediateCycleMs = null,
    int? ImmediateCount = null);

public sealed record NmLayout(
    byte? NidByte = null,
    byte? CbvByte = null,
    byte? UserDataOffset = null,
    int? UserDataLength = null);

public sealed record NmFeatures(
    bool? ActiveWakeupBitEnabled = null,
    bool? RepeatMsgIndEnabled = null,
    bool? PnEnabled = null);

public sealed record NmNodeDef(
    string Name,
    byte? StationAddress = null,
    byte? NodeIdentifier = null,
    bool IsNmNode = false);

/// <summary>DBC 内两个来源给出不同值；不静默择一。</summary>
public sealed record NmConfigConflict(string Field, string[] Values, string[] Sources);

public sealed record NmProvenance(
    string? ProfilePath = null,
    string? ProfileHash = null,
    string ResolvedFrom = "dbc");

/// <summary>一条报文（M1 的 NM 识别结果 + 周期/超时基线）。</summary>
public sealed record NmMessageDef(
    string CanIdHex,
    bool IsExtended,
    string? Name,
    string? Sender,
    byte Dlc,
    bool IsNm,
    string[] Reasons,
    int? CycleMs = null,
    int? TimeoutMs = null,
    bool MarkerAuthoritative = false);

public sealed record NmProfile(
    NmProfileSource Source,
    IReadOnlyList<NmMessageDef> Messages,
    NmTimings Timings,
    IReadOnlyList<NmNodeDef> Nodes,
    IReadOnlyList<NmConfigConflict> Conflicts,
    IReadOnlyList<string> MissingFields,
    NmProvenance Provenance,
    NmIdMappingMode? IdMapping = null,
    NmLayout? Layout = null,
    NmFeatures? Features = null);

/// <summary>用户在补全对话框里填的内容；序列化为 <c>*.nmprofile.json</c>。</summary>
public sealed record NmUserSupplement(
    IReadOnlyList<string>? NmMessageSelectors = null,
    NmIdMappingMode? IdMapping = null,
    byte? NidByte = null,
    byte? CbvByte = null,
    byte? UserDataOffset = null,
    int? UserDataLength = null,
    NmTimings? Timings = null,
    NmFeatures? Features = null,
    IReadOnlyList<NmNodeDef>? Nodes = null,
    string? DbcPath = null,
    string? DbcHash = null);
```

创建 `src/PeakCan.Host.Core/Analysis/Nm/NmReport.cs`：

```csharp
namespace PeakCan.Host.Core.Analysis.Nm;

public sealed record NmWindow(double Start, double End, string WindowRef);

public sealed record NmCycleStats(
    double? MedianMs,
    double? P95Ms,
    double? MinMs,
    double? MaxMs,
    double? PhaseMs,
    int DeviationCount,
    int IntervalCount = 0,
    int? ExpectedMs = null,
    int? ToleranceMs = null,
    int ConsecutiveDeviationCount = 0);

public sealed record NmSegment(double Start, double End, string Kind);

public sealed record NmEpisode(
    int Index,
    double Start,
    double End,
    string Kind,
    string? WakeupNid,
    IReadOnlyList<string> Participants,
    bool BoundaryStart,
    bool BoundaryEnd);

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
    bool SilentBeyondTimeout);

public sealed record NmNodeReport(
    string? Nid,
    string? CanIdHex,
    byte? StationAddress,
    double FirstSeen,
    double LastSeen,
    int FrameCount,
    NmCycleStats Cycle,
    IReadOnlyList<NmSegment> Activity,
    bool LeftEarly,
    bool SilentBeyondTimeout);

/// <summary>EvidenceKind: measured | inferred | boundary。</summary>
public sealed record NmEvent(
    double T,
    string Type,
    string? Node,
    string? CanIdHex,
    string Detail,
    int EpisodeIndex,
    string EvidenceKind);

public sealed record NmFinding(
    string Code,
    string Severity,
    string Message,
    IReadOnlyList<double> EvidenceTimes,
    bool IsInference,
    string? SuggestedNextCheck,
    string? RuleId = null,
    string? RequirementId = null,
    string? SourceRef = null);

public sealed record NmTruncation(string Section, int Returned, int Total);

public sealed record NmInputMissing(
    NmCapability Capability,
    string InputKey,
    string Why,
    string? Suggestion = null,
    string? SuggestionSource = null);

public sealed record NmCapabilityState(
    NmCapability Capability,
    bool Available,
    IReadOnlyList<NmInputMissing> Missing);

public sealed record NmReport(
    NmWindow Window,
    string SourceId,
    ushort? Channel,
    IReadOnlyList<NmEpisode> Episodes,
    IReadOnlyList<NmMessageReport> Messages,
    IReadOnlyList<NmNodeReport> Nodes,
    IReadOnlyList<NmEvent> Events,
    IReadOnlyList<NmFinding> Findings,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<NmCapabilityState> Capabilities,
    IReadOnlyList<NmInputMissing> MissingInputs,
    IReadOnlyList<NmTruncation> Truncations,
    string? StandardId = null,
    string? ProfileHash = null,
    string? EngineVersion = null);
```

创建 `src/PeakCan.Host.Core/Analysis/Nm/NmAnalysisRequest.cs`：

```csharp
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 一次分析所需的全部输入快照。调用方必须在 UI 线程取好快照（帧列表引用、
/// DBC、profile、窗口），再交给后台线程。
/// </summary>
public sealed record NmAnalysisRequest(
    string SourceId,
    ushort? Channel,
    IReadOnlyList<ReplayFrame> Frames,
    PeakCan.HIL.Core.Dbc.DbcDocument? Dbc,
    NmProfile Profile,
    double Start,
    double End,
    string WindowRef = "full");
```

- [ ] **Step 2: 写契约测试**

创建 `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmContractTests.cs`：

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmContractTests
{
    [Fact]
    public void NmProfile_M1Defaults_HaveNullLayoutAndFeatures()
    {
        var profile = new NmProfile(
            NmProfileSource.Dbc,
            Array.Empty<NmMessageDef>(),
            new NmTimings(),
            Array.Empty<NmNodeDef>(),
            Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(),
            new NmProvenance());

        Assert.Null(profile.Layout);
        Assert.Null(profile.Features);
        Assert.Equal(NmProfileSource.Dbc, profile.Source);
    }

    [Fact]
    public void NmFinding_CarriesEvidenceAndInferenceFlag()
    {
        var finding = new NmFinding(
            "NM_CYCLE_DEVIATION", "warn", "period deviates",
            new[] { 1.0, 2.0 }, IsInference: true, "check bus load");

        Assert.False(string.IsNullOrEmpty(finding.Code));
        Assert.NotEmpty(finding.EvidenceTimes);
        Assert.True(finding.IsInference);
    }
}
```

- [ ] **Step 3: 跑测试**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmContractTests"
```

Expected: PASS。

- [ ] **Step 4: 提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmContractTests.cs
git commit -m "feat(nm): add M1 analyzer data contracts"
```

---

## Task 2: 从 DBC 构建 `NmProfile`

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmProfileBuilder.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmProfileBuilderTests.cs`

**Interfaces:**
- Consumes: 计划 A 的 `DbcDocument.NmAttributes`、`Message.MsgTypeIndex` / `NmAsrMessage`、`Node.NmStationAddress`、`TryGetMessageEnumName`
- Produces: `NmProfileBuilder.Build(DbcDocument? dbc, string? dbcPath) → NmProfile`

- [ ] **Step 1: 写失败测试**

```csharp
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmProfileBuilderTests
{
    private const string DbcText = """
VERSION ""

BU_: ECU_A ECU_B

BO_ 1280 NM_Message: 8 ECU_A
 SG_ S1 : 0|8@1+ (1,0) [0|255] "" ECU_B

BO_ 512 Normal: 8 ECU_B
 SG_ S2 : 0|8@1+ (1,0) [0|255] "" ECU_A

BA_DEF_ BO_  "MsgType" ENUM  "CDD","COM","COM_LONG","COM_LONG_FC","CANNM";
BA_ "MsgType" BO_ 1280 4;
BA_ "NmAsrMessage" BO_ 1280 "Yes";
BA_ "GenMsgCycleTime" BO_ 1280 500;
BA_ "MsgType" BO_ 512 1;
BA_ "NmBaseAddress" 1280;
BA_ "NmAsrMessageCount" 256;
BA_ "NmAsrBaseAddress" 1280;
BA_ "NmAsrWaitBusSleepTime" 2000;
BA_ "NmAsrTimeoutTime" 2000;
BA_ "NmAsrRepeatMessageTime" 1600;
BA_ "NmAsrCanMsgCycleTime" 500;
BA_ "DBName" "CAN_Network";
BA_ "NmStationAddress" BU_ ECU_A 244;
BA_ "NmAsrNode" BU_ ECU_A "Yes";
""";

    [Fact]
    public void Build_MarksMessageViaEnumNameAndNmAsrMessage()
    {
        var dbc = DbcParser.Parse(DbcText).Value!;
        var profile = NmProfileBuilder.Build(dbc, @"C:\nets\demo.dbc");

        var nm = Assert.Single(profile.Messages, m => m.IsNm);
        Assert.Equal("0x500", nm.CanIdHex);
        Assert.Equal("NM_Message", nm.Name);
        Assert.Equal("ECU_A", nm.Sender);
        Assert.Equal(500, nm.CycleMs);
        Assert.True(nm.MarkerAuthoritative);
        Assert.Contains(nm.Reasons, r => r.Contains("MsgType=CANNM"));
    }

    [Fact]
    public void Build_DoesNotTreatIndexOfFourAsNmWithoutEnumDefinition()
    {
        var text = DbcText.Replace(
            "BA_DEF_ BO_  \"MsgType\" ENUM  \"CDD\",\"COM\",\"COM_LONG\",\"COM_LONG_FC\",\"CANNM\";",
            string.Empty);
        var dbc = DbcParser.Parse(text).Value!;
        var profile = NmProfileBuilder.Build(dbc, null);

        // NmAsrMessage=Yes 仍然把 0x500 标成 NM，但理由不能是 MsgType=CANNM。
        var nm = Assert.Single(profile.Messages, m => m.IsNm);
        Assert.DoesNotContain(nm.Reasons, r => r.Contains("MsgType"));
        Assert.Contains("NmAsrMessage=Yes", nm.Reasons);
    }

    [Fact]
    public void Build_EmitsConflictWhenGlobalCycleAndMessageCycleDiffer()
    {
        var dbc = DbcParser.Parse(DbcText.Replace(
            "BA_ \"GenMsgCycleTime\" BO_ 1280 500;",
            "BA_ \"GenMsgCycleTime\" BO_ 1280 640;")).Value!;
        var profile = NmProfileBuilder.Build(dbc, null);

        var conflict = Assert.Single(profile.Conflicts);
        Assert.Equal("CycleMs", conflict.Field);
        Assert.Contains("640", conflict.Values);
        Assert.Contains("500", conflict.Values);
    }

    [Fact]
    public void Build_WithoutDbc_ReturnsEmptyProfileWithMissingField()
    {
        var profile = NmProfileBuilder.Build(null, null);

        Assert.Empty(profile.Messages);
        Assert.Contains("dbc", profile.MissingFields);
    }

    [Fact]
    public void Build_CopiesNodeStationAddress()
    {
        var dbc = DbcParser.Parse(DbcText).Value!;
        var profile = NmProfileBuilder.Build(dbc, null);

        var node = Assert.Single(profile.Nodes, n => n.Name == "ECU_A");
        Assert.Equal((byte?)244, node.StationAddress);
        Assert.True(node.IsNmNode);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmProfileBuilderTests"
```

Expected: 编译失败，`NmProfileBuilder` 不存在。

- [ ] **Step 3: 实现**

```csharp
using System.Globalization;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 把 DBC 的 NM 属性翻译成 <see cref="NmProfile"/>。只陈述文件写了什么；
/// 用户补充输入的合并由 <see cref="NmSupplementMerger"/> 负责。
/// </summary>
public static class NmProfileBuilder
{
    public static NmProfile Build(DbcDocument? dbc, string? dbcPath)
    {
        if (dbc is null)
        {
            return new NmProfile(
                NmProfileSource.None,
                Array.Empty<NmMessageDef>(),
                new NmTimings(),
                Array.Empty<NmNodeDef>(),
                Array.Empty<NmConfigConflict>(),
                new[] { "dbc" },
                new NmProvenance(null, null, "none"));
        }

        var globals = dbc.NmAttributes;
        var timings = new NmTimings(
            CycleMs: globals?.CanMsgCycleTimeMs,
            TimeoutMs: globals?.TimeoutTimeMs,
            RepeatMessageMs: globals?.RepeatMessageTimeMs,
            WaitBusSleepMs: globals?.WaitBusSleepTimeMs);

        var messages = new List<NmMessageDef>(dbc.Messages.Count);
        var conflicts = new List<NmConfigConflict>();

        foreach (var message in dbc.Messages)
        {
            var reasons = new List<string>();
            var authoritative = false;

            if (message.MsgTypeIndex is { } index
                && dbc.TryGetMessageEnumName("MsgType", index, out var enumName)
                && string.Equals(enumName, "CANNM", StringComparison.Ordinal))
            {
                reasons.Add("MsgType=CANNM");
                authoritative = true;
            }

            if (message.NmAsrMessage == true)
            {
                reasons.Add("NmAsrMessage=Yes");
                authoritative = true;
            }

            var cycle = message.GenMsgCycleTimeMs is { } msgCycle ? (int)msgCycle : timings.CycleMs;
            if (reasons.Count > 0 && timings.CycleMs is { } globalCycle
                && message.GenMsgCycleTimeMs is { } messageCycle
                && messageCycle != (uint)globalCycle)
            {
                conflicts.Add(new NmConfigConflict(
                    "CycleMs",
                    new[] { globalCycle.ToString(CultureInfo.InvariantCulture),
                            messageCycle.ToString(CultureInfo.InvariantCulture) },
                    new[] { "NmAsrCanMsgCycleTime", "GenMsgCycleTime" }));
            }

            messages.Add(new NmMessageDef(
                CanIdHex: $"0x{message.Id & 0x1FFFFFFFu:X}",
                IsExtended: (message.Id & 0x80000000u) != 0,
                Name: message.Name,
                Sender: message.Sender,
                Dlc: message.Dlc,
                IsNm: reasons.Count > 0,
                Reasons: reasons.ToArray(),
                CycleMs: cycle,
                TimeoutMs: timings.TimeoutMs,
                MarkerAuthoritative: authoritative));
        }

        var nodes = dbc.Nodes
            .Select(n => new NmNodeDef(n.Name, n.NmStationAddress, n.NmAsrNodeIdentifier, n.NmAsrNode == true))
            .ToArray();

        var missing = new List<string>();
        if (dbc.Messages.All(m => m.MsgTypeIndex is null && m.NmAsrMessage is null))
            missing.Add("nm_message_selection");

        return new NmProfile(
            NmProfileSource.Dbc,
            messages,
            timings,
            nodes,
            conflicts,
            missing,
            new NmProvenance(dbcPath, null, "dbc"));
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmProfileBuilderTests"
```

Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmProfileBuilder.cs tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmProfileBuilderTests.cs
git commit -m "feat(nm): build NmProfile from DBC attributes"
```

---

## Task 3: 用户补充输入合并与 sidecar

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmSupplementMerger.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmSidecarStore.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmSupplementTests.cs`

**Interfaces:**
- Consumes: `NmProfile`（Task 2）、`NmUserSupplement`
- Produces:
  - `NmSupplementMerger.Merge(NmProfile profile, NmUserSupplement? supplement) → NmProfile`
  - `NmSidecarStore.Load(string dbcPath) → NmUserSupplement?`
  - `NmSidecarStore.Save(string dbcPath, NmUserSupplement supplement) → void`
  - `NmSidecarStore.PathFor(string dbcPath) → string`

- [ ] **Step 1: 写失败测试**

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmSupplementTests
{
    private static NmProfile EmptyProfile() => new(
        NmProfileSource.Dbc,
        Array.Empty<NmMessageDef>(),
        new NmTimings(),
        Array.Empty<NmNodeDef>(),
        Array.Empty<NmConfigConflict>(),
        Array.Empty<string>(),
        new NmProvenance());

    [Fact]
    public void Merge_UserLayoutWinsAndSourceBecomesSidecar()
    {
        var merged = NmSupplementMerger.Merge(EmptyProfile(), new NmUserSupplement(
            NidByte: 0, CbvByte: 1, UserDataOffset: 2, UserDataLength: 0));

        Assert.Equal((byte?)0, merged.Layout!.NidByte);
        Assert.Equal((byte?)1, merged.Layout.CbvByte);
        Assert.Equal((byte?)2, merged.Layout.UserDataOffset);
        Assert.Equal(0, merged.Layout.UserDataLength);
        Assert.True((merged.Source & NmProfileSource.Sidecar) != 0);
    }

    [Fact]
    public void Merge_UserMessageSelectorMarksMessageAsNm()
    {
        var baseProfile = EmptyProfile() with
        {
            Messages = new[]
            {
                new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, false, Array.Empty<string>()),
            },
        };

        var merged = NmSupplementMerger.Merge(baseProfile, new NmUserSupplement(
            NmMessageSelectors: new[] { "0x18FFFAF4" }));

        var msg = Assert.Single(merged.Messages);
        Assert.True(msg.IsNm);
        Assert.Contains("UserSelected", msg.Reasons);
        Assert.False(msg.MarkerAuthoritative);
    }

    [Fact]
    public void Merge_NullSupplement_ReturnsSameProfile()
    {
        var profile = EmptyProfile();
        var merged = NmSupplementMerger.Merge(profile, null);

        Assert.Same(profile, merged);
    }

    [Fact]
    public void Sidecar_RoundTripsNextToDbc()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nm-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbcPath = System.IO.Path.Combine(dir, "net.dbc");
        File.WriteAllText(dbcPath, "VERSION \"\"");

        var supplement = new NmUserSupplement(
            NidByte: 0, CbvByte: 1, Timings: new NmTimings(CycleMs: 500));

        NmSidecarStore.Save(dbcPath, supplement);
        var loaded = NmSidecarStore.Load(dbcPath);

        Assert.Equal(System.IO.Path.Combine(dir, "net.nmprofile.json"), NmSidecarStore.PathFor(dbcPath));
        Assert.NotNull(loaded);
        Assert.Equal((byte?)0, loaded!.NidByte);
        Assert.Equal(500, loaded.Timings!.CycleMs);
    }

    [Fact]
    public void Sidecar_MissingFile_ReturnsNull()
    {
        var dbcPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dbc");
        Assert.Null(NmSidecarStore.Load(dbcPath));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmSupplementTests"
```

Expected: 编译失败。

- [ ] **Step 3: 实现合并器**

```csharp
namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 逐字段合并用户补充输入。用户显式值优先于 DBC；空值不覆盖。
/// </summary>
public static class NmSupplementMerger
{
    public static NmProfile Merge(NmProfile profile, NmUserSupplement? supplement)
    {
        if (supplement is null) return profile;

        var source = profile.Source | NmProfileSource.Sidecar;

        var selected = supplement.NmMessageSelectors is { Count: > 0 } ids
            ? new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase)
            : null;

        var messages = profile.Messages
            .Select(m =>
            {
                if (selected is not null && selected.Contains(m.CanIdHex) && !m.IsNm)
                {
                    return m with
                    {
                        IsNm = true,
                        Reasons = m.Reasons.Append("UserSelected").ToArray(),
                        MarkerAuthoritative = false,
                    };
                }
                return m;
            })
            .ToArray();

        var layout = new NmLayout(
            NidByte: supplement.NidByte ?? profile.Layout?.NidByte,
            CbvByte: supplement.CbvByte ?? profile.Layout?.CbvByte,
            UserDataOffset: supplement.UserDataOffset ?? profile.Layout?.UserDataOffset,
            UserDataLength: supplement.UserDataLength ?? profile.Layout?.UserDataLength);

        var timings = supplement.Timings is null
            ? profile.Timings
            : new NmTimings(
                CycleMs: supplement.Timings.CycleMs ?? profile.Timings.CycleMs,
                TimeoutMs: supplement.Timings.TimeoutMs ?? profile.Timings.TimeoutMs,
                RepeatMessageMs: supplement.Timings.RepeatMessageMs ?? profile.Timings.RepeatMessageMs,
                WaitBusSleepMs: supplement.Timings.WaitBusSleepMs ?? profile.Timings.WaitBusSleepMs,
                ImmediateCycleMs: supplement.Timings.ImmediateCycleMs ?? profile.Timings.ImmediateCycleMs,
                ImmediateCount: supplement.Timings.ImmediateCount ?? profile.Timings.ImmediateCount);

        var nodes = supplement.Nodes is { Count: > 0 } ? supplement.Nodes.ToArray() : profile.Nodes;

        return profile with
        {
            Source = source,
            Messages = messages,
            Timings = timings,
            Nodes = nodes,
            Layout = layout,
            Features = supplement.Features ?? profile.Features,
            IdMapping = supplement.IdMapping ?? profile.IdMapping,
            MissingFields = Array.Empty<string>(),
        };
    }
}
```

- [ ] **Step 4: 实现 sidecar 存储**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// <c>*.nmprofile.json</c> 读写。文件与 DBC 同目录同名；写入用临时文件 + 原子替换。
/// </summary>
public static class NmSidecarStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string dbcPath)
    {
        var dir = System.IO.Path.GetDirectoryName(dbcPath) ?? string.Empty;
        var name = System.IO.Path.GetFileNameWithoutExtension(dbcPath);
        return System.IO.Path.Combine(dir, name + ".nmprofile.json");
    }

    public static NmUserSupplement? Load(string dbcPath)
    {
        var path = PathFor(dbcPath);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<NmUserSupplement>(File.ReadAllText(path), s_json);
        }
        catch (JsonException)
        {
            // 坏 sidecar 不阻断分析：当作没有补充输入。
            return null;
        }
    }

    public static void Save(string dbcPath, NmUserSupplement supplement)
    {
        var path = PathFor(dbcPath);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(supplement, s_json));
        File.Move(tmp, path, overwrite: true);
    }
}
```

- [ ] **Step 5: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmSupplementTests"
```

Expected: PASS。若 `SnakeCaseLower` 在 .NET 10 上可用（`JsonNamingPolicy.SnakeCaseLower` 自 .NET 8 起）则无需改动。

- [ ] **Step 6: 提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmSupplementMerger.cs \
        src/PeakCan.Host.Core/Analysis/Nm/NmSidecarStore.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmSupplementTests.cs
git commit -m "feat(nm): merge user supplements and persist nmprofile sidecar"
```

---

## Task 4: 能力门控与建议算法

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmCapabilityGate.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmSuggestionEngine.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmCapabilityGateTests.cs`

**Interfaces:**
- Produces:
  - `NmCapabilityGate.Evaluate(NmProfile profile, IReadOnlyList<ReplayFrame> frames, DbcDocument? dbc) → (IReadOnlyList<NmCapabilityState> Capabilities, IReadOnlyList<NmInputMissing> Missing)`
  - `NmSuggestionEngine.SuggestNidByte(...)`, `SuggestCycleMs(...)`, `SuggestTimeoutMs(...)`, `SuggestUsableMessages(...)`

- [ ] **Step 1: 写失败测试**

```csharp
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmCapabilityGateTests
{
    private static ReplayFrame Frame(double t, uint id, bool ext, params byte[] data)
        => new(t, id, (byte)data.Length, data, FrameFlags.None, ext);

    private static NmProfile Profile(bool withLayout) => new(
        NmProfileSource.Dbc,
        new[]
        {
            new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, true,
                new[] { "MsgType=CANNM" }, 500, 2000, true),
        },
        new NmTimings(CycleMs: 500, TimeoutMs: 2000),
        new[] { new NmNodeDef("BMS", 0xF4, null, true) },
        Array.Empty<NmConfigConflict>(),
        Array.Empty<string>(),
        new NmProvenance(),
        Layout: withLayout ? new NmLayout(0, 1, 2, 0) : null,
        Features: withLayout ? new NmFeatures(PnEnabled: false) : null);

    private static ReplayFrame[] Frames()
        => Enumerable.Range(0, 20)
            .Select(i => Frame(i * 0.5, 0x18FFFAF4, true, 0xF4, 0x00, 0, 0, 0, 0, 0, 0))
            .ToArray();

    [Fact]
    public void Evaluate_CompleteProfile_EnablesSemanticCapabilities()
    {
        var (caps, missing) = NmCapabilityGate.Evaluate(Profile(true), Frames(), null);

        Assert.True(caps.Single(c => c.Capability == NmCapability.IdentifyMessages).Available);
        Assert.True(caps.Single(c => c.Capability == NmCapability.Timing).Available);
        Assert.True(caps.Single(c => c.Capability == NmCapability.NidCheck).Available);
        Assert.True(caps.Single(c => c.Capability == NmCapability.CbvDecode).Available);
        Assert.True(caps.Single(c => c.Capability == NmCapability.UserData).Available);
        Assert.Empty(missing);
    }

    [Fact]
    public void Evaluate_NoLayout_ReportsMissingNidAndCbvWithSuggestions()
    {
        var (caps, missing) = NmCapabilityGate.Evaluate(Profile(false), Frames(), null);

        Assert.False(caps.Single(c => c.Capability == NmCapability.NidCheck).Available);
        var nid = Assert.Single(missing, m => m.InputKey == "nid_byte");
        Assert.Equal(NmCapability.NidCheck, nid.Capability);
        Assert.False(string.IsNullOrWhiteSpace(nid.Why));
        Assert.False(string.IsNullOrWhiteSpace(nid.Suggestion));
        Assert.Contains(missing, m => m.InputKey == "cbv_byte");
    }

    [Fact]
    public void Evaluate_NoNmMessages_ReportsIdentifyMissingAndNoGuess()
    {
        var profile = Profile(true) with
        {
            Messages = new[]
            {
                new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, false, Array.Empty<string>()),
            },
        };
        var (caps, missing) = NmCapabilityGate.Evaluate(profile, Frames(), null);

        Assert.False(caps.Single(c => c.Capability == NmCapability.IdentifyMessages).Available);
        var identify = Assert.Single(missing, m => m.InputKey == "nm_message_selection");
        Assert.Contains("NWM", identify.Suggestion);
    }

    [Fact]
    public void SuggestNidByte_FindsConstantByteEqualToIdLowByte()
    {
        var frames = Enumerable.Range(0, 10)
            .Select(i => Frame(i * 0.5, 0x18FFFAF4, true, 0xF4, 0x00, 1, 2, 3, 4, 5, 6))
            .ToArray();

        var suggestion = NmSuggestionEngine.SuggestNidByte(frames, 0x18FFFAF4, stationAddress: 0xF4);

        Assert.Equal((byte?)0, suggestion.Byte);
        Assert.Contains("0xF4", suggestion.Evidence);
    }

    [Fact]
    public void SuggestCycleMs_UsesMedianAndRequiresFiveSamples()
    {
        var frames = Enumerable.Range(0, 6)
            .Select(i => Frame(i * 0.5, 0x18FFFAF4, true, 0xF4))
            .ToArray();

        Assert.Equal(500, NmSuggestionEngine.SuggestCycleMs(frames, 0x18FFFAF4));

        var tooFew = frames.Take(4).ToArray();
        Assert.Null(NmSuggestionEngine.SuggestCycleMs(tooFew, 0x18FFFAF4));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmCapabilityGateTests"
```

Expected: 编译失败。

- [ ] **Step 3: 实现建议算法**

> **执行备注（2026-11-19）**：本步骤的示例代码有一处越界缺陷（`payloads[0].Data[b]` 在长度守卫前执行）且 `SuggestNidByte` 的"100% 恒定"判据在共享 ID 总线上恒返回 null。实际实现已按审查意见改为"逐字节众数 + 样本比例"，以提交 `4381cfe` 的代码为准；照抄下方示例会回退缺陷。

```csharp
using System.Globalization;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>确定性建议。只是建议，永不自动生效（spec §6.6.4）。</summary>
public static class NmSuggestionEngine
{
    public static (byte? Byte, string Evidence) SuggestNidByte(
        IReadOnlyList<ReplayFrame> frames, uint canId, byte? stationAddress)
    {
        var payloads = frames.Where(f => f.Id == canId && f.Data.Length >= 2).ToList();
        if (payloads.Count < 5) return (null, "样本不足 5 帧");

        for (byte b = 0; b < 8; b++)
        {
            var first = payloads[0].Data[b];
            var constant = payloads.All(f => f.Data.Length > b && f.Data[b] == first);
            if (!constant) continue;
            var lowByte = (byte)(canId & 0xFF);
            if ((stationAddress is { } sa && first == sa) || first == lowByte)
            {
                return (b, FormattableString.Invariant(
                    $"byte{b} 在 {payloads.Count} 帧中恒为 0x{first:X2}，且等于站地址或 CAN ID 低字节 0x{lowByte:X2}"));
            }
        }
        return (null, "未找到恒定且等于站地址/ID 低字节的字节");
    }

    public static int? SuggestCycleMs(IReadOnlyList<ReplayFrame> frames, uint canId)
    {
        var times = frames.Where(f => f.Id == canId).Select(f => f.Timestamp).OrderBy(t => t).ToList();
        if (times.Count < 6) return null;
        var intervals = new List<double>(times.Count - 1);
        for (int i = 1; i < times.Count; i++) intervals.Add(times[i] - times[i - 1]);
        intervals.Sort();
        return (int)Math.Round(intervals[intervals.Count / 2] * 1000.0);
    }

    public static int? SuggestTimeoutMs(int? cycleMs) => cycleMs is { } c ? c * 3 : null;

    public static IReadOnlyList<string> SuggestNmMessageCandidates(NmProfile profile)
        => profile.Messages
            .Where(m => m.Name is not null &&
                        (m.Name.Contains("NWM", StringComparison.OrdinalIgnoreCase) ||
                         m.Name.Contains("NM", StringComparison.OrdinalIgnoreCase)))
            .Select(m => $"{m.CanIdHex}({m.Name})")
            .ToArray();
}
```

- [ ] **Step 4: 实现门控**

```csharp
using System.Globalization;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 按"所需输入是否齐备"决定每项能力是否可用。缺项不是拒绝分析，而是降级 + 提示。
/// </summary>
public static class NmCapabilityGate
{
    public static (IReadOnlyList<NmCapabilityState> Capabilities, IReadOnlyList<NmInputMissing> Missing) Evaluate(
        NmProfile profile, IReadOnlyList<ReplayFrame> frames, DbcDocument? dbc)
    {
        var nmMessages = profile.Messages.Where(m => m.IsNm).ToArray();
        var missing = new List<NmInputMissing>();
        var states = new List<NmCapabilityState>();

        void Add(NmCapability capability, bool available, params NmInputMissing[] items)
        {
            states.Add(new NmCapabilityState(capability, available, items));
            missing.AddRange(items);
        }

        if (nmMessages.Length > 0)
        {
            Add(NmCapability.IdentifyMessages, true);
        }
        else
        {
            var candidates = NmSuggestionEngine.SuggestNmMessageCandidates(profile);
            var suggestion = candidates.Count > 0
                ? "按名称候选：" + string.Join(", ", candidates)
                : "从 DBC 报文清单勾选，或按名称/区间/实测周期选择";
            Add(NmCapability.IdentifyMessages, false, new NmInputMissing(
                NmCapability.IdentifyMessages, "nm_message_selection",
                "DBC 没有 MsgType=CANNM 或 NmAsrMessage=Yes 的报文",
                suggestion, "dbc"));
        }

        if (nmMessages.Any(m => m.CycleMs is not null))
        {
            Add(NmCapability.Timing, true);
        }
        else
        {
            var suggested = nmMessages.Length > 0
                ? NmSuggestionEngine.SuggestCycleMs(frames, ParseId(nmMessages[0].CanIdHex))
                : null;
            Add(NmCapability.Timing, false, new NmInputMissing(
                NmCapability.Timing, "cycle_ms",
                "DBC 没有 NM 周期属性",
                suggested is null ? "用实测间隔中位数" : $"{suggested} ms（实测中位）",
                suggested is null ? null : "measured"));
        }

        Add(NmCapability.Activity, true);
        Add(NmCapability.RawContent, true);

        var nmIds = new HashSet<uint>(nmMessages.Select(m => ParseId(m.CanIdHex)));
        var signalDecode = dbc is not null && dbc.Messages
            .Any(m => nmIds.Contains(m.Id & 0x1FFFFFFFu) && m.Signals.Count > 0);
        // SignalDecode 缺失不产生 missing_inputs 条目：spec §6.6.1 对它的建议是"—"，
        // 不是用户可补的输入。
        Add(NmCapability.SignalDecode, signalDecode);

        if (profile.Layout?.NidByte is { } nidByte)
        {
            if (nidByte >= nmMessages.Select(m => (int)m.Dlc).DefaultIfEmpty(0).Max())
            {
                Add(NmCapability.NidCheck, false, new NmInputMissing(
                    NmCapability.NidCheck, "nid_byte",
                    $"NID 字节 {nidByte} 超出 DLC", "选择小于 DLC 的字节", "user"));
            }
            else
            {
                Add(NmCapability.NidCheck, true);
            }
        }
        else
        {
            (byte? Byte, string Evidence) suggestion = nmMessages.Length > 0
                ? NmSuggestionEngine.SuggestNidByte(
                    frames, ParseId(nmMessages[0].CanIdHex),
                    profile.Nodes.FirstOrDefault(n => n.Name == nmMessages[0].Sender)?.StationAddress)
                : (null, "没有 NM 报文");
            Add(NmCapability.NidCheck, false, new NmInputMissing(
                NmCapability.NidCheck, "nid_byte",
                "未配置 NID 字节，无法校验源节点标识",
                suggestion.Byte is { } b ? $"建议 byte{b}" : suggestion.Evidence,
                suggestion.Byte is null ? null : "measured"));
        }

        if (profile.Layout?.CbvByte is { } cbvByte)
        {
            Add(NmCapability.CbvDecode, true);
        }
        else
        {
            Add(NmCapability.CbvDecode, false, new NmInputMissing(
                NmCapability.CbvDecode, "cbv_byte",
                "未配置 CBV 字节，无法解码控制位",
                "选一个取值少、位翻转明显的字节；位角色需要你确认",
                "measured"));
        }

        var pnKnown = profile.Features?.PnEnabled is not null && profile.Layout?.CbvByte is not null;
        Add(NmCapability.PnCheck, pnKnown,
            pnKnown
                ? Array.Empty<NmInputMissing>()
                : new[] { new NmInputMissing(NmCapability.PnCheck, "pn_enabled",
                    "PN 开关或 CBV 字节缺失，无法判定 PNI", "不会自动建议 PN 开关", null) });

        var userDataKnown = profile.Layout is { UserDataOffset: not null, UserDataLength: not null };
        Add(NmCapability.UserData, userDataKnown,
            userDataKnown
                ? Array.Empty<NmInputMissing>()
                : new[] { new NmInputMissing(NmCapability.UserData, "user_data_range",
                    "用户数据 offset/length 缺失", "尾部全零时建议 length 0", "measured") });

        // 企标规则属于 M2，M1 不可用，但不列为"可补输入"。
        Add(NmCapability.OemRules, false);

        return (states, missing);
    }

    private static uint ParseId(string canIdHex)
        => uint.Parse(canIdHex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
```

> 说明：`params NmInputMissing[]` 允许直接传数组（`Array.Empty<NmInputMissing>()` / `new[] { ... }`），上面的写法合法；`missing` 只收集非空项，`states` 保留每项能力自己的缺失清单。

- [ ] **Step 5: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmCapabilityGateTests"
```

Expected: PASS。

- [ ] **Step 6: 提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmCapabilityGate.cs \
        src/PeakCan.Host.Core/Analysis/Nm/NmSuggestionEngine.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmCapabilityGateTests.cs
git commit -m "feat(nm): capability gating and deterministic input suggestions"
```

---

## Task 5: 帧选择、方向字段与归一化

**Files:**
- Modify: `src/PeakCan.Host.Core/Replay/ReplayFrame.cs`（追加 `IsTransmit`）
- Modify: `src/PeakCan.Host.Core/Replay/AscFormat.cs`（记录 rx/tx、rtr）
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmFrameSelector.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmFrameSelectorTests.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Replay/AscDirectionTests.cs`

**Interfaces:**
- Produces:
  - `ReplayFrame.IsTransmit`（`bool?`，null = 未知）
  - `NmFrameSelector.Select(NmAnalysisRequest request, out NmSelectionDiagnostics diagnostics) → IReadOnlyList<ReplayFrame>`
  - `NmSelectionDiagnostics(int ErrorFrames, int RtrFrames, int DroppedFrames, int TransmitFrames, int ReceiveFrames, int DirectionUnknownFrames, bool TimestampRegression, bool MultiChannel)`

- [ ] **Step 1: 写 ASC 方向失败测试**

创建 `tests/PeakCan.Host.Core.Tests/Replay/AscDirectionTests.cs`：

```csharp
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Replay;

public class AscDirectionTests
{
    [Fact]
    public void TryParseDataLine_TxMarker_SetsIsTransmitTrue()
    {
        var ok = AscFormat.TryParseDataLine(
            "0.000000 1  18FFFAF4x       Tx   d 8  F4 00 00 00 00 00 00 00",
            out var frame, out var reason);

        Assert.True(ok, reason);
        Assert.True(frame.IsTransmit);
    }

    [Fact]
    public void TryParseDataLine_RxMarker_SetsIsTransmitFalse()
    {
        var ok = AscFormat.TryParseDataLine(
            "0.000000 1  18FFFAF4x       Rx   d 8  F4 00 00 00 00 00 00 00",
            out var frame, out _);

        Assert.True(ok);
        Assert.False(frame.IsTransmit);
    }

    [Fact]
    public void TryParseDataLine_NoDirectionMarker_LeavesNull()
    {
        var ok = AscFormat.TryParseDataLine(
            "0.000000 1  18FFFAF4x       d 8  F4 00 00 00 00 00 00 00",
            out var frame, out _);

        Assert.True(ok);
        Assert.Null(frame.IsTransmit);
    }

    [Fact]
    public void TryParseDataLine_RtrMarker_SetsRtrFlag()
    {
        var ok = AscFormat.TryParseDataLine(
            "0.000000 1  123           Rx   rtr",
            out var frame, out _);

        Assert.True(ok);
        Assert.True((frame.Flags & PeakCan.HIL.Core.FrameFlags.Rtr) != 0);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~AscDirectionTests"
```

Expected: 编译失败（`IsTransmit` 不存在）。

- [ ] **Step 3: 扩展 `ReplayFrame`**

```csharp
public sealed record ReplayFrame(
    double Timestamp,
    uint Id,
    byte Dlc,
    byte[] Data,
    FrameFlags Flags,
    bool IsExtended = false,
    ushort Channel = 0,
    bool? IsTransmit = null);
```

- [ ] **Step 4: ASC 解析填方向**

在 `TryParseDataLine` 里，`int scan = 3;` 之前加：

```csharp
bool? isTransmit = null;
```

方向/帧型预扫描循环改为（把 `rtr` 一并消费，否则后面的"DLC 解析"会把 `rtr` 当数字）：

```csharp
while (scan < tokens.Length &&
       (tokens[scan].Equals("rx", StringComparison.OrdinalIgnoreCase) ||
        tokens[scan].Equals("tx", StringComparison.OrdinalIgnoreCase) ||
        tokens[scan].Equals("fd", StringComparison.OrdinalIgnoreCase) ||
        tokens[scan].Equals("rtr", StringComparison.OrdinalIgnoreCase)))
{
    if (tokens[scan].Equals("tx", StringComparison.OrdinalIgnoreCase)) isTransmit = true;
    else if (tokens[scan].Equals("rx", StringComparison.OrdinalIgnoreCase)) isTransmit = false;
    else if (tokens[scan].Equals("rtr", StringComparison.OrdinalIgnoreCase)) flags |= FrameFlags.Rtr;
    scan++;
}
```

随后的 DLC 解析分支改为（保持原有"非法 DLC 报错"行为，只新增"token 已耗尽 = 无数据帧"）：

```csharp
if (scan + 1 < tokens.Length &&
    (tokens[scan].Equals("d", StringComparison.OrdinalIgnoreCase) ||
     tokens[scan].Equals("l", StringComparison.OrdinalIgnoreCase) ||
     tokens[scan].Equals("fd", StringComparison.OrdinalIgnoreCase)))
{
    // 原分支保持不变
}
else if (scan < tokens.Length &&
         byte.TryParse(tokens[scan], NumberStyles.Integer, CultureInfo.InvariantCulture, out dlc))
{
    dataStartIndex = scan + 1;
}
else if (scan < tokens.Length)
{
    reason = $"invalid DLC '{tokens[scan]}'";
    return false;
}
else
{
    dlc = 0;
    dataStartIndex = scan;
}
```

数据循环里的两个 case 改为：

```csharp
case "rx": isTransmit ??= false; continue;
case "tx": isTransmit ??= true; continue;
case "rtr": flags |= FrameFlags.Rtr; continue;
```

最后构造：

```csharp
frame = new ReplayFrame(ts, id, (byte)data.Count, data.ToArray(), flags, isExtended, channel, isTransmit);
```

> `rtr` 帧通常没有数据字节；`data.Count == 0` 是合法输入，`Dlc = 0` 会被下游标为 `NM_FRAME_TOO_SHORT`，但 RTR 已在选择阶段被排除。

- [ ] **Step 5: 写选择器测试**

创建 `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmFrameSelectorTests.cs`：

```csharp
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmFrameSelectorTests
{
    private static ReplayFrame F(double t, uint id, FrameFlags flags = FrameFlags.None, ushort ch = 0, bool? tx = null)
        => new(t, id, 8, new byte[8], flags, true, ch, tx);

    private static NmAnalysisRequest Request(IReadOnlyList<ReplayFrame> frames, ushort? channel = null,
        double start = 0, double end = double.MaxValue)
        => new("src", channel, frames, null, EmptyProfile(), start, end);

    private static NmProfile EmptyProfile() => new(
        NmProfileSource.None, Array.Empty<NmMessageDef>(), new NmTimings(),
        Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance());

    [Fact]
    public void Select_DropsErrorAndRtrFramesAndCountsThem()
    {
        var frames = new[]
        {
            F(0.0, 0x100),
            F(0.1, 0x100, FrameFlags.ErrFrame),
            F(0.2, 0x100, FrameFlags.Rtr),
        };

        var selected = NmFrameSelector.Select(Request(frames), out var diag);

        Assert.Single(selected);
        Assert.Equal(1, diag.ErrorFrames);
        Assert.Equal(1, diag.RtrFrames);
    }

    [Fact]
    public void Select_SortsNonMonotonicTimestampsAndReportsRegression()
    {
        var frames = new[] { F(1.0, 0x100), F(0.5, 0x100), F(2.0, 0x100) };

        var selected = NmFrameSelector.Select(Request(frames), out var diag);

        Assert.True(diag.TimestampRegression);
        Assert.Equal(new[] { 0.5, 1.0, 2.0 }, selected.Select(f => f.Timestamp));
    }

    [Fact]
    public void Select_MultiChannelWithoutExplicitChannel_ReturnsEmptyAndFlagsIt()
    {
        var frames = new[] { F(0.0, 0x100, ch: 0), F(0.1, 0x100, ch: 1) };

        var selected = NmFrameSelector.Select(Request(frames), out var diag);

        Assert.Empty(selected);
        Assert.True(diag.MultiChannel);
    }

    [Fact]
    public void Select_ExplicitChannel_FiltersToThatChannel()
    {
        var frames = new[] { F(0.0, 0x100, ch: 0), F(0.1, 0x101, ch: 1) };

        var selected = NmFrameSelector.Select(Request(frames, channel: 1), out _);

        Assert.Equal(0x101u, Assert.Single(selected).Id);
    }

    [Fact]
    public void Select_WindowIsHalfOpen()
    {
        var frames = new[] { F(0.9, 0x100), F(1.0, 0x100), F(2.0, 0x100) };

        var selected = NmFrameSelector.Select(Request(frames, start: 1.0, end: 2.0), out _);

        Assert.Single(selected);
        Assert.Equal(1.0, selected[0].Timestamp);
    }
}
```

- [ ] **Step 6: 实现选择器**

```csharp
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

public sealed record NmSelectionDiagnostics(
    int ErrorFrames,
    int RtrFrames,
    int DroppedFrames,
    int TransmitFrames,
    int ReceiveFrames,
    int DirectionUnknownFrames,
    bool TimestampRegression,
    bool MultiChannel);

/// <summary>
/// 把原始帧归一到"可分析的 NM 输入"：单通道、窗口内、时间升序、去掉错误帧与 RTR。
/// 不做 J1939 虚拟帧合并——分析只看总线上的真实帧。
/// </summary>
public static class NmFrameSelector
{
    public static IReadOnlyList<ReplayFrame> Select(
        NmAnalysisRequest request, out NmSelectionDiagnostics diagnostics)
    {
        var frames = request.Frames;

        var channels = frames.Select(f => f.Channel).Distinct().Take(2).ToArray();
        var multiChannel = request.Channel is null && channels.Length > 1;
        if (multiChannel)
        {
            diagnostics = new NmSelectionDiagnostics(0, 0, frames.Count, 0, 0, 0, false, true);
            return Array.Empty<ReplayFrame>();
        }

        var filtered = new List<ReplayFrame>(frames.Count);
        bool regression = false;
        double previous = double.NegativeInfinity;
        int errors = 0, rtr = 0, tx = 0, rx = 0, unknown = 0, dropped = 0;

        foreach (var frame in frames)
        {
            if (request.Channel is { } channel && frame.Channel != channel) { dropped++; continue; }
            if (frame.Timestamp < request.Start || frame.Timestamp >= request.End) { dropped++; continue; }
            if ((frame.Flags & FrameFlags.ErrFrame) != 0) { errors++; continue; }
            if ((frame.Flags & FrameFlags.Rtr) != 0) { rtr++; continue; }
            if (frame.Timestamp < previous) regression = true;
            previous = frame.Timestamp;
            if (frame.IsTransmit == true) tx++;
            else if (frame.IsTransmit == false) rx++;
            else unknown++;
            filtered.Add(frame);
        }

        filtered.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        diagnostics = new NmSelectionDiagnostics(
            errors, rtr, dropped, tx, rx, unknown, regression, false);
        return filtered;
    }
}
```

- [ ] **Step 7: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmFrameSelectorTests|FullyQualifiedName~AscDirectionTests"
```

Expected: 全绿；现有 `Replay` 测试不回归。

- [ ] **Step 8: 提交**

```bash
git add src/PeakCan.Host.Core/Replay/ReplayFrame.cs \
        src/PeakCan.Host.Core/Replay/AscFormat.cs \
        src/PeakCan.Host.Core/Analysis/Nm/NmFrameSelector.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmFrameSelectorTests.cs \
        tests/PeakCan.Host.Core.Tests/Replay/AscDirectionTests.cs
git commit -m "feat(nm): add frame direction, RTR flag and NM frame selection"
```

---

## Task 6: Episode 分割

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmEpisodeBuilder.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmEpisodeBuilderTests.cs`

**Interfaces:**
- Produces: `NmEpisodeBuilder.Build(IReadOnlyList<NmFrame> frames, int timeoutMs, double windowStart, double windowEnd) → IReadOnlyList<NmEpisode>`
- 本任务引入内部结构 `NmFrame(NmMessageDef Def, ReplayFrame Frame)`，由 Task 11 的组装器构造。

- [ ] **Step 1: 写失败测试**

```csharp
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmEpisodeBuilderTests
{
    private static NmFrame F(double t, string id = "0x500")
        => new(new NmMessageDef(id, false, "NM", "A", 8, true, new[] { "test" }),
               new ReplayFrame(t, 0x500, 2, new byte[] { 0, 0 }, FrameFlags.None));

    [Fact]
    public void Build_SplitsOnTimeoutSilence()
    {
        // 首帧不从 0 开始：这样 BoundaryStart 的判定才可区分（trace 开头不是活动）。
        var frames = new[] { F(0.1), F(0.6), F(1.1), F(4.0), F(4.5) };

        var episodes = NmEpisodeBuilder.Build(frames, timeoutMs: 2000, windowStart: 0, windowEnd: 5);

        Assert.Equal(2, episodes.Count);
        Assert.Equal(0.1, episodes[0].Start);
        Assert.Equal(1.1, episodes[0].End);
        Assert.Equal(4.0, episodes[1].Start);
        Assert.False(episodes[0].BoundaryStart);
    }

    [Fact]
    public void Build_MarksBoundaryWhenWindowStartsWithOrEndsInsideActivity()
    {
        var frames = new[] { F(0), F(0.5) };

        var episodes = NmEpisodeBuilder.Build(frames, 2000, 0, 0.6);

        Assert.True(Assert.Single(episodes).BoundaryStart);
        Assert.True(episodes[0].BoundaryEnd);
    }

    [Fact]
    public void Build_NoFrames_ReturnsEmpty()
    {
        Assert.Empty(NmEpisodeBuilder.Build(Array.Empty<NmFrame>(), 2000, 0, 10));
    }

    [Fact]
    public void Build_SharedIdParticipants_AreReportedByCanId()
    {
        var frames = new[] { F(0), F(0.5) };

        var episode = Assert.Single(NmEpisodeBuilder.Build(frames, 2000, 0, 1));

        Assert.Contains("0x500", episode.Participants);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmEpisodeBuilderTests"
```

Expected: 编译失败。

- [ ] **Step 3: 实现**

```csharp
namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>已识别为 NM 的一帧（报文定义 + 原始帧）。</summary>
public sealed record NmFrame(NmMessageDef Def, PeakCan.Host.Core.Replay.ReplayFrame Frame);

/// <summary>
/// 按 "全部 NM 报文静默 ≥ timeout" 切 episode。
/// M1 的 Kind 固定为 "active"；wakeup/normal/release 阶段属于推断，通过事件表达。
/// </summary>
public static class NmEpisodeBuilder
{
    public static IReadOnlyList<NmEpisode> Build(
        IReadOnlyList<NmFrame> frames, int timeoutMs, double windowStart, double windowEnd)
    {
        if (frames.Count == 0) return Array.Empty<NmEpisode>();

        var timeout = timeoutMs / 1000.0;
        var episodes = new List<NmEpisode>();
        var ordered = frames.OrderBy(f => f.Frame.Timestamp).ToArray();

        var start = ordered[0].Frame.Timestamp;
        var last = start;
        var participants = new HashSet<string>(StringComparer.Ordinal) { ordered[0].Def.CanIdHex };
        var index = 0;

        for (int i = 1; i < ordered.Length; i++)
        {
            var t = ordered[i].Frame.Timestamp;
            if (t - last >= timeout)
            {
                episodes.Add(new NmEpisode(index++, start, last, "active", null,
                    participants.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
                    BoundaryStart: start <= windowStart,
                    BoundaryEnd: false));
                start = t;
                participants.Clear();
            }
            last = t;
            participants.Add(ordered[i].Def.CanIdHex);
        }

        episodes.Add(new NmEpisode(index, start, last, "active", null,
            participants.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            BoundaryStart: start <= windowStart,
            BoundaryEnd: windowEnd - last <= timeout));
        return episodes;
    }
}
```

> 说明：`timeout` 判断用 `>=`，与 spec "静默 ≥ 阈值"一致。`BoundaryStart` 的判据是"首个 NM 帧就在窗口起点"，由调用方传入实际窗口；测试中 `windowStart=0` 且首帧 `t=0` 时命中。

- [ ] **Step 4: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmEpisodeBuilderTests"
```

Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmEpisodeBuilder.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmEpisodeBuilderTests.cs
git commit -m "feat(nm): split NM episodes on full-network silence"
```

---

## Task 7: 周期与相位统计

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmCycleAnalyzer.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmCycleAnalyzerTests.cs`

**Interfaces:**
- Produces: `NmCycleAnalyzer.Analyze(IReadOnlyList<double> timestamps, NmMessageDef def, IReadOnlyList<NmEpisode> episodes) → NmCycleStats`

- [ ] **Step 1: 写失败测试**

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmCycleAnalyzerTests
{
    private static NmMessageDef Def(int? cycle = 500, int? timeout = 2000)
        => new("0x500", false, "NM", "A", 8, true, new[] { "test" }, cycle, timeout);

    private static NmEpisode[] OneEpisode(double start, double end)
        => new[] { new NmEpisode(0, start, end, "active", null, new[] { "0x500" }, false, false) };

    [Fact]
    public void Analyze_RegularIntervals_ProducesMedianAndNoDeviation()
    {
        var times = Enumerable.Range(0, 10).Select(i => i * 0.5).ToArray();

        var stats = NmCycleAnalyzer.Analyze(times, Def(), OneEpisode(0, 4.5));

        Assert.Equal(500, stats.MedianMs);
        Assert.Equal(0, stats.DeviationCount);
        Assert.Equal(9, stats.IntervalCount);
        Assert.Equal(500, stats.ExpectedMs);
        Assert.Equal(100, stats.ToleranceMs); // max(0.2*500, 30)
    }

    [Fact]
    public void Analyze_FewerThanFiveIntervals_ReportsInsufficientSamplesOnly()
    {
        var times = new[] { 0.0, 0.5, 1.0, 1.5 };

        var stats = NmCycleAnalyzer.Analyze(times, Def(), OneEpisode(0, 1.5));

        Assert.Equal(3, stats.IntervalCount);
        Assert.Equal(0, stats.DeviationCount);
        Assert.Null(stats.MedianMs);
    }

    [Fact]
    public void Analyze_ExcludesIntervalsCrossingEpisodeBoundary()
    {
        var times = new[] { 0.0, 0.5, 1.0, 5.0, 5.5, 6.0, 6.5, 7.0, 7.5, 8.0 };
        var episodes = new[]
        {
            new NmEpisode(0, 0, 1.0, "active", null, new[] { "0x500" }, false, false),
            new NmEpisode(1, 5.0, 8.0, "active", null, new[] { "0x500" }, false, false),
        };

        var stats = NmCycleAnalyzer.Analyze(times, Def(), episodes);

        // 9 个间隔：ep1 内 2 个 + 跨边界 1 个（剔除）+ ep2 内 6 个 = 8 个有效。
        Assert.Equal(8, stats.IntervalCount);
        Assert.Equal(500, stats.MedianMs);
        Assert.Equal(0, stats.DeviationCount);
    }

    [Fact]
    public void Analyze_DeviationAboveFivePercent_ReportsCount()
    {
        // 2.0→2.7 的间隔是 0.7s，超出 500ms ± 100ms 的容差；0.6s 恰好等于容差边界，
        // 用 `>` 判据时不算偏差，所以这里必须用明显越界的值。
        var times = new[] { 0.0, 0.5, 1.0, 1.5, 2.0, 2.7, 3.2, 3.7, 4.2, 4.7 };

        var stats = NmCycleAnalyzer.Analyze(times, Def(), OneEpisode(0, 4.7));

        Assert.True(stats.DeviationCount > 0);
    }

    [Fact]
    public void Analyze_NoExpectedCycle_StillProducesMedian()
    {
        var times = Enumerable.Range(0, 10).Select(i => i * 0.5).ToArray();

        var stats = NmCycleAnalyzer.Analyze(times, Def(cycle: null), OneEpisode(0, 4.5));

        Assert.Equal(500, stats.MedianMs);
        Assert.Null(stats.ExpectedMs);
        Assert.Equal(0, stats.DeviationCount);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmCycleAnalyzerTests"
```

Expected: 编译失败。

- [ ] **Step 3: 实现**

```csharp
namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 逐报文周期统计。规则见 spec §7.4：剔除跨 episode 边界与超过超时阈值的间隔；
/// 有效样本 &lt; 5 时只报样本不足，不下偏差结论。
/// </summary>
public static class NmCycleAnalyzer
{
    private const int MinimumSamples = 5;

    public static NmCycleStats Analyze(
        IReadOnlyList<double> timestamps,
        NmMessageDef def,
        IReadOnlyList<NmEpisode> episodes)
    {
        var ordered = timestamps.OrderBy(t => t).ToArray();
        var timeoutSec = (def.TimeoutMs ?? int.MaxValue) / 1000.0;

        var intervals = new List<double>();
        for (int i = 1; i < ordered.Length; i++)
        {
            var delta = ordered[i] - ordered[i - 1];
            if (delta <= 0) continue;
            if (delta > timeoutSec) continue;
            if (CrossesEpisodeBoundary(ordered[i - 1], ordered[i], episodes)) continue;
            intervals.Add(delta);
        }

        if (intervals.Count < MinimumSamples)
        {
            return new NmCycleStats(null, null, null, null, null, 0,
                IntervalCount: intervals.Count,
                ExpectedMs: def.CycleMs,
                ToleranceMs: Tolerance(def.CycleMs));
        }

        var sorted = intervals.OrderBy(x => x).ToArray();
        var median = Percentile(sorted, 0.5);
        var p95 = Percentile(sorted, 0.95);

        int deviations = 0;
        if (def.CycleMs is { } expected)
        {
            var toleranceMs = Tolerance(expected)!.Value;
            var expectedSec = expected / 1000.0;
            var toleranceSec = toleranceMs / 1000.0;
            deviations = intervals.Count(x => Math.Abs(x - expectedSec) > toleranceSec);
        }

        var phase = episodes.Count > 0
            ? (ordered[0] - episodes[0].Start) * 1000.0
            : 0.0;

        return new NmCycleStats(
            MedianMs: median * 1000.0,
            P95Ms: p95 * 1000.0,
            MinMs: sorted[0] * 1000.0,
            MaxMs: sorted[^1] * 1000.0,
            PhaseMs: phase,
            DeviationCount: deviations,
            IntervalCount: intervals.Count,
            ExpectedMs: def.CycleMs,
            ToleranceMs: Tolerance(def.CycleMs));
    }

    private static bool CrossesEpisodeBoundary(double from, double to, IReadOnlyList<NmEpisode> episodes)
        => episodes.Any(e => from < e.Start && to >= e.Start || from <= e.End && to > e.End);

    private static int? Tolerance(int? cycleMs)
        => cycleMs is { } c ? Math.Max((int)Math.Round(c * 0.2), 30) : null;

    private static double Percentile(double[] sorted, double p)
    {
        var index = (int)Math.Round((sorted.Length - 1) * p);
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmCycleAnalyzerTests"
```

Expected: PASS。若「剔除跨边界」用例的 `IntervalCount` 与预期差 1，按真实剔除规则修正断言并在计划里备注——**不要放宽规则**。

- [ ] **Step 5: 提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmCycleAnalyzer.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmCycleAnalyzerTests.cs
git commit -m "feat(nm): per-message cycle and phase statistics"
```

---

## Task 8: 空档归因

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmGapAnalyzer.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmGapAnalyzerTests.cs`

**Interfaces:**
- Produces: `NmGapAnalyzer.Analyze(NmGapInput input) → NmGapResult(IReadOnlyList<NmFinding> Findings, IReadOnlyList<NmEvent> Events, IReadOnlyList<string> Unknowns)`

`NmGapInput`：`NmFrames`, `AllFrames`（含非 NM，用于 bus-wide 判断）, `Profile`, `Episodes`, `Window`, `Diagnostics`。

- [ ] **Step 1: 写失败测试**

```csharp
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmGapAnalyzerTests
{
    private static NmFrame Nm(double t, string id = "0x500", byte nid = 0xF4)
        => new(new NmMessageDef(id, false, "NM", "A", 8, true, new[] { "test" }, 500, 2000),
               new ReplayFrame(t, 0x500, 2, new byte[] { nid, 0 }, FrameFlags.None));

    private static ReplayFrame Other(double t) => new(t, 0x100, 8, new byte[8], FrameFlags.None);

    private static NmEpisode[] Episodes(params (double s, double e)[] spans)
        => spans.Select((s, i) => new NmEpisode(i, s.s, s.e, "active", null, new[] { "0x500" }, false, false)).ToArray();

    private static NmProfile Profile() => new(
        NmProfileSource.Dbc,
        new[] { new NmMessageDef("0x500", false, "NM", "A", 8, true, new[] { "test" }, 500, 2000) },
        new NmTimings(500, 2000),
        new[] { new NmNodeDef("A", 0xF4, null, true) },
        Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance());

    private static NmSelectionDiagnostics Diag(bool regression = false)
        => new(0, 0, 0, 0, 0, 0, regression, false);

    [Fact]
    public void Analyze_AllNmSilent_ProducesOnlyAllSilentFinding()
    {
        var nm = new[] { Nm(0), Nm(0.5), Nm(1.0), Nm(4.0) };
        var input = new NmGapInput("src", nm, nm.Select(f => f.Frame).ToArray(), Profile(),
            Episodes((0, 1.0), (4.0, 4.0)), new NmWindow(0, 5, "full"), Diag());

        var result = NmGapAnalyzer.Analyze(input);

        Assert.Contains(result.Findings, f => f.Code == "NM_ALL_NM_SILENT");
        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_NODE_DROPOUT");
        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_SENDER_SILENT_BEYOND_TIMEOUT");
    }

    [Fact]
    public void Analyze_BusWideSilence_ProducesBusWideFindingWithUnknown()
    {
        var nm = new[] { Nm(0), Nm(0.5), Nm(4.0) };
        var all = new[] { nm[0].Frame, nm[1].Frame, nm[2].Frame };
        var input = new NmGapInput("src", nm, all, Profile(),
            Episodes((0, 0.5), (4.0, 4.0)), new NmWindow(0, 5, "full"), Diag());

        var result = NmGapAnalyzer.Analyze(input);

        Assert.Contains(result.Findings, f => f.Code == "NM_BUS_WIDE_SILENCE");
        Assert.Contains(result.Unknowns, u => u.Contains("recorder_gap_or_bus_sleep"));
    }

    [Fact]
    public void Analyze_PartialDropout_ProducesNodeDropout()
    {
        // 0x500 在 1.0s 后停止，0x501 一直发到 4.0s —— 这是"单报文掉线"，
        // 不是全网静默（全网静默时不会切出第二个 episode）。
        var nm = new List<NmFrame> { Nm(0, "0x500"), Nm(0.5, "0x500"), Nm(1.0, "0x500") };
        for (int i = 0; i <= 8; i++) nm.Add(Nm(i * 0.5, "0x501"));
        var input = new NmGapInput("src", nm, nm.Select(f => f.Frame).ToArray(), Profile(),
            Episodes((0, 4.0)), new NmWindow(0, 5, "full"), Diag());

        var result = NmGapAnalyzer.Analyze(input);

        Assert.Contains(result.Findings, f => f.Code == "NM_NODE_DROPOUT");
        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_ALL_NM_SILENT");
    }

    [Fact]
    public void Analyze_SingleLongIntervalWhileOthersActive_ProducesSenderSilent()
    {
        // 0x500 中间断 3s 后又恢复：这是单个间隔超时，不是持续掉线。
        var nm = new List<NmFrame> { Nm(0, "0x500"), Nm(0.5, "0x500"), Nm(1.0, "0x500"),
                                     Nm(4.0, "0x500"), Nm(4.5, "0x500") };
        for (int i = 0; i <= 8; i++) nm.Add(Nm(i * 0.5, "0x501"));
        var input = new NmGapInput("src", nm, nm.Select(f => f.Frame).ToArray(), Profile(),
            Episodes((0, 4.5)), new NmWindow(0, 5, "full"), Diag());

        var result = NmGapAnalyzer.Analyze(input);

        Assert.Contains(result.Findings, f => f.Code == "NM_SENDER_SILENT_BEYOND_TIMEOUT");
        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_NODE_DROPOUT");
    }

    [Fact]
    public void Analyze_TimestampRegression_AddsLogGapSuspected()
    {
        var nm = new[] { Nm(0), Nm(0.5) };
        var input = new NmGapInput("src", nm, nm.Select(f => f.Frame).ToArray(), Profile(),
            Episodes((0, 0.5)), new NmWindow(0, 1, "full"), Diag(regression: true));

        var result = NmGapAnalyzer.Analyze(input);

        Assert.Contains(result.Findings, f => f.Code == "NM_TIMESTAMP_REGRESSION");
        Assert.Contains(result.Findings, f => f.Code == "NM_LOG_GAP_SUSPECTED");
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmGapAnalyzerTests"
```

Expected: 编译失败。

- [ ] **Step 3: 实现**

```csharp
namespace PeakCan.Host.Core.Analysis.Nm;

public sealed record NmGapInput(
    string SourceId,
    IReadOnlyList<NmFrame> NmFrames,
    IReadOnlyList<PeakCan.Host.Core.Replay.ReplayFrame> AllFrames,
    NmProfile Profile,
    IReadOnlyList<NmEpisode> Episodes,
    NmWindow Window,
    NmSelectionDiagnostics Diagnostics);

public sealed record NmGapResult(
    IReadOnlyList<NmFinding> Findings,
    IReadOnlyList<NmEvent> Events,
    IReadOnlyList<string> Unknowns);

/// <summary>
/// 空档归因，严格按 spec §7.5 的优先级：先网络级、后单报文。
/// 只在有可验证证据时升级为 log gap；不得把"没有 NM 帧"说成睡眠。
/// </summary>
public static class NmGapAnalyzer
{
    public static NmGapResult Analyze(NmGapInput input)
    {
        var findings = new List<NmFinding>();
        var events = new List<NmEvent>();
        var unknowns = new List<string>();

        var timeoutMs = input.Profile.Timings.TimeoutMs ?? 2000;
        var cycleMs = input.Profile.Timings.CycleMs ?? 500;
        var timeoutSec = timeoutMs / 1000.0;
        var busSilenceThreshold = Math.Max(2 * cycleMs, 500) / 1000.0;

        // 1) 每个 episode 之后到下一个 episode 之间是"全部 NM 静默"。
        for (int i = 0; i + 1 < input.Episodes.Count; i++)
        {
            var gapStart = input.Episodes[i].End;
            var gapEnd = input.Episodes[i + 1].Start;
            if (gapEnd - gapStart < timeoutSec) continue;

            findings.Add(new NmFinding(
                "NM_ALL_NM_SILENT", "info",
                $"全部 NM 报文静默 {gapEnd - gapStart:F2}s（≥ 超时 {timeoutMs}ms）",
                new[] { gapStart, gapEnd }, IsInference: false,
                "正常释放/下电/记录停止；不对单个报文重复报 dropout"));
            events.Add(new NmEvent(gapStart, "all_nm_silent", null, null,
                $"NM silent until {gapEnd:F3}s", i, "measured"));
        }

        // 2) 单报文静默但其它 NM 仍在发送：区分"持续掉线"与"单个间隔超时"。
        //    - 持续掉线：该报文到 episode 结束前再没出现 → NM_NODE_DROPOUT
        //    - 单间隔超时：中间断了又恢复 → NM_SENDER_SILENT_BEYOND_TIMEOUT
        var perMessage = input.NmFrames.GroupBy(f => f.Def.CanIdHex, StringComparer.Ordinal).ToArray();
        if (perMessage.Length > 1)
        {
            foreach (var group in perMessage)
            {
                var times = group.Select(f => f.Frame.Timestamp).OrderBy(t => t).ToArray();
                var lastEpisode = input.Episodes.Count > 0 ? input.Episodes[^1] : null;

                if (lastEpisode is not null
                    && lastEpisode.End - times[^1] > timeoutSec
                    && OthersActiveBetween(times[^1], lastEpisode.End, group.Key, input.NmFrames))
                {
                    findings.Add(new NmFinding(
                        "NM_NODE_DROPOUT", "warn",
                        $"{group.Key} 从 {times[^1]:F2}s 起未再发送（episode 结束 {lastEpisode.End:F2}s），其它 NM 报文仍在发送",
                        new[] { times[^1], lastEpisode.End }, IsInference: true,
                        "检查该发送节点的复位/掉线/发送停止"));
                    events.Add(new NmEvent(times[^1], "sender_silent", null, group.Key,
                        $"{lastEpisode.End - times[^1]:F2}s", lastEpisode.Index, "inferred"));
                }

                for (int i = 1; i < times.Length; i++)
                {
                    var gap = times[i] - times[i - 1];
                    if (gap <= timeoutSec) continue;
                    if (IsInsideAllNmSilent(times[i - 1], times[i], input.Episodes)) continue;
                    if (!OthersActiveBetween(times[i - 1], times[i], group.Key, input.NmFrames)) continue;

                    findings.Add(new NmFinding(
                        "NM_SENDER_SILENT_BEYOND_TIMEOUT", "warn",
                        $"{group.Key} 静默 {gap:F2}s（> 超时 {timeoutMs}ms），其它 NM 报文仍在发送",
                        new[] { times[i - 1], times[i] }, IsInference: true,
                        "检查发送端是否停止发送/掉线"));
                    events.Add(new NmEvent(times[i - 1], "sender_silent", null, group.Key,
                        $"{gap:F2}s", EpisodeIndexOf(times[i - 1], input.Episodes), "inferred"));
                }
            }
        }

        // 3) 整条总线无任何帧 → bus-wide silence。
        var allTimes = input.AllFrames.Select(f => f.Timestamp).OrderBy(t => t).ToArray();
        for (int i = 1; i < allTimes.Length; i++)
        {
            var gap = allTimes[i] - allTimes[i - 1];
            if (gap < busSilenceThreshold) continue;
            findings.Add(new NmFinding(
                "NM_BUS_WIDE_SILENCE", "warn",
                $"全网无帧 {gap:F2}s（阈值 {busSilenceThreshold * 1000:F0}ms）",
                new[] { allTimes[i - 1], allTimes[i] }, IsInference: false,
                "确认记录仪是否暂停/下电；不要据此判断节点掉线"));
            events.Add(new NmEvent(allTimes[i - 1], "bus_wide_silence", null, null,
                $"{gap:F2}s", 0, "measured"));
            unknowns.Add("recorder_gap_or_bus_sleep");
        }

        // 4) 只有可验证证据才报 log gap。
        if (input.Diagnostics.TimestampRegression)
        {
            findings.Add(new NmFinding(
                "NM_TIMESTAMP_REGRESSION", "warn",
                "时间戳非单调，已排序后分析",
                new[] { input.Window.Start }, IsInference: false,
                "检查记录文件完整性"));
            findings.Add(new NmFinding(
                "NM_LOG_GAP_SUSPECTED", "warn",
                "存在时间戳回退，可能存在记录仪丢帧",
                new[] { input.Window.Start }, IsInference: true,
                "对比记录仪日志/其它日志源"));
        }

        return new NmGapResult(findings, events, unknowns.Distinct().ToArray());
    }

    /// <summary>区间是否落在两个 episode 之间的"全部 NM 静默"里。</summary>
    private static bool IsInsideAllNmSilent(double from, double to, IReadOnlyList<NmEpisode> episodes)
    {
        for (int i = 0; i + 1 < episodes.Count; i++)
        {
            if (from >= episodes[i].End && to <= episodes[i + 1].Start) return true;
        }
        return false;
    }

    /// <summary>区间内有其它 NM 报文在发送（否则是全网静默，不是单报文掉线）。</summary>
    private static bool OthersActiveBetween(
        double from, double to, string excludeCanId, IReadOnlyList<NmFrame> all)
        => all.Any(f => !string.Equals(f.Def.CanIdHex, excludeCanId, StringComparison.Ordinal)
                        && f.Frame.Timestamp > from && f.Frame.Timestamp <= to);

    private static int EpisodeIndexOf(double t, IReadOnlyList<NmEpisode> episodes)
    {
        for (int i = 0; i < episodes.Count; i++)
            if (t >= episodes[i].Start && t <= episodes[i].End) return i;
        return episodes.Count > 0 ? episodes.Count - 1 : 0;
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmGapAnalyzerTests"
```

Expected: PASS。`IsInsideAllNmSilent` 必须按"跨两个相邻 episode 的静默区间"判断（单个 episode 内部取 `from >= e.End && to <= e.Start` 恒为 false，是死逻辑）。**不要删掉"全 NM 静默优先"规则**。

- [ ] **Step 5: 提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmGapAnalyzer.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmGapAnalyzerTests.cs
git commit -m "feat(nm): gap attribution with network-first priority"
```

---

## Task 9: 内容分析（观察突发、原始字节/位、NID/CBV/PN/用户数据）

> **执行备注（2026-11-19）**：下面的示例代码有三处与最终实现不同，以提交 `f160cce` 及其后续修复为准：(1) 字节/位事件改为按 `(byte)` / `(byte, bit)` 聚合的"变化段"，不是逐变化上报；(2) 共享 ID（组内 distinct NID > 1）抑制 `NM_NID_CHANGED`、只按 NID 值报 `NM_UNKNOWN_NID`——因此 **M1 不产出 `NM_NID_CHANGED`**，M2 引入节点身份后恢复；(3) Truncation 只在真正丢弃事件时记录，`Total` 为尝试数。

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmContentAnalyzer.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmContentAnalyzerTests.cs`

**Interfaces:**
- Produces: `NmContentAnalyzer.Analyze(NmContentInput input) → NmContentResult(IReadOnlyList<NmEvent> Events, IReadOnlyList<NmFinding> Findings, IReadOnlyList<NmTruncation> Truncations)`

- [ ] **Step 1: 写失败测试**

```csharp
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmContentAnalyzerTests
{
    private static NmFrame Nm(double t, byte[] data, string id = "0x500")
        => new(new NmMessageDef(id, false, "NM", "A", 8, true, new[] { "test" }, 500, 2000),
               new ReplayFrame(t, 0x500, (byte)data.Length, data, FrameFlags.None));

    private static NmProfile Profile(NmLayout? layout = null, NmFeatures? features = null) => new(
        NmProfileSource.Dbc,
        new[] { new NmMessageDef("0x500", false, "NM", "A", 8, true, new[] { "test" }, 500, 2000) },
        new NmTimings(500, 2000),
        new[] { new NmNodeDef("A", 0xF4, null, true) },
        Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance(),
        Layout: layout, Features: features);

    [Fact]
    public void Analyze_BurstAfterSilence_EmitsObservedBurstEvent()
    {
        var frames = new[]
        {
            Nm(0, new byte[] { 0xF4, 0 }, "0x500"),
            Nm(1.0, new byte[] { 0xF4, 0 }, "0x500"),
            Nm(1.05, new byte[] { 0xF4, 0 }, "0x500"),
            Nm(1.10, new byte[] { 0xF4, 0 }, "0x500"),
        };

        var result = NmContentAnalyzer.Analyze(new NmContentInput(frames, Profile(), 500));

        var burst = Assert.Single(result.Events, e => e.Type == "burst_observed");
        Assert.Contains("3", burst.Detail);
        // 只是事件，不是 finding：不判是否符合 5×50ms。
        Assert.DoesNotContain(result.Findings, f => f.Code.StartsWith("NM_WAKEUP_BURST"));
    }

    [Fact]
    public void Analyze_ByteChange_EmitsCompressedEvent()
    {
        var frames = new[]
        {
            Nm(0.0, new byte[] { 0xF4, 0x00 }),
            Nm(0.5, new byte[] { 0xF4, 0x08 }),
            Nm(1.0, new byte[] { 0xF4, 0x08 }),
            Nm(1.5, new byte[] { 0x27, 0x08 }),
        };

        var result = NmContentAnalyzer.Analyze(new NmContentInput(frames, Profile(), 500));

        Assert.Contains(result.Events, e => e.Type == "byte_change" && e.Detail.Contains("byte0"));
        Assert.Contains(result.Events, e => e.Type == "bit_change" && e.Detail.Contains("byte1.bit3"));
    }

    [Fact]
    public void Analyze_NidByteConfigured_ChecksAgainstStationAddress()
    {
        var frames = new[]
        {
            Nm(0.0, new byte[] { 0x27, 0x00 }),
            Nm(0.5, new byte[] { 0x27, 0x00 }),
        };

        var result = NmContentAnalyzer.Analyze(new NmContentInput(
            frames, Profile(new NmLayout(NidByte: 0, CbvByte: 1)), 500));

        Assert.Contains(result.Findings, f => f.Code == "NM_NID_MISMATCH");
    }

    [Fact]
    public void Analyze_PnDisabledButPniSet_ProducesFinding()
    {
        var frames = new[]
        {
            Nm(0.0, new byte[] { 0xF4, 0x10 }),
            Nm(0.5, new byte[] { 0xF4, 0x10 }),
        };

        var result = NmContentAnalyzer.Analyze(new NmContentInput(
            frames, Profile(new NmLayout(NidByte: 0, CbvByte: 1), new NmFeatures(PnEnabled: false)), 500));

        Assert.Contains(result.Findings, f => f.Code == "NM_PNI_WHILE_PN_DISABLED");
    }

    [Fact]
    public void Analyze_UserDataLengthZeroButNonZeroTail_ProducesFinding()
    {
        var frames = new[]
        {
            Nm(0.0, new byte[] { 0xF4, 0x00, 0xAA, 0, 0, 0, 0, 0 }),
            Nm(0.5, new byte[] { 0xF4, 0x00, 0xAA, 0, 0, 0, 0, 0 }),
        };

        var result = NmContentAnalyzer.Analyze(new NmContentInput(
            frames, Profile(new NmLayout(0, 1, 2, 0)), 500));

        Assert.Contains(result.Findings, f => f.Code == "NM_USER_DATA_UNUSED_NONZERO");
    }

    [Fact]
    public void Analyze_WithoutLayout_EmitsNoNidOrCbvFindings()
    {
        var frames = new[]
        {
            Nm(0.0, new byte[] { 0x27, 0xFF }),
            Nm(0.5, new byte[] { 0x27, 0xFF }),
        };

        var result = NmContentAnalyzer.Analyze(new NmContentInput(frames, Profile(), 500));

        Assert.DoesNotContain(result.Findings, f => f.Code.StartsWith("NM_NID"));
        Assert.DoesNotContain(result.Findings, f => f.Code == "NM_PNI_WHILE_PN_DISABLED");
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmContentAnalyzerTests"
```

Expected: 编译失败。

- [ ] **Step 3: 实现**

```csharp
namespace PeakCan.Host.Core.Analysis.Nm;

public sealed record NmContentInput(
    IReadOnlyList<NmFrame> Frames,
    NmProfile Profile,
    int CycleMs);

public sealed record NmContentResult(
    IReadOnlyList<NmEvent> Events,
    IReadOnlyList<NmFinding> Findings,
    IReadOnlyList<NmTruncation> Truncations);

/// <summary>
/// 内容级观测：突发、字节/位变化、NID/CBV/PN/用户数据。所有"语义"判断只在
/// profile 明确给出对应输入时才做。
/// </summary>
public static class NmContentAnalyzer
{
    private const int MaxEventsPerMessage = 200;

    public static NmContentResult Analyze(NmContentInput input)
    {
        var events = new List<NmEvent>();
        var findings = new List<NmFinding>();
        var truncations = new List<NmTruncation>();

        foreach (var group in input.Frames.GroupBy(f => f.Def.CanIdHex, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(f => f.Frame.Timestamp).ToArray();
            var produced = 0;

            void Emit(NmEvent e)
            {
                if (produced >= MaxEventsPerMessage) return;
                events.Add(e);
                produced++;
            }

            DetectBurst(ordered, input.CycleMs, Emit);
            DetectByteAndBitChanges(ordered, group.Key, Emit);

            if (produced >= MaxEventsPerMessage)
                truncations.Add(new NmTruncation("events:" + group.Key, produced, ordered.Length));

            CheckNid(ordered, input, findings);
            CheckCbv(ordered, input, findings);
            CheckUserData(ordered, input, findings);
        }

        return new NmContentResult(events, findings, truncations);
    }

    private static void DetectBurst(NmFrame[] ordered, int cycleMs, Action<NmEvent> emit)
    {
        var cycleSec = cycleMs / 1000.0;
        for (int i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].Frame.Timestamp - ordered[i - 1].Frame.Timestamp < cycleSec) continue;

            // 静默后的短间隔序列：连续 ≥2 个 < 0.5×cycle 的间隔。
            var burstStart = i;
            int count = 0;
            for (int j = i; j + 1 < ordered.Length; j++)
            {
                var delta = ordered[j + 1].Frame.Timestamp - ordered[j].Frame.Timestamp;
                if (delta >= 0.5 * cycleSec) break;
                count++;
            }
            if (count >= 2)
            {
                emit(new NmEvent(ordered[burstStart].Frame.Timestamp, "burst_observed",
                    ordered[burstStart].Def.Sender, ordered[burstStart].Def.CanIdHex,
                    $"{count + 1} 帧短间隔（相邻 < {0.5 * cycleMs:F0}ms）", 0, "measured"));
            }
        }
    }

    private static void DetectByteAndBitChanges(NmFrame[] ordered, string canId, Action<NmEvent> emit)
    {
        if (ordered.Length < 2) return;
        var previous = ordered[0].Frame.Data;
        for (int i = 1; i < ordered.Length; i++)
        {
            var current = ordered[i].Frame.Data;
            for (int b = 0; b < Math.Min(previous.Length, current.Length); b++)
            {
                if (previous[b] == current[b]) continue;
                var xor = previous[b] ^ current[b];
                emit(new NmEvent(ordered[i].Frame.Timestamp, "byte_change", null, canId,
                    $"byte{b}: 0x{previous[b]:X2} → 0x{current[b]:X2}", 0, "measured"));
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((xor & (1 << bit)) == 0) continue;
                    emit(new NmEvent(ordered[i].Frame.Timestamp, "bit_change", null, canId,
                        $"byte{b}.bit{bit}: {(previous[b] & (1 << bit)) != 0 ? 1 : 0} → {(current[b] & (1 << bit)) != 0 ? 1 : 0}",
                        0, "measured"));
                }
            }
            previous = current;
        }
    }

    private static void CheckNid(NmFrame[] ordered, NmContentInput input, List<NmFinding> findings)
    {
        if (input.Profile.Layout?.NidByte is not { } nidByte) return;

        var frames = ordered.Where(f => f.Frame.Data.Length > nidByte).ToArray();
        if (frames.Length == 0) return;

        var expected = input.Profile.Nodes
            .FirstOrDefault(n => n.Name == ordered[0].Def.Sender)?.StationAddress;
        var values = frames.Select(f => f.Frame.Data[nidByte]).Distinct().ToArray();

        if (values.Length > 1)
        {
            findings.Add(new NmFinding("NM_NID_CHANGED", "warn",
                $"同一发送者的 NID 在窗口内变化：{string.Join(",", values.Select(v => $"0x{v:X2}"))}",
                frames.Select(f => f.Frame.Timestamp).Take(5).ToArray(), true,
                "检查发送节点配置/复位"));
        }

        if (expected is { } sa && values.Length == 1 && values[0] != sa)
        {
            findings.Add(new NmFinding("NM_NID_MISMATCH", "warn",
                $"NID 0x{values[0]:X2} ≠ 站地址 0x{sa:X2}",
                new[] { frames[0].Frame.Timestamp }, false,
                "核对 DBC 站地址与发送内容"));
        }

        if (values.Length == 1 && input.Profile.Nodes.Count > 0 &&
            !input.Profile.Nodes.Any(n => n.StationAddress == values[0]))
        {
            findings.Add(new NmFinding("NM_UNKNOWN_NID", "info",
                $"NID 0x{values[0]:X2} 不在 DBC 节点表中",
                new[] { frames[0].Frame.Timestamp }, true,
                "确认矩阵是否遗漏该节点"));
        }
    }

    private static void CheckCbv(NmFrame[] ordered, NmContentInput input, List<NmFinding> findings)
    {
        if (input.Profile.Layout?.CbvByte is not { } cbvByte) return;
        if (input.Profile.Features?.PnEnabled != false) return;

        // AUTOSAR CanNm 的 PNI 位于 CBV bit4；只在用户明确配置 CBV 字节时解释。
        var hit = ordered.FirstOrDefault(f => f.Frame.Data.Length > cbvByte &&
                                              (f.Frame.Data[cbvByte] & 0x10) != 0);
        if (hit is not null)
        {
            findings.Add(new NmFinding("NM_PNI_WHILE_PN_DISABLED", "warn",
                "PN 关闭但 CBV PNI 位置 1", new[] { hit.Frame.Timestamp }, false,
                "核对 CanNmPnEnabled 与实际发送内容"));
        }
    }

    private static void CheckUserData(NmFrame[] ordered, NmContentInput input, List<NmFinding> findings)
    {
        var layout = input.Profile.Layout;
        if (layout?.UserDataOffset is not { } offset || layout.UserDataLength != 0) return;

        var hit = ordered.FirstOrDefault(f => f.Frame.Data.Skip(offset).Any(b => b != 0));
        if (hit is not null)
        {
            findings.Add(new NmFinding("NM_USER_DATA_UNUSED_NONZERO", "warn",
                $"用户数据长度为 0，但 byte{offset} 之后存在非零字节",
                new[] { hit.Frame.Timestamp }, false,
                "核对用户数据长度配置"));
        }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmContentAnalyzerTests"
```

Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmContentAnalyzer.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmContentAnalyzerTests.cs
git commit -m "feat(nm): burst, byte/bit change and gated NID/CBV/user-data checks"
```

---

## Task 10: DBC 信号摘要

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmSignalSummary.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmSignalSummaryTests.cs`

**Interfaces:**
- Produces: `NmSignalSummary.Build(DbcDocument dbc, IReadOnlyList<NmFrame> frames) → IReadOnlyList<NmEvent>`

- [ ] **Step 1: 写失败测试**

```csharp
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmSignalSummaryTests
{
    [Fact]
    public void Build_DecodedSignalChanges_EmitSignalEvent()
    {
        var dbc = DbcParser.Parse("""
VERSION ""

BO_ 1280 NM_Message: 8 ECU_A
 SG_ WakeReason : 0|8@1+ (1,0) [0|255] "" ECU_B
""").Value!;

        var frames = new[]
        {
            new NmFrame(new NmMessageDef("0x500", false, "NM_Message", "ECU_A", 8, true, new[] { "test" }),
                new ReplayFrame(0.0, 0x500, 8, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None)),
            new NmFrame(new NmMessageDef("0x500", false, "NM_Message", "ECU_A", 8, true, new[] { "test" }),
                new ReplayFrame(0.5, 0x500, 8, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None)),
        };

        var events = NmSignalSummary.Build(dbc, frames);

        var e = Assert.Single(events, x => x.Type == "signal_change");
        Assert.Contains("WakeReason", e.Detail);
        Assert.Contains("0", e.Detail);
        Assert.Contains("2", e.Detail);
    }

    [Fact]
    public void Build_NoSignalsOnMessage_ReturnsEmpty()
    {
        var dbc = DbcParser.Parse("""
VERSION ""

BO_ 1280 NM_Message: 8 ECU_A
""").Value!;
        var frames = Array.Empty<NmFrame>();

        Assert.Empty(NmSignalSummary.Build(dbc, frames));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmSignalSummaryTests"
```

Expected: 编译失败。

- [ ] **Step 3: 实现**

```csharp
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 复用 hil-core 的 <see cref="SignalDecoder"/> 解码 NM 报文上的 DBC 信号，
/// 只在值变化时产出事件。不解码时返回空，不报错。
/// </summary>
public static class NmSignalSummary
{
    public static IReadOnlyList<NmEvent> Build(DbcDocument dbc, IReadOnlyList<NmFrame> frames)
    {
        var events = new List<NmEvent>();
        foreach (var group in frames.GroupBy(f => f.Def.CanIdHex, StringComparer.Ordinal))
        {
            var id = uint.Parse(group.Key.AsSpan(2), System.Globalization.NumberStyles.HexNumber);
            if (!dbc.MessagesById.TryGetValue(id, out var message) || message.Signals.Count == 0)
                continue;

            var previous = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var frame in group.OrderBy(f => f.Frame.Timestamp))
            {
                foreach (var signal in message.Signals)
                {
                    // hil-core 的 API 是 Decode(data, signal) → double（无 TryDecode）。
                    var value = SignalDecoder.Decode(frame.Frame.Data, signal);
                    if (previous.TryGetValue(signal.Name, out var old))
                    {
                        if (Math.Abs(old - value) < 1e-9) continue;
                        events.Add(new NmEvent(frame.Frame.Timestamp, "signal_change", null, group.Key,
                            $"{signal.Name}: {old:F0} → {value:F0}", 0, "measured"));
                    }
                    previous[signal.Name] = value;
                }
            }
        }
        return events;
    }
}
```

> 已核对 `src/PeakCan.HIL.Core/Dbc/SignalDecoder.cs`：公开入口是 `Decode(ReadOnlySpan<byte> data, Signal signal) → double`（没有 `TryDecode`）。数据短于信号位宽时高位置零，不会抛异常。

- [ ] **Step 4: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmSignalSummaryTests"
```

Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmSignalSummary.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmSignalSummaryTests.cs
git commit -m "feat(nm): DBC signal change summary for NM messages"
```

---

## Task 11: `NmAnalyzer` 组装、findings 与 unknowns

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmAnalyzer.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmAnalyzerTests.cs`

**Interfaces:**
- Consumes: Task 2–10 全部组件
- Produces: `NmAnalyzer.Analyze(NmAnalysisRequest request, CancellationToken ct = default) → NmReport`；常量 `NmAnalyzer.EngineVersion = "nm-m1-1"`

- [ ] **Step 1: 写失败测试**

```csharp
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmAnalyzerTests
{
    private static ReplayFrame F(double t, uint id, bool ext, params byte[] data)
        => new(t, id, (byte)data.Length, data, FrameFlags.None, ext);

    private static NmProfile Profile() => new(
        NmProfileSource.Dbc,
        new[] { new NmMessageDef("0x500", false, "NM_Message", "ECU_A", 8, true,
            new[] { "MsgType=CANNM" }, 500, 2000, true) },
        new NmTimings(500, 2000, 1600, 2000),
        new[] { new NmNodeDef("ECU_A", 0xF4, null, true) },
        Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance());

    [Fact]
    public void Analyze_RegularNmStream_ProducesEpisodesMessagesAndCapabilities()
    {
        var frames = Enumerable.Range(0, 41)
            .Select(i => F(i * 0.5, 0x500, false, 0xF4, 0x00, 0, 0, 0, 0, 0, 0))
            .ToArray();

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(), 0, 20.5));

        Assert.Single(report.Episodes);
        var msg = Assert.Single(report.Messages);
        Assert.Equal(41, msg.FrameCount);
        Assert.Equal(500, msg.Cycle.MedianMs);
        Assert.Equal(NmAnalyzer.EngineVersion, report.EngineVersion);
        Assert.Contains(report.Capabilities, c => c.Capability == NmCapability.IdentifyMessages && c.Available);
        Assert.Contains(report.Unknowns, u => u.Contains("bus_sleep"));
    }

    [Fact]
    public void Analyze_ConfiguredNmMessageAbsent_ProducesMessageAbsent()
    {
        var frames = new[] { F(0, 0x100, false, 0, 0, 0, 0, 0, 0, 0, 0) };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(), 0, 1));

        Assert.Contains(report.Findings, f => f.Code == "NM_MESSAGE_ABSENT");
    }

    [Fact]
    public void Analyze_DlcMismatch_ProducesFinding()
    {
        var frames = new[] { F(0, 0x500, false, 0xF4, 0) };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, Profile(), 0, 1));

        Assert.Contains(report.Findings, f => f.Code == "NM_DLC_MISMATCH");
    }

    [Fact]
    public void Analyze_NoNmFrames_ReturnsEmptyWithMissingInputs()
    {
        var frames = new[] { F(0, 0x100, false, 0, 0, 0, 0, 0, 0, 0, 0) };
        var profile = Profile() with
        {
            Messages = new[]
            {
                new NmMessageDef("0x500", false, "NM_Message", "ECU_A", 8, false, Array.Empty<string>()),
            },
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, null, profile, 0, 1));

        Assert.Empty(report.Messages);
        Assert.Contains(report.MissingInputs, m => m.InputKey == "nm_message_selection");
        Assert.Contains("该窗口", string.Join(" ", report.Assumptions));
    }

    [Fact]
    public void Analyze_Cancellation_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            NmAnalyzer.Analyze(new NmAnalysisRequest(
                "src", null, Array.Empty<ReplayFrame>(), null, Profile(), 0, 1), cts.Token));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmAnalyzerTests"
```

Expected: 编译失败。

- [ ] **Step 3: 实现组装**

```csharp
using System.Globalization;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// NM 分析门面：选择帧 → 切 episode → 逐报文周期/空档 → 内容事件 → findings。
/// 纯函数，无 IO、无 UI、无 registry 访问。
/// </summary>
public static class NmAnalyzer
{
    public const string EngineVersion = "nm-m1-1";

    private static readonly string[] s_unknowns =
    {
        "接收端本地 NM 超时状态不可观测（trace 只看到发送）",
        "总线睡眠不可证（bus_sleep；只能观察 NM 静默）",
        "节点内部状态机/唤醒原因不可观测（除非内容字典定义）",
        "未录制节点是否存在不可知",
        "记录仪是否丢帧只能给出 log_gap_suspected",
    };

    public static NmReport Analyze(NmAnalysisRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var selected = NmFrameSelector.Select(request, out var diagnostics);
        var window = new NmWindow(request.Start, request.End, request.WindowRef);
        var (capabilities, missing) = NmCapabilityGate.Evaluate(
            request.Profile, selected, request.Dbc);

        var assumptions = new List<string>();
        if (diagnostics.MultiChannel)
            assumptions.Add("存在多个通道且未指定 channel，已跳过分析；请显式指定 channel。");

        if (request.Profile.Timings.TimeoutMs is null)
            assumptions.Add("未配置 NM 超时，按 2000ms 处理。");

        var nmMessages = request.Profile.Messages.Where(m => m.IsNm).ToArray();
        // 单次建索引：1M 帧 × 每帧线性找定义会变成 O(n²)。
        var defById = nmMessages.ToDictionary(m => ParseId(m.CanIdHex));
        var nmFrames = selected
            .Where(f => defById.ContainsKey(f.Id & 0x1FFFFFFFu))
            .Select(f => new NmFrame(defById[f.Id & 0x1FFFFFFFu], f))
            .ToList();

        var timeoutMs = request.Profile.Timings.TimeoutMs ?? 2000;
        var cycleMs = request.Profile.Timings.CycleMs ?? 500;

        var episodes = NmEpisodeBuilder.Build(nmFrames, timeoutMs, request.Start, request.End);
        ct.ThrowIfCancellationRequested();

        var findings = new List<NmFinding>();
        var events = new List<NmEvent>();
        var truncations = new List<NmTruncation>();

        foreach (var conflict in request.Profile.Conflicts)
        {
            findings.Add(new NmFinding(
                "NM_CONFIG_CONFLICT", "warn",
                $"{conflict.Field} 存在冲突：{string.Join(" / ", conflict.Values)}（来自 {string.Join(" / ", conflict.Sources)}）",
                Array.Empty<double>(), false, "先统一 DBC/矩阵配置"));
        }

        var messageReports = new List<NmMessageReport>();
        foreach (var def in nmMessages)
        {
            ct.ThrowIfCancellationRequested();
            var frames = nmFrames.Where(f => f.Def.CanIdHex == def.CanIdHex).ToArray();
            if (frames.Length == 0)
            {
                findings.Add(new NmFinding("NM_MESSAGE_ABSENT", "warn",
                    $"{def.CanIdHex}（{def.Name}）在窗口内从未出现",
                    Array.Empty<double>(), false, "核对 DBC/矩阵/记录范围"));
                continue;
            }

            var timestamps = frames.Select(f => f.Frame.Timestamp).ToArray();
            var cycle = NmCycleAnalyzer.Analyze(timestamps, def, episodes);

            // spec §7.4: 偏差占比 > 5% 或连续 3 帧超容差才算 finding；
            // 共享 ID 不拆节点时本报告只给按报文的周期结论，不做按节点的偏差归因。
            if (NmCycleAnalyzer.IsDeviationSignificant(cycle))
            {
                findings.Add(new NmFinding("NM_CYCLE_DEVIATION", "warn",
                    $"{def.CanIdHex} 周期偏差 {cycle.DeviationCount}/{cycle.IntervalCount} 次（期望 {def.CycleMs}ms）",
                    timestamps.Take(5).ToArray(), true, "检查调度/负载/时钟"));
            }

            if (frames.Any(f => f.Frame.Dlc != def.Dlc))
            {
                findings.Add(new NmFinding("NM_DLC_MISMATCH", "warn",
                    $"{def.CanIdHex} 实测 DLC 与 DBC（{def.Dlc}）不一致",
                    timestamps.Take(5).ToArray(), false, "核对帧格式/配置"));
            }

            var activity = new[]
            {
                new NmSegment(timestamps[0], timestamps[^1], "active"),
            };
            messageReports.Add(new NmMessageReport(
                def.CanIdHex, def.IsExtended, def.Name, def.Sender,
                frames.Length, timestamps[0], timestamps[^1], cycle, activity,
                SilentBeyondTimeout: episodes.Count > 0 &&
                    request.End - timestamps[^1] > timeoutMs / 1000.0));
        }

        if (episodes.Any(e => e.BoundaryStart || e.BoundaryEnd))
        {
            findings.Add(new NmFinding("NM_TRACE_BOUNDARY", "info",
                "NM 活动/空档触及 trace 边界", new[] { request.Start, request.End },
                false, "记录边界，不是故障"));
        }

        var gap = NmGapAnalyzer.Analyze(new NmGapInput(
            request.SourceId, nmFrames, selected, request.Profile, episodes, window, diagnostics));
        findings.AddRange(gap.Findings);
        events.AddRange(gap.Events);

        var content = NmContentAnalyzer.Analyze(new NmContentInput(nmFrames, request.Profile, cycleMs));
        findings.AddRange(content.Findings);
        events.AddRange(content.Events);
        truncations.AddRange(content.Truncations);

        if (request.Dbc is not null)
            events.AddRange(NmSignalSummary.Build(request.Dbc, nmFrames));

        var unknowns = new List<string>(s_unknowns);
        unknowns.AddRange(gap.Unknowns);
        if (nmFrames.Count == 0)
            assumptions.Add("该窗口未发现 NM 帧；不推断节点状态。");
        if (diagnostics.DirectionUnknownFrames > 0)
            assumptions.Add($"direction=unknown 的帧 {diagnostics.DirectionUnknownFrames} 条；M1 不排除 Tx。");

        events.Sort((a, b) => a.T.CompareTo(b.T));

        return new NmReport(
            window,
            request.SourceId,
            request.Channel,
            episodes,
            messageReports,
            Array.Empty<NmNodeReport>(),
            events,
            findings,
            assumptions,
            unknowns.Distinct().ToArray(),
            capabilities,
            missing,
            truncations,
            StandardId: null,
            ProfileHash: null,
            EngineVersion: EngineVersion);
    }

    private static uint ParseId(string hex)
        => uint.Parse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmAnalyzerTests"
```

Expected: PASS。`Analyze_RegularNmStream` 的 `Cycle.MedianMs` 与 `Episodes.Count` 若与预期不符，先核对 `NmCycleAnalyzer` 的边界剔除再改断言。

- [ ] **Step 5: 提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmAnalyzer.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmAnalyzerTests.cs
git commit -m "feat(nm): assemble deterministic NM report"
```

---

## Task 12: 报告缓存

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmReportCache.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmReportCacheTests.cs`

**Interfaces:**
- Produces:
  - `NmCacheKey(string SourceId, ushort? Channel, int FrameCount, string DbcHash, string SupplementHash, double Start, double End, string EngineVersion)`
  - `NmReportCache.TryGet(NmCacheKey key, out NmReport report)`
  - `NmReportCache.Put(NmCacheKey key, NmReport report)`
  - `NmReportCache.Invalidate()` / `Invalidate(string sourceId)`

- [ ] **Step 1: 写失败测试**

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmReportCacheTests
{
    private static NmReport Report() => new(
        new NmWindow(0, 1, "full"), "src", null,
        Array.Empty<NmEpisode>(), Array.Empty<NmMessageReport>(), Array.Empty<NmNodeReport>(),
        Array.Empty<NmEvent>(), Array.Empty<NmFinding>(), Array.Empty<string>(),
        Array.Empty<string>(), Array.Empty<NmCapabilityState>(), Array.Empty<NmInputMissing>(),
        Array.Empty<NmTruncation>(), EngineVersion: NmAnalyzer.EngineVersion);

    private static NmCacheKey Key(string dbcHash = "d1", string supplement = "s1") =>
        new("src", null, 100, dbcHash, supplement, 0, 10, NmAnalyzer.EngineVersion);

    [Fact]
    public void PutThenTryGet_ReturnsCachedReport()
    {
        var cache = new NmReportCache();
        cache.Put(Key(), Report());

        Assert.True(cache.TryGet(Key(), out var report));
        Assert.Equal(NmAnalyzer.EngineVersion, report.EngineVersion);
    }

    [Fact]
    public void DifferentDbcHash_MissesCache()
    {
        var cache = new NmReportCache();
        cache.Put(Key("dbc-a"), Report());

        Assert.False(cache.TryGet(Key("dbc-b"), out _));
    }

    [Fact]
    public void EngineVersionBump_Invalidates()
    {
        var cache = new NmReportCache();
        cache.Put(Key(), Report());

        var bumped = new NmCacheKey("src", null, 100, "d1", "s1", 0, 10, "nm-m1-2");
        Assert.False(cache.TryGet(bumped, out _));
    }

    [Fact]
    public void InvalidateSource_DropsOnlyThatSource()
    {
        var cache = new NmReportCache();
        cache.Put(Key(), Report());
        cache.Put(new NmCacheKey("other", null, 100, "d1", "s1", 0, 10, NmAnalyzer.EngineVersion), Report());

        cache.Invalidate("src");

        Assert.False(cache.TryGet(Key(), out _));
        Assert.True(cache.TryGet(
            new NmCacheKey("other", null, 100, "d1", "s1", 0, 10, NmAnalyzer.EngineVersion), out _));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmReportCacheTests"
```

Expected: 编译失败。

- [ ] **Step 3: 实现**

```csharp
namespace PeakCan.Host.Core.Analysis.Nm;

public sealed record NmCacheKey(
    string SourceId,
    ushort? Channel,
    int FrameCount,
    string DbcHash,
    string SupplementHash,
    double Start,
    double End,
    string EngineVersion);

/// <summary>
/// 进程内报告缓存。线程安全（分析可能在 Task.Run 里完成）。
/// 换 DBC / sidecar / engine 版本、load/unload 都必须失效。
/// </summary>
public sealed class NmReportCache
{
    private readonly Dictionary<NmCacheKey, NmReport> _items = new();
    private readonly Lock _gate = new();

    public bool TryGet(NmCacheKey key, out NmReport report)
    {
        lock (_gate) return _items.TryGetValue(key, out report!);
    }

    public void Put(NmCacheKey key, NmReport report)
    {
        lock (_gate) _items[key] = report;
    }

    public void Invalidate(string sourceId)
    {
        lock (_gate)
        {
            foreach (var key in _items.Keys.Where(k => k.SourceId == sourceId).ToArray())
                _items.Remove(key);
        }
    }

    public void Invalidate()
    {
        lock (_gate) _items.Clear();
    }
}
```

> `Lock` 是 .NET 9+ 的 `System.Threading.Lock`；仓库目标框架是 net10.0，可直接用。若构建报找不到 `Lock`，改用 `private readonly object _gate = new();`。

- [ ] **Step 4: 跑测试确认通过**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmReportCacheTests"
```

Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src/PeakCan.Host.Core/Analysis/Nm/NmReportCache.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmReportCacheTests.cs
git commit -m "feat(nm): in-process report cache keyed by dbc/supplement/window"
```

---

## Task 13: 合成 fixture 回归与性能基线

**Files:**
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmAnalyzerFixtureTests.cs`
- Create（测试内联生成，不落盘）：周期/断流/日志空档/边界/原始内容/DLC 六类场景

**Interfaces:**
- Consumes: `NmAnalyzer`

- [ ] **Step 1: 写场景测试**

```csharp
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

/// <summary>
/// spec §13.2 M1 fixture 1–7 的等价内联场景。ASC 文件版本留给集成测试，
/// 这里锁定引擎行为，不依赖文件 IO。
/// </summary>
public class NmAnalyzerFixtureTests
{
    private static NmProfile Profile() => new(
        NmProfileSource.Dbc,
        new[] { new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, true,
            new[] { "MsgType=CANNM" }, 500, 2000, true) },
        new NmTimings(500, 2000, 1600, 2000, 50, 5),
        new[] { new NmNodeDef("BMS", 0xF4, null, true) },
        Array.Empty<NmConfigConflict>(), Array.Empty<string>(), new NmProvenance());

    private static ReplayFrame F(double t, uint id = 0x18FFFAF4, int dlc = 8)
        => new(t, id, (byte)dlc, Enumerable.Repeat((byte)0xF4, dlc).ToArray(), FrameFlags.None, true);

    private static NmReport Run(IReadOnlyList<ReplayFrame> frames, double end)
        => NmAnalyzer.Analyze(new NmAnalysisRequest("src", null, frames, null, Profile(), 0, end));

    [Fact]
    public void Fixture1_TimingBreakAndBurst()
    {
        var frames = new List<ReplayFrame>();
        for (int i = 0; i < 5; i++) frames.Add(F(i * 0.05));
        for (int i = 1; i <= 8; i++) frames.Add(F(0.2 + i * 0.5));
        // 尾段必须与 4.2s 的最后一帧间隔 ≥ 2s（超时），否则切不出第二个 episode，
        // 也就不会有 NM_ALL_NM_SILENT。5.0s 只有 0.8s 间隔，切不开。
        for (int i = 0; i < 5; i++) frames.Add(F(7.0 + i * 0.05));

        var report = Run(frames, 7.2);

        Assert.Contains(report.Events, e => e.Type == "burst_observed");
        Assert.Equal(2, report.Episodes.Count);
        Assert.Contains(report.Findings, f => f.Code == "NM_ALL_NM_SILENT");
    }

    [Fact]
    public void Fixture3_BoundaryMarked()
    {
        var frames = new[] { F(0), F(0.5), F(1.0) };

        var report = Run(frames, 1.0);

        Assert.True(report.Episodes[0].BoundaryStart);
        Assert.True(report.Episodes[0].BoundaryEnd);
        Assert.Contains(report.Findings, f => f.Code == "NM_TRACE_BOUNDARY");
    }

    [Fact]
    public void Fixture4_RawContentEventsWithoutNidConclusions()
    {
        var frames = new[]
        {
            new ReplayFrame(0.0, 0x18FFFAF4, 8, new byte[] { 0xF4, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None, true),
            new ReplayFrame(0.5, 0x18FFFAF4, 8, new byte[] { 0x27, 0x08, 0, 0, 0, 0, 0, 0 }, FrameFlags.None, true),
        };

        var report = Run(frames, 0.5);

        Assert.Contains(report.Events, e => e.Type == "byte_change");
        Assert.Contains(report.Events, e => e.Type == "bit_change");
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("NM_NID"));
        Assert.DoesNotContain(report.Findings, f => f.Code == "NM_PNI_WHILE_PN_DISABLED");
    }

    [Fact]
    public void Fixture6_AbsentAndDlcMismatch()
    {
        var present = new[] { F(0, dlc: 4), F(0.5, dlc: 4) };

        var report = Run(present, 1.0);

        Assert.Contains(report.Findings, f => f.Code == "NM_DLC_MISMATCH");
    }

    [Fact]
    public void Fixture7_NoNm_ReturnsEmptyAndExplains()
    {
        var frames = new[] { new ReplayFrame(0, 0x100, 8, new byte[8], FrameFlags.None) };
        var profile = Profile() with
        {
            Messages = new[]
            {
                new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, false, Array.Empty<string>()),
            },
        };

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest("src", null, frames, null, profile, 0, 1));

        Assert.Empty(report.Messages);
        Assert.Contains(report.Assumptions, a => a.Contains("未发现 NM 帧"));
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("NM_NODE"));
    }
}
```

- [ ] **Step 2: 跑测试**

```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~NmAnalyzerFixtureTests"
```

Expected: 5 个用例全绿。失败时先检查 fixture 的时间轴是否与断言一致，再改测试数据；**不要为了让断言过而放宽引擎规则**。

- [ ] **Step 3: 手动性能基线（不写成 CI 断言）**

在开发机上用一段 100 万帧的合成输入跑一次：

```csharp
// 临时测试（本地跑完删除，不入库）
[Fact]
public void Perf_OneMillionFrames_ManualBaseline()
{
    var frames = new List<ReplayFrame>(1_000_000);
    for (int i = 0; i < 1_000_000; i++)
        frames.Add(new ReplayFrame(i * 0.001, 0x18FFFAF4, 8,
            new byte[] { 0xF4, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None, true));

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var report = NmAnalyzer.Analyze(new NmAnalysisRequest("src", null, frames, null, Profile(), 0, 1000));
    sw.Stop();

    Console.WriteLine($"NM overview 1M frames: {sw.ElapsedMilliseconds} ms, events={report.Events.Count}");
}
```

Expected: 开发机 < 2000ms（spec AC7）。把实测值追加到本计划的「性能记录」一节；**不要提交这个临时测试文件**。

- [ ] **Step 4: 提交**

```bash
git add tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmAnalyzerFixtureTests.cs
git commit -m "test(nm): pin M1 analyzer behavior on synthetic fixtures"
```

---

## Self-Review

**1. Spec 覆盖**

| spec 要求 | 任务 |
|---|---|
| §5.1 NmProfile 字段与来源 | Task 1、2、3 |
| §5.2 NmReport 全部 M1 字段 | Task 1、11 |
| §6.6.1 能力—输入矩阵 | Task 4 |
| §6.6.4 建议算法（确定性、不自动应用） | Task 4 |
| §6.6.6 sidecar 持久化 | Task 3 |
| §7.1 帧选择/方向/时间戳/RTR/错误帧/多通道 | Task 5 |
| §7.2 识别优先级（`MsgType` 经枚举确认 > `NmAsrMessage` > 名称候选） | Task 2 |
| §7.3 episode 分割 | Task 6 |
| §7.4 周期/相位/样本不足/容差/共享 ID 抑制 | Task 7（共享 ID 抑制：`NmMessageReport` 不拆节点，天然按报文统计） |
| §7.5 空档优先级 | Task 8 |
| §7.6 观察突发 + 主动唤醒位 | Task 9（主动唤醒位留 M2：需要 CBV 位角色，spec §7.6 只列 `NM_ACTIVE_WAKEUP`，不在 §7.8 M1 code 清单） |
| §7.7 NID/CBV/用户数据（输入齐备时） | Task 9 |
| §7.8 M1 finding 清单 | Task 8、9、11 |
| §7.9 不可知项 | Task 11 |
| §10 后台/快照/单次遍历/压缩/缓存 key | Task 5、9、12（`Task.Run` 由计划 C 的调用方负责） |
| §13.2 M1 fixture 1–7 | Task 13 |
| AC2–AC5、AC7、AC12 | Task 11、13（AC12 的补全后能力开启 → Task 3+4） |

**2. Placeholder 扫描**：无 TBD。`SignalDecoder.Decode` 的签名已对照仓库核实；`Lock` 附了 .NET 9 不可用时的替代写法。

**3. 类型一致性**：`NmFrame`、`NmAnalysisRequest`、`NmSelectionDiagnostics`、`NmContentInput/Result`、`NmGapInput/Result` 在全篇一致；`ParseId` 在三处重复实现（`NmProfileBuilder`/`NmCapabilityGate`/`NmAnalyzer`），**执行时不合并**——合并会把 Task 2/4 的文件拖进 Task 11 的 diff，收益只是少 6 行；留给整条分支的终审决定是否统一。

**4. Review Focus 覆盖**：8 条全部有对应测试。

**5. 已知偏差**

- `NmProfile` 保留 `Layout`/`Features`（M1 需要），M2 专属字段不声明。
- `NM_NID_MISMATCH` 等四个 finding 在 M1 输入齐备时产出（依据 §7.8 末句），与 finding 表的"阶段"列字面不同。
- `NM_ACTIVE_WAKEUP`（主动唤醒位）留到 M2：它需要 CBV 位角色表，M1 没有。
- `ParseId` 重复三处（`NmProfileBuilder`/`NmCapabilityGate`/`NmAnalyzer`）；**本计划不合并**，终审再判。

## 性能记录

| 日期 | 帧数 | 耗时 | 环境 | 备注 |
|---|---|---|---|---|
| 2026-11-20 | 1,000,000 | Release 1783 ms（Debug 2037 ms） | 开发机 | AC7 基线（< 2000ms），余量约 11%，含首次 JIT |
