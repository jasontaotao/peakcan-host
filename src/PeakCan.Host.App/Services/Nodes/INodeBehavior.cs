namespace PeakCan.Host.App.Services.Nodes;

/// <summary>节点行为引擎：由宿主服务（Task 16 NodeHostService）驱动生命周期与报文分发。</summary>
public interface INodeBehavior
{
    /// <summary>绑定运行上下文并启动周期扫描（周期表开始计时）。</summary>
    void Attach(INodeContext ctx);

    /// <summary>解除绑定：停掉扫描定时器、清空 pending 规则；之后 <see cref="OnMessageArrived"/> 为 no-op。</summary>
    void Detach();

    /// <summary>由 NodeHostService 的 consumer 任务调用（不在 SDK 读线程）。</summary>
    void OnMessageArrived(NodeMessageArrived message);
}
