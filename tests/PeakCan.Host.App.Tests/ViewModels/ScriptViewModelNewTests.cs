using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.HIL.Core;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.Services.Ui;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// 脚本页「新建」。它和"全选删除"的区别全在**路径是否解绑**：只清文本的话，
/// 下一次 Ctrl+S 会把空内容写回原来那个真实存在的 .js 文件。
/// </summary>
public sealed class ScriptViewModelNewTests : IDisposable
{
    private readonly string _dir;
    private readonly ScriptEngine _engine =
        new(NullLogger<ScriptEngine>.Instance, null, null, null);

    public ScriptViewModelNewTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"scriptnew-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* 清理失败不影响断言 */ }
    }

    private ScriptViewModel NewVm(
        IFileDialogService? dialog = null,
        IMessageBoxPrompt? prompt = null,
        ScriptDraftStore? drafts = null)
        => new(NullLogger<ScriptViewModel>.Instance, _engine, dialog, prompt, drafts);

    private string NewScriptFile(string content)
    {
        var path = Path.Combine(_dir, $"script-{Guid.NewGuid():N}.js");
        File.WriteAllText(path, content);
        return path;
    }

    private ScriptDraftStore NewDraftStore()
        => new(NullLogger<ScriptDraftStore>.Instance, Path.Combine(_dir, "draft.json"));

    /// <summary>打开一个磁盘上已存在的脚本，返回 VM / 路径 / 那个对话框替身。</summary>
    private async Task<(ScriptViewModel Vm, string Path, IFileDialogService Dialog)>
        OpenedVmAsync(ScriptDraftStore? drafts = null)
    {
        var path = NewScriptFile("log('from file');");
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(path);
        var vm = NewVm(dialog, drafts: drafts);
        await vm.OpenCommand.ExecuteAsync(null);
        return (vm, path, dialog);
    }

    [Fact]
    public async Task New_Clears_The_Editor_And_Unbinds_The_File()
    {
        var (vm, _, _) = await OpenedVmAsync();

        try
        {
            await vm.NewCommand.ExecuteAsync(null);

            vm.ScriptText.Should().BeEmpty();
            vm.CurrentFilePath.Should().BeNull("不解绑路径，下一次保存会写回原文件");
            vm.HasUnsavedChanges.Should().BeFalse("空白就是新建后的磁盘状态");
            vm.StatusText.Should().Contain("新建");
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task New_Makes_The_Next_Save_Ask_For_A_Path_And_Leaves_The_Old_File_Alone()
    {
        var (vm, path, dialog) = await OpenedVmAsync();

        try
        {
            await vm.NewCommand.ExecuteAsync(null);
            // 保存框被取消 → 什么都不写。
            dialog.ShowSaveDialog(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
                  .Returns((string?)null);

            await vm.SaveCommand.ExecuteAsync(null);

            dialog.Received(1).ShowSaveDialog(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
            File.ReadAllText(path).Should().Be("log('from file');",
                "新建后的第一次保存绝不能覆盖原来那个文件");
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task New_Asks_Before_Discarding_Unsaved_Changes()
    {
        var path = NewScriptFile("log('on disk');");
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(path);
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
              .Returns(Task.FromResult(MessageBoxResult.No));
        var vm = NewVm(dialog, prompt);

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);
            vm.ScriptText = "log('edited');";

            await vm.NewCommand.ExecuteAsync(null);

            vm.ScriptText.Should().Be("log('edited');", "用户选了「否」，内容必须原样留着");
            vm.CurrentFilePath.Should().Be(path);
            vm.StatusText.Should().Contain("取消", "按了没反应会让用户以为程序卡了");
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task New_Proceeds_When_The_User_Confirms()
    {
        // 取消路径已有两条用例，确认路径一条都没有：把 Yes 误写成 OK 时，
        // 取消用例（返回 No）照样绿，而生产环境里「是」永远进不去。
        var path = NewScriptFile("log('on disk');");
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(path);
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
              .Returns(Task.FromResult(MessageBoxResult.Yes));
        var vm = NewVm(dialog, prompt);

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);
            vm.ScriptText = "log('edited');";

            await vm.NewCommand.ExecuteAsync(null);

            vm.ScriptText.Should().BeEmpty("用户点了「是」，新建必须真的发生");
            vm.CurrentFilePath.Should().BeNull();
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task New_Then_Save_Writes_Utf8_Without_Bom_And_With_Lf()
    {
        // 这条是"新建会重建编码 / BOM / 行尾基线"的唯一证明：拿一个带 BOM + CRLF
        // 的文件打开，新建后另存，读回的字节必须是新建口径（无 BOM、只有 LF）。
        // 少复位任一项，这里立刻红。
        var source = Path.Combine(_dir, "bom-crlf.js");
        File.WriteAllBytes(source,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
                .Concat(new UTF8Encoding(false).GetBytes("log(1);\r\nlog(2);\r\n"))
                .ToArray());

        var target = Path.Combine(_dir, "fresh.js");
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(source);
        dialog.ShowSaveDialog(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
              .Returns(target);
        var vm = NewVm(dialog);

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);
            await vm.NewCommand.ExecuteAsync(null);
            vm.ScriptText = "log(3);\nlog(4);";

            await vm.SaveCommand.ExecuteAsync(null);

            var bytes = File.ReadAllBytes(target);
            bytes.Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF },
                "新建脚本默认带 BOM 会把单文件脚本变成多字节前缀，很多工具会读坏");
            new UTF8Encoding(false).GetString(bytes)
                .Should().Be("log(3);\nlog(4);", "新建脚本默认 LF，不能沿用原文件的 CRLF");
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task New_Asks_Even_When_The_User_Deleted_Everything()
    {
        // 把内容全删光也是一次真改动：此时 ScriptText 是空白，但基线不是。
        // 判脏只看 HasUnsavedChanges，不能再叠一层 !IsNullOrWhiteSpace。
        var path = NewScriptFile("log('on disk');");
        var dialog = Substitute.For<IFileDialogService>();
        dialog.ShowOpenDialog(Arg.Any<string>()).Returns(path);
        var prompt = Substitute.For<IMessageBoxPrompt>();
        prompt.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
              .Returns(Task.FromResult(MessageBoxResult.No));
        var vm = NewVm(dialog, prompt);

        try
        {
            await vm.OpenCommand.ExecuteAsync(null);
            vm.ScriptText = "";

            await vm.NewCommand.ExecuteAsync(null);

            await prompt.Received(1).ShowAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>());
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task New_Drops_The_Previous_Draft()
    {
        var drafts = NewDraftStore();
        var (vm, _, _) = await OpenedVmAsync(drafts);

        try
        {
            vm.ScriptText = "log('unsaved');";
            vm.PersistDraft();
            drafts.Load().Should().NotBeNull("前置条件：草稿里确实有东西");

            await vm.NewCommand.ExecuteAsync(null);

            drafts.Load().Should().BeNull("新建是干净文档，旧草稿留着会在下次启动时复活");
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public async Task New_Requests_An_Editor_Reset()
    {
        var (vm, _, _) = await OpenedVmAsync();
        var pushed = new List<string>();

        try
        {
            vm.EditorTextReplaceRequested += pushed.Add;

            await vm.NewCommand.ExecuteAsync(null);

            pushed.Should().ContainSingle("清空 VM 而不推回编辑器，textarea 里会留着旧脚本")
                  .Which.Should().BeEmpty();
        }
        finally { vm.Dispose(); }
    }
}
