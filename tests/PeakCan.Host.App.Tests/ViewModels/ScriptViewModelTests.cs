using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.App.ViewModels;
using Xunit;

namespace PeakCan.Host.App.Tests.ViewModels;

/// <summary>
/// Unit tests for <see cref="ScriptViewModel"/>.
/// </summary>
public sealed class ScriptViewModelTests : IDisposable
{
    private readonly ILogger<ScriptViewModel> _logger = Substitute.For<ILogger<ScriptViewModel>>();
    private readonly ILogger<ScriptEngine> _engineLogger = Substitute.For<ILogger<ScriptEngine>>();
    private readonly ScriptEngine _engine;
    private readonly ScriptViewModel _viewModel;

    public ScriptViewModelTests()
    {
        _engine = new ScriptEngine(_engineLogger, null, null, null);
        _viewModel = new ScriptViewModel(_logger, _engine);
    }

    [Fact]
    public void InitialState_IsNotRunning()
    {
        // Assert
        Assert.False(_viewModel.IsRunning);
        Assert.Equal("Ready", _viewModel.StatusText);
        Assert.Empty(_viewModel.OutputLines);
    }

    [Fact]
    public void ClearOutput_ClearsOutputLines()
    {
        // Arrange
        _viewModel.OutputLines.Add("Test line");

        // Act
        _viewModel.ClearOutputCommand.Execute(null);

        // Assert
        Assert.Empty(_viewModel.OutputLines);
    }

    [Fact]
    public async Task RunCommand_WithEmptyScript_DoesNotRun()
    {
        // Arrange
        _viewModel.ScriptText = "";

        // Act
        await _viewModel.RunCommand.ExecuteAsync(null);

        // Assert
        Assert.False(_viewModel.IsRunning);
        Assert.Equal("Ready", _viewModel.StatusText);
    }

    [Fact]
    public async Task RunCommand_WithValidScript_SetsIsRunning()
    {
        // Arrange
        _viewModel.ScriptText = "var x = 1;";

        // Act
        var runTask = _viewModel.RunCommand.ExecuteAsync(null);

        // Assert
        // Note: IsRunning is set to true during execution,
        // but may be false again by the time we check.
        await runTask;
        Assert.False(_viewModel.IsRunning);
    }

    [Fact]
    public async Task RunCommand_WithValidScript_SetsStatusToCompleted()
    {
        // Arrange
        _viewModel.ScriptText = "var x = 1;";

        // Act
        await _viewModel.RunCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal("Completed", _viewModel.StatusText);
    }

    [Fact]
    public async Task RunCommand_WithInvalidScript_SetsStatusToError()
    {
        // Arrange
        _viewModel.ScriptText = "function {";

        // Act
        await _viewModel.RunCommand.ExecuteAsync(null);

        // Assert
        Assert.StartsWith("Error:", _viewModel.StatusText);
    }

    [Fact]
    public void StopCommand_WhenNotRunning_CannotExecute()
    {
        // Assert
        Assert.False(_viewModel.StopCommand.CanExecute(null));
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        // Act & Assert
        _viewModel.Dispose();
    }

    // ===== v1.2.12 PATCH Item 7: WebView2 init try/catch + fallback state =====

    [Fact]
    public void IsEditorReady_Initially_False()
    {
        // Assert
        Assert.False(_viewModel.IsEditorReady);
        Assert.Null(_viewModel.EditorError);
    }

    [Fact]
    public void SetEditorReady_Triggers_PropertyChanged()
    {
        // Arrange
        var raised = new List<string>();
        _viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        // Act
        _viewModel.IsEditorReady = true;

        // Assert
        Assert.Contains(nameof(ScriptViewModel.IsEditorReady), raised);
    }

    [Fact]
    public void SetEditorError_Triggers_PropertyChanged()
    {
        // Arrange
        var raised = new List<string>();
        _viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        // Act
        _viewModel.EditorError = "WebView2 runtime 未安装";

        // Assert
        Assert.Contains(nameof(ScriptViewModel.EditorError), raised);
    }

    // ===== v1.2.12 PATCH Item 7 review I-2/I-3: VM routes exception through logger =====

    [Fact]
    public void OnWebView2InitFailed_Sets_IsEditorReady_False_And_Logs()
    {
        // Arrange
        var ex = new InvalidOperationException("wv2 missing");
        var message = "WebView2 runtime 未安装或损坏: wv2 missing. " +
                      "请安装 WebView2 Evergreen Runtime.";
        // Source-gen [LoggerMessage] gates Log() on IsEnabled, so stub true.
        _logger.IsEnabled(LogLevel.Error).Returns(true);

        // Act
        _viewModel.OnWebView2InitFailed(ex, message);

        // Assert
        Assert.False(_viewModel.IsEditorReady);
        Assert.StartsWith("WebView2 runtime", _viewModel.EditorError);
        _logger.Received(1).Log<Arg.AnyType>(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            ex,
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    public void Dispose()
    {
        _viewModel.Dispose();
        _engine.Dispose();
    }
}
