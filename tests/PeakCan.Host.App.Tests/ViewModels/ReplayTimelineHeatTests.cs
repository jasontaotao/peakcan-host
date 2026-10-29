using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Services;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// P0：Replay 时间轴热力条。把整段录制压成固定格数，
/// 验证分桶、密度归一、错误帧标红、边界钳制与加载生命周期。
/// </summary>
public class ReplayTimelineHeatTests
{
    private static ReplayFrame FrameAt(double timestamp, bool isError = false)
        => new(timestamp, 0x123, 8, new byte[8],
               isError ? FrameFlags.ErrFrame : FrameFlags.None);

    private static (ReplayViewModel Vm, IReplayService Service, IFileDialogService Dialog) NewVm()
    {
        var service = Substitute.For<IReplayService>();
        var dialog = Substitute.For<IFileDialogService>();
        var library = new TraceSessionLibrary(
            Path.Combine(Path.GetTempPath(), $"tmtrace-heat-{Guid.NewGuid():N}.tmtrace"),
            NullLogger<TraceSessionLibrary>.Instance);
        var recent = new RecentSessionsService(
            NullLogger<RecentSessionsService>.Instance,
            Path.Combine(Path.GetTempPath(), $"recent-heat-{Guid.NewGuid():N}.json"));
        var vm = new ReplayViewModel(
            service, dialog,
            Substitute.For<IAscContentHasher>(), Substitute.For<IAscLocator>(),
            library, recent);
        return (vm, service, dialog);
    }

    [Fact]
    public void Empty_Frames_Produce_No_Cells()
    {
        var (vm, service, _) = NewVm();
        service.Frames.Returns(Array.Empty<ReplayFrame>());
        service.TotalDuration.Returns(0.0);

        vm.RebuildTimelineHeat();

        vm.TimelineHeat.Should().BeEmpty();
    }

    [Fact]
    public void Uniform_Frames_Produce_Fixed_Cell_Count_And_Cover_Full_Duration()
    {
        var (vm, service, _) = NewVm();
        var frames = Enumerable.Range(0, 100)
            .Select(i => FrameAt(i * 10.0 / 100))
            .ToArray();
        service.Frames.Returns(frames);
        service.TotalDuration.Returns(10.0);

        vm.RebuildTimelineHeat();

        vm.TimelineHeat.Should().HaveCount(ReplayViewModel.HeatCellCount);
        vm.TimelineHeat.Sum(c => c.FrameCount).Should().Be(100);
        vm.TimelineHeat[0].Start.Should().Be(0.0);
        vm.TimelineHeat[^1].End.Should().BeApproximately(10.0, 1e-9);
    }

    [Fact]
    public void Intensity_Is_Normalized_By_Max_Bucket()
    {
        var (vm, service, _) = NewVm();
        // 桶 0 有 3 帧（最大），桶 100 有 1 帧，其余为空。
        service.Frames.Returns(new[]
        {
            FrameAt(0.000), FrameAt(0.001), FrameAt(0.002),
            FrameAt(5.0),
        });
        service.TotalDuration.Returns(10.0);

        vm.RebuildTimelineHeat();

        vm.TimelineHeat[0].FrameCount.Should().Be(3);
        vm.TimelineHeat[0].Intensity.Should().Be(1.0);
        vm.TimelineHeat[100].FrameCount.Should().Be(1);
        // 开方 + 6% 下限的归一结果（不是线性 1/3，也不是二值 1）。
        vm.TimelineHeat[100].Intensity.Should().BeApproximately(
            0.06 + 0.94 * Math.Sqrt(1.0 / 3.0), 1e-9);
        vm.TimelineHeat[50].Intensity.Should().Be(0.0);
    }

    [Fact]
    public void Any_Error_Frame_Flags_Whole_Bucket()
    {
        var (vm, service, _) = NewVm();
        service.Frames.Returns(new[]
        {
            FrameAt(5.00),                       // 桶 100 普通帧
            FrameAt(5.01, isError: true),        // 同桶错误帧 → 整格红
            FrameAt(8.00, isError: true),        // 桶 160 单独错误帧
            FrameAt(1.00),                       // 桶 20 普通帧
        });
        service.TotalDuration.Returns(10.0);

        vm.RebuildTimelineHeat();

        vm.TimelineHeat[100].HasError.Should().BeTrue("桶内任一条错误帧即整格标红");
        vm.TimelineHeat[160].HasError.Should().BeTrue();
        vm.TimelineHeat[20].HasError.Should().BeFalse("1.0s 是普通帧");
        vm.TimelineHeat[100].ToolTipText.Should().Contain("含错误帧");
    }

    [Fact]
    public void Frame_At_Duration_Clamps_To_Last_Cell()
    {
        var (vm, service, _) = NewVm();
        service.Frames.Returns(new[] { FrameAt(10.0) });
        service.TotalDuration.Returns(10.0);

        vm.RebuildTimelineHeat();

        vm.TimelineHeat[^1].FrameCount.Should().Be(1, "t == duration 必须落在最后一格");
    }

    [Fact]
    public async Task OpenAsync_Rebuilds_Heat_After_Successful_Load()
    {
        var (vm, service, dialog) = NewVm();
        service.TotalDuration.Returns(10.0);
        service.Frames.Returns(new[] { FrameAt(1.0), FrameAt(5.0) });
        service.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns("trace.asc");

        await vm.OpenCommand.ExecuteAsync(null);

        vm.TimelineHeat.Should().NotBeEmpty();
        vm.TimelineHeat.Sum(c => c.FrameCount).Should().Be(2);
    }

    [Fact]
    public async Task Failed_Load_Clears_Heat()
    {
        var (vm, service, dialog) = NewVm();
        service.TotalDuration.Returns(10.0);
        service.Frames.Returns(new[] { FrameAt(1.0) });
        vm.RebuildTimelineHeat();
        vm.TimelineHeat.Should().NotBeEmpty();

        service.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new ReplayFormatException("bad asc"));
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns("bad.asc");

        await vm.OpenCommand.ExecuteAsync(null);

        vm.TimelineHeat.Should().BeEmpty("加载失败后不得继续展示上一次的热力数据");
    }

    [Fact]
    public async Task Cancelled_Load_Clears_Heat()
    {
        var (vm, service, dialog) = NewVm();
        service.TotalDuration.Returns(10.0);
        service.Frames.Returns(new[] { FrameAt(1.0) });
        vm.RebuildTimelineHeat();
        vm.TimelineHeat.Should().NotBeEmpty();

        service.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new OperationCanceledException());
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns("cancelled.asc");

        await vm.OpenCommand.ExecuteAsync(null);

        vm.TimelineHeat.Should().BeEmpty();
        vm.IsLoaded.Should().BeFalse();
    }
}
