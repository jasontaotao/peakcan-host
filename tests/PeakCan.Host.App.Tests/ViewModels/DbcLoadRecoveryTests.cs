using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.ViewModels;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// P1 防事故：DBC 加载失败后的恢复路径（重试 / 查看日志 / 打开目录）。
/// <para>
/// 失败的形态本身不危险，危险的是**死路**：原来只把状态文字改成 FAIL，
/// 用户既不知道该修哪，也不知道日志在哪。这里钉住三件事——错误条有内容、
/// 重试真的用同一个路径重来、两个「打开」动作真的落到 shell 上。
/// </para>
/// </summary>
public sealed class DbcLoadRecoveryTests
{
    private sealed class FakeFileDialogService : IFileDialogService
    {
        public string? NextResult { get; set; }
        public string? ShowOpenDialog(string filter) => NextResult;
        public string? ShowSaveDialog(string filter, string? defaultExt, string? initialDirectory) => null;
    }

    private static DbcViewModel NewVm(
        DbcService svc,
        IShellLauncher? shell = null,
        IFileDialogService? fileDialog = null,
        Func<string>? logDirectoryProvider = null)
        => new(svc, new SignalViewModel(), NullLogger<DbcViewModel>.Instance,
               fileDialog, shell, logDirectoryProvider);

    private static void RaiseLoaded(DbcService svc, DbcDocument doc)
        => svc.GetType().GetEvent(nameof(DbcService.DbcLoaded))!.RaiseMethod(svc, doc);

    private static void RaiseFailed(DbcService svc, Error error)
        => svc.GetType().GetEvent(nameof(DbcService.LoadFailed))!.RaiseMethod(svc, error);

    private static DbcDocument EmptyDocument()
        => new("", new List<Node>(), new List<Message>(),
               new Dictionary<uint, Message>(), new Dictionary<string, ValueTable>());

    /// <summary>
    /// 造一个"加载必然失败"的服务替身。关键点：失败事件必须在 <see cref="DbcService.LoadAsync"/>
    /// 调用**内部**同步抛出——产品里 <c>DbcService</c> 就是在 await 链里 raise 的，
    /// 事件在调用返回后才到属于另一种（脚本侧）失败，会被当作外来失败处理。
    /// </summary>
    private static DbcService FailingService(Error error)
    {
        var svc = Substitute.For<DbcService>(NullLogger<DbcService>.Instance);
        svc.When(s => s.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
           .Do(_ => RaiseFailed(svc, error));
        return svc;
    }

    [Fact]
    public async Task Load_Failure_Populates_Error_Bar_And_Enables_Retry()
    {
        var svc = FailingService(new Error(ErrorCode.ParseFailure, "unexpected token at line 3"));
        var dialogs = new FakeFileDialogService { NextResult = @"C:\tmp\bad.dbc" };
        var vm = NewVm(svc, fileDialog: dialogs);

        await vm.OpenCommand.ExecuteAsync(null);

        vm.LoadError.Should().NotBeNull();
        vm.LoadError.Should().Contain("解析", "错误条要说人话，代码值留给日志");
        vm.LoadError.Should().Contain("unexpected token", "原始信息不能丢，否则没法排查");
        vm.RetryLoadCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Retry_Before_Any_Load_Is_Disabled()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);

        vm.RetryLoadCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Retry_Reloads_The_Same_Path()
    {
        var svc = FailingService(new Error(ErrorCode.IoError, "missing"));
        var dialogs = new FakeFileDialogService { NextResult = @"C:\tmp\bad.dbc" };
        var vm = NewVm(svc, fileDialog: dialogs);

        await vm.OpenCommand.ExecuteAsync(null);
        await vm.RetryLoadCommand.ExecuteAsync(null);

        await svc.Received(2).LoadAsync(@"C:\tmp\bad.dbc", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Successful_Load_Clears_Error()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var dialogs = new FakeFileDialogService { NextResult = @"C:\tmp\bad.dbc" };
        var vm = NewVm(svc, fileDialog: dialogs);

        await vm.OpenCommand.ExecuteAsync(null);
        RaiseFailed(svc, new Error(ErrorCode.ParseFailure, "boom"));
        RaiseLoaded(svc, EmptyDocument());

        vm.LoadError.Should().BeNull("加载成功了错误条必须消失，否则用户会一直以为还坏着");
    }

    [Fact]
    public async Task Failure_Without_Ui_Path_Still_Reports_Error()
    {
        // 脚本侧（DbcApi）也可能触发加载失败：错误条照显，但没有"重试"目标。
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var vm = NewVm(svc);

        RaiseFailed(svc, new Error(ErrorCode.IoError, "script load failed"));

        vm.LoadError.Should().Contain("script load failed");
        vm.RetryLoadCommand.CanExecute(null).Should().BeFalse("没有 UI 侧路径时不该假装能重试");
    }

    [Fact]
    public async Task Script_Side_Failure_Clears_The_Ui_Retry_Target()
    {
        // 界面先打开 A，之后脚本加载 B 失败：错误条要显示 B 的错，但「重试」绝不能
        // 悄悄去重载 A —— 那会把当前文档换成另一个文件，用户完全不知情。
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var dialogs = new FakeFileDialogService { NextResult = @"C:\tmp\A.dbc" };
        var vm = NewVm(svc, fileDialog: dialogs);
        await vm.OpenCommand.ExecuteAsync(null);
        RaiseLoaded(svc, EmptyDocument());

        // 脚本侧失败：不经过 LoadPathAsync，_uiLoadInFlight 为 false。
        RaiseFailed(svc, new Error(ErrorCode.IoError, "B.dbc missing"));

        vm.LoadError.Should().Contain("B.dbc missing");
        vm.RetryLoadCommand.CanExecute(null).Should().BeFalse("重试目标已失效，不能留着指向 A");
        vm.RevealDbcFileCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Overlapping_Ui_Loads_Keep_The_Retry_Target()
    {
        // A 在飞时再起 B：A 先结束。若用 bool 记"界面加载在飞"，A 结束就把标记清了，
        // B 随后的失败会被误判成脚本侧失败、恢复入口被无辜关掉。
        var gateA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var svc = Substitute.For<DbcService>(NullLogger<DbcService>.Instance);
        var calls = 0;
        svc.LoadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
           .Returns(_ => Interlocked.Increment(ref calls) == 1 ? gateA.Task : gateB.Task);

        var dialogs = new FakeFileDialogService { NextResult = @"C:\tmp\A.dbc" };
        var vm = NewVm(svc, fileDialog: dialogs);

        var loadingA = vm.OpenCommand.ExecuteAsync(null);
        dialogs.NextResult = @"C:\tmp\B.dbc";
        var loadingB = vm.OpenCommand.ExecuteAsync(null);

        gateA.SetResult();
        await loadingA;
        // B 仍在飞时失败到达（生产里就是这个时序：事件在 LoadAsync 内部同步抛）。
        RaiseFailed(svc, new Error(ErrorCode.IoError, "B failed"));
        gateB.SetResult();
        await loadingB;

        vm.RetryLoadCommand.CanExecute(null).Should()
            .BeTrue("B 是界面发起的加载，重试目标不该被清掉");
    }

    [Fact]
    public async Task Reveal_Routes_The_Failed_Path_To_The_Shell()
    {
        var svc = new DbcService(NullLogger<DbcService>.Instance);
        var shell = Substitute.For<IShellLauncher>();
        var dialogs = new FakeFileDialogService { NextResult = @"C:\tmp\bad.dbc" };
        var vm = NewVm(svc, shell, dialogs);

        await vm.OpenCommand.ExecuteAsync(null);
        vm.RevealDbcFileCommand.Execute(null);

        shell.Received(1).Reveal(@"C:\tmp\bad.dbc");
    }

    [Fact]
    public void Reveal_With_No_Path_Is_Disabled()
    {
        var shell = Substitute.For<IShellLauncher>();
        var vm = NewVm(new DbcService(NullLogger<DbcService>.Instance), shell);

        vm.RevealDbcFileCommand.CanExecute(null).Should().BeFalse();
        shell.DidNotReceiveWithAnyArgs().Reveal(default!);
    }

    [Fact]
    public async Task Reveal_Failure_Is_Surfaced_Not_Swallowed()
    {
        var shell = Substitute.For<IShellLauncher>();
        shell.When(s => s.Reveal(Arg.Any<string>()))
             .Do(_ => throw new FileNotFoundException("gone"));
        var dialogs = new FakeFileDialogService { NextResult = @"C:\tmp\gone.dbc" };
        var vm = NewVm(new DbcService(NullLogger<DbcService>.Instance), shell, dialogs);
        await vm.OpenCommand.ExecuteAsync(null);

        vm.RevealDbcFileCommand.Execute(null);

        vm.Status.Should().Contain("gone", "打开目录失败要写在状态栏，不能静默");
    }

    [Fact]
    public void Open_Log_Folder_Reveals_The_Newest_Log_File()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var older = Path.Combine(dir, "peak-20260101.log");
            var newer = Path.Combine(dir, "peak-20260102.log");
            File.WriteAllText(older, "old");
            File.WriteAllText(newer, "new");
            File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newer, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

            var shell = Substitute.For<IShellLauncher>();
            var vm = NewVm(new DbcService(NullLogger<DbcService>.Instance), shell, logDirectoryProvider: () => dir);

            vm.OpenLogFolderCommand.Execute(null);

            shell.Received(1).Reveal(newer);
            shell.DidNotReceiveWithAnyArgs().OpenFolder(default!);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }

    [Fact]
    public void Open_Log_Folder_Opens_The_Directory_When_No_Log_File_Exists()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var shell = Substitute.For<IShellLauncher>();
            var vm = NewVm(new DbcService(NullLogger<DbcService>.Instance), shell, logDirectoryProvider: () => dir);

            vm.OpenLogFolderCommand.Execute(null);

            shell.Received(1).OpenFolder(dir);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }

    [Fact]
    public void Open_Log_Folder_Reports_A_Missing_Directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}");
        var shell = Substitute.For<IShellLauncher>();
        var vm = NewVm(new DbcService(NullLogger<DbcService>.Instance), shell, logDirectoryProvider: () => dir);

        vm.OpenLogFolderCommand.Execute(null);

        vm.Status.Should().Contain("不存在", "日志目录都没有时不能装作打开了");
        shell.DidNotReceiveWithAnyArgs().Reveal(default!);
        shell.DidNotReceiveWithAnyArgs().OpenFolder(default!);
    }

    [Fact]
    public void Newest_Log_File_Picks_The_Most_Recently_Written()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var older = Path.Combine(dir, "peak-20260101.log");
            var newer = Path.Combine(dir, "peak-20260102.log");
            File.WriteAllText(older, "old");
            File.WriteAllText(newer, "new");
            File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newer, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

            AppLogPaths.NewestLogFile(dir).Should().Be(newer);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }

    [Fact]
    public void Newest_Log_File_Returns_Null_When_Directory_Missing()
    {
        AppLogPaths.NewestLogFile(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}"))
            .Should().BeNull();
    }
}
