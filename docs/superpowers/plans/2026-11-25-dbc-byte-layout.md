# DBC 页字节位图实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 DBC 页选中消息时显示载荷的字节/位分布图（经典 CAN 8 字节、CAN FD 最多 64 字节），静态渲染、零定时器。

**Architecture:** 位映射与快照计算放在 `PeakCan.Host.Core.Analysis.Dbc.SignalBitLayout`（纯函数，与 `SignalDecoder` 语义逐行同构）；App 侧 `DbcViewModel` 持有选中消息 / mux / 快照，并投影成可绑定的行；`ByteLayoutView` 只做渲染与 hover。

**Tech Stack:** C# / .NET 10 / WPF / CommunityToolkit.Mvvm / xUnit + FluentAssertions。

**Spec:** `docs/superpowers/specs/2026-11-25-dbc-byte-layout-design.md`

## Global Constraints

- **不修改 `peakcan-hil-core`**；`SignalBitLayout` 只落在 `PeakCan.Host.Core`（spec D1）。
- 不新增 NuGet 依赖。
- TDD：每个生产代码改动前先写失败测试；只跑定向测试，不跑全量。
- `TreatWarningsAsErrors=true`、`AnalysisMode=Recommended`：构建必须 0 warning。
- 用户面向 / 业务注释用中文；技术 API、协议字段注释用英文。
- **本计划不包含 commit 步骤**：用户明确要求后才提交。
- 改动 `DbcView.xaml` / `DbcViewModel.cs` 前先 `git status --porcelain`；若发现非本任务的改动，先停下来问（共享工作目录）。

## Review Focus

1. **畸形 DBC（DLC>64 / Length=0 / Length>64 / StartBit 越界）**：必须不抛异常、不静默丢格；用户应看到逐信号标注。→ Task 1、Task 2。
2. **mux 默认值与无匹配分支**：默认取最小值，不是固定 0；无匹配时复用器仍画，且给出可见提示。→ Task 2、Task 4。
3. **普通消息重叠**：后声明信号覆盖，先声明者记录在 `OverlappedSignals`，表格与 hover 必须标注重叠。→ Task 2。
4. **搜索 / 重载后的选中生命周期**：搜索过滤与 DBC 重载不能让位图悄悄消失或指向旧消息。→ Task 5。
5. **布局↔解码漂移**：位序必须与 `SignalDecoder` 一致；用手写常量向量做差分测试，禁止用布局函数生成特征序列。→ Task 2。

## File Structure

| 文件 | 责任 |
|---|---|
| `src/PeakCan.Host.Core/Analysis/Dbc/SignalBitLayout.cs` | 纯布局计算：位映射、mux、重叠、越界、诊断、快照 |
| `tests/PeakCan.Host.Core.Tests/Analysis/Dbc/SignalBitLayoutTests.cs` | 纯函数单测（映射 / 边界 / mux / 重叠 / 解码一致性） |
| `tests/PeakCan.Host.Core.Tests/Analysis/Dbc/SignalBitLayoutFixtureTests.cs` | 真实 DBC fixture 验收 |
| `tests/PeakCan.Host.Core.Tests/TestData/Dbc/byte-layout-demo.dbc` | 与 mockup 等价的演示 DBC |
| `src/PeakCan.Host.App/ViewModels/DbcMessageViewModel.cs` | 新增 `RawMessage`；`Signals` 字符串列表保持不动 |
| `src/PeakCan.Host.App/ViewModels/DbcViewModel/LayoutFlow.partial.cs`（新增） | 选中 / mux / 快照 / `VisibleSignals` / `LayoutRows` |
| `src/PeakCan.Host.App/ViewModels/DbcViewModel/LoadingFlow.partial.cs` | 重载时清空选中 |
| `src/PeakCan.Host.App/ViewModels/DbcViewModel/SearchFlow.partial.cs` | 搜索后按 `Id + Name` 恢复选中 |
| `src/PeakCan.Host.App/Composition/Converters/ByteLayoutPaletteBrushConverter.cs`（新增） | `PaletteIndex` → 色块笔刷 |
| `src/PeakCan.Host.App/Views/ByteLayoutView.xaml` + `.xaml.cs`（新增） | 位图、mux 下拉、警告、hover 卡片 |
| `src/PeakCan.Host.App/Views/DbcView.xaml` | `SelectedItem` 绑定 + RowDetails 替换 |
| `src/PeakCan.Host.App/Themes/Colors.xaml` | `ByteLayoutFree` + `ByteLayoutSignal1..9` |
| `src/PeakCan.Host.App/App.xaml` | 注册 `ByteLayoutPaletteBrush` 转换器 |

---

### Task 1: Core 位映射与快照骨架

**Files:**
- Create: `src/PeakCan.Host.Core/Analysis/Dbc/SignalBitLayout.cs`
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/Dbc/SignalBitLayoutTests.cs`

**Interfaces:**
- Consumes: `PeakCan.HIL.Core.Dbc.Message`、`Signal`、`ByteOrder`。
- Produces:
  - `SignalBitLayout.Compute(Message message)`（本任务；Task 2 追加 `ushort? selectedMuxValue` 可选参数）
  - `SignalBitLayoutSnapshot`、`BitCell`、`SignalSegment`、`SignalDiagnostic`、`LayoutWarning`。

- [ ] **Step 1: 写失败测试（Intel / Motorola / 越界 / 长度异常 / DLC 截断）**

```csharp
using FluentAssertions;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Dbc;
using Xunit;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.Host.Core.Tests.Analysis.Dbc;

public class SignalBitLayoutTests
{
    private static Signal Sig(
        string name, ushort start, byte len,
        ByteOrder order = ByteOrder.LittleEndian,
        bool muxor = false, bool muxed = false, ushort? mux = null)
        => new(name, start, len, order, DbcValueType.Unsigned, 1, 0, 0, 0,
               "", Array.Empty<string>(), muxor, muxed, mux);

    private static Message Msg(byte dlc, params Signal[] signals)
        => new(0x100, "M", dlc, "ECU", signals, false, null);

    private static BitCell Cell(SignalBitLayoutSnapshot s, int byteIndex, int bitIndex)
        => s.Cells.Single(c => c.ByteIndex == byteIndex && c.BitIndex == bitIndex);

    [Fact]
    public void Compute_IntelSignal_MapsBitsFromLsb()
    {
        var snapshot = SignalBitLayout.Compute(Msg(8, Sig("S", 0, 12)));

        for (var bit = 0; bit < 8; bit++)
            Cell(snapshot, 0, bit).OwnerSignal.Should().Be("S");
        for (var bit = 0; bit < 4; bit++)
            Cell(snapshot, 1, bit).OwnerSignal.Should().Be("S");
        Cell(snapshot, 1, 4).OwnerSignal.Should().BeNull();
        snapshot.Segments.Single().InRangeBitCount.Should().Be(12);
        snapshot.Segments.Single().OutOfRangeBitCount.Should().Be(0);
    }

    [Fact]
    public void Compute_MotorolaSignal_MapsStartBitAsMsbZero()
    {
        var snapshot = SignalBitLayout.Compute(
            Msg(8, Sig("S", 55, 8, ByteOrder.BigEndian)));

        Cell(snapshot, 6, 0).OwnerSignal.Should().Be("S");
        for (var bit = 1; bit <= 7; bit++)
            Cell(snapshot, 7, bit).OwnerSignal.Should().Be("S");
        Cell(snapshot, 7, 0).OwnerSignal.Should().BeNull();
    }

    [Fact]
    public void Compute_PartialOutOfDlc_KeepsInRangeBitsAndAddsDiagnostic()
    {
        var snapshot = SignalBitLayout.Compute(Msg(8, Sig("S", 60, 8)));

        var segment = snapshot.Segments.Single();
        segment.InRangeBitCount.Should().Be(4);
        segment.OutOfRangeBitCount.Should().Be(4);
        snapshot.Diagnostics.Should().Contain(d =>
            d.SignalName == "S" && d.Code == "partial_out_of_dlc");
        Cell(snapshot, 7, 4).OwnerSignal.Should().Be("S");
    }

    [Fact]
    public void Compute_LengthZeroAndOver64_AreReportedWithoutThrowing()
    {
        var snapshot = SignalBitLayout.Compute(
            Msg(8, Sig("Zero", 0, 0), Sig("Long", 0, 65)));

        snapshot.Segments.Should().HaveCount(2);
        snapshot.Segments.Should().OnlyContain(s => s.Cells.Count == 0);
        snapshot.Diagnostics.Should().Contain(d => d.Code == "length_zero");
        snapshot.Diagnostics.Should().Contain(d => d.Code == "length_unsupported");
    }

    [Fact]
    public void Compute_DlcOver64_TruncatesTo512CellsAndWarns()
    {
        var snapshot = SignalBitLayout.Compute(Msg(65, Sig("S", 0, 8)));

        snapshot.DeclaredDlc.Should().Be(65);
        snapshot.RenderedDlc.Should().Be(64);
        snapshot.Cells.Should().HaveCount(512);
        snapshot.Warnings.Should().Contain(w => w.Code == "dlc_truncated");
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~SignalBitLayoutTests" -v minimal
```
Expected: 编译失败，`SignalBitLayout` / `SignalBitLayoutSnapshot` 等类型不存在。

- [ ] **Step 3: 写最小实现（快照类型 + Intel/Motorola 映射 + 越界/长度/DLC 截断）**

```csharp
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.Host.Core.Analysis.Dbc;

public sealed record BitCell(
    int ByteIndex,
    int BitIndex,
    string? OwnerSignal,
    IReadOnlyList<string> OverlappedSignals);

public sealed record SignalSegment(
    string SignalName,
    int StartBit,
    int Length,
    ByteOrder Order,
    int PaletteIndex,
    IReadOnlyList<BitCell> Cells,
    int InRangeBitCount,
    int OutOfRangeBitCount);

public sealed record SignalDiagnostic(string SignalName, string Code, string Text);

public sealed record LayoutWarning(string Code, string Text);

public sealed record SignalBitLayoutSnapshot(
    int DeclaredDlc,
    int RenderedDlc,
    ushort? EffectiveMuxValue,
    IReadOnlyList<BitCell> Cells,
    IReadOnlyList<SignalSegment> Segments,
    IReadOnlyList<SignalDiagnostic> Diagnostics,
    IReadOnlyList<LayoutWarning> Warnings);

public static class SignalBitLayout
{
    private const int MaxRenderedBytes = 64;
    private const int MaxSignalBits = 64;

    public static SignalBitLayoutSnapshot Compute(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var declaredDlc = message.Dlc;
        var renderedDlc = Math.Min(declaredDlc, MaxRenderedBytes);
        var warnings = new List<LayoutWarning>();
        if (declaredDlc > MaxRenderedBytes)
        {
            warnings.Add(new LayoutWarning(
                "dlc_truncated",
                $"DLC 声明 {declaredDlc} 字节，超出 CAN FD 上限，仅显示前 {MaxRenderedBytes} 字节。"));
        }

        var cellCount = renderedDlc * 8;
        var ownerNames = new string?[cellCount];
        var diagnostics = new List<SignalDiagnostic>();
        var segments = new List<SignalSegment>(message.Signals.Count);

        for (var paletteIndex = 0; paletteIndex < message.Signals.Count; paletteIndex++)
        {
            var signal = message.Signals[paletteIndex];
            var inRange = 0;
            var outOfRange = 0;

            if (signal.Length == 0)
            {
                diagnostics.Add(new SignalDiagnostic(
                    signal.Name, "length_zero", "长度为 0，无位可绘制。"));
            }
            else if (signal.Length > MaxSignalBits)
            {
                diagnostics.Add(new SignalDiagnostic(
                    signal.Name, "length_unsupported",
                    $"长度 {signal.Length} 超过 {MaxSignalBits}，不支持。"));
            }
            else
            {
                for (var i = 0; i < signal.Length; i++)
                {
                    var absBit = signal.StartBit + i;
                    var byteIndex = absBit >> 3;
                    var bitIndex = signal.Order == ByteOrder.LittleEndian
                        ? absBit & 7
                        : 7 - (absBit & 7);
                    if (byteIndex >= renderedDlc)
                    {
                        outOfRange++;
                        continue;
                    }
                    inRange++;
                    ownerNames[byteIndex * 8 + bitIndex] = signal.Name;
                }

                if (inRange > 0 && outOfRange > 0)
                {
                    diagnostics.Add(new SignalDiagnostic(
                        signal.Name, "partial_out_of_dlc",
                        $"部分超出 DLC（{inRange}/{signal.Length} 位在界内）。"));
                }
                else if (inRange == 0)
                {
                    diagnostics.Add(new SignalDiagnostic(
                        signal.Name, "out_of_dlc", "全部超出 DLC。"));
                }
            }

            segments.Add(new SignalSegment(
                signal.Name, signal.StartBit, signal.Length, signal.Order,
                paletteIndex, Array.Empty<BitCell>(), inRange, outOfRange));
        }

        var cells = new BitCell[cellCount];
        for (var cellIndex = 0; cellIndex < cellCount; cellIndex++)
        {
            cells[cellIndex] = new BitCell(
                cellIndex / 8, cellIndex % 8,
                ownerNames[cellIndex], Array.Empty<string>());
        }

        var finalSegments = new List<SignalSegment>(segments.Count);
        foreach (var segment in segments)
        {
            finalSegments.Add(segment with
            {
                Cells = cells
                    .Where(c => string.Equals(
                        c.OwnerSignal, segment.SignalName, StringComparison.Ordinal))
                    .ToList(),
            });
        }

        return new SignalBitLayoutSnapshot(
            declaredDlc, renderedDlc, null, cells, finalSegments, diagnostics, warnings);
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~SignalBitLayoutTests" -v minimal
```
Expected: 5/5 PASS。

---

### Task 2: mux、重叠与布局↔解码一致性

**Files:**
- Modify: `src/PeakCan.Host.Core/Analysis/Dbc/SignalBitLayout.cs`
- Modify: `tests/PeakCan.Host.Core.Tests/Analysis/Dbc/SignalBitLayoutTests.cs`

**Interfaces:**
- Consumes: Task 1 的全部类型。
- Produces: `Compute(Message message, ushort? selectedMuxValue = null)`；填充 `EffectiveMuxValue`、`BitCell.OverlappedSignals`、`overlap` / `mux_no_match` 诊断。

- [ ] **Step 1: 追加失败测试（mux / 重叠 / 解码一致性 / 确定性）**

```csharp
    [Fact]
    public void Compute_Mux_FiltersOtherBranches_AndKeepsSelector()
    {
        var snapshot = SignalBitLayout.Compute(
            Msg(8,
                Sig("Mode", 0, 4, muxor: true),
                Sig("A", 8, 8, muxed: true, mux: 0),
                Sig("B", 16, 8, muxed: true, mux: 1)),
            selectedMuxValue: 1);

        snapshot.EffectiveMuxValue.Should().Be((ushort?)1);
        snapshot.Segments.Select(s => s.SignalName)
            .Should().BeEquivalentTo("Mode", "B");
        Cell(snapshot, 1, 0).OwnerSignal.Should().Be("B");
        Cell(snapshot, 0, 0).OwnerSignal.Should().Be("Mode");
    }

    [Fact]
    public void Compute_Mux_DefaultsToMinimumValue()
    {
        var snapshot = SignalBitLayout.Compute(
            Msg(8,
                Sig("Mode", 0, 4, muxor: true),
                Sig("A", 8, 8, muxed: true, mux: 5),
                Sig("B", 16, 8, muxed: true, mux: 3)),
            selectedMuxValue: null);

        snapshot.EffectiveMuxValue.Should().Be((ushort?)3);
        snapshot.Segments.Select(s => s.SignalName)
            .Should().Contain("B").And.NotContain("A");
    }

    [Fact]
    public void Compute_Mux_NoMatchingBranch_WarnsAndKeepsSelector()
    {
        var snapshot = SignalBitLayout.Compute(
            Msg(8,
                Sig("Mode", 0, 4, muxor: true),
                Sig("A", 8, 8, muxed: true, mux: 0)),
            selectedMuxValue: 9);

        snapshot.EffectiveMuxValue.Should().Be((ushort?)9);
        snapshot.Warnings.Should().Contain(w => w.Code == "mux_no_match");
        snapshot.Segments.Select(s => s.SignalName)
            .Should().BeEquivalentTo("Mode");
    }

    [Fact]
    public void Compute_Overlap_LastDeclaredWins_AndLoserDiagnostic()
    {
        var snapshot = SignalBitLayout.Compute(
            Msg(8,
                Sig("CellVolt_2", 24, 16),
                Sig("BalanceFlags", 39, 8, ByteOrder.BigEndian)));

        var cell = Cell(snapshot, 4, 0);
        cell.OwnerSignal.Should().Be("BalanceFlags");
        cell.OverlappedSignals.Should().Contain("CellVolt_2");
        snapshot.Diagnostics.Should().Contain(d =>
            d.SignalName == "CellVolt_2" && d.Code == "overlap");
        snapshot.Diagnostics.Should().Contain(d =>
            d.SignalName == "BalanceFlags" && d.Code == "overlap");
    }

    [Fact]
    public void Compute_BitPositions_AgreeWithSignalDecoder_OnHandWrittenVectors()
    {
        AssertVector(4, 4, ByteOrder.BigEndian, new byte[] { 0x5A }, 10);

        var fd16 = new byte[40];
        fd16[37] = 0x0F;
        AssertVector(300, 16, ByteOrder.BigEndian, fd16, 0xF000);

        var fd8 = new byte[39];
        fd8[37] = 0xF0;
        fd8[38] = 0x0F;
        AssertVector(300, 8, ByteOrder.LittleEndian, fd8, 0xFF);
    }

    private static void AssertVector(
        ushort start, byte length, ByteOrder order, byte[] payload, ulong expected)
    {
        var signal = Sig("S", start, length, order);
        var snapshot = SignalBitLayout.Compute(
            Msg((byte)payload.Length, signal));
        var segment = snapshot.Segments.Single();
        segment.Cells.Should().HaveCount(length);

        ulong assembled = 0;
        var i = 0;
        foreach (var cell in segment.Cells)
        {
            var bit = (ulong)((payload[cell.ByteIndex] >> cell.BitIndex) & 1);
            assembled = order == ByteOrder.LittleEndian
                ? assembled | (bit << i)
                : (assembled << 1) | bit;
            i++;
        }

        SignalDecoder.DecodeRaw(payload, signal).Should().Be(expected);
        assembled.Should().Be(expected);
    }

    [Fact]
    public void Compute_Repeated_IsDeterministic()
    {
        var message = Msg(8, Sig("A", 0, 8), Sig("B", 4, 8));

        var first = SignalBitLayout.Compute(message);
        var second = SignalBitLayout.Compute(message);

        first.Should().BeEquivalentTo(second);
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~SignalBitLayoutTests" -v minimal
```
Expected: `Compute` 不接受第二个参数 / mux 与重叠断言失败（至少 4 个失败）。

- [ ] **Step 3: 把 `Compute` 替换为最终实现**

保留 Task 1 已写的五个 record 类型；把下面的 `Compute` 和三个 helper 放进 `SignalBitLayout` 类内（helper 放在 `Compute` 之后）。

```csharp
    public static SignalBitLayoutSnapshot Compute(
        Message message, ushort? selectedMuxValue = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        var declaredDlc = message.Dlc;
        var renderedDlc = Math.Min(declaredDlc, MaxRenderedBytes);
        var warnings = new List<LayoutWarning>();
        if (declaredDlc > MaxRenderedBytes)
        {
            warnings.Add(new LayoutWarning(
                "dlc_truncated",
                $"DLC 声明 {declaredDlc} 字节，超出 CAN FD 上限，仅显示前 {MaxRenderedBytes} 字节。"));
        }

        var (hasMux, minMux) = FindMuxRange(message.Signals);
        var effectiveMux = hasMux ? selectedMuxValue ?? minMux : null;
        if (hasMux && effectiveMux is { } mux && !HasMuxBranch(message.Signals, mux))
        {
            warnings.Add(new LayoutWarning("mux_no_match", $"mux={mux} 无匹配信号。"));
        }

        var visible = new List<Signal>();
        foreach (var signal in message.Signals)
        {
            if (signal.IsMultiplexed
                && !signal.IsMultiplexor
                && signal.MultiplexValue != effectiveMux)
            {
                continue;
            }
            visible.Add(signal);
        }

        var cellCount = renderedDlc * 8;
        var claimants = new List<int>?[cellCount];
        var ownerIndex = new int[cellCount];
        Array.Fill(ownerIndex, -1);

        var signalCells = new List<int>[visible.Count];
        var inRange = new int[visible.Count];
        var outOfRange = new int[visible.Count];
        var diagnostics = new List<SignalDiagnostic>();

        for (var signalIndex = 0; signalIndex < visible.Count; signalIndex++)
        {
            var signal = visible[signalIndex];
            signalCells[signalIndex] = new List<int>();

            if (signal.Length == 0)
            {
                diagnostics.Add(new SignalDiagnostic(
                    signal.Name, "length_zero", "长度为 0，无位可绘制。"));
                continue;
            }
            if (signal.Length > MaxSignalBits)
            {
                diagnostics.Add(new SignalDiagnostic(
                    signal.Name, "length_unsupported",
                    $"长度 {signal.Length} 超过 {MaxSignalBits}，不支持。"));
                continue;
            }

            for (var i = 0; i < signal.Length; i++)
            {
                var absBit = signal.StartBit + i;
                var byteIndex = absBit >> 3;
                var bitIndex = signal.Order == ByteOrder.LittleEndian
                    ? absBit & 7
                    : 7 - (absBit & 7);
                if (byteIndex >= renderedDlc)
                {
                    outOfRange[signalIndex]++;
                    continue;
                }

                inRange[signalIndex]++;
                var cellIndex = byteIndex * 8 + bitIndex;
                signalCells[signalIndex].Add(cellIndex);
                (claimants[cellIndex] ??= new List<int>()).Add(signalIndex);
            }

            if (inRange[signalIndex] > 0 && outOfRange[signalIndex] > 0)
            {
                diagnostics.Add(new SignalDiagnostic(
                    signal.Name, "partial_out_of_dlc",
                    $"部分超出 DLC（{inRange[signalIndex]}/{signal.Length} 位在界内）。"));
            }
            else if (inRange[signalIndex] == 0)
            {
                diagnostics.Add(new SignalDiagnostic(
                    signal.Name, "out_of_dlc", "全部超出 DLC。"));
            }
        }

        for (var cellIndex = 0; cellIndex < cellCount; cellIndex++)
        {
            if (claimants[cellIndex] is { Count: > 0 } claims)
                ownerIndex[cellIndex] = claims[^1];
        }

        var cells = new BitCell[cellCount];
        for (var cellIndex = 0; cellIndex < cellCount; cellIndex++)
        {
            var claims = claimants[cellIndex];
            if (claims is null || claims.Count == 0)
            {
                cells[cellIndex] = new BitCell(
                    cellIndex / 8, cellIndex % 8, null, Array.Empty<string>());
                continue;
            }

            var owner = ownerIndex[cellIndex];
            var overlapped = claims.Count <= 1
                ? (IReadOnlyList<string>)Array.Empty<string>()
                : claims.Take(claims.Count - 1)
                    .Select(index => visible[index].Name)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            cells[cellIndex] = new BitCell(
                cellIndex / 8, cellIndex % 8, visible[owner].Name, overlapped);
        }

        AddOverlapDiagnostics(visible, claimants, diagnostics);

        var segments = new List<SignalSegment>(visible.Count);
        for (var signalIndex = 0; signalIndex < visible.Count; signalIndex++)
        {
            var signal = visible[signalIndex];
            var owned = new List<BitCell>();
            foreach (var cellIndex in signalCells[signalIndex])
            {
                if (ownerIndex[cellIndex] == signalIndex)
                    owned.Add(cells[cellIndex]);
            }

            segments.Add(new SignalSegment(
                signal.Name,
                signal.StartBit,
                signal.Length,
                signal.Order,
                signalIndex,
                owned,
                inRange[signalIndex],
                outOfRange[signalIndex]));
        }

        return new SignalBitLayoutSnapshot(
            declaredDlc,
            renderedDlc,
            effectiveMux,
            cells,
            segments,
            diagnostics,
            warnings);
    }

    private static (bool HasMux, ushort? Min) FindMuxRange(IReadOnlyList<Signal> signals)
    {
        var hasMux = false;
        ushort? min = null;
        foreach (var signal in signals)
        {
            if (!signal.IsMultiplexed || signal.MultiplexValue is not { } value) continue;
            hasMux = true;
            if (min is null || value < min) min = value;
        }
        return (hasMux, min);
    }

    private static bool HasMuxBranch(IReadOnlyList<Signal> signals, ushort mux)
    {
        foreach (var signal in signals)
        {
            if (signal.IsMultiplexed && signal.MultiplexValue == mux) return true;
        }
        return false;
    }

    private static void AddOverlapDiagnostics(
        IReadOnlyList<Signal> visible,
        IReadOnlyList<List<int>?> claimants,
        List<SignalDiagnostic> diagnostics)
    {
        var others = new Dictionary<int, SortedSet<string>>();
        var order = new List<int>();
        foreach (var claims in claimants)
        {
            if (claims is not { Count: > 1 }) continue;
            for (var a = 0; a < claims.Count; a++)
            {
                if (!others.TryGetValue(claims[a], out var set))
                {
                    set = new SortedSet<string>(StringComparer.Ordinal);
                    others[claims[a]] = set;
                    order.Add(claims[a]);
                }
                for (var b = 0; b < claims.Count; b++)
                {
                    if (a != b) set.Add(visible[claims[b]].Name);
                }
            }
        }

        foreach (var index in order)
        {
            diagnostics.Add(new SignalDiagnostic(
                visible[index].Name, "overlap",
                $"与 {string.Join("、", others[index])} 重叠。"));
        }
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~SignalBitLayoutTests" -v minimal
```
Expected: 全部 PASS（含三组手写解码向量）。

---

### Task 3: `DbcMessageViewModel.RawMessage`

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/DbcMessageViewModel.cs`
- Modify: `tests/PeakCan.Host.App.Tests/ViewModels/DbcMessageViewModelTests.cs`

**Interfaces:**
- Consumes: `Message`。
- Produces: `DbcMessageViewModel.RawMessage`（`Message?`，`From` 时赋值）；`Signals` / `Dlc` 字符串保持不变。

- [ ] **Step 1: 写失败测试**

```csharp
    [Fact]
    public void From_Retains_RawMessage_For_Layout()
    {
        var msg = MakeMessage();

        var vm = DbcMessageViewModel.From(msg);

        vm.RawMessage.Should().BeSameAs(msg);
        vm.Signals.Should().NotBeEmpty("字符串列表仍供搜索与旧 RowDetails 使用");
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~DbcMessageViewModelTests" -v minimal
```
Expected: 编译失败，`RawMessage` 不存在。

- [ ] **Step 3: 最小实现**

在 `DbcMessageViewModel` 的 `Signals` 属性后新增：

```csharp
    /// <summary>
    /// 原始解析消息。位图 / mux / DLC 需要强类型字段；字符串投影
    /// <see cref="Signals"/> / <see cref="Dlc"/> 保持原样供搜索与兼容使用。
    /// </summary>
    public Message? RawMessage { get; init; }
```

在 `From` 的对象初始化器中加入：

```csharp
            RawMessage = m,
```

- [ ] **Step 4: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~DbcMessageViewModelTests" -v minimal
```
Expected: 全部 PASS。

---

### Task 4: `DbcViewModel` 选中 / mux / 快照 / 绑定行

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/DbcViewModel/LayoutFlow.partial.cs`
- Create: `tests/PeakCan.Host.App.Tests/ViewModels/DbcLayoutViewModelTests.cs`

**Interfaces:**
- Consumes: `SignalBitLayout.Compute(Message, ushort?)`、`DbcMessageViewModel.RawMessage`。
- Produces:
  - `DbcViewModel.SelectedMessage`（`DbcMessageViewModel?`）
  - `SelectedMuxValue`（`ushort?`）、`MuxValues`（`ObservableCollection<ushort>`）、`HasMux`（bool）
  - `LayoutSnapshot`（`SignalBitLayoutSnapshot?`）、`LayoutEmptyText`（string）
  - `VisibleSignals`（`ObservableCollection<DbcSignalRow>`）
  - `LayoutRows`（`ObservableCollection<ByteLayoutRow>`）
  - `DbcSignalRow` / `ByteLayoutRow` / `ByteLayoutCell`（App 视图投影）
- 规则：切换消息时 `SelectedMuxValue` 重置为最小值；mux 变化的重新计算由 `UpdateLayout` 统一负责。

- [ ] **Step 1: 写失败测试**

```csharp
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core.Dbc;
using Xunit;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels;

public class DbcLayoutViewModelTests
{
    private static Signal Sig(
        string name, ushort start, byte len,
        ByteOrder order = ByteOrder.LittleEndian,
        bool muxor = false, bool muxed = false, ushort? mux = null)
        => new(name, start, len, order, DbcValueType.Unsigned, 1, 0, 0, 0,
               "", Array.Empty<string>(), muxor, muxed, mux);

    private static Message MuxMessage()
        => new(0x100, "MuxMsg", 8, "ECU", new List<Signal>
        {
            Sig("Mode", 0, 4, muxor: true),
            Sig("A", 8, 8, muxed: true, mux: 0),
            Sig("B", 16, 8, muxed: true, mux: 1),
        }, true, 0);

    private static void RaiseLoaded(DbcService svc, params Message[] messages)
    {
        var doc = new DbcDocument(
            "", new List<Node>(), messages,
            new Dictionary<uint, Message>(),
            new Dictionary<string, ValueTable>());
        svc.GetType().GetEvent(nameof(DbcService.DbcLoaded))!.RaiseMethod(svc, doc);
    }

    private static DbcViewModel NewVm(out DbcService svc)
    {
        svc = new DbcService(NullLogger<DbcService>.Instance);
        return new DbcViewModel(
            svc, new SignalViewModel(), NullLogger<DbcViewModel>.Instance);
    }

    [Fact]
    public void Selecting_MuxMessage_Populates_MuxValues_And_DefaultsToMinimum()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, MuxMessage());

        vm.SelectedMessage = vm.Messages.Single();

        vm.HasMux.Should().BeTrue();
        vm.MuxValues.Should().Equal((ushort)0, (ushort)1);
        vm.SelectedMuxValue.Should().Be((ushort?)0);
        vm.LayoutSnapshot.Should().NotBeNull();
        vm.LayoutSnapshot!.EffectiveMuxValue.Should().Be((ushort?)0);
        vm.VisibleSignals.Select(s => s.Name).Should().BeEquivalentTo("Mode", "A");
        vm.LayoutRows.Should().HaveCount(8);
    }

    [Fact]
    public void Switching_Mux_Recomputes_Snapshot_And_VisibleSignals()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, MuxMessage());
        vm.SelectedMessage = vm.Messages.Single();

        vm.SelectedMuxValue = 1;

        vm.LayoutSnapshot!.EffectiveMuxValue.Should().Be((ushort?)1);
        vm.VisibleSignals.Select(s => s.Name).Should().BeEquivalentTo("Mode", "B");
    }

    [Fact]
    public void Selecting_Nothing_Clears_Layout()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, MuxMessage());
        vm.SelectedMessage = vm.Messages.Single();

        vm.SelectedMessage = null;

        vm.LayoutSnapshot.Should().BeNull();
        vm.LayoutRows.Should().BeEmpty();
        vm.VisibleSignals.Should().BeEmpty();
        vm.HasMux.Should().BeFalse();
        vm.SelectedMuxValue.Should().BeNull();
    }

    [Fact]
    public void NoMuxMessage_Reports_HasMuxFalse()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, new Message(
            0x200, "Plain", 8, "ECU",
            new List<Signal> { Sig("S", 0, 8) }, false, null));

        vm.SelectedMessage = vm.Messages.Single();

        vm.HasMux.Should().BeFalse();
        vm.SelectedMuxValue.Should().BeNull();
        vm.LayoutSnapshot!.EffectiveMuxValue.Should().BeNull();
    }

    [Fact]
    public void SelectedMuxWithoutBranch_ProducesWarning()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, MuxMessage());
        vm.SelectedMessage = vm.Messages.Single();

        vm.SelectedMuxValue = 9;

        vm.LayoutSnapshot!.Warnings.Should().Contain(w => w.Code == "mux_no_match");
        vm.VisibleSignals.Select(s => s.Name).Should().BeEquivalentTo("Mode");
    }

    [Fact]
    public void LayoutRows_Are_Ordered_Bit7_To_Bit0()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, MuxMessage());
        vm.SelectedMessage = vm.Messages.Single();

        vm.LayoutRows[0].Cells.Select(c => c.BitNumber)
            .Should().Equal(7, 6, 5, 4, 3, 2, 1, 0);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~DbcLayoutViewModelTests" -v minimal
```
Expected: 编译失败，`SelectedMuxValue` / `LayoutSnapshot` / `VisibleSignals` / `LayoutRows` 不存在。

- [ ] **Step 3: 实现 `LayoutFlow.partial.cs`**

```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Dbc;
using DbcValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class DbcViewModel
{
    [ObservableProperty]
    private DbcMessageViewModel? _selectedMessage;

    [ObservableProperty]
    private ushort? _selectedMuxValue;

    [ObservableProperty]
    private SignalBitLayoutSnapshot? _layoutSnapshot;

    [ObservableProperty]
    private bool _hasMux;

    [ObservableProperty]
    private string _layoutEmptyText = "";

    public ObservableCollection<ushort> MuxValues { get; } = new();

    public ObservableCollection<DbcSignalRow> VisibleSignals { get; } = new();

    public ObservableCollection<ByteLayoutRow> LayoutRows { get; } = new();

    private bool _suppressLayout;

    partial void OnSelectedMessageChanged(DbcMessageViewModel? value)
    {
        if (_suppressLayout) return;
        RebuildMuxAndLayout();
    }

    partial void OnSelectedMuxValueChanged(ushort? value)
    {
        if (_suppressLayout) return;
        UpdateLayout();
    }

    private void RebuildMuxAndLayout()
    {
        _suppressLayout = true;
        try
        {
            MuxValues.Clear();
            if (SelectedMessage?.RawMessage is { } message)
            {
                var values = message.Signals
                    .Where(s => s.IsMultiplexed && s.MultiplexValue is not null)
                    .Select(s => s.MultiplexValue!.Value)
                    .Distinct()
                    .OrderBy(v => v);
                foreach (var value in values) MuxValues.Add(value);
            }

            HasMux = MuxValues.Count > 0;
            SelectedMuxValue = HasMux ? MuxValues[0] : null;
        }
        finally
        {
            _suppressLayout = false;
        }

        UpdateLayout();
    }

    private void UpdateLayout()
    {
        LayoutRows.Clear();
        VisibleSignals.Clear();
        LayoutEmptyText = "";

        if (SelectedMessage?.RawMessage is not { } message)
        {
            LayoutSnapshot = null;
            return;
        }

        var snapshot = SignalBitLayout.Compute(message, SelectedMuxValue);
        LayoutSnapshot = snapshot;

        if (snapshot.RenderedDlc == 0)
        {
            LayoutEmptyText = "无信号 / 无载荷（DLC=0）";
        }
        else if (snapshot.Segments.Count > 0
                 && snapshot.Cells.All(c => c.OwnerSignal is null))
        {
            LayoutEmptyText = $"{snapshot.Segments.Count} 个信号均无可绘制位";
        }

        var diagnostics = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in snapshot.Diagnostics.GroupBy(
                     d => d.SignalName, StringComparer.Ordinal))
        {
            diagnostics[group.Key] = string.Join(
                "；", group.Select(d => d.Text));
        }

        foreach (var segment in snapshot.Segments)
        {
            var signal = message.Signals.FirstOrDefault(s =>
                string.Equals(s.Name, segment.SignalName, StringComparison.Ordinal));
            if (signal is null) continue;

            VisibleSignals.Add(new DbcSignalRow(
                signal,
                segment.PaletteIndex,
                diagnostics.TryGetValue(segment.SignalName, out var text)
                    ? text
                    : ""));
        }

        var paletteByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var segment in snapshot.Segments)
            paletteByName.TryAdd(segment.SignalName, segment.PaletteIndex);

        foreach (var group in snapshot.Cells
                     .GroupBy(c => c.ByteIndex)
                     .OrderBy(g => g.Key))
        {
            var cells = new List<ByteLayoutCell>(8);
            foreach (var cell in group.OrderByDescending(c => c.BitIndex))
            {
                var palette = cell.OwnerSignal is not null
                    && paletteByName.TryGetValue(cell.OwnerSignal, out var index)
                    ? index
                    : -1;
                cells.Add(new ByteLayoutCell(
                    cell.BitIndex,
                    cell.OwnerSignal,
                    palette,
                    FormatCellTooltip(message, cell)));
            }

            LayoutRows.Add(new ByteLayoutRow(group.Key, cells));
        }
    }

    private static string FormatCellTooltip(Message message, BitCell cell)
    {
        var head = $"B{cell.ByteIndex} bit{cell.BitIndex}";
        if (cell.OwnerSignal is null) return $"{head} 未分配";

        head += $" · {cell.OwnerSignal}";
        var signal = message.Signals.FirstOrDefault(s =>
            string.Equals(s.Name, cell.OwnerSignal, StringComparison.Ordinal));
        if (signal is null) return head;

        var detail = string.Create(
            CultureInfo.InvariantCulture,
            $"{signal.StartBit}|{signal.Length}@{(signal.Order == ByteOrder.LittleEndian ? '1' : '0')} · factor {signal.Factor}, offset {signal.Offset} {signal.Unit}");
        if (cell.OverlappedSignals.Count > 0)
        {
            detail += $"\n与 {string.Join("、", cell.OverlappedSignals)} 重叠";
        }

        return $"{head}\n{detail}";
    }
}

public sealed record DbcSignalRow(Signal Signal, int PaletteIndex, string DiagnosticText)
{
    public string Name => Signal.Name;

    public string BitLayout => string.Create(
        CultureInfo.InvariantCulture,
        $"{Signal.StartBit}|{Signal.Length}@{(Signal.Order == ByteOrder.LittleEndian ? '1' : '0')}{(Signal.ValueType == DbcValueType.Signed ? '-' : '+')}");

    public string FactorOffset => string.Create(
        CultureInfo.InvariantCulture, $"{Signal.Factor} / {Signal.Offset}");

    public string Unit => Signal.Unit;

    public string Comment => Signal.Comment ?? "";
}

public sealed record ByteLayoutRow(int ByteIndex, IReadOnlyList<ByteLayoutCell> Cells)
{
    public string ByteLabel => $"B{ByteIndex}";
}

public sealed record ByteLayoutCell(
    int BitNumber, string? OwnerSignal, int PaletteIndex, string Tooltip);
```

- [ ] **Step 4: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~DbcLayoutViewModelTests" -v minimal
```
Expected: 6/6 PASS。

---

### Task 5: 搜索 / 重载的选中生命周期

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/DbcViewModel/LoadingFlow.partial.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/DbcViewModel/SearchFlow.partial.cs`
- Modify: `tests/PeakCan.Host.App.Tests/ViewModels/DbcLayoutViewModelTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `SelectedMessage` / `LayoutSnapshot`。
- Produces: 重载时 `SelectedMessage = null`；搜索后按 `Id + Name` 恢复选中；过滤掉则清空。

- [ ] **Step 1: 追加失败测试**

```csharp
    [Fact]
    public void Reload_Clears_Selection_And_Layout()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc, MuxMessage());
        vm.SelectedMessage = vm.Messages.Single();
        vm.LayoutSnapshot.Should().NotBeNull("precondition");

        RaiseLoaded(svc, new Message(
            0x200, "Other", 8, "ECU",
            new List<Signal> { Sig("S", 0, 8) }, false, null));

        vm.SelectedMessage.Should().BeNull();
        vm.LayoutSnapshot.Should().BeNull();
        vm.LayoutRows.Should().BeEmpty();
    }

    [Fact]
    public void Search_Keeps_Selection_When_Message_Still_Visible()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc,
            new Message(0x100, "Alpha", 8, "ECU",
                new List<Signal> { Sig("S", 0, 8) }, false, null),
            new Message(0x200, "Beta", 8, "ECU",
                new List<Signal> { Sig("S", 0, 8) }, false, null));
        vm.SelectedMessage = vm.Messages.Single(m => m.Name == "Alpha");

        vm.SearchText = "Alpha";

        vm.SelectedMessage.Should().NotBeNull();
        vm.SelectedMessage!.Name.Should().Be("Alpha");
        vm.LayoutSnapshot.Should().NotBeNull();
    }

    [Fact]
    public void Search_Clears_Selection_When_Message_Is_Filtered_Out()
    {
        var vm = NewVm(out var svc);
        RaiseLoaded(svc,
            new Message(0x100, "Alpha", 8, "ECU",
                new List<Signal> { Sig("S", 0, 8) }, false, null),
            new Message(0x200, "Beta", 8, "ECU",
                new List<Signal> { Sig("S", 0, 8) }, false, null));
        vm.SelectedMessage = vm.Messages.Single(m => m.Name == "Alpha");

        vm.SearchText = "Beta";

        vm.SelectedMessage.Should().BeNull();
        vm.LayoutSnapshot.Should().BeNull();
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~DbcLayoutViewModelTests" -v minimal
```
Expected: 重载/搜索生命周期用例失败（选中未清空或未恢复）。

- [ ] **Step 3: 实现生命周期**

`OnLoaded` 的 UI 委托开头加一行（在 `Messages.Clear()` 之前）：

```csharp
            SelectedMessage = null;
```

`ApplyFilter` 改为：

```csharp
    private void ApplyFilter()
    {
        var selected = SelectedMessage;
        FilteredMessages.Clear();
        var pattern = SearchText.Trim();
        foreach (var m in _allMessages)
        {
            if (pattern.Length == 0
                || m.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || m.Sender.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || m.Signals.Any(s => s.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                FilteredMessages.Add(m);
            }
        }

        if (selected is not null)
        {
            SelectedMessage = FilteredMessages.FirstOrDefault(
                m => string.Equals(m.Id, selected.Id, StringComparison.Ordinal)
                     && string.Equals(m.Name, selected.Name, StringComparison.Ordinal));
        }

        NotifyEmptyStateChanged();
    }
```

- [ ] **Step 4: 跑测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~DbcLayoutViewModelTests" -v minimal
```
Expected: 9/9 PASS。

---

### Task 6: 色板、转换器与 `ByteLayoutView`

**Files:**
- Create: `src/PeakCan.Host.App/Composition/Converters/ByteLayoutPaletteBrushConverter.cs`
- Create: `src/PeakCan.Host.App/Views/ByteLayoutView.xaml`
- Create: `src/PeakCan.Host.App/Views/ByteLayoutView.xaml.cs`
- Modify: `src/PeakCan.Host.App/Themes/Colors.xaml`
- Modify: `src/PeakCan.Host.App/App.xaml`
- Create: `tests/PeakCan.Host.App.Tests/Converters/ByteLayoutPaletteBrushConverterTests.cs`
- Create: `tests/PeakCan.Host.App.Tests/Views/ByteLayoutViewLayoutTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `LayoutSnapshot` / `MuxValues` / `SelectedMuxValue` / `HasMux` / `LayoutEmptyText` / `LayoutRows`；`ByteLayoutCell` / `ByteLayoutRow`。
- Produces: `ByteLayoutPaletteBrushConverter`（`int PaletteIndex` → `Brush`）；`ByteLayoutView` 控件。

- [ ] **Step 1: 写转换器失败测试**

```csharp
using System.Globalization;
using System.Windows.Media;
using FluentAssertions;
using PeakCan.Host.App.Composition.Converters;
using Xunit;

namespace PeakCan.Host.App.Tests.Converters;

public class ByteLayoutPaletteBrushConverterTests
{
    [Fact]
    public void NegativeIndex_ReturnsFreeBrush()
    {
        var converter = new ByteLayoutPaletteBrushConverter();

        var brush = converter.Convert(-1, typeof(Brush), null!, CultureInfo.InvariantCulture);

        brush.Should().BeSameAs(ByteLayoutPaletteBrushConverter.FreeBrush);
    }

    [Fact]
    public void PaletteIndex_CyclesEveryNineSignals()
    {
        var converter = new ByteLayoutPaletteBrushConverter();

        var first = converter.Convert(0, typeof(Brush), null!, CultureInfo.InvariantCulture);
        var wrapped = converter.Convert(9, typeof(Brush), null!, CultureInfo.InvariantCulture);

        wrapped.Should().BeSameAs(first);
        first.Should().NotBeSameAs(ByteLayoutPaletteBrushConverter.FreeBrush);
    }

    [Fact]
    public void ConvertBack_IsNotSupported()
    {
        var converter = new ByteLayoutPaletteBrushConverter();

        var act = () => converter.ConvertBack(
            ByteLayoutPaletteBrushConverter.FreeBrush, typeof(int), null!,
            CultureInfo.InvariantCulture);

        act.Should().Throw<NotSupportedException>();
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~ByteLayoutPaletteBrushConverterTests" -v minimal
```
Expected: 编译失败，转换器不存在。

- [ ] **Step 3: 实现转换器 + 色板 + 注册**

`src/PeakCan.Host.App/Composition/Converters/ByteLayoutPaletteBrushConverter.cs`：

```csharp
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace PeakCan.Host.App.Composition.Converters;

/// <summary>
/// Maps a signal palette index to its bit-cell brush. Negative index means an
/// unassigned cell. Colors live in Themes/Colors.xaml; the static palette is a
/// designer/test fallback when no Application exists.
/// </summary>
public sealed class ByteLayoutPaletteBrushConverter : IValueConverter
{
    public static readonly SolidColorBrush FreeBrush = Create(0xFF, 0xF1, 0xF3, 0xF5);

    private static readonly SolidColorBrush[] Palette =
    {
        Create(0xFF, 0x2B, 0x6C, 0xB0),
        Create(0xFF, 0x2F, 0x85, 0x5A),
        Create(0xFF, 0xB7, 0x79, 0x1F),
        Create(0xFF, 0x9B, 0x2C, 0x5C),
        Create(0xFF, 0x6B, 0x46, 0xA6),
        Create(0xFF, 0x0E, 0x80, 0x74),
        Create(0xFF, 0xB0, 0x3A, 0x2E),
        Create(0xFF, 0x4D, 0x6F, 0xB3),
        Create(0xFF, 0x7A, 0x6A, 0x2F),
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int index || index < 0) return FreeBrush;

        var slot = index % Palette.Length;
        var key = $"ByteLayoutSignal{slot + 1}";
        return Application.Current?.TryFindResource(key) as Brush ?? Palette[slot];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Create(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
```

`Themes/Colors.xaml` 追加（放在 `TraceHl1..5` 之后）：

```xml
    <!-- DBC 字节位图（spec 2026-11-25 §5.6）：按信号声明顺序循环取色 -->
    <SolidColorBrush x:Key="ByteLayoutFree" Color="#F1F3F5" />
    <SolidColorBrush x:Key="ByteLayoutSignal1" Color="#2B6CB0" />
    <SolidColorBrush x:Key="ByteLayoutSignal2" Color="#2F855A" />
    <SolidColorBrush x:Key="ByteLayoutSignal3" Color="#B7791F" />
    <SolidColorBrush x:Key="ByteLayoutSignal4" Color="#9B2C5C" />
    <SolidColorBrush x:Key="ByteLayoutSignal5" Color="#6B46A6" />
    <SolidColorBrush x:Key="ByteLayoutSignal6" Color="#0E8074" />
    <SolidColorBrush x:Key="ByteLayoutSignal7" Color="#B03A2E" />
    <SolidColorBrush x:Key="ByteLayoutSignal8" Color="#4D6FB3" />
    <SolidColorBrush x:Key="ByteLayoutSignal9" Color="#7A6A2F" />
```

`App.xaml` 在其它转换器注册旁追加：

```xml
            <conv:ByteLayoutPaletteBrushConverter x:Key="ByteLayoutPaletteBrush" />
```

- [ ] **Step 4: 跑转换器测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~ByteLayoutPaletteBrushConverterTests" -v minimal
```
Expected: 3/3 PASS。

- [ ] **Step 5: 写 `ByteLayoutView` XAML 布局测试（先红）**

```csharp
using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

public class ByteLayoutViewLayoutTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static XDocument Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull();
        return XDocument.Load(Path.Combine(
            dir!.FullName, "src", "PeakCan.Host.App", "Views", "ByteLayoutView.xaml"));
    }

    [Fact]
    public void View_Contains_Uniform_Viewbox_And_Mux_ComboBox()
    {
        var doc = Load();

        var viewbox = doc.Descendants(P + "Viewbox").Single();
        ((string?)viewbox.Attribute("Stretch")).Should().Be("Uniform");

        var combo = doc.Descendants(P + "ComboBox").Single();
        ((string?)combo.Attribute("ItemsSource")).Should().Contain("MuxValues");
        ((string?)combo.Attribute("SelectedItem")).Should().Contain("SelectedMuxValue");
    }

    [Fact]
    public void Cells_Are_Keyboard_Focusable_And_Have_Accessible_Names()
    {
        var doc = Load();

        var border = doc.Descendants(P + "Border").Single(
            b => ((string?)b.Attribute("Focusable")) == "True");

        ((string?)border.Attribute("AutomationProperties.Name")).Should().Contain("Tooltip");
        border.Attribute("MouseEnter").Should().NotBeNull();
        border.Attribute("GotFocus").Should().NotBeNull();
    }
}
```

- [ ] **Step 6: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~ByteLayoutViewLayoutTests" -v minimal
```
Expected: 文件不存在。

- [ ] **Step 7: 实现 `ByteLayoutView.xaml`**

```xml
<UserControl x:Class="PeakCan.Host.App.Views.ByteLayoutView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <StackPanel Margin="16,8,8,8">
        <ItemsControl ItemsSource="{Binding LayoutSnapshot.Warnings}">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <TextBlock Text="{Binding Text}" Foreground="{StaticResource WarnText}"
                               FontSize="11" TextWrapping="Wrap" Margin="0,0,0,4" />
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>

        <StackPanel Orientation="Horizontal" Margin="0,0,0,6"
                    Visibility="{Binding HasMux, Converter={StaticResource BoolToVis}}">
            <TextBlock Text="多路复用" VerticalAlignment="Center" Margin="0,0,6,0" />
            <ComboBox ItemsSource="{Binding MuxValues}"
                      SelectedItem="{Binding SelectedMuxValue, Mode=TwoWay}"
                      MinWidth="90" IsReadOnly="True" />
        </StackPanel>

        <TextBlock Text="{Binding LayoutEmptyText}"
                   Foreground="{StaticResource TextSecondary}"
                   FontSize="12" TextWrapping="Wrap" Margin="0,0,0,4"
                   Visibility="{Binding LayoutEmptyText, Converter={StaticResource NonEmptyStringToVis}}" />

        <Viewbox Stretch="Uniform" HorizontalAlignment="Left">
            <StackPanel>
                <ItemsControl ItemsSource="{Binding LayoutRows}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <DockPanel Margin="0,1">
                                <TextBlock Text="{Binding ByteLabel}" Width="30"
                                           FontFamily="Consolas" FontSize="10"
                                           Foreground="{StaticResource TextSecondary}"
                                           VerticalAlignment="Center" />
                                <ItemsControl ItemsSource="{Binding Cells}">
                                    <ItemsControl.ItemsPanel>
                                        <ItemsPanelTemplate>
                                            <UniformGrid Rows="1" Columns="8" />
                                        </ItemsPanelTemplate>
                                    </ItemsControl.ItemsPanel>
                                    <ItemsControl.ItemTemplate>
                                        <DataTemplate>
                                            <Border Width="40" Height="28" Margin="1" CornerRadius="3"
                                                    Background="{Binding PaletteIndex,
                                                        Converter={StaticResource ByteLayoutPaletteBrush}}"
                                                    AutomationProperties.Name="{Binding Tooltip}"
                                                    Focusable="True"
                                                    MouseEnter="OnCellMouseEnter"
                                                    MouseLeave="OnCellMouseLeave"
                                                    GotFocus="OnCellGotFocus"
                                                    LostFocus="OnCellLostFocus">
                                                <TextBlock Text="{Binding BitNumber}" FontSize="8"
                                                           HorizontalAlignment="Left"
                                                           VerticalAlignment="Top"
                                                           Margin="3,1,0,0">
                                                    <TextBlock.Style>
                                                        <Style TargetType="TextBlock">
                                                            <Setter Property="Foreground" Value="#CCFFFFFF" />
                                                            <Style.Triggers>
                                                                <DataTrigger Binding="{Binding PaletteIndex}"
                                                                             Value="-1">
                                                                    <Setter Property="Foreground"
                                                                            Value="{StaticResource TextDisabled}" />
                                                                </DataTrigger>
                                                            </Style.Triggers>
                                                        </Style>
                                                    </TextBlock.Style>
                                                </TextBlock>
                                            </Border>
                                        </DataTemplate>
                                    </ItemsControl.ItemTemplate>
                                </ItemsControl>
                            </DockPanel>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </StackPanel>
        </Viewbox>

        <Popup x:Name="CellPopup" Placement="Top" VerticalOffset="-6"
               StaysOpen="True" AllowsTransparency="True" PopupAnimation="Fade"
               IsHitTestVisible="False">
            <Border MaxWidth="260" Padding="10,8" Background="White"
                    BorderBrush="#D7DEE7" BorderThickness="1" CornerRadius="8">
                <StackPanel>
                    <TextBlock x:Name="CellPopupTitle" FontWeight="SemiBold" FontSize="12"
                               TextWrapping="Wrap" />
                    <TextBlock x:Name="CellPopupDetail" FontSize="11" Margin="0,4,0,0"
                               TextWrapping="Wrap" Foreground="#23364D" />
                </StackPanel>
            </Border>
        </Popup>
    </StackPanel>
</UserControl>
```

- [ ] **Step 8: 实现 `ByteLayoutView.xaml.cs`**

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Views;

public partial class ByteLayoutView : UserControl
{
    private ByteLayoutCell? _hovered;

    public ByteLayoutView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ClosePopup();
        Unloaded += (_, _) => ClosePopup();
    }

    private void OnCellMouseEnter(object sender, MouseEventArgs e) => ShowCell(sender);

    private void OnCellMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ByteLayoutCell cell }
            && ReferenceEquals(_hovered, cell))
        {
            ClosePopup();
        }
    }

    private void OnCellGotFocus(object sender, RoutedEventArgs e) => ShowCell(sender);

    private void OnCellLostFocus(object sender, RoutedEventArgs e) => ClosePopup();

    private void ShowCell(object sender)
    {
        if (sender is not FrameworkElement { DataContext: ByteLayoutCell cell }) return;

        _hovered = cell;
        CellPopupTitle.Text = cell.OwnerSignal ?? $"bit {cell.BitNumber}";
        CellPopupDetail.Text = cell.Tooltip;
        CellPopup.PlacementTarget = (UIElement)sender;
        CellPopup.IsOpen = true;
    }

    private void ClosePopup()
    {
        _hovered = null;
        CellPopup.IsOpen = false;
    }
}
```

- [ ] **Step 9: 跑 XAML 测试 + 构建确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~ByteLayoutViewLayoutTests|FullyQualifiedName~ByteLayoutPaletteBrushConverterTests" -v minimal
dotnet build src/PeakCan.Host.App/PeakCan.Host.App.csproj -c Debug
```
Expected: 测试 5/5 PASS；构建 0 warning。

---

### Task 7: `DbcView.xaml` 集成、演示 DBC 与验收

**Files:**
- Modify: `src/PeakCan.Host.App/Views/DbcView.xaml`
- Modify: `tests/PeakCan.Host.App.Tests/Views/DbcViewLayoutTests.cs`
- Create: `tests/PeakCan.Host.Core.Tests/TestData/Dbc/byte-layout-demo.dbc`
- Modify: `tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj`
- Create: `tests/PeakCan.Host.Core.Tests/Analysis/Dbc/SignalBitLayoutFixtureTests.cs`

**Interfaces:**
- Consumes: Task 6 的 `ByteLayoutView`；Task 4 的 `VisibleSignals`；Task 2 的 `SignalBitLayout`。
- Produces: DBC 页选中消息后显示位图 + 信号表；演示 DBC 可被单测与手动验收共用。

- [ ] **Step 1: 写 XAML 集成失败测试**

在 `tests/PeakCan.Host.App.Tests/Views/DbcViewLayoutTests.cs` 追加：

```csharp
    [Fact]
    public void Grid_Binds_SelectedItem_To_SelectedMessage()
    {
        var grid = DbcView().Descendants(P + "DataGrid").Single();

        ((string?)grid.Attribute("SelectedItem")).Should().Contain("SelectedMessage");
    }

    [Fact]
    public void RowDetails_Host_ByteLayoutView_And_VisibleSignals()
    {
        var rowDetails = DbcView()
            .Descendants(P + "DataGrid.RowDetailsTemplate").Single();

        rowDetails.Descendants().Should()
            .Contain(e => e.Name.LocalName == "ByteLayoutView");
        rowDetails.Descendants(P + "ItemsControl").Should()
            .Contain(e => ((string?)e.Attribute("ItemsSource"))?.Contains(
                "VisibleSignals", StringComparison.Ordinal) == true);
    }
```

- [ ] **Step 2: 跑测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~DbcViewLayoutTests" -v minimal
```
Expected: 两个新用例失败（缺少 `SelectedItem` / RowDetails 仍是旧模板）。

- [ ] **Step 3: 改 `DbcView.xaml`**

文件头加命名空间：

```xml
             xmlns:views="clr-namespace:PeakCan.Host.App.Views"
```

`DataGrid` 的 `ItemsSource` 后加：

```xml
                      SelectedItem="{Binding SelectedMessage, Mode=TwoWay}"
```

把现有 `DataGrid.RowDetailsTemplate`（旧 `ItemsControl ItemsSource="{Binding Signals}"`）整体替换为：

```xml
                <DataGrid.RowDetailsTemplate>
                    <DataTemplate>
                        <!-- 行 VM 是 DbcMessageViewModel；位图/信号表只对当前选中行，
                             因此把 DataContext 抬到 DataGrid 的 DbcViewModel。 -->
                        <StackPanel DataContext="{Binding DataContext,
                            RelativeSource={RelativeSource AncestorType=DataGrid}}">
                            <views:ByteLayoutView />
                            <ItemsControl ItemsSource="{Binding VisibleSignals}"
                                          Margin="16,0,8,8">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate>
                                        <Grid Margin="0,1">
                                            <Grid.ColumnDefinitions>
                                                <ColumnDefinition Width="18" />
                                                <ColumnDefinition Width="160" />
                                                <ColumnDefinition Width="120" />
                                                <ColumnDefinition Width="110" />
                                                <ColumnDefinition Width="50" />
                                                <ColumnDefinition Width="160" />
                                                <ColumnDefinition Width="*" />
                                            </Grid.ColumnDefinitions>
                                            <Rectangle Width="10" Height="10"
                                                       RadiusX="2" RadiusY="2"
                                                       Fill="{Binding PaletteIndex,
                                                           Converter={StaticResource ByteLayoutPaletteBrush}}" />
                                            <TextBlock Grid.Column="1" Text="{Binding Name}"
                                                       FontFamily="Consolas" FontSize="11"
                                                       TextTrimming="CharacterEllipsis"
                                                       ToolTip="{Binding Name}" />
                                            <TextBlock Grid.Column="2" Text="{Binding BitLayout}"
                                                       FontFamily="Consolas" FontSize="11" />
                                            <TextBlock Grid.Column="3" Text="{Binding FactorOffset}"
                                                       FontFamily="Consolas" FontSize="11" />
                                            <TextBlock Grid.Column="4" Text="{Binding Unit}"
                                                       FontSize="11" />
                                            <TextBlock Grid.Column="5" Text="{Binding Comment}"
                                                       FontSize="11" Foreground="{StaticResource TextSecondary}"
                                                       TextTrimming="CharacterEllipsis" />
                                            <TextBlock Grid.Column="6" Text="{Binding DiagnosticText}"
                                                       FontSize="11" Foreground="{StaticResource WarnText}"
                                                       TextWrapping="Wrap" />
                                        </Grid>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
                        </StackPanel>
                    </DataTemplate>
                </DataGrid.RowDetailsTemplate>
```

- [ ] **Step 4: 跑 XAML 测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~DbcViewLayoutTests" -v minimal
```
Expected: 全部 PASS。

- [ ] **Step 5: 落演示 DBC fixture**

`tests/PeakCan.Host.Core.Tests/TestData/Dbc/byte-layout-demo.dbc`（ASCII，LF）：

```text
VERSION ""
NS_ :
BS_:
BU_: BMS
BO_ 929 BMS_Status: 8 BMS
 SG_ SOC : 0|12@1+ (0.1,0) [0|100] "%" Vector__XXX
 SG_ PackCurrent : 12|12@1- (0.1,-200) [-200|200] "A" Vector__XXX
 SG_ PackVoltage : 24|16@1+ (0.01,0) [0|655.35] "V" Vector__XXX
 SG_ Mode M : 40|4@1+ (1,0) [0|15] "" Vector__XXX
 SG_ CellVoltMax m0 : 44|12@1+ (0.001,0) [0|4.095] "V" Vector__XXX
 SG_ CellVoltMin m1 : 44|12@1+ (0.001,0) [0|4.095] "V" Vector__XXX
 SG_ FaultFlags : 55|8@0+ (1,0) [0|255] "" Vector__XXX
BO_ 930 BMS_CellVolt: 8 BMS
 SG_ Group M : 0|8@1+ (1,0) [0|255] "" Vector__XXX
 SG_ CellVolt_1 m0 : 8|16@1+ (0.001,0) [0|65.535] "V" Vector__XXX
 SG_ CellVolt_2 m0 : 24|16@1+ (0.001,0) [0|65.535] "V" Vector__XXX
 SG_ CellVolt_5 m1 : 8|16@1+ (0.001,0) [0|65.535] "V" Vector__XXX
 SG_ CellVolt_6 m1 : 24|16@1+ (0.001,0) [0|65.535] "V" Vector__XXX
 SG_ BalanceFlags : 39|8@0+ (1,0) [0|255] "" Vector__XXX
 SG_ CRC : 48|8@1+ (1,0) [0|255] "" Vector__XXX
 SG_ AliveCounter : 56|4@1+ (1,0) [0|15] "" Vector__XXX
```

`tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj` 在 `TestData\Nm\**\*` 旁加：

```xml
  <ItemGroup>
    <Content Include="TestData\Dbc\**\*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 6: 写 fixture 验收测试（先红）**

```csharp
using FluentAssertions;
using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core.Analysis.Dbc;
using Xunit;

namespace PeakCan.Host.Core.Tests.Analysis.Dbc;

public class SignalBitLayoutFixtureTests
{
    private static Message Load(string name)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "TestData", "Dbc", "byte-layout-demo.dbc");
        File.Exists(path).Should().BeTrue($"fixture 必须随测试复制到 {path}");
        var doc = DbcParser.Parse(File.ReadAllText(path)).Value;
        doc.Should().NotBeNull("演示 DBC 必须能解析");
        return doc!.Messages.Single(m => m.Name == name);
    }

    [Fact]
    public void BmsCellVolt_Mux0_OverlapsAtB4Bit0()
    {
        var snapshot = SignalBitLayout.Compute(Load("BMS_CellVolt"), 0);

        var cell = snapshot.Cells.Single(c =>
            c.ByteIndex == 4 && c.BitIndex == 0);
        cell.OwnerSignal.Should().Be("BalanceFlags");
        cell.OverlappedSignals.Should().Contain("CellVolt_2");
        snapshot.EffectiveMuxValue.Should().Be((ushort?)0);
    }

    [Fact]
    public void BmsCellVolt_Mux1_SelectsCellVolt5And6()
    {
        var snapshot = SignalBitLayout.Compute(Load("BMS_CellVolt"), 1);

        snapshot.Segments.Select(s => s.SignalName).Should()
            .Contain("CellVolt_5").And.Contain("CellVolt_6")
            .And.NotContain("CellVolt_1").And.NotContain("CellVolt_2");
    }
}
```

- [ ] **Step 7: 跑 fixture 测试确认失败**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~SignalBitLayoutFixtureTests" -v minimal
```
Expected: 文件缺失（如果 csproj 未改）或断言失败。

- [ ] **Step 8: 跑 fixture 测试确认通过**

Run:
```bash
dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~SignalBitLayoutFixtureTests" -v minimal
```
Expected: 2/2 PASS。

- [ ] **Step 9: 手动验收**

Run:
```bash
dotnet run --project src/PeakCan.Host.App/PeakCan.Host.App.csproj
```
打开 `tests/PeakCan.Host.Core.Tests/TestData/Dbc/byte-layout-demo.dbc`，逐项确认：

1. 选中 `BMS_CellVolt`：8 行位图出现；mux 下拉默认 `Group = 0`；`CellVolt_1` / `CellVolt_2` 上色，`CellVolt_5` / `CellVolt_6` 不上色；B4 bit0 的颜色属于 `BalanceFlags`，信号表标注与 `CellVolt_2` 重叠。
2. 切到 `mux = 1`：`CellVolt_5` / `CellVolt_6` 上色，`CellVolt_1` / `CellVolt_2` 消失；位图重建一次，无定时器。
3. 选中 `BMS_Status`：mux 下拉出现 0/1；`CellVoltMax` / `CellVoltMin` 按分支切换。
4. 搜索 `Balance`：选中行不丢，位图保持；搜索 `SOC`：选中清空、位图消失。
5. 重新打开同一个 DBC：选中清空，位图消失，不残留旧消息。
6. hover 任一格子：卡片显示 `B4 bit0 · BalanceFlags`、`Start|Len@Order`、factor/offset、重叠信息；用 Tab 聚焦格子也能打开。

预期：全部符合；与 `docs/dbc-byte-layout-mockup.html` 的视觉布局一致（规则差异按 spec §5.7）。

---

## Self-Review

**1. Spec coverage**

- §4 D1（Host.Core 落点）：Task 1/2 文件路径；无 hil-core 改动。
- §4 数据模型（`RawMessage`、`VisibleSignals`、选中/mux/快照）：Task 3/4。
- §5.1 位序（Intel/Motorola、i 顺序、列 7→0）：Task 1 映射 + Task 2 解码一致性。
- §5.2 DLC 行数 / >64 截断：Task 1。
- §5.3 Length=0/>64、部分/全部越界、不抛异常：Task 1。
- §5.4 重叠：Task 2。
- §5.5 mux 默认最小值 / 无匹配 / 单层：Task 2、Task 4。
- §5.6 快照契约 / 诊断 / PaletteIndex：Task 1/2，Task 4 投影。
- §5.7 mockup 差异：Task 7 手动验收第 1/2/3 项。
- §6 渲染/生命周期/性能：Task 6（Uniform Viewbox、无定时器）、Task 5（搜索/重载）。
- §7 错误态：Task 1（诊断）、Task 4（`LayoutEmptyText` + warnings）、Task 6（警告展示）。
- §8 测试：Task 1/2/4/5/6/7。
- §9 验收：Task 7 Step 9。
- §10 决策：D1–D6 已落到实现。

**2. Placeholder scan**

无 `TBD` / `TODO` / “类似 Task N” / “添加适当的错误处理”。每个代码步骤都有实际代码。

**3. Type consistency**

- Core：`SignalBitLayout.Compute(Message, ushort? = null)`；`SignalBitLayoutSnapshot` 字段 `DeclaredDlc/RenderedDlc/EffectiveMuxValue/Cells/Segments/Diagnostics/Warnings` 与 spec §5.6 一致。
- App：`SelectedMessage` / `SelectedMuxValue` / `MuxValues` / `HasMux` / `LayoutSnapshot` / `LayoutEmptyText` / `VisibleSignals` / `LayoutRows` 在 Task 4 定义，Task 5/6/7 只消费，无改名。
- 视图投影：`DbcSignalRow`（`Signal/PaletteIndex/DiagnosticText` + `Name/BitLayout/FactorOffset/Unit/Comment`）、`ByteLayoutRow`（`ByteIndex/Cells/ByteLabel`）、`ByteLayoutCell`（`BitNumber/OwnerSignal/PaletteIndex/Tooltip`）在 Task 4 定义，Task 6/7 只绑定。
- `ByteLayoutPaletteBrushConverter` 接受 `int`；`ByteLayoutCell.PaletteIndex` / `DbcSignalRow.PaletteIndex` 都是 `int`。

**4. Review Focus**

每条 Review Focus 都有测试：
1. 畸形 DBC → Task 1 `Compute_LengthZeroAndOver64...`、`Compute_DlcOver64...`、Task 2 越界测试。
2. mux 默认/无匹配 → Task 2 `Compute_Mux_DefaultsToMinimumValue`、`Compute_Mux_NoMatchingBranch...`。
3. 普通重叠 → Task 2 `Compute_Overlap...`、Task 7 fixture 撞位测试。
4. 搜索/重载生命周期 → Task 5 三个测试。
5. 布局↔解码漂移 → Task 2 三组手写向量。

## Execution Handoff

本计划不含 commit 步骤。实现完成后按用户是否要求决定是否提交。
