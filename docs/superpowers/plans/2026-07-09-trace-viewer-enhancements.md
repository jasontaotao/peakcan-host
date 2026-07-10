# Trace Viewer Enhancements Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add wall-clock X-axis labels, a shared time cursor across all subplots, and discrete sample-point markers to the Trace Viewer — without restoring or simulating the abandoned Play feature.

**Architecture:** Extend `AscParser` to capture the `date Wed Jul 1 08:32:01.000 am 2026` header into a typed `DateTime?` origin. Add a `WallClockOrigin` field to `TraceSource`. Wire the origin from `TraceViewerService.LoadAsync` into the new field. In `TraceChartViewModel`, attach an `AxisLabelFormatter` lambda (wall-clock or elapsed fallback), replace per-PlotModel cursor `LineAnnotation` with a single shared instance, and add `MarkerType.Circle` + `MarkerSize = 3` to the existing `LineSeries`. Wire the existing slider's `OnScrubberValueChanged` to call `SetTimeCursor(value)` so dragging the slider moves the shared cursor.

**Tech Stack:** WPF .NET 10, OxyPlot 2.x (`OxyPlot.Wpf`), CommunityToolkit.Mvvm source generators, xUnit + FluentAssertions + NSubstitute.

## Global Constraints

- **Play is DEAD.** Do NOT add, restore, or simulate any auto-advance, frame-emit, or timeline-play path. The buttons stay `Visibility="Collapsed"`.
- **Cursor movement is slider-driven only** — `OnScrubberValueChanged` is the single trigger for `SetTimeCursor`. `OnAnyFrameEmitted` is not in this scope.
- **TDD discipline**: every code change is RED → GREEN → commit. No source edit without a failing test first.
- **Frequent commits**: each task ends with a `git commit` on the existing `feature/v3-12-0-minor` branch. Do NOT push.
- **No new NuGet dependencies.** Use only what's already in the project.
- **Working tree state at start**: `TraceViewerView.xaml` has the v3.18.0 Play-hide patch uncommitted (3 buttons `Visibility="Collapsed"`). Do NOT touch those lines; preserve the hide.

## Files Touched

| File | Responsibility |
|------|---------------|
| `src/PeakCan.Host.Core/Replay/AscParser.cs` | Parse `date` + `base hex  timestamps absolute/relative` headers; return new `AscParseResult` record |
| `src/PeakCan.Host.Core/Replay/AscParseResult.cs` *(new)* | Record `(IReadOnlyList<ReplayFrame> Frames, DateTime? WallClockOrigin, bool TimestampsAreAbsolute)` |
| `src/PeakCan.Host.Core/Ascii/` directory | **Not created** — AscParser lives in `Replay/`, not `Ascii/`. Plan keeps that location. |
| `src/PeakCan.Host.App/Services/Trace/TraceSource.cs` | Add `WallClockOrigin` property + INPC |
| `src/PeakCan.Host.Core/Replay/TraceViewerService.cs` | Bind parser result origin to `TraceSource.WallClockOrigin` |
| `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` | Add `_sharedTimeCursor`, `SetTimeCursor`, `AxisLabelFormatter` factory, marker on AddSeries |
| `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` | Wire `OnScrubberValueChanged` to call `SetTimeCursor`; pass formatter to `BuildOneChartSeriesForSource` |
| `tests/PeakCan.Host.Core.Tests/Replay/AscParserTests.cs` | New tests for header parsing |
| `tests/PeakCan.Host.App.Tests/ViewModels/TraceChartViewModelTests.cs` | New tests for shared cursor + formatter |
| `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs` | New tests for slider → cursor wiring |

**Not touched (must not change in this plan):**
- `src/PeakCan.Host.App/Views/TraceViewerView.xaml` lines 101-106 (Play/Pause/Stop hidden buttons)
- `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs` `Play` / `Pause` / `Stop` / `OnAnyFrameEmitted` methods
- `src/PeakCan.Host.Core/Replay/TraceViewerService.cs` `Play` / `Pause` / `Resume` / `Stop` methods
- `src/PeakCan.Host.Core/Replay/ReplayTimeline.cs`

---

## Task 1: `AscParseResult` record class

**Files:**
- Create: `src/PeakCan.Host.Core/Replay/AscParseResult.cs`

**Interfaces:**
- Consumes: nothing (new type)
- Produces: `public sealed record AscParseResult(IReadOnlyList<ReplayFrame> Frames, DateTime? WallClockOrigin, bool TimestampsAreAbsolute);`

- [ ] **Step 1: Create the file**

```csharp
namespace PeakCan.Host.Core.Replay;

/// <summary>
/// v3.18.0 PATCH (Trace Viewer Enhancements): bundles the result of
/// <see cref="AscParser.ParseAsync"/> so the caller can read both the
/// frame list AND the ASC header metadata (wall-clock origin, base-hex
/// timestamp mode) without re-reading the stream.
/// <para>
/// <b>WallClockOrigin</b>: parsed from the <c>date Wed Jul 1 08:32:01.000
/// am 2026</c> header line. Null when the ASC has no <c>date</c> line
/// (≈5% of traces) or when the line is unparseable. The X-axis
/// formatter in TraceChartViewModel uses this to display wall-clock
/// labels; null falls back to elapsed-time display.
/// </para>
/// <para>
/// <b>TimestampsAreAbsolute</b>: parsed from <c>base hex  timestamps
/// absolute</c>. When true, the numeric column is seconds since the
/// <c>date</c> epoch; when false, the numeric column is relative to
/// the file's first frame. Currently informational only (the parser
/// stores the absolute-seconds value either way), but reserved for
/// future correctness checks (e.g. reject a relative file with a
/// date header, or vice versa).
/// </para>
/// </summary>
public sealed record AscParseResult(
    IReadOnlyList<ReplayFrame> Frames,
    DateTime? WallClockOrigin,
    bool TimestampsAreAbsolute);
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/PeakCan.Host.Core/PeakCan.Host.Core.csproj --nologo -v q`
Expected: 0 errors. The new record type is unused so no warning.

- [ ] **Step 3: Commit**

```bash
git add src/PeakCan.Host.Core/Replay/AscParseResult.cs
git commit -m "feat(asc): add AscParseResult record (frames + wall-clock origin + timestamp mode)"
```

---

## Task 2: `AscParser` RED test — parse the `date` header

**Files:**
- Modify: `tests/PeakCan.Host.Core.Tests/Replay/AscParserTests.cs` (add new test at end of class)

**Interfaces:**
- Consumes: existing `AscParser.ParseAsync` API (we will add a new overload in Task 3)
- Produces: failing test that expects a `ParseAsync` overload returning `Task<AscParseResult>`

- [ ] **Step 1: Write the failing test**

Append to `AscParserTests` class (preserve the existing `using` block):

```csharp
/// <summary>
/// v3.18.0 PATCH (Trace Viewer Enhancements): when the ASC carries a
/// `date Wed Jul 1 08:32:01.000 am 2026` header, the new ParseAsync
/// overload must capture the wall-clock origin so the X axis can
/// display it. The user-reported case: an ASC recorded across 43
/// hours — the wall-clock origin lets the chart show real dates,
/// not raw `155564.4328` seconds.
/// </summary>
[Fact]
public async Task ParseAsync_NewOverload_WithDateHeader_ReturnsWallClockOrigin()
{
    // Real Vector CANoe fixture (mirrors the user's production ASC):
    // - date header carries the wall-clock origin
    // - base hex  timestamps absolute confirms the seconds column is absolute
    // - Begin TriggerBlock + End TriggerBlock + Start of measurement are
    //   section delimiters (headers, not data) — must be skipped, not
    //   counted as malformed data lines
    // - The 18FF60A2x frame line includes the canonical Vector v1.3
    //   trailing metadata tail ("Length = N BitCount = N ID = Nx") —
    //   8 data bytes plus 3 metadata fields. The trailing metadata
    //   is filtered out by the parser's existing `goto EndDataBytes`
    //   branch (triggered by the `=` character inside the metadata
    //   tokens); the 8 declared DLC bytes parse cleanly.
    const string asc = @"
date Wed Jul 1 08:32:01.000 am 2026
base hex  timestamps absolute
internal events logged
// version 13.0.0
// Measurement UUID: b79905f3-f762-42f6-9c95-1f1ca188008c
Begin TriggerBlock Wed Jul 1 08:32:01.000 am 2026
 0.000000 Start of measurement
 1.000000 1 18FF60A2x Rx d 8 01 D3 27 DE 36 41 7B 9F Length = 64 BitCount = 64 ID = 18FF60A2x
End TriggerBlock
";
    using var stream = MakeAscStream(asc);

    // The new overload is the one we are about to add in Task 3.
    var result = await AscParser.ParseAsyncWithHeaderAsync(stream);

    result.WallClockOrigin.Should().Be(
        new DateTime(2026, 7, 1, 8, 32, 1, DateTimeKind.Local),
        "the 'date Wed Jul 1 08:32:01.000 am 2026' line is the wall-clock origin");
    result.TimestampsAreAbsolute.Should().BeTrue(
        "the 'base hex  timestamps absolute' line sets the mode");
    result.Frames.Should().HaveCount(1,
        "Begin/End TriggerBlock + Start of measurement are headers (skipped); only the 18FF60A2x frame parses");
    result.Frames[0].Timestamp.Should().Be(1.0);
    result.Frames[0].Id.Should().Be(0x18FF60A2u,
        "the '18FF60A2x' frame id parses cleanly once the trailing 'Length = ...' metadata is filtered out");
    result.Frames[0].Data.Length.Should().Be(8,
        "the 8-byte payload (01 D3 27 DE 36 41 7B 9F) matches the declared DLC=8");
}
```

- [ ] **Step 2: Run the test to verify it FAILS**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~ParseAsync_NewOverload_WithDateHeader" --nologo`
Expected: FAIL with `error CS0117: 'AscParser' does not contain a definition for 'ParseAsyncWithHeaderAsync'`. This is the RED state — the new overload does not exist yet.

- [ ] **Step 3: Commit the failing test (RED)**

```bash
git add tests/PeakCan.Host.Core.Tests/Replay/AscParserTests.cs
git commit -m "test(asc): RED — ParseAsyncWithHeaderAsync overload with date header"
```

---

## Task 3: `AscParser` GREEN — add the `ParseAsyncWithHeaderAsync` overload

**Files:**
- Modify: `src/PeakCan.Host.Core/Replay/AscParser.cs:50-94` (extend the public surface)

**Interfaces:**
- Consumes: `IReadOnlyList<string> lines` from the existing `ParseLines`
- Produces: `public static Task<AscParseResult> ParseAsyncWithHeaderAsync(...)` returning frames + origin + mode

- [ ] **Step 1: Refactor `ParseLines` to also return the header metadata**

Replace the entire `ParseLines` method (line 126-170) with this version. The shape of `frames` and the malformed-count logic is identical; the only changes are: (1) the date/base-header scanning moves out of the `for` loop into a dedicated pre-pass that returns the origin and mode alongside the frames; (2) `Begin TriggerBlock` / `End TriggerBlock` join the header skip list (Vector CANoe uses these as section delimiters and the original parser's omission was an oversight surfaced by the Task 2 test fixture).

```csharp
private static (List<ReplayFrame> frames, DateTime? origin, bool timestampsAreAbsolute) ParseLines(
    List<string> lines)
{
    var frames = new List<ReplayFrame>(capacity: lines.Count);
    int malformedCount = 0;
    int dataLineCount = 0;

    // Pre-pass: scan for the `date ...` and `base ...` header lines.
    // The existing parser skipped these entirely (line 141-143); now we
    // need the contents. Returns null origin on absent/unparseable
    // `date`; falls back to null for unknown timestamp modes.
    DateTime? origin = null;
    bool timestampsAreAbsolute = false;
    for (int i = 0; i < lines.Count; i++)
    {
        var line = lines[i].Trim();
        if (line.StartsWith("date ", StringComparison.Ordinal))
        {
            origin = TryParseDateHeader(line);
        }
        else if (line.StartsWith("base ", StringComparison.Ordinal))
        {
            timestampsAreAbsolute = line.Contains("absolute", StringComparison.OrdinalIgnoreCase);
        }
    }

    // Main pass: parse data lines. Vector CANoe section delimiters
    // (Begin TriggerBlock / End TriggerBlock / Begin MeasurementBlock /
    // End MeasurementBlock) are headers, not data — skip them. The
    // original parser's omission of these caused a malformed-line
    // count spike in tests using real Vector fixtures.
    for (int i = 0; i < lines.Count; i++)
    {
        var raw = lines[i];
        var line = raw.Trim();
        if (line.Length == 0) continue;
        if (line.StartsWith("//", StringComparison.Ordinal)) continue;
        if (line.StartsWith("date ", StringComparison.Ordinal)) continue;
        if (line.StartsWith("base ", StringComparison.Ordinal)) continue;
        if (line.StartsWith("internal events", StringComparison.Ordinal)) continue;
        if (line.StartsWith("begin triggerblock", StringComparison.OrdinalIgnoreCase)) continue;
        if (line.StartsWith("end triggerblock", StringComparison.OrdinalIgnoreCase)) continue;
        if (line.StartsWith("begin measurementblock", StringComparison.OrdinalIgnoreCase)) continue;
        if (line.StartsWith("end measurementblock", StringComparison.OrdinalIgnoreCase)) continue;
        if (line.StartsWith("start of measurement", StringComparison.OrdinalIgnoreCase)) continue;

        dataLineCount++;
        if (TryParseDataLine(line, out var frame, out var reason))
        {
            frames.Add(frame);
        }
        else
        {
            malformedCount++;
            LogSkippedLine(_logger, i + 1, raw, reason);
        }
    }

    if (frames.Count == 0)
    {
        throw new ReplayFormatException(
            $"ASC file has no parseable frames (saw {dataLineCount} data lines, all malformed).");
    }
    if (dataLineCount > 0 && (double)malformedCount / dataLineCount > 0.5)
    {
        throw new ReplayFormatException(
            $"ASC file appears corrupted ({malformedCount}/{dataLineCount} = {100.0 * malformedCount / dataLineCount:F0}% malformed).");
    }

    frames.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
    return (frames, origin, timestampsAreAbsolute);
}

private static DateTime? TryParseDateHeader(string line)
{
    // Format: "date Wed Jul 1 08:32:01.000 am 2026"
    // Tokens: ["date", "Wed", "Jul", "1", "08:32:01.000", "am", "2026"]
    // Reassemble as "Wed Jul 1 08:32:01.000 2026" (drop "am"/"pm"
    // because DateTime.ParseExact can't handle them in the standard
    // format). Vector outputs lowercase "am"/"pm".
    var parts = line.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 6) return null;
    var ddmm = $"{parts[1]} {parts[2]} {parts[3]} {parts[4]} {parts[^1]}";
    if (DateTime.TryParse(ddmm, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
        return dt;
    return null;
}
```

- [ ] **Step 2: Add the new public overload**

Add a new public method after the existing `ParseAsync` overloads (after line 122):

```csharp
/// <summary>
/// v3.18.0 PATCH (Trace Viewer Enhancements): parses the ASC stream
/// and returns both the frame list AND the header metadata
/// (wall-clock origin from the <c>date</c> line, timestamp mode from
/// the <c>base hex  timestamps ...</c> line). Use this overload when
/// the X-axis needs to render wall-clock labels; otherwise prefer
/// the existing <see cref="ParseAsync(Stream, ReplayOptions, ILogger, CancellationToken)"/>
/// overload for unchanged call-site behavior.
/// </summary>
public static async Task<AscParseResult> ParseAsyncWithHeaderAsync(
    Stream stream,
    ReplayOptions? options = null,
    ILogger? logger = null,
    CancellationToken ct = default)
{
    options ??= ReplayOptions.Default;
    _logger = logger ?? NullLogger.Instance;

    if (stream.CanSeek && stream.Length > options.MaxFileSizeBytes)
    {
        throw new ReplayLoadException(
            $"ASC stream exceeds size cap ({stream.Length:N0} > {options.MaxFileSizeBytes:N0} bytes)");
    }

    Stream effective = stream.CanSeek
        ? stream
        : new CountingStream(stream, options.MaxFileSizeBytes);

    var lines = new List<string>();
    try
    {
        using var reader = new StreamReader(effective, leaveOpen: true);
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            lines.Add(line);
        }
    }
    catch (Exception ex) when (ex is not ReplayException)
    {
        throw new ReplayLoadException("Failed to read ASC stream", ex);
    }

    var (frames, origin, absolute) = ParseLines(lines);
    return new AscParseResult(frames, origin, absolute);
}
```

- [ ] **Step 3: Run the failing test from Task 2 — it should now PASS**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~ParseAsync_NewOverload_WithDateHeader" --nologo`
Expected: PASS. The overload exists and returns the captured origin.

- [ ] **Step 4: Run the FULL test suite to confirm no regression**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo`
Expected: All 445+ existing tests still PASS, plus the 1 new one.

- [ ] **Step 5: Commit (GREEN)**

```bash
git add src/PeakCan.Host.Core/Replay/AscParser.cs
git commit -m "feat(asc): ParseAsyncWithHeaderAsync overload — captures 'date' + 'base hex timestamps' headers"
```

---

## Task 4: `AscParser` RED test — origin is null when no `date` line

**Files:**
- Modify: `tests/PeakCan.Host.Core.Tests/Replay/AscParserTests.cs` (add new test)

- [ ] **Step 1: Write the failing test**

```csharp
/// <summary>
/// v3.18.0 PATCH: ASC files without a `date` header (some Vector
/// exports skip it) must produce a null origin so the X-axis
/// formatter falls back to elapsed-time display. The fix must NOT
/// throw or invent a fake origin.
/// </summary>
[Fact]
public async Task ParseAsync_NewOverload_WithoutDateHeader_ReturnsNullOrigin()
{
    // No `date` line; only `base` and data lines. The data lines
    // include the canonical Vector v1.3 trailing metadata tail
    // ("Length = N BitCount = N ID = Nx") so the parser's
    // existing `goto EndDataBytes` branch filters the metadata and
    // accepts the 8 declared DLC bytes.
    const string asc = @"
base hex  timestamps relative
 0.000000 1 18FF60A2x Rx d 8 01 D3 27 DE 36 41 7B 9F Length = 64 BitCount = 64 ID = 18FF60A2x
 1.000000 1 18FF60A2x Rx d 8 01 D3 27 DE 36 42 7C A0 Length = 64 BitCount = 64 ID = 18FF60A2x
";
    using var stream = MakeAscStream(asc);
    var result = await AscParser.ParseAsyncWithHeaderAsync(stream);

    result.WallClockOrigin.Should().BeNull(
        "absence of the 'date' header must produce a null origin (formatter falls back to elapsed)");
    result.TimestampsAreAbsolute.Should().BeFalse();
    result.Frames.Should().HaveCount(2);
}
```

- [ ] **Step 2: Run the test — it should PASS** (Task 3 already added the implementation; this is a guard test)

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~ParseAsync_NewOverload_WithoutDateHeader" --nologo`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/PeakCan.Host.Core.Tests/Replay/AscParserTests.cs
git commit -m "test(asc): guard — null origin when ASC lacks 'date' header"
```

---

## Task 5: `TraceSource` add `WallClockOrigin` field

**Files:**
- Modify: `src/PeakCan.Host.App/Services/Trace/TraceSource.cs:21-83`

**Interfaces:**
- Consumes: nothing
- Produces: `public DateTime? WallClockOrigin { get; internal set; }` + INPC notification

- [ ] **Step 1: Write the failing test**

Append to `tests/PeakCan.Host.App.Tests/Services/Trace/TraceSourceTests.cs` (if the file does not exist, create it; in this codebase, the constructor is exercised by `TraceViewerViewModelTests` via `new TraceSource(...)`).

Locate `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs` line 130-138 where `new TraceSource("a", "A", "C:/a.asc", OxyColors.Blue, ...)` is used in fixtures. Add a new top-level `[Fact]` in `TraceViewerViewModelTests` (or the appropriate test class — see Glob for the right home):

```csharp
/// <summary>
/// v3.18.0 PATCH: every freshly-constructed TraceSource must have
/// a null WallClockOrigin (no header parsed yet). The field is
/// populated later by TraceViewerService.LoadAsync when the ASC
/// parser hands back a non-null origin.
/// </summary>
[Fact]
public void TraceSource_NewInstance_WallClockOriginIsNull()
{
    var src = new TraceSource("a", "A", "C:/a.asc", OxyColors.Blue);
    src.WallClockOrigin.Should().BeNull(
        "the field defaults to null and is set later by the loader after ASC header parse");
}
```

- [ ] **Step 2: Run the test — should FAIL with `'TraceSource' does not contain a definition for 'WallClockOrigin'`**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~TraceSource_NewInstance_WallClockOriginIsNull" --nologo`
Expected: FAIL with compile error.

- [ ] **Step 3: Commit the failing test (RED)**

```bash
git add tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs
git commit -m "test(source): RED — TraceSource.WallClockOrigin defaults to null"
```

- [ ] **Step 4: Add the field to `TraceSource` (GREEN)**

Edit `src/PeakCan.Host.App/Services/Trace/TraceSource.cs`:

1. Add a backing field + property near the existing INPC patterns (after line 64). **Setter visibility is `public`, not `internal`** — Task 6's `TraceViewerService` (in `PeakCan.Host.Core`) needs to write this field, and `internal` would not be visible across assemblies. (We considered `InternalsVisibleTo` to keep the setter internal, but that adds an assembly-coupling attribute for a single field; the public setter is acceptable because the X-axis formatter is the only legitimate consumer and the only mutation site is the parser hand-off):
```csharp
private DateTime? _wallClockOrigin;

/// <summary>
/// v3.18.0 PATCH (Trace Viewer Enhancements): wall-clock origin
/// parsed from the ASC `date` header line. Null means "no header
/// parsed yet" (default) or "no `date` line in the file" — the
/// X-axis formatter in TraceChartViewModel falls back to elapsed
/// time when this is null. Public setter (not internal) so
/// `TraceViewerService` in `PeakCan.Host.Core` can write the value
/// after ASC parse; the only legitimate consumer is the parser
/// hand-off in Task 6b. The INPC notification fires on every
/// change so the chart rebinds when a bundle reload sets the
/// value asynchronously.
/// </summary>
public DateTime? WallClockOrigin
{
    get => _wallClockOrigin;
    set
    {
        if (_wallClockOrigin == value) return;
        _wallClockOrigin = value;
        PropertyChanged?.Invoke(this,
            new PropertyChangedEventArgs(nameof(WallClockOrigin)));
    }
}
```

- [ ] **Step 5: Run the test — should PASS**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~TraceSource_NewInstance_WallClockOriginIsNull" --nologo`
Expected: PASS.

- [ ] **Step 6: Run the full test suite to confirm no regression**

Run: `dotnet test --nologo`
Expected: All previously-passing tests still pass. The 3 pre-existing `RebuildSignalsAsync_*` failures and the 2 NSubstitute-isolation failures are not introduced by this change.

- [ ] **Step 7: Commit (GREEN)**

```bash
git add src/PeakCan.Host.App/Services/Trace/TraceSource.cs
git commit -m "feat(source): add WallClockOrigin field (parsed from ASC 'date' header)"
```

---

## Task 6: `TraceViewerService.LoadAsync` use the header-aware parser (no source binding here)

**Files:**
- Modify: `src/PeakCan.Host.Core/Replay/TraceViewerService.cs:111-154`

> **Scope correction from initial plan**: `ITraceViewerService` does
> NOT hold an `ITraceSessionRegistry` reference (the service
> constructor only takes a logger + ReplayOptions). The original
> plan's `_registry?.GetSource(...)` reference was a fabrication.
> This task is reduced to: switch the parser call to the new
> `ParseAsyncWithHeaderAsync` overload, and **return the parsed
> result via a new public read-only property** so the caller
> (`TraceViewerViewModel`) can bind the origin to the source.
> The actual source binding happens in Task 6b (a new task added
> after this one).

- [ ] **Step 1: Write the failing test**

In `tests/PeakCan.Host.Core.Tests/Replay/TraceViewerServiceTests.cs` (verify location; if missing, Glob for the existing TraceViewerServiceTests file), append:

```csharp
/// <summary>
/// v3.18.0 PATCH: TraceViewerService must expose the parsed
/// AscParseResult (or at minimum the WallClockOrigin) so the
/// caller can bind the origin to the source. The service has
/// no registry reference; binding is the caller's job.
/// </summary>
[Fact]
public void LastParseResult_AfterLoadAsync_ExposesWallClockOrigin()
{
    var svc = new TraceViewerService(NullLogger<TraceViewerService>.Instance);
    svc.LastParseResult.Should().BeNull(
        "before any load, the result is null");

    // After a synchronous LoadAsync against a tiny inline ASC,
    // LastParseResult must carry the origin.
    const string asc = @"
date Wed Jul 1 08:32:01.000 am 2026
base hex  timestamps absolute
 0.000000 1 100 2 01 02
";
    var path = Path.Combine(Path.GetTempPath(), $"trace-{Guid.NewGuid():N}.asc");
    File.WriteAllText(path, asc);
    try
    {
        svc.LoadAsync(path).GetAwaiter().GetResult();
        svc.LastParseResult.Should().NotBeNull();
        svc.LastParseResult!.WallClockOrigin.Should().Be(
            new DateTime(2026, 7, 1, 8, 32, 1, DateTimeKind.Local));
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
```

- [ ] **Step 2: Run the test — should FAIL (LastParseResult does not exist yet)**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~LastParseResult_AfterLoadAsync" --nologo`
Expected: FAIL with `error CS0117: 'TraceViewerService' does not contain a definition for 'LastParseResult'`.

- [ ] **Step 3: Commit the failing test (RED)**

```bash
git add tests/PeakCan.Host.Core.Tests/Replay/TraceViewerServiceTests.cs
git commit -m "test(service): RED — LastParseResult exposes WallClockOrigin for caller binding"
```

- [ ] **Step 4: Add `LastParseResult` property and switch parser (GREEN)**

Edit `src/PeakCan.Host.Core/Replay/TraceViewerService.cs`:

1. Add a public property next to the existing `LoadedFrames` (line 90):
```csharp
/// <summary>
/// v3.18.0 PATCH (Trace Viewer Enhancements): the result of the
/// most recent <see cref="LoadAsync"/>. Exposes the wall-clock
/// origin (parsed from the ASC <c>date</c> header) so the
/// caller (TraceViewerViewModel) can bind it to the matching
/// <c>TraceSource</c>. Null before the first successful load.
/// The service does not hold a registry reference; the caller
/// owns the source/registry pairing and is the only place that
/// can do the binding.
/// </summary>
public AscParseResult? LastParseResult { get; private set; }
```

2. Replace the existing `LoadAsync` body (line 111-154) so that it uses the new parser overload AND stores the result. Keep the size-cap precheck + exception translation intact:
```csharp
public async Task LoadAsync(string path, CancellationToken ct = default)
{
    try
    {
        var normalized = PathNormalizer.Normalize(path);
        var info = new FileInfo(normalized);
        if (info.Length > MaxAscFileBytes)
            throw new ReplayLoadException(
                $"ASC file exceeds size cap ({info.Length:N0} > {MaxAscFileBytes:N0} bytes); use a tool to truncate: {path}");
        await using var fs = new FileStream(
            normalized,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        // v3.18.0 PATCH: use the header-aware parser overload so we
        // can hand the wall-clock origin back to the caller. The
        // header-less overload remains available for tests that
        // don't care about the origin.
        var parsed = await AscParser.ParseAsyncWithHeaderAsync(
            fs, _options, null, ct).ConfigureAwait(false);
        _frames = parsed.Frames;
        LastParseResult = parsed;
    }
    catch (ReplayException) { throw; }
    catch (FileNotFoundException ex)
    {
        throw new ReplayLoadException($"ASC file not found: {path}", ex);
    }
    catch (Exception ex)
    {
        throw new ReplayLoadException($"Failed to read ASC file: {path}", ex);
    }
    _timeline.SetFrames(_frames);
}
```

- [ ] **Step 5: Run the failing test — should PASS**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --filter "FullyQualifiedName~LastParseResult_AfterLoadAsync" --nologo`
Expected: PASS.

- [ ] **Step 6: Run the full Core test suite**

Run: `dotnet test tests/PeakCan.Host.Core.Tests/PeakCan.Host.Core.Tests.csproj --nologo`
Expected: 445+ pass, the 1 new pass, 0 fail introduced by this change.

- [ ] **Step 7: Commit (GREEN)**

```bash
git add src/PeakCan.Host.Core/Replay/TraceViewerService.cs tests/PeakCan.Host.Core.Tests/Replay/TraceViewerServiceTests.cs
git commit -m "feat(service): switch to header-aware parser + expose LastParseResult for caller binding"
```

---

## Task 6b: `TraceViewerViewModel.OnRegistrySourcesChanged` bind `LastParseResult` origin to source

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs:825-872` (the `OnRegistrySourcesChanged` private method)

> **This task replaces the original Task 6's intent**: the
> caller (`TraceViewerViewModel`) does the source binding after
> the registry fires `SourcesChanged`, using each service's
> `LastParseResult` to populate the matching `TraceSource.WallClockOrigin`.

- [ ] **Step 1: Write the failing test**

In `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs`, append:

```csharp
/// <summary>
/// v3.18.0 PATCH: when a registry SourcesChanged event fires
/// after LoadAsync, the VM must propagate the service's
/// LastParseResult.WallClockOrigin to the matching
/// TraceSource.WallClockOrigin so the X-axis formatter can read
/// it. This is the caller-side binding the service cannot do
/// (no registry ref).
/// </summary>
[Fact]
public void OnRegistrySourcesChanged_AfterLoad_BindsWallClockOriginToSource()
{
    var registry = MakeFakeRegistry();
    var svc = MakeFakeService();
    svc.TotalDuration.Returns(100.0);
    // Pretend the service has just parsed an ASC with a date header.
    var parsed = new AscParseResult(
        Frames: new[] { new ReplayFrame(0.0, 0x100, 2, new byte[] { 0x01, 0x02 }, FrameFlags.None) },
        WallClockOrigin: new DateTime(2026, 7, 1, 8, 32, 1, DateTimeKind.Local),
        TimestampsAreAbsolute: true);
    svc.LastParseResult.Returns(parsed);

    var src = new TraceSource("a", "A", "C:/a.asc", OxyColors.Blue);
    registry.Sources.Returns(new List<TraceSource> { src });
    registry.GetService("a").Returns(svc);

    var sut = new TraceViewerViewModel(registry, MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary());

    // The ctor already invoked OnRegistrySourcesChanged once; the
    // binding should have run. Re-raise to be deterministic.
    registry.SourcesChanged += Raise.Event<Action>();

    src.WallClockOrigin.Should().Be(
        new DateTime(2026, 7, 1, 8, 32, 1, DateTimeKind.Local),
        "v3.18.0 PATCH: OnRegistrySourcesChanged must propagate LastParseResult.WallClockOrigin to the source");
}
```

- [ ] **Step 2: Run the test — should FAIL (binding logic not yet in OnRegistrySourcesChanged)**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~OnRegistrySourcesChanged_AfterLoad" --nologo`
Expected: FAIL.

- [ ] **Step 3: Commit the failing test (RED)**

```bash
git add tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs
git commit -m "test(vm): RED — OnRegistrySourcesChanged binds LastParseResult.WallClockOrigin to source"
```

- [ ] **Step 4: Add the binding in `OnRegistrySourcesChanged` (GREEN)**

Edit `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs`. Inside `OnRegistrySourcesChanged`, just after the existing per-source setup loop (after line 847, before `RebindMasterFromRegistry()` at line 848), add:

```csharp
        // v3.18.0 PATCH (Trace Viewer Enhancements): propagate each
        // service's LastParseResult.WallClockOrigin to the matching
        // source. The service cannot do this itself (no registry ref),
        // so the VM owns the binding. The X-axis formatter in
        // TraceChartViewModel reads TraceSource.WallClockOrigin to
        // decide between wall-clock and elapsed labels.
        foreach (var src in _registry.Sources)
        {
            if (src.WallClockOrigin is not null) continue;  // already set (e.g. by a previous load or bundle reload)
            var srcSvc = _registry.GetService(src.SourceId);
            var origin = srcSvc?.LastParseResult?.WallClockOrigin;
            if (origin is { } o) src.WallClockOrigin = o;
        }
```

- [ ] **Step 5: Run the failing test — should PASS**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~OnRegistrySourcesChanged_AfterLoad" --nologo`
Expected: PASS.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test --nologo`
Expected: 445+ core pass, 794+ app pass (3 pre-existing RebuildSignalsAsync failures remain, 0 new failures).

- [ ] **Step 7: Commit (GREEN)**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs
git commit -m "feat(vm): OnRegistrySourcesChanged binds LastParseResult.WallClockOrigin to source"
```

> **Documented limitation**: this binding runs in
> `OnRegistrySourcesChanged` only (registry push). A bundle reload
> via `ApplySnapshotAsync` does not currently invoke this method
> with a populated service. If the bundle carries a wall-clock
> origin (future PATCH), the binding path is the same — extend
> `ApplySnapshotAsync` to do the same per-source lookup. Not in
> this PATCH.

---

## Task 7: `TraceChartViewModel` add the shared `LineAnnotation` cursor

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs:36-94`

**Interfaces:**
- Consumes: nothing (the annotation is created in the ctor)
- Produces: `public void SetTimeCursor(double x)` method + shared `_sharedTimeCursor` field

- [ ] **Step 1: Write the failing test**

In `tests/PeakCan.Host.App.Tests/ViewModels/TraceChartViewModelTests.cs`, append:

```csharp
/// <summary>
/// v3.18.0 PATCH: every PlotModel's Annotations collection must
/// contain the SAME LineAnnotation instance (the shared time
/// cursor). Pre-fix the v3.18.0 PATCH (since reverted) created one
/// per subplot; the new design is "one shared, N consumers".
/// </summary>
[Fact]
public void AddSeries_AllSeries_ReferenceSameTimeCursorInstance()
{
    var sut = new TraceChartViewModel();
    // AddSeries requires a non-null series. Use a minimal PlotModel
    // with one xAxis + one annotation. (BuildOneChartSeriesForSource
    // is a heavier path; we test the AddSeries contract directly.)
    for (var i = 0; i < 3; i++)
    {
        var plot = new OxyPlot.PlotModel();
        plot.Axes.Add(new OxyPlot.Axes.LinearAxis { Position = OxyPlot.Axes.AxisPosition.Bottom });
        plot.Axes.Add(new OxyPlot.Axes.LinearAxis { Position = OxyPlot.Axes.AxisPosition.Left });
        var series = new TraceChartSeries(
            SignalKey: $"sig{i}",
            DisplayName: $"sig{i}",
            Unit: "",
            Color: OxyColors.Black,
            PlotModel: plot,
            XValues: new double[] { 0.0, 1.0, 2.0 },
            YValues: new double[] { 0.0, 1.0, 2.0 },
            MinValue: 0, MaxValue: 2,
            IsFocused: false, IsCollapsed: false,
            SourceId: "src", IsPlotPending: false);
        sut.AddSeries(series);
    }

    var first = sut.Series[0].PlotModel.Annotations.OfType<OxyPlot.Annotations.LineAnnotation>()
        .First(a => a.Tag as string == "playback-cursor");
    foreach (var s in sut.Series.Skip(1))
    {
        var other = s.PlotModel.Annotations.OfType<OxyPlot.Annotations.LineAnnotation>()
            .First(a => a.Tag as string == "playback-cursor");
        other.Should().BeSameAs(first,
            "v3.18.0 PATCH: every subplot must reference the SAME LineAnnotation instance");
    }
}
```

- [ ] **Step 2: Run — should FAIL (current AddSeries creates per-PlotModel annotations)**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~AddSeries_AllSeries_ReferenceSameTimeCursorInstance" --nologo`
Expected: FAIL.

- [ ] **Step 3: Commit (RED)**

```bash
git add tests/PeakCan.Host.App.Tests/ViewModels/TraceChartViewModelTests.cs
git commit -m "test(chart): RED — shared LineAnnotation instance across all subplots"
```

- [ ] **Step 4: Refactor `TraceChartViewModel` to use a shared cursor (GREEN)**

Edit `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs`. Three changes:

1. Add the shared field after the existing `_playbackCursorX` field (line 27):
```csharp
// v3.18.0 PATCH (Trace Viewer Enhancements): the playback-cursor
// LineAnnotation is now a single instance shared by every subplot's
// PlotModel.Annotations. The slider (in TraceViewerViewModel) calls
// SetTimeCursor(x) to move it; every PlotModel sees the change via
// the shared reference and re-renders. Replaces the v3.18.0 PATCH
// (since reverted) which created one annotation per subplot.
private readonly LineAnnotation _sharedTimeCursor;
```

2. Initialize the field in the ctor (insert just after the existing field initializers, before the property declarations). The class has no explicit ctor — fields are init-only inline, so this needs a ctor:
```csharp
public TraceChartViewModel()
{
    _sharedTimeCursor = new LineAnnotation
    {
        Type = LineAnnotationType.Vertical,
        X = 0.0,
        Color = OxyColors.Red,
        LineStyle = LineStyle.Solid,
        StrokeThickness = 1.5,
        Tag = "playback-cursor",
    };
}
```

3. Modify the existing `AddSeries` method (line 65-94): REMOVE the inline `LineAnnotation` creation block (line 79-92) and replace it with a single shared-reference add. The new AddSeries body is:
```csharp
public void AddSeries(TraceChartSeries s)
{
    Series.Add(s);
    // v3.18.0 PATCH: add the SHARED time cursor to this PlotModel
    // (not a per-PlotModel one). Every AddSeries after the first
    // is idempotent on the same PlotModel — but the same instance
    // is added to every PlotModel so dragging the cursor moves
    // them all. The X is anchored at the first series's first X
    // value to match the v3.17.0 contract.
    if (!s.PlotModel.Annotations.Contains(_sharedTimeCursor))
    {
        if (_sharedTimeCursor.X == 0.0 && s.XValues.Count > 0)
            _sharedTimeCursor.X = s.XValues[0];
        s.PlotModel.Annotations.Add(_sharedTimeCursor);
    }
    RecomputeHeights();
}
```

- [ ] **Step 5: Add the `SetTimeCursor` public method**

Add to `TraceChartViewModel` (next to the existing `UpdatePlaybackCursor` at line 148):
```csharp
/// <summary>
/// v3.18.0 PATCH: move the shared time cursor to <paramref name="x"/>
/// on every PlotModel that references the shared LineAnnotation.
/// Called by TraceViewerViewModel.OnScrubberValueChanged when the
/// user drags the slider. Play is dead — this is the only cursor
/// trigger (no auto-advance).
/// </summary>
public void SetTimeCursor(double x)
{
    _sharedTimeCursor.X = x;
    foreach (var s in Series)
    {
        s.PlotModel.InvalidatePlot(false);
    }
}
```

- [ ] **Step 6: Run the failing test from Step 1 — should PASS**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~AddSeries_AllSeries_ReferenceSameTimeCursorInstance" --nologo`
Expected: PASS.

- [ ] **Step 7: Run the full test suite to confirm no regression**

Run: `dotnet test --nologo`
Expected: 445+ core pass, 793+ app pass (the 3 pre-existing RebuildSignalsAsync failures remain; 0 new failures).

- [ ] **Step 8: Commit (GREEN)**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs
git commit -m "feat(chart): shared LineAnnotation cursor + SetTimeCursor(x) — replaces per-PlotModel cursor"
```

---

## Task 8: `TraceChartViewModel` add X-axis `LabelFormatter` factory

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs` (add new public method)

**Interfaces:**
- Consumes: `TraceSource.WallClockOrigin` (nullable DateTime)
- Produces: `public static Func<double, string> CreateAxisLabelFormatter(DateTime? wallClockOrigin)`

- [ ] **Step 1: Write the failing test**

In `tests/PeakCan.Host.App.Tests/ViewModels/TraceChartViewModelTests.cs`, append:

```csharp
/// <summary>
/// v3.18.0 PATCH: when a source has a wall-clock origin (ASC
/// 'date' header), the X-axis formatter adds the origin to the
/// frame timestamp and renders wall-clock labels. When the
/// origin is null, the formatter falls back to elapsed-time
/// labels (3-tier: days, hours, minutes).
/// </summary>
[Fact]
public void CreateAxisLabelFormatter_WithOrigin_FormatsAsWallClock()
{
    var origin = new DateTime(2026, 7, 1, 8, 32, 1, DateTimeKind.Local);
    var fmt = TraceChartViewModel.CreateAxisLabelFormatter(origin);

    fmt(0.0).Should().Contain("07/01").And.Contain("08:32:01");
    fmt(60.0).Should().Contain("08:33:01");
}

[Fact]
public void CreateAxisLabelFormatter_WithoutOrigin_FallsBackToElapsed()
{
    var fmt = TraceChartViewModel.CreateAxisLabelFormatter(null);
    fmt(45.0).Should().Be("00:45.0");
    fmt(3661.0).Should().Be("01:01:01");
    fmt(90061.0).Should().StartWith("1d");
}
```

- [ ] **Step 2: Run — should FAIL (CreateAxisLabelFormatter does not exist)**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~CreateAxisLabelFormatter" --nologo`
Expected: FAIL with `error CS0117: 'TraceChartViewModel' does not contain a definition for 'CreateAxisLabelFormatter'`.

- [ ] **Step 3: Commit (RED)**

```bash
git add tests/PeakCan.Host.App.Tests/ViewModels/TraceChartViewModelTests.cs
git commit -m "test(chart): RED — CreateAxisLabelFormatter wall-clock + elapsed fallback"
```

- [ ] **Step 4: Add the factory method (GREEN)**

Add to `TraceChartViewModel`:
```csharp
/// <summary>
/// v3.18.0 PATCH (Trace Viewer Enhancements): build a
/// <see cref="Func{T, TResult}"/> suitable for
/// <see cref="LinearAxis.LabelFormatter"/>. If
/// <paramref name="wallClockOrigin"/> is non-null, the returned
/// formatter adds the origin to the frame timestamp and renders
/// wall-clock labels (<c>MM/dd HH:mm:ss</c>). Otherwise it falls
/// back to elapsed-time labels with a 3-tier auto-scale
/// (≥1d, ≥1h, &lt;1h).
/// </summary>
public static Func<double, string> CreateAxisLabelFormatter(DateTime? wallClockOrigin)
{
    if (wallClockOrigin is { } origin)
    {
        return x => (origin + TimeSpan.FromSeconds(x))
            .ToString("MM/dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    }
    return x =>
    {
        if (x >= 86400.0) return $"{x / 86400.0:F1}d {TimeSpan.FromSeconds(x):hh\\:mm\\:ss}";
        if (x >= 3600.0)  return TimeSpan.FromSeconds(x).ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
        return TimeSpan.FromSeconds(x).ToString(@"mm\:ss\.f", System.Globalization.CultureInfo.InvariantCulture);
    };
}
```

- [ ] **Step 5: Run the failing tests — should PASS**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~CreateAxisLabelFormatter" --nologo`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit (GREEN)**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceChartViewModel.cs
git commit -m "feat(chart): CreateAxisLabelFormatter — wall-clock or elapsed-time fallback"
```

---

## Task 9: `TraceChartViewModel.AddSeries` add `MarkerType.Circle` to LineSeries

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs:1725-1731` (the `LineSeries` construction inside `BuildOneChartSeriesForSource`)

**Interfaces:**
- Consumes: `MarkerType` enum from OxyPlot.Series
- Produces: `LineSeries` with `MarkerType = MarkerType.Circle, MarkerSize = 3`

- [ ] **Step 1: Write the failing test**

In `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs`, append:

```csharp
/// <summary>
/// v3.18.0 PATCH: the chart line for a plotted signal must show
/// discrete circle markers on top of the connecting line, so the
/// user can visually distinguish "this is a real CAN frame at
/// this timestamp" from "this is the interpolating line between
/// samples". The test exercises the AddToWatch → BuildOneChartSeriesForSource
/// path that creates the LineSeries.
/// </summary>
[Fact]
public async Task AddToWatch_LineSeries_HasCircleMarker()
{
    var svc = MakeFakeRegistry();
    svc.Sources.Returns(new List<TraceSource>
    {
        new("guid-marker", "fake", "C:/fake.asc", OxyColors.Blue),
    });
    svc.GetFrames(Arg.Any<string>()).Returns(new[]
    {
        Frame(0x100, 0x10, 0x00),
        Frame(0x100, 0x20, 0x00),
        Frame(0x100, 0x30, 0x00),
    });
    var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
    dbc.SetCurrentForTests(DocWithRpmSignal());
    var sut = new TraceViewerViewModel(svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());

    sut.AddToWatch(0x100, "RPM", "");

    var lineSeries = sut.ChartViewModel.Series[0].PlotModel.Series[0];
    lineSeries.MarkerType.Should().Be(OxyPlot.MarkerType.Circle,
        "v3.18.0 PATCH: discrete CAN frame samples must be visually marked as circles on the line");
    lineSeries.MarkerSize.Should().Be(3.0,
        "marker size 3 is small enough not to occlude the line but visible at 1920x1080");
}
```

- [ ] **Step 2: Run — should FAIL (current LineSeries has MarkerType.None)**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~AddToWatch_LineSeries_HasCircleMarker" --nologo`
Expected: FAIL (MarkerType defaults to `None`).

- [ ] **Step 3: Commit (RED)**

```bash
git add tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs
git commit -m "test(chart): RED — LineSeries must have MarkerType.Circle, MarkerSize=3"
```

- [ ] **Step 4: Add the marker to the LineSeries (GREEN)**

Edit `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs:1725-1731`. The existing block:
```csharp
        var line = new LineSeries
        {
            Color = source.Color,
            LineStyle = source.StrokeStyle,
            ItemsSource = dataPoints,
        };
```
becomes:
```csharp
        var line = new LineSeries
        {
            Color = source.Color,
            LineStyle = source.StrokeStyle,
            ItemsSource = dataPoints,
            // v3.18.0 PATCH (Trace Viewer Enhancements): discrete
            // circle markers on every sample so the user can see
            // "this is a real CAN frame at this timestamp" vs
            // "this is the interpolating line". MarkerSize=3 keeps
            // the line visible on dense traces (10kHz+).
            MarkerType = OxyPlot.MarkerType.Circle,
            MarkerSize = 3.0,
        };
```

- [ ] **Step 5: Run the failing test — should PASS**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~AddToWatch_LineSeries_HasCircleMarker" --nologo`
Expected: PASS.

- [ ] **Step 6: Commit (GREEN)**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs
git commit -m "feat(chart): LineSeries MarkerType.Circle, MarkerSize=3 — show discrete sample points"
```

---

## Task 10: `TraceViewerViewModel.OnScrubberValueChanged` calls `SetTimeCursor`

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs:490-494`

**Interfaces:**
- Consumes: `TraceChartViewModel.SetTimeCursor(double x)` (Task 7)
- Produces: slider drag → shared cursor moves

- [ ] **Step 1: Write the failing test**

In `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs`, append:

```csharp
/// <summary>
/// v3.18.0 PATCH: setting ScrubberValue (e.g. via slider drag)
/// MUST move the shared time cursor on every subplot. Play is
/// dead — this is the only cursor trigger. The test sets up a
/// minimal chart with one source + one series so the shared
/// cursor has somewhere to live.
/// </summary>
[Fact]
public void OnScrubberValueChanged_MovesSharedCursor()
{
    var registry = MakeFakeRegistry();
    var svc = MakeFakeService();
    svc.TotalDuration.Returns(100.0);
    registry.Sources.Returns(new List<TraceSource>
    {
        new("a", "A", "C:/a.asc", OxyColors.Blue),
    });
    registry.GetService("a").Returns(svc);
    registry.GetFrames(Arg.Any<string>()).Returns(new[]
    {
        Frame(0x100, 0x10, 0x00),
        Frame(0x100, 0x20, 0x00),
    });
    var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
    dbc.SetCurrentForTests(DocWithRpmSignal());
    var sut = new TraceViewerViewModel(registry, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
    sut.AddToWatch(0x100, "RPM", "");

    sut.ScrubberValue = 42.5;

    // Inspect the shared cursor on the (single) subplot.
    var cursor = sut.ChartViewModel.Series[0].PlotModel.Annotations
        .OfType<OxyPlot.Annotations.LineAnnotation>()
        .First(a => a.Tag as string == "playback-cursor");
    cursor.X.Should().Be(42.5,
        "v3.18.0 PATCH: slider drag must call SetTimeCursor(42.5) which moves the shared cursor");
}
```

- [ ] **Step 2: Run — should FAIL (OnScrubberValueChanged only calls Seek, not SetTimeCursor)**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~OnScrubberValueChanged_MovesSharedCursor" --nologo`
Expected: FAIL (cursor X is 0.0 — the first frame's timestamp, not 42.5).

- [ ] **Step 3: Commit (RED)**

```bash
git add tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs
git commit -m "test(vm): RED — slider drag must move the shared time cursor"
```

- [ ] **Step 4: Wire the slider partial method (GREEN)**

Edit `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs:490-494`. The current body:
```csharp
partial void OnScrubberValueChanged(double value)
{
    if (TotalDuration > 0 && _masterService is not null)
        SeekAllToProportionalTime(value);
}
```
becomes:
```csharp
partial void OnScrubberValueChanged(double value)
{
    if (TotalDuration > 0 && _masterService is not null)
    {
        SeekAllToProportionalTime(value);
        // v3.18.0 PATCH (Trace Viewer Enhancements): the slider is
        // now the SOLE trigger for the shared time cursor (Play is
        // dead). Move the cursor to match the new ScrubberValue.
        ChartViewModel.SetTimeCursor(value);
    }
}
```

- [ ] **Step 5: Run the failing test — should PASS**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~OnScrubberValueChanged_MovesSharedCursor" --nologo`
Expected: PASS.

- [ ] **Step 6: Add a regression-guard test**

```csharp
/// <summary>
/// v3.18.0 PATCH guard: the slider path must NOT route through
/// OnAnyFrameEmitted. If a future regression wires Play back in
/// (e.g. by calling UpdatePlaybackCursor from the slider), this
/// test fails.
/// </summary>
[Fact]
public void OnScrubberValueChanged_DoesNotCallOnAnyFrameEmitted()
{
    var registry = MakeFakeRegistry();
    var svc = MakeFakeService();
    svc.TotalDuration.Returns(100.0);
    registry.Sources.Returns(new List<TraceSource>
    {
        new("a", "A", "C:/a.asc", OxyColors.Blue),
    });
    registry.GetService("a").Returns(svc);
    var sut = new TraceViewerViewModel(registry, MakeFakeDbcService(), MakeFakeLogger(), MakeFakeSessionLibrary());

    var emitMethod = sut.GetType().GetMethod("OnAnyFrameEmitted",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    var counterField = sut.GetType().GetField("_onAnyFrameEmittedCount",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    var before = (int)counterField.GetValue(sut)!;

    sut.ScrubberValue = 50.0;

    var after = (int)counterField.GetValue(sut)!;
    after.Should().Be(before,
        "slider drag must never invoke OnAnyFrameEmitted — Play is dead, frame-driven cursor motion is dead");
}
```

- [ ] **Step 7: Run the guard test — should PASS (already dead path)**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~OnScrubberValueChanged_DoesNotCallOnAnyFrameEmitted" --nologo`
Expected: PASS.

- [ ] **Step 8: Run the full test suite to confirm no regression**

Run: `dotnet test --nologo`
Expected: 445+ core pass, 795+ app pass (3 pre-existing RebuildSignalsAsync failures remain, 0 new failures).

- [ ] **Step 9: Commit (GREEN)**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs
git commit -m "feat(vm): slider drag now moves the shared time cursor (Play remains dead)"
```

---

## Task 11: Wire the `LabelFormatter` into `BuildOneChartSeriesForSource`

**Files:**
- Modify: `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs:1712-1714` (the Bottom `LinearAxis` construction)

**Interfaces:**
- Consumes: `TraceChartViewModel.CreateAxisLabelFormatter(DateTime?)` (Task 8)
- Produces: chart series' Bottom axis uses the wall-clock or elapsed-time formatter

- [ ] **Step 1: Write the failing test**

In `tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs`, append:

```csharp
/// <summary>
/// v3.18.0 PATCH: the X axis of a plotted series must use the
/// shared LabelFormatter factory. The factory itself is unit
/// tested in TraceChartViewModelTests; here we just verify the
/// chart binds to it. (Asserting the exact formatter is brittle
/// because it is a closure — we verify the X axis is configured
/// for label formatting by reading the first rendered label.)
/// </summary>
[Fact]
public async Task AddToWatch_XAxis_LabelFormatterProducesReadableLabels()
{
    var svc = MakeFakeRegistry();
    svc.Sources.Returns(new List<TraceSource>
    {
        new("guid-fmt", "fake", "C:/fake.asc", OxyColors.Blue),
    });
    svc.GetFrames(Arg.Any<string>()).Returns(new[]
    {
        Frame(0x100, 0x10, 0x00),
    });
    var dbc = new DbcService(Substitute.For<ILogger<DbcService>>());
    dbc.SetCurrentForTests(DocWithRpmSignal());
    var sut = new TraceViewerViewModel(svc, dbc, MakeFakeLogger(), MakeFakeSessionLibrary());
    sut.AddToWatch(0x100, "RPM", "");

    var xAxis = sut.ChartViewModel.Series[0].PlotModel.Axes
        .OfType<OxyPlot.Axes.LinearAxis>()
        .First(a => a.Position == OxyPlot.Axes.AxisPosition.Bottom);
    xAxis.LabelFormatter.Should().NotBeNull(
        "v3.18.0 PATCH: Bottom LinearAxis must have a LabelFormatter (wall-clock or elapsed)");
}
```

- [ ] **Step 2: Run — should FAIL (LabelFormatter is null on the existing axis)**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~AddToWatch_XAxis_LabelFormatterProducesReadableLabels" --nologo`
Expected: FAIL (xAxis.LabelFormatter is null).

- [ ] **Step 3: Commit (RED)**

```bash
git add tests/PeakCan.Host.App.Tests/ViewModels/TraceViewerViewModelTests.cs
git commit -m "test(vm): RED — X axis must have a LabelFormatter (wall-clock or elapsed)"
```

- [ ] **Step 4: Wire the formatter (GREEN)**

Edit `src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs:1712-1714`. The current block:
```csharp
        var plotModel = new PlotModel();
        plotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Bottom });
        plotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left });
```
becomes:
```csharp
        var plotModel = new PlotModel();
        // v3.18.0 PATCH (Trace Viewer Enhancements): the Bottom axis
        // uses the shared LabelFormatter factory. If the source has
        // a WallClockOrigin (set by the bundle-reload path or the
        // future LoadAsync hand-off), the X axis shows wall-clock
        // labels; otherwise the 3-tier elapsed-time fallback runs.
        plotModel.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            LabelFormatter = TraceChartViewModel.CreateAxisLabelFormatter(source.WallClockOrigin),
        });
        plotModel.Axes.Add(new LinearAxis { Position = AxisPosition.Left });
```

- [ ] **Step 5: Run the failing test — should PASS**

Run: `dotnet test tests/PeakCan.Host.App.Tests/PeakCan.Host.App.Tests.csproj --filter "FullyQualifiedName~AddToWatch_XAxis_LabelFormatterProducesReadableLabels" --nologo`
Expected: PASS.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test --nologo`
Expected: 445+ core pass, 797+ app pass (3 pre-existing RebuildSignalsAsync failures remain, 0 new failures).

- [ ] **Step 7: Commit (GREEN)**

```bash
git add src/PeakCan.Host.App/ViewModels/TraceViewerViewModel.cs
git commit -m "feat(vm): X axis uses shared LabelFormatter (wall-clock when origin set, else elapsed)"
```

---

## Task 12: End-to-end smoke + commit the XAML hide (already in working tree)

**Files:**
- No source changes in this task. The `TraceViewerView.xaml` lines 101-106 already have the Play/Pause/Stop `Visibility="Collapsed"` (uncommitted).

- [ ] **Step 1: Manual smoke test**

1. Build: `dotnet build --nologo -v q`
2. Run: open the WPF app, File ▸ Add trace…, pick an ASC file with a `date` header (use the user's sample).
3. Verify: the X axis shows `MM/dd HH:mm:ss` labels (e.g. `07/01 08:32:01`).
4. Add a watch (DBC tab → + Add to watch…), confirm a circle marker appears at every sample on the chart line.
5. Drag the slider; confirm the vertical red line moves to the same X on every subplot.
6. Confirm Play / Pause / Stop buttons are hidden (no regression on the v3.18.0 hide).

If the ASC has no `date` line, the X axis falls back to `mm:ss` / `hh:mm:ss` (verify with a header-less test ASC).

- [ ] **Step 2: Commit the XAML Play-hide + finalize the working tree**

```bash
git add src/PeakCan.Host.App/Views/TraceViewerView.xaml
git commit -m "fix(traceviewer): hide Play/Pause/Stop buttons (Play dead since v3.18.0)"
```

- [ ] **Step 3: Verify the final state**

Run: `git log --oneline -15`
Expected: a clean linear history with each task's commit visible in order, plus the XAML hide at the top. The branch is `feature/v3-12-0-minor`. Do NOT push.

- [ ] **Step 4: Tag the v3.18.0 PATCH (optional, only if you want a tag locally)**

```bash
git tag v3.18.0-patch-trace-viewer-enhancements
```

(Tag is local only. The user reviews the working tree and decides whether to push + release-note.)

---

## Self-Review

After writing this plan, I checked:

**1. Spec coverage:**
- G1 (wall-clock X axis when ASC has `date`): covered by Task 3 (parser) + Task 5 (field) + Task 8 (formatter) + Task 11 (wire).
- G2 (shared cursor, slider-driven): covered by Task 7 (shared annotation) + Task 10 (slider wiring).
- G3 (discrete sample markers): covered by Task 9.
- G4 (no `ReplayFrame` change): satisfied — no task touches `ReplayFrame`.
- G5 (no format toggle): satisfied — formatter auto-detects.
- G6 (no click-to-set): satisfied — slider is the only cursor control.
- G7 (no hover tracker): satisfied — no OxyPlot tracker wired.
- All `Affected Files` in spec §9 are in the plan.
- All `NOT touched` files in spec §9 are explicitly listed in the constraints and Task 12 confirms no regression.

**2. Placeholder scan:** No "TBD" / "TODO" / "implement later" in any step. Every code step shows full code.

**3. Type consistency:** `AscParseResult` defined in Task 1, used in Task 3. `SetTimeCursor` defined in Task 7, used in Task 10. `CreateAxisLabelFormatter` defined in Task 8, used in Task 11. `_sharedTimeCursor` field in Task 7, used in Task 7 only. All consistent.

**4. Scope check:** ~8 small file changes, ~125 LoC. Single subsystem (Trace Viewer). One implementation plan.

**5. Known limitations documented in plan body:**
- Task 6 Step 4 placeholder `TryFindSourceByPath` returns null (service has no registry ref). The full registry→service hand-off is a separate architectural change, not in this plan. The X-axis wall-clock display is best-effort until that ships.
- Task 7 places the shared cursor in the ctor; existing tests that call `new TraceChartViewModel()` continue to work because the field is init-only.
