using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services.Trace;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.Host.Infrastructure.HIL;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// 独立 ECU 脚本 JSON 编辑器 ViewModel.
/// Open/Save/SaveAs/Format + EcuScriptLoader.Parse 校验 + 脏跟踪 + BrowseEcu 同步.
/// </summary>
public partial class EcuScriptEditorViewModel : ObservableObject
{
    private readonly IFileDialogService _fileDialog;
    private readonly IMessageBoxPrompt _messageBox;
    private readonly ILogger<EcuScriptEditorViewModel> _logger;

    [ObservableProperty] private string _editorText = "";
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _windowTitle = "ECU Script Editor";
    [ObservableProperty] private bool _isValidEcuScript;

    private string? _savedText = "";
    private bool _loadExternalInProgress;

    /// <summary>脏跟踪: EditorText 与最近一次 Load/Save 的内容是否不同.</summary>
    public bool HasUnsavedChanges => !string.Equals(EditorText, _savedText, StringComparison.Ordinal);

    /// <summary>WindowTitle 自动从 FilePath 派生, 无需手动同步.</summary>
    partial void OnFilePathChanged(string? value)
        => WindowTitle = string.IsNullOrEmpty(value)
            ? "ECU Script Editor"
            : $"ECU Script Editor - {Path.GetFileName(value)}";

    public EcuScriptEditorViewModel(
        IFileDialogService fileDialog,
        IMessageBoxPrompt messageBox,
        ILogger<EcuScriptEditorViewModel> logger)
    {
        _fileDialog = fileDialog ?? throw new ArgumentNullException(nameof(fileDialog));
        _messageBox = messageBox ?? throw new ArgumentNullException(nameof(messageBox));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [RelayCommand]
    private async Task Open()
    {
        if (HasUnsavedChanges)
        {
            var r = await _messageBox.ShowAsync("Discard changes?",
                "Opening a file will discard unsaved changes. Continue?", null);
            if (r != MessageBoxResult.Yes) return;
        }
        var path = _fileDialog.ShowOpenDialog("ECU Script JSON|*.ecu.json|All Files|*.*");
        if (path is null) return;
        if (!TryReadFile(path, out var content, out var readError))
        {
            ErrorMessage = readError;
            return;
        }
        ApplyLoadedContent(path, content);
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrEmpty(FilePath))
        {
            SaveAs();
            return;
        }
        TrySaveTo(FilePath);
    }

    [RelayCommand]
    private void SaveAs()
    {
        var dir = FilePath is null ? null : Path.GetDirectoryName(FilePath);
        var chosen = _fileDialog.ShowSaveDialog("ECU Script JSON|*.ecu.json", ".ecu.json", dir);
        if (chosen is null) return;
        TrySaveTo(chosen);
    }

    [RelayCommand]
    private void Format()
    {
        try
        {
            using var doc = JsonDocument.Parse(EditorText);
            EditorText = JsonSerializer.Serialize(doc.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
            ErrorMessage = null;
        }
        catch (JsonException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>共享读文件 helper: 统一 Open/LoadInitialPath/LoadExternal 的异常处理.</summary>
    private bool TryReadFile(string path, [NotNullWhen(true)] out string? content, out string? error)
    {
        try
        {
            content = File.ReadAllText(path);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            content = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>三条加载路径共用: 设 EditorText + 校验 + 脏基线 + FilePath.</summary>
    private void ApplyLoadedContent(string path, string content)
    {
        EditorText = content;
        IsValidEcuScript = TryValidate(content, out var validateError);
        ErrorMessage = IsValidEcuScript ? null : validateError;
        _savedText = content;
        FilePath = path;   // 最后设 -> 回填 handler 读到已更新的 IsValidEcuScript
        StatusMessage = IsValidEcuScript
            ? $"Opened {path}"
            : $"Opened {path} (not a valid ECU script)";
    }

    /// <summary>校验 JSON 是否合法 ECU 脚本. 4 类已知异常 + 防御兜底.</summary>
    private bool TryValidate(string json, [NotNullWhen(false)] out string? error)
    {
        try
        {
            _ = EcuScriptLoader.Parse(json);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>校验 + 写文件 + 更新状态. 校验失败不写文件.</summary>
    private void TrySaveTo(string path)
    {
        if (!TryValidate(EditorText, out var validateError))
        {
            ErrorMessage = validateError;
            StatusMessage = "Save blocked: invalid JSON.";
            return;
        }
        try
        {
            File.WriteAllText(path, EditorText);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Save failed.";
            return;
        }
        IsValidEcuScript = true;
        _savedText = EditorText;
        FilePath = path;
        StatusMessage = $"Saved {path}";
    }

    /// <summary>种子加载: factory 首次打开时从 EcuScriptPath 加载.</summary>
    public void LoadInitialPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            ErrorMessage = null;
            return;
        }
        if (!File.Exists(path))
        {
            ErrorMessage = $"File not found: {path}";
            return;
        }
        if (!TryReadFile(path, out var content, out var readError))
        {
            ErrorMessage = readError;
            return;
        }
        ApplyLoadedContent(path, content);
    }

    /// <summary>BrowseEcu -> 编辑器同步: 脏确认后加载.</summary>
    public async Task LoadExternalAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (_loadExternalInProgress) return;
        _loadExternalInProgress = true;
        try
        {
            if (HasUnsavedChanges)
            {
                var r = await _messageBox.ShowAsync("Discard changes?",
                    $"Loading {path} will discard unsaved changes. Continue?", null);
                if (r != MessageBoxResult.Yes) return;
            }
            if (!TryReadFile(path, out var content, out var readError))
            {
                ErrorMessage = readError;
                return;
            }
            ApplyLoadedContent(path, content);
        }
        finally
        {
            _loadExternalInProgress = false;
        }
    }

    /// <summary>关窗 = 会话结束: 清所有状态. 重开 = 新会话重新种子.</summary>
    public void Reset()
    {
        EditorText = "";
        _savedText = "";
        FilePath = null;
        IsValidEcuScript = false;
        ErrorMessage = null;
        StatusMessage = "Ready";
    }
}
