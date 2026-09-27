using System.Diagnostics;
using System.Globalization;
using A2lEditor.Core.Layout;
using PeakCan.Host.Core.Xcp.Calibration;
using PeakCan.Host.Core.Xcp.Protocol;

namespace PeakCan.Host.Core.Xcp.Bench;

/// <summary>写交错场景结果（B-1 host 侧事实 + B-2 耗时）。</summary>
public sealed record WriteInterleavedResult(
    int ReadsBefore,
    int ReadsBeforeFailed,
    IReadOnlyList<double> ValuesBefore,
    int ReadsAfter,
    int ReadsAfterFailed,
    IReadOnlyList<double> ValuesAfter,
    TimeSpan WriteElapsed,
    ReadModifyRestoreResult Rmr)
{
    /// <summary>B-1 host 侧口径：写后读流全部成功 = 采集命令流未中断。</summary>
    public bool ReadsContinuedAfterWrite => ReadsAfterFailed == 0 && ReadsAfter > 0;
}

/// <summary>
/// S7-T4 B 系场景：写前后轮询读流不中断（B-1 采集继续的 host 侧事实）+
/// 写步耗时（B-2）。真机 DAQ 表行为由人工在批次运行时观察（spec T4 补记口径）。
/// 单拍读 = SET_MTA+UPLOAD（持门）；失败记归因不中断批次（宁全不全）。
/// </summary>
public static class WriteInterleavedScenario
{
    public static async Task<WriteInterleavedResult> RunAsync(
        XcpCalibrationWriter writer,
        XcpMaster master,
        ValueContract contract,
        A2lEditor.Core.Model.A2lDocument document,
        double testPhysical,
        string scenarioName,
        int beats = 2,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(master);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegative(beats);

        var tp = timeProvider ?? TimeProvider.System;
        var runs = CalibrationRunPlanner.PlanWriteRuns(
            document, contract.Segments[0].Address, contract.TotalByteLength);

        // ---- 写前读拍 ----
        var (readsBefore, failedBefore, valuesBefore) = await PollBeatsAsync(
            master, runs, contract, beats, ct).ConfigureAwait(false);

        // ---- RMR 写（内含保存/写/校验/还原/确认）----
        var t0 = tp.GetTimestamp();
        var rmr = await ReadModifyRestore.RunAsync(
            writer, master, contract, document, testPhysical, scenarioName, ct: ct).ConfigureAwait(false);
        var elapsed = tp.GetElapsedTime(t0);

        // ---- 写后读拍 ----
        var (readsAfter, failedAfter, valuesAfter) = await PollBeatsAsync(
            master, runs, contract, beats, ct).ConfigureAwait(false);

        return new WriteInterleavedResult(
            readsBefore, failedBefore, valuesBefore,
            readsAfter, failedAfter, valuesAfter,
            elapsed, rmr);
    }

    private static async Task<(int Reads, int Failed, IReadOnlyList<double> Values)> PollBeatsAsync(
        XcpMaster master,
        IReadOnlyList<CalibrationWriteRun>? runs,
        ValueContract contract,
        int beats,
        CancellationToken ct)
    {
        var values = new List<double>();
        var failed = 0;
        if (runs is null || runs.Count == 0)
            return (0, 0, values);

        for (var i = 0; i < beats; i++)
        {
            try
            {
                var raw = new byte[contract.TotalByteLength];
                using (var gate = await master.EnterMemorySequenceAsync(ct).ConfigureAwait(false))
                {
                    foreach (var run in runs)
                    {
                        var chunk = await XcpUploadReader.ReadAsync(
                            master, run.PhysicalAddress, run.ByteLength, ct).ConfigureAwait(false);
                        chunk.CopyTo(raw, run.SourceOffset);
                    }
                }
                values.Add(contract.Decode(raw));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单拍失败是观测事实（真机超时/负响应都可能出现），归因计数不中断批次。
                failed++;
            }
        }
        return (beats, failed, values);
    }

    private static string Format(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
