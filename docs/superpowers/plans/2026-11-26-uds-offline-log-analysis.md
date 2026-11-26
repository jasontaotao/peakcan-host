# UDS 离线日志分析 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 UDS 窗口新增“日志分析”页签，只读解析 ASC/BLF，离线重组 ISO-TP、配对请求/响应事务、解释 DID/例程/时序，产出可导出、可交给现有 AI 对话解读的不可变报告。

**Architecture:** Core 新增纯分析管线（帧分类 → 离线 ISO-TP 重组 → 事务配对 + 响应校验 → 时序 → 解释），App 层负责文件 IO、ViewModel、导出、AI Handoff；不接触硬件、UdsClient、发送服务或 LLM。

**Tech Stack:** .NET 10 / C# / xUnit + FluentAssertions / WPF + CommunityToolkit.Mvvm / System.Text.Json（仅账号状态文件，不新增依赖）。

**Spec:** `docs/superpowers/specs/2026-11-25-uds-offline-log-analysis-design.md`

## Global Constraints

- **只跑定向测试**：每步只跑本任务涉及的项目/过滤器；**绝不跑全量测试**。
- **不得修改 `peakcan-hil-core`**；本计划只读引用它（`DidDefinition`、`DidValueDecoder`、`IFileDialogService`、`FrameFlags`）。
- **无新增 NuGet 依赖**；`Directory.Packages.props` 不动。
- **Core 分析层**不得引用 WPF、文件对话框、`UdsClient`、硬件通道、发送服务或 `ILlmProvider`。
- **不静默降级**：缺 ODX、缺定义、短包、坏 PCI、孤儿连续帧都必须带原因出现在报告里。
- **注释语言**：用户/业务注释中文；技术 API、协议字段注释英文。
- **commit/push 只在用户明确要求时执行**；本计划的“Commit checkpoint”是逻辑检查点，执行前先获得授权。
- 200 MB 文件上限沿用 `ReplayOptions.DefaultMaxFileSizeBytes`；超限显式报错。
- 证据序号 = 稳定排序后的位置序号（从 0 开始），不改写解析器原始顺序。
- UI 文案中文；报告/导出/AI 使用同一份不可变报告快照。

## Review Focus

These are the input classes the spec implies but no single test naturally covers; each line names the owning task's test.

1. **日志从传输中段开始**（孤儿 CF、无 FF）→ 不得建事务或静默丢弃；必须进噪声桶并计入闭合等式。Task 3。
2. **同一 ID 上并行诊断源/周期帧**（伪 `0x76`、重复最终响应）→ 不得静默判成功；校验失败进噪声、重复响应标 `DuplicateFinalResponse`。Task 4。
3. **0x78 长等待 + 迟到响应** → 原事务仍可闭合、耗时不崩、P2 只标记不剔除。Task 4/5。
4. **空文件 / 极小文件 / 200 MB 以上 / 当前地址零匹配** → 各自有明确空态或错误，不产生“完整报告”。Task 8/11。
5. **未加载 ODX 与加载后重分析** → 原始值 + 降级原因；旧报告不被热更新。Task 6/11。

---

## File Structure

```text
src/PeakCan.Host.Core/Analysis/Uds/           # 纯分析（新增）
  UdsOfflineAddress.cs  UdsFrameClass.cs  ClassicIsoTpPci.cs
  UdsFrameClassifier.cs  UdsFrameCounts.cs
  UdsOfflinePdu.cs  OfflineIsoTpReassembler.cs
  UdsOfflineTransaction.cs  UdsResponseValidator.cs  UdsTransactionBuilder.cs
  UdsSessionTiming.cs  UdsSessionTimingTracker.cs
  UdsDegradeReason.cs  UdsInterpretation.cs  UdsServiceInterpreter.cs
  UdsOfflineAnalysisRequest.cs  UdsOfflineReport.cs  UdsNoiseSummary.cs
  UdsOfflineAnalyzer.cs

src/PeakCan.Host.App/Services/Uds/            # App 层边界（新增）
  IUdsOfflineTraceLoader.cs  UdsOfflineTraceLoader.cs
  UdsOfflineReportWriter.cs  UdsOfflineRedactor.cs
  UdsOfflineAiPromptBuilder.cs
  UdsChatHandoffPayload.cs  ITraceViewerChatHandoffService.cs
  TraceViewerChatHandoffService.cs

src/PeakCan.Host.App/ViewModels/Uds/
  UdsLogAnalysisViewModel.cs                  # 新增
src/PeakCan.Host.App/Views/
  UdsLogAnalysisView.xaml(.cs)                # 新增

src/PeakCan.Host.App/Windows/UdsWindow.xaml    # 新增页签 + 在线控件显隐
src/PeakCan.Host.App/ViewModels/Uds/UdsViewModel.cs
src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/ChatFlow.cs
src/PeakCan.Host.App/Composition/AppHostBuilder.cs
```

---

### Task 1: 数据库加载状态 + ODX 导入成功标记

**Files:**
- Create: `src/PeakCan.Host.Core/Uds/Database/UserJsonLoadStatus.cs`
- Modify: `src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs`
- Modify: `src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/Uds/OdxImportViewModel.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Uds/Database/UserJsonLoadStatusTests.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/Uds/OdxImportViewModelTests.cs`（已有文件则追加）

**Interfaces:**
- Consumes: `DidDefinition` / `RoutineDefinition`（PeakCan.HIL.Core）。
- Produces: `UserJsonLoadStatus { NotConfigured, Missing, Loaded, Failed }`；`DidDatabase.UserJsonLoadStatus`、`RoutineDatabase.UserJsonLoadStatus`（只读）；`OdxImportViewModel.HasSuccessfulImport`（只读）。

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/PeakCan.Host.Core.Tests/Uds/Database/UserJsonLoadStatusTests.cs
using System;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.Core.Uds.Database;
using Xunit;

namespace PeakCan.Host.Core.Tests.Uds.Database;

public sealed class UserJsonLoadStatusTests
{
    private static string TempJson(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "uds-dids.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Not_configured_when_path_is_null()
    {
        var db = new DidDatabase(userJsonPath: null, NullLogger<DidDatabase>.Instance);
        db.UserJsonLoadStatus.Should().Be(UserJsonLoadStatus.NotConfigured);
    }

    [Fact]
    public void Missing_when_file_absent()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.json");
        var db = new DidDatabase(path, NullLogger<DidDatabase>.Instance);
        db.UserJsonLoadStatus.Should().Be(UserJsonLoadStatus.Missing);
        db.Find(0xF190).Should().NotBeNull("内置 DID 必须仍然可用");
    }

    [Fact]
    public void Failed_when_json_malformed()
    {
        var db = new DidDatabase(TempJson("{ not json"), NullLogger<DidDatabase>.Instance);
        db.UserJsonLoadStatus.Should().Be(UserJsonLoadStatus.Failed);
        db.Find(0xF190).Should().NotBeNull();
    }

    [Fact]
    public void Loaded_when_json_valid()
    {
        var db = new DidDatabase(TempJson("{\"dids\":[]}"), NullLogger<DidDatabase>.Instance);
        db.UserJsonLoadStatus.Should().Be(UserJsonLoadStatus.Loaded);
    }
}
```

```csharp
// tests/PeakCan.Host.App.Tests/ViewModels/Uds/OdxImportViewModelTests.cs（追加用例）
[Fact]
public async Task Successful_import_sets_HasSuccessfulImport()
{
    var vm = new OdxImportViewModel(new FakeOdxService(OdxImportResult.Ok(1, 2, 3, Array.Empty<string>())));
    await vm.ImportAsync("x.odx");
    vm.HasSuccessfulImport.Should().BeTrue();
}

[Fact]
public async Task Failed_import_does_not_set_HasSuccessfulImport()
{
    var vm = new OdxImportViewModel(new FakeOdxService(OdxImportResult.Failed(OdxErrorCode.ParseError, "bad")));
    await vm.ImportAsync("x.odx");
    vm.HasSuccessfulImport.Should().BeFalse();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~UserJsonLoadStatusTests"`
Expected: FAIL — `UserJsonLoadStatus` 类型不存在。

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/PeakCan.Host.Core/Uds/Database/UserJsonLoadStatus.cs
namespace PeakCan.Host.Core.Uds.Database;

public enum UserJsonLoadStatus
{
    NotConfigured,
    Missing,
    Loaded,
    Failed,
}
```

`DidDatabase`（`RoutineDatabase` 同构）：

```csharp
public UserJsonLoadStatus UserJsonLoadStatus { get; private set; }

public DidDatabase(string? userJsonPath, ILogger<DidDatabase>? logger, PathOptions options)
{
    _logger = logger;
    _options = options;
    var builtIn = BuiltInDefaults();
    var user = LoadUserFile(userJsonPath);
    All = MergeBuiltInAndUser(builtIn, user);
}

private List<DidDefinition>? LoadUserFile(string? path)
{
    if (string.IsNullOrEmpty(path))
    {
        UserJsonLoadStatus = UserJsonLoadStatus.NotConfigured;
        // ... existing log path ...
        return null;
    }
    if (!File.Exists(path))
    {
        UserJsonLoadStatus = UserJsonLoadStatus.Missing;
        // ... existing log path ...
        return null;
    }
    try { /* existing read/deserialize */ UserJsonLoadStatus = UserJsonLoadStatus.Loaded; return dto?.Dids; }
    catch (JsonException) { UserJsonLoadStatus = UserJsonLoadStatus.Failed; /* existing log */ return null; }
    catch (IOException) { UserJsonLoadStatus = UserJsonLoadStatus.Failed; /* existing log */ return null; }
}
```

`OdxImportViewModel`：

```csharp
[ObservableProperty]
private bool _hasSuccessfulImport;

// ImportAsync 成功后：
if (!_importCts.IsCancellationRequested && !result.HasError)
    HasSuccessfulImport = true;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~UserJsonLoadStatusTests"`
Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~OdxImportViewModelTests"`
Expected: PASS（两组）。

- [ ] **Step 5: Commit checkpoint**

```bash
git add src/PeakCan.Host.Core/Uds/Database/UserJsonLoadStatus.cs \
        src/PeakCan.Host.Core/Uds/Database/DidDatabase.cs \
        src/PeakCan.Host.Core/Uds/Database/RoutineDatabase.cs \
        src/PeakCan.Host.App/ViewModels/Uds/OdxImportViewModel.cs \
        tests/PeakCan.Host.Core.Tests/Uds/Database/UserJsonLoadStatusTests.cs \
        tests/PeakCan.Host.App.Tests/ViewModels/Uds/OdxImportViewModelTests.cs
git commit -m "feat(uds): expose definition-file load status for offline reports"
```

---

### Task 2: Core 契约 + 帧分类器 + 计数闭合

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsOfflineAddress.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsFrameClass.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Uds/ClassicIsoTpPci.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsFrameClassifier.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsFrameEvidence.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsFrameCounts.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Uds/UdsFrameClassifierTests.cs`

**Interfaces:**
- Consumes: `ReplayFrame`, `FrameFlags`。
- Produces: `UdsOfflineAddress(ushort Channel, uint RequestId, uint ResponseId, bool IsExtended)`；`UdsFrameClass`；`UdsNoiseReason`；`UdsDirectionRole`；`UdsFrameClassification`；`ClassicIsoTpPci.TryParse`；`UdsFrameClassifier.Classify`；`UdsFrameCounts.From` / `Closes`。

- [ ] **Step 1: Write the failing tests**

```csharp
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Replay;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Uds;

public sealed class UdsFrameClassifierTests
{
    private static readonly UdsOfflineAddress Addr = new(Channel: 1, RequestId: 0x7E0, ResponseId: 0x7E8, IsExtended: false);

    private static ReplayFrame Frame(uint id, byte[] data, FrameFlags flags = FrameFlags.None,
        ushort channel = 1, bool extended = false)
        => new(Timestamp: 0, Id: id, Dlc: (byte)data.Length, Data: data, Flags: flags,
               IsExtended: extended, Channel: channel);

    [Fact]
    public void Can_fd_is_unsupported_even_when_address_matches()
    {
        var frames = new[] { Frame(0x7E0, new byte[] { 0x02, 0x10, 0x01 }, FrameFlags.Fd) };
        var c = new UdsFrameClassifier().Classify(frames, Addr);
        c.Should().ContainSingle().Which.Class.Should().Be(UdsFrameClass.UnsupportedInput);
    }

    [Fact]
    public void Other_id_on_same_channel_is_non_diagnostic()
    {
        var frames = new[] { Frame(0x123, new byte[] { 0x01, 0x02 }) };
        var c = new UdsFrameClassifier().Classify(frames, Addr);
        c.Should().ContainSingle().Which.Class.Should().Be(UdsFrameClass.NonDiagnostic);
    }

    [Fact]
    public void Matching_request_frame_is_diagnostic_request()
    {
        var frames = new[] { Frame(0x7E0, new byte[] { 0x02, 0x10, 0x01 }) };
        var c = new UdsFrameClassifier().Classify(frames, Addr);
        var item = c.Should().ContainSingle().Subject;
        item.Class.Should().Be(UdsFrameClass.MatchedDiagnostic);
        item.Role.Should().Be(UdsDirectionRole.Request);
    }

    [Fact]
    public void Invalid_pci_is_noise_with_reason()
    {
        var frames = new[] { Frame(0x7E0, new byte[] { 0x0F, 0x10 }) }; // SF 声明 15 字节但只有 1 字节
        var c = new UdsFrameClassifier().Classify(frames, Addr);
        c.Should().ContainSingle().Which.Class.Should().Be(UdsFrameClass.Noise);
        c[0].NoiseReason.Should().Be(UdsNoiseReason.InvalidPci);
    }

    [Fact]
    public void Counts_close_on_mixed_log()
    {
        var frames = new[]
        {
            Frame(0x7E0, new byte[] { 0x02, 0x10, 0x01 }),          // matched
            Frame(0x123, new byte[] { 0x01 }),                       // non-diagnostic
            Frame(0x7E0, new byte[] { 0x0F, 0x10 }),                 // noise
            Frame(0x7E8, new byte[] { 0x01 }, FrameFlags.Fd),        // unsupported
        };
        var classes = new UdsFrameClassifier().Classify(frames, Addr);
        var counts = UdsFrameCounts.From(classes, frames.Length);
        counts.Closes.Should().BeTrue();
        counts.Input.Should().Be(4);
        counts.MatchedDiagnostic.Should().Be(1);
        counts.NonDiagnostic.Should().Be(1);
        counts.Noise.Should().Be(1);
        counts.UnsupportedInput.Should().Be(1);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsFrameClassifierTests"`
Expected: FAIL — 类型不存在。

- [ ] **Step 3: Write minimal implementation**

```csharp
// 契约（同一命名空间）
public sealed record UdsOfflineAddress(ushort Channel, uint RequestId, uint ResponseId, bool IsExtended)
{
    public bool Matches(ReplayFrame f)
        => f.Channel == Channel && f.IsExtended == IsExtended
           && (f.Id == RequestId || f.Id == ResponseId);

    public UdsDirectionRole RoleOf(ReplayFrame f)
        => f.Id == RequestId ? UdsDirectionRole.Request : UdsDirectionRole.Response;
}

public enum UdsFrameClass { MatchedDiagnostic, NonDiagnostic, Noise, UnsupportedInput }
public enum UdsDirectionRole { Request, Response }
public enum UdsNoiseReason { InvalidPci, InsufficientDlc, SequenceError, OrphanConsecutiveFrame, ResponseValidationFailed, UnsupportedFrameType }

public sealed record UdsFrameClassification(
    int EvidenceIndex, UdsFrameClass Class, UdsNoiseReason? NoiseReason = null, UdsDirectionRole? Role = null);

// spec §6.2：原始证据（只有匹配诊断/噪声/未支持进入证据表；非诊断帧只聚合）
public sealed record UdsFrameEvidence(
    int Index, double Timestamp, ushort Channel, uint Id, bool IsExtended,
    bool? IsTransmit, UdsFrameClass Class, UdsNoiseReason? NoiseReason,
    string FrameKind, byte Dlc, byte[] Data)
{
    public string Display =>
        $"{Index}  {Timestamp:F6}  ch{Channel}  0x{Id:X3}  {FrameKind}  {Convert.ToHexString(Data)}";
}

public sealed record UdsFrameCounts(int Input, int MatchedDiagnostic, int NonDiagnostic, int Noise, int UnsupportedInput)
{
    public bool Closes => Input == MatchedDiagnostic + NonDiagnostic + Noise + UnsupportedInput;

    public static UdsFrameCounts From(IReadOnlyList<UdsFrameClassification> c, int input) => new(
        input,
        c.Count(x => x.Class == UdsFrameClass.MatchedDiagnostic),
        c.Count(x => x.Class == UdsFrameClass.NonDiagnostic),
        c.Count(x => x.Class == UdsFrameClass.Noise),
        c.Count(x => x.Class == UdsFrameClass.UnsupportedInput));
}
```

```csharp
public enum ClassicPciKind { SingleFrame, FirstFrame, ConsecutiveFrame, FlowControl, Invalid }
public readonly record struct ClassicPci(ClassicPciKind Kind, int PayloadOffset, int DeclaredLength, int Sequence);

public static class ClassicIsoTpPci
{
    public static bool TryParse(ReadOnlySpan<byte> data, out ClassicPci pci)
    {
        pci = default;
        if (data.Length == 0) return false;
        var kind = data[0] >> 4;
        switch (kind)
        {
            case 0: // Single Frame
            {
                var len = data[0] & 0x0F;
                if (len > data.Length - 1) return false;
                pci = new(ClassicPciKind.SingleFrame, 1, len, 0);
                return true;
            }
            case 1: // First Frame
            {
                if (data.Length < 2) return false;
                var len = ((data[0] & 0x0F) << 8) | data[1];
                if (len <= 7 || len > 4095) return false;
                pci = new(ClassicPciKind.FirstFrame, 2, len, 0);
                return true;
            }
            case 2: // Consecutive Frame
                if (data.Length < 2) return false;
                pci = new(ClassicPciKind.ConsecutiveFrame, 1, 0, data[0] & 0x0F);
                return true;
            case 3: // Flow Control
                if (data.Length < 3) return false;
                pci = new(ClassicPciKind.FlowControl, 1, 0, 0);
                return true;
            default:
                return false;
        }
    }
}
```

```csharp
public sealed class UdsFrameClassifier
{
    public IReadOnlyList<UdsFrameClassification> Classify(
        IReadOnlyList<ReplayFrame> frames, UdsOfflineAddress address)
    {
        var result = new List<UdsFrameClassification>(frames.Count);
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            if ((f.Flags & (FrameFlags.Fd | FrameFlags.Rtr | FrameFlags.ErrFrame)) != 0)
            {
                result.Add(new(i, UdsFrameClass.UnsupportedInput, UdsNoiseReason.UnsupportedFrameType));
                continue;
            }
            if (!address.Matches(f))
            {
                result.Add(new(i, UdsFrameClass.NonDiagnostic));
                continue;
            }
            if (!ClassicIsoTpPci.TryParse(f.Data, out _))
            {
                var reason = f.Data.Length == 0 ? UdsNoiseReason.InsufficientDlc : UdsNoiseReason.InvalidPci;
                result.Add(new(i, UdsFrameClass.Noise, reason));
                continue;
            }
            result.Add(new(i, UdsFrameClass.MatchedDiagnostic, null, address.RoleOf(f)));
        }
        return result;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsFrameClassifierTests"`
Expected: PASS（5 个用例）。

- [ ] **Step 5: Commit checkpoint**

```bash
git add src/PeakCan.Host.Core/Analysis/Uds tests/PeakCan.Host.Core.Tests/Analysis/Uds/UdsFrameClassifierTests.cs
git commit -m "feat(uds): classify offline log frames and close the count equation"
```

---

### Task 3: 离线 ISO-TP 重组器

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsOfflinePdu.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Uds/OfflineIsoTpReassembler.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Uds/OfflineIsoTpReassemblerTests.cs`

**Interfaces:**
- Consumes: `ReplayFrame`、`UdsFrameClassification`、`ClassicIsoTpPci`。
- Produces: `UdsOfflinePdu`；`UdsTransportWarning`；`OfflineIsoTpReassembler.Reassemble(frames, classes, address)` → `UdsReassemblyResult(Pdus, Warnings, ExtraNoiseIndices)`。

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Single_frame_forms_complete_pdu()
{
    var frames = new[] { F(0x7E0, 0x02, 0x10, 0x01) };
    var r = Run(frames);
    r.Pdus.Should().ContainSingle().Which.Should().Match<UdsOfflinePdu>(p =>
        p.Role == UdsDirectionRole.Request && p.IsComplete && !p.IsMultiFrame
        && p.Payload.SequenceEqual(new byte[] { 0x10, 0x01 }));
}

[Fact]
public void First_and_consecutive_frames_reassemble()
{
    // FF: 0x10 0x0A + 6 bytes; CF: 0x21 + remaining 4 bytes
    var frames = new[]
    {
        F(0x7E0, 0x10, 0x0A, 0x62, 0xF1, 0x90, 0x31, 0x32, 0x33),
        F(0x7E0, 0x21, 0x34, 0x35, 0x36, 0x37),
    };
    var r = Run(frames);
    var pdu = r.Pdus.Should().ContainSingle().Subject;
    pdu.IsComplete.Should().BeTrue();
    pdu.Payload.Should().Equal(0x62, 0xF1, 0x90, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37);
    pdu.FirstEvidenceIndex.Should().Be(0);
    pdu.LastEvidenceIndex.Should().Be(1);
}

[Fact]
public void Orphan_consecutive_frame_is_noise_not_a_pdu()
{
    var r = Run(new[] { F(0x7E0, 0x21, 0x01, 0x02) });
    r.Pdus.Should().BeEmpty();
    r.ExtraNoiseIndices.Should().ContainSingle().Which.Should().Be(0);
}

[Fact]
public void Wrong_sequence_marks_that_frame_noise_and_keeps_partial_pdu()
{
    var frames = new[]
    {
        F(0x7E0, 0x10, 0x0A, 0x62, 0xF1, 0x90, 0x31, 0x32, 0x33),
        F(0x7E0, 0x22, 0x34, 0x35, 0x36, 0x37), // 期望 0x21
    };
    var r = Run(frames);
    var pdu = r.Pdus.Should().ContainSingle().Subject;
    pdu.IsComplete.Should().BeFalse();
    r.ExtraNoiseIndices.Should().Contain(1);
}

[Fact]
public void Flow_control_is_evidence_only_and_forms_no_pdu()
{
    var frames = new[]
    {
        F(0x7E0, 0x10, 0x0A, 0x62, 0xF1, 0x90, 0x31, 0x32, 0x33),
        F(0x7E8, 0x30, 0x00, 0x00), // FC（响应方向）
        F(0x7E0, 0x21, 0x34, 0x35, 0x36, 0x37),
    };
    var r = Run(frames);
    var pdu = r.Pdus.Should().ContainSingle().Subject;
    pdu.Evidence.Should().Contain(1, "流控帧必须作为证据挂到多帧 PDU");
    r.ExtraNoiseIndices.Should().BeEmpty();
}

[Fact]
public void Direction_unknown_frames_still_reassemble_by_can_id()
{
    var frames = new[]
    {
        new ReplayFrame(0.0, 0x7E0, 3, new byte[] { 0x02, 0x10, 0x01 },
                        FrameFlags.None, IsExtended: false, Channel: 1, IsTransmit: null),
    };
    var r = Run(frames);
    r.Pdus.Should().ContainSingle().Which.Role.Should().Be(UdsDirectionRole.Request);
}
```

`Run` 辅助方法：先 `UdsFrameClassifier.Classify`，再 `new OfflineIsoTpReassembler().Reassemble`。

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~OfflineIsoTpReassemblerTests"`
Expected: FAIL — `OfflineIsoTpReassembler` 不存在。

- [ ] **Step 3: Write minimal implementation**

```csharp
public sealed record UdsOfflinePdu(
    UdsDirectionRole Role,
    byte[] Payload,
    double FirstTimestamp,
    double LastTimestamp,
    int FirstEvidenceIndex,
    int LastEvidenceIndex,
    IReadOnlyList<int> Evidence,
    bool IsMultiFrame,
    bool IsComplete,
    IReadOnlyList<string> Warnings);

public sealed record UdsTransportWarning(int EvidenceIndex, string Reason);

public sealed record UdsReassemblyResult(
    IReadOnlyList<UdsOfflinePdu> Pdus,
    IReadOnlyList<UdsTransportWarning> Warnings,
    IReadOnlyList<int> ExtraNoiseIndices);

public sealed class OfflineIsoTpReassembler
{
    private sealed class Stream
    {
        public bool Active;
        public int Declared;
        public int NextSeq;
        public List<byte> Data = new();
        public List<int> Evidence = new();
        public List<int> FlowControl = new();
        public double FirstTs;
        public double LastTs;
        public UdsDirectionRole Role;
    }

    public UdsReassemblyResult Reassemble(
        IReadOnlyList<ReplayFrame> frames,
        IReadOnlyList<UdsFrameClassification> classes,
        UdsOfflineAddress address)
    {
        var pdus = new List<UdsOfflinePdu>();
        var warnings = new List<UdsTransportWarning>();
        var noise = new List<int>();
        // 每个 (Channel, Id, IsExtended) 一个流；address 已保证方向由 ID 决定。
        var streams = new Dictionary<(ushort, uint, bool), Stream>();

        Stream GetStream(uint id, UdsDirectionRole role)
        {
            var key = (address.Channel, id, address.IsExtended);
            if (!streams.TryGetValue(key, out var s)) streams[key] = s = new Stream { Role = role };
            return s;
        }

        for (var i = 0; i < frames.Count; i++)
        {
            var c = classes[i];
            if (c.Class != UdsFrameClass.MatchedDiagnostic || c.Role is null) continue;
            var f = frames[i];
            if (!ClassicIsoTpPci.TryParse(f.Data, out var pci)) continue; // classifier 已拦
            var s = GetStream(f.Id, c.Role.Value);

            switch (pci.Kind)
            {
                case ClassicPciKind.SingleFrame:
                {
                    var payload = f.Data[pci.PayloadOffset..].ToArray();
                    pdus.Add(new UdsOfflinePdu(
                        s.Role, payload, f.Timestamp, f.Timestamp, i, i,
                        new[] { i }, false, true, Array.Empty<string>()));
                    break;
                }

                case ClassicPciKind.FirstFrame:
                    if (s.Active)
                    {
                        warnings.Add(new(LastEvidence(s), "NewFirstFrameOverwroteIncompletePdu"));
                        pdus.Add(Build(s, complete: false, lastIndex: s.Evidence[^1], lastTs: s.LastTs));
                    }
                    s = new Stream { Active = true, Declared = pci.DeclaredLength, NextSeq = 1, Role = c.Role.Value,
                                     FirstTs = f.Timestamp, LastTs = f.Timestamp };
                    s.Data.AddRange(f.Data[pci.PayloadOffset..].ToArray());
                    s.Evidence.Add(i);
                    streams[(address.Channel, f.Id, address.IsExtended)] = s;
                    break;

                case ClassicPciKind.ConsecutiveFrame:
                    if (!s.Active) { noise.Add(i); break; }
                    if (pci.Sequence != s.NextSeq)
                    {
                        noise.Add(i);
                        warnings.Add(new(i, "SequenceError"));
                        pdus.Add(Build(s, complete: false, lastIndex: s.Evidence[^1], lastTs: s.LastTs));
                        s.Active = false;
                        break;
                    }
                    s.NextSeq = (s.NextSeq + 1) & 0x0F;
                    s.Data.AddRange(f.Data[pci.PayloadOffset..].ToArray());
                    s.Evidence.Add(i);
                    s.LastTs = f.Timestamp;
                    if (s.Data.Count >= s.Declared)
                        pdus.Add(Build(s, complete: true, lastIndex: i, lastTs: f.Timestamp));
                    break;

                case ClassicPciKind.FlowControl:
                    if (s.Active) s.FlowControl.Add(i); // 证据挂到在途多帧 PDU，不单独成 PDU
                    break;
            }
        }

        // 日志结束仍活跃的 PDU：保留不完整。
        foreach (var s in streams.Values.Where(x => x.Active))
            pdus.Add(Build(s, complete: false, lastIndex: s.Evidence[^1], lastTs: s.LastTs));

        return new(pdus, warnings, noise);
    }

    private static int LastEvidence(Stream s) => s.Evidence.Count == 0 ? 0 : s.Evidence[^1];

    private static UdsOfflinePdu Build(Stream s, bool complete, int lastIndex, double lastTs)
    {
        var len = Math.Min(s.Declared, s.Data.Count);
        var evidence = s.Evidence.Concat(s.FlowControl).OrderBy(x => x).ToArray();
        return new UdsOfflinePdu(
            s.Role, s.Data.Take(len).ToArray(), s.FirstTs, lastTs,
            evidence[0], lastIndex, evidence, true, complete,
            complete ? Array.Empty<string>() : new[] { "IncompletePdu" });
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~OfflineIsoTpReassemblerTests"`
Expected: PASS（4 个用例）。

- [ ] **Step 5: Commit checkpoint**

```bash
git add src/PeakCan.Host.Core/Analysis/Uds/UdsOfflinePdu.cs \
        src/PeakCan.Host.Core/Analysis/Uds/OfflineIsoTpReassembler.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Uds/OfflineIsoTpReassemblerTests.cs
git commit -m "feat(uds): add offline ISO-TP reassembly with orphan and sequence evidence"
```

---

### Task 4: 事务配对 + 响应一致性校验

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsOfflineTransaction.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsResponseValidator.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsTransactionBuilder.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Uds/UdsTransactionBuilderTests.cs`

**Interfaces:**
- Consumes: `UdsOfflinePdu`。
- Produces: `UdsFinalKind { Positive, Negative, None }`；`UdsOfflineTransaction`；`UdsResponseValidator.TryValidate`；`UdsTransactionBuilder.Build(pdus)` → `UdsTransactionBuildResult(Transactions, OrphanResponses, ValidationFailures, DuplicateFinalResponses)`。

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Positive_response_closes_matching_request()
{
    var r = Build(
        Req(0x22, 0xF1, 0x90),
        Resp(0x62, 0xF1, 0x90, 0x31));
    r.Transactions.Should().ContainSingle();
    r.Transactions[0].FinalKind.Should().Be(UdsFinalKind.Positive);
    r.Transactions[0].Responses.Should().ContainSingle();
}

[Fact]
public void Pending_nrc_does_not_close_transaction()
{
    var r = Build(
        Req(0x10, 0x02),
        Resp(0x7F, 0x10, 0x78),
        Resp(0x50, 0x02, 0x00, 0x32, 0x01, 0xF4));
    var t = r.Transactions.Should().ContainSingle().Subject;
    t.FinalKind.Should().Be(UdsFinalKind.Positive);
    t.PendingCount.Should().Be(1);
}

[Fact]
public void Fake_0x76_with_wrong_block_counter_does_not_close_transfer()
{
    var r = Build(
        Req(0x36, 0x01, 0xAA, 0xBB),
        Resp(0x76, 0x02)); // 回显块序号 2，与请求 1 不符
    var t = r.Transactions.Should().ContainSingle().Subject;
    t.FinalKind.Should().Be(UdsFinalKind.None);
    r.ValidationFailures.Should().ContainSingle();
}

[Fact]
public void Duplicate_final_response_marks_transaction_ambiguous()
{
    var r = Build(
        Req(0x10, 0x01),
        Resp(0x50, 0x01, 0x00, 0x32, 0x01, 0xF4),
        Resp(0x50, 0x01, 0x00, 0x32, 0x01, 0xF4));
    var t = r.Transactions.Should().ContainSingle().Subject;
    t.HasDuplicateFinalResponse.Should().BeTrue();
    t.IsAmbiguous.Should().BeTrue();
    r.DuplicateFinalResponses.Should().ContainSingle();
}

[Fact]
public void Orphan_response_is_counted_not_attached()
{
    var r = Build(Resp(0x62, 0xF1, 0x90, 0x31));
    r.Transactions.Should().BeEmpty();
    r.OrphanResponses.Should().ContainSingle();
}

[Fact]
public void Late_response_still_closes_the_request()
{
    // 105 秒远超任何 P2；首版不按墙钟剔除在途请求。
    var r = Build(
        ReqAt(0.0, 0x22, 0xF1, 0x90),
        RespAt(105.0, 0x62, 0xF1, 0x90, 0x31));
    r.Transactions.Should().ContainSingle().Which.FinalKind.Should().Be(UdsFinalKind.Positive);
}
```

`Req` / `Resp` 用 `UdsOfflinePdu` 构造，`Evidence` 固定 `[index]`，时间递增；`ReqAt` / `RespAt` 额外接收时间戳。

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsTransactionBuilderTests"`
Expected: FAIL — `UdsTransactionBuilder` 不存在。

- [ ] **Step 3: Write minimal implementation**

```csharp
public enum UdsFinalKind { Positive, Negative, None }

public sealed record UdsOfflineTransaction(
    UdsOfflinePdu Request,
    IReadOnlyList<UdsOfflinePdu> Responses,
    UdsFinalKind FinalKind,
    int PendingCount,
    byte? FinalNrc,
    bool IsOrphan,
    bool IsAmbiguous,
    bool HasDuplicateFinalResponse,
    IReadOnlyList<string> Unknowns);

public sealed record UdsTransactionBuildResult(
    IReadOnlyList<UdsOfflineTransaction> Transactions,
    IReadOnlyList<UdsOfflinePdu> OrphanResponses,
    IReadOnlyList<UdsOfflinePdu> ValidationFailures,
    IReadOnlyList<UdsOfflinePdu> DuplicateFinalResponses)
{
    public IReadOnlyList<string> FailedChecks { get; init; } = Array.Empty<string>();
}

internal static class UdsResponseValidator
{
    public static bool TryValidate(UdsOfflinePdu request, UdsOfflinePdu response, out string? failed)
    {
        failed = null;
        var req = request.Payload; var res = response.Payload;
        if (res.Length < 2) { failed = "PayloadTooShort"; return false; }
        var sid = req.Length == 0 ? -1 : req[0];
        switch (sid)
        {
            case 0x10:
                if (res.Length < 6) { failed = "PayloadTooShort"; return false; }
                if (res[1] != req[1]) { failed = "SessionTypeEcho"; return false; }
                return true;
            case 0x22:
                if (res.Length < 3 || res[1] != req[1] || res[2] != req[2]) { failed = "DidEcho"; return false; }
                return true;
            case 0x2E:
                if (res.Length < 3 || res[1] != req[1] || res[2] != req[2]) { failed = "DidEcho"; return false; }
                return true;
            case 0x27:
                if (res[1] != (req[1] & 0x7F)) { failed = "SubFunctionEcho"; return false; }
                return true;
            case 0x31:
                if (res.Length < 4 || res[2] != req[2] || res[3] != req[3]) { failed = "RoutineIdEcho"; return false; }
                return true;
            case 0x34:
                return res.Length >= 3;
            case 0x36:
                if (res[1] != req[1]) { failed = "BlockSequenceEcho"; return false; }
                return true;
            case 0x37:
                return res.Length >= 1;
            case 0x3E:
                if (res[1] != (req[1] & 0x7F)) { failed = "SubFunctionEcho"; return false; }
                return true;
            default:
                return true; // 未列服务：SID 配对 + 最小长度已由上面 >=2 保证
        }
    }
}
```

`UdsTransactionBuilder.Build` 核心循环：

```csharp
public UdsTransactionBuildResult Build(IReadOnlyList<UdsOfflinePdu> pdus)
{
    var open = new List<OpenTx>();
    var closed = new List<OpenTx>();
    var orphans = new List<UdsOfflinePdu>();
    var failed = new List<UdsOfflinePdu>();
    var dups = new List<UdsOfflinePdu>();

    foreach (var pdu in pdus)
    {
        if (pdu.Role == UdsDirectionRole.Request)
        {
            // 请求：按 SID 找同 SID 未闭合 -> 标歧义
            var sameSid = open.FirstOrDefault(x => x.Sid == pdu.Payload[0]);
            if (sameSid is not null) sameSid.Ambiguous = true;
            open.Add(new OpenTx(pdu));
            continue;
        }
        if (pdu.Role != UdsDirectionRole.Response) continue;

        var resSid = pdu.Payload[0];
        if (resSid == 0x7F)
        {
            if (pdu.Payload.Length < 3) { failed.Add(pdu); continue; }
            var reqSid = pdu.Payload[1]; var nrc = pdu.Payload[2];
            var tx = open.FirstOrDefault(x => x.Sid == reqSid);
            if (tx is null) { orphans.Add(pdu); continue; }
            tx.Responses.Add(pdu);
            if (nrc == 0x78) tx.PendingCount++;
            else { tx.Nrc = nrc; tx.Final = UdsFinalKind.Negative; open.Remove(tx); closed.Add(tx); }
            continue;
        }

        var reqSidExpected = (byte)(resSid - 0x40);
        var target = open.FirstOrDefault(x => x.Sid == reqSidExpected);
        if (target is null)
        {
            // 已闭合事务仍可能收到第二个最终响应：不能静默变成孤立响应。
            var closedTarget = closed.LastOrDefault(x => x.Sid == reqSidExpected && !x.Duplicate);
            if (closedTarget is not null)
            {
                closedTarget.Responses.Add(pdu);
                closedTarget.Duplicate = true;
                closedTarget.Ambiguous = true;
                dups.Add(pdu);
            }
            else orphans.Add(pdu);
            continue;
        }
        if (!UdsResponseValidator.TryValidate(target.Request, pdu, out _))
        { failed.Add(pdu); continue; }
        target.Responses.Add(pdu);
        target.Final = UdsFinalKind.Positive;
        open.Remove(target);
        closed.Add(target);
    }
    var done = closed.Select(x => x.ToTransaction()).ToList();
    foreach (var tx in open) done.Add(tx.ToIncompleteTransaction());
    return new(done, orphans, failed, dups);
}
```

> `OpenTx` 是 builder 的私有可变包装：`Sid`、`Request`、`Responses`、`PendingCount`、`Nrc`、`Final`、`Ambiguous`、`Duplicate`；`ToTransaction()` 把 `Duplicate` 映射为 `HasDuplicateFinalResponse`，把 `Ambiguous` 映射为 `IsAmbiguous`。

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsTransactionBuilderTests"`
Expected: PASS（5 个用例）。

- [ ] **Step 5: Commit checkpoint**

```bash
git add src/PeakCan.Host.Core/Analysis/Uds/UdsOfflineTransaction.cs \
        src/PeakCan.Host.Core/Analysis/Uds/UdsResponseValidator.cs \
        src/PeakCan.Host.Core/Analysis/Uds/UdsTransactionBuilder.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Uds/UdsTransactionBuilderTests.cs
git commit -m "feat(uds): pair offline UDS transactions with response validation"
```

---

### Task 5: 会话时序与耗时

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsSessionTiming.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsSessionTimingTracker.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Uds/UdsSessionTimingTrackerTests.cs`

**Interfaces:**
- Consumes: `UdsOfflineTransaction`、`UdsOfflinePdu`。
- Produces: `UdsSessionTiming(int P2Ms, int P2StarMs, string Source)`；`UdsSessionTimingTracker.Apply(transactions)`；transaction 的 `FirstResponseMs` / `TotalMs` / `TimingSource` init 属性。

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void P2_and_P2star_are_read_in_correct_units()
{
    var txs = new[] { SessionControl(0x02, p2: 0x0032, p2Star: 0x01F4) };
    var tracker = new UdsSessionTimingTracker();
    var timed = tracker.Apply(txs);

    tracker.Timing!.P2Ms.Should().Be(50);
    tracker.Timing.P2StarMs.Should().Be(5000);
    timed[0].TimingSource.Should().Be("ECU:0x50");
}

[Fact]
public void No_session_control_leaves_timing_unknown()
{
    var tracker = new UdsSessionTimingTracker();
    var timed = tracker.Apply(new[] { ReadDidTransaction() });
    tracker.Timing.Should().BeNull();
    timed[0].TimingSource.Should().Be("Unknown");
}

[Fact]
public void Unclosed_transaction_has_no_total_duration_but_has_first_response()
{
    var tracker = new UdsSessionTimingTracker();
    var timed = tracker.Apply(new[] { PendingOnlyTransaction() });
    timed[0].FirstResponseMs.Should().NotBeNull();
    timed[0].TotalMs.Should().BeNull();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsSessionTimingTrackerTests"`
Expected: FAIL — 类型不存在。

- [ ] **Step 3: Write minimal implementation**

```csharp
public sealed record UdsSessionTiming(int P2Ms, int P2StarMs, string Source);

public sealed class UdsSessionTimingTracker
{
    public UdsSessionTiming? Timing { get; private set; }

    public IReadOnlyList<UdsOfflineTransaction> Apply(IReadOnlyList<UdsOfflineTransaction> transactions)
    {
        var result = new List<UdsOfflineTransaction>(transactions.Count);
        foreach (var tx in transactions.OrderBy(t => t.Request.FirstTimestamp))
        {
            var first = tx.Responses.FirstOrDefault();
            var firstMs = first is null
                ? (double?)null
                : (first.FirstTimestamp - tx.Request.LastTimestamp) * 1000.0;
            var totalMs = tx.FinalKind == UdsFinalKind.None || tx.Responses.Count == 0
                ? (double?)null
                : (tx.Responses[^1].LastTimestamp - tx.Request.FirstTimestamp) * 1000.0;
            var copy = tx with
            {
                FirstResponseMs = firstMs,
                TotalMs = totalMs,
                TimingSource = Timing?.Source ?? "Unknown",
            };
            result.Add(copy);

            if (TryReadSessionTiming(copy, out var timing)) Timing = timing;
        }
        return result;
    }

    private static bool TryReadSessionTiming(UdsOfflineTransaction tx, out UdsSessionTiming timing)
    {
        timing = default!;
        if (tx.Request.Payload.Length == 0 || tx.Request.Payload[0] != 0x10) return false;
        if (tx.FinalKind != UdsFinalKind.Positive || tx.Responses.Count == 0) return false;
        var res = tx.Responses[^1].Payload;
        if (res.Length < 6 || res[0] != 0x50 || res[1] != tx.Request.Payload[1]) return false;
        var p2 = (res[2] << 8) | res[3];
        var p2Star = ((res[4] << 8) | res[5]) * 10;
        timing = new UdsSessionTiming(p2, p2Star, "ECU:0x50");
        return true;
    }
}
```

`UdsOfflineTransaction` 增加：`public double? FirstResponseMs { get; init; }`、`public double? TotalMs { get; init; }`、`public string TimingSource { get; init; } = "Unknown";`。

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsSessionTimingTrackerTests"`
Expected: PASS（3 个用例）。

- [ ] **Step 5: Commit checkpoint**

```bash
git add src/PeakCan.Host.Core/Analysis/Uds/UdsSessionTiming.cs \
        src/PeakCan.Host.Core/Analysis/Uds/UdsSessionTimingTracker.cs \
        src/PeakCan.Host.Core/Analysis/Uds/UdsOfflineTransaction.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Uds/UdsSessionTimingTrackerTests.cs
git commit -m "feat(uds): track offline session timing and durations"
```

---

### Task 6: 服务解释与 ODX 降级

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsOfflineDiagnostics.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsDegradeReason.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsInterpretation.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsServiceInterpreter.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Uds/UdsServiceInterpreterTests.cs`

**Interfaces:**
- Consumes: `DidDefinition`、`RoutineDefinition`、`DidValueDecoder`、`UserJsonLoadStatus`。
- Produces: `UdsOfflineDiagnostics(Dids, Routines, OdxLoaded, DidUserJsonStatus, RoutineUserJsonStatus, SnapshotId)`；`UdsDegradeReason`；`UdsInterpretation`；`UdsServiceInterpreter.Interpret(transaction, diagnostics)`。

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void No_odx_and_no_definition_reports_NoOdx()
{
    var diag = new UdsOfflineDiagnostics(Array.Empty<DidDefinition>(), Array.Empty<RoutineDefinition>(),
        OdxLoaded: false, UserJsonLoadStatus.NotConfigured, UserJsonLoadStatus.NotConfigured, "snap");
    var i = new UdsServiceInterpreter().Interpret(ReadDid(0x1234, 0x01), diag);
    i.DegradeReason.Should().Be(UdsDegradeReason.NoOdx);
    i.DecodedValue.Should().BeNull("原始值由报告层保留");
}

[Fact]
public void Builtin_did_without_fields_reports_NoFieldTable()
{
    var vin = new DidDefinition(0xF190, "VIN", "Vehicle Identification Number", 17, false);
    var diag = new UdsOfflineDiagnostics(new[] { vin }, Array.Empty<RoutineDefinition>(),
        OdxLoaded: false, UserJsonLoadStatus.Missing, UserJsonLoadStatus.Missing, "snap");
    var i = new UdsServiceInterpreter().Interpret(ReadDid(0xF190, Enumerable.Repeat((byte)'A', 17).ToArray()), diag);
    i.DegradeReason.Should().Be(UdsDegradeReason.NoFieldTable);
    i.DidName.Should().Be("VIN");
}

[Fact]
public void Odx_loaded_but_missing_did_reports_OdxMissingDefinition()
{
    var diag = new UdsOfflineDiagnostics(Array.Empty<DidDefinition>(), Array.Empty<RoutineDefinition>(),
        OdxLoaded: true, UserJsonLoadStatus.Missing, UserJsonLoadStatus.Missing, "snap");
    var i = new UdsServiceInterpreter().Interpret(ReadDid(0x1234, 0x01), diag);
    i.DegradeReason.Should().Be(UdsDegradeReason.OdxMissingDefinition);
}

[Fact]
public void Unknown_sid_falls_back_to_hex_without_throwing()
{
    var diag = new UdsOfflineDiagnostics(Array.Empty<DidDefinition>(), Array.Empty<RoutineDefinition>(),
        OdxLoaded: false, UserJsonLoadStatus.NotConfigured, UserJsonLoadStatus.NotConfigured, "snap");
    var i = new UdsServiceInterpreter().Interpret(RequestOnly(0xF0), diag);
    i.ServiceDisplay.Should().Be("SID 0xF0");
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsServiceInterpreterTests"`
Expected: FAIL — 类型不存在。

- [ ] **Step 3: Write minimal implementation**

```csharp
public sealed record UdsOfflineDiagnostics(
    IReadOnlyList<DidDefinition> Dids,
    IReadOnlyList<RoutineDefinition> Routines,
    bool OdxLoaded,
    UserJsonLoadStatus DidUserJsonStatus,
    UserJsonLoadStatus RoutineUserJsonStatus,
    string SnapshotId);

public enum UdsDegradeReason { NoOdx, OdxMissingDefinition, NoFieldTable, PayloadTooShort, IncompleteData }

public sealed record UdsInterpretation(
    string ServiceDisplay,
    string? DidName,
    string? RoutineName,
    string? DecodedValue,
    UdsDegradeReason? DegradeReason,
    string? DegradeDetail);

public sealed class UdsServiceInterpreter
{
    private static readonly IReadOnlyDictionary<byte, string> Services = new Dictionary<byte, string>
    {
        [0x10] = "DiagnosticSessionControl",
        [0x11] = "ECUReset",
        [0x14] = "ClearDiagnosticInformation",
        [0x19] = "ReadDTCInformation",
        [0x22] = "ReadDataByIdentifier",
        [0x27] = "SecurityAccess",
        [0x28] = "CommunicationControl",
        [0x2E] = "WriteDataByIdentifier",
        [0x31] = "RoutineControl",
        [0x34] = "RequestDownload",
        [0x36] = "TransferData",
        [0x37] = "RequestTransferExit",
        [0x3E] = "TesterPresent",
        [0x7F] = "NegativeResponse",
    };

    public UdsInterpretation Interpret(UdsOfflineTransaction tx, UdsOfflineDiagnostics diag)
    {
        var req = tx.Request.Payload;
        var sid = req.Length == 0 ? (byte)0 : req[0];
        var display = Services.TryGetValue(sid, out var name) ? name : $"SID 0x{sid:X2}";

        if (tx.FinalKind == UdsFinalKind.Negative)
            return new(display, null, null, null, null, tx.FinalNrc is { } nrc ? $"NRC 0x{nrc:X2}" : null);

        if (sid == 0x22 && tx.FinalKind == UdsFinalKind.Positive && tx.Responses.Count > 0)
            return InterpretDid(tx, diag, display);

        if (sid == 0x31 && tx.FinalKind == UdsFinalKind.Positive && req.Length >= 4)
        {
            var routineId = (ushort)((req[2] << 8) | req[3]);
            var routine = diag.Routines.FirstOrDefault(r => r.Id == routineId);
            return new(display, null, routine?.Name, null, null,
                routine is null ? $"例程 0x{routineId:X4} 无定义" : null);
        }

        return new(display, null, null, null, null, null);
    }

    private static UdsInterpretation InterpretDid(
        UdsOfflineTransaction tx, UdsOfflineDiagnostics diag, string display)
    {
        var req = tx.Request.Payload;
        if (req.Length < 3) return new(display, null, null, null, UdsDegradeReason.PayloadTooShort, "请求缺 DID");
        var did = (ushort)((req[1] << 8) | req[2]);
        var res = tx.Responses[^1].Payload;
        if (res.Length < 3) return new(display, null, null, null, UdsDegradeReason.PayloadTooShort, "响应缺 DID");
        if (((res[1] << 8) | res[2]) != did)
            return new(display, null, null, null, UdsDegradeReason.IncompleteData, "响应 DID 回显不一致");

        var def = diag.Dids.FirstOrDefault(d => d.Id == did);
        if (def is null)
        {
            var reason = diag.OdxLoaded ? UdsDegradeReason.OdxMissingDefinition : UdsDegradeReason.NoOdx;
            return new(display, null, null, null, reason, $"DID 0x{did:X4} 无定义");
        }
        if (res.Length - 3 < def.LengthBytes)
            return new(display, def.Name, null, null, UdsDegradeReason.PayloadTooShort,
                $"数据 {res.Length - 3} 字节 < 定义 {def.LengthBytes} 字节");
        if (def.Fields.Count == 0)
            return new(display, def.Name, null, null, UdsDegradeReason.NoFieldTable, "定义没有字段表");

        var decoded = DidValueDecoder.Decode(res.AsSpan(3), def.Fields);
        var text = string.Join("; ", decoded.Select(d =>
            d.Unit.Length == 0 ? $"{d.Name}={d.PhysicalValue}" : $"{d.Name}={d.PhysicalValue} {d.Unit}"));
        return new(display, def.Name, null, text, null, null);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsServiceInterpreterTests"`
Expected: PASS（4 个用例）。

- [ ] **Step 5: Commit checkpoint**

```bash
git add src/PeakCan.Host.Core/Analysis/Uds/UdsOfflineDiagnostics.cs \
        src/PeakCan.Host.Core/Analysis/Uds/UdsDegradeReason.cs \
        src/PeakCan.Host.Core/Analysis/Uds/UdsInterpretation.cs \
        src/PeakCan.Host.Core/Analysis/Uds/UdsServiceInterpreter.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Uds/UdsServiceInterpreterTests.cs
git commit -m "feat(uds): interpret offline services with explicit ODX degradation"
```

---

### Task 7: 分析编排、报告与噪声汇总

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsOfflineAnalysisRequest.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsNoiseSummary.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsOfflineReport.cs`
- Create: `src/PeakCan.Host.Core/Analysis/Uds/UdsOfflineAnalyzer.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Analysis/Uds/UdsOfflineAnalyzerTests.cs`

**Interfaces:**
- Consumes: Tasks 2–6 的全部类型；`ReplayFrame`。
- Produces: `UdsOfflineAnalysisRequest`；`UdsNoiseSummary`；`UdsOfflineReport`；`UdsOfflineAnalyzer.Analyze(request, ct)`。

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Mixed_log_satisfies_count_closure_and_keeps_noise_evidence()
{
    var req = Request(
        Frame(0x7E0, 0x02, 0x10, 0x01),
        Frame(0x123, 0xAA),                                   // non-diagnostic
        Frame(0x7E0, 0x0F, 0x10),                             // invalid PCI
        Frame(0x7E8, 0x02, 0x50, 0x01));                       // response

    var report = new UdsOfflineAnalyzer().Analyze(req);

    report.Counts.Closes.Should().BeTrue();
    report.Counts.NonDiagnostic.Should().Be(1);
    report.Counts.Noise.Should().Be(1);
    report.Counts.MatchedDiagnostic.Should().Be(2);
    report.Noise.TopIds.Should().Contain(x => x.Id == 0x123 && x.Count == 1);
}

[Fact]
public void Pre_cancelled_token_returns_partial_report_with_completed_stages()
{
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var report = new UdsOfflineAnalyzer().Analyze(Request(Frame(0x7E0, 0x02, 0x10, 0x01)), cts.Token);
    report.IsPartial.Should().BeTrue();
    report.CompletedStages.Should().Contain("classify");
    report.Transactions.Should().BeEmpty();
}

[Fact]
public void Report_is_immutable_snapshot_of_definitions()
{
    var did = new DidDefinition(0xF190, "VIN", "", 17, false);
    var diag = new UdsOfflineDiagnostics(new List<DidDefinition> { did }, Array.Empty<RoutineDefinition>(),
        false, UserJsonLoadStatus.Missing, UserJsonLoadStatus.Missing, "snap");
    var report = new UdsOfflineAnalyzer().Analyze(Request(diag,
        Frame(0x7E0, 0x03, 0x22, 0xF1, 0x90),
        Frame(0x7E8, 0x13, 0x62, 0xF1, 0x90, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
              0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41)));
    report.Transactions.Should().ContainSingle();
    report.Diagnostics.Dids.Should().ContainSingle();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsOfflineAnalyzerTests"`
Expected: FAIL — 类型不存在。

- [ ] **Step 3: Write minimal implementation**

```csharp
public sealed record UdsOfflineAnalysisRequest(
    Guid ReportId,
    string SourceId,
    string SourcePath,
    IReadOnlyList<ReplayFrame> Frames,
    UdsOfflineAddress Address,
    UdsOfflineDiagnostics Diagnostics,
    string EngineVersion);

public sealed record UdsNoiseIdCount(uint Id, bool IsExtended, int Count, int FirstEvidenceIndex, int LastEvidenceIndex);
public sealed record UdsNoiseReasonCount(UdsNoiseReason Reason, int Count);

public sealed record UdsNoiseSummary(
    IReadOnlyList<UdsNoiseIdCount> TopIds,
    IReadOnlyList<UdsNoiseReasonCount> Reasons,
    int ResponseValidationFailed,
    int DuplicateFinalResponses,
    bool AggregationTruncated,
    int DistinctNonDiagnosticIds);

public sealed record UdsOfflineReport(
    Guid ReportId,
    string SourceId,
    string SourcePath,
    UdsOfflineAddress Address,
    UdsOfflineDiagnostics Diagnostics,
    string EngineVersion,
    double LogStart,
    double LogEnd,
    UdsFrameCounts Counts,
    UdsNoiseSummary Noise,
    IReadOnlyList<UdsFrameEvidence> Evidence,
    IReadOnlyList<UdsOfflineTransaction> Transactions,
    IReadOnlyList<UdsOfflinePdu> OrphanResponses,
    IReadOnlyList<UdsTransportWarning> Warnings,
    bool IsPartial,
    string? PartialReason,
    IReadOnlyList<string> CompletedStages);
```

```csharp
public sealed class UdsOfflineAnalyzer
{
    public UdsOfflineReport Analyze(UdsOfflineAnalysisRequest request, CancellationToken ct = default)
    {
        var stages = new List<string>();
        var ordered = StableOrder(request.Frames);
        var logStart = ordered.Length == 0 ? 0 : ordered[0].Timestamp;
        var logEnd = ordered.Length == 0 ? 0 : ordered[^1].Timestamp;

        var classes = new UdsFrameClassifier().Classify(ordered, request.Address);
        stages.Add("classify");
        if (ct.IsCancellationRequested) return Partial("classify", classes, request, ordered, stages);

        var re = new OfflineIsoTpReassembler().Reassemble(ordered, classes, request.Address);
        stages.Add("reassemble");
        if (ct.IsCancellationRequested) return Partial("reassemble", classes, request, ordered, stages);

        var build = new UdsTransactionBuilder().Build(re.Pdus);
        stages.Add("pair");
        if (ct.IsCancellationRequested) return Partial("pair", classes, request, ordered, stages);

        var timed = new UdsSessionTimingTracker().Apply(build.Transactions);
        stages.Add("timing");

        var interpreted = timed.Select(t => t with
        {
            Interpretation = new UdsServiceInterpreter().Interpret(t, request.Diagnostics),
        }).ToArray();
        stages.Add("interpret");

        var noiseIndices = classes.Where(c => c.Class == UdsFrameClass.Noise).Select(c => c.EvidenceIndex)
            .Concat(re.ExtraNoiseIndices).Concat(build.ValidationFailures.SelectMany(p => p.Evidence))
            .Distinct().ToArray();
        var finalClasses = classes.Select(c =>
            noiseIndices.Contains(c.EvidenceIndex) && c.Class == UdsFrameClass.MatchedDiagnostic
                ? c with { Class = UdsFrameClass.Noise, NoiseReason = UdsNoiseReason.ResponseValidationFailed }
                : c).ToArray();
        var counts = UdsFrameCounts.From(finalClasses, ordered.Length);

        var noise = BuildNoiseSummary(classes, noiseIndices, build, ordered);
        var evidence = finalClasses.Where(c => c.Class != UdsFrameClass.NonDiagnostic)
            .Select(c => ToEvidence(c, ordered[c.EvidenceIndex])).ToArray();
        return new UdsOfflineReport(
            request.ReportId, request.SourceId, request.SourcePath, request.Address,
            request.Diagnostics, request.EngineVersion, logStart, logEnd, counts, noise,
            evidence, interpreted, build.OrphanResponses, re.Warnings, false, null, stages);
    }

    private static ReplayFrame[] StableOrder(IReadOnlyList<ReplayFrame> frames)
        => frames.Select((f, i) => (f, i)).OrderBy(x => x.f.Timestamp).ThenBy(x => x.i)
                 .Select(x => x.f).ToArray();
}
```

`UdsOfflineTransaction` 增加 `public UdsInterpretation? Interpretation { get; init; }`。

`Partial(...)` 用现有已完成阶段构造 `IsPartial=true` 的报告（事务集合为空，计数仍来自分类），`PartialReason` 写 `"cancelled at <stage>"`。

`BuildNoiseSummary` 的职责：把 `NonDiagnostic` 帧按 `(Id, IsExtended)` 聚合（Top 20；超出置 `AggregationTruncated=true` 并给出 distinct 数）；按 `UdsNoiseReason` 统计噪声原因；`ResponseValidationFailed = build.ValidationFailures.Count`，`DuplicateFinalResponses = build.DuplicateFinalResponses.Count`。

`ToEvidence(c, f)`：`FrameKind` 取 `ClassicIsoTpPci.TryParse` 的 `Kind.ToString()`（失败写 `"Invalid"`），其余字段直接来自 `ReplayFrame` 与分类结果。

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsOfflineAnalyzerTests"`
Expected: PASS（3 个用例）。

- [ ] **Step 5: Commit checkpoint**

```bash
git add src/PeakCan.Host.Core/Analysis/Uds/UdsOfflineAnalysisRequest.cs \
        src/PeakCan.Host.Core/Analysis/Uds/UdsNoiseSummary.cs \
        src/PeakCan.Host.Core/Analysis/Uds/UdsOfflineReport.cs \
        src/PeakCan.Host.Core/Analysis/Uds/UdsOfflineAnalyzer.cs \
        src/PeakCan.Host.Core/Analysis/Uds/UdsOfflineTransaction.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Uds/UdsOfflineAnalyzerTests.cs
git commit -m "feat(uds): assemble immutable offline report with noise summary"
```

---

### Task 8: App 层 ASC/BLF 加载边界

**Files:**
- Create: `src/PeakCan.Host.App/Services/Uds/IUdsOfflineTraceLoader.cs`
- Create: `src/PeakCan.Host.App/Services/Uds/UdsOfflineTraceLoader.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/UdsOfflineTraceLoaderTests.cs`

**Interfaces:**
- Consumes: `AscParser`、`BlfParser`、`ReplayOptions`、`ReplayFrame`。
- Produces: `IUdsOfflineTraceLoader.LoadAsync(path, ct)` → `UdsOfflineTraceLoadResult(Frames, Format)`；`UdsOfflineLoadException`。

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task Missing_file_throws_specific_error()
{
    var loader = new UdsOfflineTraceLoader();
    var act = () => loader.LoadAsync(Path.Combine(Path.GetTempPath(), "nope.asc"));
    await act.Should().ThrowAsync<UdsOfflineLoadException>()
        .WithMessage("*不存在*");
}

[Fact]
public async Task File_over_limit_is_rejected_explicitly()
{
    var path = Path.GetTempFileName();
    await File.WriteAllBytesAsync(path, new byte[64]);
    var loader = new UdsOfflineTraceLoader(new ReplayOptions(MaxFileSizeBytes: 16));
    var act = () => loader.LoadAsync(path);
    await act.Should().ThrowAsync<UdsOfflineLoadException>()
        .WithMessage("*200*"); // 消息里给出实际上限
}

[Fact]
public async Task Asc_extension_dispatches_to_asc_parser_and_reports_format()
{
    var path = Path.GetTempFileName();
    await File.WriteAllTextAsync(path,
        "date Mon Nov 26 10:00:00 2026\n" +
        " 0.000 1  7E0             Rx   d 3 02 10 01\n");
    var loader = new UdsOfflineTraceLoader();
    var result = await loader.LoadAsync(path);
    result.Format.Should().Be("ASC");
    result.Frames.Should().ContainSingle();
}

[Fact]
public async Task Unsupported_extension_is_rejected()
{
    var loader = new UdsOfflineTraceLoader();
    var act = () => loader.LoadAsync(Path.ChangeExtension(Path.GetTempFileName(), ".txt"));
    await act.Should().ThrowAsync<UdsOfflineLoadException>();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsOfflineTraceLoaderTests"`
Expected: FAIL — 类型不存在。

- [ ] **Step 3: Write minimal implementation**

```csharp
public interface IUdsOfflineTraceLoader
{
    Task<UdsOfflineTraceLoadResult> LoadAsync(string path, CancellationToken ct = default);
}

public sealed record UdsOfflineTraceLoadResult(IReadOnlyList<ReplayFrame> Frames, string Format);

public sealed class UdsOfflineLoadException(string message) : Exception(message);

public sealed class UdsOfflineTraceLoader(ReplayOptions? options = null) : IUdsOfflineTraceLoader
{
    private readonly ReplayOptions _options = options ?? ReplayOptions.Default;

    public async Task<UdsOfflineTraceLoadResult> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new UdsOfflineLoadException($"文件不存在：{path}");
        var length = new FileInfo(path).Length;
        if (length > _options.MaxFileSizeBytes)
            throw new UdsOfflineLoadException(
                $"文件 {length} 字节超过上限 {_options.MaxFileSizeBytes} 字节（200 MB 边界）");

        await using var stream = File.OpenRead(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".asc" => new(await AscParser.ParseAsync(stream, _options, logger: null, ct), "ASC"),
            ".blf" => new(await BlfParser.ParseAsync(stream, _options, logger: null, ct), "BLF"),
            _ => throw new UdsOfflineLoadException($"不支持的日志格式：{ext}（仅支持 .asc / .blf）"),
        };
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsOfflineTraceLoaderTests"`
Expected: PASS（4 个用例）。

- [ ] **Step 5: Commit checkpoint**

```bash
git add src/PeakCan.Host.App/Services/Uds/IUdsOfflineTraceLoader.cs \
        src/PeakCan.Host.App/Services/Uds/UdsOfflineTraceLoader.cs \
        tests/PeakCan.Host.App.Tests/Services/UdsOfflineTraceLoaderTests.cs
git commit -m "feat(uds): load ASC/BLF for offline analysis with explicit limits"
```

---

### Task 9: 报告导出与脱敏

**Files:**
- Create: `src/PeakCan.Host.App/Services/Uds/UdsOfflineReportWriter.cs`
- Create: `src/PeakCan.Host.App/Services/Uds/UdsOfflineRedactor.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/UdsOfflineReportWriterTests.cs`
- Test helper: `tests/PeakCan.Host.App.Tests/Services/UdsOfflineFixtures.cs`

**Interfaces:**
- Consumes: `UdsOfflineReport`。
- Produces: `UdsOfflineReportWriter.WriteMarkdown(report, redact)`；`WriteCsv(report, redact)`；`UdsOfflineRedactor.ShouldMask(UdsOfflineTransaction)`。

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Markdown_contains_closure_counts_and_noise_summary()
{
    var text = UdsOfflineReportWriter.WriteMarkdown(Fixtures.Report(), redact: false);
    text.Should().Contain("# UDS 离线分析报告");
    text.Should().Contain("input_frames=");
    text.Should().Contain("non_diagnostic=");
    text.Should().Contain("response_validation_failed=");
    text.Should().Contain("duplicate_final_responses=");
}

[Fact]
public void Csv_starts_with_key_value_summary_rows_then_header()
{
    var lines = UdsOfflineReportWriter.WriteCsv(Fixtures.Report(), redact: false)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries);
    lines[0].Should().StartWith("# report_id=");
    lines.Should().Contain(l => l.StartsWith("# input_frames="));
    lines.Should().Contain(l => l.StartsWith("transaction_index,"));
}

[Fact]
public void Redacted_markdown_hides_security_seed_and_did_value()
{
    var text = UdsOfflineReportWriter.WriteMarkdown(Fixtures.ReportWithSensitiveData(), redact: true);
    text.Should().NotContain("11223344");   // SecurityAccess seed
    text.Should().NotContain("ABCDEFGH");   // DID ASCII 值
    text.Should().Contain("0x7E0");          // CAN ID 保留
}

[Fact]
public void Partial_report_is_marked()
{
    var text = UdsOfflineReportWriter.WriteMarkdown(Fixtures.PartialReport(), redact: false);
    text.Should().Contain("partial");
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsOfflineReportWriterTests"`
Expected: FAIL — 类型不存在。

- [ ] **Step 3: Write minimal implementation**

```csharp
public static class UdsOfflineRedactor
{
    private static readonly HashSet<byte> SensitiveRequests = new() { 0x27, 0x2E, 0x34, 0x36, 0x37 };

    public static bool ShouldMask(UdsOfflineTransaction tx)
        => tx.Request.Payload.Length > 0 && SensitiveRequests.Contains(tx.Request.Payload[0]);
}
```

```csharp
public static class UdsOfflineReportWriter
{
    public static string WriteMarkdown(UdsOfflineReport r, bool redact)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# UDS 离线分析报告");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- report_id: `{r.ReportId}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- engine: `{r.EngineVersion}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- source: `{(redact ? "（已脱敏）" : r.SourcePath)}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- address: ch{r.Address.Channel} 0x{r.Address.RequestId:X3} → 0x{r.Address.ResponseId:X3} ext={r.Address.IsExtended}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- odx_loaded: {r.Diagnostics.OdxLoaded}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- did_user_json: {r.Diagnostics.DidUserJsonStatus}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- routine_user_json: {r.Diagnostics.RoutineUserJsonStatus}");
        if (r.IsPartial) sb.AppendLine(CultureInfo.InvariantCulture, $"- partial: {r.PartialReason}");
        sb.AppendLine();
        sb.AppendLine("## 计数");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- input_frames={r.Counts.Input}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- matched={r.Counts.MatchedDiagnostic}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- non_diagnostic={r.Counts.NonDiagnostic}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- noise={r.Counts.Noise}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- unsupported={r.Counts.UnsupportedInput}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- response_validation_failed={r.Noise.ResponseValidationFailed}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- duplicate_final_responses={r.Noise.DuplicateFinalResponses}");
        sb.AppendLine();
        sb.AppendLine("## 噪声（按 CAN ID）");
        foreach (var n in r.Noise.TopIds)
            sb.AppendLine(CultureInfo.InvariantCulture, $"- 0x{n.Id:X3} ext={n.IsExtended} x{n.Count} [{n.FirstEvidenceIndex}..{n.LastEvidenceIndex}]");
        if (r.Noise.AggregationTruncated)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- 聚合已截断：共 {r.Noise.DistinctNonDiagnosticIds} 个 ID");
        sb.AppendLine();
        sb.AppendLine("## 事务");
        sb.AppendLine("| # | service | final | pending | nrc | first_ms | total_ms | evidence |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        for (var i = 0; i < r.Transactions.Count; i++)
        {
            var t = r.Transactions[i];
            var data = redact && UdsOfflineRedactor.ShouldMask(t) ? "（已脱敏）" : Hex(t.Request.Payload);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {i + 1} | {t.Interpretation?.ServiceDisplay ?? "?"} | {t.FinalKind} | {t.PendingCount} | " +
                $"{(t.FinalNrc is { } n ? $"0x{n:X2}" : "—")} | {t.FirstResponseMs:F3} | {t.TotalMs:F3} | " +
                $"{string.Join(",", t.Request.Evidence)} |");
            if (!redact) sb.AppendLine(CultureInfo.InvariantCulture, $"  请求数据：{data}");
        }
        return sb.ToString();
    }

    public static string WriteCsv(UdsOfflineReport r, bool redact)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# report_id={r.ReportId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# input_frames={r.Counts.Input}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# matched={r.Counts.MatchedDiagnostic}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# non_diagnostic={r.Counts.NonDiagnostic}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# noise={r.Counts.Noise}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# unsupported={r.Counts.UnsupportedInput}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# response_validation_failed={r.Noise.ResponseValidationFailed}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# duplicate_final_responses={r.Noise.DuplicateFinalResponses}");
        sb.AppendLine("transaction_index,service,final_kind,pending_count,nrc,first_ms,total_ms,first_evidence,last_evidence");
        for (var i = 0; i < r.Transactions.Count; i++)
        {
            var t = r.Transactions[i];
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{i + 1},{t.Interpretation?.ServiceDisplay},{t.FinalKind},{t.PendingCount}," +
                $"{(t.FinalNrc is { } n ? "0x" + n.ToString("X2") : "")},{t.FirstResponseMs:F3},{t.TotalMs:F3}," +
                $"{t.Request.Evidence.FirstOrDefault()},{t.Request.Evidence.LastOrDefault()}");
        }
        return sb.ToString();
    }

    private static string Hex(byte[] data) => Convert.ToHexString(data);
}
```

> 脱敏只影响导出文本；`UdsOfflineReport` 本身不被修改（同一快照）。

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsOfflineReportWriterTests"`
Expected: PASS（4 个用例）。

- [ ] **Step 5: Commit checkpoint**

```bash
git add src/PeakCan.Host.App/Services/Uds/UdsOfflineReportWriter.cs \
        src/PeakCan.Host.App/Services/Uds/UdsOfflineRedactor.cs \
        tests/PeakCan.Host.App.Tests/Services/UdsOfflineReportWriterTests.cs
git commit -m "feat(uds): export offline reports with noise counts and redaction"
```

---

### Task 10: AI 提示词、Handoff 服务与 TraceViewer 接线

**Files:**
- Create: `src/PeakCan.Host.App/Services/Uds/UdsChatHandoffPayload.cs`
- Create: `src/PeakCan.Host.App/Services/Uds/UdsOfflineAiPromptBuilder.cs`
- Create: `src/PeakCan.Host.App/Services/Uds/ITraceViewerChatHandoffService.cs`
- Create: `src/PeakCan.Host.App/Services/Uds/TraceViewerChatHandoffService.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/ChatFlow.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/UdsOfflineAiPromptBuilderTests.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerUdsHandoffTests.cs`

**Interfaces:**
- Consumes: `UdsOfflineReport`、`UdsOfflineTransaction`、`WindowHostService`、`TraceViewerView`、`TraceViewerViewModel`。
- Produces: `UdsChatHandoffPayload`；`UdsOfflineAiPromptBuilder.Build(report, transaction?)`；`ITraceViewerChatHandoffService.Handoff(payload)`；`TraceViewerViewModel.AcceptChatHandoff(payload)`、`internal string BuildSystemMessageForTest()`。

- [ ] **Step 1: Write the failing tests**

```csharp
// Prompt builder
[Fact]
public void Prompt_carries_report_identity_counts_and_odx_state()
{
    var report = Fixtures.ReportNoOdx();
    var payload = UdsOfflineAiPromptBuilder.Build(report, report.Transactions[0]);
    payload.Prompt.Should().Contain(report.ReportId.ToString());
    payload.Prompt.Should().Contain("输入帧");
    payload.Prompt.Should().Contain("响应校验失败");
    payload.Prompt.Should().Contain("未加载 ODX");
    payload.Prompt.Should().NotContain("AB CD EF"); // 不塞原始敏感载荷
}

// TraceViewer VM
[Fact]
public void AcceptChatHandoff_prefills_without_sending()
{
    var vm = ChatVm();
    var payload = new UdsChatHandoffPayload("问题", "按发送开始", Guid.NewGuid(), "summary", "tx");
    vm.AcceptChatHandoff(payload);

    vm.ChatInput.Should().Be("问题");
    vm.ChatPrefillHint.Should().Be("按发送开始");
    vm.RightPanelSelectedIndex.Should().Be(TraceViewerViewModel.ChatPanelTabIndex);
    vm.ChatInputFocusTicket.Should().BeGreaterThan(0);

    var system = vm.BuildSystemMessageForTest();
    system.Should().Contain(payload.ReportId.ToString());
    system.Should().Contain("其它 trace/DBC/NM 数据不是证据");
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsOfflineAiPromptBuilderTests|FullyQualifiedName~TraceViewerUdsHandoffTests"`
Expected: FAIL — 类型/方法不存在。

- [ ] **Step 3: Write minimal implementation**

```csharp
public sealed record UdsChatHandoffPayload(
    string Prompt,
    string Hint,
    Guid ReportId,
    string ReportSummary,
    string? SelectedTransaction);

public static class UdsOfflineAiPromptBuilder
{
    public static UdsChatHandoffPayload Build(UdsOfflineReport r, UdsOfflineTransaction? tx)
    {
        var sb = new StringBuilder();
        sb.AppendLine("请基于以下 UDS 离线分析报告解读选定事务。");
        sb.AppendLine(CultureInfo.InvariantCulture, $"report_id: {r.ReportId}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"地址: ch{r.Address.Channel} 0x{r.Address.RequestId:X3} → 0x{r.Address.ResponseId:X3} ext={r.Address.IsExtended}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"输入帧 {r.Counts.Input} / 匹配诊断 {r.Counts.MatchedDiagnostic} / 非诊断 {r.Counts.NonDiagnostic} / 噪声 {r.Counts.Noise} / 未支持 {r.Counts.UnsupportedInput}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"响应校验失败 {r.Noise.ResponseValidationFailed}；重复最终响应 {r.Noise.DuplicateFinalResponses}");
        sb.AppendLine(r.Diagnostics.OdxLoaded
            ? "ODX 已加载。"
            : "未加载 ODX：DID/例程按原始值显示，不要解释为异常。");
        if (tx is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"事务: {tx.Interpretation?.ServiceDisplay} final={tx.FinalKind} pending={tx.PendingCount} " +
                $"nrc={(tx.FinalNrc is { } n ? $"0x{n:X2}" : "—")} first={tx.FirstResponseMs:F3}ms total={tx.TotalMs:F3}ms");
            sb.AppendLine("已知事实: " + string.Join("; ", tx.Unknowns.Count == 0 ? new[] { "见报告" } : tx.Unknowns));
        }
        sb.AppendLine("要求: 区分日志事实、假设与不可知；不要声称 ECU 未发送；做“没有/只有”断言前引用上面的计数。");
        return new(sb.ToString(), "已填入 UDS 事务解读问题，按发送开始分析",
            r.ReportId, $"UDS 离线报告 {r.ReportId}", tx?.Interpretation?.ServiceDisplay);
    }
}
```

```csharp
public interface ITraceViewerChatHandoffService
{
    void Handoff(UdsChatHandoffPayload payload);
}

public sealed class TraceViewerChatHandoffService(
    WindowHostService host,
    Func<TraceViewerViewModel> viewModelFactory) : ITraceViewerChatHandoffService
{
    public void Handoff(UdsChatHandoffPayload payload)
    {
        var win = host.Show(WindowKey.TraceViewer, () => new TraceViewerView(viewModelFactory()));
        if (win is null) return;
        if (win is TraceViewerView view && view.DataContext is TraceViewerViewModel vm)
            vm.AcceptChatHandoff(payload);
        if (!win.IsVisible) win.Show(); else win.Activate();
    }
}
```

`TraceViewerViewModel`（`ChatFlow.cs`）：

```csharp
private UdsChatHandoffPayload? _udsHandoff;

internal void AcceptChatHandoff(UdsChatHandoffPayload payload)
{
    _udsHandoff = payload;
    PrefillChatInput(payload.Prompt);
    ChatPrefillHint = payload.Hint;
    RightPanelSelectedIndex = ChatPanelTabIndex;
    ChatInputFocusTicket++;
}

internal string BuildSystemMessageForTest() => BuildSystemMessage();

// BuildSystemMessage 末尾追加：
if (_udsHandoff is { } h)
{
    sb.AppendLine();
    sb.AppendLine("UDS 离线分析 Handoff 规则:");
    sb.AppendLine(CultureInfo.InvariantCulture, $"- ReportId: {h.ReportId}");
    sb.AppendLine("- 本次问题只以该报告为证据；其它 trace/DBC/NM 数据不是证据。");
    sb.AppendLine("- 首版不新增 UDS AI 工具；只用 Handoff 提供的事实，不调用无关工具补造证据。");
    sb.AppendLine("- 事务列表不等于总线全部流量；做“没有/只有”断言前先引用报告噪声计数。");
    sb.AppendLine("- 未加载 ODX 时不得把 DID/例程原始 hex 解释为异常。");
    sb.AppendLine("- 区分日志事实、假设与不可知，不把时间相关性写成因果。");
}
```

> 说明：`TraceViewerChatHandoffService` 是 10 行窗口胶水，窗口生命周期由 `WindowHostService` 覆盖；单测覆盖 `AcceptChatHandoff` + 提示词 + 后续 `AppHostBuilder` 解析，不在无 WPF 环境下伪造 `Window`。

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsOfflineAiPromptBuilderTests|FullyQualifiedName~TraceViewerUdsHandoffTests"`
Expected: PASS。

- [ ] **Step 5: Commit checkpoint**

```bash
git add src/PeakCan.Host.App/Services/Uds/UdsChatHandoffPayload.cs \
        src/PeakCan.Host.App/Services/Uds/UdsOfflineAiPromptBuilder.cs \
        src/PeakCan.Host.App/Services/Uds/ITraceViewerChatHandoffService.cs \
        src/PeakCan.Host.App/Services/Uds/TraceViewerChatHandoffService.cs \
        src/PeakCan.Host.App/ViewModels/TraceViewerViewModel/ChatFlow.cs \
        tests/PeakCan.Host.App.Tests/Services/UdsOfflineAiPromptBuilderTests.cs \
        tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerUdsHandoffTests.cs
git commit -m "feat(uds): hand off offline analysis to the existing AI chat"
```

---

### Task 11: 日志分析 ViewModel

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/Uds/UdsLogAnalysisViewModel.cs`
- Create: `src/PeakCan.Host.App/Services/Uds/IUdsExportPrompt.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/Uds/UdsLogAnalysisViewModelTests.cs`

**Interfaces:**
- Consumes: `IUdsOfflineTraceLoader`、`UdsOfflineAnalyzer`、`UdsOfflineReportWriter`、`UdsOfflineAiPromptBuilder`、`ITraceViewerChatHandoffService`、`IFileDialogService`、`DidDatabase`、`RoutineDatabase`、`OdxImportViewModel`。
- Produces: `UdsLogAnalysisViewModel`（文件/地址/分析/取消/报告/筛选/选中事务/状态/导出/AI）；`IUdsExportPrompt` + `UdsExportChoice { Redacted, Raw, Cancel }`。

- [ ] **Step 1: Write the failing tests**

```csharp
private static UdsLogAnalysisViewModel Vm(
    IUdsOfflineTraceLoader loader, ITraceViewerChatHandoffService? handoff = null,
    IUdsExportPrompt? exportPrompt = null)
    => new(loader, new UdsOfflineAnalyzer(), handoff ?? new FakeHandoff(), new FakeDialogs(),
        exportPrompt ?? new FakeExportPrompt(UdsExportChoice.Cancel),
        new DidDatabase(userJsonPath: null), new RoutineDatabase(userJsonPath: null),
        new OdxImportViewModel(new FakeOdxService(OdxImportResult.Ok(0, 0, 0, Array.Empty<string>()))));

[Fact]
public async Task Analyze_produces_report_and_visible_rows()
{
    var vm = Vm(new FakeLoader(Fixtures.ThreeFrameLog()));
    vm.FilePath = "fake.asc";
    await vm.AnalyzeAsync();

    vm.Report.Should().NotBeNull();
    vm.VisibleTransactions.Should().ContainSingle();
    vm.Status.Should().Contain("分析完成");
}

[Fact]
public async Task No_matching_frames_shows_address_specific_empty_state()
{
    var vm = Vm(new FakeLoader(new[] { Frame(0x123, 0x01) }));
    vm.FilePath = "fake.asc";
    await vm.AnalyzeAsync();

    vm.Status.Should().Contain("当前设置下未识别到诊断事务");
    vm.Report.Should().NotBeNull();
    vm.Report!.Transactions.Should().BeEmpty();
}

[Fact]
public async Task Cancel_produces_partial_report()
{
    var vm = Vm(new FakeLoader(Fixtures.ManyFrames())); // fake 不抛取消，便于观察分析器部分结果
    vm.FilePath = "fake.asc";
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    await vm.AnalyzeAsync(cts.Token);
    vm.IsPartial.Should().BeTrue();
    vm.Status.Should().Contain("部分");
}

[Fact]
public async Task Interpret_selected_sends_handoff_without_provider_call()
{
    var handoff = new FakeHandoff();
    var vm = Vm(new FakeLoader(Fixtures.ThreeFrameLog()), handoff);
    vm.FilePath = "fake.asc";
    await vm.AnalyzeAsync();
    vm.SelectedTransaction = vm.Report!.Transactions[0];

    vm.InterpretSelectedCommand.Execute(null);

    handoff.Payload.Should().NotBeNull();
    handoff.Payload!.Prompt.Should().Contain(vm.Report.ReportId.ToString());
}

[Fact]
public async Task Export_cancel_writes_nothing()
{
    var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "r.md");
    var vm = Vm(new FakeLoader(Fixtures.ThreeFrameLog()),
        exportPrompt: new FakeExportPrompt(UdsExportChoice.Cancel, savePath: path));
    vm.FilePath = "fake.asc";
    await vm.AnalyzeAsync();

    await vm.ExportAsync();

    File.Exists(path).Should().BeFalse();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsLogAnalysisViewModelTests"`
Expected: FAIL — 类型不存在。

- [ ] **Step 3: Write minimal implementation**

```csharp
public enum UdsExportChoice { Redacted, Raw, Cancel }

public interface IUdsExportPrompt
{
    string? ChooseSavePath(out bool isCsv);       // null = 取消
    bool ConfirmOverwrite(string path);
    UdsExportChoice AskRedaction();
}
```

```csharp
public sealed partial class UdsLogAnalysisViewModel : ObservableObject
{
    private readonly IUdsOfflineTraceLoader _loader;
    private readonly UdsOfflineAnalyzer _analyzer;
    private readonly ITraceViewerChatHandoffService _handoff;
    private readonly IFileDialogService _dialogs;
    private readonly IUdsExportPrompt _exportPrompt;
    private readonly DidDatabase _dids;
    private readonly RoutineDatabase _routines;
    private readonly OdxImportViewModel _odxImport;
    private CancellationTokenSource? _cts;

    public UdsLogAnalysisViewModel(
        IUdsOfflineTraceLoader loader,
        UdsOfflineAnalyzer analyzer,
        ITraceViewerChatHandoffService handoff,
        IFileDialogService dialogs,
        IUdsExportPrompt exportPrompt,
        DidDatabase dids,
        RoutineDatabase routines,
        OdxImportViewModel odxImport)
    {
        _loader = loader;
        _analyzer = analyzer;
        _handoff = handoff;
        _dialogs = dialogs;
        _exportPrompt = exportPrompt;
        _dids = dids;
        _routines = routines;
        _odxImport = odxImport;
    }

    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string _requestIdText = "7E0";
    [ObservableProperty] private string _responseIdText = "7E8";
    [ObservableProperty] private string _channelText = "1";
    [ObservableProperty] private bool _isExtended;
    [ObservableProperty] private string _status = "未选择文件";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isPartial;
    [ObservableProperty] private UdsOfflineReport? _report;
    [ObservableProperty] private UdsOfflineTransaction? _selectedTransaction;
    [ObservableProperty] private string _filterText = "";

    public ObservableCollection<UdsOfflineTransaction> VisibleTransactions { get; } = new();
    public ObservableCollection<UdsFrameEvidence> SelectedEvidence { get; } = new();
    public UdsNoiseSummary? Noise => Report?.Noise;
    public bool HasReport => Report is not null;
    public bool IsOdxLoaded => Report?.Diagnostics.OdxLoaded ?? false;

    [RelayCommand]
    private async Task OpenLogAsync()
    {
        var path = _dialogs.ShowOpenDialog("ASC/BLF (*.asc;*.blf)|*.asc;*.blf|所有文件 (*.*)|*.*");
        if (path is null) return;
        FilePath = path;
        await AnalyzeAsync();
    }

    public async Task AnalyzeAsync(CancellationToken externalToken = default)
    {
        if (string.IsNullOrWhiteSpace(FilePath)) { Status = "未选择文件"; return; }
        CancelCurrent();
        Report = null; // 旧报告立即失效（D11）
        VisibleTransactions.Clear();
        IsBusy = true; IsPartial = false;
        Status = "分析中…";
        var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _cts = cts;
        try
        {
            var path = FilePath;
            var address = ParseAddress();
            var frames = await _loader.LoadAsync(path, cts.Token);
            var request = new UdsOfflineAnalysisRequest(
                Guid.NewGuid(), Path.GetFileName(path), path, frames.Frames, address,
                SnapshotDiagnostics(), EngineVersion());
            var report = await Task.Run(() => _analyzer.Analyze(request, cts.Token), cts.Token);
            Report = report;
            IsPartial = report.IsPartial;
            RefreshVisible();
            Status = report.IsPartial
                ? $"部分报告：{report.PartialReason}"
                : report.Transactions.Count == 0
                    ? "当前设置下未识别到诊断事务"
                    : $"分析完成：{report.Transactions.Count} 个事务";
        }
        catch (OperationCanceledException)
        {
            Status = "已取消";
        }
        catch (UdsOfflineLoadException ex)
        {
            Status = "读取失败：" + ex.Message;
        }
        catch (FormatException)
        {
            Status = "地址格式错误：请用十六进制填写通道/请求 ID/响应 ID";
        }
        finally
        {
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            IsBusy = false;
        }
    }

    private UdsOfflineDiagnostics SnapshotDiagnostics() => new(
        _dids.All.ToArray(), _routines.All.ToArray(), _odxImport.HasSuccessfulImport,
        _dids.UserJsonLoadStatus, _routines.UserJsonLoadStatus,
        SnapshotId: _dids.UserJsonLoadStatus + "/" + _routines.UserJsonLoadStatus);

    private static string EngineVersion()
        => typeof(UdsLogAnalysisViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";

    [RelayCommand] private Task Analyze() => AnalyzeAsync();
    // 生成 AnalyzeCommand；测试/调用方也可直接 await AnalyzeAsync。

    [RelayCommand] private void Cancel() => CancelCurrent();
    private void CancelCurrent() => _cts?.Cancel();

    [RelayCommand] private void InterpretSelected()
    {
        if (Report is null) return;
        _handoff.Handoff(UdsOfflineAiPromptBuilder.Build(Report, SelectedTransaction));
    }

    [RelayCommand] private Task Export() => ExportAsync();

    public async Task ExportAsync()
    {
        if (Report is null) { Status = "没有可导出的报告"; return; }
        var path = _exportPrompt.ChooseSavePath(out var isCsv);
        if (path is null) return;
        if (!_exportPrompt.ConfirmOverwrite(path)) return;
        var choice = _exportPrompt.AskRedaction();
        if (choice == UdsExportChoice.Cancel) return;
        try
        {
            var text = isCsv
                ? UdsOfflineReportWriter.WriteCsv(Report, choice == UdsExportChoice.Redacted)
                : UdsOfflineReportWriter.WriteMarkdown(Report, choice == UdsExportChoice.Redacted);
            await File.WriteAllTextAsync(path, text, Encoding.UTF8);
            Status = (choice == UdsExportChoice.Redacted ? "已脱敏导出：" : "已导出：") + path;
        }
        catch (Exception ex) { Status = "导出失败：" + ex.Message; }
    }

    partial void OnFilterTextChanged(string value) => RefreshVisible();
    partial void OnSelectedTransactionChanged(UdsOfflineTransaction? value) => RefreshSelectedEvidence();

    private void RefreshSelectedEvidence()
    {
        SelectedEvidence.Clear();
        if (Report is null || SelectedTransaction is null) return;
        var indices = SelectedTransaction.Request.Evidence
            .Concat(SelectedTransaction.Responses.SelectMany(r => r.Evidence))
            .ToHashSet();
        foreach (var e in Report.Evidence.Where(e => indices.Contains(e.Index)))
            SelectedEvidence.Add(e);
    }

    private void RefreshVisible()
    {
        VisibleTransactions.Clear();
        if (Report is null) return;
        foreach (var t in Report.Transactions)
        {
            if (FilterText.Length == 0
                || (t.Interpretation?.ServiceDisplay.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ?? false)
                || t.FinalKind.ToString().Contains(FilterText, StringComparison.OrdinalIgnoreCase)
                || (t.FinalNrc is { } n && n.ToString("X2").Contains(FilterText, StringComparison.OrdinalIgnoreCase)))
                VisibleTransactions.Add(t);
        }
    }

    private UdsOfflineAddress ParseAddress()
    {
        uint Parse(string s) => Convert.ToUInt32(s.Replace("0x", "", StringComparison.OrdinalIgnoreCase).Trim(), 16);
        return new UdsOfflineAddress(ushort.Parse(ChannelText), Parse(RequestIdText),
            Parse(ResponseIdText), IsExtended);
    }
}
```

> 地址解析失败时抛 `FormatException`，由调用方在 `AnalyzeAsync` 外层捕获并显示“地址格式错误”，不静默用默认值。

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsLogAnalysisViewModelTests"`
Expected: PASS（5 个用例）。

- [ ] **Step 5: Commit checkpoint**

```bash
git add src/PeakCan.Host.App/ViewModels/Uds/UdsLogAnalysisViewModel.cs \
        src/PeakCan.Host.App/Services/Uds/IUdsExportPrompt.cs \
        tests/PeakCan.Host.App.Tests/ViewModels/Uds/UdsLogAnalysisViewModelTests.cs
git commit -m "feat(uds): add offline log analysis view model"
```

---

### Task 12: 日志分析 View 与 UDS 窗口页签

**Files:**
- Create: `src/PeakCan.Host.App/Views/UdsLogAnalysisView.xaml`
- Create: `src/PeakCan.Host.App/Views/UdsLogAnalysisView.xaml.cs`
- Modify: `src/PeakCan.Host.App/Windows/UdsWindow.xaml`
- Modify: `src/PeakCan.Host.App/ViewModels/Uds/UdsViewModel.cs`
- Test: `tests/PeakCan.Host.App.Tests/Views/UdsLogAnalysisViewLayoutTests.cs`

**Interfaces:**
- Consumes: `UdsLogAnalysisViewModel`。
- Produces: 新页签 `日志分析`；`UdsViewModel.SelectedTabIndex`、`IsOnlineTab`、`IsLogAnalysisTab`、`LogAnalysis`。

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Window_has_log_analysis_tab_bound_to_child_view_model()
{
    var doc = LoadXaml("src", "PeakCan.Host.App", "Windows", "UdsWindow.xaml");
    var tabs = doc.Descendants(P + "TabItem").ToList();
    tabs.Should().Contain(t => (string?)t.Attribute("Header") == "日志分析");
    var tab = tabs.Single(t => (string?)t.Attribute("Header") == "日志分析");
    tab.Descendants().Should().Contain(e =>
        (((string?)e.Attribute("DataContext")) ?? "").Contains("LogAnalysis", StringComparison.Ordinal));
}

[Fact]
public void Online_controls_are_hidden_when_log_analysis_tab_selected()
{
    var doc = LoadXaml("src", "PeakCan.Host.App", "Windows", "UdsWindow.xaml");
    doc.Descendants(P + "Button").Should().Contain(b =>
        (string?)b.Attribute("Content") == "默认" &&
        (((string?)b.Attribute("Visibility")) ?? "").Contains("IsOnlineTab", StringComparison.Ordinal));
    // ODX 按钮不绑定在线页签显隐（加载 ODX 在离线页仍可用）
    var odx = doc.Descendants(P + "Button").Single(b => (string?)b.Attribute("Content") == "加载 ODX…");
    (((string?)odx.Attribute("Visibility")) ?? "").Should().NotContain("IsOnlineTab");
}

[Fact]
public void Analysis_view_exposes_required_commands()
{
    var doc = LoadXaml("src", "PeakCan.Host.App", "Views", "UdsLogAnalysisView.xaml");
    doc.Descendants(P + "Button").Select(b => (string?)b.Attribute("Command"))
        .Should().Contain(new[] { "{Binding OpenLogCommand}", "{Binding AnalyzeCommand}" })
        .And.Contain(c => c!.Contains("Export"));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsLogAnalysisViewLayoutTests"`
Expected: FAIL — 文件/页签不存在。

- [ ] **Step 3: Write minimal implementation**

`UdsViewModel` 增加：

```csharp
public const int LogAnalysisTabIndex = 4;

[ObservableProperty]
private int _selectedTabIndex;

public bool IsOnlineTab => SelectedTabIndex != LogAnalysisTabIndex;
public bool IsLogAnalysisTab => SelectedTabIndex == LogAnalysisTabIndex;

public UdsLogAnalysisViewModel? LogAnalysis { get; }
```

在现有 7 参生产 ctor 末尾加 `UdsLogAnalysisViewModel? logAnalysis = null`，赋值 `LogAnalysis = logAnalysis;`；back-compat ctor 继续传 null。

```csharp
partial void OnSelectedTabIndexChanged(int value)
{
    OnPropertyChanged(nameof(IsOnlineTab));
    OnPropertyChanged(nameof(IsLogAnalysisTab));
}
```

`UdsWindow.xaml` 根节点补 `xmlns:views="clr-namespace:PeakCan.Host.App.Views"`。

`UdsWindow.xaml`：

```xml
<TabControl SelectedIndex="{Binding SelectedTabIndex, Mode=TwoWay}">
  <!-- 现有 DID / 例程 / DTC / 刷写 页签保持；在线控件加：
       Visibility="{Binding IsOnlineTab, Converter={StaticResource BoolToVis}}" -->
  <TabItem Header="日志分析">
    <views:UdsLogAnalysisView DataContext="{Binding LogAnalysis}"/>
  </TabItem>
</TabControl>
```

顶部会话条逐个控件加 `Visibility="{Binding IsOnlineTab, Converter={StaticResource BoolToVis}}"`（默认/扩展/编程、SecurityAccess、TesterPresent）；`加载 ODX…` 不加。在线输出日志 `Border` 加同样绑定；离线状态由 `UdsLogAnalysisView` 自己显示。

`UdsLogAnalysisView.xaml` 结构（最小可用）：

```xml
<UserControl x:Class="PeakCan.Host.App.Views.UdsLogAnalysisView" ...>
  <DockPanel Margin="8">
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal">
      <Button Content="打开 ASC/BLF" Command="{Binding OpenLogCommand}" Padding="6,2"/>
      <Button Content="重新分析" Command="{Binding AnalyzeCommand}" Padding="6,2" Margin="8,0,0,0"/>
      <Button Content="取消" Command="{Binding CancelCommand}" Padding="6,2" Margin="8,0,0,0"/>
      <Button Content="导出报告" Command="{Binding ExportCommand}" Padding="6,2" Margin="8,0,0,0"/>
      <Button Content="在 AI 中解读选中事务" Command="{Binding InterpretSelectedCommand}" Padding="6,2" Margin="8,0,0,0"/>
      <TextBlock Text="{Binding Status}" Margin="12,0,0,0" VerticalAlignment="Center"/>
    </StackPanel>
    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,6">
      <TextBlock Text="通道" VerticalAlignment="Center"/>
      <TextBox Text="{Binding ChannelText}" Width="40" Margin="4,0,8,0"/>
      <TextBlock Text="请求 ID" VerticalAlignment="Center"/>
      <TextBox Text="{Binding RequestIdText}" Width="60" Margin="4,0,8,0"/>
      <TextBlock Text="响应 ID" VerticalAlignment="Center"/>
      <TextBox Text="{Binding ResponseIdText}" Width="60" Margin="4,0,8,0"/>
      <CheckBox Content="扩展帧" IsChecked="{Binding IsExtended}" Margin="8,0,0,0"/>
      <TextBox Text="{Binding FilterText}" Width="160" Margin="16,0,0,0"
               ToolTip="按服务名/结果/NRC 过滤"/>
    </StackPanel>
    <Grid>
      <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/><ColumnDefinition Width="320"/>
      </Grid.ColumnDefinitions>
      <DataGrid ItemsSource="{Binding VisibleTransactions}" SelectedItem="{Binding SelectedTransaction}"
                AutoGenerateColumns="False" IsReadOnly="True">
        <DataGrid.Columns>
          <DataGridTextColumn Header="服务" Binding="{Binding Interpretation.ServiceDisplay}"/>
          <DataGridTextColumn Header="结果" Binding="{Binding FinalKind}"/>
          <DataGridTextColumn Header="pending" Binding="{Binding PendingCount}"/>
          <DataGridTextColumn Header="NRC" Binding="{Binding FinalNrc, StringFormat=0x{0:X2}}"/>
          <DataGridTextColumn Header="首次(ms)" Binding="{Binding FirstResponseMs, StringFormat=F3}"/>
          <DataGridTextColumn Header="总(ms)" Binding="{Binding TotalMs, StringFormat=F3}"/>
        </DataGrid.Columns>
      </DataGrid>
      <StackPanel Grid.Column="1" Margin="8,0,0,0">
        <TextBlock Text="噪声汇总" FontWeight="SemiBold"/>
        <TextBlock Text="{Binding Noise.AggregationTruncated, StringFormat='聚合已截断：{0}'}"/>
        <TextBlock Text="响应校验失败 / 重复最终响应" Margin="0,4,0,0"/>
        <TextBlock Text="{Binding Noise.ResponseValidationFailed}"/>
        <TextBlock Text="{Binding Noise.DuplicateFinalResponses}"/>
        <TextBlock Text="{Binding SelectedTransaction.Unknowns, StringFormat='不可知：{0}'}" TextWrapping="Wrap"/>
        <TextBlock Text="原始证据" FontWeight="SemiBold" Margin="0,8,0,4"/>
        <ListBox ItemsSource="{Binding SelectedEvidence}" Height="180">
          <ListBox.ItemTemplate>
            <DataTemplate>
              <TextBlock Text="{Binding Display}" FontFamily="Consolas" FontSize="11"/>
            </DataTemplate>
          </ListBox.ItemTemplate>
        </ListBox>
      </StackPanel>
    </Grid>
  </DockPanel>
</UserControl>
```

`OpenLogCommand` 在 VM 里用 `IFileDialogService.ShowOpenDialog("ASC/BLF (*.asc;*.blf)|*.asc;*.blf")` 选择文件，选中后调用 `AnalyzeAsync`。

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsLogAnalysisViewLayoutTests"`
Expected: PASS（3 个用例）。

- [ ] **Step 5: Commit checkpoint**

```bash
git add src/PeakCan.Host.App/Views/UdsLogAnalysisView.xaml \
        src/PeakCan.Host.App/Views/UdsLogAnalysisView.xaml.cs \
        src/PeakCan.Host.App/Windows/UdsWindow.xaml \
        src/PeakCan.Host.App/ViewModels/Uds/UdsViewModel.cs \
        tests/PeakCan.Host.App.Tests/Views/UdsLogAnalysisViewLayoutTests.cs
git commit -m "feat(uds): add the log-analysis tab and hide online controls while active"
```

---

### Task 13: DI 接线

**Files:**
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs`
- Create: `src/PeakCan.Host.App/Services/Uds/WpfUdsExportPrompt.cs`
- Test: `tests/PeakCan.Host.App.Tests/Composition/AppHostBuilderTests.cs`（追加）

**Interfaces:**
- Consumes: 上述全部服务与 VM。
- Produces: DI 可解析 `IUdsOfflineTraceLoader`、`UdsOfflineAnalyzer`、`ITraceViewerChatHandoffService`、`UdsLogAnalysisViewModel`；`UdsViewModel.LogAnalysis` 非空。

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Offline_analysis_services_and_child_view_model_resolve()
{
    using var host = BuildHost();
    host.Services.GetRequiredService<IUdsOfflineTraceLoader>().Should().NotBeNull();
    host.Services.GetRequiredService<UdsOfflineAnalyzer>().Should().NotBeNull();
    host.Services.GetRequiredService<ITraceViewerChatHandoffService>().Should().NotBeNull();
    var uds = host.Services.GetRequiredService<UdsViewModel>();
    uds.LogAnalysis.Should().NotBeNull();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~Offline_analysis_services_and_child_view_model_resolve"`
Expected: FAIL — 服务未注册。

- [ ] **Step 3: Write minimal implementation**

在 `AppHostBuilder.cs` 的 UDS 区块加入：

```csharp
builder.Services.AddSingleton<IUdsOfflineTraceLoader, UdsOfflineTraceLoader>();
builder.Services.AddSingleton<UdsOfflineAnalyzer>();
builder.Services.AddSingleton<IUdsExportPrompt, WpfUdsExportPrompt>();
builder.Services.AddSingleton<ITraceViewerChatHandoffService>(sp =>
    new TraceViewerChatHandoffService(
        sp.GetRequiredService<PeakCan.Host.App.Services.Ui.WindowHostService>(),
        () => sp.GetRequiredService<TraceViewerViewModel>()));
builder.Services.AddSingleton<UdsLogAnalysisViewModel>(sp => new UdsLogAnalysisViewModel(
    sp.GetRequiredService<IUdsOfflineTraceLoader>(),
    sp.GetRequiredService<UdsOfflineAnalyzer>(),
    sp.GetRequiredService<ITraceViewerChatHandoffService>(),
    sp.GetRequiredService<PeakCan.HIL.Core.IFileDialogService>(),
    sp.GetRequiredService<IUdsExportPrompt>(),
    sp.GetRequiredService<PeakCan.Host.Core.Uds.Database.DidDatabase>(),
    sp.GetRequiredService<PeakCan.Host.Core.Uds.Database.RoutineDatabase>(),
    sp.GetRequiredService<OdxImportViewModel>()));
```

`IUdsExportPrompt` 生产实现 `WpfUdsExportPrompt`（SaveFileDialog + MessageBox 三选一）放在 `Services/Uds/`；测试注入 fake。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug --filter "FullyQualifiedName~Offline_analysis_services_and_child_view_model_resolve"`
Expected: PASS。

- [ ] **Step 5: Commit checkpoint**

```bash
git add src/PeakCan.Host.App/Composition/AppHostBuilder.cs \
        src/PeakCan.Host.App/Services/Uds/WpfUdsExportPrompt.cs \
        tests/PeakCan.Host.App.Tests/Composition/AppHostBuilderTests.cs
git commit -m "feat(uds): wire offline analysis services into DI"
```

---

### Task 14: Golden ASC/BLF 等价 + 变异验证

**Files:**
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/Uds/UdsOfflineGoldenTests.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/Uds/Fixtures/offline-diagnostic.asc`
- Test helpers: BLF synth（复制 `BlfParserTests` 的 `WriteFileHeader` / `WriteObject` 模式）

**Interfaces:**
- Consumes: `AscParser`、`BlfParser`、`UdsOfflineAnalyzer`。
- Produces: 规范化报告等价断言 + 变异执行记录。

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Asc_and_blf_produce_the_same_normalized_report()
{
    var asc = await AscParser.ParseAsync(AscStream(), ReplayOptions.Default, null, default);
    var blf = await BlfParser.ParseAsync(BlfStream(), ReplayOptions.Default, null, default);

    var req = Fixtures.Request(asc.Length);
    var a = new UdsOfflineAnalyzer().Analyze(req with { Frames = asc });
    var b = new UdsOfflineAnalyzer().Analyze(req with { Frames = blf, ReportId = a.ReportId });

    Normalize(a).Should().BeEquivalentTo(Normalize(b));
}
```

`Normalize` 取事务的服务、FinalKind、PendingCount、NRC、首次/总耗时（四舍五入到 0.001）、证据顺序与噪声计数，不含文件格式元数据。

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsOfflineGoldenTests"`
Expected: FAIL — fixture/helper 不存在。

- [ ] **Step 3: Write fixture 与 BLF synth**

ASC fixture 覆盖：多帧 DID、连续 `0x78` 后成功、最终 NRC、未闭合请求、其他 ID 周期帧噪声。BLF 用同一帧序列合成（`ObjTypeCanMessage`，时间戳按 ASC 的秒 × 1e9）。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~UdsOfflineGoldenTests"`
Expected: PASS。

- [ ] **Step 5: 变异验证（不提交变异）**

按 spec §15.4 逐条执行并用对应测试证明变红，然后还原：

```text
1. 删除 P2* ×10            → UdsSessionTimingTrackerTests P2_and_P2star_are_read_in_correct_units
2. 每个 0x78 建独立事务     → UdsTransactionBuilderTests Pending_nrc_does_not_close_transaction
3. 丢弃方向未知帧           → OfflineIsoTpReassemblerTests Direction_unknown_frames_still_reassemble_by_can_id
4. P2 超阈值删除在途请求    → UdsTransactionBuilderTests Late_response_still_closes_the_request
5. 删除噪声桶               → UdsFrameClassifierTests Counts_close_on_mixed_log
6. 去掉响应长度/回显校验    → UdsTransactionBuilderTests Fake_0x76_with_wrong_block_counter_does_not_close_transfer
7. 第二个最终响应归孤立     → UdsTransactionBuilderTests Duplicate_final_response_marks_transaction_ambiguous
8. 脱敏只遮首字节           → UdsOfflineReportWriterTests Redacted_markdown_hides_security_seed_and_did_value
```

- [ ] **Step 6: 定向回归（只跑本次相关测试）**

```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj -c Debug \
  --filter "FullyQualifiedName~Analysis.Uds"
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj -c Debug \
  --filter "FullyQualifiedName~UdsOffline|FullyQualifiedName~UdsLogAnalysis|FullyQualifiedName~TraceViewerUdsHandoff|FullyQualifiedName~OdxImportViewModelTests"
dotnet build PeakCan.Host.slnx -c Debug
```

Expected: 全部 PASS，构建 0 error / 0 warning。**不跑全量测试。**

- [ ] **Step 7: Commit checkpoint**

```bash
git add tests/PeakCan.Host.Core.Tests/Analysis/Uds/UdsOfflineGoldenTests.cs \
        tests/PeakCan.Host.Core.Tests/Analysis/Uds/Fixtures/offline-diagnostic.asc
git commit -m "test(uds): golden ASC/BLF parity and mutation coverage"
```

---

## Self-Review

**1. Spec coverage**

| Spec 章节 | 任务 |
|---|---|
| §5 架构 / §5.1 分层 | Task 2–7（Core）、Task 8–11（App） |
| §6.1–6.6 契约 | Task 2、3、6、7 |
| §7 帧选择 / 重组 / 分类 | Task 2、3 |
| §8 配对 / 响应校验 | Task 4 |
| §9 时序 | Task 5 |
| §10 服务与 ODX 降级 | Task 6 |
| §11 窗口 / VM / 生命周期 | Task 11、12 |
| §12 错误处理 | Task 2、3、4、7、11 |
| §13 导出 / 脱敏 | Task 9、11 |
| §14 AI Handoff | Task 10 |
| §15 测试 / 变异 | 各任务 + Task 14 |
| §16 验收 | Task 14 定向回归 + 人工 WPF 验收（未自动化） |
| §17 风险 | 通过保守标记（歧义、噪声、未闭合）实现 |

**2. Placeholder scan:** 无 TBD/TODO；所有代码块给出具体类型与关键实现；`Fixtures.*` 为测试项目内真实辅助类（Task 9 建 `UdsOfflineFixtures`，Task 14 建 Core golden fixture）。

**3. Type consistency:** `UdsOfflineDiagnostics` 在 Task 6 定义、Task 7/11 消费；`UdsOfflineTransaction` 的 `FirstResponseMs/TotalMs/TimingSource` 在 Task 5 增加、`Interpretation` 在 Task 7 增加；`UserJsonLoadStatus` 在 Task 1 定义、Task 6 消费。`ResponseValidationFailed` / `DuplicateFinalResponse` 命名按 spec 最新版。

**4. Review Focus:** 5 条输入类均有对应测试（Task 3 孤儿 CF、Task 4 伪 0x76/重复响应、Task 4/5 迟到、Task 8/11 空态与上限、Task 6/11 ODX）。

## Execution Handoff

计划已保存。执行前请确认：

- 执行方式：**Native**（本会话逐任务实现）还是 **Subagent-driven**（每任务一个实现者 + 审查者）。
- 本计划默认不自动 commit/push；每个 Commit checkpoint 需你显式授权。
- 全量测试不在本计划内；人工 WPF 验收（FD/大日志渲染、页签切换手感）仍需你在真机完成。

