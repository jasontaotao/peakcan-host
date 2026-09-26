using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using PeakCan.Host.Core.Xcp.Record;
using PeakCan.Host.Core.Xcp.Replay;

namespace PeakCan.Host.App.ViewModels.Xcp;

/// <summary>回放通道清单行（勾选 → 触发渲染请求）。</summary>
public sealed partial class XcpReplayChannelItemViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public XcpReplayChannelItemViewModel(string name, string unit, int sampleCount, int gapCount,
        double minTime, double maxTime, string lastValueText, bool hasMetadata)
    {
        Name = name;
        Unit = unit;
        SampleCount = sampleCount;
        GapCount = gapCount;
        MinTime = minTime;
        MaxTime = maxTime;
        LastValueText = lastValueText;
        HasMetadata = hasMetadata;
    }

    public string Name { get; }
    public string Unit { get; }
    public int SampleCount { get; }
    public int GapCount { get; }
    public double MinTime { get; }
    public double MaxTime { get; }
    public string LastValueText { get; }
    public bool HasMetadata { get; }
}

/// <summary>
/// S6-T3 回放面板 VM（spec D3 / T2 补记口径）：文件加载 → Core 解码 → 通道清单 +
/// 元数据/警示态。渲染面归视图 code-behind（ScottPlot WpfPlot 直操，与 TraceViewer 同款分层）。
/// </summary>
public sealed class XcpReplayPanelViewModel : ObservableObject
{
    private readonly Func<string, Mdf4FileData> _readFile;
    private readonly Func<string?>? _currentA2lSha256Provider;
    private XcpReplayResult? _result;

    public XcpReplayPanelViewModel(
        Func<string, Mdf4FileData>? readFile = null,
        Func<string?>? currentA2lSha256Provider = null)
    {
        _readFile = readFile ?? Mdf4StreamReader.Read;
        _currentA2lSha256Provider = currentA2lSha256Provider;
    }

    public ObservableCollection<XcpReplayChannelItemViewModel> Channels { get; } = new();

    public IReadOnlyList<XcpReplayGapMark> GapMarks { get; private set; } = [];

    public int RenderRequestCount { get; private set; }

    public string FilePathText { get; private set; } = "（未加载）";

    public string StatusText { get; private set; } = string.Empty;

    /// <summary>指纹/元数据降级警示（非空 = 红字面）。</summary>
    public string WarningText { get; private set; } = string.Empty;

    public string MetadataSourceText { get; private set; } = string.Empty;

    /// <summary>加载成功 / 勾选变化后 +1；视图 code-behind 据此重画。</summary>
    public event Action? RenderRequested;

    /// <summary>
    /// 当前 A2L 指纹基准：SHA-256(UTF8(doc.RawText)) 大写十六进制——与包侧
    /// ContractSnapshot.Sha256OfText 同口径（对解析文本而非文件字节，spec S1 A1）。
    /// </summary>
    public static string ComputeA2lSha256(string rawText)
    {
        ArgumentException.ThrowIfNullOrEmpty(rawText);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawText)));
    }

    /// <summary>
    /// 加载 MF4（主记录或触发记录）。解析/解码失败 → 状态区红字，不裸抛（S4 sink 故障面惯例）。
    /// </summary>
    public void LoadFile(string path)
    {
        try
        {
            var file = _readFile(path);
            var result = XcpReplayDecoder.Decode(file, currentA2lSha256: _currentA2lSha256Provider?.Invoke());
            _result = result;

            Channels.Clear();
            foreach (var ch in result.Channels)
            {
                var valid = ch.Times.Count > 0 && !ch.Invalid[^1];
                Channels.Add(new XcpReplayChannelItemViewModel(
                    ch.ObjectName,
                    ch.Metadata?.Unit ?? string.Empty,
                    ch.Times.Count,
                    ch.GapCountOfInvalidRows(),
                    ch.Times.Count > 0 ? ch.Times[0] : 0,
                    ch.Times.Count > 0 ? ch.Times[^1] : 0,
                    valid ? ch.Values[^1].ToString("G6", System.Globalization.CultureInfo.InvariantCulture) : "—",
                    ch.Metadata is not null));
                // 勾选变化 → 渲染请求（视图 code-behind 重画曲线）。
                var item = Channels[^1];
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(XcpReplayChannelItemViewModel.IsSelected))
                        RequestRender();
                };
            }

            GapMarks = result.GapMarks;
            FilePathText = path;
            MetadataSourceText = result.MetadataSource switch
            {
                XcpReplayMetadataSource.SnapshotAttachment => "元数据：文件内快照",
                XcpReplayMetadataSource.ExternalContracts => "元数据：当前 A2L",
                _ => "元数据：无（仅对象名）",
            };
            WarningText = result.FingerprintState == XcpReplayFingerprintState.Mismatched
                ? "A2L 指纹与记录文件不符——单位/限值已停用，曲线仍按记录值显示。"
                : string.Empty;
            StatusText = $"已加载 {System.IO.Path.GetFileName(path)}（{result.Channels.Count} 通道，{result.GapMarks.Count} 条归因标注）";

            RequestRender();
        }
        catch (Exception ex) when (ex is FormatException or System.IO.IOException or UnauthorizedAccessException)
        {
            _result = null;
            Channels.Clear();
            GapMarks = [];
            FilePathText = path;
            MetadataSourceText = string.Empty;
            WarningText = string.Empty;
            StatusText = $"加载失败：{ex.Message}";
            RequestRender();
        }
    }

    /// <summary>渲染取数面（code-behind 用）：勾选通道的曲线数据 + 标注。</summary>
    public XcpReplayResult? Result => _result;

    private void RequestRender()
    {
        RenderRequestCount++;
        RenderRequested?.Invoke();
    }
}

/// <summary>空窗行计数（Invalid 标记直读，含 NaN 行）。</summary>
file static class XcpReplayChannelExtensions
{
    public static int GapCountOfInvalidRows(this XcpReplayChannel ch)
        => ch.Invalid.Count(i => i);
}
