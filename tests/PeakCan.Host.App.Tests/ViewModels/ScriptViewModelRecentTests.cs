using System;
using System.IO;
using System.Linq;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.App.Services.Ui;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// 脚本页「最近打开」。会话 MRU 与脚本 MRU 分开——前者点一条是按会话打开，
/// 混在一起会让菜单点出错误的行为。
/// </summary>
public sealed class ScriptViewModelRecentTests : IDisposable
{
    private readonly string _dir;
    private readonly ScriptEngine _engine = new(NullLogger<ScriptEngine>.Instance, null, null, null);

    public ScriptViewModelRecentTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"scriptrecent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private RecentScriptsStore NewStore()
        => new(NullLogger<RecentScriptsStore>.Instance, Path.Combine(_dir, "recent.json"));

    private ScriptViewModel NewVm(IFileDialogService? dialog, RecentScriptsStore? store)
        => new(NullLogger<ScriptViewModel>.Instance, _engine, dialog, prompt: null,
               draftStore: null, recentScripts: store);

    private string NewScriptFile(string content)
    {
        var path = Path.Combine(_dir, $"script-{Guid.NewGuid():N}.js");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public async System.Threading.Tasks.Task Opening_A_File_Puts_It_At_The_Top_Of_The_List()
    {
        var store = NewStore();
        var file = NewScriptFile("log(1);");
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(file);
        var vm = NewVm(dialog, store);

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);

            vm.RecentScripts.Should().ContainSingle();
            vm.RecentScripts[0].Path.Should().Be(file);
            vm.RecentScripts[0].Label.Should().Be(Path.GetFileName(file), "菜单里只显示文件名");
            vm.HasRecentScripts.Should().BeTrue();
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Opening_A_Second_File_Keeps_The_First_One_Below_It()
    {
        var first = NewScriptFile("a");
        var second = NewScriptFile("b");
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(first, second);
        var vm = NewVm(dialog, NewStore());

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);
            await vm.OpenCommand.ExecuteAsync(null);

            vm.RecentScripts.Select(r => r.Path).Should().Equal(second, first);
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Saving_To_A_New_File_Also_Remembers_It()
    {
        var target = Path.Combine(_dir, $"saved-{Guid.NewGuid():N}.js");
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowSaveDialog(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(target);
        var vm = NewVm(dialog, NewStore());

        try
        {
            vm.ScriptText = "log('new');";
            await vm.SaveCommand.ExecuteAsync(null);

            vm.RecentScripts.Should().ContainSingle();
            vm.RecentScripts[0].Path.Should().Be(target);
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async System.Threading.Tasks.Task OpenRecent_Loads_That_File_Without_Asking()
    {
        var store = NewStore();
        var file = NewScriptFile("log('recent');");
        store.Add(file);
        var dialog = Substitute.For<IFileDialogService>();
        var vm = NewVm(dialog, store);
        vm.RefreshRecentScripts();

        try
        {
            await vm.OpenRecentScriptCommand.ExecuteAsync(vm.RecentScripts[0].Path);

            vm.ScriptText.Should().Be("log('recent');");
            vm.CurrentFilePath.Should().Be(file);
            dialog.DidNotReceiveWithAnyArgs().ShowOpenDialog(default!);
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async System.Threading.Tasks.Task OpenRecent_With_A_Deleted_File_Says_So()
    {
        var store = NewStore();
        var missing = Path.Combine(_dir, "gone.js");
        store.Add(missing);
        var vm = NewVm(dialog: null, store);
        vm.RefreshRecentScripts();

        try
        {
            await vm.OpenRecentScriptCommand.ExecuteAsync(missing);

            vm.StatusText.Should().Contain("不存在", "菜单上还留着一个开不了的文件，必须说明原因");
            vm.CurrentFilePath.Should().BeNull();
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async System.Threading.Tasks.Task Open_Failure_Is_Not_Remembered()
    {
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(Path.Combine(_dir, "missing.js"));
        var vm = NewVm(dialog, NewStore());

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);

            vm.RecentScripts.Should().BeEmpty("打不开的文件进列表只会变成一条点不动的死项");
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public void HasRecentScripts_Raises_PropertyChanged_When_The_List_Grows()
    {
        // XAML 的 IsEnabled 完全靠这条通知：少了它，打开第一个文件后菜单仍是灰的。
        var store = NewStore();
        store.Add(@"C:\scripts\a.js");
        var vm = NewVm(dialog: null, store);
        var raised = new System.Collections.Generic.List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        try
        {
            vm.RefreshRecentScripts();

            raised.Should().Contain(nameof(ScriptViewModel.HasRecentScripts));
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public void ClearRecentScripts_Empties_Both_The_List_And_The_Store()
    {
        var store = NewStore();
        store.Add(@"C:\scripts\a.js");
        var vm = NewVm(dialog: null, store);
        vm.RefreshRecentScripts();

        try
        {
            vm.ClearRecentScriptsCommand.Execute(null);

            vm.RecentScripts.Should().BeEmpty();
            vm.HasRecentScripts.Should().BeFalse();
            store.Load().Should().BeEmpty();
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public void Refresh_Loads_What_The_Store_Has()
    {
        var store = NewStore();
        store.Add(@"C:\scripts\a.js");
        var vm = NewVm(dialog: null, store);
        vm.HasRecentScripts.Should().BeFalse();

        vm.RefreshRecentScripts();

        vm.HasRecentScripts.Should().BeTrue();
        vm.RecentScripts[0].Label.Should().Be("a.js");
    }

    [Fact]
    public void Without_A_Store_Nothing_Is_Recorded_And_Nothing_Throws()
    {
        var vm = NewVm(dialog: null, store: null);

        try
        {
            vm.RefreshRecentScripts();

            vm.HasRecentScriptsStore.Should().BeFalse();
            vm.RecentScripts.Should().BeEmpty();
        }
        finally { vm.Dispose(); }
    }
}
