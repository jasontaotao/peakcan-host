using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using FluentAssertions;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// P2 显示偏好：主标签可见子集 + 表格密度。
/// <para>
/// 隐藏主标签刻意保留**绝对索引**（TabSpec.IsVisible 折叠），所以这里的用例
/// 钉的是三件事：选中项不能停在隐藏页上、最后一个标签隐藏不掉、持久化按 key 走。
/// </para>
/// </summary>
[Collection(WpfAppTestCollection.Name)]
public sealed class AppShellViewLayoutTests
{
    private static AppShellViewModel NewVm() => AppShellViewModelTests.NewVm();

    [Fact]
    public void Hiding_The_Current_Tab_Moves_Selection_To_The_First_Visible()
    {
        var vm = NewVm();
        vm.SelectedMainTabIndex = 0;

        vm.ShowTraceTab = false;

        vm.SelectedMainTabIndex.Should().Be(1, "当前页被藏掉后要落到第一个可见页，否则内容区停在无标签头的页上");
        vm.MainTabs[0].IsVisible.Should().BeFalse();
    }

    [Fact]
    public void Hiding_A_Non_Current_Tab_Keeps_Selection()
    {
        var vm = NewVm();
        vm.SelectedMainTabIndex = 0;

        vm.ShowReplayTab = false;

        vm.SelectedMainTabIndex.Should().Be(0);
        vm.MainTabs[2].IsVisible.Should().BeFalse();
    }

    [Fact]
    public void Cannot_Hide_The_Last_Visible_Main_Tab()
    {
        var vm = NewVm();
        vm.ShowDbcTab = false;
        vm.ShowReplayTab = false;
        vm.ShowScriptTab = false;

        vm.ShowTraceTab = false;

        vm.ShowTraceTab.Should().BeTrue("至少留一个标签，否则标签栏空掉且无从恢复");
        vm.MainTabs[0].IsVisible.Should().BeTrue();
        vm.StatusMessage.Should().Contain("至少");
    }

    [Fact]
    public void Navigating_To_A_Hidden_Tab_Is_Refused_With_A_Message()
    {
        var vm = NewVm();
        vm.ShowScriptTab = false;
        vm.SelectedMainTabIndex = 0;

        // 菜单与快捷键共用 ShowScriptCommand。
        vm.ShowScriptCommand.Execute(null);

        vm.SelectedMainTabIndex.Should().Be(0);
        vm.MainTabs[3].IsVisible.Should().BeFalse();
        vm.StatusMessage.Should().Contain("已隐藏");
    }

    [Fact]
    public void Out_Of_Range_Selected_Index_Falls_Back_To_The_First_Visible()
    {
        // layout.json 被手改 / 从 5 标签版本降级：非法 SelectedIndex 不会抛，
        // 但主区域会空白，而且退出时坏值又被写回去。
        var vm = NewVm();

        vm.SelectedMainTabIndex = 99;

        vm.SelectedMainTabIndex.Should().Be(0);
    }

    [Fact]
    public void Persisted_Subset_Round_Trips_By_Key()
    {
        var vm = NewVm();
        vm.ShowScriptTab = false;
        var persisted = vm.PersistedVisibleMainTabs();
        persisted.Should().Be("TDR", "持久化按 key 拼串，不能用索引");

        var restored = NewVm();
        restored.RestoreVisibleMainTabs(persisted);

        restored.ShowScriptTab.Should().BeFalse();
        restored.MainTabs[3].IsVisible.Should().BeFalse();
        restored.ShowTraceTab.Should().BeTrue();
        restored.ShowDbcTab.Should().BeTrue();
        restored.ShowReplayTab.Should().BeTrue();
    }

    [Fact]
    public void All_Visible_Persists_As_Null()
    {
        NewVm().PersistedVisibleMainTabs().Should().BeNull("全显示是默认值，旧配置缺字段同样表示全显示");
    }

    [Fact]
    public void Unknown_Keys_Are_Ignored_Instead_Of_Hiding_Everything()
    {
        var vm = NewVm();

        vm.RestoreVisibleMainTabs("Z");

        vm.MainTabs.Should().OnlyContain(t => t.IsVisible, "无法识别的配置要退回全显示，不能把标签藏光");
    }

    [Fact]
    public void Compact_Tables_Toggle_Updates_The_Global_Row_Height()
    {
        RunSta(() =>
        {
            var vm = NewVm();

            vm.IsCompactTables = true;
            ((double)Application.Current!.Resources["DataGridRowHeight"]).Should().Be(18, "所有 DataGrid 都吃同一个动态资源");

            vm.IsCompactTables = false;
            ((double)Application.Current!.Resources["DataGridRowHeight"]).Should().Be(22);
        });
    }

    [Fact]
    public void Shell_Xaml_Wires_The_Preferences()
    {
        var xaml = File.ReadAllText(RepoFile("src", "PeakCan.Host.App", "AppShell.xaml"));

        foreach (var prop in new[]
                 {
                     nameof(AppShellViewModel.ShowTraceTab),
                     nameof(AppShellViewModel.ShowDbcTab),
                     nameof(AppShellViewModel.ShowReplayTab),
                     nameof(AppShellViewModel.ShowScriptTab),
                     nameof(AppShellViewModel.ShowXcpTab),
                     nameof(AppShellViewModel.IsCompactTables),
                 })
        {
            xaml.Should().Contain($"{{Binding {prop}, Mode=TwoWay}}", $"{prop} 没有菜单入口（绑定失败是静默的）");
        }

        xaml.Should().Contain("{Binding IsVisible, Converter={StaticResource BoolToVis}}",
            "隐藏的主标签要靠 ItemContainerStyle 折叠");
    }

    [Fact]
    public void Preference_Properties_Are_Real_On_The_ViewModel()
    {
        var type = typeof(AppShellViewModel);
        foreach (var name in new[] { "ShowTraceTab", "ShowDbcTab", "ShowReplayTab", "ShowScriptTab", "ShowXcpTab", "IsCompactTables" })
        {
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            prop.Should().NotBeNull($"{name} 不存在，菜单绑定会静默失败");
            prop!.PropertyType.Should().Be<bool>();
        }
    }

    // ---- STA helpers（照 AppShellLayoutPersistenceTests）----

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
        if (thread.IsAlive) throw new TimeoutException("STA thread did not complete within 30 s.");
        if (caught is not null) throw caught;
    }

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
}
