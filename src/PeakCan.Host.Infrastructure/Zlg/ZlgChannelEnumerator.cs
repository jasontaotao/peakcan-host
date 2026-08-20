using Microsoft.Extensions.Logging;
using PeakCan.HIL.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>
/// ZLG 通道枚举器。遍历已知设备类型和设备索引，发现可用设备。
/// </summary>
public sealed class ZlgChannelEnumerator : IChannelEnumerator
{
    private static readonly uint[] KnownDeviceTypes =
    {
        ZlgDeviceType.USBCANFD_200U,
        ZlgDeviceType.USBCANFD,
        ZlgDeviceType.USBCAN2,
        ZlgDeviceType.USBCAN1,
    };

    private const uint MaxDeviceIndex = 4;
    private const uint MaxCanChannels = 2;

    private readonly ILogger<ZlgChannelEnumerator> _logger;

    public ZlgChannelEnumerator(ILogger<ZlgChannelEnumerator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IReadOnlyList<ChannelInfo> Enumerate()
    {
        var result = new List<ChannelInfo>();
        _logger.LogInformation("ZLG enumeration start: {Count} device types", KnownDeviceTypes.Length);
        foreach (var devType in KnownDeviceTypes)
        {
            for (uint devIdx = 0; devIdx < MaxDeviceIndex; devIdx++)
            {
                try
                {
                    _logger.LogInformation("ZLG trying OpenDevice devType={DevType} devIdx={DevIdx}", devType, devIdx);
                    var ret = ZlgNative.ZCAN_OpenDevice(devType, devIdx, 0);
                    _logger.LogInformation("ZLG OpenDevice returned {Ret}", ret);
                    if (ret != ZlgError.Success) continue;

                    // 设备打开了，枚举通道
                    _logger.LogInformation("ZLG device opened successfully: devType={DevType} devIdx={DevIdx}", devType, devIdx);
                    try
                    {
                        // 尝试读取设备信息确认设备存在
                        var boardRet = ZlgNative.ZCAN_GetDeviceInf(devType, devIdx, out var info);
                        _logger.LogInformation("ZLG ReadBoardInfo returned {Ret}", boardRet);
                        if (boardRet == ZlgError.Success)
                        {
                            var canCount = info.canNum > 0 ? info.canNum : MaxCanChannels;
                            var devName = System.Text.Encoding.ASCII.GetString(info.strDeviceType).TrimEnd('\0');
                            _logger.LogInformation("ZLG device found: {Name}, {Count} channels", devName, canCount);
                            for (uint ch = 0; ch < canCount && ch < MaxCanChannels; ch++)
                            {
                                var handle = EncodeHandle(devType, devIdx, ch);
                                var name = $"{devName} {devIdx}-{ch}";
                                result.Add(new ChannelInfo(handle, name));
                            }
                        }
                        else
                        {
                            // ReadBoardInfo 失败，按默认 2 通道处理
                            _logger.LogWarning("ZLG ReadBoardInfo failed, using default channels");
                            for (uint ch = 0; ch < MaxCanChannels; ch++)
                            {
                                var handle = EncodeHandle(devType, devIdx, ch);
                                var name = $"ZLG-{devType}-{devIdx}-{ch}";
                                result.Add(new ChannelInfo(handle, name));
                            }
                        }
                    }
                    finally
                    {
                        ZlgNative.ZCAN_CloseDevice(devType, devIdx);
                    }
                }
                catch (DllNotFoundException dllEx)
                {
                    _logger.LogError(dllEx, "ZLG zlgcan.dll not found! Make sure the DLL is in the output directory");
                    break;
                }
                catch (Exception ex)
                {
                    // DllNotFound 等异常，记录日志后继续
                    _logger.LogWarning(ex, "ZLG probe failed devType={DevType} devIdx={DevIdx}", devType, devIdx);
                    break;
                }
            }
        }
        _logger.LogInformation("ZLG enumeration complete: {Count} channels found", result.Count);
        return result;
    }

    public static ushort EncodeHandle(uint devType, uint devIdx, uint canIdx)
        => (ushort)(0x8000 | ((devType & 0x7F) << 8) | ((devIdx & 0x0F) << 4) | (canIdx & 0x0F));
}