using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Services.Nodes;

/// <summary>
/// 节点宿主（DI singleton，spec §10）。SDK 读线程只入队（有界 256，DropOldest，深度 ≥230 告警一次——修订 15），
/// 单 consumer 任务做规则分发；周期发送由 behavior 自己的 TimeProvider 定时器驱动。
/// </summary>
public sealed partial class NodeHostService : IDisposable
{
    private readonly Func<NodeConfig, NodeRuntimeState, INodeContext> _contextFactory;
    private readonly Func<NodeConfig, INodeBehavior> _behaviorFactory;
    private readonly ChannelRouter? _router;
    private readonly ILogger<NodeHostService> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly object _gate = new();
    private readonly List<SimulatedNode> _nodes = new();
    private readonly Channel<(SimulatedNode Node, NodeMessageArrived Message)> _queue;
    private Task? _consumer;
    private CancellationTokenSource? _consumerCts;
    private bool _nearCapacityLogged;

    /// <summary>节点活动统一通道（Started/Stopped/RuleMatched/Error…；触发方线程同步引发，UI 自行 marshal）。</summary>
    public event Action<NodeActivity>? Activity;

    /// <summary>
    /// 创建节点宿主。<paramref name="router"/> 为可选（测试注入 null）；非空时把 J1939 后端的
    /// <see cref="IFrameSink"/> 挂上/摘下路由器。<paramref name="loggerFactory"/> 亦为可选：
    /// 默认行为工厂用它解析 <see cref="RuleBasedBehavior"/> 的类别日志器（ScriptAction 降级告警 9441）。
    /// </summary>
    public NodeHostService(
        Func<NodeConfig, NodeRuntimeState, INodeContext> contextFactory,
        Func<NodeConfig, INodeBehavior>? behaviorFactory = null,
        ChannelRouter? router = null,
        ILogger<NodeHostService>? logger = null,
        ILoggerFactory? loggerFactory = null)
    {
        _contextFactory = contextFactory;
        _behaviorFactory = behaviorFactory ?? (cfg => new RuleBasedBehavior(cfg.Messages, cfg.Rules, _loggerFactory?.CreateLogger<RuleBasedBehavior>()));
        _router = router;
        _logger = logger ?? NullLogger<NodeHostService>.Instance;
        _loggerFactory = loggerFactory;
        _queue = Channel.CreateBounded<(SimulatedNode, NodeMessageArrived)>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    }

    /// <summary>已注册节点的快照（无锁遍历安全）。</summary>
    public IReadOnlyList<SimulatedNode> Nodes { get { lock (_gate) return _nodes.ToList(); } }

    /// <summary>注册节点（名称唯一/非空；未启动）。</summary>
    public Result<Unit> AddNode(NodeConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
            return Result<Unit>.Fail(ErrorCode.InvalidArgument, "节点名称不能为空");
        lock (_gate)
        {
            if (_nodes.Any(n => n.Config.Name == config.Name))
                return Result<Unit>.Fail(ErrorCode.InvalidArgument, $"节点 '{config.Name}' 已存在");
            _nodes.Add(new SimulatedNode(config, _behaviorFactory(config)));
        }

        return Result<Unit>.Ok(default);
    }

    /// <summary>移除节点；运行中 → Error（先停）。</summary>
    public Result<Unit> RemoveNode(string name)
    {
        lock (_gate)
        {
            var node = _nodes.FirstOrDefault(n => n.Config.Name == name);
            if (node is null)
                return Result<Unit>.Fail(ErrorCode.NotFound, $"节点 '{name}' 不存在");
            if (node.IsRunning)
                return Result<Unit>.Fail(ErrorCode.InvalidState, $"节点 '{name}' 正在运行，请先停止");
            if (node.Context is not null)
                Unwire(node);
            _nodes.Remove(node);
        }

        return Result<Unit>.Ok(default);
    }

    /// <summary>启动节点（幂等；SA 冲突——与运行中节点同 J1939 源地址——→ Error）。</summary>
    public Result<Unit> StartNode(string name)
    {
        SimulatedNode? node;
        lock (_gate)
        {
            node = _nodes.FirstOrDefault(n => n.Config.Name == name);
            if (node is null)
                return Result<Unit>.Fail(ErrorCode.NotFound, $"节点 '{name}' 不存在");
            if (node.IsRunning)
                return Result<Unit>.Ok(default);   // 幂等

            if (node.Config.Identity is J1939NodeIdentity identity)
            {
                var conflicting = _nodes.FirstOrDefault(other =>
                    other != node && other.IsRunning &&
                    other.Config.Identity is J1939NodeIdentity otherId && otherId.Sa == identity.Sa);
                if (conflicting is not null)
                    return Result<Unit>.Fail(ErrorCode.InvalidState,
                        $"源地址 SA 0x{identity.Sa:X2} 已被运行中的节点 '{conflicting.Config.Name}' 占用");
            }
        }

        node.Start(_contextFactory);
        Wire(node);
        EnsureConsumer();
        Raise(node.Config.Name, NodeActivityKind.Started, "started");
        return Result<Unit>.Ok(default);
    }

    /// <summary>停止节点（幂等：未运行直接 Ok，不引发 Activity）。</summary>
    public Result<Unit> StopNode(string name)
    {
        lock (_gate)
        {
            var node = _nodes.FirstOrDefault(n => n.Config.Name == name);
            if (node is null)
                return Result<Unit>.Fail(ErrorCode.NotFound, $"节点 '{name}' 不存在");
            if (!node.IsRunning)
                return Result<Unit>.Ok(default);   // 幂等
            Unwire(node);
            node.Stop();
        }

        Raise(name, NodeActivityKind.Stopped, "stopped");
        return Result<Unit>.Ok(default);
    }

    /// <summary>批量启动：<paramref name="tag"/> 为 null 启动全部，否则只启动该标签节点。</summary>
    public void StartAll(string? tag = null)
    {
        foreach (var node in Nodes.Where(n => n.Config.Tag == tag || tag is null).ToList())
            _ = StartNode(node.Config.Name);
    }

    /// <summary>批量停止：<paramref name="tag"/> 为 null 停止全部运行中节点，否则只停止该标签节点（未运行的 no-op）。</summary>
    public void StopAll(string? tag = null)
    {
        foreach (var node in Nodes.Where(n => n.Config.Tag == tag || tag is null && n.IsRunning).ToList())
            _ = StopNode(node.Config.Name);
    }

    /// <summary>挂接：后端事件 → 入队；Report/SendFailed → Activity。</summary>
    private void Wire(SimulatedNode node)
    {
        if (node.Context is null)
            return;
        node.Context.MessageArrived += m => Enqueue(node, m);
        node.Context.Reported += (kind, detail) => Raise(node.Config.Name, kind, detail);
        node.Context.SendFailed += ex => Raise(node.Config.Name, NodeActivityKind.Error, ex.Message);
        if (_router is not null && node.Context is IFrameSink sink)
            _router.AttachSink(sink);
    }

    private void Unwire(SimulatedNode node)
    {
        if (node.Context is null)
            return;
        if (_router is not null && node.Context is IFrameSink sink)
            _router.DetachSink(sink);
    }

    private void Enqueue(SimulatedNode node, NodeMessageArrived message)
    {
        // 修订 15：深度 ≥230 告警一次（_nearCapacityLogged 抑制后续重复告警）。
        if (_queue.Reader.Count >= 230 && !_nearCapacityLogged)
        {
            _nearCapacityLogged = true;
            LogQueueNearCapacity(_logger, _queue.Reader.Count);
        }

        _queue.Writer.TryWrite((node, message));   // DropOldest：满时丢最老
    }

    private void EnsureConsumer()
    {
        lock (_gate)
        {
            if (_consumer is not null)
                return;
            _consumerCts = new CancellationTokenSource();
            var token = _consumerCts.Token;
            _consumer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var (node, message) in _queue.Reader.ReadAllAsync(token))
                    {
                        try
                        {
                            node.Behavior.OnMessageArrived(message);
                        }
                        catch (Exception ex)
                        {
                            LogBehaviorThrew(_logger, ex, node.Config.Name);
                            Raise(node.Config.Name, NodeActivityKind.Error, ex.Message);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, token);
        }
    }

    private void Raise(string name, NodeActivityKind kind, string detail) =>
        Activity?.Invoke(new NodeActivity(name, kind, detail, DateTimeOffset.UtcNow));

    /// <summary>停止全部节点并取消 consumer 任务（宿主关闭时调用）。</summary>
    public void Dispose()
    {
        foreach (var node in Nodes.ToList())
            _ = StopNode(node.Config.Name);
        _consumerCts?.Cancel();
    }

    // 计划原文参数名 count/node 与模板占位符 {Count}/{Node} 不匹配（SYSLIB1014/1015，LoggerMessage
    // 生成器按名匹配，Task 15 同款修订）——按占位符改名 Count/Node。
    [LoggerMessage(EventId = 9401, Level = LogLevel.Warning, Message = "NodeHostService queue near capacity ({Count}/256)")]
    private static partial void LogQueueNearCapacity(ILogger logger, int Count);

    [LoggerMessage(EventId = 9402, Level = LogLevel.Error, Message = "Node behavior threw for {Node}")]
    private static partial void LogBehaviorThrew(ILogger logger, Exception ex, string Node);
}
