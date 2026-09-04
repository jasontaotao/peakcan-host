namespace PeakCan.Host.App.Services.Nodes;

/// <summary>虚拟 ECU 角色（身份 + 行为 + 运行时信号表）。生命周期由 <see cref="NodeHostService"/> 驱动。</summary>
public sealed class SimulatedNode
{
    /// <summary>
    /// 节点配置（身份、周期表、规则表）。<see cref="NodeHostService.UpdateNode"/> 在
    /// 节点未运行时替换（节点实例保留 → <see cref="Runtime"/> 跨更新不丢）。
    /// </summary>
    public NodeConfig Config { get; internal set; }

    /// <summary>运行时信号值表（跨启停保持）。</summary>
    public NodeRuntimeState Runtime { get; } = new();

    /// <summary>
    /// 行为引擎（宿主经 behaviorFactory 创建）。<see cref="NodeHostService.UpdateNode"/>
    /// 替换配置时一并重建——<see cref="RuleBasedBehavior"/> 在 ctor 快照 messages/rules。
    /// </summary>
    public INodeBehavior Behavior { get; internal set; }

    /// <summary>当前运行上下文；运行期间非空，停止后置 null。</summary>
    public INodeContext? Context { get; private set; }

    /// <summary>是否运行中（<see cref="Start"/>/<see cref="Stop"/> 翻转）。</summary>
    public bool IsRunning { get; private set; }

    internal SimulatedNode(NodeConfig config, INodeBehavior behavior)
        => (Config, Behavior) = (config, behavior);

    /// <summary>启动（幂等）：经工厂创建上下文 → ctx.Start → 行为绑定。仅由 <see cref="NodeHostService"/> 调用。</summary>
    internal void Start(Func<NodeConfig, NodeRuntimeState, INodeContext> contextFactory)
    {
        if (IsRunning)
            return;
        Context = contextFactory(Config, Runtime);
        Context.Start();
        Behavior.Attach(Context);
        IsRunning = true;
    }

    /// <summary>停止（幂等）：行为解绑 → ctx.Stop/Dispose → 置空。仅由 <see cref="NodeHostService"/> 调用。</summary>
    internal void Stop()
    {
        if (!IsRunning)
            return;
        Behavior.Detach();
        Context?.Stop();
        Context?.Dispose();
        Context = null;
        IsRunning = false;
    }
}
