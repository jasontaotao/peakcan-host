namespace PeakCan.Host.App.Services.Nodes;

/// <summary>行为引擎编程所依赖的运行环境契约（由宿主服务实现）。</summary>
public interface INodeContext : IDisposable
{
    /// <summary>节点身份（源地址等）。</summary>
    NodeIdentity Identity { get; }

    /// <summary>节点运行时信号值表（SetSignalAction 写入、DbcSignals 载荷读取）。</summary>
    NodeRuntimeState Runtime { get; }

    /// <summary>注入时钟（行为内所有调度/延迟经此计，测试用 FakeTimeProvider 驱动）。</summary>
    TimeProvider Clock { get; }

    /// <summary>启动：注册本机身份/挂路由器等（由 <see cref="SimulatedNode.Start"/> 调用）。</summary>
    void Start();

    /// <summary>停止：Start 的反向清理（由 <see cref="SimulatedNode.Stop"/> 调用）。</summary>
    void Stop();

    /// <summary>fire-and-forget 发送（后端路由：J1939MessageRef→TP/单帧；CanMessageRef→ NotSupportedException）。</summary>
    void Send(MessageRef target, NodePayloadSource payload);

    /// <summary>behavior 上报活动（RuleMatched 等）→ NodeHostService.Activity。</summary>
    void Report(NodeActivityKind kind, string detail);

    /// <summary>后端引发（SDK 读线程）→ NodeHostService 入队。</summary>
    event Action<NodeMessageArrived>? MessageArrived;

    /// <summary>后端发送失败（异步通道，SDK 线程引发）→ NodeHostService 入队处理。</summary>
    event Action<Exception>? SendFailed;

    /// <summary><see cref="Report"/> 的订阅通道：宿主转成 <c>NodeActivity</c> 供 UI/场景记录。</summary>
    event Action<NodeActivityKind, string>? Reported;
}
