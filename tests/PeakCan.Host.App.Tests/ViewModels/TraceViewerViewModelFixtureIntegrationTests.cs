// v3.50.2 INTEGRATION TEST: load real user fixtures + drag green anchor
// at a known timestamp + assert watch list Latest decodes correctly.
//
// User reported: Latest = last-frame values regardless of green anchor X.
// Root cause TBD; this test reproduces the production data path.

using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core.Dbc;
using PeakCan.Host.Core.Replay;
using Xunit;
using ValueType = PeakCan.Host.Core.Dbc.ValueType;
using PeakCan.Host.App.Services.AnalysisApiKey;
using NSubstitute;

namespace PeakCan.Host.App.Tests.ViewModels;

public class TraceViewerViewModelFixtureIntegrationTests
{
    private static readonly string FixtureDir = Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "Can");

    [Fact]
    public async Task GreenAnchor_DecodesRealV2BSignals_AtAnchorTime()
    {
        var ascPath = Path.Combine(FixtureDir, "Logging.asc");
        var dbcPath = Path.Combine(FixtureDir, "pure_electric_v4.6.dbc");
        if (!File.Exists(ascPath) || !File.Exists(dbcPath)) return;

        // Load DBC
        var dbcText = await File.ReadAllTextAsync(dbcPath);
        var dbcResult = DbcParser.Parse(dbcText);
        dbcResult.IsSuccess.Should().BeTrue($"DBC parse must succeed; error: {dbcResult.Error?.Message}");
        var dbc = dbcResult.Value!;
        var v2bCmd = dbc.MessagesById.Values
            .FirstOrDefault(m => m.Name == "V2B_CMD");
        v2bCmd.Should().NotBeNull("V2B_CMD must be in DBC");
        var aliveCount = v2bCmd!.Signals.FirstOrDefault(s => s.Name == "V2B_AliveCount");
        aliveCount.Should().NotBeNull();

        // Load ASC
        var registry = new TraceSessionRegistry(
            new TableauPalette(),
            NullLoggerFactory.Instance);
        var source = await registry.LoadAsync(ascPath);

        // Pick a green anchor time mid-trace (the 0-phase, before any
        // signal goes active — V2B_AliveCount should be a small
        // constant like 1 in that range).
        var frames = registry.GetFrames(source.SourceId);
        frames.Should().NotBeEmpty();
        var anchorTs = frames[100].Timestamp; // very early — well before
                                              // the 19:13:50 active phase
        var idx = BinarySearchLatestAtOrBefore(frames, anchorTs);
        var expectedAlive = SignalDecoder.Decode(
            frames[idx].Data.AsSpan(), aliveCount!);

        var libPath = Path.Combine(Path.GetTempPath(),
            $"tmtrace-int-{Guid.NewGuid():N}.tmtrace");
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        dbcService.SetCurrentForTests(dbc);

        var vm = new TraceViewerViewModel(
            registry,
            dbcService,
            NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libPath,
                NullLogger<TraceSessionLibrary>.Instance),
                apiKeyManager: new PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager(
                    Substitute.For<PeakCan.Host.Core.Analysis.ICredentialStore>(),
                    Substitute.For<Microsoft.Extensions.Logging.ILogger<PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager>>()));
        vm.MasterSourceId = source.SourceId;

        // Add a watch row for V2B_AliveCount (cross-source)
        vm.WatchedSignals.Add(new WatchedSignalRow(
            canIdHex: "0x1802F3D0",
            messageName: "V2B_CMD",
            signalName: "V2B_AliveCount",
            unit: "bit",
            sourceId: null)
        { Signal = aliveCount });

        // Drag green anchor to early trace
        vm.RefreshAtAnchor(anchorTs);

        var row = vm.WatchedSignals.First(w => !w.IsPlaceholder);
        row.LatestValue.Should().Be(expectedAlive,
            $"green anchor at t={anchorTs}s (frame idx {idx}) must decode the actual " +
            $"V2B_AliveCount value at that frame, NOT the last frame's value. " +
            $"Expected: {expectedAlive}; Actual: {row.LatestValue}");
    }

    private static int BinarySearchLatestAtOrBefore(
        IReadOnlyList<ReplayFrame> frames, double targetTs)
    {
        int lo = 0, hi = frames.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (frames[mid].Timestamp <= targetTs) { result = mid; lo = mid + 1; }
            else { hi = mid - 1; }
        }
        return result;
    }

    [Fact]
    public async Task RefreshFrameCounts_DecodesV2B_HVOnCmd_As1_FromRealTrace()
    {
        var ascPath = Path.Combine(FixtureDir, "Logging.asc");
        var dbcPath = Path.Combine(FixtureDir, "pure_electric_v4.6.dbc");
        if (!File.Exists(ascPath) || !File.Exists(dbcPath)) return;

        // Load DBC
        var dbcText = await File.ReadAllTextAsync(dbcPath);
        var dbcResult = DbcParser.Parse(dbcText);
        dbcResult.IsSuccess.Should().BeTrue();
        var dbc = dbcResult.Value!;

        // Load ASC
        var registry = new TraceSessionRegistry(
            new TableauPalette(), NullLoggerFactory.Instance);
        var source = await registry.LoadAsync(ascPath);

        // Build a minimal TraceViewerViewModel + DBC + watch row for V2B_HVOnCmd
        var libPath = Path.Combine(Path.GetTempPath(),
            $"tmtrace-int-{Guid.NewGuid():N}.tmtrace");
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        dbcService.SetCurrentForTests(dbc);

        var vm = new TraceViewerViewModel(
            registry, dbcService, NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libPath, NullLogger<TraceSessionLibrary>.Instance),
            apiKeyManager: new PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager(
                Substitute.For<PeakCan.Host.Core.Analysis.ICredentialStore>(),
                Substitute.For<Microsoft.Extensions.Logging.ILogger<PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager>>()));
        vm.MasterSourceId = source.SourceId;

        // Add V2B_HVOnCmd watch row. The TraceViewerViewModel will
        // populate row.Signal via OnWatchedSignalsCollectionChangedForSignalCache.
        vm.WatchedSignals.Add(new WatchedSignalRow(
            canIdHex: "0x1802F3D0",
            messageName: "V2B_CMD",
            signalName: "V2B_HVOnCmd",
            unit: "bit",
            sourceId: null));

        // OnRegistrySourcesChanged only runs when Sources are wired in.
        // Invoke via reflection (private event handler).
        var onRegistryChanged = typeof(TraceViewerViewModel)
            .GetMethod("OnRegistrySourcesChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        onRegistryChanged!.Invoke(vm, null);

        var row = vm.WatchedSignals.First(w => !w.IsPlaceholder);
        row.Signal.Should().NotBeNull("CollectionChanged should have populated row.Signal");
        row.Signal!.Name.Should().Be("V2B_HVOnCmd",
            "the cache lookup must find the V2B_HVOnCmd signal ref, not a sibling or wrong message");
        row.Signal.StartBit.Should().Be(12);
        row.Signal.Length.Should().Be((byte)2);
        row.Signal.Order.Should().Be(ByteOrder.LittleEndian);

        // For every V2B_CMD frame byte 1 = 0x11 → V2B_HVOnCmd = 1.
        row.LatestValue.Should().Be(1.0,
            "after OnRegistrySourcesChanged + RefreshFrameCounts fills row.LatestValue");
    }

    [Fact]
    public async Task GreenAnchorAt_MidTrace_DecodesWatchList_AsExpected()
    {
        // v3.50.2 BUGFIX: user reports Latest doesn't match the
        // value at the green-anchor timestamp. This test loads the
        // real .asc + .dbc, drags the green anchor to a known
        // mid-trace timestamp (2500s), and asserts that every
        // watch list row's LatestValue decodes the actual
        // signal value at that frame.
        //
        // The .asc fixture has 1916 V2B_CMD frames. Every frame's
        // byte 1 = 0x11 or 0x21. So V2B_HVOnCmd = 1 (early) or
        // 2 (late). Latest should never be 0 or 3.
        var ascPath = Path.Combine(FixtureDir, "Logging.asc");
        var dbcPath = Path.Combine(FixtureDir, "pure_electric_v4.6.dbc");
        if (!File.Exists(ascPath) || !File.Exists(dbcPath)) return;

        var dbcText = await File.ReadAllTextAsync(dbcPath);
        var dbcResult = DbcParser.Parse(dbcText);
        dbcResult.IsSuccess.Should().BeTrue();
        var dbc = dbcResult.Value!;

        var registry = new TraceSessionRegistry(
            new TableauPalette(), NullLoggerFactory.Instance);
        var source = await registry.LoadAsync(ascPath);

        var libPath = Path.Combine(Path.GetTempPath(),
            $"tmtrace-int-{Guid.NewGuid():N}.tmtrace");
        var dbcService = new DbcService(NullLogger<DbcService>.Instance);
        dbcService.SetCurrentForTests(dbc);

        var vm = new TraceViewerViewModel(
            registry, dbcService, NullLogger<TraceViewerViewModel>.Instance,
            new TraceSessionLibrary(libPath, NullLogger<TraceSessionLibrary>.Instance),
            apiKeyManager: new PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager(
                Substitute.For<PeakCan.Host.Core.Analysis.ICredentialStore>(),
                Substitute.For<Microsoft.Extensions.Logging.ILogger<PeakCan.Host.App.Services.AnalysisApiKey.ApiKeyManager>>()));
        vm.MasterSourceId = source.SourceId;

        // Add 7 V2B_xxx watch rows
        string[] signals = { "V2B_AliveCount", "V2B_HVOnCmd", "V2B_FittingHVOnCmd",
                            "V2B_Speed", "V2B_HeatOnCmd", "V2B_PosRlySt", "V2B_NegRlySt" };
        foreach (var s in signals)
        {
            vm.WatchedSignals.Add(new WatchedSignalRow(
                canIdHex: "0x1802F3D0",
                messageName: "V2B_CMD",
                signalName: s,
                unit: s.Contains("Speed") ? "km/h" : "bit",
                sourceId: null));
        }

        // Trigger OnRegistrySourcesChanged via reflection
        var onRegistryChanged = typeof(TraceViewerViewModel)
            .GetMethod("OnRegistrySourcesChanged",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        onRegistryChanged!.Invoke(vm, null);

        // Drag green anchor to mid-trace (2500s)
        var midFrameIdx = registry.GetFrames(source.SourceId).Count / 2;
        var midFrame = registry.GetFrames(source.SourceId)[midFrameIdx];
        vm.RefreshAtAnchor(midFrame.Timestamp);

        // Assert: every watch list row decodes a value in the
        // expected range (V2B_AliveCount 0-15, V2B_HVOnCmd 0-3,
        // V2B_FittingHVOnCmd 0-3, V2B_Speed 0-255 km/h, etc.)
        // and is NOT 0.00 for every row (which would mean
        // RefreshAtAnchor didn't actually decode the anchor frame).
        var rows = vm.WatchedSignals.Where(w => !w.IsPlaceholder).ToList();
        rows.Should().HaveCount(7);

        var aliveRow = rows.First(r => r.SignalName == "V2B_AliveCount");
        aliveRow.LatestValue.Should().BeInRange(0, 15,
            $"V2B_AliveCount must decode the actual mid-trace value, not 0. " +
            $"Anchor timestamp = {midFrame.Timestamp}s (frame idx {midFrameIdx})");

        var hvOnRow = rows.First(r => r.SignalName == "V2B_HVOnCmd");
        hvOnRow.LatestValue.Should().BeInRange(0, 3,
            $"V2B_HVOnCmd must decode the actual mid-trace value, not 3. " +
            $"Anchor timestamp = {midFrame.Timestamp}s. Actual = {hvOnRow.LatestValue}");

        var speedRow = rows.First(r => r.SignalName == "V2B_Speed");
        speedRow.LatestValue.Should().BeInRange(0, 255,
            $"V2B_Speed must decode the actual mid-trace value. " +
            $"Anchor timestamp = {midFrame.Timestamp}s. Actual = {speedRow.LatestValue}");

        // Critical assertion: NO row's LatestValue = 0.00 (which
        // would mean RefreshAtAnchor decoded an empty / wrong frame)
        // for any signal that should have a non-zero value at the
        // anchor time. (At least V2B_AliveCount is a counter that
        // increments per frame, so it cannot be 0 at mid-trace.)
        aliveRow.LatestValue.Should().NotBe(0.0,
            "V2B_AliveCount is a counter and must be > 0 at mid-trace. " +
            $"Got: {aliveRow.LatestValue}");

        // Also assert the decoded byte 1 / byte 2 of the anchor frame
        // for direct visibility into what the test expects.
        var expectedAlive = SignalDecoder.Decode(
            midFrame.Data.AsSpan(),
            dbc.MessagesById.Values.First(m => m.Name == "V2B_CMD")
                .Signals.First(s => s.Name == "V2B_AliveCount"));
        var expectedHv = SignalDecoder.Decode(
            midFrame.Data.AsSpan(),
            dbc.MessagesById.Values.First(m => m.Name == "V2B_CMD")
                .Signals.First(s => s.Name == "V2B_HVOnCmd"));
        aliveRow.LatestValue.Should().Be(expectedAlive,
            $"V2B_AliveCount at anchor frame should match SignalDecoder.Decode directly. " +
            $"Expected: {expectedAlive}; Actual: {aliveRow.LatestValue}");
        hvOnRow.LatestValue.Should().Be(expectedHv,
            $"V2B_HVOnCmd at anchor frame should match SignalDecoder.Decode directly. " +
            $"Expected: {expectedHv}; Actual: {hvOnRow.LatestValue}");
    }

    [Fact]
    public void V2B_HVOnCmd_Decodes_From_RealFrameData_As_1()
    {
        // User-reported regression: V2B_HVOnCmd Latest = 0.00 in
        // watch list, but the actual CAN payload has byte 1 = 0x11
        // for every V2B_CMD frame → bit 12..13 (little-endian) = 0b01 = 1.
        // This test pins the decode result to 1 so a regression in
        // SignalDecoder.ReadLittleEndian / ReadBigEndian is caught
        // before the user sees it.
        var sig = new Signal(
            Name: "V2B_HVOnCmd",
            StartBit: 12,
            Length: 2,
            Order: ByteOrder.LittleEndian,
            ValueType: ValueType.Unsigned,
            Factor: 1.0,
            Offset: 0.0,
            Min: 0,
            Max: 3,
            Unit: "bit",
            Receivers: Array.Empty<string>());

        // Real V2B_CMD frame payload from the user's .asc fixture
        // (sampled at multiple timestamps; byte 0..7 = 00 11 00 00 00 00 00 XX).
        var data = new byte[] { 0x00, 0x11, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };

        SignalDecoder.Decode(data, sig).Should().Be(1.0,
            "V2B_HVOnCmd : 12|2@1+ on byte1=0x11 must decode to 1 (bits 12,13 = 0b01)");

        // Sister check: V2B_AliveCount : 8|4@1+ on same byte 1
        // → bits 8,9,10,11 = 0,0,0,1 = 1
        var alive = new Signal(
            Name: "V2B_AliveCount", StartBit: 8, Length: 4,
            Order: ByteOrder.LittleEndian, ValueType: ValueType.Unsigned,
            Factor: 1.0, Offset: 0.0, Min: 0, Max: 15, Unit: "bit",
            Receivers: Array.Empty<string>());
        SignalDecoder.Decode(data, alive).Should().Be(1.0,
            "V2B_AliveCount : 8|4@1+ on byte1=0x11 must decode to 1 (low nibble)");

        // Sister: V2B_Speed : 16|8@1+ on byte 2 = 0x00
        var speed = new Signal(
            Name: "V2B_Speed", StartBit: 16, Length: 8,
            Order: ByteOrder.LittleEndian, ValueType: ValueType.Unsigned,
            Factor: 1.0, Offset: 0.0, Min: 0, Max: 255, Unit: "km/h",
            Receivers: Array.Empty<string>());
        SignalDecoder.Decode(data, speed).Should().Be(0.0,
            "V2B_Speed on byte 2 = 0x00 must decode to 0");
    }
}
