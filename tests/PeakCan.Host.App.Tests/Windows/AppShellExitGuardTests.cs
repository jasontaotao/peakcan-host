using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.Tests.ViewModels;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.Windows;

/// <summary>
/// P1 防事故最后一道门：**退出应用**时若正在刷写，必须让用户明确点头。
/// <para>
/// 与 <see cref="UdsCloseGuardTests"/> 同构：判定抽成静态方法便于直驱，
/// 另有一条真实 <c>Close()</c> 的接线用例把 <c>Closing += OnClosing</c> 钉住
/// ——只测静态方法会让"事件没挂上"这类线接错误全绿。
/// </para>
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public sealed class AppShellExitGuardTests
{
    [Fact]
    public void Exit_While_Idle_Never_Prompts()
    {
        var prompt = Substitute.For<IMessageBoxPrompt>();

        var cancel = AppShell.ShouldCancelExit(prompt, isFlashing: false, hasUnsavedScript: false, owner: null);

        cancel.Should().BeFalse("没在刷写就不该拦退出");
        prompt.DidNotReceiveWithAnyArgs().Confirm(default!, default!, default);
    }

    [Fact]
    public void Exit_While_Flashing_User_Declines_Cancels_Exit()
    {
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.Confirm(default!, default!, default).ReturnsForAnyArgs(false);

        var cancel = AppShell.ShouldCancelExit(prompt, isFlashing: true, hasUnsavedScript: false, owner: null);

        cancel.Should().BeTrue("用户选了「否」，程序不能退出");
        prompt.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>());
    }

    [Fact]
    public void Exit_While_Flashing_User_Confirms_Allows_Exit()
    {
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.Confirm(default!, default!, default).ReturnsForAnyArgs(true);

        var cancel = AppShell.ShouldCancelExit(prompt, isFlashing: true, hasUnsavedScript: false, owner: null);

        cancel.Should().BeFalse();
    }

    [Fact]
    public void Exit_With_Unsaved_Script_Prompts_Even_Without_Flashing()
    {
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.Confirm(default!, default!, default).ReturnsForAnyArgs(false);

        var cancel = AppShell.ShouldCancelExit(
            prompt, isFlashing: false, hasUnsavedScript: true, owner: null);

        cancel.Should().BeTrue("未保存的脚本草稿退出就没了，必须让用户点头");
        prompt.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>());
    }

    [Fact]
    public void Confirm_Message_Lists_Both_Risks()
    {
        string? captured = null;
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.Confirm(default!, default!, default)
            .ReturnsForAnyArgs(ci =>
            {
                captured = ci.ArgAt<string>(1);
                return true;
            });

        AppShell.ShouldCancelExit(prompt, isFlashing: true, hasUnsavedScript: true, owner: null);

        captured.Should().NotBeNull();
        captured.Should().Contain("刷写");
        captured.Should().Contain("未保存", "两个危险源要一次列全，不能只提一个");
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

        AppShell.ShouldCancelExit(prompt, isFlashing: true, hasUnsavedScript: false, owner: null);

        captured.Should().NotBeNull();
        captured.Should().Contain("刷写", "要写明退出会打断刷写");
        captured.Should().Contain("编程会话", "要写明 ECU 会停在什么状态");
    }

    // STA 辅助照抄 AppShellLayoutPersistenceTests：AppShell 是 WPF Window，
    // ctor 要求 STA；XAML 里的 {StaticResource} 令牌要靠 RunWithTokenResources
    // 临时建 Application 并合并生产 Colors.xaml。
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

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
        thread.Join(TimeSpan.FromSeconds(30));
        LeakedApplicationReset.CleanupLeakedApplication();

        if (thread.IsAlive)
            throw new TimeoutException("STA thread did not complete within 30 s (likely a deadlock in the exit guard).");
        if (caught is not null) throw caught;
    }

    /// <summary>
    /// 真窗口接线：刷写中点 ✕ → 弹确认；选「否」窗口还在；刷写结束后再关就放行，
    /// 且整个过程只问一次。
    /// </summary>
    [Fact]
    public void Real_Close_While_Flashing_Is_Cancelled_When_User_Declines()
    {
        RunSta(() =>
        {
            var udsVm = AppShellViewModelTests.NewUdsVmForTests();
            udsVm.Flash.IsFlashing = true;
            var prompt = Substitute.For<IMessageBoxPrompt>();
            prompt.Confirm(default!, default!, default).ReturnsForAnyArgs(false);

            var shell = new AppShell
            {
                DataContext = AppShellViewModelTests.NewVm(udsVm: udsVm, prompt: prompt),
                Prompt = prompt,
                WindowStateStore = null,
                LayoutStateStore = null,
            };
            Application.Current!.MainWindow = shell;
            shell.Show();
            Pump();

            shell.Close();
            Pump();

            shell.IsVisible.Should().BeTrue("用户选了「否」，窗口必须还在");
            prompt.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>());

            // 刷写结束后再关：这次不该再问，而且必须真的关掉。
            udsVm.Flash.IsFlashing = false;
            shell.Close();
            Pump();

            shell.IsVisible.Should().BeFalse("刷写已结束，关窗应当放行");
            prompt.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>());
        });
    }

    /// <summary>
    /// 真实关窗的第二条危险分支：脚本页有未保存改动。
    /// 只测静态方法会让"DataContext → HasUnsavedScript 这条线没接上"全绿。
    /// </summary>
    [Fact]
    public void Real_Close_With_Unsaved_Script_Is_Cancelled_When_User_Declines()
    {
        RunSta(() =>
        {
            var engine = new ScriptEngine(NullLogger<ScriptEngine>.Instance, null, null, null);
            var scriptVm = new ScriptViewModel(NullLogger<ScriptViewModel>.Instance, engine);
            try
            {
                scriptVm.ScriptText = "unsaved draft";
                var prompt = Substitute.For<IMessageBoxPrompt>();
                prompt.Confirm(default!, default!, default).ReturnsForAnyArgs(false);

                var shell = new AppShell
                {
                    DataContext = AppShellViewModelTests.NewVm(scriptVm: scriptVm, prompt: prompt),
                    Prompt = prompt,
                    WindowStateStore = null,
                    LayoutStateStore = null,
                };
                Application.Current!.MainWindow = shell;
                shell.Show();
                Pump();

                shell.Close();
                Pump();

                shell.IsVisible.Should().BeTrue("脚本有未保存改动，用户选了「否」，窗口必须还在");
                prompt.Received(1).Confirm(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>());

                // 草稿与"磁盘内容"一致（初始都是空串）→ 不再算脏。
                scriptVm.ScriptText = "";
                shell.Close();
                Pump();

                shell.IsVisible.Should().BeFalse("没有未保存内容，关窗应当放行");
            }
            finally
            {
                scriptVm.Dispose();
                engine.Dispose();
            }
        });
    }
}
