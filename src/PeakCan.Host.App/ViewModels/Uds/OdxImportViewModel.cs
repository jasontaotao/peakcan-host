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
public sealed partial class OdxImportViewModel : ObservableObject, IDisposable
{
    private readonly IOdxImportService _service;
    private readonly ILogger<OdxImportViewModel>? _logger;
    // v3.9.0 MINOR P6: per-import CancellationTokenSource. Created
    // at the start of ImportAsync, disposed in the finally. CancelImport
    // calls Cancel() on it; the service's CT check (in
    // ParseAndIndexOneDocument, between the DOP / DTC-DOP / ECU-JOB
    // walks) raises OperationCanceledException, which propagates out
    // of ImportAsync and the VM's finally resets IsBusy + disposes
    // the CTS.
    private CancellationTokenSource? _importCts;

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
        // v3.9.0 MINOR P6: own the per-import CTS. Linked to none
        // (no external cancel); CancelImport() calls Cancel() on
        // _importCts directly. The CTS is disposed in the finally
        // so a re-import (second call to ImportAsync) starts with
        // a fresh CTS, not a disposed one.
        _importCts = new CancellationTokenSource();
        try
        {
            var result = await _service.ImportAsync(odxPath!, _importCts.Token).ConfigureAwait(true);
            // v3.9.0 MINOR P6: if the import succeeded BEFORE the CT
            // was cancelled (race window: CancelImport runs after
            // the service returns but before the await completes),
            // overwrite the "Cancelled" status with the real result.
            // The CT.IsCancellationRequested check guards against
            // the race in the other direction (cancel arrived first,
            // service returned a result anyway because it was past
            // its last CT check).
            if (!_importCts.IsCancellationRequested)
            {
                LastStatus = result.HasError
                    ? $"ODX load FAILED: {result.ErrorCode} — {result.ErrorMessage}"
                    : $"ODX loaded: {result.DidCount} DIDs / {result.RoutineCount} Routines / {result.DtcCount} DTCs from {odxPath}";

                if (result.Warnings.Count > 0)
                    LastStatus += $" (with {result.Warnings.Count} warning(s))";
            }
        }
        catch (OperationCanceledException)
        {
            // v3.9.0 MINOR P6: the service raised OCE in response to
            // CancelImport. The CancelImport method already set the
            // status; don't overwrite it here.
        }
        finally
        {
            _importCts?.Dispose();
            _importCts = null;
            IsBusy = false;
        }
    }

    /// <summary>
    /// v3.8.8 PATCH F3 + v3.9.0 MINOR P6: window-close path. A 500 MB+
    /// ODX can take seconds to parse; if the user closes the ODX
    /// import window mid-import, the in-flight <see cref="ImportAsync"/>
    /// leaves <see cref="IsBusy"/> = <c>true</c> and the
    /// <see cref="ImportCommand"/>'s <c>CanExecute = _ =&gt; !IsBusy</c>
    /// lambda keeps the button disabled when the window is re-shown.
    /// The user is stuck until app restart.
    /// <para>
    /// <b>v3.8.8 fix:</b> a public <c>CancelImport()</c> method that
    /// clears <see cref="IsBusy"/>, surfaces a "Cancelled"
    /// <see cref="LastStatus"/>, and re-enables
    /// <see cref="ImportCommand"/>.
    /// </para>
    /// <para>
    /// <b>v3.9.0 P6 fix:</b> also call <c>Cancel()</c> on
    /// <c>_importCts</c> so the in-flight <c>_service.ImportAsync</c>
    /// task actually stops. The service's CT check (in
    /// <c>ParseAndIndexOneDocument</c>, between major parse
    /// segments) raises <see cref="OperationCanceledException"/>,
    /// which the VM's <c>ImportAsync</c> catch arm absorbs. The
    /// <c>CancelImport</c> "Cancelled" status is preserved (the
    /// OCE catch arm does NOT overwrite <c>LastStatus</c>).
    /// </para>
    /// </summary>
    public void CancelImport()
    {
        // v3.9.0 MINOR P6: cancel the in-flight import first so the
        // service's CT check raises OCE. The CT.IsCancellationRequested
        // guard in ImportAsync's success branch prevents the
        // post-cancel result from overwriting the "Cancelled" status.
        _importCts?.Cancel();
        IsBusy = false;
        LastStatus = "ODX import cancelled by user (window closed mid-import).";
        ImportCommand.NotifyCanExecuteChanged();
    }

    // v3.9.0 MINOR P6: implement IDisposable to satisfy CA1001
    // (OdxImportViewModel holds a CancellationTokenSource field that
    // must be disposed). The VM is a DI singleton; the host disposes
    // it at app shutdown. Dispose is idempotent: safe to call when
    // no in-flight import is active (no CTS to dispose).
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Cancel any in-flight import so the OCE catch arm in
        // ImportAsync can observe the cancellation and the
        // orphan's finally can dispose the CTS without a race.
        _importCts?.Cancel();
        _importCts?.Dispose();
        _importCts = null;
    }
}
