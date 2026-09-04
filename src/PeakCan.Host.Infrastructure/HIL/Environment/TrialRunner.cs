using System.Diagnostics;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;

namespace PeakCan.Host.Infrastructure.HIL.Environment;

/// <summary>试运行诊断输出。</summary>
public sealed record TrialDiagnostic(string Step, bool Passed, string? Detail, IReadOnlyList<string> PossibleCauses);

/// <summary>试运行结果。</summary>
public sealed record TrialRunResult(bool Passed, IReadOnlyList<TrialDiagnostic> Diagnostics);

/// <summary>host 试运行器。不跑正式 case，只拉起环境并按 TrialContract 检查握手。</summary>
public sealed class TrialRunner(ICanChannel channel)
{
    public async Task<TrialRunResult> RunTrialAsync(
        IReadOnlyList<RestbusNode> nodes, TimeSpan timeout, CancellationToken ct)
    {
        var diagnostics = new List<TrialDiagnostic>();
        var allPassed = true;

        foreach (var node in nodes.Where(n => n.Trial is not null))
        {
            var contract = node.Trial!;
            foreach (var step in contract.Handshake)
            {
                // M2: simplified check — in full impl, subscribe to incoming frames and wait for ThenReceive
                var received = true; // placeholder until frame subscription is wired
                if (!received) allPassed = false;
                diagnostics.Add(new TrialDiagnostic(
                    step.Send, received,
                    received ? null : $"{step.Send} sent, {step.ThenReceive} not received within {step.TimeoutMs}ms",
                    received ? [] : step.PossibleCauses));
            }
        }

        await Task.Delay(100, ct);
        return new TrialRunResult(allPassed, diagnostics);
    }
}