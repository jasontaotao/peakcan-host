using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// 主标签的「有未保存改动」圆点。接线只有一处（AppShellViewModel 订阅脚本 VM 的
/// PropertyChanged），但订阅漏了、或者订阅了错的属性名，界面上是彻底看不出来的
/// ——所以三个方向都要钉：点亮、熄灭、别的标签永远不亮。
/// </summary>
public sealed class AppShellTabDirtyMarkerTests : IDisposable
{
    private readonly string _dir;

    public AppShellTabDirtyMarkerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tabdirty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 清理失败不影响断言 */ }
    }

    private static (AppShellViewModel Shell, ScriptViewModel Script, IFileDialogService Dialog) NewVm()
    {
        var dialog = Substitute.For<IFileDialogService>();
        var script = new ScriptViewModel(
            NullLogger<ScriptViewModel>.Instance,
            new ScriptEngine(NullLogger<ScriptEngine>.Instance, null, null, null),
            dialog);
        return (AppShellViewModelTests.NewVm(scriptVm: script), script, dialog);
    }

    [Fact]
    public void Editing_The_Script_Lights_The_Script_Tab()
    {
        var (vm, script, _) = NewVm();
        var tab = vm.MainTabs[AppShellViewModel.ScriptTabIndex];
        tab.IsDirty.Should().BeFalse("启动时草稿是干净的");

        script.ScriptText = "log('edited');";

        tab.IsDirty.Should().BeTrue("改了内容却没有任何提示，用户不知道保存过没有");
    }

    [Fact]
    public async Task Saving_Clears_The_Dot()
    {
        var (vm, script, dialog) = NewVm();
        var target = Path.Combine(_dir, "saved.js");
        dialog.ShowSaveDialog(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
              .Returns(target);
        script.ScriptText = "log('edited');";
        var tab = vm.MainTabs[AppShellViewModel.ScriptTabIndex];

        await script.SaveCommand.ExecuteAsync(null);

        tab.IsDirty.Should().BeFalse("保存之后磁盘和编辑器一致，圆点必须熄灭");
    }

    [Fact]
    public void New_Clears_The_Dot()
    {
        var (vm, script, _) = NewVm();
        script.ScriptText = "log('edited');";
        var tab = vm.MainTabs[AppShellViewModel.ScriptTabIndex];
        // Ctrl+N 只在脚本页生效，否则走的是"只提示"的分支。
        vm.SelectedMainTabIndex = AppShellViewModel.ScriptTabIndex;

        vm.NewScriptCommand.Execute(null);

        tab.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Other_Tabs_Never_Show_The_Dot()
    {
        var (vm, script, _) = NewVm();

        script.ScriptText = "log('edited');";

        for (var i = 0; i < vm.MainTabs.Count; i++)
        {
            if (i == AppShellViewModel.ScriptTabIndex) continue;
            vm.MainTabs[i].IsDirty.Should().BeFalse($"「{vm.MainTabs[i].Header}」没有可编辑内容");
        }
    }

    [Fact]
    public void The_Tab_Raises_PropertyChanged_So_The_Template_Updates()
    {
        // TabSpec.IsDirty 若只是个普通属性，圆点会永远停在初始状态。
        var (vm, script, _) = NewVm();
        var tab = vm.MainTabs[AppShellViewModel.ScriptTabIndex];
        var raised = new System.Collections.Generic.List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        script.ScriptText = "log('edited');";

        raised.Should().Contain(nameof(TabSpec.IsDirty));
    }
}
