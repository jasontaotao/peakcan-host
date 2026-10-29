using FluentAssertions;
using PeakCan.Host.App.Tests.ViewModels;
using PeakCan.Host.App.ViewModels.Uds;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels.Uds;

/// <summary>
/// P1 防事故：刷写期间锁诊断面板。
/// <para>
/// 两件事必须钉住：(1) <see cref="UdsViewModel.IsDiagnosticsEnabled"/> 跟着
/// <c>Flash.IsFlashing</c> 双向翻转并发出通知（否则 XAML 上的 IsEnabled 永远停在初值）；
/// (2) 已经在跑的 TesterPresent 保活循环会被停掉——它每 2 秒往诊断 CAN-ID 发
/// <c>3E 00</c>，刷写期间这是实打实的抢会话，不是"按钮点不动"就能解决的。
/// </para>
/// <para>
/// TesterPresent 用直接置 <see cref="SessionPanelViewModel.TesterPresentActive"/> 的方式
/// 模拟"循环正在跑"：真实循环会走 UdsClient 发帧，在没有通道的测试环境里必然立刻抛错
/// 自杀，那会让断言变成测传输层而不是测本逻辑。
/// </para>
/// </summary>
public sealed class UdsDiagnosticsLockTests
{
    [Fact]
    public void Flash_Start_Locks_Diagnostics_Panels()
    {
        var vm = AppShellViewModelTests.NewUdsVmForTests();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Flash.IsFlashing = true;

        vm.IsDiagnosticsEnabled.Should().BeFalse();
        changed.Should().Contain(nameof(UdsViewModel.IsDiagnosticsEnabled),
            "XAML 的 IsEnabled 只认属性通知，不发通知界面就停在旧值");
    }

    [Fact]
    public void Flash_End_Unlocks_Diagnostics_Panels()
    {
        var vm = AppShellViewModelTests.NewUdsVmForTests();
        vm.Flash.IsFlashing = true;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Flash.IsFlashing = false;

        vm.IsDiagnosticsEnabled.Should().BeTrue("刷写结束后面板要自动恢复，不能让用户重开窗口");
        changed.Should().Contain(nameof(UdsViewModel.IsDiagnosticsEnabled),
            "解锁方向同样要发通知——否则绑定停在 true，面板永久锁死");
    }

    [Fact]
    public void Flash_Start_Stops_A_Running_TesterPresent_Loop()
    {
        var vm = AppShellViewModelTests.NewUdsVmForTests();
        vm.Session.TesterPresentActive = true;

        vm.Flash.IsFlashing = true;

        vm.Session.TesterPresentActive.Should().BeFalse();
        vm.OutputLog.Should().Contain(
            l => l.Level == "Warn" && l.Message.Contains("TesterPresent"),
            "自动停掉就必须留下痕迹，否则用户会以为保活还在跑");
    }

    [Fact]
    public void Flash_End_Does_Not_Restart_TesterPresent()
    {
        // 刻意不自动恢复：刷写收尾常跟 ECU 复位，这时注入 3E 00 会打断收尾流程。
        var vm = AppShellViewModelTests.NewUdsVmForTests();
        vm.Session.TesterPresentActive = true;
        vm.Flash.IsFlashing = true;

        vm.Flash.IsFlashing = false;

        vm.Session.TesterPresentActive.Should().BeFalse();
    }

    [Fact]
    public void Suspend_For_Flash_Without_Running_Loop_Is_NoOp()
    {
        var vm = AppShellViewModelTests.NewUdsVmForTests();
        var before = vm.OutputLog.Count;

        vm.Session.SuspendForFlash();

        vm.Session.TesterPresentActive.Should().BeFalse();
        vm.OutputLog.Count.Should().Be(before, "没在跑就不该写日志——日志里每一行都得对应真实发生的事");
    }
}
