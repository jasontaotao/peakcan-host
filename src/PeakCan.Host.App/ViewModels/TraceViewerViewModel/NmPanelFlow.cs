using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PeakCan.Host.App.Services.ChatTools;
using PeakCan.Host.App.Services.Nm;
using PeakCan.Host.Core.Analysis;
using PeakCan.Host.Core.Analysis.Nm;

namespace PeakCan.Host.App.ViewModels;

public sealed record NmCapabilityChipVm(string Name, bool Available, string Tooltip);
public sealed record NmTimelineSegmentVm(double Left, double Width, string Kind, double Start, double End);
public sealed record NmTimelineRowVm(string Label, IReadOnlyList<NmTimelineSegmentVm> Segments);
public sealed record NmEventRowVm(double T, string TimeText, string Type, string Subject,
    string Detail, string EvidenceKind, int EpisodeIndex);

public sealed partial class TraceViewerViewModel
{
    private const double NmTimelineWidth = 1000.0;

    [ObservableProperty] private bool _nmHasReport;
    [ObservableProperty] private bool _nmIsAnalyzing;
    [ObservableProperty] private bool _nmCanCancel;
    [ObservableProperty] private string _nmStatus = "";
    [ObservableProperty] private string _nmProfileSummary = "";
    [ObservableProperty] private string _nmMissingText = "";

    public ObservableCollection<NmCapabilityChipVm> NmCapabilityChips { get; } = new();
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
            // spec §9.1：时间条默认每个 NM 报文一行，行标签按 DBC 发送节点标注。
            // DBC 未给出 Name/Sender 时不渲染空括号内容，用 "—" 占位。
            var who = string.IsNullOrEmpty(m.Sender) ? "—" : m.Sender;
            var name = string.IsNullOrEmpty(m.Name) ? "" : " " + m.Name;
            return new NmTimelineRowVm($"{m.CanIdHex}{name} ({who})", segments);
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
        // 旧请求只取消不 Dispose：取消令牌仍可能被在途的 Task.Run 使用。
        _nmCts?.Cancel();
        var cts = new CancellationTokenSource();
        _nmCts = cts;
        var ct = cts.Token;

        NmIsAnalyzing = true;
        NmCanCancel = true;
        NmStatus = "分析中…";
        try
        {
            var snapshot = await ((IChatToolContext)this).GetNmSnapshotAsync(new NmToolQuery(
                TStart: window?.Start, TEnd: window?.End,
                WindowRef: window is null ? "full" : "viewport"), ct);
            // 被更新一次分析替换（或窗口已关闭）时丢弃结果，避免旧报告覆盖新报告。
            if (IsCurrentNmRun(cts)) ApplyNmReport(snapshot);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentNmRun(cts)) NmStatus = "分析已取消";
        }
        catch (Exception ex)
        {
            if (IsCurrentNmRun(cts))
            {
                NmStatus = "分析失败：" + ex.Message;
                ClearNmReport();
            }
        }
        finally
        {
            // 只有当前分析才回写状态：被新一次分析替换的旧任务不得把按钮/状态改回去。
            if (IsCurrentNmRun(cts))
            {
                NmIsAnalyzing = false;
                NmCanCancel = false;
            }
        }
    }

    /// <summary>当前分析是否仍是最近一次（窗口 Dispose 后 _nmCts 置 null，一律不算）。</summary>
    private bool IsCurrentNmRun(CancellationTokenSource cts) => ReferenceEquals(_nmCts, cts);

    /// <summary>失败时清空旧报告，避免换 trace 后残留上一次的段/事件/能力/摘要。</summary>
    private void ClearNmReport()
    {
        NmHasReport = false;
        NmProfileSummary = "";
        NmMissingText = "";
        NmCapabilityChips.Clear();
        NmTimelineRows.Clear();
        NmEventRows.Clear();
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

    /// <summary>导出 NM 报告：按用户选择的扩展名写 Markdown 或 CSV。</summary>
    [RelayCommand]
    private async Task ExportNmReportAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md|CSV (*.csv)|*.csv",
            FileName = "nm-report.md",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            // 导出不复用面板的 _nmCts：即使面板分析被取消，导出本身仍应完成。
            var snapshot = await ((IChatToolContext)this).GetNmSnapshotAsync(
                new NmToolQuery(), CancellationToken.None);
            var text = dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                ? NmReportWriter.WriteCsv(snapshot.Report)
                : NmReportWriter.WriteMarkdown(snapshot);
            await File.WriteAllTextAsync(dlg.FileName, text, Encoding.UTF8);
            NmStatus = "已导出：" + dlg.FileName;
        }
        catch (Exception ex)
        {
            // 导出失败（无 trace、无写权限等）必须在面板上可见，禁止静默吞掉。
            NmStatus = "导出失败：" + ex.Message;
        }
    }

    /// <summary>取消面板分析；只取消 NM 通道，不影响聊天请求。</summary>
    [RelayCommand]
    private void CancelNmAnalysis() => _nmCts?.Cancel();

    [RelayCommand]
    private void SeekToNmEvent(NmEventRowVm? row)
    {
        if (row is not null) _masterService?.Seek(row.T);
    }

    [RelayCommand]
    private void SeekToNmTime(double t) => _masterService?.Seek(t);
}
