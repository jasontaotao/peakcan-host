using PeakCan.Host.App.ViewModels;

namespace PeakCan.Host.App.Services;

/// <summary>
/// P1-2（2026-09-06，setter 注入清零）：已连接通道快照源。AppShellViewModel 在
/// 连接状态变化时 <see cref="Publish"/>，<see cref="HilViewModel"/> ctor 注入本接口
/// 读取快照。取代旧 <c>HilViewModel.SetConnectedChannelsProvider</c> setter 注入
/// （AppShell 构造时直连 HilViewModel——因 DI factory 引用 shell 会形成
/// AppShell⇄HilViewModel 循环解析死锁，被迫 transient + setter）。本服务无依赖，
/// DI 可先于两者解析，环消失，HilViewModel 恢复 singleton。
/// </summary>
public interface IConnectedChannelsSource
{
    /// <summary>最近一次发布的已连接通道快照（未发布过 = 空列表）。</summary>
    IReadOnlyList<HilViewModel.ConnectedChannel> Current { get; }

    /// <summary>快照替换后触发，HilViewModel 刷新可用通道与命令状态。</summary>
    event Action? Changed;

    /// <summary>发布新快照（整体替换，读方拿原子快照）。生产者：AppShellViewModel。</summary>
    void Publish(IReadOnlyList<HilViewModel.ConnectedChannel> snapshot);
}

/// <inheritdoc/>
public sealed class ConnectedChannelsSource : IConnectedChannelsSource
{
    private volatile IReadOnlyList<HilViewModel.ConnectedChannel> _current =
        Array.Empty<HilViewModel.ConnectedChannel>();

    /// <inheritdoc/>
    public IReadOnlyList<HilViewModel.ConnectedChannel> Current => _current;

    /// <inheritdoc/>
    public event Action? Changed;

    /// <summary>发布新快照（整体替换，读方拿原子快照）。线程安全：volatile 引用替换。</summary>
    public void Publish(IReadOnlyList<HilViewModel.ConnectedChannel> snapshot)
    {
        _current = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Changed?.Invoke();
    }
}
