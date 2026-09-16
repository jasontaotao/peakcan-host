using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core;
using PeakCan.Host.Core.HIL.Analysis;
using PeakCan.Host.Infrastructure.HIL.Reporting;
using PeakCan.Host.Core.HIL;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// SecOC App 接线（2026-09-16 plan, Task 6）：HIL 面板 SecOc 配置文件输入。
/// 覆盖 BuildRunRequest 透传、空值零回归、panel-state 往返。
/// </summary>
public sealed class HilViewModelSecOcTests
{
    private static HilViewModel CreateViewModel()
        => new(
            Substitute.For<IHilRunnerService>(),
            NullLogger<HilViewModel>.Instance,
            Substitute.For<IFileDialogService>(),
            Substitute.For<IHilAnalysisService>(),
            Substitute.For<IHilReportService>());

    [Fact]
    public void BuildRunRequest_WithSecOcConfigPath_PassesItThrough()
    {
        var vm = CreateViewModel();
        vm.SecOcConfigPath = @"C:\secoc-pdus.secoc";

        var request = vm.BuildRunRequest(hardwareChannels: null);

        request.SecOcConfigPath.Should().Be(@"C:\secoc-pdus.secoc");
    }

    [Fact]
    public void BuildRunRequest_WithoutSecOcConfigPath_LeavesNull()
    {
        // 空（含空白）→ null，零回归：不配置 SecOC 时请求不带 config。
        var vm = CreateViewModel();
        vm.SecOcConfigPath = "   ";

        var request = vm.BuildRunRequest(hardwareChannels: null);

        request.SecOcConfigPath.Should().BeNull();
    }

    [Fact]
    public void PanelState_RoundTripsSecOcConfigPath()
    {
        var vm = CreateViewModel();
        vm.SecOcConfigPath = @"C:\secoc-pdus.secoc";

        var state = vm.CapturePanelState();

        var restored = CreateViewModel();
        restored.ApplyPanelState(state);
        restored.SecOcConfigPath.Should().Be(@"C:\secoc-pdus.secoc");
    }
}
