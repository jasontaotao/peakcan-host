using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.Host.App.Services;
using PeakCan.Host.Core.Uds.Odx;

namespace PeakCan.Host.App.ViewModels.Uds;

/// <summary>
/// 加载 ODX / PDX 文件的 WPF ViewModel。持有 <see cref="IsBusy"/>
/// 状态 + 最后一次 <see cref="LastStatus"/> 用于 status bar。
/// 注意:此 VM 不弹文件对话框 — 文件 dialog 由 <c>OdxImportWindow</c>
/// 在 code-behind 里调用并把选中路径传给 <see cref="ImportAsync"/>。
/// </summary>
public sealed partial class OdxImportViewModel : ObservableObject
{
    private readonly IOdxImportService _service;
    private readonly ILogger<OdxImportViewModel>? _logger;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _lastStatus;

    public IRelayCommand<string> ImportCommand { get; }

    public OdxImportViewModel(IOdxImportService service, ILogger<OdxImportViewModel>? logger = null)
    {
        _service = service;
        _logger = logger;
        ImportCommand = new AsyncRelayCommand<string>(
            ImportAsync, _ => !IsBusy);
    }

    public async Task ImportAsync(string? odxPath)
    {
        IsBusy = true;
        LastStatus = $"Loading ODX: {odxPath}…";
        try
        {
            var result = await _service.ImportAsync(odxPath!).ConfigureAwait(true);
            LastStatus = result.HasError
                ? $"ODX load FAILED: {result.ErrorCode} — {result.ErrorMessage}"
                : $"ODX loaded: {result.DidCount} DIDs / {result.RoutineCount} Routines / {result.DtcCount} DTCs from {odxPath}";

            if (result.Warnings.Count > 0)
                LastStatus += $" (with {result.Warnings.Count} warning(s))";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
