# Mobile Trace Viewer P5 Chart Stability & UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore the selected-signal chart after playback/filter/seek state changes, preserve user X zoom during chart refreshes, and raise the mobile chart limit from 2 to 4 signals.

**Architecture:** Keep chart sample ownership in `TraceSessionViewModel` / `TraceChartViewModel`, and make every destructive viewport operation optionally restart history backfill. Add a small pure `ChartZoomState` model so UI can capture and restore LiveCharts X-axis limits across render rebuilds without coupling Mobile.Core to LiveCharts. Keep the MAUI view thin: it maps 1–4 selected signals to colored series and alternating Start/End Y axes.

**Tech Stack:** .NET 10, MAUI Android, LiveCharts2, CommunityToolkit.Mvvm, xUnit, FluentAssertions, NSubstitute.

**Spec:** `docs/superpowers/specs/2026-09-07-mobile-trace-viewer-design.md` (P3 chart), plus P4 follow-up review findings recorded in the handoff.

## Global Constraints

- Branch: continue on current `feature/mobile-trace-viewer-p4`; do not push and do not merge without explicit user confirmation.
- Do not commit `.acceptance/` or touch `.worktrees/`.
- User-visible text and business comments are Chinese; API/XML comments are English.
- Conventional commits; no Co-Authored-By.
- `PeakCan.Host.Mobile.Core` remains independent of MAUI/LiveCharts.
- Tests must not use `Thread.Sleep` or real `Task.Delay`.
- Critical/Important review findings must be fixed before completion.
- Manual UI claims require an emulator screenshot or explicit measured UI bounds.

## File Structure

- Modify `src/PeakCan.Host.Mobile.Core/ViewModels/TraceSessionViewModel.cs`: restart chart backfill after destructive operations.
- Modify `src/PeakCan.Host.Mobile.Core/ViewModels/TraceChartViewModel.cs`: configurable/fixed maximum of 4 signals.
- Create `src/PeakCan.Host.Mobile.Core/ViewModels/ChartZoomState.cs`: pure X-axis zoom persistence model.
- Modify `src/PeakCan.Host.Mobile/Views/TracePage.xaml.cs`: capture/restore X zoom, render up to 4 signals, reset state explicitly.
- Modify `src/PeakCan.Host.Mobile/Views/SignalSelectionPage.xaml.cs`: display the 4-signal limit.
- Test `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/ChartZoomStateTests.cs`.
- Test `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceSessionViewModelTests.cs`.
- Test `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceChartViewModelTests.cs`.

---

## Task 1: Pure Chart Zoom State

**Files:**
- Create: `src/PeakCan.Host.Mobile.Core/ViewModels/ChartZoomState.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/ChartZoomStateTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct ChartAxisRange(double Min, double Max)`
  - `sealed class ChartZoomState`
  - `bool Capture(double? minimum, double? maximum)`
  - `bool TryGet(out ChartAxisRange range)`
  - `void Reset()`

- [ ] **Step 1: Write failing tests**

Add this test file:

```csharp
using FluentAssertions;
using PeakCan.Host.Mobile.Core.ViewModels;
using Xunit;

namespace PeakCan.Host.Mobile.Core.Tests.ViewModels;

public class ChartZoomStateTests
{
    [Fact]
    public void Capture_Stores_Finite_Increasing_Range()
    {
        var state = new ChartZoomState();

        state.Capture(4, 6).Should().BeTrue();

        state.TryGet(out var range).Should().BeTrue();
        range.Should().Be(new ChartAxisRange(4, 6));
    }

    [Theory]
    [InlineData(null, 6)]
    [InlineData(4, null)]
    [InlineData(6, 4)]
    [InlineData(double.NaN, 6)]
    [InlineData(4, double.PositiveInfinity)]
    public void Capture_Ignores_Invalid_Range(double? minimum, double? maximum)
    {
        var state = new ChartZoomState();

        state.Capture(minimum, maximum).Should().BeFalse();
        state.TryGet(out _).Should().BeFalse();
    }

    [Fact]
    public void Reset_Removes_Captured_Range()
    {
        var state = new ChartZoomState();
        state.Capture(4, 6);

        state.Reset();

        state.TryGet(out _).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run:
```powershell
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --filter "FullyQualifiedName~ChartZoomStateTests" --nologo
```
Expected: compile failure because `ChartZoomState` does not exist.

- [ ] **Step 3: Implement the model**

Create:

```csharp
namespace PeakCan.Host.Mobile.Core.ViewModels;

/// <summary>A finite, increasing one-dimensional axis range.</summary>
public readonly record struct ChartAxisRange(double Minimum, double Maximum);

/// <summary>Captures and restores chart X zoom without depending on a chart library.</summary>
public sealed class ChartZoomState
{
    public ChartAxisRange? XRange { get; private set; }

    public bool Capture(double? minimum, double? maximum)
    {
        if (minimum is not { } min || maximum is not { } max)
            return false;
        if (!double.IsFinite(min) || !double.IsFinite(max) || min >= max)
            return false;

        XRange = new ChartAxisRange(min, max);
        return true;
    }

    public bool TryGet(out ChartAxisRange range)
    {
        if (XRange is { } captured)
        {
            range = captured;
            return true;
        }

        range = default;
        return false;
    }

    public void Reset() => XRange = null;
}
```

- [ ] **Step 4: Run tests to verify pass**

Run the same filter command.
Expected: 3 passed.

- [ ] **Step 5: Commit**

```powershell
git add src/PeakCan.Host.Mobile.Core/ViewModels/ChartZoomState.cs tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/ChartZoomStateTests.cs
git commit -m "feat(mobile): add chart zoom state model"
```

---

## Task 2: Restart Chart Backfill After Destructive State Changes

**Files:**
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceSessionViewModel.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceSessionViewModelTests.cs`

**Interfaces:**
- Consumes existing `StartChartBackfill()`, `ClearPlaybackBuffer()`, and `BackfillSelectedSignalsAsync()`.
- Produces: destructive viewport operations restore chart history whenever valid selections remain.

- [ ] **Step 1: Add failing lifecycle tests**

Use the existing `Env`, `AsyncFrameSeq`, and DBC fixtures. Add at least these tests:

```csharp
private static DbcCatalog EngineDbc() => DbcCatalog.Parse("""
    VERSION ""
    NS_ :
    BS_:
    BU_: ECM

    BO_ 256 EngineData: 8 ECM
     SG_ EngineSpeed : 0|16@1+ (0.25,0) [0|16000] "rpm" Vector__XXX
    """).Catalog!;
```

```csharp
[Fact]
public async Task Stop_Restarts_Selected_Signal_Backfill()
{
    var env = new Env();
    var frames = new AsyncFrameSeq(F(0, 0x100), F(5, 0x100));
    env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
    var key = new SignalSelectionKey(0x100, false, "EngineData", "EngineSpeed");

    env.Vm.SetDbc(EngineDbc());
    env.Vm.Chart.Select(key).Should().BeTrue();
    await env.Vm.OpenAsync("foo.asc", "foo.asc", 0);
    await env.Vm.BackfillSelectedSignalsAsync();

    env.Vm.StopCommand.Execute(null);
    await env.Vm.BackfillSelectedSignalsAsync();

    env.Vm.Chart.RenderPoints[key].Select(p => p.Timestamp).Should().Equal(0, 5);
}

[Fact]
public async Task IdFilter_Change_Restarts_Selected_Signal_Backfill()
{
    var env = new Env();
    var frames = new AsyncFrameSeq(F(0, 0x100), F(5, 0x100));
    env.SourceFactory.LastSource.OpenAsync(default).ReturnsForAnyArgs(Task.FromResult(frames.OpenResult));
    var key = new SignalSelectionKey(0x100, false, "EngineData", "EngineSpeed");

    env.Vm.SetDbc(EngineDbc());
    env.Vm.Chart.Select(key).Should().BeTrue();
    await env.Vm.OpenAsync("foo.asc", "foo.asc", 0);
    await env.Vm.BackfillSelectedSignalsAsync();

    env.Vm.SetIdFilter("0x100");
    await env.Vm.BackfillSelectedSignalsAsync();

    env.Vm.Chart.RenderPoints[key].Select(p => p.Timestamp).Should().Equal(0, 5);
}
```

Add equivalent tests for:
- `TogglePlay` from `Ready`;
- `SeekTo` in `Ready` after `DurationKnown` is forced via test reflection or an existing duration scanner helper;
- replay from `Ended` if an existing fake-player pattern already reaches that state.

- [ ] **Step 2: Run new tests and verify failure**

Run:
```powershell
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --filter "FullyQualifiedName~TraceSessionViewModelTests" --nologo
```
Expected: new lifecycle tests fail because `RenderPoints` remains empty after the destructive operation.

- [ ] **Step 3: Implement minimal restart behavior**

Change the private method signature:

```csharp
private void ClearPlaybackBuffer(bool restartChartBackfill = false)
{
    _chartBackfillCts?.Cancel();
    lock (_emitGate) _pending.Clear();
    _rows.Clear();
    SkippedLinesText = string.Empty;
    _chart.Clear();
    UpdateViewport();

    if (restartChartBackfill)
        StartChartBackfill();
}
```

Use `restartChartBackfill: true` for:
- `SetDbc` (replacing its separate `StartChartBackfill()` call);
- `TogglePlay` when leaving `Ready`, `Ended`, or `Failed`;
- `Stop`;
- `SeekTo` when `State == SessionState.Ready`;
- `OnIdFilterTextChanged`;
- `MarkReadyForEmit`.

Keep `OpenAsync`'s initial clear non-restarting, because it starts backfill explicitly after state becomes `Ready`.

- [ ] **Step 4: Run tests to verify pass**

Run:
```powershell
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --filter "FullyQualifiedName~TraceSessionViewModelTests" --nologo
```
Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add src/PeakCan.Host.Mobile.Core/ViewModels/TraceSessionViewModel.cs tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceSessionViewModelTests.cs
git commit -m "fix(mobile): restore chart history after state changes"
```

---

## Task 3: Support Four Selected Signals

**Files:**
- Modify: `src/PeakCan.Host.Mobile.Core/ViewModels/TraceChartViewModel.cs`
- Modify: `src/PeakCan.Host.Mobile/Views/SignalSelectionPage.xaml.cs`
- Test: `tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceChartViewModelTests.cs`

**Interfaces:**
- Produces:
  - `private const int MaxSelectedSignals = 4;`
  - `Select` accepts up to 4 known signals.
  - selection page message: `"最多选择 4 个信号。"`

- [ ] **Step 1: Change the existing limit test**

Replace `Select_Accepts_At_Most_Two_Signals` with:

```csharp
[Fact]
public void Select_Accepts_At_Most_Four_Signals()
{
    var vm = Create();
    var unknown = new SignalSelectionKey(0x101, false, "EngineData", "EngineSpeed");
    var low = new SignalSelectionKey(0x12C, false, "MuxData", "LowValue");

    vm.Select(unknown).Should().BeFalse();
    vm.Select(Speed).Should().BeTrue();
    vm.Select(Temp).Should().BeTrue();
    vm.Select(High).Should().BeTrue();
    vm.Select(low).Should().BeTrue();
    vm.SelectedSignals.Should().HaveCount(4);
}
```

If a fifth valid signal is needed, extend the DBC fixture with another known message/signal; do not use an unknown key to assert the limit.

- [ ] **Step 2: Run test to verify failure**

Run:
```powershell
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --filter "FullyQualifiedName~TraceChartViewModelTests" --nologo
```
Expected: the changed test fails while only 2 signals are accepted.

- [ ] **Step 3: Implement the 4-signal limit**

In `TraceChartViewModel`:

```csharp
private const int MaxSelectedSignals = 4;
```

Change:

```csharp
if (_stores.Count >= MaxSelectedSignals) return false;
```

In `SignalSelectionPage.xaml.cs`, change the alert text to:

```csharp
await DisplayAlertAsync("无法选择", "最多选择 4 个信号。", "确定");
```

- [ ] **Step 4: Run chart tests**

Run:
```powershell
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --filter "FullyQualifiedName~TraceChartViewModelTests" --nologo
```
Expected: all pass.

- [ ] **Step 5: Commit**

```powershell
git add src/PeakCan.Host.Mobile.Core/ViewModels/TraceChartViewModel.cs src/PeakCan.Host.Mobile/Views/SignalSelectionPage.xaml.cs tests/PeakCan.Host.Mobile.Core.Tests/ViewModels/TraceChartViewModelTests.cs
git commit -m "feat(mobile): allow four chart signals"
```

---

## Task 4: Preserve Zoom and Render Four Series

**Files:**
- Modify: `src/PeakCan.Host.Mobile/Views/TracePage.xaml.cs`

**Interfaces:**
- Consumes `ChartZoomState`, `ChartAxisRange`, and `TraceChartViewModel.MaxSelectedSignals` behavior.
- Produces: chart render does not reset user X zoom; reset button clears the captured range.

- [ ] **Step 1: Add a private zoom field**

```csharp
private readonly ChartZoomState _zoomState = new();
```

- [ ] **Step 2: Capture before rebuilding and restore after assignment**

At the start of `RenderChart()`, before changing `SignalChart.Series` / axes:

```csharp
var existingXAxis = SignalChart.XAxes.FirstOrDefault();
_zoomState.Capture(existingXAxis?.MinLimit, existingXAxis?.MaxLimit);
```

After `SignalChart.XAxes = yAxes;` is assigned:

```csharp
if (_zoomState.TryGet(out var xRange) && SignalChart.XAxes.Count > 0)
{
    SignalChart.XAxes[0].MinLimit = xRange.Minimum;
    SignalChart.XAxes[0].MaxLimit = xRange.Maximum;
}
```

If the LiveCharts property types are not nullable on the installed package version, adapt capture with safe local conversion while preserving the same `ChartZoomState` semantics.

- [ ] **Step 3: Update colors, empty text, and Y-axis positions**

Use at least four series colors:

```csharp
var seriesColors = new[]
{
    SKColors.MediumBlue,
    SKColors.IndianRed,
    SKColors.SeaGreen,
    SKColors.DarkOrange,
};
```

Set empty text:

```csharp
ChartEmptyLabel.Text = chart.Messages.Count == 0
    ? "请先加载 DBC"
    : "请选择 1–4 个 DBC 信号";
```

Set Y-axis side by index:

```csharp
Position = index % 2 == 0
    ? LiveChartsCore.Measure.AxisPosition.Start
    : LiveChartsCore.Measure.AxisPosition.End,
```

- [ ] **Step 4: Reset zoom explicitly**

Change:

```csharp
private void OnResetZoomClicked(object? sender, EventArgs e)
{
    _zoomState.Reset();
    RenderChart();
}
```

Because capture happens after reset, the next render re-autoscales.

- [ ] **Step 5: Build Android**

Run:
```powershell
dotnet build src/PeakCan.Host.Mobile/PeakCan.Host.Mobile.csproj -f net10.0-android -p:EmbedAssembliesIntoApk=true --nologo
```
Expected: build succeeds with 0 errors.

- [ ] **Step 6: Commit**

```powershell
git add src/PeakCan.Host.Mobile/Views/TracePage.xaml.cs
git commit -m "feat(mobile): preserve chart zoom across renders"
```

---

## Task 5: Full Regression, Emulator Acceptance, Review

**Files:**
- No production file changes expected.
- Local-only evidence under `.acceptance/` (do not commit).

- [ ] **Step 1: Run Mobile.Core tests**

```powershell
dotnet test tests/PeakCan.Host.Mobile.Core.Tests/PeakCan.Host.Mobile.Core.Tests.csproj --nologo
```
Expected: 0 failed; coverage remains at or above the existing P4 level.

- [ ] **Step 2: Run Android build**

Use the Task 4 build command.
Expected: 0 errors.

- [ ] **Step 3: Install and verify on emulator**

Use the existing embedded APK and `emulator-5554` workflow. Clear app data, then:
1. Open `.acceptance/two-signal.blf` through the file/intent flow.
2. Load `.acceptance/two-signal.dbc`.
3. Select four available signals if the fixture supports them; otherwise use all valid fixture signals and separately verify the four-limit alert with a synthetic local DBC.
4. Confirm chart history is visible after selection.
5. Zoom in, press `复位`, zoom in again, then verify a later chart refresh does not reset zoom.
6. Press Stop and confirm selected-signal history returns.
7. Change ID filter and confirm selected-signal history returns after backfill.
8. Save screenshot to `.acceptance/latest-chart-p5.png` and verify all controls remain on screen.

- [ ] **Step 4: Run Superpowers code review**

Use `superpowers:requesting-code-review`.
Fix all Critical/Important findings with focused tests.

- [ ] **Step 5: Record completion**

If all checks pass, update the plan checkboxes. Do not merge, push, or delete the branch without explicit user confirmation.

## Self-Review

- **Spec coverage:** The approved P5 scope covers backfill lifecycle, zoom persistence, and 2→4 signal expansion; each has a task and verification.
- **Placeholder scan:** No TBD/TODO items; all implementation and tests include concrete behavior.
- **Type consistency:** `ChartZoomState`, `ChartAxisRange`, and `TraceChartViewModel.Select` usage are consistent across tasks.
- **Risk control:** LiveCharts nullable axis limits may require a small UI-only adaptation, but Mobile.Core remains chart-library independent.
