using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.Host.Core;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>
/// ZLG 通道枚举器。遍历 USBCANFD 系列的已知设备类型与设备索引，
/// 用 <see cref="ZlgDeviceManager"/> 打开/释放设备（引用计数保护正在使用的设备）。
/// <para>OpenDevice 成功返回非 0 句柄；0 才是失败。GetDeviceInf 失败时不伪造通道。</para>
/// </summary>
public sealed class ZlgChannelEnumerator : IChannelEnumerator
{
    private const uint MaxDeviceIndex = 4;
    private const uint MaxCanChannels = 15;

    private readonly IZlgApi _api;
    private readonly ZlgDeviceManager _devices;
    private readonly ILogger<ZlgChannelEnumerator> _logger;

    public ZlgChannelEnumerator(
        IZlgApi api,
        ZlgDeviceManager devices,
        ILogger<ZlgChannelEnumerator>? logger = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _logger = logger ?? NullLogger<ZlgChannelEnumerator>.Instance;
    }

    /// <inheritdoc />
    public string? LastDiagnostic { get; private set; }

    public IReadOnlyList<ChannelInfo> Enumerate()
    {
        LastDiagnostic = null;
        var result = new List<ChannelInfo>();
        var seen = new HashSet<ushort>();

        try
        {
            foreach (var devType in ZlgDeviceType.All)
            {
                for (uint devIdx = 0; devIdx < MaxDeviceIndex; devIdx++)
                {
                    if (!_devices.TryAcquire(devType, devIdx, out var handle))
                        continue;

                    try
                    {
                        var info = ZcanDeviceInfo.Create();
                        var ret = _api.GetDeviceInf(handle, ref info);
                        if (ret != ZlgError.Success)
                        {
                            _logger.LogWarning(
                                "ZLG GetDeviceInf failed: devType={DevType} devIdx={DevIdx} ret={Ret}",
                                devType, devIdx, ret);
                            continue;
                        }

                        var canCount = Math.Min((uint)info.CanNum, MaxCanChannels);
                        if (canCount == 0)
                        {
                            _logger.LogDebug(
                                "ZLG device reports 0 channels: devType={DevType} devIdx={DevIdx}",
                                devType, devIdx);
                            continue;
                        }

                        var devName = ReadAscii(info.HwType);
                        if (string.IsNullOrWhiteSpace(devName))
                            devName = $"ZLG-{devType}";

                        _logger.LogInformation(
                            "ZLG device found: {Name} type={DevType} idx={DevIdx} channels={Count}",
                            devName, devType, devIdx, canCount);

                        for (uint ch = 0; ch < canCount; ch++)
                        {
                            var channelHandle = EncodeHandle(devType, devIdx, ch);
                            if (seen.Add(channelHandle))
                                result.Add(new ChannelInfo(channelHandle, $"{devName} {devIdx}-{ch}"));
                        }
                    }
                    finally
                    {
                        _devices.Release(devType, devIdx);
                    }
                }
            }
        }
        catch (DllNotFoundException ex)
        {
            LastDiagnostic = "zlgcan.dll 未找到：请把 x86 的 zlgcan.dll 放到程序目录";
            _logger.LogError(ex, "ZLG zlgcan.dll not found");
        }
        catch (BadImageFormatException ex)
        {
            LastDiagnostic = "zlgcan.dll 位数不匹配：当前程序必须为 32 位（x86）";
            _logger.LogError(ex, "ZLG zlgcan.dll bitness mismatch");
        }
        catch (EntryPointNotFoundException ex)
        {
            LastDiagnostic = "zlgcan.dll 版本不匹配：缺少 ZCAN 入口";
            _logger.LogError(ex, "ZLG zlgcan.dll entry point missing");
        }
        catch (Exception ex)
        {
            // IChannelEnumerator 契约要求 Non-throwing：驱动热插拔时可能抛
            // SEHException / MarshalDirectiveException，不能让异常穿到 UI。
            LastDiagnostic = $"ZLG 枚举失败：{ex.GetType().Name}";
            _logger.LogWarning(ex, "ZLG enumeration failed unexpectedly");
        }

        if (result.Count == 0 && LastDiagnostic is null)
        {
            // 空结果本身是正常状态，但对用户没有可操作性：区分"没插""被占用"
            // "驱动/插件缺失"需要现场提示，否则只能看到一句"没有设备"。
            LastDiagnostic =
                "未发现 ZLG 设备：请确认 USB 已连接、ZLG 驱动已安装，"
                + "并完全退出 ZCANPRO/CANTest 等会占用设备的程序；"
                + "若已占用，重启本程序后再试。";
        }

        _logger.LogInformation("ZLG enumeration complete: {Count} channels", result.Count);
        return result;
    }

    public static ushort EncodeHandle(uint devType, uint devIdx, uint canIdx)
        => (ushort)(0x8000 | ((devType & 0x7F) << 8) | ((devIdx & 0x0F) << 4) | (canIdx & 0x0F));

    private static string ReadAscii(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return string.Empty;
        var end = Array.IndexOf(bytes, (byte)0);
        if (end < 0) end = bytes.Length;
        return Encoding.ASCII.GetString(bytes, 0, end).Trim();
    }
}
