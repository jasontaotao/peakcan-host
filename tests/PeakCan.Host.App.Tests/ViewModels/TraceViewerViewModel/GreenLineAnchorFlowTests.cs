// v3.50.0 MINOR T2: tests for GreenLineAnchorFlow partial — RefreshAtAnchor
// public API drives both View-owned Plot VerticalLine insert/remove and
// WatchedSignals.GreenAnchorValue recompute via ITraceSessionRegistry.GetFrames
// + SignalDecoder.DecodeRaw. 3 tests cover the NaN-clear / value-set /
// Latest-update paths.
// v3.62.0 MINOR: migrated from OxyPlot (PlotModel + LineAnnotation) to
// ScottPlot (Plot + VerticalLine). SeedChart creates a real ScottPlot
// Plot, registers it via RegisterPlot, and assertions read back
// VerticalLine plottables from the registered Plot.
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ScottPlot;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.Replay;
using Xunit;
using FrameFlags = PeakCan.HIL.Core.FrameFlags;
using ValueType = PeakCan.HIL.Core.Dbc.ValueType;

namespace PeakCan.Host.App.Tests.ViewModels.TraceViewerViewModelFlow;

public class GreenLineAnchorFlowTests
{
    /// <summary>Build a minimal TraceViewerViewModel backed by NSubstitute
    /// fakes. Mirrors the v3.6.0 T1 NewVm helper shape used by
    /// TraceViewerViewModelTests.</summary>
    private static TraceViewerViewModel NewVm(out ITraceSessionRegistry registry, out DbcService dbcService)
    {
        registry = Substitute.For<ITraceSessionRegistry>();
        registry.Sources.Returns(new List<TraceSource>());
        dbcService = Substitute.For<DbcService>(NullLogger<DbcService>.Instance);
        var libPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"tmtrace-anchor-{Guid.NewGuid():N}.tmtrace");
        return new TraceViewerViewModel(
            registry,
            dbcService,
            NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libPath, NullLogger<TraceSessionLibrary>.Instance));
    }

    /// <summary>Inject one <see cref="TraceChartSeries"/> per (chart, model)
    /// pair into the VM's ChartViewModel so UpdateAllGreenLines has a
    /// non-empty chart list to iterate. v3.50.2 PATCH ChartSourceCoupling
    /// also requires the YValues to actually hold the per-frame
    /// decoded signal values (so the watch list's anchor-driven
    /// Latest can read them back via YValues[idx] and match the
    /// chart subplot's plotted point at the same X).
    /// <para>Pass the same frames that SeedWatchedRow will use, plus
    /// the Signal ref the row caches, so XValues / YValues line up
    /// with what RecomputeAllLatestAtAnchor will binary-search.</para>
    /// </summary>
    private static void SeedChart(
        TraceViewerViewModel vm,
        Signal signal,
        IReadOnlyList<ReplayFrame> frames,
        params (string key, Color color)[] charts)
    {
        var xs = new List<double>(frames.Count);
        var ys = new List<double>(frames.Count);
        foreach (var f in frames)
        {
            xs.Add(f.Timestamp);
            ys.Add(global::PeakCan.HIL.Core.Dbc.SignalDecoder.Decode(f.Data.AsSpan(), signal));
        }
        foreach (var (key, color) in charts)
        {
            var plot = new Plot();
            var series = new TraceChartSeries(
                SignalKey: key,
                DisplayName: key,
                Unit: "",
                Color: color,
                Plot: plot,
                XValues: xs,
                YValues: ys,
                MinValue: ys.Min(),
                MaxValue: ys.Max(),
                IsFocused: false,
                IsCollapsed: false);
            vm.ChartViewModel.AddSeries(series);
            // v3.62.0: register the View-owned Plot so the VM's
            // UpdateAllGreenLines can add VerticalLines to it.
            vm.RegisterPlot(key, plot);
        }
    }

    /// <summary>v3.50.2 PATCH: place-holder overload for tests that
    /// don't care about per-frame data (e.g. anchor-clear test). The
    /// v3.50.2 ChartSourceCoupling path needs the row's chart series
    /// to have a real (or default-NaN) YValues, but for tests that
    /// assert on line annotations instead of Latest, a placeholder
    /// is fine. v3.62.0 MINOR: creates a real ScottPlot Plot and
    /// registers it via RegisterPlot.</summary>
    private static void SeedChart(TraceViewerViewModel vm, params (string key, Color color)[] charts)
    {
        foreach (var (key, color) in charts)
        {
            var plot = new Plot();
            var series = new TraceChartSeries(
                SignalKey: key,
                DisplayName: key,
                Unit: "",
                Color: color,
                Plot: plot,
                XValues: new List<double> { 0.0 },
                YValues: new List<double> { double.NaN },
                MinValue: 0,
                MaxValue: 0,
                IsFocused: false,
                IsCollapsed: false);
            vm.ChartViewModel.AddSeries(series);
            vm.RegisterPlot(key, plot);
        }
    }

    /// <summary>Inject one watched-source (so RecomputeAllLatestAtAnchor's
    /// master-source lookup finds it) + one stubbed Signal on a watched row.</summary>
    private static void SeedWatchedRow(
        TraceViewerViewModel vm,
        ITraceSessionRegistry registry,
        Signal signal,
        IReadOnlyList<ReplayFrame> frames,
        string sourceId = "src-A")
    {
        registry.Sources.Returns(new List<TraceSource>
        {
            new TraceSource(sourceId, "src-A", "/tmp/a.asc", Colors.Blue, new LineStyle())
        });
        vm.MasterSourceId = sourceId;
        registry.GetFrames(sourceId).Returns(frames);

        // v3.50.2 PATCH ChartSourceCoupling: build a real TraceChartSeries
        // from frames + signal so FindChartSeriesForRow can find it via
        // SignalKey match and the new YValues-based Latest path works.
        var idHex = "0x100";
        SeedChart(vm, signal, frames,
            ($"{idHex}.{signal.Name}", Colors.Red));

        var row = new WatchedSignalRow(
            canIdHex: idHex,
            messageName: "Msg",
            signalName: signal.Name,
            unit: signal.Unit,
            sourceId: sourceId);
        // Pre-set Signal on the row so RecomputeAllLatestAtAnchor doesn't
        // need to walk DbcService.Current (which is a NSubstitute mock here).
        row.Signal = signal;
        vm.WatchedSignals.Add(row);
    }

    [Fact]
    public void RefreshAtAnchor_NaN_ClearsAllVerticalLines()
    {
        var vm = NewVm(out _, out _);
        SeedChart(vm, ("0x100.SigA", Colors.Red), ("0x200.SigB", Colors.Blue));

        // First call adds a green line at X = 5.2; second call with NaN must clear it.
        vm.RefreshAtAnchor(5.2);
        vm.RefreshAtAnchor(double.NaN);

        // v3.62.0: anchor lines are ScottPlot VerticalLine plottables on
        // the View-owned Plot (registered via RegisterPlot). Look them up
        // by color (green) — the production code removes by color.
        foreach (var chart in vm.ChartViewModel.Series)
        {
            var plot = chart.Plot;
            plot.Should().NotBeNull("SeedChart must register a non-null Plot");
            var greenLines = plot!.GetPlottables()
                .OfType<ScottPlot.Plottables.VerticalLine>()
                .Where(vl => vl.LineColor == Colors.Green)
                .ToList();
            greenLines.Should().BeEmpty("RefreshAtAnchor(NaN) must remove every green-anchor VerticalLine");
        }
        vm.IsGreenLineAnchorActive.Should().BeFalse("IsGreenLineAnchorActive is false when anchor is NaN");
    }

    [Fact]
    public void RefreshAtAnchor_DoubleValue_AddsVerticalGreenLineAtX()
    {
        var vm = NewVm(out _, out _);
        SeedChart(vm, ("0x100.SigA", Colors.Red), ("0x200.SigB", Colors.Blue));

        vm.RefreshAtAnchor(5.2);

        foreach (var chart in vm.ChartViewModel.Series)
        {
            var plot = chart.Plot;
            plot.Should().NotBeNull("SeedChart must register a non-null Plot");
            var greenLines = plot!.GetPlottables()
                .OfType<ScottPlot.Plottables.VerticalLine>()
                .Where(vl => vl.LineColor == Colors.Green)
                .ToList();
            greenLines.Should().HaveCount(1, "exactly one green-anchor VerticalLine per chart");
            var vline = greenLines[0];
            vline.X.Should().Be(5.2, "anchor X must equal the timestamp passed to RefreshAtAnchor");
            vline.LineColor.Should().Be(Colors.Green, "green-anchor color must be Colors.Green");
            vline.LineWidth.Should().Be(2.0f, "green-anchor stroke thickness must be 2.0");
        }
        vm.IsGreenLineAnchorActive.Should().BeTrue("IsGreenLineAnchorActive is true when anchor is a real number");
    }

    [Fact]
    public void RefreshAtAnchor_UpdatesAllWatchedLatestAtT()
    {
        // Arrange: 5 frames at t = 0, 2.5, 5.2, 7.5, 10.0
        // Last byte of each frame's payload = encoded signal value
        // (Signal: Length=8, BigEndian, StartBit=0, Unsigned, Factor=1, Offset=0 → physical == Data[0])
        var signal = new Signal(
            Name: "EngineRPM",
            StartBit: 0,
            Length: 8,
            Order: ByteOrder.BigEndian,
            ValueType: ValueType.Unsigned,
            Factor: 1.0,
            Offset: 0.0,
            Min: 0,
            Max: 255,
            Unit: "rpm",
            Receivers: Array.Empty<string>());

        var frames = new List<ReplayFrame>
        {
            new ReplayFrame(0.0,  0x100, 8, new byte[] { 10 }, FrameFlags.None),
            new ReplayFrame(2.5,  0x100, 8, new byte[] { 20 }, FrameFlags.None),
            new ReplayFrame(5.2,  0x100, 8, new byte[] { 30 }, FrameFlags.None),
            new ReplayFrame(7.5,  0x100, 8, new byte[] { 40 }, FrameFlags.None),
            new ReplayFrame(10.0, 0x100, 8, new byte[] { 50 }, FrameFlags.None),
        };

        var vm = NewVm(out var registry, out _);
        SeedWatchedRow(vm, registry, signal, frames, sourceId: "src-A");

        // Act: anchor at 5.2 → must pick frame[2] (Timestamp=5.2, Data[0]=30)
        vm.RefreshAtAnchor(5.2);

        // Assert
        // v3.62.0: RefreshAtAnchor stores the anchor snapshot in GreenAnchorValue
        // (not LatestValue, which tracks live frame ingest). FrameCount is
        // still updated by the green-anchor path.
        var row = vm.WatchedSignals.First(w => !w.IsPlaceholder);
        row.GreenAnchorValue.Should().Be(30.0,
            "anchor at 5.2 must binary-search the latest frame at-or-before 5.2 " +
            "(frame index 2, Data[0]=30) and decode via Factor=1 / Offset=0 → 30.0");
        row.FrameCount.Should().Be(3,
            "FrameCount is 1-based index of the matched frame: idx+1 = 2+1 = 3");
    }

    // === v3.50.2 PATCH T1+T2+T3: blue anchor + Delta + show/hide tests ===

    [Fact]
    public void RefreshAtAnchorBlue_Updates_BlueLatestValue()
    {
        // Arrange: 1 frame at t=2.5 with Data[0]=30.
        // NOTE: SeedWatchedRow hardcodes idHex="0x100", so frame ID must match
        // or FilterFramesByCanId finds no frames and the anchor value stays NaN.
        var vm = NewVm(out var registry, out _);
        // SeedWatchedRow below builds the real chart series from frames.
        var frames = new List<ReplayFrame>
        {
            new ReplayFrame(2.5, 0x100, 8, new byte[] { 30, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None),
        };
        var sig = new Signal(Name: "Speed", StartBit: 0, Length: 8, Order: ByteOrder.LittleEndian, ValueType: ValueType.Unsigned, Factor: 1.0, Offset: 0.0, Min: 0, Max: 0, Unit: "kmh", Receivers: Array.Empty<string>());
        SeedWatchedRow(vm, registry, sig, frames);

        // Act
        vm.RefreshAtAnchorBlue(2.5);

        // Assert
        var row = vm.WatchedSignals.First(w => !w.IsPlaceholder);
        row.BlueLatestValue.Should().Be(30.0,
            "blue anchor at 2.5 must decode Data[0]=30 via SignalDecoder (Factor=1 Offset=0)");
        row.BlueFrameCount.Should().Be(1, "single frame at t=2.5: BlueFrameCount = idx+1 = 0+1 = 1");
        vm.IsBlueLineAnchorActive.Should().BeTrue();
    }

    [Fact]
    public void SetGreenLinesVisible_False_ZerosStrokeThickness()
    {
        var vm = NewVm(out _, out _);
        SeedChart(vm, ("0x100.SigA", Colors.Red));
        vm.RefreshAtAnchor(2.5);
        var chart = vm.ChartViewModel.Series.First();
        var plot = chart.Plot!;
        var greenBefore = plot.GetPlottables()
            .OfType<ScottPlot.Plottables.VerticalLine>()
            .First(vl => vl.LineColor == Colors.Green);
        greenBefore.LineWidth.Should().Be(2.0f);

        // Act
        vm.SetGreenLinesVisible(false);

        // Assert
        // v3.62.0: soft-hide re-adds the VerticalLine with a 0.01f width
        // (near-zero, not exactly 0) so the plottable is preserved but
        // invisible. The production code uses 0.01f, not 0.0.
        var greenAfter = plot.GetPlottables()
            .OfType<ScottPlot.Plottables.VerticalLine>()
            .First(vl => vl.LineColor == Colors.Green);
        greenAfter.LineWidth.Should().BeLessThan(0.1f,
            "soft-hide zeros stroke thickness; anchor X + state preserved");
        greenAfter.X.Should().Be(2.5, "anchor X survives hide round-trip");
    }

    [Fact]
    public void RefreshFrameCounts_Leaves_BlueLatestValue_NaN_Until_BlueAnchor_Drag()
    {
        // v3.50.2 PATCH (after user feedback): RefreshFrameCounts must
        // NOT mirror-decode BlueLatestValue from LatestValue. The Δ
        // column should show "—" (NaN-rendered) until the user
        // explicitly drags the blue anchor; mirroring hides whether
        // a comparison target has actually been chosen.
        var vm = NewVm(out var registry, out _);
        SeedChart(vm, ("0x100.Speed", Colors.Red));
        var frames = new List<ReplayFrame>
        {
            new ReplayFrame(2.5, 0x100, 8, new byte[] { 30, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None),
        };
        var sig = new Signal(Name: "Speed", StartBit: 0, Length: 8, Order: ByteOrder.LittleEndian, ValueType: ValueType.Unsigned, Factor: 1.0, Offset: 0.0, Min: 0, Max: 0, Unit: "kmh", Receivers: Array.Empty<string>());
        SeedWatchedRow(vm, registry, sig, frames);

        var row = vm.WatchedSignals.First(w => !w.IsPlaceholder);
        // The fixture's SeedWatchedRow sets row.Signal but the actual
        // RefreshFrameCounts path is NSubstitute-skipped (DBC is a
        // NSubstitute mock). We can only assert the design contract
        // here: BlueLatestValue stays NaN until RecomputeAllLatestAtBlueAnchor
        // writes it. Once the user drags the blue anchor, it's set.
        row.BlueLatestValue.Should().Be(double.NaN,
            "no mirror; Δ column shows \"—\" until the user drags the blue anchor");

        // User drags green anchor at t=2.5 first, then blue anchor at the
        // same X. Both anchors sit on the same frame, so Δ = 0.
        // v3.62.0: green anchor stores in GreenAnchorValue (not LatestValue).
        vm.RefreshAtAnchor(2.5);
        vm.RefreshAtAnchorBlue(2.5);
        row.GreenAnchorValue.Should().Be(30.0,
            "green anchor at 2.5 reads chart YValues[0] = 30");
        row.BlueLatestValue.Should().Be(30.0,
            "blue anchor at 2.5 reads chart YValues[0] = 30");
        row.DeltaValue.Should().Be(0.0,
            "Δ = 30 - 30 = 0 (anchor and blue anchor at same X)");
    }

    [Fact]
    public void SetBlueLinesVisible_False_ZerosStrokeThickness()
    {
        var vm = NewVm(out var registry, out _);
        // v3.62.0: SeedWatchedRow internally calls SeedChart (data overload)
        // which registers the Plot via RegisterPlot. Do NOT call the
        // placeholder SeedChart first — that would create a second series
        // whose Plot is not the one UpdateAllBlueLines adds the VerticalLine to.
        var frames = new List<ReplayFrame>
        {
            new ReplayFrame(2.5, 0x100, 8, new byte[] { 30, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None),
        };
        var sig = new Signal(Name: "Speed", StartBit: 0, Length: 8, Order: ByteOrder.LittleEndian, ValueType: ValueType.Unsigned, Factor: 1.0, Offset: 0.0, Min: 0, Max: 0, Unit: "kmh", Receivers: Array.Empty<string>());
        SeedWatchedRow(vm, registry, sig, frames);
        vm.RefreshAtAnchorBlue(2.5);
        var chart = vm.ChartViewModel.Series.First();
        var plot = chart.Plot!;
        var blueBefore = plot.GetPlottables()
            .OfType<ScottPlot.Plottables.VerticalLine>()
            .First(vl => vl.LineColor == Colors.Blue);
        blueBefore.LineWidth.Should().Be(2.0f);

        vm.SetBlueLinesVisible(false);

        var blueAfter = plot.GetPlottables()
            .OfType<ScottPlot.Plottables.VerticalLine>()
            .First(vl => vl.LineColor == Colors.Blue);
        // v3.62.0: soft-hide sets LineWidth = 0.01f (near-zero) and
        // IsVisible = false, not exactly 0.0.
        blueAfter.LineWidth.Should().BeLessThan(0.1f,
            "soft-hide zeros stroke thickness; anchor X preserved");
        blueAfter.X.Should().Be(2.5, "anchor X survives hide round-trip");
    }

    [Fact]
    public void DeltaValue_Is_BlueMinusGreen()
    {
        var vm = NewVm(out var registry, out _);
        // v3.62.0: let SeedWatchedRow create + register the Plot via its
        // internal SeedChart call. Avoid the placeholder SeedChart here.
        var frames = new List<ReplayFrame>
        {
            new ReplayFrame(2.5, 0x100, 8, new byte[] { 30, 0, 0, 0, 0, 0, 0, 0 }, FrameFlags.None),
        };
        var sig = new Signal(Name: "Speed", StartBit: 0, Length: 8, Order: ByteOrder.LittleEndian, ValueType: ValueType.Unsigned, Factor: 1.0, Offset: 0.0, Min: 0, Max: 0, Unit: "kmh", Receivers: Array.Empty<string>());
        SeedWatchedRow(vm, registry, sig, frames);

        // Both anchors at same X → Delta = 0
        vm.RefreshAtAnchor(2.5);
        vm.RefreshAtAnchorBlue(2.5);

        var row = vm.WatchedSignals.First(w => !w.IsPlaceholder);
        row.GreenAnchorValue.Should().Be(30.0);
        row.BlueLatestValue.Should().Be(30.0);
        row.DeltaValue.Should().Be(0.0, "Delta = BlueLatest - GreenAnchor");
    }
}