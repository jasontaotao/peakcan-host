using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.Trace;
using PeakCan.Host.App.ViewModels;
using PeakCan.Host.Core;
using PeakCan.Host.Core.HIL.Contracts;
using PeakCan.Host.Infrastructure.HIL;
using PeakCan.Host.Infrastructure.HIL.Generators;
using PeakCan.Host.Infrastructure.HIL.Odx;

namespace PeakCan.Host.App.ViewModels.EcuSimulator;

/// <summary>
/// ECU Simulator 主 VM（HilStudioWindow col4）。表单编辑 EcuScript（文件视角）,
/// 保存走 ToJson 文件视角 round-trip（约束 #1/#2）。暴露 EcuScriptEditorViewModel 同款契约
/// 供 AppShell 三路同步（LoadInitialPath/LoadExternalAsync/Reset）。
/// </summary>
public sealed partial class EcuSimulatorViewModel : ObservableObject
{
    private readonly ILogger _logger;
    private readonly IFileDialogService _fileDialog;
    private readonly IMessageBoxPrompt _messageBox;
    private string? _suitePath;
    private string _savedJson = "";

    public EditableEcuScript Script { get; } = new();
    public ObservableCollection<EditableEcuState> States => Script.States;
    public ObservableCollection<EditableDidValue> DidValues => Script.DidValues;
    public IReadOnlyList<string> GeneratorNames { get; }

    [ObservableProperty] private EditableEcuState? _selectedState;
    [ObservableProperty] private EditableEcuTransition? _selectedTransition;
    [ObservableProperty] private EditableDidValue? _selectedDidValue;

    // 切状态清残留转移, 防编辑/删除错目标（foreign transition 静默 no-op 缺陷修复）
    partial void OnSelectedStateChanged(EditableEcuState? value)
        => SelectedTransition = null;
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private bool _isValidEcuScript;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private string? _errorMessage;

    // Import ODX 参数（col4 工具栏输入框）
    [ObservableProperty] private string _odxEcuName = "";
    [ObservableProperty] private string _odxRequestIdHex = "0x7E0";
    [ObservableProperty] private string _odxResponseIdHex = "0x7E8";

    public bool HasUnsavedChanges => _savedJson.Length > 0
        && !string.Equals(Script.ToJson(), _savedJson, StringComparison.Ordinal);

    public EcuSimulatorViewModel(ILogger logger, IFileDialogService? fileDialog = null, IMessageBoxPrompt? messageBox = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _fileDialog = fileDialog ?? new WpfFileDialogService();
        _messageBox = messageBox ?? new WpfMessageBoxPrompt();
        GeneratorNames = BuiltInGenerators.CreateAll().Select(g => g.Name).ToList();
        Script.Changed += () => OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    /// <summary>反序列化并填充表单; 成功 true。加载经 EcuScriptLoader（校验 + rules 迁移 + CanIds 交换反推）。</summary>
    public bool LoadFromText(string json)
    {
        try
        {
            var script = EcuScriptLoader.Parse(json);
            Script.Name = script.Name;
            Script.RequestIdHex = EditableEcuScript.Hex(script.CanIds.ResponseId, script.CanIds.IsExtendedFrame);
            Script.ResponseIdHex = EditableEcuScript.Hex(script.CanIds.RequestId, script.CanIds.IsExtendedFrame);
            Script.IsExtendedFrame = script.CanIds.IsExtendedFrame;
            Script.InitialState = script.InitialState;
            Script.States.Clear();
            Script.DidValues.Clear();
            foreach (var group in script.StateMachine.Transitions.GroupBy(t => t.FromState ?? "wildcard"))
                Script.States.Add(EditableEcuState.FromTransitions(group.Key, group, Script));
            if (script.DidValues is { } dv)
                foreach (var (k, v) in dv)
                    Script.DidValues.Add(EditableDidValue.From(k, v, Script));
            SelectedState = Script.States.FirstOrDefault();
            SelectedTransition = null;
            IsValidEcuScript = true;
            ErrorMessage = null;
            StatusMessage = $"Loaded {Script.States.Count} state(s)";
            // 缺陷修复: _savedJson 用 Script.ToJson()（规范输出, 缩进+忽略 null）, 而非原始输入,
            // 否则 HasUnsavedChanges 在加载后立即误判 true。
            _savedJson = Script.ToJson();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ECU script load failed");
            ErrorMessage = ex.Message;
            StatusMessage = "Load failed.";
            IsValidEcuScript = false;
            return false;
        }
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (HasUnsavedChanges)
        {
            var r = await _messageBoxConfirm();
            if (r is null or false) return;
        }
        var path = _fileDialog.ShowOpenDialog("ECU Script JSON|*.json|All Files|*.*");
        if (path is null) return;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            if (LoadFromText(json)) { _suitePath = path; FilePath = path; }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ECU script open failed");
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ImportOdxAsync()
    {
        var path = _fileDialog.ShowOpenDialog("ODX Files|*.odx;*.pdx|All Files|*.*");
        if (path is null) return;
        // 与 OpenAsync 一致: 导入会整体替换当前 Script, 有未保存修改时先确认, 避免静默丢弃。
        if (await _messageBoxConfirm() is null or false) return;
        try
        {
            var json = OdxEcuScriptImporter.ImportToJson(
                path, OdxEcuName,
                ParseHexUint(OdxRequestIdHex), ParseHexUint(OdxResponseIdHex));
            // 导入成功后 Save 降级 SaveAs（_suitePath=null → SaveCore 走 SaveAs 另存 .json）,
            // 绝不覆盖源 .odx 文件（数据丢失风险）。
            if (LoadFromText(json)) { _suitePath = null; FilePath = null; }
            StatusMessage = $"Imported {Path.GetFileName(path)}";
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "ODX import failed (no UDS services)");
            ErrorMessage = ex.Message;
            StatusMessage = "Import ODX failed.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ODX import failed");
            ErrorMessage = ex.Message;
            StatusMessage = "Import ODX failed.";
        }
    }

    private static uint ParseHexUint(string s)
    {
        var clean = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        return uint.Parse(clean, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<bool?> _messageBoxConfirm()
    {
        if (!HasUnsavedChanges) return true;
        var r = await _messageBox.ShowAsync("Discard changes?",
            "Opening a file will discard unsaved changes. Continue?", null);
        return r == System.Windows.MessageBoxResult.Yes;
    }

    [RelayCommand]
    private void Save() => SaveCore(_suitePath);

    [RelayCommand]
    private void SaveAs()
    {
        var dir = _suitePath is null ? null : Path.GetDirectoryName(_suitePath);
        var chosen = _fileDialog.ShowSaveDialog("ECU Script JSON|*.json", ".json", dir);
        if (chosen is null) return;
        SaveCore(chosen);
    }

    private void SaveCore(string? path)
    {
        if (string.IsNullOrEmpty(path)) { SaveAs(); return; }
        try
        {
            var json = Script.ToJson();
            File.WriteAllText(path, json);
            _savedJson = json;
            _suitePath = path;
            FilePath = path;
            IsValidEcuScript = true;
            ErrorMessage = null;
            StatusMessage = $"Saved {path}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ECU script save failed");
            ErrorMessage = ex.Message;
            StatusMessage = "Save failed.";
        }
    }

    [RelayCommand]
    private void AddState()
    {
        var s = EditableEcuState.FromTransitions($"State{States.Count + 1}", Array.Empty<EcuStateTransition>(), Script);
        Script.States.Add(s);
        SelectedState = s;
        SelectedTransition = null;   // 显式兜底: 切换目标状态后不应保留旧状态的转移
    }

    [RelayCommand]
    private void RemoveState()
    {
        if (SelectedState is null) return;
        Script.States.Remove(SelectedState);
        SelectedTransition = null;
        SelectedState = Script.States.LastOrDefault();
    }

    [RelayCommand]
    private void AddTransition()
    {
        if (SelectedState is null) return;
        var t = EditableEcuTransition.FromTransition(
            new EcuStateTransition { ServiceId = 0x22, Response = new StaticResponse(new byte[] { 0x7F, 0x22, 0x11 }) },
            Script.Notify);
        SelectedState.Transitions.Add(t);
        SelectedTransition = t;
    }

    [RelayCommand]
    private void RemoveTransition()
    {
        if (SelectedState is null || SelectedTransition is null) return;
        SelectedState.Transitions.Remove(SelectedTransition);
        SelectedTransition = SelectedState.Transitions.LastOrDefault();
    }

    [RelayCommand]
    private void AddDidValue()
    {
        var d = new EditableDidValue { Notify = Script.Notify, KeyHex = "0xF190", BytesHex = "00" };
        Script.DidValues.Add(d);
        SelectedDidValue = d;
    }

    [RelayCommand]
    private void RemoveDidValue()
    {
        if (SelectedDidValue is null) return;
        Script.DidValues.Remove(SelectedDidValue);
        SelectedDidValue = Script.DidValues.LastOrDefault();
    }

    // ---- 契约（Task 7 AppShell 消费） ----

    public void LoadInitialPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            if (LoadFromText(json)) { _suitePath = path; FilePath = path; }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    public async Task LoadExternalAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            if (LoadFromText(json)) { _suitePath = path; FilePath = path; }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    public void Reset()
    {
        Script.States.Clear();
        Script.DidValues.Clear();
        Script.Name = "";
        Script.RequestIdHex = "0x7E0";
        Script.ResponseIdHex = "0x7E8";
        Script.IsExtendedFrame = false;
        Script.InitialState = "default";
        SelectedState = null;
        SelectedTransition = null;
        SelectedDidValue = null;
        FilePath = null;
        _suitePath = null;
        _savedJson = "";
        IsValidEcuScript = false;
        ErrorMessage = null;
        StatusMessage = "Ready";
        // 缺陷修复: _savedJson 在最后的 Script 变更通知之后才清空（States.Clear 等先触发
        // Script.Changed → HasUnsavedChanges=true 的陈旧值）, 这里补发一次让 WPF 绑定读到 false。
        OnPropertyChanged(nameof(HasUnsavedChanges));
    }
}
