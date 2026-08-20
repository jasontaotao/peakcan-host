using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>
/// ZLG 通道探测。实现 <see cref="IChannelProbe"/>，对指定 handle 执行探测。
/// 通过 <see cref="CompositeChannelProbe"/> 与 PEAK 的 probe 共存。
/// </summary>
public sealed class ZlgChannelProbe : IChannelProbe
{
    private readonly ILogger<ZlgChannelProbe> _logger;

    public ZlgChannelProbe(ILogger<ZlgChannelProbe>? logger = null)
    {
        _logger = logger ?? NullLogger<ZlgChannelProbe>.Instance;
    }

    public ProbeResult Probe(ushort handle)
    {
        // 只处理 ZLG 范围的 handle（高 1 位固定 1）
        if (handle < 0x0100)
            return new ProbeResult(false, "Not a ZLG handle range");

        var devType = (uint)((handle >> 8) & 0x7F);
        var devIdx = (uint)((handle >> 4) & 0x0F);
        var canIdx = (uint)(handle & 0x0F);

        try
        {
            // 对于 ZLG，探测 = 尝试打开设备 + 初始化 CAN 通道
            var openRet = ZlgNative.ZCAN_OpenDevice(devType, devIdx, 0);
            if (openRet != ZlgError.Success)
            {
                var (code, msg) = ZlgErrorMapper.ToErrorCode(openRet);
                return new ProbeResult(false, $"ZLG probe failed: {code} {msg}");
            }

            // 快速初始化/复位测试
            try { ZlgNative.ZCAN_ResetCAN(devType, devIdx, canIdx); }
            catch { /* best-effort */ }
            return new ProbeResult(true, $"ZLG dev {devType}/{devIdx} ch{canIdx} detected");
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, $"ZLG probe exception: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 确保设备关闭，不泄漏
            try { ZlgNative.ZCAN_CloseDevice(devType, devIdx); }
            catch { /* best-effort */ }
        }
    }
}