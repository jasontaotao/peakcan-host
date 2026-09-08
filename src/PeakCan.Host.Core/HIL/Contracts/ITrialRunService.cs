using PeakCan.HIL.Core.Dbc;
using PeakCan.Host.Core;

namespace PeakCan.Host.Core.HIL.Contracts;

/// <summary>一次试运行使用的已连接通道上下文。Channel 必须是宿主已连接实例；服务不得断开。</summary>
public sealed record TrialChannelContext(
    string LogicalName,
    string? DisplayName,
    ICanChannel Channel,
    DbcDocument? Dbc);

/// <summary>试运行编排契约（App 层调用，Infrastructure 实现）。</summary>
public interface ITrialRunService
{
    Task<TrialRunResult> RunAsync(
        string suitePath,
        IReadOnlyList<TrialChannelContext> channels,
        CancellationToken ct = default);
}
