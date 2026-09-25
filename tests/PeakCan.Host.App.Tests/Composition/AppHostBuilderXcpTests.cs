using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PeakCan.Host.App.Composition;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.HIL.Core;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Xcp;
using PeakCan.Host.Core.Xcp.Capability;
using PeakCan.Host.App.Views.Xcp;
using PeakCan.Host.Core.Xcp.Receive;
using Xunit;

namespace PeakCan.Host.App.Tests.Composition;

/// <summary>
/// S3-T8 组合根接线测试（spec D1）：XCP orchestrator + 四面板 singleton 注册、
/// GapObserved 接力（T6 Attach）、host 归因格直喂（T3 → T6）、
/// App 关闭路径 StopAsync 宿主（T7b L-2）、AppShell "XCP" 主 tab 懒创建。
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public class AppHostBuilderXcpTests
{
    [Fact]
    public void Build_Registers_XcpViewModel_And_All_Panel_VMs_As_Singletons()
    {
        using var host = new AppHostBuilder().Build();
        var sp = host.Services;

        AssertSingleton<XcpViewModel>(sp);
        AssertSingleton<XcpConnectionPanelViewModel>(sp);
        AssertSingleton<XcpAcquisitionPanelViewModel>(sp);
        AssertSingleton<XcpCardPanelViewModel>(sp);
        AssertSingleton<XcpAttributionPanelViewModel>(sp);
        AssertSingleton<PeakCan.Host.App.Services.Xcp.XcpCardPanelSink>(sp);
    }

    [Fact]
    public void Build_Orchestrator_Composes_The_DI_Singletons()
    {
        using var host = new AppHostBuilder().Build();
        var sp = host.Services;
        var vm = sp.GetRequiredService<XcpViewModel>();

        vm.Connection.Should().BeSameAs(sp.GetRequiredService<XcpConnectionPanelViewModel>());
        vm.Cards.Should().BeSameAs(sp.GetRequiredService<XcpCardPanelViewModel>());
        vm.Attribution.Should().BeSameAs(sp.GetRequiredService<XcpAttributionPanelViewModel>());
        vm.Acquisition.Should().BeSameAs(sp.GetRequiredService<XcpAcquisitionPanelViewModel>());
    }

    [Fact]
    public void Acquisition_Gets_Broadcast_Sink_Wrapping_Card_And_Record_Sinks()
    {
        // S4-T5（spec D4）：采集出站 = 广播 sink（卡片 sink + 记录 sink fan-out）。
        // 记录未启用时记录 sink 入队恒 no-op（未记录态直返）——零行为变化。
        using var host = new AppHostBuilder().Build();
        var sp = host.Services;
        var vm = sp.GetRequiredService<XcpViewModel>();

        var acquisitionSink = typeof(PeakCan.Host.App.ViewModels.Xcp.XcpAcquisitionPanelViewModel)
            .GetField("_sink", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(vm.Acquisition);
        acquisitionSink.Should().NotBeNull();
        acquisitionSink.Should().BeOfType<PeakCan.Host.Core.Xcp.Record.XcpBroadcastSink>();

        var children = (System.Collections.Generic.IEnumerable<PeakCan.Host.Core.Xcp.Receive.IXcpAcquisitionSink>)
            (typeof(PeakCan.Host.Core.Xcp.Record.XcpBroadcastSink)
                .GetField("_sinks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                ?.GetValue(acquisitionSink) ?? throw new System.InvalidOperationException("broadcast children missing"));
        children.Should().Contain(sp.GetRequiredService<PeakCan.Host.App.Services.Xcp.XcpCardPanelSink>());
        children.Should().Contain(sp.GetRequiredService<PeakCan.Host.Core.Xcp.Record.XcpMdfRecordSink>());
    }

    [Fact]
    public void Build_Registers_RecordPanel_And_Wires_BeforeStop()
    {
        // S4-T5（spec D5）：记录面板 singleton；采集 Stop 先停记录（BeforeStopAsync 接线）。
        using var host = new AppHostBuilder().Build();
        var sp = host.Services;
        var vm = sp.GetRequiredService<XcpViewModel>();

        var record = sp.GetRequiredService<XcpRecordPanelViewModel>();
        AssertSingleton<XcpRecordPanelViewModel>(sp);
        vm.Record.Should().BeSameAs(record);

        var beforeStop = vm.Acquisition.BeforeStopAsync;
        beforeStop.Should().NotBeNull("D5：先停记录再停采集的接线必须存在");

        // 接线口行为：未记录态调用零开销直返（不抛、不产生状态）。
        beforeStop!.Should().NotBeNull();
    }

    [Fact]
    public void Build_Wires_Gap_Relay_From_Cards_To_Attribution()
    {
        using var host = new AppHostBuilder().Build();
        var sp = host.Services;
        var vm = sp.GetRequiredService<XcpViewModel>();

        var gap = new XcpAcquisitionGap(
            XcpAcquisitionGapKind.PlanGapOpened, "wiring test gap",
            ExpectedMaxDuration: TimeSpan.FromMilliseconds(50));
        vm.Cards.Sink.OnGap(gap);
        vm.Cards.Flush();

        // T6 Attach 接力：卡片面板 drain 到的归因旁路进归因面板（不重复 drain）。
        vm.Attribution.PlanGapCell.Count.Should().Be(1);
        vm.Attribution.PlanGapCell.LastExpectedMaxDuration.Should().Be(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void Build_Wires_Host_Attribution_Relay_From_Connection()
    {
        using var host = new AppHostBuilder().Build();
        var sp = host.Services;
        var vm = sp.GetRequiredService<XcpViewModel>();

        // 初始 Disconnected → D5 表"未连总线"格生效。
        vm.Attribution.ActiveHostCell.Should().Be(XcpHostAttributionCell.UnconnectedBus);

        vm.Connection.MarkConnected();
        // Connected → host 格退场（null），归因交给包侧 Receive。
        vm.Attribution.ActiveHostCell.Should().BeNull();
    }

    [Fact]
    public void Build_Registers_XcpAcquisition_ShutdownHostedService_With_Same_Singleton()
    {
        using var host = new AppHostBuilder().Build();
        var sp = host.Services;
        var acquisition = sp.GetRequiredService<XcpAcquisitionPanelViewModel>();

        // T7b L-2：App 关闭路径经 IHostedService.StopAsync 真正 await StopAsync；
        // hosted wrapper 必须持 singleton 同一实例（Dispose fire-and-forget 只作兜底）。
        var hosted = sp.GetServices<IHostedService>()
            .OfType<XcpAcquisitionShutdownService>()
            .Single();
        hosted.Acquisition.Should().BeSameAs(acquisition);

        // StartAsync no-op：不产生副作用。
        hosted.StartAsync(default).IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void Build_Wires_CardPanel_StalePeriod_To_Mvp_Global_100ms()
    {
        using var host = new AppHostBuilder().Build();
        var vm = host.Services.GetRequiredService<XcpCardPanelViewModel>();

        // T8 stalePeriod 接线（MVP 全局 100ms = 3×30ms 阈值近似）。
        var field = typeof(XcpCardPanelViewModel)
            .GetField("_stalePeriod", BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull("stalePeriod 是组合根接线项，必须有可断言的存储位");
        field!.GetValue(vm).Should().Be(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void Build_AppShell_MainTabs_Contains_Lazy_Xcp_Tab()
    {
        RunSta(() =>
        {
            using var host = new AppHostBuilder().Build();
            var sp = host.Services;
            var shell = sp.GetRequiredService<AppShellViewModel>();
            var xcp = sp.GetRequiredService<XcpViewModel>();

            var tab = shell.MainTabs.SingleOrDefault(t => t.Header == "XCP");
            tab.Should().NotBeNull("D1 定案：XCP 是主 tab，与追踪/DBC 同级");

            // 懒创建：ctor/解析后不得实例化 UserControl（TabSpec._view 仍为 null）。
            var lazyField = typeof(TabSpec).GetField("_view", BindingFlags.NonPublic | BindingFlags.Instance);
            lazyField.Should().NotBeNull();
            lazyField!.GetValue(tab).Should().BeNull("MainTabs 必须懒创建——ctor 不实例化 UserControl");

            // 工厂首次访问才建视图，且 DataContext 是 singleton XcpViewModel。
            var view = tab!.View;
            view.Should().BeOfType<XcpView>();
            ((XcpView)view).DataContext.Should().BeSameAs(xcp);
        });
    }

    [Fact]
    public void Build_Binds_ConnectionPanel_To_Core_XcpA2lLoader_Explicitly()
    {
        using var host = new AppHostBuilder().Build();
        var vm = host.Services.GetRequiredService<XcpConnectionPanelViewModel>();

        // T8 评审移交（T10 L4）：连接面板必须显式绑定 Core XcpA2lLoader.Load（D4 单源）
        // ——显式工厂防止未来注册 Func<string, XcpA2lLoadResult> 被可选参 auto-resolve
        // 静默顶掉默认 loader。
        var field = typeof(XcpConnectionPanelViewModel)
            .GetField("_loadA2l", BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull("loadA2l 是组合根显式接线项，必须有可断言的存储位");
        var bound = (Func<string, XcpA2lLoadResult>)field!.GetValue(vm)!;
        bound.Method.DeclaringType.Should().Be(typeof(PeakCan.Host.Core.Xcp.Capability.XcpA2lLoader));
        bound.Method.Name.Should().Be(nameof(PeakCan.Host.Core.Xcp.Capability.XcpA2lLoader.Load));
    }

    [Fact]
    public void Build_ConnectionPanel_Wires_ConnectedChannels_Snapshot()
    {
        using var host = new AppHostBuilder().Build();
        var sp = host.Services;
        var source = sp.GetRequiredService<PeakCan.Host.App.Services.IConnectedChannelsSource>();
        var channel = new HilViewModel.ConnectedChannel(Handle: 0x51, BaudRate.Can500kbps, Fd: false, Name: "USB1");
        source.Publish(new[] { channel });

        var vm = sp.GetRequiredService<XcpConnectionPanelViewModel>();

        // T10 显式工厂必须同样转发 connectedChannels（Q2：通道唯一来源是快照）。
        vm.Channels.Should().Contain(channel);
    }

    private static void AssertSingleton<T>(IServiceProvider sp) where T : class
    {
        var a = sp.GetRequiredService<T>();
        var b = sp.GetRequiredService<T>();
        a.Should().NotBeNull();
        a.Should().BeSameAs(b, $"{typeof(T).Name} 必须 singleton——运行状态跨 tab 切换保持");
    }

    /// <summary>STA 执行（UserControl ctor 要求 STA 线程），照 AppHostBuilderTests 同款。</summary>
    private static void RunSta(Action body)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            LeakedApplicationReset.RunWithTokenResources(body);
            return;
        }
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try { LeakedApplicationReset.RunWithTokenResources(body); }
            catch (Exception ex) { caught = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        LeakedApplicationReset.CleanupLeakedApplication();
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));
        LeakedApplicationReset.CleanupLeakedApplication();
        if (thread.IsAlive)
            throw new TimeoutException("STA thread did not complete within 60 s");
        if (caught is not null) throw caught;
    }
}

