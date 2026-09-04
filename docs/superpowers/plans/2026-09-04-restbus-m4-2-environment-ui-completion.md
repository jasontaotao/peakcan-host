# Restbus M4.2 Environment UI Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the M2 environment-configuration gaps exposed by M4.1: signal overrides, source-channel persistence, save-time validation, runtime signal steps, read-only rules, and bus-load preview.

**Architecture:** Keep `RestbusNode` as the single persisted model. Add an optional `SourceChannel` to preserve DBC origin semantics. Studio becomes the configuration owner: it edits signal overrides through a DBC-aware VM, validates environment before save, exposes runtime `SetEnvironmentSignal` steps, and presents rules/load as read-only diagnostics. No new host execution path is introduced.

**Tech Stack:** .NET 10, C# records, xUnit, WPF/CommunityToolkit MVVM, System.Text.Json.

**Spec:** `D:\claude_proj2\peakcan-host\docs\superpowers\specs\2026-09-03-restbus-node-unification-design.md`

## Global Constraints

- DBC remains the only normal-UI source for CAN IDs, message geometry, intervals, and signal layout.
- Template rules are read-only in the normal UI; no blank ECA rule creation.
- Environment nodes persist inside `TestSuite.Environment`.
- `SignalOverrides` keys remain `"MessageName.SignalName"`.
- Runtime payload encoding remains unchanged in host.
- Existing suite JSON must deserialize without migration.
- No Python; use PowerShell and dotnet CLI.
- Do not add scripted dynamic payloads in M4.2.

---

### Task 1: Persist DBC source channel in hil-core

**Files:**
- Modify: `peakcan-hil-core/src/PeakCan.HIL.Core/HIL/Environment/RestbusNode.cs`
- Modify: `peakcan-hil-core/src/PeakCan.HIL.Core/HIL/Environment/RestbusNodeValidator.cs`
- Test: `peakcan-hil-core/tests/PeakCan.HIL.Core.Tests/HIL/Environment/RestbusNodeValidatorTests.cs`

**Interfaces:**
- Produces: `RestbusNode.SourceChannel?: string`
- Produces: validator errors when `SourceChannel` is undeclared or disagrees with `Channel`.

- [ ] Write failing validator tests for source/channel agreement, undeclared source, and null compatibility.
- [ ] Run hil-core environment tests; expect new cases to fail.
- [ ] Add optional `SourceChannel` to `RestbusNode`.
- [ ] Extend `RestbusNodeValidator`:
  - if `Channels` is non-empty, `SourceChannel`, when set, must exist;
  - if `SourceChannel` is set, `Channel` must equal it;
  - old JSON without `SourceChannel` remains valid.
- [ ] Run tests and build.
- [ ] Commit: `feat(core): persist restbus source channel binding`.

### Task 2: Studio source-channel semantics

**Files:**
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentTabViewModel.cs`
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentNodeViewModel.cs`
- Test: `peakcan-studio/tests/PeakCan.Studio.App.Tests/Restbus/EnvironmentTabChannelTests.cs`

**Interfaces:**
- Consumes: `RestbusNode.SourceChannel`
- Produces: `SourceLabel` derived from persisted source; channel choices restricted when source exists.

- [ ] Write failing tests: generate sets `SourceChannel`; load preserves it; undeclared source cannot generate.
- [ ] Replace memory-only `_sourceChannel` behavior with persisted `SourceChannel`.
- [ ] Update `SourceLabel` to use `SourceChannel`.
- [ ] Keep `Channel` synchronized with `SourceChannel`.
- [ ] Run Restbus VM tests.
- [ ] Commit: `fix(studio): persist DBC source channel on environment nodes`.

### Task 3: Signal override editor model

**Files:**
- Create: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentSignalValueViewModel.cs`
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentNodeViewModel.cs`
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentNodeMessageViewModel.cs`
- Test: `peakcan-studio/tests/PeakCan.Studio.App.Tests/Restbus/EnvironmentSignalOverrideTests.cs`

**Interfaces:**
- Consumes: `DbcDocument`, `NodeMessage.Payload`, `RestbusNode.SignalOverrides`
- Produces: observable rows with `SignalName`, `Value`, `Min`, `Max`, `Unit`, `IsOverridden`, `ErrorText`.
- Produces: `SetSignalValue(messageName, signalName, value)` updates the immutable node.

- [ ] Write failing tests for initial value, override persistence, min/max rejection, and removing/ignoring unchanged default.
- [ ] Build signal rows from `DbcSignalsSource` and matching DBC message signals.
- [ ] Initialize display value from existing override, else DBC `Offset`.
- [ ] Validate `[Min, Max]`; do not update the node on invalid values.
- [ ] Preserve overrides in `ToNode()` and suite round trip.
- [ ] Run tests.
- [ ] Commit: `feat(studio): add environment signal override editor model`.

### Task 4: Environment tab signal editor UI

**Files:**
- Modify: `peakcan-studio/src/PeakCan.Studio.App/Views/EnvironmentTab.xaml`
- Modify: `peakcan-studio/src/PeakCan.Studio.App/Windows/HilStudioWindow.xaml.cs`
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/HilStudioViewModel.cs`
- Test: `peakcan-studio/tests/PeakCan.Studio.App.Tests/Restbus/EnvironmentSignalOverrideTests.cs`

**Interfaces:**
- Consumes: signal rows from Task 3
- Produces: each DBC message expands a signal table; missing DBC shows a warning instead of a fake editor.

- [ ] Add an expandable signal section under each message row.
- [ ] Bind numeric values, ranges, units, override state, and errors.
- [ ] Pass the per-channel DBC provider into `EnvironmentTabViewModel`.
- [ ] Show `DBC 不可用` when signal layout cannot be resolved.
- [ ] Build Studio and run Restbus tests.
- [ ] Commit: `feat(studio): expose environment signal editing in node cards`.

### Task 5: Save-time environment validation and channel sync

**Files:**
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentTabViewModel.cs`
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/TestSuiteBuilder/RoundTripFlow.partial.cs`
- Modify: `peakcan-studio/src/PeakCan.Studio.App/Windows/HilStudioWindow.xaml.cs`
- Test: `peakcan-studio/tests/PeakCan.Studio.App.Tests/Restbus/EnvironmentSaveValidationTests.cs`

**Interfaces:**
- Consumes: `RestbusNodeValidator.Validate`
- Produces: `GetValidationErrors(channels)` and save-blocking behavior.

- [ ] Write failing tests for duplicate nodes, missing channel, undeclared source channel, and invalid override target.
- [ ] Add environment validation before `SaveToSuite`.
- [ ] Block save when errors exist and surface a readable error.
- [ ] Re-run validation when suite channel declarations change.
- [ ] Run Studio tests.
- [ ] Commit: `feat(studio): validate environment nodes before suite save`.

### Task 6: Read-only rules presentation

**Files:**
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentNodeViewModel.cs`
- Modify: `peakcan-studio/src/PeakCan.Studio.App/Views/EnvironmentTab.xaml`
- Test: `peakcan-studio/tests/PeakCan.Studio.App.Tests/Restbus/EnvironmentRulesPresentationTests.cs`

**Interfaces:**
- Produces: `RuleRows` with trigger, condition, action, and delay text.
- Produces: no command that edits or creates rules.

- [ ] Write failing tests for template node rule summaries and DBC node empty-rules state.
- [ ] Add read-only rule row VMs.
- [ ] Add an expandable rules section to the node card.
- [ ] Ensure template nodes show all `ResponseRule` entries; DBC nodes show an explicit empty state.
- [ ] Run tests and build.
- [ ] Commit: `feat(studio): show restbus template rules read-only`.

### Task 7: Static bus-load preview

**Files:**
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentTabViewModel.cs`
- Modify: `peakcan-studio/src/PeakCan.Studio.App/Views/EnvironmentTab.xaml`
- Test: `peakcan-studio/tests/PeakCan.Studio.App.Tests/Restbus/EnvironmentBusLoadTests.cs`

**Interfaces:**
- Consumes: `RestbusNode.Messages`, `ChannelConfig.BaudRate`, `ChannelConfig.Fd`
- Produces: `BusLoadText`, `FrameRateText`, and `RecalculateBusLoad()`.

- [ ] Write failing tests for single-channel, multi-channel, missing baud rate, and disabled messages.
- [ ] Replace name-only channel propagation with `ChannelConfig`-aware propagation.
- [ ] Aggregate per-channel load and total frame rate.
- [ ] Bind preview text above the enabled node list.
- [ ] Run tests and build.
- [ ] Commit: `feat(studio): add static environment bus load preview`.

### Task 8: SetEnvironmentSignal step in Studio

**Files:**
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/TestSuiteBuilder/StepKindInfo.cs`
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/TestSuiteBuilder/StepFieldDescriptors.cs`
- Test: `peakcan-studio/tests/PeakCan.Studio.App.Tests/TestSuiteBuilder/SetEnvironmentSignalStepTests.cs`

**Interfaces:**
- Consumes: `TestCaseStepKind.SetEnvironmentSignal`, `SetEnvironmentSignalStep`
- Produces: toolbox entry and fields `NodeName`, `MessageName`, `SignalName`, `Value`.

- [ ] Write failing tests for toolbox registration, defaults, and table slots.
- [ ] Add `SetEnvironmentSignal` to the toolbox.
- [ ] Add field descriptors, defaults, and table slots.
- [ ] Validate required parameter mapping against `StepParametersFactory`.
- [ ] Run Studio test suite.
- [ ] Commit: `feat(studio): support SetEnvironmentSignal step editing`.

### Task 9: Save-as-template UI

**Files:**
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentTabViewModel.cs`
- Modify: `peakcan-studio/src/PeakCan.Studio.App/Views/EnvironmentTab.xaml`
- Test: `peakcan-studio/tests/PeakCan.Studio.App.Tests/Restbus/SaveAsTemplateTests.cs`

**Interfaces:**
- Consumes: `EnvironmentNodeViewModel.SaveAsTemplate`
- Produces: node-card command that refreshes `AvailableTemplates`.

- [ ] Write failing test that saving a node adds a `user-*` template to the catalog.
- [ ] Add “另存为模板” command and confirm default override stripping.
- [ ] Refresh the template list after save.
- [ ] Run tests and build.
- [ ] Commit: `feat(studio): expose save environment node as template`.

### Task 10: Dirty-state and runtime warning surfaces

**Files:**
- Modify: `peakcan-studio/src/PeakCan.Studio.App/Services/UndoRedoManager.cs`
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/TestSuiteBuilder/TestSuiteBuilderViewModel.cs`
- Modify: `peakcan-studio/src/PeakCan.Studio.App/ViewModels/Restbus/EnvironmentTabViewModel.cs`
- Modify: `peakcan-studio/src/PeakCan.Studio.App/Views/EnvironmentTab.xaml`
- Test: `peakcan-studio/tests/PeakCan.Studio.App.Tests/Restbus/EnvironmentDirtyAndWarningsTests.cs`

**Interfaces:**
- Produces: `UndoRedoManager.MarkDirty()`.
- Produces: environment mutation event and `Warnings` collection for missing DBC / unsupported script payloads.

- [ ] Write failing tests for dirty state and warning aggregation.
- [ ] Add `MarkDirty()` and invoke it from environment mutations.
- [ ] Emit warnings when a saved DBC node has no current DBC or uses unsupported `ScriptCallbackSource`.
- [ ] Bind warnings to the environment header.
- [ ] Run full Studio tests and build.
- [ ] Commit: `feat(studio): track environment dirty state and runtime warnings`.

### Task 11: Final verification

**Files:**
- Modify: `D:\claude_proj2\.sdd\restbus-unification\ledger.md`

- [ ] Run hil-core environment tests.
- [ ] Run Studio tests and Release build.
- [ ] Run host Infrastructure tests.
- [ ] Verify an old suite JSON without `SourceChannel` loads unchanged.
- [ ] Manually check Environment tab: generate DBC node, edit signal, save/reload, view rules/load, save-as-template.
- [ ] Update ledger with decisions and completed scope.
- [ ] Commit: `docs: mark restbus M4.2 environment UI completion`.
