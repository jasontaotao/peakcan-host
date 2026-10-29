using FluentAssertions;
using NSubstitute;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.Windows;
using Xunit;

namespace PeakCan.Host.App.Tests.Windows;

/// <summary>
/// P1 防事故：刷写中关闭 UDS 诊断窗口必须确认。
/// <para>
/// 只测判定本体 <see cref="UdsWindow.ShouldCancelClose"/>（静态、不碰 WPF 可视树），
/// 窗口事件接线靠 XAML/代码审查覆盖——`Closing` 里能 await 的写法会死锁，
/// 所以这条链刻意是同步的。
/// </para>
/// </summary>
public sealed class UdsCloseGuardTests
{
    [Fact]
    public void Close_While_Idle_Never_Prompts()
    {
        var prompt = Substitute.For<IMessageBoxPrompt>();

        var cancel = UdsWindow.ShouldCancelClose(prompt, isFlashing: false, owner: null);

        cancel.Should().BeFalse("没在刷写就不该打扰用户");
        prompt.DidNotReceiveWithAnyArgs().Confirm(default!, default!, default);
    }

    [Fact]
    public void Close_While_Flashing_User_Says_No_Cancels_Close()
    {
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.Confirm(default!, default!, default).ReturnsForAnyArgs(false);

        var cancel = UdsWindow.ShouldCancelClose(prompt, isFlashing: true, owner: null);

        cancel.Should().BeTrue("用户选了「否」，窗口必须关不掉");
        prompt.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<System.Windows.Window?>());
    }

    [Fact]
    public void Close_While_Flashing_User_Confirms_Allows_Close()
    {
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.Confirm(default!, default!, default).ReturnsForAnyArgs(true);

        var cancel = UdsWindow.ShouldCancelClose(prompt, isFlashing: true, owner: null);

        cancel.Should().BeFalse();
    }

    [Fact]
    public void Skip_Close_Confirm_Is_False_Without_A_Closing_Shell()
    {
        // 没有 owner（独立窗口 / 测试）时必须照常确认——跳过逻辑只认"宿主正在关"。
        UdsWindow.ShouldSkipCloseConfirm(owner: null).Should().BeFalse();
    }

    [Fact]
    public void Confirm_Message_Names_The_Consequence()
    {
        string? captured = null;
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.Confirm(default!, default!, default)
            .ReturnsForAnyArgs(ci =>
            {
                captured = ci.ArgAt<string>(1);
                return true;
            });

        UdsWindow.ShouldCancelClose(prompt, isFlashing: true, owner: null);

        captured.Should().NotBeNull();
        captured.Should().Contain("中止", "要写明关窗会中止刷写");
        captured.Should().Contain("编程会话", "要写明 ECU 会停在什么状态");
    }
}
