# Restbus M5 Trace-to-Environment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an explicit host workflow that turns a recorded/loaded ASC or BLF trace into selected periodic Restbus nodes and writes them into an existing TestSuite.

**Architecture:** Keep recognition and node construction as pure Host.Core services over parsed `ReplayFrame` data; keep suite mutation behind a small atomic writer. Add a WPF preview dialog on the Recording panel so the user sees every candidate frame group, excludes DUT traffic, and confirms before any suite is changed. DBC-backed messages use the existing DBC signal pipeline; messages without a DBC definition fall back to captured fixed payload and are clearly marked as non-signal-editable.

**Tech Stack:** .NET 10, C# records, xUnit, FluentAssertions, WPF/MVVM, System.Text.Json, existing `PeakCan.HIL.Core` DBC/Environment types.

**Spec:** `docs/superpowers/specs/2026-09-03-restbus-node-unification-design.md` §5, §12, §14 M5.

## Global Constraints

- `NodeMessage.IntervalMs >= 10`; quantize from trace median intervals, never emit a smaller interval.
- Environment is embedded in `TestSuite.Environment`; do not introduce external environment references.
- Ordinary generated nodes remain read-only in the UI; do not infer ECA rules, UDS behavior, or `TrialContract` from traces.
- No hardware access in recognition, node building, or suite writing.
- Suite writes are explicit, validated, and atomic; never silently replace an existing node or conflicting frame.
- Preserve CAN FD semantics in `NodeMessage.Fd`.
- Use `HILJsonOptions.Default` for all TestSuite JSON operations.
- Suggested branches: hil-core `feat/restbus-m5-trace-to-environment`, host `feat/restbus-m5-trace-to-environment`.
- hil-core must be merged before host because Host.Core has a local sibling ProjectReference.

## Execution Status (2026-09-04)

- Tasks 1-7 implemented and committed.
- Focused tests: hil-core DBC generator 8/8; Host.Core Replay 136/136; recognizer 6/6; builder 5/5; suite writer 4/4; Trace-to-Environment VM 1/1.
- Full verification: hil-core 298/298 PASS; Host App Release build PASS.
- E2E test: TraceToEnvironmentEndToEndTests 1/1 PASS (periodic, irregular, J1939, multi-channel grouping, conflict preservation).

## M5 Rulings

1. M5.1 covers ASC + BLF. CSV recording remains disabled in the current Recording UI and is out of scope.
2. Trace channels are restored by adding `Channel` to `ReplayFrame`; ASC and BLF parsers populate it. A missing ASC channel defaults to `0`.
3. Periodic recognition uses the median frame-to-frame interval and median absolute deviation. A group is periodic when it has at least 4 valid deltas, median >= 10 ms, and `MAD / median <= 0.35`.
4. For extended IDs, the default node grouping is J1939 source address. A source can be excluded as DUT with one checkbox. Standard IDs are grouped and excluded individually.
5. If the target suite declares channels, every imported node must have `Channel == SourceChannel`, and both must name a declared suite channel. The dialog maps a numeric trace channel to a suite channel only when the suite `ChannelConfig.Handle` parses to the same value; otherwise the user chooses the channel. Single-channel suites leave `Channel` null.
6. DBC-backed selected messages use `DbcSignalsSource` and captured physical values as `SignalOverrides`; `AutoCounter` / `AutoChecksum` are detected through `DbcRestbusGenerator`. Unmapped messages use `FixedHexSource` and remain visible as fixed-payload nodes.
7. M5 does not derive templates or rules. The generated node has empty `Rules`, null `UdsBehavior`, and null `Trial`.

---

### Task 1: hil-core DBC message factory

**Files:**
- Modify: `src/PeakCan.HIL.Core/HIL/Environment/DbcRestbusGenerator.cs`
- Test: `tests/PeakCan.HIL.Core.Tests/HIL/Environment/DbcRestbusGeneratorTests.cs`

**Interfaces:**
- Produces:

```csharp
public static NodeMessage CreateDbcNodeMessage(
    Message message,
    int intervalMs,
    GeneratorOptions? options = null,
    ICollection<string>? warnings = null);
```

`DbcRestbusGenerator.Generate` must delegate to this method so single-message behavior and whole-node behavior stay identical.

- [x] **Step 1: Write failing tests**

```csharp
[Fact]
public void CreateDbcNodeMessage_Preserves_Counter_Checksum_And_Fd()
{
    var message = DbcFixtures.MessageWithCounterChecksum(); // reuse the existing local fixture style
    var warnings = new List<string>();

    var actual = DbcRestbusGenerator.CreateDbcNodeMessage(
        message, 100, new GeneratorOptions("Cnt", "CRC"), warnings);

    actual.Ref.Should().BeEquivalentTo(new CanMessageRef(message.Id & 0x1FFFFFFFu, true));
    actual.IntervalMs.Should().Be(100);
    actual.Fd.Should().BeTrue();
    actual.AutoCounter.Should().NotBeNull();
    actual.AutoChecksum.Should().NotBeNull();
    warnings.Should().BeEmpty();
}

[Fact]
public void CreateDbcNodeMessage_Rejects_Interval_Below_Ten()
{
    var act = () => DbcRestbusGenerator.CreateDbcNodeMessage(DbcFixtures.SimpleMessage(), 9);
    act.Should().Throw<ArgumentOutOfRangeException>();
}
```

- [x] **Step 2: Run tests**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "FullyQualifiedName~DbcRestbusGeneratorTests"`
Expected: the two new tests fail because the public method does not exist.

- [x] **Step 3: Implement**

Rename the private `CreateNodeMessage` body to `CreateDbcNodeMessage`, add argument checks for `message` and `intervalMs`, and update `Generate` to call it. Keep warning behavior unchanged.

- [x] **Step 4: Run tests**

Run: `dotnet test tests/PeakCan.HIL.Core.Tests --filter "FullyQualifiedName~DbcRestbusGeneratorTests"`
Expected: all DBC generator tests pass.

- [x] **Step 5: Commit**

```bash
git add src/PeakCan.HIL.Core/HIL/Environment/DbcRestbusGenerator.cs tests/PeakCan.HIL.Core.Tests/HIL/Environment/DbcRestbusGeneratorTests.cs
git commit -m "feat(core): expose dbc restbus message factory"
```

---

### Task 2: Restore trace channel in ReplayFrame and parsers

**Files:**
- Modify: `src/PeakCan.Host.Core/Replay/ReplayFrame.cs`
- Modify: `src/PeakCan.Host.Core/Replay/AscFormat.cs`
- Modify: `src/PeakCan.Host.Core/Replay/BlfParser/CanMessageFlow.cs`
- Modify: `src/PeakCan.Host.Core/Replay/BlfParser/CanMessage2Flow.cs`
- Modify: `src/PeakCan.Host.Core/Replay/BlfParser/CanFdMessageFlow.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Replay/AscParserTests.cs`
- Test: `tests/PeakCan.Host.Core.Tests/Replay/BlfParserTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record ReplayFrame(
    double Timestamp,
    uint Id,
    byte Dlc,
    byte[] Data,
    FrameFlags Flags,
    bool IsExtended = false,
    ushort Channel = 0);
```

Existing callers continue to compile because `Channel` is optional.

- [x] **Step 1: Write failing parser tests**

```csharp
[Fact]
public void Asc_DataLine_Preserves_Channel()
{
    var ok = AscFormat.TryParseDataLine(
        "0.100000 02  1F3  8  01 02 03 04 05 06 07 08",
        out var frame, out var reason);

    ok.Should().BeTrue(reason);
    frame.Channel.Should().Be((ushort)0x02);
}

[Fact]
public void Blf_CanMessage_Preserves_Channel()
{
    var frame = BlfTestObjects.CanMessage(channel: 0x02, id: 0x1F3, data: [0x01, 0x02]);
    frame.Channel.Should().Be((ushort)0x02);
}
```

Adapt fixture construction to the existing BLF parser tests.

- [x] **Step 2: Run tests**

Run: `dotnet test tests/PeakCan.Host.Core.Tests --filter "FullyQualifiedName~Replay"`
Expected: new channel assertions fail.

- [x] **Step 3: Implement**

Add optional `Channel` to `ReplayFrame`. Parse ASC token 1 as `ushort.TryParse(..., NumberStyles.HexNumber, InvariantCulture)` and pass it to the record. Pass the BLF `channel` value in the three CAN message flows.

- [x] **Step 4: Run tests**

Run: `dotnet test tests/PeakCan.Host.Core.Tests --filter "FullyQualifiedName~Replay"`
Expected: all replay parser and timeline tests pass.

- [x] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/Replay tests/PeakCan.Host.Core.Tests/Replay
git commit -m "feat(host): preserve trace channel on replay frames"
```

---

### Task 3: Pure trace-to-environment recognizer

**Files:**
- Create: `src/PeakCan.Host.Core/HIL/Environment/TraceRestbusRecognizer.cs`
- Create: `src/PeakCan.Host.Core/HIL/Environment/TraceRestbusModels.cs`
- Test: `tests/PeakCan.Host.Core.Tests/HIL/Environment/TraceRestbusRecognizerTests.cs`

**Interfaces:**
- Consumes: `ReplayFrame`, `CanMessageRef`, `J1939MessageRef`, `J1939Id`.
- Produces:

```csharp
public sealed record TraceRecognitionOptions(
    int MinFrames = 4,
    double MaxIntervalCv = 0.35,
    IReadOnlySet<uint>? ExcludedIds = null,
    IReadOnlySet<byte>? ExcludedJ1939SourceAddresses = null);

public sealed record TraceFrameCandidate(
    ushort Channel,
    uint Id,
    bool IsExtended,
    int FrameCount,
    int IntervalMs,
    double IntervalCv,
    bool IsPeriodic,
    bool IsFd,
    byte? SourceAddress,
    byte? DestinationAddress,
    uint? Priority,
    uint? Pgn,
    byte[] RepresentativePayload);

public sealed record TraceRecognitionResult(
    IReadOnlyList<TraceFrameCandidate> Candidates,
    IReadOnlyList<string> Warnings);

public static class TraceRestbusRecognizer
{
    public static TraceRecognitionResult Recognize(
        IReadOnlyList<ReplayFrame> frames,
        TraceRecognitionOptions? options = null);
}
```

- [x] **Step 1: Write failing tests**

```csharp
[Fact]
public void Groups_Standard_Ids_By_Channel_And_Id()
{
    var frames = PeriodicFrames(channel: 1, id: 0x123, intervalSec: 0.02, count: 5);
    var result = TraceRestbusRecognizer.Recognize(frames);

    result.Candidates.Should().HaveCount(1);
    var candidate = result.Candidates.Single();
    candidate.Channel.Should().Be(1);
    candidate.Id.Should().Be(0x123u);
    candidate.IntervalMs.Should().Be(20);
    candidate.IsPeriodic.Should().BeTrue();
}

[Fact]
public void Groups_Extended_Ids_By_J1939_Source_Address()
{
    var frames = new[]
    {
        Frame(0.00, J1939Id.Compose(6, 0xFF00, 0x11), [1], true, 2),
        Frame(0.02, J1939Id.Compose(6, 0xFF01, 0x11), [2], true, 2),
        Frame(0.00, J1939Id.Compose(6, 0xFF00, 0x22), [3], true, 2),
    };

    var result = TraceRestbusRecognizer.Recognize(frames);
    result.Candidates.Should().HaveCount(3);
    result.Candidates.Count(c => c.SourceAddress == 0x11).Should().Be(2);
}

[Fact]
public void Marks_Irregular_Group_As_Non_Periodic()
{
    var frames = new[]
    {
        Frame(0.00, 0x321, [1], false, 1),
        Frame(0.13, 0x321, [1], false, 1),
        Frame(0.17, 0x321, [1], false, 1),
        Frame(0.98, 0x321, [1], false, 1),
    };

    var result = TraceRestbusRecognizer.Recognize(frames);
    result.Candidates.Single().IsPeriodic.Should().BeFalse();
}

[Fact]
public void Excludes_Selected_Ids_And_J1939_Source_Addresses()
{
    var frames = PeriodicFrames(1, 0x123, 0.02, 5)
        .Concat(PeriodicFrames(1, 0x456, 0.02, 5))
        .Concat(PeriodicFrames(1, J1939Id.Compose(6, 0xFF00, 0x77), 0.02, 5, true))
        .ToList();
    var options = new TraceRecognitionOptions(
        ExcludedIds: new HashSet<uint> { 0x123 },
        ExcludedJ1939SourceAddresses: new HashSet<byte> { 0x77 });

    var result = TraceRestbusRecognizer.Recognize(frames, options);
    result.Candidates.Select(c => c.Id).Should().BeEquivalentTo([0x456u]);
}
```

- [x] **Step 2: Run tests**

Run: `dotnet test tests/PeakCan.Host.Core.Tests --filter "FullyQualifiedName~TraceRestbusRecognizerTests"`
Expected: compilation fails because recognizer types are missing.

- [x] **Step 3: Implement**

- Ignore error frames.
- Group standard frames by `(Channel, Id, false)` and extended frames by `(Channel, Id, true)`.
- Use `J1939Id` only for `IsExtended == true`; expose source, destination, priority, and PGN, but key periodicity by raw ID.
- Compute deltas in milliseconds, median, median absolute deviation, and `IntervalCv = MAD / median`.
- Classify as periodic only when delta count >= `MinFrames`, median rounded to nearest integer is >= 10, and CV <= `MaxIntervalCv`.
- Set `IntervalMs` to `Math.Max(10, (int)Math.Round(median))`.
- Preserve FD from the first frame in the group as IsFd.
- Use the last frame payload as RepresentativePayload in this task.

- [x] **Step 4: Run tests**

Run: `dotnet test tests/PeakCan.Host.Core.Tests --filter "FullyQualifiedName~TraceRestbusRecognizerTests"`
Expected: all recognizer tests pass.

- [x] **Step 5: Run broader Host.Core tests**

Run: `dotnet test tests/PeakCan.Host.Core.Tests`
Expected: all tests pass except already documented hardware/environment skips.

- [x] **Step 6: Commit**

```bash
git add src/PeakCan.Host.Core/HIL/Environment tests/PeakCan.Host.Core.Tests/HIL/Environment
git commit -m "feat(host): recognize periodic trace frame candidates"
```

---

### Task 4: DBC-aware Restbus node builder

**Files:**
- Create: `src/PeakCan.Host.Core/HIL/Environment/TraceRestbusNodeBuilder.cs`
- Test: `tests/PeakCan.Host.Core.Tests/HIL/Environment/TraceRestbusNodeBuilderTests.cs`

**Interfaces:**
- Consumes: `TraceRestbusRecognizer`, `DbcRestbusGenerator.CreateDbcNodeMessage`, `SignalDecoder`, `DbcParser`.
- Produces:

```csharp
public sealed record TraceNodeBuildRequest(
    string Name,
    string? Channel,
    NodeIdentity Identity,
    IReadOnlyList<TraceFrameCandidate> Messages,
    DbcDocument? Dbc,
    bool UseDbcWhenAvailable = true);

public sealed record TraceNodeBuildResult(
    RestbusNode? Node,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public static class TraceRestbusNodeBuilder
{
    public static TraceNodeBuildResult Build(
        TraceNodeBuildRequest request,
        GeneratorOptions? dbcOptions = null);
}
```

- [x] **Step 1: Write failing tests**

```csharp
[Fact]
public void Builds_Fixed_Hex_Messages_When_Dbc_Is_Missing()
{
    var message = new TraceFrameCandidate(
        1, 0x123, false, 5, 20, 0, true, false, null, null, null, null, [0x01, 0x02]);
    var request = new TraceNodeBuildRequest(
        "Trace-0x123", "CAN_A", new RawCanNodeIdentity(), [message], null);

    var result = TraceRestbusNodeBuilder.Build(request);

    result.Node.Should().NotBeNull();
    var nodeMessage = result.Node!.Messages.Single();
    nodeMessage.Ref.Should().BeEquivalentTo(new CanMessageRef(0x123, false));
    nodeMessage.Payload.Should().BeEquivalentTo(new FixedHexSource("0102"));
    nodeMessage.IntervalMs.Should().Be(20);
}

[Fact]
public void Builds_Dbc_Message_With_Captured_Signal_Overrides()
{
    const string dbcText = """
        VERSION ""
        NS_ :
        BS_:
        BU_: Charger
        BO_ 291 ChargerMsg: 2 Charger
         SG_ Voltage : 0|16@1+ (0.1,0) [0|0] "V" Vector,XXX
        """;
    var dbc = DbcParser.Parse(dbcText).Value!;
    var message = new TraceFrameCandidate(
        1, 0x123, false, 5, 20, 0, true, false, null, null, null, null, [0x64, 0x00]);
    var request = new TraceNodeBuildRequest(
        "Charger", "CAN_A", new RawCanNodeIdentity(), [message], dbc);

    var result = TraceRestbusNodeBuilder.Build(request);

    result.Errors.Should().BeEmpty();
    result.Node!.Messages.Single().Payload.Should().BeEquivalentTo(new DbcSignalsSource("ChargerMsg"));
    result.Node!.SignalOverrides!.Should().ContainKey("ChargerMsg.Voltage");
    result.Node!.SignalOverrides!["ChargerMsg.Voltage"].Should().Be(10.0);
}

[Fact]
public void Copies_Requested_Channel_Verbatim()
{
    var message = new TraceFrameCandidate(
        1, 0x123, false, 5, 20, 0, true, false, null, null, null, null, [0x01]);
    var request = new TraceNodeBuildRequest(
        "Trace", "MISSING", new RawCanNodeIdentity(), [message], null);

    var result = TraceRestbusNodeBuilder.Build(request);
    result.Node!.Channel.Should().Be("MISSING");
}
```

- [x] **Step 2: Run tests**

Run: `dotnet test tests/PeakCan.Host.Core.Tests --filter "FullyQualifiedName~TraceRestbusNodeBuilderTests"`
Expected: compilation fails because builder is missing.

- [x] **Step 3: Implement**

- Build one `NodeMessage` per candidate.
- For each DBC message matched by `(candidate.Id, candidate.IsExtended)`:
  - Decode every DBC signal from `RepresentativePayload` with `SignalDecoder.Decode`.
  - Create `DbcSignalsSource(message.Name)` through `DbcRestbusGenerator.CreateDbcNodeMessage`.
  - Add `SignalOverrides["<MessageName>.<SignalName>"]` for every successfully decoded signal; collect a warning for failed signals.
- For unmatched messages, use `FixedHexSource(Convert.ToHexString(payload))`.
- Preserve FD on the built `NodeMessage`; if `candidate.IsFd` is true, return a copy with `Fd = true`.
- Return null `Node` when zero messages can be built; list every blocking error.

- [x] **Step 4: Run tests**

Run: `dotnet test tests/PeakCan.Host.Core.Tests --filter "FullyQualifiedName~TraceRestbusNodeBuilderTests"`
Expected: all builder tests pass.

- [x] **Step 5: Commit**

```bash
git add src/PeakCan.Host.Core/HIL/Environment tests/PeakCan.Host.Core.Tests/HIL/Environment
git commit -m "feat(host): build restbus nodes from trace candidates"
```

---

### Task 5: Atomic suite environment writer

**Files:**
- Create: `src/PeakCan.Host.App/Services/HIL/SuiteEnvironmentWriter.cs`
- Test: `tests/PeakCan.Host.App.Tests/Services/SuiteEnvironmentWriterTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record SuiteEnvironmentWriteResult(bool Success, string? Error, TestSuite? Suite);

public sealed class SuiteEnvironmentWriter
{
    public SuiteEnvironmentWriteResult AppendNodes(
        string suitePath,
        IReadOnlyList<RestbusNode> incomingNodes,
        IReadOnlyList<ChannelConfig>? knownChannels = null);
}
```

- [x] **Step 1: Write failing tests**

Use temporary `.json` files with a minimal suite created through `TestSuite` serialization.

```csharp
[Fact]
public void Appends_Nodes_And_Preserves_Existing_Suite_Data()
{
    var path = CreateSuiteFile(existingEnvironment: []);
    var incoming = new[] { FixedNode("Trace-0x123") };

    var result = new SuiteEnvironmentWriter().AppendNodes(path, incoming);

    result.Success.Should().BeTrue();
    result.Suite!.Environment.Should().HaveCount(1);
    Reload(path).Environment!.Single().Name.Should().Be("Trace-0x123");
}

[Fact]
public void Rejects_Duplicate_Node_Name()
{
    var path = CreateSuiteFile(existingEnvironment: [FixedNode("Duplicate")]);
    var result = new SuiteEnvironmentWriter().AppendNodes(path, [FixedNode("Duplicate")]);

    result.Success.Should().BeFalse();
    result.Error.Should().Contain("Duplicate");
}

[Fact]
public void Rejects_Send_Id_Conflict_In_Same_Channel()
{
    var path = CreateSuiteFile(existingEnvironment: [FixedNode("Existing", id: 0x123)]);
    var result = new SuiteEnvironmentWriter().AppendNodes(path, [FixedNode("Incoming", id: 0x123)]);

    result.Success.Should().BeFalse();
    result.Error.Should().Contain("0x123");
}

[Fact]
public void Rejects_Node_Channel_Missing_From_Multichannel_Suite()
{
    var path = CreateSuiteFile(channels: [new ChannelConfig("A", "51", null, false)]);
    var result = new SuiteEnvironmentWriter().AppendNodes(
        path, [FixedNode("Trace", channel: "MISSING")],
        knownChannels: [new ChannelConfig("A", "51", null, false)]);

    result.Success.Should().BeFalse();
    result.Error.Should().Contain("MISSING");
}
```

- [x] **Step 2: Run tests**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "FullyQualifiedName~SuiteEnvironmentWriterTests"`
Expected: compilation fails because writer is missing.

- [x] **Step 3: Implement**

- Load with `JsonSerializer.Deserialize<TestSuite>(json, HILJsonOptions.Default)`.
- Build `Environment = existing + incoming` and run `RestbusNodeValidator.Validate`.
- Add explicit send-key conflict checks:
  - Raw CAN key: `(Channel ?? "", "can", Id, IsExtended)`.
  - J1939 key: `(Channel ?? "", "j1939", Priority, Pgn, Sa, Da)`.
- Do not mutate the original file until validation succeeds.
- Write to `suitePath + ".tmp"`, then `File.Move(temp, suitePath, overwrite: true)`.
- On any failure, delete the temp file if it exists and return a specific error without changing the suite.

- [x] **Step 4: Run tests**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "FullyQualifiedName~SuiteEnvironmentWriterTests"`
Expected: all writer tests pass.

- [x] **Step 5: Commit**

```bash
git add src/PeakCan.Host.App/Services/HIL tests/PeakCan.Host.App.Tests/Services
git commit -m "feat(host): append validated trace environment nodes to suite"
```

---

### Task 6: Recording panel preview and confirmation UI

**Files:**
- Create: `src/PeakCan.Host.App/ViewModels/HIL/TraceToEnvironmentViewModel.cs`
- Create: `src/PeakCan.Host.App/Views/HIL/TraceToEnvironmentWindow.xaml`
- Create: `src/PeakCan.Host.App/Views/HIL/TraceToEnvironmentWindow.xaml.cs`
- Modify: `src/PeakCan.Host.App/ViewModels/RecordViewModel.cs`
- Modify: `src/PeakCan.Host.App/Views/RecordView.xaml`
- Modify: `src/PeakCan.Host.App/AppShellViewModel.cs`
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/TraceToEnvironmentViewModelTests.cs`

**Interfaces:**
- Consumes: `TraceRestbusRecognizer.Recognize`, `TraceRestbusNodeBuilder.Build`, `SuiteEnvironmentWriter.AppendNodes`, `IFileDialogService`, `HilViewModel.SuitePath`.
- Produces:

```csharp
public sealed partial class TraceToEnvironmentViewModel : ObservableObject
{
    public string TracePath { get; set; }
    public string SuitePath { get; set; }
    public ObservableCollection<TraceCandidateRowViewModel> Candidates { get; }
    public string Status { get; }
    public IReadOnlyList<string> BlockingErrors { get; }

    [RelayCommand] private Task LoadAsync();
    [RelayCommand] private void RefreshNodes();
    [RelayCommand] private void BrowseTrace();
    [RelayCommand] private void BrowseSuite();
    [RelayCommand] private Task WriteSuiteAsync();
}

public sealed partial class TraceCandidateRowViewModel : ObservableObject
{
    public bool Include { get; set; }
    public string NodeName { get; set; }
    public string Channel { get; set; }
    public string Identity { get; }
    public string Message { get; }
    public int FrameCount { get; }
    public int IntervalMs { get; }
    public double IntervalCv { get; }
    public string PayloadMode { get; }
    public IReadOnlyList<string> Warnings { get; }
    public TraceFrameCandidate Candidate { get; }
}
```

- [x] **Step 1: Write failing VM tests**

```csharp
[Fact]
public async Task Load_Analyzes_Trace_And_Creates_One_Row_Per_Identity()
{
    var trace = CreatePeriodicTrace(id: 0x123, intervalMs: 20, channel: 1);
    var vm = new TraceToEnvironmentViewModel(
        new StubFileDialog { TracePath = trace, SuitePath = SuitePath },
        new SuiteEnvironmentWriter()) { SuitePath = SuitePath };

    vm.TracePath = trace;
    await vm.LoadAsync();

    vm.Candidates.Should().HaveCount(1);
    vm.Candidates[0].Include.Should().BeTrue();
    vm.Candidates[0].IntervalMs.Should().Be(20);
    vm.BlockingErrors.Should().BeEmpty();
}

[Fact]
public async Task WriteSuite_Appends_Only_Selected_Candidates()
{
    var vm = await CreateLoadedViewModelWithTwoCandidatesAsync();
    vm.Candidates[1].Include = false;

    await vm.WriteSuiteAsync();

    var suite = ReloadSuite(vm.SuitePath);
    suite.Environment.Should().ContainSingle(n => n.Name == vm.Candidates[0].NodeName);
    vm.Status.Should().Contain("写入成功");
}

[Fact]
public async Task WriteSuite_Does_Not_Write_When_Node_Channel_Is_Invalid()
{
    var vm = await CreateLoadedViewModelWithTwoCandidatesAsync(multichannelSuite: true);
    vm.Candidates[0].Channel = "MISSING";

    await vm.WriteSuiteAsync();

    vm.BlockingErrors.Should().Contain(e => e.Contains("MISSING"));
    File.ReadAllText(vm.SuitePath).Should().Be(OriginalSuiteJson);
}
```

- [x] **Step 2: Run tests**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "FullyQualifiedName~TraceToEnvironmentViewModelTests"`
Expected: compilation fails because VM is missing.

- [x] **Step 3: Implement VM**

- Parse `.asc` with `AscParser.ParseAsync` and `.blf` with `BlfParser.ParseAsync`; show a specific error for unsupported extensions.
- Build row identity: extended IDs become `J1939 SA 0x11` and group all messages from that source address; standard IDs are `CAN ID 0x123`.
- Default node names use the DBC sender name when all messages match one sender; otherwise `Trace-SA-0x11` / `Trace-ID-0x123`.
- Load suite channels and DBC paths from the suite JSON; cache parsed DBC by channel.
- Map the numeric trace `Channel` to a suite channel only when `ushort.TryParse(ChannelConfig.Handle, HexNumber)` equals it. If no handle matches, leave `Channel` blank in a multi-channel suite; leave it null in a single-channel suite. The user must resolve a blank channel before writing.
- `RefreshNodes` builds one `RestbusNode` per included row group using `TraceRestbusNodeBuilder`.
- `WriteSuiteAsync` calls `SuiteEnvironmentWriter.AppendNodes`; on success set `Status` with suite path and node count; on failure set `BlockingErrors` and do not close the dialog.

- [x] **Step 4: Add Recording panel entry**

In `RecordView.xaml`, after the stop button:

```xml
<Button Content="转为环境..." Command="{Binding OpenTraceToEnvironmentCommand}" Width="92" Margin="12,0,0,0" />
```

In `RecordViewModel`, add:
- `public event EventHandler<string>? TraceToEnvironmentRequested;`
- `[RelayCommand(CanExecute = nameof(CanOpenTraceToEnvironment))] private void OpenTraceToEnvironment() => TraceToEnvironmentRequested?.Invoke(this, OutputPath);`
- `CanOpenTraceToEnvironment()` returns false while recording and requires a nonempty `OutputPath`.
- Raise `OpenTraceToEnvironmentCommand.NotifyCanExecuteChanged()` after Start/Stop and `PollNow`.

In `AppShellViewModel`, subscribe and open the dialog; use the existing dialog service pattern where practical. The handler sets `TracePath` from the recording output and `SuitePath` from `Hil.SuitePath`.

- [x] **Step 5: Add preview window XAML**

The window must include:
- target trace `TextBox + 浏览`;
- target suite `TextBox + 浏览`;
- `分析trace` and `写入 suite` buttons;
- `DataGrid` with columns: `Include`, `NodeName`, `Channel`, `Identity`, `Message`, `FrameCount`, `IntervalMs`, `IntervalCv`, `PayloadMode`;
- warnings/errors `ItemsControl` below the grid;
- fixed `Status` bar at the bottom.

Use `AutoGenerateColumns="False"`, `CanUserAddRows="False"`, and local read-only column bindings. Declare any converters locally; do not rely on app-level converter resources after the M4.2 XAML lesson.

- [x] **Step 6: Run tests**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "FullyQualifiedName~TraceToEnvironmentViewModelTests"`
Expected: all new VM tests pass.

- [x] **Step 7: Build Host App**

Run: `dotnet build src/PeakCan.Host.App -c Release`
Expected: build succeeds. Close running Host before Debug builds.

- [x] **Step 8: Commit**

```bash
git add src/PeakCan.Host.App tests/PeakCan.Host.App.Tests
git commit -m "feat(host): add trace-to-environment preview and suite writer"
```

---

### Task 7: End-to-end fixture, formal review, and docs

**Files:**
- Test: `tests/PeakCan.Host.App.Tests/ViewModels/TraceToEnvironmentEndToEndTests.cs`
- Modify: `docs/superpowers/plans/2026-09-04-restbus-m5-trace-to-environment.md`
- Modify: `D:\claude_proj2\.sdd\restbus-unification\ledger.md`

**Interfaces:**
- Consumes all Task 1–6 interfaces unchanged.

- [x] **Step 1: Add ASC end-to-end test**

Create a temporary trace with:
- `0x123`, channel 1, every 20 ms, stable payload;
- `0x456`, channel 1, irregular timestamps;
- `0x18FF0055`, channel 1, every 50 ms, extended/J1939 source `0x55`.

Assert:
- recognizer returns the 20 ms and 50 ms groups as periodic and the irregular group as non-periodic;
- VM selects only periodic rows by default;
- builder creates `Trace-ID-0x123` with fixed payload and `Trace-SA-0x55` with J1939 identity;
- writer appends both nodes to a two-channel suite and preserves original case/channel fields;
- an existing-ID conflict produces a blocking error and leaves the suite file byte-for-byte unchanged.

- [x] **Step 2: Run focused E2E test**

Run: `dotnet test tests/PeakCan.Host.App.Tests --filter "FullyQualifiedName~TraceToEnvironmentEndToEndTests"`
Expected: pass.

- [x] **Step 3: Run full verification**

Run:

```bash
dotnet test tests/PeakCan.HIL.Core.Tests
dotnet test tests/PeakCan.Host.Core.Tests
dotnet test tests/PeakCan.Host.App.Tests
dotnet build src/PeakCan.Host.App -c Release
```

Expected: hil-core and Host.Core all pass; Host App tests pass except the pre-existing Copilot/AppData environment failures already documented in M4.2; Release build passes.

- [x] **Step 4: Superpowers formal review**

Review all changed files and branches against this plan. At minimum verify:
- no DUT frames are included unless the user selected them;
- no suite file is touched before explicit command and successful validation;
- generated nodes cannot introduce send-ID conflicts;
- DBC-backed and fixed-payload nodes are visually distinguishable;
- XAML resources used by the new window are locally declared;
- no Studio or hardware dependency leaks into Host.Core.

- [x] **Step 5: Fix review findings**

Implement Critical/Important fixes before handoff. Re-run failing focused tests plus Task 7 full verification.

- [x] **Step 6: Update plan and ledger**

Mark every completed task checkbox. Add M5 rulings, commits, test counts, remaining risks, and merge order:
1. hil-core M5 branch PR;
2. host M5 branch PR;
3. Studio has no required M5.1 code change unless review finds suite compatibility issues.

- [x] **Step 7: Commit docs**

```bash
git add docs/superpowers/plans/2026-09-04-restbus-m5-trace-to-environment.md
git commit -m "docs: mark restbus m5 trace-to-environment complete"
```

Then update the sibling ledger.

## Acceptance Checklist

- Recording panel has a discoverable `转为环境...` action after capture.
- Preview shows every frame group, period, stability, payload mode, and warnings before import.
- User can exclude the tested device by unchecking a raw ID or one J1939 source address.
- Import writes only selected, validated nodes to the chosen suite and reports success/failure explicitly.
- A saved suite can be reopened in Studio and edited through the existing Environment tab.
- No environment rules/UDS behavior are silently invented from trace data.




## M5 Formal Review & Closure (2026-09-04)

- E2E coverage added by commit `c9c4f62`; it verifies periodic/irregular recognition, J1939 node identity, suite write, multi-channel grouping, and conflict rejection without changing the suite.
- Important fix 1: preview grouping now includes trace `Channel`, preventing the same raw ID or J1939 source on two physical channels from being merged into one node.
- Important fix 2: preview `PayloadMode` now reports `DBC signals`, `fixed hex`, or `DBC + fixed hex` based on actual DBC message matches, satisfying the visual distinction requirement.
- Full verification (2026-09-04):
  - hil-core `298/298 PASS`.
  - Host Core `1043 PASS / 5 pre-existing AppData permission failures`.
  - Host App `1435 PASS / 13 pre-existing AppData permission failures`; no M5 failures.
  - Host App Release build `PASS` (warnings only).
- Studio has no required M5.1 code change; the existing Environment tab consumes the generated embedded suite nodes.
- Merge order: hil-core M5 PR first, then host M5 PR.

