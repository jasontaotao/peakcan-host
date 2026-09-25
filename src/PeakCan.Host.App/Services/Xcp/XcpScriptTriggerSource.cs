using PeakCan.Host.App.Services.Scripting;
using PeakCan.Host.Core.Xcp.Record;

namespace PeakCan.Host.App.Services.Xcp;

/// <summary>
/// S4-T6 脚本触发源（spec D6 触发源 v0.2）：复用脚本引擎现成出站口
/// <see cref="ScriptOutputHub.OutputReceived"/>——脚本输出行以 <see cref="TriggerPrefix"/>
/// 开头（大小写不敏感）即触发捕获。事件链内不外抛（解析/触发异常记入
/// <see cref="LastError"/> 可见）；捕获进行中的重复触发由引擎拒绝计数承载。
/// </summary>
public sealed class XcpScriptTriggerSource : IDisposable
{
    /// <summary>脚本触发行前缀（例：<c>xcp-trigger: overrun detected</c>）。</summary>
    public const string TriggerPrefix = "xcp-trigger:";

    private readonly ScriptOutputHub _hub;
    private readonly XcpTriggerRecordEngine _engine;
    private long _emitted;

    public XcpScriptTriggerSource(ScriptOutputHub hub, XcpTriggerRecordEngine engine)
    {
        _hub = hub;
        _engine = engine;
        _hub.OutputReceived += OnOutput;
    }

    /// <summary>已受理（含被引擎拒绝）的触发行数。</summary>
    public long EmittedCount => Interlocked.Read(ref _emitted);

    /// <summary>最近一次触发异常（null = 无）。</summary>
    public Exception? LastError { get; private set; }

    private void OnOutput(ScriptOutputLine line)
    {
        if (line.Message?.TrimStart().StartsWith(TriggerPrefix, StringComparison.OrdinalIgnoreCase) != true)
            return;
        Interlocked.Increment(ref _emitted);
        _ = FireAsync();
    }

    private async Task FireAsync()
    {
        try
        {
            await _engine.TriggerAsync(DateTimeOffset.Now, "script").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex;
        }
    }

    public void Dispose() => _hub.OutputReceived -= OnOutput;
}
