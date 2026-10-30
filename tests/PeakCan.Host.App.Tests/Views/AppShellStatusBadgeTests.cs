using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using PeakCan.Host.App.Tests.ViewModels;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.App.ViewModels.Uds;
using Xunit;

namespace PeakCan.Host.App.Tests.Views;

/// <summary>
/// 主窗口状态栏的诊断会话徽章（P2：会话状态徽章带）。
/// <para>
/// 徽章的意义是"关掉 UDS 窗口也能看见 ECU 当前会话 / 安全等级 / 保活 / 刷写"——
/// 所以绑定路径必须真实存在：WPF 绑定失败是静默的，路径写错时徽章只会一直空白。
/// </para>
/// </summary>
public sealed class AppShellStatusBadgeTests
{
    private static string RepoFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PeakCan.Host.slnx")))
            dir = dir.Parent;

        if (dir is null)
            throw new FileNotFoundException(
                $"找不到 PeakCan.Host.slnx —— 测试是从非仓库目录运行的？起始目录: {AppContext.BaseDirectory}");

        return Path.Combine(new[] { dir.FullName }.Concat(relative).ToArray());
    }

    private static string ShellXaml()
        => File.ReadAllText(RepoFile("src", "PeakCan.Host.App", "AppShell.xaml"));

    [Theory]
    [InlineData("{Binding Uds.Session.CurrentSession")]
    [InlineData("{Binding Uds.Session.SecurityLevelText")]
    [InlineData("{Binding Uds.Session.TesterPresentActive")]
    [InlineData("{Binding Uds.Flash.IsFlashing")]
    public void Status_Bar_Binds_Each_Diagnostic_State(string binding)
    {
        // 缺了这条绑定，徽章就只剩空白（WPF 绑定失败是静默的）。
        // 用 xUnit 的 Assert 而不是 FluentAssertions：后者的 because 参数会被
        // CA2241 当成格式串误报。
        Assert.Contains(binding, ShellXaml(), StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_Roots_Exist_On_The_ViewModel_Chain()
    {
        var shell = typeof(AppShellViewModel);
        var udsProp = shell.GetProperty(nameof(AppShellViewModel.Uds), BindingFlags.Public | BindingFlags.Instance);
        udsProp.Should().NotBeNull("状态栏经 Uds.* 绑定路径访问诊断 VM");
        udsProp!.PropertyType.Should().Be<UdsViewModel>();

        typeof(UdsViewModel).GetProperty("Session", BindingFlags.Public | BindingFlags.Instance)
            .Should().NotBeNull();
        typeof(UdsViewModel).GetProperty("Flash", BindingFlags.Public | BindingFlags.Instance)
            .Should().NotBeNull();
        typeof(SessionPanelViewModel).GetProperty(nameof(SessionPanelViewModel.SecurityLevelText),
                BindingFlags.Public | BindingFlags.Instance)
            .Should().NotBeNull();

        // 叶子属性也要校验：绑定路径写错是静默失败，徽章只会一直空白。
        typeof(SessionPanelViewModel).GetProperty(nameof(SessionPanelViewModel.CurrentSession),
                BindingFlags.Public | BindingFlags.Instance).Should().NotBeNull();
        typeof(SessionPanelViewModel).GetProperty(nameof(SessionPanelViewModel.TesterPresentActive),
                BindingFlags.Public | BindingFlags.Instance).Should().NotBeNull();
        typeof(PeakCan.Host.App.ViewModels.Uds.FlashPipeline.FlashPanelViewModel)
            .GetProperty("IsFlashing", BindingFlags.Public | BindingFlags.Instance)
            .Should().NotBeNull();
    }

    [Fact]
    public void Security_Level_Text_Projects_The_Granted_Level()
    {
        var session = AppShellViewModelTests.NewUdsVmForTests().Session;

        session.GrantedSecurityLevel = null;
        session.SecurityLevelText.Should().Be("未解锁");

        session.GrantedSecurityLevel = 0x03;
        session.SecurityLevelText.Should().Be("等级 3");
    }

    [Fact]
    public void Typing_A_Request_Level_Does_Not_Claim_The_Ecu_Is_Unlocked()
    {
        // 等级输入框 TwoWay 绑的是 SecurityLevel（请求值）。用户敲了没点
        // SecurityAccess 时 ECU 根本没解锁——徽章必须仍然是"未解锁"。
        var session = AppShellViewModelTests.NewUdsVmForTests().Session;
        session.GrantedSecurityLevel = null;

        session.SecurityLevel = 0x0B;

        session.SecurityLevelText.Should().Be("未解锁",
            "请求值不是已授予等级；谎报已解锁会让操作者跳过解锁，后续写操作 NRC 0x33");
    }

    [Fact]
    public void Security_Level_Text_Raises_PropertyChanged()
    {
        var session = AppShellViewModelTests.NewUdsVmForTests().Session;
        var raised = new System.Collections.Generic.List<string>();
        session.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        session.GrantedSecurityLevel = 0x01;

        raised.Should().Contain(nameof(SessionPanelViewModel.SecurityLevelText),
            "徽章绑的是 SecurityLevelText，不通知就会停在「未解锁」");
    }
}
