using A2lEditor.Core;
using A2lEditor.Core.Layout;
using PeakCan.Host.App.ViewModels.Xcp;
using PeakCan.Host.Core.Xcp.Protocol;
using PeakCan.Host.Core.Xcp.Replay;

namespace PeakCan.Host.App.Tests.ViewModels.Xcp;

/// <summary>
/// S6-T4：MAP 可视化面板 VM（spec D4）。在线/离线读路径的 Core 钉在 XcpMapReaderTests，
/// 本测试钉：清单装配 / 读函数注入透传 / 渲染请求 / 失败进状态区。
/// </summary>
public sealed class XcpMapPanelViewModelTests
{
    private static XcpMapData FakeData(bool offline) => new(
        "TestMap",
        [0, 1, 2], [0, 1],
        new double[2, 3],
        "V", offline,
        offline ? "离线模式：无 ECU 数据（仅结构）" : "在线读取：3×2（SET_MTA+UPLOAD，零 DOWNLOAD）");

    [Fact]
    public void Maps_list_populated_from_provider()
    {
        var vm = new XcpMapPanelViewModel(mapsProvider: () => ["TestMap", "OtherMap"]);
        vm.RefreshMaps();
        Assert.Equal(2, vm.Maps.Count);
        Assert.Contains("2 个 MAP", vm.StatusText);
    }

    [Fact]
    public async Task Read_passes_through_injected_reader_and_requests_render()
    {
        var called = new List<string>();
        var vm = new XcpMapPanelViewModel(
            mapsProvider: () => ["TestMap"],
            readMap: (name, _) =>
            {
                called.Add(name);
                return Task.FromResult(FakeData(offline: false));
            });
        vm.RefreshMaps();
        vm.SelectedMap = "TestMap";

        await vm.ReadAsync();

        Assert.Equal(["TestMap"], called);
        Assert.NotNull(vm.Result);
        Assert.False(vm.Result!.IsOffline);
        Assert.Contains("在线", vm.StatusText);
        Assert.Equal(1, vm.RenderRequestCount);
    }

    [Fact]
    public async Task Reader_failure_surfaces_in_status_without_throw()
    {
        var vm = new XcpMapPanelViewModel(
            mapsProvider: () => ["TestMap"],
            readMap: (_, _) => Task.FromException<XcpMapData>(
                new InvalidOperationException("MAP 'X' 段映射覆盖不到 0x20000000")));
        vm.RefreshMaps();
        vm.SelectedMap = "TestMap";

        await vm.ReadAsync();

        Assert.Contains("读取失败", vm.StatusText);
        Assert.Null(vm.Result);
        Assert.Equal(0, vm.RenderRequestCount);
    }

    [Fact]
    public async Task Read_without_selection_sets_status()
    {
        var vm = new XcpMapPanelViewModel(mapsProvider: () => ["TestMap"]);
        await vm.ReadAsync();
        Assert.Contains("选择", vm.StatusText);
        Assert.Null(vm.Result);
    }

    [Fact]
    public async Task Negative_response_lands_in_status_text_not_silently_swallowed()
    {
        // T7 评审 P1-1：从机负响应必须进状态区（AsyncRelayCommand 不得静默吞）。
        var vm = new XcpMapPanelViewModel(
            mapsProvider: () => ["Km"],
            readMap: (_, _) => Task.FromException<XcpMapData>(
                new XcpErrorResponseException(new XcpErrorResponse(XcpError.AccessDenied))));
        vm.Maps.Add("Km");
        vm.SelectedMap = "Km";

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Contains("负响应", vm.StatusText);
        Assert.Contains("AccessDenied", vm.StatusText);
    }
}
