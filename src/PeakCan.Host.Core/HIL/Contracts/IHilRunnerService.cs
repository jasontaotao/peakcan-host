using System.Collections.Generic;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;

namespace PeakCan.HIL.Core.HIL;

/// <summary>
/// Decouples the WPF App layer from the Infrastructure-layer HilRunnerService.
/// App project references Core but not Infrastructure — this interface is the bridge.
/// </summary>
public interface IHilRunnerService
{
    Task<TestSuiteResult> RunAsync(
        PeakCan.HIL.Core.HIL.HilRunRequest request,
        IProgress<TestProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>最近一次 RunAsync 实际解析的 DBC 文档；未运行或无 DBC 时为 null。</summary>
    DbcDocument? LastDbcDocument { get; }

    /// <summary>多通道运行各通道的 DBC 文档字典（按 ChannelId）；单通道或未运行时为 null。</summary>
    IReadOnlyDictionary<ChannelId, DbcDocument>? LastPerChannelDbcs { get; }

    /// <summary>本次 run 实际使用的 case-log 目录（CaptureCaseLogs 成功时非 null）。</summary>
    string? LastCaseLogDirectory { get; }
}
