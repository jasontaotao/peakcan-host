using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.Dbc;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.App.Services.Nodes.J1939;

/// <summary>
/// J1939 节点后端：<see cref="INodeContext"/>（发送路由 + 活动上报）+ <see cref="IFrameSink"/>（单帧应用报文直通）。
/// <para>修订 2（P0-1）：单帧应用报文（PF&lt;0xF0、非 0xEB/0xEC、非 RTR）直接包装为
/// <see cref="NodeMessageArrived"/>（Mode=Single）；多帧经 <see cref="J1939TpLayer.MessageReceived"/>。
/// OnFrame 在 SDK 读线程触发——只做解析与事件引发（NodeHostService 负责入队），不发帧不阻塞。</para>
/// <para>TP 帧不在 <see cref="OnFrame"/> 转发给层：生产接收路径由 <c>J1939TpSinkAdapter</c>
/// 喂 <see cref="J1939TpLayer.ProcessFrame"/>（SinkWiringService 挂接），此处再转发会双份投递。</para>
/// </summary>
internal sealed partial class J1939NodeContext : INodeContext, IFrameSink
{
    private readonly NodeConfig _config;
    private readonly J1939TpLayer _layer;
    private readonly Func<CanFrame, CancellationToken, ValueTask<Result<Unit>>> _singleFrameSend;
    private readonly DbcService _dbcService;
    private readonly DbcEncodeService _dbcEncoder;
    private readonly ChannelRouter? _router;
    private readonly ILogger<J1939NodeContext> _logger;

    /// <summary>J1939 源地址（取自 <see cref="J1939NodeIdentity"/>）。</summary>
    /// <exception cref="InvalidOperationException">节点身份不是 J1939 身份（配置错误）。</exception>
    private byte Sa => _config.Identity is J1939NodeIdentity j ? j.Sa
        : throw new InvalidOperationException("J1939NodeContext 需要 J1939NodeIdentity");

    /// <inheritdoc />
    public NodeIdentity Identity => _config.Identity;

    /// <inheritdoc />
    public NodeRuntimeState Runtime { get; }

    /// <inheritdoc />
    public TimeProvider Clock { get; } = TimeProvider.System;

    /// <inheritdoc />
    public event Action<NodeMessageArrived>? MessageArrived;

    /// <inheritdoc />
    public event Action<Exception>? SendFailed;

    /// <inheritdoc />
    public event Action<NodeActivityKind, string>? Reported;

    /// <summary>
    /// 创建 J1939 节点上下文。<paramref name="dbcService"/>/<paramref name="dbcEncoder"/> 支撑
    /// <see cref="DbcSignalsSource"/> 载荷（brief 修正：原稿占位字段，实现时并入构造参数）；
    /// <paramref name="router"/> 可选（测试注入 null）——非空时 Start/Stop 挂上/摘下本 sink
    /// （router 侧幂等，与 NodeHostService.Wire 的挂接叠加无副作用）。
    /// </summary>
    public J1939NodeContext(
        NodeConfig config,
        NodeRuntimeState runtime,
        J1939TpLayer layer,
        Func<CanFrame, CancellationToken, ValueTask<Result<Unit>>> singleFrameSend,
        DbcService dbcService,
        DbcEncodeService dbcEncoder,
        ChannelRouter? router = null,
        ILogger<J1939NodeContext>? logger = null)
    {
        _config = config;
        Runtime = runtime;
        _layer = layer;
        _singleFrameSend = singleFrameSend;
        _dbcService = dbcService;
        _dbcEncoder = dbcEncoder;
        _router = router;
        _logger = logger ?? NullLogger<J1939NodeContext>.Instance;
    }

    /// <inheritdoc />
    public void Start()
    {
        _layer.RegisterLocalAddress(Sa);
        _layer.MessageReceived += OnTpMessage;
        _router?.AttachSink(this);
    }

    /// <inheritdoc />
    public void Stop()
    {
        _router?.DetachSink(this);
        _layer.MessageReceived -= OnTpMessage;
        _layer.UnregisterLocalAddress(Sa);
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    /// <summary>IFrameSink：单帧应用报文直通（TP 帧交层自己的 adapter，这里跳过防重复上报）。</summary>
    public void OnFrame(CanFrame frame)
    {
        try
        {
            if (!frame.Id.IsExtended || frame.Flags.HasFlag(FrameFlags.Rtr))
                return;
            var id = new J1939Id(frame.Id.Raw);
            if (id.PduFormat is 0xEB or 0xEC || !id.IsPdu1)
                return;

            MessageArrived?.Invoke(new NodeMessageArrived(
                new J1939MessageRef(id.Pgn, id.Priority, TpMode.Single, id.SourceAddress, id.DestinationAddress),
                id.SourceAddress, frame.Data.ToArray(), frame.Timestamp.TotalMicroseconds / 1_000_000.0));
        }
        catch (ArgumentException)
        {
            // 畸形帧：静默丢弃（sink 契约）
        }
    }

    /// <summary>IFrameSink：同路由其他 sink 抛错时被调用——只记日志（9302 同款语义），不动接收路径。</summary>
    public void OnError(Exception ex)
        => LogSiblingSinkError(_logger, ex);

    // 计划原文内联 _logger.LogWarning(ex, "J1939NodeContext: sibling sink error; …")——按仓库约定
    // 改 [LoggerMessage]（J1939TpSinkAdapter 9301/9302 同款裁定：logger 类别已携带类名，模板不重复）。
    // EventId 9421：nodes 域 9401/9402（host）、9411/9412（library）、9441（behavior）之外顺延。
    [LoggerMessage(EventId = 9421, Level = LogLevel.Warning, Message = "sibling sink error; node receive path unaffected")]
    private static partial void LogSiblingSinkError(ILogger logger, Exception ex);

    private void OnTpMessage(J1939Message m)
        => MessageArrived?.Invoke(new NodeMessageArrived(
            new J1939MessageRef(m.Pgn, m.Priority, m.Mode, m.Sa, m.Da),
            m.Sa, m.Payload, m.CompletedTimestampSec));

    /// <summary>fire-and-forget 发送（同步可完成则内联）；失败 → SendFailed/Report(Error)，绝不抛调用方。</summary>
    public void Send(MessageRef target, NodePayloadSource payload)
    {
        ValueTask<Result<Unit>> pending;
        try
        {
            pending = SendCore(target, payload);
        }
        catch (Exception ex)
        {
            SendFailed?.Invoke(ex);
            return;
        }

        if (pending.IsCompletedSuccessfully)
        {
            Observe(pending.Result);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                Observe(await pending);
            }
            catch (Exception ex)
            {
                SendFailed?.Invoke(ex);
            }
        });
    }

    private void Observe(Result<Unit> result)
    {
        if (!result.IsSuccess)
            Report(NodeActivityKind.Error, result.Error?.Message ?? "send failed");
    }

    // 修订（有据）：brief 原稿 SendCore 整体为 async 方法——NotSupportedException/FormatException
    // 等首段同步异常会被 async 语义捕获进 ValueTask 而不同步抛出（Send 的 catch 收不到），
    // SendFailed 只在线程池续跑时引发；brief 自带测试 Send_CanMessageRef_Not_Supported 在
    // Send 返回后立即断言 SendFailed（无等待），必竞态失败（RED 实测：failed 集合为空）。
    // 最小修订：校验/解析段保持同步（异常经 Send 的 catch 同步转 SendFailed），路由段原样
    // 移入 SendRouted/SendSingleAsync——Bam/RtsCts/Single 三路行为、Result 语义逐字不变。
    private ValueTask<Result<Unit>> SendCore(MessageRef target, NodePayloadSource payload)
    {
        if (target is not J1939MessageRef jref)
            throw new NotSupportedException($"CanMessageRef 后端本期不实现（spec §10）：{target.GetType().Name}");

        var bytes = payload switch
        {
            FixedHexSource hex => ParseHex(hex.Hex),
            DbcSignalsSource dbc => EncodeDbc(dbc.MessageName),
            ScriptCallbackSource script => throw new NotSupportedException($"ScriptCallbackSource '{script.CallbackRef}' 尚未支持（本期降级）"),
            _ => throw new NotSupportedException(payload.GetType().Name),
        };
        if (bytes.Length == 0 || bytes.Length > _optionsMax)
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidArgument, $"payload 长度 {bytes.Length} 越界（1..1785）"));

        var mode = jref.Mode ?? (bytes.Length > 8 ? TpMode.Bam : TpMode.Single);
        if (mode == TpMode.Bam)
            return new ValueTask<Result<Unit>>(_layer.SendBamAsync(jref.Pgn, jref.Priority, Sa, bytes));
        if (mode == TpMode.RtsCts)
        {
            if (jref.Da is null)
                return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidArgument, "RTS/CTS 发送需要目标地址 Da"));
            return new ValueTask<Result<Unit>>(_layer.SendRtsCtsAsync(jref.Pgn, jref.Priority, Sa, jref.Da.Value, bytes));
        }

        // 单帧直发：PDU1 应用帧需要 Da（GBT27930 全部应用 PGN 为 PDU1）
        if (jref.Da is null)
            return ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidArgument, "单帧发送需要目标地址 Da"));
        var id = J1939Id.Compose(jref.Priority, jref.Pgn, Sa, jref.Da.Value);
        var frame = new CanFrame(new CanId(id, FrameFormat.Extended), bytes, FrameFlags.None, ChannelId.None, default);
        return SendSingleAsync(frame);
    }

    private async ValueTask<Result<Unit>> SendSingleAsync(CanFrame frame)
        => await _singleFrameSend(frame, CancellationToken.None).ConfigureAwait(false);

    private const int _optionsMax = 1785;

    private static byte[] ParseHex(string hex)
    {
        var cleaned = hex.Replace(" ", "").Replace("-", "");
        return Convert.FromHexString(cleaned);
    }

    private byte[] EncodeDbc(string messageName)
    {
        var dbc = _dbcService.Current ?? throw new InvalidOperationException("DBC 未加载，无法按 DbcSignals 编码");
        var message = dbc.Messages.FirstOrDefault(m => string.Equals(m.Name, messageName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"DBC 中不存在消息 '{messageName}'");
        var values = new Dictionary<string, double>();
        foreach (var signal in message.Signals)
            values[signal.Name] = Runtime.TryGetSignalValue(messageName, signal.Name, out var v) ? v : 0.0;
        return _dbcEncoder.Encode(message, values);
    }

    /// <inheritdoc />
    public void Report(NodeActivityKind kind, string detail) => Reported?.Invoke(kind, detail);
}
