using Microsoft.Extensions.Logging;
using Peak.Can.Basic.BackwardCompatibility;
using PeakCan.HIL.Core;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Peak;

/// <summary>
/// PEAK-SDK implementation of <see cref="IChannelEnumerator"/>. Asks the
/// driver which channels are attached via <c>PCAN_ATTACHED_CHANNELS</c>.
/// <para>
/// <b>Never call <c>PCANBasic.Initialize</c> here.</b> Field observation:
/// after a PEAK channel has been initialized in this process, the ZLG
/// kernel plugin reports no device (<c>ZCAN_OpenDevice</c> returns 0) until
/// the app restarts. Enumerating is therefore kept read-only so opening the
/// connection dialog cannot trigger that state. The exact driver-level
/// mechanism is not proven; the read-only rule is the safe invariant either way.
/// </para>
/// </summary>
public sealed partial class PeakChannelEnumerator : IChannelEnumerator
{
    /// <summary>PCAN supports at most 16 attached channels per query.</summary>
    private const int MaxAttachedChannels = 16;

    private readonly ILogger<PeakChannelEnumerator> _logger;

    public PeakChannelEnumerator(ILogger<PeakChannelEnumerator> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string? LastDiagnostic { get; private set; }

    public IReadOnlyList<ChannelInfo> Enumerate()
    {
        LastDiagnostic = null;
        var result = new List<ChannelInfo>();
        try
        {
            var attached = new TPCANChannelInformation[MaxAttachedChannels];
            var status = PCANBasic.GetValue(
                PCANBasic.PCAN_NONEBUS, TPCANParameter.PCAN_ATTACHED_CHANNELS, attached);
            if (!PeakErrorMapper.IsOk((uint)status))
            {
                // 老驱动可能不支持 PCAN_ATTACHED_CHANNELS；此时 PEAK 会"看不到设备"。
                // 把原因交给 UI，而不是静默返回空列表。
                LastDiagnostic = $"PEAK 通道查询失败（{status}）：请确认 PEAK 驱动版本";
                LogQueryFailed(_logger, status.ToString());
                return result;
            }

            foreach (var channel in attached)
            {
                if (channel.channel_handle == 0) break;

                // 0 = unavailable, 2 = occupied by another application.
                // 1 = available, 3 = open in PCAN-View (still initiable).
                if (channel.channel_condition is PCANBasic.PCAN_CHANNEL_UNAVAILABLE
                    or PCANBasic.PCAN_CHANNEL_OCCUPIED)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(channel.device_name)
                    ? $"PCAN-USB {channel.channel_handle - 0x50}"
                    : channel.device_name;
                result.Add(new ChannelInfo(channel.channel_handle, name));
            }
        }
        catch (Exception ex)
        {
            // DllNotFound, EntryPointNotFound, etc. on machines without the
            // PEAK driver. Report once; the provider still emits its device entry.
            LastDiagnostic = $"PEAK 枚举失败：{ex.GetType().Name}（可能未安装 PEAK 驱动）";
            LogProbeFailed(_logger, ex);
        }
        return result;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "PEAK attached-channel query returned {Status}; treating as no hardware")]
    private static partial void LogQueryFailed(ILogger logger, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PEAK enumeration failed; treating as no hardware")]
    private static partial void LogProbeFailed(ILogger logger, Exception error);
}
