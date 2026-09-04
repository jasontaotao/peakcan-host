using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.HIL;
using PeakCan.HIL.Core.HIL.Environment;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.HIL.Contracts;
using PeakCan.HIL.Core.Uds.IsoTp;
using PeakCan.HIL.Core.J1939;

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
        List<(NodeMessageRuntimeState MsgState, NodeMessage Msg)>? toSend = null;
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
                        (toSend ??= []).Add((msgState, nodeState.Node.Messages[i]));

                    var quantum = Math.Max(ScanIntervalMs,
                        (nodeState.Node.Messages[i].IntervalMs + ScanIntervalMs - 1) / ScanIntervalMs * ScanIntervalMs);
                    msgState.NextDueMs = now + quantum;
                }
            }
        }

        if (toSend is not null)
            foreach (var (msgState, msg) in toSend)
                SendFrame(msgState, msg);

        ProcessIncoming();
    }

    private void SendFrame(NodeMessageRuntimeState msgState, NodeMessage msg)
    {
        if (msg.Ref is J1939MessageRef jRef)
        {
            SendJ1939Frame(jRef, msgState, msg);
            return;
        }
        if (msg.Ref is not CanMessageRef canRef) return;
        var id = new CanId(canRef.Id, canRef.IsExtended ? FrameFormat.Extended : FrameFormat.Standard);
        var payload = msgState.BuildPayload(_encoder, _dbc);
        if (payload is null) return;
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
            case SetSignalAction set: /* DBC signal encode in M2 */ break;
            case StartMessageAction start: SetMessageEnabled(node, start.Ref, true); break;
            case StopMessageAction stop: SetMessageEnabled(node, stop.Ref, false); break;
            case ScriptAction script:
                _logger.LogWarning("ScriptAction '{Ref}' not supported in EnvironmentRuntime.", script.ScriptRef);
                break;
        }
    }

    private void SendActionFrame(RestbusNode node, SendMessageAction action)
    {
        if (action.Ref is not CanMessageRef canRef) return;
        var id = new CanId(canRef.Id, canRef.IsExtended ? FrameFormat.Extended : FrameFormat.Standard);
        byte[] payload = action.Payload switch
        {
            FixedHexSource hex => ParseHexStatic(hex.Hex),
            _ => [],
        };
        var frame = new CanFrame(id, payload, FrameFlags.None, default, default, FrameSource.Environment);
        _channel.WriteAsync(frame).AsTask().GetAwaiter().GetResult();
    }

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
        if (ruleRef is CanMessageRef canRef)
            return frame.Id.Raw == canRef.Id && frame.Id.IsExtended == canRef.IsExtended;
        return false;
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
                // TODO(M3): honor delayMs — currently response sent immediately (spec §6.6)\n                var (response, _) = sm.ProcessRequest(request);
                nodeState.UdsResponses++;
                (responses ??= []).Add((nodeState, response));
            }
        }
        if (responses is null) return;
        foreach (var (nodeState, response) in responses)
        {
            var uds = nodeState.Node.UdsBehavior!;
            var respId = new CanId(uds.CanIds.ResponseId, uds.CanIds.IsExtendedFrame ? FrameFormat.Extended : FrameFormat.Standard);
            var respFrame = new CanFrame(respId, response, FrameFlags.None, default, default, FrameSource.Environment);
            _channel.WriteAsync(respFrame).AsTask().GetAwaiter().GetResult();
        }
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

    private void SendJ1939Frame(J1939MessageRef jRef, NodeMessageRuntimeState msgState, NodeMessage msg)
    {
        var payload = msgState.BuildPayload(_encoder, _dbc);
        if (payload is null) return;
        var sa = jRef.Sa ?? 0x00;
        var priority = jRef.Priority;

        if (payload.Length <= 8)
        {
            var id = J1939Id.Compose(priority, jRef.Pgn, sa, jRef.Da);
            var frame = new CanFrame(new CanId(id, FrameFormat.Extended), payload, FrameFlags.None, default, default, FrameSource.Environment);
            var result = _channel.WriteAsync(frame).AsTask().GetAwaiter().GetResult();
            if (result.IsSuccess) { msgState.ConsecutiveFailures = 0; msgState.FramesSent++; }
            else msgState.ConsecutiveFailures++;
        }
        else if (_tpLayer is { } tp)
        {
            var task = jRef.Mode == TpMode.RtsCts && jRef.Da is { } da
                ? tp.SendRtsCtsAsync(jRef.Pgn, priority, sa, da, payload)
                : tp.SendBamAsync(jRef.Pgn, priority, sa, payload);
            var result = task.GetAwaiter().GetResult();
            if (result.IsSuccess) { msgState.ConsecutiveFailures = 0; msgState.FramesSent++; }
            else msgState.ConsecutiveFailures++;
        }
        else
        {
            _logger.LogWarning("J1939 TP message {Ref} >8B but no TpLayer provided.", msg.Ref);
        }
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
                if (!Signals.HasValues)
                    foreach (var s in msg.Signals)
                        Signals.Set(s.Name, Signals.GetOrInit(s.Name, s.Offset));
                return encoder.Encode(msg, Signals.ToDictionary());
            }
            default:
                return null;
        }
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
