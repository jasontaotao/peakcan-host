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
                NullLogger<TraceSessionLibrary>.Instance));
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
}
