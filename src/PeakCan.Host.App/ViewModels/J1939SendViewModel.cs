using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;
using PeakCan.HIL.Core.J1939;
using PeakCan.HIL.Core.Services;
using PeakCan.Host.App.Services;
using PeakCan.Host.App.Services.J1939;

namespace PeakCan.Host.App.ViewModels;

/// <summary>
/// View-model for the Send tab's J1939 sub-panel (spec §8.2): PGN / 优先级 /
/// SA / DA / 模式（单帧、BAM、RTS-CTS）/ 载荷 / 周期字段，单次发送 +
/// 周期发送命令，错误信息直显 <see cref="Status"/>。
/// <para>
/// 按模式分流：<see cref="TpMode.Single"/> → <see cref="SendService.SendAsync"/>
/// （<see cref="J1939Id.Compose"/> 组 ID）；BAM / RTS-CTS →
/// <see cref="J1939TpLayer.SendBamAsync"/> / <see cref="J1939TpLayer.SendRtsCtsAsync"/>
/// （async void fire-and-forget + try/catch 置状态——TP 任务在整包收尾后才完成，
/// 命令不等它）。周期发送经 <see cref="J1939CyclicSendService"/>（tick 侧 _inFlight
/// 闸防在途重入）。
/// </para>
/// <para>
/// 与 SendViewModel 同屏：<see cref="SendViewModel.J1939"/> 暴露本 VM，
/// SendView.xaml 以 <c>J1939.*</c> 绑定（构造注入，DI 单例）。
/// </para>
/// </summary>
public sealed partial class J1939SendViewModel : ObservableObject
{
    private readonly J1939TpLayer _layer;
    private readonly SendService _send;
    private readonly ILogger<J1939SendViewModel> _logger;
    private readonly J1939CyclicSendService _cyclic;

    /// <summary>PGN（hex，支持 0x 前缀），默认充电握手 BRM。</summary>
    [ObservableProperty]
    private string _pgnHex = "0x0200";

    /// <summary>J1939 优先级（0–7，十进制；越界由 Compose 抛出转消息）。</summary>
    [ObservableProperty]
    private string _priority = "6";

    /// <summary>源地址 SA（hex，支持 0x 前缀）。</summary>
    [ObservableProperty]
    private string _sourceAddressHex = "0xF4";

    /// <summary>目标地址 DA（hex；留空 = 无 DA——BAM 广播 / PDU2 PGN 不带 DA）。</summary>
    [ObservableProperty]
    private string _destinationAddressHex = "0x56";

    /// <summary>发送模式索引：0=单帧 1=BAM 2=RTS-CTS。</summary>
    [ObservableProperty]
    private int _modeIndex;

    /// <summary>载荷（hex 字节序列，空格/连字符为分隔符——与 SendViewModel 同款解析）。</summary>
    [ObservableProperty]
    private string _payloadHex = string.Empty;

    /// <summary>周期发送间隔（ms）；0 = 单次（周期发送按钮拒绝 0/负值）。</summary>
    [ObservableProperty]
    private string _intervalMs = "0";

    /// <summary>BAM 帧间隔参考值（ms，50–200）；实际间隔由 J1939TpLayer 的
    /// J1939TpOptions.BamIntervalMs 决定，此字段仅作面板参考展示位。</summary>
    [ObservableProperty]
    private string _bamIntervalMs = "50";

    /// <summary>状态文本（错误信息直显：解析 / Compose / 长度校验异常转消息）。</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Production ctor — DI 注入层、单帧发送服务、周期服务（tests 可省略周期服务，回退内置 CyclicTimerFactory 实例）。</summary>
    public J1939SendViewModel(
        J1939TpLayer layer,
        SendService send,
        ILogger<J1939SendViewModel> logger,
        J1939CyclicSendService? cyclic = null)
    {
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cyclic = cyclic ?? new J1939CyclicSendService(new CyclicTimerFactory());
    }

    /// <summary>单次发送（命令）：解析 + 校验后按模式分流；错误直显 Status。</summary>
    [RelayCommand]
    private void Send()
    {
        if (!TryBuildSpec(out var spec, out var error))
        {
            Status = error;
            LogSendRejected(_logger, error);
            return;
        }

        // async void fire-and-forget（brief 契约）：TP 任务在整包收尾后才完成，
        // 命令不等它；异常在 SendCore 内 try/catch 转状态，不逃逸命令。
        SendCore(spec);
    }

    /// <summary>启动周期发送（命令）：与单次发送同一套解析/校验；周期需 &gt; 0 ms；
    /// 单帧 + PDU2 PGN 拒绝启动（tick 侧 <see cref="J1939Id.Compose"/> 对 PDU2 禁 DA，
    /// 否则每 tick 抛异常、仅 FailureCount 递增，UI 却显示已启动）。</summary>
    [RelayCommand]
    private void StartCyclic()
    {
        if (!TryBuildSpec(out var spec, out var error))
        {
            Status = error;
            LogSendRejected(_logger, error);
            return;
        }
        if (!int.TryParse(IntervalMs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intervalMs) || intervalMs <= 0)
        {
            Status = "周期需为正整数毫秒（0=单次，请用发送按钮）";
            LogSendRejected(_logger, Status);
            return;
        }
        if (spec.Mode == TpMode.Single && !J1939Id.IsPdu1Pgn(spec.Pgn))
        {
            Status = "PDU2 PGN（PF≥0xF0）不支持单帧循环发送；请改用 BAM 或 RTS-CTS 模式";
            LogSendRejected(_logger, Status);
            return;
        }
        _cyclic.Start(spec, TimeSpan.FromMilliseconds(intervalMs));
        Status = $"J1939 循环发送已启动（{intervalMs} ms）";
    }

    /// <summary>停止周期发送（命令）。幂等。</summary>
    [RelayCommand]
    private void StopCyclic()
    {
        _cyclic.Stop();
        Status = "J1939 循环发送已停止";
    }

    /// <summary>按模式分流执行发送；异常（Compose 越界 / 通道故障）转 Status，不逃逸 async void。</summary>
    private async void SendCore(J1939SendSpec spec)
    {
        try
        {
            switch (spec.Mode)
            {
                case TpMode.Single:
                {
                    var frame = new CanFrame(
                        new CanId(J1939Id.Compose(spec.Priority, spec.Pgn, spec.Sa, spec.Da), FrameFormat.Extended),
                        spec.Payload, FrameFlags.None, ChannelId.None, default);
                    var r = await spec.SingleFrameSend(frame, CancellationToken.None).ConfigureAwait(true);
                    Status = r.IsSuccess
                        ? $"已发送 {spec.Payload.Length} 字节 @ 0x{frame.Id.Raw:X8}"
                        : $"FAIL: {r.Error!.Code} {r.Error.Message}";
                    break;
                }
                case TpMode.Bam:
                {
                    var r = await spec.Layer.SendBamAsync(spec.Pgn, spec.Priority, spec.Sa, spec.Payload).ConfigureAwait(true);
                    Status = r.IsSuccess
                        ? $"BAM 已发送（{spec.Payload.Length} 字节）"
                        : $"FAIL: {r.Error!.Code} {r.Error.Message}";
                    break;
                }
                case TpMode.RtsCts:
                {
                    if (spec.Da is not { } da)
                    {
                        Status = DaRequiredError;
                        return;
                    }
                    var r = await spec.Layer.SendRtsCtsAsync(spec.Pgn, spec.Priority, spec.Sa, da, spec.Payload).ConfigureAwait(true);
                    Status = r.IsSuccess
                        ? $"RTS-CTS 已发送（{spec.Payload.Length} 字节）"
                        : $"FAIL: {r.Error!.Code} {r.Error.Message}";
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // Never let an exception escape an async void — the WPF
            // dispatcher would surface it as unhandled and crash the app.
            Status = $"FAIL: {ex.Message}";
            LogSendThrew(_logger, ex);
        }
    }

    /// <summary>RTS-CTS 模式的 DA 必填错误文案（brief 测试锚点："目标地址"）。</summary>
    private const string DaRequiredError = "RTS-CTS 模式必须提供目标地址（DA）";

    /// <summary>
    /// 解析全部字段并应用模式规则，产出 <see cref="J1939SendSpec"/>（Send 与
    /// StartCyclic 共用，保证两条路径同一套校验）。失败时 <paramref name="error"/>
    /// 携带直显消息。
    /// </summary>
    private bool TryBuildSpec([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out J1939SendSpec? spec, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        spec = null;
        if (!TryParseHexUInt32(PgnHex, out var pgn))
        {
            error = $"PGN 无效: {PgnHex}";
            return false;
        }
        if (!byte.TryParse(Priority, NumberStyles.Integer, CultureInfo.InvariantCulture, out var priority))
        {
            error = $"优先级无效: {Priority}";
            return false;
        }
        if (!TryParseHexByte(SourceAddressHex, out var sa))
        {
            error = $"源地址无效: {SourceAddressHex}";
            return false;
        }
        byte? da = null;
        if (!string.IsNullOrWhiteSpace(DestinationAddressHex))
        {
            if (!TryParseHexByte(DestinationAddressHex, out var parsedDa))
            {
                error = $"目标地址无效: {DestinationAddressHex}";
                return false;
            }
            da = parsedDa;
        }
        var mode = ModeIndex switch
        {
            0 => TpMode.Single,
            1 => TpMode.Bam,
            2 => TpMode.RtsCts,
            _ => (TpMode?)null,
        };
        if (mode is null)
        {
            error = $"模式无效: {ModeIndex}";
            return false;
        }
        byte[] payload;
        try
        {
            payload = ParseHex(PayloadHex);
        }
        catch (FormatException ex)
        {
            error = $"载荷无效: {ex.Message}";
            return false;
        }
        // RTS-CTS 点对点必带 DA（brief 契约：Da 必填）；BAM 广播 / 单帧 PDU2 可留空。
        if (mode == TpMode.RtsCts && da is null)
        {
            error = DaRequiredError;
            return false;
        }
        // 修订 13：≤8 字节应走单帧，TP 模式下发层会拒绝——入口先给出指引。
        if (mode != TpMode.Single && payload.Length is >= 1 and <= 8)
        {
            error = "≤8 字节请选择单帧模式";
            return false;
        }
        spec = new J1939SendSpec(
            _layer,
            (frame, ct) => _send.SendAsync(frame, ct),
            pgn, priority, sa, da, payload, mode.Value);
        error = null;
        return true;
    }

    /// <summary>
    /// Parse a hex string into bytes（镜像 SendViewModel.ParseHex）：空格/连字符
    /// 为分隔符，奇数长度前补 0；空输入抛 <see cref="FormatException"/>。
    /// </summary>
    /// <exception cref="FormatException">残留非 hex 字符，或输入仅分隔符/为空。</exception>
    private static byte[] ParseHex(string s)
    {
        var stripped = s.Replace(" ", string.Empty, StringComparison.Ordinal)
                        .Replace("-", string.Empty, StringComparison.Ordinal);
        if (stripped.Length == 0)
        {
            throw new FormatException("hex 为空（仅分隔符或无输入）");
        }
        if ((stripped.Length & 1) == 1)
        {
            stripped = "0" + stripped;
        }
        var bytes = new byte[stripped.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = byte.Parse(stripped.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        return bytes;
    }

    /// <summary>Parse a hex uint（可选 0x 前缀，如 "0x0200" / "0200"）。</summary>
    private static bool TryParseHexUInt32(string text, out uint value)
    {
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Parse a hex byte（可选 0x 前缀，如 "0xF4" / "F4"）。</summary>
    private static bool TryParseHexByte(string text, out byte value)
    {
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }
        return byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "J1939 send rejected: {Reason}")]
    private static partial void LogSendRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "J1939 send threw")]
    private static partial void LogSendThrew(ILogger logger, Exception ex);
}
