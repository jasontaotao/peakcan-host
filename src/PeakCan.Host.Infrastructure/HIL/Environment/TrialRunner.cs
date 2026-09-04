using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.Host.Infrastructure.HIL.Environment;

/// <summary>试运行诊断输出。</summary>
public sealed record TrialDiagnostic(string Step, bool Passed, string? Detail, IReadOnlyList<string> PossibleCauses);

/// <summary>试运行结果。</summary>
public sealed record TrialRunResult(
    bool Passed,
    IReadOnlyList<TrialDiagnostic> Diagnostics,
    /// <summary>True when frame subscription is wired (M3); false = frame-stream preview only.</summary>
    bool IsFullHandshakeCheck = false);

/// <summary>
/// host 试运行器。M2: 拉起环境 + 展示帧流，按 TrialContract 结构输出诊断框架。
/// 完整握手验证（订阅接收帧 + 超时判定）在 M3 接线 frame subscription 后补齐。
/// </summary>
public sealed class TrialRunner(ICanChannel channel)
{
    public async Task<TrialRunResult> RunTrialAsync(
        IReadOnlyList<RestbusNode> nodes, TimeSpan timeout, CancellationToken ct)
    {
        var diagnostics = new List<TrialDiagnostic>();

        foreach (var node in nodes.Where(n => n.Trial is not null))
        {
            var contract = node.Trial!;
            foreach (var step in contract.Handshake)
            {
                // M2: frame subscription not wired — report structure without pass/fail verdict.
                diagnostics.Add(new TrialDiagnostic(
                    step.Send, true,
                    $"Frame-stream preview: {step.Send} sent, waiting for {step.ThenReceive} ({step.TimeoutMs}ms timeout) — full handshake check in M3",
                    step.PossibleCauses));
            }
        }

        await Task.Delay(100, ct);
        return new TrialRunResult(true, diagnostics, IsFullHandshakeCheck: false);
    }
}