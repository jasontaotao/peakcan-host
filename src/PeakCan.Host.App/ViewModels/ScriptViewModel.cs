using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services.Scripting;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// ViewModel for the Script tab. Manages script text, execution state,
/// and the output buffer.
/// <para>
/// <b>Output buffering:</b> Log lines from the script engine are collected
/// in a concurrent queue and flushed to the UI at 30 Hz via a
/// <see cref="System.Windows.Threading.DispatcherTimer"/>. This prevents
/// UI flooding at high frame rates (8k fps scripts would otherwise
/// generate 8k UI updates/sec).
/// </para>
/// </summary>
public sealed partial class ScriptViewModel : ObservableObject
{
    private readonly ILogger<ScriptViewModel> _logger;
    private readonly ScriptEngine _engine;
    // 文件对话框/确认弹窗都可以为 null：测试构造点不参与文件流。
    // 生产 DI 两个都注册（WpfFileDialogService / WpfMessageBoxPrompt），漏装配时
    // 命令会把原因写进 StatusText，而不是像以前那样静默什么都不做。
    private readonly PeakCan.HIL.Core.IFileDialogService? _fileDialog;
    private readonly PeakCan.Host.App.Services.Trace.IMessageBoxPrompt? _prompt;

    // Timer for flushing output buffer to UI at 30 Hz.
    private readonly System.Windows.Threading.DispatcherTimer _flushTimer;

    [ObservableProperty]
    // 编辑内容一变，未保存状态也要跟着变（工具栏将来要显示脏标记）。
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    private string _scriptText = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "Ready";

    // v1.2.12 PATCH Item 7: editor-ready state surfaced for the XAML fallback
    // TextBlock. Set true by ScriptView.OnLoaded after WebView2 init succeeds;
    // set false + EditorError message when WebView2 init or NavigateToString
    // throws (e.g. missing WebView2 Evergreen Runtime).
    [ObservableProperty]
    private bool _isEditorReady;

    [ObservableProperty]
    private string? _editorError;

    /// <summary>Output lines displayed in the UI.</summary>
    public ObservableCollection<string> OutputLines { get; } = new();

    public ScriptViewModel(
        ILogger<ScriptViewModel> logger,
        ScriptEngine engine,
        // 可选：既有测试构造点（2 参）不涉及文件流，不必改动。
        PeakCan.HIL.Core.IFileDialogService? fileDialog = null,
        PeakCan.Host.App.Services.Trace.IMessageBoxPrompt? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(engine);

        _logger = logger;
        _engine = engine;
        _fileDialog = fileDialog;
        _prompt = prompt;

        // Subscribe to script engine output.
        _engine.OutputReceived += OnOutputReceived;

        // Setup flush timer (30 Hz).
        _flushTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _flushTimer.Tick += (_, _) => FlushOutputBuffer();
        _flushTimer.Start();
    }



    /// <summary>
    /// Cleanup: stop timer and unsubscribe from engine events.
    /// </summary>
    public void Dispose()
    {
        _flushTimer.Stop();
        _engine.OutputReceived -= OnOutputReceived;
    }
}
