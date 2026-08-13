using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.Trace;

namespace PeakCan.Host.App.ViewModels;

public sealed partial class TraceViewerViewModel
{
    // v3.4.2 PATCH: XAML "Clear" button binding. Empty string → parser
    // returns null → unfiltered rebuild.
    [RelayCommand]
    private void ClearCanIdFilter() => CanIdFilter = "";

    // v3.4.3 PATCH: detach per-source INPC subscriptions. Idempotent --
    // subtracting an absent handler is a no-op.
    private void DetachAllSourcePropertyHandlers()
    {
        foreach (var src in _registry.Sources)
            src.PropertyChanged -= OnAnySourcePropertyChanged;
    }

    // v3.4.3 PATCH: react to TraceSource.CanIdFilter changes by
    // refreshing frame counts + removing orphan chart series
    // synchronously. The TraceSource instance only exposes CanIdFilter
    // as INPC today, so the filter guard is a safety net for future
    // fields. v3.14.3 PATCH: do NOT call RebuildSignalsCore -- user
    // opt-ins in the signal table must survive filter changes; only
    // the per-row FrameCount + LatestValue columns are refreshed.
    private void OnAnySourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TraceSource.CanIdFilter)) return;
        if (_dbcService.Current is null) return;
        RefreshFrameCounts();
        RemoveOrphanChartSeries();
        ChartViewModel.SyncYAxes();
    }


    private void RebindMasterFromRegistry()
    {
        // Pure master-resolution step -- caller (OnRegistrySourcesChanged)
        // owns the per-source INPC detach/attach. Keeping this method
        // idempotent avoids re-binding when invoked after a SourcesChanged
        // event.
        if (_registry.Sources.Count == 0)
        {
            _masterService = null;
            MasterSourceId = "";
            return;
        }
        // Master invariant: prefer current MasterSourceId if still in Sources;
        // else fall back to Sources[0] (deterministic default).
        var newMaster = _registry.Sources.FirstOrDefault(
            s => s.SourceId == MasterSourceId) ?? _registry.Sources[0];
        MasterSourceId = newMaster.SourceId;
        _masterService = _allServices.TryGetValue(newMaster.SourceId, out var svc) ? svc : null;
    }
}