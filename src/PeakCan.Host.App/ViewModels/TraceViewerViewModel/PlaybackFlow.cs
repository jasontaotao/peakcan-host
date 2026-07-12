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


    private void PropagateLoopToAllServices()
    {
        foreach (var svc in _allServices.Values)
            svc.Loop = Loop;
    }

    private void PropagateSpeedToAllServices()
    {
        foreach (var svc in _allServices.Values)
            svc.SetSpeed(Speed);
    }




    // v3.4.3 PATCH: detach per-source INPC subscriptions. Idempotent --
    // subtracting an absent handler is a no-op (mirrors the existing
    // DetachAllServiceHandlers pattern).
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


    // v3.3.0 MINOR: master timeline is the clock; each non-master is
    // positioned at the proportional point of its own total duration.
    // Formula: nonMaster.t = (master.t / master.totalDuration) * nonMaster.totalDuration.
    // Clamp ratio to [0, 1] to handle transient slider overshoot.
    // v3.8.6 PATCH H1: also clamp masterT to [0, masterDur] before the
    // master branch svc.Seek(masterT) call. Symmetric-miss of the v3.8.4
    // L1 Replay-tab clamp; the comment at the call site said "ratio"
    // clamp handles overshoot, but the master direct-seek got the raw
    // (potentially out-of-range) value, leaving no frame in range after
    // the timeline walked past _frames.Count.
    private void SeekAllToProportionalTime(double masterT)
    {
        if (_masterService is null) return;
        var masterDur = _masterService.TotalDuration;
        if (masterDur <= 0)
        {
            // No total duration to clamp against -- defensively drop negatives.
            if (masterT < 0) masterT = 0;
            _masterService.Seek(masterT);
            return;
        }
        var clampedMasterT = Math.Clamp(masterT, 0.0, masterDur);
        var ratio = Math.Clamp(clampedMasterT / masterDur, 0.0, 1.0);
        foreach (var (sourceId, svc) in _allServices)
        {
            if (sourceId == MasterSourceId)
            {
                svc.Seek(clampedMasterT);
            }
            else
            {
                svc.Seek(ratio * svc.TotalDuration);
            }
        }
    }

    private void RebindMasterFromRegistry()
    {
        // Pure master-resolution step -- caller (OnRegistrySourcesChanged)
        // owns the attach/detach lifecycle via AttachAllServiceHandlers /
        // DetachAllServiceHandlers. Keeping this method idempotent avoids
        // double-attaching the FrameEmitted + PlaybackEnded handlers when
        // invoked after a SourcesChanged event.
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