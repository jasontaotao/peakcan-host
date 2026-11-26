using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PeakCan.HIL.Core;
using PeakCan.Host.Core;
using PeakCan.Host.Infrastructure.Channel;

namespace PeakCan.Host.Infrastructure.Zlg;

/// <summary>
/// ZLG USBCANFD 通道，实现 <see cref="ICanChannel"/>。
/// <para>
/// ZCAN 是句柄式 API：设备句柄由 <see cref="ZlgDeviceManager"/> 持有并引用计数，
/// 通道句柄由 <c>ZCAN_InitCAN</c> 返回。所有读写都用通道句柄，绝不把原生句柄
/// 与 <see cref="ChannelId.Handle"/> 的 16 位合成编码混用。
/// </para>
/// </summary>
public sealed partial class ZlgCanChannel : ChannelReadLoop, ICanChannel
{
    internal new const int MaxConsecutiveReadFailures = ChannelReadLoop.MaxConsecutiveReadFailures;

    private const int ReceiveBatchSize = 32;
    private const int ReadWaitTimeMs = 0;
    // GetReceiveNum 对不支持的类型可能返回错误码；超过此值视为异常而不是帧数。
    private const uint MaxPlausibleFrames = 100_000;

    private readonly uint _devType;
    private readonly uint _devIdx;
    private readonly uint _canIdx;
    private readonly ZlgDeviceManager _deviceManager;
    private readonly IZlgApi _api;
    private readonly ILogger<ZlgCanChannel> _logger;

    private readonly ZcanReceiveData[] _classicBuffer = CreateClassicBuffer();
    private readonly ZcanReceiveFdData[] _fdBuffer = CreateFdBuffer();

    private readonly object _connectLock = new();
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private bool _connected;
    private bool _connecting;
    private bool _disconnecting;
    private IntPtr _channelHandle;

    public bool IsConnected => _connected;

    public event Action<CanFrame>? FrameReceived;

    public ZlgCanChannel(
        ChannelId id,
        ZlgDeviceManager deviceManager,
        IZlgApi api,
        ILogger<ZlgCanChannel>? logger = null)
        : base(id, logger ?? NullLogger<ZlgCanChannel>.Instance)
    {
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _logger = logger ?? NullLogger<ZlgCanChannel>.Instance;

        _devType = (uint)((id.Handle >> 8) & 0x7F);
        _devIdx = (uint)((id.Handle >> 4) & 0x0F);
        _canIdx = (uint)(id.Handle & 0x0F);
    }

    public async Task<Result<Unit>> ConnectAsync(BaudRate baud, bool fd, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_connectLock)
        {
            if (_connected || _connecting)
                return Result<Unit>.Fail(
                    ErrorCode.InvalidState,
                    _connected ? "Channel is already connected" : "Channel is connecting");
            if (_disconnecting)
                return Result<Unit>.Fail(ErrorCode.InvalidState, "Channel is disconnecting");
            // 占位：Init/Start 在锁外执行，必须防止两个并发 Connect 各自打开
            // 通道后互相覆盖句柄/读循环。
            _connecting = true;
        }

        var releaseDevice = true;
        try
        {
            if (!_deviceManager.TryAcquire(_devType, _devIdx, out var deviceHandle))
                return Result<Unit>.Fail(
                    ErrorCode.HardwareNotAvailable,
                    $"OpenDevice failed: devType={_devType} devIdx={_devIdx}");
            var (arbBps, dataBps) = ZlgBaudRate.Resolve(baud, fd);
            if (arbBps is null)
                return Result<Unit>.Fail(
                    ErrorCode.HardwareParameter, $"Unsupported baud rate: {baud.Name}");

            var abitRet = _api.SetValue(
                deviceHandle,
                $"{_canIdx}/canfd_abit_baud_rate",
                arbBps.Value.ToString(CultureInfo.InvariantCulture));

            (byte Timing0, byte Timing1)? fallbackTiming = null;
            if (abitRet != ZlgError.Success)
            {
                if (fd)
                {
                    return Result<Unit>.Fail(
                        ErrorCode.HardwareParameter, $"SetValue(abit) failed: {abitRet}");
                }

                fallbackTiming = ZlgBaudRate.ClassicTiming(baud.Name);
                if (fallbackTiming is null)
                {
                    return Result<Unit>.Fail(
                        ErrorCode.HardwareParameter,
                        $"SetValue(abit) failed: {abitRet} (no classic timing fallback for {baud.Name})");
                }

                _logger.LogWarning(
                    "ZLG SetValue(abit) failed ({Ret}); falling back to classic timing for {Baud}",
                    abitRet, baud.Name);
            }

            if (fd)
            {
                if (dataBps is null)
                    return Result<Unit>.Fail(
                        ErrorCode.HardwareParameter, $"Missing FD data baud rate for {baud.Name}");

                var dbitRet = _api.SetValue(
                    deviceHandle,
                    $"{_canIdx}/canfd_dbit_baud_rate",
                    dataBps.Value.ToString(CultureInfo.InvariantCulture));
                if (dbitRet != ZlgError.Success)
                    return Result<Unit>.Fail(
                        ErrorCode.HardwareParameter, $"SetValue(dbit) failed: {dbitRet}");

                // ISO CAN FD（非 ISO 总线需要后续提供切换项）。
                var isoRet = _api.SetValue(deviceHandle, $"{_canIdx}/canfd_standard", "0");
                if (isoRet != ZlgError.Success)
                    _logger.LogWarning("ZLG SetValue(canfd_standard) returned {Ret}", isoRet);
            }

            var config = ZlgBaudRate.BuildConfig(fd, fallbackTiming);
            var channelHandle = _api.InitCan(deviceHandle, _canIdx, ref config);
            if (channelHandle == IntPtr.Zero)
                return Result<Unit>.Fail(ErrorCode.HardwareNotAvailable, "InitCAN failed");

            var startRet = _api.StartCan(channelHandle);
            if (startRet != ZlgError.Success)
            {
                _ = _api.ResetCan(channelHandle);
                return Result<Unit>.Fail(
                    ErrorCode.HardwareNotAvailable, $"StartCAN failed: {startRet}");
            }

            var cts = new CancellationTokenSource();
            lock (_connectLock)
            {
                _channelHandle = channelHandle;
                _cts = cts;
                _readLoop = Task.Run(() => ReadLoopAsync(cts.Token), ct);
                _connected = true;
            }

            releaseDevice = false;
            return Result<Unit>.Ok(default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ZLG connect failed: handle=0x{Handle:X4}", Id.Handle);
            return Result<Unit>.Fail(ErrorCode.HardwareNotAvailable, ex.Message);
        }
        finally
        {
            if (releaseDevice)
                _deviceManager.Release(_devType, _devIdx);
            lock (_connectLock) { _connecting = false; }
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? cts;
        Task? loop;
        IntPtr channelHandle;
        lock (_connectLock)
        {
            if (!_connected) return;
            _disconnecting = true;
            cts = _cts;
            loop = _readLoop;
            channelHandle = _channelHandle;
            _connected = false;
            _cts = null;
            _readLoop = null;
            _channelHandle = IntPtr.Zero;
        }

        try
        {
            try { cts?.Cancel(); }
            catch { /* best-effort */ }

            if (loop is not null)
            {
                try { await loop.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected */ }
            }

            if (channelHandle != IntPtr.Zero)
            {
                try { _ = _api.ResetCan(channelHandle); }
                catch { /* best-effort */ }
            }

            _deviceManager.Release(_devType, _devIdx);
        }
        finally
        {
            cts?.Dispose();
            lock (_connectLock) { _disconnecting = false; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 读循环放弃（bus-dead heuristic 命中）后的状态收口。
    /// 仅在“已连接且非主动断开”时生效。
    /// </summary>
    internal void MarkDisconnectedAfterReadLoopGiveUp()
    {
        CancellationTokenSource? staleCts;
        IntPtr channelHandle;
        lock (_connectLock)
        {
            if (!_connected || _disconnecting) return;
            _connected = false;
            staleCts = _cts;
            channelHandle = _channelHandle;
            _cts = null;
            _readLoop = null;
            _channelHandle = IntPtr.Zero;
        }

        try { staleCts?.Dispose(); }
        catch { /* best-effort */ }

        if (channelHandle != IntPtr.Zero)
        {
            try { _ = _api.ResetCan(channelHandle); }
            catch { /* best-effort：设备可能已拔出 */ }
        }

        _deviceManager.Release(_devType, _devIdx);
    }

    private IntPtr CurrentChannelHandle
    {
        get
        {
            lock (_connectLock) return _channelHandle;
        }
    }

    private static ZcanReceiveData[] CreateClassicBuffer()
    {
        var buffer = new ZcanReceiveData[ReceiveBatchSize];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = ZcanReceiveData.Create();
        return buffer;
    }

    private static ZcanReceiveFdData[] CreateFdBuffer()
    {
        var buffer = new ZcanReceiveFdData[ReceiveBatchSize];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = ZcanReceiveFdData.Create();
        return buffer;
    }
}

/// <summary>ZLG 波特率/初始化配置映射。</summary>
internal static class ZlgBaudRate
{
    /// <summary>FD 预设的仲裁/数据段速率；classic 只返回仲裁段。</summary>
    public static (int? ArbBps, int? DataBps) Resolve(BaudRate baud, bool fd)
    {
        if (fd)
        {
            return baud.Name switch
            {
                "1 Mbps (FD)" => (1_000_000, 1_000_000),
                "2 Mbps (FD)" => (1_000_000, 2_000_000),
                "5 Mbps (FD)" => (1_000_000, 5_000_000),
                _ => (null, null),
            };
        }

        return baud.Name switch
        {
            "1 Mbps" => (1_000_000, null),
            "800 kbps" => (800_000, null),
            "500 kbps" => (500_000, null),
            "250 kbps" => (250_000, null),
            "125 kbps" => (125_000, null),
            "100 kbps" => (100_000, null),
            "50 kbps" => (50_000, null),
            "20 kbps" => (20_000, null),
            "10 kbps" => (10_000, null),
            _ => (null, null),
        };
    }

    /// <summary>
    /// 构造 ZCAN_CHANNEL_INIT_CONFIG。USBCANFD 的速率由 SetValue 驱动，
    /// 正常情况下 timing 保持 0；只有 SetValue 失败时才回退到 SJA1000 时序表。
    /// </summary>
    public static ZcanChannelInitConfig BuildConfig(
        bool fd, (byte Timing0, byte Timing1)? fallbackTiming = null)
    {
        var config = new ZcanChannelInitConfig
        {
            CanType = fd ? ZcanCanType.CanFd : ZcanCanType.Can,
        };

        if (fd)
        {
            config.CanFd = new ZcanCanFdInit
            {
                AccCode = 0,
                AccMask = 0xFFFF_FFFF,
                Filter = 0,
                Mode = 0,
            };
        }
        else
        {
            config.Can = new ZcanCanInit
            {
                AccCode = 0,
                AccMask = 0xFFFF_FFFF,
                Filter = 0,
                Timing0 = fallbackTiming?.Timing0 ?? 0,
                Timing1 = fallbackTiming?.Timing1 ?? 0,
                Mode = 0,
            };
        }

        return config;
    }

    /// <summary>经典 SJA1000 时序（仅作为 SetValue 失败时的回退）。</summary>
    public static (byte Timing0, byte Timing1)? ClassicTiming(string name) => name switch
    {
        "1 Mbps" => (0x00, 0x14),
        "800 kbps" => (0x00, 0x16),
        "500 kbps" => (0x00, 0x1C),
        "250 kbps" => (0x01, 0x1C),
        "125 kbps" => (0x03, 0x1C),
        "100 kbps" => (0x04, 0x1C),
        "50 kbps" => (0x09, 0x1C),
        "20 kbps" => (0x18, 0x1C),
        "10 kbps" => (0x31, 0x1C),
        _ => null,
    };
}
