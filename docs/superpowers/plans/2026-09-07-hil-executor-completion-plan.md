# HIL Executor Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete the HIL executor by implementing stop/partial-cancel, TrialRunner wiring, preflight/run configuration, execution-state UX, persistence, run history, and result-copy affordances.

**Architecture:** Keep execution orchestration in `HilViewModel`; keep domain-facing contracts in `PeakCan.Host.Core`; keep hardware/JSON/report services in Infrastructure. Introduce small services for preflight, trial execution, panel state, and history. Preserve the existing WPF + CommunityToolkit.Mvvm structure and avoid changing hil-core.

**Tech Stack:** .NET 10 / WPF / CommunityToolkit.Mvvm / xUnit / FluentAssertions / System.Text.Json / Microsoft.Extensions.Hosting.

**Spec:** `docs/superpowers/specs/2026-09-06-hil-executor-completion-design.md`

## Global Constraints

- Do not modify the `PeakCan.HIL.Core` package; host-owned engine/runtime changes are allowed.
- User cancellation and suite timeout must remain distinguishable; only user cancellation sets history `cancelled=true`.
- Case/suite teardown, frame-drain, and channel disconnect are cleanup obligations and must not be blocked by an already-cancelled token.
- Trial must reuse connected `ICanChannel` instances and must not disconnect or reconnect them.
- Trial lookup must preserve extended-frame semantics by matching `CanId.Raw` and `FrameFormat`.
- Run/Rerun/Trial must be disabled while `IsRunning`, `IsTrialing`, or `IsAnalyzing`.
- If cases exist and none are selected, `Run` must be disabled.
- History is capped to the most recent 50 records.
- UI copy is Chinese; no new UI automation—use VM/service tests plus manual acceptance.
- Keep the WebView2 report container structure unchanged during layout work.

---

### Task 1: Engine partial-cancel and timeout semantics

**Files:**
- Modify: `src/PeakCan.Host.Core/HIL/TestSuiteEngine.cs`
- Test: `tests/PeakCan.Host.Core.Tests/HIL/TestSuiteEngineCancellationTests.cs`

**Interfaces:**
- Consumes: existing `TestSuiteEngine.ExecuteAsync(suite, ctx, config, progress, externalCt, sinkFactory, frameStats)`.
- Produces: partial `TestSuiteResult` for user cancel and suite timeout; cancellation reason is embedded in `TestCaseResult.FailureReason` without changing hil-core result contracts.

- [ ] **Step 1: Write cancellation engine tests**

Create `TestSuiteEngineCancellationTests.cs` with a recording fixture and an executor that cancels:

```csharp
public sealed class CancelOnExecuteFixture : ITestFixture
{
    public List<CancellationToken> SetupTokens { get; } = new();
    public List<CancellationToken> TeardownTokens { get; } = new();
    private readonly CancellationTokenSource _cts = new();

    public async Task SetupAsync(IAssertionContext ctx, CancellationToken ct)
    {
        SetupTokens.Add(ct);
        if (_cts.Token.IsCancellationRequested) throw new OperationCanceledException(_cts.Token);
    }

    public async Task TeardownAsync(IAssertionContext ctx, CancellationToken ct)
    {
        TeardownTokens.Add(ct);
    }

    public void Cancel() => _cts.Cancel();
}
```

Add four tests:

1. `UserCancel_DuringStep_ReturnsPartial_And_RunsTeardownsWithNone`.
2. `UserCancel_DuringCaseSetup_ReturnsFailedCancelledCase`.
3. `SuiteTimeout_ReturnsPartialTimeout_NotCancelled`.
4. `UserCancel_DuringSuiteSetup_ThrowsOperationCanceledException`.

Each partial-cancel assertion should check `CaseResults.Count`, `SkippedCases`, teardown token values, and `FailureReason`.

- [ ] **Step 2: Run the new tests and verify failure**

```powershell
dotnet test tests/PeakCan.Host.Core.Tests --nologo --filter "FullyQualifiedName~TestSuiteEngineCancellationTests"
```

Expected: compile errors because `TestSuiteEngineCancellationTests` does not yet satisfy existing constructors, or assertion failures because current engine throws/omits partial behavior.

- [ ] **Step 3: Implement cancellation semantics**

In `ExecuteAsync`, retain the linked token for timeout but capture `externalCt.IsCancellationRequested` before catching OCE. In case setup and step execution, map OCE to `"已取消"` when external cancellation is requested, otherwise `"套件超时"`. Change case/suite teardown calls to `CancellationToken.None`, catch teardown OCE, and do not rethrow it. Break the case loop after cancellation/timeout. Return the partial result with existing `SkippedCases = TotalCases - CaseResults.Count`.

In `ExecuteCaseAsync`, change `WaitForFrameDrainAsync(ct)` to `WaitForFrameDrainAsync(CancellationToken.None)` or catch OCE, then always detach/dispose sink.

- [ ] **Step 4: Run tests**

```powershell
dotnet test tests/PeakCan.Host.Core.Tests --nologo --filter "FullyQualifiedName~TestSuiteEngineCancellationTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/PeakCan.Host.Core/HIL/TestSuiteEngine.cs tests/PeakCan.Host.Core.Tests/HIL/TestSuiteEngineCancellationTests.cs
git commit -m "feat(hil): support partial engine cancellation and timeout"
```

---

### Task 2: Runner cleanup and Stop command

**Files:**
- Modify: `src/PeakCan.Host.Infrastructure/HIL/HilRunnerService.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/HilViewModel.cs`
- Test: `tests/PeakCan.Host.Infrastructure.Tests/HilRunnerServiceCancellationTests.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelStopTests.cs`

**Interfaces:**
- Consumes: partial result semantics from Task 1.
- Produces: `StopCommand` on `HilViewModel`; runner finally-disconnect uses `CancellationToken.None`; VM distinguishes partial completion from `OperationCanceledException`.

- [ ] **Step 1: Write runner cancellation test**

Create a fake multi/single channel whose `DisconnectAsync` records its token, then assert the finally path receives `CancellationToken.None`:

```csharp
[Fact]
public async Task Disconnect_InFinally_UsesCancellationTokenNone()
{
    var token = new CancellationToken(true);
    var channel = new RecordingDisconnectChannel();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => channel.DisconnectAsync(token));
    Assert.Equal(token, channel.LastDisconnectToken);
}
```

Add a service-level test that injects a cancelling engine/channel and asserts no OCE masks the partial result.

- [ ] **Step 2: Write VM Stop test**

Create a fake runner that stores the incoming token and returns a partial result after `CancelAfter(25)`:

```csharp
private sealed class CancellingRunner : IHilRunnerService
{
    public CancellationToken LastToken { get; private set; }
    public async Task<TestSuiteResult> RunAsync(HilRunRequest request, IProgress<TestProgress>? progress = null, CancellationToken ct = default)
    {
        LastToken = ct;
        await Task.Delay(100, ct);
        return new TestSuiteResult("S", 2, 1, 1, 0, 100, Array.Empty<string>(), Array.Empty<TestCaseResult>());
    }
    public DbcDocument? LastDbcDocument => null;
    public IReadOnlyDictionary<ChannelId, DbcDocument>? LastPerChannelDbcs => null;
    public string? LastCaseLogDirectory => null;
}
```

Assert `StopCommand.CanExecute(null)` flips with `IsRunning`, cancellation yields status `已取消（完成 1/2）`, `_lastResult` is set, and report generation is attempted.

- [ ] **Step 3: Run tests and verify failure**

```powershell
dotnet test tests/PeakCan.Host.Infrastructure.Tests --nologo --filter "FullyQualifiedName~HilRunnerServiceCancellationTests"
dotnet test tests/PeakCan.Host.App.Tests --nologo --filter "FullyQualifiedName~HilViewModelStopTests"
```

Expected: FAIL because cleanup currently uses the cancelled token and `StopCommand` does not exist.

- [ ] **Step 4: Implement**

In `HilRunnerService.RunAsync`, replace both finally disconnect calls with `DisconnectAllAsync(CancellationToken.None)` / `DisconnectAsync(CancellationToken.None)`.

In `HilViewModel`, add `private CancellationTokenSource? _runCts`, create it in `RunAsync`, pass `_runCts.Token`, and dispose/null it in finally. Add:

```csharp
[RelayCommand]
private void Stop() => _runCts?.Cancel();

private bool CanStop() => IsRunning;
```

Notify `StopCommand` when `IsRunning` changes. Add a separate `catch (OperationCanceledException)` path that renders partial results if `_lastResult` exists and otherwise shows `已取消`.

- [ ] **Step 5: Run tests**

Run both commands from Step 3.

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/PeakCan.Host.Infrastructure/HIL/HilRunnerService.cs src/PeakCan.Host.App/ViewModels/HilViewModel.cs tests/PeakCan.Host.Infrastructure.Tests/HilRunnerServiceCancellationTests.cs tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelStopTests.cs
git commit -m "feat(hil): add stop command and cancellation-safe cleanup"
```

---

### Task 3: Connected-channel snapshot and change notification

**Files:**
- Modify: `src/PeakCan.Host.App/Services/ConnectedChannelsSource.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/HilViewModel.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/AppShellViewModel/ChannelFlow.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/ConnectedChannelsSourceTests.cs`

**Interfaces:**
- Consumes: `ChannelConnectionCoordinator.ConnectionsChanged`.
- Produces: `event Action? Changed` and `ConnectedChannel(ushort Handle, BaudRate BaudRate, bool Fd, string Name, ICanChannel Channel)`.

- [ ] **Step 1: Write Changed-event tests**

```csharp
[Fact]
public void Publish_RaisesChanged_WithSnapshot()
{
    var source = new ConnectedChannelsSource();
    var raised = false;
    source.Changed += () => raised = true;

    source.Publish(new[] { new HilViewModel.ConnectedChannel(0x51, BaudRate.CanFd1Mbps, true, "USB1", new FakeChannel()) });

    Assert.True(raised);
    Assert.Single(source.Current);
}
```

- [ ] **Step 2: Run test and verify failure**

```powershell
dotnet test tests/PeakCan.Host.App.Tests --nologo --filter "FullyQualifiedName~ConnectedChannelsSourceTests"
```

Expected: FAIL because `Changed` and `Channel` do not exist.

- [ ] **Step 3: Implement**

Change `ConnectedChannel` from `readonly record struct` to `sealed record class` and append `ICanChannel Channel`. Update all direct constructions and tests. In `ConnectedChannelsSource`, raise `Changed` after `_current` is assigned. In `ChannelFlow.PublishConnectedChannels`, include `c.Channel`. In `HilViewModel`, subscribe once in the constructor to call `RefreshAvailableChannels()` and notify `RunCommand`/`TrialRunCommand`.

- [ ] **Step 4: Run tests**

Repeat Step 2 and run existing connected-channel tests.

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/PeakCan.Host.App/Services/ConnectedChannelsSource.cs src/PeakCan.Host.App/ViewModels/HilViewModel.cs src/PeakCan.Host.App/ViewModels/AppShellViewModel/ChannelFlow.cs tests/PeakCan.Host.App.Tests/Services/ConnectedChannelsSourceTests.cs
git commit -m "feat(hil): push connected channel snapshots"
```

---

### Task 4: Trial contracts and CanId lookup

**Files:**
- Create: `src/PeakCan.Host.Core/HIL/Contracts/TrialRunContracts.cs`
- Modify: `src/PeakCan.Host.Infrastructure/HIL/Environment/TrialRunner.cs`
- Test: `tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/TrialRunnerCanIdTests.cs`

**Interfaces:**
- Consumes: `CanId`, `FrameFormat`, `RestbusNode`, `TrialContract`.
- Produces: `TrialRunResult`, `TrialDiagnostic`, and `MessageIdLookup = Func<string, CanId?>`.

- [ ] **Step 1: Write CanId matching tests**

Move existing result records into Core, then add:

```csharp
[Fact]
public async Task StandardAndExtendedRaw_AreDistinct()
{
    var channel = new FakeChannel();
    var runner = new TrialRunner(channel)
    {
        MessageIdLookup = name => name == "BRM_EXT"
            ? new CanId(0x100, FrameFormat.Extended)
            : null
    };
    _ = Task.Run(async () =>
    {
        await Task.Delay(25);
        channel.RaiseFrameReceived(new CanFrame(
            new CanId(0x100, FrameFormat.Standard), new byte[] { 1 }, FrameFlags.None, default, default));
    });
var result = await runner.RunTrialAsync([MakeNode("BRM_EXT", 50)], CancellationToken.None);
Assert.False(result.Diagnostics[0].Passed);
}

private static RestbusNode MakeNode(string thenReceive, int timeoutMs) => new()
{
    Name = "T",
    Identity = new RawCanNodeIdentity(),
    Trial = new TrialContract("tpl",
        [new HandshakeExpectation("CRM", thenReceive, timeoutMs, ["cause"])], [])
};
```

- [ ] **Step 2: Run tests and verify failure**

```powershell
dotnet test tests/PeakCan.Host.Infrastructure.Tests --nologo --filter "FullyQualifiedName~TrialRunnerCanIdTests"
```

Expected: FAIL because lookup is still `Func<string, uint?>` and matching ignores frame format.

- [ ] **Step 3: Implement contracts**

Move `TrialDiagnostic` and `TrialRunResult` into `PeakCan.Host.Core.HIL.Contracts`. Change `MessageIdLookup` to `Func<string, CanId?>?`. Remove the unused outer `TimeSpan timeout` parameter from `RunTrialAsync`; use each `HandshakeExpectation.TimeoutMs`. Match frames with `frame.Id.Raw == expectedId.Raw && frame.Id.Format == expectedId.Format`. Update existing tests and callers.

- [ ] **Step 4: Run tests**

Repeat Step 2 and run the existing `TrialRunnerFullCheckTests`.

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/PeakCan.Host.Core/HIL/Contracts/TrialRunContracts.cs src/PeakCan.Host.Infrastructure/HIL/Environment/TrialRunner.cs tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment
git commit -m "refactor(hil): preserve CAN frame format in trial lookup"
```

---

### Task 5: TrialRunService and VM diagnostics

**Files:**
- Create: `src/PeakCan.Host.Core/HIL/Contracts/ITrialRunService.cs`
- Create: `src/PeakCan.Host.Infrastructure/HIL/Environment/TrialRunService.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/HilViewModel.cs`
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs`
- Test: `tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/TrialRunServiceTests.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelTrialTests.cs`

**Interfaces:**
- Consumes: `ITrialRunService.RunAsync(string suitePath, IReadOnlyList<TrialChannelContext> channels, CancellationToken ct)`.
- Produces: `TrialChannelContext(string LogicalName, string? DisplayName, ICanChannel Channel, DbcDocument? Dbc)` and VM collections `TrialDiagnostics`, `IsTrialing`.

- [ ] **Step 1: Write service tests**

Create tests for missing channel, empty environment, preview, runtime lifecycle, and multi-channel routing. For example:

```csharp
[Fact]
public async Task MissingLogicalChannel_ThrowsInvalidOperationException()
{
    var suitePath = WriteSuite("""{"environment":[{"name":"T","channel":"bus-a"}]}""");
    var service = new TrialRunService(
        new RecordingRuntimeFactory(),
        NullLogger<TrialRunService>.Instance);

    await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(
        suitePath,
        [new TrialChannelContext("bus-b", "USB2", new FakeChannel(), null)],
        default));
}
```

- [ ] **Step 2: Write VM tests**

Assert `CanTrial` requires suite path, no preflight critical, Hardware mode, connected channels, and `!IsRunning && !IsTrialing && !IsAnalyzing`. Assert preview result shows `preview（无法完整判定）` and does not present itself as a full handshake pass.

- [ ] **Step 3: Run tests and verify failure**

```powershell
dotnet test tests/PeakCan.Host.Infrastructure.Tests --nologo --filter "FullyQualifiedName~TrialRunServiceTests"
dotnet test tests/PeakCan.Host.App.Tests --nologo --filter "FullyQualifiedName~HilViewModelTrialTests"
```

Expected: FAIL because service/VM integration does not exist.

- [ ] **Step 4: Implement service**

Deserialize suite with `HILJsonOptions.Default`, group nodes by `node.Channel` (empty for single-channel), resolve each `TrialChannelContext`, start one `EnvironmentRuntime` per channel, run one `TrialRunner` per channel, merge diagnostics, then stop runtime and unsubscribe handlers in finally. Never call `DisconnectAsync`.

- [ ] **Step 5: Implement VM**

Replace the `Task.Delay(100)` stub with `ITrialRunService`. Maintain `IsTrialing`, fill `TrialDiagnostics`, and set `TrialRunStatus` from result counts. Register the service in DI.

- [ ] **Step 6: Run tests**

Run both commands from Step 3.

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/PeakCan.Host.Core/HIL/Contracts/ITrialRunService.cs src/PeakCan.Host.Infrastructure/HIL/Environment/TrialRunService.cs src/PeakCan.Host.App/ViewModels/HilViewModel.cs src/PeakCan.Host.App/Composition/AppHostBuilder.cs tests/PeakCan.Host.Infrastructure.Tests/HIL/Environment/TrialRunServiceTests.cs tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelTrialTests.cs
git commit -m "feat(hil): wire trial runner through environment runtime"
```

---

### Task 6: Preflight and editable paths

**Files:**
- Create: `src/PeakCan.Host.App/Services/HIL/SuitePreflightService.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/HilViewModel.cs`
- Modify: `src/PeakCan.Host.App/Views/HilView.xaml`
- Test: `tests/PeakCan.Host.App.Tests/Services/HIL/SuitePreflightServiceTests.cs`

**Interfaces:**
- Consumes: `HILJsonOptions.Default`, `StepValidatorRegistry`, suite JSON.
- Produces: `HilPreflightRequest`, `PreflightResult`, and `_preflightHasCritical`.

- [ ] **Step 1: Write preflight tests**

Cover six cases:

1. Bad JSON returns Critical with line number.
2. Missing suite/DBC file returns Critical.
3. Mode-specific Trace/ECU/Matrix missing file returns Critical.
4. Dangling environment channel returns Critical.
5. Duplicate/invalid channel declarations return Critical.
6. Valid suite returns no Critical.

Use temp files and inline JSON so tests do not depend on real hardware.

- [ ] **Step 2: Run tests and verify failure**

```powershell
dotnet test tests/PeakCan.Host.App.Tests --nologo --filter "FullyQualifiedName~SuitePreflightServiceTests"
```

Expected: FAIL because the service is absent.

- [ ] **Step 3: Implement service**

Define:

```csharp
public sealed record HilPreflightRequest(
    string SuitePath,
    string? DbcPath,
    string? TracePath,
    string? EcuScriptPath,
    string? MatrixPath,
    string? CaseLogDirectory,
    HilMode Mode);

public sealed record PreflightResult(
    IReadOnlyList<PreflightIssue> Issues,
    bool HasCritical);
```

Deserialize suite, run `StepValidatorRegistry`, validate environment channel references, verify current-mode files, and validate non-empty case-log directory path/creatability.

- [ ] **Step 4: Wire VM and paths**

Remove `IsReadOnly` from the five path boxes. Run preflight after Browse and after a 500 ms debounce for manual edits. On any relevant path change, set `_preflightHasCritical = false`, then re-run preflight and notify `RunCommand`. Update the warning banner.

- [ ] **Step 5: Run tests**

Repeat Step 2 and run existing HilViewModel tests.

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/PeakCan.Host.App/Services/HIL/SuitePreflightService.cs src/PeakCan.Host.App/ViewModels/HilViewModel.cs src/PeakCan.Host.App/Views/HilView.xaml tests/PeakCan.Host.App.Tests/Services/HIL/SuitePreflightServiceTests.cs
git commit -m "feat(hil): add run preflight and editable paths"
```

---

### Task 7: Suite change, progress timing, and failure rerun

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/HilViewModel.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelExecutionFlowTests.cs`

**Interfaces:**
- Consumes: `_runCts`, `_lastResult`, `TestProgress.CurrentCaseName`.
- Produces: `RunElapsedText`, `RerunFailedCommand`, and suite-change banner state.

- [ ] **Step 1: Write execution-flow tests**

Use a fake clock/timer and fake runner to assert:

1. External suite change shows banner and does not auto-reload.
2. Reload preserves checked case ids.
3. Progress callback updates current case and percent.
4. Timer starts/stops with `IsRunning`.
5. Rerun only executes previously failed case names and preserves user checks.
6. Skipped-only partial result does not enable rerun.

```csharp
[Fact]
public void RerunFailed_IsDisabled_WhenOnlySkippedCasesRemain()
{
    var vm = CreateViewModel();
    vm.SetLastResult(new TestSuiteResult(
        "S", 2, 1, 0, 1, 100,
        Array.Empty<string>(),
        new[] { MakeCaseResult("case_1", passed: true) }));

    Assert.False(vm.RerunFailedCommand.CanExecute(null));
}
```

- [ ] **Step 2: Run tests and verify failure**

```powershell
dotnet test tests/PeakCan.Host.App.Tests --nologo --filter "FullyQualifiedName~HilViewModelExecutionFlowTests"
```

Expected: FAIL because the features are absent.

- [ ] **Step 3: Implement**

Add a 2-second suite fingerprint timer comparing `LastWriteTime + Length`, a 1-second run timer for `RunElapsedText`, and store `CurrentCaseName`/`CompletedCases` from progress. Set `RerunFailedCommand.CanExecute` to `!IsRunning && !IsTrialing && !IsAnalyzing && _lastResult is { FailedCases: > 0 }`. Rerun should override only the request’s `SelectedCaseNames`, leave item checkboxes unchanged, and replace `_lastResult`.

- [ ] **Step 4: Run tests**

Repeat Step 2.

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/PeakCan.Host.App/ViewModels/HilViewModel.cs tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelExecutionFlowTests.cs
git commit -m "feat(hil): add suite change detection timing and rerun"
```

---

### Task 8: Run configuration, filtering, and multi-channel gating

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/HilViewModel.cs`
- Modify: `src/PeakCan.Host.App/Views/HilView.xaml`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelRunConfigurationTests.cs`

**Interfaces:**
- Consumes: `LoadCaseList`, `BuildHardwareChannels`, `HilRunRequest.CaseLogDirectory`.
- Produces: `CaseLogDirectory`, `CaseFilter`, `SelectedCaseCount`, and cached `DeclaredChannelCount`.

- [ ] **Step 1: Write configuration tests**

Assert:

1. `CanRun=false` when declared channel count exceeds connected count.
2. `CanRun=false` when declarations fail to parse or contain duplicates.
3. `SelectedCaseNames` is null when no case list exists.
4. `SelectedCaseNames` is empty only when cases exist and all are unchecked; this state disables Run.
5. Case filter preserves checked state and all-select affects only visible items.
6. Log directory is passed to the request.

```csharp
[Fact]
public void CanRun_IsFalse_WhenNoCasesSelected()
{
    var vm = CreateConfiguredHardwareViewModel();
    vm.SelectNoCases();
    Assert.False(vm.RunCommand.CanExecute(null));
}
```

- [ ] **Step 2: Run tests and verify failure**

```powershell
dotnet test tests/PeakCan.Host.App.Tests --nologo --filter "FullyQualifiedName~HilViewModelRunConfigurationTests"
```

Expected: FAIL.

- [ ] **Step 3: Implement**

Merge cases and channel declarations into one JSON parse in `LoadCaseList`; cache declared count and validity. Remove `Math.Min` truncation and silent single-channel fallback. Add `CaseFilter`, a 300 ms debounce, and visible-item all-select behavior. Add case-log directory TextBox/Browse/Open; Open should create the directory best-effort before `Process.Start`.

- [ ] **Step 4: Run tests**

Repeat Step 2 and run existing truncation tests.

Expected: PASS after rewriting old truncation expectations to interception expectations.

- [ ] **Step 5: Commit**

```powershell
git add src/PeakCan.Host.App/ViewModels/HilViewModel.cs src/PeakCan.Host.App/Views/HilView.xaml tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelRunConfigurationTests.cs
git commit -m "feat(hil): enforce run configuration and case filtering"
```

---

### Task 9: Panel state persistence

**Files:**
- Create: `src/PeakCan.Host.App/Services/HIL/HilPanelStateStore.cs`
- Modify: `src/PeakCan.Host.App/Windows/HilWindow.xaml.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/HilViewModel.cs`
- Modify: `src/PeakCan.Host.App/Composition/AppHostBuilder.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/HIL/HilPanelStateStoreTests.cs`

**Interfaces:**
- Consumes: `LayoutStateStore` persistence pattern.
- Produces: `HilPanelStateDto`, `HilPanelStateStore.LoadAsync`, and `HilPanelStateStore.Set`.

- [ ] **Step 1: Write store tests**

Mirror `LayoutStateStoreTests`:

1. Round trip preserves every field.
2. Missing file returns null.
3. Corrupt JSON returns null and logs.
4. Oversized file is treated as empty.
5. Reload from disk restores DTO.

Use `Path.Combine(Path.GetTempPath(), $"hil-panel-{Guid.NewGuid():N}.json")`.

```csharp
[Fact]
public void Set_ThenGet_RoundTripsSelectedCaseIds()
{
    var store = CreateStore();
    var dto = MakePanelState();
    dto = dto with { SelectedCaseIds = ["case_1", "case_2"] };
    store.Set(dto);
    store.Get().Should().Be(dto);
}
```

- [ ] **Step 2: Run tests and verify failure**

```powershell
dotnet test tests/PeakCan.Host.App.Tests --nologo --filter "FullyQualifiedName~HilPanelStateStoreTests"
```

Expected: FAIL because the store is absent.

- [ ] **Step 3: Implement**

Use schema envelope `hil-panel/v1`, atomic `tmp` + rename, and 1 MB load cap. Persist `selectedMode`, paths, `caseLogDirectory`, toggles, and `selectedCaseIds`. Remove `_hardwareChannel = "USB1"`. Load on `HilWindow.Loaded`, then restore path, load case list, run preflight, and re-check by case id.

- [ ] **Step 4: Run tests**

Repeat Step 2 and run HilWindow/persistence tests.

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/PeakCan.Host.App/Services/HIL/HilPanelStateStore.cs src/PeakCan.Host.App/Windows/HilWindow.xaml.cs src/PeakCan.Host.App/ViewModels/HilViewModel.cs src/PeakCan.Host.App/Composition/AppHostBuilder.cs tests/PeakCan.Host.App.Tests/Services/HIL/HilPanelStateStoreTests.cs
git commit -m "feat(hil): persist hil panel state"
```

---

### Task 10: Run history and failure detail copy

**Files:**
- Create: `src/PeakCan.Host.App/Services/HIL/HilRunHistoryStore.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/HilViewModel.cs`
- Modify: `src/PeakCan.Host.App/Views/HilView.xaml`
- Modify: `src/PeakCan.Host.App/Views/HilView.xaml.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/HIL/HilRunHistoryStoreTests.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelHistoryTests.cs`

**Interfaces:**
- Consumes: `HilRunRequest`, `TestSuiteResult`, `_runCts.IsCancellationRequested`.
- Produces: `HilRunHistoryDto` with `errorMessage`, and `BuildFailureDetail(StepNode node)`.

- [ ] **Step 1: Write history store tests**

Assert round trip, corruption tolerance, oversized-file tolerance, and 50-item tail trimming. Include a DTO with `errorMessage`.

```csharp
[Fact]
public void Save_Trims_Tail_AfterFiftyRecords()
{
    var store = CreateStore();
    foreach (var index in Enumerable.Range(0, 60))
        store.Append(MakeHistory($"suite-{index:00}"));

    var history = store.Get();
    history.Should().HaveCount(50);
    history.First().SuitePath.Should().EndWith("suite-10");
}
```

- [ ] **Step 2: Write history VM tests**

Use fake runner to assert four write paths: normal completion, partial user cancel (`cancelled=true`), partial timeout (`cancelled=false`, timeout error), and exception with no result. Assert trial/analysis never writes history.

- [ ] **Step 3: Run tests and verify failure**

```powershell
dotnet test tests/PeakCan.Host.App.Tests --nologo --filter "FullyQualifiedName~HilRunHistory"
```

Expected: FAIL because store/history integration is absent.

- [ ] **Step 4: Implement history**

Use schema envelope `hil-history/v1`, atomic writes, and a 50-record cap. Add a fourth History tab with time, suite, mode, result, elapsed, cancelled marker, and error tooltip. `OpenReport` should be enabled whenever `reportPath` is non-empty, recheck `File.Exists` on click, and show a friendly message if the file disappeared. `LoadSuite` should set path, reload cases, and run preflight.

- [ ] **Step 5: Implement failure copy**

Add a ContextMenu to failed `StepNode` templates. Extract a pure code-behind helper:

```csharp
internal static string BuildFailureDetail(
    string suiteName,
    TestCaseNode caseNode,
    StepNode stepNode) =>
    string.Join(Environment.NewLine,
        $"Suite: {suiteName}",
        $"Case: {caseNode.Name}",
        $"Step: {stepNode.Name}",
        $"Status: {stepNode.Status}",
        $"Message: {stepNode.Message}",
        $"Actual: {stepNode.ActualValue}",
        $"Expected: {stepNode.ExpectedValue}",
        $"Channel: {stepNode.Channel}",
        $"Frames: {string.Join("; ", stepNode.Frames.Select(f => $"{f.CanId} {f.DataHex}"))}");
```

Unit-test the helper; call `Clipboard.SetText` only from code-behind.

- [ ] **Step 6: Run tests**

Repeat Step 3 and run failure-copy tests.

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/PeakCan.Host.App/Services/HIL/HilRunHistoryStore.cs src/PeakCan.Host.App/ViewModels/HilViewModel.cs src/PeakCan.Host.App/Views/HilView.xaml src/PeakCan.Host.App/Views/HilView.xaml.cs tests/PeakCan.Host.App.Tests/Services/HIL/HilRunHistoryStoreTests.cs tests/PeakCan.Host.App.Tests/ViewModels/HilViewModelHistoryTests.cs
git commit -m "feat(hil): add run history and failure copy"
```

---

### Task 11: Chinese copy, layout polish, and constants

**Files:**
- Modify: `src/PeakCan.Host.App/Views/HilView.xaml`
- Modify: `src/PeakCan.Host.App/ViewModels/HilViewModel.cs`
- Test: `tests/PeakCan.Host.App.Tests/Views/HilViewLayoutTests.cs`

**Interfaces:**
- Consumes: all functional commands created earlier.
- Produces: final Chinese labels, responsive top layout, new diagnostic/history panels, and named PCAN constants.

- [ ] **Step 1: Write XAML/localization assertions**

Add smoke tests that load the XAML string and assert:

1. `Mode:`, `Run`, `Browse...`, `Faults`, `Analyze`, `Open ECU Editor`, and English tab names are absent.
2. Stop button binds `StopCommand`.
3. Log-directory row is bound and only visible when `CaptureCaseLogs=true`.
4. Trial diagnostics DataGrid has four bound columns.
5. History tab exists.

```csharp
[Fact]
public void HilView_DoesNotContainLegacyEnglishButtonCopy()
{
    var xaml = File.ReadAllText(ResolveHilViewPath());
    xaml.Should().NotContain("Content=\"Mode:\"");
    xaml.Should().NotContain("Content=\"Run\"");
    xaml.Should().NotContain("Content=\"Browse...\"");
    xaml.Should().Contain("Content=\"停止\"");
}
```

- [ ] **Step 2: Run tests and verify failure**

```powershell
dotnet test tests/PeakCan.Host.App.Tests --nologo --filter "FullyQualifiedName~HilViewLayoutTests"
```

Expected: FAIL.

- [ ] **Step 3: Implement layout**

Convert the top configuration area to `WrapPanel`, replace hard-coded widths `150/180/260/160/170` with relative sizing, group checkboxes, add the Stop button, trial diagnostics Expander, and History tab. Keep the WebView2 report Grid/Border/fallback TextBlock structure unchanged. Move `0x8000` and `0x50` into `PcanHandleConstants` with source comments.

- [ ] **Step 4: Run tests**

Repeat Step 2 and run all App tests.

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/PeakCan.Host.App/Views/HilView.xaml src/PeakCan.Host.App/ViewModels/HilViewModel.cs tests/PeakCan.Host.App.Tests/Views/HilViewLayoutTests.cs
git commit -m "style(hil): localize and reorganize hil view"
```

---

## Final Validation

```powershell
dotnet build PeakCan.Host.slnx --nologo
dotnet test tests/PeakCan.Host.Core.Tests --nologo
dotnet test tests/PeakCan.Host.Infrastructure.Tests --nologo
dotnet test tests/PeakCan.Host.App.Tests --nologo
git diff --check
```

Manual acceptance:

1. Run a long suite and stop mid-case; verify completed/cancelled cases render and report generates.
2. Run a suite with `TimeoutMs`; verify status says timeout, not user cancellation.
3. Trial a Hardware suite; verify runtime starts, diagnostics render, and channel remains connected.
4. Run with insufficient channels and zero selected cases; verify Run is disabled with a clear reason.
5. Reopen HIL window and verify paths, toggles, and case selection restore.
6. Verify History tab shows normal/cancel/timeout/exception records and stale report paths are handled gracefully.

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Engine cancellation changes fixture lifecycle behavior | Medium | Task 1 uses recording fixtures and explicitly asserts `CancellationToken.None` on teardown. |
| Trial runtime lifecycle leaks handlers | Medium | Task 5 uses recording runtime/factory tests and finally cleanup. |
| Legacy tests assume truncation behavior | Medium | Task 8 rewrites truncation tests to interception tests. |
| XAML layout breaks WebView2 report airspace | Medium | Task 11 preserves the existing report Grid/Border/fallback structure. |
| Persistence stores drift from JSON schema | Low | Tasks 9/10 mirror `LayoutStateStoreTests` and include schema-envelope round trips. |
