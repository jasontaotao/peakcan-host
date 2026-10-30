using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// P1 全局快捷键的**作用域**契约：页面动作只在目标页面激活时生效。
/// <para>
/// 这里只验证"命令层"的行为——按键到命令的映射是 XAML 声明，
/// 由 <see cref="Views.AppShellShortcutLayoutTests"/> 用结构断言覆盖。
/// 两层分开测的理由：键位表改错和门控写错是两类事故，混在一起失败信息会互相掩盖。
/// </para>
/// <para>
/// 与 <see cref="TraceFilterBuilderTests"/> 一样走 MTA 直驱，不构造 WPF 控件。
/// 加入 <see cref="WpfAppTestCollection"/> 是因为本类与 AppShellViewModelTests
/// 共用同一套构造路径，避免与那批 STA 用例并发抢 WPF 单例。
/// </para>
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public class AppShellShortcutTests
{
    private static CanFrame Frame(uint id)
        => new(new CanId(id, FrameFormat.Standard),
               new byte[] { 0x01 },
               FrameFlags.None,
               ChannelId.None,
               Timestamp.FromMicroseconds(1_000_000UL));

    [Fact]
    public void Clear_Trace_Frames_Is_Gated_To_Trace_Tab()
    {
        var trace = new TraceViewModel();
        trace.AppendBatchCore(new[] { Frame(0x100), Frame(0x200) });
        var vm = AppShellViewModelTests.NewVm(traceVm: trace);

        // 非追踪页：既不响应，也不能被"强行执行"绕过（命令体内还有一道守卫）。
        vm.SelectedMainTabIndex = 1;
        vm.ClearTraceFramesCommand.CanExecute(null).Should().BeFalse();
        vm.ClearTraceFramesCommand.Execute(null);
        trace.Entries.Should().HaveCount(2, "在 DBC 页按快捷键不得清掉追踪缓冲");

        vm.SelectedMainTabIndex = 0;
        vm.ClearTraceFramesCommand.CanExecute(null).Should().BeTrue();
        vm.ClearTraceFramesCommand.Execute(null);
        trace.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Clear_Trace_Frames_Requires_Non_Empty_Buffer()
    {
        var vm = AppShellViewModelTests.NewVm(traceVm: new TraceViewModel());

        vm.SelectedMainTabIndex = 0;
        vm.ClearTraceFramesCommand.CanExecute(null)
            .Should().BeFalse("空表本来就没有可清的内容，按键不应有反应");
    }

    [Fact]
    public void Toggle_Trace_Pause_RoundTrips_And_Is_Gated_To_Trace_Tab()
    {
        var trace = new TraceViewModel();
        var vm = AppShellViewModelTests.NewVm(traceVm: trace);

        vm.SelectedMainTabIndex = 1;
        vm.ToggleTracePauseCommand.CanExecute(null).Should().BeFalse();
        vm.ToggleTracePauseCommand.Execute(null);
        trace.IsPaused.Should().BeFalse();

        vm.SelectedMainTabIndex = 0;
        vm.ToggleTracePauseCommand.CanExecute(null).Should().BeTrue();
        vm.ToggleTracePauseCommand.Execute(null);
        trace.IsPaused.Should().BeTrue();
        vm.ToggleTracePauseCommand.Execute(null);
        trace.IsPaused.Should().BeFalse();
    }

    [Fact]
    public void Export_Trace_Csv_Requires_Frames()
    {
        // 只测 CanExecute：Execute 会弹 SaveFileDialog（模态），测试里绝不能触发。
        var trace = new TraceViewModel();
        var vm = AppShellViewModelTests.NewVm(traceVm: trace);
        vm.SelectedMainTabIndex = 0;

        vm.ExportTraceCsvCommand.CanExecute(null).Should().BeFalse();

        trace.AppendBatchCore(new[] { Frame(0x100) });
        vm.ExportTraceCsvCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Export_Trace_Csv_Follows_Visible_Rows_Not_The_Buffer()
    {
        // 命令导出的是**可见行**（EntriesView），所以判据也必须看可见行：
        // 过滤把表格清空后再按 Ctrl+E，不该弹出一个没东西可导的保存框。
        var trace = new TraceViewModel();
        trace.AppendBatchCore(new[] { Frame(0x100) });
        var vm = AppShellViewModelTests.NewVm(traceVm: trace);
        vm.SelectedMainTabIndex = 0;

        trace.IdListText = "0x7FF";   // 缓冲区仍有 1 帧，但被过滤掉
        trace.Entries.Should().HaveCount(1);
        trace.EntriesView.Count.Should().Be(0);

        vm.ExportTraceCsvCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Replay_Shortcuts_Are_Gated_To_Replay_Tab()
    {
        var vm = AppShellViewModelTests.NewVm();

        vm.SelectedMainTabIndex = 0;
        vm.ReplayPrevFrameCommand.CanExecute(null).Should().BeFalse();
        vm.ReplayNextFrameCommand.CanExecute(null).Should().BeFalse();
        vm.ReplayAddBookmarkCommand.CanExecute(null).Should().BeFalse();

        // 回放页激活但没加载文件时同样不生效（委托给 ReplayViewModel 自己的 CanExecute）。
        vm.SelectedMainTabIndex = 2;
        vm.ReplayPrevFrameCommand.CanExecute(null).Should().BeFalse();
        vm.ReplayNextFrameCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Recording_Panel_Shortcut_Toggles_Panel()
    {
        var vm = AppShellViewModelTests.NewVm();
        vm.IsRecordingPanelOpen.Should().BeFalse();

        vm.ToggleRecordingPanelCommand.Execute(null);
        vm.IsRecordingPanelOpen.Should().BeTrue();
        vm.ToggleRecordingPanelCommand.Execute(null);
        vm.IsRecordingPanelOpen.Should().BeFalse();
    }

    [Fact]
    public void Tab_Index_Constants_Match_The_Actual_Main_Tabs_Order()
    {
        // 常量与 ctor 里 MainTabs 的构造顺序必须同步：只读 Header，不碰 TabSpec.View，
        // 因此不需要 STA。这条兜住"在 DBC 与回放之间插一个 tab"导致的
        // "菜单写 Ctrl+1 是追踪、按键切到别的页"。
        var vm = AppShellViewModelTests.NewVm();

        vm.MainTabs[AppShellViewModel.TraceTabIndex].Header.Should().Be("追踪");
        vm.MainTabs[AppShellViewModel.DbcTabIndex].Header.Should().Be("DBC");
        vm.MainTabs[AppShellViewModel.ReplayTabIndex].Header.Should().Be("回放");
        vm.MainTabs[AppShellViewModel.ScriptTabIndex].Header.Should().Be("脚本");
        // XCP 是可选追加项：未注入时索引 4 不存在，这本身也是 ShowXcp 的门控前提。
        vm.MainTabs.Count.Should().Be(AppShellViewModel.XcpTabIndex);
    }

    [Fact]
    public void ShowScript_Navigates_To_Script_Tab_And_ShowXcp_Is_Unavailable_Without_Xcp()
    {
        var vm = AppShellViewModelTests.NewVm();

        vm.ShowScriptCommand.Execute(null);
        vm.SelectedMainTabIndex.Should().Be(3, "脚本 tab 一直是第 4 项，此前没有任何菜单/快捷键入口");

        vm.ShowXcpCommand.CanExecute(null).Should().BeFalse("NewVm 不注入 XcpViewModel，MainTabs 只有 4 项");
        vm.ShowXcpCommand.Execute(null);
        vm.SelectedMainTabIndex.Should().Be(3);
    }

    // ---- Ctrl+S 分流 / Ctrl+Shift+S ----
    //
    // 脚本 VM 用**独立的**对话框替身：AppShell 自己也有一份 IFileDialogService（保存会话），
    // 共用替身就分不清是"脚本保存"还是"会话保存"发起的调用。

    private static (AppShellViewModel Shell, ScriptViewModel Script,
                    IFileDialogService ScriptDialog, IFileDialogService ShellDialog)
        NewVmWithScript()
    {
        var scriptDialog = Substitute.For<IFileDialogService>();
        var shellDialog = Substitute.For<IFileDialogService>();
        var script = new ScriptViewModel(
            NullLogger<ScriptViewModel>.Instance,
            new ScriptEngine(NullLogger<ScriptEngine>.Instance, null, null, null),
            scriptDialog);
        return (AppShellViewModelTests.NewVm(scriptVm: script, shellDialogs: shellDialog),
                script,
                scriptDialog,
                shellDialog);
    }

    private static (AppShellViewModel Shell, IFileDialogService ScriptDialog, IFileDialogService ShellDialog)
        NewVmWithScriptDialog()
    {
        var (shell, _, scriptDialog, shellDialog) = NewVmWithScript();
        return (shell, scriptDialog, shellDialog);
    }

    [Fact]
    public void CtrlS_Saves_The_Script_When_The_Script_Tab_Is_Active()
    {
        var (vm, scriptDialog, shellDialog) = NewVmWithScriptDialog();
        vm.SelectedMainTabIndex = AppShellViewModel.ScriptTabIndex;

        vm.SaveCurrentCommand.Execute(null);

        // 没有绑定路径 → 脚本保存先问文件名（同步调用，Execute 返回前已经发生）。
        scriptDialog.Received(1)
            .ShowSaveDialog(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
        shellDialog.DidNotReceiveWithAnyArgs()
            .ShowSaveDialog(default!, default, default);
    }

    [Fact]
    public void CtrlS_Saves_The_Session_When_Another_Tab_Is_Active()
    {
        var (vm, scriptDialog, shellDialog) = NewVmWithScriptDialog();
        vm.SelectedMainTabIndex = AppShellViewModel.TraceTabIndex;

        vm.SaveCurrentCommand.Execute(null);

        // 只断言"没弹脚本框"是不够的：把 else 分支删成空方法，Ctrl+S 在追踪页
        // 会变成毫无反应，而那种写法仍然能通过"没弹脚本框"的断言。
        scriptDialog.DidNotReceiveWithAnyArgs()
            .ShowSaveDialog(default!, default, default);
        shellDialog.Received(1).ShowSaveDialog(
            Arg.Is<string>(f => f.Contains("tmtrace")),
            Arg.Any<string?>(),
            Arg.Any<string?>());
    }

    [Fact]
    public void CtrlShiftS_Outside_The_Script_Tab_Only_Explains_Itself()
    {
        var (vm, scriptDialog, _) = NewVmWithScriptDialog();
        vm.SelectedMainTabIndex = AppShellViewModel.TraceTabIndex;

        vm.SaveScriptAsCommand.Execute(null);

        scriptDialog.DidNotReceiveWithAnyArgs()
            .ShowSaveDialog(default!, default, default);
        vm.StatusMessage.Should().Contain("脚本页", "键按了不能全无反应，否则用户以为程序卡了");
    }

    [Fact]
    public void CtrlShiftS_On_The_Script_Tab_Asks_For_A_Target()
    {
        var (vm, scriptDialog, _) = NewVmWithScriptDialog();
        vm.SelectedMainTabIndex = AppShellViewModel.ScriptTabIndex;

        vm.SaveScriptAsCommand.Execute(null);

        scriptDialog.Received(1)
            .ShowSaveDialog(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    // ---- Ctrl+N 新建脚本 ----

    [Fact]
    public void CtrlN_On_The_Script_Tab_Clears_The_Script()
    {
        var (vm, script, _, _) = NewVmWithScript();
        vm.SelectedMainTabIndex = AppShellViewModel.ScriptTabIndex;
        script.ScriptText = "log('old');";

        vm.NewScriptCommand.Execute(null);

        script.ScriptText.Should().BeEmpty();
    }

    [Fact]
    public void CtrlN_Outside_The_Script_Tab_Only_Explains_Itself()
    {
        // 键被窗口级 KeyBinding 吞了，什么都不做等于"按了没反应"。
        var (vm, script, _, _) = NewVmWithScript();
        vm.SelectedMainTabIndex = AppShellViewModel.TraceTabIndex;
        script.ScriptText = "log('old');";

        vm.NewScriptCommand.Execute(null);

        script.ScriptText.Should().Be("log('old');");
        vm.StatusMessage.Should().Contain("脚本页");
    }
}
