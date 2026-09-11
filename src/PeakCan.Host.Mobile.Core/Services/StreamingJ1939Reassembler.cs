using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.Host.Core.J1939;
using PeakCan.Host.Core.Replay;

namespace PeakCan.Host.Mobile.Core.Services;

/// <summary>J1939 TP 重组行（供 J1939 tab 列表展示）。</summary>
/// <param name="StatusText">完成 / 截断 / 丢包。</param>
public sealed record J1939ReassembledRow(
    string PgnText,
    string SaText,
    string DaText,
    string ModeText,
    string LengthText,
    string CompletedText,
    string StatusText);

/// <summary>
/// 流式 J1939 TP 重组：播放器帧流在 ID 过滤之前 tap（spec §6）。复用 Host.Core
/// <see cref="J1939TpLayer"/> 的 Offline 模式——不启 watchdog、禁止发送、完整性判定
/// 由 <see cref="Flush"/> 结算。线程契约（review 修正）：Ingest 只在 player 线程；
/// Reset/Flush 由调用方时序保证不并发（Stop/Seek/重播/换文件在 UI 线程发起）。
/// 即使瞬时并发也无害——Reset 原子替换 layer 引用，straggler 帧落入新层。
/// 事件引发在调用线程（UI 侧经 dispatcher Post）。
///</summary>
public sealed class StreamingJ1939Reassembler
{
    private readonly Func<CanFrame, CancellationToken, ValueTask<Result<Unit>>> _sendAsync;
    private J1939TpLayer _layer;
    private int _malformedCount;

    public StreamingJ1939Reassembler()
    {
        // 与桌面 J1939ReassemblyService 构造逐字一致：离线重组从不发送
        _sendAsync = (_, _) =>
            ValueTask.FromResult(Result<Unit>.Fail(ErrorCode.InvalidState, "offline reassembly never sends"));
        _layer = CreateLayer();
    }

    /// <summary>完整重组消息引发（调用线程）。</summary>
    public event Action<J1939ReassembledRow>? MessageReassembled;

    /// <summary>被窄捕获的畸形 TP 帧数（诊断用）。</summary>
    public int MalformedCount => _malformedCount;

    /// <summary>喂入一帧播放数据；仅扩展帧进入层，畸形 TP 帧窄捕获并计数。</summary>
    public void Ingest(ReplayFrame frame)
    {
        if (!frame.IsExtended) return;
        try
        {
            _layer.ProcessFrame(ToCanFrame(frame));
        }
        catch (ArgumentException)
        {
            Interlocked.Increment(ref _malformedCount);
        }
    }

    /// <summary>
    /// EOF 结算：未闭合会话按 Truncated/PacketLoss 逐条引发。先按
    /// (Last, First, Sa, Da) 稳定预排序（桌面注释明确要求：FlushPendingSessions
    /// 按字典枚举顺序返回，不可依赖——(Sa, Da) 唯一标识会话，为全序）。
    /// </summary>
    public void Flush()
    {
        var pending = _layer.FlushPendingSessions()
            .OrderBy(r => r.LastFrameTimestampSec)
            .ThenBy(r => r.FirstFrameTimestampSec)
            .ThenBy(r => r.Sa)
            .ThenBy(r => r.Da);

        foreach (var result in pending)
        {
            var message = new J1939Message(
                result.Pgn, result.Sa, result.Da, result.Priority, result.Mode,
                result.PartialPayload, result.FirstFrameTimestampSec, result.LastFrameTimestampSec);
            Raise(FromMessage(message),
                result.Outcome == J1939SessionOutcome.PacketLoss ? "丢包" : "截断");
        }
    }

    /// <summary>Seek/Stop/重播/换文件：整体丢弃未闭合会话（new 新层替换，旧层随 GC 回收）。</summary>
    public void Reset()
    {
        _layer.MessageReceived -= OnMessageReceived;
        _layer = CreateLayer();
    }

    private J1939TpLayer CreateLayer()
    {
        var layer = new J1939TpLayer(_sendAsync, J1939TpOptions.Offline);
        layer.MessageReceived += OnMessageReceived;
        return layer;
    }

    private void OnMessageReceived(J1939Message message) => Raise(FromMessage(message), "完成");

    private void Raise(J1939ReassembledRow row, string status)
        => MessageReassembled?.Invoke(row with { StatusText = status });

    private static J1939ReassembledRow FromMessage(J1939Message m)
        => new(
            $"0x{m.Pgn:X6}",
            m.Sa.ToString("X2", System.Globalization.CultureInfo.InvariantCulture),
            m.Da.ToString("X2", System.Globalization.CultureInfo.InvariantCulture),
            m.Mode switch
            {
                TpMode.Bam => "BAM",
                TpMode.RtsCts => "RTS/CTS",
                TpMode.Single => "SINGLE",
                _ => m.Mode.ToString(),
            },
            m.Payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            m.CompletedTimestampSec.ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
            string.Empty);

    /// <summary>ReplayFrame → CanFrame 适配：与桌面 J1939ReassemblyService.ToCanFrame
    /// 转换逻辑相同（6 行纯函数；两侧均有测试钉住）。</summary>
    private static CanFrame ToCanFrame(ReplayFrame frame)
    {
        var format = frame.IsExtended ? FrameFormat.Extended : FrameFormat.Standard;
        var totalUs = (ulong)(frame.Timestamp * 1_000_000.0);
        return new CanFrame(
            new CanId(frame.Id, format),
            frame.Data,
            frame.Flags,
            ChannelId.None,
            new Timestamp(totalUs));
    }
}