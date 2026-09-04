using System.Collections.Concurrent;
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
    /// <summary>True when frame subscription was wired (full check); false = frame-stream preview only.</summary>
    bool IsFullHandshakeCheck = false);

/// <summary>
/// host 试运行器。订阅通道帧事件，按 TrialContract 握手链逐步检查 ThenReceive 是否在 timeout 内到达。
/// 消息名→ID 解析由 EnvironmentRuntime 的 DBC 或模板帧定义提供（通过 lookupDelegate 注入）。
/// </summary>
public sealed class TrialRunner(ICanChannel channel)
{
    /// <summary>
    /// 消息名→CAN ID 查找委托。由 HilRunnerService 注入（基于 DBC 或模板 FixedHex ID）。
    /// 返回 null 表示消息名不可解析（该步自动通过，不做超时判定）。
    /// </summary>
    public Func<string, uint?>? MessageIdLookup { get; set; }

    public async Task<TrialRunResult> RunTrialAsync(
        IReadOnlyList<RestbusNode> nodes, TimeSpan timeout, CancellationToken ct)
    {
        var diagnostics = new List<TrialDiagnostic>();
        var allPassed = true;
        var lookup = MessageIdLookup;
        var isFullCheck = lookup is not null;

        // Subscribe to incoming frames
        var receivedQueue = new ConcurrentQueue<CanFrame>();
        void OnFrameReceived(CanFrame frame) => receivedQueue.Enqueue(frame);
        channel.FrameReceived += OnFrameReceived;

        try
        {
            foreach (var node in nodes.Where(n => n.Trial is not null))
            {
                var contract = node.Trial!;
                foreach (var step in contract.Handshake)
                {
                    if (!isFullCheck)
                    {
                        // Preview mode: report structure without verdict
                        diagnostics.Add(new TrialDiagnostic(
                            step.Send, true,
                            $"Frame-stream preview: {step.Send} → {step.ThenReceive} ({step.TimeoutMs}ms) — full check requires MessageIdLookup",
                            step.PossibleCauses));
                        continue;
                    }

                    // Full check: wait for ThenReceive frame
                    var expectedId = step.ThenReceive is not null ? lookup!(step.ThenReceive) : null;
                    if (expectedId is null)
                    {
                        diagnostics.Add(new TrialDiagnostic(
                            step.Send, true,
                            $"'{step.ThenReceive}' not resolvable — skipping timeout check.",
                            []));
                        continue;
                    }

                    var deadline = System.Environment.TickCount64 + step.TimeoutMs;
                    var received = false;
                    while (System.Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
                    {
                        if (receivedQueue.TryDequeue(out var frame) && frame.Id.Raw == expectedId.Value)
                        {
                            received = true;
                            break;
                        }
                        await Task.Delay(10, ct);
                    }

                    if (!received) allPassed = false;
                    diagnostics.Add(new TrialDiagnostic(
                        step.Send, received,
                        received
                            ? $"{step.ThenReceive} received within {step.TimeoutMs}ms"
                            : $"{step.Send} sent, {step.ThenReceive} NOT received within {step.TimeoutMs}ms",
                        received ? [] : step.PossibleCauses));
                }
            }

            await Task.Delay(100, ct);
            return new TrialRunResult(allPassed, diagnostics, IsFullHandshakeCheck: isFullCheck);
        }
        finally
        {
            channel.FrameReceived -= OnFrameReceived;
        }
    }
}