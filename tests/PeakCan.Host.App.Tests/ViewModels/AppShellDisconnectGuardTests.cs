using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Uds;
using PeakCan.Host.Core;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// P1 防事故：断开前的危险任务确认。
/// <para>
/// 契约两条：**没有危险任务就不打扰用户**（快路径不能退化成每次断开都弹窗）；
/// **有危险任务时用户选「否」必须真的不断开**——这里最容易犯的错是弹窗照弹、
/// 返回 false 却已经被忽略，用户以为取消了，连接却断了。
/// </para>
/// </summary>
public sealed class AppShellDisconnectGuardTests
{
    /// <summary>
    /// 建一个已连接的 shell。连接走 FakeChannelProbe/FakeChannelFactory
    /// （与 AppShellViewModelTests 的既有做法一致），否则 DisconnectCommand 的
    /// CanExecute 直接挡在门口，测不到确认逻辑。
    /// </summary>
    private static async Task<AppShellViewModel> NewConnectedVmAsync(
        IMessageBoxPrompt prompt,
        UdsViewModel? udsVm = null,
        RecordViewModel? recordVm = null)
    {
        var vm = AppShellViewModelTests.NewVm(udsVm: udsVm, recordVm: recordVm, prompt: prompt);
        vm.SelectedChannel = new ChannelInfo(0x51, "USB1");
        await vm.ConnectCommand.ExecuteAsync(null);
        vm.IsConnected.Should().BeTrue("前置条件：确认逻辑只在已连接时才有意义");
        return vm;
    }

    private static RecordViewModel NewRecordingVm(out RecordService service, out string path)
    {
        service = new RecordService(NullLogger<RecordService>.Instance);
        path = Path.Combine(Path.GetTempPath(), $"guard-{Guid.NewGuid():N}.asc");
        // 真录制一次：IsRecordingNow 读的是 service 真值，而不是 200ms 轮询的属性。
        service.StartRecording(path, RecordService.RecordFormat.Asc);
        return new RecordViewModel(service, NullLogger<RecordViewModel>.Instance);
    }

    [Fact]
    public async Task Disconnect_Without_Busy_Tasks_Does_Not_Prompt()
    {
        var prompt = Substitute.For<IMessageBoxPrompt>();
        var vm = await NewConnectedVmAsync(prompt);

        await vm.DisconnectCommand.ExecuteAsync(null);

        vm.IsConnected.Should().BeFalse();
        await prompt.DidNotReceiveWithAnyArgs().ShowAsync(default!, default!, default);
    }

    [Fact]
    public async Task Disconnect_During_Flash_User_Cancels_Keeps_Connection()
    {
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(default!, default!, default).ReturnsForAnyArgs(Task.FromResult(System.Windows.MessageBoxResult.No));

        var udsVm = NewUdsVm();
        udsVm.Flash.IsFlashing = true;
        var vm = await NewConnectedVmAsync(prompt, udsVm);

        await vm.DisconnectCommand.ExecuteAsync(null);

        vm.IsConnected.Should().BeTrue("用户选了「否」，连接必须保持");
        vm.StatusMessage.Should().Contain("取消", "要明确告诉用户断开没执行");
        await prompt.Received(1).ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Windows.Window?>());
    }

    [Fact]
    public async Task Disconnect_During_Flash_User_Confirms_Disconnects()
    {
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(default!, default!, default).ReturnsForAnyArgs(Task.FromResult(System.Windows.MessageBoxResult.Yes));

        var udsVm = NewUdsVm();
        udsVm.Flash.IsFlashing = true;
        var vm = await NewConnectedVmAsync(prompt, udsVm);

        await vm.DisconnectCommand.ExecuteAsync(null);

        vm.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task Disconnect_During_Recording_User_Cancels_Keeps_Connection()
    {
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(default!, default!, default).ReturnsForAnyArgs(Task.FromResult(System.Windows.MessageBoxResult.No));

        var recordVm = NewRecordingVm(out var service, out var path);
        try
        {
            var vm = await NewConnectedVmAsync(prompt, recordVm: recordVm);

            await vm.DisconnectCommand.ExecuteAsync(null);

            vm.IsConnected.Should().BeTrue("用户选了「否」，连接必须保持");
        }
        finally
        {
            service.StopRecording();
            recordVm.Dispose();
            TryDelete(path);
        }
    }

    [Fact]
    public async Task Busy_Warning_Lists_Every_Active_Task()
    {
        string? captured = null;
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(default!, default!, default)
            .ReturnsForAnyArgs(ci =>
            {
                captured = ci.ArgAt<string>(1);
                return Task.FromResult(System.Windows.MessageBoxResult.No);
            });

        var udsVm = NewUdsVm();
        udsVm.Flash.IsFlashing = true;
        var recordVm = NewRecordingVm(out var service, out var path);
        try
        {
            var vm = await NewConnectedVmAsync(prompt, udsVm, recordVm);
            await vm.DisconnectCommand.ExecuteAsync(null);
        }
        finally
        {
            service.StopRecording();
            recordVm.Dispose();
            TryDelete(path);
        }

        captured.Should().NotBeNull();
        captured.Should().Contain("刷写", "刷写在跑就必须点名，否则用户不知道风险来自哪");
        captured.Should().Contain("录制");
    }

    [Fact]
    public async Task Disconnect_Aborts_When_A_New_Risk_Appears_While_Confirming()
    {
        // 模态框只锁 owner 窗口，UDS 窗口这时仍可操作——用户完全可能在确认框
        // 前面又点了一次「开始刷写」。确认过的风险放行，**新出现**的必须重新确认，
        // 否则这一屏确认就白点了。
        var udsVm = NewUdsVm();
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(default!, default!, default).ReturnsForAnyArgs(_ =>
        {
            udsVm.Flash.IsFlashing = true; // 模拟"弹窗期间刷写开始了"
            return Task.FromResult(System.Windows.MessageBoxResult.Yes);
        });

        var recordVm = NewRecordingVm(out var service, out var path);
        try
        {
            var vm = await NewConnectedVmAsync(prompt, udsVm, recordVm);

            await vm.DisconnectCommand.ExecuteAsync(null);

            vm.IsConnected.Should().BeTrue("刷写是弹窗之后才开始的，用户没确认过这一项");
            vm.StatusMessage.Should().Contain("状态已变化");
        }
        finally
        {
            service.StopRecording();
            recordVm.Dispose();
            TryDelete(path);
        }
    }

    private static UdsViewModel NewUdsVm()
        => AppShellViewModelTests.NewUdsVmForTests();

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 测试清理失败不影响断言 */ }
    }
}
