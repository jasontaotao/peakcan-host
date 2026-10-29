using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// 脚本页的「打开 / 保存」——这两个按钮曾经绑在不存在的命令上（点了没反应）。
/// 除了文件读写，还必须钉住 **C#→编辑器的回灌请求**：只把文本写进
/// <see cref="ScriptViewModel.ScriptText"/> 而不推回 WebView2，用户下一次键入
/// 会用编辑器里的旧内容把刚打开的文件覆盖掉。
/// </summary>
public sealed class ScriptViewModelFileTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly ScriptEngine _engine =
        new(NullLogger<ScriptEngine>.Instance, null, null, null);

    static ScriptViewModelFileTests()
    {
        // 与 App.OnStartup 一样：GBK/CP932 一类代码页要先注册提供程序。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public void Dispose()
    {
        _engine.Dispose();
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch (IOException) { /* 清理失败不影响断言 */ }
        }
    }

    private string NewTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"script-{Guid.NewGuid():N}.js");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    private ScriptViewModel NewVm(IFileDialogService? dialog = null, IMessageBoxPrompt? prompt = null)
        => new(NullLogger<ScriptViewModel>.Instance, _engine, dialog, prompt);

    [Fact]
    public async Task Open_Loads_File_Into_Editor_And_Requests_Replacement()
    {
        const string content = "log('from file');";
        var path = NewTempFile(content);
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(path);
        var vm = NewVm(dialog);
        var pushed = new List<string>();
        vm.EditorTextReplaceRequested += pushed.Add;

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);

            vm.ScriptText.Should().Be(content);
            vm.CurrentFilePath.Should().Be(path);
            vm.StatusText.Should().Contain(Path.GetFileName(path));
            vm.HasUnsavedChanges.Should().BeFalse("刚打开的内容就是磁盘内容");
            pushed.Should().ContainSingle("编辑器必须被要求整段替换，否则下一次键入会覆盖刚打开的文件")
                  .Which.Should().Be(content);
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task Open_Cancelled_Keeps_Current_Text()
    {
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns((string?)null);
        var vm = NewVm(dialog);
        vm.ScriptText = "draft";

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);

            vm.ScriptText.Should().Be("draft");
            vm.StatusText.Should().Be("已取消打开");
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task Open_With_Unsaved_Changes_Is_Cancelled_When_User_Declines()
    {
        var path = NewTempFile("from file");
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(path);
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
              .Returns(Task.FromResult(MessageBoxResult.No));
        var vm = NewVm(dialog, prompt);
        vm.ScriptText = "unsaved draft";

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);

            vm.ScriptText.Should().Be("unsaved draft", "用户选了「否」，草稿不能被丢弃");
            vm.CurrentFilePath.Should().BeNull();
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task Open_With_Unsaved_Changes_Proceeds_When_User_Confirms()
    {
        var path = NewTempFile("from file");
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(path);
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
              .Returns(Task.FromResult(MessageBoxResult.Yes));
        var vm = NewVm(dialog, prompt);
        vm.ScriptText = "unsaved draft";

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);

            vm.ScriptText.Should().Be("from file");
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task Save_Writes_To_The_Opened_Path_Without_Asking()
    {
        var path = NewTempFile("old");
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(path);
        var vm = NewVm(dialog);

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);
            vm.ScriptText = "new content";
            vm.HasUnsavedChanges.Should().BeTrue();

            await vm.SaveCommand.ExecuteAsync(null);

            File.ReadAllText(path).Should().Be("new content");
            vm.HasUnsavedChanges.Should().BeFalse();
            dialog.DidNotReceiveWithAnyArgs()
                  .ShowSaveDialog(default!, default, default);
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task Open_Reads_Gbk_And_Saves_Back_In_Gbk()
    {
        // 中文注释的 .js 很常见是 GBK。一路按 UTF-8 读会静默变成 U+FFFD，
        // 用户改一行再保存就把原文件的非 ASCII 内容不可逆地写坏。
        var gbk = Encoding.GetEncoding(936);
        const string original = "// 中文注释\nlog('x');";
        var path = Path.Combine(Path.GetTempPath(), $"script-{Guid.NewGuid():N}.js");
        _tempFiles.Add(path);
        File.WriteAllBytes(path, gbk.GetBytes(original));

        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(path);
        var vm = NewVm(dialog);

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);

            vm.ScriptText.Should().Be(original, "GBK 文件必须按系统 OEM 代码页解码，不能变成替换字符");

            vm.ScriptText = "// 改过的注释\nlog('y');";
            await vm.SaveCommand.ExecuteAsync(null);

            gbk.GetString(File.ReadAllBytes(path))
               .Should().Be("// 改过的注释\nlog('y');", "保存要按源文件编码写回");
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task Open_Normalizes_Crlf_And_Save_Restores_Crlf()
    {
        // textarea 取值会把 CRLF 归一成 LF：两侧口径不一致会让"没改过"也算脏，
        // 一保存整份文件的行尾就全变了。
        var path = Path.Combine(Path.GetTempPath(), $"script-{Guid.NewGuid():N}.js");
        _tempFiles.Add(path);
        File.WriteAllText(path, "a\r\nb");
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(path);
        var vm = NewVm(dialog);

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);

            vm.ScriptText.Should().Be("a\nb");
            vm.HasUnsavedChanges.Should().BeFalse("行尾归一不是用户改的，不能算作未保存改动");

            await vm.SaveCommand.ExecuteAsync(null);

            File.ReadAllText(path).Should().Be("a\r\nb", "保存要还原源文件的换行风格");
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task Save_Raises_HasUnsavedChanges_Notification()
    {
        // 脏状态是给工具栏/退出确认读的；改完 _savedText 不发通知，界面会一直显示旧状态。
        var path = NewTempFile("old");
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(path);
        var vm = NewVm(dialog);

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);
            vm.ScriptText = "changed";
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

            await vm.SaveCommand.ExecuteAsync(null);

            raised.Should().Contain(nameof(ScriptViewModel.HasUnsavedChanges));
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task Save_Without_A_Path_Asks_For_One()
    {
        var path = Path.Combine(Path.GetTempPath(), $"script-{Guid.NewGuid():N}.js");
        _tempFiles.Add(path);
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowSaveDialog(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
              .Returns(path);
        var vm = NewVm(dialog);
        vm.ScriptText = "brand new";

        try
        {
            await vm.SaveCommand.ExecuteAsync(null);

            File.ReadAllText(path).Should().Be("brand new");
            vm.CurrentFilePath.Should().Be(path);
            vm.HasUnsavedChanges.Should().BeFalse();
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task Save_Cancelled_Reports_It_Instead_Of_Doing_Nothing()
    {
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowSaveDialog(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
              .Returns((string?)null);
        var vm = NewVm(dialog);
        vm.ScriptText = "draft";

        try
        {
            await vm.SaveCommand.ExecuteAsync(null);

            vm.StatusText.Should().Be("已取消保存");
            vm.HasUnsavedChanges.Should().BeTrue("没保存成功，草稿仍是未保存状态");
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task Open_Failure_Is_Visible_In_Status()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.js");
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(missing);
        var vm = NewVm(dialog);

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);

            vm.StatusText.Should().StartWith("打开失败", "读不到文件必须让用户看见，不能静默");
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task Missing_File_Dialog_Is_Reported_Not_Silent()
    {
        // 生产 DI 必注入对话框；万一装配漏了，按钮也不能一声不响。
        var vm = NewVm();

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);

            vm.StatusText.Should().Be("文件对话框不可用");
        }
        finally { vm.Dispose(); }
    }
}
