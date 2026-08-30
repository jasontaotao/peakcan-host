using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PeakCan.Host.App.Services.Nodes;

/// <summary>
/// B1 周期消息表 + B2 ECA 响应规则表（spec §10）。周期调度经 ctx.Clock 单一扫描定时器
/// （10ms），规则动作五种原语 + ScriptAction 逃生口（本期显式报错，修订 10）。
/// <para>线程模型：<see cref="OnMessageArrived"/> 在 NodeHostService consumer 任务上调用；
/// 定时器回调在 TimeProvider 的定时线程上调用——两者都只触碰本类的加锁状态与 ctx.Send（fire-and-forget）。</para>
/// </summary>
public sealed partial class RuleBasedBehavior : INodeBehavior
{
    /// <summary>周期扫描周期（ms）：GBT27930 最快 CCS/BCL 50ms 的 1/5，判定精度足够。</summary>
    public const int ScanIntervalMs = 10;

    private readonly IReadOnlyList<NodeMessage> _messages;
    private readonly IReadOnlyList<ResponseRule> _rules;
    private readonly ILogger<RuleBasedBehavior> _logger;
    private readonly object _gate = new();
    private readonly List<(ResponseRule Rule, long DueMs)> _pending = new();
    private INodeContext? _ctx;
    private ITimer? _scanTimer;
    private bool[] _enabled = Array.Empty<bool>();
    private long[] _nextDueMs = Array.Empty<long>();

    public RuleBasedBehavior(IReadOnlyList<NodeMessage> messages, IReadOnlyList<ResponseRule> rules)
    {
        _messages = messages;
        _rules = rules;
        _logger = NullLogger<RuleBasedBehavior>.Instance;
    }

    /// <inheritdoc/>
    public void Attach(INodeContext ctx)
    {
        var now = NowMs(ctx);
        lock (_gate)
        {
            _enabled = _messages.Select(m => m.Enabled).ToArray();
            _nextDueMs = _messages.Select(m => now + m.IntervalMs).ToArray();
            _pending.Clear();
            // 评审修复：_ctx 必须在索引数组就绪后发布——否则 Attach 窗口内到达并命中
            // Start/Stop 规则时，SetEnabled 会在零长数组上越界（IndexOutOfRangeException）。
            _ctx = ctx;
            _scanTimer = ctx.Clock.CreateTimer(Scan, null, TimeSpan.FromMilliseconds(ScanIntervalMs), TimeSpan.FromMilliseconds(ScanIntervalMs));
        }
    }

    /// <inheritdoc/>
    public void Detach()
    {
        lock (_gate)
        {
            _scanTimer?.Dispose();
            _scanTimer = null;
            _pending.Clear();
            _ctx = null;
        }
    }

    /// <inheritdoc/>
    public void OnMessageArrived(NodeMessageArrived message)
    {
        // 评审修复：ctx 在 _gate 内捕获并校验——Detach 与在途调用并发时，锁的先后决定
        // 命运：Detach 先拿到锁 → 在途调用看到 null 直接 no-op；在途调用先拿到锁 →
        // 本批次在合法 Attached 状态下完成。避免对已停止节点再发送/上报。
        List<ResponseRule>? immediate = null;
        INodeContext? ctx;
        long dueAt;
        lock (_gate)
        {
            ctx = _ctx;
            if (ctx is null)
                return;
            dueAt = NowMs(ctx);
            foreach (var rule in _rules)
            {
                if (!MessageRefMatcher.Matches(rule.Trigger, message.Ref))
                    continue;
                if (rule.Condition is { } cond && message.Payload.Length <= cond.Offset)
                    continue;
                if (rule.Condition is { } c2 && (message.Payload[c2.Offset] & c2.Mask) != c2.Value)
                    continue;

                if (rule.DelayMs > 0)
                    _pending.Add((rule, dueAt + rule.DelayMs));
                else
                    (immediate ??= new List<ResponseRule>()).Add(rule);
            }
        }

        if (immediate is not null)
            foreach (var rule in immediate)
                Execute(rule, ctx);
    }

    private void Scan(object? state)
    {
        // 评审修复：ctx 在 _gate 内捕获并校验——Timer.Dispose 不等待在途回调，
        // Detach 后仍在飞的 tick 不得再发送周期帧 / 触发 pending 规则。
        List<ResponseRule>? dueRules = null;
        INodeContext? ctx;
        lock (_gate)
        {
            ctx = _ctx;
            if (ctx is null)
                return;
            var now = NowMs(ctx);

            for (int i = 0; i < _messages.Count; i++)
            {
                if (!_enabled[i] || _messages[i].IntervalMs <= 0 || now < _nextDueMs[i])
                    continue;
                ctx.Send(_messages[i].Ref, _messages[i].Payload);
                _nextDueMs[i] = now + _messages[i].IntervalMs;   // 漂移容忍（不做追赶补发，防突发风暴）
            }

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (now < _pending[i].DueMs)
                    continue;
                (dueRules ??= new List<ResponseRule>()).Add(_pending[i].Rule);
                _pending.RemoveAt(i);
            }
        }

        if (dueRules is not null)
            foreach (var rule in dueRules)
                Execute(rule, ctx);
    }

    private void Execute(ResponseRule rule, INodeContext ctx)
    {
        ctx.Report(NodeActivityKind.RuleMatched, Describe(rule));
        switch (rule.Action)
        {
            case SendMessageAction send:
                ctx.Send(send.Ref, send.Payload);
                break;

            case SetSignalAction set:
                ctx.Runtime.SetSignalValue(set.MessageName, set.SignalName, set.Value);
                break;

            case StartMessageAction start:
                SetEnabled(ctx, start.Ref, true);
                break;

            case StopMessageAction stop:
                SetEnabled(ctx, stop.Ref, false);
                break;

            case ScriptAction script:
                LogScriptUnsupported(_logger, script.ScriptRef);
                ctx.Report(NodeActivityKind.Error, $"ScriptAction '{script.ScriptRef}' 尚未支持（本期降级，见计划修订 10）");
                break;
        }
    }

    private void SetEnabled(INodeContext ctx, MessageRef target, bool enabled)
    {
        lock (_gate)
        {
            for (int i = 0; i < _messages.Count; i++)
            {
                if (!MessageRefMatcher.Matches(target, _messages[i].Ref))
                    continue;
                _enabled[i] = enabled;
                if (enabled)
                    _nextDueMs[i] = NowMs(ctx) + _messages[i].IntervalMs;
            }
        }
    }

    private static long NowMs(INodeContext ctx) => ctx.Clock.GetUtcNow().ToUnixTimeMilliseconds();

    private static string Describe(ResponseRule rule) => rule.Action switch
    {
        SendMessageAction a => $"send {Describe(a.Ref)}",
        SetSignalAction a => $"set {a.MessageName}.{a.SignalName}={a.Value}",
        StartMessageAction a => $"start {Describe(a.Ref)}",
        StopMessageAction a => $"stop {Describe(a.Ref)}",
        ScriptAction a => $"script {a.ScriptRef}",
        _ => "?",
    };

    private static string Describe(MessageRef r) => r switch
    {
        J1939MessageRef j => $"PGN 0x{j.Pgn:X4}",
        CanMessageRef c => $"ID 0x{c.Id:X}",
        _ => "?",
    };

    // 计划原文参数名 refr 与模板占位符 {Ref} 不匹配（SYSLIB1014/1015，LoggerMessage 生成器按名匹配）——按占位符改名 Ref。
    [LoggerMessage(EventId = 9441, Level = LogLevel.Warning, Message = "Script escape-hatch not supported: {Ref}")]
    private static partial void LogScriptUnsupported(ILogger logger, string Ref);
}
