using System.IO;
using A2lEditor.Core;
using PeakCan.Host.App.ViewModels.Xcp;
using PeakCan.Host.Core.Xcp.Capability;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Xcp;

/// <summary>
/// S6-T7 评审 P0-1 修复钉：A2L 加载 → MAP 清单装配的组合根→VM 最后一跳。
/// 两条路径都钉：A2L 后加载（PropertyChanged 事件）/ A2L 先加载（ctor 初始装配）。
/// </summary>
public sealed class XcpMapPanelWiringTests
{
    private static string FindSharedRealA2L()
    {
        var inOutput = Path.Combine(AppContext.BaseDirectory, "TestData", "App_merge_INCA.a2l");
        if (File.Exists(inOutput)) return inOutput;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir!.FullName, "PeakCan.Host.Core.Tests", "TestData", "App_merge_INCA.a2l");
            if (File.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException("App_merge_INCA.a2l not found");
    }

    [Fact]
    public async Task A2l_loaded_after_ctor_populates_map_list()
    {
        var connection = new XcpConnectionPanelViewModel();
        var map = NewWiredMapPanel(connection);
        _ = new XcpViewModel(connection: connection, map: map);
        Assert.Empty(map.Maps);

        connection.A2lPath = FindSharedRealA2L();
        connection.LoadA2LCommand.Execute(null);

        Assert.NotEmpty(map.Maps);
        Assert.Equal(15, map.Maps.Count); // 真机 fixture 15 个 MAP 对象
    }

    [Fact]
    public async Task A2l_loaded_before_ctor_populates_map_list_on_construction()
    {
        var connection = new XcpConnectionPanelViewModel();
        connection.A2lPath = FindSharedRealA2L();
        connection.LoadA2LCommand.Execute(null);

        var map = NewWiredMapPanel(connection);
        _ = new XcpViewModel(connection: connection, map: map);

        Assert.Equal(15, map.Maps.Count);
    }

    /// <summary>组合根（AppHostBuilder）同款 mapsProvider 闭包——本测试钉的是
    /// "RefreshMaps 触发时机"这一跳，不是 provider 本身。</summary>
    private static XcpMapPanelViewModel NewWiredMapPanel(XcpConnectionPanelViewModel connection) =>
        new(mapsProvider: () => connection.LoadedResult?.Document.Modules
            .SelectMany(m => m.Characteristics)
            .Where(c => c.Type == "MAP")
            .Select(c => c.Name)
            .ToList() ?? []);

    [Fact]
    public async Task A2l_reloaded_while_loaded_refreshes_map_list()
    {
        // S6 挂账 P2-5 钉：Loaded→Loaded 重载不触发 PropertyChanged——
        // 显式 A2lLoaded 事件必须驱动 RefreshMaps（重载后清单仍是新 LoadedResult 的）。
        var connection = new XcpConnectionPanelViewModel();
        var refreshCount = 0;
        var map = new XcpMapPanelViewModel(mapsProvider: () =>
        {
            refreshCount++;
            return connection.LoadedResult?.Document.Modules
                .SelectMany(m => m.Characteristics)
                .Where(c => c.Type == "MAP")
                .Select(c => c.Name)
                .ToList() ?? [];
        });
        _ = new XcpViewModel(connection: connection, map: map);

        connection.A2lPath = FindSharedRealA2L();
        connection.LoadA2LCommand.Execute(null);
        var afterFirst = refreshCount;

        connection.LoadA2LCommand.Execute(null); // 重载（状态机恒 Loaded，无 PropertyChanged）

        Assert.True(afterFirst >= 1);
        Assert.True(refreshCount > afterFirst, "重载必须再触发一次 RefreshMaps（P2-5）");
        Assert.NotEmpty(map.Maps);
    }
}
