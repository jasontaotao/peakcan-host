using System;
using System.Collections.Generic;
using System.IO;
using A2lEditor.Core;
using A2lEditor.Core.IfData;
using A2lEditor.Core.Layout;
using A2lEditor.Core.Model;
using A2lEditor.Core.Parsing;
using FluentAssertions;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Xcp;
using PeakCan.Host.Core.Xcp.Capability;
using Xunit;
// 本测试文件大量构造 A2L 最小模型，显式指回 System.IO.File/Path（repo 内有同名类型冲突先例）。
using File = System.IO.File;
using Path = System.IO.Path;

namespace PeakCan.Host.App.Tests.ViewModels.Xcp;

/// <summary>
/// S3-T3 红测：XcpConnectionPanelViewModel（spec D6/Q2/D5）。
/// <para>
/// Q2 定案：通道选择仅来自 IConnectedChannelsSource 快照，XCP tab 不做独立连接控件；
/// D5 定案：未连接态是归因合并表"未连总线"格的生产者（host 两态之一，不进记录文件）；
/// D4/T2 定案：A2L 加载走注入委托（默认 XcpA2lLoader.Load），失败形状是显式 Result，
/// 另有第三类失败（文件不存在 / IO 异常）必须进状态区而不是裸抛穿 UI
///（T2 评审 LOW 发现：XcpA2lLoader.Load 不捕获 IO 异常）。
/// </para>
/// </summary>
public class XcpConnectionPanelViewModelTests : IDisposable
{
    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static A2lDocument EmptyDocument() =>
        new(A2lVersion.V1_6x, "P", "c", "", null, Array.Empty<A2lModule>(), "", 0);

    private static XcpA2lLoadResult.Loaded LoadedResultWith(
        params ValidationNote[] notes)
    {
        var doc = EmptyDocument();
        return new XcpA2lLoadResult.Loaded(
            doc,
            BuildIfData(),
            notes,
            new ContractSet(doc));
    }

    private static XcpIfData BuildIfData() => new(
        Scope: XcpIfDataScope.ModuleLevel,
        ProtocolLayer: null, Daq: null, Pag: null, Pgm: null,
        OnCan: [],
        Segments: [],
        Unmodelled: [],
        Missing: [],
        SourceText: string.Empty);

    private static readonly int[] NoteLines = { 1, 2 };

    private static ValidationNote Note(string message) =>
        new(CrossCheckKind.MissingAddress, message, NoteLines, ErrorSeverity.Warning);

    private static Func<string, XcpA2lLoadResult> DelegateReturning(
        XcpA2lLoadResult result) => _ => result;

    private static Func<string, XcpA2lLoadResult> DelegateThrowing(Exception ex) =>
        _ => throw ex;

    private static ConnectedChannelsSource NewSource(
        params HilViewModel.ConnectedChannel[] snapshot)
    {
        var source = new ConnectedChannelsSource();
        if (snapshot.Length > 0) source.Publish(snapshot);
        return source;
    }

    private static HilViewModel.ConnectedChannel Channel(string name) =>
        new(Handle: 0x51, BaudRate.Can500kbps, Fd: false, Name: name);
    private readonly List<string> _tempFiles = new();

    /// <summary>注入委托类用例的占位 A2L 文件（预检 File.Exists 必须能命中）。
    /// 登记进 _tempFiles，Dispose 统一删除——不留 %TEMP% 残留（T3 评审 LOW-2）。</summary>
    private string TempA2lFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"xcp-t3-vm-{Guid.NewGuid():N}.a2l");
        File.WriteAllText(path, "ASAP2_VERSION 1 40\n");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch (IOException) { /* best effort */ }
        }
    }


    /// <summary>
    /// 真机 A2L 冒烟夹具。首选 bin/TestData（csproj Content 拷贝，Cli.Tests 同款
    /// 先例——T3 评审 MEDIUM-2：不依赖"输出目录嵌在源码树内"假设）；拷贝缺失时
    /// 回溯源码树降级（本地开发布局）。
    /// </summary>
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

        throw new InvalidOperationException(
            "App_merge_INCA.a2l not found relative to App.Tests output — repo checkout layout changed?");
    }

    // ------------------------------------------------------------------
    // (a) A2L 加载成功：ValidationNote 进状态区 + 状态含合同对象数
    // ------------------------------------------------------------------

    [Fact]
    public void LoadA2L_success_puts_validation_notes_and_contract_count_into_status_area()
    {
        var result = LoadedResultWith(Note("cross-check one"), Note("cross-check two"));
        var vm = new XcpConnectionPanelViewModel(loadA2l: DelegateReturning(result));
        vm.A2lPath = TempA2lFile();

        vm.LoadA2LCommand.Execute(null);

        vm.ConnectionState.Should().Be(XcpConnectionState.Loaded);
        vm.ValidationNotes.Should().BeSameAs(result.ValidationNotes);
        vm.LoadedResult.Should().BeSameAs(result);
        vm.StatusLines.Should().Contain(l => l.Contains("cross-check one"));
        vm.StatusLines.Should().Contain(l => l.Contains("cross-check two"));
        vm.StatusLines.Should().Contain(l =>
            l.Contains("合同对象") && l.Contains(result.Contracts.All.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void LoadA2L_with_real_loader_reports_declared_contract_count()
    {
        var vm = new XcpConnectionPanelViewModel(loadA2l: XcpA2lLoader.Load);
        vm.A2lPath = FindSharedRealA2L();

        vm.LoadA2LCommand.Execute(null);

        vm.ConnectionState.Should().Be(XcpConnectionState.Loaded);
        // A-11 口径（T2 钉过）：965 MEASUREMENT + 1377 CHARACTERISTIC + 49 AXIS_PTS。
        vm.LoadedResult!.Contracts.All.Should().HaveCount(2391);
        vm.StatusLines.Should().Contain(l => l.Contains("2391 个合同对象"));
    }

    // ------------------------------------------------------------------
    // (a') 第一类失败：Load 返回 Failed(Kind, Message) → 显示且不炸
    // ------------------------------------------------------------------

    [Fact]
    public void LoadA2L_result_failure_shows_kind_and_message_without_throwing()
    {
        var failed = new XcpA2lLoadResult.Failed(
            XcpA2lLoadFailureKind.NoXcpIfData, @"A2L has no XCP IF_DATA: C:\a2l\bad.a2l");
        var vm = new XcpConnectionPanelViewModel(loadA2l: DelegateReturning(failed));
        vm.A2lPath = TempA2lFile();

        vm.LoadA2LCommand.Execute(null);

        vm.ConnectionState.Should().Be(XcpConnectionState.Disconnected,
            "失败的加载不得把状态机推到 Loaded");
        vm.LoadedResult.Should().BeNull();
        vm.StatusLines.Should().Contain(l =>
            l.Contains("加载失败") && l.Contains("NoXcpIfData") && l.Contains("bad.a2l"));
    }

    [Fact]
    public void LoadA2L_result_failure_after_success_keeps_loaded_state()
    {
        var loaded = LoadedResultWith(Note("ok"));
        var failed = new XcpA2lLoadResult.Failed(
            XcpA2lLoadFailureKind.ParseFailed, "parse boom");
        XcpA2lLoadResult current = loaded;
        var vm = new XcpConnectionPanelViewModel(loadA2l: _ =>
        {
            var r = current;
            current = failed;
            return r;
        });
        vm.A2lPath = TempA2lFile();

        vm.LoadA2LCommand.Execute(null);
        vm.LoadA2LCommand.Execute(null);

        vm.ConnectionState.Should().Be(XcpConnectionState.Loaded,
            "重载失败不清掉已加载的好状态");
        vm.LoadedResult.Should().BeSameAs(loaded);
        vm.StatusLines.Should().Contain(l => l.Contains("parse boom"));
    }

    // ------------------------------------------------------------------
    // (a'') 第三类失败（T2 评审 LOW）：文件不存在预检 + IO 异常捕获 → 状态区，不炸
    // ------------------------------------------------------------------

    [Fact]
    public void LoadA2L_missing_file_reports_error_without_calling_loader()
    {
        var loaderCalled = false;
        var vm = new XcpConnectionPanelViewModel(loadA2l: _ =>
        {
            loaderCalled = true;
            return LoadedResultWith();
        });
        var missing = Path.Combine(Path.GetTempPath(), $"xcp-t3-missing-{Guid.NewGuid():N}.a2l");
        File.Exists(missing).Should().BeFalse();
        vm.A2lPath = missing;

        vm.LoadA2LCommand.Execute(null);

        loaderCalled.Should().BeFalse("文件不存在时不得调用 Load（IO 异常裸抛是 T2 评审发现）");
        vm.ConnectionState.Should().Be(XcpConnectionState.Disconnected);
        vm.StatusLines.Should().Contain(l =>
            l.Contains("文件不存在") && l.Contains(missing));
    }

    [Fact]
    public void LoadA2L_io_exception_is_caught_into_status_area_not_thrown()
    {
        var existing = Path.Combine(
            Path.GetTempPath(), $"xcp-t3-io-{Guid.NewGuid():N}.a2l");
        File.WriteAllText(existing, "ASAP2_VERSION 1 40\n");

        var ioVm = new XcpConnectionPanelViewModel(
            loadA2l: DelegateThrowing(new IOException("file locked by INCA")));
        ioVm.A2lPath = existing;
        ioVm.LoadA2LCommand.Execute(null);
        ioVm.ConnectionState.Should().Be(XcpConnectionState.Disconnected);
        ioVm.StatusLines.Should().Contain(l =>
            l.Contains("读取失败") && l.Contains("file locked by INCA"));

        var unauthorizedVm = new XcpConnectionPanelViewModel(
            loadA2l: DelegateThrowing(new UnauthorizedAccessException("access denied")));
        unauthorizedVm.A2lPath = existing;
        unauthorizedVm.LoadA2LCommand.Execute(null);
        unauthorizedVm.ConnectionState.Should().Be(XcpConnectionState.Disconnected);
        unauthorizedVm.StatusLines.Should().Contain(l =>
            l.Contains("读取失败") && l.Contains("access denied"));

        File.Delete(existing);
    }

    // ------------------------------------------------------------------
    // (b) 通道选择仅来自 IConnectedChannelsSource 快照；空快照 → Start 禁用
    // ------------------------------------------------------------------

    [Fact]
    public void Channels_come_only_from_connected_channels_source_snapshot()
    {
        var source = NewSource(Channel("USB1"), Channel("USB2"));
        var vm = new XcpConnectionPanelViewModel(connectedChannels: source);

        vm.Channels.Should().HaveCount(2);
        vm.Channels.Should().Contain(Channel("USB1"));
        vm.Channels.Should().Contain(Channel("USB2"));

        source.Publish(new[] { Channel("USB1"), Channel("USB2"), Channel("USBCAN 0-1") });
        vm.Channels.Should().HaveCount(3, "Changed 事件触发刷新");
        vm.Channels.Should().Contain(Channel("USBCAN 0-1"));
    }

    [Fact]
    public void Channel_disappearing_from_snapshot_clears_selection()
    {
        var usb1 = Channel("USB1");
        var source = NewSource(usb1, Channel("USB2"));
        var vm = new XcpConnectionPanelViewModel(connectedChannels: source);
        vm.SelectedChannel = usb1;

        source.Publish(new[] { Channel("USB2") });

        vm.SelectedChannel.Should().BeNull("选中的通道不在新快照里 = 失效");
    }

    [Fact]
    public void Empty_snapshot_means_no_channels_and_start_disabled()
    {
        var source = NewSource();
        var vm = new XcpConnectionPanelViewModel(
            loadA2l: DelegateReturning(LoadedResultWith()), connectedChannels: source);

        vm.Channels.Should().BeEmpty();
        vm.SelectedChannel.Should().BeNull();
        vm.CanStart.Should().BeFalse("空快照无可选通道，Start 允许标志必须为 false");

        // 即使 A2L 已加载，没有通道依旧不允许 Start。
        var loaded = LoadedResultWith();
        vm.A2lPath = TempA2lFile();
        vm.LoadA2LCommand.Execute(null);
        vm.ConnectionState.Should().Be(XcpConnectionState.Loaded);
        vm.CanStart.Should().BeFalse();
    }

    [Fact]
    public void CanStart_requires_loaded_state_and_selected_channel()
    {
        var source = NewSource(Channel("USB1"));
        var loaded = LoadedResultWith();
        var vm = new XcpConnectionPanelViewModel(
            loadA2l: DelegateReturning(loaded), connectedChannels: source);

        // 通道已选但 A2L 未加载 → 不允许。
        vm.SelectedChannel = source.Current[0];
        vm.CanStart.Should().BeFalse();

        // A2L 已加载 + 通道已选 → 允许。
        vm.A2lPath = TempA2lFile();
        vm.LoadA2LCommand.Execute(null);
        vm.CanStart.Should().BeTrue();

        // 清掉选择 → 回到不允许。
        vm.SelectedChannel = null;
        vm.CanStart.Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // (c) ConnectionState 状态机 + D5 "未连总线" 格生产者
    // ------------------------------------------------------------------

    [Fact]
    public void ConnectionState_machine_maps_unconnected_state_to_d5_unconnected_bus_cell()
    {
        var vm = new XcpConnectionPanelViewModel(
            loadA2l: DelegateReturning(LoadedResultWith()));

        // 初始 Disconnected → D5 表"未连总线"格。
        vm.ConnectionState.Should().Be(XcpConnectionState.Disconnected);
        vm.AttributionCell.Should().Be(XcpHostAttributionCell.UnconnectedBus);

        // 加载成功 → Loaded：仍未连总线，归因格不变。
        vm.A2lPath = TempA2lFile();
        vm.LoadA2LCommand.Execute(null);
        vm.ConnectionState.Should().Be(XcpConnectionState.Loaded);
        vm.AttributionCell.Should().Be(XcpHostAttributionCell.UnconnectedBus);

        // CONNECT 成功（T7 采集生命周期驱动）→ Connected：不再是"未连总线"。
        vm.MarkConnected();
        vm.ConnectionState.Should().Be(XcpConnectionState.Connected);
        vm.AttributionCell.Should().BeNull("Connected 态不再是 host 未连总线格");

        // 断开 → 回 Disconnected，"未连总线"格重新生效。
        vm.MarkDisconnected();
        // T3 评审 MEDIUM-1：A2L 仍有效 → 回 Loaded，不推死胡同；"未连总线"格同格生效。
        vm.ConnectionState.Should().Be(XcpConnectionState.Loaded);
        vm.AttributionCell.Should().Be(XcpHostAttributionCell.UnconnectedBus);
    }

    [Fact]
    public void Start_allowed_again_after_disconnect_when_a2l_still_loaded()
    {
        var source = NewSource(Channel("USB1"));
        var vm = new XcpConnectionPanelViewModel(
            loadA2l: DelegateReturning(LoadedResultWith()), connectedChannels: source);
        vm.A2lPath = TempA2lFile();
        vm.LoadA2LCommand.Execute(null);
        vm.SelectedChannel = source.Current[0];
        vm.CanStart.Should().BeTrue();

        vm.MarkDisconnected();

        vm.ConnectionState.Should().Be(XcpConnectionState.Loaded, "A2L 仍有效——D6 的 Stop 不要求重载 A2L（T3 评审 MEDIUM-1）");
        vm.CanStart.Should().BeTrue("通道快照未变，重连后允许直接再次 Start");
    }

    [Fact]
    public void Disconnect_without_loaded_a2l_falls_back_to_disconnected()
    {
        var vm = new XcpConnectionPanelViewModel();

        vm.MarkDisconnected();

        vm.ConnectionState.Should().Be(XcpConnectionState.Disconnected);
    }

    // ------------------------------------------------------------------
    // (d) T10 L2 下沉：LoadA2LCommand CanExecute 门（Connected 态禁用，
    //     T3 评审 LOW-3 / T8 评审移交）。视图 DataTrigger 保留作双保险。
    // ------------------------------------------------------------------

    [Fact]
    public void LoadA2LCommand_executable_before_connection()
    {
        var vm = new XcpConnectionPanelViewModel(loadA2l: DelegateReturning(LoadedResultWith()));

        vm.LoadA2LCommand.CanExecute(null)
            .Should().BeTrue("Disconnected 态必须允许加载 A2L");
    }

    [Fact]
    public void LoadA2LCommand_disabled_in_connected_state()
    {
        var vm = new XcpConnectionPanelViewModel(loadA2l: DelegateReturning(LoadedResultWith()));
        vm.A2lPath = TempA2lFile();
        vm.LoadA2LCommand.Execute(null);
        vm.ConnectionState.Should().Be(XcpConnectionState.Loaded);
        vm.LoadA2LCommand.CanExecute(null).Should().BeTrue();

        vm.MarkConnected();

        // 门下沉 VM 后对所有命令宿主生效（原视图 DataTrigger 只覆盖单按钮实例）。
        // 注：CommunityToolkit 的 ICommand.Execute 本身不查 CanExecute，门由
        // 命令宿主（WPF 按钮/快捷键）执行前查询——本测试钉的是 CanExecute 语义。
        vm.LoadA2LCommand.CanExecute(null)
            .Should().BeFalse("Connected 态重载成功会把状态降回 Loaded——归因格说谎窗口期");
    }

    [Fact]
    public void LoadA2LCommand_disabled_even_when_connected_without_loaded_a2l()
    {
        // 边角：未加载 A2L 直接 MarkConnected（通道快照驱动的连接路径）同样禁用。
        var vm = new XcpConnectionPanelViewModel();

        vm.MarkConnected();

        vm.LoadA2LCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void LoadA2LCommand_re_enabled_after_disconnect()
    {
        var vm = new XcpConnectionPanelViewModel(loadA2l: DelegateReturning(LoadedResultWith()));
        vm.A2lPath = TempA2lFile();
        vm.LoadA2LCommand.Execute(null);
        vm.MarkConnected();
        vm.LoadA2LCommand.CanExecute(null).Should().BeFalse();

        // T3 评审 MEDIUM-1：A2L 仍有效 → 回 Loaded，重载必须重新可用（D6：Stop
        // 不要求重载 A2L，禁用门不得把用户推进死胡同）。
        vm.MarkDisconnected();

        vm.ConnectionState.Should().Be(XcpConnectionState.Loaded);
        vm.LoadA2LCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void LoadA2LCommand_notifies_can_execute_changed_on_state_transitions()
    {
        var vm = new XcpConnectionPanelViewModel(loadA2l: DelegateReturning(LoadedResultWith()));
        var notifications = 0;
        vm.LoadA2LCommand.CanExecuteChanged += (_, _) => notifications++;

        vm.MarkConnected();
        var afterConnect = notifications;

        vm.MarkDisconnected();

        afterConnect.Should().BeGreaterThan(0, "进入 Connected 必须通知命令宿主重查 CanExecute");
        notifications.Should().BeGreaterThan(afterConnect, "退出 Connected 也必须通知");
    }
}
