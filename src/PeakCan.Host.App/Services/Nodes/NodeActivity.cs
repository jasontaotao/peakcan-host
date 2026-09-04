namespace PeakCan.Host.App.Services.Nodes;

/// <summary>节点活动种类。</summary>
public enum NodeActivityKind : byte
{
    /// <summary>节点启动。</summary>
    Started,

    /// <summary>节点停止。</summary>
    Stopped,

    /// <summary>响应规则命中。</summary>
    RuleMatched,

    /// <summary>周期报文已发送。</summary>
    MessageSent,

    /// <summary>错误（如 ScriptAction 降级）。</summary>
    Error,
}

/// <summary>节点活动记录（UI 活动日志与将来 Scenario 的统一通道；触发方线程同步引发，UI 自行 marshal）。</summary>
/// <param name="NodeName">活动所属节点名。</param>
/// <param name="Kind">活动种类。</param>
/// <param name="Detail">自由文本细节（规则描述、异常消息等）。</param>
/// <param name="TimestampUtc">活动时间（UTC）。</param>
public sealed record NodeActivity(string NodeName, NodeActivityKind Kind, string Detail, DateTimeOffset TimestampUtc);
