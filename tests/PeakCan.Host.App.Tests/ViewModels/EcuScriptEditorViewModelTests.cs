using System.IO;
using System.Text.Json;
using System.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.HIL;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// EcuScriptEditorViewModel 单元测试.
/// NSubstitute fake IFileDialogService + IMessageBoxPrompt; 临时文件 try-finally 清理.
/// </summary>
public sealed class EcuScriptEditorViewModelTests
{
    private readonly IFileDialogService _fileDialog = Substitute.For<IFileDialogService>();
    private readonly IMessageBoxPrompt _messageBox = Substitute.For<IMessageBoxPrompt>();
    private readonly EcuScriptEditorViewModel _vm;

    private const string ValidJson = """
        {"name":"Test","canIds":{"requestId":"0x7E0","responseId":"0x7E8"},"rules":[{"serviceId":"0x3E","responseData":[126]}]}
        """;

    public EcuScriptEditorViewModelTests()
    {
        _vm = new EcuScriptEditorViewModel(
            _fileDialog, _messageBox, NullLogger<EcuScriptEditorViewModel>.Instance);
    }

    private static string TempFile(string content = "")
    {
        var path = Path.Combine(Path.GetTempPath(), $"ecu_test_{Guid.NewGuid():N}.json");
        if (content.Length > 0) File.WriteAllText(path, content);
        return path;
    }

    // ── 基础属性 ──

    [Fact]
    public void InitialState_HasUnsavedChanges_IsFalse()
    {
        _vm.HasUnsavedChanges.Should().BeFalse();
    }

    [Fact]
    public void InitialState_WindowTitle_IsDefault()
    {
        _vm.WindowTitle.Should().Be("ECU Script Editor");
    }

    // ── Open ──

    [Fact]
    public async Task Open_ValidFile_LoadsContentAndSetsFilePath()
    {
        var path = TempFile(ValidJson);
        try
        {
            _fileDialog.ShowOpenDialog(Arg.Any<string>()).Returns(path);

            await _vm.OpenCommand.ExecuteAsync(null);

            _vm.EditorText.Should().Be(ValidJson);
            _vm.FilePath.Should().Be(path);
            _vm.IsValidEcuScript.Should().BeTrue();
            _vm.ErrorMessage.Should().BeNull();
            _vm.WindowTitle.Should().Contain(Path.GetFileName(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Open_Cancel_ShowOpenDialogReturnsNull_NoChange()
    {
        _fileDialog.ShowOpenDialog(Arg.Any<string>()).Returns((string?)null);

        await _vm.OpenCommand.ExecuteAsync(null);

        _vm.EditorText.Should().Be("");
        _vm.FilePath.Should().BeNull();
    }

    [Fact]
    public async Task Open_WithUnsavedChanges_UserCancelsConfirm_DoesNotLoad()
    {
        _vm.EditorText = "{\"dirty\":true}";
        _messageBox.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
            .Returns(MessageBoxResult.No);

        await _vm.OpenCommand.ExecuteAsync(null);

        _fileDialog.DidNotReceive().ShowOpenDialog(Arg.Any<string>());
        _vm.EditorText.Should().Be("{\"dirty\":true}");
    }

    [Fact]
    public async Task Open_WithUnsavedChanges_UserConfirms_Loads()
    {
        var path = TempFile(ValidJson);
        try
        {
            _vm.EditorText = "{\"dirty\":true}";
            _messageBox.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
                .Returns(MessageBoxResult.Yes);
            _fileDialog.ShowOpenDialog(Arg.Any<string>()).Returns(path);

            await _vm.OpenCommand.ExecuteAsync(null);

            _vm.EditorText.Should().Be(ValidJson);
            _vm.FilePath.Should().Be(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Open_InvalidJson_LoadsContent_ButIsValidEcuScriptFalse()
    {
        var path = TempFile("""{"a":1}""");
        try
        {
            _fileDialog.ShowOpenDialog(Arg.Any<string>()).Returns(path);

            await _vm.OpenCommand.ExecuteAsync(null);

            _vm.EditorText.Should().Be("""{"a":1}""");
            _vm.IsValidEcuScript.Should().BeFalse();
            _vm.ErrorMessage.Should().NotBeNullOrEmpty();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── SaveAs ──

    [Fact]
    public async Task SaveAs_Valid_WritesFileAndUpdatesFilePath()
    {
        _vm.EditorText = ValidJson;
        var path = TempFile();
        try
        {
            _fileDialog.ShowSaveDialog(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(path);

            _vm.SaveAsCommand.Execute(null);

            File.Exists(path).Should().BeTrue();
            File.ReadAllText(path).Should().Be(ValidJson);
            _vm.FilePath.Should().Be(path);
            _vm.IsValidEcuScript.Should().BeTrue();
            _vm.StatusMessage.Should().Contain("Saved");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SaveAs_Cancel_DoesNotWriteFile()
    {
        _vm.EditorText = ValidJson;
        _fileDialog.ShowSaveDialog(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns((string?)null);

        _vm.SaveAsCommand.Execute(null);

        _vm.FilePath.Should().BeNull();
        _vm.StatusMessage.Should().Be("Ready");
    }

    [Fact]
    public void SaveAs_FirstTime_FilePathNull_DoesNotThrow()
    {
        _vm.EditorText = ValidJson;
        var path = TempFile();
        try
        {
            _fileDialog.ShowSaveDialog(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(path);

            var act = () => _vm.SaveAsCommand.Execute(null);

            act.Should().NotThrow();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("""{"name":"Test"}""", "KeyNotFound")]               // 缺 canIds
    [InlineData("""{"name":1}""", "InvalidOperation")]               // name 非字符串
    [InlineData("""[invalid json""", "Json")]                         // 非法 JSON
    public void SaveAs_InvalidJson_Blocked_FileNotCreated(string invalidJson, string _)
    {
        _vm.EditorText = invalidJson;
        var path = TempFile();
        try
        {
            _fileDialog.ShowSaveDialog(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
                .Returns(path);

            _vm.SaveAsCommand.Execute(null);

            File.Exists(path).Should().BeFalse();
            _vm.ErrorMessage.Should().NotBeNullOrEmpty();
            _vm.StatusMessage.Should().Contain("blocked");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Save ──

    [Fact]
    public void Save_ExistingPath_WritesDirectly_NoDialog()
    {
        var path = TempFile(ValidJson);
        try
        {
            _vm.EditorText = ValidJson;
            _vm.FilePath = path;  // 模拟已有路径

            _vm.SaveCommand.Execute(null);

            _fileDialog.DidNotReceive().ShowSaveDialog(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
            File.ReadAllText(path).Should().Be(ValidJson);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Format ──

    [Fact]
    public void Format_ValidJson_PrettyPrintsWithNewlines()
    {
        _vm.EditorText = ValidJson;

        _vm.FormatCommand.Execute(null);

        _vm.EditorText.Should().Contain("\n");
        EcuScriptLoader.Parse(_vm.EditorText);  // 不抛异常
        _vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void Format_InvalidJson_SetsErrorMessage_KeepsContent()
    {
        var invalid = """[invalid json""";
        _vm.EditorText = invalid;

        _vm.FormatCommand.Execute(null);

        _vm.EditorText.Should().Be(invalid);
        _vm.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    // ── LoadInitialPath ──

    [Fact]
    public void LoadInitialPath_MissingFile_SetsErrorMessage()
    {
        _vm.LoadInitialPath(Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.json"));

        _vm.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void LoadInitialPath_EmptyOrNull_ClearsErrorNoThrow(string? path)
    {
        var act = () => _vm.LoadInitialPath(path);

        act.Should().NotThrow();
        _vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public void LoadInitialPath_ValidFile_LoadsContent()
    {
        var path = TempFile(ValidJson);
        try
        {
            _vm.LoadInitialPath(path);

            _vm.EditorText.Should().Be(ValidJson);
            _vm.FilePath.Should().Be(path);
            _vm.IsValidEcuScript.Should().BeTrue();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── LoadExternalAsync ──

    [Fact]
    public async Task LoadExternalAsync_EmptyPath_NoOp()
    {
        await _vm.LoadExternalAsync("");

        _ = _messageBox.DidNotReceive().ShowAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>());
        _vm.EditorText.Should().Be("");
    }

    [Fact]
    public async Task LoadExternalAsync_WithUnsavedChanges_UserCancels_KeepsContent()
    {
        var path = TempFile(ValidJson);
        try
        {
            _vm.EditorText = "{\"dirty\":true}";
            _messageBox.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
                .Returns(MessageBoxResult.No);

            await _vm.LoadExternalAsync(path);

            _vm.EditorText.Should().Be("{\"dirty\":true}");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task LoadExternalAsync_UserConfirms_LoadsContent()
    {
        var path = TempFile(ValidJson);
        try
        {
            _vm.EditorText = "{\"dirty\":true}";
            _messageBox.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
                .Returns(MessageBoxResult.Yes);

            await _vm.LoadExternalAsync(path);

            _vm.EditorText.Should().Be(ValidJson);
            _vm.FilePath.Should().Be(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task LoadExternalAsync_InProgress_SecondCallSkipped()
    {
        // 用未完成的 Task 模拟 MessageBox 阻塞
        var tcs = new TaskCompletionSource<MessageBoxResult>();
        _messageBox.ShowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Window?>())
            .Returns(tcs.Task);

        var path = TempFile(ValidJson);
        try
        {
            _vm.EditorText = "{\"dirty\":true}";
#pragma warning disable CS4014 // 故意不 await -- 模拟并发调用
            var first = _vm.LoadExternalAsync(path);
#pragma warning restore CS4014
            // 第一次还在 await MessageBox（未完成）

            await _vm.LoadExternalAsync(path);  // 第二次应被 guard 跳过

            // 完成 first
            tcs.SetResult(MessageBoxResult.Yes);
            await first.ConfigureAwait(true);

            // 只有第一次加载了
            _vm.EditorText.Should().Be(ValidJson);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── Reset ──

    [Fact]
    public void Reset_ClearsAllState()
    {
        var path = TempFile(ValidJson);
        try
        {
            _vm.LoadInitialPath(path);

            _vm.Reset();

            _vm.EditorText.Should().Be("");
            _vm.FilePath.Should().BeNull();
            _vm.IsValidEcuScript.Should().BeFalse();
            _vm.ErrorMessage.Should().BeNull();
            _vm.StatusMessage.Should().Be("Ready");
            _vm.WindowTitle.Should().Be("ECU Script Editor");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ── WindowTitle 自动派生 ──

    [Fact]
    public void WindowTitle_FollowsFilePath_Automatically()
    {
        var path = TempFile(ValidJson);
        try
        {
            _vm.LoadInitialPath(path);

            _vm.WindowTitle.Should().Be($"ECU Script Editor - {Path.GetFileName(path)}");

            _vm.Reset();

            _vm.WindowTitle.Should().Be("ECU Script Editor");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
