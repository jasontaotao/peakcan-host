using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Uds.Odx;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Uds;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core.Analysis.Uds;
using PeakCan.Host.Core.Replay;
using PeakCan.Host.Core.Uds.Database;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// 离线页的功能寻址配置。
/// <para>
/// <b>不配的代价是看得见的：</b>测试仪用功能寻址（如 <c>7DF</c>）发出的请求会被判
/// 背景流量丢掉，而 ECU 在物理响应 ID 上回的响应仍被收下——报告里凭空多出
/// 「孤立响应」，且说不清来源。真实日志 <c>150635_log.asc</c> 里正是多出三条。
/// </para>
/// </summary>
public sealed class UdsLogAnalysisFunctionalAddressTests
{
    private sealed class StubLoader(ReplayFrame[] frames) : IUdsOfflineTraceLoader
    {
        public Task<UdsOfflineTraceLoadResult> LoadAsync(string path, CancellationToken ct = default)
            => Task.FromResult(new UdsOfflineTraceLoadResult(frames, "ASC"));
    }

    private sealed class NoopHandoff : ITraceViewerChatHandoffService
    {
        public void Handoff(UdsChatHandoffPayload payload) { }
    }

    private sealed class NoopDialogs : IFileDialogService
    {
        public string? ShowOpenDialog(string filter) => null;
        public string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory) => null;
    }

    private sealed class NoopExport : IUdsExportPrompt
    {
        public string? ChooseSavePath(out bool isCsv) { isCsv = false; return null; }
        public bool ConfirmOverwrite(string path) => true;
        public UdsExportChoice AskRedaction() => UdsExportChoice.Cancel;
    }

    private sealed class NoopOdxService : IOdxImportService
    {
        public Task<OdxImportResult> ImportAsync(string path, CancellationToken ct = default)
            => Task.FromResult(OdxImportResult.Ok(0, 0, 0, Array.Empty<string>()));
    }

    private static ReplayFrame Frame(double ts, uint id, params byte[] data)
        => new(ts, id, (byte)data.Length, data, FrameFlags.None,
            IsExtended: false, Channel: 1, IsTransmit: true);

    private static UdsLogAnalysisViewModel Vm(UdsTransportSettingsStore? settings, params ReplayFrame[] frames)
    {
        var vm = new UdsLogAnalysisViewModel(new StubLoader(frames),
            new UdsOfflineAnalyzer(),
            new NoopHandoff(),
            new NoopDialogs(),
            new NoopExport(),
            new DidDatabase(userJsonPath: null),
            new RoutineDatabase(userJsonPath: null),
            new OdxImportViewModel(new NoopOdxService()),
            settings);
        // 产品默认通道为空（必须显式选择）；本文件用例统一按通道 1 跑。
        vm.ChannelText = "1";
        return vm;
    }

    /// <summary>
    /// 不配功能 ID 时，功能广播请求被丢掉、它的响应变成孤儿——这是**已知的**行为，
    /// 用它来证明「配了才有区别」，防止测试空转。
    /// </summary>
    [Fact]
    public async Task Without_a_functional_id_the_broadcast_response_becomes_an_orphan()
    {
        var vm = Vm(settings: null,
            Frame(0.0, 0x7DF, 0x02, 0x10, 0x03),
            Frame(0.01, 0x7E8, 0x06, 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4));
        vm.FilePath = "x.asc";

        await vm.AnalyzeAsync();

        vm.Report!.OrphanResponses.Should().ContainSingle(
            "请求被当背景流量丢了，响应却因为 ID 匹配被收下");
    }

    [Fact]
    public async Task Configuring_the_functional_id_closes_the_broadcast_exchange()
    {
        var vm = Vm(settings: null,
            Frame(0.0, 0x7DF, 0x02, 0x10, 0x03),
            Frame(0.01, 0x7E8, 0x06, 0x50, 0x03, 0x00, 0x32, 0x01, 0xF4));
        vm.FilePath = "x.asc";
        vm.FunctionalIdText = "7DF";

        await vm.AnalyzeAsync();

        vm.Report!.OrphanResponses.Should().BeEmpty();
        vm.Report.Transactions.Should().ContainSingle()
            .Which.FinalKind.Should().Be(UdsFinalKind.Positive);
    }

    /// <summary>留空 = 不解析，不能因为空串抛异常。</summary>
    [Fact]
    public async Task An_empty_functional_id_means_no_functional_addressing()
    {
        var vm = Vm(settings: null, Frame(0.0, 0x7E0, 0x02, 0x10, 0x03),
            Frame(0.01, 0x7E8, 0x02, 0x50, 0x03));
        vm.FilePath = "x.asc";
        vm.FunctionalIdText = "   ";

        await vm.AnalyzeAsync();

        vm.Report!.Transactions.Should().ContainSingle();
    }

    /// <summary>
    /// 填了非法值必须报错，不能静默当成没填——用户会以为配上了，
    /// 而报告继续把广播请求当背景流量。
    /// </summary>
    [Fact]
    public async Task An_invalid_functional_id_surfaces_an_error_instead_of_silently_ignoring_it()
    {
        var vm = Vm(settings: null, Frame(0.0, 0x7E0, 0x02, 0x10, 0x03),
            Frame(0.01, 0x7E8, 0x02, 0x50, 0x03));
        vm.FilePath = "x.asc";
        vm.FunctionalIdText = "ZZZ";

        await vm.AnalyzeAsync();

        vm.Report.Should().BeNull("地址解析失败时不能产出报告");
        vm.Status.Should().Contain("功能", "错误信息要指出是哪个字段");
    }

    /// <summary>配置里的功能 ID 要带到离线页，用户不用每次手敲。</summary>
    [Fact]
    public void The_functional_id_is_inherited_from_saved_settings()
    {
        var path = System.IO.Path.Combine(Path.GetTempPath(), $"uds-fa-{Guid.NewGuid():N}.json");
        try
        {
            var store = new UdsTransportSettingsStore(
                NullLogger<UdsTransportSettingsStore>.Instance, path);
            store.Save(UdsTransportSettings.Default with { FunctionalId = 0x7DF });

            var vm = Vm(store, Frame(0.0, 0x7E0, 0x02, 0x10, 0x03));

            vm.FunctionalIdText.Should().Be("7DF");
        }
        finally
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
    }
}
