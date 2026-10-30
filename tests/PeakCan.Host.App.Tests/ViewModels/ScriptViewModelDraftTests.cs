using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.App.Services.Ui;
using PeakCan.Host.App.Tests.Collections;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// 脚本草稿的存取与恢复（关程序不丢内容）。
/// <para>
/// 这里用真实的 <see cref="ScriptDraftStore"/> 指向临时目录：草稿的安全性全在"什么时候写、
/// 恢复时拿什么当基线"，用替身反而测不到真行为。
/// </para>
/// </summary>
public sealed class ScriptViewModelDraftTests : IDisposable
{
    static ScriptViewModelDraftTests()
        // OEM 代码页（GBK/CP936 …）需要 provider；照 ScriptViewModelFileTests 的做法。
        => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly string _dir;
    private readonly ScriptEngine _engine = new(NullLogger<ScriptEngine>.Instance, null, null, null);

    public ScriptViewModelDraftTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"scriptdraftvm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _engine.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>推真消息泵 duration 这么久（防抖 timer 只有在 dispatcher 转起来时才会 tick）。</summary>
    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer(DispatcherPriority.Background) { Interval = duration };
        stop.Tick += (_, _) =>
        {
            stop.Stop();
            frame.Continue = false;
        };
        stop.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>照 AppShellViewLayoutTests 的 STA 线程包装（VM 的 timer 绑 UI 线程 dispatcher）。</summary>
    private static void RunSta(Action body)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            body();
            return;
        }

        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try { body(); }
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

    private ScriptDraftStore NewStore() => new(NullLogger<ScriptDraftStore>.Instance, Path.Combine(_dir, "draft.json"));

    private ScriptViewModel NewVm(ScriptDraftStore? store)
        => new(NullLogger<ScriptViewModel>.Instance, _engine, fileDialog: null, prompt: null, draftStore: store);

    private string NewScriptFile(string content)
    {
        var path = Path.Combine(_dir, $"script-{Guid.NewGuid():N}.js");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void PersistDraft_Writes_Dirty_Text_And_Path()
    {
        var store = NewStore();
        var vm = NewVm(store);

        vm.ScriptText = "log('edit');";
        vm.PersistDraft().Should().BeTrue();

        var draft = store.Load();
        draft.Should().NotBeNull();
        draft!.Text.Should().Be("log('edit');");
        draft.Path.Should().BeNull();
    }

    [Fact]
    public void Editing_Schedules_A_Debounced_Draft_Write()
    {
        // OnScriptTextChanged 是源生成器的分部钩子：签名写错时**不会报错**，
        // 只是永远不被调用（草稿功能无声失效）。这条用例钉住"输入确实会调度落盘"。
        var vm = NewVm(NewStore());
        vm.IsDraftPersistPending.Should().BeFalse();

        vm.ScriptText = "typing";

        vm.IsDraftPersistPending.Should().BeTrue();
        vm.PersistDraft();
        vm.IsDraftPersistPending.Should().BeFalse("显式落盘后不该再排一次");
    }

    [Fact]
    public void PersistDraft_Clears_The_Draft_When_Nothing_Is_Dirty()
    {
        var store = NewStore();
        var vm = NewVm(store);
        vm.ScriptText = "temporary";
        vm.PersistDraft();
        store.Load().Should().NotBeNull();

        vm.ScriptText = "";
        vm.PersistDraft().Should().BeTrue();

        store.Load().Should().BeNull("没有未保存内容时草稿必须消失，否则下次启动会翻出旧内容");
    }

    [Fact]
    public void Draft_Is_Not_Written_Without_A_Store()
    {
        var vm = NewVm(store: null);

        vm.ScriptText = "no store";
        vm.PersistDraft().Should().BeTrue();
        vm.HasDraftStore.Should().BeFalse();
    }

    [Fact]
    public void RestoreDraft_Brings_Back_Text_And_Marks_It_Dirty()
    {
        var store = NewStore();
        var source = NewScriptFile("log('saved');");
        store.Save(new ScriptDraftDto("log('unsaved');", source, DateTime.UtcNow.Ticks));

        var vm = NewVm(store);
        vm.RestoreDraft();

        vm.ScriptText.Should().Be("log('unsaved');");
        vm.CurrentFilePath.Should().Be(source, "恢复后 Ctrl+S 应该写回原文件");
        vm.HasUnsavedChanges.Should().BeTrue();
        vm.StatusText.Should().Contain("已恢复");
    }

    [Fact]
    public void Draft_Matching_The_File_Is_Cleared_And_Not_Dirty()
    {
        var store = NewStore();
        var source = NewScriptFile("log('same');");
        store.Save(new ScriptDraftDto("log('same');", source, DateTime.UtcNow.Ticks));

        var vm = NewVm(store);
        vm.RestoreDraft();

        vm.ScriptText.Should().Be("log('same');");
        vm.HasUnsavedChanges.Should().BeFalse("草稿与磁盘一致，不该显示成未保存");
        store.Load().Should().BeNull();
    }

    [Fact]
    public void Missing_Source_File_Keeps_The_Text_But_Drops_The_Path()
    {
        var store = NewStore();
        var gone = Path.Combine(_dir, "deleted.js");
        store.Save(new ScriptDraftDto("log('orphan');", gone, DateTime.UtcNow.Ticks));

        var vm = NewVm(store);
        vm.RestoreDraft();

        vm.ScriptText.Should().Be("log('orphan');");
        vm.HasUnsavedChanges.Should().BeTrue();
        // 未知编码 + 未知内容：保留路径会让一次 Ctrl+S 用默认 UTF-8/LF 覆盖一个
        // 真实存在（但这次读不到）的文件。
        vm.CurrentFilePath.Should().BeNull();
        vm.StatusText.Should().Contain("不可读");
    }

    [Fact]
    public void Stale_Draft_Is_Discarded_When_The_File_Is_Newer()
    {
        var store = NewStore();
        var source = NewScriptFile("log('newer');");
        // 草稿写于 5 分钟前，磁盘文件刚刚被（成功保存后清理失败 / 外部编辑器）更新。
        store.Save(new ScriptDraftDto("log('stale');", source, DateTime.UtcNow.AddMinutes(-5).Ticks));

        var vm = NewVm(store);
        vm.RestoreDraft();

        vm.ScriptText.Should().BeEmpty("旧草稿不能覆盖更新的文件");
        vm.StatusText.Should().Contain("未恢复");
        store.Load().Should().BeNull();
    }

    [Fact]
    public void Empty_Draft_Does_Not_Touch_The_Editor()
    {
        var store = NewStore();
        var vm = NewVm(store);

        vm.RestoreDraft();

        vm.ScriptText.Should().BeEmpty();
        vm.CurrentFilePath.Should().BeNull();
    }

    [Fact]
    public void Restore_Does_Not_Overwrite_Existing_Content()
    {
        var store = NewStore();
        store.Save(new ScriptDraftDto("log('draft');", null, DateTime.UtcNow.Ticks));
        var vm = NewVm(store);
        vm.ScriptText = "log('already here');";

        vm.RestoreDraft();

        vm.ScriptText.Should().Be("log('already here');");
    }

    [Fact]
    public void Oversized_Text_Is_Not_Persisted_And_Says_So()
    {
        var store = NewStore();
        var vm = NewVm(store);

        vm.ScriptText = new string('x', ScriptDraftStore.MaxDraftChars + 1);
        vm.PersistDraft().Should().BeFalse();

        store.Load().Should().BeNull();
        vm.StatusText.Should().Contain("字符");
    }

    [Fact]
    public async System.Threading.Tasks.Task Restore_Then_Save_Keeps_The_Source_Encoding_And_Line_Endings()
    {
        // 恢复时用磁盘原文重建基线（TryLoadBaseline）——一旦它和 OpenAsync 的解码
        // 口径漂移，用户"恢复 → Ctrl+S"就会把 GBK/CRLF 的原文件写成 UTF-8/LF。
        var gbk = Encoding.GetEncoding(936);
        var path = Path.Combine(_dir, $"gbk-{Guid.NewGuid():N}.js");
        File.WriteAllBytes(path, gbk.GetBytes("// 中文注释\r\nlog('x');"));

        var store = NewStore();
        store.Save(new ScriptDraftDto("// 中文注释\nlog('y');", path, DateTime.UtcNow.Ticks));

        var vm = new ScriptViewModel(
            NullLogger<ScriptViewModel>.Instance, _engine,
            Substitute.For<PeakCan.HIL.Core.IFileDialogService>(), prompt: null, draftStore: store);

        try
        {
            vm.RestoreDraft();
            await vm.SaveCommand.ExecuteAsync(null);

            gbk.GetString(File.ReadAllBytes(path)).Should().Be("// 中文注释\r\nlog('y');");
        }
        finally { vm.Dispose(); }
    }

    [Fact]
    public void Debounce_Timer_Actually_Persists_The_Draft()
    {
        // 只断言"timer 被排上了"不够：Tick 回调里少一句 PersistDraft() 也会全绿，
        // 而功能其实已经失效。这里推真消息泵，等防抖窗口过去。
        RunSta(() =>
        {
            var store = NewStore();
            var vm = new ScriptViewModel(
                NullLogger<ScriptViewModel>.Instance, _engine,
                fileDialog: null, prompt: null, draftStore: store)
            {
                DraftDebounceMs = 10,
            };

            try
            {
                vm.ScriptText = "debounced";
                PumpFor(TimeSpan.FromMilliseconds(600));

                store.Load().Should().NotBeNull();
            }
            finally { vm.Dispose(); }
        });
    }

    [Fact]
    public async System.Threading.Tasks.Task Opening_A_File_Clears_The_Previous_Draft()
    {
        var store = NewStore();
        var dialog = NSubstitute.Substitute.For<PeakCan.HIL.Core.IFileDialogService>();
        var source = NewScriptFile("log('opened');");
        dialog.ShowOpenDialog(NSubstitute.Arg.Any<string>()).Returns(source);

        var vm = new ScriptViewModel(
            NullLogger<ScriptViewModel>.Instance, _engine, dialog, prompt: null, draftStore: store);
        vm.ScriptText = "log('scratch');";
        vm.PersistDraft();

        await vm.OpenCommand.ExecuteAsync(null);

        store.Load().Should().BeNull("打开文件后旧草稿属于别的脚本，必须清掉");
    }
}
