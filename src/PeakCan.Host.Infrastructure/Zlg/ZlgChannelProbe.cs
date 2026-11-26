using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>
/// ZLG 通道探测。按 ZCAN 句柄语义打开设备、读取设备信息、校验通道号后释放。
/// 复用 <see cref="ZlgDeviceManager"/>，不会关闭正在被通道使用的设备。
/// </summary>
public sealed class ZlgChannelProbe : IChannelProbe
{
    private readonly IZlgApi _api;
    private readonly ZlgDeviceManager _devices;
    private readonly ILogger<ZlgChannelProbe> _logger;

    public ZlgChannelProbe(
        IZlgApi api,
        ZlgDeviceManager devices,
        ILogger<ZlgChannelProbe>? logger = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _logger = logger ?? NullLogger<ZlgChannelProbe>.Instance;
    }

    public ProbeResult Probe(ushort handle)
    {
        if ((handle & 0x8000) == 0)
            return new ProbeResult(false, "Not a ZLG handle range");

        var devType = (uint)((handle >> 8) & 0x7F);
        var devIdx = (uint)((handle >> 4) & 0x0F);
        var canIdx = (uint)(handle & 0x0F);

        try
        {
            if (!_devices.TryAcquire(devType, devIdx, out var deviceHandle))
                return new ProbeResult(false, $"ZLG device not found: type={devType} idx={devIdx}");

            try
            {
                var info = ZcanDeviceInfo.Create();
                var ret = _api.GetDeviceInf(deviceHandle, ref info);
                if (ret != ZlgError.Success)
                    return new ProbeResult(false, $"ZLG GetDeviceInf failed: {ret}");

                if (canIdx >= info.CanNum)
                    return new ProbeResult(
                        false, $"ZLG channel {canIdx} out of range (device has {info.CanNum})");

                return new ProbeResult(true, $"ZLG dev {devType}/{devIdx} ch{canIdx} detected");
            }
            finally
            {
                _devices.Release(devType, devIdx);
            }
        }
        catch (DllNotFoundException)
        {
            return new ProbeResult(false, "zlgcan.dll not found (x86 required)");
        }
        catch (BadImageFormatException)
        {
            return new ProbeResult(false, "zlgcan.dll bitness mismatch (x86 required)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ZLG probe failed: handle=0x{Handle:X4}", handle);
            return new ProbeResult(false, $"ZLG probe exception: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
