using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Analysis.Nm;

namespace PeakCan.Host.App.ViewModels;

public sealed record NmCapabilityChipVm(string Name, bool Available, string Tooltip);
public sealed record NmMessageRowVm(string CanIdHex, string? Name, string? Sender, int FrameCount,
    string CycleText, string FirstSeenText, string LastSeenText, string Status);
public sealed record NmTimelineSegmentVm(double Left, double Width, string Kind, double Start, double End);
public sealed record NmTimelineRowVm(string Label, IReadOnlyList<NmTimelineSegmentVm> Segments);
public sealed record NmEventRowVm(double T, string TimeText, string Type, string Subject,
    string Detail, string EvidenceKind, int EpisodeIndex);

public sealed partial class TraceViewerViewModel
{
    private const double NmTimelineWidth = 1000.0;

    [ObservableProperty] private bool _nmHasReport;
    [ObservableProperty] private bool _nmIsAnalyzing;
    [ObservableProperty] private string _nmStatus = "";
    [ObservableProperty] private string _nmProfileSummary = "";
    [ObservableProperty] private string _nmMissingText = "";

    public ObservableCollection<NmCapabilityChipVm> NmCapabilityChips { get; } = new();
    public ObservableCollection<NmMessageRowVm> NmMessageRows { get; } = new();
    public ObservableCollection<NmTimelineRowVm> NmTimelineRows { get; } = new();
    public ObservableCollection<NmEventRowVm> NmEventRows { get; } = new();

    private Views.TraceViewerViewNmPanel? _nmPanelContent;
    private CancellationTokenSource? _nmCts;
    public Views.TraceViewerViewNmPanel? NmPanelContent
    {
        get
        {
            if (_nmPanelContent is null)
            {
                _nmPanelContent = new Views.TraceViewerViewNmPanel { DataContext = this };
                _ = RunNmAnalysisAsync(null);
            }
            return _nmPanelContent;
        }
    }

    public static IReadOnlyList<NmTimelineRowVm> BuildTimelineRows(NmReport report)
        => report.Messages.Select(m =>
        {
            var segments = m.Activity.Select(s =>
            {
                var left = ToX(s.Start, report.Window);
                var right = ToX(s.End, report.Window);
                return new NmTimelineSegmentVm(left, Math.Max(right - left, 1), s.Kind, s.Start, s.End);
            }).ToArray();
            return new NmTimelineRowVm($"{m.CanIdHex} {m.Name}", segments);
        }).ToArray();

    public static IReadOnlyList<NmCapabilityChipVm> BuildCapabilityChips(NmReport report)
        => report.Capabilities.Select(c => new NmCapabilityChipVm(
            c.Capability.ToString(), c.Available,
            c.Available ? "可用" : string.Join("; ", c.Missing.Select(m => m.Why)))).ToArray();

    private static double ToX(double t, NmWindow w)
    {
        var span = Math.Max(w.End - w.Start, 1e-9);
        return Math.Clamp((t - w.Start) / span * NmTimelineWidth, 0, NmTimelineWidth);
    }

    internal async Task RunNmAnalysisAsync((double Start, double End)? window)
    {
        _nmCts?.Cancel();
        _nmCts = new CancellationTokenSource();
        var ct = _nmCts.Token;

        NmIsAnalyzing = true;
        NmStatus = "分析中…";
        try
        {
            var snapshot = await ((IChatToolContext)this).GetNmSnapshotAsync(new NmToolQuery(
                TStart: window?.Start, TEnd: window?.End,
                WindowRef: window is null ? "full" : "viewport"), ct);
            ApplyNmReport(snapshot);
        }
        catch (OperationCanceledException)
        {
            NmStatus = "分析已取消";
        }
        catch (Exception ex)
        {
            NmStatus = "分析失败：" + ex.Message;
        }
        finally
        {
            NmIsAnalyzing = false;
        }
    }

    internal void ApplyNmReport(NmToolSnapshot snapshot)
    {
        var report = snapshot.Report;
        NmHasReport = true;
        NmProfileSummary = $"来源 {snapshot.Profile.Provenance.ResolvedFrom} · NM 报文 {snapshot.Profile.Messages.Count(m => m.IsNm)} · " +
            $"周期 {snapshot.Profile.Timings.CycleMs?.ToString(CultureInfo.InvariantCulture) ?? "—"}ms · 超时 {snapshot.Profile.Timings.TimeoutMs?.ToString(CultureInfo.InvariantCulture) ?? "—"}ms";
        NmMissingText = report.MissingInputs.Count == 0
            ? "输入齐备"
            : $"缺项 {report.MissingInputs.Count} → 补全";

        NmCapabilityChips.Clear();
        foreach (var chip in BuildCapabilityChips(report)) NmCapabilityChips.Add(chip);
        NmMessageRows.Clear();
        foreach (var m in report.Messages)
            NmMessageRows.Add(new NmMessageRowVm(m.CanIdHex, m.Name, m.Sender, m.FrameCount,
                m.Cycle.MedianMs is { } med ? $"{med:F0}ms" : "—",
                TraceTimeFormatter.Format(m.FirstSeen, null),
                TraceTimeFormatter.Format(m.LastSeen, null),
                m.SilentBeyondTimeout ? "超出超时" : "正常"));
        NmTimelineRows.Clear();
        foreach (var row in BuildTimelineRows(report)) NmTimelineRows.Add(row);
        NmEventRows.Clear();
        foreach (var e in report.Events.Take(500))
            NmEventRows.Add(new NmEventRowVm(e.T, TraceTimeFormatter.Format(e.T, null), e.Type,
                e.Node ?? e.CanIdHex ?? "", e.Detail, e.EvidenceKind, e.EpisodeIndex));

        NmStatus = report.Events.Count > 500
            ? $"显示前 500 条事件（共 {report.Events.Count}）"
            : $"事件 {report.Events.Count} 条";
        if (report.Messages.Count == 0)
            NmStatus = "本窗口未发现 NM 帧";
    }

    [RelayCommand]
    private Task AnalyzeNmFullAsync() => RunNmAnalysisAsync(null);

    [RelayCommand]
    private Task AnalyzeNmWindowAsync()
    {
        var viewports = ChartViewModel.CaptureViewports();
        var vp = viewports.Count > 0 ? viewports[0] : null;
        return RunNmAnalysisAsync(vp is null ? null : (vp.XMin, vp.XMax));
    }

    [RelayCommand]
    private void SeekToNmEvent(NmEventRowVm? row)
    {
        if (row is not null) _masterService?.Seek(row.T);
    }

    [RelayCommand]
    private void SeekToNmTime(double t) => _masterService?.Seek(t);
}
