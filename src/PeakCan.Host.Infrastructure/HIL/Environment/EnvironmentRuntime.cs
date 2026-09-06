using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.Core;
using PeakCan.Host.Core.J1939;

namespace PeakCan.Host.Infrastructure.HIL.Environment;

/// <summary>
/// 统一环境执行器。10ms 单扫描定时器驱动周期帧和 pending 规则。
/// spec §6.1: Start 后 enabled 周期帧先立即发送一次，后续按量化周期调度。
/// </summary>
public sealed class EnvironmentRuntime : PeakCan.HIL.Core.HIL.StepExecutor.IEnvironmentRuntimeBridge
{
    private const int ScanIntervalMs = 10;
    private const int QueueCapacity = 256;
    private const int MaxConsecutiveSendFailures = 10;

    private readonly ICanChannel _channel;
    private readonly ILogger<EnvironmentRuntime> _logger;
    private readonly DbcDocument? _dbc;
    private readonly DbcEncodeService _encoder = new();
    private readonly J1939TpLayer? _tpLayer;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<CanFrame> _incoming = new();
    private ITimer? _scanTimer;
    private List<NodeRuntimeState> _states = [];
    private readonly List<(NodeRuntimeState State, byte[] Response, long DueMs)> _pendingUdsResponses = new();
    private long _droppedFrames;
    private long _lastDropWarningTicks;
    private bool _running;

    public EnvironmentRuntime(ICanChannel channel, ILogger<EnvironmentRuntime>? logger = null, DbcDocument? dbc = null, J1939TpLayer? tpLayer = null)
    {
        _channel = channel;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EnvironmentRuntime>.Instance;
        _dbc = dbc;
        _tpLayer = tpLayer;
    }

    public void Start(IReadOnlyList<RestbusNode> nodes, IReadOnlyList<ChannelConfig>? channels)
    {
        lock (_gate)
        {
            _states = nodes.Select(n => new NodeRuntimeState(n)).ToList();
            ApplySignalOverrides();
            _running = true;
            _scanTimer = new Timer(Scan, null, 0, ScanIntervalMs);
        }
        // Synchronous first send: enabled periodic frames are sent once immediately.
        Scan(null);
    }

    /// <summary>Test helper: processes incoming frames without waiting for the 10ms timer.</summary>
    public void ScanForTest() => ProcessIncoming();

    public void Stop()
    {
        lock (_gate)
        {
            _scanTimer?.Dispose();
            _scanTimer = null;
            _running = false;
            _pendingUdsResponses.Clear();
        }
    }

    public void UpdateFrameData(string nodeName, MessageRef msgRef, byte[] data)
    {
        lock (_gate)
        {
            var state = _states.FirstOrDefault(s => s.Node.Name == nodeName);
            state?.UpdateFixedHexData(msgRef, data);
        }
    }

    /// <summary>设置节点+消息+信号的运行时值（SetEnvironmentSignalStep 调用点）。</summary>
    public void SetSignalValue(string nodeName, string messageName, string signalName, double value)
    {
        lock (_gate)
        {
            var state = _states.FirstOrDefault(s => s.Node.Name == nodeName);
            var msgState = state?.Messages.FirstOrDefault(m =>
                (m.Source as DbcSignalsSource)?.MessageName == messageName);
            msgState?.Signals.Set(signalName, value);
        }
    }

    /// <summary>获取 DbcSignalsSource 编码后的 payload 字节（测试/诊断用）。</summary>
    public byte[]? GetEncodedPayload(string nodeName, string messageName)
    {
        lock (_gate)
        {
            var state = _states.FirstOrDefault(s => s.Node.Name == nodeName);
            var msgState = state?.Messages.FirstOrDefault(m =>
                (m.Source as DbcSignalsSource)?.MessageName == messageName);
            return msgState?.BuildPayload(_encoder, _dbc);
        }
    }

    private void ApplySignalOverrides()
    {
        foreach (var nodeState in _states)
        {
            if (nodeState.Node.SignalOverrides is not { } overrides) continue;
            foreach (var (key, value) in overrides)
            {
                var parts = key.Split('.', 2);
                if (parts.Length != 2) continue;
                var msgState = nodeState.Messages.FirstOrDefault(m =>
                    (m.Source as DbcSignalsSource)?.MessageName == parts[0]);
                msgState?.Signals.Set(parts[1], value);
            }
        }
    }

    public IReadOnlyList<NodeRunStats> GetStats()
    {
        lock (_gate)
        {
            return [.. _states.Select(s => new NodeRunStats(
                s.Node.Name,
                s.Messages.Sum(m => m.FramesSent),
                s.RulesMatched,
                s.UdsResponses))];
        }
    }

    public void InjectIncomingFrame(CanFrame frame)
    {
        if (_incoming.Count >= QueueCapacity)
        {
            _incoming.TryDequeue(out _);
            Interlocked.Increment(ref _droppedFrames);
            ThrottleDropWarning();
        }
        _incoming.Enqueue(frame);
    }

    private void Scan(object? state)
    {
        List<(RestbusNode Node, NodeMessageRuntimeState MsgState, NodeMessage Msg, byte[] Payload)>? toSend = null;
        lock (_gate)
        {
            if (!_running) return;
            var now = System.Environment.TickCount64;

            foreach (var nodeState in _states)
            {
                for (int i = 0; i < nodeState.Messages.Count; i++)
                {
                    var msgState = nodeState.Messages[i];
                    if (!msgState.Enabled || now < msgState.NextDueMs) continue;

                    var payload = msgState.BuildPayload(_encoder, _dbc);
                    if (payload is not null)
                        (toSend ??= []).Add((nodeState.Node, msgState, nodeState.Node.Messages[i], payload));

                    var quantum = Math.Max(ScanIntervalMs,
                        (nodeState.Node.Messages[i].IntervalMs + ScanIntervalMs - 1) / ScanIntervalMs * ScanIntervalMs);
                    msgState.NextDueMs = now + quantum;
                }
            }
        }

        if (toSend is not null)
            foreach (var (node, msgState, msg, payload) in toSend)
                SendFrame(node, msgState, msg, payload);

        ProcessIncoming();
    }

    private void SendFrame(RestbusNode node, NodeMessageRuntimeState msgState, NodeMessage msg, byte[] payload)
    {
        if (msg.Ref is J1939MessageRef jRef)
        {
            SendJ1939Frame(jRef, node, msgState, msg, payload);
            return;
        }
        if (msg.Ref is not CanMessageRef canRef) return;
        var id = new CanId(canRef.Id, canRef.IsExtended ? FrameFormat.Extended : FrameFormat.Standard);
        var flags = msg.Fd ? FrameFlags.Fd : FrameFlags.None;
        var frame = new CanFrame(id, payload, flags, default, default, FrameSource.Environment);
        var result = _channel.WriteAsync(frame).AsTask().GetAwaiter().GetResult();

        if (result.IsSuccess)
        {
            msgState.ConsecutiveFailures = 0;
            msgState.FramesSent++;
        }
        else
        {
            msgState.ConsecutiveFailures++;
            if (msgState.ConsecutiveFailures >= MaxConsecutiveSendFailures)
            {
                _logger.LogError("Environment message {Ref}: stopped after {N} consecutive failures.", msg.Ref, MaxConsecutiveSendFailures);
                msgState.Enabled = false;
            }
        }
    }

    private void ProcessIncoming()
    {
        FlushDueUdsResponses();
        while (_incoming.TryDequeue(out var frame))
        {
            if (frame.FrameSource == FrameSource.Environment) continue;

            // UDS routing (spec S6.6)
            ProcessUdsRequests(frame);

            List<(RestbusNode Node, ResponseRule Rule)>? matched = null;
            lock (_gate)
            {
                foreach (var nodeState in _states)
                {
                    foreach (var rule in nodeState.Node.Rules)
                    {
                        if (!MatchesIncoming(rule.Trigger, frame)) continue;
                        if (!MatchesCondition(rule.Condition, frame)) continue;
                        nodeState.RulesMatched++;
                        (matched ??= []).Add((nodeState.Node, rule));
                    }
                }
            }

            if (matched is not null)
                foreach (var (node, rule) in matched)
                    ExecuteAction(node, rule.Action);
        }
    }

    private void ExecuteAction(RestbusNode node, NodeAction action)
    {
        switch (action)
        {
            case SendMessageAction send: SendActionFrame(node, send); break;
            case SetSignalAction set: SetSignalValue(node, set); break;
            case StartMessageAction start: SetMessageEnabled(node, start.Ref, true); break;
            case StopMessageAction stop: SetMessageEnabled(node, stop.Ref, false); break;
            case ScriptAction script:
                _logger.LogWarning("ScriptAction '{Ref}' not supported in EnvironmentRuntime.", script.ScriptRef);
                break;
        }
    }

    /// <summary>
    /// setSignal 规则原语：把信号值写入本节点同名 DBC 报文的运行时信号表，
    /// 该报文下次到期发送时由 DbcSignalsSource 编码生效。
    /// 目标报文不是 DbcSignalsSource 载荷（如 fixedHex）时无法编码信号，记警告跳过。
    /// </summary>
    private void SetSignalValue(RestbusNode node, SetSignalAction action)
    {
        lock (_gate)
        {
            var state = _states.FirstOrDefault(s => s.Node.Name == node.Name);
            var target = state?.Messages.FirstOrDefault(m =>
                m.Source is DbcSignalsSource src && src.MessageName == action.MessageName);
            if (target is null)
            {
                _logger.LogWarning(
                    "SetSignalAction: node '{Node}' has no DbcSignalsSource message '{Message}' — signal '{Signal}' ignored.",
                    node.Name, action.MessageName, action.SignalName);
                return;
            }
            target.EnsureSignalsInitialized(_dbc);
            target.Signals.Set(action.SignalName, action.Value);
        }
    }

    private void SendActionFrame(RestbusNode node, SendMessageAction action)
    {
        byte[] payload = action.Payload switch
        {
            FixedHexSource hex => ParseHexStatic(hex.Hex),
            _ => [],
        };
        switch (action.Ref)
        {
            case CanMessageRef canRef:
            {
                var id = new CanId(canRef.Id, canRef.IsExtended ? FrameFormat.Extended : FrameFormat.Standard);
                var frame = new CanFrame(id, payload, FrameFlags.None, default, default, FrameSource.Environment);
                _channel.WriteAsync(frame).AsTask().GetAwaiter().GetResult();
                break;
            }
            case J1939MessageRef jRef:
                SendActionJ1939Frame(node, jRef, payload);
                break;
        }
    }

    /// <summary>
    /// 规则 send 动作的 J1939 帧。SA 回落顺序：Ref.Sa → 节点身份 Sa（修复前 Ref.Sa 为空时恒发 0x00）。
    /// &gt;8 字节按 Ref.Mode 走 TP；发送计数计入命中的周期消息。
    /// </summary>
    private void SendActionJ1939Frame(RestbusNode node, J1939MessageRef jRef, byte[] payload)
    {
        if (payload.Length == 0)
        {
            _logger.LogWarning("J1939 send action {Ref}: no payload, skipped.", jRef);
            return;
        }
        var sa = ResolveSa(node, jRef.Sa);
        NodeMessageRuntimeState? msgState;
        lock (_gate)
        {
            var state = _states.FirstOrDefault(s => s.Node.Name == node.Name);
            msgState = state?.Messages.FirstOrDefault(m => MatchesRefStatic(jRef, m.Ref));
        }
        // 阻塞发送在锁外执行（与周期 SendFrame 一致），命中周期消息时顺带计入 FramesSent
        SendJ1939Payload(jRef, sa, payload, msgState);
    }

    private static byte ResolveSa(RestbusNode node, byte? refSa)
        => refSa ?? (node.Identity as J1939NodeIdentity)?.Sa ?? 0x00;

    private void SetMessageEnabled(RestbusNode node, MessageRef target, bool enabled)
    {
        lock (_gate)
        {
            var state = _states.FirstOrDefault(s => s.Node.Name == node.Name);
            if (state is null) return;
            foreach (var m in state.Messages)
            {
                if (MatchesRefStatic(target, m.Ref))
                {
                    m.Enabled = enabled;
                    if (enabled) m.NextDueMs = System.Environment.TickCount64 + ScanIntervalMs;
                }
            }
        }
    }

    private static bool MatchesIncoming(MessageRef ruleRef, CanFrame frame)
    {
        switch (ruleRef)
        {
            case CanMessageRef canRef:
                return frame.Id.Raw == canRef.Id && frame.Id.IsExtended == canRef.IsExtended;
            case J1939MessageRef jRef:
                // 触发宽容匹配：PGN + 优先级必须相等，SA/DA 仅在 Ref 指定时比较
                // （修复前 J1939 触发恒 false——GB/T 27930 等协议模板的规则链整体失效）。
                var id = new J1939Id(frame.Id.Raw & J1939Id.Raw29Mask);
                return id.Pgn == jRef.Pgn
                    && id.Priority == jRef.Priority
                    && (jRef.Sa is not { } sa || id.SourceAddress == sa)
                    && (jRef.Da is not { } da || id.DestinationAddress == da);
            default:
                return false;
        }
    }

    private static bool MatchesCondition(BytePattern? cond, CanFrame frame)
    {
        if (cond is null) return true;
        if (frame.Data.Length <= cond.Offset) return false;
        return (frame.Data.Span[cond.Offset] & cond.Mask) == cond.Value;
    }

    private static byte[] ParseHexStatic(string hex)
    {
        var clean = hex.Replace(" ", "").Replace("-", "");
        var bytes = new byte[clean.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static bool MatchesRefStatic(MessageRef a, MessageRef b) => (a, b) switch
    {
        (CanMessageRef ca, CanMessageRef cb) => ca.Id == cb.Id && ca.IsExtended == cb.IsExtended,
        (J1939MessageRef ja, J1939MessageRef jb) => ja.Pgn == jb.Pgn && ja.Priority == jb.Priority,
        _ => false,
    };

    private void ProcessUdsRequests(CanFrame frame)
    {
        List<(NodeRuntimeState State, byte[] Response)>? responses = null;
        lock (_gate)
        {
            foreach (var nodeState in _states)
            {
                if (nodeState.StateMachine is not { } sm) continue;
                if (nodeState.Node.UdsBehavior is not { } uds) continue;
                if (frame.Id.Raw != uds.CanIds.RequestId) continue;

                var request = ExtractUdsPayload(frame);
                if (request.Length == 0) continue;
                var (response, delayMs) = sm.ProcessRequest(request);
                nodeState.UdsResponses++;
                // delayMs>0 的响应挂 pending 队列，由扫描 tick 到期发出（精度 = 扫描周期）
                if (delayMs > 0)
                    _pendingUdsResponses.Add((nodeState, response, System.Environment.TickCount64 + delayMs));
                else
                    (responses ??= []).Add((nodeState, response));
            }
        }
        if (responses is not null) SendUdsResponses(responses);
    }

    private void SendUdsResponses(List<(NodeRuntimeState State, byte[] Response)> responses)
    {
        foreach (var (nodeState, response) in responses)
        {
            var uds = nodeState.Node.UdsBehavior!;
            var respId = new CanId(uds.CanIds.ResponseId, uds.CanIds.IsExtendedFrame ? FrameFormat.Extended : FrameFormat.Standard);
            var respFrame = new CanFrame(respId, response, FrameFlags.None, default, default, FrameSource.Environment);
            _channel.WriteAsync(respFrame).AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>把到期的延迟 UDS 响应转出发送（延迟精度 = 扫描周期 10ms）。</summary>
    private void FlushDueUdsResponses()
    {
        List<(NodeRuntimeState State, byte[] Response)>? due = null;
        lock (_gate)
        {
            if (_pendingUdsResponses.Count == 0) return;
            var now = System.Environment.TickCount64;
            for (int i = _pendingUdsResponses.Count - 1; i >= 0; i--)
            {
                if (_pendingUdsResponses[i].DueMs > now) continue;
                due ??= [];
                due.Add((_pendingUdsResponses[i].State, _pendingUdsResponses[i].Response));
                _pendingUdsResponses.RemoveAt(i);
            }
        }
        if (due is not null) SendUdsResponses(due);
    }

    private static byte[] ExtractUdsPayload(CanFrame frame)
    {
        // ISO-TP single frame: byte 0 high nibble = type (0), low nibble = length
        var data = frame.Data.ToArray();
        if (data.Length < 2) return [];
        if ((data[0] >> 4) == 0)
        {
            var len = data[0] & 0x0F;
            if (len == 0 || data.Length < 1 + len) return [];
            return data[1..(1 + len)];
        }
        return data; // not ISO-TP, treat as raw
    }

    private void SendJ1939Frame(J1939MessageRef jRef, RestbusNode node, NodeMessageRuntimeState msgState, NodeMessage msg, byte[] payload)
    {
        // SA 回落：Ref.Sa → 节点身份 Sa（修复前 Ref.Sa 为空时恒发 0x00）
        var sa = ResolveSa(node, jRef.Sa);
        SendJ1939Payload(jRef, sa, payload, msgState);
    }

    /// <summary>J1939 发送公共路径（≤8B 单帧 / &gt;8B 按 Ref.Mode 走 TP）。返回是否发送成功。</summary>
    private bool SendJ1939Payload(J1939MessageRef jRef, byte sa, byte[] payload, NodeMessageRuntimeState? msgState)
    {
        if (payload.Length <= 8)
        {
            var id = J1939Id.Compose(jRef.Priority, jRef.Pgn, sa, jRef.Da);
            var frame = new CanFrame(new CanId(id, FrameFormat.Extended), payload, FrameFlags.None, default, default, FrameSource.Environment);
            var result = _channel.WriteAsync(frame).AsTask().GetAwaiter().GetResult();
            return RecordJ1939Result(jRef, result.IsSuccess, msgState);
        }
        if (_tpLayer is { } tp)
        {
            var task = jRef.Mode == TpMode.RtsCts && jRef.Da is { } da
                ? tp.SendRtsCtsAsync(jRef.Pgn, jRef.Priority, sa, da, payload)
                : tp.SendBamAsync(jRef.Pgn, jRef.Priority, sa, payload);
            var result = task.GetAwaiter().GetResult();
            return RecordJ1939Result(jRef, result.IsSuccess, msgState);
        }
        _logger.LogWarning("J1939 TP message {Ref} >8B but no TpLayer provided.", jRef);
        return false;
    }

    private bool RecordJ1939Result(J1939MessageRef jRef, bool success, NodeMessageRuntimeState? msgState)
    {
        if (msgState is null) return success;
        if (success) { msgState.ConsecutiveFailures = 0; msgState.FramesSent++; }
        else msgState.ConsecutiveFailures++;
        return success;
    }

    private void ThrottleDropWarning()
    {
        var now = System.Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastDropWarningTicks) < 5000) return;
        Interlocked.Exchange(ref _lastDropWarningTicks, now);
        _logger.LogWarning("Environment incoming queue overflow: {Dropped} frames dropped.", Interlocked.Read(ref _droppedFrames));
    }
}

internal sealed class NodeRuntimeState
{
    public RestbusNode Node { get; }
    public EcuStateMachine? StateMachine { get; set; }
    public long UdsResponses { get; set; }
    public long RulesMatched { get; set; }
    public List<NodeMessageRuntimeState> Messages { get; } = [];

    public NodeRuntimeState(RestbusNode node)
    {
        Node = node;
        if (node.UdsBehavior is { } uds)
            StateMachine = new EcuStateMachine(uds.Transitions, null, uds.InitialState);
        foreach (var msg in node.Messages) Messages.Add(new NodeMessageRuntimeState(msg));
    }

    public void UpdateFixedHexData(MessageRef msgRef, byte[] data)
    {
        foreach (var m in Messages)
            if (m.Source is FixedHexSource && MatchesRef(m.Ref, msgRef))
                m.FixedHexData = data;
    }

    private static bool MatchesRef(MessageRef a, MessageRef b) => (a, b) switch
    {
        (CanMessageRef ca, CanMessageRef cb) => ca.Id == cb.Id && ca.IsExtended == cb.IsExtended,
        (J1939MessageRef ja, J1939MessageRef jb) => ja.Pgn == jb.Pgn && ja.Priority == jb.Priority,
        _ => false,
    };
}

internal sealed class NodeMessageRuntimeState
{
    public MessageRef Ref { get; }
    public NodePayloadSource Source { get; }
    public bool Enabled { get; set; }
    public long NextDueMs { get; set; }
    public int ConsecutiveFailures { get; set; }
    public long FramesSent { get; set; }
    public byte[]? FixedHexData { get; set; }
    public ushort CounterValue { get; set; }
    public NodeSignalState Signals { get; } = new();

    public NodeMessageRuntimeState(NodeMessage msg)
    {
        Ref = msg.Ref;
        Source = msg.Payload;
        Enabled = msg.Enabled;
        FixedHexData = (msg.Payload as FixedHexSource) is { } hex ? ParseHex(hex.Hex) : null;
        CounterValue = msg.AutoCounter is { } ac ? ac.StartValue : (ushort)0;
    }

    public byte[]? BuildPayload(DbcEncodeService encoder, DbcDocument? dbc)
    {
        switch (Source)
        {
            case FixedHexSource:
                return FixedHexData;
            case DbcSignalsSource dbcSource when dbc is not null:
            {
                var msg = dbc.Messages.FirstOrDefault(m => m.Name == dbcSource.MessageName);
                if (msg is null) return null;
                EnsureSignalsInitialized(dbc);
                return encoder.Encode(msg, Signals.ToDictionary());
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// 首次编码/setSignal 前按 DBC 信号定义预填信号表（初值取信号 offset）。
    /// 预填后 setSignal 写入的值不会被覆盖，其余信号保持初值而非 0。
    /// </summary>
    public void EnsureSignalsInitialized(DbcDocument? dbc)
    {
        if (Source is not DbcSignalsSource dbcSource || dbc is null || Signals.HasValues) return;
        var msg = dbc.Messages.FirstOrDefault(m => m.Name == dbcSource.MessageName);
        if (msg is null) return;
        foreach (var s in msg.Signals)
            Signals.Set(s.Name, Signals.GetOrInit(s.Name, s.Offset));
    }

    private static byte[] ParseHex(string hex)
    {
        var clean = hex.Replace(" ", "").Replace("-", "");
        var bytes = new byte[clean.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
        return bytes;
    }
}
