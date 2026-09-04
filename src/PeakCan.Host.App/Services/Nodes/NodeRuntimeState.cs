namespace PeakCan.Host.App.Services.Nodes;

/// <summary>
/// 节点运行时信号值表（键 = (DBC 消息名, 信号名)）；DbcSignals payload 每周期 live 求值（spec §10）。
/// 每个运行中的节点一份（<see cref="NodeConfig"/> 停启间保留），线程安全（内部锁）。
/// </summary>
public sealed class NodeRuntimeState
{
    private readonly object _gate = new();
    private readonly Dictionary<(string Message, string Signal), double> _values = new();

    /// <summary>写入（或覆盖）一个信号工程值。</summary>
    public void SetSignalValue(string messageName, string signalName, double value)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageName);
        ArgumentException.ThrowIfNullOrEmpty(signalName);
        lock (_gate)
            _values[(messageName, signalName)] = value;
    }

    /// <summary>读取信号工程值；未写入过返回 false。</summary>
    public bool TryGetSignalValue(string messageName, string signalName, out double value)
    {
        lock (_gate)
            return _values.TryGetValue((messageName, signalName), out value!);
    }
}
