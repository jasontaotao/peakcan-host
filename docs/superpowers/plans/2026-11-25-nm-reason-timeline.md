# NM 唤醒原因原始值时间线 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 peakcan-host 的 NM 面板时间轴上增加“唤醒原因 / 保持唤醒原因”的原始值区间，保留 DBC 值表解释和来源，数据不完整时明确降级。

**Architecture:** Core 新增独立的 `NmReasonDictionaryStore`（字典加载）与 `NmReasonAnalyzer`（纯函数时间线分析），结果以结构化 `NmReport.ReasonTimelines` 挂到报告；App 层只做加载接线、时间轴行渲染、事件行与导出，不重新计算。

**Tech Stack:** C# / .NET 10、WPF、xUnit + FluentAssertions、System.Text.Json、`PeakCan.HIL.Core`（sibling ProjectReference，DBC 与 BLF 解析）。

**Spec:** `docs/superpowers/specs/2026-11-25-nm-reason-timeline-design.md`（执行前必须通读；本计划的每个任务都从该 spec 派生）

## Global Constraints

- **不要 commit、不要 push**：用户明确要求只在他说要的时候提交。每个任务结束时停下，等用户统一指示。
- **只跑定向测试**：`dotnet test <单个项目.csproj> --filter ...`。禁止跑整个解决方案或全量测试。
- **每任务完成后调用 `code-reviewer` agent** 审阅本任务改动，未审阅不得进入下一任务。
- **不要修改** `D:\claude_proj2\peakcan-hil-core`（HIL.Core 通过 sibling ProjectReference 引用）。
- 注释语言：面向用户 / 业务逻辑用中文；技术 API、DBC 字段、协议字段用英文。
- 第一版只实现 `Raw` / `Enum` 整值语义；`Bitmask` 必须显式降级并在 Unknowns 记录，不得逐位展开。
- 所有“含义未配置”“不可观测”“日志截断”的文案必须如实，不得用推断替换观测。

## Review Focus

- **同 ID 双帧型**：同一份 DBC 里标准帧与扩展帧可能共享数值 ID；字典字段必须只分析 `extended` 匹配的那一种，否则会把两条报文的原因混成一条时间线。
- **VAL_ 按原始值匹配**：DBC 信号有 factor/offset 时，值表键仍是原始值；用物理值匹配会漏掉条目。
- **同名信号跨报文**：同一信号名可能出现在多条报文；必须按 `can_id + extended + signal` 绑定，不能只按信号名。
- **字典条目越界/负数**：条目键超出信号位宽或为负时，必须加载期拒绝或明确忽略，不能静默丢弃。
- **窗口从中间开始/结束**：viewport 分析时首段必须标 `StartUnknown`、末段必须标 `EndUnknown`，不能把“窗口内第一次看到”当成“第一次出现”。

---

### Task 1: 原因字典模型与加载器

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmReasonDictionary.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmReasonDictionaryTests.cs`

**Interfaces:**
- Consumes: 无（新模块）
- Produces:
  - `NmReasonKind { Raw, Enum, Bitmask }`
  - `NmReasonSource(string? Doc, string? Revision)`
  - `NmReasonFieldDef(string Signal, string CanIdHex, bool IsExtended, NmReasonKind Kind = Raw, IReadOnlyDictionary<long,string>? Entries = null)`
  - `NmReasonDictionary(int Version, IReadOnlyList<NmReasonFieldDef> Fields, NmReasonSource? Source = null)`
  - `LoadedNmReasonDictionary(NmReasonDictionary Dictionary, string Path, string Sha256)`
  - `NmReasonDictionaryException(string message)`
  - `NmReasonDictionaryStore.LoadFrom(string path)` → `LoadedNmReasonDictionary`
  - `NmReasonDictionaryStore.Load(string json, string path)` → `LoadedNmReasonDictionary`
  - `NmReasonDictionaryStore.ResolvePath(string profilePath, string reasonsPath)` → `string`

- [ ] **Step 1: 写失败测试**

```csharp
using System.Text;
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmReasonDictionaryTests
{
    private const string Minimal = """
    {
      "version": 1,
      "fields": [
        { "signal": "BMS_Wakeup_reasons", "can_id": "0x18FFFAF4", "extended": true }
      ]
    }
    """;

    [Fact]
    public void MinimalField_LoadsAsRawWithoutEntries()
    {
        var loaded = NmReasonDictionaryStore.Load(Minimal, "mem.nmreasons.json");
        var field = Assert.Single(loaded.Dictionary.Fields);
        Assert.Equal("BMS_Wakeup_reasons", field.Signal);
        Assert.Equal("0x18FFFAF4", field.CanIdHex);
        Assert.True(field.IsExtended);
        Assert.Equal(NmReasonKind.Raw, field.Kind);
        Assert.Null(field.Entries);
        Assert.Equal(64, loaded.Sha256.Length);
    }

    [Fact]
    public void MissingExtended_IsRejected()
    {
        var json = """
        { "version": 1, "fields": [ { "signal": "S", "can_id": "0x1" } ] }
        """;
        var ex = Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.Load(json, "mem.json"));
        Assert.Contains("extended", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownKind_IsRejected()
    {
        var json = """
        { "version": 1, "fields": [
          { "signal": "S", "can_id": "0x1", "extended": true, "kind": "bogus" } ] }
        """;
        Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.Load(json, "mem.json"));
    }

    [Fact]
    public void BitmaskEntryNotPowerOfTwo_IsRejected()
    {
        var json = """
        { "version": 1, "fields": [
          { "signal": "S", "can_id": "0x1", "extended": true,
            "kind": "bitmask", "entries": { "3": "two bits" } } ] }
        """;
        var ex = Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.Load(json, "mem.json"));
        Assert.Contains("2 的幂", ex.Message);
    }

    [Fact]
    public void DuplicateField_IsRejected()
    {
        var json = """
        { "version": 1, "fields": [
          { "signal": "S", "can_id": "0x1", "extended": true },
          { "signal": "S", "can_id": "0x1", "extended": true } ] }
        """;
        Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.Load(json, "mem.json"));
    }

    [Fact]
    public void BadJson_IsRejected()
    {
        Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.Load("{ not json", "mem.json"));
    }

    [Fact]
    public void RelativePath_ResolvesFromProfileDirectory()
    {
        var resolved = NmReasonDictionaryStore.ResolvePath(
            @"C:\projects\bms\matrix.nmprofile.json", "reasons.nmreasons.json");
        Assert.Equal(@"C:\projects\bms\reasons.nmreasons.json", resolved);
    }

    [Fact]
    public void AbsolutePath_IsKept()
    {
        var resolved = NmReasonDictionaryStore.ResolvePath(
            @"C:\projects\bms\matrix.nmprofile.json", @"D:\other\reasons.json");
        Assert.Equal(@"D:\other\reasons.json", resolved);
    }

    [Fact]
    public void MissingFile_Throws()
    {
        Assert.Throws<NmReasonDictionaryException>(
            () => NmReasonDictionaryStore.LoadFrom(@"D:\does-not-exist\nmreasons.json"));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
cd /d/claude_proj2/peakcan-host
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmReasonDictionaryTests"
```
Expected: FAIL，`NmReasonDictionaryStore` / `NmReasonDictionaryException` 未定义。

- [ ] **Step 3: 实现字典模型与加载器**

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeakCan.Host.Core.Analysis.Nm;

public enum NmReasonKind
{
    Raw,
    Enum,
    Bitmask,
}

public sealed record NmReasonSource(string? Doc, string? Revision);

public sealed record NmReasonFieldDef(
    string Signal,
    string CanIdHex,
    bool IsExtended,
    NmReasonKind Kind = NmReasonKind.Raw,
    IReadOnlyDictionary<long, string>? Entries = null);

public sealed record NmReasonDictionary(
    int Version,
    IReadOnlyList<NmReasonFieldDef> Fields,
    NmReasonSource? Source = null);

/// <summary>加载成功后的字典及其来源身份；报告用 Path/Sha256 追溯解释来源。</summary>
public sealed record LoadedNmReasonDictionary(
    NmReasonDictionary Dictionary,
    string Path,
    string Sha256);

public sealed class NmReasonDictionaryException : Exception
{
    public NmReasonDictionaryException(string message) : base(message) { }
    public NmReasonDictionaryException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// <c>*.nmreasons.json</c> 读写。第一版只要求字段声明；条目与 kind 可选。
/// </summary>
public static class NmReasonDictionaryStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    /// <summary>相对路径相对 nmprofile 所在目录解析；绝对路径原样返回。</summary>
    public static string ResolvePath(string profilePath, string reasonsPath)
    {
        if (Path.IsPathRooted(reasonsPath)) return reasonsPath;
        var dir = Path.GetDirectoryName(profilePath) ?? string.Empty;
        return Path.Combine(dir, reasonsPath);
    }

    public static LoadedNmReasonDictionary LoadFrom(string path)
    {
        if (!File.Exists(path))
            throw new NmReasonDictionaryException($"原因字典不存在：{path}");
        try
        {
            return Load(File.ReadAllText(path), path);
        }
        catch (IOException ex)
        {
            throw new NmReasonDictionaryException($"原因字典读取失败：{path}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new NmReasonDictionaryException($"原因字典无读取权限：{path}", ex);
        }
    }

    public static LoadedNmReasonDictionary Load(string json, string path)
    {
        NmReasonDictionaryDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<NmReasonDictionaryDto>(json, s_json);
        }
        catch (JsonException ex)
        {
            throw new NmReasonDictionaryException($"原因字典 JSON 解析失败：{ex.Message}", ex);
        }

        if (dto is null)
            throw new NmReasonDictionaryException("原因字典反序列化结果为空");
        if (dto.Version < 1)
            throw new NmReasonDictionaryException($"原因字典 version 非法：{dto.Version}");

        var fields = new List<NmReasonFieldDef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in dto.Fields ?? Array.Empty<ReasonFieldDto>())
        {
            if (item is null)
                throw new NmReasonDictionaryException("fields 含空项");
            if (string.IsNullOrWhiteSpace(item.Signal))
                throw new NmReasonDictionaryException("fields[].signal 必填");
            if (string.IsNullOrWhiteSpace(item.CanIdHex))
                throw new NmReasonDictionaryException("fields[].can_id 必填");
            if (item.Extended is null)
                throw new NmReasonDictionaryException(
                    $"字段 {item.Signal} 缺少 extended：标准帧/扩展帧必须显式声明");
            if (!IsCanIdShape(item.CanIdHex))
                throw new NmReasonDictionaryException(
                    $"字段 {item.Signal} 的 can_id「{item.CanIdHex}」不是 0x 十六进制");

            var kind = item.Kind ?? NmReasonKind.Raw;
            var entries = item.Entries is { Count: > 0 }
                ? new Dictionary<long, string>(item.Entries)
                : null;

            if (kind == NmReasonKind.Bitmask && entries is not null)
            {
                foreach (var key in entries.Keys)
                {
                    if (key <= 0 || (key & (key - 1)) != 0)
                        throw new NmReasonDictionaryException(
                            $"字段 {item.Signal} 的 bitmask 条目键 {key} 不是 2 的幂");
                }
            }

            var identity = $"{item.CanIdHex}|{item.Extended}|{item.Signal}";
            if (!seen.Add(identity))
                throw new NmReasonDictionaryException($"字段重复声明：{identity}");

            fields.Add(new NmReasonFieldDef(
                item.Signal, item.CanIdHex, item.Extended.Value, kind, entries));
        }

        var dictionary = new NmReasonDictionary(
            dto.Version, fields,
            dto.Source is null ? null : new NmReasonSource(dto.Source.Doc, dto.Source.Revision));

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return new LoadedNmReasonDictionary(dictionary, path, hash);
    }

    private static bool IsCanIdShape(string text)
    {
        if (!text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return false;
        return uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out _);
    }

    private sealed record NmReasonDictionaryDto(
        int Version,
        IReadOnlyList<ReasonFieldDto>? Fields,
        SourceDto? Source);

    private sealed record ReasonFieldDto(
        string? Signal,
        string? CanIdHex,
        bool? Extended,
        NmReasonKind? Kind,
        Dictionary<long, string>? Entries);

    private sealed record SourceDto(string? Doc, string? Revision);
}
```

- [ ] **Step 4: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmReasonDictionaryTests"
```
Expected: PASS（9 个用例）。

- [ ] **Step 5: 调用 code-reviewer 审阅本任务**

使用 `Agent` 工具 `subagent_type: "code-reviewer"`，传入本任务改动文件路径与 spec 路径，确认无阻塞问题后进入 Task 2。

---

### Task 2: 结果模型 + 基础切段（同 episode 值变化、首段/末段）

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmReasonModels.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Nm/NmReasonAnalyzer.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmSignalSummary.cs`（只加 `SkipKey` helper）
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmReasonAnalyzerTests.cs`

**Interfaces:**
- Consumes: `NmReasonDictionary` / `NmReasonFieldDef`（Task 1）、`NmFrame`、`NmEpisode`、`NmWindow`、`SignalDecoder.DecodeRaw`、`DbcDocument.MessagesById`
- Produces:
  - `NmReasonLabel(string Text, string Source)`
  - `NmReasonSegmentEnd { Changed, EpisodeEnd, Unreadable, WindowEnd }`
  - `NmReasonSegment(double Start, double End, ulong RawValue, IReadOnlyList<NmReasonLabel> Labels, bool StartUnknown, bool EndUnknown, NmReasonSegmentEnd EndReason, double? NextObservedAt, IReadOnlyList<byte> FirstFrameBytes)`
  - `NmReasonTransition(double T, string Signal, string CanIdHex, bool IsExtended, ulong OldRaw, ulong NewRaw, IReadOnlyList<NmReasonLabel> OldLabels, IReadOnlyList<NmReasonLabel> NewLabels)`
  - `NmReasonTimeline(string Signal, string CanIdHex, bool IsExtended, NmReasonKind DeclaredKind, NmReasonKind EffectiveKind, IReadOnlyList<NmReasonSegment> Segments, IReadOnlyList<NmReasonTransition> Transitions, IReadOnlyList<double> UnreadableFrameTimes, IReadOnlyList<string> Unknowns)`
  - `NmReasonAnalysis(IReadOnlyList<NmReasonTimeline> Timelines, IReadOnlyList<NmTruncation> Truncations, IReadOnlySet<string> CoveredSignals)`
  - `NmReasonAnalyzer.Analyze(NmReasonDictionary, DbcDocument, IReadOnlyList<NmFrame>, IReadOnlyList<NmEpisode>, NmWindow)` → `NmReasonAnalysis`
  - `NmSignalSummary.SkipKey(string canIdHex, bool isExtended, string signalName)` → `string`

- [ ] **Step 1: 写失败测试（基础切段）**

```csharp
using System.Text;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmReasonAnalyzerTests
{
    private const string CanId = "0x18FFFAF4";

    private static DbcDocument WakeupDbc(string? valLine =
        """VAL_ 2566912756 BMS_Wakeup_reasons 0 "Default" 48 "Waked up by NW PDU" ;""")
        => DbcParser.Parse($"""
VERSION ""
BO_ 2566912756 BMS_NWM: 8 BMS
 SG_ BMS_Address : 0|8@1+ (1,0) [0|255] "bit" Vector__XXX
 SG_ BMS_Wakeup_reasons : 16|8@1+ (1,0) [0|255] "bit" Vector__XXX
{valLine}
""").Value!;

    private static NmFrame Frame(double t, byte reason, byte address = 1)
        => new(
            new NmMessageDef(CanId, true, "BMS_NWM", "BMS", 8, true, ["test"]),
            new ReplayFrame(t, 0x98FFFAF4u, 8,
                new byte[] { address, 0x00, reason, 0x00, 0, 0, 0, 0 },
                FrameFlags.None, IsExtended: true));

    private static NmReasonDictionary Dict()
        => new(1, [new NmReasonFieldDef("BMS_Wakeup_reasons", CanId, true)]);

    private static NmEpisode[] OneEpisode(double start, double end)
        => [new NmEpisode(0, start, end, "nm", null, [CanId], false, false)];

    private static NmReasonTimeline Single(NmReasonAnalysis analysis)
        => Assert.Single(analysis.Timelines);

    [Fact]
    public void ValueChange_SplitsSegments_AndEmitsTransition()
    {
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(), [Frame(0.1, 0x00), Frame(0.6, 0x30), Frame(1.1, 0x30)],
            OneEpisode(0.1, 1.1), new NmWindow(0.1, 1.1, "test"));
        var timeline = Single(analysis);

        Assert.Equal(2, timeline.Segments.Count);
        Assert.Single(timeline.Transitions);

        var first = timeline.Segments[0];
        Assert.Equal(0.1, first.Start, 3);
        Assert.Equal(0.1, first.End, 3);          // 最后确认 0x00 的帧
        Assert.Equal(0UL, first.RawValue);
        Assert.Equal(NmReasonSegmentEnd.Changed, first.EndReason);
        Assert.Equal(0.6, first.NextObservedAt!.Value);
        Assert.True(first.StartUnknown);

        var second = timeline.Segments[1];
        Assert.Equal(0.6, second.Start, 3);
        Assert.Equal(1.1, second.End, 3);
        Assert.Equal(48UL, second.RawValue);
        Assert.Equal(NmReasonSegmentEnd.WindowEnd, second.EndReason);
        Assert.True(second.EndUnknown);

        Assert.Equal(48UL, timeline.Transitions[0].NewRaw);
    }

    [Fact]
    public void SameValueAcrossFrames_IsOneSegment()
    {
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(), [Frame(0.1, 0x30), Frame(0.6, 0x30), Frame(1.1, 0x30)],
            OneEpisode(0.1, 1.1), new NmWindow(0.1, 1.1, "test"));
        var timeline = Single(analysis);
        Assert.Single(timeline.Segments);
        Assert.Empty(timeline.Transitions);
        Assert.True(timeline.Segments[0].StartUnknown);
        Assert.True(timeline.Segments[0].EndUnknown);
    }

    [Fact]
    public void MissingSignal_YieldsEmptyTimelineWithUnknown()
    {
        var dict = new NmReasonDictionary(1,
            [new NmReasonFieldDef("NoSuchSignal", CanId, true)]);
        var analysis = NmReasonAnalyzer.Analyze(
            dict, WakeupDbc(), [Frame(0.1, 0x30)], OneEpisode(0.1, 0.1),
            new NmWindow(0.1, 0.1, "test"));
        var timeline = Single(analysis);
        Assert.Empty(timeline.Segments);
        Assert.Contains(timeline.Unknowns, u => u.Contains("NoSuchSignal"));
        Assert.Equal(NmReasonKind.Raw, timeline.EffectiveKind);
    }

    [Fact]
    public void DlcTooShort_IsUnreadableNotZero()
    {
        var frame = new NmFrame(
            new NmMessageDef(CanId, true, "BMS_NWM", "BMS", 2, true, ["test"]),
            new ReplayFrame(0.1, 0x98FFFAF4u, 2, new byte[] { 1, 0 },
                FrameFlags.None, IsExtended: true));
        var analysis = NmReasonAnalyzer.Analyze(
            Dict(), WakeupDbc(), [frame], OneEpisode(0.1, 0.1),
            new NmWindow(0.1, 0.1, "test"));
        var timeline = Single(analysis);
        Assert.Empty(timeline.Segments);
        Assert.Contains(0.1, timeline.UnreadableFrameTimes);
        Assert.DoesNotContain(timeline.Segments, s => s.RawValue == 0);
    }

    [Fact]
    public void StandardFrameField_DoesNotMatchExtendedFrames()
    {
        var dict = new NmReasonDictionary(1,
            [new NmReasonFieldDef("BMS_Wakeup_reasons", CanId, IsExtended: false)]);
        var analysis = NmReasonAnalyzer.Analyze(
            dict, WakeupDbc(), [Frame(0.1, 0x30)],
            OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
        var timeline = Single(analysis);
        Assert.Empty(timeline.Segments);
        Assert.Contains(timeline.Unknowns, u => u.Contains(CanId));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmReasonAnalyzerTests"
```
Expected: FAIL，类型未定义。

- [ ] **Step 3: 实现模型与分析器（基础版）**

`NmReasonModels.cs`：

```csharp
namespace PeakCan.Host.Core.Analysis.Nm;

public sealed record NmReasonLabel(string Text, string Source);

public enum NmReasonSegmentEnd
{
    Changed,
    EpisodeEnd,
    Unreadable,
    WindowEnd,
}

public sealed record NmReasonSegment(
    double Start,
    double End,
    ulong RawValue,
    IReadOnlyList<NmReasonLabel> Labels,
    bool StartUnknown,
    bool EndUnknown,
    NmReasonSegmentEnd EndReason,
    double? NextObservedAt,
    IReadOnlyList<byte> FirstFrameBytes);

public sealed record NmReasonTransition(
    double T,
    string Signal,
    string CanIdHex,
    bool IsExtended,
    ulong OldRaw,
    ulong NewRaw,
    IReadOnlyList<NmReasonLabel> OldLabels,
    IReadOnlyList<NmReasonLabel> NewLabels);

public sealed record NmReasonTimeline(
    string Signal,
    string CanIdHex,
    bool IsExtended,
    NmReasonKind DeclaredKind,
    NmReasonKind EffectiveKind,
    IReadOnlyList<NmReasonSegment> Segments,
    IReadOnlyList<NmReasonTransition> Transitions,
    IReadOnlyList<double> UnreadableFrameTimes,
    IReadOnlyList<string> Unknowns);

public sealed record NmReasonAnalysis(
    IReadOnlyList<NmReasonTimeline> Timelines,
    IReadOnlyList<NmTruncation> Truncations,
    IReadOnlySet<string> CoveredSignals);
```

`NmReasonAnalyzer.cs`（基础版完整代码，Task 3 再扩跨 episode）：

```csharp
using System.Globalization;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.Core.Analysis.Nm;

/// <summary>
/// 把字典声明的字段逐帧解码成“值区间”。纯函数，无 IO。
/// 第一版只做整值（Raw/Enum）；Bitmask 由调用方降级前先记录。
/// </summary>
public static class NmReasonAnalyzer
{
    private const int MaxSegmentsPerField = 200;

    public static NmReasonAnalysis Analyze(
        NmReasonDictionary dictionary,
        DbcDocument dbc,
        IReadOnlyList<NmFrame> frames,
        IReadOnlyList<NmEpisode> episodes,
        NmWindow window)
    {
        var timelines = new List<NmReasonTimeline>();
        var truncations = new List<NmTruncation>();
        var covered = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in dictionary.Fields)
        {
            var unknowns = new List<string>();
            var timeline = AnalyzeField(
                field, dbc, frames, episodes, window, unknowns, truncations);
            timelines.Add(timeline);
            covered.Add(NmSignalSummary.SkipKey(
                field.CanIdHex, field.IsExtended, field.Signal));
        }

        return new NmReasonAnalysis(timelines, truncations, covered);
    }

    private static NmReasonTimeline AnalyzeField(
        NmReasonFieldDef field,
        DbcDocument dbc,
        IReadOnlyList<NmFrame> frames,
        IReadOnlyList<NmEpisode> episodes,
        NmWindow window,
        List<string> unknowns,
        List<NmTruncation> truncations)
    {
        var effective = field.Kind == NmReasonKind.Bitmask ? NmReasonKind.Raw : field.Kind;
        if (field.Kind == NmReasonKind.Bitmask)
            unknowns.Add($"{field.Signal}：第一版不支持位掩码，已按原始整值显示");

        var message = FindMessage(dbc, field.CanIdHex, field.IsExtended);
        var signal = message?.Signals.FirstOrDefault(
            s => string.Equals(s.Name, field.Signal, StringComparison.Ordinal));
        if (signal is null)
        {
            unknowns.Add($"{field.Signal}：DBC 中未找到该信号（{field.CanIdHex}）");
            return Empty(field, effective, unknowns);
        }

        var fieldFrames = frames
            .Where(f => string.Equals(f.Def.CanIdHex, field.CanIdHex, StringComparison.OrdinalIgnoreCase)
                        && f.Def.IsExtended == field.IsExtended
                        && f.Frame.Timestamp >= window.Start
                        && f.Frame.Timestamp <= window.End)
            .OrderBy(f => f.Frame.Timestamp)
            .ToArray();

        var segments = new List<NmReasonSegment>();
        var transitions = new List<NmReasonTransition>();
        var unreadable = new List<double>();
        var labelsCache = new Dictionary<ulong, IReadOnlyList<NmReasonLabel>>();

        SegmentBuilder? current = null;

        void Close(double end, NmReasonSegmentEnd reason, double? next)
        {
            if (current is null) return;
            segments.Add(new NmReasonSegment(
                current.Start, end, current.Raw, LabelsOf(current.Raw, labelsCache),
                current.StartUnknown, reason == NmReasonSegmentEnd.WindowEnd,
                reason, next, current.FirstBytes));
            current = null;
        }

        foreach (var frame in fieldFrames)
        {
            var data = frame.Frame.Data;
            if (!Fits(data, signal))
            {
                unreadable.Add(frame.Frame.Timestamp);
                continue;
            }

            var raw = SignalDecoder.DecodeRaw(data, signal);
            var t = frame.Frame.Timestamp;

            if (current is not null && current.Raw != raw)
            {
                transitions.Add(new NmReasonTransition(
                    t, field.Signal, field.CanIdHex, field.IsExtended,
                    current.Raw, raw, LabelsOf(current.Raw, labelsCache),
                    LabelsOf(raw, labelsCache)));
                Close(current.LastConfirmedT, NmReasonSegmentEnd.Changed, t);
            }

            if (current is null)
            {
                current = new SegmentBuilder(
                    t, raw, true, data.ToArray());
            }

            current.LastConfirmedT = t;
        }

        if (current is not null)
            Close(current.LastConfirmedT, NmReasonSegmentEnd.WindowEnd, null);

        // 段数上限：保留前 N 段，记录截断（attempted 以实际生成的段计数）。
        if (segments.Count > MaxSegmentsPerField)
        {
            var kept = segments.Take(MaxSegmentsPerField).ToArray();
            truncations.Add(new NmTruncation(
                "reason:segments:" + field.CanIdHex,
                kept.Length, segments.Count));
            segments = kept.ToList();
        }

        return new NmReasonTimeline(
            field.Signal, field.CanIdHex, field.IsExtended,
            field.Kind, effective, segments, transitions, unreadable, unknowns);

        IReadOnlyList<NmReasonLabel> LabelsOf(
            ulong raw, Dictionary<ulong, IReadOnlyList<NmReasonLabel>> cache)
        {
            if (cache.TryGetValue(raw, out var cached)) return cached;
            var labels = BuildLabels(field, signal, raw, dbc);
            cache[raw] = labels;
            return labels;
        }
    }

    private static IReadOnlyList<NmReasonLabel> BuildLabels(
        NmReasonFieldDef field, Signal signal, ulong raw, DbcDocument dbc)
    {
        if (raw > long.MaxValue) return Array.Empty<NmReasonLabel>();
        var rawLong = (long)raw;
        var labels = new List<NmReasonLabel>(2);

        if (signal.ValueTableName is { Length: > 0 } tableName
            && dbc.ValueTables.TryGetValue(tableName, out var table)
            && table.Entries.TryGetValue(rawLong, out var dbcText))
        {
            labels.Add(new NmReasonLabel(dbcText, "dbc"));
        }

        if (field.Entries is { } entries
            && entries.TryGetValue(rawLong, out var dictText)
            && labels.All(l => !string.Equals(l.Text, dictText, StringComparison.Ordinal)))
        {
            labels.Add(new NmReasonLabel(dictText, "dictionary"));
        }

        return labels;
    }

    private static Message? FindMessage(DbcDocument dbc, string canIdHex, bool isExtended)
    {
        if (!canIdHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return null;
        if (!uint.TryParse(canIdHex.AsSpan(2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out var id)) return null;
        if (!dbc.MessagesById.TryGetValue(id, out var message)) return null;
        if (((message.Id & 0x80000000u) != 0) != isExtended) return null;
        return message;
    }

    private static bool Fits(ReadOnlySpan<byte> data, Signal signal)
    {
        if (signal.Length == 0) return false;
        var bits = data.Length * 8;
        return signal.Order == ByteOrder.LittleEndian
            ? signal.StartBit + signal.Length - 1 < bits
            : signal.StartBit < bits && signal.StartBit >= signal.Length - 1;
    }

    private static NmReasonTimeline Empty(
        NmReasonFieldDef field, NmReasonKind effective, List<string> unknowns)
        => new(field.Signal, field.CanIdHex, field.IsExtended,
            field.Kind, effective, Array.Empty<NmReasonSegment>(),
            Array.Empty<NmReasonTransition>(), Array.Empty<double>(), unknowns);

    private sealed class SegmentBuilder(
        double start, ulong raw, bool startUnknown, byte[] firstBytes)
    {
        public double Start { get; } = start;
        public ulong Raw { get; } = raw;
        public bool StartUnknown { get; } = startUnknown;
        public byte[] FirstBytes { get; } = firstBytes;
        public double LastConfirmedT { get; set; } = start;
    }
}
```

`NmSignalSummary.cs` 末尾新增：

```csharp
/// <summary>原因分析覆盖的信号键：含扩展位，防止标准/扩展同 ID 串用。</summary>
public static string SkipKey(string canIdHex, bool isExtended, string signalName)
    => canIdHex + "|" + (isExtended ? "e" : "s") + "|" + signalName;
```

- [ ] **Step 4: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmReasonAnalyzerTests"
```
Expected: PASS（5 个用例）。

- [ ] **Step 5: 调用 code-reviewer 审阅本任务**

---

### Task 3: 跨 episode、不可读恢复、窗口边界与限流

**Files:**
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmReasonAnalyzer.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmReasonAnalyzerTests.cs`

**Interfaces:**
- Consumes: Task 2 的 `NmReasonAnalyzer` 与模型
- Produces: 同一入口；新增 `EndReason=EpisodeEnd` / `Unreadable` 行为与 `NmTruncation`

- [ ] **Step 1: 追加失败测试**

```csharp
[Fact]
public void SameValueAcrossEpisodes_StartsNewSegment()
{
    var frames = new[] { Frame(0.1, 0x30), Frame(0.6, 0x30), Frame(1.6, 0x30), Frame(2.0, 0x30) };
    var episodes = new[]
    {
        new NmEpisode(0, 0.1, 0.6, "nm", null, [CanId], false, false),
        new NmEpisode(1, 1.6, 2.0, "nm", null, [CanId], false, false),
    };
    var analysis = NmReasonAnalyzer.Analyze(
        Dict(), WakeupDbc(), frames, episodes, new NmWindow(0, 2.0, "test"));
    var timeline = Single(analysis);

    Assert.Equal(2, timeline.Segments.Count);
    Assert.Empty(timeline.Transitions);  // 值没变，不产事件
    Assert.Equal(NmReasonSegmentEnd.EpisodeEnd, timeline.Segments[0].EndReason);
    Assert.Equal(1.6, timeline.Segments[0].NextObservedAt!.Value);
    Assert.Equal(NmReasonSegmentEnd.WindowEnd, timeline.Segments[1].EndReason);
}

[Fact]
public void UnreadableBetweenReadableFrames_SplitsAtUnreadable()
{
    var readable = Frame(0.1, 0x30);
    var shortFrame = new NmFrame(
        new NmMessageDef(CanId, true, "BMS_NWM", "BMS", 2, true, ["test"]),
        new ReplayFrame(0.6, 0x98FFFAF4u, 2, new byte[] { 1, 0 },
            FrameFlags.None, IsExtended: true));
    var after = Frame(1.1, 0x30);
    var analysis = NmReasonAnalyzer.Analyze(
        Dict(), WakeupDbc(), [readable, shortFrame, after],
        OneEpisode(0.1, 1.1), new NmWindow(0.1, 1.1, "test"));
    var timeline = Single(analysis);

    Assert.Equal(2, timeline.Segments.Count);
    Assert.Equal(NmReasonSegmentEnd.Unreadable, timeline.Segments[0].EndReason);
    Assert.Equal(1.1, timeline.Segments[0].NextObservedAt!.Value);
    Assert.Contains(0.6, timeline.UnreadableFrameTimes);
    Assert.Empty(timeline.Transitions);
}

[Fact]
public void UnreadableAtEnd_IsWindowEnd()
{
    var readable = Frame(0.1, 0x30);
    var shortFrame = new NmFrame(
        new NmMessageDef(CanId, true, "BMS_NWM", "BMS", 2, true, ["test"]),
        new ReplayFrame(0.6, 0x98FFFAF4u, 2, new byte[] { 1, 0 },
            FrameFlags.None, IsExtended: true));
    var analysis = NmReasonAnalyzer.Analyze(
        Dict(), WakeupDbc(), [readable, shortFrame],
        OneEpisode(0.1, 0.6), new NmWindow(0.1, 0.6, "test"));
    var timeline = Single(analysis);
    Assert.Single(timeline.Segments);
    Assert.Equal(NmReasonSegmentEnd.WindowEnd, timeline.Segments[0].EndReason);
    Assert.True(timeline.Segments[0].EndUnknown);
}

[Fact]
public void ViewportWindow_FirstSegmentStartUnknown_LastEndUnknown()
{
    var frames = new[] { Frame(0.1, 0x00), Frame(0.5, 0x30), Frame(12.0, 0x30) };
    var analysis = NmReasonAnalyzer.Analyze(
        Dict(), WakeupDbc(), frames,
        OneEpisode(1.0, 12.0), new NmWindow(1.0, 12.0, "viewport"));
    var timeline = Single(analysis);

    // 窗口从 1.0 开始：0.1/0.5 的帧被过滤，首个可读帧是 12.0 的 0x30。
    Assert.Single(timeline.Segments);
    Assert.True(timeline.Segments[0].StartUnknown);
    Assert.True(timeline.Segments[0].EndUnknown);
}

[Fact]
public void SegmentCap_ReturnsTruncation()
{
    var frames = Enumerable.Range(0, 250)
        .Select(i => Frame(0.1 + i * 0.01, (byte)(i % 200)))
        .ToArray();
    var analysis = NmReasonAnalyzer.Analyze(
        Dict(), WakeupDbc(), frames, OneEpisode(0.1, 2.6),
        new NmWindow(0.1, 2.6, "test"));
    Assert.Contains(analysis.Truncations,
        t => t.Section.StartsWith("reason:segments:", StringComparison.Ordinal));
    Assert.Equal(200, Single(analysis).Segments.Count);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmReasonAnalyzerTests"
```
Expected: 新用例 FAIL（跨 episode / 不可读不在当前实现里）。

- [ ] **Step 3: 扩展分析器**

在 `AnalyzeField` 的逐帧循环里加入 episode 索引与不可读处理，替换 Task 2 的循环：

```csharp
var lastEpisode = -1;
var pendingUnreadable = false;

foreach (var frame in fieldFrames)
{
    var data = frame.Frame.Data;
    var t = frame.Frame.Timestamp;
    if (!Fits(data, signal))
    {
        if (current is not null) pendingUnreadable = true;
        unreadable.Add(t);
        continue;
    }

    var raw = SignalDecoder.DecodeRaw(data, signal);
    var episode = NmEpisodeBuilder.IndexAt(t, episodes);

    if (current is not null && raw != current.Raw)
    {
        transitions.Add(new NmReasonTransition(
            t, field.Signal, field.CanIdHex, field.IsExtended,
            current.Raw, raw, LabelsOf(current.Raw, labelsCache),
            LabelsOf(raw, labelsCache)));
    }

    if (current is not null)
    {
        // 优先级：中断 > episode 切换 > 同 episode 值变化。
        // 跨 episode 且值变化时，段尾仍是 EpisodeEnd（这不是同一 episode 内的切换），
        // 值变化由上面的 transition 单独记录。
        if (episode != lastEpisode && lastEpisode >= 0)
            Close(current.LastConfirmedT,
                pendingUnreadable ? NmReasonSegmentEnd.Unreadable : NmReasonSegmentEnd.EpisodeEnd, t);
        else if (pendingUnreadable)
            Close(current.LastConfirmedT, NmReasonSegmentEnd.Unreadable, t);
        else if (raw != current.Raw)
            Close(current.LastConfirmedT, NmReasonSegmentEnd.Changed, t);
    }

    if (current is null)
        current = new SegmentBuilder(t, raw, true, data.ToArray());

    current.LastConfirmedT = t;
    lastEpisode = episode;
    pendingUnreadable = false;
}

if (current is not null)
    Close(current.LastConfirmedT, NmReasonSegmentEnd.WindowEnd, null);
```

并把 `Close` 的 `labelsCache` 参数保持原样（它是本地函数）。

- [ ] **Step 4: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmReasonAnalyzerTests"
```
Expected: PASS（10 个用例）。

- [ ] **Step 5: 调用 code-reviewer 审阅本任务**

---

### Task 4: 标签来源、冲突与 bitmask 降级

**Files:**
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmReasonAnalyzer.cs`（仅 `BuildLabels` 已有，补测试与降级断言）
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmReasonAnalyzerTests.cs`

**Interfaces:**
- Consumes: Task 2/3
- Produces: `Labels` 可含 `dbc` 与 `dictionary` 两条；`DeclaredKind=Bitmask` 时 `EffectiveKind=Raw`

- [ ] **Step 1: 追加失败测试**

```csharp
[Fact]
public void DbcValueTable_ProvidesLabel()
{
    var analysis = NmReasonAnalyzer.Analyze(
        Dict(), WakeupDbc(), [Frame(0.1, 0x30)],
        OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
    var segment = Assert.Single(Single(analysis).Segments);
    var label = Assert.Single(segment.Labels);
    Assert.Equal("Waked up by NW PDU", label.Text);
    Assert.Equal("dbc", label.Source);
}

[Fact]
public void NoValueTable_LabelsEmpty()
{
    var analysis = NmReasonAnalyzer.Analyze(
        Dict(), WakeupDbc(valLine: ""), [Frame(0.1, 0x30)],
        OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
    Assert.Empty(Assert.Single(Single(analysis).Segments).Labels);
}

[Fact]
public void DictionaryEntry_IsAddedWhenDbcHasNoText()
{
    var dict = new NmReasonDictionary(1,
        [new NmReasonFieldDef("BMS_Wakeup_reasons", CanId, true,
            Entries: new Dictionary<long, string> { [48] = "NM PDU 唤醒" })]);
    var analysis = NmReasonAnalyzer.Analyze(
        dict, WakeupDbc(valLine: ""), [Frame(0.1, 0x30)],
        OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
    var label = Assert.Single(Assert.Single(Single(analysis).Segments).Labels);
    Assert.Equal("NM PDU 唤醒", label.Text);
    Assert.Equal("dictionary", label.Source);
}

[Fact]
public void ConflictingTexts_BothLabelsPreserved()
{
    var dict = new NmReasonDictionary(1,
        [new NmReasonFieldDef("BMS_Wakeup_reasons", CanId, true,
            Entries: new Dictionary<long, string> { [48] = "字典里的叫法" })]);
    var analysis = NmReasonAnalyzer.Analyze(
        dict, WakeupDbc(), [Frame(0.1, 0x30)],
        OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
    var labels = Assert.Single(Single(analysis).Segments).Labels;
    Assert.Equal(2, labels.Count);
    Assert.Contains(labels, l => l.Source == "dbc");
    Assert.Contains(labels, l => l.Source == "dictionary");
}

[Fact]
public void BitmaskDeclared_IsDegradedToRawWithUnknown()
{
    var dict = new NmReasonDictionary(1,
        [new NmReasonFieldDef("BMS_Wakeup_reasons", CanId, true,
            Kind: NmReasonKind.Bitmask)]);
    var analysis = NmReasonAnalyzer.Analyze(
        dict, WakeupDbc(), [Frame(0.1, 0x30)],
        OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
    var timeline = Single(analysis);
    Assert.Equal(NmReasonKind.Bitmask, timeline.DeclaredKind);
    Assert.Equal(NmReasonKind.Raw, timeline.EffectiveKind);
    Assert.Contains(timeline.Unknowns, u => u.Contains("位掩码"));
}

[Fact]
public void ValTableMatchesRawValue_NotPhysical()
{
    var dbc = DbcParser.Parse("""
VERSION ""
BO_ 2566912756 BMS_NWM: 8 BMS
 SG_ BMS_Wakeup_reasons : 16|8@1+ (2,10) [0|510] "bit" Vector__XXX
VAL_ 2566912756 BMS_Wakeup_reasons 48 "Waked up by NW PDU" ;
""").Value!;
    var analysis = NmReasonAnalyzer.Analyze(
        Dict(), dbc, [Frame(0.1, 0x30)],
        OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
    var label = Assert.Single(Assert.Single(Single(analysis).Segments).Labels);
    Assert.Equal("Waked up by NW PDU", label.Text);
}

[Fact]
public void SameSignalNameOnAnotherMessage_IsNotUsed()
{
    var dbc = DbcParser.Parse("""
VERSION ""
BO_ 2566912756 BMS_NWM: 8 BMS
 SG_ BMS_Wakeup_reasons : 16|8@1+ (1,0) [0|255] "bit" Vector__XXX
BO_ 2550136832 OTHER: 8 BMS
 SG_ BMS_Wakeup_reasons : 16|8@1+ (1,0) [0|255] "bit" Vector__XXX
VAL_ 2566912756 BMS_Wakeup_reasons 48 "Waked up by NW PDU" ;
VAL_ 2550136832 BMS_Wakeup_reasons 48 "OTHER TEXT" ;
""").Value!;
    var analysis = NmReasonAnalyzer.Analyze(
        Dict(), dbc, [Frame(0.1, 0x30)],
        OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
    var label = Assert.Single(Assert.Single(Single(analysis).Segments).Labels);
    Assert.Equal("Waked up by NW PDU", label.Text);
}

[Fact]
public void OutOfRangeEntry_DoesNotThrowAndDoesNotMatch()
{
    var dict = new NmReasonDictionary(1,
        [new NmReasonFieldDef("BMS_Wakeup_reasons", CanId, true,
            Entries: new Dictionary<long, string>
            {
                [-1] = "negative",
                [999] = "out of range",
            })]);
    var analysis = NmReasonAnalyzer.Analyze(
        dict, WakeupDbc(valLine: ""), [Frame(0.1, 0x30)],
        OneEpisode(0.1, 0.1), new NmWindow(0.1, 0.1, "test"));
    Assert.Empty(Assert.Single(Single(analysis).Segments).Labels);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmReasonAnalyzerTests"
```
Expected: `ConflictingTexts_BothLabelsPreserved` 与 `BitmaskDeclared...` 可能已通过（Task 2 已实现），其余通过。若全部已通过，说明 Task 2 提前覆盖，保留测试作为回归锁定，进入 Step 3。若失败，按下述实现补齐。

- [ ] **Step 3: 确认实现细节**

`BuildLabels` 必须满足：
- DBC 先查，字典后查；
- 文本相同则只保留 DBC 一条；
- 文本不同则两条都留；
- `raw > long.MaxValue` 直接返回空（值表键是 `long`）。

当前 Task 2 代码已满足；如测试暴露偏差，只改 `BuildLabels`，不要改切段逻辑。

- [ ] **Step 4: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmReasonAnalyzerTests"
```
Expected: PASS（18 个用例）。

- [ ] **Step 5: 调用 code-reviewer 审阅本任务**

---

### Task 5: 接入 NmAnalyzer、抑制 signal_change、引擎版本

**Files:**
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmAnalysisRequest.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmSignalSummary.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmAnalyzer.cs`
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmReport.cs`（新增 init 属性）
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmReasonIntegrationTests.cs`

**Interfaces:**
- Consumes: `NmReasonAnalyzer.Analyze`、`NmSignalSummary.SkipKey`
- Produces:
  - `NmAnalysisRequest` 新增 `LoadedNmReasonDictionary? Reasons = null` 与 `string? ReasonDictionaryNote = null`
  - `NmSignalSummary.Build(dbc, frames, maxEventsPerMessage, out truncation, IReadOnlySet<string>? skipSignals = null)`
  - `NmReport.ReasonTimelines` / `ReasonDictionaryPath` / `ReasonDictionaryHash`

- [ ] **Step 1: 写失败测试**

```csharp
using System.Text;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmReasonIntegrationTests
{
    [Fact]
    public void DeclaredField_ProducesTimeline_AndSuppressesSignalChange()
    {
        var dbc = DbcParser.Parse("""
VERSION ""
BO_ 2566912756 BMS_NWM: 8 BMS
 SG_ BMS_Address : 0|8@1+ (1,0) [0|255] "bit" Vector__XXX
 SG_ BMS_Wakeup_reasons : 16|8@1+ (1,0) [0|255] "bit" Vector__XXX
VAL_ 2566912756 BMS_Wakeup_reasons 0 "Default" 48 "Waked up by NW PDU" ;
""").Value!;
        // 不能依赖 NmProfileBuilder 自动识别 NM：测试 DBC 没有 MsgType 属性。
        // 显式构造带 IsNm 的 profile，避免“报文未被当作 NM 帧”导致空时间线。
        var profile = new NmProfile(
            NmProfileSource.Dbc,
            [new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, true, ["UserSelected"])],
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance());
        var frames = new[]
        {
            new ReplayFrame(0.1, 0x98FFFAF4u, 8,
                new byte[] { 1, 0, 0x00, 0, 0, 0, 0, 0 }, FrameFlags.None, IsExtended: true),
            new ReplayFrame(0.6, 0x98FFFAF4u, 8,
                new byte[] { 1, 0, 0x30, 0, 0, 0, 0, 0 }, FrameFlags.None, IsExtended: true),
        };
        var dictionary = new LoadedNmReasonDictionary(
            new NmReasonDictionary(1,
                [new NmReasonFieldDef("BMS_Wakeup_reasons", "0x18FFFAF4", true)]),
            "mem.nmreasons.json", "hash");

        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, dbc, profile, 0, 1, "test", Reasons: dictionary));

        Assert.Single(report.ReasonTimelines);
        Assert.Equal("mem.nmreasons.json", report.ReasonDictionaryPath);
        Assert.Equal("hash", report.ReasonDictionaryHash);
        Assert.DoesNotContain(report.Events,
            e => e.Type == "signal_change" && e.Detail.Contains("BMS_Wakeup_reasons"));
        Assert.Contains(report.Assumptions, a => a.Contains("不代表根因"));
    }

    [Fact]
    public void NoDictionary_KeepsExistingSignalChange()
    {
        var dbc = DbcParser.Parse("""
VERSION ""
BO_ 2566912756 BMS_NWM: 8 BMS
 SG_ BMS_Wakeup_reasons : 16|8@1+ (1,0) [0|255] "bit" Vector__XXX
VAL_ 2566912756 BMS_Wakeup_reasons 0 "Default" 48 "Waked up by NW PDU" ;
""").Value!;
        var profile = new NmProfile(
            NmProfileSource.Dbc,
            [new NmMessageDef("0x18FFFAF4", true, "BMS_NWM", "BMS", 8, true, ["UserSelected"])],
            new NmTimings(CycleMs: 500, TimeoutMs: 2000),
            Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
            Array.Empty<string>(), new NmProvenance());
        var frames = new[]
        {
            new ReplayFrame(0.1, 0x98FFFAF4u, 8,
                new byte[] { 1, 0, 0x00, 0, 0, 0, 0, 0 }, FrameFlags.None, IsExtended: true),
            new ReplayFrame(0.6, 0x98FFFAF4u, 8,
                new byte[] { 1, 0, 0x30, 0, 0, 0, 0, 0 }, FrameFlags.None, IsExtended: true),
        };
        var report = NmAnalyzer.Analyze(new NmAnalysisRequest(
            "src", null, frames, dbc, profile, 0, 1, "test"));

        Assert.Empty(report.ReasonTimelines);
        Assert.Contains(report.Events,
            e => e.Type == "signal_change" && e.Detail.Contains("BMS_Wakeup_reasons"));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmReasonIntegrationTests"
```
Expected: FAIL，`Reasons` 参数 / `ReasonTimelines` 不存在。

- [ ] **Step 3: 扩展请求、信号摘要与分析器**

`NmReport.cs` 末尾（在现有 init 属性之后）加：

```csharp
public IReadOnlyList<NmReasonTimeline> ReasonTimelines { get; init; }
    = Array.Empty<NmReasonTimeline>();
public string? ReasonDictionaryPath { get; init; }
public string? ReasonDictionaryHash { get; init; }
```

`NmAnalysisRequest.cs` 末尾加：

```csharp
    // 原因字典：null 表示未配置。Note 非空用于报告 Unknowns（加载失败的明确说明）。
    LoadedNmReasonDictionary? Reasons = null,
    string? ReasonDictionaryNote = null);
```

`NmSignalSummary.cs` 的四参重载改为：

```csharp
public static IReadOnlyList<NmEvent> Build(
    DbcDocument dbc,
    IReadOnlyList<NmFrame> frames,
    int maxEventsPerMessage,
    out NmTruncation? truncation,
    IReadOnlySet<string>? skipSignals = null)
{
    // ...原实现...
    foreach (var signal in message.Signals)
    {
        if (skipSignals?.Contains(SkipKey(canId, group.Key.IsExtended, signal.Name)) == true)
            continue;
        // ...原解码逻辑...
    }
}
```

`NmAnalyzer.cs`：

1. `EngineVersion` 改为 `"nm-m1-2"`。

2. **变量声明位置**：`Finish` 是定义在约 145 行的本地函数，会捕获 `reason`；因此 `reason` 必须在 `IReadOnlyList<NmEpisode> episodes = Array.Empty<NmEpisode>();`（约 131 行）旁先声明，不能在信号事件附近才声明：

```csharp
var reason = new NmReasonAnalysis(
    Array.Empty<NmReasonTimeline>(),
    Array.Empty<NmTruncation>(),
    new HashSet<string>(StringComparer.Ordinal));
```

3. **计算位置**：紧接 `episodes = NmEpisodeBuilder.Build(...)` 之后、信号事件构建之前：

```csharp
if (request.Dbc is null)
{
    if (request.Reasons is not null)
        unknowns.Add("原因字典已加载，但未加载 DBC，无法解析信号");
}
else if (request.Reasons is not null)
{
    reason = NmReasonAnalyzer.Analyze(
        request.Reasons.Dictionary, request.Dbc, nmFrames, episodes, window);
    truncations.AddRange(reason.Truncations);
    assumptions.Add(
        "原因解释来自 DBC 值表或项目字典，是 BMS 自报字段的翻译；与网络行为只表达时间关系，不代表根因。");
}

if (request.ReasonDictionaryNote is { Length: > 0 } note)
    unknowns.Add(note);
```

4. 传给 `NmSignalSummary.Build`：

```csharp
var signalEvents = NmSignalSummary.Build(
    request.Dbc, group.ToArray(), MaxEventsPerMessage, out var truncation,
    skipSignals: reason.CoveredSignals);
```

5. 报告构造末尾的 init 块追加：

```csharp
ReasonTimelines = reason.Timelines,
ReasonDictionaryPath = request.Reasons?.Path,
ReasonDictionaryHash = request.Reasons?.Sha256,
```

- [ ] **Step 4: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmReasonIntegrationTests"
```
Expected: PASS（2 个用例）。

同时跑现有 NM 分析测试确认无回归：
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmAnalyzerTests"
```
Expected: 全部 PASS。若因 EngineVersion 文案变化的既有断言失败，按测试意图更新断言（不是放宽判据）。

- [ ] **Step 5: 调用 code-reviewer 审阅本任务**

---

### Task 6: sidecar 字段、缓存键与 App 加载入口

**Files:**
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmProfile.cs`（`NmUserSupplement` 末尾加 `ReasonsPath`）
- Modify: `src/PeakCan.Host.Core/Analysis/Nm/NmReportCache.cs`（`NmCacheKey` init 属性）
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmFlow.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/NmInputWizardViewModel.cs:553-570`（`ToSupplement` 保留字段）
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmCacheKeyTests.cs`、`tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmSidecarReasonsPathTests.cs`
- 说明：`ToSupplement()` 的 `ReasonsPath` 透传是纯字段赋值，由 code-reviewer 核对，不为它单独造夹具

**Interfaces:**
- Consumes: Task 5 的请求参数
- Produces:
  - `NmUserSupplement.ReasonsPath`
  - `NmCacheKey.ReasonDictionaryHash { get; init; } = "none"`
  - App 加载函数 `LoadReasonDictionary(string? dbcPath, string? reasonsPath)`（放在 `NmFlow.cs` 内的私有静态方法）

- [ ] **Step 1: 写失败测试**

`NmCacheKeyTests.cs` 追加：

```csharp
[Fact]
public void ReasonDictionaryHashChange_MissesCache()
{
    var a = Key() with { ReasonDictionaryHash = "dict-a" };
    var b = Key() with { ReasonDictionaryHash = "dict-b" };
    Assert.NotEqual(a, b);
}

[Fact]
public void DefaultReasonDictionaryHash_IsNone()
{
    Assert.Equal("none", Key().ReasonDictionaryHash);
}
```

`NmSidecarReasonsPathTests.cs`（新建）：

```csharp
using PeakCan.Host.Core.Analysis.Nm;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmSidecarReasonsPathTests
{
    [Fact]
    public void Sidecar_RoundTripsReasonsPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.nmprofile.json");
        try
        {
            NmSidecarStore.SaveTo(path, new NmUserSupplement(
                ReasonsPath: "reasons.nmreasons.json"));
            var loaded = NmSidecarStore.LoadFrom(path);
            Assert.NotNull(loaded);
            Assert.Equal("reasons.nmreasons.json", loaded!.ReasonsPath);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmCacheKeyTests|FullyQualifiedName~NmSidecarReasonsPathTests"
```
Expected: FAIL（`ReasonDictionaryHash` / `ReasonsPath` 未定义）。

- [ ] **Step 3: 实现字段与接线**

`NmProfile.cs`：

```csharp
public sealed record NmUserSupplement(
    // ...既有字段...
    int ProfileVersion = 1,
    // 独立原因字典的显式路径（相对 nmprofile 目录或绝对路径）；null = 未配置。
    string? ReasonsPath = null);
```

`NmReportCache.cs`：

```csharp
public sealed record NmCacheKey(
    // ...既有位置参数...
    string EngineVersion)
{
    /// <summary>原因字典内容哈希；无字典为 "none"。init 属性避免破坏既有构造点。</summary>
    public string ReasonDictionaryHash { get; init; } = "none";
}
```

`NmFlow.cs` 在 supplement 加载之后：

```csharp
var reasonLoad = LoadReasonDictionary(dbcPath, supplement?.ReasonsPath);
```

并在文件内加：

```csharp
/// <summary>
/// 显式加载原因字典。路径为空返回 (null, null)；加载失败返回错误说明，不抛到调用方。
/// </summary>
private static (LoadedNmReasonDictionary? Dictionary, string? Note) LoadReasonDictionary(
    string? dbcPath, string? reasonsPath)
{
    if (string.IsNullOrWhiteSpace(reasonsPath)) return (null, null);
    if (dbcPath is null) return (null, "原因字典已配置，但没有 DBC 路径无法解析相对路径");
    var full = NmReasonDictionaryStore.ResolvePath(dbcPath, reasonsPath);
    try
    {
        return (NmReasonDictionaryStore.LoadFrom(full), null);
    }
    catch (NmReasonDictionaryException ex)
    {
        return (null, "原因字典不可用：" + ex.Message);
    }
}
```

缓存键构造改为：

```csharp
var key = new NmCacheKey(
    sourceId, query.Channel, start, end, windowRef,
    frames.Count, traceLoadVersion,
    string.Concat(
        NmCanonicalProfileHash.Compute(profile, standardId), ":",
        NmDbcSignalHash.Compute(dbc)),
    NmAnalyzer.EngineVersion)
{
    ReasonDictionaryHash = reasonLoad.Dictionary?.Sha256 ?? "none",
};
```

请求构造改为：

```csharp
var request = new NmAnalysisRequest(
    sourceId, query.Channel, frames, dbc, profile, start, end, windowRef,
    Standard: selected, Reasons: reasonLoad.Dictionary,
    ReasonDictionaryNote: reasonLoad.Note);
```

`NmInputWizardViewModel.cs`：

1. `ToSupplement()` 末尾加：

```csharp
ReasonsPath: _existing?.ReasonsPath);
```

2. 试算路径在 `_snapshot.DbcPath` 可用时加载：

```csharp
var reasonLoad = LoadReasonDictionaryForTrial();
...
var report = await Task.Run(() => NmAnalyzer.Analyze(new NmAnalysisRequest(
    _snapshot.SourceId, _snapshot.Channel, _snapshot.Frames,
    dbc, profile, _snapshot.Report.Window.Start, end, "trial",
    Reasons: reasonLoad.Dictionary, ReasonDictionaryNote: reasonLoad.Note)));
```

并新增：

```csharp
private (LoadedNmReasonDictionary? Dictionary, string? Note) LoadReasonDictionaryForTrial()
{
    if (_snapshot.DbcPath is null || _existing?.ReasonsPath is not { Length: > 0 } reasonsPath)
        return (null, null);
    var full = NmReasonDictionaryStore.ResolvePath(_snapshot.DbcPath, reasonsPath);
    try
    {
        return (NmReasonDictionaryStore.LoadFrom(full), null);
    }
    catch (NmReasonDictionaryException ex)
    {
        return (null, "原因字典不可用：" + ex.Message);
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmCacheKeyTests|FullyQualifiedName~NmSidecarReasonsPathTests"
```
Expected: PASS。

- [ ] **Step 5: 调用 code-reviewer 审阅本任务**

---

### Task 7: 时间轴原因行

**Files:**
- Modify: `src/PeakCan.Host.App/Converters/NmSegmentBrushConverter.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmPanelFlow.cs:447-491`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/NmPanelFlowTests.cs`、`tests/PeakCan.Host.App.Tests/Converters/NmSegmentBrushConverterTests.cs`

**Interfaces:**
- Consumes: `NmReport.ReasonTimelines`
- Produces: `NmTimelineKinds.Reason`、`BuildReasonTimelineRows`、`ReasonBrush`

- [ ] **Step 1: 写失败测试**

`NmSegmentBrushConverterTests.cs` 追加（沿用它现有对 `NmTimelineKinds.All` 的覆盖断言）：

```csharp
[Fact]
public void ReasonKind_HasDistinctBrushFromUnknown()
{
    Assert.NotEqual(NmSegmentBrushConverter.UnknownBrush,
        NmSegmentBrushConverter.BrushFor(NmTimelineKinds.Reason));
}
```

`NmPanelFlowTests.cs` 追加：

```csharp
[Fact]
public void BuildTimelineRows_AppendsReasonRowsBeforeNetworkRow()
{
    var report = ReportWithReasons();
    var rows = NmPanelFlow.BuildTimelineRows(report);
    Assert.Contains(rows, r => r.Label.Contains("原因 BMS_Wakeup_reasons", StringComparison.Ordinal));
}

[Fact]
public void BuildTimelineRows_NoDictionary_HasNoReasonRows()
{
    var report = ReportWithReasons(reasons: []);
    Assert.DoesNotContain(NmPanelFlow.BuildTimelineRows(report),
        r => r.Label.StartsWith("原因 ", StringComparison.Ordinal));
}
```

`ReportWithReasons` helper（放在测试类内）：

```csharp
private static NmReport ReportWithReasons(
    IReadOnlyList<NmReasonTimeline>? reasons = null)
{
    var reason = new NmReasonTimeline(
        "BMS_Wakeup_reasons", "0x18FFFAF4", true,
        NmReasonKind.Raw, NmReasonKind.Raw,
        [new NmReasonSegment(0.2, 0.2, 0x30,
            [new NmReasonLabel("Waked up by NW PDU", "dbc")],
            false, false, NmReasonSegmentEnd.WindowEnd, null, [1, 0, 0x30])],
        [], [], []);
    return new NmReport(
        new NmWindow(0, 30, "test"), "src", null,
        [], [], [], [], [], [], [], [], [], [],
        EngineVersion: NmAnalyzer.EngineVersion)
    {
        ReasonTimelines = reasons ?? [reason],
    };
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~NmPanelFlowTests|FullyQualifiedName~NmSegmentBrushConverterTests"
```
Expected: FAIL（`Reason` / 原因行不存在）。

- [ ] **Step 3: 实现时间轴行**

`NmSegmentBrushConverter.cs`：
- `NmTimelineKinds` 加 `public const string Reason = "reason";` 并加入 `All`。
- 加笔刷：

```csharp
/// <summary>原因区间：深棕，与字节/位/信号等既有标记色相区分。</summary>
public static readonly SolidColorBrush ReasonBrush = Create(0xFF, 0x6D, 0x4C, 0x41);
```
- `BrushFor` 加 `NmTimelineKinds.Reason => ReasonBrush,`。
- `NmTimelineText.SegmentKind` 加 `NmTimelineKinds.Reason => "原因"`。

`NmPanelFlow.cs`：

```csharp
public static IReadOnlyList<NmTimelineRowVm> BuildReasonTimelineRows(NmReport report)
{
    var rows = new List<NmTimelineRowVm>(report.ReasonTimelines.Count);
    foreach (var timeline in report.ReasonTimelines)
    {
        var segments = new List<NmTimelineSegmentVm>(timeline.Segments.Count);
        foreach (var s in timeline.Segments)
        {
            var visualEnd = s.EndReason == NmReasonSegmentEnd.WindowEnd
                ? s.End
                : s.NextObservedAt ?? s.End;
            var left = ToX(s.Start, report.Window);
            var right = ToX(visualEnd, report.Window);
            var label = s.Labels.Count == 0
                ? $"含义未配置（{FormatReasonRaw(s.RawValue)}）"
                : string.Join(" / ", s.Labels.Select(l => l.Text));
            var duration = s.StartUnknown || s.EndReason != NmReasonSegmentEnd.Changed
                ? string.Create(CultureInfo.InvariantCulture, $"≥ {s.End - s.Start:F3}s")
                : string.Create(CultureInfo.InvariantCulture, $"观测区间 {s.End - s.Start:F3}s");
            var tooltip = string.Create(CultureInfo.InvariantCulture,
                $"{label}  {s.Start:F3}s – {visualEnd:F3}s  时长 {duration}  原始值 {FormatReasonRaw(s.RawValue)}");
            segments.Add(new NmTimelineSegmentVm(
                left, Math.Max(right - left, 1), NmTimelineKinds.Reason,
                s.Start, visualEnd, tooltip, "measured", false, false));
        }
        rows.Add(new NmTimelineRowVm($"原因 {timeline.Signal}", segments));
    }
    return rows;
}

private static string FormatReasonRaw(ulong raw)
    => "0x" + raw.ToString("X", CultureInfo.InvariantCulture);
```

`BuildTimelineRows` 在 `rows.Add(network)` 之前插入：

```csharp
rows.AddRange(BuildReasonTimelineRows(report));
if (BuildNetworkTimelineRow(report) is { } network)
    rows.Add(network);
```

XAML 提示：`TraceViewerViewNmPanel.xaml` 中 `Grid.Row="0"` 的 `ScrollViewer` 内 `<StackPanel>`（约 42 行开始）的最后一个子元素之后加：

```xml
<TextBlock Text="{Binding NmReasonHint}" FontSize="10" Foreground="#666"
           Margin="0,2,0,0" TextWrapping="Wrap" />
```
`NmPanelFlow` 新增属性，并在 `ApplyNmReport` 中 `ApplyNmCalibration(report, profile)` 之后设置：

```csharp
[ObservableProperty] private string _nmReasonHint = "";

NmReasonHint = report.ReasonTimelines.Count > 0
    ? ""
    : report.ReasonDictionaryPath is null
        ? "原因字典未加载（可在 nmprofile.json 配置 reasons_path）"
        : "原因字典已配置，但未产生可用时间线";
```

- [ ] **Step 4: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~NmPanelFlowTests|FullyQualifiedName~NmSegmentBrushConverterTests"
```
Expected: PASS。

- [ ] **Step 5: 调用 code-reviewer 审阅本任务**

---

### Task 8: 事件表原因行、形状与跳转

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/NmPanelFlow.cs:1083-1100`
- Modify: `src/PeakCan.Host.App/Converters/NmEventShapeConverter.cs:26-33`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/NmPanelFlowTests.cs`

**Interfaces:**
- Consumes: `NmReasonTransition`
- Produces: `NmEventRowVm` 行（Type=`reason_change`）、`MarkerKind("reason_change")`、`ShapeFor(NmTimelineKinds.Reason)`

- [ ] **Step 1: 写失败测试**

测试文件需确保 `using PeakCan.Host.App.Converters;`（`NmTimelineKinds` / `NmEventShapeConverter`）与 `using PeakCan.Host.Core.Analysis.Nm;` 已存在。

```csharp
[Fact]
public void ReasonTransitions_ProduceEventRows()
{
    var report = MakeReport() with { ReasonTimelines = ReasonTimelinesForTest() };
    var vm = NewVm();
    vm.ApplyNmReport(MakeSnapshot(report, MakeProfile()));
    vm.NmEventRows.Should().ContainSingle(r => r.Type == "reason_change")
        .Which.Detail.Should().Contain("0x30");
}

[Fact]
public void ReasonChangeShape_IsNotUnknown()
{
    Assert.NotEqual(NmEventShapeConverter.UnknownShape,
        NmEventShapeConverter.ShapeFor(NmTimelineKinds.Reason));
}

private static IReadOnlyList<NmReasonTimeline> ReasonTimelinesForTest() =>
[
    new NmReasonTimeline(
        "BMS_Wakeup_reasons", "0x18FFFAF4", true,
        NmReasonKind.Raw, NmReasonKind.Raw,
        [new NmReasonSegment(0.2, 0.2, 0x30,
            [new NmReasonLabel("Waked up by NW PDU", "dbc")],
            false, false, NmReasonSegmentEnd.WindowEnd, null, [1, 0, 0x30])],
        [new NmReasonTransition(0.6, "BMS_Wakeup_reasons", "0x18FFFAF4", true,
            0x00, 0x30, [], [new NmReasonLabel("Waked up by NW PDU", "dbc")])],
        [], []),
];
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~NmPanelFlowTests"
```
Expected: FAIL（无 `reason_change` 行 / 形状为 Unknown）。

- [ ] **Step 3: 实现**

`NmPanelFlow.cs` 在 `_allNmEventRows` 的现有循环之后：

```csharp
foreach (var timeline in report.ReasonTimelines)
{
    foreach (var tr in timeline.Transitions)
    {
        var label = tr.NewLabels.Count == 0
            ? "含义未配置"
            : string.Join(" / ", tr.NewLabels.Select(l => l.Text));
        var subject = tr.CanIdHex;
        var subjectLabel = displayByCanId.TryGetValue(
            (tr.CanIdHex, tr.IsExtended), out var display)
            ? display
            : tr.CanIdHex;
        _allNmEventRows.Add(new NmEventRowVm(
            tr.T, TraceTimeFormatter.Format(tr.T, null), "reason_change",
            subject,
            string.Create(CultureInfo.InvariantCulture,
                $"{tr.Signal}: 0x{tr.OldRaw:X} → 0x{tr.NewRaw:X}（{label}）"),
            "measured",
            NmEpisodeBuilder.IndexAt(tr.T, report.Episodes),
            tr.CanIdHex)
        {
            SubjectLabel = subjectLabel,
        });
    }
}
```

`NmTimelineText.EventType` 加：

```csharp
"reason_change" => "原因变化",
```

`MarkerKind` 加：

```csharp
"reason_change" => NmTimelineKinds.Reason,
```

`NmEventShapeConverter` 加：

```csharp
public static readonly Geometry ReasonShape =
    Freeze(Geometry.Parse("M 2.25,1 L 7.5,2.25 L 6.75,7.5 L 1.5,6.75 Z"));

// ShapeFor:
NmTimelineKinds.Reason => ReasonShape,
```

- [ ] **Step 4: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~NmPanelFlowTests"
```
Expected: PASS。

- [ ] **Step 5: 调用 code-reviewer 审阅本任务**

---

### Task 9: Markdown / CSV 导出

**Files:**
- Modify: `src/PeakCan.Host.App/Services/Nm/NmReportWriter.cs:208-245`
- Test: `tests/PeakCan.Host.App.Tests/Services/Nm/NmReportWriterTests.cs`

**Interfaces:**
- Consumes: `NmReport.ReasonTimelines`
- Produces: `## Reason Timelines` Markdown 段；CSV 末尾追加独立 `# Reason` 段，现有首行表头不变

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Markdown_ContainsReasonTimelinesAndSources()
{
    var md = NmReportWriter.WriteMarkdown(SnapshotWithReason());
    Assert.Contains("## Reason Timelines", md);
    Assert.Contains("reasons_path", md);
    Assert.Contains("reason_dict_hash", md);
    Assert.Contains("Waked up by NW PDU", md);
    Assert.Contains("| dbc |", md);
}

[Fact]
public void Csv_KeepsExistingHeaderAndAppendsReasonSection()
{
    var csv = NmReportWriter.WriteCsv(SnapshotWithReason().Report);
    var firstLine = csv.Split("\r\n")[0];
    Assert.Equal("time,type,subject,detail,evidence_kind,episode_index", firstLine);
    Assert.Contains("# Reason", csv);
    Assert.Contains("BMS_Wakeup_reasons,0x18FFFAF4", csv);
}
```

`SnapshotWithReason` helper（放在 `NmReportWriterTests` 类内）：

```csharp
private static NmToolSnapshot SnapshotWithReason()
{
    var profile = new NmProfile(
        NmProfileSource.Dbc, Array.Empty<NmMessageDef>(), new NmTimings(),
        Array.Empty<NmNodeDef>(), Array.Empty<NmConfigConflict>(),
        Array.Empty<string>(), new NmProvenance());
    var report = Report() with
    {
        ReasonTimelines =
        [
            new NmReasonTimeline(
                "BMS_Wakeup_reasons", "0x18FFFAF4", true,
                NmReasonKind.Raw, NmReasonKind.Raw,
                [new NmReasonSegment(0.2, 0.2, 0x30,
                    [new NmReasonLabel("Waked up by NW PDU", "dbc")],
                    false, false, NmReasonSegmentEnd.WindowEnd, null, [1, 0, 0x30])],
                [], [], []),
        ],
        ReasonDictionaryPath = "reasons.nmreasons.json",
        ReasonDictionaryHash = "dict-hash",
    };
    return new NmToolSnapshot("src", null, "C:/nets/net.dbc", "hash", profile, report,
        Array.Empty<PeakCan.Host.Core.Replay.ReplayFrame>());
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~NmReportWriterTests"
```
Expected: FAIL。

- [ ] **Step 3: 实现导出**

`WriteMarkdown` 在 `foreach (var e in r.Events)` 结束后、下一个 `## ...` 段之前插入：

```csharp
sb.AppendLine();
sb.AppendLine("## Reason Timelines");
sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
    $"- reasons_path: `{(redact ? "（已脱敏）" : r.ReasonDictionaryPath ?? "—")}`"));
sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
    $"- reason_dict_hash: `{r.ReasonDictionaryHash ?? "—"}`"));
sb.AppendLine("| signal | can_id | start | end | observed | raw | labels | source | completeness |");
sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
foreach (var timeline in r.ReasonTimelines)
{
    foreach (var s in timeline.Segments)
    {
        var labels = s.Labels.Count == 0
            ? "含义未配置"
            : string.Join(" / ", s.Labels.Select(l => Esc(red.Apply(l.Text))));
        var sources = s.Labels.Count == 0
            ? "none"
            : string.Join("/", s.Labels.Select(l => l.Source));
        var completeness = s.EndUnknown || s.StartUnknown ? "≥" : "观测区间";
        var next = s.NextObservedAt is { } n
            ? $"下次观测 {n:F3}s"
            : "之后无可观测帧";
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"| {timeline.Signal} | {timeline.CanIdHex} | {s.Start:F3} | {s.End:F3} | {s.End - s.Start:F3} | 0x{s.RawValue:X} | {labels} | {sources} | {completeness}（{next}） |"));
    }
}
sb.AppendLine();
sb.AppendLine("Unknowns:");
foreach (var timeline in r.ReasonTimelines)
    foreach (var u in timeline.Unknowns)
        sb.AppendLine("- " + red.Apply(u));
```

`WriteCsv` 在 `foreach (var e in report.Events)` 结束后、`return sb.ToString();` 之前追加：

```csharp
sb.Append("\r\n\r\n# Reason\r\n");
sb.Append("signal,can_id,start,end,observed,raw,labels,sources,completeness\r\n");
foreach (var timeline in report.ReasonTimelines)
{
    foreach (var s in timeline.Segments)
    {
        var labels = s.Labels.Count == 0
            ? "含义未配置"
            : string.Join(" / ", s.Labels.Select(l => red.Apply(l.Text)));
        var sources = s.Labels.Count == 0
            ? "none"
            : string.Join("/", s.Labels.Select(l => l.Source));
        sb.Append(Csv(timeline.Signal)).Append(',')
          .Append(Csv(timeline.CanIdHex)).Append(',')
          .Append(s.Start.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
          .Append(s.End.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
          .Append((s.End - s.Start).ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
          .Append("0x").Append(s.RawValue.ToString("X", CultureInfo.InvariantCulture)).Append(',')
          .Append(Csv(labels)).Append(',')
          .Append(Csv(sources)).Append(',')
          .Append(s.EndUnknown || s.StartUnknown ? ">=" : "observed").Append("\r\n");
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~NmReportWriterTests"
```
Expected: PASS。

- [ ] **Step 5: 调用 code-reviewer 审阅本任务**

---

### Task 10: 真实数据回归、无值表降级与反向验证

**Files:**
- Create: `tests/PeakCan.Host.Core.Tests/TestData/Nm/nm16.blf`（从 `C:\Users\13777\Desktop\can_log\微卡26商改网络测试NM_16测试Log.blf` 复制）
- Create: `tests/PeakCan.Host.Core.Tests/TestData/Nm/bms.dbc`（从 `C:\Users\13777\Desktop\can_log\LCV&微卡平台_BMS通信矩阵_20260706.dbc` 复制）
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Nm/NmReasonRealTraceTests.cs`

**Interfaces:**
- Consumes: Task 1–5 全部
- Produces: 真实数据验收 + 反向验证记录

- [ ] **Step 1: 复制数据并锁定来源**

```bash
mkdir -p tests/PeakCan.Host.Core.Tests/TestData/Nm
cp "/c/Users/13777/Desktop/can_log/微卡26商改网络测试NM_16测试Log.blf" tests/PeakCan.Host.Core.Tests/TestData/Nm/nm16.blf
cp "/c/Users/13777/Desktop/can_log/LCV&微卡平台_BMS通信矩阵_20260706.dbc" tests/PeakCan.Host.Core.Tests/TestData/Nm/bms.dbc
sha256sum tests/PeakCan.Host.Core.Tests/TestData/Nm/bms.dbc
```
Expected: `23d121d65b103a1a4ea5cde027b6124649e23a92d52d55715e6f4a8e4ae7d43f`。

- [ ] **Step 2: 写真实数据测试**

```csharp
using System.Security.Cryptography;
using System.Text;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Nm;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Nm;

public class NmReasonRealTraceTests
{
    private const string DbcSha =
        "23d121d65b103a1a4ea5cde027b6124649e23a92d52d55715e6f4a8e4ae7d43f";

    static NmReasonRealTraceTests()
        => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static string DataDir()
        => Path.Combine(AppContext.BaseDirectory, "TestData", "Nm");

    private static (DbcDocument Dbc, IReadOnlyList<ReplayFrame> Frames) Load(bool keepVal)
    {
        var dbcBytes = File.ReadAllBytes(Path.Combine(DataDir(), "bms.dbc"));
        Assert.Equal(DbcSha, Convert.ToHexString(SHA256.HashData(dbcBytes)).ToLowerInvariant());

        var text = Encoding.GetEncoding("GBK").GetString(dbcBytes);
        if (!keepVal)
        {
            var lines = text.Split('\n')
                .Where(l => !l.StartsWith("VAL_ 2566912756 BMS_Wakeup_reasons", StringComparison.Ordinal)
                         && !l.StartsWith("VAL_ 2566912756 BMS_Stayawake_reasons", StringComparison.Ordinal));
            text = string.Join('\n', lines);
        }
        var dbc = DbcParser.Parse(text).Value!;

        using var fs = File.OpenRead(Path.Combine(DataDir(), "nm16.blf"));
        var frames = BlfParser.ParseAsync(fs, ReplayOptions.Default)
            .GetAwaiter().GetResult();
        return (dbc, frames);
    }

    private static NmReport Analyze(bool keepVal)
    {
        var (dbc, frames) = Load(keepVal);
        var profile = NmProfileBuilder.Build(dbc, null);
        var loaded = new LoadedNmReasonDictionary(
            new NmReasonDictionary(1,
            [
                new NmReasonFieldDef("BMS_Wakeup_reasons", "0x18FFFAF4", true),
                new NmReasonFieldDef("BMS_Stayawake_reasons", "0x18FFFAF4", true),
            ]),
            "mem.nmreasons.json", "hash");
        return NmAnalyzer.Analyze(new NmAnalysisRequest(
            "real", null, frames, dbc, profile, 0, frames[^1].Timestamp + 1e-9,
            "full", Reasons: loaded));
    }

    [Fact]
    public void RealTrace_WakeupAndStayawakeSegmentsMatchProbe()
    {
        var report = Analyze(keepVal: true);
        var wakeup = report.ReasonTimelines.Single(t => t.Signal == "BMS_Wakeup_reasons");
        var stay = report.ReasonTimelines.Single(t => t.Signal == "BMS_Stayawake_reasons");

        Assert.Collection(wakeup.Segments,
            s => { Assert.Equal((0.095, 0.095), (Math.Round(s.Start, 3), Math.Round(s.End, 3))); Assert.Equal(0UL, s.RawValue); Assert.Equal(0.609, s.NextObservedAt!.Value); Assert.Equal(NmReasonSegmentEnd.Changed, s.EndReason); },
            s => { Assert.Equal(0.609, Math.Round(s.Start, 3)); Assert.Equal(9.714, Math.Round(s.End, 3)); Assert.Equal(48UL, s.RawValue); Assert.Equal(NmReasonSegmentEnd.EpisodeEnd, s.EndReason); Assert.Equal(12.677, s.NextObservedAt!.Value); Assert.Contains(s.Labels, l => l.Text == "Waked up by NW PDU" && l.Source == "dbc"); },
            s => { Assert.Equal(12.677, Math.Round(s.Start, 3)); Assert.Equal(30.536, Math.Round(s.End, 3)); Assert.Equal(NmReasonSegmentEnd.WindowEnd, s.EndReason); Assert.True(s.EndUnknown); });

        Assert.Collection(stay.Segments,
            s => { Assert.Equal(0.095, Math.Round(s.Start, 3)); Assert.Equal(9.714, Math.Round(s.End, 3)); Assert.Equal(1UL, s.RawValue); Assert.Equal(NmReasonSegmentEnd.EpisodeEnd, s.EndReason); },
            s => { Assert.Equal(12.677, Math.Round(s.Start, 3)); Assert.Equal(19.826, Math.Round(s.End, 3)); Assert.Equal(2UL, s.RawValue); Assert.Equal(NmReasonSegmentEnd.Changed, s.EndReason); Assert.Equal(20.363, s.NextObservedAt!.Value); },
            s => { Assert.Equal(20.363, Math.Round(s.Start, 3)); Assert.Equal(30.536, Math.Round(s.End, 3)); Assert.Equal(1UL, s.RawValue); Assert.Equal(NmReasonSegmentEnd.WindowEnd, s.EndReason); });
    }

    [Fact]
    public void RealTrace_WithoutValueTables_LabelsAreEmpty()
    {
        var report = Analyze(keepVal: false);
        var wakeup = report.ReasonTimelines.Single(t => t.Signal == "BMS_Wakeup_reasons");
        Assert.All(wakeup.Segments, s => Assert.Empty(s.Labels));
        Assert.True(wakeup.Segments.Any(s => s.RawValue == 48UL));
    }
}
```

- [ ] **Step 3: 跑真实数据测试**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmReasonRealTraceTests"
```
Expected: PASS（2 个用例）。若 App 测试项目没有把 TestData 复制到输出目录，在 `PeakCan.Host.Core.Tests.csproj` 加：

```xml
<ItemGroup>
  <Content Include="TestData\Nm\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

并在同一 `PeakCan.Host.Core.Tests.csproj` 加编码包（版本由 `Directory.Packages.props` 统一管理）：

```xml
<ItemGroup>
  <PackageReference Include="System.Text.Encoding.CodePages" />
</ItemGroup>
```

- [ ] **Step 4: 反向验证（三处，逐一执行后恢复）**

1. 注释掉 `NmReasonAnalyzer` 中跨 episode 的 `EpisodeEnd` 分支 → `SameValueAcrossEpisodes_StartsNewSegment` 与真实数据 `Stayawake` 段数断言必须变红。
2. 把 `Fits` 改成 `return true` → `DlcTooShort_IsUnreadableNotZero` 与真实数据无关断言必须变红（会产生假 0 值）。
3. 把 `NmSignalSummary.Build` 的 `skipSignals` 传 `null` → `DeclaredField_ProducesTimeline_AndSuppressesSignalChange` 必须变红。

每处执行：
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmReason"
```
确认变红后立即恢复（用 `git diff` 确认恢复干净；不要 commit）。

- [ ] **Step 5: 最终定向回归**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmReason"
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~NmPanelFlowTests|FullyQualifiedName~NmReportWriterTests|FullyQualifiedName~NmSegmentBrushConverterTests|FullyQualifiedName~NmInputWizardViewModelTests"
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~NmAnalyzerTests|FullyQualifiedName~NmCacheKeyTests"
```
Expected: 全部 PASS。失败必须如实汇报，不得改判据掩盖。

- [ ] **Step 6: 调用 code-reviewer 审阅整个分支改动**

审阅范围：spec、本计划、所有新增/修改文件；重点检查“不猜”边界、来源追溯、DLC/跨 episode 处理、导出脱敏。
</content>
</Write>
